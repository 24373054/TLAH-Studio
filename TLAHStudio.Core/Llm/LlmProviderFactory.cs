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

            "deepseek" => new OpenAICompatibleProvider(
                httpClient,
                apiKey,
                baseUrl ?? "https://api.deepseek.com",
                model ?? "deepseek-v4-pro",
                "deepseek"),

            "openai" => new OpenAICompatibleProvider(
                httpClient,
                apiKey,
                baseUrl ?? "https://api.openai.com",
                model ?? "gpt-4o",
                "openai"),

            // openai_compat, or any provider implementing the OpenAI API shape.
            _ => new OpenAICompatibleProvider(
                httpClient,
                apiKey,
                baseUrl ?? "https://api.openai.com",
                model ?? "gpt-4o",
                providerName)
        };
    }
}
