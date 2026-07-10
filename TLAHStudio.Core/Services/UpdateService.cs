using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Reflection;
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
    private const string DefaultUpdateCheckUrl = "https://download.matrixlabs.cn/tlah/windows/latest.json";

    public string CurrentVersion { get; }
    public string UpdateCheckUrl => _updateCheckUrl;
    public string InstallId => _installId;

    public UpdateService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _manifestPublicKeyBase64 = UpdateCrypto.PublicKeyBase64;

        _installPath = ResolveInstallPath();
        var state = ReadVersionState(_installPath, AppContext.BaseDirectory);
        CurrentVersion = state.CurrentVersion;
        _updateCheckUrl = state.UpdateCheckUrl;
        _installId = GetOrCreateInstallId(state.InstallId);
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

    internal static string DefaultInstallIdPath
    {
        get
        {
            var appDataRoot = Environment.GetEnvironmentVariable("TLAH_STUDIO_APPDATA_ROOT");
            if (string.IsNullOrWhiteSpace(appDataRoot))
            {
                appDataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TLAH Studio");
            }

            return Path.Combine(appDataRoot, "config", "install-id.txt");
        }
    }

    internal static string GetOrCreateInstallId(string? preferredInstallId, string? statePath = null)
    {
        statePath ??= DefaultInstallIdPath;
        try
        {
            if (File.Exists(statePath))
            {
                var existing = File.ReadAllText(statePath).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                    return existing;
            }
        }
        catch
        {
            // Fall through to a usable in-memory ID when local state is unreadable.
        }

        var installId = string.IsNullOrWhiteSpace(preferredInstallId)
            ? GenerateInstallId()
            : preferredInstallId.Trim();
        var tempPath = statePath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(tempPath, installId, new UTF8Encoding(false));
            try
            {
                File.Move(tempPath, statePath, overwrite: false);
            }
            catch (IOException)
            {
                // Another process may have won the first-start race. Its ID is
                // authoritative so every process remains in the same rollout bucket.
                var winner = File.Exists(statePath) ? File.ReadAllText(statePath).Trim() : string.Empty;
                if (!string.IsNullOrWhiteSpace(winner))
                    return winner;

                File.Move(tempPath, statePath, overwrite: true);
            }
        }
        catch
        {
            // Updates still work when the config directory is temporarily read-only;
            // the generated ID simply cannot be guaranteed across this restart.
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }

        return installId;
    }

    internal static int GetRolloutBucket(string installId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(installId ?? string.Empty));
        return (int)(BinaryPrimitives.ReadUInt32BigEndian(hash) % 100);
    }

    internal sealed record VersionState(
        string CurrentVersion,
        string UpdateCheckUrl,
        string InstallId,
        string? SourcePath);

    internal static string DefaultInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "TLAH Studio");

    internal static string ResolveInstallPath(string? baseDirectory = null)
    {
        var baseDir = NormalizeDirectory(baseDirectory ?? AppContext.BaseDirectory);
        if (File.Exists(Path.Combine(baseDir, "version.json")) ||
            File.Exists(Path.Combine(baseDir, "TLAHStudio.App.exe")))
        {
            return baseDir;
        }

        return DefaultInstallPath;
    }

    internal static VersionState ReadVersionState(string installPath, string? baseDirectory = null)
    {
        var fallbackVersion = GetAssemblyVersion();
        var paths = new[]
            {
                Path.Combine(NormalizeDirectory(installPath), "version.json"),
                Path.Combine(NormalizeDirectory(baseDirectory ?? AppContext.BaseDirectory), "version.json")
            }
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var versionPath in paths)
        {
            if (!File.Exists(versionPath))
                continue;

            try
            {
                var json = File.ReadAllText(versionPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var version = root.TryGetProperty("version", out var versionElement)
                    ? versionElement.GetString()
                    : null;
                var updateUrl = root.TryGetProperty("updateUrl", out var urlElement)
                    ? urlElement.GetString()
                    : null;
                var installId = root.TryGetProperty("installId", out var installIdElement)
                    ? installIdElement.GetString()
                    : null;

                return new VersionState(
                    string.IsNullOrWhiteSpace(version) ? fallbackVersion : version,
                    string.IsNullOrWhiteSpace(updateUrl) ? DefaultUpdateCheckUrl : updateUrl,
                    string.IsNullOrWhiteSpace(installId) ? GenerateInstallId() : installId,
                    versionPath);
            }
            catch
            {
                return new VersionState(
                    fallbackVersion,
                    DefaultUpdateCheckUrl,
                    GenerateInstallId(),
                    versionPath);
            }
        }

        return new VersionState(
            fallbackVersion,
            DefaultUpdateCheckUrl,
            GenerateInstallId(),
            null);
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string GetAssemblyVersion()
    {
        var assembly = typeof(UpdateService).Assembly;
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+')[0]
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
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

            var signatureUrl = root.TryGetProperty("signatureUrl", out var signatureUrlElement)
                ? signatureUrlElement.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(signatureUrl) &&
                (!Uri.TryCreate(signatureUrl, UriKind.Absolute, out var signatureUri) ||
                 signatureUri.Scheme != Uri.UriSchemeHttps ||
                 !Uri.TryCreate(_updateCheckUrl, UriKind.Absolute, out var manifestUri) ||
                 !string.Equals(signatureUri.Host, manifestUri.Host, StringComparison.OrdinalIgnoreCase)))
                return null;

            // 2. Verify latest.json signature
            if (_manifestPublicKeyBase64 != "REPLACE_WITH_YOUR_PUBLIC_KEY")
            {
                var sigValid = await UpdateCrypto.VerifyLatestJsonAsync(
                    client, _updateCheckUrl, jsonText, _manifestPublicKeyBase64, ct, signatureUrl);
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
                var rolloutPercent = Math.Clamp(rp.GetInt32(), 0, 100);
                if (rolloutPercent == 0)
                    return null;
                if (rolloutPercent < 100)
                {
                    var bucket = GetRolloutBucket(_installId);
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
            Arguments = $"{QuoteArgument(installerPath)} {Environment.ProcessId} {QuoteArgument(_installPath)}",
            UseShellExecute = true
        });
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
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
