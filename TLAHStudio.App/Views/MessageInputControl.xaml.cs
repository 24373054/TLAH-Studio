using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;
using TLAHStudio.App.ViewModels;

namespace TLAHStudio.App.Views;

public sealed partial class MessageInputControl : UserControl
{
    private ChatPageViewModel? _vm;
    private bool _loaded;

    public MessageInputControl()
    {
        InitializeComponent();
        RoleCombo.Items.Add("user");
        RoleCombo.Items.Add("system");
        RoleCombo.SelectionChanged += (_, _) =>
        {
            if (_vm != null)
                _vm.SelectedRole = RoleCombo.SelectedItem?.ToString() ?? "user";
        };
        AgentModeToggle.Checked += (_, _) =>
        {
            if (_vm != null)
                _vm.IsAgentModeEnabled = true;
            UpdateAgentModeVisualState();
        };
        AgentModeToggle.Unchecked += (_, _) =>
        {
            if (_vm != null)
                _vm.IsAgentModeEnabled = false;
            UpdateAgentModeVisualState();
        };
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
            AgentModeToggle.IsChecked = _vm.IsAgentModeEnabled;
            UpdateSendingState();
            UpdateAgentModeVisualState();
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
        ActionGrid.Margin = compact
            ? new Thickness(8, 0, 0, 0)
            : new Thickness(12, 0, 0, 0);
        SendBtn.MinWidth = compact ? 44 : 54;
        StopBtn.MinWidth = compact ? 44 : 54;
        InputBox.Padding = compact
            ? new Thickness(12, 10, 12, 10)
            : new Thickness(14, 10, 14, 10);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;

        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift)
            return;

        e.Handled = true;
        Send();
    }

    private void Send_Click(object s, RoutedEventArgs e) => Send();

    private void Stop_Click(object s, RoutedEventArgs e) =>
        _vm?.StopSendingCommand.Execute(null);

    private async void Send()
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text) || _vm == null)
            return;

        _vm.InputText = InputBox.Text;
        InputBox.Text = string.Empty;
        UpdateSendingState();
        try
        {
            await _vm.SendMessageAsync();
        }
        catch
        {
        }
        finally
        {
            UpdateSendingState();
        }
    }

    public void SendFromShortcut() => Send();

    public void StopFromShortcut() => _vm?.StopSendingCommand.Execute(null);

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
                AgentModeToggle.IsChecked = _vm?.IsAgentModeEnabled == true;
                UpdateAgentModeVisualState();
            });
    }

    private void UpdateSendingState()
    {
        var sending = _vm?.IsSending == true;
        SendBtn.Visibility = sending ? Visibility.Collapsed : Visibility.Visible;
        StopBtn.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        SendingRing.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        SendBtn.IsEnabled = !sending;
        AgentModeToggle.IsEnabled = !sending;
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
}
