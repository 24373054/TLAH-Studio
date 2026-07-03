using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.UI.Xaml;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace TLAHStudio.App.ViewModels;

// ─────────────────────────────────────────────────────────────────────
// Settings helper — uses file-based persistence to avoid WinUI init dependency
// ─────────────────────────────────────────────────────────────────────

internal static class LocalStore
{
    private static string StoreDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLAH Studio", "config");

    public static string? Get(string key)
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            var path = Path.Combine(StoreDir, key);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    public static void Set(string key, string value)
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            File.WriteAllText(Path.Combine(StoreDir, key), value);
        }
        catch { /* ignore */ }
    }

    public static void ClearAll()
    {
        try
        {
            if (Directory.Exists(StoreDir))
                Directory.Delete(StoreDir, recursive: true);
        }
        catch { /* ignore */ }
    }
}

// ─────────────────────────────────────────────────────────────────────
// App State Service — global app-level state (current chat, etc.)
// ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Holds global app state: currently selected chat ID.
/// Fires events when selection changes so other ViewModels can react.
/// </summary>
public interface IAppStateService
{
    Guid? CurrentChatId { get; }
    event EventHandler<Guid>? ChatSelected;
    event EventHandler? ChatDeselected;
    Task SelectChatAsync(Guid chatId);
    void ClearSelection();
}

public class AppStateService : IAppStateService
{
    public Guid? CurrentChatId { get; private set; }

    public event EventHandler<Guid>? ChatSelected;
    public event EventHandler? ChatDeselected;

    public Task SelectChatAsync(Guid chatId)
    {
        CurrentChatId = chatId;
        ChatSelected?.Invoke(this, chatId);
        return Task.CompletedTask;
    }

    public void ClearSelection()
    {
        CurrentChatId = null;
        ChatDeselected?.Invoke(this, EventArgs.Empty);
    }
}

// ─────────────────────────────────────────────────────────────────────
// Theme Service — dark/light toggle
// Replaces: ThemeContext.tsx
// ─────────────────────────────────────────────────────────────────────

public interface IThemeService
{
    ElementTheme CurrentTheme { get; }
    void Initialize();
    void ToggleTheme();
    void SetTheme(ElementTheme theme); // M4.7.0
}

public record ElementTheme(string Value)
{
    public static readonly ElementTheme Dark = new("dark");
    public static readonly ElementTheme Light = new("light");
}

public class ThemeService : IThemeService
{
    private const string StorageKey = "tlah-theme";

    public ElementTheme CurrentTheme { get; private set; }

    public ThemeService()
    {
        var stored = LocalStore.Get(StorageKey);
        CurrentTheme = stored == "light" ? ElementTheme.Light : ElementTheme.Dark;
        // Don't call ApplyTheme() in constructor — Application.Current is not ready yet
    }

    public void Initialize()
    {
        ApplyTheme();
    }

    public void ToggleTheme()
    {
        SetTheme(CurrentTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark);
    }

