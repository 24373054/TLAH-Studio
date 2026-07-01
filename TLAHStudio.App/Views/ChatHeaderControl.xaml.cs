using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TLAHStudio.App.ViewModels;

namespace TLAHStudio.App.Views;

public sealed partial class ChatHeaderControl : UserControl
{
    private SettingsDialogViewModel? _svm;
    private bool _loaded;

    public ChatHeaderControl()
    {
        App.Log("ChatHeaderControl ctor entered.");
        InitializeComponent();
        App.Log("ChatHeaderControl XAML initialized.");
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        if (App.MainWindow is not MainWindow w) return;
        UpdateTitle(w.ChatVM.CurrentChat?.Title);
        w.ChatVM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatPageViewModel.CurrentChat))
                DispatcherQueue.TryEnqueue(() => UpdateTitle(w.ChatVM.CurrentChat?.Title));
            if (args.PropertyName == nameof(ChatPageViewModel.IsAgentActivityPanelOpen))
                DispatcherQueue.TryEnqueue(UpdateActivityButton);
        };
        UpdateActivityButton();
    }

    private void UpdateTitle(string? title)
    {
        TitleBlock.Text = string.IsNullOrWhiteSpace(title) ? "Select a chat" : title;
    }

    private async void Title_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w || w.ChatVM.CurrentChat == null) return;
        var titleBox = new TextBox
        {
            Text = w.ChatVM.CurrentChat.Title,
            PlaceholderText = "Chat title",
            MinWidth = 420,
            Background = Brush("InputBackgroundBrush"),
            Foreground = Brush("TextPrimaryBrush"),
            BorderBrush = Brush("BorderSubtleBrush")
        };

        var dlg = new ContentDialog
        {
            Title = "Edit title",
            Content = titleBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            RequestedTheme = CurrentTheme(w),
            XamlRoot = w.Content.XamlRoot
        };
        ApplyDialogChrome(dlg, w);

        if (await w.TryShowDialogAsync(dlg) == ContentDialogResult.Primary)
        {
            await w.ChatVM.UpdateTitleAsync(titleBox.Text);
            await w.SidebarVM.LoadChatsAsync();
            Play(InteractionSound.Complete);
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow w)
        {
            w.ThemeService.ToggleTheme();
            w.SoundService.Play(InteractionSound.Toggle);
        }
    }

    private async void SystemPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w || w.ChatVM.CurrentChat == null) return;
        Play(InteractionSound.Navigate);

        var promptBox = new TextBox
        {
            Text = w.ChatVM.CurrentChat.SystemPrompt,
            PlaceholderText = "Leave empty to inherit the global system prompt.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 560,
            MinHeight = 260,
            Background = Brush("InputBackgroundBrush"),
            Foreground = Brush("TextPrimaryBrush"),
            BorderBrush = Brush("BorderSubtleBrush")
        };
        promptBox.RequestedTheme = CurrentTheme(w);

        var dlg = new ContentDialog
        {
            Title = "System prompt",
            Content = promptBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            RequestedTheme = CurrentTheme(w),
            XamlRoot = w.Content.XamlRoot
        };
        ApplyDialogChrome(dlg, w);

        if (await w.TryShowDialogAsync(dlg) == ContentDialogResult.Primary)
        {
            await w.ChatVM.UpdateSystemPromptAsync(promptBox.Text);
            Play(InteractionSound.Complete);
        }
    }

    private async void Background_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w) return;
        Play(InteractionSound.Navigate);
        var dlg = new BackgroundSettingsDialog
        {
            DataContext = w.BgSettingsVM,
            RequestedTheme = CurrentTheme(w),
            XamlRoot = w.Content.XamlRoot
        };
        await w.TryShowDialogAsync(dlg);
    }

    private void Activity_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w) return;
        w.ToggleAgentActivityPanel();
        Play(InteractionSound.Toggle);
        UpdateActivityButton();
    }

    private async void Agent_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w) return;
        Play(InteractionSound.Navigate);
        var dlg = new AgentFileDialog
        {
            DataContext = w.AgentFileVM,
            RequestedTheme = CurrentTheme(w),
            XamlRoot = w.Content.XamlRoot
        };
        await w.TryShowDialogAsync(dlg);
    }

    private void UpdateActivityButton()
    {
        var isOpen = App.MainWindow is MainWindow window && window.ChatVM.IsAgentActivityPanelOpen;
        ActivityButton.Background = isOpen ? Brush("AccentStrongBrush") : Brush("SurfaceElevatedBrush");
        ActivityButton.Foreground = isOpen ? Brush("AccentTextBrush") : Brush("TextSecondaryBrush");
        ActivityButton.BorderBrush = isOpen ? Brush("AccentStrongBrush") : Brush("BorderSubtleBrush");
    }

    private async void Settings_Click(object s, RoutedEventArgs e)
    {
        var w = App.MainWindow as MainWindow;
        if (w == null) return;
        w.SoundService.Play(InteractionSound.Navigate);
        _svm = w.SettingsVM;
        await _svm.LoadAsync();
        var dlg = new SettingsContentDialog
        {
            DataContext = _svm,
            RequestedTheme = CurrentTheme(w),
            XamlRoot = w.Content.XamlRoot
        };
        await w.TryShowDialogAsync(dlg);
    }

    private static Microsoft.UI.Xaml.ElementTheme CurrentTheme(MainWindow window) =>
        window.Content is FrameworkElement root
            ? root.ActualTheme
            : Microsoft.UI.Xaml.ElementTheme.Default;

    private static void ApplyDialogChrome(ContentDialog dialog, MainWindow window)
    {
        var isLight = CurrentTheme(window) == Microsoft.UI.Xaml.ElementTheme.Light;
        var background = ColorBrush(isLight, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x14, 0x1C, 0x28);
        var foreground = ColorBrush(isLight, 0xFF, 0x16, 0x1D, 0x28, 0xFF, 0xFF, 0xFF, 0xFF);
        var secondary = ColorBrush(isLight, 0xFF, 0x58, 0x67, 0x79, 0xFF, 0xDC, 0xE4, 0xEE);
        var input = ColorBrush(isLight, 0xFF, 0xFF, 0xFF, 0xFF, 0xF0, 0x0B, 0x11, 0x19);
        var border = ColorBrush(isLight, 0xFF, 0xD3, 0xDD, 0xE9, 0x66, 0x6F, 0x7D, 0x91);

        dialog.Background = background;
        dialog.Resources["ContentDialogBackground"] = background;
        dialog.Resources["ContentDialogForeground"] = foreground;
        dialog.Resources["TextFillColorPrimaryBrush"] = foreground;
        dialog.Resources["TextFillColorSecondaryBrush"] = secondary;
        dialog.Resources["TextControlBackground"] = input;
        dialog.Resources["TextControlBackgroundPointerOver"] = input;
        dialog.Resources["TextControlBackgroundFocused"] = input;
        dialog.Resources["TextControlForeground"] = foreground;
        dialog.Resources["TextControlForegroundFocused"] = foreground;
        dialog.Resources["TextControlPlaceholderForeground"] = secondary;
        dialog.Resources["TextControlBorderBrush"] = border;
        dialog.Resources["TextControlBorderBrushPointerOver"] = border;
        dialog.Resources["TextControlBorderBrushFocused"] = border;
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush ColorBrush(
        bool light,
        byte lightA,
        byte lightR,
        byte lightG,
        byte lightB,
        byte darkA,
        byte darkR,
        byte darkG,
        byte darkB)
    {
        var color = light
            ? Microsoft.UI.ColorHelper.FromArgb(lightA, lightR, lightG, lightB)
            : Microsoft.UI.ColorHelper.FromArgb(darkA, darkR, darkG, darkB);
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
    }

    private static Microsoft.UI.Xaml.Media.Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Microsoft.UI.Xaml.Media.Brush brush
            ? brush
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);

    private static void Play(InteractionSound sound)
    {
        if (App.MainWindow is MainWindow w)
            w.SoundService.Play(sound);
    }
}
