using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;

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
    // M4.9.0: Plan mode tools
    public const string EnterPlanMode = "enter_plan_mode";
    public const string ExitPlanMode = "exit_plan_mode";
    public const string AskUserQuestion = "ask_user_question";
    public const string Skill = "skill";
    public const string ResearchVerify = "research_verify";
    public const string SpreadsheetCreate = "spreadsheet_create";
    public const string SpreadsheetInspect = "spreadsheet_inspect";
    public const string SpreadsheetUpdate = "spreadsheet_update";
    public const string DocumentCreate = "document_create";
    public const string DocumentInspect = "document_inspect";
    public const string DiagramCreate = "diagram_create";

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
    int MaxOutputChars,
    string PermissionMode = AgentPermissionModes.RequestApproval,
    bool HasInvocationAuthorization = false,
    bool HasPolicyAuthorization = false)
{
    public string EffectivePermissionMode => HasInvocationAuthorization || HasPolicyAuthorization
        ? AgentPermissionModes.BypassPermissions
        : AgentPermissionModes.Normalize(PermissionMode);
}

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
    string InterruptBehavior = AgentToolInterruptBehaviors.AllowCancel,
    bool RequiresUserInteraction = false)
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
        AgentToolMetadata metadata = normalized switch
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
            AgentToolNames.CodeDiff => new(
                normalized, requiresApproval,
                IsReadOnly: true, IsConcurrencySafe: true,
                IsDestructive: false,
                AgentToolRenderHints.File, 24_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs),

            // M4.9.0
            AgentToolNames.AskUserQuestion => new(
                normalized, requiresApproval,
                IsReadOnly: true, IsConcurrencySafe: true,
                IsDestructive: false,
                AgentToolRenderHints.Text, 20_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs),

            AgentToolNames.Skill => new(
                normalized, requiresApproval,
                IsReadOnly: true, IsConcurrencySafe: true,
                IsDestructive: false,
                AgentToolRenderHints.Text, 50_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs),

            AgentToolNames.EnterPlanMode => new(
                normalized, requiresApproval,
                IsReadOnly: true, IsConcurrencySafe: true,
                IsDestructive: false,
                AgentToolRenderHints.File, 100_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs),

            AgentToolNames.ExitPlanMode => new(
                normalized, requiresApproval,
                IsReadOnly: false, IsConcurrencySafe: false,
                IsDestructive: false,
                AgentToolRenderHints.File, 100_000,
                AgentToolResultPersistenceModes.PersistLargeOutputs),

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
            AgentToolNames.BrowserRead or
            AgentToolNames.ResearchVerify => new(
                normalized,
                requiresApproval,
                IsReadOnly: normalized != AgentToolNames.ResearchVerify,
                IsConcurrencySafe: normalized != AgentToolNames.ResearchVerify,
                IsDestructive: false,
                AgentToolRenderHints.Network,
                normalized == AgentToolNames.ResearchVerify ? 50_000 : 18_000,
                normalized == AgentToolNames.ResearchVerify
                    ? AgentToolResultPersistenceModes.Artifact
                    : AgentToolResultPersistenceModes.PersistLargeOutputs,
                IsOpenWorld: true),

            AgentToolNames.SpreadsheetInspect or
            AgentToolNames.DocumentInspect => new(
                normalized,
                requiresApproval,
                IsReadOnly: true,
                IsConcurrencySafe: true,
                IsDestructive: false,
                AgentToolRenderHints.File,
                24_000,
                AgentToolResultPersistenceModes.Artifact),

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
            AgentToolNames.CodeRollback or
            AgentToolNames.SpreadsheetCreate or
            AgentToolNames.SpreadsheetUpdate or
            AgentToolNames.DocumentCreate or
            AgentToolNames.DiagramCreate => new(
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

        return metadata with
        {
            RequiresUserInteraction = normalized is AgentToolNames.AskUserQuestion or AgentToolNames.ExitPlanMode
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
            AgentToolNames.ResearchVerify => "Verify research",
            AgentToolNames.SpreadsheetCreate => "Create spreadsheet",
            AgentToolNames.SpreadsheetInspect => "Inspect spreadsheet",
            AgentToolNames.SpreadsheetUpdate => "Update spreadsheet",
            AgentToolNames.DocumentCreate => "Create document",
            AgentToolNames.DocumentInspect => "Inspect document",
            AgentToolNames.DiagramCreate => "Create diagram",
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
            AgentToolNames.ResearchVerify => "Cross-checking public sources",
            AgentToolNames.SpreadsheetCreate => "Creating spreadsheet attachment",
            AgentToolNames.SpreadsheetInspect => "Inspecting spreadsheet structure",
            AgentToolNames.SpreadsheetUpdate => "Updating spreadsheet attachment",
            AgentToolNames.DocumentCreate => "Creating document attachment",
            AgentToolNames.DocumentInspect => "Inspecting document structure",
            AgentToolNames.DiagramCreate => "Rendering diagram attachments",
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
    IReadOnlyList<AgentToolArtifact>? Artifacts = null,
    string? Warning = null,
    bool OutcomeUncertain = false,
    bool MayHaveCommitted = false,
    object? StructuredContent = null,
    string? ErrorCode = null,
    bool Retryable = false,
    IReadOnlyList<AgentToolSource>? Sources = null,
    long? DurationMs = null,
    IReadOnlyDictionary<string, object>? Diagnostics = null)
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        success = Success,
        output = SecretRedactor.RedactText(Output),
        error = SecretRedactor.RedactText(Error ?? string.Empty),
        errorCode = ErrorCode,
        retryable = Retryable,
        warning = SecretRedactor.RedactText(Warning ?? string.Empty),
        outcomeUncertain = OutcomeUncertain,
        mayHaveCommitted = MayHaveCommitted,
        durationMs = DurationMs,
        structuredContent = RedactStructured(StructuredContent),
        sources = Sources ?? [],
        diagnostics = RedactStructured(Diagnostics),
        artifacts = Artifacts ?? []
    });

    private static object? RedactStructured(object? value)
    {
        if (value == null)
            return null;

        try
        {
            var redacted = SecretRedactor.RedactJson(JsonSerializer.Serialize(value));
            using var document = JsonDocument.Parse(redacted);
            return document.RootElement.Clone();
        }
        catch
        {
            return SecretRedactor.RedactText(value.ToString() ?? string.Empty);
        }
    }
}

