using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;

#pragma warning disable CA1416

namespace TLAHStudio.Core.Services.AgentRuntime;

/// <summary>
/// The real agent run state machine. Owns the while-loop for agent execution.
/// M2.7.0: Extracted from LlmService.ContinueAgentRunInternalAsync.
/// </summary>
public interface IAgentRunEngineV2
{
    /// <summary>
    /// Run the agent loop to completion or until paused for approval.
    /// Emits frames on each state transition for UI/SDK consumption.
    /// </summary>
    Task<AgentRunResult> RunAsync(
        AgentRunState state,
        AgentEngineOptions options,
        IProgress<AgentRunFrame>? frameProgress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resume a paused agent run (after approval).
    /// </summary>
    Task<AgentRunResult> ResumeAsync(
        AgentRunState state,
        AgentEngineOptions options,
        IProgress<AgentRunFrame>? frameProgress = null,
        CancellationToken ct = default);
}

public sealed record AgentRunResult(
    AgentRunState FinalState,
    string? AssistantContent,
    LlmResponse? LastResponse,
    List<AgentEvent> Events);

public class AgentRunEngineV2 : IAgentRunEngineV2
{
    private readonly DbContext _db;
    private readonly IChatService _chatService;
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISandboxCommandService _sandboxCommandService;
    private readonly IAgentToolRegistry _agentTools;
    private readonly IToolPlatformService _toolPlatform;
    private readonly IAgentEventStream _eventStream;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IProviderStreamAdapter _providerStreamAdapter;
    private readonly IToolExecutionScheduler _toolExecutionScheduler;
    private readonly IAgentContextManager _contextManager;
    private readonly IProjectMemoryService _projectMemory;
    private readonly IToolResultPersistenceService _toolResultPersistence;

