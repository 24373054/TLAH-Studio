using System.Text;
using System.Text.RegularExpressions;

namespace TLAHStudio.Core.Services;

/// <summary>
/// Recognizes a deliberately narrow set of catastrophic command invocations.
/// This is a semantic guard for accidental model output, not an OS security
/// boundary: dynamically assembled or downloaded scripts require process/ACL
/// isolation to contain adversarial behavior.
/// </summary>
internal static partial class CatastrophicCommandAnalyzer
{
    private const int MaxDepth = 4;
    private const int MaxPayloadChars = 64 * 1024;

    private enum ShellDialect
    {
        PowerShell,
        Cmd,
        Posix
    }

    internal sealed record Analysis(bool IsCatastrophic, bool IsOpaque, string? Evidence)
    {
        public static Analysis Safe { get; } = new(false, false, null);
    }

    private sealed record InvocationSegment(string Text, char SeparatorAfter);

    private sealed record PipelineContext(
        IReadOnlyList<string> PathTargets,
        string? StaticInput,
        bool IsOpaque);

    public static Analysis Analyze(string command, string? workingDirectory = null) =>
        Analyze(command, 0, ShellDialect.PowerShell, workingDirectory);

    private static Analysis Analyze(
        string command,
        int depth,
        ShellDialect dialect,
        string? workingDirectory,
        PipelineContext? initialPipeline = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Analysis.Safe;
        if (depth > MaxDepth || command.Length > MaxPayloadChars)
            return new Analysis(false, true, "Command wrapper is too deep or large to inspect safely.");

        var sawOpaque = false;
        string? opaqueEvidence = null;
        PipelineContext? pipeline = initialPipeline;
        foreach (var segment in SplitInvocations(command, dialect))
        {
            var tokens = Tokenize(segment.Text, dialect);
            if (tokens.Count == 0)
                continue;
            if (dialect == ShellDialect.Cmd &&
                string.Equals(tokens[0], "rem", StringComparison.OrdinalIgnoreCase))
                continue;

            var result = AnalyzeInvocation(tokens, depth, dialect, workingDirectory, pipeline);
            if (result.IsCatastrophic)
                return result;
            if (result.IsOpaque)
            {
                sawOpaque = true;
                opaqueEvidence ??= result.Evidence;
            }

            pipeline = segment.SeparatorAfter == '|'
                ? BuildPipelineContext(tokens, dialect, workingDirectory, pipeline)
                : null;
        }

        return sawOpaque
            ? new Analysis(false, true, opaqueEvidence)
            : Analysis.Safe;
    }

