using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.Tools.Models;
using TLAHStudio.Core.Services.Workspace;

namespace TLAHStudio.Core.Services.Tools.PerTool;

/// <summary>
/// M2.11.0: Read a file with line range support.
/// </summary>
public class CodeReadToolV3 : AgentToolV3Base
{
    public override LlmToolDefinition Definition => new("read",
        "Read a file. Supports offset and limit for reading specific ranges.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "File path relative to workspace root" },
                ["offset"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Start line number (1-based)" },
                ["limit"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Max lines to read (default 200)" }
            },
            ["required"] = new[] { "path" }
        });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        var path = args?.GetValueOrDefault("path") ?? "";
        var offset = int.TryParse(args?.GetValueOrDefault("offset", "1"), out var o) ? Math.Max(1, o) : 1;
        var limit = int.TryParse(args?.GetValueOrDefault("limit", "200"), out var l) ? Math.Clamp(l, 1, 500) : 200;

        var fullPath = ResolvePath(context.ChatId, path);
        if (!File.Exists(fullPath))
            return new AgentToolResult(false, string.Empty, $"File not found: {path}");
        if (IsBinary(fullPath))
            return new AgentToolResult(false, string.Empty, $"Cannot read binary file: {path}");

        var lines = await File.ReadAllLinesAsync(fullPath, Encoding.UTF8, ct);
        if (offset > lines.Length)
            return new AgentToolResult(true, $"(file has {lines.Length} lines, offset {offset} is beyond EOF)");
        var end = Math.Min(offset + limit - 1, lines.Length);
        var result = new StringBuilder();
        result.AppendLine($"# {path} (lines {offset}-{end} of {lines.Length})");
        for (int i = offset - 1; i < end; i++)
            result.AppendLine($"{i + 1:D4}|{lines[i]}");
        return new AgentToolResult(true, SecretRedactor.RedactText(result.ToString()));
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        var path = args?.GetValueOrDefault("path") ?? "";
        var fullPath = ResolvePath(chatId, path);
        return Task.FromResult(ToolEffectPlan.ReadOnly([fullPath]));
    }

    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;

    private static string ResolvePath(Guid chatId, string path)
    {
        var root = WorkspaceRootStore.GetRoot(chatId, out _);
        var normalized = path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, normalized));
    }

    private static bool IsBinary(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".exe" or ".dll" or ".so" or ".dylib" or ".obj" or ".pdb" or ".jpg" or ".jpeg" or ".png" or ".gif" or ".ico" or ".mp3" or ".mp4" or ".zip" or ".tar" or ".gz" or ".7z";
    }
}

/// <summary>
/// M2.11.0: Edit a file with exact-match safety.
/// </summary>
public class CodeEditToolV3 : AgentToolV3Base
{
    public override LlmToolDefinition Definition => new("edit",
        "Edit a file by replacing a specific string. The old_string must match exactly.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "File path relative to workspace root" },
                ["old_string"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Exact string to replace" },
                ["new_string"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Replacement string" },
                ["replace_all"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Replace all occurrences (default false)" }
            },
            ["required"] = new[] { "path", "old_string", "new_string" }
        });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var path = root.GetProperty("path").GetString() ?? "";
        var oldStr = root.GetProperty("old_string").GetString() ?? "";
        var newStr = root.GetProperty("new_string").GetString() ?? "";
        var replaceAll = root.TryGetProperty("replace_all", out var ra) && ra.GetBoolean();

        var fullPath = ResolvePath(context.ChatId, path);
        if (!File.Exists(fullPath))
            return new AgentToolResult(false, string.Empty, $"File not found: {path}");

        var content = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        var count = 0;
        var idx = content.IndexOf(oldStr, StringComparison.Ordinal);
        if (idx < 0)
            return new AgentToolResult(false, string.Empty, $"old_string not found in {path}. The file may have changed.");

        var sb = new StringBuilder(content);
        while (idx >= 0)
        {
            sb.Remove(idx, oldStr.Length);
            sb.Insert(idx, newStr);
            count++;
            if (!replaceAll) break;
            idx = sb.ToString().IndexOf(oldStr, idx + newStr.Length, StringComparison.Ordinal);
        }

        await File.WriteAllTextAsync(fullPath, sb.ToString(), Encoding.UTF8, ct);
        return new AgentToolResult(true, $"Edited {path}: {count} replacement(s) made.");
    }

    public override Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString() ?? "";
        var fullPath = ResolvePath(chatId, path);
        return Task.FromResult(ToolEffectPlan.Write([fullPath], [fullPath]));
    }

    public override Task<ToolRollbackPlan?> CreateRollbackPlanAsync(string argumentsJson, AgentToolResult result, CancellationToken ct = default)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString() ?? "";
        return Task.FromResult<ToolRollbackPlan?>(new ToolRollbackPlan(true, "Use git to revert the file or restore from backup.",
            $"git checkout -- {path}", [path]));
    }

    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.All;
    private static string ResolvePath(Guid chatId, string path)
    {
        var root = WorkspaceRootStore.GetRoot(chatId, out _);
        return Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
    }
}

