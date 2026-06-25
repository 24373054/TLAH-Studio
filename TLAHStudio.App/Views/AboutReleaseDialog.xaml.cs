using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using TLAHStudio.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace TLAHStudio.App.Views;

public sealed partial class AboutReleaseDialog : ContentDialog
{
    private IAppReleaseService? _service;
    private AppReleaseSnapshot? _snapshot;

    public AboutReleaseDialog()
    {
        InitializeComponent();
    }

    public async Task LoadAsync(IAppReleaseService service)
    {
        _service = service;
        StatusText.Text = "Loading release information...";
        _snapshot = await service.GetSnapshotAsync();
        Render();
        StatusText.Text = string.Empty;
    }

    private void Render()
    {
        if (_snapshot == null)
            return;

        CurrentVersionText.Text = _snapshot.CurrentVersion;
        LatestVersionText.Text = _snapshot.LatestVersion;
        ChannelText.Text = _snapshot.Channel;
        SizeText.Text = _snapshot.InstallerSizeText;
        ShaText.Text = string.IsNullOrWhiteSpace(_snapshot.Sha256)
            ? "Not available offline"
            : _snapshot.Sha256;
        SignatureText.Text = _snapshot.SignatureStatus;
        CertificateText.Text = string.IsNullOrWhiteSpace(_snapshot.CertificateSubject)
            ? "No certificate details available."
            : $"{_snapshot.CertificateSubject}\n{_snapshot.CertificateThumbprint}";
        LogsText.Text = _snapshot.LogsDirectory;
    }

    private void Copy_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_snapshot == null)
            return;

        var package = new DataPackage();
        package.SetText(BuildReleaseInfo(_snapshot));
        Clipboard.SetContent(package);
        StatusText.Text = "Release information copied.";
    }

    private async void ExportDiagnostics_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_service == null)
            return;

        try
        {
            StatusText.Text = "Exporting diagnostics...";
            var path = await _service.ExportDiagnosticsAsync();
            StatusText.Text = $"Diagnostics exported: {path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private void OpenDownloadPage_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var url = _snapshot?.DownloadPageUrl ?? "https://download.matrixlabs.cn";
        Open(url);
    }

    private void OpenLogs_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_snapshot?.LogsDirectory))
            Open(_snapshot.LogsDirectory);
    }

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static string BuildReleaseInfo(AppReleaseSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TLAH Studio");
        sb.AppendLine($"Current version: {snapshot.CurrentVersion}");
        sb.AppendLine($"Latest manifest: {snapshot.LatestVersion}");
        sb.AppendLine($"Channel: {snapshot.Channel}");
        sb.AppendLine($"Installer: {snapshot.InstallerUrl}");
        sb.AppendLine($"Installer size: {snapshot.InstallerSizeText}");
        sb.AppendLine($"SHA256: {snapshot.Sha256}");
        sb.AppendLine($"Signature: {snapshot.SignatureStatus}");
        if (!string.IsNullOrWhiteSpace(snapshot.CertificateSubject))
            sb.AppendLine($"Certificate: {snapshot.CertificateSubject} / {snapshot.CertificateThumbprint}");
        sb.AppendLine($"Download page: {snapshot.DownloadPageUrl}");
        sb.AppendLine($"Update manifest: {snapshot.UpdateCheckUrl}");
        return sb.ToString();
    }
}