    private static Analysis AnalyzeInvocation(
        IReadOnlyList<string> rawTokens,
        int depth,
        ShellDialect dialect,
        string? workingDirectory,
        PipelineContext? pipeline)
    {
        var tokens = rawTokens.ToList();
        StripLaunchPrefixes(tokens);
        if (tokens.Count == 0)
            return Analysis.Safe;

        var executable = NormalizeExecutable(tokens[0]);
        var args = tokens.Skip(1).ToList();

        // PowerShell call-operator script blocks are split into a segment that
        // starts with "{". Inspect the block body instead of treating the brace
        // as an unknown executable.
        if (tokens[0] == "{")
        {
            var body = tokens.Skip(1).ToList();
            if (body.Count > 0 && body[^1] == "}")
                body.RemoveAt(body.Count - 1);
            return body.Count == 0
                ? Analysis.Safe
                : Analyze(string.Join(' ', body), depth + 1, ShellDialect.PowerShell, workingDirectory);
        }

        if (executable is "cmd")
            return AnalyzeWrapperPayload(args, ["/c", "/k"], depth, ShellDialect.Cmd, workingDirectory);

        if (executable is "powershell" or "pwsh")
        {
            var encodedIndex = FindOption(args, "-encodedcommand", "-enc", "-ec", "-e");
            if (encodedIndex >= 0)
            {
                if (encodedIndex + 1 >= args.Count || !TryDecodePowerShell(args[encodedIndex + 1], out var decoded))
                    return new Analysis(false, true, "PowerShell encoded command could not be decoded.");
                return Analyze(decoded, depth + 1, ShellDialect.PowerShell, workingDirectory);
            }

            var commandIndex = FindOption(args, "-command", "-c");
            if (commandIndex >= 0)
                return AnalyzeJoinedPayload(args, commandIndex + 1, depth, ShellDialect.PowerShell, workingDirectory);
            if (FindOption(args, "-file", "-f") >= 0)
                return new Analysis(false, true, "PowerShell script-file contents are not statically inspected.");
            return Analysis.Safe;
        }

        if (executable is "sh" or "bash" or "dash" or "zsh")
        {
            var commandIndex = args.FindIndex(value =>
                value.Length > 1 && value[0] == '-' && value[1..].Contains('c'));
            return commandIndex < 0
                ? Analysis.Safe
                : AnalyzeJoinedPayload(args, commandIndex + 1, depth, ShellDialect.Posix, workingDirectory);
        }

        if (executable is "invoke-expression" or "iex")
        {
            if (args.Count == 0)
                return new Analysis(false, true, "Invoke-Expression has no inspectable payload.");
            var payload = string.Join(' ', args);
            return payload.Contains('$', StringComparison.Ordinal)
                ? new Analysis(false, true, "Invoke-Expression payload is dynamically assembled.")
                : Analyze(payload, depth + 1, ShellDialect.PowerShell, workingDirectory);
        }

        if (executable is "foreach-object" or "foreach" or "%" or
            "where-object" or "where" or "?" or
            "invoke-command" or "icm" or
            "start-job" or "sajb")
        {
            return AnalyzeStaticPowerShellBlocks(
                args,
                depth,
                workingDirectory,
                pipeline);
        }

        if (executable == "wsl")
            return AnalyzeWslPayload(args, depth, workingDirectory);

        if (executable == "diskpart")
            return AnalyzeDiskPartInput(args, workingDirectory, pipeline);

        if (ScriptExtensionRegex().IsMatch(tokens[0]))
            return new Analysis(false, true, "Script-file contents are not statically inspected.");

        return IsCatastrophicInvocation(
                executable,
                args,
                pipeline,
                dialect,
                workingDirectory)
            ? new Analysis(true, false, string.Join(' ', tokens))
            : Analysis.Safe;
    }

