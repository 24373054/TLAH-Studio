using System.Text.RegularExpressions;

namespace TLAHStudio.Core.Services.Tools;

/// <summary>
/// M4.6.0: Result of flag-level command validation.
/// </summary>
public enum FlagValidationResult
{
    /// <summary>All flags are safe — command can be auto-approved.</summary>
    Allow,
    /// <summary>Command is not in the allowlist — fall through to normal safety checks.</summary>
    NotInAllowlist,
    /// <summary>An unsafe flag or pattern was detected — command is blocked.</summary>
    Reject
}

/// <summary>
/// M4.6.0: Per-flag argument type expected by the allowlist validator.
/// </summary>
public enum FlagArgType { None, Number, String }

/// <summary>
/// M4.6.0: Configuration for a single command/subcommand in the flag allowlist.
/// </summary>
/// <param name="SafeFlags">Map of flag token → expected argument type.</param>
/// <param name="AdditionalDangerousCheck">Optional callback. Returns the rejection reason, or null if safe.</param>
/// <param name="RegexCheck">Optional regex that the raw command string must match.</param>
public sealed record CommandConfig(
    Dictionary<string, FlagArgType> SafeFlags,
    Func<string, string[]?, string?>? AdditionalDangerousCheck = null,
    Regex? RegexCheck = null)
{
    public static readonly CommandConfig ReadOnly = new(
        new Dictionary<string, FlagArgType>(), null, null);
}

/// <summary>
/// M4.6.0: Flag-level command allowlist validator.
///
/// Adopted from Claude Code's readOnlyValidation.ts and bashSecurity.ts.
/// Parses shell commands into tokens, matches against a allowlist of safe
/// flags per command, and blocks dangerous operations before execution.
/// </summary>
public interface IFlagLevelValidationService
{
    /// <summary>
    /// Validate a shell command against the flag-level allowlist.
    /// Returns Allow (safe), NotInAllowlist (unrecognized — defer to other checks),
    /// or a rejection reason string (blocked).
    /// </summary>
    (FlagValidationResult Result, string? Reason) Validate(string command);
}

public sealed class FlagLevelValidationService : IFlagLevelValidationService
{
    // ── WARNING PATTERNS (destructive but allowed with a note) ────────
    // These never block — they are informational warnings the UI can display.
    // Adopted verbatim from Claude Code's destructiveCommandWarning.ts.

