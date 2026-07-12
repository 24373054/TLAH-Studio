using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLAHStudio.App.ViewModels;
using TLAHStudio.App.Infrastructure;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using Windows.UI;

namespace TLAHStudio.App.Views;

public sealed partial class AgentActivityPanelControl : UserControl
{
    private ChatPageViewModel? _vm;
    private IUiDensityService? _densityService;
    private IInteractionSoundService? _sound;
    private bool _renderQueued;
    private bool _fullRefreshQueued;
    private bool _subscriptionsAttached;
    private bool _panelActive;
    private readonly HashSet<Guid> _expandedRuns = new();
    private readonly HashSet<Guid> _collapsedRuns = new();
    private readonly Dictionary<Guid, Expander> _runExpanders = new();
    private readonly Dictionary<Guid, int> _runHeaderSignatures = new();
    private readonly Dictionary<Guid, int> _runContentSignatures = new();

    private BindableCollectionAdapter<AgentActivityRun>? _activityItems;
    private DispatcherQueueTimer? _renderTimer;

    public AgentActivityPanelControl()
    {
        App.Log("AgentActivityPanelControl ctor entered.");
        InitializeComponent();
        App.Log("AgentActivityPanelControl XAML initialized.");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        ActivityRepeater.ItemTemplate = new ActivityRunElementFactory(this);
    }

    public void Bind(
        ChatPageViewModel vm,
        IUiDensityService densityService,
        IInteractionSoundService sound)
    {
        DetachSubscriptions();
        DisposeActivityItems();
        ClearRunElements(clearExpansionState: true);

        _vm = vm;
        _densityService = densityService;
        _sound = sound;
        if (_panelActive)
        {
            AttachSubscriptions();
            EnsureActivityItems();
            RequestRender(immediate: true, fullRefresh: true);
        }
    }

