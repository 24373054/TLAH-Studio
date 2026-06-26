using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;

#pragma warning disable CA1416 // TLAH Studio is a Windows desktop client; DPAPI is intentionally Windows-only.

namespace TLAHStudio.Core.Services;

/// <summary>
/// THE CORE ORCHESTRATION SERVICE.
/// Executes the complete send-message flow — from loading chat history,
/// through calling the LLM provider, to storing the debug artifacts.
/// Maps 1:1 from services/llm_service.py.
/// </summary>
public class LlmService : ILlmService
{
    private readonly DbContext _db;
    private readonly IChatService _chatService;
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISandboxCommandService _sandboxCommandService;
    private readonly IAgentToolRegistry _agentTools;
    private readonly IToolPlatformService _toolPlatform;

    public LlmService(
        DbContext db,
        IChatService chatService,
        ISettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ISandboxCommandService? sandboxCommandService = null,
        IAgentToolRegistry? agentTools = null,
        IToolPlatformService? toolPlatform = null)
    {
        _db = db;
        _chatService = chatService;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _sandboxCommandService = sandboxCommandService ?? new SandboxCommandService();
        _toolPlatform = toolPlatform ?? new ToolPlatformService(db);
        if (agentTools != null)
        {
            _agentTools = agentTools;
        }
        else
        {
            var network = new NetworkSecurityService();
            var router = new ExecutionBackendRouter(
                _sandboxCommandService, _toolPlatform, network, httpClientFactory);
            var mcp = new McpClientService(db, _toolPlatform, network, httpClientFactory);
            _agentTools = new AgentToolRegistry(
            [
                new SandboxExecAgentTool(_sandboxCommandService),
                new TerminalExecAgentTool(router),
                new FileListAgentTool(_sandboxCommandService),
                new FileReadAgentTool(_sandboxCommandService, _toolPlatform),
                new FileWriteAgentTool(_sandboxCommandService, _toolPlatform),
                new FileSearchAgentTool(_sandboxCommandService, _toolPlatform),
                new GitAgentTool(_sandboxCommandService),
                new HttpRequestAgentTool(_toolPlatform, network, httpClientFactory),
                new WebSearchAgentTool(_toolPlatform, network, httpClientFactory),
                new BrowserReadAgentTool(_toolPlatform, network, httpClientFactory),
                new McpListToolsAgentTool(mcp),
                new McpCallAgentTool(mcp)
            ]);
        }
    }

    /// <summary>
    /// THE CORE ORCHESTRATION FUNCTION.
    /// Maps 1:1 from send_message() in services/llm_service.py.
    /// </summary>
    public async Task<SendMessageResult> SendMessageAsync(
        Guid chatId,
        string userContent,
        string? role = null,
        CancellationToken ct = default)
    {
        // 1. Load chat
        var chat = await _db.Set<Chat>()
            .FirstOrDefaultAsync(c => c.Id == chatId, ct)
            ?? throw new InvalidOperationException($"Chat not found: {chatId}");

        // 2. Build effective settings
        var effective = await _settingsService.GetEffectiveSettingsAsync(chatId, ct);
        EnsureProviderReady(effective.ApiKey, effective.BaseUrl, effective.Model);

        // 3. Build message history (before adding the new message)
        var priorMessages = await _db.Set<Message>()
            .Where(m => m.ChatId == chatId)
            .OrderBy(m => m.SequenceNum)
            .ToListAsync(ct);

        // 4. Build system prompt (chat override > global, + agent file)
        var systemPrompt = await SystemPromptBuilder.BuildAsync(
            _db.Set<Chat>(),
            _db.Set<GlobalSettings>(),
            _db.Set<AgentFile>(),
            _db.Set<ProjectSpace>(),
            _db.Set<ConfigProfile>(),
            chatId,
            ct);

        // 5. Determine the role to use
        var msgRole = role ?? effective.UserRole;

        // 6. Determine turn number
        var turnCount = await _db.Set<Turn>()
            .CountAsync(t => t.ChatId == chatId, ct);
        var turnNumber = turnCount + 1;

        // 7. Create Turn
        var turn = new Turn
        {
            ChatId = chatId,
            TurnNumber = turnNumber
        };
        _db.Set<Turn>().Add(turn);
        await _db.SaveChangesAsync(ct);

        // 8. Save the outgoing message
        var seq = await _chatService.GetNextSequenceAsync(chatId, ct);
        var sentMessage = new Message
        {
            ChatId = chatId,
            Role = msgRole,
            Content = userContent,
            TurnId = turn.Id,
            SequenceNum = seq
        };
        _db.Set<Message>().Add(sentMessage);
        await _db.SaveChangesAsync(ct);

        // 9. Create LLM provider using raw HttpClient (NOT any SDK)
        var httpClient = _httpClientFactory.CreateClient("LLM");
        var provider = LlmProviderFactory.Create(
            httpClient,
            effective.Provider,
            effective.ApiKey,
            effective.BaseUrl,
            effective.Model);

        // 10. Build the full messages list for the LLM call
        // (prior messages + the new message)
        var messagesForLlm = priorMessages
            .Select(ToProviderMessage)
            .ToList();
        messagesForLlm.Add(new MessagePayload(NormalizeProviderRole(msgRole), userContent));

        // 11. Call the LLM (async, no event loop hacks needed in C#)
        var llmResult = await provider.ChatAsync(
            messages: messagesForLlm,
            systemPrompt: systemPrompt,
            temperature: effective.Temperature,
            maxTokens: effective.MaxTokens,
            ct: ct);

        // 12. Store raw request
        var rawRequest = new RawRequest
        {
            TurnId = turn.Id,
            Provider = provider.ProviderName,
            EndpointUrl = provider.EndpointUrl,
            RequestJson = SafeJson(llmResult.RawRequest, effective.ApiKey)
        };
        _db.Set<RawRequest>().Add(rawRequest);

        // 13. Store raw response
        var rawResponse = new RawResponse
        {
            TurnId = turn.Id,
            Provider = provider.ProviderName,
            ResponseJson = SafeJson(llmResult.RawResponse, effective.ApiKey),
            HttpStatusCode = llmResult.HttpStatus,
            LatencyMs = llmResult.LatencyMs,
            TokenUsageJson = llmResult.TokenUsage != null
                ? JsonSerializer.Serialize(llmResult.TokenUsage)
                : null
        };
        _db.Set<RawResponse>().Add(rawResponse);

        // 14. Save assistant message
        var assistantContent = llmResult.AssistantText;
        var assistantMessage = new Message
        {
            ChatId = chatId,
            Role = "assistant",
            Content = assistantContent,
            TurnId = turn.Id,
            SequenceNum = seq + 1
        };
        _db.Set<Message>().Add(assistantMessage);

        // 15. Update chat timestamp
        chat.UpdatedAt = DateTime.UtcNow;

        // 16. Commit everything
        await _db.SaveChangesAsync(ct);
        await LogAuditAsync("send", "turn", turn.Id.ToString("D"), $"Sent message in \"{chat.Title}\".", chat.ProjectSpaceId, chat.Id, new
        {
            turn.TurnNumber,
            provider = provider.ProviderName,
            provider.EndpointUrl,
            llmResult.HttpStatus,
            llmResult.LatencyMs
        }, ct);

        return new SendMessageResult(turn, sentMessage, assistantMessage, rawRequest, rawResponse);
    }

