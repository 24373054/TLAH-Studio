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
        CancellationToken ct = default);

    /// <summary>Short name for this provider type.</summary>
    string ProviderName { get; }

    /// <summary>Full endpoint URL being called.</summary>
    string EndpointUrl { get; }
}
