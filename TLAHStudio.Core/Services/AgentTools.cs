using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services;

public static class AgentToolNames
{
    public const string SandboxExec = "sandbox_exec";
    public const string LegacySandboxExec = "sandbox.exec";
    public const string TerminalExec = "terminal_exec";
    public const string FileList = "file_list";
    public const string FileRead = "file_read";
    public const string FileWrite = "file_write";
    public const string FileSend = "file_send";
    public const string FileSearch = "file_search";
    public const string FileInfo = "file_info";
    public const string FileMkdir = "file_mkdir";
    public const string FileMove = "file_move";
    public const string FileDelete = "file_delete";
    public const string Git = "git";
    public const string HttpRequest = "http_request";
    public const string WebSearch = "web_search";
    public const string BrowserRead = "browser_read";
    public const string ReadPersistedOutput = "read_persisted_output";
    public const string ToolSearch = "tool_search";
    public const string TodoWrite = "todo_write";
    public const string TaskCreate = "task_create";
    public const string TaskUpdate = "task_update";
    public const string TaskList = "task_list";
    public const string TaskOutput = "task_output";
    public const string TaskStop = "task_stop";
    public const string TaskSendMessage = "task_send_message";
    public const string McpListTools = "mcp_list_tools";
    public const string McpListResources = "mcp_list_resources";
    public const string McpReadResource = "mcp_read_resource";
    public const string McpCall = "mcp_call";
    public const string MemoryRead = "memory_read";
    public const string MemoryWrite = "memory_write";
    public const string CodeRead = "read";
    public const string CodeGrep = "grep";
    public const string CodeGlob = "glob";
    public const string CodeEdit = "edit";
    public const string CodeMultiEdit = "multi_edit";
    public const string CodeDiff = "diff";
    public const string CodeApplyPatch = "apply_patch";
    public const string CodeRollback = "rollback";
    public const string CodeDiagnostics = "lsp_diagnostics";
    public const string CodeSymbols = "symbols";

    public static string Normalize(string name) =>
        string.Equals(name, LegacySandboxExec, StringComparison.OrdinalIgnoreCase)
            ? SandboxExec
            : name;
}

public sealed record AgentToolExecutionContext(
    Guid ChatId,
    Guid AgentRunId,
    Guid InvocationId,
    int TimeoutSeconds,
    int MaxOutputChars);

public sealed record AgentToolArtifact(
    string RelativePath,
    string ContentType,
    long SizeBytes,
    string Sha256);

public static class AgentToolRenderHints
{
    public const string Text = "text";
    public const string Terminal = "terminal";
    public const string File = "file";
    public const string Network = "network";
    public const string Git = "git";
    public const string Mcp = "mcp";
}

public static class AgentToolResultPersistenceModes
{
    public const string Inline = "inline";
    public const string PersistLargeOutputs = "persist_large_outputs";
    public const string Artifact = "artifact";
}

public static class AgentToolInterruptBehaviors
{
    public const string AllowCancel = "allow_cancel";
    public const string PreferGracefulStop = "prefer_graceful_stop";
    public const string FinishAtomicOperation = "finish_atomic_operation";
}

public sealed record AgentToolRenderBlock(
    string Title,
    string Subtitle,
    string Body,
    string RenderHint,
    string? PrimaryPath = null,
    bool IsTruncated = false,
    IReadOnlyList<AgentToolArtifact>? Artifacts = null);

