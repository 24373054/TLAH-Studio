namespace TLAHStudio.Core.Services.Tools;

/// <summary>
/// M2.9.0: Interprets shell command exit codes with tool-specific semantics.
/// Prevents false-positive "failure" classifications for tools like grep and diff.
/// </summary>
public static class CommandSemantics
{
    /// <summary>
    /// Determines if an exit code represents actual failure for the given command.
    /// </summary>
    public static bool IsExitCodeFailure(string command, int exitCode)
    {
        if (exitCode == 0) return false;

        var cmd = GetBaseCommand(command);

        // grep / rg: exit 1 = no matches (not a failure)
        if ((cmd == "grep" || cmd == "rg") && exitCode == 1) return false;

        // diff: exit 1 = files differ (not a failure), exit 2 = error
        if (cmd == "diff") return exitCode >= 2;

        // test / [: exit 1 = condition false (not a failure)
        if ((cmd == "test" || cmd == "[")) return false;

        // git diff: exit 1 = differences found
        if (cmd == "git" && command.Contains("diff") && exitCode == 1) return false;

        // Default: any non-zero is failure
        return true;
    }

    private static string GetBaseCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;
        var trimmed = command.Trim();
        // Handle piped commands — take the last one for exit code semantics
        var parts = trimmed.Split('|');
        var lastPart = parts[^1].Trim();
        // Get the first word
        var words = lastPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;
    }

    /// <summary>
    /// Known destructive commands that should trigger safety warnings.
    /// </summary>
    public static readonly HashSet<string> DestructiveCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "format", "diskpart", "shutdown", "restart", "logoff",
        "del", "erase", "rm", "rmdir", "rd", "dd",
        "mkfs", "fdisk", "parted", "chkdsk", "sfc",
        "rm -rf", "rm -r", "rd /s", "del /f", "del /q",
        "format-c", "diskutil", "bcdedit",
        "git push --force", "git push -f", "git reset --hard",
        "dropdb", "drop table", "truncate",
        "Set-ExecutionPolicy", "Remove-Item -Recurse",
        "chmod 777", // Security-sensitive
    };

    /// <summary>
    /// Check if a command appears to be destructive.
    /// </summary>
    public static bool IsDestructive(string command)
    {
        var lower = command.ToLowerInvariant().Trim();
        return DestructiveCommands.Any(dc => lower.Contains(dc.ToLowerInvariant()));
    }

    /// <summary>
    /// Known read-only commands (no side effects).
    /// </summary>
    public static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "dir", "ls", "type", "cat", "echo", "pwd", "cd",
        "get-childitem", "get-content", "get-location",
        "find", "grep", "rg", "head", "tail", "wc",
        "which", "where", "whoami", "hostname", "date", "time",
        "git status", "git log", "git diff", "git show",
        "git branch", "git tag", "git remote -v",
        "dotnet --version", "dotnet --list-sdks", "dotnet --list-runtimes",
        "python --version", "node --version", "npm --version",
        "docker ps", "docker images", "docker version",
    };
}
