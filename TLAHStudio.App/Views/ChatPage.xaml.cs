using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Automation;
using Windows.UI;
using TLAHStudio.App.ViewModels;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace TLAHStudio.App.Views;

public sealed partial class ChatPage : UserControl
{
    private ChatPageViewModel? _vm;
    private DebugPanelViewModel? _debugVm;
    private IBackgroundService? _backgroundService;
    private IUiDensityService? _densityService;
    private ISandboxCommandService? _sandbox;
    private IInteractionSoundService? _sound;
    private bool _bound;
    private double _chatBubbleOpacity = 1;
    private int _lastMessageCount;
    private bool _isNarrow;
    private bool _renderQueued;
    private DateTimeOffset _lastRenderAt = DateTimeOffset.MinValue;
    private string _lastLayoutSignature = string.Empty;
    private readonly Dictionary<Guid, CachedMessageElement> _messageElementCache = new();
    private const int RenderThrottleMs = 50;

    public ChatPage()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) =>
        {
            InvalidateMessageCache();
            RequestRender(immediate: true);
        };
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
        IUiDensityService densityService,
        ISandboxCommandService sandbox,
        IInteractionSoundService sound)
    {
        if (_bound) return;
        _bound = true;

        _vm = vm;
        _debugVm = debugVm;
        _backgroundService = backgroundService;
        _densityService = densityService;
        _sandbox = sandbox;
        _sound = sound;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        _vm.AgentProgressLines.CollectionChanged += OnAgentProgressChanged;
        _vm.StreamingMessageUpdated += (_, _) => RequestRender();
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ChatPageViewModel.CurrentChat)
                or nameof(ChatPageViewModel.ErrorMessage)
                or nameof(ChatPageViewModel.IsLoading)
                or nameof(ChatPageViewModel.IsAgentLiveVisible)
                or nameof(ChatPageViewModel.AgentLiveSummary))
                RequestRender();
            if (args.PropertyName is nameof(ChatPageViewModel.AgentStatusText)
                or nameof(ChatPageViewModel.IsAgentStatusVisible)
                or nameof(ChatPageViewModel.CurrentAgentRunStatus))
                DispatcherQueue.TryEnqueue(UpdateAgentStatus);
        };

        ApplyBackgroundConfig(_backgroundService.GetConfig());
        _backgroundService.ConfigChanged += (_, config) => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyBackgroundConfig(config);
            InvalidateMessageCache();
            RequestRender(immediate: true);
        });

        _densityService.DensityChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            ApplyDensity();
            InvalidateMessageCache();
            RequestRender(immediate: true);
        });

        ApplyDensity();
        UpdateAgentStatus();
        RequestRender(immediate: true);
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

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm?.IsLoading != true &&
            e.Action == NotifyCollectionChangedAction.Add &&
            e.NewItems?.OfType<Message>().Any(m =>
                string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase)) == true)
        {
            _sound?.Play(InteractionSound.Receive);
        }

        RequestRender(immediate: e.Action is NotifyCollectionChangedAction.Reset);
    }

    private void OnAgentProgressChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RequestRender();

    private void RequestRender(bool immediate = false)
    {
        if (immediate)
        {
            DispatcherQueue.TryEnqueue(RenderMessages);
            return;
        }

        if (_renderQueued)
            return;

        _renderQueued = true;
        var delayMs = Math.Max(
            0,
            RenderThrottleMs - (int)(DateTimeOffset.UtcNow - _lastRenderAt).TotalMilliseconds);
        _ = Task.Run(async () =>
        {
            if (delayMs > 0)
                await Task.Delay(delayMs);
            DispatcherQueue.TryEnqueue(() =>
            {
                _renderQueued = false;
                RenderMessages();
            });
        });
    }

    private void InvalidateMessageCache()
    {
        _messageElementCache.Clear();
        _lastLayoutSignature = string.Empty;
    }

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
        _lastRenderAt = DateTimeOffset.UtcNow;

        var shouldScrollToBottom =
            _vm.Messages.Count != _lastMessageCount || IsNearBottom();
        _lastMessageCount = _vm.Messages.Count;

        var elements = new List<UIElement>();
        if (!string.IsNullOrWhiteSpace(_vm.ErrorMessage))
            elements.Add(BuildErrorState(_vm.ErrorMessage));

        if (_vm.CurrentChat == null)
        {
            SyncMessageChildren(elements.Append(BuildNoChatState()).ToList(), "no-chat");
            return;
        }

        if (_vm.Messages.Count == 0)
        {
            SyncMessageChildren(elements.Append(BuildEmptyState()).ToList(), "empty");
            return;
        }

        foreach (var message in _vm.Messages)
            elements.Add(GetCachedMessageElement(message));

        if (_vm.IsAgentLiveVisible && _vm.AgentProgressLines.Count > 0)
            elements.Add(BuildAgentLiveCard());

        var signature = string.Join(
            "|",
            _vm.Messages.Select(m => $"{m.Id:N}:{m.Role}:{m.Content.Length}:{m.TurnId?.ToString("N") ?? "draft"}"))
            + $"|err:{_vm.ErrorMessage?.Length ?? 0}|live:{_vm.AgentProgressLines.Count}";
        SyncMessageChildren(elements, signature);

        if (shouldScrollToBottom)
        {
            DispatcherQueue.TryEnqueue(() =>
                MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight, null, true));
        }
    }

    private UIElement GetCachedMessageElement(Message message)
    {
        var signature = $"{message.Role}|{message.Content}|{message.TurnId?.ToString("N") ?? "draft"}|{_chatBubbleOpacity}|{IsCompactDensity()}";
        if (_messageElementCache.TryGetValue(message.Id, out var cached) &&
            string.Equals(cached.Signature, signature, StringComparison.Ordinal))
        {
            return cached.Element;
        }

        var element = BuildMessage(message);
        _messageElementCache[message.Id] = new CachedMessageElement(signature, element);
        return element;
    }

    private void SyncMessageChildren(IReadOnlyList<UIElement> elements, string signature)
    {
        if (!string.Equals(signature, _lastLayoutSignature, StringComparison.Ordinal) ||
            MessagesStack.Children.Count != elements.Count)
        {
            MessagesStack.Children.Clear();
            foreach (var element in elements)
                MessagesStack.Children.Add(element);
            _lastLayoutSignature = signature;
            return;
        }

        for (var i = 0; i < elements.Count; i++)
        {
            if (!ReferenceEquals(MessagesStack.Children[i], elements[i]))
                MessagesStack.Children[i] = elements[i];
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
        var isDraft = isAssistant && message.TurnId == null && _vm?.IsSending == true;

        var row = new Grid
        {
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = IsCompactDensity() ? 740 : 840
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = IsCompactDensity()
                ? new Thickness(12, 9, 12, 9)
                : new Thickness(16, 13, 16, 13),
            BorderThickness = new Thickness(1),
            BorderBrush = isUser ? AccentSubtleBrush() : MessageBorderBrush(),
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
        stack.Children.Add(BuildMessageBody(message, isDraft, isUser));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        if (!isDraft)
        {
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
        }

        border.Child = stack;
        row.Children.Add(border);
        return row;
    }

    private UIElement BuildMessageBody(Message message, bool isDraft, bool isUser)
    {
        if (AssistantContentFormatter.TryParse(message.Content, out var thinking, out var answer, out var isExpanded))
        {
            var parsed = MessageAttachmentFormatter.Extract(answer);
            return BuildLiveStreamBody(thinking, parsed.Body, isExpanded, parsed.Attachments, message.ChatId);
        }

        var body = MessageAttachmentFormatter.Extract(message.Content);
        if (body.Attachments.Count > 0)
        {
            return BuildMessageBodyWithAttachments(
                string.IsNullOrEmpty(body.Body) && isDraft
                    ? "Waiting for the first token..."
                    : body.Body,
                body.Attachments,
                message.ChatId,
                isUser,
                isDraft);
        }

        return new TextBlock
        {
            Text = string.IsNullOrEmpty(message.Content) && isDraft
                ? "Waiting for the first token..."
                : message.Content,
            TextWrapping = TextWrapping.Wrap,
            Foreground = isUser
                ? AccentTextBrush()
                : string.IsNullOrEmpty(message.Content) && isDraft
                    ? TextSecondaryBrush()
                    : TextPrimaryBrush(),
            FontSize = IsCompactDensity() ? 13 : 14,
            LineHeight = IsCompactDensity() ? 20 : 22
        };
    }

    private UIElement BuildLiveStreamBody(
        string thinking,
        string answer,
        bool isExpanded,
        IReadOnlyList<MessageAttachment>? attachments,
        Guid chatId)
    {
        var panel = new StackPanel { Spacing = 8 };
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock
        {
            Text = isExpanded ? "Thinking..." : $"Thinking collapsed · {thinking.Length} chars",
            Foreground = TextSecondaryBrush(),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var preview = AssistantContentFormatter.Preview(thinking);
        if (!isExpanded && !string.IsNullOrWhiteSpace(preview))
        {
            header.Children.Add(new TextBlock
            {
                Text = preview,
                Foreground = TextMutedBrush(),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                MaxWidth = IsCompactDensity() ? 640 : 720
            });
        }

        var thinkingBox = new Expander
        {
            IsExpanded = isExpanded,
            Header = header,
            Background = ThemeBrush(
                Color.FromArgb(0xAA, 0xF3, 0xF7, 0xFD),
                Color.FromArgb(0x8A, 0x0E, 0x17, 0x25)),
            BorderBrush = MessageBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        thinkingBox.Content = new ScrollViewer
        {
            MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = thinking,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextSecondaryBrush(),
                FontSize = IsCompactDensity() ? 12 : 13,
                LineHeight = IsCompactDensity() ? 18 : 20
            }
        };
        panel.Children.Add(thinkingBox);

        if (!string.IsNullOrEmpty(answer))
        {
            panel.Children.Add(new TextBlock
            {
                Text = answer,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextPrimaryBrush(),
                FontSize = IsCompactDensity() ? 13 : 14,
                LineHeight = IsCompactDensity() ? 20 : 22
            });
        }

        AddAttachmentCards(panel, attachments, chatId);

        return panel;
    }

    private UIElement BuildMessageBodyWithAttachments(
        string body,
        IReadOnlyList<MessageAttachment> attachments,
        Guid chatId,
        bool isUser,
        bool isDraft)
    {
        var panel = new StackPanel { Spacing = 10 };
        if (!string.IsNullOrWhiteSpace(body) || isDraft)
        {
            panel.Children.Add(new TextBlock
            {
                Text = body,
                TextWrapping = TextWrapping.Wrap,
                Foreground = isUser
                    ? AccentTextBrush()
                    : string.IsNullOrEmpty(body) && isDraft
                        ? TextSecondaryBrush()
                        : TextPrimaryBrush(),
                FontSize = IsCompactDensity() ? 13 : 14,
                LineHeight = IsCompactDensity() ? 20 : 22
            });
        }

        AddAttachmentCards(panel, attachments, chatId);
        return panel;
    }

    private void AddAttachmentCards(
        StackPanel panel,
        IReadOnlyList<MessageAttachment>? attachments,
        Guid chatId)
    {
        if (attachments is not { Count: > 0 })
            return;

        var attachmentPanel = new StackPanel { Spacing = 8 };
        foreach (var attachment in attachments)
            attachmentPanel.Children.Add(BuildAttachmentCard(attachment, chatId));
        panel.Children.Add(attachmentPanel);
    }

    private UIElement BuildAttachmentCard(MessageAttachment attachment, Guid chatId)
    {
        var path = ResolveAttachmentPath(chatId, attachment.RelativePath);
        var exists = path != null && File.Exists(path);
        var filename = Path.GetFileName(attachment.RelativePath);
        if (string.IsNullOrWhiteSpace(filename))
            filename = attachment.RelativePath;

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            BorderBrush = MessageBorderBrush(),
            Background = ThemeBrush(
                Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0xA8, 0x0E, 0x17, 0x25)),
            MaxWidth = IsCompactDensity() ? 660 : 720
        };

        var stack = new StackPanel { Spacing = 8 };
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new SymbolIcon
        {
            Symbol = AttachmentSymbol(attachment.ContentType),
            Foreground = AccentBrush(),
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Top
        });

        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(new TextBlock
        {
            Text = filename,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        title.Children.Add(new TextBlock
        {
            Text = $"{attachment.ContentType} · {FormatSize(attachment.SizeBytes)} · sha256 {ShortHash(attachment.Sha256)}",
            FontSize = 11,
            Foreground = TextMutedBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        actions.Children.Add(AttachmentButton(Symbol.Forward, "Open", exists, () => OpenAttachmentAsync(path)));
        actions.Children.Add(AttachmentButton(Symbol.Save, "Save As", exists, () => SaveAttachmentAsAsync(path, filename)));
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        stack.Children.Add(header);

        if (!exists)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "The sandbox file is no longer available on this device.",
                Foreground = TextSecondaryBrush(),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
        }
        else if (IsImageAttachment(attachment))
        {
            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 720, 360) },
                Child = new Microsoft.UI.Xaml.Controls.Image
                {
                    Source = new BitmapImage(new Uri(path!)),
                    Stretch = Stretch.Uniform,
                    MaxHeight = IsCompactDensity() ? 240 : 300,
                    HorizontalAlignment = HorizontalAlignment.Left
                }
            });
        }
        else if (IsVideoAttachment(attachment))
        {
            stack.Children.Add(new MediaPlayerElement
            {
                Source = MediaSource.CreateFromUri(new Uri(path!)),
                AreTransportControlsEnabled = true,
                Height = IsCompactDensity() ? 220 : 280,
                MaxWidth = IsCompactDensity() ? 620 : 680
            });
        }
        else if (IsTextAttachment(attachment) && TryReadTextPreview(path!, out var preview))
        {
            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Background = ThemeBrush(
                    Color.FromArgb(0xCC, 0xF5, 0xF8, 0xFC),
                    Color.FromArgb(0xCC, 0x09, 0x10, 0x1B)),
                Child = new TextBlock
                {
                    Text = preview,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = TextSecondaryBrush(),
                    FontFamily = new FontFamily("Cascadia Mono"),
                    FontSize = IsCompactDensity() ? 12 : 13,
                    MaxHeight = 220
                }
            });
        }

        card.Child = stack;
        return card;
    }

    private Button AttachmentButton(Symbol symbol, string tooltip, bool enabled, Func<Task> action)
    {
        var button = new Button
        {
            Content = new SymbolIcon { Symbol = symbol },
            Width = 34,
            Height = 32,
            Padding = new Thickness(0),
            IsEnabled = enabled,
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0x00, 0x00, 0x00)),
            Foreground = AccentBrush(),
            BorderBrush = MessageBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += async (_, _) => await action();
        return button;
    }

    private string? ResolveAttachmentPath(Guid chatId, string relativePath)
    {
        if (_sandbox == null || string.IsNullOrWhiteSpace(relativePath))
            return null;

        try
        {
            var normalizedRelative = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedRelative))
                return null;

            var root = Path.GetFullPath(_sandbox.GetSandboxRoot(chatId))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, normalizedRelative));
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task OpenAttachmentAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var file = await StorageFile.GetFileFromPathAsync(path);
        await Launcher.LaunchFileAsync(file);
    }

    private async Task SaveAttachmentAsAsync(string? path, string filename)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || App.MainWindow == null)
            return;

        var extension = Path.GetExtension(filename);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".bin";

        var picker = new FileSavePicker
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(filename),
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        picker.FileTypeChoices.Add($"{extension.TrimStart('.').ToUpperInvariant()} file", [extension]);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var target = await picker.PickSaveFileAsync();
        if (target == null)
            return;

        CachedFileManager.DeferUpdates(target);
        await using (var source = File.OpenRead(path))
        await using (var output = await target.OpenStreamForWriteAsync())
        {
            output.SetLength(0);
            await source.CopyToAsync(output);
        }
        await CachedFileManager.CompleteUpdatesAsync(target);
    }

    private static Symbol AttachmentSymbol(string contentType)
    {
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return Symbol.Pictures;
        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return Symbol.Video;
        if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return Symbol.Audio;
        if (contentType.Contains("zip", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("compressed", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("rar", StringComparison.OrdinalIgnoreCase))
            return Symbol.Folder;
        return Symbol.Document;
    }

    private static bool IsImageAttachment(MessageAttachment attachment) =>
        attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideoAttachment(MessageAttachment attachment) =>
        attachment.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextAttachment(MessageAttachment attachment) =>
        attachment.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        attachment.ContentType.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadTextPreview(string path, out string preview)
    {
        preview = string.Empty;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 64 * 1024)
                return false;

            var text = File.ReadAllText(path);
            preview = text.Length <= 3000 ? text : text[..3000] + "\n...";
            return !string.IsNullOrWhiteSpace(preview);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / 1024.0 / 1024.0:0.0} MB";
    }

    private static string ShortHash(string sha256) =>
        string.IsNullOrWhiteSpace(sha256)
            ? "unknown"
            : sha256.Length <= 12 ? sha256 : sha256[..12];

    private UIElement BuildAgentLiveCard()
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = IsCompactDensity()
                ? new Thickness(12, 10, 12, 10)
                : new Thickness(15, 13, 15, 13),
            BorderThickness = new Thickness(1),
            BorderBrush = AccentSubtleBrush(),
            Background = ThemeBrush(
                Color.FromArgb(0xF2, 0xFB, 0xFD, 0xFF),
                Color.FromArgb(0xEA, 0x11, 0x1B, 0x2B)),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = IsCompactDensity() ? 740 : 840
        };

        var stack = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new ProgressRing
        {
            Width = 18,
            Height = 18,
            IsActive = true,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AccentBrush()
        });
        var headerText = new StackPanel { Spacing = 2 };
        headerText.Children.Add(new TextBlock
        {
            Text = "Live agent activity",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush()
        });
        headerText.Children.Add(new TextBlock
        {
            Text = _vm?.AgentLiveSummary ?? "Working...",
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextSecondaryBrush(),
            FontSize = IsCompactDensity() ? 12 : 13,
            LineHeight = IsCompactDensity() ? 18 : 20
        });
        Grid.SetColumn(headerText, 1);
        header.Children.Add(headerText);
        stack.Children.Add(header);

        var lines = _vm?.AgentProgressLines.TakeLast(IsCompactDensity() ? 6 : 8).ToList()
                    ?? new List<AgentProgressLine>();
        foreach (var line in lines)
            stack.Children.Add(BuildAgentProgressLine(line));

        border.Child = stack;
        return border;
    }

    private UIElement BuildAgentProgressLine(AgentProgressLine line)
    {
        var grid = new Grid
        {
            ColumnSpacing = 9,
            Margin = new Thickness(0, 1, 0, 0)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tag = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3),
            Background = ProgressTagBrush(line.Severity),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = line.Label,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ProgressTagTextBrush(line.Severity)
            }
        };
        grid.Children.Add(tag);

        var content = new StackPanel { Spacing = 5 };
        content.Children.Add(new TextBlock
        {
            Text = line.Text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextPrimaryBrush(),
            FontSize = IsCompactDensity() ? 12 : 13,
            LineHeight = IsCompactDensity() ? 18 : 20
        });
        var toolPreview = BuildAgentToolPreview(line);
        if (toolPreview != null)
            content.Children.Add(toolPreview);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private UIElement? BuildAgentToolPreview(AgentProgressLine line)
    {
        if (string.IsNullOrWhiteSpace(line.ToolTitle) &&
            string.IsNullOrWhiteSpace(line.Preview) &&
            string.IsNullOrWhiteSpace(line.PrimaryPath))
        {
            return null;
        }

        var stack = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrWhiteSpace(line.ToolTitle) ||
            !string.IsNullOrWhiteSpace(line.PrimaryPath))
        {
            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(line.PrimaryPath)
                    ? line.ToolTitle
                    : $"{line.ToolTitle} · {line.PrimaryPath}",
                Foreground = TextPrimaryBrush(),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            stack.Children.Add(title);
        }

        if (!string.IsNullOrWhiteSpace(line.Preview))
        {
            stack.Children.Add(new TextBlock
            {
                Text = line.IsTruncated ? $"{line.Preview} [truncated]" : line.Preview,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextSecondaryBrush(),
                FontSize = 12,
                LineHeight = 18,
                MaxLines = 3
            });
        }

        return new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 8, 10, 8),
            BorderThickness = new Thickness(1),
            BorderBrush = ToolPreviewBorderBrush(line.RenderHint),
            Background = ToolPreviewBackgroundBrush(line.RenderHint),
            Child = stack
        };
    }

    private Brush ToolPreviewBackgroundBrush(string? renderHint)
    {
        var dark = renderHint switch
        {
            AgentToolRenderHints.Terminal => Color.FromArgb(0xDC, 0x08, 0x10, 0x1F),
            AgentToolRenderHints.File => Color.FromArgb(0xDC, 0x0E, 0x1A, 0x2A),
            AgentToolRenderHints.Network => Color.FromArgb(0xDC, 0x0C, 0x16, 0x25),
            AgentToolRenderHints.Git => Color.FromArgb(0xDC, 0x14, 0x13, 0x24),
            AgentToolRenderHints.Mcp => Color.FromArgb(0xDC, 0x12, 0x14, 0x22),
            _ => Color.FromArgb(0xDC, 0x10, 0x18, 0x28)
        };
        var light = renderHint switch
        {
            AgentToolRenderHints.Terminal => Color.FromArgb(0xF5, 0xF7, 0xFB, 0xFF),
            AgentToolRenderHints.File => Color.FromArgb(0xF5, 0xF7, 0xFA, 0xFD),
            AgentToolRenderHints.Network => Color.FromArgb(0xF5, 0xF5, 0xFA, 0xFF),
            AgentToolRenderHints.Git => Color.FromArgb(0xF5, 0xFB, 0xF7, 0xFF),
            AgentToolRenderHints.Mcp => Color.FromArgb(0xF5, 0xF8, 0xF7, 0xFF),
            _ => Color.FromArgb(0xF5, 0xF7, 0xFA, 0xFD)
        };
        return ThemeBrush(light, dark);
    }

    private Brush ToolPreviewBorderBrush(string? renderHint)
    {
        var dark = renderHint switch
        {
            AgentToolRenderHints.Terminal => Color.FromArgb(0xA0, 0x46, 0x5F, 0x83),
            AgentToolRenderHints.File => Color.FromArgb(0xA0, 0x3A, 0x61, 0x83),
            AgentToolRenderHints.Network => Color.FromArgb(0xA0, 0x3B, 0x5D, 0x8A),
            AgentToolRenderHints.Git => Color.FromArgb(0xA0, 0x5C, 0x54, 0x8A),
            AgentToolRenderHints.Mcp => Color.FromArgb(0xA0, 0x56, 0x56, 0x83),
            _ => Color.FromArgb(0xA0, 0x3A, 0x4C, 0x66)
        };
        var light = renderHint switch
        {
            AgentToolRenderHints.Terminal => Color.FromArgb(0xFF, 0xD7, 0xE2, 0xF0),
            AgentToolRenderHints.File => Color.FromArgb(0xFF, 0xD4, 0xE1, 0xEE),
            AgentToolRenderHints.Network => Color.FromArgb(0xFF, 0xD3, 0xE0, 0xF2),
            AgentToolRenderHints.Git => Color.FromArgb(0xFF, 0xDF, 0xD9, 0xF1),
            AgentToolRenderHints.Mcp => Color.FromArgb(0xFF, 0xDC, 0xDF, 0xEF),
            _ => Color.FromArgb(0xFF, 0xD8, 0xE1, 0xEC)
        };
        return ThemeBrush(light, dark);
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
        package.SetText(MessageAttachmentFormatter.StripAttachments(
            AssistantContentFormatter.StripThinking(message.Content)));
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
            Text = MessageAttachmentFormatter.StripAttachments(
                AssistantContentFormatter.StripThinking(message.Content)),
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
        VisibleMessageText(message.Content).StartsWith("[API Error", StringComparison.OrdinalIgnoreCase) ||
        VisibleMessageText(message.Content).StartsWith("[Error", StringComparison.OrdinalIgnoreCase);

    private static string VisibleMessageText(string content) =>
        MessageAttachmentFormatter.StripAttachments(
            AssistantContentFormatter.StripThinking(content));

    private SolidColorBrush MessageBrush(string role)
    {
        var color = role.ToLowerInvariant() switch
        {
            "user" => ThemeColor(
                Color.FromArgb(0xFF, 0x2F, 0x5F, 0xEA),
                Color.FromArgb(0xFF, 0x34, 0x67, 0xF6)),
            "system" => ThemeColor(
                Color.FromArgb(0xFF, 0xEE, 0xF4, 0xFB),
                Color.FromArgb(0xEA, 0x1E, 0x2A, 0x3D)),
            "tool" => ThemeColor(
                Color.FromArgb(0xFF, 0xF3, 0xF8, 0xFF),
                Color.FromArgb(0xEA, 0x11, 0x20, 0x30)),
            _ => ThemeColor(
                Color.FromArgb(0xF8, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0xE8, 0x14, 0x21, 0x32))
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
        Color.FromArgb(0xFF, 0x17, 0x20, 0x33),
        Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));

    private SolidColorBrush TextSecondaryBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0x56, 0x65, 0x7A),
        Color.FromArgb(0xFF, 0xE0, 0xE8, 0xF4)));

    private SolidColorBrush TextMutedBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0x7D, 0x8B, 0xA0),
        Color.FromArgb(0xFF, 0x91, 0xA0, 0xB4)));

    private SolidColorBrush AccentBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0x2F, 0x5F, 0xEA),
        Color.FromArgb(0xFF, 0x71, 0xA7, 0xFF)));

    private SolidColorBrush AccentTextBrush() => new(Microsoft.UI.Colors.White);

    private SolidColorBrush AccentSubtleBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0xBF, 0xD2, 0xFF),
        Color.FromArgb(0x99, 0x71, 0xA7, 0xFF)));

    private SolidColorBrush MessageBorderBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0xD2, 0xDC, 0xEB),
        Color.FromArgb(0x6E, 0x71, 0x81, 0x95)));

    private SolidColorBrush ThemeBrush(Color light, Color dark) => new(ThemeColor(light, dark));

    private SolidColorBrush ProgressTagBrush(string severity) => new(severity switch
    {
        AgentEventSeverities.Error => ThemeColor(
            Color.FromArgb(0xFF, 0xFE, 0xE2, 0xE2),
            Color.FromArgb(0xFF, 0x45, 0x1D, 0x24)),
        AgentEventSeverities.Warning => ThemeColor(
            Color.FromArgb(0xFF, 0xFE, 0xF3, 0xC7),
            Color.FromArgb(0xFF, 0x43, 0x34, 0x18)),
        _ => ThemeColor(
            Color.FromArgb(0xFF, 0xDB, 0xEA, 0xFF),
            Color.FromArgb(0xFF, 0x1A, 0x2F, 0x52))
    });

    private SolidColorBrush ProgressTagTextBrush(string severity) => new(severity switch
    {
        AgentEventSeverities.Error => ThemeColor(
            Color.FromArgb(0xFF, 0xB9, 0x1C, 0x1C),
            Color.FromArgb(0xFF, 0xFF, 0xB4, 0xB4)),
        AgentEventSeverities.Warning => ThemeColor(
            Color.FromArgb(0xFF, 0x92, 0x4E, 0x0E),
            Color.FromArgb(0xFF, 0xF8, 0xD6, 0x7A)),
        _ => ThemeColor(
            Color.FromArgb(0xFF, 0x1D, 0x4E, 0x9A),
            Color.FromArgb(0xFF, 0xB8, 0xD4, 0xFF))
    });

    private bool IsCompactDensity() => _densityService?.CurrentDensity == UiDensity.Compact;

    private sealed record CachedMessageElement(string Signature, UIElement Element);

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
