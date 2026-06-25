using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TLAHStudio.App.ViewModels;

/// <summary>
/// Background image and transparency settings.
/// Maps from BackgroundSettings.tsx + BackgroundContext.tsx.
/// </summary>
public partial class BackgroundSettingsDialogViewModel : ObservableObject
{
    private readonly IBackgroundService _bgService;

    [ObservableProperty] private string? _imageBase64;
    [ObservableProperty] private int _brightness = 100;   // 0-200, 100 = normal
    [ObservableProperty] private int _opacity = 30;        // 0-100, background opacity
    [ObservableProperty] private int _chatOpacity = 100;   // 0-100, chat bubble opacity

    public BackgroundSettingsDialogViewModel(IBackgroundService bgService)
    {
        _bgService = bgService;

        var config = _bgService.GetConfig();
        ImageBase64 = config.Image;
        Brightness = config.Brightness;
        Opacity = config.Opacity;
        ChatOpacity = config.ChatOpacity;
    }

    [RelayCommand]
    private void Save()
    {
        _bgService.UpdateConfig(new BgConfig(ImageBase64, Brightness, Opacity, ChatOpacity));
    }

    [RelayCommand]
    private void Reset()
    {
        _bgService.ResetConfig();
        var config = _bgService.GetConfig();
        ImageBase64 = config.Image;
        Brightness = config.Brightness;
        Opacity = config.Opacity;
        ChatOpacity = config.ChatOpacity;
    }

    [RelayCommand]
    private async Task PickImageAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary
        };
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".gif");
        picker.FileTypeFilter.Add(".webp");

        // WinUI 3 requires associating with a window handle
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            using var stream = await file.OpenReadAsync();
            var bytes = new byte[stream.Size];
            using var ms = new MemoryStream(bytes);
            await stream.AsStreamForRead().CopyToAsync(ms);
            ImageBase64 = Convert.ToBase64String(bytes);
        }
    }
}
