using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.Json;
using TLAHStudio.App.ViewModels;
using TLAHStudio.App.Views;
using TLAHStudio.Core.Services;
using Windows.Storage.Streams;
using Windows.System;

namespace TLAHStudio.App;

public sealed partial class MainWindow : Window
{
    private bool _firstRunSetupChecked;
    private bool _isNarrowLayout;
    private readonly SemaphoreSlim _contentDialogGate = new(1, 1);

    public MainViewModel ViewModel { get; }
    public SidebarViewModel SidebarVM { get; }
    public ChatPageViewModel ChatVM { get; }
    public DebugPanelViewModel DebugVM { get; }
    public SettingsDialogViewModel SettingsVM { get; }
    public BackgroundSettingsDialogViewModel BgSettingsVM { get; }
    public AgentFileDialogViewModel AgentFileVM { get; }
    public PrivacyDataViewModel PrivacyDataVM { get; }
    public TeamWorkspaceViewModel TeamWorkspaceVM { get; }
    public ToolPlatformViewModel ToolPlatformVM { get; }
    public UpdateNotificationViewModel UpdateNotificationVM { get; }
    public IThemeService ThemeService { get; }
    public IBackgroundService BackgroundService { get; }
    public IUiDensityService UiDensityService { get; }
    public IAppReleaseService AppReleaseService { get; }
    public ISandboxCommandService SandboxCommandService { get; }

