using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services.Background;

/// <summary>
/// M2.13.0: Background task status and record.
/// </summary>
public sealed record BackgroundTask(
    Guid Id, Guid ChatId, string Description, string Status,
    DateTime StartedAt, DateTime? CompletedAt, string? ResultSummary, string? Error,
    string Kind = "task", string? OutputPath = null, string InputJson = "{}", string? LastMessage = null);

/// <summary>
/// M2.13.0: Background task service for long-running operations.
/// Tasks survive app restarts via database persistence.
/// </summary>
public interface IBackgroundTaskService
{
    Task<BackgroundTask> CreateAsync(
        Guid chatId,
        string description,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default,
        Guid? taskId = null,
        string kind = "task",
        string? outputPath = null,
        string inputJson = "{}");
    Task<IReadOnlyList<BackgroundTask>> ListAsync(Guid? chatId = null, CancellationToken ct = default);
    Task<BackgroundTask?> GetAsync(Guid taskId, CancellationToken ct = default);
    Task StopAsync(Guid taskId, CancellationToken ct = default);
    Task SendMessageAsync(Guid taskId, string message, CancellationToken ct = default);
}

public class BackgroundTaskService : IBackgroundTaskService
{
    private readonly DbContext _db;
    private readonly Type _dbContextType;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cts, BackgroundTask Task)> _running = new();

    public BackgroundTaskService(DbContext db)
    {
        _db = db;
        _dbContextType = db.GetType();
        _connectionString = db.Database.GetDbConnection().ConnectionString;
    }

    public async Task<BackgroundTask> CreateAsync(
        Guid chatId,
        string description,
        Func<CancellationToken, Task> action,
        CancellationToken ct = default,
        Guid? taskId = null,
        string kind = "task",
        string? outputPath = null,
        string inputJson = "{}")
    {
        var task = new BackgroundTask(taskId ?? Guid.NewGuid(), chatId, description,
            "running", DateTime.UtcNow, null, null, null, kind, outputPath, inputJson);

        await _dbGate.WaitAsync(ct);
        try
        {
            _db.Set<BackgroundTaskRecord>().Add(new BackgroundTaskRecord
            {
                Id = task.Id, ChatId = task.ChatId, Description = task.Description,
                Kind = task.Kind,
                Status = "running", StartedAt = task.StartedAt,
                OutputPath = task.OutputPath,
                InputJson = task.InputJson
            });
            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            _dbGate.Release();
        }

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

    public async Task<IReadOnlyList<BackgroundTask>> ListAsync(Guid? chatId = null, CancellationToken ct = default)
    {
        await _dbGate.WaitAsync(ct);
        try
        {
            var query = _db.Set<BackgroundTaskRecord>().AsQueryable();
            if (chatId.HasValue)
                query = query.Where(r => r.ChatId == chatId.Value);

            var tasks = await query.OrderByDescending(r => r.StartedAt).Take(20).Select(r =>
                new BackgroundTask(r.Id, r.ChatId, r.Description, r.Status,
                    r.StartedAt, r.CompletedAt, r.ResultSummary, r.Error,
                    r.Kind, r.OutputPath, r.InputJson, r.LastMessage)).ToListAsync(ct);
            return tasks;
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public async Task<BackgroundTask?> GetAsync(Guid taskId, CancellationToken ct = default)
    {
        // Check in-memory first
        if (_running.TryGetValue(taskId, out var running))
            return running.Task;

        await _dbGate.WaitAsync(ct);
        try
        {
            var record = await _db.Set<BackgroundTaskRecord>().FirstOrDefaultAsync(r => r.Id == taskId, ct);
            if (record == null) return null;

            return new BackgroundTask(
                record.Id, record.ChatId, record.Description, record.Status,
                record.StartedAt, record.CompletedAt, record.ResultSummary, record.Error,
                record.Kind, record.OutputPath, record.InputJson, record.LastMessage);
        }
        finally
        {
            _dbGate.Release();
        }
    }

    public Task StopAsync(Guid taskId, CancellationToken ct = default)
    {
        if (_running.TryRemove(taskId, out var running))
            running.Cts.Cancel();
        return Task.CompletedTask;
    }

    public async Task SendMessageAsync(Guid taskId, string message, CancellationToken ct = default)
    {
        string? outputPath = null;
        await _dbGate.WaitAsync(ct);
        try
        {
            var record = await _db.Set<BackgroundTaskRecord>().FirstOrDefaultAsync(r => r.Id == taskId, ct);
            if (record == null)
                return;

            record.LastMessage = message;
            outputPath = record.OutputPath;
            await _db.SaveChangesAsync(ct);
        }
        finally
        {
            _dbGate.Release();
        }

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(
                outputPath,
                $"{Environment.NewLine}[message {DateTime.UtcNow:O}] {message}{Environment.NewLine}",
                ct);
        }
    }

    private async Task UpdateRecordAsync(BackgroundTask task)
    {
        await using var db = CreateIndependentDbContext();
        var record = await db.Set<BackgroundTaskRecord>().FindAsync(task.Id);
        if (record != null)
        {
            record.Status = task.Status;
            record.CompletedAt = task.CompletedAt;
            record.ResultSummary = task.ResultSummary;
            record.Error = task.Error;
            record.OutputPath = task.OutputPath;
            record.InputJson = task.InputJson;
            record.LastMessage = task.LastMessage;
            await db.SaveChangesAsync();
        }
    }

    private DbContext CreateIndependentDbContext()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("Background task persistence requires a database connection string.");

        var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(_dbContextType);
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(builderType)!;
        builder.UseSqlite(_connectionString);
        var options = builderType.GetProperty(nameof(DbContextOptionsBuilder.Options))!.GetValue(builder);
        return (DbContext)Activator.CreateInstance(_dbContextType, options!)!;
    }
}

/// <summary>
/// EF entity for persisting background task state across app restarts.
/// </summary>
public class BackgroundTaskRecord
{
    public Guid Id { get; set; }
    public Guid ChatId { get; set; }
    public string Kind { get; set; } = "task";
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? ResultSummary { get; set; }
    public string? Error { get; set; }
    public string? OutputPath { get; set; }
    public string InputJson { get; set; } = "{}";
    public string? LastMessage { get; set; }
}
