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

                frameProgress?.Report(new AgentRunFrame(stepNumber, AgentRunFrameKinds.ModelRequest, events.ToArray()));

                var streamMetrics = CreateStreamMetrics();
                var outputStream = CreateTrackedStream(options.OutputStream, streamMetrics);
                lastResponse = await _providerStreamAdapter.ChatAsync(
                    new ProviderStreamRequest(provider, modelMessages, systemPrompt,
                        effective.Temperature, effective.MaxTokens, guard.Tools, outputStream,
                        Reasoning: BuildReasoningOptions(effective)), ct);

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
                                lastResponse = await _providerStreamAdapter.ChatAsync(
                                    new ProviderStreamRequest(provider, retryMessages, systemPrompt,
                                        effective.Temperature, effective.MaxTokens, retryGuard.Tools, outputStream,
                                        Reasoning: BuildReasoningOptions(effective)), ct);
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
                            lastResponse = await _providerStreamAdapter.ChatAsync(
                                new ProviderStreamRequest(provider, truncMessages, systemPrompt,
                                    effective.Temperature, effective.MaxTokens, truncGuard.Tools, outputStream,
                                    Reasoning: BuildReasoningOptions(effective)), ct);
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
                    await FinalizeStepFailed(run, step, state, options,
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
                    SyncRunState(run, state, terminal: true);
                    state.Messages.Add(new MessagePayload("assistant", lastResponse.AssistantText, ReasoningContent: lastResponse.ReasoningText));
                    await AppendEventAsync(state, options, new AgentEventAppendRequest(
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
                    await FinalizeStepFailed(run, step, state, options, "All tool calls were invalid.", events, ct);
                    assistantContent = "Agent stopped: invalid tool requests.";
                    break;
                }

                // Plan batches for multi-tool execution
                var planItems = validToolCalls.Select(tc =>
                    new ToolExecutionPlanItem(tc.Name, tc.ArgumentsJson, tc.Id)).ToList();
                var batches = _toolExecutionScheduler.PlanBatches(planItems);

                frameProgress?.Report(new AgentRunFrame(stepNumber, AgentRunFrameKinds.ToolBatchPlanned, events.ToArray(),
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

                        var bypassPermissions = AgentPermissionModes.IsBypass(options.PermissionMode);
                        if (!bypassPermissions && (policy.IsDenied || item.Safety.IsBlocked))
                        {
                            await HandleDeniedInvocationAsync(state, item, step, policy.IsDenied ? "denied_by_policy" : "blocked_by_safety", options, events, ct);
                            continue;
                        }

                        // M4.6.0: Bypass-immune safety checks survive even AutoApproveTools
                        // mode. Operations on .git/, .env, shell configs always need approval.
                        var safetyRequiresApproval = item.Safety.RequiresExplicitApproval ||
                                                     (item.Safety.BypassImmune && options.AutoApproveTools);

                        // M4.9.0: In Plan mode, all write/destructive operations require
                        // explicit user approval — even if the mode is BypassPermissions.
                        if (state.IsPlanMode &&
                            (item.Safety.IsWriteOperation || item.Tool.Metadata.IsDestructive ||
                             !item.Safety.IsReadOnly))
                        {
                            safetyRequiresApproval = true;
                        }

                        var needsApproval = !options.AutoApproveTools
                            ? ((item.Tool.Metadata.RequiresApproval && !policy.IsAllowed) ||
                               (safetyRequiresApproval && !policy.IsAllowed))
                            : (item.Safety.BypassImmune && !policy.IsAllowed);

                        if (needsApproval)
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

                        // M4.9.0: Plan mode state transitions.
                        if (string.Equals(item.ToolCall.Name, AgentToolNames.EnterPlanMode, StringComparison.OrdinalIgnoreCase))
                        {
                            state.IsPlanMode = true;
                            state.PrePlanMode = options.PermissionMode;
                        }
                        else if (string.Equals(item.ToolCall.Name, AgentToolNames.ExitPlanMode, StringComparison.OrdinalIgnoreCase))
                        {
                            state.IsPlanMode = false;
                            // Restore pre-plan mode, with circuit breaker: never
                            // restore to Plan (shouldn't happen, but guard).
                            var restored = state.PrePlanMode ?? AgentPermissionModes.RequestApproval;
                            if (restored == AgentPermissionModes.Plan)
                                restored = AgentPermissionModes.RequestApproval;
                            options = options with { PermissionMode = restored };
                        }
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
                    _ = Task.Run(() =>
                        _sessionMemory.ExtractAsync(state.ChatId, state.RunId, state.Messages,
                            stepSandbox, stepMeta.FilesChanged, stepMeta.CommandsRun,
                            stepMeta.RecentFailures, stepMeta.OpenQuestions, stepMeta.NextActions,
                            CancellationToken.None), CancellationToken.None);
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
                    state.Status = AgentRunStatuses.Paused;
                    SyncRunState(run, state);
                    await AppendEventAsync(state, options, new AgentEventAppendRequest(
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
        return $"""

        TLAH Agent Mode is enabled.
        Registered tools: {tools}
        Workspace root: {sandboxRoot}
        {hostAccessLine}
        Use the workspace root as the default working directory. In sandboxed modes, work only inside that root. Do not read unrelated host user files or run destructive, privileged, registry, service, shutdown, or system-configuration operations.
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
            var snapshot = await BuildSnapshotAsync(state, ct);
            options.Progress.Report(new AgentProgressUpdate(
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

        return evt;
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

        var preview = await _toolLifecycleRunner.PreviewAsync(
            state.ChatId,
            invocation.ToolName,
            invocation.ArgumentsJson,
            ct);
        var item = new ToolBatchItem(
            new LlmToolCall(invocation.ProviderCallId, invocation.ToolName, invocation.ArgumentsJson),
            tool,
            invocation,
            preview.Safety,
            preview.EffectPlan);

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
                AgentPermissionModes.Normalize(options.PermissionMode)), ct);

        item.Invocation.SafetyLevel = scheduled.Safety.Level;
        item.Invocation.SafetySummary = scheduled.Safety.Summary;
        item.Invocation.SafetyJson = SecretRedactor.RedactJson(scheduled.Safety.PreviewJson);
        await AppendLifecycleEventsAsync(state, options, item, step, scheduled, events, ct);
        await CompleteInvocationWithResultAsync(state, item, step, scheduled.Result, options, events, ct);
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

    private static bool IsTaskTool(string name) =>
        name is AgentToolNames.TodoWrite or AgentToolNames.TaskCreate or AgentToolNames.TaskUpdate or
            AgentToolNames.TaskList or AgentToolNames.TaskOutput or AgentToolNames.TaskStop or
            AgentToolNames.TaskSendMessage;

    private static bool IsBackgroundTaskTool(string name) =>
        name is AgentToolNames.TaskOutput or AgentToolNames.TaskStop or AgentToolNames.TaskSendMessage;

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
        summaryMessages = await BuildModelMessagesWithRuntimeContextAsync(state.ChatId, state.RunId, summaryMessages, ct);

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
            state.MaxSteps = Math.Max(state.MaxSteps, stepNumber);
            state.Status = AgentRunStatuses.Completed;
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
            output.Report(update);
        });
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