public sealed record AgentToolMetadata(
    string Name,
    bool RequiresApproval,
    bool IsReadOnly,
    bool IsConcurrencySafe,
    bool IsDestructive,
    string RenderHint,
    int MaxResultSizeChars,
    string ResultPersistence,
    bool IsOpenWorld = false,
    string UserFacingName = "",
    string ActivityDescription = "",
    string InterruptBehavior = AgentToolInterruptBehaviors.AllowCancel)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(UserFacingName)
            ? AgentToolUx.UserFacingName(Name)
            : UserFacingName;

    public string DefaultActivityDescription =>
        string.IsNullOrWhiteSpace(ActivityDescription)
            ? AgentToolUx.ActivityDescription(Name)
            : ActivityDescription;

    public static AgentToolMetadata For(string name, bool requiresApproval)
    {
        var normalized = AgentToolNames.Normalize(name);
        return normalized switch
        {
            AgentToolNames.FileList or
            AgentToolNames.FileRead or
            AgentToolNames.FileSearch or
            AgentToolNames.FileInfo or
            AgentToolNames.MemoryRead or
            AgentToolNames.CodeRead or
            AgentToolNames.CodeGrep or
            AgentToolNames.CodeGlob or
            AgentToolNames.ToolSearch or
            AgentToolNames.TaskList or
            AgentToolNames.TaskOutput or
            AgentToolNames.ReadPersistedOutput or
            AgentToolNames.CodeDiff or
            AgentToolNames.CodeDiagnostics or
            AgentToolNames.CodeSymbols => new(
                normalized,
                requiresApproval,
                IsReadOnly: true,
                IsConcurrencySafe: true,
                IsDestructive: false,
                AgentToolRenderHints.File,
                24_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs),

            AgentToolNames.WebSearch or
            AgentToolNames.BrowserRead => new(
                normalized,
                requiresApproval,
                IsReadOnly: true,
                IsConcurrencySafe: true,
                IsDestructive: false,
                AgentToolRenderHints.Network,
                18_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs,
                IsOpenWorld: true),

            AgentToolNames.McpListTools => new(
                normalized,
                requiresApproval,
                IsReadOnly: true,
                IsConcurrencySafe: true,
                IsDestructive: false,
                AgentToolRenderHints.Mcp,
                18_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs,
                IsOpenWorld: true),

            AgentToolNames.McpListResources or
            AgentToolNames.McpReadResource => new(
                normalized,
                requiresApproval,
                IsReadOnly: true,
                IsConcurrencySafe: true,
                IsDestructive: false,
                AgentToolRenderHints.Mcp,
                18_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs,
                IsOpenWorld: true),

            AgentToolNames.FileSend => new(
                normalized,
                requiresApproval,
                IsReadOnly: true,
                IsConcurrencySafe: true,
                IsDestructive: false,
                AgentToolRenderHints.File,
                8_000,
                AgentToolResultPersistenceModes.Artifact),

            AgentToolNames.FileWrite or
            AgentToolNames.FileMkdir or
            AgentToolNames.FileMove or
            AgentToolNames.MemoryWrite or
            AgentToolNames.TodoWrite or
            AgentToolNames.TaskCreate or
            AgentToolNames.TaskUpdate or
            AgentToolNames.TaskSendMessage or
            AgentToolNames.CodeEdit or
            AgentToolNames.CodeMultiEdit or
            AgentToolNames.CodeApplyPatch or
            AgentToolNames.CodeRollback => new(
                normalized,
                requiresApproval,
                IsReadOnly: false,
                IsConcurrencySafe: false,
                IsDestructive: false,
                AgentToolRenderHints.File,
                18_000,
                AgentToolResultPersistenceModes.Artifact),

            AgentToolNames.FileDelete => new(
                normalized,
                requiresApproval,
                IsReadOnly: false,
                IsConcurrencySafe: false,
                IsDestructive: true,
                AgentToolRenderHints.File,
                12_000,
                AgentToolResultPersistenceModes.Inline),

            AgentToolNames.Git => new(
                normalized,
                requiresApproval,
                IsReadOnly: false,
                IsConcurrencySafe: false,
                IsDestructive: true,
                AgentToolRenderHints.Git,
                18_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs),

            AgentToolNames.HttpRequest => new(
                normalized,
                requiresApproval,
                IsReadOnly: false,
                IsConcurrencySafe: false,
                IsDestructive: false,
                AgentToolRenderHints.Network,
                18_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs,
                IsOpenWorld: true),

            AgentToolNames.McpCall => new(
                normalized,
                requiresApproval,
                IsReadOnly: false,
                IsConcurrencySafe: false,
                IsDestructive: true,
                AgentToolRenderHints.Mcp,
                18_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs,
                IsOpenWorld: true),

            AgentToolNames.TaskStop => new(
                normalized,
                requiresApproval,
                IsReadOnly: false,
                IsConcurrencySafe: false,
                IsDestructive: false,
                AgentToolRenderHints.Text,
                8_000,
                AgentToolResultPersistenceModes.Inline),

            AgentToolNames.SandboxExec or
            AgentToolNames.TerminalExec => new(
                normalized,
                requiresApproval,
                IsReadOnly: false,
                IsConcurrencySafe: false,
                IsDestructive: true,
                AgentToolRenderHints.Terminal,
                20_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs),

            _ => new(
                normalized,
                requiresApproval,
                IsReadOnly: false,
                IsConcurrencySafe: false,
                IsDestructive: true,
                AgentToolRenderHints.Text,
                12_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs,
                IsOpenWorld: true)
        };
    }
}