    public void SetPanelActive(bool active)
    {
        if (_panelActive == active)
            return;

        _panelActive = active;
        if (active)
        {
            AttachSubscriptions();
            EnsureActivityItems();
            RequestRender(immediate: true, fullRefresh: true);
        }
        else
        {
            StopRenderTimer();
            DetachSubscriptions();
            DisposeActivityItems();
            ClearRunElements();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_panelActive)
        {
            AttachSubscriptions();
            EnsureActivityItems();
            RequestRender(immediate: true, fullRefresh: true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRenderTimer();
        DetachSubscriptions();
        DisposeActivityItems();
        ClearRunElements();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) =>
        RequestRender(immediate: true, fullRefresh: true);

    private void OnAgentActivityChanged(object? sender, EventArgs e) => RequestRender();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ChatPageViewModel.CurrentChat)
            or nameof(ChatPageViewModel.AgentActivitySummary)
            or nameof(ChatPageViewModel.HasAgentActivity))
        {
            RequestRender();
        }
    }

    private void OnDensityChanged(object? sender, UiDensity density) =>
        RequestRender(immediate: true, fullRefresh: true);

    private void AttachSubscriptions()
    {
        if (_subscriptionsAttached || _vm == null || _densityService == null)
            return;

        _vm.AgentActivityChanged += OnAgentActivityChanged;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _densityService.DensityChanged += OnDensityChanged;
        _subscriptionsAttached = true;
    }

    private void DetachSubscriptions()
    {
        if (!_subscriptionsAttached)
            return;

        if (_vm != null)
        {
            _vm.AgentActivityChanged -= OnAgentActivityChanged;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (_densityService != null)
            _densityService.DensityChanged -= OnDensityChanged;

        _subscriptionsAttached = false;
    }

    private void EnsureActivityItems()
    {
        if (_activityItems != null || _vm == null)
            return;

        _activityItems = new BindableCollectionAdapter<AgentActivityRun>(_vm.AgentActivityRuns);
        ActivityRepeater.ItemsSource = _activityItems;
    }

    private void DisposeActivityItems()
    {
        ActivityRepeater.ItemsSource = null;
        _activityItems?.Dispose();
        _activityItems = null;
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        _sound?.Play(InteractionSound.Toggle);
        if (App.MainWindow is MainWindow window)
            window.ToggleAgentActivityPanel(false);
    }

    private void RequestRender(bool immediate = false, bool fullRefresh = false)
    {
        if (!_panelActive)
            return;

        _fullRefreshQueued |= fullRefresh;

        if (immediate && DispatcherQueue.HasThreadAccess)
        {
            StopRenderTimer();
            FlushRender();
            return;
        }

        if (_renderQueued)
            return;

        _renderQueued = true;
        if (immediate)
        {
            if (!DispatcherQueue.TryEnqueue(FlushRender))
            {
                _renderQueued = false;
                _fullRefreshQueued = false;
            }
            return;
        }

        _renderTimer ??= CreateRenderTimer();
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private DispatcherQueueTimer CreateRenderTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(64);
        timer.IsRepeating = false;
        timer.Tick += OnRenderTimerTick;
        return timer;
    }

    private void OnRenderTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        FlushRender();
    }

    private void StopRenderTimer()
    {
        _renderTimer?.Stop();
        _renderQueued = false;
    }

    private void FlushRender()
    {
        _renderQueued = false;
        var fullRefresh = _fullRefreshQueued;
        _fullRefreshQueued = false;

        if (fullRefresh)
            RebuildGeneratedElements();

        Render();
    }

    private void RebuildGeneratedElements()
    {
        ActivityEmptyStateHost.Content = null;
        ActivityRepeater.ItemsSource = null;
        ClearRunElements();
        if (_activityItems != null)
            ActivityRepeater.ItemsSource = _activityItems;
    }

    private void Render()
    {
        if (_vm == null)
            return;

        SummaryText.Text = _vm.AgentActivitySummary;
        if (_vm.CurrentChat == null)
        {
            ShowEmptyState("Select a chat", "Agent runs for the selected conversation will appear here.");
            return;
        }

        if (!_vm.HasAgentActivity)
        {
            ShowEmptyState("No agent runs yet", "Start Agent mode to build a persistent execution trail.");
            return;
        }

        ActivityEmptyStateHost.Content = null;
        if (ActivityRepeater.ItemsSource == null && _activityItems != null)
            ActivityRepeater.ItemsSource = _activityItems;
        ActivityScrollViewer.Visibility = Visibility.Visible;
        var activeRunIds = _vm.AgentActivityRuns.Select(run => run.Id).ToHashSet();
        PruneRunState(activeRunIds);

        foreach (var (runId, expander) in _runExpanders.ToArray())
        {
            var run = _vm.AgentActivityRuns.FirstOrDefault(candidate => candidate.Id == runId);
            if (run != null)
                UpdateRunExpander(expander, run);
        }
    }

    private void PruneRunState(HashSet<Guid> activeRunIds)
    {
        foreach (var staleId in _runExpanders.Keys.Where(id => !activeRunIds.Contains(id)).ToArray())
            RemoveRunElement(staleId, _runExpanders[staleId]);

        _expandedRuns.RemoveWhere(id => !activeRunIds.Contains(id));
        _collapsedRuns.RemoveWhere(id => !activeRunIds.Contains(id));
    }

    private void ShowEmptyState(string title, string body)
    {
        ActivityRepeater.ItemsSource = null;
        ClearRunElements();
        ActivityScrollViewer.Visibility = Visibility.Collapsed;
        ActivityEmptyStateHost.Content = BuildEmptyState(title, body);
    }

    private void ClearRunElements(bool clearExpansionState = false)
    {
        _runExpanders.Clear();
        _runHeaderSignatures.Clear();
        _runContentSignatures.Clear();
        if (clearExpansionState)
        {
            _expandedRuns.Clear();
            _collapsedRuns.Clear();
        }
    }

    private void RecycleRunElement(UIElement element)
    {
        if (element is not Expander expander || expander.Tag is not Guid runId)
            return;

        RemoveRunElement(runId, expander);
    }

    private void RemoveRunElement(Guid runId, Expander expander)
    {
        if (_runExpanders.TryGetValue(runId, out var cached) && ReferenceEquals(cached, expander))
            _runExpanders.Remove(runId);

        _runHeaderSignatures.Remove(runId);
        _runContentSignatures.Remove(runId);
        expander.Header = null;
        expander.Content = null;
        expander.Tag = null;
    }

    private UIElement BuildEmptyState(string title, string body)
    {
        var stack = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(10, 28, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            TextAlignment = TextAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = TextSecondaryBrush(),
            FontSize = 12,
            LineHeight = 18
        });
        return stack;
    }

    private Expander GetOrUpdateRunExpander(AgentActivityRun run, int index)
    {
        if (_runExpanders.TryGetValue(run.Id, out var expander))
        {
            UpdateRunExpander(expander, run);
            return expander;
        }

        expander = BuildRunExpander(run, index);
        _runExpanders[run.Id] = expander;
        _runHeaderSignatures[run.Id] = RunHeaderSignature(run);
        return expander;
    }

    private void UpdateRunExpander(Expander expander, AgentActivityRun run)
    {
        var signature = RunHeaderSignature(run);
        if (!_runHeaderSignatures.TryGetValue(run.Id, out var current) || current != signature)
        {
            expander.Header = BuildRunHeader(run);
            expander.Background = RunBackgroundBrush(run);
            expander.BorderBrush = run.IsActive ? AccentSubtleBrush() : PanelBorderBrush();
            _runHeaderSignatures[run.Id] = signature;
        }
        UpdateExpandedRunContent(expander, run);
    }

    private Expander BuildRunExpander(AgentActivityRun run, int index)
    {
        var runId = run.Id;
        var isExpanded = ShouldExpand(run, index);
        var expander = new Expander
        {
            Tag = runId,
            IsExpanded = isExpanded,
            Header = BuildRunHeader(run),
            Content = isExpanded ? BuildRunContent(run) : null,
            Background = RunBackgroundBrush(run),
            BorderBrush = run.IsActive ? AccentSubtleBrush() : PanelBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0)
        };
        if (isExpanded)
            _runContentSignatures[run.Id] = RunContentSignature(run);
        expander.Expanding += (_, _) =>
        {
            _expandedRuns.Add(runId);
            _collapsedRuns.Remove(runId);
            RefreshExpandedRunContent(runId, expander);
        };
        expander.Collapsed += (_, _) =>
        {
            _collapsedRuns.Add(runId);
            _expandedRuns.Remove(runId);
        };
        return expander;
    }

    private void UpdateExpandedRunContent(Expander expander, AgentActivityRun run)
    {
        if (!expander.IsExpanded)
            return;

        var signature = RunContentSignature(run);
        if (_runContentSignatures.TryGetValue(run.Id, out var current) && current == signature)
            return;

        expander.Content = BuildRunContent(run);
        _runContentSignatures[run.Id] = signature;
    }

    private void RefreshExpandedRunContent(Guid runId, Expander expander)
    {
        var run = _vm?.AgentActivityRuns.FirstOrDefault(candidate => candidate.Id == runId);
        if (run == null)
            return;

        var signature = RunContentSignature(run);
        if (_runContentSignatures.TryGetValue(run.Id, out var current) && current == signature)
            return;

        expander.Content = BuildRunContent(run);
        _runContentSignatures[run.Id] = signature;
    }

    private static int RunContentSignature(AgentActivityRun run)
    {
        var hash = new HashCode();
        hash.Add(run.StatusText, StringComparer.Ordinal);
        hash.Add(run.TimeText, StringComparer.Ordinal);
        hash.Add(run.ErrorMessage, StringComparer.Ordinal);

        foreach (var task in run.Tasks)
            hash.Add(task);

        foreach (var line in run.Lines)
            hash.Add(line);

        return hash.ToHashCode();
    }

    private static int RunHeaderSignature(AgentActivityRun run) =>
        HashCode.Combine(run.DisplayTitle, run.StatusText, run.TimeText, run.Status, run.IsActive);

    private bool ShouldExpand(AgentActivityRun run, int index)
    {
        if (_expandedRuns.Contains(run.Id))
            return true;
        if (_collapsedRuns.Contains(run.Id))
            return false;
        return run.IsActive || index == 0;
    }

    private UIElement BuildRunHeader(AgentActivityRun run)
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            Padding = new Thickness(10, 9, 8, 9),
            MinWidth = 0
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { Spacing = 4, MinWidth = 0 };
        title.Children.Add(new TextBlock
        {
            Text = run.DisplayTitle,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextPrimaryBrush(),
            MaxLines = 1
        });
        title.Children.Add(new TextBlock
        {
            Text = $"{run.StatusText} · {run.TimeText}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextSecondaryBrush(),
            FontSize = 11,
            LineHeight = 16,
            MaxLines = 2
        });
        grid.Children.Add(title);

        var status = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 3, 7, 3),
            Background = StatusBrush(run.Status),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = StatusLabel(run.Status),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = StatusTextBrush(run.Status)
            }
        };
        Grid.SetColumn(status, 1);
        grid.Children.Add(status);
        return grid;
    }

    private UIElement BuildRunContent(AgentActivityRun run)
    {
        var stack = new StackPanel
        {
            Spacing = IsCompactDensity() ? 8 : 10,
            Padding = new Thickness(10, 0, 10, 10)
        };

        stack.Children.Add(BuildSectionTitle("Run summary"));
        stack.Children.Add(BuildRunSummary(run));

        stack.Children.Add(BuildSectionTitle("Task list"));
        stack.Children.Add(BuildTaskList(run));

        stack.Children.Add(BuildSectionTitle("Tool timeline"));
        if (run.Lines.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "No persisted events for this run.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextSecondaryBrush(),
                FontSize = 12
            });
            return stack;
        }

        // M4.7.0: Tree-draw with proper indentation.
        // M4.9.3: Use per-line Depth so child events (tool result under tool,
        // approval granted under approval) indent under their parent instead
        // of all sharing the same top-level prefix.
        for (int i = 0; i < run.Lines.Count; i++)
        {
            var line = run.Lines[i];
            var isLast = i == run.Lines.Count - 1;
            var depth = Math.Max(0, line.Depth);
            // M4.9.3: Each depth level adds a 3-char "│  " continuation column,
            // then the branch. Root (depth 0): just "├─ "/"└─ " padded to 6.
            // Depth 1: "│  ├─ ". Depth 2: "│  │  ├─ ".
            var cont = depth == 0 ? "" : string.Concat(Enumerable.Repeat("│  ", depth));
            var branch = isLast ? "└─ " : "├─ ";
            var prefix = cont + branch;
            if (depth == 0) prefix = prefix.PadRight(6); // align root width to child width

            string? elapsed = null;
            if (i > 0)
            {
                var prev = run.Lines[i - 1];
                var delta = line.CreatedAt - prev.CreatedAt;
                elapsed = delta.TotalSeconds < 1
                    ? $"{delta.TotalMilliseconds:F0}ms"
                    : $"{delta.TotalSeconds:F1}s";
            }

            stack.Children.Add(BuildActivityLine(line, prefix, elapsed));
        }

        return stack;
    }

    private UIElement BuildSectionTitle(string text) => new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        FontSize = 11,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = TextSecondaryBrush(),
        Margin = new Thickness(0, 4, 0, 0)
    };

    private UIElement BuildRunSummary(AgentActivityRun run)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 7, 9, 7),
            Background = ToolPreviewBackgroundBrush(null),
            BorderBrush = ToolPreviewBorderBrush(null),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = $"{run.StatusText}\n{run.Lines.Count} event{(run.Lines.Count == 1 ? string.Empty : "s")} · {run.TimeText}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextPrimaryBrush(),
                FontSize = IsCompactDensity() ? 12 : 13,
                LineHeight = IsCompactDensity() ? 17 : 19
            }
        };
        return border;
    }

    private UIElement BuildTaskList(AgentActivityRun run)
    {
        if (run.Tasks.Count == 0)
        {
            return new TextBlock
            {
                Text = "No tracked tasks for this run.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextSecondaryBrush(),
                FontSize = 12
            };
        }

        var stack = new StackPanel { Spacing = 6, MinWidth = 0 };
        foreach (var task in run.Tasks.Take(12))
            stack.Children.Add(BuildTaskRow(task));
        if (run.Tasks.Count > 12)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"+ {run.Tasks.Count - 12} more",
                Foreground = TextSecondaryBrush(),
                FontSize = 11
            });
        }
        return stack;
    }

    private UIElement BuildTaskRow(AgentTaskSnapshot task)
    {
        var grid = new Grid
        {
            ColumnSpacing = 7,
            MinWidth = 0
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var status = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 2, 5, 2),
            Background = TaskStatusBrush(task.Status),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = TaskStatusLabel(task.Status),
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = TaskStatusTextBrush(task.Status)
            }
        };
        grid.Children.Add(status);

        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(task.Description)
                ? task.Title
                : $"{task.Title} · {task.Description}",
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 3,
            Foreground = TextPrimaryBrush(),
            FontSize = IsCompactDensity() ? 12 : 13,
            LineHeight = IsCompactDensity() ? 17 : 19
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private UIElement BuildActivityLine(AgentProgressLine line, string treePrefix = "", string? elapsed = null)
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            MinWidth = 0
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // M4.7.0: Tree character + severity tag + elapsed time
        var header = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 5 };
        if (!string.IsNullOrWhiteSpace(treePrefix))
        {
            header.Children.Add(new TextBlock
            {
                Text = treePrefix,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12,
                Foreground = TextSecondaryBrush(),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        var tag = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 3, 6, 3),
            Background = ProgressTagBrush(line.Severity),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = line.Label,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ProgressTagTextBrush(line.Severity)
            }
        };
        header.Children.Add(tag);
        if (elapsed != null)
        {
            header.Children.Add(new TextBlock
            {
                Text = $"+{elapsed}",
                FontSize = 10,
                Foreground = TextSecondaryBrush(),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        grid.Children.Add(header);

        var content = new StackPanel
        {
            Spacing = 5,
            MinWidth = 0
        };
        content.Children.Add(new TextBlock
        {
            Text = line.Text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextPrimaryBrush(),
            FontSize = IsCompactDensity() ? 12 : 13,
            LineHeight = IsCompactDensity() ? 17 : 19
        });

        var preview = BuildToolPreview(line);
        if (preview != null)
            content.Children.Add(preview);

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private UIElement? BuildToolPreview(AgentProgressLine line)
    {
        if (string.IsNullOrWhiteSpace(line.ToolTitle) &&
            string.IsNullOrWhiteSpace(line.Preview) &&
            string.IsNullOrWhiteSpace(line.PrimaryPath))
        {
            return null;
        }

        var stack = new StackPanel { Spacing = 4, MinWidth = 0 };
        var titleText = string.IsNullOrWhiteSpace(line.PrimaryPath)
            ? line.ToolTitle
            : string.IsNullOrWhiteSpace(line.ToolTitle)
                ? line.PrimaryPath
                : $"{line.ToolTitle} · {line.PrimaryPath}";
        if (!string.IsNullOrWhiteSpace(titleText))
        {
            stack.Children.Add(new TextBlock
            {
                Text = titleText,
                Foreground = TextPrimaryBrush(),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2
            });
        }

        if (!string.IsNullOrWhiteSpace(line.Preview))
        {
            stack.Children.Add(new TextBlock
            {
                Text = line.IsTruncated ? $"{line.Preview} [truncated]" : line.Preview,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TextSecondaryBrush(),
                FontSize = 11,
                LineHeight = 16,
                MaxLines = 5
            });
        }

        return new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 7, 9, 7),
            BorderThickness = new Thickness(1),
            BorderBrush = ToolPreviewBorderBrush(line.RenderHint),
            Background = ToolPreviewBackgroundBrush(line.RenderHint),
            Child = stack
        };
    }

    private static string StatusLabel(string status) => status switch
    {
        AgentRunStatuses.Running => "Running",
        AgentRunStatuses.AwaitingApproval => "Review",
        AgentRunStatuses.Completed => "Done",
        AgentRunStatuses.Paused => "Paused",
        AgentRunStatuses.Cancelled => "Stopped",
        AgentRunStatuses.Failed => "Failed",
        _ => "Running"
    };

    private bool IsCompactDensity() => _densityService?.CurrentDensity == UiDensity.Compact;

    private bool IsLightTheme() => ActualTheme == Microsoft.UI.Xaml.ElementTheme.Light;

    private Color ThemeColor(Color light, Color dark) => IsLightTheme() ? light : dark;

    private SolidColorBrush ThemeBrush(Color light, Color dark) => new(ThemeColor(light, dark));

    private SolidColorBrush TextPrimaryBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0x1D, 0x1B, 0x24),
        Color.FromArgb(0xFF, 0xF4, 0xF2, 0xEE)));

    private SolidColorBrush TextSecondaryBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0x63, 0x5E, 0x6E),
        Color.FromArgb(0xFF, 0xE0, 0xE3, 0xEB)));

    private SolidColorBrush AccentSubtleBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0xB5, 0xAE, 0xFF),
        Color.FromArgb(0x99, 0x90, 0x85, 0xFF)));

    private SolidColorBrush PanelBorderBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0xD8, 0xD8, 0xD0),
        Color.FromArgb(0x6E, 0x5D, 0x64, 0x76)));

    private SolidColorBrush RunBackgroundBrush(AgentActivityRun run) => run.IsActive
        ? ThemeBrush(
            Color.FromArgb(0xF8, 0xFC, 0xFA, 0xFF),
            Color.FromArgb(0xE8, 0x1B, 0x1A, 0x29))
        : ThemeBrush(
            Color.FromArgb(0xEE, 0xFF, 0xFE, 0xFC),
            Color.FromArgb(0xCA, 0x11, 0x14, 0x1D));

    private SolidColorBrush StatusBrush(string status)
    {
        return status switch
        {
            AgentRunStatuses.Completed => SuccessSurfaceBrush(),
            AgentRunStatuses.Failed => DangerSurfaceBrush(),
            AgentRunStatuses.Cancelled => NeutralSurfaceBrush(),
            AgentRunStatuses.AwaitingApproval => WarningSurfaceBrush(),
            _ => InfoSurfaceBrush()
        };
    }

    private SolidColorBrush StatusTextBrush(string status)
    {
        return status switch
        {
            AgentRunStatuses.Completed => SuccessTextBrush(),
            AgentRunStatuses.Failed => DangerTextBrush(),
            AgentRunStatuses.AwaitingApproval => WarningTextBrush(),
            _ => InfoTextBrush()
        };
    }

    private SolidColorBrush ProgressTagBrush(string severity)
    {
        return severity switch
        {
            AgentEventSeverities.Error => DangerSurfaceBrush(),
            AgentEventSeverities.Warning => WarningSurfaceBrush(),
            _ => InfoSurfaceBrush()
        };
    }

    private SolidColorBrush ProgressTagTextBrush(string severity)
    {
        return severity switch
        {
            AgentEventSeverities.Error => DangerTextBrush(),
            AgentEventSeverities.Warning => WarningTextBrush(),
            _ => InfoTextBrush()
        };
    }

    private SolidColorBrush TaskStatusBrush(string status)
    {
        return status switch
        {
            AgentTaskStatuses.Completed => SuccessSurfaceBrush(),
            AgentTaskStatuses.Blocked => WarningSurfaceBrush(),
            AgentTaskStatuses.Cancelled => NeutralSurfaceBrush(),
            AgentTaskStatuses.InProgress => InfoSurfaceBrush(),
            _ => NeutralSurfaceBrush()
        };
    }

    private SolidColorBrush TaskStatusTextBrush(string status)
    {
        return status switch
        {
            AgentTaskStatuses.Completed => SuccessTextBrush(),
            AgentTaskStatuses.Blocked => WarningTextBrush(),
            _ => InfoTextBrush()
        };
    }

    private SolidColorBrush SuccessSurfaceBrush() => ThemeBrush(
        Color.FromArgb(0xFF, 0xE0, 0xF6, 0xEF),
        Color.FromArgb(0x20, 0x30, 0x3C, 0x35));

    private SolidColorBrush SuccessTextBrush() => ThemeBrush(
        Color.FromArgb(0xFF, 0x00, 0x8A, 0x78),
        Color.FromArgb(0xFF, 0x48, 0xC6, 0xA3));

    private SolidColorBrush DangerSurfaceBrush() => ThemeBrush(
        Color.FromArgb(0xFF, 0xFF, 0xE8, 0xEC),
        Color.FromArgb(0x3D, 0x6B, 0x26, 0x31));

    private SolidColorBrush DangerTextBrush() => ThemeBrush(
        Color.FromArgb(0xFF, 0xD6, 0x4B, 0x63),
        Color.FromArgb(0xFF, 0xFA, 0x8A, 0x9A));

    private SolidColorBrush WarningSurfaceBrush() => ThemeBrush(
        Color.FromArgb(0xFF, 0xFE, 0xF3, 0xC7),
        Color.FromArgb(0x3D, 0x3D, 0x30, 0x1B));

    private SolidColorBrush WarningTextBrush() => ThemeBrush(
        Color.FromArgb(0xFF, 0xB4, 0x53, 0x09),
        Color.FromArgb(0xFF, 0xE6, 0xBC, 0x62));

    private SolidColorBrush InfoSurfaceBrush() => ThemeBrush(
        Color.FromArgb(0xFF, 0xEA, 0xE8, 0xFF),
        Color.FromArgb(0x23, 0x2A, 0x25, 0x4D));

    private SolidColorBrush InfoTextBrush() => ThemeBrush(
        Color.FromArgb(0xFF, 0x66, 0x5C, 0xD7),
        Color.FromArgb(0xFF, 0x90, 0x85, 0xFF));

    private SolidColorBrush NeutralSurfaceBrush() => ThemeBrush(
        Color.FromArgb(0xFF, 0xFF, 0xFE, 0xFC),
        Color.FromArgb(0xFF, 0x20, 0x25, 0x32));

    private static string TaskStatusLabel(string status) => status switch
    {
        AgentTaskStatuses.InProgress => "Working",
        AgentTaskStatuses.Completed => "Done",
        AgentTaskStatuses.Blocked => "Blocked",
        AgentTaskStatuses.Cancelled => "Dropped",
        _ => "To do"
    };

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
    private sealed class ActivityRunElementFactory : IElementFactory
    {
        private readonly AgentActivityPanelControl _owner;

        public ActivityRunElementFactory(AgentActivityPanelControl owner)
        {
            _owner = owner;
        }

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            if (args.Data is not AgentActivityRun run)
                return new Grid();

            var index = _owner._vm?.AgentActivityRuns.IndexOf(run) ?? 0;
            return _owner.GetOrUpdateRunExpander(run, Math.Max(0, index));
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            // Expansion intent is stored separately; release the generated
            // tree so virtualization can reclaim headers, previews, and brushes.
            _owner.RecycleRunElement(args.Element);
        }
    }
}