public sealed record AgentToolSource(
    string Uri,
    string? Title = null,
    string? Provider = null,
    DateTime? RetrievedAt = null,
    string? CitationId = null);

internal static class AgentToolInputValidator
{
    private static readonly HashSet<string> SupportedTypes = new(
        ["string", "integer", "number", "boolean", "object", "array", "null"],
        StringComparer.Ordinal);

    public static AgentToolValidationResult ValidateRequiredProperties(
        LlmToolDefinition definition,
        JsonElement root)
    {
        return ValidateValue(definition.InputSchema, root, "$", definition.Name);
    }

    private static AgentToolValidationResult ValidateValue(
        object? schema,
        JsonElement value,
        string path,
        string toolName)
    {
        if (TryGetSchemaValue(schema, "type", out var typeValue))
        {
            var expectedTypes = EnumerateSchemaStrings(typeValue)
                .Where(SupportedTypes.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // Ignore malformed/unknown schema types for plugin compatibility.
            if (expectedTypes.Length > 0 &&
                !expectedTypes.Any(type => MatchesType(value, type)))
            {
                return AgentToolValidationResult.Fail(
                    $"Tool argument '{path}' for '{toolName}' must be {FormatTypes(expectedTypes)}; " +
                    $"received {DescribeKind(value)}.");
            }
        }

        if (TryGetSchemaValue(schema, "enum", out var enumValue) &&
            TryEnumerateEnumValues(enumValue, out var allowedValues) &&
            !allowedValues.Any(candidate => JsonValuesEquivalent(value, candidate)))
        {
            var choices = string.Join(", ", allowedValues.Select(item => item.GetRawText()));
            if (choices.Length > 160)
                choices = choices[..157] + "...";
            return AgentToolValidationResult.Fail(
                $"Tool argument '{path}' for '{toolName}' must be one of: {choices}.");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var objectResult = ValidateObject(schema, value, path, toolName);
            if (!objectResult.Success)
                return objectResult;
        }
        else if (value.ValueKind == JsonValueKind.Array &&
                 TryGetSchemaValue(schema, "items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var itemResult = ValidateValue(
                    itemSchema,
                    item,
                    $"{path}[{index}]",
                    toolName);
                if (!itemResult.Success)
                    return itemResult;
                index++;
            }
        }

        return AgentToolValidationResult.Ok;
    }

