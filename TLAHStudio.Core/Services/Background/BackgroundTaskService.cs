using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services.Background;

/// <summary>
/// M2.13.0: Background task status and record.
/// </summary>
public sealed record BackgroundTask(
    Guid Id, Guid ChatId, string Description, string Status,
    DateTime StartedAt, DateTime? CompletedAt, string? ResultSummary, string? Error);

/// <summary>
/// M2.13.0: Background task service for long-running operations.
/// Tasks survive app restarts via database persistence.
/// </summary>
public interface IBackgroundTaskService
{
    Task<BackgroundTask> CreateAsync(Guid chatId, string description, Func<CancellationToken, Task> action, CancellationToken ct = default);
    Task<IReadOnlyList<BackgroundTask>> ListAsync(Guid? chatId = null, CancellationToken ct = default);
    Task<BackgroundTask?> GetAsync(Guid taskId, CancellationToken ct = default);
    Task StopAsync(Guid taskId, CancellationToken ct = default);
}

public class BackgroundTaskService : IBackgroundTaskService
{
    private readonly DbContext _db;
    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cts, BackgroundTask Task)> _running = new();

    public BackgroundTaskService(DbContext db) => _db = db;

    public async Task<BackgroundTask> CreateAsync(
        Guid chatId, string description, Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        var task = new BackgroundTask(Guid.NewGuid(), chatId, description,
            "running", DateTime.UtcNow, null, null, null);

        // Persist to DB
        _db.Set<BackgroundTaskRecord>().Add(new BackgroundTaskRecord
        {
            Id = task.Id, ChatId = task.ChatId, Description = task.Description,
            Status = "running", StartedAt = task.StartedAt
        });
        await _db.SaveChangesAsync(ct);

        var cts = new CancellationTokenSource();
        _running[task.Id] = (cts, task);

        _ = Task.Run(async () =>
        {
            try
            {
                await action(cts.Token);
                var completed = task with
                {
                    Status = "completed", CompletedAt = DateTime.UtcNow, ResultSummary = "Task completed successfully."
                };
                _running[task.Id] = (cts, completed);
                await UpdateRecordAsync(completed);
            }
            catch (OperationCanceledException)
            {
                var cancelled = task with
                {
                    Status = "cancelled", CompletedAt = DateTime.UtcNow, Error = "Cancelled by user."
                };
                _running[task.Id] = (cts, cancelled);
                await UpdateRecordAsync(cancelled);
            }
            catch (Exception ex)
            {
                var failed = task with
                {
                    Status = "failed", CompletedAt = DateTime.UtcNow, Error = ex.Message
                };
                _running[task.Id] = (cts, failed);
                await UpdateRecordAsync(failed);
            }
        }, ct);

        return task;
    }

    public Task<IReadOnlyList<BackgroundTask>> ListAsync(Guid? chatId = null, CancellationToken ct = default)
    {
        var query = _db.Set<BackgroundTaskRecord>().AsQueryable();
        if (chatId.HasValue)
            query = query.Where(r => r.ChatId == chatId.Value);

        var tasks = query.OrderByDescending(r => r.StartedAt).Take(20).Select(r =>
            new BackgroundTask(r.Id, r.ChatId, r.Description, r.Status,
                r.StartedAt, r.CompletedAt, r.ResultSummary, r.Error)).ToList();
        return Task.FromResult<IReadOnlyList<BackgroundTask>>(tasks);
    }

    public Task<BackgroundTask?> GetAsync(Guid taskId, CancellationToken ct = default)
    {
        // Check in-memory first
        if (_running.TryGetValue(taskId, out var running))
            return Task.FromResult<BackgroundTask?>(running.Task);

        var record = _db.Set<BackgroundTaskRecord>().FirstOrDefault(r => r.Id == taskId);
        if (record == null) return Task.FromResult<BackgroundTask?>(null);

        return Task.FromResult<BackgroundTask?>(new BackgroundTask(
            record.Id, record.ChatId, record.Description, record.Status,
            record.StartedAt, record.CompletedAt, record.ResultSummary, record.Error));
    }

    public Task StopAsync(Guid taskId, CancellationToken ct = default)
    {
        if (_running.TryRemove(taskId, out var running))
            running.Cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task UpdateRecordAsync(BackgroundTask task)
    {
        var record = await _db.Set<BackgroundTaskRecord>().FindAsync(task.Id);
        if (record != null)
        {
            record.Status = task.Status;
            record.CompletedAt = task.CompletedAt;
            record.ResultSummary = task.ResultSummary;
            record.Error = task.Error;
            await _db.SaveChangesAsync();
        }
    }
}

/// <summary>
/// EF entity for persisting background task state across app restarts.
/// </summary>
public class BackgroundTaskRecord
{
    public Guid Id { get; set; }
    public Guid ChatId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? ResultSummary { get; set; }
    public string? Error { get; set; }
}
