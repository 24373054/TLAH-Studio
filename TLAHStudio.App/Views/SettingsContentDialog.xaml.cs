using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLAHStudio.App.ViewModels;
using TLAHStudio.Core.Services;

namespace TLAHStudio.App.Views;

public sealed partial class SettingsContentDialog : ContentDialog
{
    private SettingsDialogViewModel? _vm;
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

    public SettingsContentDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _vm = DataContext as SettingsDialogViewModel;
            if (_vm == null) return;

            await _vm.LoadAsync();
            ProviderCombo.ItemsSource = _vm.Providers;
            ChatProviderCombo.ItemsSource = _vm.Providers;
            PopulateGlobal();
            PopulateChat();
            UpdateScope();
            UpdateTestText();
        };
    }

    private void PopulateGlobal()
    {
        if (_vm == null) return;
        ProviderCombo.SelectedItem = _vm.SelectedProvider;
        ApiKeyBox.Password = _vm.ApiKey;
        BaseUrlBox.Text = _vm.BaseUrl;
        ModelBox.Text = _vm.Model;
        TempSlider.Value = _vm.Temperature;
        MaxTokensBox.Value = _vm.MaxTokens;
        SysPromptBox.Text = _vm.SystemPrompt;
        UserRoleBox.Text = _vm.UserRole;
    }

    private void PopulateChat()
    {
        if (_vm == null) return;
        ChatProviderCombo.SelectedItem = _vm.Providers.FirstOrDefault(p => p.Key == _vm.ChatProvider);
        ChatApiKeyBox.Password = _vm.ChatApiKey ?? string.Empty;
        ChatApiKeyBox.Tag = null;
        ChatBaseUrlBox.Text = _vm.ChatBaseUrl ?? string.Empty;
        ChatModelBox.Text = _vm.ChatModel ?? string.Empty;
        ChatTempBox.Text = _vm.ChatTemperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ChatMaxTokensBox.Text = _vm.ChatMaxTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ChatUserRoleBox.Text = _vm.ChatUserRole ?? string.Empty;
    }

    private void Scope_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not Button button) return;
        if ((button.Tag as string) == "chat" && !_vm.HasCurrentChat) return;
        _vm.IsGlobalTab = (button.Tag as string) != "chat";
        UpdateScope();
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
        _vm.Model = ModelBox.Text;
        _vm.Temperature = TempSlider.Value;
        _vm.MaxTokens = double.IsNaN(MaxTokensBox.Value) ? _vm.MaxTokens : (int)MaxTokensBox.Value;
        _vm.SystemPrompt = SysPromptBox.Text;
        _vm.UserRole = UserRoleBox.Text;
    }

    private void CollectChat()
    {
        if (_vm == null) return;
        _vm.ChatProvider = ChatProviderCombo.SelectedItem is ProviderInfo provider ? provider.Key : null;
        _vm.ChatApiKey = Equals(ChatApiKeyBox.Tag, "clear")
            ? string.Empty
            : NullIfWhiteSpace(ChatApiKeyBox.Password);
        _vm.ChatBaseUrl = NullIfWhiteSpace(ChatBaseUrlBox.Text);
        _vm.ChatModel = NullIfWhiteSpace(ChatModelBox.Text);
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
        ScopeHint.Text = _vm.IsGlobalTab
            ? "These defaults apply to every chat unless a chat-level override is set."
            : $"Overrides for chat {_vm.ChatIdLabel}. Empty fields inherit the global setting.";

        SetScopeTab(GlobalTabButton, _vm.IsGlobalTab);
        SetScopeTab(ChatTabButton, !_vm.IsGlobalTab);
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
}
