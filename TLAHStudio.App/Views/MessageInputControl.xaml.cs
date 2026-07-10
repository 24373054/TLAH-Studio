using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;
using TLAHStudio.Core.Services;
using TLAHStudio.App.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace TLAHStudio.App.Views;

public sealed partial class MessageInputControl : UserControl
{
    private ChatPageViewModel? _vm;
    private bool _loaded;
    private bool _suppressSound;

    // M4.9.5 Phase E1: command history stack + navigation index.
    // _historyIndex == -1 means "not navigating history" (current draft).
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string _draftBeforeHistory = string.Empty;
    private const int MaxHistoryEntries = 100;

    // M4.9.5 Phase E2: slash command completion. _allCommands is the cached
    // full list (refreshed lazily); _slashToken is the /-prefix being typed.
    private IReadOnlyList<SlashCommand>? _allCommands;
    private bool _commandsLoading;
    private DateTime _commandsLoadedAt;
    private const double CommandsCacheSeconds = 30;

    public MessageInputControl()
    {
        App.Log("MessageInputControl ctor entered.");
        InitializeComponent();
        App.Log("MessageInputControl XAML initialized.");
        InputBox.PreviewKeyDown += OnPreviewKeyDown;
        InputBox.KeyDown += OnKeyDown;
        InputBox.TextChanged += OnTextChanged;
        InputRoot.SizeChanged += OnInputRootSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object s, RoutedEventArgs e)
    {
        if (_loaded)
            return;

        _loaded = true;
        var w = App.MainWindow as MainWindow;
        if (w != null)
        {
            _vm = w.ChatVM;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _suppressSound = true;
            UpdateRoleButton();
            SelectPermissionMode(_vm.SelectedAgentPermissionMode);
            UpdateWorkspaceButton();
            _suppressSound = false;
            UpdateSendingState();
            UpdateAgentModeVisualState();
            UpdatePermissionModeButton();
            UpdatePlaceholder();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = null;
        _loaded = false;
    }

    private void OnInputRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 620;
        InputRoot.Padding = compact
            ? new Thickness(12, 10, 12, 10)
            : new Thickness(20, 14, 20, 14);
        var controlMargin = compact ? new Thickness(0, 0, 8, 0) : new Thickness(0, 0, 10, 0);
        RoleButton.Margin = controlMargin;
        AgentModeButton.Margin = controlMargin;
        WorkspaceButton.Margin = controlMargin;
        PermissionModeButton.Margin = controlMargin;
        ActionGrid.Margin = compact
            ? new Thickness(8, 0, 0, 0)
            : new Thickness(12, 0, 0, 0);
        SendBtn.MinWidth = compact ? 44 : 54;
        StopBtn.MinWidth = compact ? 44 : 54;
        InputBox.Padding = compact
            ? new Thickness(12, 10, 12, 10)
            : new Thickness(14, 10, 14, 10);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // M4.9.5 Phase E2: when the slash popup is open, intercept navigation
        // keys (Tab/Enter accept, Up/Down move selection, Esc dismiss).
        if (SlashPopup.IsOpen && HandleSlashPopupKeys(e))
            return;
        // M4.9.5 Phase E1: Up/Down arrow navigates command history when the
        // caret is on the first/last line of a multi-line input (so plain
        // arrow movement still works mid-text).
        if (HandleHistoryNavigation(e))
            return;
        HandleEnterKey(e);
    }

    private bool HandleSlashPopupKeys(KeyRoutedEventArgs e)
    {
        if (e.Handled) return false;
        var key = e.Key;
        if (key == Windows.System.VirtualKey.Escape)
        {
            HideSlashPopup();
            e.Handled = true;
            return true;
        }
        if (key is Windows.System.VirtualKey.Tab or Windows.System.VirtualKey.Enter)
        {
            if (SlashList.SelectedItem is SlashCommand cmd)
                AcceptSlashCommand(cmd);
            else
                HideSlashPopup();
            e.Handled = true;
            return true;
        }
        if (key == Windows.System.VirtualKey.Up)
        {
            MoveSlashSelection(-1);
            e.Handled = true;
            return true;
        }
        if (key == Windows.System.VirtualKey.Down)
        {
            MoveSlashSelection(1);
            e.Handled = true;
            return true;
        }
        return false;
    }

