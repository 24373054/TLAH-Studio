using System.Text.Json;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services;

public sealed class MemoryReadAgentTool : IAgentTool
{
    private readonly IProjectMemoryService _memory;

    public MemoryReadAgentTool(IProjectMemoryService memory)
    {
        _memory = memory;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.MemoryRead,
        "Read the current project memory file for stable project facts, preferences, and recurring instructions.",
        new Dictionary<string, object>
        {
            ["reason"] = AgentToolSupport.StringProperty("Why the memory file is needed.")
        });

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var path = await _memory.GetMemoryPathAsync(context.ChatId, ct);
        var content = await _memory.ReadAsync(context.ChatId, ct);
        return new AgentToolResult(true, $"Path: {path}\n\n{AgentToolSupport.Limit(content, context.MaxOutputChars)}");
    }
}

public sealed class MemoryWriteAgentTool : IAgentTool
{
    private readonly IProjectMemoryService _memory;

    public MemoryWriteAgentTool(IProjectMemoryService memory)
    {
        _memory = memory;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.MemoryWrite,
        "Update the current project memory file with stable facts, preferences, or recurring instructions that should persist across chats.",
        new Dictionary<string, object>
        {
            ["content"] = AgentToolSupport.StringProperty("Markdown content to write to project memory."),
            ["append"] = AgentToolSupport.BooleanProperty("Append to the existing memory instead of replacing it."),
            ["reason"] = AgentToolSupport.StringProperty("Why this memory update is useful and stable.")
        },
        ["content", "reason"]);

    public bool RequiresApproval => true;

    public AgentToolValidationResult ValidateInput(string argumentsJson)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return AgentToolValidationResult.Fail(error ?? "Invalid tool arguments.");
        if (!root.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(content.GetString()))
        {
            return AgentToolValidationResult.Fail("The content argument is required.");
        }

        return AgentToolValidationResult.Ok;
    }

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);

        var content = AgentToolSupport.GetString(root, "content");
        var append = root.TryGetProperty("append", out var appendValue) &&
                     appendValue.ValueKind == JsonValueKind.True;
        await _memory.WriteAsync(context.ChatId, content, append, ct);
        var path = await _memory.GetMemoryPathAsync(context.ChatId, ct);
        var updated = await _memory.ReadAsync(context.ChatId, ct);
        return new AgentToolResult(
            true,
            $"Memory {(append ? "appended" : "written")}.\nPath: {path}\n\n{AgentToolSupport.Limit(updated, context.MaxOutputChars)}");
    }
}
