using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TLAHStudio.Core.Helpers;

namespace TLAHStudio.Core.Services;

public sealed class SandboxCommandService : ISandboxCommandService
{
    private static readonly Regex AbsoluteWindowsPathRegex = new(
        @"(?<![\w])([A-Za-z]:\\[^""'\r\n;|&<>]*)",
        RegexOptions.Compiled);

    private static readonly Regex NestedShellFileRegex = new(
        @"(?ix)^\s*(?:powershell|pwsh)(?:\.exe)?\b[\s\S]*?\s-file\s+[""']?(?<file>[^""'\r\n;|&<>]+\.ps1)[""']?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex DirectScriptInvocationRegex = new(
        @"(?ix)(?:^|[;&]\s*)[&.]?\s*[""']?(?<file>\.?\\?[A-Za-z0-9_. -]+\.ps1)[""']?",
        RegexOptions.Compiled);

    private static readonly Regex BlockedTokenRegex = new(
        @"(?ix)
        \b(
            format|diskpart|shutdown|restart-computer|stop-computer|set-executionpolicy|
            takeown|icacls|bcdedit|bootrec|netsh|sc(?:\.exe)?|schtasks|
            start-process|powershell|pwsh|cmd|wscript|cscript|mshta|
            invoke-webrequest|invoke-restmethod|start-bitstransfer|curl|wget|ssh|scp|ftp|
            reg(?:\.exe)?\s+(?:add|delete|import|restore|save)|
            remove-item|del|erase|rmdir|rd
        )\b",
        RegexOptions.Compiled);

    private static readonly string[] BlockedPathMarkers =
    [
        "$env:userprofile",
        "$env:home",
        "$home",
        "%userprofile%",
        "%homepath%",
        "%appdata%",
        "%localappdata%",
        "\\windows",
        "\\program files",
        "\\programdata",
        "\\users\\"
    ];

    private readonly string _root;

    public SandboxCommandService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio",
            "sandboxes"))
    {
    }

    public SandboxCommandService(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string GetSandboxRoot(Guid chatId)
    {
        var path = Path.Combine(_root, chatId.ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<SandboxCommandResult> ExecuteAsync(
        Guid chatId,
        string command,
        SandboxCommandOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SandboxCommandOptions();
        var sandboxRoot = GetSandboxRoot(chatId);
        command = NormalizeCommand(command.Trim());

        var blocked = ValidateCommand(command, sandboxRoot);
        if (blocked != null)
        {
            return new SandboxCommandResult(
                command,
                sandboxRoot,
                ExitCode: -1,
                TimedOut: false,
                Duration: TimeSpan.Zero,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                BlockedReason: blocked);
        }

        var psi = new ProcessStartInfo
        {
            FileName = ResolvePowerShellPath(),
            WorkingDirectory = sandboxRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(WithUtf8Console(command));
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["TLAH_SANDBOX"] = "1";
        psi.Environment["TLAH_SANDBOX_ROOT"] = sandboxRoot;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var sw = Stopwatch.StartNew();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var timedOut = false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120)));

        process.OutputDataReceived += (_, e) => AppendLimited(stdout, e.Data, options.MaxOutputChars);
        process.ErrorDataReceived += (_, e) => AppendLimited(stderr, e.Data, options.MaxOutputChars);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }

        sw.Stop();

        return new SandboxCommandResult(
            command,
            sandboxRoot,
            process.HasExited ? process.ExitCode : -1,
            timedOut,
            sw.Elapsed,
            SecretRedactor.RedactText(stdout.ToString()),
            SecretRedactor.RedactText(stderr.ToString()));
    }

    private static string? ValidateCommand(string command, string sandboxRoot)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "Command is empty.";
        if (command.Length > 4000)
            return "Command is too long for the sandbox executor.";

        var lower = command.ToLowerInvariant();
        if (lower.Contains("..", StringComparison.Ordinal))
            return "Parent-directory traversal is not allowed in sandbox commands.";

        foreach (var marker in BlockedPathMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return $"The command references a protected host path marker: {marker}.";
        }

        if (BlockedTokenRegex.IsMatch(command))
            return "The command contains an operation that is blocked by the sandbox policy.";

        var scriptBlocked = ValidateScriptReferences(command, sandboxRoot);
        if (scriptBlocked != null)
            return scriptBlocked;

        foreach (Match match in AbsoluteWindowsPathRegex.Matches(command))
        {
            var rawPath = match.Groups[1].Value.Trim();
            try
            {
                var fullPath = Path.GetFullPath(rawPath);
                if (!IsUnderDirectory(fullPath, sandboxRoot))
                    return $"Absolute host paths are blocked outside the sandbox: {rawPath}";
            }
            catch
            {
                return $"The command contains an invalid absolute path: {rawPath}";
            }
        }

        return null;
    }

    private static string NormalizeCommand(string command)
    {
        var nested = NestedShellFileRegex.Match(command);
        if (!nested.Success)
            return command;

        var script = nested.Groups["file"].Value.Trim();
        if (IsSafeRelativeScriptName(script))
            return $"& .\\{script}";

        return command;
    }

    internal static string WithUtf8Console(string command) =>
        "$tlahUtf8 = [System.Text.UTF8Encoding]::new($false); " +
        "[Console]::InputEncoding = $tlahUtf8; " +
        "[Console]::OutputEncoding = $tlahUtf8; " +
        "$OutputEncoding = $tlahUtf8; " +
        command;

    private static string? ValidateScriptReferences(string command, string sandboxRoot)
    {
        foreach (Match match in DirectScriptInvocationRegex.Matches(command))
        {
            var raw = match.Groups["file"].Value.Trim().Trim('"', '\'');
            if (!raw.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsSafeRelativeScriptName(raw))
                return $"Script execution is limited to simple .ps1 filenames in the sandbox: {raw}";

            var scriptPath = Path.Combine(sandboxRoot, raw.TrimStart('.', '\\', '/'));
            var fullScriptPath = Path.GetFullPath(scriptPath);
            if (!IsUnderDirectory(fullScriptPath, sandboxRoot))
                return $"Script path escapes the sandbox: {raw}";
            if (!File.Exists(fullScriptPath))
                continue;

            var scriptText = File.ReadAllText(fullScriptPath);
            var blocked = ValidateCommand(scriptText, sandboxRoot);
            if (blocked != null)
                return $"The script {raw} contains blocked content: {blocked}";
        }

        return null;
    }

    private static bool IsSafeRelativeScriptName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (Path.IsPathRooted(value) || value.Contains("..", StringComparison.Ordinal))
            return false;

        var normalized = value.Trim().Trim('"', '\'').TrimStart('.', '\\', '/');
        return normalized.Equals(Path.GetFileName(normalized), StringComparison.OrdinalIgnoreCase) &&
               normalized.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderDirectory(string path, string parent)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullParent, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePowerShellPath()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var windowsPowerShell = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windowsPowerShell) ? windowsPowerShell : "powershell.exe";
    }

    private static void AppendLimited(StringBuilder builder, string? line, int maxChars)
    {
        if (line == null || builder.Length >= maxChars)
            return;

        var remaining = maxChars - builder.Length;
        var value = line.Length + Environment.NewLine.Length > remaining
            ? line[..Math.Max(0, remaining)]
            : line + Environment.NewLine;
        builder.Append(value);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
