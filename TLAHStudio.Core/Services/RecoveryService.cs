using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

/// <summary>
/// M3.0.0: Run and update recovery service.
/// Handles incomplete agent runs on startup and update interruptions.
/// </summary>
public interface IRecoveryService
{
    Task<IReadOnlyList<AgentRun>> GetIncompleteRunsAsync(CancellationToken ct = default);
    Task AutoRecoverAsync(CancellationToken ct = default);
    Task<bool> NeedsUpdateRecoveryAsync(CancellationToken ct = default);
    Task RecoverUpdateAsync(CancellationToken ct = default);
}

public class RecoveryService : IRecoveryService
{
    private readonly DbContext _db;

    public RecoveryService(DbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<AgentRun>> GetIncompleteRunsAsync(CancellationToken ct = default)
    {
        return await _db.Set<AgentRun>()
            .Where(r => r.Status == AgentRunStatuses.Paused ||
                        r.Status == AgentRunStatuses.AwaitingApproval)
            .Include(r => r.Chat)
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync(ct);
    }

    public async Task AutoRecoverAsync(CancellationToken ct = default)
    {
        // Mark any 'running' runs as 'paused' (in case of crash without checkpoint save)
        var running = await _db.Set<AgentRun>()
            .Where(r => r.Status == AgentRunStatuses.Running)
            .ToListAsync(ct);

        foreach (var run in running)
        {
            run.Status = AgentRunStatuses.Paused;
            run.ErrorMessage = "The application closed while this run was active. Resume to continue.";
            run.UpdatedAt = DateTime.UtcNow;
        }

        if (running.Count > 0)
            await _db.SaveChangesAsync(ct);
    }

    public Task<bool> NeedsUpdateRecoveryAsync(CancellationToken ct = default)
    {
        var signalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio", "update-in-progress.signal");
        return Task.FromResult(File.Exists(signalPath));
    }

    public Task RecoverUpdateAsync(CancellationToken ct = default)
    {
        var signalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio", "update-in-progress.signal");
        if (File.Exists(signalPath))
            File.Delete(signalPath);
        return Task.CompletedTask;
    }
}