    public AgentRunEngineV2(
        DbContext db,
        IChatService chatService,
        ISettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ISandboxCommandService sandboxCommandService,
        IAgentToolRegistry agentTools,
        IToolPlatformService toolPlatform,
        IAgentEventStream eventStream,
        ICheckpointStore checkpointStore,
        IProviderStreamAdapter providerStreamAdapter,
        IToolExecutionScheduler toolExecutionScheduler,
        IAgentContextManager contextManager,
        IProjectMemoryService projectMemory,
        IToolResultPersistenceService toolResultPersistence)
    {
        _db = db;
        _chatService = chatService;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _sandboxCommandService = sandboxCommandService;
        _agentTools = agentTools;
        _toolPlatform = toolPlatform;
        _eventStream = eventStream;
        _checkpointStore = checkpointStore;
        _providerStreamAdapter = providerStreamAdapter;
        _toolExecutionScheduler = toolExecutionScheduler;
        _contextManager = contextManager;
        _projectMemory = projectMemory;
        _toolResultPersistence = toolResultPersistence;
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunState state,
        AgentEngineOptions options,
        IProgress<AgentRunFrame>? frameProgress = null,
        CancellationToken ct = default)
    {
        using var scope = _eventStream.BeginRun(new AgentRun { Id = state.RunId });
        var events = new List<AgentEvent>();
        var contextOptions = BuildContextOptions(options);
        string? assistantContent = null;
        LlmResponse? lastResponse = null;

        try
        {
            // Build system prompt with memory
            var systemPrompt = await BuildSystemPromptAsync(state.ChatId, ct);
            var provider = LlmProviderFactory.Create(
                _httpClientFactory.CreateClient("LLM"),
                (await _settingsService.GetEffectiveSettingsAsync(state.ChatId, ct)).Provider,
                (await _settingsService.GetEffectiveSettingsAsync(state.ChatId, ct)).ApiKey,
                (await _settingsService.GetEffectiveSettingsAsync(state.ChatId, ct)).BaseUrl,
                (await _settingsService.GetEffectiveSettingsAsync(state.ChatId, ct)).Model);
            var effective = await _settingsService.GetEffectiveSettingsAsync(state.ChatId, ct);

            // Pre-loop: handle pending approval from resume
            var pending = await _db.Set<ToolInvocation>()
                .Include(i => i.AgentStep)
                .Where(i => i.AgentRunId == state.RunId &&
                    (i.Status == ToolInvocationStatuses.Approved ||
                     i.Status == ToolInvocationStatuses.Denied))
                .OrderBy(i => i.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (pending != null)
            {
                var pendingResult = await ExecuteSingleInvocationAsync(
                    state, pending, options, ct);
                events.AddRange(pendingResult.Events);
                if (pendingResult.Frame != null)
                    frameProgress?.Report(pendingResult.Frame);
            }

            // Main agent loop
            while (state.CurrentStep < state.MaxSteps)
            {
                ct.ThrowIfCancellationRequested();
                var stepNumber = state.CurrentStep + 1;

                // Create step record
                var step = new AgentStep
                {
                    AgentRunId = state.RunId,
                    StepNumber = stepNumber,
                    Kind = "model",
                    Status = AgentStepStatuses.Running,
                    Summary = "Model selected the next action.",
                    InputJson = SecretRedactor.RedactJson(JsonSerializer.Serialize(state.Messages))
                };
                _db.Set<AgentStep>().Add(step);
                await _db.SaveChangesAsync(ct);

                // Context compaction check
                var prepared = _contextManager.Prepare(state.Messages, contextOptions);
                if (prepared.WasCompacted)
                {
                    state.Messages = prepared.Messages;
                    step.InputJson = SecretRedactor.RedactJson(JsonSerializer.Serialize(state.Messages));
                    await SaveCheckpointAsync(state, ct);
                    await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
                        new AgentRun { Id = state.RunId },
                        AgentEventTypes.ContextCompacted,
                        prepared.Summary,
                        new { prepared.EstimatedTokensBefore, prepared.EstimatedTokensAfter },
                        step.Id,
                        Severity: AgentEventSeverities.Warning), events, ct);
                }

                // Protocol guard
                var guard = ToolProtocolGuard.RepairForProvider(state.Messages, _agentTools.Definitions);
                if (guard.HasRepairs)
                {
                    state.Messages = guard.Messages.ToList();
                    await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
                        new AgentRun { Id = state.RunId },
                        AgentEventTypes.ProtocolRepair,
                        "Tool protocol guard repaired messages.",
                        new { issues = guard.Issues },
                        step.Id,
                        Severity: guard.IsRejected ? AgentEventSeverities.Error : AgentEventSeverities.Warning), events, ct);
                }

                if (guard.IsRejected)
                {
                    await FinalizeStepFailed(step, state, guard.RejectionReason!, events, ct);
                    assistantContent = $"Agent stopped: {guard.RejectionReason}";
                    break;
                }

