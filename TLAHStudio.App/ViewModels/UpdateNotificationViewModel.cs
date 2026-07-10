using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using TLAHStudio.Core.Services;

namespace TLAHStudio.App.ViewModels;

/// <summary>
/// Manages the update notification + download flow.
/// </summary>
public partial class UpdateNotificationViewModel : ObservableObject
{
    private UpdateCheckResult? _updateInfo;
    private readonly IUpdateService _updateService;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _versionText = string.Empty;
    [ObservableProperty] private string _packageInfoText = string.Empty;
    [ObservableProperty] private string _releaseNotes = string.Empty;
    [ObservableProperty] private bool _isForceUpdate;
    [ObservableProperty] private int _downloadProgress; // 0-100
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private string _downloadStatus = string.Empty;
    [ObservableProperty] private string? _errorMessage;

    public UpdateCheckResult? UpdateInfo => _updateInfo;
    public IUpdateService AppUpdateService => _updateService;

    public UpdateNotificationViewModel(IUpdateService updateService)
    {
        _updateService = updateService;
    }

    public void ShowUpdate(UpdateCheckResult info)
    {
        _updateInfo = info;
        VersionText = $"Version {info.Version} is available (current: {AppUpdateService.CurrentVersion})";
        PackageInfoText = BuildPackageInfo(info);
        ReleaseNotes = info.ReleaseNotes ?? "No release notes available.";
        IsForceUpdate = info.ForceUpdate;
        DownloadProgress = 0;
        IsDownloading = false;
        DownloadStatus = string.Empty;
        ErrorMessage = null;
        IsVisible = true;
    }

    [RelayCommand]
    private void OpenBrowserToDownload()
    {
        if (_updateInfo != null && !string.IsNullOrEmpty(_updateInfo.InstallerUrl))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _updateInfo.InstallerUrl,
                UseShellExecute = true
            });
        }
        IsVisible = false;
    }

    /// <summary>
    /// Download the installer, verify SHA256, then launch Updater.exe.
    /// This is the automated update path (Phase 3).
    /// </summary>
    [RelayCommand]
    private async Task DownloadAndInstallAsync()
    {
        if (_updateInfo == null) return;

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatus = "Downloading...";
        ErrorMessage = null;
        _downloadCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<int>(p =>
            {
                DownloadProgress = p;
                DownloadStatus = $"Downloading... {p}%";
            });

            var installerPath = await AppUpdateService.DownloadInstallerAsync(
                _updateInfo, progress, _downloadCts.Token);

            if (installerPath == null)
            {
                ErrorMessage = "Download failed or SHA256 mismatch. Please try again or download manually.";
                IsDownloading = false;
                return;
            }

            DownloadStatus = "Verified. Launching installer...";
            DownloadProgress = 100;

            // Brief pause so the user sees the completion state
            await Task.Delay(500);

            // Launch Updater.exe and exit this app
            AppUpdateService.LaunchUpdater(installerPath);
            Application.Current.Exit();
        }
        catch (OperationCanceledException)
        {
            DownloadStatus = "Download cancelled.";
            IsDownloading = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Update failed: {ex.Message}";
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    [RelayCommand]
    private void RemindLater()
    {
        IsVisible = false;
    }

    private static string BuildPackageInfo(UpdateCheckResult info)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Channel))
            parts.Add($"Channel: {info.Channel}");
        if (info.InstallerSizeBytes is > 0)
            parts.Add($"Size: {info.InstallerSizeBytes.Value / 1024d / 1024d:0.0} MB");
        if (!string.IsNullOrWhiteSpace(info.Sha256))
            parts.Add($"SHA256: {info.Sha256}");
        return string.Join("\n", parts);
    }
}
