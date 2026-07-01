using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services.AgentRuntime;
using TLAHStudio.Core.Services.Tools.Models;

namespace TLAHStudio.Core.Services;

public sealed record AgentEventAppendRequest(
    AgentRun Run,
    string EventType,
    string Summary,
    object? Data = null,
    Guid? StepId = null,
    Guid? ToolInvocationId = null,
    string Severity = AgentEventSeverities.Info);

public sealed record AgentRuntimeStreamUpdate(
    string EventType,
    string Delta,
    string Snapshot,
    DateTime CreatedAt);

public interface IAgentEventStream
{
    Task<AgentEvent> AppendAsync(AgentEventAppendRequest request, CancellationToken ct = default);
    IDisposable BeginRun(AgentRun run);
    Task FlushAsync(CancellationToken ct = default);
    AgentEventStreamMetrics GetMetrics();
}

public sealed class AgentEventStream : IAgentEventStream
{
    private sealed class EventBuffer
    {
        public Guid RunId { get; init; }
        public int NextSequenceNumber { get; set; }
        public List<AgentEvent> Pending { get; } = [];
        public int AppendedCount { get; set; }
        public int FlushCount { get; set; }
        public TimeSpan DbWriteTime { get; set; }
        public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    }

    private sealed class EventBufferScope : IDisposable
    {
        private readonly EventBuffer? _previous;
        private bool _disposed;

        public EventBufferScope(EventBuffer? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            CurrentBuffer.Value = _previous;
            _disposed = true;
        }
    }

    private static readonly AsyncLocal<EventBuffer?> CurrentBuffer = new();
    private readonly DbContext _db;
    private readonly IAgentEventSubscriptionService? _subscriptions;
    private AgentEventStreamMetrics _lastMetrics = AgentEventStreamMetrics.Empty;

    public AgentEventStream(DbContext db, IAgentEventSubscriptionService? subscriptions = null)
    {
        _db = db;
        _subscriptions = subscriptions;
    }

    public IDisposable BeginRun(AgentRun run)
    {
        var sequenceNumber = _db.Set<AgentEvent>()
            .Where(e => e.AgentRunId == run.Id)
            .Select(e => (int?)e.SequenceNumber)
            .Max() ?? 0;
        var previous = CurrentBuffer.Value;
        CurrentBuffer.Value = new EventBuffer
        {
            RunId = run.Id,
            NextSequenceNumber = sequenceNumber + 1
        };
        return new EventBufferScope(previous);
    }

    public async Task<AgentEvent> AppendAsync(
        AgentEventAppendRequest request,
        CancellationToken ct = default)
    {
        var buffer = CurrentBuffer.Value;
        var sequenceNumber = buffer?.RunId == request.Run.Id
            ? buffer.NextSequenceNumber++
            : await _db.Set<AgentEvent>()
                .Where(e => e.AgentRunId == request.Run.Id)
                .Select(e => (int?)e.SequenceNumber)
                .MaxAsync(ct) ?? 0;
        var dataJson = request.Data == null
            ? "{}"
            : SecretRedactor.RedactJson(JsonSerializer.Serialize(request.Data));
        var agentEvent = new AgentEvent
        {
            AgentRunId = request.Run.Id,
            AgentStepId = request.StepId,
            ToolInvocationId = request.ToolInvocationId,
            SequenceNumber = buffer?.RunId == request.Run.Id ? sequenceNumber : sequenceNumber + 1,
            EventType = request.EventType,
            Severity = request.Severity,
            Summary = SecretRedactor.RedactText(request.Summary),
            DataJson = dataJson,
            CreatedAt = DateTime.UtcNow
        };

        if (buffer?.RunId == request.Run.Id)
        {
            buffer.Pending.Add(agentEvent);
            buffer.AppendedCount++;
            _subscriptions?.Publish(request.Run.Id, agentEvent);
            return agentEvent;
        }

        _db.Set<AgentEvent>().Add(agentEvent);
        await _db.SaveChangesAsync(ct);
        _subscriptions?.Publish(request.Run.Id, agentEvent);
        return agentEvent;
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        var buffer = CurrentBuffer.Value;
        if (buffer == null || buffer.Pending.Count == 0)
            return;

        var pending = buffer.Pending.ToArray();
        buffer.Pending.Clear();
        var started = DateTime.UtcNow;
        _db.Set<AgentEvent>().AddRange(pending);
        await _db.SaveChangesAsync(ct);
        buffer.FlushCount++;
        buffer.DbWriteTime += DateTime.UtcNow - started;
        _lastMetrics = new AgentEventStreamMetrics(
            buffer.AppendedCount,
            buffer.FlushCount,
            buffer.Pending.Count,
            buffer.DbWriteTime,
            DateTime.UtcNow - buffer.StartedAt);
    }

    public AgentEventStreamMetrics GetMetrics()
    {
        var buffer = CurrentBuffer.Value;
        return buffer == null
            ? _lastMetrics
            : new AgentEventStreamMetrics(
                buffer.AppendedCount,
                buffer.FlushCount,
                buffer.Pending.Count,
                buffer.DbWriteTime,
                DateTime.UtcNow - buffer.StartedAt);
    }
}

