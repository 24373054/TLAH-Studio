using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services;

internal static partial class WorkspaceCodeToolSupport
{
    public static string Resolve(ISandboxCommandService sandbox, Guid chatId, string path) =>
        AgentToolSupport.ResolveSandboxPath(sandbox, chatId, path);

    public static string Relative(ISandboxCommandService sandbox, Guid chatId, string path) =>
        Path.GetRelativePath(sandbox.GetSandboxRoot(chatId), path);

    public static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    public static async Task WriteTextAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
    }

    public static string BuildDiff(string oldContent, string newContent, string label = "file")
    {
        var oldLines = SplitLines(oldContent);
        var newLines = SplitLines(newContent);
        var sb = new StringBuilder();
        sb.AppendLine($"--- {label}");
        sb.AppendLine($"+++ {label}");
        var max = Math.Max(oldLines.Length, newLines.Length);
        for (var i = 0; i < max && sb.Length < 24_000; i++)
        {
            var oldLine = i < oldLines.Length ? oldLines[i] : null;
            var newLine = i < newLines.Length ? newLines[i] : null;
            if (oldLine == newLine)
                continue;
            sb.AppendLine($"@@ line {i + 1} @@");
            if (oldLine != null)
                sb.AppendLine($"- {oldLine}");
            if (newLine != null)
                sb.AppendLine($"+ {newLine}");
        }

        return sb.Length <= $"--- {label}{Environment.NewLine}+++ {label}{Environment.NewLine}".Length
            ? "No textual changes."
            : sb.ToString().TrimEnd();
    }

    public static string[] SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    public static string WildcardToRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var escaped = Regex.Escape(normalized)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", @"[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal);
        return $"^{escaped}$";
    }

    public static string HashRelativePath(string relativePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/').ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string ReadString(JsonElement root, string name, string fallback = "") =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    public static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;

    public static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static IReadOnlyList<string> ExtractPatchPaths(string patch)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in SplitLines(patch))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal) ||
                line.StartsWith("*** Update File: ", StringComparison.Ordinal) ||
                line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                paths.Add(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim());
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal) ||
                line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var path = line[4..].Trim();
                if (path == "/dev/null")
                    continue;
                if (path.StartsWith("a/", StringComparison.Ordinal) ||
                    path.StartsWith("b/", StringComparison.Ordinal))
                {
                    path = path[2..];
                }
                paths.Add(path);
                continue;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts.Skip(2).Take(2))
                {
                    var path = part.StartsWith("a/", StringComparison.Ordinal) ||
                               part.StartsWith("b/", StringComparison.Ordinal)
                        ? part[2..]
                        : part;
                    paths.Add(path);
                }
            }
        }

        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed record WorkspaceBackupMetadata(
    string Id,
    string RelativePath,
    bool Existed,
    DateTime CreatedAtUtc,
    string? BackupRelativePath);

internal static class WorkspaceBackupStore
{
    public static async Task<WorkspaceBackupMetadata> CreateAsync(
        ISandboxCommandService sandbox,
        Guid chatId,
        string fullPath,
        CancellationToken ct)
    {
        var root = sandbox.GetSandboxRoot(chatId);
        var relative = Path.GetRelativePath(root, fullPath);
        var hash = WorkspaceCodeToolSupport.HashRelativePath(relative);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupDir = Path.Combine(root, ".tlah_code_backups", hash);
        Directory.CreateDirectory(backupDir);
        var existed = File.Exists(fullPath);
        var backupPath = existed ? Path.Combine(backupDir, $"{stamp}.bak") : null;
        if (backupPath != null)
            File.Copy(fullPath, backupPath, overwrite: true);

        var metadata = new WorkspaceBackupMetadata(
            $"{hash}/{stamp}",
            relative,
            existed,
            DateTime.UtcNow,
            backupPath == null ? null : Path.GetRelativePath(root, backupPath));
        await File.WriteAllTextAsync(
            Path.Combine(backupDir, $"{stamp}.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            ct);
        return metadata;
    }

    public static async Task<WorkspaceBackupMetadata?> FindAsync(
        ISandboxCommandService sandbox,
        Guid chatId,
        string path,
        string? backupId,
        CancellationToken ct)
    {
        var root = sandbox.GetSandboxRoot(chatId);
        string? metadataPath = null;
        if (!string.IsNullOrWhiteSpace(backupId))
        {
            var safeId = backupId.Replace('\\', '/').Trim('/');
            var parts = safeId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
                metadataPath = Path.Combine(root, ".tlah_code_backups", parts[0], $"{parts[1]}.json");
        }
        else
        {
            var fullPath = WorkspaceCodeToolSupport.Resolve(sandbox, chatId, path);
            var relative = Path.GetRelativePath(root, fullPath);
            var hash = WorkspaceCodeToolSupport.HashRelativePath(relative);
            var dir = Path.Combine(root, ".tlah_code_backups", hash);
            metadataPath = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.json").OrderByDescending(p => p).FirstOrDefault()
                : null;
        }

        if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath))
            return null;

        var json = await File.ReadAllTextAsync(metadataPath, ct);
        return JsonSerializer.Deserialize<WorkspaceBackupMetadata>(json);
    }
}