public static class AgentToolUx
{
    public static string UserFacingName(string toolName)
    {
        var normalized = AgentToolNames.Normalize(toolName);
        return normalized switch
        {
            AgentToolNames.SandboxExec => "Sandbox command",
            AgentToolNames.TerminalExec => "Terminal command",
            AgentToolNames.FileList => "List files",
            AgentToolNames.FileRead => "Read file",
            AgentToolNames.FileWrite => "Write file",
            AgentToolNames.FileSend => "Send file",
            AgentToolNames.FileSearch => "Search files",
            AgentToolNames.FileInfo => "Inspect file",
            AgentToolNames.FileMkdir => "Create folder",
            AgentToolNames.FileMove => "Move file",
            AgentToolNames.FileDelete => "Delete file",
            AgentToolNames.Git => "Git operation",
            AgentToolNames.HttpRequest => "HTTP request",
            AgentToolNames.WebSearch => "Web search",
            AgentToolNames.BrowserRead => "Read web page",
            AgentToolNames.ReadPersistedOutput => "Read persisted output",
            AgentToolNames.ToolSearch => "Search tools",
            AgentToolNames.TodoWrite => "Update todo list",
            AgentToolNames.TaskCreate => "Create task",
            AgentToolNames.TaskUpdate => "Update task",
            AgentToolNames.TaskList => "List tasks",
            AgentToolNames.TaskOutput => "Read task output",
            AgentToolNames.TaskStop => "Stop task",
            AgentToolNames.TaskSendMessage => "Message task",
            AgentToolNames.McpListTools => "List MCP tools",
            AgentToolNames.McpListResources => "List MCP resources",
            AgentToolNames.McpReadResource => "Read MCP resource",
            AgentToolNames.McpCall => "Call MCP tool",
            AgentToolNames.MemoryRead => "Read project memory",
            AgentToolNames.MemoryWrite => "Write project memory",
            AgentToolNames.CodeRead => "Read code",
            AgentToolNames.CodeGrep => "Search code",
            AgentToolNames.CodeGlob => "Find files",
            AgentToolNames.CodeDiff => "Preview diff",
            AgentToolNames.CodeEdit => "Edit file",
            AgentToolNames.CodeMultiEdit => "Edit multiple ranges",
            AgentToolNames.CodeApplyPatch => "Apply patch",
            AgentToolNames.CodeRollback => "Rollback edit",
            AgentToolNames.CodeDiagnostics => "Run diagnostics",
            AgentToolNames.CodeSymbols => "Find symbols",
            _ => normalized
        };
    }

    public static string ActivityDescription(string toolName)
    {
        var normalized = AgentToolNames.Normalize(toolName);
        return normalized switch
        {
            AgentToolNames.SandboxExec or AgentToolNames.TerminalExec => "Running command",
            AgentToolNames.FileList or AgentToolNames.CodeGlob => "Listing workspace paths",
            AgentToolNames.FileRead or AgentToolNames.CodeRead => "Reading file",
            AgentToolNames.FileWrite or AgentToolNames.CodeEdit or AgentToolNames.CodeMultiEdit => "Writing file",
            AgentToolNames.FileSend => "Preparing attachment",
            AgentToolNames.FileSearch or AgentToolNames.CodeGrep => "Searching text",
            AgentToolNames.FileInfo => "Inspecting file metadata",
            AgentToolNames.FileMkdir => "Creating folder",
            AgentToolNames.FileMove => "Moving workspace path",
            AgentToolNames.FileDelete => "Deleting workspace path",
            AgentToolNames.CodeDiff => "Building diff preview",
            AgentToolNames.CodeApplyPatch => "Applying patch",
            AgentToolNames.CodeRollback => "Restoring backup",
            AgentToolNames.CodeDiagnostics => "Checking diagnostics",
            AgentToolNames.CodeSymbols => "Indexing code symbols",
            AgentToolNames.Git => "Running Git",
            AgentToolNames.HttpRequest => "Calling HTTP endpoint",
            AgentToolNames.WebSearch => "Searching the web",
            AgentToolNames.BrowserRead => "Reading web content",
            AgentToolNames.ReadPersistedOutput => "Reading persisted output",
            AgentToolNames.ToolSearch => "Searching tool catalog",
            AgentToolNames.TodoWrite => "Updating todo list",
            AgentToolNames.TaskCreate => "Creating task",
            AgentToolNames.TaskUpdate => "Updating task",
            AgentToolNames.TaskList => "Listing tasks",
            AgentToolNames.TaskOutput => "Reading background output",
            AgentToolNames.TaskStop => "Stopping background task",
            AgentToolNames.TaskSendMessage => "Sending background message",
            AgentToolNames.McpListTools => "Discovering MCP tools",
            AgentToolNames.McpListResources => "Discovering MCP resources",
            AgentToolNames.McpReadResource => "Reading MCP resource",
            AgentToolNames.McpCall => "Calling MCP server",
            AgentToolNames.MemoryRead => "Loading memory",
            AgentToolNames.MemoryWrite => "Saving memory",
            _ => $"Running {normalized}"
        };
    }