    private void MoveSlashSelection(int delta)
    {
        var n = SlashList.Items.Count;
        if (n == 0) return;
        var cur = SlashList.SelectedIndex;
        var next = cur < 0 ? (delta > 0 ? 0 : n - 1) : (cur + delta + n) % n;
        SlashList.SelectedIndex = next;
        if (SlashList.SelectedItem != null)
            SlashList.ScrollIntoView(SlashList.SelectedItem);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e) => HandleEnterKey(e);

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var text = InputBox.Text ?? string.Empty;
        var len = text.Length;
        CharCounter.Text = $"{len} chars";
        CharCounter.Visibility = len > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

        // M4.9.5 Phase E2: surface slash completion when the first non-space
        // token starts with '/' and no whitespace follows the command name yet.
        UpdateSlashCompletion(text);
    }

    /// <summary>
    /// M4.9.5 Phase E2: show/hide the slash command popup based on the current
    /// input. Shown only when the caret is still typing the command token
    /// (text starts with / and has no space yet). Filters the cached command
    /// list by prefix.
    /// </summary>
    private async void UpdateSlashCompletion(string text)
    {
        var trimmed = text.TrimStart();
        bool showSlash = trimmed.StartsWith('/') && trimmed.Length > 1
            && !trimmed.Contains(' ') && !trimmed.Contains('\n');
        if (!showSlash)
        {
            HideSlashPopup();
            return;
        }
        var token = trimmed[1..].ToLowerInvariant();
        var commands = await EnsureCommandsAsync();

        var current = InputBox.Text ?? string.Empty;
        if (!string.Equals(current, text, StringComparison.Ordinal))
        {
            var currentTrimmed = current.TrimStart();
            if (!currentTrimmed.StartsWith('/') ||
                currentTrimmed.Contains(' ') ||
                currentTrimmed.Contains('\n'))
            {
                HideSlashPopup();
                return;
            }

            token = currentTrimmed[1..].ToLowerInvariant();
        }

        var filtered = commands.Where(c => c.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                               .Take(40)
                               .ToList();
        if (filtered.Count == 0)
        {
            HideSlashPopup();
            return;
        }
        SlashList.ItemsSource = filtered;
        SlashList.SelectedIndex = 0;
        SlashList.ScrollIntoView(filtered[0]);
        if (!SlashPopup.IsOpen)
            SlashPopup.IsOpen = true;
    }

    private async Task<IReadOnlyList<SlashCommand>> EnsureCommandsAsync()
    {
        if (_vm == null) return Array.Empty<SlashCommand>();
        var now = DateTime.UtcNow;
        if (_allCommands != null && (now - _commandsLoadedAt).TotalSeconds < CommandsCacheSeconds)
            return _allCommands;
        if (_commandsLoading)
            return _allCommands ?? Array.Empty<SlashCommand>();
        _commandsLoading = true;
        try
        {
            _allCommands = await _vm.GetSlashCommandsAsync();
            _commandsLoadedAt = now;
        }
        catch
        {
            _allCommands = Array.Empty<SlashCommand>();
        }
        finally { _commandsLoading = false; }
        return _allCommands;
    }

    private void HideSlashPopup()
    {
        if (SlashPopup.IsOpen)
            SlashPopup.IsOpen = false;
    }

    private void SlashList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SlashCommand cmd)
            AcceptSlashCommand(cmd);
    }

    /// <summary>Replace the /-token in the input with the accepted command,
    /// keep the caret after it so the user can type arguments, then close.</summary>
    private void AcceptSlashCommand(SlashCommand cmd)
    {
        var text = InputBox.Text ?? string.Empty;
        // Find the leading '/' (skip leading whitespace).
        int slashIdx = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '/') { slashIdx = i; break; }
            if (!char.IsWhiteSpace(text[i])) { slashIdx = -1; break; }
        }
        if (slashIdx >= 0)
        {
            var prefix = text[..slashIdx];
            InputBox.Text = $"{prefix}/{cmd.Name} ";
            InputBox.Select(InputBox.Text.Length, 0);
        }
        HideSlashPopup();
    }

    private void HandleEnterKey(KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != Windows.System.VirtualKey.Enter)
            return;

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift)
            return;

        e.Handled = true;
        Send();
    }

    /// <summary>
    /// M4.9.5 Phase E1: Up/Down navigates the sent-command history. Up (when
    /// caret on line 1) walks back; Down (when caret on last line) walks
    /// forward; reaching the end restores the in-progress draft. Returns true
    /// if the key was consumed.
    /// </summary>
    private bool HandleHistoryNavigation(KeyRoutedEventArgs e)
    {
        if (e.Handled)
            return false;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        bool up = e.Key == Windows.System.VirtualKey.Up;
        bool down = e.Key == Windows.System.VirtualKey.Down;
        if (!up && !down)
            return false;

        // Require Ctrl+Arrow for history nav inside multi-line so plain arrows
        // move the caret; single-line input is always history-nav (caret can't
        // move up/down anyway).
        bool multiline = InputBox.Text?.Contains('\n') == true;
        if (multiline && !ctrl)
            return false;

        if (_history.Count == 0)
            return false;

        if (up)
        {
            if (_historyIndex == -1)
            {
                _draftBeforeHistory = InputBox.Text ?? string.Empty;
                _historyIndex = _history.Count - 1;
            }
            else if (_historyIndex > 0)
            {
                _historyIndex--;
            }
            else
            {
                return false; // already at oldest
            }
            InputBox.Text = _history[_historyIndex];
            InputBox.Select(InputBox.Text.Length, 0);
            e.Handled = true;
            return true;
        }
        else // down
        {
            if (_historyIndex == -1)
                return false; // not navigating
            if (_historyIndex < _history.Count - 1)
            {
                _historyIndex++;
                InputBox.Text = _history[_historyIndex];
            }
            else
            {
                // past the newest → restore draft
                _historyIndex = -1;
                InputBox.Text = _draftBeforeHistory;
            }
            InputBox.Select(InputBox.Text.Length, 0);
            e.Handled = true;
            return true;
        }
    }

    /// <summary>
    /// M4.9.5 Phase E4: dynamic placeholder reflects the current workspace /
    /// agent mode so the user always knows what context a send will hit.
    /// </summary>
    private void UpdatePlaceholder()
    {
        if (_vm == null) return;
        var mode = _vm.IsAgentModeEnabled ? "agent" : "chat";
        var ws = string.IsNullOrWhiteSpace(_vm.WorkspacePath) ? null
            : System.IO.Path.GetFileName(_vm.WorkspacePath.TrimEnd('\\', '/'));
        InputBox.PlaceholderText = ws == null
            ? $"Type a message ({mode})… (Shift+Enter newline · ↑/↓ history)"
            : $"Message {mode} @ {ws}… (Shift+Enter newline · ↑/↓ history)";
    }

    private void Send_Click(object s, RoutedEventArgs e) => Send();

    private void Stop_Click(object s, RoutedEventArgs e)
    {
        Play(InteractionSound.Toggle);
        _vm?.StopSendingCommand.Execute(null);
    }

    private void UserRole_Click(object sender, RoutedEventArgs e) => SetRole("user");

    private void SystemRole_Click(object sender, RoutedEventArgs e) => SetRole("system");

    private void AgentMode_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        _vm.IsAgentModeEnabled = !_vm.IsAgentModeEnabled;
        UpdateAgentModeVisualState();
        Play(InteractionSound.Toggle);
    }

    private void FullAccess_Click(object sender, RoutedEventArgs e) =>
        SetPermissionMode(AgentPermissionModes.BypassPermissions);

    private void AutoApprove_Click(object sender, RoutedEventArgs e) =>
        SetPermissionMode(AgentPermissionModes.AutoApprove);

    private void AskApproval_Click(object sender, RoutedEventArgs e) =>
        SetPermissionMode(AgentPermissionModes.RequestApproval);

    private async void NewWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        try
        {
            await _vm.CreateWorkspaceRootAsync();
            UpdateWorkspaceButton();
            Play(InteractionSound.Toggle);
        }
        catch (Exception ex)
        {
            _vm.ErrorMessage = ex.Message;
            Play(InteractionSound.Error);
        }
    }

    private async void ChooseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || App.MainWindow == null)
            return;

        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null)
                return;

            await _vm.SetWorkspaceRootAsync(folder.Path);
            UpdateWorkspaceButton();
            Play(InteractionSound.Toggle);
        }
        catch (Exception ex)
        {
            _vm.ErrorMessage = ex.Message;
            Play(InteractionSound.Error);
        }
    }

    private async void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || string.IsNullOrWhiteSpace(_vm.WorkspacePath) || !Directory.Exists(_vm.WorkspacePath))
            return;

        try
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(_vm.WorkspacePath);
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex)
        {
            _vm.ErrorMessage = ex.Message;
            Play(InteractionSound.Error);
        }
    }

    private async void UseSandbox_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        try
        {
            await _vm.ClearWorkspaceRootAsync();
            UpdateWorkspaceButton();
            Play(InteractionSound.Toggle);
        }
        catch (Exception ex)
        {
            _vm.ErrorMessage = ex.Message;
            Play(InteractionSound.Error);
        }
    }

    private async void Send()
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text) || _vm == null)
            return;

        var sentText = InputBox.Text;
        _vm.InputText = sentText;
        InputBox.Text = string.Empty;

        // M4.9.5 Phase E1: record non-empty sent text in history. Skip if the
        // latest entry is identical (avoids streaks of duplicates).
        var trimmed = sentText.TrimEnd();
        if (trimmed.Length > 0 && (_history.Count == 0 || _history[^1] != trimmed))
        {
            _history.Add(trimmed);
            if (_history.Count > MaxHistoryEntries)
                _history.RemoveAt(0);
        }
        _historyIndex = -1;
        _draftBeforeHistory = string.Empty;

        UpdateSendingState();
        Play(InteractionSound.Send);
        try
        {
            await _vm.SendMessageAsync();
            Play(string.IsNullOrWhiteSpace(_vm.ErrorMessage)
                ? InteractionSound.Complete
                : InteractionSound.Error);
        }
        catch
        {
            Play(InteractionSound.Error);
        }
        finally
        {
            UpdateSendingState();
        }
    }

    public void SendFromShortcut() => Send();

    public void StopFromShortcut()
    {
        Play(InteractionSound.Toggle);
        _vm?.StopSendingCommand.Execute(null);
    }

    public void FocusMessageInput()
    {
        InputBox.Focus(FocusState.Programmatic);
        InputBox.Select(InputBox.Text.Length, 0);
    }

    /// <summary>
    /// M4.9.6: Inject a slash command text and trigger completion popup.
    /// Used by the Ctrl+K command palette to dispatch slash commands.
    /// </summary>
    public void InjectSlashCommand(string text)
    {
        InputBox.Text = text;
        InputBox.Select(InputBox.Text.Length, 0);
        InputBox.Focus(FocusState.Programmatic);
        UpdateSlashCompletion(text);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatPageViewModel.IsSending))
            DispatcherQueue.TryEnqueue(UpdateSendingState);
        if (e.PropertyName == nameof(ChatPageViewModel.IsAgentModeEnabled))
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateAgentModeVisualState();
                UpdatePlaceholder();
            });
        if (e.PropertyName == nameof(ChatPageViewModel.SelectedRole))
            DispatcherQueue.TryEnqueue(UpdateRoleButton);
        if (e.PropertyName == nameof(ChatPageViewModel.SelectedAgentPermissionMode))
            DispatcherQueue.TryEnqueue(() => SelectPermissionMode(_vm?.SelectedAgentPermissionMode));
        if (e.PropertyName is nameof(ChatPageViewModel.WorkspaceDisplayName)
            or nameof(ChatPageViewModel.WorkspacePath)
            or nameof(ChatPageViewModel.IsWorkspaceConfigured)
            or nameof(ChatPageViewModel.WorkspaceToolTip))
        {
            DispatcherQueue.TryEnqueue(UpdateWorkspaceButton);
            DispatcherQueue.TryEnqueue(UpdatePlaceholder);
        }
    }

    private void UpdateSendingState()
    {
        var sending = _vm?.IsSending == true;
        SendBtn.Visibility = sending ? Visibility.Collapsed : Visibility.Visible;
        StopBtn.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        SendingRing.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        SendBtn.IsEnabled = !sending;
        RoleButton.IsEnabled = !sending;
        AgentModeButton.IsEnabled = !sending;
        WorkspaceButton.IsEnabled = !sending;
        PermissionModeButton.IsEnabled = !sending;
    }

    private void UpdateAgentModeVisualState()
    {
        var enabled = _vm?.IsAgentModeEnabled == true;
        AgentModeButton.Background = enabled
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["InputBackgroundBrush"];
        AgentModeButton.Foreground = enabled
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
        ToolTipService.SetToolTip(
            AgentModeButton,
            enabled
                ? "Agent: On. Multi-step tool use is enabled."
                : "Agent: Off. Send a normal chat message.");
    }

    private void Play(InteractionSound sound)
    {
        if (!_suppressSound && App.MainWindow is MainWindow w)
            w.SoundService.Play(sound);
    }

    private void SelectPermissionMode(string? mode)
    {
        var normalized = AgentPermissionModes.Normalize(mode);
        if (_vm != null && !string.Equals(_vm.SelectedAgentPermissionMode, normalized, StringComparison.OrdinalIgnoreCase))
            _vm.SelectedAgentPermissionMode = normalized;
        UpdatePermissionModeButton();
    }

    private void SetRole(string role)
    {
        if (_vm == null)
            return;
        _vm.SelectedRole = string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)
            ? "system"
            : "user";
        UpdateRoleButton();
        Play(InteractionSound.Toggle);
    }

    private void UpdateRoleButton()
    {
        var role = string.Equals(_vm?.SelectedRole, "system", StringComparison.OrdinalIgnoreCase)
            ? "system"
            : "user";
        ToolTipService.SetToolTip(RoleButton, $"Role: {role}");
        AutomationProperties.SetName(RoleButton, $"Message role: {role}");
    }

    private void SetPermissionMode(string mode)
    {
        if (_vm == null)
            return;
        _vm.SelectedAgentPermissionMode = AgentPermissionModes.Normalize(mode);
        UpdatePermissionModeButton();
        Play(InteractionSound.Toggle);
    }

    private void UpdatePermissionModeButton()
    {
        var item = GetPermissionModeItem(_vm?.SelectedAgentPermissionMode);
        ToolTipService.SetToolTip(PermissionModeButton, $"Access: {item.Label}. {item.Description}");
        AutomationProperties.SetName(PermissionModeButton, $"Agent permission mode: {item.Label}");
    }

    private void UpdateWorkspaceButton()
    {
        var label = string.IsNullOrWhiteSpace(_vm?.WorkspaceDisplayName)
            ? "Sandbox"
            : _vm.WorkspaceDisplayName;
        ToolTipService.SetToolTip(WorkspaceButton, $"Workspace: {label}\n{_vm?.WorkspaceToolTip ?? "Using this chat's private sandbox."}");
        AutomationProperties.SetName(WorkspaceButton, $"Workspace: {label}");
        OpenWorkspaceItem.IsEnabled = !string.IsNullOrWhiteSpace(_vm?.WorkspacePath) &&
            Directory.Exists(_vm.WorkspacePath);
        UseSandboxItem.IsEnabled = _vm?.IsWorkspaceConfigured == true;
    }

    private void Plan_Click(object sender, RoutedEventArgs e) =>
        SetPermissionMode(AgentPermissionModes.Plan);

    private static PermissionModeItem GetPermissionModeItem(string? mode)
    {
        var normalized = AgentPermissionModes.Normalize(mode);
        return normalized switch
        {
            AgentPermissionModes.BypassPermissions => new(
                AgentPermissionModes.BypassPermissions,
                "Full access",
                "All tools run without approval prompts. Use with trusted workspaces only."),
            AgentPermissionModes.Plan => new(
                AgentPermissionModes.Plan,
                "Plan",
                "Read-only exploration and design. Writes and terminal execution are blocked until plan is approved."),
            AgentPermissionModes.AutoApprove => new(
                AgentPermissionModes.AutoApprove,
                "Auto approve",
                "Approves detected tool directions automatically while keeping safety blocks."),
            AgentPermissionModes.RequestApproval => new(
                AgentPermissionModes.RequestApproval,
                "Ask",
                "Requests approval for risky tools and scoped permission rules."),
            _ => new(
                AgentPermissionModes.RequestApproval,
                "Ask",
                "Requests approval for risky tools and scoped permission rules.")
        };
    }

    private sealed record PermissionModeItem(string Mode, string Label, string Description)
    {
        public override string ToString() => Label;
    }
}