/// <summary>
/// M2.11.0: Glob file search.
/// </summary>
public class CodeGlobToolV3 : AgentToolV3Base
{
    public override LlmToolDefinition Definition => new("glob",
        "Find files matching a glob pattern.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["pattern"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Glob pattern (e.g., **/*.cs)" },
                ["path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Search directory (defaults to workspace root)" }
            },
            ["required"] = new[] { "pattern" }
        });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var pattern = doc.RootElement.GetProperty("pattern").GetString() ?? "*";
        var searchPath = doc.RootElement.TryGetProperty("path", out var sp) ? sp.GetString() ?? "." : ".";
        var root = ResolvePath(context.ChatId, searchPath);
        if (!Directory.Exists(root))
            root = ResolvePath(context.ChatId, ".");

        var files = await Task.Run(() =>
        {
            try
            {
                return System.IO.Directory.GetFiles(root, pattern,
                    new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 8 })
                    .Take(50).Select(f => f[(root.Length + 1)..]).ToList();
            }
            catch { return []; }
        }, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Glob results for \"{pattern}\" ({files.Count} files):");
        foreach (var f in files) sb.AppendLine($"  {f}");
        if (files.Count == 50) sb.AppendLine("  ... (results truncated at 50)");
        return new AgentToolResult(true, sb.ToString());
    }

    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
    private static string ResolvePath(Guid chatId, string path) =>
        Path.GetFullPath(Path.Combine(
            WorkspaceRootStore.GetRoot(chatId, out _),
            path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
}

/// <summary>
/// M2.11.0: Content search with ripgrep fallback.
/// </summary>
public class CodeGrepToolV3 : AgentToolV3Base
{
    public override LlmToolDefinition Definition => new("grep",
        "Search file contents with regex. Uses ripgrep if available, falls back to .NET search.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["pattern"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Regular expression to search for" },
                ["path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Directory or file to search" },
                ["glob"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "File pattern filter (e.g., *.cs)" }
            },
            ["required"] = new[] { "pattern" }
        });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var pattern = doc.RootElement.GetProperty("pattern").GetString() ?? "";
        var searchPath = doc.RootElement.TryGetProperty("path", out var sp) ? sp.GetString() : null;
        var glob = doc.RootElement.TryGetProperty("glob", out var g) ? g.GetString() : null;
        var root = ResolvePath(context.ChatId, searchPath ?? ".");

        // Try ripgrep first
        var lines = await TryRipgrepAsync(root, pattern, glob, ct);
        if (lines == null)
        {
            // Fallback to .NET recursive search
            lines = await DotNetSearchAsync(root, pattern, glob, ct);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Grep for \"{pattern}\" {(lines?.Count ?? 0)} matches:");
        if (lines != null)
            foreach (var line in lines.Take(40))
                sb.AppendLine(line);
        if ((lines?.Count ?? 0) > 40)
            sb.AppendLine($"  ... ({lines!.Count - 40} more matches)");
        return new AgentToolResult(true, SecretRedactor.RedactText(sb.ToString()));
    }

    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;

    private static async Task<List<string>?> TryRipgrepAsync(string root, string pattern, string? glob, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "rg.exe",
                Arguments = $"--line-number --no-heading --max-count 40 {(glob != null ? $"--glob {glob}" : "")} {EscapeArg(pattern)} {root}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode is 0 or 1
                ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList()
                : null;
        }
        catch { return null; }
    }

    private static async Task<List<string>> DotNetSearchAsync(string root, string pattern, string? glob, CancellationToken ct)
    {
        var results = new List<string>();
        if (!Directory.Exists(root)) return results;
        var files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => glob == null || MatchesGlob(Path.GetFileName(f), glob))
            .Take(100);
        foreach (var file in files)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(file, ct);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(lines[i], pattern))
                        results.Add($"{file[(root.Length + 1)..]}:{i + 1}: {lines[i][..Math.Min(lines[i].Length, 200)]}");
                }
            }
            catch { /* skip unreadable files */ }
            if (results.Count >= 40) break;
        }
        return results;
    }

    private static bool MatchesGlob(string fileName, string glob) =>
        System.Text.RegularExpressions.Regex.IsMatch(fileName,
            "^" + System.Text.RegularExpressions.Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string EscapeArg(string arg) => $"\"{arg.Replace("\"", "\\\"")}\"";
    private static string ResolvePath(Guid chatId, string path) =>
        Path.GetFullPath(Path.Combine(
            WorkspaceRootStore.GetRoot(chatId, out _),
            (path ?? ".").Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
}

/// <summary>
/// M2.11.0: Compute file diff.
/// </summary>
public class CodeDiffToolV3 : AgentToolV3Base
{
    public override LlmToolDefinition Definition => new("diff",
        "Show the diff of changes made to a file or between two versions.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["path"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "File path to diff" }
            },
            ["required"] = new[] { "path" }
        });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(argumentsJson);
        var path = doc.RootElement.GetProperty("path").GetString() ?? "";
        var root = WorkspaceRootStore.GetRoot(context.ChatId, out _);
        var fullPath = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));

        // Try git diff
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git", Arguments = $"diff -- {EscapeArg(fullPath)}",
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = await proc.StandardOutput.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (!string.IsNullOrWhiteSpace(output))
                    return new AgentToolResult(true, output);
            }
        }
        catch { }

        return new AgentToolResult(true, $"(no diff available for {path})");
    }

    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
    private static string EscapeArg(string arg) => $"\"{arg.Replace("\"", "\\\"")}\"";
}

/// <summary>
/// M2.11.0: File change detector.
/// </summary>
public interface IFileChangeDetector
{
    Task<bool> HasChangedAsync(string filePath, string expectedSha256, CancellationToken ct = default);
    Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default);
}

public class FileChangeDetector : IFileChangeDetector
{
    public Task<bool> HasChangedAsync(string filePath, string expectedSha256, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return Task.FromResult(true);
        var actual = ComputeSha256(filePath);
        return Task.FromResult(!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase));
    }

    public Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
        => Task.FromResult(ComputeSha256(filePath));

    private static string ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = System.Security.Cryptography.SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch { return "unknown"; }
    }
}
