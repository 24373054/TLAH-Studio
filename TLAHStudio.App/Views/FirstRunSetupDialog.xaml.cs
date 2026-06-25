using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLAHStudio.App.ViewModels;
using TLAHStudio.Core.Services;

namespace TLAHStudio.App.Views;

public sealed partial class FirstRunSetupDialog : ContentDialog
{
    private SettingsDialogViewModel? _vm;

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
        ProviderCombo.ItemsSource = _vm.Providers;
        ProviderCombo.SelectedItem = _vm.SelectedProvider ?? _vm.Providers.FirstOrDefault();
        BaseUrlBox.Text = _vm.BaseUrl;
        ModelBox.Text = _vm.Model;
        ProviderCombo.SelectionChanged += ProviderCombo_SelectionChanged;
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || ProviderCombo.SelectedItem is not ProviderInfo provider)
            return;

        _vm.SelectedProvider = provider;
        BaseUrlBox.Text = provider.DefaultBaseUrl;
        ModelBox.Text = provider.DefaultModel;
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
        _vm.Model = ModelBox.Text.Trim();
        _vm.ApiKey = ApiKeyBox.Password;
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusCard.Visibility = Visibility.Visible;
    }
}
