using System.Net.Http;

namespace TLAHStudio.Core.Llm;

/// <summary>
/// Factory for creating LLM provider instances.
/// Maps 1:1 from create_provider() in llm/__init__.py.
/// </summary>
public static class LlmProviderFactory
{
    /// <summary>
    /// Creates the appropriate ILlmProvider based on the provider name.
    /// Uses raw HttpClient (NOT SDKs) to capture exact wire-format JSON.
    /// </summary>
    public static ILlmProvider Create(
        HttpClient httpClient,
        string providerName,
        string apiKey,
        string? baseUrl = null,
        string? model = null)
    {
        return providerName switch
        {
            "anthropic" => new AnthropicProvider(
                httpClient,
                apiKey,
                baseUrl ?? "https://api.anthropic.com",
                model ?? "claude-sonnet-4-6"),

            // openai, deepseek, openai_compat, or any OpenAI-compatible API
            _ => new OpenAICompatibleProvider(
                httpClient,
                apiKey,
                baseUrl ?? "https://api.openai.com",
                model ?? "gpt-4o")
        };
    }
}