public sealed class CodeReadAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeReadAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeRead,
        "Read a text file from the chat workspace with optional line slicing.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path inside the chat workspace."),
            ["start_line"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "1-based first line to read." },
            ["line_count"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum number of lines to read." },
            ["reason"] = AgentToolSupport.StringProperty("Why this file needs to be read.")
        },
        ["path"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var path = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path"));
            if (!File.Exists(path))
                return new AgentToolResult(false, string.Empty, "File not found.");

            var text = await WorkspaceCodeToolSupport.ReadTextAsync(path, ct);
            var lines = WorkspaceCodeToolSupport.SplitLines(text);
            var start = Math.Max(1, WorkspaceCodeToolSupport.ReadInt(root, "start_line", 1));
            var count = Math.Clamp(WorkspaceCodeToolSupport.ReadInt(root, "line_count", 220), 1, 2000);
            var selected = lines.Skip(start - 1).Take(count).Select((line, index) => $"{start + index,5}: {line}");
            var output = $"File: {WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, path)}\nLines: {lines.Length}\n\n" +
                         string.Join(Environment.NewLine, selected);
            return new AgentToolResult(true, AgentToolSupport.Limit(output, context.MaxOutputChars));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class CodeGlobAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeGlobAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeGlob,
        "Find files and folders in the chat workspace using wildcard patterns such as **/*.cs.",
        new Dictionary<string, object>
        {
            ["pattern"] = AgentToolSupport.StringProperty("Wildcard pattern relative to the workspace root."),
            ["path"] = AgentToolSupport.StringProperty("Relative directory to search from. Defaults to root."),
            ["max_results"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum paths to return." },
            ["reason"] = AgentToolSupport.StringProperty("Why this glob search is needed.")
        },
        ["pattern"]);

    public bool RequiresApproval => false;

    public Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return Task.FromResult(new AgentToolResult(false, string.Empty, error));
            var searchRoot = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path", "."));
            if (!Directory.Exists(searchRoot))
                return Task.FromResult(new AgentToolResult(false, string.Empty, "Search path is not a directory."));
            var pattern = WorkspaceCodeToolSupport.ReadString(root, "pattern", "**/*");
            var regex = new Regex(WorkspaceCodeToolSupport.WildcardToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var max = Math.Clamp(WorkspaceCodeToolSupport.ReadInt(root, "max_results", 200), 1, 2000);
            var baseRoot = _sandbox.GetSandboxRoot(context.ChatId);
            var matches = Directory
                .EnumerateFileSystemEntries(searchRoot, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(baseRoot, p).Replace('\\', '/'))
                .Where(p => !p.StartsWith(".tlah_code_backups/", StringComparison.OrdinalIgnoreCase))
                .Where(p => regex.IsMatch(p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToArray();
            return Task.FromResult(new AgentToolResult(
                true,
                matches.Length == 0 ? "No matches." : string.Join(Environment.NewLine, matches)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(new AgentToolResult(false, string.Empty, ex.Message));
        }
    }
}

public sealed class CodeGrepAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeGrepAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeGrep,
        "Search text files in the chat workspace using literal text or regex.",
        new Dictionary<string, object>
        {
            ["query"] = AgentToolSupport.StringProperty("Text or regex to search for."),
            ["path"] = AgentToolSupport.StringProperty("Relative file or directory path. Defaults to root."),
            ["regex"] = AgentToolSupport.BooleanProperty("Treat query as a .NET regular expression."),
            ["case_sensitive"] = AgentToolSupport.BooleanProperty("Use case-sensitive matching."),
            ["max_results"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum matches to return." },
            ["reason"] = AgentToolSupport.StringProperty("Why this search is needed.")
        },
        ["query"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var query = WorkspaceCodeToolSupport.ReadString(root, "query");
            if (string.IsNullOrWhiteSpace(query))
                return new AgentToolResult(false, string.Empty, "The query argument is required.");
            var start = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path", "."));
            var files = File.Exists(start)
                ? [start]
                : Directory.Exists(start)
                    ? Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories).ToArray()
                    : [];
            var max = Math.Clamp(WorkspaceCodeToolSupport.ReadInt(root, "max_results", 200), 1, 2000);
            var caseSensitive = WorkspaceCodeToolSupport.ReadBool(root, "case_sensitive");
            var useRegex = WorkspaceCodeToolSupport.ReadBool(root, "regex");
            var regex = useRegex
                ? new Regex(query, RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase))
                : null;
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var output = new StringBuilder();
            var count = 0;
            foreach (var file in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (ct.IsCancellationRequested || count >= max || IsProbablyBinary(file))
                    continue;
                var text = await WorkspaceCodeToolSupport.ReadTextAsync(file, ct);
                var lines = WorkspaceCodeToolSupport.SplitLines(text);
                for (var i = 0; i < lines.Length && count < max; i++)
                {
                    var matched = regex?.IsMatch(lines[i]) ?? lines[i].Contains(query, comparison);
                    if (!matched)
                        continue;
                    output.AppendLine($"{WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, file)}:{i + 1}: {lines[i]}");
                    count++;
                }
            }

            return new AgentToolResult(true, count == 0 ? "No matches." : output.ToString().TrimEnd());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static bool IsProbablyBinary(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".zip" or ".7z" or ".rar" or ".dll" or ".exe";
    }
}

public sealed class CodeDiffAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeDiffAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeDiff,
        "Preview a textual diff between an existing workspace file and proposed content.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path inside the workspace."),
            ["proposed_content"] = AgentToolSupport.StringProperty("Proposed full file content to compare."),
            ["reason"] = AgentToolSupport.StringProperty("Why this diff is needed.")
        },
        ["path", "proposed_content"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var path = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path"));
            var oldContent = File.Exists(path) ? await WorkspaceCodeToolSupport.ReadTextAsync(path, ct) : string.Empty;
            var proposed = WorkspaceCodeToolSupport.ReadString(root, "proposed_content");
            var diff = WorkspaceCodeToolSupport.BuildDiff(oldContent, proposed, WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, path));
            return new AgentToolResult(true, AgentToolSupport.Limit(diff, context.MaxOutputChars));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class CodeEditAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeEditAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeEdit,
        "Edit one file by replacing exact text, or create it when create_if_missing is true.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path inside the workspace."),
            ["old_text"] = AgentToolSupport.StringProperty("Exact text to replace. Required unless creating a missing file."),
            ["new_text"] = AgentToolSupport.StringProperty("Replacement text or new file content."),
            ["replace_all"] = AgentToolSupport.BooleanProperty("Replace every occurrence instead of only the first."),
            ["create_if_missing"] = AgentToolSupport.BooleanProperty("Create the file if it does not exist."),
            ["reason"] = AgentToolSupport.StringProperty("Why this edit is needed.")
        },
        ["path", "new_text"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var path = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path"));
            var oldText = WorkspaceCodeToolSupport.ReadString(root, "old_text");
            var newText = WorkspaceCodeToolSupport.ReadString(root, "new_text");
            var replaceAll = WorkspaceCodeToolSupport.ReadBool(root, "replace_all");
            var createIfMissing = WorkspaceCodeToolSupport.ReadBool(root, "create_if_missing");
            var oldContent = File.Exists(path) ? await WorkspaceCodeToolSupport.ReadTextAsync(path, ct) : string.Empty;
            if (!File.Exists(path) && !createIfMissing)
                return new AgentToolResult(false, string.Empty, "File not found. Set create_if_missing to true to create it.");

            string newContent;
            if (!File.Exists(path))
            {
                newContent = newText;
            }
            else
            {
                if (string.IsNullOrEmpty(oldText))
                    return new AgentToolResult(false, string.Empty, "old_text is required when editing an existing file.");
                if (!oldContent.Contains(oldText, StringComparison.Ordinal))
                    return new AgentToolResult(false, string.Empty, "old_text was not found exactly once in the file.");
                newContent = replaceAll
                    ? oldContent.Replace(oldText, newText, StringComparison.Ordinal)
                    : ReplaceFirst(oldContent, oldText, newText);
            }

            var backup = await WorkspaceBackupStore.CreateAsync(_sandbox, context.ChatId, path, ct);
            await WorkspaceCodeToolSupport.WriteTextAsync(path, newContent, ct);
            var diff = WorkspaceCodeToolSupport.BuildDiff(oldContent, newContent, WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, path));
            var artifact = await AgentToolSupport.ArtifactAsync(_sandbox.GetSandboxRoot(context.ChatId), path, ct);
            return new AgentToolResult(
                true,
                $"Edited {artifact.RelativePath}\nBackup: {backup.Id}\n\n{AgentToolSupport.Limit(diff, context.MaxOutputChars)}",
                Artifacts: [artifact]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0
            ? text
            : text[..index] + newText + text[(index + oldText.Length)..];
    }
}

