using System.Text.Json;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.Tools;
using TLAHStudio.Core.Services.Tools.Models;

namespace TLAHStudio.Core.Services.Memory;

/// <summary>
/// M2.10.0: List memory files for the current project.
/// </summary>
public sealed class MemoryListToolV3 : AgentToolV3Base
{
    private readonly IMemoryDirectoryService _memory;
    public MemoryListToolV3(IMemoryDirectoryService memory) => _memory = memory;
    public override LlmToolDefinition Definition => new("memory_list",
        "List available memory files for the current project.",
        new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var files = await _memory.ListFilesAsync(context.ChatId, ct);
        var result = new System.Text.StringBuilder();
        result.AppendLine($"Memory files ({files.Count}):");
        foreach (var f in files)
            result.AppendLine($"  - {f.FileName} [{f.Type}] — {f.Description} ({f.SizeBytes} bytes)");
        return new AgentToolResult(true, result.ToString());
    }

    public override async Task<ToolEffectPlan> PlanEffectsAsync(string argumentsJson, Guid chatId, ISandboxCommandService sandbox, CancellationToken ct = default)
    {
        var files = await _memory.ListFilesAsync(chatId, ct);
        return new ToolEffectPlan(
            files.Select(f => f.FileName).ToList(), [], [], [], [], [], "low", false, false);
    }

    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
}

/// <summary>
/// M2.10.0: Read a specific memory file.
/// </summary>
public sealed class MemoryReadToolV3 : AgentToolV3Base
{
    private readonly IMemoryDirectoryService _memory;
    public MemoryReadToolV3(IMemoryDirectoryService memory) => _memory = memory;
    public override LlmToolDefinition Definition => new("memory_read",
        "Read a specific memory file. Use file_name=\"MEMORY.md\" for the index.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["file_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Memory file name to read" }
            },
            ["required"] = new[] { "file_name" }
        });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        var fileName = args?.GetValueOrDefault("file_name", "MEMORY.md") ?? "MEMORY.md";
        var content = await _memory.ReadFileAsync(context.ChatId, fileName, ct);
        if (string.IsNullOrEmpty(content))
            return new AgentToolResult(false, string.Empty, $"Memory file not found: {fileName}");
        return new AgentToolResult(true, content);
    }

    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
}

/// <summary>
/// M2.10.0: Write/create a memory file.
/// </summary>
public sealed class MemoryWriteToolV3 : AgentToolV3Base
{
    private readonly IMemoryDirectoryService _memory;
    public MemoryWriteToolV3(IMemoryDirectoryService memory) => _memory = memory;
    public override LlmToolDefinition Definition => new("memory_write",
        "Write or update a memory file. Each file should hold ONE typed fact.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["file_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Memory file name (e.g., conventions.md)" },
                ["type"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Memory type: user, feedback, project, reference", ["enum"] = new[] { "user", "feedback", "project", "reference" } },
                ["description"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "One-line summary (used for relevance matching)" },
                ["content"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Memory content in markdown" }
            },
            ["required"] = new[] { "file_name", "type", "description", "content" }
        });
    public override bool RequiresApproval => true;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        var fileName = args?.GetValueOrDefault("file_name") ?? "memory.md";
        var type = args?.GetValueOrDefault("type") ?? "reference";
        var description = args?.GetValueOrDefault("description") ?? "";
        var content = args?.GetValueOrDefault("content") ?? "";
        await _memory.WriteFileAsync(context.ChatId, fileName, type, content, description, ct);
        return new AgentToolResult(true, $"Memory written: {fileName} [{type}] — {description}");
    }

    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
}

/// <summary>
/// M2.10.0: Delete a memory file.
/// </summary>
public sealed class MemoryDeleteToolV3 : AgentToolV3Base
{
    private readonly IMemoryDirectoryService _memory;
    public MemoryDeleteToolV3(IMemoryDirectoryService memory) => _memory = memory;
    public override LlmToolDefinition Definition => new("memory_delete",
        "Delete a memory file. Use with caution — this cannot be undone.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["file_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Memory file name to delete" }
            },
            ["required"] = new[] { "file_name" }
        });
    public override bool RequiresApproval => true;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        var fileName = args?.GetValueOrDefault("file_name") ?? "";
        if (string.IsNullOrWhiteSpace(fileName))
            return new AgentToolResult(false, string.Empty, "file_name is required.");
        await _memory.DeleteFileAsync(context.ChatId, fileName, ct);
        return new AgentToolResult(true, $"Memory file deleted: {fileName}");
    }

    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.BeforeUse | ToolHookTriggers.AfterUse;
}

/// <summary>
/// M2.10.0: Search memory files by relevance.
/// </summary>
public sealed class MemorySearchToolV3 : AgentToolV3Base
{
    private readonly IMemoryDirectoryService _memory;
    public MemorySearchToolV3(IMemoryDirectoryService memory) => _memory = memory;
    public override LlmToolDefinition Definition => new("memory_search",
        "Search project memory files for relevant information.",
        new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Search query" },
                ["max_results"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Max results (default 5)" }
            },
            ["required"] = new[] { "query" }
        });
    public override bool RequiresApproval => false;

    public override async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, string>>(argumentsJson);
        var query = args?.GetValueOrDefault("query") ?? "";
        var maxResults = int.TryParse(args?.GetValueOrDefault("max_results"), out var n) ? n : 5;
        var results = await _memory.SearchAsync(context.ChatId, query, maxResults, ct);
        var output = new System.Text.StringBuilder();
        output.AppendLine($"Memory search results for \"{query}\":");
        foreach (var r in results)
            output.AppendLine($"  - {r.FileName} [{r.Type}] score={r.RelevanceScore:F2}: {r.Description}\n    {r.Snippet}");
        if (results.Count == 0)
            output.AppendLine("  (no results)");
        return new AgentToolResult(true, output.ToString());
    }

    public override ToolHookTriggers SupportedHooks => ToolHookTriggers.None;
}
