using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TLAHStudio.App.ViewModels;
using Windows.Storage.Streams;

namespace TLAHStudio.App.Views;

public sealed partial class BackgroundSettingsDialog : ContentDialog
{
    private BackgroundSettingsDialogViewModel? _vm;
    private bool _ready;

    public BackgroundSettingsDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _vm = DataContext as BackgroundSettingsDialogViewModel;
            if (_vm == null) return;

            BrightnessSlider.Value = _vm.Brightness;
            OpacitySlider.Value = _vm.Opacity;
            ChatOpacitySlider.Value = _vm.ChatOpacity;
            _ready = true;
            await UpdatePreviewAsync();
        };
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.PickImageCommand.ExecuteAsync(null);
        await UpdatePreviewAsync();
    }

    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_vm == null) return;
        var deferral = args.GetDeferral();
        _vm.Brightness = (int)Math.Round(BrightnessSlider.Value);
        _vm.Opacity = (int)Math.Round(OpacitySlider.Value);
        _vm.ChatOpacity = (int)Math.Round(ChatOpacitySlider.Value);
        _vm.SaveCommand.Execute(null);
        await Task.CompletedTask;
        deferral.Complete();
    }

    private async void Reset_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_vm == null) return;
        args.Cancel = true;
        _vm.ResetCommand.Execute(null);
        BrightnessSlider.Value = _vm.Brightness;
        OpacitySlider.Value = _vm.Opacity;
        ChatOpacitySlider.Value = _vm.ChatOpacity;
        await UpdatePreviewAsync();
    }

    private void Preview_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        UpdatePreviewChrome();
    }

    private async Task UpdatePreviewAsync()
    {
        if (_vm == null) return;

        if (string.IsNullOrWhiteSpace(_vm.ImageBase64))
        {
            PreviewImage.Source = null;
            PreviewImage.Opacity = 0;
            NoImageText.Visibility = Visibility.Visible;
        }
        else
        {
            PreviewImage.Source = await LoadBitmapAsync(_vm.ImageBase64);
            NoImageText.Visibility = Visibility.Collapsed;
        }

        UpdatePreviewChrome();
    }

    private void UpdatePreviewChrome()
    {
        BrightnessValue.Text = $"Brightness: {BrightnessSlider.Value:0}%";
        OpacityValue.Text = $"Background opacity: {OpacitySlider.Value:0}%";
        ChatOpacityValue.Text = $"Chat bubble opacity: {ChatOpacitySlider.Value:0}%";

        PreviewImage.Opacity = string.IsNullOrWhiteSpace(_vm?.ImageBase64)
            ? 0
            : Math.Clamp(OpacitySlider.Value / 100.0, 0, 1);

        if (BrightnessSlider.Value < 100)
        {
            PreviewBrightnessOverlay.Fill = new SolidColorBrush(Microsoft.UI.Colors.Black);
            PreviewBrightnessOverlay.Opacity = (100 - BrightnessSlider.Value) / 100.0;
        }
        else
        {
            PreviewBrightnessOverlay.Fill = new SolidColorBrush(Microsoft.UI.Colors.White);
            PreviewBrightnessOverlay.Opacity = (BrightnessSlider.Value - 100) / 100.0;
        }
    }

    private static async Task<BitmapImage> LoadBitmapAsync(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0);

        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }
}
