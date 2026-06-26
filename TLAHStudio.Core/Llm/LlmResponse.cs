namespace TLAHStudio.Core.Llm;

/// <summary>
/// A simple message payload for LLM API calls.
/// Maps from dict {role, content} used in the Python code.
/// </summary>
public record MessagePayload(
    string Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null);

public sealed record LlmToolDefinition(
    string Name,
    string Description,
    Dictionary<string, object> InputSchema);

public sealed record LlmToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

public sealed record LlmStreamUpdate(
    string Delta,
    string Snapshot,
    string EventType = "text_delta",
    bool IsFinal = false);

/// <summary>
/// The complete result of an LLM API call — everything needed for debugging.
/// Maps 1:1 from LLMResponse dataclass in llm/base.py.
/// </summary>
public record LlmResponse(
    Dictionary<string, object> RawRequest,       // Complete request payload as JSON-compatible dict
    Dictionary<string, object> RawResponse,      // Complete response payload as JSON-compatible dict
    int HttpStatus,                               // HTTP status code
    int LatencyMs,                                // Round-trip time in milliseconds
    string AssistantText,                         // Extracted assistant message content
    Dictionary<string, int>? TokenUsage = null,    // Parsed token usage
    string? Error = null,                          // Error message if call failed
    IReadOnlyList<LlmToolCall>? ToolCalls = null
);
