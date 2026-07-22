using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.Json;
using TLAHStudio.App.ViewModels;
using TLAHStudio.App.Views;
using TLAHStudio.App.Views.Dialogs;
using TLAHStudio.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Windows.Storage.Streams;
using Windows.System;
using TLAHStudio.App.Infrastructure;

namespace TLAHStudio.App;

public sealed partial class MainWindow : Window
{
    private bool _firstRunSetupChecked;
    private bool _isNarrowLayout;
    private readonly SemaphoreSlim _contentDialogGate = new(1, 1);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _paneLayoutTimer;
    private WorkbenchPaneKind _appliedPane = WorkbenchPaneKind.Uninitialized;
    private double _appliedPaneWidth = -1;
    private int _capabilityWorkbenchOpen;

    private enum WorkbenchPaneKind
    {
        Uninitialized,
        None,
        Activity,
        ActivityOverlay,
        Review,
        ReviewOverlay
    }

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
    public CapabilityWorkbenchViewModel CapabilityWorkbenchVM { get; }
    public UpdateNotificationViewModel UpdateNotificationVM { get; }
    public IThemeService ThemeService { get; }
    public IBackgroundService BackgroundService { get; }
    public IUiDensityService UiDensityService { get; }
    public IInteractionSoundService SoundService { get; }
    public IAppReleaseService AppReleaseService { get; }
    public ISandboxCommandService SandboxCommandService { get; }

