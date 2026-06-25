using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TLAHStudio.Core.Services;

/// <summary>
/// Complete update service with security hardening (Phase 4).
/// - Checks latest.json for new versions
/// - Verifies latest.json signature (Ed25519/ECDsa P-256)
/// - Anti-downgrade protection
/// - SHA256 installer verification
/// - Gray release support (by installId hash)
/// - Force update enforcement
/// </summary>
public class UpdateService : IUpdateService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _installPath;
    private readonly string _updateCheckUrl;
    private readonly string _installId;
    private readonly string _manifestPublicKeyBase64;

    public string CurrentVersion { get; }
    public string UpdateCheckUrl => _updateCheckUrl;
    public string InstallId => _installId;

    public UpdateService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _manifestPublicKeyBase64 = UpdateCrypto.PublicKeyBase64;

        _installPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "TLAH Studio");

        // Read or generate install ID (persisted in version.json, used for gray release)
        var versionPath = Path.Combine(_installPath, "version.json");
        if (File.Exists(versionPath))
        {
            try
            {
                var json = File.ReadAllText(versionPath);
                using var doc = JsonDocument.Parse(json);
                CurrentVersion = doc.RootElement.GetProperty("version").GetString() ?? "0.0.0";
                _updateCheckUrl = doc.RootElement.TryGetProperty("updateUrl", out var url)
                    ? url.GetString() ?? "https://download.matrixlabs.cn/tlah/windows/latest.json"
                    : "https://download.matrixlabs.cn/tlah/windows/latest.json";
                _installId = doc.RootElement.TryGetProperty("installId", out var iid)
                    ? iid.GetString() ?? GenerateInstallId()
                    : GenerateInstallId();
            }
            catch
            {
                CurrentVersion = "0.0.0";
                _updateCheckUrl = "https://download.matrixlabs.cn/tlah/windows/latest.json";
                _installId = GenerateInstallId();
            }
        }
        else
        {
            CurrentVersion = "1.0.0";
            _updateCheckUrl = "https://download.matrixlabs.cn/tlah/windows/latest.json";
            _installId = GenerateInstallId();
        }
    }

    internal UpdateService(
        IHttpClientFactory httpClientFactory,
        string installPath,
        string currentVersion,
        string updateCheckUrl,
        string installId,
        string manifestPublicKeyBase64)
    {
        _httpClientFactory = httpClientFactory;
        _installPath = installPath;
        CurrentVersion = currentVersion;
        _updateCheckUrl = updateCheckUrl;
        _installId = installId;
        _manifestPublicKeyBase64 = manifestPublicKeyBase64;
    }

    private static string GenerateInstallId()
    {
        return Guid.NewGuid().ToString("D");
    }

    /// <summary>
    /// Fetches latest.json, verifies signature, applies gray release filter,
    /// anti-downgrade check, and returns update info only if everything passes.
    /// </summary>
    public async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Update");
            client.Timeout = TimeSpan.FromMinutes(2);

            // 1. Fetch latest.json
            var response = await client.GetAsync(_updateCheckUrl, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var jsonText = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(jsonText);
            var root = json.RootElement;

            // 2. Verify latest.json signature
            if (_manifestPublicKeyBase64 != "REPLACE_WITH_YOUR_PUBLIC_KEY")
            {
                var sigValid = await UpdateCrypto.VerifyLatestJsonAsync(
                    client, _updateCheckUrl, jsonText, _manifestPublicKeyBase64, ct);
                if (!sigValid)
                    return null; // Signature invalid — possible tampering, silently ignore
            }

            // 3. Parse version info
            var latestVersion = root.GetProperty("version").GetString() ?? "0.0.0";

            // 4. Anti-downgrade: don't install older versions
            if (!IsNewer(latestVersion, CurrentVersion))
                return null;

            // 5. Force update check: if current version is below minSupportedVersion, force update
            var minSupported = root.TryGetProperty("minSupportedVersion", out var mv)
                ? mv.GetString() : null;
            var forceUpdate = root.TryGetProperty("forceUpdate", out var fu) && fu.GetBoolean();

            // If forceUpdate is set, enforce it regardless
            // If minSupportedVersion is set and current < minSupported, force update
            if (!forceUpdate && !string.IsNullOrEmpty(minSupported))
            {
                if (Version.TryParse(CurrentVersion, out var cur) &&
                    Version.TryParse(minSupported, out var min))
                {
                    forceUpdate = cur < min;
                }
            }

            // 6. Gray release: check rollout percentage against installId hash
            if (root.TryGetProperty("rolloutPercent", out var rp) && rp.ValueKind == JsonValueKind.Number)
            {
                var rolloutPercent = rp.GetInt32();
                if (rolloutPercent < 100)
                {
                    var hash = (uint)_installId.GetHashCode();
                    var bucket = hash % 100;
                    if (bucket >= rolloutPercent)
                    {
                        // This install is not in the rollout group — skip update
                        return null;
                    }
                }
            }

            return new UpdateCheckResult(
                Version: latestVersion,
                Channel: root.TryGetProperty("channel", out var ch) ? ch.GetString() ?? "stable" : "stable",
                InstallerUrl: root.TryGetProperty("installerUrl", out var iu) ? iu.GetString()! : "",
                InstallerSizeBytes: ReadOptionalLong(root, "installerSizeBytes")
                    ?? ReadOptionalLong(root, "sizeBytes")
                    ?? ReadOptionalLong(root, "installerSize"),
                Sha256: root.TryGetProperty("sha256", out var sh) ? sh.GetString() : null,
                ReleaseNotes: root.TryGetProperty("releaseNotes", out var rn) ? rn.GetString() : null,
                ForceUpdate: forceUpdate,
                MinSupportedVersion: minSupported
            );
        }
        catch
        {
            return null;
        }
    }

    public static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(candidate, out var c) || !Version.TryParse(current, out var cur))
            return string.Compare(candidate, current, StringComparison.OrdinalIgnoreCase) > 0;
        return c > cur;
    }

    private static long? ReadOptionalLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    public async Task<string?> DownloadInstallerAsync(
        UpdateCheckResult updateInfo,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var tempDir = Path.GetTempPath();
        var fileName = $"TLAHStudioSetup-{updateInfo.Version}.exe";
        var filePath = Path.Combine(tempDir, fileName);

        try
        {
            var client = _httpClientFactory.CreateClient("Update");
            client.Timeout = TimeSpan.FromMinutes(30);

            using var response = await client.GetAsync(
                updateInfo.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(
                filePath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;
            int lastReportedPercent = -1;

            while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    var percent = (int)(totalRead * 100 / totalBytes);
                    if (percent != lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        progress?.Report(percent);
                    }
                }
            }

            // SHA256 verification
            if (!string.IsNullOrEmpty(updateInfo.Sha256))
            {
                fileStream.Close();
                var actualHash = await ComputeSha256Async(filePath, ct);
                if (!string.Equals(actualHash, updateInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(filePath); } catch { /* ignore */ }
                    return null;
                }
            }

            return filePath;
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(filePath); } catch { /* ignore */ }
            return null;
        }
        catch
        {
            try { File.Delete(filePath); } catch { /* ignore */ }
            return null;
        }
    }

    public void LaunchUpdater(string installerPath)
    {
        var updaterPath = Path.Combine(_installPath, "TLAHStudio.Updater.exe");
        if (!File.Exists(updaterPath))
        {
            updaterPath = Path.Combine(AppContext.BaseDirectory, "TLAHStudio.Updater.exe");
            if (!File.Exists(updaterPath))
                return;
        }

        updaterPath = StageUpdater(updaterPath);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = $"\"{installerPath}\" {Environment.ProcessId}",
            UseShellExecute = true
        });
    }

    private static string StageUpdater(string updaterPath)
    {
        try
        {
            var sourceDir = Path.GetDirectoryName(updaterPath);
            if (string.IsNullOrWhiteSpace(sourceDir))
                return updaterPath;

            var stagingDir = Path.Combine(
                Path.GetTempPath(),
                "TLAHStudioUpdater",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);

            var stagedUpdaterPath = Path.Combine(stagingDir, Path.GetFileName(updaterPath));
            File.Copy(updaterPath, stagedUpdaterPath, overwrite: true);

            // Development and older publish layouts are multi-file. Copying top-level
            // files keeps that fallback runnable, while single-file updater releases
            // all install-directory DLL locks after this method returns.
            if (File.Exists(Path.Combine(sourceDir, "TLAHStudio.Updater.dll")))
            {
                foreach (var file in Directory.EnumerateFiles(sourceDir))
                {
                    var target = Path.Combine(stagingDir, Path.GetFileName(file));
                    if (!File.Exists(target))
                        File.Copy(file, target, overwrite: true);
                }
            }

            return stagedUpdaterPath;
        }
        catch
        {
            return updaterPath;
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
