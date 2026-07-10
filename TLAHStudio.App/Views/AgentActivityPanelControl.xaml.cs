using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLAHStudio.App.ViewModels;
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
    private readonly HashSet<Guid> _expandedRuns = new();
    private readonly HashSet<Guid> _collapsedRuns = new();
    private readonly Dictionary<Guid, Expander> _runExpanders = new();
    private readonly Dictionary<Guid, string> _runContentSignatures = new();

    public AgentActivityPanelControl()
    {
        App.Log("AgentActivityPanelControl ctor entered.");
        InitializeComponent();
        App.Log("AgentActivityPanelControl XAML initialized.");
        ActualThemeChanged += (_, _) => RequestRender(immediate: true);
    }

    public void Bind(
        ChatPageViewModel vm,
        IUiDensityService densityService,
        IInteractionSoundService sound)
    {
        _vm = vm;
        _densityService = densityService;
        _sound = sound;
        _vm.AgentActivityChanged += (_, _) => RequestRender();
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ChatPageViewModel.CurrentChat)
                or nameof(ChatPageViewModel.AgentActivitySummary)
                or nameof(ChatPageViewModel.HasAgentActivity))
            {
                RequestRender();
            }
        };
        _densityService.DensityChanged += (_, _) => DispatcherQueue.TryEnqueue(() => RequestRender(immediate: true));
        RequestRender(immediate: true);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        _sound?.Play(InteractionSound.Toggle);
        if (App.MainWindow is MainWindow window)
            window.ToggleAgentActivityPanel(false);
    }

    private void RequestRender(bool immediate = false)
    {
        if (immediate)
        {
            DispatcherQueue.TryEnqueue(Render);
            return;
        }

        if (_renderQueued)
            return;

        _renderQueued = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(40);
            DispatcherQueue.TryEnqueue(() =>
            {
                _renderQueued = false;
                Render();
            });
        });
    }

    private void Render()
    {
        if (_vm == null)
            return;

        SummaryText.Text = _vm.AgentActivitySummary;

        if (_vm.CurrentChat == null)
        {
            ClearRunElements();
            ActivityStack.Children.Add(BuildEmptyState("Select a chat", "Agent runs for the selected conversation will appear here."));
            return;
        }

        if (!_vm.HasAgentActivity)
        {
            ClearRunElements();
            ActivityStack.Children.Add(BuildEmptyState("No agent runs yet", "Start Agent mode to build a persistent execution trail."));
            return;
        }

        RemoveEmptyStateIfNeeded();
        var activeRunIds = new HashSet<Guid>();
        for (var i = 0; i < _vm.AgentActivityRuns.Count; i++)
        {
            var run = _vm.AgentActivityRuns[i];
            activeRunIds.Add(run.Id);
            var expander = GetOrUpdateRunExpander(run, i);
            if (ActivityStack.Children.Count <= i)
            {
                ActivityStack.Children.Add(expander);
            }
            else if (!ReferenceEquals(ActivityStack.Children[i], expander))
            {
                if (ActivityStack.Children.Contains(expander))
                    ActivityStack.Children.Remove(expander);
                ActivityStack.Children.Insert(i, expander);
            }
        }

        for (var i = ActivityStack.Children.Count - 1; i >= _vm.AgentActivityRuns.Count; i--)
            ActivityStack.Children.RemoveAt(i);

        foreach (var staleId in _runExpanders.Keys.Where(id => !activeRunIds.Contains(id)).ToArray())
        {
            _runExpanders.Remove(staleId);
            _runContentSignatures.Remove(staleId);
        }
    }

    private void ClearRunElements()
    {
        _runExpanders.Clear();
        _runContentSignatures.Clear();
        ActivityStack.Children.Clear();
    }

    private void RemoveEmptyStateIfNeeded()
    {
        if (_runExpanders.Count == 0)
            ActivityStack.Children.Clear();
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
            expander.Header = BuildRunHeader(run);
            UpdateExpandedRunContent(expander, run);
            expander.Background = RunBackgroundBrush(run);
            expander.BorderBrush = run.IsActive ? AccentSubtleBrush() : PanelBorderBrush();
            return expander;
        }

        expander = BuildRunExpander(run, index);
        _runExpanders[run.Id] = expander;
        return expander;
    }

    private Expander BuildRunExpander(AgentActivityRun run, int index)
    {
        var isExpanded = ShouldExpand(run, index);
        var expander = new Expander
        {
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
            _expandedRuns.Add(run.Id);
            _collapsedRuns.Remove(run.Id);
            RefreshExpandedRunContent(run.Id, expander);
        };
        expander.Collapsed += (_, _) =>
        {
            _collapsedRuns.Add(run.Id);
            _expandedRuns.Remove(run.Id);
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

    private static string RunContentSignature(AgentActivityRun run)
    {
        var last = run.Lines.LastOrDefault();
        return $"{run.Status}|{run.TimeText}|{run.Tasks.Count}|{run.Lines.Count}|{last?.SequenceNumber}|{last?.Text}";
    }

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
        Color.FromArgb(0xFF, 0x17, 0x20, 0x33),
        Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));

    private SolidColorBrush TextSecondaryBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0x56, 0x65, 0x7A),
        Color.FromArgb(0xFF, 0xE0, 0xE8, 0xF4)));

    private SolidColorBrush AccentSubtleBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0xBF, 0xD2, 0xFF),
        Color.FromArgb(0x99, 0x71, 0xA7, 0xFF)));

    private SolidColorBrush PanelBorderBrush() => new(ThemeColor(
        Color.FromArgb(0xFF, 0xD2, 0xDC, 0xEB),
        Color.FromArgb(0x6E, 0x71, 0x81, 0x95)));

    private SolidColorBrush RunBackgroundBrush(AgentActivityRun run) => run.IsActive
        ? ThemeBrush(
            Color.FromArgb(0xF8, 0xFA, 0xFD, 0xFF),
            Color.FromArgb(0xE8, 0x12, 0x1B, 0x2B))
        : ThemeBrush(
            Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(0xCA, 0x0E, 0x16, 0x24));

    // M4.9.4: Status brushes now resolve from theme tokens (Success/Warning/
    // Info/Danger) instead of hardcoded hex, so they honor theme switching.
    private SolidColorBrush StatusBrush(string status)
    {
        var key = status switch
        {
            AgentRunStatuses.Completed => "SuccessSurfaceBrush",
            AgentRunStatuses.Failed => "DangerSurfaceBrush",
            AgentRunStatuses.Cancelled => "SurfaceElevatedBrush",
            AgentRunStatuses.AwaitingApproval => "WarningSurfaceBrush",
            _ => "InfoSurfaceBrush"
        };
        return (SolidColorBrush)Application.Current.Resources[key];
    }

    private SolidColorBrush StatusTextBrush(string status)
    {
        var key = status switch
        {
            AgentRunStatuses.Completed => "SuccessBrush",
            AgentRunStatuses.Failed => "DangerBrush",
            AgentRunStatuses.AwaitingApproval => "WarningBrush",
            _ => "InfoBrush"
        };
        return (SolidColorBrush)Application.Current.Resources[key];
    }

    private SolidColorBrush ProgressTagBrush(string severity)
    {
        var key = severity switch
        {
            AgentEventSeverities.Error => "DangerSurfaceBrush",
            AgentEventSeverities.Warning => "WarningSurfaceBrush",
            _ => "InfoSurfaceBrush"
        };
        return (SolidColorBrush)Application.Current.Resources[key];
    }

    private SolidColorBrush ProgressTagTextBrush(string severity)
    {
        var key = severity switch
        {
            AgentEventSeverities.Error => "DangerBrush",
            AgentEventSeverities.Warning => "WarningBrush",
            _ => "InfoBrush"
        };
        return (SolidColorBrush)Application.Current.Resources[key];
    }

    private SolidColorBrush TaskStatusBrush(string status)
    {
        var key = status switch
        {
            AgentTaskStatuses.Completed => "SuccessSurfaceBrush",
            AgentTaskStatuses.Blocked => "WarningSurfaceBrush",
            AgentTaskStatuses.Cancelled => "SurfaceElevatedBrush",
            AgentTaskStatuses.InProgress => "InfoSurfaceBrush",
            _ => "SurfaceBrush"
        };
        return (SolidColorBrush)Application.Current.Resources[key];
    }

    private SolidColorBrush TaskStatusTextBrush(string status)
    {
        var key = status switch
        {
            AgentTaskStatuses.Completed => "SuccessBrush",
            AgentTaskStatuses.Blocked => "WarningBrush",
            _ => "InfoBrush"
        };
        return (SolidColorBrush)Application.Current.Resources[key];
    }

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
}
