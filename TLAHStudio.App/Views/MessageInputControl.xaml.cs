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

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e) => HandleEnterKey(e);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e) => HandleEnterKey(e);

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var len = InputBox.Text?.Length ?? 0;
        CharCounter.Text = $"{len} chars";
        CharCounter.Visibility = len > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
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

        _vm.InputText = InputBox.Text;
        InputBox.Text = string.Empty;
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

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatPageViewModel.IsSending))
            DispatcherQueue.TryEnqueue(UpdateSendingState);
        if (e.PropertyName == nameof(ChatPageViewModel.IsAgentModeEnabled))
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateAgentModeVisualState();
            });
        if (e.PropertyName == nameof(ChatPageViewModel.SelectedRole))
            DispatcherQueue.TryEnqueue(UpdateRoleButton);
        if (e.PropertyName == nameof(ChatPageViewModel.SelectedAgentPermissionMode))
            DispatcherQueue.TryEnqueue(() => SelectPermissionMode(_vm?.SelectedAgentPermissionMode));
        if (e.PropertyName is nameof(ChatPageViewModel.WorkspaceDisplayName)
            or nameof(ChatPageViewModel.WorkspacePath)
            or nameof(ChatPageViewModel.IsWorkspaceConfigured)
            or nameof(ChatPageViewModel.WorkspaceToolTip))
            DispatcherQueue.TryEnqueue(UpdateWorkspaceButton);
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
