using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLAHStudio.App.ViewModels;
using TLAHStudio.Core.Services;
using Windows.Storage.Pickers;

namespace TLAHStudio.App.Views;

public sealed partial class SidebarPage : UserControl
{
    private const string SidebarStateKey = "tlah-sidebar-state";
    private SidebarViewModel? _vm;
    private bool _loaded;
    private bool _syncingSelection;
    private bool _isCollapsed;
    private bool _responsiveCompact;
    private bool _collapsedBeforeResponsive;

    public SidebarPage()
    {
        App.Log("SidebarPage ctor entered.");
        _isCollapsed = string.Equals(
            LocalStore.Get(SidebarStateKey),
            "collapsed",
            StringComparison.OrdinalIgnoreCase);
        InitializeComponent();
        App.Log("SidebarPage XAML initialized.");
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object s, RoutedEventArgs e)
    {
        if (_loaded) return; _loaded = true;
        var w = App.MainWindow as MainWindow; if (w == null) return;
        _vm = w.SidebarVM;
        DataContext = _vm;
        ChatListView.ItemsSource = _vm.SidebarItems;
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SidebarViewModel.SelectedChat))
                DispatcherQueue.TryEnqueue(SyncSelectedChat);
            if (args.PropertyName == nameof(SidebarViewModel.LastDeletedChat))
                DispatcherQueue.TryEnqueue(UpdateUndoPanel);
            if (args.PropertyName is nameof(SidebarViewModel.HasVisibleChats)
                or nameof(SidebarViewModel.SearchText)
                or nameof(SidebarViewModel.ShowArchived))
                DispatcherQueue.TryEnqueue(UpdateSections);
        };
        w.UiDensityService.DensityChanged += (_, _) => DispatcherQueue.TryEnqueue(() => ApplyDensity(w.UiDensityService));
        await _vm.LoadChatsAsync();
        SyncSelectedChat();
        UpdateUndoPanel();
        UpdateSections();
        ApplyDensity(w.UiDensityService);
        ApplyCollapsedState();
    }

    private async void NewChat_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        Play(InteractionSound.Navigate);
        await _vm.CreateChatAsync();
    }

    private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _syncingSelection)
            return;

        if (sender is not ListView list)
            return;

        var chat = list.SelectedItem is SidebarEntry { Chat: { } selected }
            ? selected
            : null;
        if (chat == null)
        {
            list.SelectedItem = null;
            return;
        }

        _syncingSelection = true;
        list.SelectedItem = _vm.SidebarItems.FirstOrDefault(entry => entry.Chat?.Id == chat.Id);
        _syncingSelection = false;

        _vm.SelectedChat = chat;
        Play(InteractionSound.Navigate);
    }

    private async void DeleteChat_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || GetChatFromSender(sender) is not { } chat)
            return;

        if (App.MainWindow is not MainWindow w)
            return;

        var confirmed = false;
        ContentDialog? dialog = null;
        var textPrimary = DialogBrush(w, "TextPrimaryBrush");
        var textSecondary = DialogBrush(w, "TextSecondaryBrush");
        var surfaceElevated = DialogBrush(w, "SurfaceElevatedBrush");
        var border = DialogBrush(w, "BorderSubtleBrush");
        var dialogSurface = DialogBrush(w, "DialogSurfaceBrush");

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 120,
            Padding = new Thickness(16, 9, 16, 9),
            Background = surfaceElevated,
            Foreground = textPrimary,
            BorderBrush = border,
            CornerRadius = new CornerRadius(8)
        };
        cancelButton.Click += (_, _) => dialog?.Hide();

        var deleteButton = new Button
        {
            Content = "Delete",
            MinWidth = 120,
            Padding = new Thickness(16, 9, 16, 9),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xDC, 0x26, 0x26)),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8)
        };
        deleteButton.Click += (_, _) =>
        {
            confirmed = true;
            dialog?.Hide();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(deleteButton);

        var content = new StackPanel
        {
            Width = 420,
            Spacing = 16
        };
        content.Children.Add(new TextBlock
        {
            Text = "Delete Chat",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = textPrimary
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Delete \"{chat.Title}\"? This action cannot be undone.",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = textSecondary
        });
        content.Children.Add(new Border
        {
            Height = 1,
            Background = border,
            Margin = new Thickness(0, 4, 0, 0)
        });
        content.Children.Add(buttons);

        dialog = new ContentDialog
        {
            Content = content,
            Background = dialogSurface,
            Foreground = textPrimary,
            XamlRoot = w.Content.XamlRoot,
            RequestedTheme = CurrentTheme(w)
        };
        ApplyDialogChrome(dialog, w);

        await w.TryShowDialogAsync(dialog);
        if (!confirmed)
            return;

        await _vm.DeleteChatAsync(chat.Id);
        Play(InteractionSound.Delete);
        SyncSelectedChat();
        UpdateUndoPanel();
        UpdateSections();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm == null) return;
        _vm.SearchText = SearchBox.Text;
    }

    private void ArchivedToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.ShowArchived = ArchivedToggle.IsChecked == true;
        Play(InteractionSound.Toggle);
    }

    private async void PinChat_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || GetChatFromSender(sender) is not { } chat)
            return;
        await _vm.TogglePinnedAsync(chat.Id);
        Play(InteractionSound.Complete);
        SyncSelectedChat();
        UpdateSections();
    }

    private async void ArchiveChat_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || GetChatFromSender(sender) is not { } chat)
            return;
        await _vm.ToggleArchivedAsync(chat.Id);
        Play(InteractionSound.Complete);
        SyncSelectedChat();
        UpdateSections();
    }

    private async void RenameChat_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || GetChatFromSender(sender) is not { } chat || App.MainWindow is not MainWindow w)
            return;

        var titleBox = new TextBox
        {
            Text = chat.Title,
            MinWidth = 360,
            Background = DialogBrush(w, "SurfaceElevatedBrush"),
            Foreground = DialogBrush(w, "TextPrimaryBrush"),
            BorderBrush = DialogBrush(w, "BorderSubtleBrush"),
            CornerRadius = new CornerRadius(8)
        };
        var dialog = new ContentDialog
        {
            Title = "Rename Chat",
            Content = titleBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = w.Content.XamlRoot,
            RequestedTheme = CurrentTheme(w)
        };
        ApplyDialogChrome(dialog, w);
        var result = await w.TryShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            await _vm.RenameChatAsync(chat.Id, titleBox.Text);
            Play(InteractionSound.Complete);
        }
    }

    private async void ExportChat_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || GetChatFromSender(sender) is not { } chat || App.MainWindow is not MainWindow w)
            return;

        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(w));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = SafeFileName($"{chat.Title}-{DateTime.Now:yyyyMMdd-HHmmss}");
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        if (file == null)
            return;

        var json = await _vm.ExportChatJsonAsync(chat.Id);
        await Windows.Storage.FileIO.WriteTextAsync(file, json);
        Play(InteractionSound.Complete);
    }

    private async void UndoDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.RestoreLastDeletedAsync();
        Play(InteractionSound.Complete);
        UpdateUndoPanel();
        UpdateSections();
        SyncSelectedChat();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w)
            return;

        Play(InteractionSound.Navigate);
        await w.SettingsVM.LoadAsync();
        var dlg = new SettingsContentDialog
        {
            DataContext = w.SettingsVM,
            RequestedTheme = CurrentTheme(w),
            XamlRoot = w.Content.XamlRoot
        };
        await w.TryShowDialogAsync(dlg);
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w)
            return;

        Play(InteractionSound.Navigate);
        var dlg = new AboutReleaseDialog
        {
            RequestedTheme = CurrentTheme(w),
            XamlRoot = w.Content.XamlRoot
        };
        await dlg.LoadAsync(w.AppReleaseService);
        await w.TryShowDialogAsync(dlg);
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w)
            return;

        Play(InteractionSound.Navigate);
        var result = await w.UpdateNotificationVM.AppUpdateService.CheckForUpdateAsync();
        if (result != null)
        {
            w.UpdateNotificationVM.ShowUpdate(result);
            var updateDialog = new UpdateNotificationDialog
            {
                RequestedTheme = CurrentTheme(w),
                XamlRoot = w.Content.XamlRoot
            };
            updateDialog.SetData(w.UpdateNotificationVM);
            await w.TryShowDialogAsync(updateDialog);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "TLAH Studio is up to date",
            Content = $"Current version: {w.UpdateNotificationVM.AppUpdateService.CurrentVersion}",
            CloseButtonText = "Close",
            RequestedTheme = CurrentTheme(w),
            XamlRoot = w.Content.XamlRoot
        };
        ApplyDialogChrome(dialog, w);
        await w.TryShowDialogAsync(dialog);
        Play(InteractionSound.Complete);
    }

    private async void Privacy_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w)
            return;

        Play(InteractionSound.Navigate);
        var dlg = new PrivacyDataDialog
        {
            DataContext = w.PrivacyDataVM,
            RequestedTheme = CurrentTheme(w),
            XamlRoot = w.Content.XamlRoot
        };
        ApplyDialogChrome(dlg, w);
        await w.TryShowDialogAsync(dlg);
    }

    private async void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w)
            return;

        Play(InteractionSound.Navigate);
        await w.TeamWorkspaceVM.LoadAsync();
        var dlg = new TeamWorkspaceDialog
        {
            DataContext = w.TeamWorkspaceVM,
            RequestedTheme = CurrentTheme(w),
            XamlRoot = w.Content.XamlRoot
        };
        if (w.Content is FrameworkElement workspaceHost)
            dlg.PrepareForHost(workspaceHost.ActualWidth);
        ApplyDialogChrome(dlg, w);
        await w.TryShowDialogAsync(dlg);
        await w.SidebarVM.LoadChatsAsync();
    }

    private async void Tools_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w)
            return;

        Play(InteractionSound.Navigate);
        await w.ToolPlatformVM.LoadAsync();
        var dlg = new ToolPlatformDialog
        {
            DataContext = w.ToolPlatformVM,
            RequestedTheme = CurrentTheme(w),
            XamlRoot = w.Content.XamlRoot
        };
        if (w.Content is FrameworkElement toolHost)
            dlg.PrepareForHost(toolHost.ActualWidth);
        ApplyDialogChrome(dlg, w);
        await w.TryShowDialogAsync(dlg);
    }

    private void Density_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow w)
            return;

        Play(InteractionSound.Toggle);
        w.UiDensityService.ToggleDensity();
        ApplyDensity(w.UiDensityService);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        _isCollapsed = !_isCollapsed;
        Play(InteractionSound.Toggle);
        if (!_responsiveCompact)
            LocalStore.Set(SidebarStateKey, _isCollapsed ? "collapsed" : "expanded");
        ApplyCollapsedState();
    }

    public void SetResponsiveCompact(bool compact)
    {
        if (_responsiveCompact == compact)
            return;

        _responsiveCompact = compact;
        if (compact)
        {
            _collapsedBeforeResponsive = _isCollapsed;
            _isCollapsed = true;
        }
        else
        {
            _isCollapsed = _collapsedBeforeResponsive;
        }

        ApplyCollapsedState();
    }

    public void FocusSearch()
    {
        if (_isCollapsed)
        {
            _isCollapsed = false;
            if (!_responsiveCompact)
                LocalStore.Set(SidebarStateKey, "expanded");
            ApplyCollapsedState();
        }

        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void UpdateUndoPanel()
    {
        if (_isCollapsed || _vm?.LastDeletedChat == null)
        {
            UndoDeletePanel.Visibility = Visibility.Collapsed;
            return;
        }

        UndoDeleteText.Text = $"Deleted \"{_vm.LastDeletedChat.Title}\"";
        UndoDeletePanel.Visibility = Visibility.Visible;
    }

    private void UpdateSections()
    {
        if (_vm == null)
            return;

        ArchivedToggle.IsChecked = _vm.ShowArchived;

        if (!string.IsNullOrWhiteSpace(_vm.SearchText))
        {
            VirtualizedEmptyListTitle.Text = "No matching chats";
            VirtualizedEmptyListBody.Text = "Clear the search field or start a new chat.";
        }
        else if (_vm.ShowArchived)
        {
            VirtualizedEmptyListTitle.Text = "No archived chats";
            VirtualizedEmptyListBody.Text = "Archived conversations will appear here.";
        }
        else
        {
            VirtualizedEmptyListTitle.Text = "No chats yet";
            VirtualizedEmptyListBody.Text = "Create a chat to start a conversation.";
        }
        VirtualizedEmptyListState.Visibility = _isCollapsed || _vm.HasVisibleChats
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SyncSelectedChat()
    {
        if (_vm == null)
            return;

        _syncingSelection = true;
        ChatListView.SelectedItem = _vm.SidebarItems.FirstOrDefault(entry => entry.Chat?.Id == _vm.SelectedChat?.Id);
        _syncingSelection = false;
    }

    private void ApplyDensity(IUiDensityService densityService)
    {
        var compact = densityService.CurrentDensity == UiDensity.Compact;
        SidebarRoot.Width = _isCollapsed ? 92 : compact ? 288 : 312;
        DensityButtonText.Text = compact ? "Compact" : "Comfort";
    }

    private void ApplyCollapsedState()
    {
        var visibility = _isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        BrandTextPanel.Visibility = visibility;
        NewChatText.Visibility = visibility;
        SearchToolsGrid.Visibility = visibility;
        ExpandedFooter.Visibility = visibility;
        CompactFooter.Visibility = _isCollapsed ? Visibility.Visible : Visibility.Collapsed;
        SidebarHeader.Padding = _isCollapsed
            ? new Thickness(8, 14, 8, 12)
            : new Thickness(20, 18, 18, 16);
        BrandHeaderGrid.ColumnSpacing = _isCollapsed ? 4 : 10;

        CollapseIcon.Glyph = _isCollapsed ? "\uE72C" : "\uE72D";
        ToolTipService.SetToolTip(CollapseButton, _isCollapsed ? "Expand sidebar" : "Collapse sidebar");
        AutomationProperties.SetName(CollapseButton, _isCollapsed ? "Expand sidebar" : "Collapse sidebar");

        NewChatButton.HorizontalContentAlignment = _isCollapsed
            ? HorizontalAlignment.Center
            : HorizontalAlignment.Left;
        NewChatButton.Padding = _isCollapsed
            ? new Thickness(0)
            : new Thickness(14, 11, 14, 11);
        NewChatButton.MinHeight = _isCollapsed ? 44 : 0;

        var selector = (SidebarEntryTemplateSelector)Resources["SidebarEntryTemplateSelector"];
        selector.IsCompact = _isCollapsed;
        ChatListView.ItemTemplateSelector = null;
        ChatListView.ItemTemplateSelector = selector;

        if (App.MainWindow is MainWindow window)
            ApplyDensity(window.UiDensityService);
        UpdateUndoPanel();
        UpdateSections();
    }

    private static ChatSummaryDto? GetChatFromSender(object sender) =>
        sender switch
        {
            FrameworkElement { Tag: ChatSummaryDto tagged } => tagged,
            FrameworkElement { DataContext: ChatSummaryDto chat } => chat,
            _ => null
        };

    private static string SafeFileName(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            value = value.Replace(ch, '-');
        return value;
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
        var border = ColorBrush(isLight, 0xFF, 0xD3, 0xDD, 0xE9, 0x66, 0x6F, 0x7D, 0x91);

        dialog.Background = background;
        dialog.Resources["ContentDialogBackground"] = background;
        dialog.Resources["ContentDialogForeground"] = foreground;
        dialog.Resources["ContentDialogBorderBrush"] = border;
        dialog.Resources["ContentDialogMaxWidth"] = 1280d;
        dialog.Resources["ContentDialogMaxHeight"] = 1000d;
        dialog.Resources["TextFillColorPrimaryBrush"] = foreground;
        dialog.Resources["TextFillColorSecondaryBrush"] = secondary;
        dialog.Resources["SolidBackgroundFillColorBaseBrush"] = background;
        dialog.Resources["LayerFillColorDefaultBrush"] = background;
        dialog.Resources["LayerFillColorAltBrush"] = background;
    }

    private static SolidColorBrush DialogBrush(MainWindow window, string key)
    {
        var light = CurrentTheme(window) == Microsoft.UI.Xaml.ElementTheme.Light;
        return key switch
        {
            "DialogSurfaceBrush" => ColorBrush(light, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x14, 0x1C, 0x28),
            "SurfaceElevatedBrush" => ColorBrush(light, 0xFF, 0xF7, 0xFA, 0xFD, 0xFF, 0x22, 0x2D, 0x3C),
            "BorderSubtleBrush" => ColorBrush(light, 0xFF, 0xD3, 0xDD, 0xE9, 0x66, 0x6F, 0x7D, 0x91),
            "TextSecondaryBrush" => ColorBrush(light, 0xFF, 0x58, 0x67, 0x79, 0xFF, 0xDC, 0xE4, 0xEE),
            "TextPrimaryBrush" => ColorBrush(light, 0xFF, 0x16, 0x1D, 0x28, 0xFF, 0xFF, 0xFF, 0xFF),
            _ => ColorBrush(light, 0xFF, 0x16, 0x1D, 0x28, 0xFF, 0xFF, 0xFF, 0xFF)
        };
    }

    private static SolidColorBrush ColorBrush(
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
        return new SolidColorBrush(color);
    }

    private static void Play(InteractionSound sound)
    {
        if (App.MainWindow is MainWindow w)
            w.SoundService.Play(sound);
    }
}
