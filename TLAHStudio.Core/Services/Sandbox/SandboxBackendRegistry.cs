using System.Diagnostics;
using TLAHStudio.Core.Services.Workspace;

namespace TLAHStudio.Core.Services.Sandbox;

/// <summary>
/// M2.13.0: Sandbox backend capability status.
/// </summary>
public sealed record SandboxBackendCapability(
    string BackendName, string Status, string Details, bool IsAvailable);

/// <summary>
/// M2.13.0: Resource limits per sandbox backend.
/// </summary>
public sealed record SandboxResourceLimits(
    int MaxRuntimeSeconds, int MaxOutputChars, int MaxMemoryMb,
    int MaxProcesses, long MaxFileSizeBytes, bool AllowNetwork);

/// <summary>
/// M2.13.0: Registry that detects available sandbox backends and their capabilities.
/// </summary>
public interface ISandboxBackendRegistry
{
    Task<IReadOnlyList<SandboxBackendCapability>> DetectCapabilitiesAsync(CancellationToken ct = default);
    SandboxResourceLimits GetLimits(string backend);
}

public class SandboxBackendRegistry : ISandboxBackendRegistry
{
    public async Task<IReadOnlyList<SandboxBackendCapability>> DetectCapabilitiesAsync(CancellationToken ct = default)
    {
        return new List<SandboxBackendCapability>
        {
            new("restricted_local", "available", "Always available — runs in-process with resource limits.", true),
            new("wsl", await CanStartAsync("wsl.exe", ["--status"], ct) ? "available" : "unavailable",
                "Windows Subsystem for Linux", await CanStartAsync("wsl.exe", ["--status"], ct)),
            new("docker", await CanStartAsync("docker.exe", ["version"], ct) ? "available" : "unavailable",
                "Docker Engine", await CanStartAsync("docker.exe", ["version"], ct)),
            new("remote", "configured", "Remote sandbox endpoint", true)
        };
    }

    public SandboxResourceLimits GetLimits(string backend) => backend switch
    {
        "wsl" => new SandboxResourceLimits(300, 100_000, 2048, 16, 100_000_000, true),
        "docker" => new SandboxResourceLimits(300, 100_000, 512, 8, 50_000_000, false),
        "remote" => new SandboxResourceLimits(600, 200_000, 1024, 16, 200_000_000, true),
        _ => new SandboxResourceLimits(30, 20_000, 512, 8, 10_000_000, true)
    };

    private static async Task<bool> CanStartAsync(string exe, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }
}

/// <summary>
/// M2.13.0: File synchronization between host and sandbox.
/// </summary>
public interface IFileSyncService
{
    Task SyncUploadAsync(Guid chatId, IEnumerable<string> localPaths, CancellationToken ct = default);
    Task<IReadOnlyList<AgentToolArtifact>> SyncExportAsync(
        Guid chatId, IEnumerable<string> sandboxPaths, string exportDir, CancellationToken ct = default);
}

public class FileSyncService : IFileSyncService
{
    public Task SyncUploadAsync(Guid chatId, IEnumerable<string> localPaths, CancellationToken ct = default)
    {
        var sandboxRoot = WorkspaceRootStore.GetRoot(chatId, out _);
        Directory.CreateDirectory(sandboxRoot);

        foreach (var localPath in localPaths)
        {
            if (!File.Exists(localPath)) continue;
            var destPath = Path.Combine(sandboxRoot, Path.GetFileName(localPath));
            File.Copy(localPath, destPath, overwrite: true);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentToolArtifact>> SyncExportAsync(
        Guid chatId, IEnumerable<string> sandboxPaths, string exportDir, CancellationToken ct = default)
    {
        var sandboxRoot = WorkspaceRootStore.GetRoot(chatId, out _);
        Directory.CreateDirectory(exportDir);
        var artifacts = new List<AgentToolArtifact>();

        foreach (var path in sandboxPaths)
        {
            var srcPath = Path.Combine(sandboxRoot, path);
            if (!File.Exists(srcPath)) continue;

            var destPath = Path.Combine(exportDir, Path.GetFileName(path));
            File.Copy(srcPath, destPath, overwrite: true);
            var fi = new FileInfo(destPath);
            var sha256 = ComputeSha256(destPath);
            artifacts.Add(new AgentToolArtifact(path, "application/octet-stream", fi.Length, sha256));
        }

        return Task.FromResult<IReadOnlyList<AgentToolArtifact>>(artifacts);
    }

    private static string ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch { return "unknown"; }
    }
}
