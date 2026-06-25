using Microsoft.UI.Xaml.Controls;
using TLAHStudio.App.ViewModels;

namespace TLAHStudio.App.Views;

public sealed partial class UpdateNotificationDialog : ContentDialog
{
    private UpdateNotificationViewModel? _vm;
    private bool _allowClose;

    public UpdateNotificationDialog()
    {
        InitializeComponent();
        Closing += Dialog_Closing;
    }

    public void SetData(UpdateNotificationViewModel vm)
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        UpdateView();
    }

    private async void Download_Click(ContentDialog s, ContentDialogButtonClickEventArgs a)
    {
        a.Cancel = true;
        if (_vm == null || _vm.IsDownloading)
            return;

        await _vm.DownloadAndInstallCommand.ExecuteAsync(null);
        UpdateView();
    }

    private void Later_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_vm?.IsForceUpdate == true)
        {
            args.Cancel = true;
            return;
        }

        _allowClose = true;
        _vm?.RemindLaterCommand.Execute(null);
    }

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_vm?.IsForceUpdate == true && !_allowClose && _vm.DownloadProgress < 100)
        {
            args.Cancel = true;
            return;
        }

        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateView);

    private void UpdateView()
    {
        if (_vm == null)
            return;

        VersionText.Text = _vm.VersionText;
        PackageInfoText.Text = _vm.PackageInfoText;
        NotesText.Text = _vm.ReleaseNotes;
        ForceNotice.Visibility = _vm.IsForceUpdate
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

        CloseButtonText = _vm.IsForceUpdate ? string.Empty : "Later";
        PrimaryButtonText = _vm.IsDownloading ? "Downloading..." : "Download";
        IsPrimaryButtonEnabled = !_vm.IsDownloading;

        DownloadProgressBar.Value = _vm.DownloadProgress;
        DownloadStatusText.Text = _vm.DownloadStatus;
        DownloadPanel.Visibility =
            _vm.IsDownloading ||
            _vm.DownloadProgress > 0 ||
            !string.IsNullOrWhiteSpace(_vm.DownloadStatus)
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;

        ErrorText.Text = _vm.ErrorMessage ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(_vm.ErrorMessage)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
    }
}