                // Call the model
                await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
                    new AgentRun { Id = state.RunId },
                    AgentEventTypes.ModelRequest,
                    $"Sending to model (step {stepNumber}, {guard.Messages.Count} msgs, {guard.Tools.Count} tools).",
                    new { stepNumber, messageCount = guard.Messages.Count, toolCount = guard.Tools.Count },
                    step.Id), events, ct);

                frameProgress?.Report(new AgentRunFrame(stepNumber, AgentRunFrameKinds.ModelRequest, events.ToArray()));

                var streamMetrics = CreateStreamMetrics();
                var outputStream = CreateTrackedStream(options.OutputStream, streamMetrics);
                lastResponse = await _providerStreamAdapter.ChatAsync(
                    new ProviderStreamRequest(provider, guard.Messages.ToList(), systemPrompt,
                        effective.Temperature, effective.MaxTokens, guard.Tools, outputStream,
                        Reasoning: BuildReasoningOptions(effective)), ct);

                // Context limit retry
                if (_contextManager.IsContextLimitError(lastResponse))
                {
                    var forced = _contextManager.Prepare(state.Messages, contextOptions, forceCompact: true);
                    if (forced.WasCompacted)
                    {
                        state.Messages = forced.Messages;
                        await SaveCheckpointAsync(state, ct);
                        await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
                            new AgentRun { Id = state.RunId },
                            AgentEventTypes.ContextCompacted,
                            "Context limit hit; compacted and retrying.",
                            new { forced.EstimatedTokensBefore, forced.EstimatedTokensAfter },
                            step.Id,
                            Severity: AgentEventSeverities.Warning), events, ct);

                        var retryGuard = ToolProtocolGuard.RepairForProvider(state.Messages, _agentTools.Definitions);
                        if (!retryGuard.IsRejected)
                        {
                            outputStream = CreateTrackedStream(options.OutputStream, streamMetrics);
                            lastResponse = await _providerStreamAdapter.ChatAsync(
                                new ProviderStreamRequest(provider, retryGuard.Messages.ToList(), systemPrompt,
                                    effective.Temperature, effective.MaxTokens, retryGuard.Tools, outputStream,
                                    Reasoning: BuildReasoningOptions(effective)), ct);
                        }
                    }
                }

                step.OutputJson = SecretRedactor.RedactJson(JsonSerializer.Serialize(lastResponse.RawResponse));
                await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
                    new AgentRun { Id = state.RunId },
                    AgentEventTypes.ModelResponse,
                    $"Model returned HTTP {lastResponse.HttpStatus}.",
                    new { stepNumber, lastResponse.HttpStatus, lastResponse.LatencyMs, toolCallCount = lastResponse.ToolCalls?.Count ?? 0 },
                    step.Id,
                    Severity: lastResponse.HttpStatus is >= 200 and < 300 ? AgentEventSeverities.Info : AgentEventSeverities.Error), events, ct);

                // Provider error
                if (lastResponse.HttpStatus is < 200 or >= 300 || !string.IsNullOrWhiteSpace(lastResponse.Error))
                {
                    await FinalizeStepFailed(step, state,
                        lastResponse.Error ?? $"HTTP {lastResponse.HttpStatus}", events, ct);
                    assistantContent = lastResponse.AssistantText;
                    break;
                }

                // Process tool calls — support MULTIPLE tool calls per response (M2.7.0)
                var allToolCalls = lastResponse.ToolCalls?.ToList() ?? [];
                if (allToolCalls.Count == 0 &&
                    AgentToolParser.TryParseSandboxCommand(lastResponse.AssistantText, out var legacyRequest))
                {
                    allToolCalls.Add(new LlmToolCall($"legacy-{Guid.NewGuid():N}", AgentToolNames.SandboxExec,
                        JsonSerializer.Serialize(new { command = legacyRequest.Command, reason = legacyRequest.Reason })));
                }

                if (allToolCalls.Count == 0)
                {
                    // Final answer — no tool calls
                    step.Kind = "final";
                    step.Status = AgentStepStatuses.Completed;
                    step.Summary = "Agent completed the task.";
                    step.CompletedAt = DateTime.UtcNow;
                    state.CurrentStep = stepNumber;
                    state.Status = AgentRunStatuses.Completed;
                    state.Messages.Add(new MessagePayload("assistant", lastResponse.AssistantText, ReasoningContent: lastResponse.ReasoningText));
                    await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
                        new AgentRun { Id = state.RunId }, AgentEventTypes.RunCompleted,
                        "Run completed.", new { state.CurrentStep, state.MaxSteps }, step.Id), events, ct);
                    assistantContent = lastResponse.AssistantText;
                    await SaveCheckpointAsync(state, ct);
                    break;
                }

                // Sanitize all tool calls
                var validToolCalls = new List<LlmToolCall>();
                foreach (var tc in allToolCalls)
                {
                    var issues = new List<ToolProtocolGuardIssue>();
                    var safe = ToolProtocolGuard.SanitizeToolCall(tc, issues);
                    if (safe != null) validToolCalls.Add(safe);
                }

                if (validToolCalls.Count == 0)
                {
                    await FinalizeStepFailed(step, state, "All tool calls were invalid.", events, ct);
                    assistantContent = "Agent stopped: invalid tool requests.";
                    break;
                }

                // Plan batches for multi-tool execution
                var planItems = validToolCalls.Select(tc =>
                    new ToolExecutionPlanItem(tc.Name, tc.ArgumentsJson)).ToList();
                var batches = _toolExecutionScheduler.PlanBatches(planItems);

                frameProgress?.Report(new AgentRunFrame(stepNumber, AgentRunFrameKinds.ToolBatchPlanned, events.ToArray(),
                    new { batchCount = batches.Count, totalTools = validToolCalls.Count }));

                // Save the assistant tool-request message
                var requestContent = FormatMultiToolRequestMessage(stepNumber, validToolCalls);
                state.Messages.Add(new MessagePayload("assistant", lastResponse.AssistantText,
                    ToolCalls: validToolCalls, ReasoningContent: lastResponse.ReasoningText));
                assistantContent = requestContent;

                // Process batches
                bool approvalNeeded = false;
                foreach (var batch in batches)
                {
                    var batchItems = new List<ToolBatchItem>();
                    foreach (var item in batch.Items)
                    {
                        var matchingCall = validToolCalls.FirstOrDefault(tc =>
                            string.Equals(tc.Name, item.ToolName, StringComparison.OrdinalIgnoreCase));
                        if (matchingCall == null) continue;

                        if (!_agentTools.TryGet(matchingCall.Name, out var tool)) continue;

                        var safety = ToolSafetyKernel.Assess(_sandboxCommandService, state.ChatId,
                            matchingCall.Name, matchingCall.ArgumentsJson);
                        var invocation = new ToolInvocation
                        {
                            AgentRunId = state.RunId, AgentStepId = step.Id,
                            ToolName = matchingCall.Name, ProviderCallId = matchingCall.Id,
                            ArgumentsJson = SecretRedactor.RedactJson(matchingCall.ArgumentsJson),
                            SafetyLevel = safety.Level, SafetySummary = safety.Summary,
                            SafetyJson = SecretRedactor.RedactJson(safety.PreviewJson),
                            RequiresApproval = tool.Metadata.RequiresApproval
                        };
                        _db.Set<ToolInvocation>().Add(invocation);
                        batchItems.Add(new ToolBatchItem(matchingCall, tool, invocation, safety));
                    }

                    await _db.SaveChangesAsync(ct);

                    foreach (var item in batchItems)
                    {
                        // Check policy
                        var policy = await _toolPlatform.EvaluatePolicyAsync(
                            state.ChatId, item.ToolCall.Name, item.ToolCall.ArgumentsJson, item.Safety, ct);

                        if (policy.IsDenied || item.Safety.IsBlocked)
                        {
                            await HandleDeniedInvocationAsync(state, item, step, policy.IsDenied ? "denied_by_policy" : "blocked_by_safety", options, events, ct);
                            continue;
                        }

                        var needsApproval = (item.Tool.Metadata.RequiresApproval && !options.AutoApproveTools && !policy.IsAllowed) ||
                                           (item.Safety.RequiresExplicitApproval && !policy.IsAllowed);

                        if (needsApproval)
                        {
                            item.Invocation.Status = ToolInvocationStatuses.AwaitingApproval;
                            step.Status = AgentStepStatuses.AwaitingApproval;
                            state.Status = AgentRunStatuses.AwaitingApproval;
                            state.PendingToolInvocationId = item.Invocation.Id;
                            state.CurrentStep = stepNumber;
                            await SaveCheckpointAsync(state, ct);
                            await _db.SaveChangesAsync(ct);
                            await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
                                new AgentRun { Id = state.RunId }, AgentEventTypes.ApprovalRequested,
                                $"Approval needed: {item.ToolCall.Name}.",
                                new { item.ToolCall.Name, item.Safety.Level, item.Safety.Warning }, step.Id, item.Invocation.Id,
                                Severity: item.Safety.RequiresExplicitApproval ? AgentEventSeverities.Warning : AgentEventSeverities.Info), events, ct);

                            frameProgress?.Report(new AgentRunFrame(stepNumber, AgentRunFrameKinds.ApprovalNeeded, events.ToArray()));
                            approvalNeeded = true;
                            break;
                        }

                        // Auto-approve and execute
                        item.Invocation.Approved = true;
                        item.Invocation.ApprovedAt = DateTime.UtcNow;
                        item.Invocation.Status = ToolInvocationStatuses.Approved;

                        var execResult = await ExecuteSingleInvocationAsync(state, item, step, options, events, ct);
                        frameProgress?.Report(execResult.Frame ?? AgentRunFrame.Empty(stepNumber, AgentRunFrameKinds.ToolResult));
                    }

                    if (approvalNeeded) break;
                }

                if (approvalNeeded)
                {
                    // Return immediately — caller will resume after approval
                    return new AgentRunResult(state.DeepClone(), assistantContent, lastResponse, events);
                }

                state.CurrentStep = stepNumber;
            }

            // Step budget finalization
            if (state.Status == AgentRunStatuses.Running)
            {
                var finalResult = await TryFinalizeAtStepBudgetAsync(state, systemPrompt, effective, options, events, ct);
                if (finalResult != null)
                {
                    assistantContent = finalResult;
                    state.Status = AgentRunStatuses.Completed;
                }
                else
                {
                    state.Status = AgentRunStatuses.Paused;
                    await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
                        new AgentRun { Id = state.RunId }, AgentEventTypes.RunPaused,
                        "Step budget reached.", new { state.CurrentStep, state.MaxSteps },
                        Severity: AgentEventSeverities.Warning), events, ct);
                    assistantContent = $"Agent paused at step {state.CurrentStep}/{state.MaxSteps}.";
                }
            }

            return new AgentRunResult(state.DeepClone(), assistantContent, lastResponse, events);
        }
        catch (OperationCanceledException)
        {
            state.Status = AgentRunStatuses.Cancelled;
            state.ErrorMessage = "Stopped by the user.";
            await SaveCheckpointAsync(state, CancellationToken.None);
            await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
                new AgentRun { Id = state.RunId }, AgentEventTypes.RunCancelled,
                "Cancelled.", new { state.CurrentStep, state.MaxSteps },
                Severity: AgentEventSeverities.Warning), events, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            state.Status = AgentRunStatuses.Failed;
            state.ErrorMessage = SecretRedactor.RedactText(ex.Message);
            await SaveCheckpointAsync(state, CancellationToken.None);
            await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
                new AgentRun { Id = state.RunId }, AgentEventTypes.Error,
                state.ErrorMessage, new { exceptionType = ex.GetType().Name },
                Severity: AgentEventSeverities.Error), events, CancellationToken.None);
            throw;
        }
        finally
        {
            await _eventStream.FlushAsync(CancellationToken.None);
        }
    }

    public async Task<AgentRunResult> ResumeAsync(
        AgentRunState state,
        AgentEngineOptions options,
        IProgress<AgentRunFrame>? frameProgress = null,
        CancellationToken ct = default)
    {
        // Resume is essentially the same as RunAsync — the engine handles pending invocations in the pre-loop
        return await RunAsync(state, options, frameProgress, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Private helpers
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> BuildSystemPromptAsync(Guid chatId, CancellationToken ct)
    {
        var prompt = await SystemPromptBuilder.BuildAsync(
            _db.Set<Chat>(), _db.Set<GlobalSettings>(), _db.Set<AgentFile>(),
            _db.Set<ProjectSpace>(), _db.Set<ConfigProfile>(), chatId, ct);
        var memoryPath = await _projectMemory.GetMemoryPathAsync(chatId, ct);
        var projectMemory = await _projectMemory.ReadAsync(chatId, ct);
        // Simplified append (delegates to actual implementation in caller)
        if (!string.IsNullOrWhiteSpace(projectMemory))
            prompt += $"\n\n[project memory: {memoryPath}]\n{projectMemory[..Math.Min(projectMemory.Length, 12_000)]}";
        return prompt;
    }

    private async Task<AgentEvent> AppendEventAsync(Guid runId, AgentEventAppendRequest request,
        List<AgentEvent> events, CancellationToken ct)
    {
        var evt = await _eventStream.AppendAsync(request, ct);
        events.Add(evt);
        return evt;
    }

    private async Task SaveCheckpointAsync(AgentRunState state, CancellationToken ct)
    {
        await _checkpointStore.SaveAsync(
            new AgentRun { Id = state.RunId }, state.CurrentStep,
            JsonSerializer.Serialize(state), ct);
    }

    private async Task FinalizeStepFailed(AgentStep step, AgentRunState state, string error,
        List<AgentEvent> events, CancellationToken ct)
    {
        step.Kind = "error";
        step.Status = AgentStepStatuses.Failed;
        step.Summary = SecretRedactor.RedactText(error);
        step.CompletedAt = DateTime.UtcNow;
        state.Status = AgentRunStatuses.Failed;
        state.ErrorMessage = step.Summary;
        await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
            new AgentRun { Id = state.RunId }, AgentEventTypes.Error, step.Summary,
            Severity: AgentEventSeverities.Error), events, ct);
        await SaveCheckpointAsync(state, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task HandleDeniedInvocationAsync(AgentRunState state, ToolBatchItem item, AgentStep step,
        string reason, AgentEngineOptions options, List<AgentEvent> events, CancellationToken ct)
    {
        item.Invocation.Approved = false;
        item.Invocation.ApprovedAt = DateTime.UtcNow;
        item.Invocation.Status = reason == "denied_by_policy" ? ToolInvocationStatuses.Denied : ToolInvocationStatuses.Failed;
        await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
            new AgentRun { Id = state.RunId }, reason == "denied_by_policy" ? AgentEventTypes.ApprovalDenied : AgentEventTypes.Error,
            $"Invocation {reason}: {item.ToolCall.Name}.",
            new { item.ToolCall.Name, item.Safety.Level }, step.Id, item.Invocation.Id,
            Severity: AgentEventSeverities.Warning), events, ct);
        await CompleteInvocationWithResultAsync(state, item, step,
            new AgentToolResult(false, string.Empty, reason == "denied_by_policy" ? "Denied by policy." : "Blocked by safety."),
            options, events, ct);
    }

    private async Task<(AgentRunFrame? Frame, List<AgentEvent> Events)> ExecuteSingleInvocationAsync(
        AgentRunState state, ToolInvocation invocation, AgentEngineOptions options, CancellationToken ct)
    {
        var events = new List<AgentEvent>();
        var step = await _db.Set<AgentStep>().FirstAsync(s => s.Id == invocation.AgentStepId, ct);

        if (!_agentTools.TryGet(invocation.ToolName, out var tool))
        {
            invocation.Status = ToolInvocationStatuses.Failed;
            step.Status = AgentStepStatuses.Failed;
            await CompleteInvocationWithResultAsync(state, new ToolBatchItem(
                new LlmToolCall(invocation.ProviderCallId, invocation.ToolName, invocation.ArgumentsJson),
                new PlaceholderTool(), invocation, ToolSafetyAssessment.LowRead("fallback", "fallback")),
                step, new AgentToolResult(false, string.Empty, $"Tool unavailable: {invocation.ToolName}"), options, events, ct);
            return (null, events);
        }

        var item = new ToolBatchItem(
            new LlmToolCall(invocation.ProviderCallId, invocation.ToolName, invocation.ArgumentsJson),
            tool, invocation, ToolSafetyAssessment.LowRead(invocation.ToolName, invocation.ToolName));

        return await ExecuteSingleInvocationAsync(state, item, step, options, events, ct);
    }

    private async Task<(AgentRunFrame? Frame, List<AgentEvent> Events)> ExecuteSingleInvocationAsync(
        AgentRunState state, ToolBatchItem item, AgentStep step, AgentEngineOptions options,
        List<AgentEvent> events, CancellationToken ct)
    {
        if (item.Invocation.Approved != true)
        {
            var result = new AgentToolResult(false, string.Empty, "Not approved.");
            item.Invocation.Status = ToolInvocationStatuses.Denied;
            step.Status = AgentStepStatuses.Denied;
            await CompleteInvocationWithResultAsync(state, item, step, result, options, events, ct);
            return (new AgentRunFrame(step.StepNumber, AgentRunFrameKinds.ToolResult, events.ToArray()), events);
        }

        item.Invocation.Status = ToolInvocationStatuses.Running;
        item.Invocation.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
            new AgentRun { Id = state.RunId }, AgentEventTypes.ToolStarted,
            $"Running {item.ToolCall.Name}.", new { item.ToolCall.Name }, step.Id, item.Invocation.Id), events, ct);

        var scheduled = await _toolExecutionScheduler.ExecuteAsync(
            new ToolExecutionRequest(new AgentRun { Id = state.RunId }, item.Invocation,
                options.CommandTimeoutSeconds, options.MaxCommandOutputChars), ct);

        await CompleteInvocationWithResultAsync(state, item, step, scheduled.Result, options, events, ct);
        return (new AgentRunFrame(step.StepNumber, AgentRunFrameKinds.ToolResult, events.ToArray()), events);
    }

    private async Task CompleteInvocationWithResultAsync(AgentRunState state, ToolBatchItem item, AgentStep step,
        AgentToolResult result, AgentEngineOptions options, List<AgentEvent> events, CancellationToken ct)
    {
        item.Invocation.Status = result.Success ? ToolInvocationStatuses.Completed : ToolInvocationStatuses.Failed;
        if (step.Status != AgentStepStatuses.Denied)
            step.Status = result.Success ? AgentStepStatuses.Completed : AgentStepStatuses.Failed;

        var contextResult = result;
        var persistence = await _toolResultPersistence.PersistForContextAsync(
            _sandboxCommandService, state.ChatId, item.Invocation, result,
            Math.Max(512, options.MaxToolResultCharsInContext), ct);
        if (persistence.Persisted)
        {
            contextResult = persistence.ContextResult;
            await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
                new AgentRun { Id = state.RunId }, AgentEventTypes.ToolResultPersisted,
                $"Large output persisted: {item.ToolCall.Name}.",
                new { item.ToolCall.Name, persistence.PersistedPath }, step.Id, item.Invocation.Id), events, ct);
        }

        item.Invocation.ResultJson = contextResult.ToJson();
        item.Invocation.CompletedAt = DateTime.UtcNow;
        step.OutputJson = item.Invocation.ResultJson;
        step.CompletedAt = DateTime.UtcNow;

        state.Messages.Add(new MessagePayload("tool", contextResult.Output,
            item.Invocation.ProviderCallId));
        await SaveCheckpointAsync(state, ct);
        await _db.SaveChangesAsync(ct);

        await AppendEventAsync(state.RunId, new AgentEventAppendRequest(
            new AgentRun { Id = state.RunId }, AgentEventTypes.ToolResult,
            result.Success ? $"{item.ToolCall.Name} completed." : $"{item.ToolCall.Name} failed.",
            new { item.ToolCall.Name, result.Success, error = result.Error },
            step.Id, item.Invocation.Id,
            Severity: result.Success ? AgentEventSeverities.Info : AgentEventSeverities.Error), events, ct);
    }

    private async Task<string?> TryFinalizeAtStepBudgetAsync(AgentRunState state, string systemPrompt,
        EffectiveSettings effective, AgentEngineOptions options, List<AgentEvent> events, CancellationToken ct)
    {
        var stepNumber = state.CurrentStep + 1;
        var step = new AgentStep
        {
            AgentRunId = state.RunId, StepNumber = stepNumber,
            Kind = "final_summary", Status = AgentStepStatuses.Running,
            Summary = "Summarize without tools.",
            InputJson = SecretRedactor.RedactJson(JsonSerializer.Serialize(state.Messages))
        };
        _db.Set<AgentStep>().Add(step);
        await _db.SaveChangesAsync(ct);

        var summaryMessages = state.Messages.ToList();
        summaryMessages.Add(new MessagePayload("user",
            "The agent has reached its step budget. Do not request tools. Give the best possible final answer now."));

        try
        {
            var provider = LlmProviderFactory.Create(_httpClientFactory.CreateClient("LLM"),
                effective.Provider, effective.ApiKey, effective.BaseUrl, effective.Model);
            var response = await _providerStreamAdapter.ChatAsync(new ProviderStreamRequest(
                provider, summaryMessages, systemPrompt,
                Math.Min(effective.Temperature, 0.4), Math.Max(512, Math.Min(effective.MaxTokens, 2048)),
                Tools: null, Reasoning: BuildReasoningOptions(effective)), ct);

            if (response.HttpStatus is < 200 or >= 300 || string.IsNullOrWhiteSpace(response.AssistantText))
            {
                step.Status = AgentStepStatuses.Failed;
                step.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return null;
            }

            step.Kind = "final";
            step.Status = AgentStepStatuses.Completed;
            step.CompletedAt = DateTime.UtcNow;
            state.CurrentStep = stepNumber;
            state.Messages.Add(new MessagePayload("assistant", response.AssistantText));
            await SaveCheckpointAsync(state, ct);
            await _db.SaveChangesAsync(ct);
            return response.AssistantText;
        }
        catch
        {
            step.Status = AgentStepStatuses.Failed;
            step.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return null;
        }
    }

    private static AgentContextOptions BuildContextOptions(AgentEngineOptions options) => new(
        options.ContextBudgetTokens, options.AutoCompactTriggerTokens, MaxToolResultCharsInContext: options.MaxToolResultCharsInContext);

    private static LlmReasoningOptions? BuildReasoningOptions(EffectiveSettings settings) =>
        string.Equals(settings.ThinkingDepth, "disabled", StringComparison.OrdinalIgnoreCase) ? null :
        new LlmReasoningOptions(settings.ThinkingDepth ?? "auto");

    private static string FormatMultiToolRequestMessage(int stepNumber, List<LlmToolCall> toolCalls)
    {
        var lines = new List<string> { $"## Step {stepNumber}: Agent requests {toolCalls.Count} tool(s)" };
        foreach (var tc in toolCalls)
        {
            var reason = ReadToolReason(tc.ArgumentsJson);
            lines.Add($"- **{tc.Name}**" + (reason != null ? $": {reason}" : ""));
        }
        return string.Join('\n', lines);
    }

    private static string? ReadToolReason(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        }
        catch { return null; }
    }

    private sealed class StreamingMetricsTracker { public int Chars; public DateTime Started = DateTime.UtcNow; }

    private static StreamingMetricsTracker CreateStreamMetrics() => new();

    private static IProgress<LlmStreamUpdate>? CreateTrackedStream(
        IProgress<LlmStreamUpdate>? output, StreamingMetricsTracker tracker)
    {
        if (output == null) return null;
        return new Progress<LlmStreamUpdate>(update =>
        {
            tracker.Chars += update.Delta?.Length ?? 0;
            output.Report(update);
        });
    }

    /// <summary>
    /// Minimal placeholder tool for edge cases where a real tool isn't available.
    /// </summary>
    private sealed class PlaceholderTool : IAgentTool
    {
        public LlmToolDefinition Definition => new("placeholder", "Placeholder tool.", new Dictionary<string, object>());
        public bool RequiresApproval => true;
        public Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct)
            => Task.FromResult(new AgentToolResult(false, string.Empty, "Placeholder tool cannot execute."));
    }
}
