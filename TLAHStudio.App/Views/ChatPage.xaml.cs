using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Windows.UI;
using TLAHStudio.App.ViewModels;
using TLAHStudio.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace TLAHStudio.App.Views;

public sealed partial class ChatPage : UserControl
{
    private ChatPageViewModel? _vm;
    private DebugPanelViewModel? _debugVm;
    private IBackgroundService? _backgroundService;
    private IUiDensityService? _densityService;
    private bool _bound;
    private double _chatBubbleOpacity = 1;
    private int _lastMessageCount;
    private bool _isNarrow;

    public ChatPage()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RenderMessages();
        SizeChanged += OnChatSizeChanged;
        AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), true);
    }

    private void OnChatSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isNarrow = e.NewSize.Width < 640;
        if (_isNarrow == isNarrow)
            return;

        _isNarrow = isNarrow;
        ApplyDensity();
    }

    public void Bind(
        ChatPageViewModel vm,
        DebugPanelViewModel debugVm,
        IBackgroundService backgroundService,
        IUiDensityService densityService)
    {
        if (_bound) return;
        _bound = true;

        _vm = vm;
        _debugVm = debugVm;
        _backgroundService = backgroundService;
        _densityService = densityService;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ChatPageViewModel.CurrentChat)
                or nameof(ChatPageViewModel.ErrorMessage)
                or nameof(ChatPageViewModel.IsLoading))
                DispatcherQueue.TryEnqueue(RenderMessages);
            if (args.PropertyName is nameof(ChatPageViewModel.AgentStatusText)
                or nameof(ChatPageViewModel.IsAgentStatusVisible)
                or nameof(ChatPageViewModel.CurrentAgentRunStatus))
                DispatcherQueue.TryEnqueue(UpdateAgentStatus);
        };

        ApplyBackgroundConfig(_backgroundService.GetConfig());
        _backgroundService.ConfigChanged += (_, config) => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyBackgroundConfig(config);
            RenderMessages();
        });

        _densityService.DensityChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyDensity();
            RenderMessages();
        });

        ApplyDensity();
        UpdateAgentStatus();
        RenderMessages();
    }

    private void UpdateAgentStatus()
    {
        if (_vm == null)
            return;
        AgentStatusBar.Visibility = _vm.IsAgentStatusVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        AgentStatusText.Text = _vm.AgentStatusText;
        AgentProgressRing.IsActive =
            _vm.CurrentAgentRunStatus is AgentRunStatuses.Running or AgentRunStatuses.AwaitingApproval;
        ResumeAgentButton.Visibility =
            _vm.CurrentAgentRunStatus is AgentRunStatuses.Paused or AgentRunStatuses.Cancelled or AgentRunStatuses.Failed
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void ResumeAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
            await _vm.ResumeAgentRunAsync();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(RenderMessages);

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (MessagesScrollViewer.ScrollableHeight <= 0)
            return;

        var delta = e.GetCurrentPoint(MessagesScrollViewer).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        var target = Math.Clamp(
            MessagesScrollViewer.VerticalOffset - delta * 0.34,
            0,
            MessagesScrollViewer.ScrollableHeight);

        MessagesScrollViewer.ChangeView(null, target, null, false);
        e.Handled = true;
    }

    private void RenderMessages()
    {
        if (_vm == null) return;

        var shouldScrollToBottom =
            _vm.Messages.Count != _lastMessageCount || IsNearBottom();
        _lastMessageCount = _vm.Messages.Count;

        MessagesStack.Children.Clear();
        if (!string.IsNullOrWhiteSpace(_vm.ErrorMessage))
            MessagesStack.Children.Add(BuildErrorState(_vm.ErrorMessage));

        if (_vm.CurrentChat == null)
        {
            MessagesStack.Children.Add(BuildNoChatState());
            return;
        }

        if (_vm.Messages.Count == 0)
        {
            MessagesStack.Children.Add(BuildEmptyState());
            return;
        }

        foreach (var message in _vm.Messages)
            MessagesStack.Children.Add(BuildMessage(message));

        if (shouldScrollToBottom)
        {
            MessagesScrollViewer.UpdateLayout();
            MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight, null, true);
        }
    }

    private bool IsNearBottom() =>
        MessagesScrollViewer.ScrollableHeight <= 0 ||
        MessagesScrollViewer.ScrollableHeight - MessagesScrollViewer.VerticalOffset < 80;

    private UIElement BuildNoChatState()
    {
        var panel = CenterStatePanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Select a chat",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Open a conversation from the sidebar, or create a new one.",
            FontSize = 14,
            Foreground = TextSecondaryBrush(),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(PrimaryStateButton("New Chat", Symbol.Add, async () =>
        {
            if (App.MainWindow is MainWindow window)
                await window.SidebarVM.CreateChatAsync();
        }));
        return panel;
    }

    private UIElement BuildEmptyState()
    {
        var panel = CenterStatePanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Start a conversation",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Messages and raw prompt inspection will appear here. Press Enter to send.",
            FontSize = 14,
            Foreground = TextSecondaryBrush(),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 440,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        return panel;
    }

    private UIElement BuildErrorState(string error)
    {
        var isConfigError =
            error.Contains("api key", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("not configured", StringComparison.OrdinalIgnoreCase);

        var border = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            Background = ThemeBrush(
                Color.FromArgb(0xFF, 0xFF, 0xF7, 0xED),
                Color.FromArgb(0xE8, 0x2A, 0x1D, 0x1B)),
            BorderBrush = ThemeBrush(
                Color.FromArgb(0xFF, 0xFD, 0xBA, 0x74),
                Color.FromArgb(0x88, 0xFF, 0x6B, 0x6B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, IsCompactDensity() ? 4 : 8)
        };

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = isConfigError
                ? "API key is missing. Open Settings to add a provider key before sending."
                : $"Request failed: {error}",
            Foreground = TextPrimaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (isConfigError)
        {
            var button = PrimaryStateButton("Settings", Symbol.Setting, OpenSettingsAsync);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
        }

        border.Child = grid;
        return border;
    }

    private UIElement BuildMessage(Message message)
    {
        var isUser = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);
        var isSystem = string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase);
        var isAssistant = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase);
        var isTool = string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase);

        var row = new Grid
        {
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = IsCompactDensity() ? 720 : 800
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = IsCompactDensity()
                ? new Thickness(12, 9, 12, 9)
                : new Thickness(15, 12, 15, 12),
            BorderThickness = new Thickness(isUser ? 0 : 1),
            BorderBrush = MessageBorderBrush(),
            Background = MessageBrush(message.Role)
        };

        var stack = new StackPanel { Spacing = 7 };
        stack.Children.Add(new TextBlock
        {
            Text = isSystem ? "system" : isUser ? "you" : isTool ? "sandbox" : "assistant",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = isUser ? AccentTextBrush() : TextMutedBrush()
        });
        stack.Children.Add(new TextBlock
        {
            Text = message.Content,
            TextWrapping = TextWrapping.Wrap,
            Foreground = isUser ? AccentTextBrush() : TextPrimaryBrush(),
            FontSize = IsCompactDensity() ? 13 : 14,
            LineHeight = IsCompactDensity() ? 20 : 22
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        actions.Children.Add(ActionButton(Symbol.Copy, "Copy", isUser, () =>
        {
            CopyMessage(message);
            return Task.CompletedTask;
        }));
        if (isAssistant)
            actions.Children.Add(ActionButton(Symbol.Refresh, IsApiError(message) ? "Retry" : "Regenerate", isUser, () => _vm?.RegenerateMessageAsync(message) ?? Task.CompletedTask));
        else if (!isTool)
            actions.Children.Add(ActionButton(Symbol.Edit, "Edit and resend", isUser, () => EditAndResendAsync(message)));
        if (!isTool)
            actions.Children.Add(ActionButton(Symbol.Forward, "Continue from here", isUser, () => _vm?.ContinueFromMessageAsync(message) ?? Task.CompletedTask));
        if (message.TurnId is { } turnId && _debugVm != null)
            actions.Children.Add(ActionButton(Symbol.Find, "Inspect prompt", isUser, () => OpenInspectorAsync(turnId)));
        stack.Children.Add(actions);

        border.Child = stack;
        row.Children.Add(border);
        return row;
    }

    private Button ActionButton(Symbol symbol, string tooltip, bool onAccent, Func<Task> action)
    {
        var button = new Button
        {
            Content = new SymbolIcon { Symbol = symbol },
            Width = 34,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0x00, 0x00, 0x00)),
            Foreground = onAccent ? AccentTextBrush() : AccentBrush(),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8)
        };
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += async (_, _) => await action();
        return button;
    }

    private static void CopyMessage(Message message)
    {
        var package = new DataPackage();
        package.SetText(message.Content);
        Clipboard.SetContent(package);
    }

    private async Task OpenInspectorAsync(Guid turnId)
    {
        if (_debugVm == null) return;
        try
        {
            await _debugVm.OpenDebugAsync(turnId);
        }
        catch (Exception ex)
        {
            App.Log($"OPEN DEBUG FAILED: {ex}");
        }
    }

    private async Task EditAndResendAsync(Message message)
    {
        if (_vm == null || App.MainWindow is not MainWindow window)
            return;

        var box = new TextBox
        {
            Text = message.Content,
            AcceptsReturn = true,
            MinHeight = 140,
            Width = 460,
            TextWrapping = TextWrapping.Wrap,
            Background = MessageBrush("system"),
            Foreground = TextPrimaryBrush(),
            BorderBrush = MessageBorderBrush(),
            CornerRadius = new CornerRadius(8)
        };
        var dialog = new ContentDialog
        {
            Title = "Edit and Resend",
            Content = box,
            PrimaryButtonText = "Send",
            CloseButtonText = "Cancel",
            XamlRoot = window.Content.XamlRoot
        };
        if (window.Content is FrameworkElement root)
            dialog.RequestedTheme = root.ActualTheme;

        var result = await window.TryShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
            await _vm.EditAndResendMessageAsync(message, box.Text);
    }

    private static StackPanel CenterStatePanel() => new()
    {
        Spacing = 9,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 120, 0, 0)
    };

    private Button PrimaryStateButton(string text, Symbol symbol, Func<Task> action)
    {
        var button = new Button
        {
            Background = AccentBrush(),
            Foreground = AccentTextBrush(),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children =
                {
                    new SymbolIcon { Symbol = symbol },
                    new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }
                }
            }
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task OpenSettingsAsync()
    {
        if (App.MainWindow is not MainWindow window)
            return;

        await window.SettingsVM.LoadAsync();
        var dialog = new SettingsContentDialog
        {
            DataContext = window.SettingsVM,
            RequestedTheme = window.Content is FrameworkElement root
                ? root.ActualTheme
                : Microsoft.UI.Xaml.ElementTheme.Default,
            XamlRoot = window.Content.XamlRoot
        };
        await window.TryShowDialogAsync(dialog);
    }

    private static bool IsApiError(Message message) =>
        message.Content.StartsWith("[API Error", StringComparison.OrdinalIgnoreCase) ||
        message.Content.StartsWith("[Error", StringComparison.OrdinalIgnoreCase);

    private SolidColorBrush MessageBrush(string role)
    {
        var color = role.ToLowerInvariant() switch
        {
            "user" => ThemeColor(
                Color.FromArgb(0xFF, 0x25, 0x63, 0xEB),
                Color.FromArgb(0xFF, 0x39, 0x7E, 0xFF)),
            "system" => ThemeColor(
                Color.FromArgb(0xFF, 0xEC, 0xF2, 0xFA),
                Color.FromArgb(0xE8, 0x21, 0x2B, 0x39)),
            "tool" => ThemeColor(
                Color.FromArgb(0xFF, 0xF2, 0xF7, 0xFF),
                Color.FromArgb(0xE8, 0x13, 0x20, 0x2E)),
            _ => ThemeColor(
                Color.FromArgb(0xF8, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0xEC, 0x17, 0x22, 0x31))
        };

        if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            color.A = (byte)Math.Round(color.A * Math.Clamp(_chatBubbleOpacity, 0.16, 1));

        return new SolidColorBrush(color);
    }

    private void ApplyBackgroundConfig(BgConfig config) =>
        _chatBubbleOpacity = Math.Clamp(config.ChatOpacity / 100.0, 0.16, 1.0);

    private bool IsLightTheme() => ActualTheme == Microsoft.UI.Xaml.ElementTheme.Light;

    private Color ThemeColor(Color light, Color dark) => IsLightTheme() ? light : dark;

    private SolidColorBrush TextPrimaryBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0x16, 0x1D, 0x28),
        Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));

    private SolidColorBrush TextSecondaryBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0x58, 0x67, 0x79),
        Color.FromArgb(0xFF, 0xDC, 0xE4, 0xEE)));

    private SolidColorBrush TextMutedBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0x84, 0x90, 0xA1),
        Color.FromArgb(0xFF, 0x9A, 0xA8, 0xBA)));

    private SolidColorBrush AccentBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0x25, 0x63, 0xEB),
        Color.FromArgb(0xFF, 0x6A, 0xA7, 0xFF)));

    private SolidColorBrush AccentTextBrush() => new(Microsoft.UI.Colors.White);

    private SolidColorBrush MessageBorderBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0xD3, 0xDD, 0xE9),
        Color.FromArgb(0x66, 0x6F, 0x7D, 0x91)));

    private SolidColorBrush ThemeBrush(Color light, Color dark) => new(ThemeColor(light, dark));

    private bool IsCompactDensity() => _densityService?.CurrentDensity == UiDensity.Compact;

    private void ApplyDensity()
    {
        var compact = IsCompactDensity();
        MessagesScrollViewer.Padding = _isNarrow
            ? compact
                ? new Thickness(10, 10, 10, 10)
                : new Thickness(14, 12, 14, 12)
            : compact
                ? new Thickness(18, 12, 18, 12)
                : new Thickness(24, 18, 24, 18);
        MessagesStack.Spacing = compact ? 10 : 14;
    }
}
