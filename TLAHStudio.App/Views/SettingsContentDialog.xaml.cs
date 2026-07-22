using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using TLAHStudio.App.ViewModels;
using TLAHStudio.Core.Services.Plugins;
using TLAHStudio.Core.Services.Workspace;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services;

namespace TLAHStudio.App.Views;

public sealed partial class SettingsContentDialog : ContentDialog
{
    private SettingsDialogViewModel? _vm;
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);
    private bool _isPopulating;
    private string _selectedCategory = "appearance";
    private XamlRoot? _observedXamlRoot;

    public SettingsContentDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            AttachResponsiveLayout();
            _vm = DataContext as SettingsDialogViewModel;
            if (_vm == null) return;

            await _vm.LoadAsync();
            ProviderCombo.ItemsSource = _vm.Providers;
            ChatProviderCombo.ItemsSource = _vm.Providers;
            ModelPicker.ItemsSource = _vm.GlobalModelOptions;
            ChatModelPicker.ItemsSource = _vm.ChatModelOptions;
            ThinkingDepthCombo.ItemsSource = _vm.ThinkingDepthOptions;
            ChatThinkingDepthCombo.ItemsSource = _vm.ChatThinkingDepthOptions;
            ProviderCombo.SelectionChanged += ProviderCombo_SelectionChanged;
            ChatProviderCombo.SelectionChanged += ChatProviderCombo_SelectionChanged;
            PopulateGlobal();
            PopulateChat();
            PopulateSkills();  // M4.9.0
            UpdateScope();
            UpdateCategorySelection();
            UpdateTestText();
            _ = AutoRefreshVisibleModelListsAsync();
        };
        Closed += (_, _) => DetachResponsiveLayout();
    }

    private void AttachResponsiveLayout()
    {
        if (ReferenceEquals(_observedXamlRoot, XamlRoot))
        {
            ApplyResponsiveLayout();
            return;
        }

        DetachResponsiveLayout();
        _observedXamlRoot = XamlRoot;
        if (_observedXamlRoot != null)
            _observedXamlRoot.Changed += OnXamlRootChanged;
        ApplyResponsiveLayout();
    }

    private void DetachResponsiveLayout()
    {
        if (_observedXamlRoot != null)
            _observedXamlRoot.Changed -= OnXamlRootChanged;
        _observedXamlRoot = null;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        if (XamlRoot == null)
            return;

        var rootSize = XamlRoot.Size;
        if (rootSize.Width <= 0 || rootSize.Height <= 0)
            return;

        // Size only the content root. Setting Width/MinWidth/MaxWidth on the
        // ContentDialog itself fights the presenter and can anchor the popup to
        // the left while its content overflows the presenter's default width.
        var compact = rootSize.Width < 760;
        var horizontalMargin = compact ? 16 : 64;
        SettingsLayoutRoot.Width = Math.Min(1040, Math.Max(0, rootSize.Width - horizontalMargin));
        SettingsNavColumn.Width = new GridLength(rootSize.Width < 620 ? 136 : compact ? 150 : 220);
        AppearanceControlColumn.Width = new GridLength(compact ? 140 : 180);
        SettingsHeaderPanel.Margin = compact
            ? new Thickness(18, 6, 16, 14)
            : new Thickness(26, 6, 24, 18);
        SettingsScrollViewer.Padding = compact
            ? new Thickness(18, 0, 16, 16)
            : new Thickness(26, 0, 24, 20);
        SettingsStatusPanel.Margin = compact
            ? new Thickness(18, 8, 16, 10)
            : new Thickness(26, 8, 24, 10);
        SettingsFooterHint.Visibility = rootSize.Height < 500
            ? Visibility.Collapsed
            : Visibility.Visible;

        var verticalChrome = rootSize.Height < 620 ? 112 : 148;
        SettingsLayoutRoot.Height = Math.Min(720, Math.Max(0, rootSize.Height - verticalChrome));
    }

    private async Task AutoRefreshVisibleModelListsAsync()
    {
        if (_vm == null)
            return;

        CollectGlobal();
        await _vm.RefreshGlobalModelsAsync();
        ModelPicker.SelectedItem = FindModelOption(_vm.GlobalModelOptions, ModelBox.Text);
        if (_vm.HasCurrentChat)
        {
            CollectChat();
            await _vm.RefreshChatModelsAsync();
            ChatModelPicker.SelectedItem = FindModelOption(_vm.ChatModelOptions, ChatModelBox.Text);
        }
        UpdateTestText();
    }

    private void PopulateGlobal()
    {
        if (_vm == null) return;
        _isPopulating = true;
        ProviderCombo.SelectedItem = _vm.SelectedProvider;
        ApiKeyBox.Password = _vm.ApiKey;
        BaseUrlBox.Text = _vm.BaseUrl;
        ModelBox.Text = _vm.Model;
        DeepSeekLongContextCheckBox.IsChecked = _vm.IsGlobalLongContextEnabled;
        ThinkingDepthCombo.SelectedItem = ReasoningDepths.Normalize(_vm.ThinkingDepth);
        ModelPicker.SelectedItem = FindModelOption(_vm.GlobalModelOptions, ModelBox.Text);
        UpdateDeepSeekControls();
        TempSlider.Value = _vm.Temperature;
        TempValueLabel.Text = _vm.Temperature.ToString("0.0");
        MaxTokensBox.Value = _vm.MaxTokens;
        SysPromptBox.Text = _vm.SystemPrompt;
        UserRoleBox.Text = _vm.UserRole;
        SoundEffectsToggle.IsOn = _vm.IsSoundEffectsEnabled;
        SoundVolumeSlider.Value = _vm.SoundVolume;
        // M4.7.0: Populate theme/density combos
        ThemeCombo.Items.Clear();
        ThemeCombo.Items.Add("Dark");
        ThemeCombo.Items.Add("Light");
        ThemeCombo.SelectedItem = (App.MainWindow as MainWindow)?.ThemeService.CurrentTheme == TLAHStudio.App.ViewModels.ElementTheme.Dark
            ? "Dark" : "Light";
        DensityCombo.Items.Clear();
        DensityCombo.Items.Add("Comfortable");
        DensityCombo.Items.Add("Compact");
        DensityCombo.SelectedItem = (App.MainWindow as MainWindow)?.UiDensityService.CurrentDensity == UiDensity.Compact
            ? "Compact" : "Comfortable";
        AquariumQualityCombo.ItemsSource = Enum.GetNames<AquariumQuality>();
        AquariumQualityCombo.SelectedItem = App.Services
            .GetRequiredService<IAquariumPreferencesService>()
            .CurrentQuality
            .ToString();
        // M4.9.0: Output style combo
        OutputStyleCombo.ItemsSource = _vm.OutputStyleOptions;
        OutputStyleCombo.SelectedItem = _vm.OutputStyle;
        _isPopulating = false;
    }

    private void PopulateChat()
    {
        if (_vm == null) return;
        _isPopulating = true;
        ChatProviderCombo.SelectedItem = _vm.Providers.FirstOrDefault(p => p.Key == _vm.ChatProvider);
        ChatApiKeyBox.Password = _vm.ChatApiKey ?? string.Empty;
        ChatApiKeyBox.Tag = null;
        ChatBaseUrlBox.Text = _vm.ChatBaseUrl ?? string.Empty;
        ChatModelBox.Text = _vm.ChatModel ?? string.Empty;
        ChatDeepSeekLongContextCheckBox.IsChecked = _vm.IsChatLongContextEnabled;
        ChatThinkingDepthCombo.SelectedItem = _vm.ChatThinkingDepth == null
            ? SettingsDialogViewModel.InheritOption
            : ReasoningDepths.Normalize(_vm.ChatThinkingDepth);
        ChatModelPicker.SelectedItem = FindModelOption(_vm.ChatModelOptions, ChatModelBox.Text);
        UpdateDeepSeekControls();
        ChatTempBox.Text = _vm.ChatTemperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ChatMaxTokensBox.Text = _vm.ChatMaxTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ChatUserRoleBox.Text = _vm.ChatUserRole ?? string.Empty;
        _isPopulating = false;
    }

    private void Scope_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not Button button) return;
        if ((button.Tag as string) == "chat" && !_vm.HasCurrentChat) return;
        _vm.IsGlobalTab = (button.Tag as string) != "chat";
        SettingsSearchBox.Text = string.Empty;
        _selectedCategory = _vm.IsGlobalTab ? "appearance" : "chat";
        UpdateScope();
        DispatcherQueue.TryEnqueue(() => NavigateToCategory(_selectedCategory));
    }

    private void ClearChatProvider_Click(object sender, RoutedEventArgs e)
    {
        ChatProviderCombo.SelectedItem = null;
    }

    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_vm == null) return;
        var deferral = args.GetDeferral();

        ErrorText.Visibility = Visibility.Collapsed;
        if (_vm.IsGlobalTab)
        {
            CollectGlobal();
            await _vm.SaveGlobalAsync();
        }
        else
        {
            CollectChat();
            await _vm.SaveChatAsync();
        }

        if (!string.IsNullOrWhiteSpace(_vm.ErrorMessage))
        {
            ErrorText.Text = _vm.ErrorMessage;
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
        }

        deferral.Complete();
    }

    private void CollectGlobal()
    {
        if (_vm == null) return;
        if (ProviderCombo.SelectedItem is ProviderInfo provider)
            _vm.Provider = provider.Key;
        _vm.ApiKey = ApiKeyBox.Password;
        _vm.BaseUrl = BaseUrlBox.Text;
        _vm.IsGlobalLongContextEnabled = DeepSeekLongContextCheckBox.IsChecked == true;
        _vm.Model = SettingsDialogViewModel.ApplyLongContextSuffix(
            ModelBox.Text,
            SettingsDialogViewModel.IsDeepSeekProvider(_vm.Provider) && _vm.IsGlobalLongContextEnabled);
        _vm.ThinkingDepth = ThinkingDepthCombo.SelectedItem as string ?? ReasoningDepths.Auto;
        _vm.Temperature = TempSlider.Value;
        _vm.MaxTokens = double.IsNaN(MaxTokensBox.Value) ? _vm.MaxTokens : (int)MaxTokensBox.Value;
        _vm.SystemPrompt = SysPromptBox.Text;
        _vm.UserRole = UserRoleBox.Text;
        _vm.IsSoundEffectsEnabled = SoundEffectsToggle.IsOn;
        _vm.SoundVolume = SoundVolumeSlider.Value;
    }

    private void CollectChat()
    {
        if (_vm == null) return;
        _vm.ChatProvider = ChatProviderCombo.SelectedItem is ProviderInfo provider ? provider.Key : null;
        _vm.ChatApiKey = Equals(ChatApiKeyBox.Tag, "clear")
            ? string.Empty
            : NullIfWhiteSpace(ChatApiKeyBox.Password);
        _vm.ChatBaseUrl = NullIfWhiteSpace(ChatBaseUrlBox.Text);
        _vm.IsChatLongContextEnabled = ChatDeepSeekLongContextCheckBox.IsChecked == true;
        _vm.ChatModel = NullIfWhiteSpace(SettingsDialogViewModel.ApplyLongContextSuffix(
            ChatModelBox.Text,
            SettingsDialogViewModel.IsDeepSeekProvider(_vm.ChatProvider) && _vm.IsChatLongContextEnabled));
        var chatThinkingDepth = ChatThinkingDepthCombo.SelectedItem as string;
        _vm.ChatThinkingDepth = chatThinkingDepth == SettingsDialogViewModel.InheritOption
            ? null
            : chatThinkingDepth;
        _vm.ChatTemperature = TryDouble(ChatTempBox.Text);
        _vm.ChatMaxTokens = TryInt(ChatMaxTokensBox.Text);
        _vm.ChatUserRole = NullIfWhiteSpace(ChatUserRoleBox.Text);
    }

    private void UpdateScope()
    {
        if (_vm == null) return;
        if (!_vm.HasCurrentChat)
            _vm.IsGlobalTab = true;

        ChatTabButton.IsEnabled = _vm.HasCurrentChat;
        GlobalForm.Visibility = _vm.IsGlobalTab ? Visibility.Visible : Visibility.Collapsed;
        ChatForm.Visibility = _vm.IsGlobalTab ? Visibility.Collapsed : Visibility.Visible;
        AppearanceNavButton.Visibility = _vm.IsGlobalTab ? Visibility.Visible : Visibility.Collapsed;
        SoundNavButton.Visibility = _vm.IsGlobalTab ? Visibility.Visible : Visibility.Collapsed;
        ConnectionNavButton.Visibility = _vm.IsGlobalTab ? Visibility.Visible : Visibility.Collapsed;
        BehaviorNavButton.Visibility = _vm.IsGlobalTab ? Visibility.Visible : Visibility.Collapsed;
        SkillsNavButton.Visibility = _vm.IsGlobalTab ? Visibility.Visible : Visibility.Collapsed;
        ChatSettingsNavButton.Visibility = _vm.IsGlobalTab ? Visibility.Collapsed : Visibility.Visible;
        ScopeHint.Text = _vm.IsGlobalTab
            ? "These defaults apply to every chat unless a chat-level override is set."
            : $"Overrides for chat {_vm.ChatIdLabel}. Empty fields inherit the global setting.";

        SetScopeTab(GlobalTabButton, _vm.IsGlobalTab);
        SetScopeTab(ChatTabButton, !_vm.IsGlobalTab);
        ApplySettingsFilter(SettingsSearchBox.Text);
        UpdateCategorySelection();
    }

    private void SettingsCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string category)
            return;

        SettingsSearchBox.Text = string.Empty;
        NavigateToCategory(category);
    }

    private void SettingsSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ApplySettingsFilter(sender.Text);
    }

    private void ApplySettingsFilter(string? query)
    {
        if (_vm == null)
            return;

        var normalized = query?.Trim() ?? string.Empty;
        if (_vm.IsGlobalTab)
        {
            var visibleCount = 0;
            if (string.IsNullOrEmpty(normalized))
            {
                AppearanceSection.Visibility = _selectedCategory == "appearance" ? Visibility.Visible : Visibility.Collapsed;
                SoundSection.Visibility = _selectedCategory == "sound" ? Visibility.Visible : Visibility.Collapsed;
                ConnectionSection.Visibility = _selectedCategory == "connection" ? Visibility.Visible : Visibility.Collapsed;
                BehaviorSection.Visibility = _selectedCategory == "behavior" ? Visibility.Visible : Visibility.Collapsed;
                SkillsSection.Visibility = _selectedCategory == "skills" ? Visibility.Visible : Visibility.Collapsed;
                visibleCount = 1;
            }
            else
            {
                visibleCount += SetSectionSearchVisibility(AppearanceSection, normalized) ? 1 : 0;
                visibleCount += SetSectionSearchVisibility(SoundSection, normalized) ? 1 : 0;
                visibleCount += SetSectionSearchVisibility(ConnectionSection, normalized) ? 1 : 0;
                visibleCount += SetSectionSearchVisibility(BehaviorSection, normalized) ? 1 : 0;
                visibleCount += SetSectionSearchVisibility(SkillsSection, normalized) ? 1 : 0;
            }
            ChatOverridesSection.Visibility = Visibility.Visible;
            SearchEmptyState.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            AppearanceSection.Visibility = Visibility.Visible;
            SoundSection.Visibility = Visibility.Visible;
            ConnectionSection.Visibility = Visibility.Visible;
            BehaviorSection.Visibility = Visibility.Visible;
            SkillsSection.Visibility = Visibility.Visible;
            var chatVisible = SetSectionSearchVisibility(ChatOverridesSection, normalized);
            SearchEmptyState.Visibility = chatVisible ? Visibility.Collapsed : Visibility.Visible;
        }

        if (string.IsNullOrEmpty(normalized))
            UpdateCategorySelection();
        else
            ClearCategorySelection();
    }

    private static bool SetSectionSearchVisibility(FrameworkElement section, string query)
    {
        var isMatch = string.IsNullOrEmpty(query) || MatchesSearch(section, query);
        section.Visibility = isMatch ? Visibility.Visible : Visibility.Collapsed;
        return isMatch;
    }

    private static bool MatchesSearch(FrameworkElement section, string query)
    {
        var searchableText = section.Tag as string ?? string.Empty;
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term => searchableText.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void NavigateToCategory(string category)
    {
        var target = CategoryTarget(category);
        if (target == null)
            return;

        _selectedCategory = category;
        ApplySettingsFilter(string.Empty);
        UpdateCategorySelection();
        // Category navigation swaps a single section in place. BringIntoView
        // could translate the dialog's scroll presenter before layout settled,
        // leaving the first controls above the viewport.
        SettingsScrollViewer.ChangeView(null, 0, null, disableAnimation: false);
    }

    private FrameworkElement? CategoryTarget(string category) => category switch
    {
        "appearance" => AppearanceSection,
        "sound" => SoundSection,
        "connection" => ConnectionSection,
        "behavior" => BehaviorSection,
        "skills" => SkillsSection,
        "chat" => ChatOverridesSection,
        _ => null
    };

    private void UpdateCategorySelection()
    {
        SetCategoryButton(AppearanceNavButton, _selectedCategory == "appearance");
        SetCategoryButton(SoundNavButton, _selectedCategory == "sound");
        SetCategoryButton(ConnectionNavButton, _selectedCategory == "connection");
        SetCategoryButton(BehaviorNavButton, _selectedCategory == "behavior");
        SetCategoryButton(SkillsNavButton, _selectedCategory == "skills");
        SetCategoryButton(ChatSettingsNavButton, _selectedCategory == "chat");
    }

    private void ClearCategorySelection()
    {
        SetCategoryButton(AppearanceNavButton, false);
        SetCategoryButton(SoundNavButton, false);
        SetCategoryButton(ConnectionNavButton, false);
        SetCategoryButton(BehaviorNavButton, false);
        SetCategoryButton(SkillsNavButton, false);
        SetCategoryButton(ChatSettingsNavButton, false);
    }

    private static void SetCategoryButton(Button button, bool selected)
    {
        button.Background = selected ? Brush("AccentSoftBrush") : TransparentBrush;
        button.Foreground = selected ? Brush("TextPrimaryBrush") : Brush("TextSecondaryBrush");
        button.FontWeight = selected
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    private void TempSlider_ValueChanged(object sender, RoutedEventArgs e)
    {
        TempValueLabel.Text = TempSlider.Value.ToString("0.0");
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulating || _vm == null) return;
        var isDark = (sender as ComboBox)?.SelectedItem is "Dark";
        var theme = isDark
            ? TLAHStudio.App.ViewModels.ElementTheme.Dark
            : TLAHStudio.App.ViewModels.ElementTheme.Light;
        RequestedTheme = isDark
            ? Microsoft.UI.Xaml.ElementTheme.Dark
            : Microsoft.UI.Xaml.ElementTheme.Light;
        (App.MainWindow as MainWindow)?.ThemeService.SetTheme(theme);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_vm == null)
                return;

            SetScopeTab(GlobalTabButton, _vm.IsGlobalTab);
            SetScopeTab(ChatTabButton, !_vm.IsGlobalTab);
            UpdateCategorySelection();
        });
    }

    private void DensityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulating || _vm == null) return;
        var density = (sender as ComboBox)?.SelectedItem is "Compact"
            ? UiDensity.Compact : UiDensity.Comfortable;
        (App.MainWindow as MainWindow)?.UiDensityService.SetDensity(density);
    }

    private void AquariumQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulating || _vm == null ||
            sender is not ComboBox { SelectedItem: string selected } ||
            !Enum.TryParse<AquariumQuality>(selected, ignoreCase: true, out var quality))
        {
            return;
        }

        App.Services.GetRequiredService<IAquariumPreferencesService>().SetQuality(quality);
    }

    // M4.9.0: Output style selector
    private void OutputStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulating || _vm == null) return;
        if (sender is ComboBox combo && combo.SelectedItem is string style)
            _vm.OutputStyle = style;
    }

    // M4.9.0: Skills management
    private async void ReloadSkills_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.LoadSkillsAsync();
        SkillsSummaryText.Text = _vm.SkillsSummary;
        SkillsListControl.ItemsSource = _vm.SkillsList;
    }

    private void OpenUserSkills_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio", "skills");
        Directory.CreateDirectory(dir);
        _ = Windows.System.Launcher.LaunchFolderPathAsync(dir);
    }

    private async void OpenProjectSkills_Click(object sender, RoutedEventArgs e)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var wsService = scope.ServiceProvider.GetService<IWorkspaceRootService>();
        var appState = scope.ServiceProvider.GetService<IAppStateService>();
        string? dir = null;
        if (wsService != null && appState?.CurrentChatId != null)
        {
            var root = await wsService.GetRootAsync(appState.CurrentChatId.Value);
            if (!string.IsNullOrWhiteSpace(root.RootPath))
            {
                dir = Path.Combine(root.RootPath, ".tlah", "skills");
                Directory.CreateDirectory(dir);
            }
        }
        if (dir != null)
            await Windows.System.Launcher.LaunchFolderPathAsync(dir);
    }

    private void OpenBundledSkills_Click(object sender, RoutedEventArgs e)
    {
        // Bundled skills are read-only built-in. Show in Explorer if available.
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "Assets", "bundled-skills");
        if (Directory.Exists(bundledDir))
            _ = Windows.System.Launcher.LaunchFolderPathAsync(bundledDir);
    }

    private async void PopulateSkills()
    {
        if (_vm == null) return;
        await _vm.LoadSkillsAsync();
        SkillsSummaryText.Text = _vm.SkillsSummary;
        SkillsListControl.ItemsSource = _vm.SkillsList;
    }

    private async void ResetGlobal_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.ResetGlobalSettings();
        await _vm.LoadAsync();
        PopulateGlobal();
    }

    private static void SetScopeTab(Button button, bool selected)
    {
        button.Background = selected ? Brush("AccentSoftBrush") : TransparentBrush;
        button.Foreground = selected ? Brush("AccentBrush") : Brush("TextSecondaryBrush");
        button.FontWeight = selected
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double? TryDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static int? TryInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private async void TestGlobal_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        CollectGlobal();
        await _vm.TestGlobalConnectionAsync();
        UpdateTestText();
    }

    private async void TestChat_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        CollectChat();
        await _vm.TestChatConnectionAsync();
        UpdateTestText();
    }

    private async void RefreshGlobalModels_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        CollectGlobal();
        await _vm.RefreshGlobalModelsAsync();
        ModelPicker.SelectedItem = FindModelOption(_vm.GlobalModelOptions, ModelBox.Text);
        UpdateTestText();
    }

    private async void RefreshChatModels_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        CollectChat();
        await _vm.RefreshChatModelsAsync();
        ChatModelPicker.SelectedItem = FindModelOption(_vm.ChatModelOptions, ChatModelBox.Text);
        UpdateTestText();
    }

    private void ClearGlobalKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Password = string.Empty;
        _vm?.ClearGlobalApiKey();
        UpdateTestText();
    }

    private void ClearChatKey_Click(object sender, RoutedEventArgs e)
    {
        ChatApiKeyBox.Password = string.Empty;
        ChatApiKeyBox.Tag = "clear";
        _vm?.ClearChatApiKey();
        UpdateTestText();
    }

    private void UpdateTestText()
    {
        if (_vm == null || string.IsNullOrWhiteSpace(_vm.TestMessage))
        {
            TestText.Visibility = Visibility.Collapsed;
            return;
        }

        TestText.Text = _vm.TestMessage;
        TestText.Visibility = Visibility.Visible;
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _isPopulating || ProviderCombo.SelectedItem is not ProviderInfo provider)
            return;

        _vm.ApplyGlobalProviderDefaults(provider);
        BaseUrlBox.Text = _vm.BaseUrl;
        ModelBox.Text = _vm.Model;
        DeepSeekLongContextCheckBox.IsChecked = _vm.IsGlobalLongContextEnabled;
        ThinkingDepthCombo.SelectedItem = ReasoningDepths.Normalize(_vm.ThinkingDepth);
        ModelPicker.SelectedItem = FindModelOption(_vm.GlobalModelOptions, ModelBox.Text);
        UpdateDeepSeekControls();
        _ = RefreshGlobalModelsAfterProviderChangeAsync();
    }

    private void ChatProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _isPopulating)
            return;

        _vm.ApplyChatProviderDefaults(ChatProviderCombo.SelectedItem as ProviderInfo);
        ChatBaseUrlBox.Text = _vm.ChatBaseUrl ?? string.Empty;
        ChatModelBox.Text = _vm.ChatModel ?? string.Empty;
        ChatDeepSeekLongContextCheckBox.IsChecked = _vm.IsChatLongContextEnabled;
        ChatThinkingDepthCombo.SelectedItem = _vm.ChatThinkingDepth == null
            ? SettingsDialogViewModel.InheritOption
            : ReasoningDepths.Normalize(_vm.ChatThinkingDepth);
        ChatModelPicker.SelectedItem = FindModelOption(_vm.ChatModelOptions, ChatModelBox.Text);
        UpdateDeepSeekControls();
        _ = RefreshChatModelsAfterProviderChangeAsync();
    }

    private async Task RefreshGlobalModelsAfterProviderChangeAsync()
    {
        if (_vm == null)
            return;

        CollectGlobal();
        await _vm.RefreshGlobalModelsAsync();
        ModelPicker.SelectedItem = FindModelOption(_vm.GlobalModelOptions, ModelBox.Text);
        UpdateTestText();
    }

    private async Task RefreshChatModelsAfterProviderChangeAsync()
    {
        if (_vm == null)
            return;

        CollectChat();
        await _vm.RefreshChatModelsAsync();
        ChatModelPicker.SelectedItem = FindModelOption(_vm.ChatModelOptions, ChatModelBox.Text);
        UpdateTestText();
    }

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulating || ModelPicker.SelectedItem is not string model)
            return;
        if (HasLongContextSuffix(model) || ProviderModelResolver.IsDeepSeekV4Model(model))
            DeepSeekLongContextCheckBox.IsChecked = true;
        ModelBox.Text = SettingsDialogViewModel.ApplyLongContextSuffix(
            model,
            SettingsDialogViewModel.IsDeepSeekProvider((ProviderCombo.SelectedItem as ProviderInfo)?.Key) &&
            DeepSeekLongContextCheckBox.IsChecked == true);
    }

    private void ChatModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulating || ChatModelPicker.SelectedItem is not string model)
            return;
        if (HasLongContextSuffix(model) || ProviderModelResolver.IsDeepSeekV4Model(model))
            ChatDeepSeekLongContextCheckBox.IsChecked = true;
        ChatModelBox.Text = SettingsDialogViewModel.ApplyLongContextSuffix(
            model,
            SettingsDialogViewModel.IsDeepSeekProvider((ChatProviderCombo.SelectedItem as ProviderInfo)?.Key) &&
            ChatDeepSeekLongContextCheckBox.IsChecked == true);
    }

    private void DeepSeekLongContext_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulating)
            return;
        ModelBox.Text = SettingsDialogViewModel.ApplyLongContextSuffix(
            ModelBox.Text,
            SettingsDialogViewModel.IsDeepSeekProvider((ProviderCombo.SelectedItem as ProviderInfo)?.Key) &&
            DeepSeekLongContextCheckBox.IsChecked == true);
    }

    private void ChatDeepSeekLongContext_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulating)
            return;
        ChatModelBox.Text = SettingsDialogViewModel.ApplyLongContextSuffix(
            ChatModelBox.Text,
            SettingsDialogViewModel.IsDeepSeekProvider((ChatProviderCombo.SelectedItem as ProviderInfo)?.Key) &&
            ChatDeepSeekLongContextCheckBox.IsChecked == true);
    }

    private void UpdateDeepSeekControls()
    {
        var showGlobal = SettingsDialogViewModel.IsDeepSeekProvider((ProviderCombo.SelectedItem as ProviderInfo)?.Key);
        DeepSeekLongContextCheckBox.Visibility = showGlobal ? Visibility.Visible : Visibility.Collapsed;

        var showChat = SettingsDialogViewModel.IsDeepSeekProvider((ChatProviderCombo.SelectedItem as ProviderInfo)?.Key);
        ChatDeepSeekLongContextCheckBox.Visibility = showChat ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string? FindModelOption(IEnumerable<string> models, string value)
    {
        var exact = models.FirstOrDefault(m => string.Equals(m, value, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        var normalized = RemoveLongContextSuffix(value);
        return models.FirstOrDefault(m => string.Equals(m, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasLongContextSuffix(string? value) =>
        ProviderModelResolver.HasLongContextSuffix(value);

    private static string RemoveLongContextSuffix(string? value)
    {
        return ProviderModelResolver.NormalizeModelForStorage(value);
    }
}