public sealed class CodeMultiEditAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeMultiEditAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeMultiEdit,
        "Apply multiple exact-text replacements to one file in order.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path inside the workspace."),
            ["edits"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "Array of {old_text,new_text,replace_all} edits.",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["old_text"] = AgentToolSupport.StringProperty("Exact text to replace."),
                        ["new_text"] = AgentToolSupport.StringProperty("Replacement text."),
                        ["replace_all"] = AgentToolSupport.BooleanProperty("Replace all occurrences.")
                    },
                    ["required"] = new[] { "old_text", "new_text" },
                    ["additionalProperties"] = false
                }
            },
            ["reason"] = AgentToolSupport.StringProperty("Why these edits are needed.")
        },
        ["path", "edits"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var path = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path"));
            if (!File.Exists(path))
                return new AgentToolResult(false, string.Empty, "File not found.");
            if (!root.TryGetProperty("edits", out var editsElement) || editsElement.ValueKind != JsonValueKind.Array)
                return new AgentToolResult(false, string.Empty, "edits must be an array.");

            var oldContent = await WorkspaceCodeToolSupport.ReadTextAsync(path, ct);
            var newContent = oldContent;
            var applied = 0;
            foreach (var edit in editsElement.EnumerateArray())
            {
                var oldText = WorkspaceCodeToolSupport.ReadString(edit, "old_text");
                var newText = WorkspaceCodeToolSupport.ReadString(edit, "new_text");
                var replaceAll = WorkspaceCodeToolSupport.ReadBool(edit, "replace_all");
                if (string.IsNullOrEmpty(oldText))
                    return new AgentToolResult(false, string.Empty, "Each edit requires old_text.");
                if (!newContent.Contains(oldText, StringComparison.Ordinal))
                    return new AgentToolResult(false, string.Empty, $"old_text for edit {applied + 1} was not found.");
                newContent = replaceAll
                    ? newContent.Replace(oldText, newText, StringComparison.Ordinal)
                    : ReplaceFirst(newContent, oldText, newText);
                applied++;
            }

            var backup = await WorkspaceBackupStore.CreateAsync(_sandbox, context.ChatId, path, ct);
            await WorkspaceCodeToolSupport.WriteTextAsync(path, newContent, ct);
            var diff = WorkspaceCodeToolSupport.BuildDiff(oldContent, newContent, WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, path));
            var artifact = await AgentToolSupport.ArtifactAsync(_sandbox.GetSandboxRoot(context.ChatId), path, ct);
            return new AgentToolResult(
                true,
                $"Applied {applied} edits to {artifact.RelativePath}\nBackup: {backup.Id}\n\n{AgentToolSupport.Limit(diff, context.MaxOutputChars)}",
                Artifacts: [artifact]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0
            ? text
            : text[..index] + newText + text[(index + oldText.Length)..];
    }
}

