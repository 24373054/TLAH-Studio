using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;

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
}

public sealed class AgentEventStream : IAgentEventStream
{
    private readonly DbContext _db;

    public AgentEventStream(DbContext db)
    {
        _db = db;
    }

    public async Task<AgentEvent> AppendAsync(
        AgentEventAppendRequest request,
        CancellationToken ct = default)
    {
        var sequenceNumber = await _db.Set<AgentEvent>()
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
            SequenceNumber = sequenceNumber + 1,
            EventType = request.EventType,
            Severity = request.Severity,
            Summary = SecretRedactor.RedactText(request.Summary),
            DataJson = dataJson,
            CreatedAt = DateTime.UtcNow
        };
        _db.Set<AgentEvent>().Add(agentEvent);
        await _db.SaveChangesAsync(ct);
        return agentEvent;
    }
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

public sealed record ToolExecutionPlanItem(string ToolName, string ArgumentsJson);

public sealed record ToolExecutionBatch(
    IReadOnlyList<ToolExecutionPlanItem> Items,
    bool Concurrent);

public sealed record ToolExecutionRequest(
    AgentRun Run,
    ToolInvocation Invocation,
    int TimeoutSeconds,
    int MaxOutputChars);

public sealed record ToolExecutionOutcome(
    ToolExecutionRequest Request,
    IAgentTool? Tool,
    AgentToolMetadata Metadata,
    ToolSafetyAssessment Safety,
    AgentToolResult Result);

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
    private readonly ISandboxCommandService _sandbox;

    public ToolExecutionScheduler(IAgentToolRegistry registry, ISandboxCommandService sandbox)
    {
        _registry = registry;
        _sandbox = sandbox;
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
    {
        var toolName = AgentToolNames.Normalize(request.Invocation.ToolName);
        if (!_registry.TryGet(toolName, out var tool))
        {
            var metadata = AgentToolMetadata.For(toolName, requiresApproval: true);
            var safety = ToolSafetyAssessment.Blocked(
                "tool",
                $"Unknown tool: {toolName}",
                "The requested tool is not registered.");
            return new ToolExecutionOutcome(
                request,
                null,
                metadata,
                safety,
                new AgentToolResult(false, string.Empty, safety.Warning ?? safety.Summary));
        }

        var validation = tool.ValidateInput(request.Invocation.ArgumentsJson);
        if (!validation.Success)
        {
            var safety = ToolSafetyAssessment.Blocked(
                "protocol",
                "Tool arguments failed validation.",
                validation.Error ?? "Invalid tool arguments.");
            return new ToolExecutionOutcome(
                request,
                tool,
                tool.Metadata,
                safety,
                new AgentToolResult(false, string.Empty, validation.Error));
        }

        var safetyAssessment = ToolSafetyKernel.Assess(
            _sandbox,
            request.Run.ChatId,
            toolName,
            request.Invocation.ArgumentsJson);
        if (safetyAssessment.IsBlocked)
        {
            return new ToolExecutionOutcome(
                request,
                tool,
                tool.Metadata,
                safetyAssessment,
                new AgentToolResult(
                    false,
                    string.Empty,
                    $"Safety policy blocked this tool call. {safetyAssessment.Warning ?? safetyAssessment.Summary}"));
        }

        var maxOutputChars = Math.Max(
            1,
            Math.Min(request.MaxOutputChars, tool.Metadata.MaxResultSizeChars));
        var result = await tool.ExecuteAsync(
            new AgentToolExecutionContext(
                request.Run.ChatId,
                request.Run.Id,
                request.Invocation.Id,
                request.TimeoutSeconds,
                maxOutputChars),
            request.Invocation.ArgumentsJson,
            ct);

        return new ToolExecutionOutcome(
            request,
            tool,
            tool.Metadata,
            safetyAssessment,
            LimitResult(result, maxOutputChars));
    }

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

    private static AgentToolResult LimitResult(AgentToolResult result, int maxChars)
    {
        if (result.Output.Length <= maxChars)
            return result;

        return result with
        {
            Output = result.Output[..maxChars] + "\n[tool output truncated by scheduler]"
        };
    }
}

public interface IAgentRunEngine
{
    Task<SendMessageResult> ContinueAsync(
        AgentRun run,
        Func<CancellationToken, Task<SendMessageResult>> continuation,
        CancellationToken ct = default);
}

public sealed class AgentRunEngine : IAgentRunEngine
{
    public Task<SendMessageResult> ContinueAsync(
        AgentRun run,
        Func<CancellationToken, Task<SendMessageResult>> continuation,
        CancellationToken ct = default)
    {
        run.UpdatedAt = DateTime.UtcNow;
        return continuation(ct);
    }
}