    public void SetTheme(ElementTheme theme)
    {
        CurrentTheme = theme;
        LocalStore.Set(StorageKey, CurrentTheme.Value);
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = CurrentTheme == ElementTheme.Dark
                ? Microsoft.UI.Xaml.ElementTheme.Dark
                : Microsoft.UI.Xaml.ElementTheme.Light;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────
// Background Service — custom background image, opacity
// Replaces: BackgroundContext.tsx
// ─────────────────────────────────────────────────────────────────────

public record BgConfig(
    string? Image,
    int Brightness,
    int Opacity,
    int ChatOpacity
)
{
    public static readonly BgConfig Default = new(null, 100, 30, 100);
}

public interface IBackgroundService
{
    BgConfig GetConfig();
    void UpdateConfig(BgConfig config);
    void ResetConfig();
    event EventHandler<BgConfig>? ConfigChanged;
}

public class BackgroundImageService : IBackgroundService
{
    private const string StorageKey = "tlah-bg";
    private BgConfig _config;

    public event EventHandler<BgConfig>? ConfigChanged;

    public BackgroundImageService()
    {
        _config = LoadConfig();
    }

    public BgConfig GetConfig() => _config;

    public void UpdateConfig(BgConfig config)
    {
        _config = config;
        SaveConfig(config);
        ConfigChanged?.Invoke(this, config);
    }

    public void ResetConfig()
    {
        UpdateConfig(BgConfig.Default);
    }

    private static BgConfig LoadConfig()
    {
        try
        {
            var raw = LocalStore.Get(StorageKey);
            if (!string.IsNullOrEmpty(raw))
            {
                var parts = raw.Split('|');
                if (parts.Length >= 4)
                {
                    return new BgConfig(
                        Image: string.IsNullOrEmpty(parts[0]) ? null : parts[0],
                        Brightness: int.TryParse(parts[1], out var b) ? b : 100,
                        Opacity: int.TryParse(parts[2], out var o) ? o : 30,
                        ChatOpacity: int.TryParse(parts[3], out var c) ? c : 100
                    );
                }
            }
        }
        catch { }

        return BgConfig.Default;
    }

    private static void SaveConfig(BgConfig config)
    {
        var raw = $"{config.Image ?? ""}|{config.Brightness}|{config.Opacity}|{config.ChatOpacity}";
        LocalStore.Set(StorageKey, raw);
    }
}

// ─────────────────────────────────────────────────────────────────────
// UI Density Service — comfortable/compact display density
// ─────────────────────────────────────────────────────────────────────

public enum UiDensity
{
    Comfortable,
    Compact
}

public interface IUiDensityService
{
    UiDensity CurrentDensity { get; }
    event EventHandler<UiDensity>? DensityChanged;
    void ToggleDensity();
    void SetDensity(UiDensity density); // M4.7.0
}

public class UiDensityService : IUiDensityService
{
    private const string StorageKey = "tlah-density";

    public UiDensity CurrentDensity { get; private set; }

    public event EventHandler<UiDensity>? DensityChanged;

    public UiDensityService()
    {
        CurrentDensity = string.Equals(LocalStore.Get(StorageKey), "compact", StringComparison.OrdinalIgnoreCase)
            ? UiDensity.Compact
            : UiDensity.Comfortable;
    }

    public void ToggleDensity()
    {
        SetDensity(CurrentDensity == UiDensity.Comfortable ? UiDensity.Compact : UiDensity.Comfortable);
    }

    public void SetDensity(UiDensity density)
    {
        CurrentDensity = density;
        LocalStore.Set(StorageKey, CurrentDensity == UiDensity.Compact ? "compact" : "comfortable");
        DensityChanged?.Invoke(this, CurrentDensity);
    }
}

// ─────────────────────────────────────────────────────────────────────
// Interaction Soundscape Service — small, generated local WAV accents
// ─────────────────────────────────────────────────────────────────────

public enum InteractionSound
{
    Launch,
    Navigate,
    Toggle,
    Send,
    Receive,
    Complete,
    Approval,
    Delete,
    Error
}

public interface IInteractionSoundService
{
    bool IsEnabled { get; }
    double Volume { get; }
    event EventHandler? SettingsChanged;
    void SetEnabled(bool enabled);
    void SetVolume(double volume);
    void Play(InteractionSound sound);
}

public sealed class InteractionSoundService : IInteractionSoundService
{
    private const string EnabledKey = "tlah-sound-enabled";
    private const string VolumeKey = "tlah-sound-volume";
    private readonly Dictionary<InteractionSound, string> _soundFiles;

    public bool IsEnabled { get; private set; }
    public double Volume { get; private set; }
    public event EventHandler? SettingsChanged;

    public InteractionSoundService()
    {
        IsEnabled = !string.Equals(LocalStore.Get(EnabledKey), "false", StringComparison.OrdinalIgnoreCase);
        Volume = double.TryParse(LocalStore.Get(VolumeKey), out var volume)
            ? Math.Clamp(volume, 0.0, 1.0)
            : 0.62;
        _soundFiles = EnsureSoundFiles();
    }

    public void SetEnabled(bool enabled)
    {
        var changed = IsEnabled != enabled;
        IsEnabled = enabled;
        LocalStore.Set(EnabledKey, enabled ? "true" : "false");
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (enabled && changed)
            Play(InteractionSound.Toggle);
    }

    public void SetVolume(double volume)
    {
        Volume = Math.Clamp(volume, 0.0, 1.0);
        LocalStore.Set(VolumeKey, Volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Play(InteractionSound sound)
    {
        if (!IsEnabled || Volume <= 0.001 || !_soundFiles.TryGetValue(sound, out var path) || !File.Exists(path))
            return;

        try
        {
            var player = new MediaPlayer
            {
                AutoPlay = false,
                Volume = Volume
            };
            player.MediaEnded += (_, _) => player.Dispose();
            player.MediaFailed += (_, _) => player.Dispose();
            player.Source = MediaSource.CreateFromUri(new Uri(path));
            player.Play();
        }
        catch (Exception ex)
        {
            App.Log($"SOUND PLAY FAILED: {sound}: {ex.Message}");
        }
    }

    private static Dictionary<InteractionSound, string> EnsureSoundFiles()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio",
            "cache",
            "soundscape-v2");
        Directory.CreateDirectory(dir);

        var files = new Dictionary<InteractionSound, string>
        {
            [InteractionSound.Launch] = Path.Combine(dir, "launch.wav"),
            [InteractionSound.Navigate] = Path.Combine(dir, "navigate.wav"),
            [InteractionSound.Toggle] = Path.Combine(dir, "toggle.wav"),
            [InteractionSound.Send] = Path.Combine(dir, "send.wav"),
            [InteractionSound.Receive] = Path.Combine(dir, "receive.wav"),
            [InteractionSound.Complete] = Path.Combine(dir, "complete.wav"),
            [InteractionSound.Approval] = Path.Combine(dir, "approval.wav"),
            [InteractionSound.Delete] = Path.Combine(dir, "delete.wav"),
            [InteractionSound.Error] = Path.Combine(dir, "error.wav")
        };

        TryCreate(files[InteractionSound.Launch], 0.34, t => Layer(t, 196, 0.18) + Layer(t, 294, 0.14) + Layer(t, 392, 0.12), 0.035);
        TryCreate(files[InteractionSound.Navigate], 0.11, t => Glide(t, 360, 520, 0.18) + Layer(t, 720, 0.04), 0.018);
        TryCreate(files[InteractionSound.Toggle], 0.12, t => Layer(t, 520, 0.16) + Layer(t, 780, 0.07), 0.018);
        TryCreate(files[InteractionSound.Send], 0.15, t => Glide(t, 430, 920, 0.20) + Layer(t, 1280, 0.05), 0.018);
        TryCreate(files[InteractionSound.Receive], 0.19, t => Layer(t, 523.25, 0.12) + Layer(t, 659.25, 0.10) + Layer(t, 880, 0.06), 0.025);
        TryCreate(files[InteractionSound.Complete], 0.24, t => Layer(t, 440, 0.11) + Layer(t, 660, 0.09) + Layer(t, 990, 0.06), 0.028);
        TryCreate(files[InteractionSound.Approval], 0.18, t => Glide(t, 300, 420, 0.12) + Layer(t, 840, 0.08), 0.028);
        TryCreate(files[InteractionSound.Delete], 0.14, t => Glide(t, 230, 110, 0.18) + Layer(t, 82, 0.08), 0.012);
        TryCreate(files[InteractionSound.Error], 0.22, t => Layer(t, 110, 0.18) + Layer(t, 146.8, 0.09), 0.012);

        return files;
    }

    private static void TryCreate(string path, double durationSeconds, Func<double, double> sample, double shimmer)
    {
        try
        {
            if (File.Exists(path))
                return;
            WriteWave(path, durationSeconds, sample, shimmer);
        }
        catch (Exception ex)
        {
            App.Log($"SOUND CREATE FAILED: {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static double Layer(double t, double frequency, double gain) =>
        Math.Sin(2 * Math.PI * frequency * t) * gain;

    private static double Glide(double t, double startFrequency, double endFrequency, double gain)
    {
        var duration = 0.2;
        var progress = Math.Clamp(t / duration, 0, 1);
        var frequency = startFrequency + (endFrequency - startFrequency) * EaseOut(progress);
        return Math.Sin(2 * Math.PI * frequency * t) * gain;
    }

    private static double EaseOut(double x) => 1 - Math.Pow(1 - x, 3);

    private static void WriteWave(string path, double durationSeconds, Func<double, double> sample, double shimmer)
    {
        const int sampleRate = 48000;
        const short channels = 2;
        const short bitsPerSample = 16;
        var sampleCount = (int)Math.Round(durationSeconds * sampleRate);
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var dataLength = sampleCount * channels * bitsPerSample / 8;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);

        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleRate;
            var position = i / (double)Math.Max(1, sampleCount - 1);
            var envelope = Envelope(position);
            var air = Math.Sin(2 * Math.PI * 4300 * t) * shimmer;
            var value = Math.Clamp((sample(t) + air) * envelope, -0.98, 0.98);
            var left = (short)(value * short.MaxValue);
            var right = (short)(value * (0.94 + 0.04 * Math.Sin(2 * Math.PI * 7 * t)) * short.MaxValue);
            writer.Write(left);
            writer.Write(right);
        }
    }

    private static double Envelope(double x)
    {
        var attack = Math.Clamp(x / 0.12, 0, 1);
        var release = Math.Clamp((1 - x) / 0.22, 0, 1);
        return Math.Pow(Math.Min(attack, release), 1.6);
    }
}

// ─────────────────────────────────────────────────────────────────────
// Release and Diagnostics Service — app trust, version, support bundle
// ─────────────────────────────────────────────────────────────────────

public record AppReleaseSnapshot(
    string CurrentVersion,
    string LatestVersion,
    string Channel,
    string InstallerUrl,
    long? InstallerSizeBytes,
    string Sha256,
    string SignatureStatus,
    string CertificateSubject,
    string CertificateThumbprint,
    string UpdateCheckUrl,
    string DownloadPageUrl,
    string LogsDirectory,
    string InstallDirectory,
    DateTime CheckedAtUtc)
{
    public string InstallerSizeText => InstallerSizeBytes is > 0
        ? FormatBytes(InstallerSizeBytes.Value)
        : "Unknown";

    private static string FormatBytes(long bytes)
    {
        var mb = bytes / 1024d / 1024d;
        return $"{mb:0.0} MB";
    }
}

public interface IAppReleaseService
{
    Task<AppReleaseSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    Task<string> GetDiagnosticsPreviewAsync(CancellationToken ct = default);
    Task<string> ExportDiagnosticsAsync(CancellationToken ct = default);
}

public class AppReleaseService : IAppReleaseService
{
    private const string DownloadPageUrl = "https://download.matrixlabs.cn";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUpdateService _updateService;
    private readonly ISettingsService _settingsService;

    public AppReleaseService(
        IHttpClientFactory httpClientFactory,
        IUpdateService updateService,
        ISettingsService settingsService)
    {
        _httpClientFactory = httpClientFactory;
        _updateService = updateService;
        _settingsService = settingsService;
    }

    public async Task<AppReleaseSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var latestVersion = _updateService.CurrentVersion;
        var channel = "stable";
        var installerUrl = string.Empty;
        string sha256 = string.Empty;
        long? sizeBytes = null;

        try
        {
            var client = _httpClientFactory.CreateClient("Update");
            var json = await client.GetStringAsync(_updateService.UpdateCheckUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            latestVersion = ReadString(root, "version", latestVersion);
            channel = ReadString(root, "channel", channel);
            installerUrl = ReadString(root, "installerUrl", installerUrl);
            sha256 = ReadString(root, "sha256", sha256);
            sizeBytes = ReadLong(root, "installerSizeBytes")
                ?? ReadLong(root, "sizeBytes")
                ?? ReadLong(root, "installerSize");
        }
        catch
        {
            // About page should remain available offline.
        }

        var signature = InspectCurrentSignature();
        return new AppReleaseSnapshot(
            CurrentVersion: _updateService.CurrentVersion,
            LatestVersion: latestVersion,
            Channel: channel,
            InstallerUrl: installerUrl,
            InstallerSizeBytes: sizeBytes,
            Sha256: sha256,
            SignatureStatus: signature.Status,
            CertificateSubject: signature.Subject,
            CertificateThumbprint: signature.Thumbprint,
            UpdateCheckUrl: _updateService.UpdateCheckUrl,
            DownloadPageUrl: DownloadPageUrl,
            LogsDirectory: App.LogDir,
            InstallDirectory: AppContext.BaseDirectory,
            CheckedAtUtc: DateTime.UtcNow);
    }

    public async Task<string> ExportDiagnosticsAsync(CancellationToken ct = default)
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "TLAH Studio",
            "Diagnostics");
        Directory.CreateDirectory(downloads);

        var exportPath = Path.Combine(downloads, $"tlah-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var tempRoot = Path.Combine(Path.GetTempPath(), "TLAHStudioDiagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var snapshot = await GetSnapshotAsync(ct);
            var diagnostic = await BuildDiagnosticObjectAsync(snapshot, ct);

            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "diagnostics.json"),
                TLAHStudio.Core.Helpers.SecretRedactor.RedactJson(JsonSerializer.Serialize(diagnostic, new JsonSerializerOptions { WriteIndented = true })),
                ct);

            CopyIfExists(Path.Combine(AppContext.BaseDirectory, "version.json"), Path.Combine(tempRoot, "version.json"));
            CopyLogs(tempRoot);

            if (File.Exists(exportPath))
                File.Delete(exportPath);
            ZipFile.CreateFromDirectory(tempRoot, exportPath);
            return exportPath;
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    public async Task<string> GetDiagnosticsPreviewAsync(CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct);
        var diagnostic = await BuildDiagnosticObjectAsync(snapshot, ct);
        return TLAHStudio.Core.Helpers.SecretRedactor.RedactJson(
            JsonSerializer.Serialize(diagnostic, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<object> BuildDiagnosticObjectAsync(AppReleaseSnapshot snapshot, CancellationToken ct)
    {
        var settings = await BuildSettingsSummaryAsync(ct);
        var recentErrors = ReadRecentErrors();
        return new
        {
            ExportedAtUtc = DateTime.UtcNow,
            App = new
            {
                snapshot.CurrentVersion,
                snapshot.LatestVersion,
                snapshot.Channel,
                snapshot.UpdateCheckUrl,
                snapshot.DownloadPageUrl,
                snapshot.SignatureStatus,
                snapshot.CertificateSubject,
                snapshot.CertificateThumbprint
            },
            System = new
            {
                Environment.OSVersion.VersionString,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture,
                RuntimeInformation.FrameworkDescription,
                Is64BitProcess = Environment.Is64BitProcess
            },
            Settings = settings,
            RecentErrors = recentErrors
        };
    }

    private async Task<object> BuildSettingsSummaryAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _settingsService.GetGlobalSettingsMaskedAsync(ct);
            return new
            {
                settings.Provider,
                settings.BaseUrl,
                settings.Model,
                settings.Temperature,
                settings.MaxTokens,
                settings.UserRole,
                HasApiKey = !string.IsNullOrWhiteSpace(settings.ApiKey),
                SystemPromptLength = settings.SystemPrompt?.Length ?? 0
            };
        }
        catch (Exception ex)
        {
            return new { Error = ex.Message };
        }
    }

    private static IReadOnlyList<string> ReadRecentErrors()
    {
        try
        {
            var logPath = Path.Combine(App.LogDir, "startup.log");
            if (!File.Exists(logPath))
                return Array.Empty<string>();

            return File.ReadLines(logPath)
                .Where(line =>
                    line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("UNHANDLED", StringComparison.OrdinalIgnoreCase))
                .TakeLast(80)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void CopyLogs(string tempRoot)
    {
        try
        {
            if (!Directory.Exists(App.LogDir))
                return;

            var logsDir = Path.Combine(tempRoot, "logs");
            Directory.CreateDirectory(logsDir);
            foreach (var file in Directory.EnumerateFiles(App.LogDir, "*.log"))
                CopyIfExists(file, Path.Combine(logsDir, Path.GetFileName(file)));
        }
        catch
        {
        }
    }

    private static void CopyIfExists(string source, string target)
    {
        try
        {
            if (File.Exists(source))
                File.Copy(source, target, overwrite: true);
        }
        catch
        {
        }
    }

    private static (string Status, string Subject, string Thumbprint) InspectCurrentSignature()
    {
        try
        {
            var path = Environment.ProcessPath
                ?? Assembly.GetEntryAssembly()?.Location
                ?? Path.Combine(AppContext.BaseDirectory, "TLAHStudio.App.exe");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return ("Signature file not found", string.Empty, string.Empty);

            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var trusted = chain.Build(cert);
            var status = trusted
                ? "Signed and trusted"
                : "Signed with untrusted or self-signed certificate";
            return (status, cert.Subject, cert.Thumbprint ?? string.Empty);
        }
        catch
        {
            return ("Unsigned", string.Empty, string.Empty);
        }
    }

    private static string ReadString(JsonElement root, string name, string fallback) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static long? ReadLong(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)
            ? number
            : null;
    }
}