    private static Analysis AnalyzeStaticPowerShellBlocks(
        IReadOnlyList<string> args,
        int depth,
        string? workingDirectory,
        PipelineContext? pipeline)
    {
        var foundBlock = false;
        var sawOpaque = false;
        string? opaqueEvidence = null;

        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] != "{")
                continue;

            foundBlock = true;
            var blockDepth = 1;
            var body = new List<string>();
            for (index++; index < args.Count; index++)
            {
                if (args[index] == "{")
                {
                    blockDepth++;
                    body.Add(args[index]);
                    continue;
                }

                if (args[index] == "}")
                {
                    blockDepth--;
                    if (blockDepth == 0)
                        break;
                    body.Add(args[index]);
                    continue;
                }

                body.Add(args[index]);
            }

            if (blockDepth != 0)
            {
                return new Analysis(
                    false,
                    true,
                    "PowerShell script block could not be inspected completely.");
            }

            if (body.Count == 0)
                continue;

            var result = Analyze(
                string.Join(' ', body),
                depth + 1,
                ShellDialect.PowerShell,
                workingDirectory,
                pipeline);
            if (result.IsCatastrophic)
                return result;
            if (result.IsOpaque)
            {
                sawOpaque = true;
                opaqueEvidence ??= result.Evidence;
            }
        }

        if (sawOpaque)
            return new Analysis(false, true, opaqueEvidence);

        if (!foundBlock && args.Any(arg =>
                arg.Equals("-ScriptBlock", StringComparison.OrdinalIgnoreCase)))
        {
            return new Analysis(
                false,
                true,
                "PowerShell script block is dynamic and cannot be inspected statically.");
        }

        return Analysis.Safe;
    }

    private static Analysis AnalyzeWrapperPayload(
        IReadOnlyList<string> args,
        IReadOnlyList<string> switches,
        int depth,
        ShellDialect dialect,
        string? workingDirectory)
    {
        var index = args.FindIndex(value => switches.Any(option =>
            string.Equals(value, option, StringComparison.OrdinalIgnoreCase)));
        return index < 0
            ? Analysis.Safe
            : AnalyzeJoinedPayload(args, index + 1, depth, dialect, workingDirectory);
    }

    private static Analysis AnalyzeJoinedPayload(
        IReadOnlyList<string> args,
        int start,
        int depth,
        ShellDialect dialect,
        string? workingDirectory)
    {
        if (start >= args.Count)
            return new Analysis(false, true, "Command wrapper has no inspectable payload.");
        var payload = string.Join(' ', args.Skip(start));
        return Analyze(payload, depth + 1, dialect, workingDirectory);
    }

    private static Analysis AnalyzeWslPayload(
        IReadOnlyList<string> args,
        int depth,
        string? workingDirectory)
    {
        var wslWorkingDirectory = ReadWslWorkingDirectory(args) ?? workingDirectory;
        var separator = args.FindIndex(value => value == "--");
        if (separator >= 0)
            return AnalyzeJoinedPayload(args, separator + 1, depth, ShellDialect.Posix, wslWorkingDirectory);

        var execIndex = FindOption(args, "-e", "--exec");
        if (execIndex >= 0)
            return AnalyzeJoinedPayload(args, execIndex + 1, depth, ShellDialect.Posix, wslWorkingDirectory);

        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "-d" or "--distribution" or "-u" or "--user" or "--cd")
            {
                i++;
                continue;
            }
            if (args[i].StartsWith("--cd=", StringComparison.OrdinalIgnoreCase))
                continue;
            if (args[i].StartsWith("-", StringComparison.Ordinal))
                continue;
            return AnalyzeJoinedPayload(args, i, depth, ShellDialect.Posix, wslWorkingDirectory);
        }

        return Analysis.Safe;
    }

    private static string? ReadWslWorkingDirectory(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("--cd", StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Count ? TrimMatchingQuotes(args[i + 1]) : null;
            if (args[i].StartsWith("--cd=", StringComparison.OrdinalIgnoreCase))
                return TrimMatchingQuotes(args[i]["--cd=".Length..]);
        }

        return null;
    }

    private static PipelineContext BuildPipelineContext(
        IReadOnlyList<string> rawTokens,
        ShellDialect dialect,
        string? workingDirectory,
        PipelineContext? upstream)
    {
        var tokens = rawTokens.ToList();
        StripLaunchPrefixes(tokens);
        if (tokens.Count == 0)
            return new PipelineContext([], null, true);

        var executable = NormalizeExecutable(tokens[0]);
        var args = tokens.Skip(1).ToList();

        if (executable is "echo" or "write-output")
        {
            var staticArguments = args.Where(arg =>
                    !arg.Equals("-NoNewline", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var staticInput = string.Join(' ', staticArguments);
            return new PipelineContext(
                PowerShellPathArguments(staticArguments).ToList(),
                staticInput,
                false);
        }

        if (executable is "get-content" or "gc" or "type")
        {
            var paths = PowerShellPathArguments(args).ToList();
            if (paths.Count == 1 && TryReadSmallFile(paths[0], workingDirectory, out var contents))
                return new PipelineContext(paths, contents, false);
            return new PipelineContext(paths, null, true);
        }

        if (executable is "get-childitem" or "gci" or "dir" or "ls" or
            "get-item" or "gi" or "resolve-path")
        {
            return new PipelineContext(PowerShellPathArguments(args).ToList(), null, false);
        }

        // A quoted/static path expression can be piped directly to
        // Remove-Item. Tokenization intentionally removes the quotes, so carry
        // only lexically path-like single values rather than treating an
        // arbitrary command's output as a known path.
        if (tokens.Count == 1 && IsStaticPipelinePath(tokens[0]))
            return new PipelineContext([tokens[0]], tokens[0], false);

        // These commands preserve the identity of pipeline objects. Carry the
        // originating path through so a later recursive Remove-Item cannot
        // hide a critical target behind a filter or projection stage.
        if (upstream is not null &&
            executable is "where-object" or "where" or "select-object" or "sort-object")
        {
            return upstream;
        }

        return new PipelineContext([], null, true);
    }

    private static Analysis AnalyzeDiskPartInput(
        IReadOnlyList<string> args,
        string? workingDirectory,
        PipelineContext? pipeline)
    {
        if (HasOption(args, "/s", "-s"))
            return AnalyzeDiskPartScript(args, workingDirectory);

        if (TryGetInputRedirection(args, out var redirectedPath))
            return AnalyzeDiskPartFile(redirectedPath, workingDirectory);

        if (pipeline is null)
            return Analysis.Safe;

        if (pipeline.StaticInput is not null)
            return AnalyzeDiskPartText(pipeline.StaticInput);

        return new Analysis(
            true,
            false,
            "DiskPart receives pipeline input whose contents cannot be inspected safely.");
    }

    private static Analysis AnalyzeDiskPartScript(
        IReadOnlyList<string> args,
        string? workingDirectory)
    {
        var scriptIndex = FindOption(args, "/s", "-s");
        if (scriptIndex < 0 || scriptIndex + 1 >= args.Count)
        {
            return new Analysis(
                true,
                false,
                "DiskPart script path is missing and cannot be inspected.");
        }

        return AnalyzeDiskPartFile(args[scriptIndex + 1], workingDirectory);
    }

    private static Analysis AnalyzeDiskPartFile(string rawPath, string? workingDirectory)
    {

        try
        {
            var normalizedPath = TrimMatchingQuotes(rawPath);
            var path = Path.IsPathRooted(normalizedPath)
                ? Path.GetFullPath(normalizedPath)
                : Path.GetFullPath(Path.Combine(workingDirectory ?? Environment.CurrentDirectory, normalizedPath));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxPayloadChars)
            {
                return new Analysis(
                    true,
                    false,
                    "DiskPart script is unavailable or too large to inspect safely.");
            }

            return AnalyzeDiskPartText(File.ReadAllText(path));
        }
        catch
        {
            return new Analysis(
                true,
                false,
                "DiskPart script could not be inspected safely.");
        }
    }

    private static Analysis AnalyzeDiskPartText(string script)
    {
        if (script.Length > MaxPayloadChars)
        {
            return new Analysis(
                true,
                false,
                "DiskPart input is too large to inspect safely.");
        }

        foreach (var rawLine in script.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 ||
                line.Equals("rem", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (DiskPartDestructiveRegex().IsMatch(line))
                return new Analysis(true, false, $"diskpart: {line}");
        }

        return Analysis.Safe;
    }

    private static bool TryGetInputRedirection(IReadOnlyList<string> args, out string path)
    {
        path = string.Empty;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "<" && i + 1 < args.Count)
            {
                path = args[i + 1];
                return true;
            }

            var marker = args[i].StartsWith("0<", StringComparison.Ordinal)
                ? 2
                : args[i].StartsWith("<", StringComparison.Ordinal)
                    ? 1
                    : 0;
            if (marker > 0 && args[i].Length > marker)
            {
                path = args[i][marker..];
                return true;
            }
        }

        return false;
    }

    private static bool TryReadSmallFile(
        string rawPath,
        string? workingDirectory,
        out string contents)
    {
        contents = string.Empty;
        try
        {
            var normalizedPath = TrimMatchingQuotes(rawPath);
            var path = Path.IsPathRooted(normalizedPath)
                ? Path.GetFullPath(normalizedPath)
                : Path.GetFullPath(Path.Combine(workingDirectory ?? Environment.CurrentDirectory, normalizedPath));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxPayloadChars)
                return false;
            contents = File.ReadAllText(path);
            return contents.Length <= MaxPayloadChars;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCatastrophicInvocation(
        string executable,
        IReadOnlyList<string> args,
        PipelineContext? pipeline,
        ShellDialect dialect,
        string? workingDirectory)
    {
        // Preview/help switches are explicitly non-executing and should never
        // trip the immutable guard.
        if (HasPowerShellSwitch(args, "whatif", minimumPrefixLength: 3) ||
            HasOption(args, "/?", "-?", "--help"))
            return false;

        if (executable == "rm")
        {
            var recursive = HasUnixFlag(args, 'r', "--recursive");
            var force = HasUnixFlag(args, 'f', "--force");
            var posixWorkingDirectory = dialect == ShellDialect.Posix
                ? workingDirectory
                : null;
            return recursive && force && PositionalArguments(args)
                .Any(target => IsCriticalDeleteTarget(target, posixWorkingDirectory));
        }

        if (executable is "remove-item" or "ri")
        {
            var recursive = HasPowerShellSwitch(
                args,
                "recurse",
                minimumPrefixLength: 2,
                aliases: ["r"]);
            return recursive &&
                (PowerShellPathArguments(args).Any(target => IsCriticalDeleteTarget(target)) ||
                 pipeline?.PathTargets.Any(target => IsCriticalDeleteTarget(target)) == true);
        }

        if (executable is "rd" or "rmdir" or "del" or "erase")
        {
            var recursive = HasOption(args, "/s") || HasPowerShellSwitch(
                args,
                "recurse",
                minimumPrefixLength: 2,
                aliases: ["r"]);
            return recursive && PowerShellPathArguments(args)
                .Any(target => IsCriticalDeleteTarget(target));
        }

        if (executable is "format" or "format-volume")
            return args.Any(IsDriveOrVolumeTarget);

        if (executable is "clear-disk" or "initialize-disk" or "remove-partition")
            return true;

        if (executable == "bcdedit")
            return HasOption(args, "/delete", "/deletevalue");

        if (executable == "bootrec")
            return HasOption(args, "/fixmbr", "/fixboot", "/rebuildbcd");

        if (executable is "remove-localuser" or "userdel" or "deluser")
            return true;

        return executable == "net" &&
               args.Count > 0 &&
               string.Equals(args[0], "user", StringComparison.OrdinalIgnoreCase) &&
               HasOption(args, "/delete");
    }

    private static IEnumerable<string> PositionalArguments(IReadOnlyList<string> args)
    {
        var afterSeparator = false;
        foreach (var arg in args)
        {
            if (arg == "--")
            {
                afterSeparator = true;
                continue;
            }
            if (!afterSeparator && arg.Length > 1 && arg[0] == '-')
                continue;
            yield return arg;
        }
    }

    private static IEnumerable<string> PowerShellPathArguments(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Equals("-path", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-literalpath", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                    yield return args[++i];
                continue;
            }

            if (!IsOption(args[i]))
                yield return args[i];
        }
    }

    private static bool IsCriticalDeleteTarget(
        string raw,
        string? posixWorkingDirectory = null)
    {
        var original = TrimMatchingQuotes(raw.Trim()).Trim();
        if (!original.StartsWith("/", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(posixWorkingDirectory) &&
            posixWorkingDirectory.StartsWith("/", StringComparison.Ordinal))
        {
            original = $"{posixWorkingDirectory.TrimEnd('/')}/{original}";
        }
        if (IsCriticalPosixDeleteTarget(original))
            return true;

        var target = NormalizeWindowsDeleteTarget(original);
        if (target.Length == 0)
            return raw.TrimStart().StartsWith("/", StringComparison.Ordinal);

        var normalized = target.ToLowerInvariant();
        if (normalized is "~" or "$home" or "${home}" or "$env:userprofile" or
            "${env:userprofile}" or "%userprofile%" or "$env:systemroot" or
            "${env:systemroot}" or "%windir%")
            return true;

        if (normalized.StartsWith("$env:systemroot\\", StringComparison.Ordinal) ||
            normalized.StartsWith("${env:systemroot}\\", StringComparison.Ordinal) ||
            normalized.StartsWith("%windir%\\", StringComparison.Ordinal))
            return true;

        if (normalized is "$env:systemdrive" or "${env:systemdrive}" or "%systemdrive%")
            return true;

        if (normalized.StartsWith("$env:systemdrive\\windows", StringComparison.Ordinal) ||
            normalized.StartsWith("${env:systemdrive}\\windows", StringComparison.Ordinal) ||
            normalized.StartsWith("%systemdrive%\\windows", StringComparison.Ordinal) ||
            normalized.StartsWith("$env:systemdrive\\users", StringComparison.Ordinal) ||
            normalized.StartsWith("${env:systemdrive}\\users", StringComparison.Ordinal) ||
            normalized.StartsWith("%systemdrive%\\users", StringComparison.Ordinal))
            return true;

        if (DriveRootRegex().IsMatch(target))
            return true;

        return CriticalWindowsDirectoryRegex().IsMatch(target);
    }

    private static bool IsCriticalPosixDeleteTarget(string raw)
    {
        if (!raw.StartsWith("/", StringComparison.Ordinal))
            return false;

        var components = new List<string>();
        foreach (var component in raw.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
                continue;
            if (component == "..")
            {
                if (components.Count > 0)
                    components.RemoveAt(components.Count - 1);
                continue;
            }
            components.Add(component);
        }

        if (components.Count == 0)
            return true;

        if (ContainsPathWildcard(components[^1]))
            components.RemoveAt(components.Count - 1);

        if (components.Count == 0)
            return true;
        if (components.Count != 1)
            return false;

        return components[0].ToLowerInvariant() is
            "bin" or "boot" or "dev" or "etc" or "lib" or "lib64" or
            "proc" or "root" or "sbin" or "sys" or "usr" or "var";
    }

    private static string TrimTrailingWildcardComponent(string target)
    {
        var trimmed = target.TrimEnd('\\');
        var separator = trimmed.LastIndexOf('\\');
        if (separator < 0 || !ContainsPathWildcard(trimmed[(separator + 1)..]))
            return trimmed;
        return trimmed[..separator];
    }

    private static string NormalizeWindowsDeleteTarget(string raw)
    {
        var target = raw.Replace('/', '\\').Trim();
        if (target.StartsWith("FileSystem::", StringComparison.OrdinalIgnoreCase))
            target = target["FileSystem::".Length..];
        if (target.StartsWith("\\\\?\\", StringComparison.Ordinal))
            target = target[4..];

        target = TrimTrailingWildcardComponent(target).TrimEnd('\\');
        if (target.Length >= 3 &&
            char.IsAsciiLetter(target[0]) &&
            target[1] == ':' &&
            target[2] == '\\')
        {
            try
            {
                target = Path.GetFullPath(target).TrimEnd('\\');
            }
            catch
            {
                // Keep the lexical form. Unknown/dynamic paths are handled by
                // the wider safety policy rather than guessed here.
            }
        }

        return target;
    }

    private static bool ContainsPathWildcard(string component) =>
        component.IndexOfAny(['*', '?', '[']) >= 0;

    private static bool IsStaticPipelinePath(string raw)
    {
        var value = TrimMatchingQuotes(raw.Trim());
        if (value.Length == 0)
            return false;

        return Path.IsPathRooted(value) ||
               value.StartsWith("./", StringComparison.Ordinal) ||
               value.StartsWith(".\\", StringComparison.Ordinal) ||
               value.StartsWith("../", StringComparison.Ordinal) ||
               value.StartsWith("..\\", StringComparison.Ordinal) ||
               value.StartsWith("~", StringComparison.Ordinal) ||
               value.StartsWith("$env:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("${env:", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("%", StringComparison.Ordinal);
    }

    private static bool IsDriveOrVolumeTarget(string raw)
    {
        var value = TrimMatchingQuotes(raw.Trim());
        return DriveDesignatorRegex().IsMatch(value) ||
               value.StartsWith("-DriveLetter", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("-DiskNumber", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasUnixFlag(IReadOnlyList<string> args, char shortFlag, string longFlag) =>
        args.Any(arg => string.Equals(arg, longFlag, StringComparison.OrdinalIgnoreCase) ||
            arg.Length > 1 && arg[0] == '-' && arg[1] != '-' &&
            arg[1..].IndexOf(shortFlag.ToString(), StringComparison.OrdinalIgnoreCase) >= 0);

    private static bool HasOption(IReadOnlyList<string> args, params string[] options) =>
        args.Any(arg => options.Any(option => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase)));

    private static bool HasPowerShellSwitch(
        IReadOnlyList<string> args,
        string canonicalName,
        int minimumPrefixLength,
        IReadOnlyList<string>? aliases = null)
    {
        foreach (var arg in args)
        {
            if (arg.Length < 2 || arg[0] != '-')
                continue;

            var separator = arg.IndexOf(':');
            var name = arg[1..(separator >= 0 ? separator : arg.Length)];
            var isAlias = aliases?.Any(alias =>
                name.Equals(alias, StringComparison.OrdinalIgnoreCase)) == true;
            var isCanonicalOrValidPrefix =
                name.Length >= minimumPrefixLength &&
                canonicalName.StartsWith(name, StringComparison.OrdinalIgnoreCase);
            if (!isAlias && !isCanonicalOrValidPrefix)
                continue;

            if (separator < 0)
                return true;

            var value = arg[(separator + 1)..].Trim();
            if (value.Equals("$false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value == "0")
            {
                continue;
            }

            // A literal true or a dynamic expression may enable the switch.
            // Treat the latter conservatively when the target is a critical
            // system path; Full access must not bypass that uncertainty.
            return true;
        }

        return false;
    }

    private static int FindOption(IReadOnlyList<string> args, params string[] options)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (options.Any(option => string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase)))
                return i;
        }
        return -1;
    }

    private static void StripLaunchPrefixes(List<string> tokens)
    {
        while (tokens.Count > 0)
        {
            var executable = NormalizeExecutable(tokens[0]);
            if (executable is "sudo" or "doas" or "command" or "call")
            {
                tokens.RemoveAt(0);
                continue;
            }
            if (executable == "env")
            {
                tokens.RemoveAt(0);
                while (tokens.Count > 0 && EnvironmentAssignmentRegex().IsMatch(tokens[0]))
                    tokens.RemoveAt(0);
                continue;
            }
            break;
        }
    }

    private static string NormalizeExecutable(string value)
    {
        var executable = Path.GetFileName(TrimMatchingQuotes(value.Trim())).ToLowerInvariant();
        foreach (var suffix in new[] { ".exe", ".com" })
        {
            if (executable.EndsWith(suffix, StringComparison.Ordinal))
                return executable[..^suffix.Length];
        }
        return executable;
    }

    private static bool TryDecodePowerShell(string raw, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(TrimMatchingQuotes(raw));
            if (bytes.Length == 0 || bytes.Length > MaxPayloadChars * 2 || bytes.Length % 2 != 0)
                return false;
            decoded = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            return decoded.Length > 0 && decoded.Length <= MaxPayloadChars && !decoded.Contains('\0');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IReadOnlyList<InvocationSegment> SplitInvocations(string command, ShellDialect dialect)
    {
        var segments = new List<InvocationSegment>();
        var current = new StringBuilder();
        char quote = '\0';
        var escaped = false;
        var comment = false;
        var braceDepth = 0;

        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (comment)
            {
                if (ch is '\r' or '\n')
                {
                    comment = false;
                    FlushSegment(segments, current, '\n');
                }
                continue;
            }
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }
            if (ch == '`')
            {
                current.Append(ch);
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                current.Append(ch);
                if (ch == quote)
                    quote = '\0';
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                current.Append(ch);
                continue;
            }
            if (dialect == ShellDialect.PowerShell && ch == '{')
            {
                braceDepth++;
                current.Append(ch);
                continue;
            }
            if (dialect == ShellDialect.PowerShell && ch == '}' && braceDepth > 0)
            {
                braceDepth--;
                current.Append(ch);
                continue;
            }
            if (ch == '#' && dialect != ShellDialect.Cmd)
            {
                comment = true;
                continue;
            }
            if (braceDepth == 0 && ch is (';' or '|' or '&' or '\r' or '\n'))
            {
                var separator = ch;
                if (i + 1 < command.Length && command[i + 1] == ch && ch is '|' or '&')
                {
                    // Logical OR/AND starts a new invocation but does not pipe
                    // the previous invocation's output into the next one.
                    separator = ';';
                    i++;
                }
                else if (ch == '|' && i + 1 < command.Length && command[i + 1] == '&')
                {
                    // POSIX |& is still a pipeline (stdout + stderr).
                    i++;
                }
                FlushSegment(segments, current, separator);
                continue;
            }
            current.Append(ch);
        }
        FlushSegment(segments, current, '\0');
        return segments;
    }

    private static IReadOnlyList<string> Tokenize(string segment, ShellDialect dialect)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        var escaped = false;
        var embeddedBraceDepth = 0;
        foreach (var ch in segment)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }
            if (ch == '`' ||
                dialect == ShellDialect.Cmd && ch == '^' ||
                dialect == ShellDialect.Posix && ch == '\\')
            {
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                else
                    current.Append(ch);
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (dialect == ShellDialect.PowerShell && ch == '{')
            {
                if (embeddedBraceDepth > 0 ||
                    current.Length > 0 && current[^1] is ('$' or '@'))
                {
                    embeddedBraceDepth++;
                    current.Append(ch);
                }
                else
                {
                    FlushToken(tokens, current);
                    tokens.Add("{");
                }
                continue;
            }
            if (dialect == ShellDialect.PowerShell && ch == '}')
            {
                if (embeddedBraceDepth > 0)
                {
                    embeddedBraceDepth--;
                    current.Append(ch);
                }
                else
                {
                    FlushToken(tokens, current);
                    tokens.Add("}");
                }
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                FlushToken(tokens, current);
                continue;
            }
            current.Append(ch);
        }
        FlushToken(tokens, current);
        return tokens;
    }

    private static void FlushSegment(
        ICollection<InvocationSegment> output,
        StringBuilder current,
        char separatorAfter)
    {
        var value = current.ToString().Trim();
        current.Clear();
        if (value.Length > 0)
            output.Add(new InvocationSegment(value, separatorAfter));
    }

    private static void FlushToken(ICollection<string> output, StringBuilder current)
    {
        if (current.Length == 0)
            return;
        output.Add(current.ToString());
        current.Clear();
    }

    private static bool IsOption(string value) =>
        value.Length > 1 && value[0] is '-' or '/';

    private static string TrimMatchingQuotes(string value) =>
        value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"'
            ? value[1..^1]
            : value;

    private static int FindIndex(this IReadOnlyList<string> values, Func<string, bool> predicate)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (predicate(values[i]))
                return i;
        }
        return -1;
    }

    [GeneratedRegex(@"(?i)\.(?:ps1|bat|cmd|sh)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptExtensionRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentAssignmentRegex();

    [GeneratedRegex(@"(?i)^[a-z]:$", RegexOptions.CultureInvariant)]
    private static partial Regex DriveDesignatorRegex();

    [GeneratedRegex(@"(?i)^[a-z]:$", RegexOptions.CultureInvariant)]
    private static partial Regex DriveRootRegex();

    [GeneratedRegex(@"(?i)^(?:[a-z]:\\windows(?:\\system32(?:\\.*)?)?|[a-z]:\\users(?:\\[^\\]+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex CriticalWindowsDirectoryRegex();

    [GeneratedRegex(@"(?ix)^(?:clean(?:\s+all)?|delete\s+(?:disk|partition|volume)|format\b|convert\s+(?:mbr|gpt|dynamic|basic)|create\s+partition\b)", RegexOptions.CultureInvariant)]
    private static partial Regex DiskPartDestructiveRegex();
}