public sealed class CodeApplyPatchAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeApplyPatchAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeApplyPatch,
        "Apply a unified diff patch to files inside the chat workspace.",
        new Dictionary<string, object>
        {
            ["patch"] = AgentToolSupport.StringProperty("Unified diff patch. Paths must be relative to the workspace root."),
            ["reason"] = AgentToolSupport.StringProperty("Why this patch is needed.")
        },
        ["patch"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var patch = WorkspaceCodeToolSupport.ReadString(root, "patch");
            if (string.IsNullOrWhiteSpace(patch))
                return new AgentToolResult(false, string.Empty, "patch is required.");
            var rootPath = _sandbox.GetSandboxRoot(context.ChatId);
            var paths = WorkspaceCodeToolSupport.ExtractPatchPaths(patch);
            if (paths.Count == 0)
                return new AgentToolResult(false, string.Empty, "No relative paths were found in the patch.");
            var backups = new List<WorkspaceBackupMetadata>();
            foreach (var path in paths)
            {
                var full = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, path);
                backups.Add(await WorkspaceBackupStore.CreateAsync(_sandbox, context.ChatId, full, ct));
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "apply --whitespace=nowarn --reject -",
                    WorkingDirectory = rootPath,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            process.Start();
            await process.StandardInput.WriteAsync(patch.AsMemory(), ct);
            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var artifacts = new List<AgentToolArtifact>();
            foreach (var path in paths)
            {
                var full = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, path);
                if (File.Exists(full))
                    artifacts.Add(await AgentToolSupport.ArtifactAsync(rootPath, full, ct));
            }

            var output = $"""
                Exit code: {process.ExitCode}
                Backups:
                {string.Join(Environment.NewLine, backups.Select(b => $"- {b.RelativePath}: {b.Id}"))}

                stdout:
                {stdout}

                stderr:
                {stderr}
                """;
            return new AgentToolResult(process.ExitCode == 0, AgentToolSupport.Limit(output, context.MaxOutputChars), process.ExitCode == 0 ? null : "git apply failed.", artifacts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class CodeRollbackAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeRollbackAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeRollback,
        "Restore a file to a backup created by edit, multi_edit, or apply_patch.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path. Used to find the latest backup when backup_id is omitted."),
            ["backup_id"] = AgentToolSupport.StringProperty("Backup id returned by a write tool, for example abcd1234/20260627010203000."),
            ["reason"] = AgentToolSupport.StringProperty("Why this rollback is needed.")
        },
        ["path"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var pathArg = WorkspaceCodeToolSupport.ReadString(root, "path");
            var backupId = WorkspaceCodeToolSupport.ReadString(root, "backup_id");
            var backup = await WorkspaceBackupStore.FindAsync(_sandbox, context.ChatId, pathArg, backupId, ct);
            if (backup == null)
                return new AgentToolResult(false, string.Empty, "No matching backup was found.");
            var rootPath = _sandbox.GetSandboxRoot(context.ChatId);
            var target = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, backup.RelativePath);
            if (!backup.Existed)
            {
                if (File.Exists(target))
                    File.Delete(target);
                return new AgentToolResult(true, $"Rolled back {backup.RelativePath} by deleting the file created after backup {backup.Id}.");
            }

            var backupPath = Path.Combine(rootPath, backup.BackupRelativePath ?? string.Empty);
            if (!File.Exists(backupPath))
                return new AgentToolResult(false, string.Empty, "Backup content file is missing.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(backupPath, target, overwrite: true);
            var artifact = await AgentToolSupport.ArtifactAsync(rootPath, target, ct);
            return new AgentToolResult(true, $"Restored {backup.RelativePath} from backup {backup.Id}.", Artifacts: [artifact]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class CodeDiagnosticsAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeDiagnosticsAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeDiagnostics,
        "Run lightweight diagnostics for JSON, XML/XAML, and C# files in the chat workspace.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file or directory path. Defaults to root."),
            ["reason"] = AgentToolSupport.StringProperty("Why diagnostics are needed.")
        });

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var start = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path", "."));
            var files = File.Exists(start)
                ? [start]
                : Directory.Exists(start)
                    ? Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories)
                        .Where(p => !p.Contains(Path.Combine(".tlah_code_backups", ""), StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                    : [];
            var diagnostics = new StringBuilder();
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".json" or ".xml" or ".xaml" or ".cs"))
                    continue;
                var rel = WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, file);
                try
                {
                    var text = await WorkspaceCodeToolSupport.ReadTextAsync(file, ct);
                    if (ext == ".json")
                    {
                        JsonDocument.Parse(text);
                    }
                    else if (ext is ".xml" or ".xaml")
                    {
                        XDocument.Parse(text);
                    }
                    else
                    {
                        var open = text.Count(c => c == '{');
                        var close = text.Count(c => c == '}');
                        if (open != close)
                            diagnostics.AppendLine($"{rel}: brace count differs ({{={open}, }}={close}).");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.AppendLine($"{rel}: {ex.Message}");
                }
            }

            return new AgentToolResult(true, diagnostics.Length == 0 ? "No diagnostics found." : diagnostics.ToString().TrimEnd());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}
