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
    public IInteractionSoundService SoundService { get; }
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
        IBackgroundService bg, IUiDensityService density, IInteractionSoundService sound, IAppReleaseService release, ISandboxCommandService sandbox)
    {
        ViewModel = mvm; SidebarVM = svm; ChatVM = cvm; DebugVM = dvm;
        SettingsVM = sv; BgSettingsVM = bv; AgentFileVM = av; PrivacyDataVM = pv; TeamWorkspaceVM = twv; ToolPlatformVM = tpv;
        UpdateNotificationVM = uv; ThemeService = ts; BackgroundService = bg;
        UiDensityService = density; SoundService = sound; AppReleaseService = release; SandboxCommandService = sandbox;

        this.InitializeComponent();

        DebugPanelView.Bind(DebugVM);
        ChatPageView.Bind(ChatVM, DebugVM, BackgroundService, UiDensityService, SandboxCommandService, SoundService);
        AgentActivityPanelView.Bind(ChatVM, UiDensityService, SoundService);
        ChatVM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatPageViewModel.IsAgentActivityPanelOpen))
                DispatcherQueue.TryEnqueue(UpdateAgentActivityPanelLayout);
        };
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
        UpdateAgentActivityPanelLayout();
    }

    private async void OnAgentApprovalRequested(object? sender, AgentApprovalRequest request)
    {
        try
        {
            SoundService.Play(InteractionSound.Approval);
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

            var content = new StackPanel { Spacing = 12, MaxWidth = 680 };
            content.Children.Add(new TextBlock
            {
                Text = $"The agent wants to use {request.ToolName}. Review what it may read, change, or access before applying a permission decision.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"]
            });

            var summaryGrid = BuildApprovalSummaryGrid(request);
            content.Children.Add(summaryGrid);

            var decisionBox = new ComboBox
            {
                ItemsSource = new[]
                {
                    "Allow once",
                    "Allow for this project",
                    "Allow globally",
                    "Always deny"
                },
                SelectedIndex = 0,
                MinWidth = 260
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                decisionBox,
                "Agent tool permission decision");
            content.Children.Add(new TextBlock
            {
                Text = "Permission decision",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"]
            });
            content.Children.Add(decisionBox);

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
                PrimaryButtonText = "Apply decision",
                CloseButtonText = "Deny once",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            if (Content is FrameworkElement root)
                dialog.RequestedTheme = root.ActualTheme;

            var result = await TryShowDialogAsync(dialog, waitForTurn: true);
            SoundService.Play(result is ContentDialogResult.Primary or ContentDialogResult.Secondary
                ? InteractionSound.Complete
                : InteractionSound.Error);
            request.Completion.TrySetResult(result switch
            {
                ContentDialogResult.Primary => decisionBox.SelectedIndex switch
                {
                    1 => AgentApprovalChoice.AllowForProject,
                    2 => AgentApprovalChoice.AllowGlobally,
                    3 => AgentApprovalChoice.AlwaysDeny,
                    _ => AgentApprovalChoice.AllowOnce
                },
                _ => AgentApprovalChoice.DenyOnce
            });
        }
        catch (Exception ex)
        {
            App.Log($"AGENT APPROVAL DIALOG FAILED: {ex}");
            request.Completion.TrySetResult(AgentApprovalChoice.AlwaysDeny);
        }
    }

    private static Grid BuildApprovalSummaryGrid(AgentApprovalRequest request)
    {
        var grid = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 8,
            Padding = new Thickness(12),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["InputBackgroundBrush"]
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var preview = ParseApprovalPreview(request);
        AddApprovalTile(grid, 0, 0, "It may read", preview.Reads);
        AddApprovalTile(grid, 0, 1, "It may change", preview.Writes);
        AddApprovalTile(grid, 1, 0, "It may access", preview.Accesses);
        AddApprovalTile(grid, 1, 1, $"Risk: {request.SafetyLevel}", preview.Risk);
        return grid;
    }

    private static void AddApprovalTile(Grid grid, int row, int column, string title, string body)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"]
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(body) ? "None detected." : body,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextSecondaryBrush"]
        });
        var border = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BorderSubtleBrush"],
            BorderThickness = new Thickness(1),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["InputBackgroundBrush"]
        };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        grid.Children.Add(border);
    }

    private static ApprovalPreview ParseApprovalPreview(AgentApprovalRequest request)
    {
        var reads = new List<string>();
        var writes = new List<string>();
        var accesses = new List<string>();

        try
        {
            using var preview = JsonDocument.Parse(request.SafetyJson);
            CollectApprovalPreview(preview.RootElement, reads, writes, accesses);
        }
        catch
        {
        }

        var risk = request.SafetySummary;
        if (string.IsNullOrWhiteSpace(risk))
            risk = "Review the tool arguments before allowing this operation.";

        return new ApprovalPreview(
            LimitApprovalLines(reads),
            LimitApprovalLines(writes),
            LimitApprovalLines(accesses),
            risk);
    }

    private static void CollectApprovalPreview(
        JsonElement element,
        List<string> reads,
        List<string> writes,
        List<string> accesses)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string? operation = null;
            if (element.TryGetProperty("operation", out var op) &&
                op.ValueKind == JsonValueKind.String)
            {
                operation = op.GetString();
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    if (property.Name.Contains("path", StringComparison.OrdinalIgnoreCase))
                    {
                        if (operation is "create" or "replace" or "append" or "delete" or "write")
                            writes.Add(value);
                        else
                            reads.Add(value);
                    }
                    else if (property.Name.Contains("url", StringComparison.OrdinalIgnoreCase) ||
                             property.Name.Contains("domain", StringComparison.OrdinalIgnoreCase) ||
                             property.Name.Contains("endpoint", StringComparison.OrdinalIgnoreCase))
                    {
                        accesses.Add(value);
                    }
                }
                else
                {
                    CollectApprovalPreview(property.Value, reads, writes, accesses);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectApprovalPreview(item, reads, writes, accesses);
        }
    }

    private static string LimitApprovalLines(IReadOnlyList<string> values)
    {
        var unique = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        return unique.Length == 0
            ? string.Empty
            : string.Join(Environment.NewLine, unique);
    }

    private sealed record ApprovalPreview(
        string Reads,
        string Writes,
        string Accesses,
        string Risk);

    private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 900;
        if (_isNarrowLayout == isNarrow)
        {
            UpdateAgentActivityPanelLayout();
            return;
        }

        _isNarrowLayout = isNarrow;
        SidebarView.SetResponsiveCompact(isNarrow);
        UpdateAgentActivityPanelLayout();
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
            SoundService.Play(InteractionSound.Navigate);
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
            SoundService.Play(InteractionSound.Toggle);
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

    public void ToggleAgentActivityPanel(bool? open = null)
    {
        ChatVM.IsAgentActivityPanelOpen = open ?? !ChatVM.IsAgentActivityPanelOpen;
        UpdateAgentActivityPanelLayout();
    }

    private void UpdateAgentActivityPanelLayout()
    {
        if (AgentActivityColumn == null || AgentActivityPanelView == null)
            return;

        var availableWidth = WorkbenchGrid.ActualWidth;
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
            availableWidth = Math.Max(0, RootGrid.ActualWidth - SidebarView.ActualWidth);

        var canShow = ChatVM.IsAgentActivityPanelOpen && availableWidth >= 780;
        if (!canShow)
        {
            AgentActivityPanelView.Visibility = Visibility.Collapsed;
            AgentActivityColumn.Width = new GridLength(0);
            return;
        }

        var targetWidth = Math.Clamp(availableWidth * 0.30, 320, 420);
        if (availableWidth - targetWidth < 520)
            targetWidth = Math.Max(300, availableWidth - 520);

        if (targetWidth < 300)
        {
            AgentActivityPanelView.Visibility = Visibility.Collapsed;
            AgentActivityColumn.Width = new GridLength(0);
            return;
        }

        AgentActivityColumn.Width = new GridLength(targetWidth);
        AgentActivityPanelView.Visibility = Visibility.Visible;
    }

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
        SoundService.Play(InteractionSound.Launch);
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
