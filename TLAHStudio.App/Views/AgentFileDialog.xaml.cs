using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLAHStudio.App.ViewModels;

namespace TLAHStudio.App.Views;

public sealed partial class AgentFileDialog : ContentDialog
{
    private AgentFileDialogViewModel? _vm;

    public AgentFileDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _vm = DataContext as AgentFileDialogViewModel;
            if (_vm == null) return;
            await _vm.LoadAsync();
            Refresh();
        };
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.UploadCommand.ExecuteAsync(null);
        Refresh();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        await _vm.DeleteCommand.ExecuteAsync(null);
        Refresh();
    }

    private void Refresh()
    {
        if (_vm == null) return;

        NoChatPanel.Visibility = _vm.HasCurrentChat ? Visibility.Collapsed : Visibility.Visible;
        AgentPanel.Visibility = _vm.HasCurrentChat ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = _vm.HasAgentFile;

        if (!_vm.HasCurrentChat)
            return;

        StatusText.Text = _vm.HasAgentFile
            ? $"{_vm.Filename} · {FormatSize(_vm.SizeBytes)}"
            : "No AGENT.md attached to this chat.";
        PreviewText.Text = _vm.HasAgentFile
            ? _vm.Content ?? string.Empty
            : "Upload a Markdown file to add per-chat instructions.";
    }

    private static string FormatSize(int bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / 1024.0 / 1024.0:0.0} MB";
    }
}