public sealed record AgentEventStreamMetrics(
    int EventCount,
    int FlushCount,
    int PendingCount,
    TimeSpan DbWriteTime,
    TimeSpan Elapsed)
{
    public static AgentEventStreamMetrics Empty { get; } =
        new(0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);
}

public interface ICheckpointStore
{
    Task SaveAsync(AgentRun run, int stepNumber, string stateJson, CancellationToken ct = default);
    Task<AgentCheckpoint?> GetLatestAsync(Guid agentRunId, CancellationToken ct = default);
}

public sealed class CheckpointStore : ICheckpointStore
{
    private readonly DbContext _db;

    public CheckpointStore(DbContext db)
    {
        _db = db;
    }

    public async Task SaveAsync(
        AgentRun run,
        int stepNumber,
        string stateJson,
        CancellationToken ct = default)
    {
        _db.Set<AgentCheckpoint>().Add(new AgentCheckpoint
        {
            AgentRunId = run.Id,
            StepNumber = stepNumber,
            StateJson = stateJson
        });
        await _db.SaveChangesAsync(ct);
    }

    public Task<AgentCheckpoint?> GetLatestAsync(
        Guid agentRunId,
        CancellationToken ct = default) =>
        _db.Set<AgentCheckpoint>()
            .Where(c => c.AgentRunId == agentRunId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
}

public sealed record ProviderStreamRequest(
    ILlmProvider Provider,
    List<MessagePayload> Messages,
    string SystemPrompt,
    double Temperature,
    int MaxTokens,
    IReadOnlyList<LlmToolDefinition>? Tools = null,
    IProgress<LlmStreamUpdate>? OutputStream = null,
    IProgress<AgentRuntimeStreamUpdate>? RuntimeStream = null,
    LlmReasoningOptions? Reasoning = null);

public interface IProviderStreamAdapter
{
    Task<LlmResponse> ChatAsync(ProviderStreamRequest request, CancellationToken ct = default);
}

public sealed class ProviderStreamAdapter : IProviderStreamAdapter
{
    public Task<LlmResponse> ChatAsync(
        ProviderStreamRequest request,
        CancellationToken ct = default)
    {
        IProgress<LlmStreamUpdate>? stream = request.OutputStream;
        if (request.RuntimeStream != null)
            stream = new BridgedLlmStreamProgress(request.OutputStream, request.RuntimeStream);

        return request.Provider.ChatAsync(
            request.Messages,
            request.SystemPrompt,
            request.Temperature,
            request.MaxTokens,
            request.Tools,
            stream,
            request.Reasoning,
            ct);
    }

    private sealed class BridgedLlmStreamProgress(
        IProgress<LlmStreamUpdate>? output,
        IProgress<AgentRuntimeStreamUpdate> runtime)
        : IProgress<LlmStreamUpdate>
    {
        public void Report(LlmStreamUpdate value)
        {
            output?.Report(value);
            runtime.Report(new AgentRuntimeStreamUpdate(
                value.EventType,
                value.Delta,
                value.Snapshot,
                DateTime.UtcNow));
        }
    }
}

public sealed record ToolExecutionPlanItem(string ToolName, string ArgumentsJson, string? ToolCallId = null);

public sealed record ToolExecutionBatch(
    IReadOnlyList<ToolExecutionPlanItem> Items,
    bool Concurrent);

public sealed record ToolExecutionRequest(
    AgentRun Run,
    ToolInvocation Invocation,
    int TimeoutSeconds,
    int MaxOutputChars,
    string PermissionMode = AgentPermissionModes.RequestApproval);

public sealed record ToolExecutionOutcome
{
    public ToolExecutionOutcome(
        ToolExecutionRequest request,
        IAgentTool? tool,
        AgentToolMetadata metadata,
        ToolSafetyAssessment safety,
        AgentToolResult result,
        ToolEffectPlan? effectPlan = null,
        ToolRollbackPlan? rollbackPlan = null,
        IReadOnlyList<AgentToolProgress>? progressEvents = null)
    {
        Request = request;
        Tool = tool;
        Metadata = metadata;
        Safety = safety;
        Result = result;
        EffectPlan = effectPlan;
        RollbackPlan = rollbackPlan;
        ProgressEvents = progressEvents ?? [];
    }

    public ToolExecutionRequest Request { get; init; }
    public IAgentTool? Tool { get; init; }
    public AgentToolMetadata Metadata { get; init; }
    public ToolSafetyAssessment Safety { get; init; }
    public AgentToolResult Result { get; init; }
    public ToolEffectPlan? EffectPlan { get; init; }
    public ToolRollbackPlan? RollbackPlan { get; init; }
    public IReadOnlyList<AgentToolProgress> ProgressEvents { get; init; }
}

public interface IToolExecutionScheduler
{
    IReadOnlyList<ToolExecutionBatch> PlanBatches(IReadOnlyList<ToolExecutionPlanItem> items);
    Task<ToolExecutionOutcome> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ToolExecutionOutcome>> ExecuteBatchAsync(
        IReadOnlyList<ToolExecutionRequest> requests,
        CancellationToken ct = default);
}

public sealed class ToolExecutionScheduler : IToolExecutionScheduler
{
    private readonly IAgentToolRegistry _registry;
    private readonly IToolLifecycleRunner _lifecycleRunner;