    public static AgentToolRenderBlock RenderUse(
        IAgentTool tool,
        string argumentsJson,
        ToolSafetyAssessment? safety = null)
    {
        var reason = TryReadString(argumentsJson, "reason");
        var primaryPath = TryReadString(argumentsJson, "path");
        var body = PrettyJson(argumentsJson);
        var subtitle = string.IsNullOrWhiteSpace(reason)
            ? tool.Metadata.DefaultActivityDescription
            : reason.Trim();
        if (safety != null)
        {
            subtitle = $"{subtitle} · {safety.Level}";
        }

        return new AgentToolRenderBlock(
            tool.Metadata.DisplayName,
            subtitle,
            body,
            tool.Metadata.RenderHint,
            string.IsNullOrWhiteSpace(primaryPath) ? null : primaryPath);
    }

    public static AgentToolRenderBlock RenderResult(IAgentTool tool, AgentToolResult result)
    {
        var title = result.Success
            ? $"{tool.Metadata.DisplayName} completed"
            : $"{tool.Metadata.DisplayName} failed";
        var subtitle = result.Artifacts is { Count: > 0 }
            ? $"{result.Artifacts.Count} artifact{(result.Artifacts.Count == 1 ? string.Empty : "s")}"
            : result.Success
                ? "Result ready"
                : result.Error ?? "Tool failed";
        var body = string.IsNullOrWhiteSpace(result.Output)
            ? result.Error ?? string.Empty
            : result.Output.TrimEnd();

        return new AgentToolRenderBlock(
            title,
            subtitle,
            body,
            tool.Metadata.RenderHint,
            result.Artifacts?.FirstOrDefault()?.RelativePath,
            IsResultTruncated(result),
            result.Artifacts);
    }

    public static bool InputsEquivalent(string leftJson, string rightJson)
    {
        try
        {
            using var left = JsonDocument.Parse(leftJson);
            using var right = JsonDocument.Parse(rightJson);
            return JsonSerializer.Serialize(left.RootElement) ==
                   JsonSerializer.Serialize(right.RootElement);
        }
        catch
        {
            return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
        }
    }

    public static bool IsResultTruncated(AgentToolResult result) =>
        result.Output.Contains("[output truncated", StringComparison.OrdinalIgnoreCase) ||
        result.Output.Contains("[tool output truncated", StringComparison.OrdinalIgnoreCase);

    private static string? TryReadString(string argumentsJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(
                doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}

public sealed record AgentToolValidationResult(bool Success, string? Error = null)
{
    public static AgentToolValidationResult Ok { get; } = new(true);
    public static AgentToolValidationResult Fail(string error) => new(false, error);
}

public sealed record AgentToolResult(
    bool Success,
    string Output,
    string? Error = null,
    IReadOnlyList<AgentToolArtifact>? Artifacts = null)
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        success = Success,
        output = SecretRedactor.RedactText(Output),
        error = SecretRedactor.RedactText(Error ?? string.Empty),
        artifacts = Artifacts ?? []
    });
}

public interface IAgentTool
{
    LlmToolDefinition Definition { get; }
    bool RequiresApproval { get; }
    AgentToolMetadata Metadata => AgentToolMetadata.For(Definition.Name, RequiresApproval);

    bool IsReadOnly => Metadata.IsReadOnly;
    bool IsConcurrencySafe => Metadata.IsConcurrencySafe;
    bool IsDestructive => Metadata.IsDestructive;
    string RenderHint => Metadata.RenderHint;
    int MaxResultSizeChars => Metadata.MaxResultSizeChars;
    string ResultPersistence => Metadata.ResultPersistence;
    string UserFacingName => Metadata.DisplayName;
    string ActivityDescription => Metadata.DefaultActivityDescription;
    string InterruptBehavior => Metadata.InterruptBehavior;