    private static AgentToolValidationResult ValidateObject(
        object? schema,
        JsonElement value,
        string path,
        string toolName)
    {
        var requiredProperties = new HashSet<string>(StringComparer.Ordinal);
        if (TryGetSchemaValue(schema, "required", out var requiredValue) &&
            requiredValue != null)
        {
            foreach (var propertyName in EnumerateSchemaStrings(requiredValue))
            {
                if (string.IsNullOrWhiteSpace(propertyName))
                    continue;
                requiredProperties.Add(propertyName);

                if (!value.TryGetProperty(propertyName, out var requiredProperty) ||
                    requiredProperty.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                    (requiredProperty.ValueKind == JsonValueKind.String &&
                     string.IsNullOrWhiteSpace(requiredProperty.GetString())))
                {
                    return AgentToolValidationResult.Fail(
                        $"Required tool argument '{AppendPath(path, propertyName)}' is missing or empty.");
                }
            }
        }

        TryGetSchemaValue(schema, "properties", out var propertiesSchema);
        var rejectUnknown =
            TryGetSchemaValue(schema, "additionalProperties", out var additionalProperties) &&
            IsExplicitFalse(additionalProperties);

        foreach (var property in value.EnumerateObject())
        {
            if (!TryGetSchemaValue(propertiesSchema, property.Name, out var propertySchema))
            {
                if (rejectUnknown)
                {
                    return AgentToolValidationResult.Fail(
                        $"Tool argument '{AppendPath(path, property.Name)}' is not allowed by the schema.");
                }

                continue;
            }

            // Strict provider schemas encode legacy optional fields as
            // required-but-nullable on the wire. Keep the original runtime
            // semantics by treating null as omission only when this field was
            // not required by the app's schema.
            if (property.Value.ValueKind == JsonValueKind.Null &&
                !requiredProperties.Contains(property.Name))
                continue;

            var propertyResult = ValidateValue(
                propertySchema,
                property.Value,
                AppendPath(path, property.Name),
                toolName);
            if (!propertyResult.Success)
                return propertyResult;
        }

        return AgentToolValidationResult.Ok;
    }

    private static bool TryGetSchemaValue(object? schema, string name, out object? value)
    {
        if (schema is JsonElement element && element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property))
        {
            value = property;
            return true;
        }

        if (schema is IReadOnlyDictionary<string, object> readOnly &&
            readOnly.TryGetValue(name, out var readOnlyValue))
        {
            value = readOnlyValue;
            return true;
        }

        if (schema is System.Collections.IDictionary dictionary && dictionary.Contains(name))
        {
            value = dictionary[name];
            return true;
        }

