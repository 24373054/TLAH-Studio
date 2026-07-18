namespace TLAHStudio.Core.Llm;

/// <summary>
/// Abstract interface for LLM API providers.
/// Maps 1:1 from LLMProvider ABC in llm/base.py.
///
/// We use HttpClient directly (NOT the official SDKs) so we can capture
/// the EXACT request and response JSON at the HTTP layer.
/// </summary>
public interface ILlmProvider
{
    /// <summary>
    /// Execute a chat completion.
    /// </summary>
    /// <param name="messages">List of {role, content} (user/assistant/system pairs).</param>
    /// <param name="systemPrompt">The system prompt to prepend.</param>
    /// <param name="temperature">Sampling temperature.</param>
    /// <param name="maxTokens">Max tokens to generate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>LlmResponse with complete raw request/response, timing, and usage.</returns>
    Task<LlmResponse> ChatAsync(
        List<MessagePayload> messages,
        string systemPrompt,
        double temperature = 0.7,
        int maxTokens = 4096,
        IReadOnlyList<LlmToolDefinition>? tools = null,
        IProgress<LlmStreamUpdate>? stream = null,
        LlmReasoningOptions? reasoning = null,
        CancellationToken ct = default);

    /// <summary>Short name for this provider type.</summary>
    string ProviderName { get; }

    /// <summary>Full endpoint URL being called.</summary>
    string EndpointUrl { get; }

    /// <summary>
    /// Optional provider features. Implementations that do not override this
    /// property retain the conservative compatibility profile.
    /// </summary>
    LlmProviderCapabilities Capabilities => LlmProviderCapabilities.Compatible;
}

public sealed record LlmProviderCapabilities(
    bool StrictToolSchemas,
    bool ParallelToolCalls,
    bool ToolChoice,
    bool ToolInputExamples,
    bool DeferredTools)
{
    public static readonly LlmProviderCapabilities Compatible =
        new(false, false, false, false, false);
}