    public async Task<SendMessageResult> RunAgentTaskAsync(
        Guid chatId,
        string userContent,
        string? role = null,
        AgentRunOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new AgentRunOptions();
        var maxSteps = Math.Clamp(options.MaxSteps, 1, 12);
        var chat = await _db.Set<Chat>()
            .FirstOrDefaultAsync(c => c.Id == chatId, ct)
            ?? throw new InvalidOperationException($"Chat not found: {chatId}");
        var effective = await _settingsService.GetEffectiveSettingsAsync(chatId, ct);
        EnsureProviderReady(effective.ApiKey, effective.BaseUrl, effective.Model);
        var priorMessages = await _db.Set<Message>()
            .Where(m => m.ChatId == chatId)
            .OrderBy(m => m.SequenceNum)
            .ToListAsync(ct);
        var msgRole = role ?? effective.UserRole;
        var turnCount = await _db.Set<Turn>()
            .CountAsync(t => t.ChatId == chatId, ct);
        var turn = new Turn
        {
            ChatId = chatId,
            TurnNumber = turnCount + 1
        };
        _db.Set<Turn>().Add(turn);
        await _db.SaveChangesAsync(ct);

        var seq = await _chatService.GetNextSequenceAsync(chatId, ct);
        var sentMessage = new Message
        {
            ChatId = chatId,
            Role = msgRole,
            Content = userContent,
            TurnId = turn.Id,
            SequenceNum = seq++
        };
        _db.Set<Message>().Add(sentMessage);
        var run = new AgentRun
        {
            ChatId = chatId,
            TurnId = turn.Id,
            Status = AgentRunStatuses.Running,
            UserRequest = userContent,
            MaxSteps = maxSteps
        };
        _db.Set<AgentRun>().Add(run);
        await _db.SaveChangesAsync(ct);
        await LogAgentEventAsync(
            run,
            AgentEventTypes.RunStarted,
            "Agent run started.",
            new { run.MaxSteps, role = msgRole },
            ct: ct);

        var state = new AgentExecutionState(
            priorMessages
            .Select(ToProviderMessage)
            .Append(new MessagePayload(NormalizeProviderRole(msgRole), userContent))
            .ToList(),
            seq);
        await SaveCheckpointAsync(run, state, ct);
        return await ContinueAgentRunInternalAsync(run, turn, sentMessage, state, options, ct);
    }

    public async Task<SendMessageResult> ResumeAgentTaskAsync(
        Guid agentRunId,
        AgentRunOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new AgentRunOptions();
        var run = await _db.Set<AgentRun>()
            .FirstOrDefaultAsync(r => r.Id == agentRunId, ct)
            ?? throw new InvalidOperationException($"Agent run not found: {agentRunId}");
        if (run.Status == AgentRunStatuses.Completed)
            throw new InvalidOperationException("The agent run is already complete.");

        var turn = await _db.Set<Turn>().FirstAsync(t => t.Id == run.TurnId, ct);
        var sentMessage = await _db.Set<Message>()
            .Where(m => m.TurnId == run.TurnId && (m.Role == "user" || m.Role == "system"))
            .OrderBy(m => m.SequenceNum)
            .FirstAsync(ct);
        var checkpoint = await _db.Set<AgentCheckpoint>()
            .Where(c => c.AgentRunId == run.Id)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The agent run has no checkpoint to resume.");
        var state = JsonSerializer.Deserialize<AgentExecutionState>(checkpoint.StateJson)
            ?? throw new InvalidOperationException("The agent checkpoint is invalid.");

        run.MaxSteps = Math.Clamp(
            Math.Max(run.MaxSteps, run.CurrentStep + Math.Clamp(options.MaxSteps, 1, 12)),
            1,
            48);
        run.Status = AgentRunStatuses.Running;
        run.ErrorMessage = null;
        run.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await LogAgentEventAsync(
            run,
            AgentEventTypes.Resume,
            "Agent run resumed from the latest checkpoint.",
            new { run.CurrentStep, run.MaxSteps },
            ct: ct);

        return await ContinueAgentRunInternalAsync(run, turn, sentMessage, state, options, ct);
    }

