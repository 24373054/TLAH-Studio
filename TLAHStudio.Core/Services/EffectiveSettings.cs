namespace TLAHStudio.Core.Services;

/// <summary>
/// The actual settings used for an LLM call — merged from global + chat override.
/// Maps 1:1 from EffectiveSettings dataclass in services/settings_service.py.
/// </summary>
public record EffectiveSettings(
    string Provider,
    string ApiKey,
    string BaseUrl,
    string Model,
    double Temperature,
    int MaxTokens,
    string SystemPrompt,
    string UserRole
);

/// <summary>
/// Provider metadata for the settings UI.
/// Maps 1:1 from ProviderInfo + SUPPORTED_PROVIDERS in schemas/settings.py.
/// </summary>
public record ProviderInfo(string Key, string Name, string DefaultBaseUrl, string DefaultModel)
{
    public static readonly List<ProviderInfo> Supported = new()
    {
        new("openai", "OpenAI", "https://api.openai.com", "gpt-4o"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-v4-pro"),
        new("openai_compat", "OpenAI Compatible", "https://api.openai.com", "gpt-4o"),
        new("anthropic", "Anthropic", "https://api.anthropic.com", "claude-sonnet-4-6"),
    };
}

public static class ProviderModelCatalog
{
    public static IReadOnlyList<string> FallbackModels(string provider) =>
        provider switch
        {
            "deepseek" =>
            [
                "deepseek-v4-pro",
                "deepseek-v4-flash",
                "deepseek-v4-pro[1m]",
                "deepseek-v4-flash[1m]",
                "deepseek-v3.1",
                "deepseek-v3.1-terminus",
                "deepseek-chat",
                "deepseek-reasoner"
            ],
            "anthropic" =>
            [
                "claude-sonnet-4-6",
                "claude-opus-4-1",
                "claude-haiku-4-5"
            ],
            "openai" =>
            [
                "gpt-4o",
                "gpt-4o-mini",
                "gpt-4.1",
                "gpt-4.1-mini"
            ],
            _ =>
            [
                ProviderInfo.Supported.FirstOrDefault(p => p.Key == provider)?.DefaultModel ?? "gpt-4o"
            ]
        };
}
