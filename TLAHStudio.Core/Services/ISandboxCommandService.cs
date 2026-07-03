namespace TLAHStudio.Core.Services;

public interface ISandboxCommandService
{
    string GetSandboxRoot(Guid chatId);

    Task<SandboxCommandResult> ExecuteAsync(
        Guid chatId,
        string command,
        SandboxCommandOptions? options = null,
        CancellationToken ct = default);
}

public sealed record SandboxCommandOptions(
    int TimeoutSeconds = 20,
    int MaxOutputChars = 12000);

public sealed record SandboxCommandResult(
    string Command,
    string WorkingDirectory,
    int ExitCode,
    bool TimedOut,
    TimeSpan Duration,
    string StandardOutput,
    string StandardError,
    string? BlockedReason = null,
    string? DestructiveWarning = null)
{
    public bool WasBlocked => !string.IsNullOrWhiteSpace(BlockedReason);
}