        value = null;
        return false;
    }

    private static IEnumerable<string> EnumerateSchemaStrings(object? value)
    {
        if (value is string text)
        {
            yield return text;
            yield break;
        }

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } item)
                yield return item;
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray())
                    if (child.ValueKind == JsonValueKind.String && child.GetString() is { } childText)
                        yield return childText;
            yield break;
        }

        if (value is System.Collections.IEnumerable values)
        {
            foreach (var item in values)
            {
                if (item is string itemText)
                    yield return itemText;
                else if (item is JsonElement itemElement &&
                         itemElement.ValueKind == JsonValueKind.String &&
                         itemElement.GetString() is { } elementText)
                    yield return elementText;
            }
        }
    }

    private static bool TryEnumerateEnumValues(
        object? value,
        out IReadOnlyList<JsonElement> elements)
    {
        var parsed = new List<JsonElement>();
        if (value is JsonElement json)
        {
            if (json.ValueKind != JsonValueKind.Array)
            {
                elements = [];
                return false;
            }

            parsed.AddRange(json.EnumerateArray().Select(item => item.Clone()));
            elements = parsed;
            return true;
        }

        if (value is not System.Collections.IEnumerable values ||
            value is string or System.Collections.IDictionary)
        {
            elements = [];
            return false;
        }

        try
        {
            foreach (var item in values)
            {
                parsed.Add(item is JsonElement itemElement
                    ? itemElement.Clone()
                    : JsonSerializer.SerializeToElement<object?>(item));
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            elements = [];
            return false;
        }

        elements = parsed;
        return true;
    }

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => IsInteger(value),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static bool IsInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
            return false;
        if (value.TryGetInt64(out _) || value.TryGetUInt64(out _))
            return true;
        if (value.TryGetDecimal(out var decimalValue))
            return decimalValue == decimal.Truncate(decimalValue);
        return value.TryGetDouble(out var doubleValue) &&
               double.IsFinite(doubleValue) &&
               doubleValue == Math.Truncate(doubleValue);
    }

    private static bool JsonValuesEquivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
        {
            if (left.TryGetDecimal(out var leftDecimal) &&
                right.TryGetDecimal(out var rightDecimal))
                return leftDecimal == rightDecimal;
            return left.GetDouble().Equals(right.GetDouble());
        }

        if (left.ValueKind != right.ValueKind)
            return false;

        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectsEquivalent(left, right),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() &&
                                   left.EnumerateArray().Zip(right.EnumerateArray())
                                       .All(pair => JsonValuesEquivalent(pair.First, pair.Second)),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal)
        };
    }

    private static bool ObjectsEquivalent(JsonElement left, JsonElement right)
    {
        var leftProperties = left.EnumerateObject().ToArray();
        var rightProperties = right.EnumerateObject().ToArray();
        if (leftProperties.Length != rightProperties.Length)
            return false;

        return leftProperties.All(property =>
            right.TryGetProperty(property.Name, out var other) &&
            JsonValuesEquivalent(property.Value, other));
    }

    private static bool IsExplicitFalse(object? value) => value switch
    {
        bool flag => !flag,
        JsonElement { ValueKind: JsonValueKind.False } => true,
        _ => false
    };

    private static string AppendPath(string path, string propertyName) =>
        path == "$" ? $"$.{propertyName}" : $"{path}.{propertyName}";

    private static string FormatTypes(IReadOnlyList<string> types) =>
        types.Count == 1
            ? $"a{(types[0][0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "n" : string.Empty)} {types[0]}"
            : string.Join(" or ", types);

    private static string DescribeKind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => IsInteger(value) ? "integer" : "number",
        JsonValueKind.String => "string",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.Null => "null",
        _ => value.ValueKind.ToString().ToLowerInvariant()
    };
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
    bool RequiresUserInteraction => Metadata.RequiresUserInteraction;

    AgentToolValidationResult ValidateInput(string argumentsJson)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return AgentToolValidationResult.Fail(error ?? "Invalid tool arguments.");
        if (root.ValueKind != JsonValueKind.Object)
            return AgentToolValidationResult.Fail("Tool arguments must be a JSON object.");
        return AgentToolInputValidator.ValidateRequiredProperties(Definition, root);
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
    /// <summary>M4.9.2: Register a tool dynamically (e.g. from a trusted plugin).</summary>
    void Register(IAgentTool tool);
    /// <summary>M4.9.2: Unregister a dynamically registered tool by name.</summary>
    bool Unregister(string name);
}

