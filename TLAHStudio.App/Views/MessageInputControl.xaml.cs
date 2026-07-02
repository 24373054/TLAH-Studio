using Microsoft.UI.Xaml;
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
        RoleCombo.Items.Add("user");
        RoleCombo.Items.Add("system");
        PermissionModeCombo.Items.Add(new PermissionModeItem(AgentPermissionModes.BypassPermissions, "Full access", "Unrestricted local terminal access; approvals are bypassed."));
        PermissionModeCombo.Items.Add(new PermissionModeItem(AgentPermissionModes.AutoApprove, "Auto approve", "Approves detected tool directions automatically while keeping safety blocks."));
        PermissionModeCombo.Items.Add(new PermissionModeItem(AgentPermissionModes.RequestApproval, "Ask", "Requests approval for risky tools and scoped permission rules."));
        RoleCombo.SelectionChanged += (_, _) =>
        {
            if (_vm != null)
                _vm.SelectedRole = RoleCombo.SelectedItem?.ToString() ?? "user";
        };
        PermissionModeCombo.SelectionChanged += (_, _) =>
        {
            if (_vm != null && PermissionModeCombo.SelectedItem is PermissionModeItem item)
                _vm.SelectedAgentPermissionMode = item.Mode;
            UpdatePermissionModeToolTip();
        };
        AgentModeToggle.Checked += (_, _) =>
        {
            if (_vm != null)
                _vm.IsAgentModeEnabled = true;
            UpdateAgentModeVisualState();
            Play(InteractionSound.Toggle);
        };
        AgentModeToggle.Unchecked += (_, _) =>
        {
            if (_vm != null)
                _vm.IsAgentModeEnabled = false;
            UpdateAgentModeVisualState();
            Play(InteractionSound.Toggle);
        };
        InputBox.PreviewKeyDown += OnPreviewKeyDown;
        InputBox.KeyDown += OnKeyDown;
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
            AgentModeToggle.IsChecked = _vm.IsAgentModeEnabled;
            SelectPermissionMode(_vm.SelectedAgentPermissionMode);
            UpdateWorkspaceButton();
            _suppressSound = false;
            UpdateSendingState();
            UpdateAgentModeVisualState();
            UpdatePermissionModeToolTip();
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
        RoleCombo.Width = compact ? 92 : 124;
        RoleCombo.Margin = compact
            ? new Thickness(0, 0, 8, 0)
            : new Thickness(0, 0, 12, 0);
        AgentModeToggle.MinWidth = compact ? 70 : 82;
        AgentModeToggle.Margin = compact
            ? new Thickness(0, 0, 8, 0)
            : new Thickness(0, 0, 12, 0);
        WorkspaceButton.MinWidth = compact ? 44 : 118;
        WorkspaceButton.Margin = compact
            ? new Thickness(0, 0, 8, 0)
            : new Thickness(0, 0, 12, 0);
        WorkspaceButtonText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PermissionModeCombo.Width = compact ? 92 : 132;
        PermissionModeCombo.Margin = compact
            ? new Thickness(0, 0, 8, 0)
            : new Thickness(0, 0, 12, 0);
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
                _suppressSound = true;
                AgentModeToggle.IsChecked = _vm?.IsAgentModeEnabled == true;
                _suppressSound = false;
                UpdateAgentModeVisualState();
            });
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
        AgentModeToggle.IsEnabled = !sending;
        WorkspaceButton.IsEnabled = !sending;
        PermissionModeCombo.IsEnabled = !sending;
    }

    private void UpdateAgentModeVisualState()
    {
        var enabled = AgentModeToggle.IsChecked == true;
        AgentModeToggle.Background = enabled
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["InputBackgroundBrush"];
        AgentModeToggle.Foreground = enabled
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
    }

    private void Play(InteractionSound sound)
    {
        if (!_suppressSound && App.MainWindow is MainWindow w)
            w.SoundService.Play(sound);
    }

    private void SelectPermissionMode(string? mode)
    {
        var normalized = AgentPermissionModes.Normalize(mode);
        for (var i = 0; i < PermissionModeCombo.Items.Count; i++)
        {
            if (PermissionModeCombo.Items[i] is PermissionModeItem item &&
                string.Equals(item.Mode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                PermissionModeCombo.SelectedIndex = i;
                UpdatePermissionModeToolTip();
                return;
            }
        }

        PermissionModeCombo.SelectedIndex = 0;
        UpdatePermissionModeToolTip();
    }

    private void UpdatePermissionModeToolTip()
    {
        if (PermissionModeCombo.SelectedItem is PermissionModeItem item)
            ToolTipService.SetToolTip(PermissionModeCombo, item.Description);
    }

    private void UpdateWorkspaceButton()
    {
        var label = _vm?.WorkspaceDisplayName;
        WorkspaceButtonText.Text = string.IsNullOrWhiteSpace(label) ? "Sandbox" : label;
        ToolTipService.SetToolTip(WorkspaceButton, _vm?.WorkspaceToolTip ?? "Workspace");
        OpenWorkspaceItem.IsEnabled = !string.IsNullOrWhiteSpace(_vm?.WorkspacePath) &&
            Directory.Exists(_vm.WorkspacePath);
        UseSandboxItem.IsEnabled = _vm?.IsWorkspaceConfigured == true;
    }

    private sealed record PermissionModeItem(string Mode, string Label, string Description)
    {
        public override string ToString() => Label;
    }
}
