using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

/// <summary>
/// Settings management service interface.
/// Maps from services/settings_service.py.
/// </summary>
public interface ISettingsService
{
    // ── Global Settings ──────────────────────────────────────────
    Task<GlobalSettings> GetGlobalSettingsRawAsync(CancellationToken ct = default);
    Task<GlobalSettingsDto> GetGlobalSettingsMaskedAsync(CancellationToken ct = default);
    Task<GlobalSettings> UpdateGlobalSettingsAsync(GlobalSettingsUpdateDto data, CancellationToken ct = default);
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    // ── Chat Settings ────────────────────────────────────────────
    Task<ChatSettings?> GetChatSettingsAsync(Guid chatId, CancellationToken ct = default);
    Task<ChatSettingsDto?> GetChatSettingsMaskedAsync(Guid chatId, CancellationToken ct = default);
    Task<ChatSettings> GetOrCreateChatSettingsAsync(Guid chatId, CancellationToken ct = default);
    Task<ChatSettings> UpdateChatSettingsAsync(Guid chatId, ChatSettingsUpdateDto data, CancellationToken ct = default);

    // ── Effective Settings Merge ─────────────────────────────────
    Task<EffectiveSettings> GetEffectiveSettingsAsync(Guid chatId, CancellationToken ct = default);

    // ── Providers ────────────────────────────────────────────────
    IReadOnlyList<ProviderInfo> GetSupportedProviders();
}

// ── DTOs ──────────────────────────────────────────────────────────

public record GlobalSettingsDto(
    string Provider,
    string ApiKey,
    string BaseUrl,
    string Model,
    bool UseLongContext,
    string ThinkingDepth,
    double Temperature,
    int MaxTokens,
    string SystemPrompt,
    string UserRole,
    string? OutputStyle = null  // M4.9.0
);

public record GlobalSettingsUpdateDto(
    string? Provider = null,
    string? ApiKey = null,
    string? BaseUrl = null,
    string? Model = null,
    bool? UseLongContext = null,
    string? ThinkingDepth = null,
    double? Temperature = null,
    int? MaxTokens = null,
    string? SystemPrompt = null,
    string? UserRole = null,
    string? OutputStyle = null   // M4.9.0
);

public record ChatSettingsDto(
    string? Provider = null,
    string? ApiKey = null,
    string? BaseUrl = null,
    string? Model = null,
    bool? UseLongContext = null,
    string? ThinkingDepth = null,
    double? Temperature = null,
    int? MaxTokens = null,
    string? UserRole = null
);

public record ChatSettingsUpdateDto(
    string? Provider = null,
    string? ApiKey = null,
    string? BaseUrl = null,
    string? Model = null,
    bool? UseLongContext = null,
    string? ThinkingDepth = null,
    double? Temperature = null,
    int? MaxTokens = null,
    string? UserRole = null
);