    public async Task<ContentDialogResult?> TryShowDialogAsync(ContentDialog dialog, bool waitForTurn = false)
    {
        if (waitForTurn)
        {
            await _contentDialogGate.WaitAsync();
        }
        else if (!_contentDialogGate.Wait(0))
        {
            App.Log($"DIALOG SUPPRESSED: {dialog.GetType().Name}; another dialog is already open.");
            return null;
        }

        try
        {
            return await dialog.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80000019)
        {
            App.Log($"DIALOG CONFLICT: {dialog.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            _contentDialogGate.Release();
        }
    }

    public MainWindow(
        MainViewModel mvm, SidebarViewModel svm, ChatPageViewModel cvm,
        DebugPanelViewModel dvm, SettingsDialogViewModel sv, BackgroundSettingsDialogViewModel bv,
        AgentFileDialogViewModel av, PrivacyDataViewModel pv, TeamWorkspaceViewModel twv, ToolPlatformViewModel tpv, UpdateNotificationViewModel uv, IThemeService ts,
        IBackgroundService bg, IUiDensityService density, IAppReleaseService release, ISandboxCommandService sandbox)
    {
        ViewModel = mvm; SidebarVM = svm; ChatVM = cvm; DebugVM = dvm;
        SettingsVM = sv; BgSettingsVM = bv; AgentFileVM = av; PrivacyDataVM = pv; TeamWorkspaceVM = twv; ToolPlatformVM = tpv;
        UpdateNotificationVM = uv; ThemeService = ts; BackgroundService = bg;
        UiDensityService = density; AppReleaseService = release; SandboxCommandService = sandbox;

        this.InitializeComponent();

        DebugPanelView.Bind(DebugVM);
        ChatPageView.Bind(ChatVM, DebugVM, BackgroundService, UiDensityService, SandboxCommandService);
        ChatVM.AgentApprovalRequested += OnAgentApprovalRequested;
        DebugVM.TurnReplayed += async (_, turnId) =>
        {
            if (ChatVM.CurrentChat != null)
                await ChatVM.LoadChatAsync(ChatVM.CurrentChat.Id);
            await DebugVM.OpenDebugAsync(turnId);
        };

        try { SystemBackdrop = new MicaBackdrop(); } catch { }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var wid = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(wid);
        SetAppIcon(appWindow);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        ConfigureTitleBarButtons(appWindow);
        RootGrid.ActualThemeChanged += (_, _) => ConfigureTitleBarButtons(appWindow);

        BackgroundService.ConfigChanged += (_, config) =>
            DispatcherQueue.TryEnqueue(() => _ = ApplyBackgroundConfigAsync(config));
        _ = ApplyBackgroundConfigAsync(BackgroundService.GetConfig());

        RootGrid.Loaded += OnRootGridLoaded;
        RootGrid.SizeChanged += OnRootGridSizeChanged;
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnGlobalKeyDown), true);
        Activated += OnFirst;
    }

    private async void OnAgentApprovalRequested(object? sender, AgentApprovalRequest request)
    {
        try
        {
            var details = request.ArgumentsJson;
            try
            {
                using var document = JsonDocument.Parse(request.ArgumentsJson);
                details = JsonSerializer.Serialize(
                    document.RootElement,
                    new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
            }

            var content = new StackPanel { Spacing = 12, MaxWidth = 620 };
            content.Children.Add(new TextBlock
            {
                Text = $"The agent wants to use {request.ToolName}. Review the exact arguments before allowing it.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"]
            });
            var argumentsText = new TextBlock
            {
                Text = details,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"]
            };
            var argumentsViewer = new ScrollViewer
            {
                Content = argumentsText,
                MinHeight = 160,
                MaxHeight = 320,
                Padding = new Thickness(12),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var argumentsBox = new Border
            {
                Child = argumentsViewer,
                CornerRadius = new CornerRadius(8),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["InputBackgroundBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderSubtleBrush"],
                BorderThickness = new Thickness(1)
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                argumentsBox,
                "Agent tool arguments preview");
            content.Children.Add(argumentsBox);

            var dialog = new ContentDialog
            {
                Title = "Approve Agent Tool",
                Content = content,
                PrimaryButtonText = "Allow once",
                SecondaryButtonText = "Allow for this project",
                CloseButtonText = "Always deny",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            if (Content is FrameworkElement root)
                dialog.RequestedTheme = root.ActualTheme;

            var result = await TryShowDialogAsync(dialog, waitForTurn: true);
            request.Completion.TrySetResult(result switch
            {
                ContentDialogResult.Primary => AgentApprovalChoice.AllowOnce,
                ContentDialogResult.Secondary => AgentApprovalChoice.AllowForProject,
                _ => AgentApprovalChoice.AlwaysDeny
            });
        }
        catch (Exception ex)
        {
            App.Log($"AGENT APPROVAL DIALOG FAILED: {ex}");
            request.Completion.TrySetResult(AgentApprovalChoice.AlwaysDeny);
        }
    }

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 900;
        if (_isNarrowLayout == isNarrow)
            return;

        _isNarrowLayout = isNarrow;
        SidebarView.SetResponsiveCompact(isNarrow);
    }

    private async void OnGlobalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsKeyDown(VirtualKey.Control) ||
                   IsKeyDown(VirtualKey.LeftControl) ||
                   IsKeyDown(VirtualKey.RightControl);

        if (ctrl && e.Key == VirtualKey.N)
        {
            e.Handled = true;
            await SidebarVM.CreateChatAsync();
            MessageInputView.FocusMessageInput();
            return;
        }

        if (ctrl && e.Key == VirtualKey.F)
        {
            e.Handled = true;
            SidebarView.FocusSearch();
            return;
        }

        if (ctrl && e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            MessageInputView.SendFromShortcut();
            return;
        }

        if (ctrl && e.Key == VirtualKey.I)
        {
            e.Handled = true;
            await OpenLatestInspectorAsync();
            return;
        }

        if (ctrl && e.Key == VirtualKey.T)
        {
            e.Handled = true;
            ThemeService.ToggleTheme();
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (ChatVM.IsSending)
            {
                e.Handled = true;
                ChatVM.StopSendingCommand.Execute(null);
                return;
            }

            if (DebugVM.IsOpen)
            {
                e.Handled = true;
                DebugVM.CloseDebugCommand.Execute(null);
            }
        }
    }

    private async Task OpenLatestInspectorAsync()
    {
        var turnId = ChatVM.Messages.LastOrDefault(m => m.TurnId != null)?.TurnId;
        if (turnId != null)
            await DebugVM.OpenDebugAsync(turnId.Value);
    }

    public void FocusMessageInput() => MessageInputView.FocusMessageInput();

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private async void OnFirst(object s, WindowActivatedEventArgs a)
    {
        Activated -= OnFirst;
        await SidebarVM.LoadChatsAsync();
    }

    private async void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootGridLoaded;
        await ShowFirstRunSetupIfNeededAsync();
    }

    private async Task ShowFirstRunSetupIfNeededAsync()
    {
        if (_firstRunSetupChecked)
            return;
        _firstRunSetupChecked = true;

        try
        {
            await SettingsVM.LoadAsync();
            if (!string.IsNullOrWhiteSpace(SettingsVM.ApiKey))
                return;

            var xamlRoot = await WaitForXamlRootAsync();
            if (xamlRoot == null)
            {
                App.Log("FIRST RUN SETUP SKIPPED: XamlRoot was not ready.");
                return;
            }

            var dialog = new FirstRunSetupDialog
            {
                DataContext = SettingsVM,
                XamlRoot = xamlRoot
            };
            if (Content is FrameworkElement root)
                dialog.RequestedTheme = root.ActualTheme;

            await TryShowDialogAsync(dialog);
        }
        catch (Exception ex)
        {
            App.Log($"FIRST RUN SETUP FAILED: {ex}");
        }
    }

    private async Task<XamlRoot?> WaitForXamlRootAsync()
    {
        for (var i = 0; i < 30; i++)
        {
            if (RootGrid.XamlRoot != null)
                return RootGrid.XamlRoot;

            await Task.Delay(100);
        }

        return RootGrid.XamlRoot;
    }

    private void ConfigureTitleBarButtons(Microsoft.UI.Windowing.AppWindow appWindow)
    {
        if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
            return;

        var isLight = RootGrid.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Light;
        var titleBar = appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x18, 0x78, 0x86, 0x98);
        titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x28, 0x78, 0x86, 0x98);
        titleBar.ButtonForegroundColor = isLight
            ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x58, 0x67, 0x79)
            : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x9A, 0xA8, 0xBA);
        titleBar.ButtonInactiveForegroundColor = isLight
            ? Microsoft.UI.ColorHelper.FromArgb(0x99, 0x58, 0x67, 0x79)
            : Microsoft.UI.ColorHelper.FromArgb(0x99, 0x9A, 0xA8, 0xBA);
    }

    private static void SetAppIcon(Microsoft.UI.Windowing.AppWindow appWindow)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            appWindow.SetIcon(iconPath);
    }

    private async Task ApplyBackgroundConfigAsync(BgConfig config)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(config.Image))
            {
                BackgroundImage.Source = null;
                BackgroundImage.Opacity = 0;
            }
            else
            {
                BackgroundImage.Source = await LoadBitmapAsync(config.Image);
                BackgroundImage.Opacity = Clamp(config.Opacity / 100.0, 0, 1);
            }

            var brightness = Clamp(config.Brightness, 0, 200);
            if (brightness < 100)
            {
                BrightnessOverlay.Fill = new SolidColorBrush(Microsoft.UI.Colors.Black);
                BrightnessOverlay.Opacity = (100 - brightness) / 100.0;
            }
            else
            {
                BrightnessOverlay.Fill = new SolidColorBrush(Microsoft.UI.Colors.White);
                BrightnessOverlay.Opacity = (brightness - 100) / 100.0;
            }
        }
        catch (Exception ex)
        {
            App.Log($"BACKGROUND APPLY FAILED: {ex}");
            BackgroundImage.Source = null;
            BackgroundImage.Opacity = 0;
            BrightnessOverlay.Opacity = 0;
        }
    }

    private static async Task<BitmapImage?> LoadBitmapAsync(string base64)
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

    private static double Clamp(double value, double min, double max) =>
        Math.Min(max, Math.Max(min, value));
}
