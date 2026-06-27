namespace TLAHStudio.Core.Llm;

/// <summary>
/// A simple message payload for LLM API calls.
/// Maps from dict {role, content} used in the Python code.
/// </summary>
public record MessagePayload(
    string Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ReasoningContent = null);

public sealed record LlmToolDefinition(
    string Name,
    string Description,
    Dictionary<string, object> InputSchema);

public sealed record LlmToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

public sealed record LlmReasoningOptions(string Depth = ReasoningDepths.Auto)
{
    public static readonly LlmReasoningOptions Auto = new(ReasoningDepths.Auto);
    public static readonly LlmReasoningOptions Off = new(ReasoningDepths.Off);
}

public static class ReasoningDepths
{
    public const string Auto = "auto";
    public const string Off = "off";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Max = "max";

    public static string Normalize(string? value)
    {
        var normalized = (value ?? Auto).Trim().ToLowerInvariant();
        return normalized switch
        {
            Off or Low or Medium or High or Max => normalized,
            _ => Auto
        };
    }
}

public sealed record LlmStreamUpdate(
    string Delta,
    string Snapshot,
    string EventType = "text_delta",
    bool IsFinal = false);

public static class LlmStreamEventTypes
{
    public const string TextDelta = "text_delta";
    public const string ThinkingDelta = "thinking_delta";
    public const string TextStarted = "text_started";
}

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
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ReasoningText = null
);