    AgentToolValidationResult ValidateInput(string argumentsJson)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return AgentToolValidationResult.Fail(error ?? "Invalid tool arguments.");
        return root.ValueKind == JsonValueKind.Object
            ? AgentToolValidationResult.Ok
            : AgentToolValidationResult.Fail("Tool arguments must be a JSON object.");
    }

    Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default);

    AgentToolRenderBlock RenderToolUse(string argumentsJson, ToolSafetyAssessment? safety = null) =>
        AgentToolUx.RenderUse(this, argumentsJson, safety);

    AgentToolRenderBlock RenderToolResult(AgentToolResult result) =>
        AgentToolUx.RenderResult(this, result);

    bool IsResultTruncated(AgentToolResult result) =>
        AgentToolUx.IsResultTruncated(result);

    bool InputsEquivalent(string leftArgumentsJson, string rightArgumentsJson) =>
        AgentToolUx.InputsEquivalent(leftArgumentsJson, rightArgumentsJson);
}

public interface IAgentToolRegistry
{
    IReadOnlyList<LlmToolDefinition> Definitions { get; }
    IReadOnlyList<AgentToolMetadata> Metadata { get; }
    bool TryGet(string name, out IAgentTool tool);
}

public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private static readonly Regex ValidToolName = new(
        "^[a-zA-Z0-9_-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly Dictionary<string, IAgentTool> _tools;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _tools = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (!ValidToolName.IsMatch(tool.Definition.Name))
            {
                throw new InvalidOperationException(
                    $"Agent tool name '{tool.Definition.Name}' is invalid. Tool names may only contain letters, numbers, underscores, and hyphens.");
            }
            _tools.Add(tool.Definition.Name, tool);
        }
    }

    public IReadOnlyList<LlmToolDefinition> Definitions =>
        _tools.Values.Select(t => t.Definition).ToArray();

    public IReadOnlyList<AgentToolMetadata> Metadata =>
        _tools.Values.Select(t => t.Metadata).ToArray();

    public bool TryGet(string name, out IAgentTool tool)
    {
        return _tools.TryGetValue(AgentToolNames.Normalize(name), out tool!);
    }
}

public sealed class SandboxExecAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public SandboxExecAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = new(
        AgentToolNames.SandboxExec,
        "Execute one restricted PowerShell command inside the chat sandbox. Host files, privileged operations, nested shells, and destructive system commands are blocked.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["command"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "A PowerShell command that only reads or writes inside the sandbox working directory."
                },
                ["reason"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "A short explanation of why this command is required."
                }
            },
            ["required"] = new[] { "command" },
            ["additionalProperties"] = false
        });

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        string command;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            command = document.RootElement.TryGetProperty("command", out var commandElement)
                ? commandElement.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException ex)
        {
            return new AgentToolResult(false, string.Empty, $"Invalid tool arguments: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(command))
            return new AgentToolResult(false, string.Empty, "The command argument is required.");

        var result = await _sandbox.ExecuteAsync(
            context.ChatId,
            command,
            new SandboxCommandOptions(context.TimeoutSeconds, context.MaxOutputChars),
            ct);

        var artifacts = await DiscoverArtifactsAsync(context.ChatId, ct);
        var output = $"""
            Exit code: {result.ExitCode}
            Timed out: {result.TimedOut}
            Duration: {result.Duration.TotalMilliseconds:F0} ms
            Working directory: {result.WorkingDirectory}

            stdout:
            {result.StandardOutput}

            stderr:
            {result.StandardError}
            """;

        if (result.WasBlocked)
            output += $"\nBlocked: {result.BlockedReason}";

        return new AgentToolResult(
            !result.WasBlocked && !result.TimedOut && result.ExitCode == 0,
            output,
            result.WasBlocked ? result.BlockedReason : result.TimedOut ? "Command timed out." : null,
            artifacts);
    }

    private async Task<IReadOnlyList<AgentToolArtifact>> DiscoverArtifactsAsync(
        Guid chatId,
        CancellationToken ct)
    {
        var root = _sandbox.GetSandboxRoot(chatId);
        if (!Directory.Exists(root))
            return [];

        var artifacts = new List<AgentToolArtifact>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(200))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > 100 * 1024 * 1024)
                continue;

            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            artifacts.Add(new AgentToolArtifact(
                Path.GetRelativePath(root, path),
                GetContentType(path),
                info.Length,
                hash));
        }

        return artifacts;
    }

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".log" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
}
