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
        new("openai_compat", "OpenAI Compatible", "https://api.openai.com", "gpt-4o"),
        new("anthropic", "Anthropic", "https://api.anthropic.com", "claude-sonnet-4-6"),
    };
}