public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private static readonly Regex ValidToolName = new(
        "^[a-zA-Z0-9_-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly Dictionary<string, IAgentTool> _tools;
    private readonly HashSet<string> _dynamicNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

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

    public IReadOnlyList<LlmToolDefinition> Definitions
    {
        get
        {
            lock (_lock)
                return _tools.Values.Select(t => t.Definition).ToArray();
        }
    }

    public IReadOnlyList<AgentToolMetadata> Metadata
    {
        get
        {
            lock (_lock)
                return _tools.Values.Select(t => t.Metadata).ToArray();
        }
    }

    public bool TryGet(string name, out IAgentTool tool)
    {
        lock (_lock)
            return _tools.TryGetValue(AgentToolNames.Normalize(name), out tool!);
    }

    /// <summary>M4.9.2: Register a tool dynamically (thread-safe). Built-in names cannot be overwritten.</summary>
    public void Register(IAgentTool tool)
    {
        if (tool == null) throw new ArgumentNullException(nameof(tool));
        if (!ValidToolName.IsMatch(tool.Definition.Name))
            throw new InvalidOperationException($"Agent tool name '{tool.Definition.Name}' is invalid.");
        lock (_lock)
        {
            // Built-in (non-dynamic) names are protected from overwrite.
            if (_tools.ContainsKey(tool.Definition.Name) && !_dynamicNames.Contains(tool.Definition.Name))
                throw new InvalidOperationException(
                    $"Cannot overwrite built-in tool '{tool.Definition.Name}' with a plugin tool.");
            _tools[tool.Definition.Name] = tool;
            _dynamicNames.Add(tool.Definition.Name);
        }
    }

    /// <summary>M4.9.2: Unregister a dynamically registered tool. Built-in tools cannot be removed.</summary>
    public bool Unregister(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var normalized = AgentToolNames.Normalize(name);
        lock (_lock)
        {
            if (!_dynamicNames.Contains(normalized)) return false;
            _dynamicNames.Remove(normalized);
            return _tools.Remove(normalized);
        }
    }
}

public sealed class SandboxExecAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;
    private readonly IExecutionBackendRouter _router;

    public SandboxExecAgentTool(
        ISandboxCommandService sandbox,
        IExecutionBackendRouter router)
    {
        _sandbox = sandbox;
        _router = router;
    }

    public LlmToolDefinition Definition { get; } = new(
        AgentToolNames.SandboxExec,
        "Execute one PowerShell command from the chat workspace. Ask and Auto use the restricted sandbox; Full access or an exactly approved Ask invocation uses the unrestricted local backend. Catastrophic operations remain blocked by the safety policy.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["command"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "A PowerShell command started from the workspace. Ask and Auto are restricted to the sandbox; Full access or an exactly approved Ask invocation may access the host, subject to the catastrophic-operation guard."
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

        var backend = AgentPermissionModes.IsBypass(context.EffectivePermissionMode)
            ? ToolExecutionBackends.UnrestrictedLocal
            : ToolExecutionBackends.RestrictedLocal;
        var result = await _router.ExecuteAsync(
            new ExecutionRequest(
                context.ChatId,
                command,
                context.TimeoutSeconds,
                context.MaxOutputChars,
                context.EffectivePermissionMode),
            backend,
            ct);

        var artifacts = await DiscoverArtifactsAsync(context.ChatId, ct);
        var output = $"""
            Backend: {result.Backend}
            Exit code: {result.ExitCode}
            Timed out: {result.TimedOut}
            Duration: {result.Duration.TotalMilliseconds:F0} ms
            Working directory: {result.WorkingDirectory}

            stdout:
            {result.StandardOutput}

            stderr:
            {result.StandardError}
            """;

        if (result.BlockedReason != null)
            output += $"\nBlocked: {result.BlockedReason}";

        return new AgentToolResult(
            result.Success,
            output,
            result.BlockedReason ??
            (result.TimedOut ? "Command timed out." :
             result.ExitCode != 0 ? $"Command exited with code {result.ExitCode}." : null),
            artifacts,
            OutcomeUncertain: result.OutcomeUncertain,
            MayHaveCommitted: result.MayHaveCommitted);
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
