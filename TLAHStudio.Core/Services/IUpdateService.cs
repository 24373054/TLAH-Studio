namespace TLAHStudio.Core.Services;

/// <summary>
/// Auto-update service. Checks latest.json on a remote server,
/// compares with the local version, downloads installer, and orchestrates updates.
/// </summary>
public interface IUpdateService
{
    /// <summary>Current installed version from version.json.</summary>
    string CurrentVersion { get; }

    /// <summary>URL where latest.json is hosted.</summary>
    string UpdateCheckUrl { get; }

    /// <summary>
    /// Check for available updates. Returns null if up-to-date,
    /// or a result with the new version info if an update is available.
    /// </summary>
    Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// Download the installer and verify its SHA256. Reports progress via callback.
    /// Returns the local path to the downloaded installer, or null on failure.
    /// </summary>
    Task<string?> DownloadInstallerAsync(
        UpdateCheckResult updateInfo,
        IProgress<int>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Launch the Updater.exe to perform the silent install and restart.
    /// The calling app should exit immediately after calling this.
    /// </summary>
    void LaunchUpdater(string installerPath);
}

/// <summary>
/// Result of an update check. Null means no update available.
/// </summary>
public record UpdateCheckResult(
    string Version,
    string Channel,
    string InstallerUrl,
    long? InstallerSizeBytes,
    string? Sha256,
    string? ReleaseNotes,
    bool ForceUpdate,
    string? MinSupportedVersion
);