    public ToolExecutionScheduler(
        IAgentToolRegistry registry,
        ISandboxCommandService sandbox,
        IToolLifecycleRunner? lifecycleRunner = null)
    {
        _registry = registry;
        _lifecycleRunner = lifecycleRunner ?? new DefaultToolLifecycleRunner(registry, sandbox);
    }

    public IReadOnlyList<ToolExecutionBatch> PlanBatches(IReadOnlyList<ToolExecutionPlanItem> items)
    {
        var batches = new List<ToolExecutionBatch>();
        var concurrent = new List<ToolExecutionPlanItem>();

        foreach (var item in items)
        {
            var canRunConcurrent =
                _registry.TryGet(item.ToolName, out var tool) &&
                tool.Metadata.IsReadOnly &&
                tool.Metadata.IsConcurrencySafe;

            if (canRunConcurrent)
            {
                concurrent.Add(item);
                continue;
            }

            FlushConcurrent();
            batches.Add(new ToolExecutionBatch([item], Concurrent: false));
        }

        FlushConcurrent();
        return batches;

        void FlushConcurrent()
        {
            if (concurrent.Count == 0)
                return;
            batches.Add(new ToolExecutionBatch(concurrent.ToArray(), Concurrent: true));
            concurrent.Clear();
        }
    }

    public async Task<ToolExecutionOutcome> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default)
        => await _lifecycleRunner.ExecuteAsync(request, ct);

    public async Task<IReadOnlyList<ToolExecutionOutcome>> ExecuteBatchAsync(
        IReadOnlyList<ToolExecutionRequest> requests,
        CancellationToken ct = default)
    {
        var remaining = requests.ToList();
        var plan = PlanBatches(requests
            .Select(r => new ToolExecutionPlanItem(r.Invocation.ToolName, r.Invocation.ArgumentsJson))
            .ToArray());
        var results = new List<ToolExecutionOutcome>();

        foreach (var batch in plan)
        {
            var batchRequests = batch.Items.Select(item => TakeNext(remaining, item)).ToArray();
            if (batch.Concurrent)
            {
                var tasks = batchRequests.Select(r => ExecuteAsync(r, ct)).ToArray();
                results.AddRange(await Task.WhenAll(tasks));
            }
            else
            {
                foreach (var request in batchRequests)
                    results.Add(await ExecuteAsync(request, ct));
            }
        }

        return results;
    }

    private static ToolExecutionRequest TakeNext(
        List<ToolExecutionRequest> remaining,
        ToolExecutionPlanItem item)
    {
        var normalized = AgentToolNames.Normalize(item.ToolName);
        var index = remaining.FindIndex(r =>
            string.Equals(AgentToolNames.Normalize(r.Invocation.ToolName), normalized, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Invocation.ArgumentsJson, item.ArgumentsJson, StringComparison.Ordinal));
        if (index < 0)
            throw new InvalidOperationException($"Scheduled tool request was not found: {item.ToolName}");

        var request = remaining[index];
        remaining.RemoveAt(index);
        return request;
    }

}

public interface IAgentRunEngine
{
    Task<SendMessageResult> ContinueAsync(
        AgentRun run,
        IAgentEventStream eventStream,
        Func<CancellationToken, Task<SendMessageResult>> continuation,
        CancellationToken ct = default);
}

public sealed class AgentRunEngine : IAgentRunEngine
{
    public async Task<SendMessageResult> ContinueAsync(
        AgentRun run,
        IAgentEventStream eventStream,
        Func<CancellationToken, Task<SendMessageResult>> continuation,
        CancellationToken ct = default)
    {
        using var scope = eventStream.BeginRun(run);
        run.UpdatedAt = DateTime.UtcNow;
        try
        {
            var result = await continuation(ct);
            await AppendRuntimeMetricsAsync(run, eventStream, ct);
            await eventStream.FlushAsync(ct);
            return result;
        }
        catch
        {
            await AppendRuntimeMetricsAsync(run, eventStream, CancellationToken.None);
            await eventStream.FlushAsync(CancellationToken.None);
            throw;
        }
    }

    private static Task AppendRuntimeMetricsAsync(
        AgentRun run,
        IAgentEventStream eventStream,
        CancellationToken ct)
    {
        var metrics = eventStream.GetMetrics();
        return eventStream.AppendAsync(
            new AgentEventAppendRequest(
                run,
                AgentEventTypes.RuntimeMetrics,
                "Agent runtime metrics captured.",
                new
                {
                    metrics.EventCount,
                    metrics.FlushCount,
                    metrics.PendingCount,
                    dbWriteMs = Math.Round(metrics.DbWriteTime.TotalMilliseconds, 2),
                    elapsedMs = Math.Round(metrics.Elapsed.TotalMilliseconds, 2)
                }),
            ct);
    }
}
