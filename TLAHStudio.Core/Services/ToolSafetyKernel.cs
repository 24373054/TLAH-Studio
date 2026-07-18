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
    bool CanOverrideBlock,
    bool BypassImmune,
    string Summary,
    string? Warning,
    string PreviewJson)
{
    public static ToolSafetyAssessment LowRead(string category, string summary, object? preview = null) =>
        Create(ToolSafetyLevels.Low, category, true, false, false, false, false, false, summary, null, preview);

    public static ToolSafetyAssessment Medium(string category, bool isReadOnly, bool isWrite, string summary, object? preview = null) =>
        Create(ToolSafetyLevels.Medium, category, isReadOnly, isWrite, false, false, false, false, summary, null, preview);

    public static ToolSafetyAssessment High(string category, bool isWrite, string summary, string warning, object? preview = null, bool bypassImmune = false) =>
        Create(ToolSafetyLevels.High, category, false, isWrite, true, false, false, bypassImmune, summary, warning, preview);

    public static ToolSafetyAssessment Blocked(string category, string summary, string warning, object? preview = null) =>
        Create(ToolSafetyLevels.Blocked, category, false, true, false, true, false, false, summary, warning, preview);

    /// <summary>
    /// A contextual restriction that Ask mode may override once and Full access
    /// may bypass. Invalid input and catastrophic operations must use Blocked.
    /// </summary>
    public static ToolSafetyAssessment Restricted(
        string category,
        string summary,
        string warning,
        object? preview = null,
        bool isReadOnly = false,
        bool isWrite = true) =>
        Create(ToolSafetyLevels.Blocked, category, isReadOnly, isWrite, true, true, true, false, summary, warning, preview);

    private static ToolSafetyAssessment Create(
        string level,
        string category,
        bool isReadOnly,
        bool isWrite,
        bool requiresExplicitApproval,
        bool isBlocked,
        bool canOverrideBlock,
        bool bypassImmune,
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
            canOverrideBlock,
            bypassImmune,
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
            AgentToolNames.FileInfo => AssessPathRead(sandbox, chatId, root, "file info"),
            AgentToolNames.FileWrite => AssessFileWrite(sandbox, chatId, root),
            AgentToolNames.FileMkdir => AssessFileMkdir(sandbox, chatId, root),
            AgentToolNames.FileMove => AssessFileMove(sandbox, chatId, root),
            AgentToolNames.FileDelete => AssessFileDelete(sandbox, chatId, root),
            AgentToolNames.FileSend => AssessFileSend(sandbox, chatId, root),
            AgentToolNames.Git => AssessGit(root),
            AgentToolNames.HttpRequest => AssessHttp(root),
            AgentToolNames.WebSearch => ToolSafetyAssessment.Medium("network", true, false, "Read-only public web search through the configured allowlist."),
            AgentToolNames.BrowserRead => ToolSafetyAssessment.Medium("network", true, false, "Read-only page fetch through the configured allowlist."),
            AgentToolNames.ResearchVerify => AssessResearchVerify(root),
            AgentToolNames.SpreadsheetInspect => AssessPathRead(sandbox, chatId, root, "spreadsheet inspect"),
            AgentToolNames.DocumentInspect => AssessPathRead(sandbox, chatId, root, "document inspect"),
            AgentToolNames.SpreadsheetCreate => AssessArtifactWrite(
                sandbox, chatId, root, "spreadsheet create", "artifacts/workbook.xlsx"),
            AgentToolNames.SpreadsheetUpdate => AssessArtifactWrite(
                sandbox, chatId, root, "spreadsheet update", "artifacts/workbook.xlsx", "output_path", validateSourcePath: true),
            AgentToolNames.DocumentCreate => AssessArtifactWrite(
                sandbox, chatId, root, "document create", "artifacts/document.docx"),
            AgentToolNames.DiagramCreate => AssessArtifactWrite(
                sandbox, chatId, root, "diagram create", "artifacts/diagram"),
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
            AgentToolNames.CodeDiagnostics or
            AgentToolNames.CodeSymbols => AssessPathRead(sandbox, chatId, root, normalizedTool),
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

        var catastrophic = CatastrophicCommandAnalyzer.Analyze(command, sandboxRoot);
        if (catastrophic.IsCatastrophic)
        {
            return ToolSafetyAssessment.Blocked(
                "command",
                $"{label} contains a catastrophic system or root deletion operation.",
                "Catastrophic disk, boot, account, or root-recursive deletion commands cannot be approved in any permission mode.",
                new { command, evidence = catastrophic.Evidence });
        }

        if (catastrophic.IsOpaque)
        {
            return ToolSafetyAssessment.High(
                "command",
                isWrite: true,
                $"{label} uses a script or wrapper whose contents cannot be fully inspected.",
                "Review the wrapper payload carefully. Full access may execute it, but static analysis cannot prove its effects.",
                new { command, evidence = catastrophic.Evidence });
        }

        var pathScanText = PathRelevantCommandText(command);
        var lowerPathScanText = pathScanText.ToLowerInvariant();
        foreach (var marker in ProtectedPathMarkers)
        {
            if (lowerPathScanText.Contains(marker, StringComparison.Ordinal))
            {
                return ToolSafetyAssessment.Restricted(
                    "path",
                    $"{label} references a protected host path marker.",
                    $"Restricted marker: {marker}",
                    new { command, marker },
                    isReadOnly: ReadOnlyCommandRegex().IsMatch(command),
                    isWrite: !ReadOnlyCommandRegex().IsMatch(command));
            }
        }

        foreach (Match match in AbsoluteWindowsPathRegex().Matches(pathScanText))
        {
            var rawPath = match.Groups[1].Value.Trim();
            try
            {
                var fullPath = Path.GetFullPath(rawPath);
                if (!IsUnderDirectory(fullPath, sandboxRoot))
                {
                    return ToolSafetyAssessment.Restricted(
                        "path",
                        $"{label} references an absolute host path outside the sandbox.",
                        rawPath,
                        new { command, path = rawPath },
                        isReadOnly: ReadOnlyCommandRegex().IsMatch(command),
                        isWrite: !ReadOnlyCommandRegex().IsMatch(command));
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
            return ToolSafetyAssessment.High(
                "command",
                isWrite: true,
                $"{label} contains a system-level destructive or privileged operation.",
                "Review the command carefully. Ask mode requires approval; Full access may execute it unless it is catastrophic.",
                new { command });
        }

        if (BypassImmunePathRegex().IsMatch(pathScanText) &&
            !ReadOnlyCommandRegex().IsMatch(command))
        {
            return ToolSafetyAssessment.High(
                "path",
                isWrite: true,
                "This command targets a sensitive repository, environment, or shell configuration path.",
                "Review the exact target. Ask and Auto modes require explicit approval.",
                new { command },
                bypassImmune: true);
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

    /// <summary>
    /// Returns a sensitive-path High assessment for repository metadata,
    /// environment files, and shell configuration. Ask/Auto require approval;
    /// Full access may proceed because this is not a catastrophic hard deny.
    /// </summary>
    private static ToolSafetyAssessment? CheckBypassImmunePath(string fullPath)
    {
        // Extract the path components and check each one against the regex.
        // We check both the full path and just the filename so that commands
        // like "rm .git" and "edit path=.env" are both caught.
        var fileName = Path.GetFileName(fullPath);
        var check = string.IsNullOrEmpty(fileName) ? fullPath : $"{fullPath}|{fileName}";
        if (BypassImmunePathRegex().IsMatch(check))
        {
            return ToolSafetyAssessment.High(
                "path",
                isWrite: true,
                $"This operation targets a protected file or directory ({fileName}).",
                "Ask and Auto modes require explicit approval for sensitive paths.",
                new { path = fullPath },
                bypassImmune: true);
        }
        return null;
    }

    private static ToolSafetyAssessment AssessPathRead(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root,
        string category)
    {
        var rawPath = ReadString(root, "path", ".");
        var resolved = ResolvePathForAuthorization(sandbox, chatId, rawPath);
        if (resolved.Error != null)
            return ToolSafetyAssessment.Blocked("path", $"{category} path is invalid.", resolved.Error);
        if (resolved.IsOutsideSandbox)
        {
            return ToolSafetyAssessment.Restricted(
                "path",
                $"{category} targets a host path outside the chat sandbox.",
                "Ask mode requires approval for this exact path; Full access may proceed.",
                new { path = resolved.FullPath },
                isReadOnly: true,
                isWrite: false);
        }

        return ToolSafetyAssessment.LowRead(category, $"{category} is constrained to the chat sandbox.", new
        {
            path = resolved.RelativePath
        });
    }

    private static ToolSafetyAssessment AssessFileWrite(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root)
    {
        var rawPath = ReadString(root, "path");
        var resolved = ResolvePathForAuthorization(sandbox, chatId, rawPath);
        if (resolved.Error != null)
            return ToolSafetyAssessment.Blocked("path", "File write path is invalid.", resolved.Error);
        if (resolved.IsOutsideSandbox)
        {
            return ToolSafetyAssessment.Restricted(
                "path",
                "File write targets a host path outside the chat sandbox.",
                "Ask mode requires approval for this exact path; Full access may proceed.",
                new
                {
                    path = resolved.FullPath,
                    operation = ReadBool(root, "append") ? "append" : File.Exists(resolved.FullPath) ? "replace" : "create"
                });
        }

        // Sensitive path check for file tools.
        var bypassCheck = CheckBypassImmunePath(resolved.FullPath ?? "");
        if (bypassCheck != null)
            return bypassCheck;

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
        var resolved = ResolvePathForAuthorization(sandbox, chatId, rawPath);
        if (resolved.Error != null)
            return ToolSafetyAssessment.Blocked("path", "File send path is invalid.", resolved.Error);
        if (resolved.IsOutsideSandbox)
        {
            return ToolSafetyAssessment.Restricted(
                "path",
                "File send targets a host path outside the chat sandbox.",
                "Ask mode requires approval for this exact file; Full access may proceed.",
                new
                {
                    path = resolved.FullPath,
                    exists = File.Exists(resolved.FullPath),
                    sizeBytes = File.Exists(resolved.FullPath) ? new FileInfo(resolved.FullPath).Length : 0
                },
                isReadOnly: true,
                isWrite: false);
        }

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

    private static ToolSafetyAssessment AssessFileMkdir(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root)
    {
        var rawPath = ReadString(root, "path");
        var resolved = ResolvePathForAuthorization(sandbox, chatId, rawPath);
        if (resolved.Error != null)
            return ToolSafetyAssessment.Blocked("path", "Directory creation path is invalid.", resolved.Error);
        if (resolved.IsOutsideSandbox)
        {
            return ToolSafetyAssessment.Restricted(
                "path",
                "Directory creation targets a host path outside the chat sandbox.",
                "Ask mode requires approval for this exact directory; Full access may proceed.",
                new { path = resolved.FullPath, exists = Directory.Exists(resolved.FullPath) });
        }

        var bypassImmuneMkdir = CheckBypassImmunePath(resolved.FullPath ?? "");
        if (bypassImmuneMkdir != null) return bypassImmuneMkdir;

        return ToolSafetyAssessment.Medium(
            "file_write",
            isReadOnly: false,
            isWrite: true,
            $"Create sandbox directory {resolved.RelativePath}.",
            new
            {
                path = resolved.RelativePath,
                exists = Directory.Exists(resolved.FullPath)
            });
    }

    private static ToolSafetyAssessment AssessFileMove(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root)
    {
        var from = ResolvePathForAuthorization(sandbox, chatId, ReadString(root, "from_path"));
        if (from.Error != null)
            return ToolSafetyAssessment.Blocked("path", "Source path is invalid.", from.Error);

        var to = ResolvePathForAuthorization(sandbox, chatId, ReadString(root, "to_path"));
        if (to.Error != null)
            return ToolSafetyAssessment.Blocked("path", "Destination path is invalid.", to.Error);

        var mode = ReadString(root, "mode", "move");
        var overwrite = ReadBool(root, "overwrite");
        if (from.IsOutsideSandbox || to.IsOutsideSandbox)
        {
            return ToolSafetyAssessment.Restricted(
                "path",
                $"File {mode} crosses the chat sandbox boundary.",
                "Ask mode requires approval for these exact host paths; Full access may proceed.",
                new
                {
                    mode,
                    from = from.FullPath,
                    to = to.FullPath,
                    overwrite,
                    sourceExists = File.Exists(from.FullPath) || Directory.Exists(from.FullPath),
                    destinationExists = File.Exists(to.FullPath) || Directory.Exists(to.FullPath)
                });
        }
        var bypassImmuneMove = CheckBypassImmunePath(from.FullPath ?? "") ?? CheckBypassImmunePath(to.FullPath ?? "");
        if (bypassImmuneMove != null) return bypassImmuneMove;

        return ToolSafetyAssessment.Medium(
            "file_write",
            isReadOnly: false,
            isWrite: true,
            $"{mode} sandbox path {from.RelativePath} to {to.RelativePath}.",
            new
            {
                mode,
                from = from.RelativePath,
                to = to.RelativePath,
                overwrite,
                sourceExists = File.Exists(from.FullPath) || Directory.Exists(from.FullPath),
                destinationExists = File.Exists(to.FullPath) || Directory.Exists(to.FullPath)
            });
    }

    private static ToolSafetyAssessment AssessFileDelete(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root)
    {
        var rawPath = ReadString(root, "path");
        var resolved = ResolvePathForAuthorization(sandbox, chatId, rawPath);
        if (resolved.Error != null)
            return ToolSafetyAssessment.Blocked("path", "Delete path is invalid.", resolved.Error);

        var recursive = ReadBool(root, "recursive");
        if (recursive && IsImmutableRecursiveDeleteTarget(resolved.FullPath!))
        {
            return ToolSafetyAssessment.Blocked(
                "path",
                "Recursive deletion of a critical host root is blocked.",
                "Drive roots, Windows/System32, shared Users roots, user profiles, and system program-data roots cannot be recursively deleted in any permission mode.",
                new { path = resolved.FullPath, recursive });
        }

        if (resolved.IsOutsideSandbox)
        {
            var existsOutside = File.Exists(resolved.FullPath) || Directory.Exists(resolved.FullPath);
            return ToolSafetyAssessment.Restricted(
                "path",
                "Delete targets a host path outside the chat sandbox.",
                "Ask mode requires approval for this exact target; Full access may proceed unless the target is a critical recursive-delete root.",
                new
                {
                    path = resolved.FullPath,
                    exists = existsOutside,
                    isDirectory = Directory.Exists(resolved.FullPath),
                    recursive
                });
        }

        if (resolved.RelativePath is "." or "")
        {
            return ToolSafetyAssessment.Blocked(
                "path",
                "Deleting the sandbox root is blocked.",
                "Choose a specific file or subdirectory.");
        }

        var exists = File.Exists(resolved.FullPath) || Directory.Exists(resolved.FullPath);
        var isDirectory = Directory.Exists(resolved.FullPath);
        var bypassImmuneDel = CheckBypassImmunePath(resolved.FullPath ?? "");
        return ToolSafetyAssessment.High(
            "file_delete",
            isWrite: true,
            exists
                ? $"Delete sandbox {(isDirectory ? "directory" : "file")} {resolved.RelativePath}."
                : $"Requested delete target {resolved.RelativePath} does not exist.",
            recursive
                ? "Recursive deletion can remove many files. Review the target carefully."
                : "Deletion is irreversible unless another backup exists.",
            new
            {
                path = resolved.RelativePath,
                exists,
                isDirectory,
                recursive
            },
            bypassImmune: bypassImmuneDel != null);
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

        if (operation is "fetch" or "pull" or "push" or "merge" or "rebase" or
            "cherry-pick" or "revert" or "remote" or "tag")
        {
            return ToolSafetyAssessment.High(
                "git",
                isWrite: true,
                $"git {operation} integrates repository or remote state.",
                "Ask and Auto modes require approval for this exact operation; Full access may proceed.",
                new { operation, arguments = args },
                bypassImmune: true);
        }

        if (operation is "reset" or "clean")
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

    private static ToolSafetyAssessment AssessResearchVerify(JsonElement root)
    {
        var createsReport = !root.TryGetProperty("create_report", out var createReport) ||
                            createReport.ValueKind != JsonValueKind.False;
        return ToolSafetyAssessment.Medium(
            "network_research",
            isReadOnly: !createsReport,
            isWrite: createsReport,
            summary: createsReport
                ? "Cross-source public research may write an evidence report inside the chat workspace."
                : "Cross-source public research is read-only and uses the configured network safeguards.",
            new
            {
                query = ReadString(root, "query"),
                createsReport,
                allowedDomains = root.TryGetProperty("allowed_domains", out var allowed)
                    ? allowed.GetRawText()
                    : "[]",
                blockedDomains = root.TryGetProperty("blocked_domains", out var blocked)
                    ? blocked.GetRawText()
                    : "[]"
            });
    }

    private static ToolSafetyAssessment AssessArtifactWrite(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root,
        string operation,
        string defaultPath,
        string outputProperty = "path",
        bool validateSourcePath = false)
    {
        if (validateSourcePath)
        {
            var sourcePath = ReadString(root, "path");
            var source = ResolvePathForAuthorization(sandbox, chatId, sourcePath);
            if (source.Error != null)
                return ToolSafetyAssessment.Blocked(
                    "path",
                    $"{operation} source path is invalid.",
                    source.Error);
            if (source.IsOutsideSandbox)
            {
                return ToolSafetyAssessment.Restricted(
                    "path",
                    $"{operation} reads a source outside the chat sandbox.",
                    "Ask mode requires approval for this exact source; Full access may proceed.",
                    new { source = source.FullPath },
                    isReadOnly: false,
                    isWrite: true);
            }
        }

        var outputPath = ReadString(root, outputProperty);
        if (string.IsNullOrWhiteSpace(outputPath))
            outputPath = validateSourcePath
                ? ReadString(root, "path", defaultPath)
                : defaultPath;
        var output = ResolvePathForAuthorization(sandbox, chatId, outputPath);
        if (output.Error != null)
            return ToolSafetyAssessment.Blocked(
                "path",
                $"{operation} output path is invalid.",
                output.Error);
        if (output.IsOutsideSandbox)
        {
            return ToolSafetyAssessment.Restricted(
                "path",
                $"{operation} writes outside the chat sandbox.",
                "Ask mode requires approval for this exact output path; Full access may proceed.",
                new { path = output.FullPath });
        }

        var sensitivePath = CheckBypassImmunePath(output.FullPath ?? string.Empty);
        if (sensitivePath != null)
            return sensitivePath;

        return ToolSafetyAssessment.Medium(
            "artifact_write",
            isReadOnly: false,
            isWrite: true,
            $"{operation} writes a validated artifact inside the chat workspace.",
            new
            {
                path = output.RelativePath,
                overwrite = ReadBool(root, "overwrite")
            });
    }

    private static ToolSafetyAssessment AssessCodeWrite(
        ISandboxCommandService sandbox,
        Guid chatId,
        JsonElement root,
        string toolName)
    {
        var rawPath = ReadString(root, "path");
        var resolved = ResolvePathForAuthorization(sandbox, chatId, rawPath);
        if (resolved.Error != null)
            return ToolSafetyAssessment.Blocked("path", $"{toolName} path is invalid.", resolved.Error);
        if (resolved.IsOutsideSandbox)
        {
            return ToolSafetyAssessment.Restricted(
                "path",
                $"{toolName} targets a host path outside the chat sandbox.",
                "Ask mode requires approval for this exact path; Full access may proceed.",
                new { path = resolved.FullPath });
        }

        var oldBytes = File.Exists(resolved.FullPath!)
            ? new FileInfo(resolved.FullPath!).Length
            : 0;
        var writeCount = 1;
        if (root.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
            writeCount = edits.GetArrayLength();

        var bypassImmuneCode = CheckBypassImmunePath(resolved.FullPath ?? "");
        if (bypassImmuneCode != null) return bypassImmuneCode;

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

        // M4.6.0: Check all patch paths for bypass-immune files.
        var bypassImmunePatch = resolved
            .Select(p => CheckBypassImmunePath(p ?? ""))
            .FirstOrDefault(r => r != null);
        if (bypassImmunePatch != null) return bypassImmunePatch;

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
        var resolved = ResolvePathForAuthorization(sandbox, chatId, rawPath);
        if (resolved.Error != null)
            return ToolSafetyAssessment.Blocked("path", "Rollback path is invalid.", resolved.Error);
        if (resolved.IsOutsideSandbox)
        {
            return ToolSafetyAssessment.Restricted(
                "path",
                "Rollback targets a host path outside the chat sandbox.",
                "Ask mode requires approval for this exact rollback; Full access may proceed.",
                new { path = resolved.FullPath, backupId = ReadString(root, "backup_id") });
        }

        var bypassImmuneRb = CheckBypassImmunePath(resolved.FullPath ?? "");
        if (bypassImmuneRb != null) return bypassImmuneRb;

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

    private static (string? FullPath, string? RelativePath, string? Error, bool IsOutsideSandbox) ResolvePathForAuthorization(
        ISandboxCommandService sandbox,
        Guid chatId,
        string path)
    {
        try
        {
            var value = path.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(value))
                value = ".";
            var sandboxRoot = Path.GetFullPath(sandbox.GetSandboxRoot(chatId));
            var fullPath = Path.IsPathRooted(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(Path.Combine(sandboxRoot, value));
            var outside = !IsUnderDirectory(fullPath, sandboxRoot);
            return (
                fullPath,
                outside ? fullPath : Path.GetRelativePath(sandboxRoot, fullPath),
                null,
                outside);
        }
        catch (Exception ex)
        {
            return (null, null, ex.Message, false);
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

    internal static bool IsImmutableRecursiveDeleteTarget(string path)
    {
        var canonicalPath = Path.GetFullPath(path);
        var fullPath = canonicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(fullPath))
            return true;

        var driveRoot = Path.GetPathRoot(canonicalPath);
        if (!string.IsNullOrWhiteSpace(driveRoot) &&
            fullPath.Equals(NormalizeComparisonPath(driveRoot), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var criticalRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCriticalRoot(criticalRoots, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var systemDirectory = Environment.SystemDirectory;
        AddCriticalRoot(criticalRoots, systemDirectory);
        if (!string.IsNullOrWhiteSpace(systemDirectory) && IsUnderDirectory(fullPath, systemDirectory))
            return true;
        AddCriticalRoot(criticalRoots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddCriticalRoot(criticalRoots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddCriticalRoot(criticalRoots, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddCriticalRoot(criticalRoots, userProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var usersRoot = Directory.GetParent(userProfile)?.FullName;
            AddCriticalRoot(criticalRoots, usersRoot);
            if (!string.IsNullOrWhiteSpace(usersRoot))
            {
                var parent = Directory.GetParent(canonicalPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent) &&
                    NormalizeComparisonPath(parent).Equals(
                        NormalizeComparisonPath(usersRoot),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return criticalRoots.Contains(fullPath);
    }

    private static void AddCriticalRoot(HashSet<string> roots, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            roots.Add(NormalizeComparisonPath(path));
    }

    private static string NormalizeComparisonPath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string PathRelevantCommandText(string command)
    {
        var tokens = TokenizeCommand(command);
        if (tokens.Count == 0)
            return command;

        var executable = Path.GetFileNameWithoutExtension(tokens[0]);
        if (!executable.Equals("rg", StringComparison.OrdinalIgnoreCase) &&
            !executable.Equals("grep", StringComparison.OrdinalIgnoreCase))
        {
            return command;
        }

        var pathTokens = new List<string>();
        var patternSeen = false;
        var positionalOnly = false;
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!positionalOnly && token == "--")
            {
                positionalOnly = true;
                continue;
            }

            if (!positionalOnly && token.StartsWith('-'))
            {
                if (SearchPatternOptions.Contains(token))
                {
                    if (i + 1 < tokens.Count)
                        i++;
                    patternSeen = true;
                }
                else if (SearchValueOptions.Contains(token) && i + 1 < tokens.Count)
                {
                    i++;
                }
                continue;
            }

            if (!patternSeen)
            {
                patternSeen = true;
                continue;
            }

            pathTokens.Add(token);
        }

        return string.Join(' ', pathTokens);
    }

    private static List<string> TokenizeCommand(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var character in command)
        {
            if (quote.HasValue)
            {
                if (character == quote.Value)
                    quote = null;
                else
                    current.Append(character);
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static readonly HashSet<string> SearchPatternOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "-e", "--regexp"
    };

    private static readonly HashSet<string> SearchValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "-A", "-B", "-C", "-f", "-g", "-j", "-m", "-r", "-t",
        "--after-context", "--before-context", "--color", "--colors", "--context",
        "--context-separator", "--encoding", "--engine", "--field-context-separator",
        "--field-match-separator", "--file", "--glob", "--hostname-bin", "--iglob",
        "--max-columns", "--max-count", "--max-depth", "--max-filesize", "--path-separator",
        "--pre", "--pre-glob", "--replace", "--sort", "--sortr", "--threads", "--type",
        "--type-add"
    };

    private static string ReadString(JsonElement root, string name, string fallback = "") =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"(?<![\w])([A-Za-z]:\\[^""'\r\n;|&<>]*)", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteWindowsPathRegex();

    [GeneratedRegex(@"(?ix)\b(shutdown|restart-computer|stop-computer|takeown|icacls|set-executionpolicy|new-localuser|net\s+user|sc(?:\.exe)?|schtasks|start-process[\s\S]*-verb\s+runas|invoke-expression|iex|reg(?:\.exe)?\s+(?:add|delete|import|restore|save)|(?:curl|wget|invoke-webrequest|invoke-restmethod)[\s\S]*\|\s*(?:powershell|pwsh|cmd|sh|bash))\b", RegexOptions.CultureInvariant)]
    private static partial Regex SystemDestructiveCommandRegex();

    [GeneratedRegex(@"(?ix)\b(remove-item|del|erase|rmdir|rd|rm\s+-[a-z]*(?:r[a-z]*f|f[a-z]*r)[a-z]*|git\s+reset\s+--hard|git\s+clean\s+-[dfx]+|git\s+push[\s\S]*--force)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DangerousCommandRegex();

    [GeneratedRegex(@"(?ix)^\s*(get-childitem|gci|dir|ls|pwd|get-location|get-content|cat|type|select-string|test-path|get-item|get-date|get-command|get-process|get-service|get-ciminstance|get-variable|resolve-path|measure-object|rg|grep|findstr|whoami|hostname|git\s+(status|log|diff|show|rev-parse|branch))\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReadOnlyCommandRegex();

    [GeneratedRegex(@"(?ix)\b(set-content|add-content|out-file|new-item|copy-item|move-item|git\s+(init|add|commit|branch))\b|(?<![<>])>(?!>)|>>", RegexOptions.CultureInvariant)]
    private static partial Regex WriteCommandRegex();

    // Sensitive-path detection used by Ask and Auto modes.
    [GeneratedRegex(@"(?ix)(?<!\w)(\.git[\\/]|\.git\b|\.gitconfig\b|\.env\b|\.bashrc\b|\.zshrc\b|\.profile\b|\.bash_profile\b|\.zshenv\b|\.claude[\\/]|\.tlah_context[\\/])", RegexOptions.CultureInvariant)]
    private static partial Regex BypassImmunePathRegex();

    private static string[] SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
