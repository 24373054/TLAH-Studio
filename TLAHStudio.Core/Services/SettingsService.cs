using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;

#pragma warning disable CA1416 // TLAH Studio is a Windows desktop client; DPAPI is intentionally Windows-only.

namespace TLAHStudio.Core.Services;

/// <summary>
/// Settings management service.
/// Maps 1:1 from services/settings_service.py.
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly DbContext _db;

    public SettingsService(DbContext db)
    {
        _db = db;
    }

    // ── Global Settings ──────────────────────────────────────────

    public async Task<GlobalSettings> GetGlobalSettingsRawAsync(CancellationToken ct = default)
    {
        var gs = await _db.Set<GlobalSettings>()
            .FirstOrDefaultAsync(g => g.Id == 1, ct);

        if (gs == null)
        {
            gs = new GlobalSettings { Id = 1 };
            _db.Set<GlobalSettings>().Add(gs);
            await _db.SaveChangesAsync(ct);
        }

        return gs;
    }

    public async Task<GlobalSettingsDto> GetGlobalSettingsMaskedAsync(CancellationToken ct = default)
    {
        var gs = await GetGlobalSettingsRawAsync(ct);
        await NormalizeGlobalSettingsAsync(gs, ct);
        var apiKey = await RevealAndMigrateGlobalApiKeyAsync(gs, ct);
        return new GlobalSettingsDto(
            Provider: gs.Provider,
            ApiKey: ApiKeyMasker.Mask(apiKey),
            BaseUrl: gs.BaseUrl,
            Model: gs.Model,
            UseLongContext: gs.UseLongContext,
            ThinkingDepth: ReasoningDepths.Normalize(gs.ThinkingDepth),
            Temperature: gs.Temperature,
            MaxTokens: gs.MaxTokens,
            SystemPrompt: gs.SystemPrompt,
            UserRole: gs.UserRole
        );
    }

    public async Task<GlobalSettings> UpdateGlobalSettingsAsync(GlobalSettingsUpdateDto data, CancellationToken ct = default)
    {
        var gs = await GetGlobalSettingsRawAsync(ct);

        if (data.Provider != null) gs.Provider = data.Provider;
        // Never overwrite API key with the masked version sent back from the UI
        if (data.ApiKey != null && !ApiKeyMasker.IsMasked(data.ApiKey))
            gs.ApiKey = ProtectedSecret.Protect(data.ApiKey.Trim());
        if (data.BaseUrl != null) gs.BaseUrl = data.BaseUrl;
        if (data.Model != null)
        {
            if (ProviderModelResolver.HasLongContextSuffix(data.Model))
                gs.UseLongContext = true;
            gs.Model = ProviderModelResolver.NormalizeModelForStorage(data.Model);
        }
        if (data.UseLongContext.HasValue) gs.UseLongContext = data.UseLongContext.Value;
        if (data.ThinkingDepth != null) gs.ThinkingDepth = ReasoningDepths.Normalize(data.ThinkingDepth);
        if (data.Temperature.HasValue) gs.Temperature = data.Temperature.Value;
        if (data.MaxTokens.HasValue) gs.MaxTokens = data.MaxTokens.Value;
        if (data.SystemPrompt != null) gs.SystemPrompt = data.SystemPrompt;
        if (data.UserRole != null) gs.UserRole = data.UserRole;

        gs.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Auto-fill default base_url and model when switching providers
        if (data.Provider != null)
        {
            foreach (var p in ProviderInfo.Supported)
            {
                if (p.Key == data.Provider)
                {
                    bool changed = false;
                    if (string.IsNullOrEmpty(gs.BaseUrl))
                    {
                        gs.BaseUrl = p.DefaultBaseUrl;
                        changed = true;
                    }
                    if (string.IsNullOrEmpty(gs.Model))
                    {
                        gs.Model = p.DefaultModel;
                        changed = true;
                    }
                    if (changed)
                        await _db.SaveChangesAsync(ct);
                    break;
                }
            }
        }

        return gs;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        var gs = await GetGlobalSettingsRawAsync(ct);
        var apiKey = await RevealAndMigrateGlobalApiKeyAsync(gs, ct);
        return !string.IsNullOrWhiteSpace(apiKey)
            && !string.IsNullOrWhiteSpace(gs.BaseUrl)
            && !string.IsNullOrWhiteSpace(gs.Model);
    }

    // ── Chat Settings ────────────────────────────────────────────

    public async Task<ChatSettings?> GetChatSettingsAsync(Guid chatId, CancellationToken ct = default)
    {
        return await _db.Set<ChatSettings>()
            .FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
    }

    public async Task<ChatSettingsDto?> GetChatSettingsMaskedAsync(Guid chatId, CancellationToken ct = default)
    {
        var cs = await GetChatSettingsAsync(chatId, ct);
        if (cs == null)
            return null;

        var apiKey = await RevealAndMigrateChatApiKeyAsync(cs, ct);
        await NormalizeChatSettingsAsync(cs, ct);
        return new ChatSettingsDto(
            Provider: cs.Provider,
            ApiKey: string.IsNullOrWhiteSpace(apiKey) ? null : ApiKeyMasker.Mask(apiKey),
            BaseUrl: cs.BaseUrl,
            Model: cs.Model,
            UseLongContext: cs.UseLongContext,
            ThinkingDepth: cs.ThinkingDepth,
            Temperature: cs.Temperature,
            MaxTokens: cs.MaxTokens,
            UserRole: cs.UserRole);
    }

    public async Task<ChatSettings> GetOrCreateChatSettingsAsync(Guid chatId, CancellationToken ct = default)
    {
        var cs = await GetChatSettingsAsync(chatId, ct);
        if (cs == null)
        {
            cs = new ChatSettings { ChatId = chatId };
            _db.Set<ChatSettings>().Add(cs);
            await _db.SaveChangesAsync(ct);
        }
        return cs;
    }

    public async Task<ChatSettings> UpdateChatSettingsAsync(Guid chatId, ChatSettingsUpdateDto data, CancellationToken ct = default)
    {
        var cs = await GetOrCreateChatSettingsAsync(chatId, ct);

        if (data.Provider != null) cs.Provider = data.Provider;
        if (data.ApiKey != null && !ApiKeyMasker.IsMasked(data.ApiKey))
            cs.ApiKey = string.IsNullOrWhiteSpace(data.ApiKey)
                ? null
                : ProtectedSecret.Protect(data.ApiKey.Trim());
        if (data.BaseUrl != null) cs.BaseUrl = data.BaseUrl;
        if (data.Model != null)
        {
            if (ProviderModelResolver.HasLongContextSuffix(data.Model))
                cs.UseLongContext = true;
            cs.Model = ProviderModelResolver.NormalizeModelForStorage(data.Model);
        }
        if (data.UseLongContext.HasValue) cs.UseLongContext = data.UseLongContext.Value;
        if (data.ThinkingDepth != null) cs.ThinkingDepth = ReasoningDepths.Normalize(data.ThinkingDepth);
        if (data.Temperature.HasValue) cs.Temperature = data.Temperature.Value;
        if (data.MaxTokens.HasValue) cs.MaxTokens = data.MaxTokens.Value;
        if (data.UserRole != null) cs.UserRole = data.UserRole;

        await _db.SaveChangesAsync(ct);
        return cs;
    }

    // ── Effective Settings Merge ─────────────────────────────────

    /// <summary>
    /// Merge global + per-chat settings into effective settings.
    /// Chat-level overrides take priority where non-null; everything else
    /// falls back to global. Maps directly from get_effective_settings()
    /// in services/settings_service.py.
    /// </summary>
    public async Task<EffectiveSettings> GetEffectiveSettingsAsync(Guid chatId, CancellationToken ct = default)
    {
        var gs = await GetGlobalSettingsRawAsync(ct);
        var cs = await GetChatSettingsAsync(chatId, ct);
        var chat = await _db.Set<Chat>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == chatId, ct);
        ConfigProfile? profile = null;
        if (chat?.ConfigProfileId != null)
        {
            profile = await _db.Set<ConfigProfile>()
                .FirstOrDefaultAsync(p => p.Id == chat.ConfigProfileId.Value, ct);
        }
        else if (chat?.ProjectSpaceId != null)
        {
            var project = await _db.Set<ProjectSpace>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == chat.ProjectSpaceId.Value, ct);
            if (project?.DefaultConfigProfileId != null)
            {
                profile = await _db.Set<ConfigProfile>()
                    .FirstOrDefaultAsync(p => p.Id == project.DefaultConfigProfileId.Value, ct);
            }
        }

        var globalApiKey = await RevealAndMigrateGlobalApiKeyAsync(gs, ct);
        var chatApiKey = cs == null ? null : await RevealAndMigrateChatApiKeyAsync(cs, ct);
        var profileApiKey = profile == null ? null : await RevealAndMigrateProfileApiKeyAsync(profile, ct);

        var provider = cs?.Provider ?? profile?.Provider ?? gs.Provider;
        var baseUrl = cs?.BaseUrl ?? profile?.BaseUrl ?? gs.BaseUrl;
        var model = cs?.Model ?? profile?.Model ?? gs.Model;
        var useLongContext = cs?.UseLongContext ?? profile?.UseLongContext ?? gs.UseLongContext;
        var resolution = ProviderModelResolver.Resolve(provider, baseUrl, model, useLongContext);

        return new EffectiveSettings(
            Provider: provider,
            ApiKey: !string.IsNullOrWhiteSpace(chatApiKey)
                ? chatApiKey
                : !string.IsNullOrWhiteSpace(profileApiKey)
                    ? profileApiKey
                    : globalApiKey,
            BaseUrl: baseUrl,
            Model: resolution.WireModel,
            Temperature: cs?.Temperature ?? profile?.Temperature ?? gs.Temperature,
            MaxTokens: cs?.MaxTokens ?? profile?.MaxTokens ?? gs.MaxTokens,
            SystemPrompt: gs.SystemPrompt,
            UserRole: cs?.UserRole ?? profile?.UserRole ?? gs.UserRole,
            UseLongContext: resolution.LongContextEnabled,
            ThinkingDepth: ReasoningDepths.Normalize(cs?.ThinkingDepth ?? profile?.ThinkingDepth ?? gs.ThinkingDepth),
            ContextBudgetTokens: resolution.ContextBudgetTokens,
            AutoCompactTriggerTokens: resolution.AutoCompactTriggerTokens
        );
    }

    // ── Providers ────────────────────────────────────────────────

    public IReadOnlyList<ProviderInfo> GetSupportedProviders() =>
        ProviderInfo.Supported;

    private async Task<string> RevealAndMigrateGlobalApiKeyAsync(GlobalSettings settings, CancellationToken ct)
    {
        var plain = ProtectedSecret.Reveal(settings.ApiKey);
        if (!string.IsNullOrWhiteSpace(plain) && !ProtectedSecret.IsProtected(settings.ApiKey))
        {
            settings.ApiKey = ProtectedSecret.Protect(plain);
            await _db.SaveChangesAsync(ct);
        }

        return plain;
    }

    private async Task NormalizeGlobalSettingsAsync(GlobalSettings settings, CancellationToken ct)
    {
        var normalizedModel = ProviderModelResolver.NormalizeModelForStorage(settings.Model);
        var changed = false;
        if (!string.Equals(settings.Model, normalizedModel, StringComparison.Ordinal))
        {
            settings.Model = normalizedModel;
            settings.UseLongContext = true;
            changed = true;
        }

        var normalizedDepth = ReasoningDepths.Normalize(settings.ThinkingDepth);
        if (!string.Equals(settings.ThinkingDepth, normalizedDepth, StringComparison.Ordinal))
        {
            settings.ThinkingDepth = normalizedDepth;
            changed = true;
        }

        if (changed)
            await _db.SaveChangesAsync(ct);
    }

    private async Task NormalizeChatSettingsAsync(ChatSettings settings, CancellationToken ct)
    {
        var changed = false;
        if (settings.Model != null && ProviderModelResolver.HasLongContextSuffix(settings.Model))
        {
            settings.Model = ProviderModelResolver.NormalizeModelForStorage(settings.Model);
            settings.UseLongContext = true;
            changed = true;
        }

        if (settings.ThinkingDepth != null)
        {
            var normalizedDepth = ReasoningDepths.Normalize(settings.ThinkingDepth);
            if (!string.Equals(settings.ThinkingDepth, normalizedDepth, StringComparison.Ordinal))
            {
                settings.ThinkingDepth = normalizedDepth;
                changed = true;
            }
        }

        if (changed)
            await _db.SaveChangesAsync(ct);
    }

    private async Task<string> RevealAndMigrateChatApiKeyAsync(ChatSettings settings, CancellationToken ct)
    {
        var plain = ProtectedSecret.Reveal(settings.ApiKey);
        if (!string.IsNullOrWhiteSpace(plain) && !ProtectedSecret.IsProtected(settings.ApiKey))
        {
            settings.ApiKey = ProtectedSecret.Protect(plain);
            await _db.SaveChangesAsync(ct);
        }

        return plain;
    }

    private async Task<string> RevealAndMigrateProfileApiKeyAsync(ConfigProfile profile, CancellationToken ct)
    {
        var plain = ProtectedSecret.Reveal(profile.ApiKey);
        if (!string.IsNullOrWhiteSpace(plain) && !ProtectedSecret.IsProtected(profile.ApiKey))
        {
            profile.ApiKey = ProtectedSecret.Protect(plain);
            await _db.SaveChangesAsync(ct);
        }

        return plain;
    }
}