    public static readonly IReadOnlyList<(Regex Pattern, string Warning)> DestructiveWarnings =
    [
        // Git destructive
        (new(@"\bgit\s+reset\s+--hard\b", RegexOptions.Compiled),
         "May discard uncommitted changes."),
        (new(@"\bgit\s+push\b[^;&|\n]*[ \t](--force|--force-with-lease|-f)\b", RegexOptions.Compiled),
         "May overwrite remote history."),
        (new(@"\bgit\s+clean\b(?![^;&|\n]*(?:-[a-zA-Z]*n|--dry-run))[^;&|\n]*-[a-zA-Z]*f", RegexOptions.Compiled),
         "May permanently delete untracked files."),
        (new(@"\bgit\s+checkout\s+(--\s+)?\.[ \t]*($|[;&|\n])", RegexOptions.Compiled),
         "May discard all working tree changes."),
        (new(@"\bgit\s+restore\s+(--\s+)?\.[ \t]*($|[;&|\n])", RegexOptions.Compiled),
         "May discard all working tree changes."),
        (new(@"\bgit\s+stash[ \t]+(drop|clear)\b", RegexOptions.Compiled),
         "May permanently remove stashed changes."),
        (new(@"\bgit\s+branch\s+(-D[ \t]|--delete\s+--force|--force\s+--delete)\b", RegexOptions.Compiled),
         "May force-delete a branch."),
        // Git safety bypass
        (new(@"\bgit\s+(commit|push|merge)\b[^;&|\n]*--no-verify\b", RegexOptions.Compiled),
         "May skip safety hooks."),
        (new(@"\bgit\s+commit\b[^;&|\n]*--amend\b", RegexOptions.Compiled),
         "May rewrite the last commit."),
        // File deletion
        (new(@"(^|[;&|\n]\s*)rm\s+-[a-zA-Z]*[rR][a-zA-Z]*f|(^|[;&|\n]\s*)rm\s+-[a-zA-Z]*f[a-zA-Z]*[rR]", RegexOptions.Compiled),
         "May recursively force-remove files."),
        (new(@"(^|[;&|\n]\s*)rm\s+-[a-zA-Z]*[rR]", RegexOptions.Compiled),
         "May recursively remove files."),
        (new(@"(^|[;&|\n]\s*)rm\s+-[a-zA-Z]*f", RegexOptions.Compiled),
         "May force-remove files."),
        // Database
        (new(@"\b(DROP|TRUNCATE)\s+(TABLE|DATABASE|SCHEMA)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "May drop or truncate database objects."),
        (new(@"\bDELETE\s+FROM\s+\w+[ \t]*(;|""|'|\n|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "May delete all rows from a database table."),
        // Infrastructure
        (new(@"\bkubectl\s+delete\b", RegexOptions.Compiled),
         "May delete Kubernetes resources."),
        (new(@"\bterraform\s+destroy\b", RegexOptions.Compiled),
         "May destroy Terraform infrastructure."),
    ];

    // ── SHARED DEPENDENCIES (must precede Allowlist — C# static fields init in textual order) ──

    private static readonly Dictionary<string, FlagArgType> EmptyFlags = new(StringComparer.OrdinalIgnoreCase);

    private static readonly CommandConfig GitReadOnly = new(
        new Dictionary<string, FlagArgType>(StringComparer.OrdinalIgnoreCase),
        GitSafeGuard, GitSafeRegex);

    private static readonly Regex FindSafeRegex = new(
        @"^find(?:\s+(?:\\[()]|(?!-delete\b|-exec\b|-execdir\b|-ok\b|-okdir\b|-fprint0?\b|-fls\b|-fprintf\b)[^<>()$`|{}&;\n\r\s]|\s)+)?$",
        RegexOptions.Compiled);

    private static readonly Regex GitSafeRegex = new(
        @"^(?!.*\s-c[\s=])(?!.*\s--exec-path[\s=])(?!.*\s--config-env[\s=]).*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── FLAG ALLOWLIST ──────────────────────────────────────────────
    // Key: command prefix (space-separated), matched by longest-prefix.
    // Value: safe flags configuration.

    private static readonly Dictionary<string, CommandConfig> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── grep ────────────────────────────────────────────────────
        ["grep"] = new CommandConfig(new Dictionary<string, FlagArgType>(StringComparer.OrdinalIgnoreCase)
        {
            // Pattern
            ["-e"] = FlagArgType.String, ["--regexp"] = FlagArgType.String,
            ["-f"] = FlagArgType.String, ["--file"] = FlagArgType.String,
            ["-F"] = FlagArgType.None, ["--fixed-strings"] = FlagArgType.None,
            ["-G"] = FlagArgType.None, ["--basic-regexp"] = FlagArgType.None,
            ["-E"] = FlagArgType.None, ["--extended-regexp"] = FlagArgType.None,
            ["-P"] = FlagArgType.None, ["--perl-regexp"] = FlagArgType.None,
            // Matching
            ["-i"] = FlagArgType.None, ["--ignore-case"] = FlagArgType.None,
            ["-v"] = FlagArgType.None, ["--invert-match"] = FlagArgType.None,
            ["-w"] = FlagArgType.None, ["--word-regexp"] = FlagArgType.None,
            ["-x"] = FlagArgType.None, ["--line-regexp"] = FlagArgType.None,
            // Output
            ["-c"] = FlagArgType.None, ["--count"] = FlagArgType.None,
            ["--color"] = FlagArgType.String,
            ["-L"] = FlagArgType.None, ["--files-without-match"] = FlagArgType.None,
            ["-l"] = FlagArgType.None, ["--files-with-matches"] = FlagArgType.None,
            ["-m"] = FlagArgType.Number, ["--max-count"] = FlagArgType.Number,
            ["-o"] = FlagArgType.None, ["--only-matching"] = FlagArgType.None,
            ["-q"] = FlagArgType.None, ["--quiet"] = FlagArgType.None, ["--silent"] = FlagArgType.None,
            ["-s"] = FlagArgType.None, ["--no-messages"] = FlagArgType.None,
            // Line prefix
            ["-b"] = FlagArgType.None, ["--byte-offset"] = FlagArgType.None,
            ["-H"] = FlagArgType.None, ["--with-filename"] = FlagArgType.None,
            ["-h"] = FlagArgType.None, ["--no-filename"] = FlagArgType.None,
            ["--label"] = FlagArgType.String,
            ["-n"] = FlagArgType.None, ["--line-number"] = FlagArgType.None,
            ["-T"] = FlagArgType.None, ["--initial-tab"] = FlagArgType.None,
            ["-Z"] = FlagArgType.None, ["--null"] = FlagArgType.None,
            // Context
            ["-A"] = FlagArgType.Number, ["--after-context"] = FlagArgType.Number,
            ["-B"] = FlagArgType.Number, ["--before-context"] = FlagArgType.Number,
            ["-C"] = FlagArgType.Number, ["--context"] = FlagArgType.Number,
            // File
            ["-a"] = FlagArgType.None, ["--text"] = FlagArgType.None,
            ["--binary-files"] = FlagArgType.String,
            ["-D"] = FlagArgType.String, ["--devices"] = FlagArgType.String,
            ["-d"] = FlagArgType.String, ["--directories"] = FlagArgType.String,
            ["--exclude"] = FlagArgType.String,
            ["--exclude-from"] = FlagArgType.String,
            ["--exclude-dir"] = FlagArgType.String,
            ["--include"] = FlagArgType.String,
            ["-r"] = FlagArgType.None, ["--recursive"] = FlagArgType.None,
            ["-R"] = FlagArgType.None, ["--dereference-recursive"] = FlagArgType.None,
            // Other
            ["--line-buffered"] = FlagArgType.None,
            ["-U"] = FlagArgType.None, ["--binary"] = FlagArgType.None,
            ["--help"] = FlagArgType.None,
            ["-V"] = FlagArgType.None, ["--version"] = FlagArgType.None,
        }),

        // ── find ────────────────────────────────────────────────────
        // Handled via regex: all flags are safe EXCEPT -delete/-exec/-execdir/
        // -ok/-okdir/-fprint/-fls/-fprintf.
        ["find"] = new CommandConfig(
            new Dictionary<string, FlagArgType>(),
            RegexCheck: FindSafeRegex),

        // ── git subcommands ─────────────────────────────────────────
        ["git status"] = GitReadOnly,
        ["git diff"] = GitReadOnly,
        ["git log"] = GitReadOnly,
        ["git show"] = GitReadOnly,
        ["git blame"] = GitReadOnly,
        ["git ls-files"] = GitReadOnly,
        ["git ls-remote"] = GitReadOnly,
        ["git rev-parse"] = GitReadOnly,
        ["git rev-list"] = GitReadOnly,
        ["git describe"] = GitReadOnly,
        ["git cat-file"] = GitReadOnly,
        ["git for-each-ref"] = GitReadOnly,
        ["git grep"] = GitReadOnly,
        ["git stash"] = new CommandConfig(new Dictionary<string, FlagArgType>(StringComparer.OrdinalIgnoreCase)
        {
            ["list"] = FlagArgType.None,
            ["show"] = FlagArgType.None,
            ["-p"] = FlagArgType.None, ["--patch"] = FlagArgType.None,
            ["--stat"] = FlagArgType.None, ["--name-only"] = FlagArgType.None,
        }, GitStashGuard),
        ["git branch"] = new CommandConfig(EmptyFlags,
            (cmd, _) => HasNoListFlag(cmd) ? "git branch without -l/--list would create a branch." : null),
        ["git tag"] = new CommandConfig(EmptyFlags,
            (cmd, _) => HasNoListFlag(cmd) ? "git tag without -l/--list would create a tag." : null),
    };

    private static string? GitSafeGuard(string cmd, string[]? _) =>
        GitSafeRegex.IsMatch(cmd) ? null : "git -c/--exec-path/--config-env injection blocked.";

    private static string? GitStashGuard(string cmd, string[]? _)
    {
        var pattern = new Regex(@"\bgit\s+stash\s+(drop|clear)\b", RegexOptions.Compiled);
        return pattern.IsMatch(cmd) ? "git stash drop/clear is destructive." : null;
    }

    private static bool HasNoListFlag(string cmd)
    {
        var hasList = new Regex(@"\s(-l\b|--list\b|--merged\b|--no-merged\b|--contains\b|--points-at\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return !hasList.IsMatch(cmd);
    }

    // ── VALIDATION ENGINE ───────────────────────────────────────────

    public (FlagValidationResult Result, string? Reason) Validate(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return (FlagValidationResult.NotInAllowlist, null);

        var tokens = Tokenize(command);
        if (tokens.Count == 0)
            return (FlagValidationResult.NotInAllowlist, null);

        // Security pre-checks (run BEFORE flag walking — Claude Code's
        // bashSecurity.ts runs these unconditionally on every command)
        for (int i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Contains('$'))
                return (FlagValidationResult.Reject, "Variable expansion is not allowed in allowlisted commands.");
            if (tokens[i].Contains('{') && (tokens[i].Contains(',') || tokens[i].Contains("..")))
                return (FlagValidationResult.Reject, "Brace expansion is not allowed in allowlisted commands.");
            if (tokens[i].Contains('\r') || tokens[i].Contains('\n'))
                return (FlagValidationResult.Reject, "Newline/carriage-return injection blocked.");
        }

        // Match longest command prefix
        var (config, args) = MatchCommand(tokens);
        if (config == null)
            return (FlagValidationResult.NotInAllowlist, null);

        // Regex check
        if (config.RegexCheck != null && !config.RegexCheck.IsMatch(command))
            return (FlagValidationResult.Reject, "Command failed allowlist regex validation.");

        // Custom danger callback
        var danger = config.AdditionalDangerousCheck?.Invoke(command, args);
        if (danger != null)
            return (FlagValidationResult.Reject, danger);

        // Flag walk
        return ValidateFlags(args, config);
    }

    /// <summary>
    /// Simple shell-quote-aware tokenizer. Splits on whitespace while
    /// respecting single-quote and double-quote boundaries.
    /// </summary>
    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;

        for (int i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (quote == null)
            {
                if (ch == '\'' || ch == '"')
                {
                    quote = ch;
                }
                else if (char.IsWhiteSpace(ch))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == quote.Value)
            {
                quote = null;
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static (CommandConfig? Config, string[] Args) MatchCommand(List<string> tokens)
    {
        for (int prefixLen = Math.Min(tokens.Count, 3); prefixLen >= 1; prefixLen--)
        {
            var key = string.Join(" ", tokens.Take(prefixLen));
            if (Allowlist.TryGetValue(key, out var config))
            {
                var args = prefixLen < tokens.Count
                    ? tokens.Skip(prefixLen).ToArray()
                    : Array.Empty<string>();
                return (config, args);
            }
        }
        return (null, Array.Empty<string>());
    }

    private static (FlagValidationResult Result, string? Reason) ValidateFlags(
        string[] args, CommandConfig config)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i];

            // POSIX -- ends option parsing
            if (token == "--")
                break;

            // Non-flag positional arg — allowed
            if (!token.StartsWith('-') || token.Length <= 1)
                continue;

            // Combined short flags: e.g. -nr → ALL must be type None
            if (token.Length > 2 && token[1] != '-' && !token.Contains('='))
            {
                foreach (var ch in token[1..])
                {
                    var sf = $"-{ch}";
                    if (!config.SafeFlags.TryGetValue(sf, out var sfType) || sfType != FlagArgType.None)
                        return (FlagValidationResult.Reject, $"Combined flag '{token}' contains unsafe or non-boolean flag '{sf}'.");
                }
                continue;
            }

            // --flag=value form
            string flag;
            string? inlineValue = null;
            var eqIdx = token.IndexOf('=');
            if (eqIdx >= 0)
            {
                flag = token[..eqIdx];
                inlineValue = token[(eqIdx + 1)..];
            }
            else
            {
                flag = token;
            }

            if (!config.SafeFlags.TryGetValue(flag, out var argType))
            {
                // Numeric shorthand: -<number> → skip (like git's -n <number>)
                if (token.Length == 2 && token[1] >= '0' && token[1] <= '9')
                    continue;
                return (FlagValidationResult.Reject, $"Flag '{flag}' is not in the allowlist for this command.");
            }

            if (argType == FlagArgType.None)
            {
                if (inlineValue != null)
                    return (FlagValidationResult.Reject, $"Flag '{flag}' does not accept a value ('{inlineValue}' given).");
                continue;
            }

            // Consume value
            string? value;
            if (inlineValue != null)
            {
                value = inlineValue;
            }
            else
            {
                i++;
                if (i >= args.Length)
                    return (FlagValidationResult.Reject, $"Flag '{flag}' requires an argument, but none was provided.");
                value = args[i];
            }

            if (argType == FlagArgType.Number)
            {
                if (!long.TryParse(value, out _))
                    return (FlagValidationResult.Reject, $"Flag '{flag}' requires a numeric argument, got '{value}'.");
            }
            // String: any non-empty value, but reject if it starts with '-' (defense-in-depth
            // against option injection — with git --sort as an exception for reverse order)
            else if (argType == FlagArgType.String)
            {
                if (value.StartsWith('-') && flag != "--sort")
                    return (FlagValidationResult.Reject, $"Flag '{flag}' argument '{value}' starts with '-' — possible option injection.");
            }
        }

        return (FlagValidationResult.Allow, null);
    }
}
