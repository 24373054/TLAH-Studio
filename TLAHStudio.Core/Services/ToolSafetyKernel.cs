using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TLAHStudio.Core.Services;

public static class ToolSafetyLevels
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Blocked = "blocked";
}

public sealed record ToolSafetyAssessment(
    string Level,
    string Category,
    bool IsReadOnly,
    bool IsWriteOperation,
    bool RequiresExplicitApproval,
    bool IsBlocked,
    string Summary,
    string? Warning,
    string PreviewJson)
{
    public static ToolSafetyAssessment LowRead(string category, string summary, object? preview = null) =>
        Create(ToolSafetyLevels.Low, category, true, false, false, false, summary, null, preview);

    public static ToolSafetyAssessment Medium(string category, bool isReadOnly, bool isWrite, string summary, object? preview = null) =>
        Create(ToolSafetyLevels.Medium, category, isReadOnly, isWrite, false, false, summary, null, preview);

    public static ToolSafetyAssessment High(string category, bool isWrite, string summary, string warning, object? preview = null) =>
        Create(ToolSafetyLevels.High, category, false, isWrite, true, false, summary, warning, preview);

    public static ToolSafetyAssessment Blocked(string category, string summary, string warning, object? preview = null) =>
        Create(ToolSafetyLevels.Blocked, category, false, true, false, true, summary, warning, preview);

    private static ToolSafetyAssessment Create(
        string level,
        string category,
        bool isReadOnly,
        bool isWrite,
        bool requiresExplicitApproval,
        bool isBlocked,
        string summary,
        string? warning,
        object? preview)
    {
        return new ToolSafetyAssessment(
            level,
            category,
            isReadOnly,
            isWrite,
            requiresExplicitApproval,
            isBlocked,
            summary,
            warning,
            JsonSerializer.Serialize(preview ?? new { }, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
    }
}

public static partial class ToolSafetyKernel
{
    private static readonly string[] ProtectedPathMarkers =
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

    public static ToolSafetyAssessment Assess(
        ISandboxCommandService sandbox,
        Guid chatId,
        string toolName,
        string argumentsJson)
    {
        var normalizedTool = AgentToolNames.Normalize(toolName);
        if (!TryParseObject(argumentsJson, out var root, out var error))
        {
            return ToolSafetyAssessment.Blocked(
                "protocol",
                "Tool arguments are not a valid JSON object.",
                error ?? "Invalid JSON arguments.");
        }

        var sandboxRoot = sandbox.GetSandboxRoot(chatId);
        return normalizedTool switch
        {
            AgentToolNames.SandboxExec => AssessCommand("PowerShell sandbox command", root, sandboxRoot),
            AgentToolNames.TerminalExec => AssessCommand("terminal command", root, sandboxRoot),
            AgentToolNames.FileList => AssessPathRead(sandbox, chatId, root, "file list"),
            AgentToolNames.FileRead => AssessPathRead(sandbox, chatId, root, "file read"),
            AgentToolNames.FileSearch => AssessPathRead(sandbox, chatId, root, "file search"),
            AgentToolNames.FileWrite => AssessFileWrite(sandbox, chatId, root),
            AgentToolNames.FileSend => AssessFileSend(sandbox, chatId, root),
            AgentToolNames.Git => AssessGit(root),
            AgentToolNames.HttpRequest => AssessHttp(root),
            AgentToolNames.WebSearch => ToolSafetyAssessment.Medium("network", true, false, "Read-only public web search through the configured allowlist."),
            AgentToolNames.BrowserRead => ToolSafetyAssessment.Medium("network", true, false, "Read-only page fetch through the configured allowlist."),
            AgentToolNames.McpListTools => ToolSafetyAssessment.Medium("mcp", true, false, "Read MCP tool metadata from configured servers."),
            AgentToolNames.McpListResources => ToolSafetyAssessment.Medium("mcp", true, false, "Read MCP resource metadata from configured servers."),
            AgentToolNames.McpReadResource => ToolSafetyAssessment.Medium("mcp", true, false, "Read MCP resource content from a configured server."),
            AgentToolNames.McpCall => ToolSafetyAssessment.High("mcp", true, "Call an external MCP tool.", "MCP tools can perform actions outside TLAH Studio depending on the server."),
            AgentToolNames.MemoryRead => ToolSafetyAssessment.LowRead("memory", "Read the project memory file."),
            AgentToolNames.MemoryWrite => ToolSafetyAssessment.Medium("memory", false, true, "Update the project memory file."),
            AgentToolNames.CodeRead or
            AgentToolNames.CodeGrep or
            AgentToolNames.CodeGlob or
            AgentToolNames.CodeDiff or
            AgentToolNames.CodeDiagnostics => AssessPathRead(sandbox, chatId, root, normalizedTool),
            AgentToolNames.CodeEdit or
            AgentToolNames.CodeMultiEdit => AssessCodeWrite(sandbox, chatId, root, normalizedTool),
            AgentToolNames.CodeApplyPatch => AssessCodePatch(sandbox, chatId, root),
            AgentToolNames.CodeRollback => AssessCodeRollback(sandbox, chatId, root),
            _ => ToolSafetyAssessment.Blocked("tool", $"Unknown tool: {toolName}", "The requested tool is not registered.")
        };
    }

    private static ToolSafetyAssessment AssessCommand(
        string label,
        JsonElement root,
        string sandboxRoot)
    {
        var command = ReadString(root, "command").Trim();
        if (string.IsNullOrWhiteSpace(command))
            return ToolSafetyAssessment.Blocked("command", $"{label} is empty.", "The command argument is required.");

        var lower = command.ToLowerInvariant();
        foreach (var marker in ProtectedPathMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
            {
                return ToolSafetyAssessment.Blocked(
                    "path",
                    $"{label} references a protected host path marker.",
                    $"Blocked marker: {marker}");
            }
        }

        foreach (Match match in AbsoluteWindowsPathRegex().Matches(command))
        {
            var rawPath = match.Groups[1].Value.Trim();
            try
            {
                var fullPath = Path.GetFullPath(rawPath);
                if (!IsUnderDirectory(fullPath, sandboxRoot))
                {
                    return ToolSafetyAssessment.Blocked(
                        "path",
                        $"{label} references an absolute host path outside the sandbox.",
                        rawPath);
                }
            }
            catch
            {
                return ToolSafetyAssessment.Blocked(
                    "path",
                    $"{label} contains an invalid absolute path.",
                    rawPath);
            }
        }

        if (SystemDestructiveCommandRegex().IsMatch(command))
        {
            return ToolSafetyAssessment.Blocked(
                "command",
                $"{label} contains a system-level destructive or privileged operation.",
                "This command is blocked before execution.",
                new { command });
        }

        if (DangerousCommandRegex().IsMatch(command))
        {
            return ToolSafetyAssessment.High(
                "command",
                isWrite: true,
                $"{label} may delete, rewrite history, or force-change repository state.",
                "Review the command carefully. Prefer a dry-run, diff, or typed file/Git tool first.",
                new { command, suggestedDryRun = SuggestDryRun(command) });
        }

        if (ReadOnlyCommandRegex().IsMatch(command))
        {
            return ToolSafetyAssessment.LowRead(
                "command",
                $"{label} appears read-only.",
                new { command });
        }

        if (WriteCommandRegex().IsMatch(command))
        {
            return ToolSafetyAssessment.Medium(
                "command",
                isReadOnly: false,
                isWrite: true,
                $"{label} writes inside the sandbox.",
                new { command });
        }

        return ToolSafetyAssessment.Medium(
            "command",
            isReadOnly: false,
            isWrite: true,
            $"{label} requires sandbox execution review.",
            new { command });
    }

    private static ToolSafetyAssessment AssessPathRead(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root,
        string category)
    {
        var rawPath = ReadString(root, "path", ".");
        var resolved = ResolvePath(sandbox, chatId, rawPath);
        return resolved.Error == null
            ? ToolSafetyAssessment.LowRead(category, $"{category} is constrained to the chat sandbox.", new
            {
                path = resolved.RelativePath
            })
            : ToolSafetyAssessment.Blocked("path", $"{category} path escapes the chat sandbox.", resolved.Error);
    }

    private static ToolSafetyAssessment AssessFileWrite(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root)
    {
        var rawPath = ReadString(root, "path");
        var resolved = ResolvePath(sandbox, chatId, rawPath);
        if (resolved.Error != null)
            return ToolSafetyAssessment.Blocked("path", "File write path escapes the chat sandbox.", resolved.Error);

        var content = ReadString(root, "content");
        var append = ReadBool(root, "append");
        var newBytes = Encoding.UTF8.GetByteCount(content);
        var oldBytes = File.Exists(resolved.FullPath)
            ? new FileInfo(resolved.FullPath).Length
            : 0;
        var preview = new
        {
            path = resolved.RelativePath,
            operation = append ? "append" : File.Exists(resolved.FullPath) ? "replace" : "create",
            existed = File.Exists(resolved.FullPath),
            oldBytes,
            newBytes,
            diff = BuildFileDiffPreview(resolved.FullPath, content, append)
        };

        return ToolSafetyAssessment.Medium(
            "file_write",
            isReadOnly: false,
            isWrite: true,
            $"File write is constrained to {resolved.RelativePath}.",
            preview);
    }

    private static ToolSafetyAssessment AssessFileSend(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root)
    {
        var rawPath = ReadString(root, "path");
        var resolved = ResolvePath(sandbox, chatId, rawPath);
        if (resolved.Error != null)
            return ToolSafetyAssessment.Blocked("path", "File send path escapes the chat sandbox.", resolved.Error);

        var exists = File.Exists(resolved.FullPath);
        var sizeBytes = exists ? new FileInfo(resolved.FullPath!).Length : 0;
        var caption = ReadString(root, "caption");
        return ToolSafetyAssessment.LowRead(
            "file_send",
            exists
                ? $"Send sandbox file {resolved.RelativePath} to the chat."
                : $"Requested file {resolved.RelativePath} does not exist yet.",
            new
            {
                path = resolved.RelativePath,
                exists,
                sizeBytes,
                caption
            });
    }

    private static ToolSafetyAssessment AssessGit(JsonElement root)
    {
        var operation = ReadString(root, "operation").Trim().ToLowerInvariant();
        var args = root.TryGetProperty("arguments", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToArray()
            : [];
        if (args.Any(a => a.IndexOfAny([';', '|', '&', '\r', '\n']) >= 0))
        {
            return ToolSafetyAssessment.Blocked(
                "git",
                "Git arguments contain shell metacharacters.",
                "Use structured Git arguments only.");
        }

        if (operation is "status" or "diff" or "log")
        {
            return ToolSafetyAssessment.LowRead(
                "git",
                $"git {operation} is read-only.",
                new { operation, arguments = args });
        }

        if (operation is "reset" or "clean" or "push")
        {
            return ToolSafetyAssessment.High(
                "git",
                isWrite: true,
                $"git {operation} may be destructive.",
                "Use project-level allow only after reviewing the target repository state.",
                new { operation, arguments = args });
        }

        return ToolSafetyAssessment.Medium(
            "git",
            isReadOnly: false,
            isWrite: true,
            $"git {operation} changes repository state.",
            new { operation, arguments = args });
    }

    private static ToolSafetyAssessment AssessHttp(JsonElement root)
    {
        var method = ReadString(root, "method", "GET").Trim().ToUpperInvariant();
        var url = ReadString(root, "url");
        return method is "GET" or "HEAD"
            ? ToolSafetyAssessment.Medium("network", true, false, $"HTTP {method} request is read-only and allowlist-checked.", new { method, url })
            : ToolSafetyAssessment.High("network", true, $"HTTP {method} can change remote state.", "Review the target domain, request body, and credential before allowing.", new { method, url });
    }

    private static ToolSafetyAssessment AssessCodeWrite(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root,
        string toolName)
    {
        var rawPath = ReadString(root, "path");
        var resolved = ResolvePath(sandbox, chatId, rawPath);
        if (resolved.Error != null)
            return ToolSafetyAssessment.Blocked("path", $"{toolName} path escapes the chat sandbox.", resolved.Error);

        var oldBytes = File.Exists(resolved.FullPath!)
            ? new FileInfo(resolved.FullPath!).Length
            : 0;
        var writeCount = 1;
        if (root.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
            writeCount = edits.GetArrayLength();

        return ToolSafetyAssessment.Medium(
            "code_write",
            isReadOnly: false,
            isWrite: true,
            $"{toolName} will write {resolved.RelativePath} and create a rollback backup.",
            new
            {
                path = resolved.RelativePath,
                existed = File.Exists(resolved.FullPath!),
                oldBytes,
                writeCount
            });
    }

    private static ToolSafetyAssessment AssessCodePatch(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root)
    {
        var patch = ReadString(root, "patch");
        if (string.IsNullOrWhiteSpace(patch))
            return ToolSafetyAssessment.Blocked("code_patch", "Patch is empty.", "The patch argument is required.");

        var paths = WorkspaceCodeToolSupport.ExtractPatchPaths(patch);
        if (paths.Count == 0)
            return ToolSafetyAssessment.Blocked("code_patch", "Patch does not declare any file paths.", "Use a unified diff with relative paths.");

        var resolved = new List<string>();
        foreach (var path in paths)
        {
            var item = ResolvePath(sandbox, chatId, path);
            if (item.Error != null)
                return ToolSafetyAssessment.Blocked("path", "Patch path escapes the chat sandbox.", item.Error);
            resolved.Add(item.RelativePath!);
        }

        return ToolSafetyAssessment.Medium(
            "code_patch",
            isReadOnly: false,
            isWrite: true,
            $"Patch will update {resolved.Count} workspace file(s) and create rollback backups.",
            new { paths = resolved });
    }

    private static ToolSafetyAssessment AssessCodeRollback(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root)
    {
        var rawPath = ReadString(root, "path");
        var resolved = ResolvePath(sandbox, chatId, rawPath);
        if (resolved.Error != null)
            return ToolSafetyAssessment.Blocked("path", "Rollback path escapes the chat sandbox.", resolved.Error);

        return ToolSafetyAssessment.Medium(
            "code_rollback",
            isReadOnly: false,
            isWrite: true,
            $"Rollback may restore or delete {resolved.RelativePath} from a previous backup.",
            new
            {
                path = resolved.RelativePath,
                backupId = ReadString(root, "backup_id")
            });
    }

    private static bool TryParseObject(
        string json,
        out JsonElement root,
        out string? error)
    {
        try
        {
            root = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone();
            if (root.ValueKind == JsonValueKind.Object)
            {
                error = null;
                return true;
            }

            error = "Tool arguments must be a JSON object.";
            return false;
        }
        catch (JsonException ex)
        {
            root = default;
            error = ex.Message;
            return false;
        }
    }

    private static (string? FullPath, string? RelativePath, string? Error) ResolvePath(
        ISandboxCommandService sandbox,
        Guid chatId,
        string relativePath)
    {
        try
        {
            var fullPath = AgentToolSupport.ResolveSandboxPath(sandbox, chatId, relativePath);
            return (fullPath, Path.GetRelativePath(sandbox.GetSandboxRoot(chatId), fullPath), null);
        }
        catch (Exception ex)
        {
            return (null, null, ex.Message);
        }
    }

    private static string BuildFileDiffPreview(
        string? fullPath,
        string newContent,
        bool append)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            return LimitLines(SplitLines(newContent).Select(l => $"+ {l}"), 40);

        var info = new FileInfo(fullPath);
        if (info.Length > 64 * 1024)
            return "Existing file is larger than 64 KiB; diff preview skipped.";

        var oldContent = File.ReadAllText(fullPath);
        var after = append ? oldContent + newContent : newContent;
        var oldLines = SplitLines(oldContent);
        var newLines = SplitLines(after);
        var sb = new StringBuilder();
        var max = Math.Max(oldLines.Length, newLines.Length);
        for (var i = 0; i < max && sb.Length < 4000; i++)
        {
            var oldLine = i < oldLines.Length ? oldLines[i] : null;
            var newLine = i < newLines.Length ? newLines[i] : null;
            if (oldLine == newLine)
                continue;
            if (oldLine != null)
                sb.AppendLine($"- {oldLine}");
            if (newLine != null)
                sb.AppendLine($"+ {newLine}");
        }

        return string.IsNullOrWhiteSpace(sb.ToString())
            ? "No textual changes."
            : sb.ToString().TrimEnd();
    }

    private static string LimitLines(IEnumerable<string> lines, int maxLines) =>
        string.Join(Environment.NewLine, lines.Take(maxLines));

    private static string SuggestDryRun(string command)
    {
        if (command.Contains("Remove-Item", StringComparison.OrdinalIgnoreCase))
            return "Use Get-ChildItem or Remove-Item -WhatIf first.";
        if (command.Contains("git reset", StringComparison.OrdinalIgnoreCase))
            return "Use git status and git diff before resetting.";
        if (command.Contains("git clean", StringComparison.OrdinalIgnoreCase))
            return "Use git clean -ndx before git clean -fdx.";
        return "Run a read-only inspection command first.";
    }

    private static bool IsUnderDirectory(string path, string parent)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullParent, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadString(JsonElement root, string name, string fallback = "") =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"(?<![\w])([A-Za-z]:\\[^""'\r\n;|&<>]*)", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteWindowsPathRegex();

    [GeneratedRegex(@"(?ix)\b(format|diskpart|shutdown|restart-computer|stop-computer|bcdedit|bootrec|takeown|icacls|set-executionpolicy|new-localuser|net\s+user|sc(?:\.exe)?|schtasks|start-process[\s\S]*-verb\s+runas|invoke-expression|iex|reg(?:\.exe)?\s+(?:add|delete|import|restore|save)|(?:curl|wget|invoke-webrequest|invoke-restmethod)[\s\S]*\|\s*(?:powershell|pwsh|cmd|sh|bash))\b", RegexOptions.CultureInvariant)]
    private static partial Regex SystemDestructiveCommandRegex();

    [GeneratedRegex(@"(?ix)\b(remove-item|del|erase|rmdir|rd|git\s+reset\s+--hard|git\s+clean\s+-[dfx]+|git\s+push[\s\S]*--force)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DangerousCommandRegex();

    [GeneratedRegex(@"(?ix)^\s*(get-childitem|gci|dir|ls|pwd|get-location|get-content|cat|type|select-string|test-path|get-item|measure-object|git\s+(status|log|diff|show|rev-parse|branch))\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReadOnlyCommandRegex();

    [GeneratedRegex(@"(?ix)\b(set-content|add-content|out-file|new-item|copy-item|move-item|git\s+(init|add|commit|branch))\b|(?<![<>])>(?!>)|>>", RegexOptions.CultureInvariant)]
    private static partial Regex WriteCommandRegex();

    private static string[] SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
