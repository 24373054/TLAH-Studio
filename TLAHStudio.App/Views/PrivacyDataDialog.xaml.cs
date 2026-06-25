using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLAHStudio.App.ViewModels;
using Windows.Storage.Pickers;

namespace TLAHStudio.App.Views;

public sealed partial class PrivacyDataDialog : ContentDialog
{
    private PrivacyDataViewModel? _vm;

    public PrivacyDataDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _vm = DataContext as PrivacyDataViewModel;
            if (_vm == null)
                return;
            await _vm.LoadAsync();
            Refresh();
        };
    }

    private void Refresh()
    {
        if (_vm == null)
            return;

        if (_vm.Summary is { } summary)
        {
            SummaryText.Text =
                $"{summary.ChatCount} chats, {summary.MessageCount} messages, {summary.TurnCount} turns, " +
                $"{summary.RawRequestCount + summary.RawResponseCount} raw debug records. " +
                $"Database: {summary.DatabaseSizeText}.";
        }

        PreviewBox.Text = _vm.DiagnosticsPreview;
        StatusText.Text = _vm.ErrorMessage ?? _vm.StatusText;
    }

    private async void ExportData_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || App.MainWindow is not MainWindow window)
            return;

        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = $"tlah-data-{DateTime.Now:yyyyMMdd-HHmmss}";
        picker.FileTypeChoices.Add("TLAH JSON", new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        if (file == null)
            return;

        await _vm.ExportAllDataAsync(file.Path);
        Refresh();
    }

    private async void ImportData_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || App.MainWindow is not MainWindow window)
            return;

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;

        await _vm.ImportAllDataAsync(file.Path);
        await window.SidebarVM.LoadChatsAsync();
        Refresh();
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;
        await _vm.ExportDiagnosticsAsync();
        Refresh();
    }

    private void CancelClearData_Click(object sender, RoutedEventArgs e)
    {
        ClearDataFlyout.Hide();
    }

    private async void ConfirmClearData_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || App.MainWindow is not MainWindow window)
            return;

        ClearDataFlyout.Hide();

        await _vm.ClearAllDataAsync();
        window.SidebarVM.SelectedChat = null;
        window.ChatVM.CurrentChat = null;
        window.ChatVM.Messages.Clear();
        await window.SidebarVM.LoadChatsAsync();
        Refresh();
    }
}
