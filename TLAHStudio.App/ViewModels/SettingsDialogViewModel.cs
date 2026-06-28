using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services;

namespace TLAHStudio.App.ViewModels;

/// <summary>
/// Settings dialog ViewModel — handles both global and per-chat settings forms.
/// Maps from SettingsModal.tsx + SettingsContext.tsx.
/// </summary>
public partial class SettingsDialogViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ILlmService _llmService;
    private readonly IAppStateService _appState;
    private readonly IInteractionSoundService _soundService;

    // ── Provider list ──────────────────────────────────────────────
    public ObservableCollection<ProviderInfo> Providers { get; } = new();
    public ObservableCollection<string> GlobalModelOptions { get; } = new();
    public ObservableCollection<string> ChatModelOptions { get; } = new();
    public IReadOnlyList<string> ThinkingDepthOptions { get; } =
    [
        ReasoningDepths.Auto,
        ReasoningDepths.Off,
        ReasoningDepths.Low,
        ReasoningDepths.Medium,
        ReasoningDepths.High,
        ReasoningDepths.Max
    ];
    public IReadOnlyList<string> ChatThinkingDepthOptions { get; } =
    [
        InheritOption,
        ReasoningDepths.Auto,
        ReasoningDepths.Off,
        ReasoningDepths.Low,
        ReasoningDepths.Medium,
        ReasoningDepths.High,
        ReasoningDepths.Max
    ];

    public const string InheritOption = "inherit";

    [ObservableProperty]
    private ProviderInfo? _selectedProvider;

    // ── Global settings form fields ─────────────────────────────────
    [ObservableProperty] private string _provider = "deepseek";
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _baseUrl = "https://api.deepseek.com";
    [ObservableProperty] private string _model = "deepseek-v4-pro";
    [ObservableProperty] private string _thinkingDepth = ReasoningDepths.Auto;
    [ObservableProperty] private double _temperature = 0.7;
    [ObservableProperty] private int _maxTokens = 4096;
    [ObservableProperty] private string _systemPrompt = "You are a helpful assistant.";
    [ObservableProperty] private string _userRole = "user";
    [ObservableProperty] private bool _isSoundEffectsEnabled = true;
    [ObservableProperty] private double _soundVolume = 0.62;

    // ── Chat-level override fields ──────────────────────────────────
    [ObservableProperty] private string? _chatProvider;
    [ObservableProperty] private string? _chatApiKey;
    [ObservableProperty] private string? _chatBaseUrl;
    [ObservableProperty] private string? _chatModel;
    [ObservableProperty] private string? _chatThinkingDepth;
    [ObservableProperty] private double? _chatTemperature;
    [ObservableProperty] private int? _chatMaxTokens;
    [ObservableProperty] private string? _chatUserRole;

    // ── State ───────────────────────────────────────────────────────
    [ObservableProperty] private bool _isGlobalTab = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _isLoadingModels;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _testMessage;
    [ObservableProperty] private bool _isGlobalLongContextEnabled;
    [ObservableProperty] private bool _isChatLongContextEnabled;

    public bool HasCurrentChat => _appState.CurrentChatId != null;
    public string ChatIdLabel => _appState.CurrentChatId?.ToString("D") ?? "No chat selected";

    public SettingsDialogViewModel(
        ISettingsService settingsService,
        ILlmService llmService,
        IAppStateService appState,
        IInteractionSoundService soundService)
    {
        _settingsService = settingsService;
        _llmService = llmService;
        _appState = appState;
        _soundService = soundService;

        foreach (var p in ProviderInfo.Supported)
            Providers.Add(p);
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(HasCurrentChat));
        OnPropertyChanged(nameof(ChatIdLabel));
        try
        {
            // Load global settings
            var gs = await _settingsService.GetGlobalSettingsMaskedAsync();
            Provider = gs.Provider;
            ApiKey = gs.ApiKey;
            BaseUrl = gs.BaseUrl;
            Model = gs.Model;
            ThinkingDepth = ReasoningDepths.Normalize(gs.ThinkingDepth);
            Temperature = gs.Temperature;
            MaxTokens = gs.MaxTokens;
            SystemPrompt = gs.SystemPrompt;
            UserRole = gs.UserRole;
            IsGlobalLongContextEnabled = gs.UseLongContext;
            IsSoundEffectsEnabled = _soundService.IsEnabled;
            SoundVolume = _soundService.Volume;

            SelectedProvider = Providers.FirstOrDefault(p => p.Key == gs.Provider);
            LoadFallbackModelOptions(GlobalModelOptions, Provider);

            // Load per-chat settings if a chat is selected
            if (_appState.CurrentChatId != null)
            {
                var cs = await _settingsService.GetChatSettingsMaskedAsync(_appState.CurrentChatId.Value);
                if (cs != null)
                {
                    ChatProvider = cs.Provider;
                    ChatApiKey = cs.ApiKey;
                    ChatBaseUrl = cs.BaseUrl;
                    ChatModel = cs.Model;
                    ChatThinkingDepth = cs.ThinkingDepth;
                    ChatTemperature = cs.Temperature;
                    ChatMaxTokens = cs.MaxTokens;
                    ChatUserRole = cs.UserRole;
                    IsChatLongContextEnabled = cs.UseLongContext == true;
                    LoadFallbackModelOptions(ChatModelOptions, ChatProvider ?? Provider);
                }
                else
                {
                    ClearChatOverrides();
                }
            }
            else
            {
                ClearChatOverrides();
            }
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task TestGlobalConnectionAsync()
    {
        IsTesting = true;
        ErrorMessage = null;
        TestMessage = null;
        try
        {
            var apiKey = await ResolveGlobalApiKeyAsync();
            var result = await _llmService.TestConnectionAsync(Provider, apiKey, BaseUrl, Model);
            TestMessage = result.Success
                ? $"Connection OK. {result.LatencyMs ?? 0}ms"
                : $"Connection failed: {result.Message}";
        }
        catch (Exception e)
        {
            TestMessage = $"Connection failed: {e.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    public async Task TestChatConnectionAsync()
    {
        if (_appState.CurrentChatId == null)
            return;

        IsTesting = true;
        ErrorMessage = null;
        TestMessage = null;
        try
        {
            var effective = await _settingsService.GetEffectiveSettingsAsync(_appState.CurrentChatId.Value);
            var provider = ChatProvider ?? effective.Provider;
            var apiKey = await ResolveChatApiKeyAsync(effective.ApiKey);
            var baseUrl = ChatBaseUrl ?? effective.BaseUrl;
            var model = ChatModel ?? effective.Model;
            var result = await _llmService.TestConnectionAsync(provider, apiKey, baseUrl, model);
            TestMessage = result.Success
                ? $"Connection OK. {result.LatencyMs ?? 0}ms"
                : $"Connection failed: {result.Message}";
        }
        catch (Exception e)
        {
            TestMessage = $"Connection failed: {e.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    public void ClearGlobalApiKey()
    {
        ApiKey = string.Empty;
        TestMessage = "API key will be cleared when you save.";
    }

    public void ClearChatApiKey()
    {
        ChatApiKey = string.Empty;
        TestMessage = "Chat API key override will be cleared when you save.";
    }

    public async Task RefreshGlobalModelsAsync()
    {
        IsLoadingModels = true;
        ErrorMessage = null;
        try
        {
            var apiKey = await ResolveGlobalApiKeyAsync();
            var models = await _llmService.ListModelsAsync(Provider, apiKey, BaseUrl);
            ReplaceModels(GlobalModelOptions, models);
            TestMessage = $"Loaded {GlobalModelOptions.Count} model option(s).";
        }
        catch (Exception e)
        {
            LoadFallbackModelOptions(GlobalModelOptions, Provider);
            TestMessage = $"Using built-in model list: {e.Message}";
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    public async Task RefreshChatModelsAsync()
    {
        IsLoadingModels = true;
        ErrorMessage = null;
        try
        {
            var effective = _appState.CurrentChatId == null
                ? null
                : await _settingsService.GetEffectiveSettingsAsync(_appState.CurrentChatId.Value);
            var provider = ChatProvider ?? effective?.Provider ?? Provider;
            var apiKey = await ResolveChatApiKeyAsync(effective?.ApiKey ?? await ResolveGlobalApiKeyAsync());
            var baseUrl = ChatBaseUrl ?? effective?.BaseUrl ?? BaseUrl;
            var models = await _llmService.ListModelsAsync(provider, apiKey, baseUrl);
            ReplaceModels(ChatModelOptions, models);
            TestMessage = $"Loaded {ChatModelOptions.Count} model option(s).";
        }
        catch (Exception e)
        {
            LoadFallbackModelOptions(ChatModelOptions, ChatProvider ?? Provider);
            TestMessage = $"Using built-in model list: {e.Message}";
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    public void ApplyGlobalProviderDefaults(ProviderInfo provider)
    {
        SelectedProvider = provider;
        Provider = provider.Key;
        BaseUrl = provider.DefaultBaseUrl;
        Model = provider.DefaultModel;
        ThinkingDepth = ReasoningDepths.Auto;
        IsGlobalLongContextEnabled = IsDeepSeekProvider(provider.Key) &&
            ProviderModelResolver.IsDeepSeekV4Model(provider.DefaultModel);
        LoadFallbackModelOptions(GlobalModelOptions, provider.Key);
    }

    public void ApplyChatProviderDefaults(ProviderInfo? provider)
    {
        ChatProvider = provider?.Key;
        ChatBaseUrl = provider?.DefaultBaseUrl;
        ChatModel = provider?.DefaultModel;
        ChatThinkingDepth = null;
        IsChatLongContextEnabled = IsDeepSeekProvider(provider?.Key) &&
            ProviderModelResolver.IsDeepSeekV4Model(provider?.DefaultModel);
        LoadFallbackModelOptions(ChatModelOptions, provider?.Key ?? Provider);
    }

    public static bool IsDeepSeekProvider(string? provider) =>
        string.Equals(provider, "deepseek", StringComparison.OrdinalIgnoreCase);

    public static string ApplyLongContextSuffix(string? model, bool enabled)
    {
        var trimmed = (model ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
            return trimmed;
        var withoutSuffix = RemoveLongContextSuffix(trimmed);
        return withoutSuffix;
    }

    [RelayCommand]
    public async Task SaveGlobalAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            await _settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
                Provider: Provider,
                ApiKey: ApiKey,
                BaseUrl: BaseUrl,
                Model: Model,
                UseLongContext: IsGlobalLongContextEnabled,
                ThinkingDepth: ThinkingDepth,
                Temperature: Temperature,
                MaxTokens: MaxTokens,
                SystemPrompt: SystemPrompt,
                UserRole: UserRole
            ));
            _soundService.SetVolume(SoundVolume);
            _soundService.SetEnabled(IsSoundEffectsEnabled);
            // Auto-select provider defaults
            var match = Providers.FirstOrDefault(p => p.Key == Provider);
            if (match != null)
            {
                if (string.IsNullOrEmpty(BaseUrl)) BaseUrl = match.DefaultBaseUrl;
                if (string.IsNullOrEmpty(Model)) Model = match.DefaultModel;
            }
            await LoadAsync(); // Refresh with masked key
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task SaveChatAsync()
    {
        if (_appState.CurrentChatId == null) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            await _settingsService.UpdateChatSettingsAsync(_appState.CurrentChatId.Value, new ChatSettingsUpdateDto(
                Provider: ChatProvider,
                ApiKey: ChatApiKey,
                BaseUrl: ChatBaseUrl,
                Model: ChatModel,
                UseLongContext: IsChatLongContextEnabled,
                ThinkingDepth: ChatThinkingDepth,
                Temperature: ChatTemperature,
                MaxTokens: ChatMaxTokens,
                UserRole: ChatUserRole
            ));
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedProviderChanged(ProviderInfo? value)
    {
        if (value != null)
            Provider = value.Key;
    }

    private static bool HasLongContextSuffix(string? model) =>
        ProviderModelResolver.HasLongContextSuffix(model);

    private static string RemoveLongContextSuffix(string model) =>
        ProviderModelResolver.NormalizeModelForStorage(model);

    private static bool IsSupportedDeepSeekLongContextModel(string model) =>
        string.Equals(model, "deepseek-v4-pro", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(model, "deepseek-v4-flash", StringComparison.OrdinalIgnoreCase);

    private static void LoadFallbackModelOptions(ObservableCollection<string> target, string? provider) =>
        ReplaceModels(target, ProviderModelCatalog.FallbackModels(provider ?? "openai"));

    private static void ReplaceModels(ObservableCollection<string> target, IEnumerable<string> models)
    {
        target.Clear();
        foreach (var model in models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase))
            target.Add(model);
    }

    private void ClearChatOverrides()
    {
        ChatProvider = null;
        ChatApiKey = null;
        ChatBaseUrl = null;
        ChatModel = null;
        ChatThinkingDepth = null;
        ChatTemperature = null;
        ChatMaxTokens = null;
        ChatUserRole = null;
        IsChatLongContextEnabled = false;
        LoadFallbackModelOptions(ChatModelOptions, Provider);
    }

    private async Task<string> ResolveGlobalApiKeyAsync()
    {
        if (!ApiKeyMasker.IsMasked(ApiKey))
            return ApiKey;

        var stored = await _settingsService.GetGlobalSettingsRawAsync();
        return ProtectedSecret.Reveal(stored.ApiKey);
    }

    private async Task<string> ResolveChatApiKeyAsync(string effectiveApiKey)
    {
        if (!string.IsNullOrWhiteSpace(ChatApiKey) && !ApiKeyMasker.IsMasked(ChatApiKey))
            return ChatApiKey;

        if (_appState.CurrentChatId != null && ApiKeyMasker.IsMasked(ChatApiKey ?? string.Empty))
        {
            var stored = await _settingsService.GetChatSettingsAsync(_appState.CurrentChatId.Value);
            return ProtectedSecret.Reveal(stored?.ApiKey);
        }

        return effectiveApiKey;
    }
}
