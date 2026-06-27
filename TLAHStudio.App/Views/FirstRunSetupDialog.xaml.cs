using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLAHStudio.App.ViewModels;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services;

namespace TLAHStudio.App.Views;

public sealed partial class FirstRunSetupDialog : ContentDialog
{
    private SettingsDialogViewModel? _vm;
    private bool _isPopulating;

    public FirstRunSetupDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as SettingsDialogViewModel;
        if (_vm == null)
            return;

        await _vm.LoadAsync();
        _isPopulating = true;
        ProviderCombo.ItemsSource = _vm.Providers;
        ModelPicker.ItemsSource = _vm.GlobalModelOptions;
        ProviderCombo.SelectedItem = _vm.SelectedProvider ?? _vm.Providers.FirstOrDefault();
        BaseUrlBox.Text = _vm.BaseUrl;
        ModelBox.Text = _vm.Model;
        DeepSeekLongContextCheckBox.IsChecked = _vm.IsGlobalLongContextEnabled;
        ModelPicker.SelectedItem = FindModelOption(_vm.GlobalModelOptions, ModelBox.Text);
        UpdateDeepSeekControls();
        _isPopulating = false;
        ProviderCombo.SelectionChanged += ProviderCombo_SelectionChanged;
        _ = RefreshModelsAsync(showStatus: false);
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _isPopulating || ProviderCombo.SelectedItem is not ProviderInfo provider)
            return;

        _vm.ApplyGlobalProviderDefaults(provider);
        BaseUrlBox.Text = _vm.BaseUrl;
        ModelBox.Text = _vm.Model;
        DeepSeekLongContextCheckBox.IsChecked = _vm.IsGlobalLongContextEnabled;
        ModelPicker.SelectedItem = FindModelOption(_vm.GlobalModelOptions, ModelBox.Text);
        UpdateDeepSeekControls();
        _ = RefreshModelsAsync();
    }

    private async void Test_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_vm == null) return;
        Collect();
        await _vm.TestGlobalConnectionAsync();
        ShowStatus(_vm.TestMessage ?? "Test completed.");
    }

    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_vm == null) return;
        var deferral = args.GetDeferral();
        Collect();

        if (string.IsNullOrWhiteSpace(_vm.ApiKey))
        {
            args.Cancel = true;
            ShowStatus("Enter an API key before continuing.");
            deferral.Complete();
            return;
        }

        await _vm.SaveGlobalAsync();
        if (!string.IsNullOrWhiteSpace(_vm.ErrorMessage))
        {
            args.Cancel = true;
            ShowStatus(_vm.ErrorMessage);
        }

        deferral.Complete();
    }

    private void Collect()
    {
        if (_vm == null) return;
        if (ProviderCombo.SelectedItem is ProviderInfo provider)
            _vm.Provider = provider.Key;
        _vm.BaseUrl = BaseUrlBox.Text.Trim();
        _vm.IsGlobalLongContextEnabled = DeepSeekLongContextCheckBox.IsChecked == true;
        _vm.Model = SettingsDialogViewModel.ApplyLongContextSuffix(
            ModelBox.Text,
            SettingsDialogViewModel.IsDeepSeekProvider(_vm.Provider) && _vm.IsGlobalLongContextEnabled);
        _vm.ApiKey = ApiKeyBox.Password;
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusCard.Visibility = Visibility.Visible;
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModelsAsync();
    }

    private async Task RefreshModelsAsync(bool showStatus = true)
    {
        if (_vm == null)
            return;

        Collect();
        await _vm.RefreshGlobalModelsAsync();
        ModelPicker.SelectedItem = FindModelOption(_vm.GlobalModelOptions, ModelBox.Text);
        if (showStatus)
            ShowStatus(_vm.TestMessage ?? "Model list refreshed.");
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

    private void DeepSeekLongContext_Changed(object sender, RoutedEventArgs e)
    {
        if (_isPopulating)
            return;
        ModelBox.Text = SettingsDialogViewModel.ApplyLongContextSuffix(
            ModelBox.Text,
            SettingsDialogViewModel.IsDeepSeekProvider((ProviderCombo.SelectedItem as ProviderInfo)?.Key) &&
            DeepSeekLongContextCheckBox.IsChecked == true);
    }

    private void UpdateDeepSeekControls()
    {
        var show = SettingsDialogViewModel.IsDeepSeekProvider((ProviderCombo.SelectedItem as ProviderInfo)?.Key);
        DeepSeekLongContextCheckBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
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