    public WorkspaceReviewViewModel WorkspaceReviewVM { get; }
    public Microsoft.UI.Xaml.ElementTheme CurrentAppTheme => RootGrid.ActualTheme;
    public bool IsWindowActive { get; private set; } = true;
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
        AgentFileDialogViewModel av, PrivacyDataViewModel pv, TeamWorkspaceViewModel twv, ToolPlatformViewModel tpv,
        CapabilityWorkbenchViewModel capabilityWorkbenchVM, UpdateNotificationViewModel uv, IThemeService ts,
        IBackgroundService bg, IUiDensityService density, IInteractionSoundService sound,
        IAppReleaseService release, ISandboxCommandService sandbox, WorkspaceReviewViewModel review)
    {
        ViewModel = mvm; SidebarVM = svm; ChatVM = cvm; DebugVM = dvm;
        SettingsVM = sv; BgSettingsVM = bv; AgentFileVM = av; PrivacyDataVM = pv; TeamWorkspaceVM = twv; ToolPlatformVM = tpv;
        CapabilityWorkbenchVM = capabilityWorkbenchVM;
        UpdateNotificationVM = uv; ThemeService = ts; BackgroundService = bg;
        UiDensityService = density; SoundService = sound; AppReleaseService = release;
        SandboxCommandService = sandbox; WorkspaceReviewVM = review;

        App.Log("MainWindow ctor entered.");
        this.InitializeComponent();
        App.Log("MainWindow XAML initialized.");

        _paneLayoutTimer = DispatcherQueue.CreateTimer();
        _paneLayoutTimer.Interval = TimeSpan.FromMilliseconds(72);
        _paneLayoutTimer.IsRepeating = false;
        _paneLayoutTimer.Tick += (_, _) => UpdateAgentActivityPanelLayout();

        DebugPanelView.Bind(DebugVM);
        ChatPageView.Bind(ChatVM, DebugVM, BackgroundService, UiDensityService, SandboxCommandService, SoundService);
        AgentActivityPanelView.Bind(ChatVM, UiDensityService, SoundService);
        WorkspaceReviewPanelView.Bind(WorkspaceReviewVM, SoundService);
        ChatVM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatPageViewModel.IsAgentActivityPanelOpen))
                DispatcherQueue.TryEnqueue(UpdateAgentActivityPanelLayout);
        };
        WorkspaceReviewVM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WorkspaceReviewViewModel.IsOpen))
                DispatcherQueue.TryEnqueue(UpdateAgentActivityPanelLayout);
        };
        ChatVM.AgentApprovalRequested += OnAgentApprovalRequested;
        // M4.9.5 Phase E2: handle /new and /settings slash commands.
        ChatVM.SlashCommandNavigationRequested += OnSlashCommandNavigation;
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
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            ConfigureTitleBarButtons(appWindow);
            ApplyWorkbenchPanelTheme();
        };

        BackgroundService.ConfigChanged += (_, config) =>
            DispatcherQueue.TryEnqueue(() => _ = ApplyBackgroundConfigAsync(config));
        _ = ApplyBackgroundConfigAsync(BackgroundService.GetConfig());

        RootGrid.Loaded += OnRootGridLoaded;
        RootGrid.SizeChanged += OnRootGridSizeChanged;
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnGlobalKeyDown), true);
        Activated += (_, args) =>
            IsWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        Activated += OnFirst;
        ApplyWorkbenchPanelTheme();
        UpdateAgentActivityPanelLayout();
    }

    /// <summary>
    /// M4.9.5 Phase E2: handle /new and /settings slash commands from the VM.
    /// /new creates a chat via the sidebar VM; /settings opens the settings
    /// dialog (same path as the header settings button).
    /// </summary>
    private async void OnSlashCommandNavigation(object? sender, string target)
    {
        try
        {
            if (target == "new")
            {
                await SidebarVM.CreateChatAsync();
                SoundService.Play(InteractionSound.Navigate);
            }
            else if (target == "settings")
            {
                SoundService.Play(InteractionSound.Navigate);
                await SettingsVM.LoadAsync();
                var dlg = new SettingsContentDialog
                {
                    DataContext = SettingsVM,
                    RequestedTheme = Content is FrameworkElement root
                        ? root.ActualTheme : Microsoft.UI.Xaml.ElementTheme.Default,
                    XamlRoot = RootGrid.XamlRoot
                };
                await TryShowDialogAsync(dlg);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Slash navigation ({target}) failed: {ex}");
        }
    }

    private async void OnAgentApprovalRequested(object? sender, AgentApprovalRequest request)
    {
        try
        {
            // M4.9.0: AskUserQuestion — custom multi-question dialog.
            if (string.Equals(request.ToolName, "ask_user_question", StringComparison.OrdinalIgnoreCase))
            {
                var answers = await ShowAskUserQuestionDialogAsync(request.ArgumentsJson);
                if (answers != null)
                {
                    request.UpdatedArgumentsJson = answers;
                    request.Completion.TrySetResult(AgentApprovalChoice.AllowOnce);
                }
                else
                {
                    request.Completion.TrySetResult(AgentApprovalChoice.DenyOnce);
                }
                return;
            }

            if (string.Equals(request.ToolName, AgentToolNames.ExitPlanMode, StringComparison.OrdinalIgnoreCase))
            {
                await ShowPlanReviewDialogAsync(request);
                return;
            }

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

            var argumentsText = new TextBox
            {
                Text = details,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
                AcceptsReturn = true,
                MinHeight = 100,
                IsReadOnly = true,
                IsSpellCheckEnabled = false
            };
            var argumentsViewer = new ScrollViewer
            {
                Content = argumentsText,
                MinHeight = 120,
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

            var editArguments = new CheckBox
            {
                Content = "Edit tool arguments (advanced)",
                IsChecked = false
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                editArguments,
                "Edit agent tool arguments");
            editArguments.Checked += (_, _) =>
            {
                argumentsText.IsReadOnly = false;
                argumentsText.Focus(FocusState.Programmatic);
            };
            editArguments.Unchecked += (_, _) =>
            {
                argumentsText.IsReadOnly = true;
                argumentsText.Text = details;
            };
            content.Children.Add(editArguments);

            var argumentsError = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DangerBrush"],
                Visibility = Visibility.Collapsed
            };
            content.Children.Add(argumentsError);

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

            dialog.Closing += (_, args) =>
            {
                if (args.Result != ContentDialogResult.Primary)
                    return;

                var candidate = argumentsText.Text;
                try
                {
                    using var document = JsonDocument.Parse(candidate);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("The JSON root must be an object.");
                    argumentsError.Visibility = Visibility.Collapsed;
                }
                catch (JsonException ex)
                {
                    args.Cancel = true;
                    argumentsError.Text = $"Enter a valid JSON object before approving. {ex.Message}";
                    argumentsError.Visibility = Visibility.Visible;
                    argumentsText.Focus(FocusState.Programmatic);
                }
            };

            var result = await TryShowDialogAsync(dialog, waitForTurn: true);
            SoundService.Play(result is ContentDialogResult.Primary or ContentDialogResult.Secondary
                ? InteractionSound.Complete
                : InteractionSound.Error);
            // Only an explicit, valid edit may replace the persisted invocation
            // arguments. The default approval path keeps the original payload.
            var editedArgs = argumentsText.Text;
            if (result == ContentDialogResult.Primary &&
                editArguments.IsChecked == true &&
                !string.Equals(editedArgs, details, StringComparison.Ordinal))
            {
                request.UpdatedArgumentsJson = editedArgs;
            }
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
            // A transient UI failure must never create a persistent global deny
            // rule. Fail closed for this invocation only.
            request.Completion.TrySetResult(AgentApprovalChoice.DenyOnce);
        }
    }

    /// <summary>
    /// Gives plan-mode exits a document review rather than exposing a generic
    /// tool JSON prompt. A denial with feedback is persisted on the invocation
    /// so the runtime returns it to the agent while remaining in plan mode.
    /// </summary>
    private async Task ShowPlanReviewDialogAsync(AgentApprovalRequest request)
    {
        var plan = ExtractPlan(request.ArgumentsJson);
        var content = new StackPanel { Spacing = 12, MaxWidth = 760 };
        content.Children.Add(new TextBlock
        {
            Text = "The agent has finished read-only exploration. Review the proposed implementation before restoring write access.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"]
        });

        var planView = new CommunityToolkit.WinUI.UI.Controls.MarkdownTextBlock
        {
            Text = plan,
            IsTextSelectionEnabled = true,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(0)
        };
        var planSurface = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            MaxHeight = 430,
            Background = (Brush)Application.Current.Resources["InputBackgroundBrush"],
            BorderBrush = (Brush)Application.Current.Resources["BorderSubtleBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new ScrollViewer
            {
                Content = planView,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(planSurface, "Implementation plan");
        content.Children.Add(planSurface);

        content.Children.Add(new TextBlock
        {
            Text = "Feedback for another planning pass (optional)",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"]
        });
        var feedback = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
            MaxHeight = 140,
            PlaceholderText = "For example: use the safer migration path and add a regression test.",
            Background = (Brush)Application.Current.Resources["InputBackgroundBrush"],
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["BorderSubtleBrush"],
            CornerRadius = new CornerRadius(8)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(feedback, "Plan feedback");
        content.Children.Add(feedback);

        var dialog = new ContentDialog
        {
            Title = "Review implementation plan",
            Content = content,
            PrimaryButtonText = "Approve and continue",
            SecondaryButtonText = "Keep planning",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (Content is FrameworkElement root)
            dialog.RequestedTheme = root.ActualTheme;

        var result = await TryShowDialogAsync(dialog, waitForTurn: true);
        SoundService.Play(result is ContentDialogResult.Primary or ContentDialogResult.Secondary
            ? InteractionSound.Complete
            : InteractionSound.Error);
        if (result == ContentDialogResult.Primary)
        {
            request.Completion.TrySetResult(AgentApprovalChoice.AllowOnce);
            return;
        }

        if (result == ContentDialogResult.Secondary && !string.IsNullOrWhiteSpace(feedback.Text))
        {
            request.UpdatedArgumentsJson = JsonSerializer.Serialize(new
            {
                plan,
                feedback = feedback.Text.Trim()
            });
        }
        request.Completion.TrySetResult(AgentApprovalChoice.DenyOnce);
    }

    private static string ExtractPlan(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.TryGetProperty("plan", out var plan) &&
                plan.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(plan.GetString()))
            {
                return plan.GetString()!.Trim();
            }
        }
        catch (JsonException)
        {
            // The approval flow still presents a readable state below.
        }

        return "The agent did not provide a readable plan. Keep planning and ask it to submit a complete implementation plan.";
    }

    /// <summary>
    /// M4.9.0: Render a multi-question dialog for AskUserQuestion.
    /// Returns a JSON string with the answers, or null if the user cancelled.
    /// </summary>
    private static async Task<string?> ShowAskUserQuestionDialogAsync(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (!doc.RootElement.TryGetProperty("questions", out var questionsEl) ||
                questionsEl.ValueKind != JsonValueKind.Array)
                return null;

            var content = new StackPanel { Spacing = 16, MaxWidth = 600 };
            var answers = new Dictionary<string, object>();

            foreach (var q in questionsEl.EnumerateArray())
            {
                var question = q.GetProperty("question").GetString() ?? "";
                var header = q.TryGetProperty("header", out var h) ? h.GetString() ?? "" : "";
                var multiSelect = q.TryGetProperty("multiSelect", out var ms) && ms.GetBoolean();
                var options = q.GetProperty("options");

                var qPanel = new StackPanel { Spacing = 6 };
                qPanel.Children.Add(new TextBlock
                {
                    Text = question,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"]
                });

                var selectedLabels = new List<string>();
                foreach (var opt in options.EnumerateArray())
                {
                    var label = opt.GetProperty("label").GetString() ?? "";
                    var desc = opt.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    var preview = opt.TryGetProperty("preview", out var pv) ? pv.GetString() ?? "" : "";

                    // M4.9.2: Option container holds the selector + an optional
                    // monospace preview box beneath it.
                    var optPanel = new StackPanel { Spacing = 4 };
                    string selectorContent = string.IsNullOrEmpty(desc) ? label : $"{label} — {desc}";

                    if (multiSelect)
                    {
                        var cb = new CheckBox
                        {
                            Content = selectorContent,
                            Tag = label,
                            MinHeight = 30
                        };
                        cb.Checked += (_, _) => { if (cb.Tag is string l) selectedLabels.Add(l); };
                        cb.Unchecked += (_, _) => { if (cb.Tag is string l) selectedLabels.Remove(l); };
                        optPanel.Children.Add(cb);
                    }
                    else
                    {
                        var rb = new RadioButton
                        {
                            Content = selectorContent,
                            Tag = label,
                            MinHeight = 30,
                            GroupName = header
                        };
                        optPanel.Children.Add(rb);
                    }

                    if (!string.IsNullOrWhiteSpace(preview))
                    {
                        var previewText = preview.Length > 500 ? preview[..500] + "\n[preview truncated]" : preview;
                        var box = new Border
                        {
                            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(8),
                            Margin = new Thickness(20, 0, 0, 0),
                            Opacity = 0.85,
                            Child = new ScrollViewer
                            {
                                MaxHeight = 160,
                                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                Content = new TextBlock
                                {
                                    Text = previewText,
                                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                                    TextWrapping = TextWrapping.Wrap,
                                    IsTextSelectionEnabled = true
                                }
                            }
                        };
                        optPanel.Children.Add(box);
                    }
                    qPanel.Children.Add(optPanel);
                }
                content.Children.Add(qPanel);
                answers[header] = multiSelect ? selectedLabels : ""; // will be filled on submit
            }

            var dialog = new ContentDialog
            {
                Title = "Answer Questions",
                Content = new ScrollViewer
                {
                    Content = content,
                    MaxHeight = 480,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                },
                PrimaryButtonText = "Submit",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = App.MainWindow?.Content?.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return null;

            // Collect final answers after dialog closes.
            var finalAnswers = new Dictionary<string, object>();
            foreach (var child in content.Children)
            {
                if (child is StackPanel qp && qp.Children.Count > 0 &&
                    qp.Children[0] is TextBlock tb)
                {
                    var qHeader = tb.Text; // use question text as key
                    var multiAnswer = new List<string>();
                    string? singleAnswer = null;
                    foreach (var c in qp.Children)
                    {
                        // M4.9.2: Each option is now wrapped in an optPanel
                        // (StackPanel) that holds the selector and optional
                        // preview box. Descend into it to find the selector.
                        var selectorHost = c is StackPanel sp ? sp : c;
                        foreach (var inner in (selectorHost is StackPanel s ? s.Children : Enumerable.Empty<UIElement>()))
                        {
                            if (inner is CheckBox cb && cb.IsChecked == true)
                                multiAnswer.Add(cb.Tag?.ToString() ?? "");
                            else if (inner is RadioButton rb && rb.IsChecked == true)
                                singleAnswer = rb.Tag?.ToString() ?? "";
                        }
                    }
                    if (multiAnswer.Count > 0)
                        finalAnswers[qHeader] = string.Join(", ", multiAnswer);
                    else if (singleAnswer != null)
                        finalAnswers[qHeader] = singleAnswer;
                }
            }

            return JsonSerializer.Serialize(new { answers = finalAnswers });
        }
        catch (Exception ex)
        {
            App.Log($"AskUserQuestion dialog failed: {ex}");
            return null;
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
        if (_isNarrowLayout != isNarrow)
        {
            _isNarrowLayout = isNarrow;
            SidebarView.SetResponsiveCompact(isNarrow);
        }

        // WinUI emits SizeChanged for every resize pixel. Coalesce those events
        // so pane visibility and GridLength are committed once per settled frame
        // instead of repeatedly collapsing and rebuilding the workbench.
        _paneLayoutTimer.Stop();
        _paneLayoutTimer.Start();
    }

    private async void OnGlobalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsKeyDown(VirtualKey.Control) ||
                   IsKeyDown(VirtualKey.LeftControl) ||
                   IsKeyDown(VirtualKey.RightControl);

        if (ctrl && IsKeyDown(VirtualKey.Shift) && e.Key == VirtualKey.D)
        {
            e.Handled = true;
            ToggleWorkspaceReviewPanel(true);
            SoundService.Play(InteractionSound.Navigate);
            return;
        }

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

        // M4.9.6: Ctrl+K / Ctrl+Shift+P — command palette.
        if ((ctrl && e.Key == VirtualKey.K) ||
            (ctrl && IsKeyDown(VirtualKey.Shift) && e.Key == VirtualKey.P))
        {
            e.Handled = true;
            _ = ShowCommandPaletteAsync();
            return;
        }
    }

    /// <summary>
    /// M4.9.6: Ctrl+K command palette. Aggregates built-in chat actions and
    /// slash commands from ISlashCommandProvider; shows a searchable dialog;
    /// dispatches the selected command.
    /// </summary>
    private async Task ShowCommandPaletteAsync()
    {
        try
        {
            var cmds = new List<PaletteCommand>
            {
                new("New Chat", "Start a new conversation", "Chat", "new", 0),
                new("Search Chats", "Focus the sidebar search", "Chat", "focus-search", 0),
                new("Toggle Theme", "Switch between light and dark", "App", "toggle-theme", 10),
                new("Open Settings", "Configure provider, model, keys", "App", "settings", 20),
                new("Create & Research", "Research evidence and create spreadsheets, documents, and diagrams", "App", "workbench", 15),
                new("Changes & Preview", "Review local Git changes", "Workspace", "changes", 10),
                new("Clear Conversation", "Remove all messages", "Chat", "clear", 30),
                new("Toggle Agent Mode", "Enable or disable agent", "Chat", "agent", 30),
                new("Stop Generation", "Cancel the running request", "Chat", "stop", 30),

                new("Permissions: Full Access", "Allow all tools without prompts", "Permissions", "perm:bypass", 40),
                new("Permissions: Auto Approve", "Auto-approve safe tools", "Permissions", "perm:auto", 40),
                new("Permissions: Ask Approval", "Prompt for each tool", "Permissions", "perm:ask", 40),
                new("Permissions: Plan Mode", "Read-only exploration", "Permissions", "perm:plan", 40),
            };

            // Append slash-command sources (skills, agent tools, MCP tools).
            try
            {
                await using var scope = App.Services.CreateAsyncScope();
                var slashProvider = scope.ServiceProvider.GetService<ISlashCommandProvider>();
                if (slashProvider != null)
                {
                    var chatId = ChatVM.CurrentChat?.Id ?? Guid.Empty;
                    var slashCmds = await slashProvider.GetCommandsAsync(chatId);
                    if (slashCmds != null)
                    {
                        foreach (var sc in slashCmds)
                        {
                            var cat = sc.Category switch { "Skill" => "Skill", "Tool" => "Tool", "MCP" => "MCP", _ => "Slash" };
                            cmds.Add(new PaletteCommand($"/{sc.Name}", sc.Description, cat, $"slash:{sc.Name}", 50));
                        }
                    }
                }
            }
            catch { /* slash provider unavailable — palette works with built-ins only */ }

            SoundService.Play(InteractionSound.Navigate);
            var palette = new TLAHStudio.App.Views.Dialogs.CommandPalette();
            palette.SetCommands(cmds);
            palette.RequestedTheme = Content is FrameworkElement root
                ? root.ActualTheme : Microsoft.UI.Xaml.ElementTheme.Default;
            palette.XamlRoot = RootGrid.XamlRoot;
            var result = await TryShowDialogAsync(palette);
            var selected = palette.SelectedCommand;
            if (result != ContentDialogResult.Primary || selected == null)
                return;

            await DispatchPaletteCommand(selected.Action);
        }
        catch (Exception ex)
        {
            App.Log($"CommandPalette failed: {ex}");
        }
    }

    private async Task DispatchPaletteCommand(string action)
    {
        switch (action)
        {
            case "new":
                await SidebarVM.CreateChatAsync();
                MessageInputView.FocusMessageInput();
                break;
            case "focus-search":
                SidebarView.FocusSearch();
                break;
            case "toggle-theme":
                ThemeService.ToggleTheme();
                break;
            case "settings":
                {
                    await SettingsVM.LoadAsync();
                    var dlg = new SettingsContentDialog
                    {
                        DataContext = SettingsVM,
                        RequestedTheme = Content is FrameworkElement root
                            ? root.ActualTheme : Microsoft.UI.Xaml.ElementTheme.Default,
                        XamlRoot = RootGrid.XamlRoot
                    };
                    await TryShowDialogAsync(dlg);
                }
                break;
            case "workbench":
                await OpenCapabilityWorkbenchAsync();
                break;
            case "clear":
                ChatVM.Messages.Clear();
                ChatVM.ErrorMessage = null;
                break;
            case "changes":
                ToggleWorkspaceReviewPanel(true);
                break;
            case "agent":
                ChatVM.IsAgentModeEnabled = !ChatVM.IsAgentModeEnabled;
                break;
            case "stop":
                ChatVM.StopSendingCommand.Execute(null);
                break;
            case "perm:bypass":
                ChatVM.SelectedAgentPermissionMode = AgentPermissionModes.BypassPermissions;
                break;
            case "perm:auto":
                ChatVM.SelectedAgentPermissionMode = AgentPermissionModes.AutoApprove;
                break;
            case "perm:ask":
                ChatVM.SelectedAgentPermissionMode = AgentPermissionModes.RequestApproval;
                break;
            case "perm:plan":
                ChatVM.SelectedAgentPermissionMode = AgentPermissionModes.Plan;
                break;
            default:
                // Slash command — dispatch through MessageInputControl input.
                if (action.StartsWith("slash:", StringComparison.Ordinal))
                {
                    var slashName = action[6..];
                    MessageInputView.InjectSlashCommand($"/{slashName}");
                }
                break;
        }
    }

    private async Task OpenLatestInspectorAsync()
    {
        var turnId = ChatVM.Messages.LastOrDefault(m => m.TurnId != null)?.TurnId;
        if (turnId != null)
            await DebugVM.OpenDebugAsync(turnId.Value);
    }

    public void FocusMessageInput() => MessageInputView.FocusMessageInput();

    public async Task OpenCapabilityWorkbenchAsync(string? page = null)
    {
        if (Interlocked.Exchange(ref _capabilityWorkbenchOpen, 1) != 0)
            return;

        try
        {
            if (!CapabilityWorkbenchVM.HasCurrentChat)
            {
                await SidebarVM.LoadChatsAsync();
                if (SidebarVM.Chats.FirstOrDefault() is { } existingChat)
                    SidebarVM.SelectedChat = existingChat;
                else
                    await SidebarVM.CreateChatAsync();
            }

            SoundService.Play(InteractionSound.Navigate);
            var dialog = new CapabilityWorkbenchDialog
            {
                DataContext = CapabilityWorkbenchVM,
                RequestedTheme = Content is FrameworkElement root
                    ? root.ActualTheme
                    : Microsoft.UI.Xaml.ElementTheme.Default,
                XamlRoot = RootGrid.XamlRoot
            };
            if (Content is FrameworkElement host)
                dialog.PrepareForHost(host.ActualWidth, host.ActualHeight);
            if (!string.IsNullOrWhiteSpace(page))
                dialog.SelectPage(page);
            await TryShowDialogAsync(dialog, waitForTurn: true);
        }
        finally
        {
            Volatile.Write(ref _capabilityWorkbenchOpen, 0);
        }
    }

    public void ToggleAgentActivityPanel(bool? open = null)
    {
        var shouldOpen = open ?? !ChatVM.IsAgentActivityPanelOpen;
        ChatVM.IsAgentActivityPanelOpen = shouldOpen;
        if (shouldOpen)
            WorkspaceReviewVM.IsOpen = false;
        UpdateAgentActivityPanelLayout();
    }

    public void ToggleWorkspaceReviewPanel(bool? open = null)
    {
        var shouldOpen = open ?? !WorkspaceReviewVM.IsOpen;
        WorkspaceReviewVM.IsOpen = shouldOpen;
        if (shouldOpen)
        {
            ChatVM.IsAgentActivityPanelOpen = false;
            _ = WorkspaceReviewVM.RefreshAsync();
        }
        UpdateAgentActivityPanelLayout();
    }

    private void UpdateAgentActivityPanelLayout()
    {
        if (AgentActivityColumn == null || WorkspaceReviewColumn == null ||
            AgentActivityPanelView == null || WorkspaceReviewPanelView == null)
            return;

        var availableWidth = WorkbenchGrid.ActualWidth;
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
            availableWidth = Math.Max(0, RootGrid.ActualWidth - SidebarView.ActualWidth);

        var targetPane = WorkbenchPaneKind.None;
        var targetWidth = 0d;
        if (availableWidth >= 780 && WorkspaceReviewVM.IsOpen)
        {
            var reviewWidth = Math.Clamp(availableWidth * 0.38, 420, 560);
            if (availableWidth - reviewWidth >= 520)
            {
                targetPane = WorkbenchPaneKind.Review;
                targetWidth = reviewWidth;
            }
        }
        else if (availableWidth >= 780 && ChatVM.IsAgentActivityPanelOpen)
        {
            var activityWidth = Math.Clamp(availableWidth * 0.30, 320, 420);
            if (availableWidth - activityWidth < 520)
                activityWidth = Math.Max(300, availableWidth - 520);

            if (activityWidth >= 300)
            {
                targetPane = WorkbenchPaneKind.Activity;
                targetWidth = activityWidth;
            }
        }

        if (targetPane == WorkbenchPaneKind.None && WorkspaceReviewVM.IsOpen && availableWidth >= 420)
        {
            targetPane = WorkbenchPaneKind.ReviewOverlay;
            targetWidth = Math.Min(560, Math.Max(420, availableWidth - 16));
        }
        else if (targetPane == WorkbenchPaneKind.None && ChatVM.IsAgentActivityPanelOpen && availableWidth >= 300)
        {
            targetPane = WorkbenchPaneKind.ActivityOverlay;
            targetWidth = Math.Min(420, Math.Max(300, availableWidth - 16));
        }

        if (_appliedPane == targetPane && Math.Abs(_appliedPaneWidth - targetWidth) < 1)
            return;

        var previousPane = _appliedPane;
        ApplyWorkbenchPanelTheme();

        var showsActivity = targetPane is WorkbenchPaneKind.Activity or WorkbenchPaneKind.ActivityOverlay;
        var showsReview = targetPane is WorkbenchPaneKind.Review or WorkbenchPaneKind.ReviewOverlay;
        AgentActivityPanelView.SetPanelActive(showsActivity);

        if (!showsActivity)
        {
            AgentActivityPanelView.Visibility = Visibility.Collapsed;
            AgentActivityColumn.Width = new GridLength(0);
            ConfigureSidePane(AgentActivityPanelView, canonicalColumn: 1);
            NocturneMotion.Reset(AgentActivityPanelView);
        }

        if (!showsReview)
        {
            WorkspaceReviewPanelView.Visibility = Visibility.Collapsed;
            WorkspaceReviewColumn.Width = new GridLength(0);
            ConfigureSidePane(WorkspaceReviewPanelView, canonicalColumn: 2);
            NocturneMotion.Reset(WorkspaceReviewPanelView);
        }

        if (targetPane == WorkbenchPaneKind.Activity)
        {
            ConfigureSidePane(AgentActivityPanelView, canonicalColumn: 1);
            AgentActivityColumn.Width = new GridLength(targetWidth);
            AgentActivityPanelView.Visibility = Visibility.Visible;
            if (previousPane != WorkbenchPaneKind.Activity)
                NocturneMotion.RevealOverlay(AgentActivityPanelView);
        }
        else if (targetPane == WorkbenchPaneKind.ActivityOverlay)
        {
            AgentActivityColumn.Width = new GridLength(0);
            ConfigureOverlayPane(AgentActivityPanelView, targetWidth);
            AgentActivityPanelView.Visibility = Visibility.Visible;
            if (previousPane != WorkbenchPaneKind.ActivityOverlay)
                NocturneMotion.RevealOverlay(AgentActivityPanelView);
        }
        else if (targetPane == WorkbenchPaneKind.Review)
        {
            ConfigureSidePane(WorkspaceReviewPanelView, canonicalColumn: 2);
            WorkspaceReviewColumn.Width = new GridLength(targetWidth);
            WorkspaceReviewPanelView.Visibility = Visibility.Visible;
            if (previousPane != WorkbenchPaneKind.Review)
                NocturneMotion.RevealOverlay(WorkspaceReviewPanelView);
        }
        else if (targetPane == WorkbenchPaneKind.ReviewOverlay)
        {
            WorkspaceReviewColumn.Width = new GridLength(0);
            ConfigureOverlayPane(WorkspaceReviewPanelView, targetWidth);
            WorkspaceReviewPanelView.Visibility = Visibility.Visible;
            if (previousPane != WorkbenchPaneKind.ReviewOverlay)
                NocturneMotion.RevealOverlay(WorkspaceReviewPanelView);
        }

        _appliedPane = targetPane;
        _appliedPaneWidth = targetWidth;
    }

    private static void ConfigureSidePane(FrameworkElement pane, int canonicalColumn)
    {
        Grid.SetColumn(pane, canonicalColumn);
        Grid.SetColumnSpan(pane, 1);
        pane.HorizontalAlignment = HorizontalAlignment.Stretch;
        pane.Width = double.NaN;
        Canvas.SetZIndex(pane, 0);
    }

    private static void ConfigureOverlayPane(FrameworkElement pane, double width)
    {
        Grid.SetColumn(pane, 0);
        Grid.SetColumnSpan(pane, 3);
        pane.HorizontalAlignment = HorizontalAlignment.Right;
        pane.Width = width;
        Canvas.SetZIndex(pane, 20);
    }

    private void ApplyWorkbenchPanelTheme()
    {
        if (AgentActivityPanelView == null || WorkspaceReviewPanelView == null || RootGrid == null)
            return;

        // Activity content is generated in code, so it cannot rely on a
        // collapsed UserControl re-inheriting the root's theme on its own.
        // Pin it to the same effective theme as the workbench before it is
        // shown; ActualThemeChanged then refreshes its generated brushes.
        var theme = RootGrid.ActualTheme;
        AgentActivityPanelView.RequestedTheme = theme;
        WorkspaceReviewPanelView.RequestedTheme = theme;
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
        titleBar.ButtonHoverBackgroundColor = isLight
            ? Microsoft.UI.ColorHelper.FromArgb(0x16, 0x0F, 0x17, 0x24)
            : Microsoft.UI.ColorHelper.FromArgb(0x2A, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonPressedBackgroundColor = isLight
            ? Microsoft.UI.ColorHelper.FromArgb(0x24, 0x0F, 0x17, 0x24)
            : Microsoft.UI.ColorHelper.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonForegroundColor = isLight
            ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x12, 0x1A, 0x28)
            : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonInactiveForegroundColor = isLight
            ? Microsoft.UI.ColorHelper.FromArgb(0xAA, 0x12, 0x1A, 0x28)
            : Microsoft.UI.ColorHelper.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
        titleBar.ButtonPressedForegroundColor = titleBar.ButtonForegroundColor;
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
