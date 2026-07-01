using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

public sealed record ExecutionRequest(
    Guid ChatId,
    string Command,
    int TimeoutSeconds,
    int MaxOutputChars,
    string PermissionMode = AgentPermissionModes.RequestApproval);

public sealed record ExecutionResult(
    string Backend,
    string WorkingDirectory,
    int ExitCode,
    bool TimedOut,
    TimeSpan Duration,
    string StandardOutput,
    string StandardError,
    string? BlockedReason = null)
{
    public bool Success => BlockedReason == null && !TimedOut && ExitCode == 0;
}

public interface IExecutionBackendRouter
{
    Task<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        string? backend = null,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, bool>> GetAvailabilityAsync(CancellationToken ct = default);
}

public sealed class ExecutionBackendRouter : IExecutionBackendRouter
{
    private readonly ISandboxCommandService _sandbox;
    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;

    public ExecutionBackendRouter(
        ISandboxCommandService sandbox,
        IToolPlatformService platform,
        INetworkSecurityService network,
        IHttpClientFactory httpClientFactory)
    {
        _sandbox = sandbox;
        _platform = platform;
        _network = network;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        string? backend = null,
        CancellationToken ct = default)
    {
        var settings = await _platform.GetSettingsAsync(ct);
        backend = string.IsNullOrWhiteSpace(backend) ? settings.DefaultBackend : backend;
        if (AgentPermissionModes.IsBypass(request.PermissionMode) &&
            (string.IsNullOrWhiteSpace(backend) ||
             string.Equals(backend, ToolExecutionBackends.RestrictedLocal, StringComparison.OrdinalIgnoreCase)))
        {
            backend = ToolExecutionBackends.UnrestrictedLocal;
        }
        var timeout = Math.Clamp(
            Math.Min(request.TimeoutSeconds, settings.MaxRuntimeSeconds),
            1,
            600);
        var outputLimit = Math.Clamp(
            Math.Min(request.MaxOutputChars, settings.MaxOutputChars),
            1000,
            200000);
        request = request with { TimeoutSeconds = timeout, MaxOutputChars = outputLimit };

        return backend switch
        {
            ToolExecutionBackends.UnrestrictedLocal => await ExecuteUnrestrictedLocalAsync(request, ct),
            ToolExecutionBackends.Wsl => await ExecuteWslAsync(request, settings, ct),
            ToolExecutionBackends.Docker => await ExecuteDockerAsync(request, settings, ct),
            ToolExecutionBackends.Remote => await ExecuteRemoteAsync(request, settings, ct),
            _ => await ExecuteRestrictedLocalAsync(request, ct)
        };
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetAvailabilityAsync(CancellationToken ct = default)
    {
        var settings = await _platform.GetSettingsAsync(ct);
        return new Dictionary<string, bool>
        {
            [ToolExecutionBackends.RestrictedLocal] = true,
            [ToolExecutionBackends.UnrestrictedLocal] = true,
            [ToolExecutionBackends.Wsl] = await CanStartAsync("wsl.exe", ["--status"], ct),
            [ToolExecutionBackends.Docker] = await CanStartAsync("docker.exe", ["version", "--format", "{{.Server.Version}}"], ct),
            [ToolExecutionBackends.Remote] =
                Uri.TryCreate(settings.RemoteEndpoint, UriKind.Absolute, out _)
        };
    }

    private async Task<ExecutionResult> ExecuteRestrictedLocalAsync(
        ExecutionRequest request,
        CancellationToken ct)
    {
        var result = await _sandbox.ExecuteAsync(
            request.ChatId,
            request.Command,
            new SandboxCommandOptions(request.TimeoutSeconds, request.MaxOutputChars),
            ct);
        return new ExecutionResult(
            ToolExecutionBackends.RestrictedLocal,
            result.WorkingDirectory,
            result.ExitCode,
            result.TimedOut,
            result.Duration,
            result.StandardOutput,
            result.StandardError,
            result.BlockedReason);
    }

    private async Task<ExecutionResult> ExecuteUnrestrictedLocalAsync(
        ExecutionRequest request,
        CancellationToken ct)
    {
        var workingDirectory = _sandbox.GetSandboxRoot(request.ChatId);
        var args = new[]
        {
            "-NoLogo",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            SandboxCommandService.WithUtf8Console(request.Command)
        };
        return await RunProcessAsync(
            ToolExecutionBackends.UnrestrictedLocal,
            ResolvePowerShellPath(),
            args,
            workingDirectory,
            request,
            ct);
    }

    private async Task<ExecutionResult> ExecuteWslAsync(
        ExecutionRequest request,
        ToolPlatformSettings settings,
        CancellationToken ct)
    {
        var root = _sandbox.GetSandboxRoot(request.ChatId);
        var wslPath = ToWslPath(root);
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.WslDistribution))
        {
            args.Add("-d");
            args.Add(settings.WslDistribution);
        }
        args.Add("--");
        args.Add("bash");
        args.Add("-lc");
        args.Add($"cd {QuoteBash(wslPath)} && {request.Command}");
        return await RunProcessAsync(
            ToolExecutionBackends.Wsl, "wsl.exe", args, root, request, ct);
    }

    private async Task<ExecutionResult> ExecuteDockerAsync(
        ExecutionRequest request,
        ToolPlatformSettings settings,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.DockerImage))
            return Blocked(ToolExecutionBackends.Docker, _sandbox.GetSandboxRoot(request.ChatId), "Docker image is not configured.");

        var root = _sandbox.GetSandboxRoot(request.ChatId);
        var args = new List<string>
        {
            "run", "--rm", "--network", "none",
            "--pids-limit", settings.MaxProcesses.ToString(),
            "--memory", $"{settings.MaxMemoryMb}m",
            "--cpus", "1",
            "-v", $"{root}:/workspace",
            "-w", "/workspace",
            settings.DockerImage,
            "pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", request.Command
        };
        return await RunProcessAsync(
            ToolExecutionBackends.Docker, "docker.exe", args, root, request, ct);
    }

    private async Task<ExecutionResult> ExecuteRemoteAsync(
        ExecutionRequest request,
        ToolPlatformSettings settings,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.RemoteEndpoint))
            return Blocked(ToolExecutionBackends.Remote, string.Empty, "Remote sandbox endpoint is not configured.");

        var uri = await _network.ValidateAsync(settings.RemoteEndpoint, settings, ct);
        using var message = new HttpRequestMessage(HttpMethod.Post, uri);
        message.Content = new StringContent(JsonSerializer.Serialize(new
        {
            command = request.Command,
            timeoutSeconds = request.TimeoutSeconds,
            maxOutputChars = request.MaxOutputChars,
            maxMemoryMb = settings.MaxMemoryMb,
            maxProcesses = settings.MaxProcesses,
            workspace = request.ChatId.ToString("N")
        }), Encoding.UTF8, "application/json");

        if (!string.IsNullOrWhiteSpace(settings.RemoteCredentialName))
        {
            var secret = await _platform.ResolveCredentialAsync(
                settings.RemoteCredentialName,
                AgentToolNames.TerminalExec,
                uri.IdnHost,
                ct);
            if (string.IsNullOrWhiteSpace(secret))
                return Blocked(ToolExecutionBackends.Remote, string.Empty, "Remote sandbox credential is unavailable or not permitted.");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        var started = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        try
        {
            using var response = await _httpClientFactory.CreateClient("Tools")
                .SendAsync(message, timeoutCts.Token);
            var text = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return new ExecutionResult(
                ToolExecutionBackends.Remote,
                root.TryGetProperty("workingDirectory", out var wd) ? wd.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("exitCode", out var code) ? code.GetInt32() : (int)response.StatusCode,
                root.TryGetProperty("timedOut", out var timedOut) && timedOut.GetBoolean(),
                started.Elapsed,
                Limit(root.TryGetProperty("stdout", out var stdout) ? stdout.GetString() ?? string.Empty : text, request.MaxOutputChars),
                Limit(root.TryGetProperty("stderr", out var stderr) ? stderr.GetString() ?? string.Empty : string.Empty, request.MaxOutputChars),
                response.IsSuccessStatusCode ? null : $"Remote sandbox returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ExecutionResult(
                ToolExecutionBackends.Remote, string.Empty, -1, true, started.Elapsed,
                string.Empty, string.Empty, "Remote sandbox request timed out.");
        }
    }

    private static async Task<ExecutionResult> RunProcessAsync(
        string backend,
        string executable,
        IReadOnlyList<string> args,
        string workingDirectory,
        ExecutionRequest request,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["TLAH_SANDBOX"] = "1";

        using var process = new Process { StartInfo = psi };
        var started = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return Blocked(backend, workingDirectory, $"{backend} is unavailable: {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }

        var stdout = await SafeAwaitAsync(stdoutTask);
        var stderr = await SafeAwaitAsync(stderrTask);
        return new ExecutionResult(
            backend,
            workingDirectory,
            process.HasExited ? process.ExitCode : -1,
            timedOut,
            started.Elapsed,
            SecretRedactor.RedactText(Limit(stdout, request.MaxOutputChars)),
            SecretRedactor.RedactText(Limit(stderr, request.MaxOutputChars)),
            timedOut ? "Execution timed out." : null);
    }

    private static async Task<bool> CanStartAsync(
        string executable,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var request = new ExecutionRequest(Guid.Empty, string.Empty, 3, 1000);
        var result = await RunProcessAsync("probe", executable, args, Environment.CurrentDirectory, request, ct);
        return result.BlockedReason == null && !result.TimedOut && result.ExitCode == 0;
    }

    private static string ToWslPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < 3 || full[1] != ':')
            return full.Replace('\\', '/');
        return $"/mnt/{char.ToLowerInvariant(full[0])}/{full[3..].Replace('\\', '/')}";
    }

    private static string QuoteBash(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static string ResolvePowerShellPath()
    {
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var windowsPowerShell = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windowsPowerShell) ? windowsPowerShell : "powershell.exe";
    }

    private static ExecutionResult Blocked(string backend, string workingDirectory, string reason) =>
        new(backend, workingDirectory, -1, false, TimeSpan.Zero, string.Empty, string.Empty, reason);

    private static string Limit(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n[output truncated]";

    private static async Task<string> SafeAwaitAsync(Task<string> task)
    {
        try { return await task; }
        catch { return string.Empty; }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
