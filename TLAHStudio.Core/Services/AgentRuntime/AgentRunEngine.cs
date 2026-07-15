using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.Plugins;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services.Context;
using TLAHStudio.Core.Services.SessionMemory;
using TLAHStudio.Core.Services.Tools.Models;

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
    private readonly IToolLifecycleRunner _toolLifecycleRunner;
    private readonly IAgentContextManager _contextManager;
    private readonly IProjectMemoryService _projectMemory;
    private readonly IToolResultPersistenceService _toolResultPersistence;
    private readonly IAgentTaskService _agentTasks;
    private readonly IReactiveCompactor _reactiveCompactor;
    private readonly ITokenBudgetService _tokenBudget;
    private readonly ISessionMemoryService _sessionMemory;
    private readonly IOutputStyleService? _outputStyle;
    private readonly ISkillLoader? _skillLoader;

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
        IToolResultPersistenceService toolResultPersistence,
        IAgentTaskService? agentTasks = null,
        IReactiveCompactor? reactiveCompactor = null,
        ITokenBudgetService? tokenBudget = null,
        IToolLifecycleRunner? toolLifecycleRunner = null,
        ISessionMemoryService? sessionMemory = null,
        IModelAssistedCompactor? modelAssistedCompactor = null,
        IOutputStyleService? outputStyle = null,
        ISkillLoader? skillLoader = null)
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
        _toolLifecycleRunner = toolLifecycleRunner ??
            new DefaultToolLifecycleRunner(agentTools, sandboxCommandService);
        _contextManager = contextManager;
        _projectMemory = projectMemory;
        _toolResultPersistence = toolResultPersistence;
        _agentTasks = agentTasks ?? new AgentTaskService(db);
        _reactiveCompactor = reactiveCompactor ?? new ReactiveCompactor(modelAssistedCompactor);
        _tokenBudget = tokenBudget ?? new TokenBudgetService();
        _sessionMemory = sessionMemory ?? new SessionMemoryService();
        _outputStyle = outputStyle;
        _skillLoader = skillLoader;
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunState state,
        AgentEngineOptions options,
        IProgress<AgentRunFrame>? frameProgress = null,
        CancellationToken ct = default)
    {
        var run = await _db.Set<AgentRun>().FirstAsync(r => r.Id == state.RunId, ct);
        // A resumed run starts a fresh execution attempt. Do not keep rendering
        // a stale provider/tool error after the resume itself succeeded.
        if (state.Status == AgentRunStatuses.Running)
            state.ErrorMessage = null;
        // M4.9.2: A fresh run (step 0) means the user actively continued —
        // reset the compaction circuit breaker so auto-compaction can resume.
        if (state.CurrentStep == 0)
            state.CompactionDisabled = false;
        SyncRunState(run, state);
        await _db.SaveChangesAsync(ct);

        using var scope = _eventStream.BeginRun(run);
        var events = new List<AgentEvent>();
        var contextOptions = BuildContextOptions(options);
        string? assistantContent = null;
        LlmResponse? lastResponse = null;
        int consecutiveCompactionFailures = 0;
        const int maxCompactionFailures = 3;

        // M4.8.0: Session memory throttling — avoid writing every step.
        int _sessionMemoryTokenEstimate = 0;
        int _sessionMemoryCallCount = 0;
        int _sessionMemoryLastWriteEstimate = 0;
        const int smInitTokenThreshold = 10_000;
        const int smUpdateTokenDelta = 5_000;
        const int smUpdateCallDelta = 3;

        // M4.9.0: Tell the skill loader which workspace we're in so project-level
        // skills (.tlah/skills/) are discovered for this chat.
        var sandboxRoot = _sandboxCommandService.GetSandboxRoot(state.ChatId);
        _skillLoader?.SetWorkspaceRoot(sandboxRoot);

        try
        {
            options = ApplyPersistedPermissionOptions(state, options);
            contextOptions = BuildContextOptions(options);
            // Build system prompt with memory
            var systemPrompt = await BuildSystemPromptAsync(state, options.PermissionMode, ct);
            var effective = await _settingsService.GetEffectiveSettingsAsync(state.ChatId, ct);
            var provider = LlmProviderFactory.Create(
                _httpClientFactory.CreateClient("LLM"),
                effective.Provider,
                effective.ApiKey,
                effective.BaseUrl,
                effective.Model);

            // Pre-loop: handle pending approval from resume
            ToolInvocation? pending = null;
            var pendingInvocationId = state.PendingToolInvocationId ?? state.UnknownOutcomeInvocationId;
            if (pendingInvocationId is { } durableInvocationId)
            {
                pending = await _db.Set<ToolInvocation>()
                    .Include(i => i.AgentStep)
                    .FirstOrDefaultAsync(i =>
                        i.Id == durableInvocationId &&
                        i.AgentRunId == state.RunId, ct);
            }
            if (pending != null &&
                pending.Status is ToolInvocationStatuses.Running or ToolInvocationStatuses.UnknownOutcome)
            {
                assistantContent = await PauseForUnknownInvocationOutcomeAsync(
                    run, state, pending, options, events, ct);
                return new AgentRunResult(state.DeepClone(), assistantContent, lastResponse, events);
            }
            if (pending != null &&
                pending.Status is ToolInvocationStatuses.Approved or ToolInvocationStatuses.Denied)
            {
                var pendingResult = await ExecuteSingleInvocationAsync(
                    state, pending, options, ct);
                events.AddRange(pendingResult.Events);
                if (pendingResult.Frame != null)
                    ReportSafely(frameProgress, pendingResult.Frame);

                if (state.Status == AgentRunStatuses.Paused)
                {
                    assistantContent = state.ErrorMessage;
                    return new AgentRunResult(state.DeepClone(), assistantContent, lastResponse, events);
                }

                options = ApplyPersistedPermissionOptions(state, options);
                systemPrompt = await BuildSystemPromptAsync(state, options.PermissionMode, ct);
            }

            // Main agent loop
            while (state.CurrentStep < state.MaxSteps)
            {
                ct.ThrowIfCancellationRequested();
                var directiveStateChanged = false;
                if (state.DeferredToolDirectivePending && state.DeferredToolCalls.Count > 0)
                {
                    state.Messages.Add(new MessagePayload(
                        "user",
                        BuildDeferredToolDirective(state.DeferredToolCalls)));
                    state.DeferredToolDirectivePending = false;
                    directiveStateChanged = true;
                }
                if (state.RecoveryDirectivePending)
                {
                    state.Messages.Add(new MessagePayload(
                        "user",
                        BuildFailureRecoveryDirective(state, completionWasAttempted: false)));
                    state.RecoveryDirectiveIssuedForFailureSignature =
                        state.LastFailedInvocationSignature;
                    state.RecoveryDirectivePending = false;
                    directiveStateChanged = true;
                }
                if (directiveStateChanged)
                    await SaveCheckpointAsync(state, ct);
                var stepNumber = state.CurrentStep + 1;
                state.CurrentStep = stepNumber;
                SyncRunState(run, state);

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
                var prepared = await PrepareContextAsync(
                    state, contextOptions, effective.Provider, effective.Model, forceCompact: false, ct,
                    provider, systemPrompt);
                if (prepared.WasCompacted)
                {
                    state.Messages = prepared.Messages;
                    // M4.8.0: Force-refresh session memory before reading for compaction.
                    await _sessionMemory.WaitForExtractionAsync(TimeSpan.FromSeconds(5), ct);
                    {
                        var smMeta = await BuildRuntimeContextMetadataAsync(state.ChatId, state.RunId, ct);
                        await _sessionMemory.ExtractAsync(state.ChatId, state.RunId, state.Messages,
                            _sandboxCommandService.GetSandboxRoot(state.ChatId), smMeta.FilesChanged,
                            smMeta.CommandsRun, smMeta.RecentFailures, smMeta.OpenQuestions,
                            smMeta.NextActions, ct);
                    }
                    // M4.5.0: Inject session memory into compacted context.
                    var smContent = await _sessionMemory.ReadForCompactAsync(
                        _sandboxCommandService.GetSandboxRoot(state.ChatId), ct);
                    if (!string.IsNullOrEmpty(smContent))
                        state.Messages.Insert(prepared.Messages.Count - 1,
                            new MessagePayload("user", $"[session memory — accumulated context across compaction cycles]\n{smContent}\n[/session memory]"));
                    // M4.5.0: Also re-inject recently read file content after compaction.
                    var fileCtx = await BuildPostCompactFileContextAsync(state.ChatId, state.RunId, ct);
                    if (!string.IsNullOrEmpty(fileCtx))
                        state.Messages.Insert(prepared.Messages.Count - 1,
                            new MessagePayload("user", fileCtx));
                    // M4.8.0: Re-inject available tools summary so the agent
                    // doesn't lose track of its tool set after compaction.
                    var toolsSummary = BuildPostCompactToolsSummary();
                    if (!string.IsNullOrEmpty(toolsSummary))
                        state.Messages.Insert(prepared.Messages.Count - 1,
                            new MessagePayload("user", toolsSummary));
                    // M4.9.2: Re-inject skill/MCP/plan state (completes 0.6).
                    var skillsSummary = await BuildPostCompactSkillsSummaryAsync(state, ct);
                    if (!string.IsNullOrEmpty(skillsSummary))
                        state.Messages.Insert(prepared.Messages.Count - 1,
                            new MessagePayload("user", skillsSummary));
                    var mcpDelta = await BuildPostCompactMcpDeltaAsync(ct);
                    if (!string.IsNullOrEmpty(mcpDelta))
                        state.Messages.Insert(prepared.Messages.Count - 1,
                            new MessagePayload("user", mcpDelta));
                    var planSummary = BuildPostCompactPlanSummary(state);
                    if (!string.IsNullOrEmpty(planSummary))
                        state.Messages.Insert(prepared.Messages.Count - 1,
                            new MessagePayload("user", planSummary));
                    step.InputJson = SecretRedactor.RedactJson(JsonSerializer.Serialize(state.Messages));
                    await SaveCheckpointAsync(state, ct);
                    var ctxMeta = await BuildRuntimeContextMetadataAsync(state.ChatId, state.RunId, ct);
                    await AppendEventAsync(state, options, new AgentEventAppendRequest(
                        new AgentRun { Id = state.RunId },
                        AgentEventTypes.ContextCompacted,
                        prepared.Summary,
                        new
                        {
                            prepared.EstimatedTokensBefore,
                            prepared.EstimatedTokensAfter,
                            files_changed = ctxMeta.FilesChanged,
                            commands_run = ctxMeta.CommandsRun,
                            open_questions = ctxMeta.OpenQuestions,
                            next_actions = ctxMeta.NextActions,
                            persisted_outputs = ctxMeta.PersistedOutputs
                        },
                        step.Id,
                        Severity: AgentEventSeverities.Warning), events, ct);
                }

                // Protocol guard
                var guard = ToolProtocolGuard.RepairForProvider(state.Messages, _agentTools.Definitions);
                if (guard.HasRepairs)
                {
                    state.Messages = guard.Messages.ToList();
                    await AppendEventAsync(state, options, new AgentEventAppendRequest(
                        new AgentRun { Id = state.RunId },
                        AgentEventTypes.ProtocolRepair,
                        "Tool protocol guard repaired messages.",
                        new { issues = guard.Issues },
                        step.Id,
                        Severity: guard.IsRejected ? AgentEventSeverities.Error : AgentEventSeverities.Warning), events, ct);
                }

                if (guard.IsRejected)
                {
                    await FinalizeStepFailed(run, step, state, options, guard.RejectionReason!, events, ct);
                    assistantContent = $"Agent stopped: {guard.RejectionReason}";
                    break;
                }

                // Call the model
                var modelMessages = await BuildModelMessagesWithRuntimeContextAsync(
                    state.ChatId, state.RunId, guard.Messages, ct);

                await AppendEventAsync(state, options, new AgentEventAppendRequest(
                    new AgentRun { Id = state.RunId },
                    AgentEventTypes.ModelRequest,
                    $"Sending to model (step {stepNumber}, {modelMessages.Count} msgs, {guard.Tools.Count} tools).",
                    new { stepNumber, messageCount = modelMessages.Count, toolCount = guard.Tools.Count, runtimeContextInjected = true },
                    step.Id), events, ct);

                ReportSafely(frameProgress,
                    new AgentRunFrame(stepNumber, AgentRunFrameKinds.ModelRequest, events.ToArray()));

                var streamMetrics = CreateStreamMetrics();
                var outputStream = CreateTrackedStream(options.OutputStream, streamMetrics);
                lastResponse = await ChatWithTransientRetryAsync(
                    new ProviderStreamRequest(provider, modelMessages, systemPrompt,
                        effective.Temperature, effective.MaxTokens, guard.Tools, outputStream,
                        Reasoning: BuildReasoningOptions(effective)), state, ct);

                // M4.8.0: Context limit retry with PTL truncation.
                if (_contextManager.IsContextLimitError(lastResponse))
                {
                    consecutiveCompactionFailures++;
                    bool compactionDisabled = state.CompactionDisabled ||
                        consecutiveCompactionFailures > maxCompactionFailures;

                    if (compactionDisabled)
                    {
                        // M4.8.0: Circuit breaker — disable auto-compaction instead
                        // of aborting the run. Fall through to PTL truncation.
                        state.CompactionDisabled = true;
                        await AppendEventAsync(state, options, new AgentEventAppendRequest(
                            new AgentRun { Id = state.RunId },
                            AgentEventTypes.CompactionSkipped,
                            $"Compaction failed {consecutiveCompactionFailures} consecutive times. Auto-compaction disabled; using PTL truncation. Run /compact to re-enable.",
                            new { consecutiveCompactionFailures, maxCompactionFailures, compactionDisabled = true },
                            step.Id,
                            Severity: AgentEventSeverities.Warning), events, ct);
                    }
                    else
                    {
                        // Try force-compact + post-compact injection + retry.
                        var forced = await PrepareContextAsync(
                            state, contextOptions, effective.Provider, effective.Model, forceCompact: true, ct,
                            provider, systemPrompt);
                        if (forced.WasCompacted)
                        {
                            consecutiveCompactionFailures = 0;
                            state.Messages = forced.Messages;
                            // M4.8.0: Force-refresh session memory before reading for compaction.
                            await _sessionMemory.WaitForExtractionAsync(TimeSpan.FromSeconds(5), ct);
                            {
                                var fSmMeta = await BuildRuntimeContextMetadataAsync(state.ChatId, state.RunId, ct);
                                await _sessionMemory.ExtractAsync(state.ChatId, state.RunId, state.Messages,
                                    _sandboxCommandService.GetSandboxRoot(state.ChatId), fSmMeta.FilesChanged,
                                    fSmMeta.CommandsRun, fSmMeta.RecentFailures, fSmMeta.OpenQuestions,
                                    fSmMeta.NextActions, ct);
                            }
                            var smContent = await _sessionMemory.ReadForCompactAsync(
                                _sandboxCommandService.GetSandboxRoot(state.ChatId), ct);
                            if (!string.IsNullOrEmpty(smContent))
                                state.Messages.Insert(forced.Messages.Count - 1,
                                    new MessagePayload("user", $"[session memory — accumulated context across compaction cycles]\n{smContent}\n[/session memory]"));
                            var fctx = await BuildPostCompactFileContextAsync(state.ChatId, state.RunId, ct);
                            if (!string.IsNullOrEmpty(fctx))
                                state.Messages.Insert(forced.Messages.Count - 1,
                                    new MessagePayload("user", fctx));
                            // M4.8.0: Re-inject tools summary.
                            var ftoolsSummary = BuildPostCompactToolsSummary();
                            if (!string.IsNullOrEmpty(ftoolsSummary))
                                state.Messages.Insert(forced.Messages.Count - 1,
                                    new MessagePayload("user", ftoolsSummary));
                            // M4.9.2: Re-inject skill/MCP/plan state (completes 0.6).
                            var fskillsSummary = await BuildPostCompactSkillsSummaryAsync(state, ct);
                            if (!string.IsNullOrEmpty(fskillsSummary))
                                state.Messages.Insert(forced.Messages.Count - 1,
                                    new MessagePayload("user", fskillsSummary));
                            var fmcpDelta = await BuildPostCompactMcpDeltaAsync(ct);
                            if (!string.IsNullOrEmpty(fmcpDelta))
                                state.Messages.Insert(forced.Messages.Count - 1,
                                    new MessagePayload("user", fmcpDelta));
                            var fplanSummary = BuildPostCompactPlanSummary(state);
                            if (!string.IsNullOrEmpty(fplanSummary))
                                state.Messages.Insert(forced.Messages.Count - 1,
                                    new MessagePayload("user", fplanSummary));
                            await SaveCheckpointAsync(state, ct);
                            var fMeta = await BuildRuntimeContextMetadataAsync(state.ChatId, state.RunId, ct);
                            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                                new AgentRun { Id = state.RunId },
                                AgentEventTypes.ContextCompacted,
                                "Context limit hit; compacted and retrying.",
                                new
                                {
                                    forced.EstimatedTokensBefore,
                                    forced.EstimatedTokensAfter,
                                    files_changed = fMeta.FilesChanged,
                                    commands_run = fMeta.CommandsRun,
                                    open_questions = fMeta.OpenQuestions,
                                    next_actions = fMeta.NextActions,
                                    persisted_outputs = fMeta.PersistedOutputs
                                },
                                step.Id,
                                Severity: AgentEventSeverities.Warning), events, ct);

                            var retryGuard = ToolProtocolGuard.RepairForProvider(state.Messages, _agentTools.Definitions);
                            if (!retryGuard.IsRejected)
                            {
                                var retryMessages = await BuildModelMessagesWithRuntimeContextAsync(
                                    state.ChatId, state.RunId, retryGuard.Messages, ct);
                                outputStream = CreateTrackedStream(options.OutputStream, streamMetrics);
                                lastResponse = await ChatWithTransientRetryAsync(
                                    new ProviderStreamRequest(provider, retryMessages, systemPrompt,
                                        effective.Temperature, effective.MaxTokens, retryGuard.Tools, outputStream,
                                        Reasoning: BuildReasoningOptions(effective)), state, ct);
                            }
                        }
                    }

                    // M4.8.0: PTL truncation — if still context-limit after compaction
                    // (or compaction is disabled), drop oldest API-rounds and retry.
                    for (int ptlAttempt = 0;
                         ptlAttempt < 3 && _contextManager.IsContextLimitError(lastResponse);
                         ptlAttempt++)
                    {
                        var msgs = state.Messages.ToList();
                        const int headKeep = 2; // system + first user
                        const int tailKeep = 6;
                        int removeIdx = -1;
                        for (int i = headKeep; i < msgs.Count - tailKeep; i++)
                        {
                            if (string.Equals(msgs[i].Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                                (msgs[i].ToolCalls?.Count ?? 0) > 0)
                            {
                                removeIdx = i;
                                break;
                            }
                        }
                        if (removeIdx < 0)
                            break; // No removable round left

                        // Remove the round: assistant + its tool results.
                        var keep = msgs.Take(removeIdx).ToList();
                        int skip = removeIdx + 1;
                        while (skip < msgs.Count &&
                               string.Equals(msgs[skip].Role, "tool", StringComparison.OrdinalIgnoreCase))
                            skip++;
                        keep.AddRange(msgs.Skip(skip));
                        state.Messages = keep;

                        var truncGuard = ToolProtocolGuard.RepairForProvider(state.Messages, _agentTools.Definitions);
                        if (!truncGuard.IsRejected)
                        {
                            var truncMessages = await BuildModelMessagesWithRuntimeContextAsync(
                                state.ChatId, state.RunId, truncGuard.Messages, ct);
                            outputStream = CreateTrackedStream(options.OutputStream, streamMetrics);
                            lastResponse = await ChatWithTransientRetryAsync(
                                new ProviderStreamRequest(provider, truncMessages, systemPrompt,
                                    effective.Temperature, effective.MaxTokens, truncGuard.Tools, outputStream,
                                    Reasoning: BuildReasoningOptions(effective)), state, ct);
                            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                                new AgentRun { Id = state.RunId },
                                AgentEventTypes.ContextCompacted,
                                $"PTL truncation round {ptlAttempt + 1}: dropped oldest tool round, {keep.Count} msgs remain.",
                                new { ptlAttempt, messageCount = keep.Count },
                                step.Id,
                                Severity: AgentEventSeverities.Warning), events, ct);
                        }
                    }
                }
                else
                {
                    consecutiveCompactionFailures = 0;
                }

                step.OutputJson = SecretRedactor.RedactJson(JsonSerializer.Serialize(lastResponse.RawResponse));
                await AppendEventAsync(state, options, new AgentEventAppendRequest(
                    new AgentRun { Id = state.RunId },
                    AgentEventTypes.ModelResponse,
                    $"Model returned HTTP {lastResponse.HttpStatus}.",
                    new { stepNumber, lastResponse.HttpStatus, lastResponse.LatencyMs, toolCallCount = lastResponse.ToolCalls?.Count ?? 0 },
                    step.Id,
                    Severity: lastResponse.HttpStatus is >= 200 and < 300 ? AgentEventSeverities.Info : AgentEventSeverities.Error), events, ct);

                // Provider error
                if (lastResponse.HttpStatus is < 200 or >= 300 || !string.IsNullOrWhiteSpace(lastResponse.Error))
                {
                    var providerError = lastResponse.Error ?? $"HTTP {lastResponse.HttpStatus}";
                    if (IsTransientProviderResponse(lastResponse))
                    {
                        await PauseForTransientProviderFailureAsync(
                            run, step, state, options, providerError, events, ct);
                        assistantContent =
                            "The model provider is temporarily unavailable. Progress was saved; resume the run to continue.";
                    }
                    else
                    {
                        // Deterministic request errors cannot be repaired by
                        // blindly resuming with the same malformed request.
                        await FinalizeStepFailed(
                            run, step, state, options, providerError, events, ct);
                        assistantContent = lastResponse.AssistantText;
                    }
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
                    if (state.ConsecutiveToolFailures > 0)
                    {
                        if (state.CompletionRecoveryAttempts == 0)
                        {
                            // A plain response immediately after an unresolved
                            // tool failure is not proof of completion. Give the
                            // model one explicit opportunity to change route.
                            step.Kind = "recovery";
                            step.Status = AgentStepStatuses.Completed;
                            step.Summary = "Completion deferred after an unresolved tool failure.";
                            step.CompletedAt = DateTime.UtcNow;
                            state.CurrentStep = stepNumber;
                            state.CompletionRecoveryAttempts++;
                            state.RecoveryAttempts++;
                            if (!string.IsNullOrWhiteSpace(lastResponse.AssistantText))
                            {
                                state.Messages.Add(new MessagePayload(
                                    "assistant",
                                    lastResponse.AssistantText,
                                    ReasoningContent: lastResponse.ReasoningText));
                            }
                            state.Messages.Add(new MessagePayload(
                                "user",
                                BuildFailureRecoveryDirective(state, completionWasAttempted: true)));
                            state.RecoveryDirectiveIssuedForFailureSignature =
                                state.LastFailedInvocationSignature;
                            state.RecoveryDirectivePending = false;
                            SyncRunState(run, state);
                            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                                new AgentRun { Id = state.RunId },
                                AgentEventTypes.ProtocolRepair,
                                "Completion deferred; recovering from the latest tool failure.",
                                new
                                {
                                    state.ConsecutiveToolFailures,
                                    state.RepeatedFailureCount,
                                    state.LastFailureSummary
                                },
                                step.Id,
                                Severity: AgentEventSeverities.Warning), events, ct);
                            await SaveCheckpointAsync(state, ct);
                            await _db.SaveChangesAsync(ct);
                            continue;
                        }

                        // The recovery prompt also ended without a viable action.
                        // Convert that ambiguous stop into an explicit user choice
                        // instead of reporting a false-successful completion.
                        allToolCalls.Add(CreateRecoveryQuestionCall(state));
                    }
                    else if (state.DeferredToolCalls.Count > 0)
                    {
                        if (state.DeferredToolRecoveryAttempts < 2)
                        {
                            step.Kind = "recovery";
                            step.Status = AgentStepStatuses.Completed;
                            step.Summary = "Completion deferred until sibling tool calls are reconsidered.";
                            step.CompletedAt = DateTime.UtcNow;
                            state.CurrentStep = stepNumber;
                            state.DeferredToolRecoveryAttempts++;
                            if (!string.IsNullOrWhiteSpace(lastResponse.AssistantText))
                            {
                                state.Messages.Add(new MessagePayload(
                                    "assistant",
                                    lastResponse.AssistantText,
                                    ReasoningContent: lastResponse.ReasoningText));
                            }
                            state.Messages.Add(new MessagePayload(
                                "user",
                                BuildDeferredToolDirective(state.DeferredToolCalls, completionWasAttempted: true)));
                            state.DeferredToolDirectivePending = false;
                            SyncRunState(run, state);
                            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                                new AgentRun { Id = state.RunId },
                                AgentEventTypes.ProtocolRepair,
                                "Completion deferred because sibling tool calls have not been reconsidered.",
                                new
                                {
                                    deferredCount = state.DeferredToolCalls.Count,
                                    state.DeferredToolRecoveryAttempts
                                },
                                step.Id,
                                Severity: AgentEventSeverities.Warning), events, ct);
                            await SaveCheckpointAsync(state, ct);
                            await _db.SaveChangesAsync(ct);
                            continue;
                        }

                        allToolCalls.Add(CreateDeferredToolQuestionCall(state));
                    }
                    else
                    {
                        var openTasks = await GetOpenCompletionTasksAsync(state.ChatId, ct);
                        if (openTasks.Count > 0)
                        {
                            if (state.OpenTaskCompletionRecoveryAttempts == 0)
                            {
                                step.Kind = "recovery";
                                step.Status = AgentStepStatuses.Completed;
                                step.Summary = "Completion deferred while tracked tasks remain open.";
                                step.CompletedAt = DateTime.UtcNow;
                                state.CurrentStep = stepNumber;
                                state.OpenTaskCompletionRecoveryAttempts++;
                                if (!string.IsNullOrWhiteSpace(lastResponse.AssistantText))
                                {
                                    state.Messages.Add(new MessagePayload(
                                        "assistant",
                                        lastResponse.AssistantText,
                                        ReasoningContent: lastResponse.ReasoningText));
                                }
                                state.Messages.Add(new MessagePayload(
                                    "user",
                                    BuildOpenTaskRecoveryDirective(openTasks)));
                                SyncRunState(run, state);
                                await AppendEventAsync(state, options, new AgentEventAppendRequest(
                                    new AgentRun { Id = state.RunId },
                                    AgentEventTypes.ProtocolRepair,
                                    "Completion deferred because tracked tasks remain open.",
                                    new
                                    {
                                        openTaskCount = openTasks.Count,
                                        tasks = openTasks.Select(t => new { t.Id, t.Title, t.Status }).ToArray()
                                    },
                                    step.Id,
                                    Severity: AgentEventSeverities.Warning), events, ct);
                                await SaveCheckpointAsync(state, ct);
                                await _db.SaveChangesAsync(ct);
                                continue;
                            }

                            allToolCalls.Add(CreateOpenTaskQuestionCall(openTasks));
                        }
                        else
                        {
                            state.OpenTaskCompletionRecoveryAttempts = 0;
                            // Final answer — no tool calls and no durable work remains.
                            step.Kind = "final";
                            step.Status = AgentStepStatuses.Completed;
                            step.Summary = "Agent completed the task.";
                            step.CompletedAt = DateTime.UtcNow;
                            state.CurrentStep = stepNumber;
                            state.Status = AgentRunStatuses.Completed;
                            state.ErrorMessage = null;
                            SyncRunState(run, state, terminal: true);
                            state.Messages.Add(new MessagePayload("assistant", lastResponse.AssistantText, ReasoningContent: lastResponse.ReasoningText));
                            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                                new AgentRun { Id = state.RunId }, AgentEventTypes.RunCompleted,
                                "Run completed.", new { state.CurrentStep, state.MaxSteps }, step.Id), events, ct);
                            assistantContent = lastResponse.AssistantText;
                            await SaveCheckpointAsync(state, ct);
                            break;
                        }
                    }
                }

                if (state.ConsecutiveToolFailures > 0 && allToolCalls.Count > 0)
                {
                    var repeatedCalls = allToolCalls
                        .Where(call => IsRepeatedFailedInvocation(state, call))
                        .ToList();
                    if (repeatedCalls.Count > 0)
                    {
                        allToolCalls = allToolCalls
                            .Where(call => !IsRepeatedFailedInvocation(state, call))
                            .ToList();
                        state.ConsecutiveToolFailures += repeatedCalls.Count;
                        state.RepeatedFailureCount += repeatedCalls.Count;
                        state.RecoveryAttempts++;
                        state.RecoveryDirectivePending = false;

                        // When the model offers no materially different action,
                        // replace the loop with an explicit user decision now.
                        if (allToolCalls.Count == 0)
                            allToolCalls.Add(CreateRecoveryQuestionCall(state));

                        await AppendEventAsync(state, options, new AgentEventAppendRequest(
                            new AgentRun { Id = state.RunId },
                            AgentEventTypes.ProtocolRepair,
                            $"Suppressed {repeatedCalls.Count} identical failed invocation(s); requesting a different route.",
                            new
                            {
                                repeatedCount = repeatedCalls.Count,
                                state.RepeatedFailureCount,
                                replacement = allToolCalls.Count == 1 &&
                                    string.Equals(allToolCalls[0].Name, AgentToolNames.AskUserQuestion, StringComparison.OrdinalIgnoreCase)
                                        ? AgentToolNames.AskUserQuestion
                                        : "materially_different_calls"
                            },
                            step.Id,
                            Severity: AgentEventSeverities.Warning), events, ct);
                    }
                }

                // Sanitize all tool calls
                var validToolCalls = new List<LlmToolCall>();
                var invalidToolCallIssues = new List<ToolProtocolGuardIssue>();
                foreach (var tc in allToolCalls)
                {
                    var safe = ToolProtocolGuard.SanitizeToolCall(tc, invalidToolCallIssues);
                    if (safe == null)
                        continue;
                    if (!_agentTools.TryGet(safe.Name, out _))
                    {
                        invalidToolCallIssues.Add(new ToolProtocolGuardIssue(
                            "unknown_tool_name",
                            $"The model requested an unregistered tool: {safe.Name}",
                            "error"));
                        continue;
                    }
                    validToolCalls.Add(safe);
                }

                if (validToolCalls.Count == 0)
                {
                    if (state.InvalidToolCallRecoveryAttempts < 2)
                    {
                        step.Kind = "protocol_recovery";
                        step.Status = AgentStepStatuses.Completed;
                        step.Summary = "Invalid tool calls rejected; requesting a corrected call.";
                        step.CompletedAt = DateTime.UtcNow;
                        state.CurrentStep = stepNumber;
                        state.InvalidToolCallRecoveryAttempts++;
                        state.RecoveryAttempts++;
                        if (!string.IsNullOrWhiteSpace(lastResponse.AssistantText))
                        {
                            state.Messages.Add(new MessagePayload(
                                "assistant",
                                lastResponse.AssistantText,
                                ReasoningContent: lastResponse.ReasoningText));
                        }
                        state.Messages.Add(new MessagePayload(
                            "user",
                            BuildInvalidToolRecoveryDirective(
                                invalidToolCallIssues,
                                state.InvalidToolCallRecoveryAttempts)));
                        if (state.DeferredToolCalls.Count > 0)
                            state.DeferredToolDirectivePending = true;
                        SyncRunState(run, state);
                        await AppendEventAsync(state, options, new AgentEventAppendRequest(
                            new AgentRun { Id = state.RunId },
                            AgentEventTypes.ProtocolRepair,
                            "All tool calls were invalid; requesting protocol repair.",
                            new
                            {
                                attempt = state.InvalidToolCallRecoveryAttempts,
                                maxAttempts = 2,
                                issues = invalidToolCallIssues.Select(i => new { i.Code, i.Summary }).ToArray()
                            },
                            step.Id,
                            Severity: AgentEventSeverities.Warning), events, ct);
                        await SaveCheckpointAsync(state, ct);
                        await _db.SaveChangesAsync(ct);
                        continue;
                    }

                    validToolCalls.Add(CreateProtocolRepairQuestionCall(invalidToolCallIssues));
                }
                else
                {
                    state.InvalidToolCallRecoveryAttempts = 0;
                    AcknowledgeDeferredToolReRequests(state, validToolCalls);
                }

                // User-interaction calls are always isolated, including Full
                // Access. Pausing an ask_user_question batch while retaining
                // sibling calls would leave provider calls without results.
                var userInteractionCall = validToolCalls.FirstOrDefault(call =>
                    string.Equals(call.Name, AgentToolNames.AskUserQuestion, StringComparison.OrdinalIgnoreCase));
                if (validToolCalls.Count > 1 && userInteractionCall != null)
                {
                    var deferredCalls = validToolCalls
                        .Where(call => !ReferenceEquals(call, userInteractionCall))
                        .ToList();
                    var deferredCount = deferredCalls.Count;
                    EnqueueDeferredToolCalls(state, deferredCalls);
                    validToolCalls = [userInteractionCall];
                    await AppendEventAsync(state, options, new AgentEventAppendRequest(
                        new AgentRun { Id = state.RunId },
                        AgentEventTypes.ProtocolRepair,
                        $"Isolated user question and deferred {deferredCount} sibling tool call(s).",
                        new { deferredCount, strategy = "isolate_user_interaction" },
                        step.Id,
                        Severity: AgentEventSeverities.Warning), events, ct);
                }
                // Other approval-mode batches remain single-invocation until
                // durable batch grants are supported. Full/Auto ordinary tool
                // batches continue to execute together.
                else if (validToolCalls.Count > 1 &&
                         !options.AutoApproveTools &&
                         !AgentPermissionModes.IsBypass(options.PermissionMode))
                {
                    var deferredCalls = validToolCalls.Skip(1).ToList();
                    var deferredCount = deferredCalls.Count;
                    EnqueueDeferredToolCalls(state, deferredCalls);
                    validToolCalls = [validToolCalls[0]];
                    await AppendEventAsync(state, options, new AgentEventAppendRequest(
                        new AgentRun { Id = state.RunId },
                        AgentEventTypes.ProtocolRepair,
                        $"Deferred {deferredCount} additional tool call(s) to preserve approval and resume integrity.",
                        new { deferredCount, strategy = "single_invocation" },
                        step.Id,
                        Severity: AgentEventSeverities.Warning), events, ct);
                }

                // Plan batches for multi-tool execution
                var planItems = validToolCalls.Select(tc =>
                    new ToolExecutionPlanItem(tc.Name, tc.ArgumentsJson, tc.Id)).ToList();
                var batches = _toolExecutionScheduler.PlanBatches(planItems);

                ReportSafely(frameProgress,
                    new AgentRunFrame(stepNumber, AgentRunFrameKinds.ToolBatchPlanned, events.ToArray(),
                        new { batchCount = batches.Count, totalTools = validToolCalls.Count }));

                // Save the assistant tool-request message
                var requestContent = FormatMultiToolRequestMessage(stepNumber, validToolCalls);
                // Persist to DB (compat with old loop)
                _db.Set<Models.Message>().Add(new Models.Message
                {
                    ChatId = state.ChatId, Role = "assistant", Content = requestContent,
                    TurnId = state.TurnId, SequenceNum = state.SequenceNum++
                });
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
                                !string.IsNullOrWhiteSpace(item.ToolCallId) &&
                                string.Equals(tc.Id, item.ToolCallId, StringComparison.Ordinal)) ??
                            validToolCalls.FirstOrDefault(tc =>
                                string.Equals(tc.Name, item.ToolName, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(tc.ArgumentsJson, item.ArgumentsJson, StringComparison.Ordinal));
                        if (matchingCall == null) continue;

                        if (!_agentTools.TryGet(matchingCall.Name, out var tool)) continue;

                        var preview = await _toolLifecycleRunner.PreviewAsync(
                            state.ChatId,
                            matchingCall.Name,
                            matchingCall.ArgumentsJson,
                            ct);
                        var safety = preview.Safety;
                        var invocation = new ToolInvocation
                        {
                            AgentRunId = state.RunId, AgentStepId = step.Id,
                            ToolName = matchingCall.Name, ProviderCallId = matchingCall.Id,
                            ArgumentsJson = SecretRedactor.RedactJson(matchingCall.ArgumentsJson),
                            ProtectedArgumentsJson = ProtectedLocalData.Protect(matchingCall.ArgumentsJson),
                            SafetyLevel = safety.Level, SafetySummary = safety.Summary,
                            SafetyJson = SecretRedactor.RedactJson(safety.PreviewJson),
                            RequiresApproval = tool.Metadata.RequiresApproval
                        };
                        _db.Set<ToolInvocation>().Add(invocation);
                        batchItems.Add(new ToolBatchItem(matchingCall, tool, invocation, safety, preview.EffectPlan));
                    }

                    await _db.SaveChangesAsync(ct);

                    // Emit ToolRequest events for each tool call
                    foreach (var item in batchItems)
                    {
                        var toolUseRender = item.Tool.RenderToolUse(item.ToolCall.ArgumentsJson, item.Safety);
                        await AppendEventAsync(state, options, new AgentEventAppendRequest(
                            new AgentRun { Id = state.RunId }, AgentEventTypes.ToolRequest,
                            $"Model requested {item.ToolCall.Name}.",
                            new
                            {
                                item.ToolCall.Name,
                                displayName = item.Tool.UserFacingName,
                                activity = item.Tool.ActivityDescription,
                                renderHint = item.Tool.RenderHint,
                                interruptBehavior = item.Tool.InterruptBehavior,
                                reason = ReadToolReason(item.ToolCall.ArgumentsJson),
                                safetyLevel = item.Safety.Level,
                                item.Safety.Category,
                                item.Safety.IsReadOnly,
                                item.Safety.IsWriteOperation,
                                item.Safety.RequiresExplicitApproval,
                                item.Safety.IsBlocked,
                                effectPlan = item.EffectPlan,
                                render = toolUseRender
                            },
                            step.Id, item.Invocation.Id,
                            Severity: AgentEventSeverities.Info), events, ct);
                    }

                    foreach (var item in batchItems)
                    {
                        // Check policy
                        var policy = await _toolPlatform.EvaluatePolicyAsync(
                            state.ChatId, item.ToolCall.Name, item.ToolCall.ArgumentsJson, item.Safety, ct);

                        var authorization = ToolAuthorizationPolicy.Evaluate(
                            options.PermissionMode,
                            item.Safety,
                            policy,
                            item.Tool.Metadata.RequiresApproval,
                            item.Tool.RequiresUserInteraction,
                            item.Tool.Metadata.IsDestructive,
                            state.IsPlanMode,
                            options.AutoApproveTools);

                        if (authorization.IsBlocked)
                        {
                            await HandleDeniedInvocationAsync(
                                state,
                                item,
                                step,
                                authorization.ReasonCode,
                                options,
                                events,
                                ct);
                            continue;
                        }

                        if (IsRepeatedFailedInvocation(state, item.ToolCall))
                        {
                            item.Invocation.Status = ToolInvocationStatuses.Failed;
                            await CompleteInvocationWithResultAsync(
                                state,
                                item,
                                step,
                                new AgentToolResult(
                                    false,
                                    string.Empty,
                                    "Identical failed invocation suppressed. Choose a materially different tool, command, or set of arguments."),
                                options,
                                events,
                                ct);
                            continue;
                        }

                        if (authorization.RequiresApproval)
                        {
                            item.Invocation.Status = ToolInvocationStatuses.AwaitingApproval;
                            step.Status = AgentStepStatuses.AwaitingApproval;
                            state.Status = AgentRunStatuses.AwaitingApproval;
                            state.PendingToolInvocationId = item.Invocation.Id;
                            state.CurrentStep = stepNumber;
                            SyncRunState(run, state);
                            await SaveCheckpointAsync(state, ct);
                            await _db.SaveChangesAsync(ct);
                            var toolUseRender = item.Tool.RenderToolUse(item.ToolCall.ArgumentsJson, item.Safety);
                            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                                new AgentRun { Id = state.RunId }, AgentEventTypes.ApprovalRequested,
                                $"Approval needed: {item.ToolCall.Name}.",
                                new
                                {
                                    item.ToolCall.Name,
                                    displayName = item.Tool.UserFacingName,
                                    activity = item.Tool.ActivityDescription,
                                    renderHint = item.Tool.RenderHint,
                                    interruptBehavior = item.Tool.InterruptBehavior,
                                    item.Safety.Level,
                                    item.Safety.Warning,
                                    autoApproveRequested = options.AutoApproveTools,
                                    render = toolUseRender
                                }, step.Id, item.Invocation.Id,
                                Severity: item.Safety.RequiresExplicitApproval ? AgentEventSeverities.Warning : AgentEventSeverities.Info), events, ct);

                            ReportSafely(frameProgress,
                                new AgentRunFrame(stepNumber, AgentRunFrameKinds.ApprovalNeeded, events.ToArray()));
                            approvalNeeded = true;
                            break;
                        }

                        // Auto-approve and execute
                        item.Invocation.Approved = true;
                        item.Invocation.ApprovedAt = DateTime.UtcNow;
                        item.Invocation.Status = ToolInvocationStatuses.Approved;

                        var execResult = await ExecuteSingleInvocationAsync(state, item, step, options, events, ct);
                        ReportSafely(frameProgress,
                            execResult.Frame ?? AgentRunFrame.Empty(stepNumber, AgentRunFrameKinds.ToolResult));

                        if (state.Status == AgentRunStatuses.Paused)
                        {
                            assistantContent = state.ErrorMessage;
                            return new AgentRunResult(state.DeepClone(), assistantContent, lastResponse, events);
                        }

                        var priorPermissionMode = options.PermissionMode;
                        var priorAutoApprove = options.AutoApproveTools;
                        options = ApplyPersistedPermissionOptions(state, options);
                        if (!string.Equals(priorPermissionMode, options.PermissionMode, StringComparison.Ordinal) ||
                            priorAutoApprove != options.AutoApproveTools)
                            systemPrompt = await BuildSystemPromptAsync(state, options.PermissionMode, ct);
                    }

                    if (approvalNeeded) break;
                }

                if (approvalNeeded)
                {
                    // Return immediately — caller will resume after approval
                    return new AgentRunResult(state.DeepClone(), assistantContent, lastResponse, events);
                }

                state.CurrentStep = stepNumber;
                SyncRunState(run, state);
                await ExtendSoftStepBudgetIfUsefulAsync(state, run, step, options, events, ct);

                // M4.8.0: Throttled session memory extraction.
                // Not every step — init at 10K tokens, update every 5K or 3 steps.
                var currentTokens = _tokenBudget.EstimateTokens(state.Messages);
                _sessionMemoryTokenEstimate = currentTokens;
                _sessionMemoryCallCount++;
                var tokenDelta = currentTokens - _sessionMemoryLastWriteEstimate;
                var shouldWrite = currentTokens >= smInitTokenThreshold &&
                    (tokenDelta >= smUpdateTokenDelta || _sessionMemoryCallCount >= smUpdateCallDelta);
                if (shouldWrite)
                {
                    var stepMeta = await BuildRuntimeContextMetadataAsync(state.ChatId, state.RunId, ct);
                    var stepSandbox = _sandboxCommandService.GetSandboxRoot(state.ChatId);
                    var messageSnapshot = state.Messages.ToArray();
                    _ = Task.Run(() => ExtractSessionMemorySafelyAsync(
                        state.ChatId,
                        state.RunId,
                        messageSnapshot,
                        stepSandbox,
                        stepMeta.FilesChanged.ToArray(),
                        stepMeta.CommandsRun.ToArray(),
                        stepMeta.RecentFailures.ToArray(),
                        stepMeta.OpenQuestions.ToArray(),
                        stepMeta.NextActions.ToArray()), CancellationToken.None);
                    _sessionMemoryLastWriteEstimate = currentTokens;
                    _sessionMemoryCallCount = 0;
                }
            }

            // Step budget finalization
            if (state.Status == AgentRunStatuses.Running)
            {
                var finalResult = await TryFinalizeAtStepBudgetAsync(state, systemPrompt, effective, options, events, ct);
                if (finalResult != null)
                {
                    assistantContent = finalResult;
                    state.Status = AgentRunStatuses.Completed;
                    SyncRunState(run, state, terminal: true);
                }
                else
                {
                    var unresolvedFailure = state.ConsecutiveToolFailures > 0;
                    state.Status = AgentRunStatuses.Paused;
                    if (unresolvedFailure)
                        state.ErrorMessage = state.LastFailureSummary;
                    SyncRunState(run, state);
                    await AppendEventAsync(state, options, new AgentEventAppendRequest(
                        new AgentRun { Id = state.RunId }, AgentEventTypes.RunPaused,
                        unresolvedFailure
                            ? "Step budget reached with an unresolved tool failure. Progress was saved."
                            : "Step budget reached.",
                        new
                        {
                            state.CurrentStep,
                            state.MaxSteps,
                            unresolvedFailure,
                            state.LastFailureSummary,
                            resumable = true
                        },
                        Severity: AgentEventSeverities.Warning), events, ct);
                    await SaveCheckpointAsync(state, ct);
                    await _db.SaveChangesAsync(ct);
                    assistantContent = unresolvedFailure
                        ? $"Agent paused at step {state.CurrentStep}/{state.MaxSteps} with an unresolved failure. Resume to try another route."
                        : $"Agent paused at step {state.CurrentStep}/{state.MaxSteps}.";
                }
            }

            return new AgentRunResult(state.DeepClone(), assistantContent, lastResponse, events);
        }
        catch (OperationCanceledException)
        {
            state.Status = AgentRunStatuses.Cancelled;
            state.ErrorMessage = "Stopped by the user.";
            SyncRunState(run, state, terminal: true);
            await _db.SaveChangesAsync(CancellationToken.None);
            await SaveCheckpointAsync(state, CancellationToken.None);
            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                new AgentRun { Id = state.RunId }, AgentEventTypes.RunCancelled,
                "Cancelled.", new { state.CurrentStep, state.MaxSteps },
                Severity: AgentEventSeverities.Warning), events, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            state.Status = AgentRunStatuses.Failed;
            state.ErrorMessage = SecretRedactor.RedactText(ex.Message);
            SyncRunState(run, state, terminal: true);
            await _db.SaveChangesAsync(CancellationToken.None);
            await SaveCheckpointAsync(state, CancellationToken.None);
            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                new AgentRun { Id = state.RunId }, AgentEventTypes.Error,
                state.ErrorMessage, new { exceptionType = ex.GetType().Name },
                Severity: AgentEventSeverities.Error), events, CancellationToken.None);
            throw;
        }
        finally
        {
            try
            {
                await _eventStream.FlushAsync(CancellationToken.None);
            }
            catch
            {
                // Event flushing is observability, not a terminal transition.
                // Preserve the state produced by the run even when telemetry
                // storage is temporarily unavailable.
                foreach (var entry in _db.ChangeTracker.Entries<AgentEvent>()
                             .Where(entry => entry.State == EntityState.Added)
                             .ToArray())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    public async Task<AgentRunResult> ResumeAsync(
        AgentRunState state,
        AgentEngineOptions options,
        IProgress<AgentRunFrame>? frameProgress = null,
        CancellationToken ct = default)
    {
        // Resume is essentially the same as RunAsync — the engine handles pending invocations in the pre-loop
        state.ErrorMessage = null;
        return await RunAsync(state, options, frameProgress, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Private helpers
    // ═══════════════════════════════════════════════════════════════

    private sealed record RuntimeContextMetadata(
        IReadOnlyList<string> FilesChanged,
        IReadOnlyList<string> CommandsRun,
        IReadOnlyList<string> OpenQuestions,
        IReadOnlyList<string> NextActions,
        IReadOnlyList<string> PersistedOutputs,
        IReadOnlyList<string> RecentFailures,
        IReadOnlyList<string> RecentReads);

    private async Task<AgentContextPreparationResult> PrepareContextAsync(
        AgentRunState state,
        AgentContextOptions options,
        string provider,
        string model,
        bool forceCompact,
        CancellationToken ct,
        ILlmProvider? llmProvider = null,
        string? systemPrompt = null)
    {
        var budget = _tokenBudget.GetBudget(provider, model);
        var tokenState = forceCompact
            ? TokenBudgetState.CompactNow
            : _tokenBudget.CheckBudget(state.Messages, budget, options.AutoCompactTriggerTokens);

        var before = _tokenBudget.EstimateTokens(state.Messages);
        if (!forceCompact && tokenState < TokenBudgetState.CompactSoon)
        {
            return new AgentContextPreparationResult(
                state.Messages.ToList(),
                false,
                before,
                before,
                "Context is within the active model budget.");
        }

        // M4.4.0: Cooldown for ALL compaction states, not just CompactSoon.
        // Without cooldown, force-compaction from context-limit errors could fire
        // at every step, producing destructive SummarizeMiddle on each iteration.
        var cooldownSteps = tokenState switch
        {
            TokenBudgetState.Blocking => 1,
            TokenBudgetState.CompactNow => 2,
            TokenBudgetState.CompactSoon => 4,
            _ => forceCompact ? 1 : 0
        };
        if (!forceCompact && tokenState >= TokenBudgetState.CompactSoon &&
            state.CurrentStep - state.LastCompactedStep < cooldownSteps)
        {
            return new AgentContextPreparationResult(
                state.Messages.ToList(),
                false,
                before,
                before,
                "Context compaction is in cooldown.");
        }

        if (forceCompact || tokenState >= TokenBudgetState.CompactSoon)
        {
            var strategy = tokenState switch
            {
                TokenBudgetState.Blocking or TokenBudgetState.CompactNow => CompactionStrategy.SummarizeMiddle,
                TokenBudgetState.CompactSoon => CompactionStrategy.Microcompact,
                _ => CompactionStrategy.TrimToolOutputs
            };

            // M4.8.0: Progressive upgrade chain — if current level savings are
            // insufficient, escalate to the next more aggressive strategy.
            var upgradeChain = strategy switch
            {
                CompactionStrategy.TrimToolOutputs => new[]
                    { CompactionStrategy.TrimToolOutputs, CompactionStrategy.Microcompact,
                      CompactionStrategy.SummarizeMiddle, CompactionStrategy.ModelAssistedSummarize,
                      CompactionStrategy.EmergencyTruncate },
                CompactionStrategy.Microcompact => new[]
                    { CompactionStrategy.Microcompact, CompactionStrategy.SummarizeMiddle,
                      CompactionStrategy.ModelAssistedSummarize, CompactionStrategy.EmergencyTruncate },
                CompactionStrategy.SummarizeMiddle => new[]
                    { CompactionStrategy.SummarizeMiddle, CompactionStrategy.ModelAssistedSummarize,
                      CompactionStrategy.EmergencyTruncate },
                _ => new[] { strategy, CompactionStrategy.EmergencyTruncate }
            };

            foreach (var tryStrategy in upgradeChain)
            {
                var compacted = await _reactiveCompactor.CompactAsync(
                    state.Messages, tokenState, tryStrategy, _tokenBudget, ct,
                    llmProvider, systemPrompt);
                if (!compacted.WasCompacted)
                    continue;
                var saved = compacted.EstimatedTokensBefore - compacted.EstimatedTokensAfter;
                var minimumUsefulSavings = tokenState == TokenBudgetState.CompactSoon
                    ? Math.Max(1_024, compacted.EstimatedTokensBefore / 20)
                    : 1;
                if (saved >= minimumUsefulSavings &&
                    compacted.EstimatedTokensAfter < compacted.EstimatedTokensBefore)
                {
                    state.LastCompactedStep = state.CurrentStep;
                    state.LastCompactedTokenEstimate = compacted.EstimatedTokensAfter;
                    return new AgentContextPreparationResult(
                        compacted.Messages,
                        true,
                        compacted.EstimatedTokensBefore,
                        compacted.EstimatedTokensAfter,
                        $"[{tryStrategy}] {compacted.Summary}");
                }
            }
        }

        return forceCompact
            ? _contextManager.Prepare(state.Messages, options, forceCompact)
            : new AgentContextPreparationResult(
                state.Messages.ToList(),
                false,
                before,
                before,
                "No useful compaction was needed.");
    }

    private async Task<List<MessagePayload>> BuildModelMessagesWithRuntimeContextAsync(
        Guid chatId,
        Guid runId,
        IReadOnlyList<MessagePayload> source,
        CancellationToken ct)
    {
        var messages = source.ToList();
        var runtimeContext = await BuildRuntimeContextBlockAsync(chatId, runId, ct);
        if (!string.IsNullOrWhiteSpace(runtimeContext))
            messages.Add(new MessagePayload("user", runtimeContext));
        return messages;
    }

    private async Task<string> BuildRuntimeContextBlockAsync(Guid chatId, Guid runId, CancellationToken ct)
    {
        var taskSummary = await _agentTasks.BuildOpenTaskSummaryAsync(chatId, ct: ct);
        var memory = await _projectMemory.ReadAsync(chatId, ct);
        var metadata = await BuildRuntimeContextMetadataAsync(chatId, runId, ct);

        var sb = new StringBuilder();
        sb.AppendLine("[runtime context]");
        sb.AppendLine("This is durable execution context, not a new user request. Use it to continue accurately after long runs or compaction.");
        sb.AppendLine();
        sb.AppendLine("## Active Tasks");
        sb.AppendLine(taskSummary);
        sb.AppendLine();
        sb.AppendLine("## Project Memory Preview");
        sb.AppendLine(TrimForContext(memory, 4_000));
        AppendList(sb, "## Files Changed", metadata.FilesChanged);
        AppendList(sb, "## Recently Read Files (re-read if needed after compaction)", metadata.RecentReads);
        AppendList(sb, "## Persisted Output References", metadata.PersistedOutputs);
        AppendList(sb, "## Recent Commands", metadata.CommandsRun);
        AppendList(sb, "## Recent Failures", metadata.RecentFailures);
        AppendList(sb, "## Open Questions", metadata.OpenQuestions);
        AppendList(sb, "## Next Actions", metadata.NextActions);
        sb.AppendLine("[/runtime context]");
        return SecretRedactor.RedactText(sb.ToString());
    }

    private async Task<RuntimeContextMetadata> BuildRuntimeContextMetadataAsync(Guid chatId, Guid runId, CancellationToken ct)
    {
        var artifacts = await _db.Set<AgentArtifact>()
            .Where(a => a.AgentRunId == runId)
            .OrderByDescending(a => a.UpdatedAt)
            .Take(24)
            .ToListAsync(ct);
        var persistedOutputs = artifacts
            .Where(a => a.RelativePath.StartsWith(".tlah_context", StringComparison.OrdinalIgnoreCase) &&
                        a.RelativePath.Contains("tool-results", StringComparison.OrdinalIgnoreCase))
            .Select(a => $"{a.RelativePath} ({a.SizeBytes} bytes)")
            .Take(12)
            .ToList();
        var filesChanged = artifacts
            .Where(a => !persistedOutputs.Any(p => p.StartsWith(a.RelativePath, StringComparison.OrdinalIgnoreCase)))
            .Select(a => $"{a.RelativePath} ({a.SizeBytes} bytes)")
            .Take(12)
            .ToList();

        var invocations = await _db.Set<ToolInvocation>()
            .Where(i => i.AgentRunId == runId)
            .OrderByDescending(i => i.CreatedAt)
            .Take(30)
            .ToListAsync(ct);
        var commands = invocations
            .Where(i => i.ToolName is AgentToolNames.SandboxExec or AgentToolNames.TerminalExec or AgentToolNames.Git)
            .Select(i => $"{i.ToolName}: {ReadJsonString(i.ArgumentsJson, "command") ?? ReadJsonString(i.ArgumentsJson, "args") ?? TrimForContext(i.ArgumentsJson, 160)}")
            .Take(12)
            .ToList();

        var tasks = await _agentTasks.ListAsync(chatId, includeCompleted: false, limit: 20, ct);
        var openQuestions = tasks
            .Where(t => t.Status == AgentTaskStatuses.Blocked)
            .Select(t => $"{t.Title}: {TrimForContext(t.Description, 140)}")
            .Take(8)
            .ToList();
        var nextActions = tasks
            .Where(t => t.Status is AgentTaskStatuses.Pending or AgentTaskStatuses.InProgress)
            .Select(t => $"{t.Status}: {t.Title}")
            .Take(12)
            .ToList();

        var failures = await _db.Set<AgentEvent>()
            .Where(e => e.AgentRunId == runId && e.Severity == AgentEventSeverities.Error)
            .OrderByDescending(e => e.CreatedAt)
            .Take(8)
            .Select(e => $"{e.EventType}: {e.Summary}")
            .ToListAsync(ct);

        // M4.4.3: Track recently read file paths so the model knows what to
        // re-read after compaction. We list paths only (not content) — the model
        // uses the `read` tool to fetch content on demand.
        var recentReads = invocations
            .Where(i => i.ToolName is AgentToolNames.FileRead or AgentToolNames.CodeRead)
            .Select(i => ReadJsonString(i.ArgumentsJson, "file_path") ??
                         ReadJsonString(i.ArgumentsJson, "path") ??
                         TrimForContext(i.ArgumentsJson, 120))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        return new RuntimeContextMetadata(filesChanged, commands, openQuestions, nextActions, persistedOutputs, failures, recentReads);
    }

    /// <summary>
    /// M4.5.0: After compaction, re-inject the content of recently read files
    /// so the agent can continue without re-reading. Capped at 5 files,
    /// 5000 chars per file, 25000 chars total. Only called post-compaction.
    /// </summary>
    private async Task<string?> BuildPostCompactFileContextAsync(Guid chatId, Guid runId, CancellationToken ct)
    {
        var invocations = await _db.Set<ToolInvocation>()
            .Where(i => i.AgentRunId == runId &&
                        (i.ToolName == AgentToolNames.FileRead || i.ToolName == AgentToolNames.CodeRead))
            .OrderByDescending(i => i.CreatedAt)
            .Take(5)
            .ToListAsync(ct);
        if (invocations.Count == 0)
            return null;

        var sandboxRoot = _sandboxCommandService.GetSandboxRoot(chatId);
        var sb = new StringBuilder();
        sb.AppendLine("[post-compaction file context — recently read files]");
        sb.AppendLine("The following files were read before compaction. Use this content to continue without re-reading.");
        sb.AppendLine();

        var totalChars = 0;
        const int maxPerFile = 5000;
        const int maxTotal = 25000;
        foreach (var inv in invocations)
        {
            var filePath = ReadJsonString(inv.ArgumentsJson, "file_path") ??
                           ReadJsonString(inv.ArgumentsJson, "path");
            if (string.IsNullOrWhiteSpace(filePath))
                continue;

            var resolved = Path.GetFullPath(Path.Combine(sandboxRoot, filePath.TrimStart('/', '\\')));
            if (!resolved.StartsWith(sandboxRoot + Path.DirectorySeparatorChar) && resolved != sandboxRoot)
                continue; // path escape guard
            if (!File.Exists(resolved))
                continue;

            try
            {
                var content = await File.ReadAllTextAsync(resolved, Encoding.UTF8, ct);
                var truncated = content.Length <= maxPerFile
                    ? content
                    : content[..maxPerFile] + $"\n[truncated — file is {content.Length} chars]";
                sb.AppendLine($"--- {filePath} ---");
                sb.AppendLine(truncated);
                sb.AppendLine();
                totalChars += truncated.Length;
                if (totalChars >= maxTotal)
                    break;
            }
            catch
            {
                // Skip files that can't be read (locked, binary, etc.)
            }
        }

        return totalChars > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// M4.8.0: After compaction, inject a summary of available tools so the
    /// agent doesn't lose track of its tool set. Capped at 2000 chars.
    /// </summary>
    private string? BuildPostCompactToolsSummary()
    {
        var tools = _agentTools.Definitions;
        if (tools.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("[post-compaction context — available tools]");
        sb.AppendLine("The following tools are available for use:");
        sb.AppendLine();

        var totalChars = 0;
        const int maxTotal = 2000;
        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var desc = string.IsNullOrWhiteSpace(tool.Description)
                ? "(no description)"
                : tool.Description.Length > 100
                    ? tool.Description[..97] + "..."
                    : tool.Description;
            var line = $"- {tool.Name}: {desc}";
            if (totalChars + line.Length + Environment.NewLine.Length > maxTotal && totalChars > 0)
                break;
            sb.AppendLine(line);
            totalChars += line.Length + Environment.NewLine.Length;
        }

        sb.AppendLine();
        sb.AppendLine("[/post-compaction context]");
        return sb.ToString();
    }

    /// <summary>
    /// M4.9.2: After compaction, re-inject the bodies of skills the agent has
    /// already invoked this run so their instructions survive the summary
    /// boundary. Mirrors Claude Code's compact.ts skill re-injection
    /// (per-skill 5K cap, 25K total). Only skills already sent (recorded in
    /// SentSkillNames) are re-injected.
    /// </summary>
    private async Task<string?> BuildPostCompactSkillsSummaryAsync(
        AgentRunState state, CancellationToken ct)
    {
        if (_skillLoader == null || state.SentSkillNames.Count == 0)
            return null;

        const int maxPerSkill = 5_000;
        const int maxTotal = 25_000;

        var sb = new StringBuilder();
        sb.AppendLine("[post-compaction context — active skills]");
        sb.AppendLine("The following skills were invoked earlier and remain active:");
        sb.AppendLine();

        var totalChars = 0;
        foreach (var name in state.SentSkillNames)
        {
            var content = await _skillLoader.GetSkillContentAsync(name, ct);
            if (string.IsNullOrWhiteSpace(content))
                continue;
            var body = content.Length > maxPerSkill
                ? content[..maxPerSkill] + "\n[truncated]"
                : content;
            var entry = $"--- skill: {name} ---\n{body}\n";
            if (totalChars + entry.Length > maxTotal)
            {
                sb.AppendLine("[additional skills omitted to stay within budget]");
                break;
            }
            sb.Append(entry);
            totalChars += entry.Length;
        }

        sb.AppendLine("[/post-compaction context]");
        return totalChars > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// M4.9.2: After compaction, re-inject a delta of active MCP servers so
    /// the agent doesn't lose MCP context. Mirrors Claude Code's compact.ts
    /// MCP-instructions delta. Capped at 1500 chars.
    /// </summary>
    private async Task<string?> BuildPostCompactMcpDeltaAsync(CancellationToken ct)
    {
        var servers = await _toolPlatform.ListMcpServersAsync(ct: ct);
        var active = servers.Where(s => s.Enabled).ToList();
        if (active.Count == 0)
            return null;

        const int maxTotal = 1_500;
        var sb = new StringBuilder();
        sb.AppendLine("[post-compaction context — active MCP servers]");
        sb.AppendLine("MCP servers available via mcp_call / mcp_list_tools:");
        sb.AppendLine();

        var totalChars = 0;
        foreach (var s in active.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var transport = string.IsNullOrWhiteSpace(s.Transport) ? "stdio" : s.Transport;
            var line = $"- {s.Name} ({transport})";
            if (totalChars + line.Length + Environment.NewLine.Length > maxTotal && totalChars > 0)
                break;
            sb.AppendLine(line);
            totalChars += line.Length + Environment.NewLine.Length;
        }

        sb.AppendLine();
        sb.AppendLine("[/post-compaction context]");
        return totalChars > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// M4.9.2: After compaction, re-inject plan-mode state so the agent
    /// remembers plan mode and the pre-plan mode to restore on exit.
    /// </summary>
    private static string? BuildPostCompactPlanSummary(AgentRunState state)
    {
        if (!state.IsPlanMode && string.IsNullOrEmpty(state.PrePlanMode))
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("[post-compaction context — plan mode state]");
        sb.AppendLine(state.IsPlanMode
            ? "Currently in plan mode (read-only research). Write tools are intercepted until exit_plan_mode is approved."
            : "Plan mode is not active.");
        if (!string.IsNullOrEmpty(state.PrePlanMode))
            sb.AppendLine($"Permission mode to restore on plan exit: {state.PrePlanMode}");
        sb.AppendLine("[/post-compaction context]");
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        sb.AppendLine();
        sb.AppendLine(title);
        if (items.Count == 0)
        {
            sb.AppendLine("- none");
            return;
        }

        foreach (var item in items)
            sb.AppendLine($"- {item}");
    }

    private static string TrimForContext(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return value.Length <= maxChars ? value : value[..maxChars] + "\n[truncated for runtime context]";
    }

    private static string? ReadJsonString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> BuildSystemPromptAsync(AgentRunState state, string permissionMode, CancellationToken ct)
    {
        var prompt = await SystemPromptBuilder.BuildAsync(
            _db.Set<Chat>(), _db.Set<GlobalSettings>(), _db.Set<AgentFile>(),
            _db.Set<ProjectSpace>(), _db.Set<ConfigProfile>(), state.ChatId, ct);
        var memoryPath = await _projectMemory.GetMemoryPathAsync(state.ChatId, ct);
        var projectMemory = await _projectMemory.ReadAsync(state.ChatId, ct);
        // Simplified append (delegates to actual implementation in caller)
        if (!string.IsNullOrWhiteSpace(projectMemory))
            prompt += $"\n\n[project memory: {memoryPath}]\n{projectMemory[..Math.Min(projectMemory.Length, 12_000)]}";
        var taskSummary = await _agentTasks.BuildOpenTaskSummaryAsync(state.ChatId, ct: ct);
        prompt += $"\n\n[current tracked tasks]\n{taskSummary}";
        prompt += BuildAgentInstructions(
            _sandboxCommandService.GetSandboxRoot(state.ChatId),
            _agentTools.Definitions.Select(t => t.Name),
            permissionMode);
        // M4.9.0: Skill listing — inject available skills with budget control.
        if (_skillLoader != null)
        {
            var skills = await _skillLoader.LoadSkillsAsync(ct);
            if (skills.Count > 0)
            {
                const int maxTotalChars = 8_000; // ~1% of 200K window
                var skillBlock = new StringBuilder();
                skillBlock.AppendLine("<system-reminder>");
                skillBlock.AppendLine("The following skills are available for use with the skill tool:");
                skillBlock.AppendLine();

                var totalChars = 0;
                var newSent = 0;
                const int maxDescChars = 250;
                foreach (var skill in skills.OrderBy(s => s.Source == "bundled" ? 0 : 1))
                {
                    if (totalChars >= maxTotalChars)
                        break;
                    var name = skill.Name;
                    if (!state.SentSkillNames.Add(name))
                        continue; // Already sent — skip to preserve prompt cache.

                    var desc = string.IsNullOrWhiteSpace(skill.WhenToUse)
                        ? skill.Description
                        : $"{skill.Description} — {skill.WhenToUse}";
                    if (desc.Length > maxDescChars)
                        desc = desc[..(maxDescChars - 1)] + "…";

                    var line = $"- {name}: {desc}";
                    if (totalChars + line.Length + Environment.NewLine.Length > maxTotalChars && totalChars > 0)
                    {
                        // Non-bundled skills: downgrade to name-only if budget tight.
                        if (skill.Source != "bundled")
                            line = $"- {name}";
                        else
                            break; // Bundled — stop adding anything.
                    }
                    skillBlock.AppendLine(line);
                    totalChars += line.Length + Environment.NewLine.Length;
                    newSent++;
                }
                skillBlock.AppendLine();
                skillBlock.AppendLine("When a skill matches the user's request, invoke the skill tool BEFORE generating any other response about the task. NEVER mention a skill without actually calling the skill tool.");
                skillBlock.AppendLine("</system-reminder>");
                if (newSent > 0)
                    prompt += $"\n\n{skillBlock}";
            }
        }
        // M4.9.0: Append active output style prompt.
        if (_outputStyle != null)
        {
            var gs = await _db.Set<GlobalSettings>().FirstOrDefaultAsync(g => g.Id == 1, ct);
            var styleName = gs?.OutputStyle ?? _outputStyle.DefaultStyleName;
            var style = _outputStyle.GetStyle(styleName);
            if (style != null && !string.IsNullOrWhiteSpace(style.Prompt))
                prompt += $"\n\n{style.Prompt}";
        }
        return prompt;
    }

    private static string BuildAgentInstructions(string sandboxRoot, IEnumerable<string> toolNames, string permissionMode)
    {
        var tools = string.Join(", ", toolNames.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
        var normalizedMode = AgentPermissionModes.Normalize(permissionMode);
        var hostAccessLine = AgentPermissionModes.IsPlan(normalizedMode)
            ? "Permission mode: Plan (read-only). Explore, research, and design only. File writes, terminal execution, and destructive operations are blocked until the plan is approved via exit_plan_mode. Write your plan to .tlah_context/plans/{{chatId}}-plan.md before calling exit_plan_mode."
            : AgentPermissionModes.IsBypass(normalizedMode)
            ? "Permission mode: Full access. terminal_exec may run unrestricted local PowerShell and can access host files, programs, and the internet when needed. Use it deliberately and state the reason."
            : normalizedMode == AgentPermissionModes.AutoApprove
                ? "Permission mode: Auto approve. The app approves detected tool directions automatically, but safety and policy blocks still apply."
                : "Permission mode: Ask approval. High-risk or policy-relevant tool calls must wait for explicit user approval.";
        var safetyBoundaryLine = AgentPermissionModes.IsBypass(normalizedMode)
            ? "Full access may use host paths, installed programs, network endpoints, package managers, Git, and privileged commands when they are necessary for the task. The runtime still rejects catastrophic root, disk, boot, or account-destruction operations."
            : "Work only inside the workspace unless an exact operation is approved. Do not access unrelated host files or run destructive, privileged, registry, service, shutdown, or system-configuration operations without the permission flow.";
        return $"""

        TLAH Agent Mode is enabled.
        Registered tools: {tools}
        Workspace root: {sandboxRoot}
        {hostAccessLine}
        Use the workspace root as the default working directory.
        {safetyBoundaryLine}
        Prefer typed memory, file, code, Git, HTTP, search, browser, terminal, and MCP tools over ad-hoc shell commands.
        Tool map: code_read/code_grep/code_glob/code_symbols inspect code; code_edit/code_multi_edit/code_apply_patch change code; file_* manages sandbox artifacts; web_search/browser_read/http_request handle web and APIs; mcp_* discovers and calls configured MCP servers; todo_* and task_* keep durable plans; terminal_exec is the escape hatch for commands and host-level work in Full access mode.
        For development work, prefer code_read, code_grep, code_glob, code_diff, code_edit, code_multi_edit, code_apply_patch, code_rollback, and code_diagnostics.
        For file work, prefer file_list, file_read, file_write, file_search, and file_send. When you create a file the user should see, preview, download, or use outside the workspace, call file_send before the final answer.
        For multi-step work, maintain a persistent task plan with todo_write, task_create, task_update, and task_list. Keep one current task in_progress when possible and mark completed tasks promptly.
        When a tool output is persisted under .tlah_context/tool-results, use read_persisted_output to recover details instead of asking the user to rerun work.
        Use tool_search when you need to discover less-common tools, MCP capabilities, task tools, or persisted-output tools.
        Do not assume a capability is unavailable before checking tool_search or mcp_list_tools when the user's request suggests external tools, repositories, browsers, files, or integrations.
        Use task_create with background=true only for independent local background work; use task_output, task_stop, and task_send_message to inspect or control it.
        Use mcp_list_tools before mcp_call, and mcp_list_resources before mcp_read_resource. Keep credentials referenced only by broker entry name; never print, store, or ask for secrets.
        Request one tool call at a time unless multiple read-only calls are clearly independent. Include a short reason in arguments when the schema supports it.
        After a tool result is returned, either request the next action or provide the final answer.
        """;
    }

    private async Task<AgentEvent> AppendEventAsync(
        AgentRunState state,
        AgentEngineOptions options,
        AgentEventAppendRequest request,
        List<AgentEvent> events,
        CancellationToken ct)
    {
        var evt = await _eventStream.AppendAsync(request, ct);
        events.Add(evt);
        if (options.Progress != null)
        {
            try
            {
                var snapshot = await BuildSnapshotAsync(state, ct);
                ReportSafely(options.Progress, new AgentProgressUpdate(
                    evt.AgentRunId,
                    evt.SequenceNumber,
                    evt.EventType,
                    evt.Severity,
                    evt.Summary,
                    evt.CreatedAt,
                    snapshot,
                    evt.AgentStepId,
                    evt.ToolInvocationId,
                    evt.DataJson));
            }
            catch
            {
                // Snapshot/progress telemetry must never become the run result.
            }
        }

        return evt;
    }

    private async Task ExtractSessionMemorySafelyAsync(
        Guid chatId,
        Guid runId,
        IReadOnlyList<MessagePayload> messages,
        string sandboxRoot,
        IReadOnlyList<string> filesChanged,
        IReadOnlyList<string> commandsRun,
        IReadOnlyList<string> recentFailures,
        IReadOnlyList<string> openQuestions,
        IReadOnlyList<string> nextActions)
    {
        try
        {
            await _sessionMemory.ExtractAsync(
                chatId,
                runId,
                messages,
                sandboxRoot,
                filesChanged,
                commandsRun,
                recentFailures,
                openQuestions,
                nextActions,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Session memory extraction failed: {ex}");
        }
    }

    private async Task<AgentRunSnapshot> BuildSnapshotAsync(AgentRunState state, CancellationToken ct)
    {
        var pending = await _db.Set<ToolInvocation>()
            .Where(i => i.AgentRunId == state.RunId &&
                        i.Status == ToolInvocationStatuses.AwaitingApproval)
            .OrderBy(i => i.CreatedAt)
            .Select(i => new ToolInvocationSnapshot(
                i.Id,
                i.ToolName,
                i.ArgumentsJson,
                i.Status,
                i.SafetyLevel,
                i.SafetySummary,
                i.SafetyJson,
                null))
            .FirstOrDefaultAsync(ct);
        var artifactCount = await _db.Set<AgentArtifact>()
            .CountAsync(a => a.AgentRunId == state.RunId, ct);
        return new AgentRunSnapshot(
            state.RunId,
            state.ChatId,
            state.TurnId,
            state.Status,
            state.CurrentStep,
            state.MaxSteps,
            state.ErrorMessage,
            artifactCount,
            pending);
    }

    private static void SyncRunState(AgentRun run, AgentRunState state, bool terminal = false)
    {
        run.Status = state.Status;
        run.CurrentStep = state.CurrentStep;
        run.MaxSteps = state.MaxSteps;
        run.ErrorMessage = state.ErrorMessage;
        run.UpdatedAt = DateTime.UtcNow;
        if (terminal && run.CompletedAt == null)
            run.CompletedAt = DateTime.UtcNow;
    }

    private async Task UpsertArtifactsAsync(
        Guid runId,
        IReadOnlyList<AgentToolArtifact>? artifacts,
        CancellationToken ct)
    {
        if (artifacts is not { Count: > 0 })
            return;

        foreach (var artifact in artifacts)
        {
            var existing = await _db.Set<AgentArtifact>().FirstOrDefaultAsync(
                a => a.AgentRunId == runId && a.RelativePath == artifact.RelativePath,
                ct);
            if (existing == null)
            {
                _db.Set<AgentArtifact>().Add(new AgentArtifact
                {
                    AgentRunId = runId,
                    RelativePath = artifact.RelativePath,
                    ContentType = artifact.ContentType,
                    SizeBytes = artifact.SizeBytes,
                    Sha256 = artifact.Sha256
                });
                continue;
            }

            existing.ContentType = artifact.ContentType;
            existing.SizeBytes = artifact.SizeBytes;
            existing.Sha256 = artifact.Sha256;
            existing.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static string FormatToolResultMessage(
        int step,
        string toolName,
        AgentToolResult result,
        AgentToolRenderBlock? render = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Agent tool result #{step}");
        sb.AppendLine($"Tool: {render?.Title ?? AgentToolUx.UserFacingName(toolName)}");
        if (!string.IsNullOrWhiteSpace(render?.Subtitle))
            sb.AppendLine($"Summary: {render.Subtitle}");
        sb.AppendLine($"Success: {result.Success}");
        var output = render?.Body ?? result.Output;
        if (!string.IsNullOrWhiteSpace(output))
            sb.AppendLine(output.TrimEnd());
        if (!string.IsNullOrWhiteSpace(result.Error))
            sb.AppendLine($"Error: {result.Error}");
        if (!string.IsNullOrWhiteSpace(result.Warning))
            sb.AppendLine($"Warning: {result.Warning}");
        if (result.Artifacts is { Count: > 0 })
        {
            sb.AppendLine("Artifacts:");
            foreach (var artifact in result.Artifacts)
                sb.AppendLine($"- {artifact.RelativePath} ({artifact.SizeBytes} bytes)");
        }

        var text = sb.ToString().TrimEnd();
        if (string.Equals(toolName, AgentToolNames.FileSend, StringComparison.OrdinalIgnoreCase) &&
            result.Artifacts is { Count: > 0 })
        {
            text = MessageAttachmentFormatter.Compose(
                text,
                result.Artifacts
                    .Select(a => new MessageAttachment(a.RelativePath, a.ContentType, a.SizeBytes, a.Sha256))
                    .ToArray());
        }

        return text;
    }

    private async Task SaveCheckpointAsync(AgentRunState state, CancellationToken ct)
    {
        await _checkpointStore.SaveAsync(
            new AgentRun { Id = state.RunId }, state.CurrentStep,
            JsonSerializer.Serialize(state), ct);
    }

    private async Task ExtendSoftStepBudgetIfUsefulAsync(
        AgentRunState state,
        AgentRun run,
        AgentStep step,
        AgentEngineOptions options,
        List<AgentEvent> events,
        CancellationToken ct)
    {
        const int hardBudget = 192;
        var nextBudget = CalculateExtendedSoftStepBudget(state);
        if (nextBudget == state.MaxSteps)
            return;

        var previousBudget = state.MaxSteps;
        var madeRecentProgress = state.LastSuccessfulStep >= state.CurrentStep - 4;
        var needsFailureRecovery = state.ConsecutiveToolFailures > 0 &&
                                   state.CompletionRecoveryAttempts == 0;
        state.MaxSteps = nextBudget;
        state.BudgetExtensionCount++;
        SyncRunState(run, state);
        await AppendEventAsync(state, options, new AgentEventAppendRequest(
            new AgentRun { Id = state.RunId },
            AgentEventTypes.RuntimeMetrics,
            $"Soft step budget extended from {previousBudget} to {state.MaxSteps}.",
            new
            {
                previousBudget,
                state.MaxSteps,
                hardBudget,
                madeRecentProgress,
                needsFailureRecovery,
                state.BudgetExtensionCount
            },
            step.Id), events, ct);
        await SaveCheckpointAsync(state, ct);
    }

    internal static int CalculateExtendedSoftStepBudget(AgentRunState state)
    {
        const int productionSoftBudget = 48;
        const int extensionSize = 24;
        const int hardBudget = 192;

        if (state.CurrentStep < state.MaxSteps ||
            state.MaxSteps < productionSoftBudget ||
            state.MaxSteps >= hardBudget)
        {
            return state.MaxSteps;
        }

        var madeRecentProgress = state.LastSuccessfulStep >= state.CurrentStep - 4;
        var needsFailureRecovery = state.ConsecutiveToolFailures > 0 &&
                                   state.CompletionRecoveryAttempts == 0;
        return madeRecentProgress || needsFailureRecovery
            ? Math.Min(hardBudget, state.MaxSteps + extensionSize)
            : state.MaxSteps;
    }

    private static bool IsRepeatedFailedInvocation(AgentRunState state, LlmToolCall toolCall) =>
        state.ConsecutiveToolFailures > 0 &&
        string.Equals(
            state.LastFailedInvocationSignature,
            ComputeInvocationSignature(toolCall),
            StringComparison.Ordinal);

    private static string ComputeInvocationSignature(LlmToolCall toolCall)
    {
        var payload = $"{AgentToolNames.Normalize(toolCall.Name)}\n{toolCall.ArgumentsJson.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string BuildFailureRecoveryDirective(
        AgentRunState state,
        bool completionWasAttempted)
    {
        var attempted = completionWasAttempted
            ? "Your previous response attempted to stop while the failure remained unresolved. "
            : string.Empty;
        var failure = string.IsNullOrWhiteSpace(state.LastFailureSummary)
            ? "The latest tool invocation failed."
            : $"Latest failure: {state.LastFailureSummary}";
        return $"""
        [failure recovery]
        {attempted}{failure}
        Do not repeat the identical failed invocation. Inspect the error and choose a materially different tool, command, argument set, or smaller step. If no safe alternative can make progress, call ask_user_question with concrete recovery choices. Do not claim the task is complete while this failure is unresolved.
        [/failure recovery]
        """;
    }

    private static LlmToolCall CreateRecoveryQuestionCall(AgentRunState state)
    {
        var failure = string.IsNullOrWhiteSpace(state.LastFailureSummary)
            ? "the latest tool failure"
            : TrimForContext(state.LastFailureSummary, 220);
        var arguments = JsonSerializer.Serialize(new
        {
            questions = new[]
            {
                new
                {
                    header = "Recovery",
                    question = $"The agent could not recover from {failure}. How should it continue?",
                    options = new[]
                    {
                        new
                        {
                            label = "Try another way",
                            description = "Continue with a materially different tool or approach."
                        },
                        new
                        {
                            label = "Stop and summarize",
                            description = "Stop execution and explain completed work, the blocker, and next steps."
                        }
                    },
                    multiSelect = false
                }
            }
        });
        return new LlmToolCall(
            $"recovery-{Guid.NewGuid():N}",
            AgentToolNames.AskUserQuestion,
            arguments);
    }

    private static void EnqueueDeferredToolCalls(
        AgentRunState state,
        IEnumerable<LlmToolCall> calls)
    {
        var known = state.DeferredToolCalls
            .Select(ComputeInvocationSignature)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            if (known.Add(ComputeInvocationSignature(call)))
                state.DeferredToolCalls.Add(call with { });
        }

        if (state.DeferredToolCalls.Count > 0)
        {
            state.DeferredToolDirectivePending = true;
            state.DeferredToolRecoveryAttempts = 0;
        }
    }

    private static void AcknowledgeDeferredToolReRequests(
        AgentRunState state,
        IReadOnlyList<LlmToolCall> requestedCalls)
    {
        if (state.DeferredToolCalls.Count == 0)
            return;

        foreach (var requested in requestedCalls)
        {
            var signature = ComputeInvocationSignature(requested);
            var index = state.DeferredToolCalls.FindIndex(call =>
                string.Equals(ComputeInvocationSignature(call), signature, StringComparison.Ordinal));
            if (index < 0)
            {
                // The model is allowed to revise arguments while reconsidering
                // a deferred operation. Match one sibling by normalized name,
                // but never inject the old arguments back into provider history.
                index = state.DeferredToolCalls.FindIndex(call =>
                    string.Equals(
                        AgentToolNames.Normalize(call.Name),
                        AgentToolNames.Normalize(requested.Name),
                        StringComparison.OrdinalIgnoreCase));
            }
            if (index >= 0)
                state.DeferredToolCalls.RemoveAt(index);
        }

        if (state.DeferredToolCalls.Count == 0)
        {
            state.DeferredToolDirectivePending = false;
            state.DeferredToolRecoveryAttempts = 0;
        }
        else
        {
            state.DeferredToolDirectivePending = true;
        }
    }

    private static string BuildDeferredToolDirective(
        IReadOnlyList<LlmToolCall> calls,
        bool completionWasAttempted = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[deferred tool calls]");
        if (completionWasAttempted)
            sb.AppendLine("Your previous response tried to finish before all deferred sibling calls were reconsidered.");
        sb.AppendLine("The runtime isolated an earlier approval/user-interaction call to preserve provider tool_call/result pairing. The sibling requests below were NOT executed:");
        foreach (var call in calls.Take(8))
        {
            var arguments = SecretRedactor.RedactText(TrimForContext(call.ArgumentsJson, 500));
            sb.AppendLine($"- {AgentToolNames.Normalize(call.Name)} arguments: {arguments}");
        }
        if (calls.Count > 8)
            sb.AppendLine($"- ... and {calls.Count - 8} more deferred request(s).");
        sb.AppendLine("Reassess each request against the latest tool result. Re-request every operation that is still needed (you may correct its arguments); do not assume any deferred operation ran. If none should run, call ask_user_question and let the user explicitly choose to skip them. Do not claim completion while this list remains unresolved.");
        sb.Append("[/deferred tool calls]");
        return sb.ToString();
    }

    private static LlmToolCall CreateDeferredToolQuestionCall(AgentRunState state)
    {
        var names = string.Join(", ", state.DeferredToolCalls
            .Select(call => AgentToolNames.Normalize(call.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6));
        var arguments = JsonSerializer.Serialize(new
        {
            questions = new[]
            {
                new
                {
                    header = "Deferred work",
                    question = $"The model did not reconsider deferred tool work ({names}). How should it continue?",
                    options = new[]
                    {
                        new { label = "Reassess and run", description = "Re-request the still-needed operations with current arguments." },
                        new { label = "Skip deferred work", description = "Explicitly skip these operations and explain the resulting limitation." }
                    },
                    multiSelect = false
                }
            }
        });
        return new LlmToolCall(
            $"recovery-deferred-{Guid.NewGuid():N}",
            AgentToolNames.AskUserQuestion,
            arguments);
    }

    internal static string? ReadSyntheticQuestionAnswer(
        LlmToolCall toolCall,
        string expectedHeader)
    {
        try
        {
            using var document = JsonDocument.Parse(toolCall.ArgumentsJson);
            if (!document.RootElement.TryGetProperty("answers", out var answers) ||
                answers.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var properties = answers.EnumerateObject().ToArray();
            if (properties.Length == 0)
                return null;

            // Newer clients key answers by the compact question header; older
            // releases used the full question text. Prefer an exact/header
            // match, then accept the sole answer for this one-question runtime
            // prompt so checkpoints remain forward-compatible.
            JsonProperty? selected = null;
            foreach (var property in properties)
            {
                if (string.Equals(property.Name, expectedHeader, StringComparison.OrdinalIgnoreCase))
                {
                    selected = property;
                    break;
                }
            }
            if (selected == null)
            {
                foreach (var property in properties)
                {
                    if (property.Name.Contains(expectedHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = property;
                        break;
                    }
                }
            }
            if (selected == null && properties.Length == 1)
                selected = properties[0];
            if (selected == null)
                return null;

            return selected.Value.Value.ValueKind switch
            {
                JsonValueKind.String => selected.Value.Value.GetString()?.Trim(),
                JsonValueKind.Array => selected.Value.Value.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()?.Trim())
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool? ApplySyntheticQuestionResolution(
        AgentRunState state,
        LlmToolCall toolCall)
    {
        if (toolCall.Id.StartsWith("recovery-deferred-", StringComparison.Ordinal))
        {
            var choice = ReadSyntheticQuestionAnswer(toolCall, "Deferred work");
            // Reassessment is a request to continue processing the durable
            // sibling set, not permission to discard it. Only the explicit
            // skip choice resolves deferred work.
            if (string.Equals(choice, "Skip deferred work", StringComparison.OrdinalIgnoreCase))
            {
                state.DeferredToolCalls.Clear();
                state.DeferredToolDirectivePending = false;
                state.DeferredToolRecoveryAttempts = 0;
            }
            else
            {
                state.DeferredToolDirectivePending = state.DeferredToolCalls.Count > 0;
            }
            // This question resolves only the deferred sibling set. It must
            // never double as evidence that an unrelated failed operation was
            // recovered.
            return false;
        }

        if (toolCall.Id.StartsWith("recovery-", StringComparison.Ordinal) &&
            !toolCall.Id.StartsWith("recovery-protocol-", StringComparison.Ordinal) &&
            !toolCall.Id.StartsWith("recovery-tasks-", StringComparison.Ordinal))
        {
            var choice = ReadSyntheticQuestionAnswer(toolCall, "Recovery");
            return string.Equals(choice, "Stop and summarize", StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }

    private static string BuildInvalidToolRecoveryDirective(
        IReadOnlyList<ToolProtocolGuardIssue> issues,
        int attempt)
    {
        var details = issues.Count == 0
            ? "No registered, provider-safe tool call remained."
            : string.Join("; ", issues.Take(6).Select(issue => $"{issue.Code}: {issue.Summary}"));
        return $"""
        [tool protocol repair {attempt}/2]
        The previous tool request was rejected before execution. {details}
        Produce a fresh call using one registered tool name and a valid JSON object matching that tool's schema. Do not repeat the malformed call. If you cannot formulate a valid call after this repair attempt, ask the user for a concrete recovery choice.
        [/tool protocol repair]
        """;
    }

    private static LlmToolCall CreateProtocolRepairQuestionCall(
        IReadOnlyList<ToolProtocolGuardIssue> issues)
    {
        var issue = issues.FirstOrDefault()?.Summary ?? "the tool request remained invalid";
        var arguments = JsonSerializer.Serialize(new
        {
            questions = new[]
            {
                new
                {
                    header = "Tool request",
                    question = $"The agent could not produce a valid tool call after two repairs ({TrimForContext(issue, 180)}). How should it continue?",
                    options = new[]
                    {
                        new { label = "Try a simpler step", description = "Continue with one smaller, schema-valid operation." },
                        new { label = "Stop and explain", description = "Pause execution and explain the protocol blocker." }
                    },
                    multiSelect = false
                }
            }
        });
        return new LlmToolCall(
            $"recovery-protocol-{Guid.NewGuid():N}",
            AgentToolNames.AskUserQuestion,
            arguments);
    }

    private async Task<IReadOnlyList<AgentTaskSnapshot>> GetOpenCompletionTasksAsync(
        Guid chatId,
        CancellationToken ct)
    {
        var tasks = await _agentTasks.ListAsync(chatId, includeCompleted: false, limit: 40, ct);
        return tasks
            .Where(task => task.Status is AgentTaskStatuses.Pending or AgentTaskStatuses.InProgress)
            .ToList();
    }

    private static string BuildOpenTaskRecoveryDirective(IReadOnlyList<AgentTaskSnapshot> tasks)
    {
        var lines = string.Join("\n", tasks.Take(12).Select(task => $"- [{task.Status}] {task.Title}"));
        return $"""
        [tracked task completion gate]
        A final answer was attempted while durable tasks are still pending or in progress:
        {lines}
        Continue the work, or update each task to completed/cancelled/blocked with an accurate reason before giving a final answer. If user input is required, call ask_user_question. Do not claim the run is complete while these task states remain open.
        [/tracked task completion gate]
        """;
    }

    private static LlmToolCall CreateOpenTaskQuestionCall(IReadOnlyList<AgentTaskSnapshot> tasks)
    {
        var names = string.Join(", ", tasks.Take(6).Select(task => task.Title));
        var arguments = JsonSerializer.Serialize(new
        {
            questions = new[]
            {
                new
                {
                    header = "Open tasks",
                    question = $"Tracked tasks remain open ({names}). How should the agent proceed?",
                    options = new[]
                    {
                        new { label = "Continue tasks", description = "Keep working and update the task states when done." },
                        new { label = "Reconcile status", description = "Review and accurately close, cancel, or block the remaining tasks." }
                    },
                    multiSelect = false
                }
            }
        });
        return new LlmToolCall(
            $"recovery-tasks-{Guid.NewGuid():N}",
            AgentToolNames.AskUserQuestion,
            arguments);
    }

    private async Task FinalizeStepFailed(
        AgentRun run,
        AgentStep step,
        AgentRunState state,
        AgentEngineOptions options,
        string error,
        List<AgentEvent> events,
        CancellationToken ct)
    {
        step.Kind = "error";
        step.Status = AgentStepStatuses.Failed;
        step.Summary = SecretRedactor.RedactText(error);
        step.CompletedAt = DateTime.UtcNow;
        state.Status = AgentRunStatuses.Failed;
        state.ErrorMessage = step.Summary;
        SyncRunState(run, state, terminal: true);
        await AppendEventAsync(state, options, new AgentEventAppendRequest(
            new AgentRun { Id = state.RunId }, AgentEventTypes.Error, step.Summary,
            Severity: AgentEventSeverities.Error), events, ct);
        await SaveCheckpointAsync(state, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task PauseForTransientProviderFailureAsync(
        AgentRun run,
        AgentStep step,
        AgentRunState state,
        AgentEngineOptions options,
        string error,
        List<AgentEvent> events,
        CancellationToken ct)
    {
        var summary = SecretRedactor.RedactText(error);
        step.Kind = "provider_transient_failure";
        step.Status = AgentStepStatuses.Failed;
        step.Summary = summary;
        step.CompletedAt = DateTime.UtcNow;
        state.CurrentStep = step.StepNumber;
        state.Status = AgentRunStatuses.Paused;
        state.ErrorMessage = summary;
        SyncRunState(run, state);
        await AppendEventAsync(state, options, new AgentEventAppendRequest(
            new AgentRun { Id = state.RunId },
            AgentEventTypes.RunPaused,
            "Model provider remained temporarily unavailable after three attempts. Progress was saved.",
            new
            {
                state.CurrentStep,
                state.MaxSteps,
                state.ConsecutiveProviderFailures,
                error = summary,
                resumable = true
            },
            step.Id,
            Severity: AgentEventSeverities.Warning), events, ct);
        await SaveCheckpointAsync(state, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task HandleDeniedInvocationAsync(AgentRunState state, ToolBatchItem item, AgentStep step,
        string reason, AgentEngineOptions options, List<AgentEvent> events, CancellationToken ct)
    {
        item.Invocation.Approved = false;
        item.Invocation.ApprovedAt = DateTime.UtcNow;
        item.Invocation.Status = reason == "denied_by_policy" ? ToolInvocationStatuses.Denied : ToolInvocationStatuses.Failed;
        await AppendEventAsync(state, options, new AgentEventAppendRequest(
            new AgentRun { Id = state.RunId }, reason == "denied_by_policy" ? AgentEventTypes.ApprovalDenied : AgentEventTypes.Error,
            $"Invocation {reason}: {item.ToolCall.Name}.",
            new
            {
                item.ToolCall.Name,
                displayName = item.Tool.UserFacingName,
                activity = item.Tool.ActivityDescription,
                item.Safety.Level,
                item.Safety.Warning
            }, step.Id, item.Invocation.Id,
            Severity: AgentEventSeverities.Warning), events, ct);
        var error = reason == "denied_by_policy"
            ? "Denied by policy."
            : item.Safety.Warning ?? item.Safety.Summary;
        await CompleteInvocationWithResultAsync(state, item, step,
            new AgentToolResult(false, string.Empty, error),
            options, events, ct);
    }

    private async Task<(AgentRunFrame? Frame, List<AgentEvent> Events)> ExecuteSingleInvocationAsync(
        AgentRunState state, ToolInvocation invocation, AgentEngineOptions options, CancellationToken ct)
    {
        var events = new List<AgentEvent>();
        var step = await _db.Set<AgentStep>().FirstAsync(s => s.Id == invocation.AgentStepId, ct);
        var executionArguments = ProtectedLocalData.Reveal(invocation.ProtectedArgumentsJson);
        if (string.IsNullOrWhiteSpace(executionArguments))
        {
            executionArguments = state.Messages
                .SelectMany(m => m.ToolCalls ?? [])
                .LastOrDefault(call => string.Equals(
                    call.Id,
                    invocation.ProviderCallId,
                    StringComparison.Ordinal))
                ?.ArgumentsJson;
        }
        if (string.IsNullOrWhiteSpace(executionArguments))
            executionArguments = invocation.ArgumentsJson;

        if (!_agentTools.TryGet(invocation.ToolName, out var tool))
        {
            invocation.Status = ToolInvocationStatuses.Failed;
            step.Status = AgentStepStatuses.Failed;
            await CompleteInvocationWithResultAsync(state, new ToolBatchItem(
                new LlmToolCall(invocation.ProviderCallId, invocation.ToolName, executionArguments),
                new PlaceholderTool(), invocation, ToolSafetyAssessment.LowRead("fallback", "fallback")),
                step, new AgentToolResult(false, string.Empty, $"Tool unavailable: {invocation.ToolName}"), options, events, ct);
            return (null, events);
        }

        var preview = await _toolLifecycleRunner.PreviewAsync(
            state.ChatId,
            invocation.ToolName,
            executionArguments,
            ct);
        var item = new ToolBatchItem(
            new LlmToolCall(invocation.ProviderCallId, invocation.ToolName, executionArguments),
            tool,
            invocation,
            preview.Safety,
            preview.EffectPlan);

        return await ExecuteSingleInvocationAsync(
            state,
            item,
            step,
            options,
            events,
            ct,
            explicitlyApproved: invocation.ExplicitUserApproval);
    }

    private async Task<(AgentRunFrame? Frame, List<AgentEvent> Events)> ExecuteSingleInvocationAsync(
        AgentRunState state, ToolBatchItem item, AgentStep step, AgentEngineOptions options,
        List<AgentEvent> events, CancellationToken ct, bool explicitlyApproved = false)
    {
        if (item.Invocation.Approved != true)
        {
            var result = new AgentToolResult(false, string.Empty, ApprovalRejectionMessage(item.Invocation));
            item.Invocation.Status = ToolInvocationStatuses.Denied;
            step.Status = AgentStepStatuses.Denied;
            await CompleteInvocationWithResultAsync(state, item, step, result, options, events, ct);
            return (new AgentRunFrame(step.StepNumber, AgentRunFrameKinds.ToolResult, events.ToArray()), events);
        }

        // Re-check only immutable safety at the execution boundary. A user grant
        // authorizes this exact persisted invocation and must survive ordinary
        // policy or contextual restriction changes during resume.
        var policy = await _toolPlatform.EvaluatePolicyAsync(
            state.ChatId, item.ToolCall.Name, item.ToolCall.ArgumentsJson, item.Safety, ct);
        var authorization = ToolAuthorizationPolicy.Evaluate(
            options.PermissionMode,
            item.Safety,
            policy,
            item.Tool.Metadata.RequiresApproval,
            item.Tool.RequiresUserInteraction,
            item.Tool.Metadata.IsDestructive,
            state.IsPlanMode,
            options.AutoApproveTools,
            explicitlyApproved);
        if (authorization.IsBlocked || authorization.RequiresApproval)
        {
            await HandleDeniedInvocationAsync(
                state, item, step,
                authorization.ReasonCode,
                options, events, ct);
            return (new AgentRunFrame(step.StepNumber, AgentRunFrameKinds.ToolResult, events.ToArray()), events);
        }
        var policyAuthorized = string.Equals(
            authorization.ReasonCode,
            "stored_allow_policy",
            StringComparison.Ordinal);

        item.Invocation.Status = ToolInvocationStatuses.Running;
        item.Invocation.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var toolUseRender = item.Tool.RenderToolUse(item.ToolCall.ArgumentsJson, item.Safety);
        await AppendEventAsync(state, options, new AgentEventAppendRequest(
            new AgentRun { Id = state.RunId }, AgentEventTypes.ToolStarted,
            $"Running {item.ToolCall.Name}.",
            new
            {
                item.ToolCall.Name,
                displayName = item.Tool.UserFacingName,
                activity = item.Tool.ActivityDescription,
                renderHint = item.Tool.RenderHint,
                interruptBehavior = item.Tool.InterruptBehavior,
                render = toolUseRender
            }, step.Id, item.Invocation.Id), events, ct);

        var scheduled = await _toolExecutionScheduler.ExecuteAsync(
            new ToolExecutionRequest(CreateRunShell(state), item.Invocation,
                options.CommandTimeoutSeconds,
                options.MaxCommandOutputChars,
                AgentPermissionModes.Normalize(options.PermissionMode),
                item.ToolCall.ArgumentsJson,
                ExplicitUserApproval: explicitlyApproved,
                PolicyAuthorization: policyAuthorized), ct);

        try
        {
            item.Invocation.SafetyLevel = scheduled.Safety.Level;
            item.Invocation.SafetySummary = scheduled.Safety.Summary;
            item.Invocation.SafetyJson = SecretRedactor.RedactJson(scheduled.Safety.PreviewJson);
            await AppendLifecycleEventsAsync(state, options, item, step, scheduled, events, ct);
            if (scheduled.Result.OutcomeUncertain &&
                scheduled.Result.MayHaveCommitted &&
                !scheduled.Safety.IsReadOnly)
            {
                // The tool explicitly reported that its write may already have
                // crossed the side-effect boundary (timeout, transport loss,
                // remote disconnect, etc.). Treat this exactly like a result
                // persistence failure: fence the invocation and require an
                // explicit Resume acknowledgement. Never classify it as an
                // ordinary failed call that the recovery loop may replay.
                var uncertainty = scheduled.Result.Error ??
                    "The tool returned an uncertain outcome after a possible side effect.";
                await MarkInvocationUnknownOutcomeAsync(
                    state,
                    item,
                    step,
                    options,
                    events,
                    new InvalidOperationException(uncertainty),
                    ct,
                    $"{item.ToolCall.Name} may have committed a side effect: {uncertainty}");
                return (new AgentRunFrame(
                    step.StepNumber,
                    AgentRunFrameKinds.Paused,
                    events.ToArray()), events);
            }
            await CompleteInvocationWithResultAsync(state, item, step, scheduled.Result, options, events, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The tool body already returned, so replaying it after a result or
            // checkpoint persistence error could duplicate side effects. Mark
            // the exact invocation as unknown-outcome and pause for a human
            // observation instead of pretending it failed safely.
            await MarkInvocationUnknownOutcomeAsync(
                state, item, step, options, events, ex, CancellationToken.None);
        }
        return (new AgentRunFrame(step.StepNumber, AgentRunFrameKinds.ToolResult, events.ToArray()), events);
    }

    private async Task AppendLifecycleEventsAsync(
        AgentRunState state,
        AgentEngineOptions options,
        ToolBatchItem item,
        AgentStep step,
        ToolExecutionOutcome outcome,
        List<AgentEvent> events,
        CancellationToken ct)
    {
        foreach (var progress in outcome.ProgressEvents)
        {
            var isHookBlocked = string.Equals(progress.Phase, "hook_blocked", StringComparison.OrdinalIgnoreCase);
            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                new AgentRun { Id = state.RunId },
                isHookBlocked ? AgentEventTypes.ToolHookBlocked : AgentEventTypes.ToolProgress,
                progress.Message,
                new
                {
                    item.ToolCall.Name,
                    phase = progress.Phase,
                    progress.Percent,
                    progress.Message,
                    effectPlan = outcome.EffectPlan
                },
                step.Id,
                item.Invocation.Id,
                isHookBlocked ? AgentEventSeverities.Warning : AgentEventSeverities.Info), events, ct);
        }

        if (outcome.RollbackPlan != null)
        {
            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                new AgentRun { Id = state.RunId },
                AgentEventTypes.ToolRollbackPlan,
                $"Rollback plan ready for {item.ToolCall.Name}.",
                new
                {
                    item.ToolCall.Name,
                    rollbackPlan = outcome.RollbackPlan,
                    effectPlan = outcome.EffectPlan
                },
                step.Id,
                item.Invocation.Id,
                AgentEventSeverities.Info), events, ct);
        }
    }

    private async Task MarkInvocationUnknownOutcomeAsync(
        AgentRunState state,
        ToolBatchItem item,
        AgentStep step,
        AgentEngineOptions options,
        List<AgentEvent> events,
        Exception persistenceError,
        CancellationToken ct,
        string? uncertaintySummary = null)
    {
        var detail = SecretRedactor.RedactText(TrimForContext(persistenceError.Message, 320));
        item.Invocation.Status = ToolInvocationStatuses.UnknownOutcome;
        item.Invocation.CompletedAt = null;
        step.Status = AgentStepStatuses.Failed;
        step.Summary = "Tool outcome requires user confirmation.";
        step.CompletedAt = DateTime.UtcNow;
        state.Status = AgentRunStatuses.Paused;
        state.PendingToolInvocationId = null;
        state.UnknownOutcomeInvocationId = item.Invocation.Id;
        state.UnknownOutcomeSummary = string.IsNullOrWhiteSpace(uncertaintySummary)
            ? $"{item.ToolCall.Name} may have completed, but its result could not be durably recorded: {detail}"
            : SecretRedactor.RedactText(TrimForContext(uncertaintySummary, 500));
        state.ErrorMessage = state.UnknownOutcomeSummary;
        state.ConsecutiveToolFailures = Math.Max(1, state.ConsecutiveToolFailures);
        state.LastFailedInvocationSignature = ComputeInvocationSignature(item.ToolCall);
        state.LastFailedToolName = AgentToolNames.Normalize(item.ToolCall.Name);
        state.LastFailureSummary = state.UnknownOutcomeSummary;
        state.RecoveryDirectiveIssuedForFailureSignature = null;
        state.RecoveryDirectivePending = true;

        var replayFenceResult = new AgentToolResult(
            false,
            string.Empty,
            $"Unknown outcome: {state.UnknownOutcomeSummary} Do not replay this invocation automatically.",
            OutcomeUncertain: true,
            MayHaveCommitted: true);
        item.Invocation.ResultJson = replayFenceResult.ToJson();
        step.OutputJson = item.Invocation.ResultJson;

        if (!state.Messages.Any(message =>
                string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(message.ToolCallId, item.Invocation.ProviderCallId, StringComparison.Ordinal)))
        {
            state.Messages.Add(new MessagePayload(
                "tool",
                replayFenceResult.ToJson(),
                item.Invocation.ProviderCallId));
        }

        // Persist the DB marker before the checkpoint. If the checkpoint store
        // itself is the component that failed, an older checkpoint may still
        // point at this invocation; the pre-loop status check will see
        // unknown_outcome and refuse to execute it.
        try
        {
            var trackedRun = await _db.Set<AgentRun>()
                .FirstOrDefaultAsync(run => run.Id == state.RunId, ct);
            if (trackedRun != null)
                SyncRunState(trackedRun, state);
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // There is no safer automatic action after the side effect boundary.
        }

        try
        {
            await SaveCheckpointAsync(state, ct);
        }
        catch
        {
            // The invocation row remains the replay fence when checkpointing is unavailable.
        }

        try
        {
            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                new AgentRun { Id = state.RunId },
                AgentEventTypes.RunPaused,
                "Tool execution has an unknown outcome; user verification is required before retrying.",
                new
                {
                    item.ToolCall.Name,
                    invocationId = item.Invocation.Id,
                    error = detail,
                    resumable = true,
                    requiresUserConfirmation = true
                },
                step.Id,
                item.Invocation.Id,
                AgentEventSeverities.Error), events, ct);
        }
        catch
        {
            // Preserve the paused in-memory state even if event persistence is unavailable.
        }
    }

    private async Task<string> PauseForUnknownInvocationOutcomeAsync(
        AgentRun run,
        AgentRunState state,
        ToolInvocation invocation,
        AgentEngineOptions options,
        List<AgentEvent> events,
        CancellationToken ct)
    {
        invocation.Status = ToolInvocationStatuses.UnknownOutcome;
        state.Status = AgentRunStatuses.Paused;
        state.PendingToolInvocationId = null;
        state.UnknownOutcomeInvocationId = invocation.Id;
        state.UnknownOutcomeSummary ??=
            $"{invocation.ToolName} started before the previous interruption, so its side effects may already exist.";
        state.ErrorMessage = state.UnknownOutcomeSummary;
        state.ConsecutiveToolFailures = Math.Max(1, state.ConsecutiveToolFailures);
        state.LastFailedToolName ??= AgentToolNames.Normalize(invocation.ToolName);
        state.LastFailureSummary = state.UnknownOutcomeSummary;
        state.RecoveryDirectivePending = true;
        SyncRunState(run, state);
        await _db.SaveChangesAsync(ct);
        await SaveCheckpointAsync(state, ct);
        await AppendEventAsync(state, options, new AgentEventAppendRequest(
            new AgentRun { Id = state.RunId },
            AgentEventTypes.RunPaused,
            "A previously started tool has an unknown outcome; it was not replayed.",
            new
            {
                invocation.ToolName,
                invocationId = invocation.Id,
                requiresUserConfirmation = true,
                resumable = true
            },
            invocation.AgentStepId,
            invocation.Id,
            AgentEventSeverities.Warning), events, ct);
        return $"{state.UnknownOutcomeSummary} Verify the resulting files or external state, then tell the agent whether to continue; the operation was not replayed.";
    }

    private async Task CompleteInvocationWithResultAsync(AgentRunState state, ToolBatchItem item, AgentStep step,
        AgentToolResult result, AgentEngineOptions options, List<AgentEvent> events, CancellationToken ct)
    {
        item.Invocation.Status = result.Success ? ToolInvocationStatuses.Completed : ToolInvocationStatuses.Failed;
        if (step.Status != AgentStepStatuses.Denied)
            step.Status = result.Success ? AgentStepStatuses.Completed : AgentStepStatuses.Failed;

        if (result.Success)
        {
            state.SuccessfulToolCalls++;
            state.LastSuccessfulStep = step.StepNumber;
            var syntheticResolution = ApplySyntheticQuestionResolution(state, item.ToolCall);
            if (SuccessfulInvocationResolvesFailure(state, item, syntheticResolution))
            {
                if (state.ConsecutiveToolFailures > 0)
                {
                    state.LastRecoveryCandidateInvocationSignature =
                        ComputeInvocationSignature(item.ToolCall);
                }
                state.ConsecutiveToolFailures = 0;
                state.RepeatedFailureCount = 0;
                state.CompletionRecoveryAttempts = 0;
                state.LastFailedInvocationSignature = null;
                state.LastFailedToolName = null;
                state.LastFailureSummary = null;
                state.RecoveryDirectivePending = false;
                state.RecoveryDirectiveIssuedForFailureSignature = null;
            }
            else
            {
                // Housekeeping progress does not prove the failed operation was
                // recovered, so the completion gate remains active.
                state.RecoveryDirectivePending = true;
            }
        }
        else
        {
            var signature = ComputeInvocationSignature(item.ToolCall);
            var repeated = string.Equals(
                signature,
                state.LastFailedInvocationSignature,
                StringComparison.Ordinal);
            state.ConsecutiveToolFailures++;
            state.RepeatedFailureCount = repeated
                ? state.RepeatedFailureCount + 1
                : 1;
            if (!repeated)
                state.CompletionRecoveryAttempts = 0;
            state.LastFailedInvocationSignature = signature;
            state.LastFailedToolName = AgentToolNames.Normalize(item.ToolCall.Name);
            state.LastFailureSummary = SecretRedactor.RedactText(TrimForContext(
                result.Error ?? "Tool invocation failed without an error message.",
                500));
            state.RecoveryDirectiveIssuedForFailureSignature = null;
            state.LastRecoveryCandidateInvocationSignature = null;
            state.RecoveryDirectivePending = true;
        }

        var contextResult = result;
        var persistence = await _toolResultPersistence.PersistForContextAsync(
            _sandboxCommandService, state.ChatId, item.Invocation, result,
            Math.Max(512, options.MaxToolResultCharsInContext), ct);
        if (persistence.Persisted)
        {
            contextResult = persistence.ContextResult;
            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                new AgentRun { Id = state.RunId }, AgentEventTypes.ToolResultPersisted,
                $"Large output persisted: {item.ToolCall.Name}.",
                new
                {
                    item.ToolCall.Name,
                    persistence.PersistedPath,
                    artifact = persistence.PersistedArtifact
                }, step.Id, item.Invocation.Id), events, ct);
        }

        item.Invocation.ResultJson = contextResult.ToJson();
        item.Invocation.CompletedAt = DateTime.UtcNow;
        step.OutputJson = item.Invocation.ResultJson;
        step.CompletedAt = DateTime.UtcNow;

        var resultRender = item.Tool.RenderToolResult(contextResult);
        var toolContent = FormatToolResultMessage(
            step.StepNumber,
            item.ToolCall.Name,
            contextResult,
            resultRender);

        // Persist tool result message to DB (compat with old loop behavior)
        _db.Set<Models.Message>().Add(new Models.Message
        {
            ChatId = state.ChatId, Role = "tool",
            Content = toolContent,
            TurnId = state.TurnId, SequenceNum = state.SequenceNum++
        });

        await UpsertArtifactsAsync(state.RunId, contextResult.Artifacts, ct);

        state.Messages.Add(new MessagePayload("tool", contextResult.ToJson(),
            item.Invocation.ProviderCallId));
        if (state.PendingToolInvocationId == item.Invocation.Id)
            state.PendingToolInvocationId = null;
        ApplyPlanModeTransition(state, item.ToolCall.Name, result.Success, options);
        await SaveCheckpointAsync(state, ct);
        await _db.SaveChangesAsync(ct);

        await AppendEventAsync(state, options, new AgentEventAppendRequest(
            new AgentRun { Id = state.RunId }, AgentEventTypes.ToolResult,
            result.Success ? $"{item.ToolCall.Name} completed." : $"{item.ToolCall.Name} failed.",
            new
            {
                item.ToolCall.Name,
                displayName = item.Tool.UserFacingName,
                activity = item.Tool.ActivityDescription,
                renderHint = item.Tool.RenderHint,
                contextResult.Success,
                isTruncated = item.Tool.IsResultTruncated(contextResult),
                error = contextResult.Error,
                warning = contextResult.Warning,
                artifactCount = contextResult.Artifacts?.Count ?? 0,
                render = resultRender
            },
            step.Id, item.Invocation.Id,
            Severity: result.Success ? AgentEventSeverities.Info : AgentEventSeverities.Error), events, ct);

        if (result.Success && IsTaskTool(item.ToolCall.Name))
        {
            var tasks = await _agentTasks.ListAsync(state.ChatId, includeCompleted: true, limit: 40, ct);
            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                new AgentRun { Id = state.RunId },
                IsBackgroundTaskTool(item.ToolCall.Name)
                    ? AgentEventTypes.BackgroundTaskUpdated
                    : AgentEventTypes.TaskUpdated,
                IsBackgroundTaskTool(item.ToolCall.Name)
                    ? "Background task state changed."
                    : "Task list changed.",
                new
                {
                    tool = item.ToolCall.Name,
                    taskCount = tasks.Count,
                    tasks
                },
                step.Id,
                item.Invocation.Id), events, ct);
        }
    }

    private static bool SuccessfulInvocationResolvesFailure(
        AgentRunState state,
        ToolBatchItem item,
        bool? syntheticResolution = null)
    {
        if (state.ConsecutiveToolFailures == 0)
            return true;

        var current = AgentToolNames.Normalize(item.ToolCall.Name);
        // A synthetic failure-recovery question resolves the failure only when
        // the user explicitly chooses to stop and summarize. "Try another way"
        // keeps the recovery gate active so the model must actually attempt a
        // materially different action.
        if (string.Equals(current, AgentToolNames.AskUserQuestion, StringComparison.OrdinalIgnoreCase))
        {
            // Ordinary questions, task/deferred prompts, and recovery choices
            // such as "Try another way" do not prove a failed operation was
            // repaired. Only the synthetic failure question's explicit
            // "Stop and summarize" choice returns true.
            return syntheticResolution == true;
        }

        if (string.IsNullOrWhiteSpace(state.LastFailedInvocationSignature) ||
            !string.Equals(
                state.RecoveryDirectiveIssuedForFailureSignature,
                state.LastFailedInvocationSignature,
                StringComparison.Ordinal))
            return false;

        var candidateSignature = ComputeInvocationSignature(item.ToolCall);
        if (string.Equals(
                candidateSignature,
                state.LastFailedInvocationSignature,
                StringComparison.Ordinal))
            return false;

        // Terminal/sandbox tools are conservatively registered as write-capable,
        // even when this exact invocation is only inspecting state. Use the
        // invocation-level safety preview as well so a successful diagnostic
        // command cannot erase an unrelated unresolved failure.
        if (item.Tool.Metadata.IsReadOnly || item.Safety.IsReadOnly ||
            IsObviousHousekeepingInvocation(item.ToolCall))
            return false;

        return current is not (
            AgentToolNames.TodoWrite or
            AgentToolNames.TaskCreate or
            AgentToolNames.TaskUpdate or
            AgentToolNames.TaskList or
            AgentToolNames.TaskOutput or
            AgentToolNames.TaskStop or
            AgentToolNames.TaskSendMessage or
            AgentToolNames.MemoryWrite or
            AgentToolNames.EnterPlanMode or
            AgentToolNames.ExitPlanMode);
    }

    private static bool IsObviousHousekeepingInvocation(LlmToolCall call)
    {
        var toolName = AgentToolNames.Normalize(call.Name);
        if (toolName is not (AgentToolNames.SandboxExec or AgentToolNames.TerminalExec))
            return false;

        var command = ReadJsonString(call.ArgumentsJson, "command")?.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return true;
        if (command.IndexOfAny([';', '|', '\r', '\n']) >= 0 ||
            command.Contains("&&", StringComparison.Ordinal) ||
            command.Contains("||", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = command.Trim().Trim('(', ')').Trim();
        return normalized.Equals("pwd", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Get-Location", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Get-Date", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Get-Date ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("whoami", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("hostname", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("git status", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("git diff", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("git diff --stat", StringComparison.OrdinalIgnoreCase);
    }

    private static string ApprovalRejectionMessage(ToolInvocation invocation)
    {
        if (!string.Equals(invocation.ToolName, AgentToolNames.ExitPlanMode, StringComparison.OrdinalIgnoreCase))
            return "Not approved.";

        try
        {
            using var document = JsonDocument.Parse(invocation.ArgumentsJson);
            if (document.RootElement.TryGetProperty("feedback", out var feedback) &&
                feedback.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(feedback.GetString()))
            {
                return $"Plan needs revision before approval. User feedback: {feedback.GetString()!.Trim()}";
            }
        }
        catch (JsonException)
        {
            // A malformed optional feedback payload should not change denial semantics.
        }

        return "Plan was not approved. Continue planning or ask the user what to change.";
    }

    private static AgentEngineOptions ApplyPersistedPermissionOptions(
        AgentRunState state,
        AgentEngineOptions options)
    {
        var permissionMode = string.IsNullOrWhiteSpace(state.EffectivePermissionMode)
            ? AgentPermissionModes.Normalize(options.PermissionMode)
            : AgentPermissionModes.Normalize(state.EffectivePermissionMode);
        var autoApproveTools = state.EffectiveAutoApproveTools ?? options.AutoApproveTools;

        if (state.IsPlanMode)
        {
            permissionMode = AgentPermissionModes.Plan;
            autoApproveTools = false;
        }

        state.EffectivePermissionMode = permissionMode;
        state.EffectiveAutoApproveTools = autoApproveTools;
        return options with
        {
            PermissionMode = permissionMode,
            AutoApproveTools = autoApproveTools
        };
    }

    private static void ApplyPlanModeTransition(
        AgentRunState state,
        string toolName,
        bool succeeded,
        AgentEngineOptions options)
    {
        if (!succeeded)
            return;

        var normalizedToolName = AgentToolNames.Normalize(toolName);
        if (string.Equals(normalizedToolName, AgentToolNames.EnterPlanMode, StringComparison.OrdinalIgnoreCase))
        {
            if (state.IsPlanMode)
                return;

            var previousMode = AgentPermissionModes.Normalize(
                state.EffectivePermissionMode ?? options.PermissionMode);
            state.PrePlanMode = previousMode == AgentPermissionModes.Plan
                ? AgentPermissionModes.RequestApproval
                : previousMode;
            state.PrePlanAutoApproveTools = state.EffectiveAutoApproveTools ?? options.AutoApproveTools;
            state.IsPlanMode = true;
            state.EffectivePermissionMode = AgentPermissionModes.Plan;
            state.EffectiveAutoApproveTools = false;
        }
        else if (string.Equals(normalizedToolName, AgentToolNames.ExitPlanMode, StringComparison.OrdinalIgnoreCase))
        {
            var restoredMode = AgentPermissionModes.Normalize(
                state.PrePlanMode ?? AgentPermissionModes.RequestApproval);
            if (restoredMode == AgentPermissionModes.Plan)
                restoredMode = AgentPermissionModes.RequestApproval;

            state.IsPlanMode = false;
            state.PrePlanMode = null;
            state.EffectivePermissionMode = restoredMode;
            state.EffectiveAutoApproveTools = state.PrePlanAutoApproveTools ??
                AgentPermissionModes.IsAutoApprove(restoredMode);
            state.PrePlanAutoApproveTools = null;
        }
    }

    private static bool IsTaskTool(string name) =>
        name is AgentToolNames.TodoWrite or AgentToolNames.TaskCreate or AgentToolNames.TaskUpdate or
            AgentToolNames.TaskList or AgentToolNames.TaskOutput or AgentToolNames.TaskStop or
            AgentToolNames.TaskSendMessage;

    private static bool IsBackgroundTaskTool(string name) =>
        name is AgentToolNames.TaskOutput or AgentToolNames.TaskStop or AgentToolNames.TaskSendMessage;

    private async Task<string?> TryFinalizeAtStepBudgetAsync(AgentRunState state, string systemPrompt,
        EffectiveSettings effective, AgentEngineOptions options, List<AgentEvent> events, CancellationToken ct)
    {
        // A summary is not a valid success transition while an operation is
        // still unresolved. The caller will checkpoint and pause instead.
        if (state.ConsecutiveToolFailures > 0 ||
            state.DeferredToolCalls.Count > 0 ||
            state.UnknownOutcomeInvocationId != null ||
            (await GetOpenCompletionTasksAsync(state.ChatId, ct)).Count > 0)
            return null;

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
        summaryMessages = await BuildModelMessagesWithRuntimeContextAsync(state.ChatId, state.RunId, summaryMessages, ct);

        try
        {
            var provider = LlmProviderFactory.Create(_httpClientFactory.CreateClient("LLM"),
                effective.Provider, effective.ApiKey, effective.BaseUrl, effective.Model);
            var response = await ChatWithTransientRetryAsync(new ProviderStreamRequest(
                provider, summaryMessages, systemPrompt,
                Math.Min(effective.Temperature, 0.4), Math.Max(512, Math.Min(effective.MaxTokens, 2048)),
                Tools: null, Reasoning: BuildReasoningOptions(effective)), state, ct);

            if (response.HttpStatus is < 200 or >= 300 ||
                !string.IsNullOrWhiteSpace(response.Error) ||
                string.IsNullOrWhiteSpace(response.AssistantText))
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
            state.MaxSteps = Math.Max(state.MaxSteps, stepNumber);
            state.Status = AgentRunStatuses.Completed;
            state.ErrorMessage = null;
            state.Messages.Add(new MessagePayload("assistant", response.AssistantText));
            await SaveCheckpointAsync(state, ct);
            await AppendEventAsync(state, options, new AgentEventAppendRequest(
                new AgentRun { Id = state.RunId }, AgentEventTypes.RunCompleted,
                "Run completed after step-budget finalization.",
                new { state.CurrentStep, state.MaxSteps }, step.Id), events, ct);
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

    private async Task<LlmResponse> ChatWithTransientRetryAsync(
        ProviderStreamRequest request,
        AgentRunState state,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        LlmResponse? lastResponse = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                lastResponse = await _providerStreamAdapter.ChatAsync(request, ct);
                if (!IsTransientProviderResponse(lastResponse))
                {
                    state.ConsecutiveProviderFailures =
                        lastResponse.HttpStatus is >= 200 and < 300 &&
                        string.IsNullOrWhiteSpace(lastResponse.Error)
                            ? 0
                            : state.ConsecutiveProviderFailures + 1;
                    return lastResponse;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastResponse = CreateProviderFailureResponse(
                    request,
                    408,
                    "The model provider request timed out.");
            }
            catch (HttpRequestException ex)
            {
                lastResponse = CreateProviderFailureResponse(
                    request,
                    503,
                    $"The model provider could not be reached: {SecretRedactor.RedactText(ex.Message)}");
            }
            catch (IOException ex)
            {
                lastResponse = CreateProviderFailureResponse(
                    request,
                    503,
                    $"The model provider connection was interrupted: {SecretRedactor.RedactText(ex.Message)}");
            }

            state.ConsecutiveProviderFailures++;
            if (attempt == maxAttempts)
                break;

            state.RecoveryAttempts++;
            ReportSafely(request.OutputStream, new LlmStreamUpdate(
                string.Empty,
                string.Empty,
                LlmStreamEventTypes.RetryReset));
            var delay = TimeSpan.FromMilliseconds(150 * (1 << (attempt - 1)));
            await Task.Delay(delay, ct);
        }

        if (lastResponse == null)
        {
            return CreateProviderFailureResponse(
                request,
                503,
                "The model provider failed without returning a response.");
        }

        if (lastResponse.HttpStatus is >= 200 and < 300 &&
            string.IsNullOrWhiteSpace(lastResponse.Error))
        {
            lastResponse = lastResponse with
            {
                Error = "The model provider returned an empty or incomplete response after three attempts."
            };
        }
        return lastResponse;
    }

    private static bool IsTransientProviderResponse(LlmResponse response)
    {
        if (response.HttpStatus is 408 or 429 || response.HttpStatus is >= 500 and <= 599)
            return true;

        if (response.HttpStatus is < 200 or >= 300)
            return false;

        if (!string.IsNullOrWhiteSpace(response.Error))
            return true;

        return string.IsNullOrWhiteSpace(response.AssistantText) &&
               (response.ToolCalls == null || response.ToolCalls.Count == 0);
    }

    private static LlmResponse CreateProviderFailureResponse(
        ProviderStreamRequest request,
        int status,
        string error) =>
        new(
            new Dictionary<string, object>
            {
                ["provider"] = request.Provider.ProviderName,
                ["messageCount"] = request.Messages.Count
            },
            new Dictionary<string, object> { ["error"] = error },
            status,
            0,
            string.Empty,
            Error: error);

    private static AgentContextOptions BuildContextOptions(AgentEngineOptions options) => new(
        options.ContextBudgetTokens, options.AutoCompactTriggerTokens, MaxToolResultCharsInContext: options.MaxToolResultCharsInContext);

    private static LlmReasoningOptions? BuildReasoningOptions(EffectiveSettings settings) =>
        string.Equals(settings.ThinkingDepth, "disabled", StringComparison.OrdinalIgnoreCase) ? null :
        new LlmReasoningOptions(settings.ThinkingDepth ?? "auto");

    private static AgentRun CreateRunShell(AgentRunState state) => new()
    {
        Id = state.RunId,
        ChatId = state.ChatId,
        TurnId = state.TurnId,
        Status = state.Status,
        UserRequest = state.UserRequest,
        CurrentStep = state.CurrentStep,
        MaxSteps = state.MaxSteps
    };

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
        // M4.4.5: Direct IProgress<T> — no SynchronizationContext capture.
        // Progress<T> captures SynchronizationContext.Current at construction
        // time and marshals every callback to the UI thread via Post(). In agent
        // mode this flooded the UI thread with ~60 callbacks/sec, causing
        // scroll lag and unresponsive thinking-expand clicks. The callback
        // only does thread-safe data operations (char count, forwarding) so
        // SynchronizationContext marshaling was unnecessary overhead.
        return new DirectProgress<LlmStreamUpdate>(update =>
        {
            tracker.Chars += update.Delta?.Length ?? 0;
            ReportSafely(output, update);
        });
    }

    private static void ReportSafely<T>(IProgress<T>? progress, T value)
    {
        if (progress == null)
            return;
        try
        {
            progress.Report(value);
        }
        catch
        {
            // Progress is a best-effort observer and cannot drive run state.
        }
    }

    /// <summary>
    /// M4.4.5: A minimal IProgress&lt;T&gt; that invokes the handler synchronously
    /// on the caller's thread. Unlike System.Progress&lt;T&gt;, this does NOT capture
    /// or use SynchronizationContext — it is a pure pass-through.
    /// </summary>
    private sealed class DirectProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public DirectProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
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
