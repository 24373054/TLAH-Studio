using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TLAHStudio.Core.Helpers;
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

    // ── Provider list ──────────────────────────────────────────────
    public ObservableCollection<ProviderInfo> Providers { get; } = new();

    [ObservableProperty]
    private ProviderInfo? _selectedProvider;

    // ── Global settings form fields ─────────────────────────────────
    [ObservableProperty] private string _provider = "openai";
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _baseUrl = "https://api.openai.com";
    [ObservableProperty] private string _model = "gpt-4o";
    [ObservableProperty] private double _temperature = 0.7;
    [ObservableProperty] private int _maxTokens = 4096;
    [ObservableProperty] private string _systemPrompt = "You are a helpful assistant.";
    [ObservableProperty] private string _userRole = "user";

    // ── Chat-level override fields ──────────────────────────────────
    [ObservableProperty] private string? _chatProvider;
    [ObservableProperty] private string? _chatApiKey;
    [ObservableProperty] private string? _chatBaseUrl;
    [ObservableProperty] private string? _chatModel;
    [ObservableProperty] private double? _chatTemperature;
    [ObservableProperty] private int? _chatMaxTokens;
    [ObservableProperty] private string? _chatUserRole;

    // ── State ───────────────────────────────────────────────────────
    [ObservableProperty] private bool _isGlobalTab = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _testMessage;

    public bool HasCurrentChat => _appState.CurrentChatId != null;
    public string ChatIdLabel => _appState.CurrentChatId?.ToString("D") ?? "No chat selected";

    public SettingsDialogViewModel(ISettingsService settingsService, ILlmService llmService, IAppStateService appState)
    {
        _settingsService = settingsService;
        _llmService = llmService;
        _appState = appState;

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
            Temperature = gs.Temperature;
            MaxTokens = gs.MaxTokens;
            SystemPrompt = gs.SystemPrompt;
            UserRole = gs.UserRole;

            SelectedProvider = Providers.FirstOrDefault(p => p.Key == gs.Provider);

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
                    ChatTemperature = cs.Temperature;
                    ChatMaxTokens = cs.MaxTokens;
                    ChatUserRole = cs.UserRole;
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
                Temperature: Temperature,
                MaxTokens: MaxTokens,
                SystemPrompt: SystemPrompt,
                UserRole: UserRole
            ));
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

    private void ClearChatOverrides()
    {
        ChatProvider = null;
        ChatApiKey = null;
        ChatBaseUrl = null;
        ChatModel = null;
        ChatTemperature = null;
        ChatMaxTokens = null;
        ChatUserRole = null;
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