    public async Task<AgentRunSnapshot?> GetLatestAgentRunAsync(
        Guid chatId,
        CancellationToken ct = default)
    {
        var run = await _db.Set<AgentRun>()
            .Where(r => r.ChatId == chatId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return run == null ? null : await ToAgentRunSnapshotAsync(run, ct);
    }

    public async Task SetAgentToolApprovalAsync(
        Guid invocationId,
        bool approved,
        string policyScope = "once",
        CancellationToken ct = default)
    {
        var invocation = await _db.Set<ToolInvocation>()
            .Include(i => i.AgentRun)
            .FirstOrDefaultAsync(i => i.Id == invocationId, ct)
            ?? throw new InvalidOperationException($"Tool invocation not found: {invocationId}");
        if (invocation.Status != ToolInvocationStatuses.AwaitingApproval)
            throw new InvalidOperationException("This tool invocation is not awaiting approval.");

        invocation.Approved = approved;
        invocation.ApprovedAt = DateTime.UtcNow;
        invocation.Status = approved
            ? ToolInvocationStatuses.Approved
            : ToolInvocationStatuses.Denied;
        invocation.AgentRun.Status = AgentRunStatuses.Paused;
        invocation.AgentRun.UpdatedAt = DateTime.UtcNow;

        if (approved && policyScope != "once")
        {
            await _toolPlatform.SavePolicyAsync(
                invocation.AgentRun.ChatId,
                invocation.ToolName,
                policyScope,
                ToolPolicyDecisions.Allow,
                ct);
        }
        else if (!approved && policyScope == ToolPolicyScopes.Global)
        {
            await _toolPlatform.SavePolicyAsync(
                invocation.AgentRun.ChatId,
                invocation.ToolName,
                ToolPolicyScopes.Global,
                ToolPolicyDecisions.Deny,
                ct);
        }

        await _db.SaveChangesAsync(ct);
        await LogAgentEventAsync(
            invocation.AgentRun,
            approved ? AgentEventTypes.ApprovalGranted : AgentEventTypes.ApprovalDenied,
            approved ? "User approved an agent tool invocation." : "User denied an agent tool invocation.",
            new { invocation.ToolName, policyScope },
            toolInvocationId: invocation.Id,
            severity: approved ? AgentEventSeverities.Info : AgentEventSeverities.Warning,
            ct: ct);
    }

    public async Task CancelAgentRunAsync(Guid agentRunId, CancellationToken ct = default)
    {
        var run = await _db.Set<AgentRun>()
            .FirstOrDefaultAsync(r => r.Id == agentRunId, ct)
            ?? throw new InvalidOperationException($"Agent run not found: {agentRunId}");
        if (run.Status == AgentRunStatuses.Completed)
            return;
        run.Status = AgentRunStatuses.Cancelled;
        run.UpdatedAt = DateTime.UtcNow;
        run.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await LogAgentEventAsync(
            run,
            AgentEventTypes.RunCancelled,
            "Agent run cancelled.",
            new { run.CurrentStep, run.MaxSteps },
            severity: AgentEventSeverities.Warning,
            ct: ct);
    }

    private async Task<SendMessageResult> ContinueAgentRunInternalAsync(
        AgentRun run,
        Turn turn,
        Message sentMessage,
        AgentExecutionState state,
        AgentRunOptions options,
        CancellationToken ct)
    {
        var chat = await _db.Set<Chat>().FirstAsync(c => c.Id == run.ChatId, ct);
        var effective = await _settingsService.GetEffectiveSettingsAsync(run.ChatId, ct);
        EnsureProviderReady(effective.ApiKey, effective.BaseUrl, effective.Model);
        var systemPrompt = await SystemPromptBuilder.BuildAsync(
            _db.Set<Chat>(),
            _db.Set<GlobalSettings>(),
            _db.Set<AgentFile>(),
            _db.Set<ProjectSpace>(),
            _db.Set<ConfigProfile>(),
            run.ChatId,
            ct);
        systemPrompt = BuildAgentSystemPrompt(
            systemPrompt,
            _sandboxCommandService.GetSandboxRoot(run.ChatId));
        var provider = LlmProviderFactory.Create(
            _httpClientFactory.CreateClient("LLM"),
            effective.Provider,
            effective.ApiKey,
            effective.BaseUrl,
            effective.Model);
        LlmResponse? lastResponse = null;
        Message? lastAssistantMessage = null;

        try
        {
            var pending = await _db.Set<ToolInvocation>()
                .Include(i => i.AgentStep)
                .Where(i => i.AgentRunId == run.Id &&
                    (i.Status == ToolInvocationStatuses.Approved ||
                     i.Status == ToolInvocationStatuses.Denied))
                .OrderBy(i => i.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (pending != null)
                await ExecuteOrDenyInvocationAsync(run, pending, state, options, ct);

            while (run.CurrentStep < run.MaxSteps)
            {
                ct.ThrowIfCancellationRequested();
                var stepNumber = run.CurrentStep + 1;
                var step = new AgentStep
                {
                    AgentRunId = run.Id,
                    StepNumber = stepNumber,
                    Kind = "model",
                    Status = AgentStepStatuses.Running,
                    Summary = "Model selected the next action.",
                    InputJson = SecretRedactor.RedactJson(JsonSerializer.Serialize(state.Messages))
                };
                _db.Set<AgentStep>().Add(step);
                await _db.SaveChangesAsync(ct);

                var guard = ToolProtocolGuard.RepairForProvider(state.Messages, _agentTools.Definitions);
                if (guard.HasRepairs)
                {
                    state.Messages = guard.Messages.ToList();
                    step.InputJson = SecretRedactor.RedactJson(JsonSerializer.Serialize(state.Messages));
                    await LogAgentEventAsync(
                        run,
                        AgentEventTypes.ProtocolRepair,
                        "Tool protocol guard repaired provider-bound messages.",
                        new { issues = guard.Issues },
                        step.Id,
                        severity: guard.IsRejected ? AgentEventSeverities.Error : AgentEventSeverities.Warning,
                        ct: ct);
                }

                if (guard.IsRejected)
                {
                    step.Kind = "protocol_error";
                    step.Status = AgentStepStatuses.Failed;
                    step.Summary = guard.RejectionReason!;
                    step.CompletedAt = DateTime.UtcNow;
                    run.CurrentStep = stepNumber;
                    run.Status = AgentRunStatuses.Failed;
                    run.ErrorMessage = guard.RejectionReason;
                    run.CompletedAt = DateTime.UtcNow;
                    run.UpdatedAt = DateTime.UtcNow;
                    await LogAgentEventAsync(
                        run,
                        AgentEventTypes.Error,
                        guard.RejectionReason!,
                        new { guard.Issues },
                        step.Id,
                        severity: AgentEventSeverities.Error,
                        ct: ct);
                    lastAssistantMessage = await AddAgentStatusMessageAsync(
                        run,
                        turn,
                        state,
                        $"Agent stopped because the tool protocol state is invalid: {guard.RejectionReason}",
                        ct);
                    break;
                }

                await LogAgentEventAsync(
                    run,
                    AgentEventTypes.ModelRequest,
                    "Sending agent context to the model.",
                    new
                    {
                        stepNumber,
                        messageCount = guard.Messages.Count,
                        toolCount = guard.Tools.Count,
                        provider = provider.ProviderName
                    },
                    step.Id,
                    ct: ct);
                lastResponse = await provider.ChatAsync(
                    messages: guard.Messages.ToList(),
                    systemPrompt: systemPrompt,
                    temperature: effective.Temperature,
                    maxTokens: effective.MaxTokens,
                    tools: guard.Tools,
                    ct: ct);
                step.OutputJson = SecretRedactor.RedactJson(JsonSerializer.Serialize(lastResponse.RawResponse));
                await LogAgentEventAsync(
                    run,
                    AgentEventTypes.ModelResponse,
                    $"Model returned HTTP {lastResponse.HttpStatus}.",
                    new
                    {
                        stepNumber,
                        lastResponse.HttpStatus,
                        lastResponse.LatencyMs,
                        toolCallCount = lastResponse.ToolCalls?.Count ?? 0,
                        hasError = !string.IsNullOrWhiteSpace(lastResponse.Error)
                    },
                    step.Id,
                    severity: lastResponse.HttpStatus is >= 200 and < 300 && string.IsNullOrWhiteSpace(lastResponse.Error)
                        ? AgentEventSeverities.Info
                        : AgentEventSeverities.Error,
                    ct: ct);

                if (lastResponse.HttpStatus is < 200 or >= 300 ||
                    !string.IsNullOrWhiteSpace(lastResponse.Error))
                {
                    step.Kind = "provider_error";
                    step.Status = AgentStepStatuses.Failed;
                    step.Summary = lastResponse.Error ?? $"Provider returned HTTP {lastResponse.HttpStatus}.";
                    step.CompletedAt = DateTime.UtcNow;
                    run.CurrentStep = stepNumber;
                    run.Status = AgentRunStatuses.Failed;
                    run.ErrorMessage = SecretRedactor.RedactText(step.Summary);
                    run.CompletedAt = DateTime.UtcNow;
                    run.UpdatedAt = DateTime.UtcNow;
                    await LogAgentEventAsync(
                        run,
                        AgentEventTypes.Error,
                        SecretRedactor.RedactText(step.Summary),
                        new { lastResponse.HttpStatus, lastResponse.LatencyMs },
                        step.Id,
                        severity: AgentEventSeverities.Error,
                        ct: ct);
                    lastAssistantMessage = await AddAgentStatusMessageAsync(
                        run,
                        turn,
                        state,
                        lastResponse.AssistantText,
                        ct);
                    break;
                }

                var toolCall = lastResponse.ToolCalls?.FirstOrDefault();
                if (toolCall == null &&
                    AgentToolParser.TryParseSandboxCommand(lastResponse.AssistantText, out var legacyToolRequest))
                {
                    toolCall = new LlmToolCall(
                        $"legacy-{Guid.NewGuid():N}",
                        AgentToolNames.SandboxExec,
                        JsonSerializer.Serialize(new
                        {
                            command = legacyToolRequest.Command,
                            reason = legacyToolRequest.Reason
                        }));
                }

                if (toolCall == null)
                {
                    step.Kind = "final";
                    step.Status = AgentStepStatuses.Completed;
                    step.Summary = "Agent completed the task.";
                    step.CompletedAt = DateTime.UtcNow;
                    run.CurrentStep = stepNumber;
                    run.Status = AgentRunStatuses.Completed;
                    run.CompletedAt = DateTime.UtcNow;
                    run.UpdatedAt = DateTime.UtcNow;
                    await LogAgentEventAsync(
                        run,
                        AgentEventTypes.RunCompleted,
                        "Agent completed the task.",
                        new { run.CurrentStep, run.MaxSteps },
                        step.Id,
                        ct: ct);
                    lastAssistantMessage = new Message
                    {
                        ChatId = run.ChatId,
                        Role = "assistant",
                        Content = lastResponse.AssistantText,
                        TurnId = turn.Id,
                        SequenceNum = state.SequenceNum++
                    };
                    _db.Set<Message>().Add(lastAssistantMessage);
                    chat.UpdatedAt = DateTime.UtcNow;
                    await SaveCheckpointAsync(run, state, ct);
                    await _db.SaveChangesAsync(ct);
                    break;
                }

                var toolIssues = new List<ToolProtocolGuardIssue>();
                var safeToolCall = ToolProtocolGuard.SanitizeToolCall(toolCall, toolIssues);
                if (toolIssues.Count > 0)
                {
                    await LogAgentEventAsync(
                        run,
                        AgentEventTypes.ProtocolRepair,
                        "Tool protocol guard repaired a model tool request.",
                        new { issues = toolIssues },
                        step.Id,
                        severity: safeToolCall == null ? AgentEventSeverities.Error : AgentEventSeverities.Warning,
                        ct: ct);
                }

                if (safeToolCall == null)
                {
                    step.Status = AgentStepStatuses.Failed;
                    step.Summary = $"Invalid tool request: {toolCall.Name}";
                    step.CompletedAt = DateTime.UtcNow;
                    run.Status = AgentRunStatuses.Failed;
                    run.ErrorMessage = step.Summary;
                    run.UpdatedAt = DateTime.UtcNow;
                    await LogAgentEventAsync(
                        run,
                        AgentEventTypes.Error,
                        step.Summary,
                        new { toolCall.Name, toolCall.ArgumentsJson },
                        step.Id,
                        severity: AgentEventSeverities.Error,
                        ct: ct);
                    await _db.SaveChangesAsync(ct);
                    lastAssistantMessage = await AddAgentStatusMessageAsync(
                        run,
                        turn,
                        state,
                        $"Agent stopped because the model requested an invalid tool: {toolCall.Name}.",
                        ct);
                    break;
                }

                toolCall = safeToolCall;
                if (!_agentTools.TryGet(toolCall.Name, out var tool))
                {
                    step.Status = AgentStepStatuses.Failed;
                    step.Summary = $"Unknown tool: {toolCall.Name}";
                    step.CompletedAt = DateTime.UtcNow;
                    run.Status = AgentRunStatuses.Failed;
                    run.ErrorMessage = step.Summary;
                    run.UpdatedAt = DateTime.UtcNow;
                    await LogAgentEventAsync(
                        run,
                        AgentEventTypes.Error,
                        step.Summary,
                        new { toolCall.Name },
                        step.Id,
                        severity: AgentEventSeverities.Error,
                        ct: ct);
                    await _db.SaveChangesAsync(ct);
                    lastAssistantMessage = await AddAgentStatusMessageAsync(
                        run,
                        turn,
                        state,
                        $"Agent stopped because the model requested an unavailable tool: {toolCall.Name}.",
                        ct);
                    break;
                }

                step.Kind = toolCall.Name;
                step.Summary = ReadToolReason(toolCall.ArgumentsJson) ?? $"Run {toolCall.Name}.";
                var safety = ToolSafetyKernel.Assess(
                    _sandboxCommandService,
                    run.ChatId,
                    toolCall.Name,
                    toolCall.ArgumentsJson);
                var invocation = new ToolInvocation
                {
                    AgentRunId = run.Id,
                    AgentStepId = step.Id,
                    ToolName = toolCall.Name,
                    ProviderCallId = toolCall.Id,
                    ArgumentsJson = SecretRedactor.RedactJson(toolCall.ArgumentsJson),
                    SafetyLevel = safety.Level,
                    SafetySummary = safety.Summary,
                    SafetyJson = SecretRedactor.RedactJson(safety.PreviewJson),
                    RequiresApproval = tool.RequiresApproval
                };
                _db.Set<ToolInvocation>().Add(invocation);

                var requestMessage = new Message
                {
                    ChatId = run.ChatId,
                    Role = "assistant",
                    Content = FormatToolRequestMessage(stepNumber, toolCall, safety),
                    TurnId = turn.Id,
                    SequenceNum = state.SequenceNum++
                };
                _db.Set<Message>().Add(requestMessage);
                lastAssistantMessage = requestMessage;
                state.Messages.Add(new MessagePayload(
                    "assistant",
                    lastResponse.AssistantText,
                    ToolCalls: [toolCall]));
                run.CurrentStep = stepNumber;
                run.UpdatedAt = DateTime.UtcNow;
                await LogAgentEventAsync(
                    run,
                    AgentEventTypes.ToolRequest,
                    $"Model requested {toolCall.Name}.",
                    new
                    {
                        toolCall.Name,
                        reason = ReadToolReason(toolCall.ArgumentsJson),
                        safety.Level,
                        safety.Category,
                        safety.IsReadOnly,
                        safety.IsWriteOperation,
                        safety.RequiresExplicitApproval,
                        safety.IsBlocked
                    },
                    step.Id,
                    invocation.Id,
                    severity: safety.Level == ToolSafetyLevels.Low ? AgentEventSeverities.Info : AgentEventSeverities.Warning,
                    ct: ct);

                var policy = await _toolPlatform.EvaluatePolicyAsync(
                    run.ChatId, toolCall.Name, ct);
                if (policy.IsDenied)
                {
                    invocation.Approved = false;
                    invocation.ApprovedAt = DateTime.UtcNow;
                    invocation.Status = ToolInvocationStatuses.Denied;
                    await _db.SaveChangesAsync(ct);
                    await LogAgentEventAsync(
                        run,
                        AgentEventTypes.ApprovalDenied,
                        "Tool invocation denied by policy.",
                        new { toolCall.Name, policy.Scope },
                        step.Id,
                        invocation.Id,
                        severity: AgentEventSeverities.Warning,
                        ct: ct);
                    await ExecuteOrDenyInvocationAsync(run, invocation, state, options, ct);
                    continue;
                }

                if (safety.IsBlocked)
                {
                    invocation.Approved = false;
                    invocation.ApprovedAt = DateTime.UtcNow;
                    invocation.Status = ToolInvocationStatuses.Failed;
                    step.Status = AgentStepStatuses.Failed;
                    await _db.SaveChangesAsync(ct);
                    await LogAgentEventAsync(
                        run,
                        AgentEventTypes.Error,
                        $"Safety policy blocked {toolCall.Name}: {safety.Summary}",
                        new { safety.Warning, safety.PreviewJson },
                        step.Id,
                        invocation.Id,
                        severity: AgentEventSeverities.Warning,
                        ct: ct);
                    await CompleteInvocationWithResultAsync(
                        run,
                        invocation,
                        step,
                        new AgentToolResult(
                            false,
                            string.Empty,
                            $"Safety policy blocked this tool call. {safety.Warning ?? safety.Summary}"),
                        state,
                        ct);
                    continue;
                }

                var requiresManualApproval =
                    (tool.RequiresApproval && !options.AutoApproveTools && !policy.IsAllowed) ||
                    (safety.RequiresExplicitApproval && !policy.IsAllowed);
                if (requiresManualApproval)
                {
                    invocation.Status = ToolInvocationStatuses.AwaitingApproval;
                    step.Status = AgentStepStatuses.AwaitingApproval;
                    run.Status = AgentRunStatuses.AwaitingApproval;
                    await SaveCheckpointAsync(run, state, ct);
                    await _db.SaveChangesAsync(ct);
                    await LogAgentEventAsync(
                        run,
                        AgentEventTypes.ApprovalRequested,
                        $"Waiting for user approval to run {toolCall.Name}.",
                        new
                        {
                            toolCall.Name,
                            safety.Level,
                            safety.Warning,
                            autoApproveRequested = options.AutoApproveTools,
                            policy.Scope
                        },
                        step.Id,
                        invocation.Id,
                        severity: safety.RequiresExplicitApproval ? AgentEventSeverities.Warning : AgentEventSeverities.Info,
                        ct: ct);
                    return await BuildAgentResultAsync(
                        run, turn, sentMessage, requestMessage, provider, lastResponse, effective.ApiKey, ct);
                }

                invocation.Approved = true;
                invocation.ApprovedAt = DateTime.UtcNow;
                invocation.Status = ToolInvocationStatuses.Approved;
                await _db.SaveChangesAsync(ct);
                await LogAgentEventAsync(
                    run,
                    AgentEventTypes.ApprovalGranted,
                    $"Tool invocation approved automatically for {toolCall.Name}.",
                    new { toolCall.Name, policy.Scope, safety.Level },
                    step.Id,
                    invocation.Id,
                    ct: ct);
                await ExecuteOrDenyInvocationAsync(run, invocation, state, options, ct);
            }

            if (run.Status == AgentRunStatuses.Running)
            {
                run.Status = AgentRunStatuses.Paused;
                run.UpdatedAt = DateTime.UtcNow;
                await LogAgentEventAsync(
                    run,
                    AgentEventTypes.RunPaused,
                    "Agent reached the step budget and paused.",
                    new { run.CurrentStep, run.MaxSteps },
                    severity: AgentEventSeverities.Warning,
                    ct: ct);
                lastAssistantMessage = await AddAgentStatusMessageAsync(
                    run,
                    turn,
                    state,
                    $"Agent paused after reaching the step budget ({run.MaxSteps}). The run is saved and can be resumed.",
                    ct);
            }

            lastResponse ??= new LlmResponse(
                new Dictionary<string, object>(),
                new Dictionary<string, object> { ["status"] = run.Status },
                200,
                0,
                lastAssistantMessage?.Content ?? string.Empty);
            lastAssistantMessage ??= await AddAgentStatusMessageAsync(
                run, turn, state, $"Agent run is {run.Status}.", ct);
            return await BuildAgentResultAsync(
                run, turn, sentMessage, lastAssistantMessage, provider, lastResponse, effective.ApiKey, ct);
        }
        catch (OperationCanceledException)
        {
            run.Status = AgentRunStatuses.Cancelled;
            run.UpdatedAt = DateTime.UtcNow;
            run.ErrorMessage = "Stopped by the user.";
            await SaveCheckpointAsync(run, state, CancellationToken.None);
            await _db.SaveChangesAsync(CancellationToken.None);
            await LogAgentEventAsync(
                run,
                AgentEventTypes.RunCancelled,
                "Agent run cancelled by cancellation token.",
                new { run.CurrentStep, run.MaxSteps },
                severity: AgentEventSeverities.Warning,
                ct: CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            run.Status = AgentRunStatuses.Failed;
            run.UpdatedAt = DateTime.UtcNow;
            run.ErrorMessage = SecretRedactor.RedactText(ex.Message);
            await SaveCheckpointAsync(run, state, CancellationToken.None);
            await _db.SaveChangesAsync(CancellationToken.None);
            await LogAgentEventAsync(
                run,
                AgentEventTypes.Error,
                run.ErrorMessage,
                new { exceptionType = ex.GetType().Name },
                severity: AgentEventSeverities.Error,
                ct: CancellationToken.None);
            throw;
        }
    }

    private async Task ExecuteOrDenyInvocationAsync(
        AgentRun run,
        ToolInvocation invocation,
        AgentExecutionState state,
        AgentRunOptions options,
        CancellationToken ct)
    {
        var step = await _db.Set<AgentStep>()
            .FirstAsync(s => s.Id == invocation.AgentStepId, ct);
        AgentToolResult result;
        if (invocation.Approved == false || invocation.Status == ToolInvocationStatuses.Denied)
        {
            result = new AgentToolResult(false, string.Empty, "The user denied this tool invocation.");
            invocation.Status = ToolInvocationStatuses.Denied;
            step.Status = AgentStepStatuses.Denied;
            await LogAgentEventAsync(
                run,
                AgentEventTypes.ApprovalDenied,
                $"Tool invocation denied: {invocation.ToolName}.",
                new { invocation.ToolName },
                step.Id,
                invocation.Id,
                severity: AgentEventSeverities.Warning,
                ct: ct);
        }
        else if (!_agentTools.TryGet(invocation.ToolName, out var tool))
        {
            result = new AgentToolResult(false, string.Empty, $"Tool is unavailable: {invocation.ToolName}");
            invocation.Status = ToolInvocationStatuses.Failed;
            step.Status = AgentStepStatuses.Failed;
        }
        else
        {
            var safety = ToolSafetyKernel.Assess(
                _sandboxCommandService,
                run.ChatId,
                invocation.ToolName,
                invocation.ArgumentsJson);
            invocation.SafetyLevel = safety.Level;
            invocation.SafetySummary = safety.Summary;
            invocation.SafetyJson = SecretRedactor.RedactJson(safety.PreviewJson);
            if (safety.IsBlocked)
            {
                result = new AgentToolResult(
                    false,
                    string.Empty,
                    $"Safety policy blocked this tool call. {safety.Warning ?? safety.Summary}");
                invocation.Status = ToolInvocationStatuses.Failed;
                step.Status = AgentStepStatuses.Failed;
                await LogAgentEventAsync(
                    run,
                    AgentEventTypes.Error,
                    $"Safety policy blocked {invocation.ToolName}: {safety.Summary}",
                    new { safety.Warning, safety.PreviewJson },
                    step.Id,
                    invocation.Id,
                    severity: AgentEventSeverities.Warning,
                    ct: ct);
                await CompleteInvocationWithResultAsync(run, invocation, step, result, state, ct);
                return;
            }

            invocation.Status = ToolInvocationStatuses.Running;
            invocation.StartedAt = DateTime.UtcNow;
            run.Status = AgentRunStatuses.Running;
            await _db.SaveChangesAsync(ct);
            await LogAgentEventAsync(
                run,
                AgentEventTypes.ToolStarted,
                $"Running tool {invocation.ToolName}.",
                new
                {
                    invocation.ToolName,
                    safety.Level,
                    safety.Category,
                    safety.IsReadOnly,
                    safety.IsWriteOperation
                },
                step.Id,
                invocation.Id,
                ct: ct);
            result = await tool.ExecuteAsync(
                new AgentToolExecutionContext(
                    run.ChatId,
                    run.Id,
                    invocation.Id,
                    options.CommandTimeoutSeconds,
                    options.MaxCommandOutputChars),
                invocation.ArgumentsJson,
                ct);
            invocation.Status = result.Success
                ? ToolInvocationStatuses.Completed
                : ToolInvocationStatuses.Failed;
            step.Status = result.Success
                ? AgentStepStatuses.Completed
                : AgentStepStatuses.Failed;
        }

        await CompleteInvocationWithResultAsync(run, invocation, step, result, state, ct);
    }

    private async Task CompleteInvocationWithResultAsync(
        AgentRun run,
        ToolInvocation invocation,
        AgentStep step,
        AgentToolResult result,
        AgentExecutionState state,
        CancellationToken ct)
    {
        if (invocation.Status != ToolInvocationStatuses.Denied)
        {
            invocation.Status = result.Success
                ? ToolInvocationStatuses.Completed
                : ToolInvocationStatuses.Failed;
        }

        if (step.Status != AgentStepStatuses.Denied)
        {
            step.Status = result.Success
                ? AgentStepStatuses.Completed
                : AgentStepStatuses.Failed;
        }

        invocation.ResultJson = result.ToJson();
        invocation.CompletedAt = DateTime.UtcNow;
        step.OutputJson = invocation.ResultJson;
        step.CompletedAt = DateTime.UtcNow;
        run.UpdatedAt = DateTime.UtcNow;
        var toolContent = FormatToolResultMessage(step.StepNumber, invocation.ToolName, result);
        _db.Set<Message>().Add(new Message
        {
            ChatId = run.ChatId,
            Role = "tool",
            Content = toolContent,
            TurnId = run.TurnId,
            SequenceNum = state.SequenceNum++
        });
        state.Messages.Add(new MessagePayload(
            "tool",
            result.ToJson(),
            invocation.ProviderCallId));
        await UpsertArtifactsAsync(run.Id, result.Artifacts, ct);
        await SaveCheckpointAsync(run, state, ct);
        await _db.SaveChangesAsync(ct);
        await LogAgentEventAsync(
            run,
            AgentEventTypes.ToolResult,
            result.Success
                ? $"Tool {invocation.ToolName} completed."
                : $"Tool {invocation.ToolName} failed.",
            new
            {
                invocation.ToolName,
                result.Success,
                error = result.Error,
                artifactCount = result.Artifacts?.Count ?? 0
            },
            step.Id,
            invocation.Id,
            severity: result.Success ? AgentEventSeverities.Info : AgentEventSeverities.Error,
            ct: ct);
    }

    private async Task<Message> AddAgentStatusMessageAsync(
        AgentRun run,
        Turn turn,
        AgentExecutionState state,
        string content,
        CancellationToken ct)
    {
        var message = new Message
        {
            ChatId = run.ChatId,
            Role = "assistant",
            Content = content,
            TurnId = turn.Id,
            SequenceNum = state.SequenceNum++
        };
        _db.Set<Message>().Add(message);
        await SaveCheckpointAsync(run, state, ct);
        await _db.SaveChangesAsync(ct);
        return message;
    }

    private async Task<SendMessageResult> BuildAgentResultAsync(
        AgentRun run,
        Turn turn,
        Message sentMessage,
        Message assistantMessage,
        ILlmProvider provider,
        LlmResponse response,
        string apiKey,
        CancellationToken ct)
    {
        var steps = await _db.Set<AgentStep>()
            .Where(s => s.AgentRunId == run.Id)
            .OrderBy(s => s.StepNumber)
            .Select(s => new
            {
                s.StepNumber,
                s.Kind,
                s.Status,
                s.Summary,
                s.InputJson,
                s.OutputJson,
                s.StartedAt,
                s.CompletedAt
            })
            .ToListAsync(ct);
        var rawRequest = await _db.Set<RawRequest>().FirstOrDefaultAsync(r => r.TurnId == turn.Id, ct);
        if (rawRequest == null)
        {
            rawRequest = new RawRequest { TurnId = turn.Id };
            _db.Set<RawRequest>().Add(rawRequest);
        }
        rawRequest.Provider = provider.ProviderName;
        rawRequest.EndpointUrl = provider.EndpointUrl;
        rawRequest.RequestJson = SafeJson(new
        {
            agentMode = true,
            runId = run.Id,
            run.Status,
            run.MaxSteps,
            sandboxRoot = _sandboxCommandService.GetSandboxRoot(run.ChatId),
            tools = _agentTools.Definitions,
            steps
        }, apiKey);

        var rawResponse = await _db.Set<RawResponse>().FirstOrDefaultAsync(r => r.TurnId == turn.Id, ct);
        if (rawResponse == null)
        {
            rawResponse = new RawResponse { TurnId = turn.Id };
            _db.Set<RawResponse>().Add(rawResponse);
        }
        rawResponse.Provider = provider.ProviderName;
        rawResponse.ResponseJson = SafeJson(response.RawResponse, apiKey);
        rawResponse.HttpStatusCode = response.HttpStatus;
        rawResponse.LatencyMs = response.LatencyMs;
        rawResponse.TokenUsageJson = response.TokenUsage == null
            ? null
            : JsonSerializer.Serialize(response.TokenUsage);
        await _db.SaveChangesAsync(ct);

        var snapshot = await ToAgentRunSnapshotAsync(run, ct);
        await LogAuditAsync(
            "agent_run",
            "agent_run",
            run.Id.ToString("D"),
            $"Agent run {run.Status} at step {run.CurrentStep}.",
            (await _db.Set<Chat>().FirstAsync(c => c.Id == run.ChatId, ct)).ProjectSpaceId,
            run.ChatId,
            new { run.Status, run.CurrentStep, run.MaxSteps, snapshot.ArtifactCount },
            ct);
        return new SendMessageResult(
            turn, sentMessage, assistantMessage, rawRequest, rawResponse, snapshot);
    }

    private async Task SaveCheckpointAsync(
        AgentRun run,
        AgentExecutionState state,
        CancellationToken ct)
    {
        _db.Set<AgentCheckpoint>().Add(new AgentCheckpoint
        {
            AgentRunId = run.Id,
            StepNumber = run.CurrentStep,
            StateJson = JsonSerializer.Serialize(state)
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task UpsertArtifactsAsync(
        Guid runId,
        IReadOnlyList<AgentToolArtifact>? artifacts,
        CancellationToken ct)
    {
        if (artifacts == null)
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
            }
            else
            {
                existing.ContentType = artifact.ContentType;
                existing.SizeBytes = artifact.SizeBytes;
                existing.Sha256 = artifact.Sha256;
                existing.UpdatedAt = DateTime.UtcNow;
            }
        }
    }

    private async Task<AgentRunSnapshot> ToAgentRunSnapshotAsync(
        AgentRun run,
        CancellationToken ct)
    {
        var pending = await _db.Set<ToolInvocation>()
            .Where(i => i.AgentRunId == run.Id &&
                        i.Status == ToolInvocationStatuses.AwaitingApproval)
            .OrderBy(i => i.CreatedAt)
            .Select(i => new ToolInvocationSnapshot(
                i.Id, i.ToolName, i.ArgumentsJson, i.Status))
            .FirstOrDefaultAsync(ct);
        var artifactCount = await _db.Set<AgentArtifact>()
            .CountAsync(a => a.AgentRunId == run.Id, ct);
        return new AgentRunSnapshot(
            run.Id,
            run.ChatId,
            run.TurnId,
            run.Status,
            run.CurrentStep,
            run.MaxSteps,
            run.ErrorMessage,
            artifactCount,
            pending);
    }

    private static string? ReadToolReason(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty("reason", out var reason)
                ? reason.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<SendMessageResult> RegenerateAssistantAsync(Guid assistantMessageId, CancellationToken ct = default)
    {
        var assistant = await _db.Set<Message>()
            .FirstOrDefaultAsync(m => m.Id == assistantMessageId, ct)
            ?? throw new InvalidOperationException($"Message not found: {assistantMessageId}");

        if (!string.Equals(assistant.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only assistant messages can be regenerated.");

        var sourceMessage = assistant.TurnId == null
            ? null
            : await _db.Set<Message>()
                .Where(m => m.TurnId == assistant.TurnId && m.SequenceNum < assistant.SequenceNum && m.Role != "tool")
                .OrderByDescending(m => m.SequenceNum)
                .FirstOrDefaultAsync(ct);

        sourceMessage ??= await _db.Set<Message>()
            .Where(m => m.ChatId == assistant.ChatId && m.SequenceNum < assistant.SequenceNum && m.Role != "assistant" && m.Role != "tool")
            .OrderByDescending(m => m.SequenceNum)
            .FirstOrDefaultAsync(ct);

        if (sourceMessage == null)
            throw new InvalidOperationException("Could not find the user message for this assistant response.");

        await _chatService.DeleteMessagesAfterAsync(assistant.Id, includeSelected: true, ct);
        var history = await _db.Set<Message>()
            .Where(m => m.ChatId == sourceMessage.ChatId && m.SequenceNum <= sourceMessage.SequenceNum)
            .OrderBy(m => m.SequenceNum)
            .ToListAsync(ct);

        return await CallProviderForExistingHistoryAsync(sourceMessage.ChatId, sourceMessage, history, sourceMessage.SequenceNum + 1, ct);
    }

    public async Task<SendMessageResult> EditAndResendAsync(Guid messageId, string content, CancellationToken ct = default)
    {
        var message = await _db.Set<Message>()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct)
            ?? throw new InvalidOperationException($"Message not found: {messageId}");

        if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Assistant messages can be regenerated, but only user/system messages can be edited and resent.");

        var chatId = message.ChatId;
        var role = string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase) ? "system" : null;
        await _chatService.DeleteMessagesAfterAsync(message.Id, includeSelected: true, ct);
        return await SendMessageAsync(chatId, content, role, ct);
    }

    public async Task<SendMessageResult> ContinueFromMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        var message = await _db.Set<Message>()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct)
            ?? throw new InvalidOperationException($"Message not found: {messageId}");

        await _chatService.DeleteMessagesAfterAsync(message.Id, includeSelected: false, ct);
        return await SendMessageAsync(message.ChatId, "Continue.", null, ct);
    }

    public async Task<SendMessageResult> ReplayTurnAsync(Guid turnId, CancellationToken ct = default)
    {
        var sourceTurn = await _db.Set<Turn>()
            .FirstOrDefaultAsync(t => t.Id == turnId, ct)
            ?? throw new InvalidOperationException($"Turn not found: {turnId}");

        var rawRequest = await _db.Set<RawRequest>()
            .FirstOrDefaultAsync(r => r.TurnId == turnId, ct)
            ?? throw new InvalidOperationException("No raw request was recorded for this turn.");

        var sourceMessage = await _db.Set<Message>()
            .Where(m => m.TurnId == turnId && m.Role != "assistant" && m.Role != "tool")
            .OrderBy(m => m.SequenceNum)
            .FirstOrDefaultAsync(ct);

        var effective = await _settingsService.GetEffectiveSettingsAsync(sourceTurn.ChatId, ct);
        EnsureProviderReady(effective.ApiKey, effective.BaseUrl, effective.Model);
        var replay = ParseReplayRequest(rawRequest.RequestJson);

        var turnCount = await _db.Set<Turn>()
            .CountAsync(t => t.ChatId == sourceTurn.ChatId, ct);
        var newTurn = new Turn
        {
            ChatId = sourceTurn.ChatId,
            TurnNumber = turnCount + 1
        };
        _db.Set<Turn>().Add(newTurn);
        await _db.SaveChangesAsync(ct);

        var seq = await _chatService.GetNextSequenceAsync(sourceTurn.ChatId, ct);
        var replayMessage = new Message
        {
            ChatId = sourceTurn.ChatId,
            Role = sourceMessage?.Role ?? effective.UserRole,
            Content = sourceMessage?.Content ?? "Replay previous request.",
            TurnId = newTurn.Id,
            SequenceNum = seq
        };
        _db.Set<Message>().Add(replayMessage);

        var httpClient = _httpClientFactory.CreateClient("LLM");
        var provider = LlmProviderFactory.Create(
            httpClient,
            effective.Provider,
            effective.ApiKey,
            effective.BaseUrl,
            replay.Model ?? effective.Model);

        var llmResult = await provider.ChatAsync(
            replay.Messages,
            replay.SystemPrompt,
            replay.Temperature ?? effective.Temperature,
            replay.MaxTokens ?? effective.MaxTokens,
            ct: ct);

        var storedRequest = new RawRequest
        {
            TurnId = newTurn.Id,
            Provider = provider.ProviderName,
            EndpointUrl = provider.EndpointUrl,
            RequestJson = SafeJson(llmResult.RawRequest, effective.ApiKey)
        };
        _db.Set<RawRequest>().Add(storedRequest);

        var storedResponse = new RawResponse
        {
            TurnId = newTurn.Id,
            Provider = provider.ProviderName,
            ResponseJson = SafeJson(llmResult.RawResponse, effective.ApiKey),
            HttpStatusCode = llmResult.HttpStatus,
            LatencyMs = llmResult.LatencyMs,
            TokenUsageJson = llmResult.TokenUsage != null
                ? JsonSerializer.Serialize(llmResult.TokenUsage)
                : null
        };
        _db.Set<RawResponse>().Add(storedResponse);

        var assistantMessage = new Message
        {
            ChatId = sourceTurn.ChatId,
            Role = "assistant",
            Content = llmResult.AssistantText,
            TurnId = newTurn.Id,
            SequenceNum = seq + 1
        };
        _db.Set<Message>().Add(assistantMessage);

        var chat = await _db.Set<Chat>().FirstOrDefaultAsync(c => c.Id == sourceTurn.ChatId, ct);
        if (chat != null)
            chat.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        await LogAuditAsync("replay", "turn", newTurn.Id.ToString("D"), "Replayed recorded prompt payload.", chat?.ProjectSpaceId, sourceTurn.ChatId, new
        {
            sourceTurnId = turnId,
            newTurn.TurnNumber,
            provider = provider.ProviderName,
            llmResult.HttpStatus,
            llmResult.LatencyMs
        }, ct);
        return new SendMessageResult(newTurn, replayMessage, assistantMessage, storedRequest, storedResponse);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        string provider,
        string apiKey,
        string baseUrl,
        string model,
        CancellationToken ct = default)
    {
        try
        {
            var plainKey = ProtectedSecret.Reveal(apiKey);
            EnsureProviderReady(plainKey, baseUrl, model);
            var httpClient = _httpClientFactory.CreateClient("LLM");
            var llmProvider = LlmProviderFactory.Create(httpClient, provider, plainKey, baseUrl, model);
            var result = await llmProvider.ChatAsync(
                new List<MessagePayload> { new("user", "Reply with OK.") },
                "You are testing whether this API connection works.",
                temperature: 0,
                maxTokens: 16,
                ct: ct);

            var ok = result.HttpStatus is >= 200 and < 300 && string.IsNullOrWhiteSpace(result.Error);
            return new ConnectionTestResult(
                ok,
                ok ? "Connection OK." : result.Error ?? $"HTTP {result.HttpStatus}",
                result.HttpStatus,
                result.LatencyMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectionTestResult(false, ex.Message);
        }
    }

    private async Task<SendMessageResult> CallProviderForExistingHistoryAsync(
        Guid chatId,
        Message sourceMessage,
        List<Message> history,
        int assistantSequence,
        CancellationToken ct)
    {
        var chat = await _db.Set<Chat>()
            .FirstOrDefaultAsync(c => c.Id == chatId, ct)
            ?? throw new InvalidOperationException($"Chat not found: {chatId}");

        var effective = await _settingsService.GetEffectiveSettingsAsync(chatId, ct);
        EnsureProviderReady(effective.ApiKey, effective.BaseUrl, effective.Model);

        var systemPrompt = await SystemPromptBuilder.BuildAsync(
            _db.Set<Chat>(),
            _db.Set<GlobalSettings>(),
            _db.Set<AgentFile>(),
            _db.Set<ProjectSpace>(),
            _db.Set<ConfigProfile>(),
            chatId,
            ct);

        var turnCount = await _db.Set<Turn>()
            .CountAsync(t => t.ChatId == chatId, ct);
        var turn = new Turn
        {
            ChatId = chatId,
            TurnNumber = turnCount + 1
        };
        _db.Set<Turn>().Add(turn);
        await _db.SaveChangesAsync(ct);

        var httpClient = _httpClientFactory.CreateClient("LLM");
        var provider = LlmProviderFactory.Create(
            httpClient,
            effective.Provider,
            effective.ApiKey,
            effective.BaseUrl,
            effective.Model);

        var messagesForLlm = history
            .Select(ToProviderMessage)
            .ToList();

        var llmResult = await provider.ChatAsync(
            messages: messagesForLlm,
            systemPrompt: systemPrompt,
            temperature: effective.Temperature,
            maxTokens: effective.MaxTokens,
            ct: ct);

        var rawRequest = new RawRequest
        {
            TurnId = turn.Id,
            Provider = provider.ProviderName,
            EndpointUrl = provider.EndpointUrl,
            RequestJson = SafeJson(llmResult.RawRequest, effective.ApiKey)
        };
        _db.Set<RawRequest>().Add(rawRequest);

        var rawResponse = new RawResponse
        {
            TurnId = turn.Id,
            Provider = provider.ProviderName,
            ResponseJson = SafeJson(llmResult.RawResponse, effective.ApiKey),
            HttpStatusCode = llmResult.HttpStatus,
            LatencyMs = llmResult.LatencyMs,
            TokenUsageJson = llmResult.TokenUsage != null
                ? JsonSerializer.Serialize(llmResult.TokenUsage)
                : null
        };
        _db.Set<RawResponse>().Add(rawResponse);

        var assistantMessage = new Message
        {
            ChatId = chatId,
            Role = "assistant",
            Content = llmResult.AssistantText,
            TurnId = turn.Id,
            SequenceNum = assistantSequence
        };
        _db.Set<Message>().Add(assistantMessage);
        chat.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await LogAuditAsync("regenerate", "turn", turn.Id.ToString("D"), $"Regenerated assistant response in \"{chat.Title}\".", chat.ProjectSpaceId, chat.Id, new
        {
            sourceMessageId = sourceMessage.Id,
            provider = provider.ProviderName,
            llmResult.HttpStatus,
            llmResult.LatencyMs
        }, ct);

        return new SendMessageResult(turn, sourceMessage, assistantMessage, rawRequest, rawResponse);
    }

    private async Task LogAuditAsync(
        string eventType,
        string entityType,
        string entityId,
        string summary,
        Guid? projectId,
        Guid? chatId,
        object metadata,
        CancellationToken ct)
    {
        _db.Set<AuditLogEntry>().Add(new AuditLogEntry
        {
            ProjectSpaceId = projectId,
            ChatId = chatId,
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            Summary = summary,
            MetadataJson = SecretRedactor.RedactJson(JsonSerializer.Serialize(metadata)),
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task LogAgentEventAsync(
        AgentRun run,
        string eventType,
        string summary,
        object? data = null,
        Guid? stepId = null,
        Guid? toolInvocationId = null,
        string severity = AgentEventSeverities.Info,
        CancellationToken ct = default)
    {
        var sequenceNumber = await _db.Set<AgentEvent>()
            .Where(e => e.AgentRunId == run.Id)
            .Select(e => (int?)e.SequenceNumber)
            .MaxAsync(ct) ?? 0;
        var dataJson = data == null
            ? "{}"
            : SecretRedactor.RedactJson(JsonSerializer.Serialize(data));
        _db.Set<AgentEvent>().Add(new AgentEvent
        {
            AgentRunId = run.Id,
            AgentStepId = stepId,
            ToolInvocationId = toolInvocationId,
            SequenceNumber = sequenceNumber + 1,
            EventType = eventType,
            Severity = severity,
            Summary = SecretRedactor.RedactText(summary),
            DataJson = dataJson,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    private static MessagePayload ToProviderMessage(Message message)
    {
        var role = NormalizeProviderRole(message.Role);
        var content = string.Equals(role, message.Role, StringComparison.OrdinalIgnoreCase)
            ? message.Content
            : $"[{message.Role}]\n{message.Content}";
        return new MessagePayload(role, content);
    }

    private static string NormalizeProviderRole(string role)
    {
        var normalized = role.ToLowerInvariant();
        return normalized is "user" or "assistant" or "system"
            ? normalized
            : "user";
    }

    private static string BuildAgentSystemPrompt(string baseSystemPrompt, string sandboxRoot)
    {
        var builder = new StringBuilder();
        builder.AppendLine(baseSystemPrompt);
        builder.AppendLine();
        builder.AppendLine("TLAH Agent Mode is enabled.");
        builder.AppendLine("You may complete multi-step tasks with typed file, Git, HTTP, search, browser, terminal, and MCP tools.");
        builder.AppendLine($"Sandbox working directory: {sandboxRoot}");
        builder.AppendLine("Work only inside the sandbox directory. Never read host user files or attempt destructive, privileged, registry, service, shutdown, or system-configuration operations.");
        builder.AppendLine($"Prefer {AgentToolNames.FileList}, {AgentToolNames.FileRead}, {AgentToolNames.FileWrite}, and {AgentToolNames.FileSearch} over terminal commands for file work.");
        builder.AppendLine($"Use {AgentToolNames.TerminalExec} only when a typed tool cannot complete the task. Use {AgentToolNames.McpListTools} before {AgentToolNames.McpCall}.");
        builder.AppendLine("Network requests are limited to configured public-domain allowlists. Credentials can only be referenced by broker entry name and must never be requested, printed, or stored.");
        builder.AppendLine("Request one tool call at a time and provide a short reason argument.");
        builder.AppendLine("If the provider does not expose native tools, the compatibility fallback is this exact JSON object with no surrounding prose:");
        builder.AppendLine("""{"tlah_tool":"sandbox.exec","command":"Get-ChildItem","reason":"List files in the sandbox"}""");
        builder.AppendLine("After a tool result is returned, either request the next action or provide the final answer.");
        builder.AppendLine("Never include API keys, tokens, or secrets in commands or final output.");
        return builder.ToString();
    }

    private static string FormatToolRequestMessage(int step, AgentToolRequest request)
    {
        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? "No reason provided."
            : request.Reason.Trim();
        return $"""
        Sandbox command request #{step}
        Reason: {reason}

        ```powershell
        {request.Command}
        ```
        """;
    }

    private static string FormatToolRequestMessage(
        int step,
        LlmToolCall request,
        ToolSafetyAssessment? safety = null)
    {
        var reason = ReadToolReason(request.ArgumentsJson) ?? "No reason provided.";
        var details = request.ArgumentsJson;
        try
        {
            using var document = JsonDocument.Parse(request.ArgumentsJson);
            details = JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
        }

        var sb = new StringBuilder();
        sb.AppendLine($"""
        Agent tool request #{step}
        Tool: {request.Name}
        Reason: {reason}

        ```json
        {details}
        ```
        """);
        if (safety != null)
        {
            sb.AppendLine();
            sb.AppendLine($"Safety: {safety.Level} / {safety.Category}");
            sb.AppendLine($"Mode: {(safety.IsReadOnly ? "read-only" : safety.IsWriteOperation ? "write/action" : "unknown")}");
            sb.AppendLine(safety.Summary);
            if (!string.IsNullOrWhiteSpace(safety.Warning))
                sb.AppendLine($"Warning: {safety.Warning}");
            if (!string.IsNullOrWhiteSpace(safety.PreviewJson) &&
                safety.PreviewJson.Trim() != "{}")
            {
                sb.AppendLine();
                sb.AppendLine("Preview:");
                sb.AppendLine("```json");
                sb.AppendLine(safety.PreviewJson);
                sb.AppendLine("```");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatToolResultMessage(int step, SandboxCommandResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Sandbox result #{step}");
        sb.AppendLine($"Working directory: {result.WorkingDirectory}");
        sb.AppendLine($"Exit code: {result.ExitCode}");
        sb.AppendLine($"Duration: {result.Duration.TotalMilliseconds:0}ms");

        if (result.WasBlocked)
        {
            sb.AppendLine($"Blocked: {result.BlockedReason}");
            return sb.ToString();
        }

        if (result.TimedOut)
            sb.AppendLine("Timed out: true");

        sb.AppendLine("STDOUT:");
        sb.AppendLine(string.IsNullOrWhiteSpace(result.StandardOutput) ? "(empty)" : result.StandardOutput.TrimEnd());
        sb.AppendLine("STDERR:");
        sb.AppendLine(string.IsNullOrWhiteSpace(result.StandardError) ? "(empty)" : result.StandardError.TrimEnd());
        return sb.ToString();
    }

    private static string FormatToolResultMessage(
        int step,
        string toolName,
        AgentToolResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Agent tool result #{step}");
        sb.AppendLine($"Tool: {toolName}");
        sb.AppendLine($"Success: {result.Success}");
        if (!string.IsNullOrWhiteSpace(result.Output))
            sb.AppendLine(result.Output.TrimEnd());
        if (!string.IsNullOrWhiteSpace(result.Error))
            sb.AppendLine($"Error: {result.Error}");
        if (result.Artifacts is { Count: > 0 })
        {
            sb.AppendLine("Artifacts:");
            foreach (var artifact in result.Artifacts)
                sb.AppendLine($"- {artifact.RelativePath} ({artifact.SizeBytes} bytes)");
        }
        return sb.ToString();
    }

    private static void EnsureProviderReady(string apiKey, string baseUrl, string model)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("API key is not configured.");
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Base URL is not configured.");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Model is not configured.");
    }

    private static string SafeJson(object value, string apiKey)
    {
        var json = SecretRedactor.RedactObject(value, apiKey);
        if (SecretRedactor.ContainsSecret(json, apiKey))
            throw new InvalidOperationException("A secret was detected in a raw debug payload.");
        return json;
    }

    private static ReplayRequest ParseReplayRequest(string requestJson)
    {
        using var doc = JsonDocument.Parse(requestJson);
        var root = doc.RootElement;
        var model = ReadString(root, "model");
        var maxTokens = ReadInt(root, "max_tokens");
        var temperature = ReadDouble(root, "temperature");
        var systemPrompt = ReadString(root, "system") ?? string.Empty;
        var messages = new List<MessagePayload>();

        if (root.TryGetProperty("messages", out var messagesElement) &&
            messagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in messagesElement.EnumerateArray())
            {
                var role = ReadString(msg, "role") ?? "user";
                var content = msg.TryGetProperty("content", out var contentElement)
                    ? ReadContent(contentElement)
                    : string.Empty;

                if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(systemPrompt))
                {
                    systemPrompt = content;
                    continue;
                }

                messages.Add(ToProviderMessage(role, content));
            }
        }

        if (messages.Count == 0)
            throw new InvalidOperationException("The recorded request does not contain replayable messages.");

        return new ReplayRequest(model, systemPrompt, messages, temperature, maxTokens);
    }

    private static string ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        return content.GetRawText();
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? ReadDouble(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;

    private record ReplayRequest(
        string? Model,
        string SystemPrompt,
        List<MessagePayload> Messages,
        double? Temperature,
        int? MaxTokens);

    private sealed class AgentExecutionState
    {
        public AgentExecutionState(List<MessagePayload> messages, int sequenceNum)
        {
            Messages = messages;
            SequenceNum = sequenceNum;
        }

        public List<MessagePayload> Messages { get; set; }
        public int SequenceNum { get; set; }
    }

    private static MessagePayload ToProviderMessage(string role, string content)
    {
        var normalized = NormalizeProviderRole(role);
        var safeContent = string.Equals(normalized, role, StringComparison.OrdinalIgnoreCase)
            ? content
            : $"[{role}]\n{content}";
        return new MessagePayload(normalized, safeContent);
    }
}
