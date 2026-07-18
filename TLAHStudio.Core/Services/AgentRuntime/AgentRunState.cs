using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services.Tools.Models;

namespace TLAHStudio.Core.Services.AgentRuntime;

/// <summary>
/// Complete runtime state for an in-progress agent run.
/// Replaces the private AgentExecutionState in LlmService.
/// </summary>
public sealed record AgentRunState
{
    public Guid RunId { get; init; }
    public Guid ChatId { get; init; }
    public Guid TurnId { get; init; }
    public string Status { get; set; } = AgentRunStatuses.Running;
    public int CurrentStep { get; set; }
    public int MaxSteps { get; set; } = 48;
    public string UserRequest { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public List<MessagePayload> Messages { get; set; } = [];
    public int SequenceNum { get; set; }
    public int TokenBudgetUsed { get; set; }
    public Guid? PendingToolInvocationId { get; set; }
    public int ContextErrorCount { get; set; }
    // M4.13.0: Durable recovery state. These counters deliberately live in the
    // checkpoint rather than local loop variables so approval/app restarts do
    // not reset loop detection or silently forget an unresolved failure.
    public int ConsecutiveToolFailures { get; set; }
    public int ConsecutiveProviderFailures { get; set; }
    public int RepeatedFailureCount { get; set; }
    public int CompletionRecoveryAttempts { get; set; }
    public int RecoveryAttempts { get; set; }
    public int ResumeCount { get; set; }
    public int BudgetExtensionCount { get; set; }
    public int SuccessfulToolCalls { get; set; }
    public int LastSuccessfulStep { get; set; } = -1;
    public string? LastFailedInvocationSignature { get; set; }
    public string? LastFailedToolName { get; set; }
    public string? LastFailureSummary { get; set; }
    public string? LastFailureCode { get; set; }
    public bool LastFailureRetryable { get; set; }
    public bool RecoveryDirectivePending { get; set; }
    // A failure is resolved only by a materially different action taken after
    // the corresponding recovery directive. Persist both sides of that proof
    // so an approval/app restart cannot accidentally turn unrelated success
    // into recovery.
    public string? RecoveryDirectiveIssuedForFailureSignature { get; set; }
    public string? LastRecoveryCandidateInvocationSignature { get; set; }
    // Approval-mode batches are intentionally reduced to one provider call so
    // every assistant tool_call remains paired with exactly one tool result.
    // Siblings stay durable here and are re-proposed to the model as user
    // context; they are never injected back as orphan provider tool calls.
    public List<LlmToolCall> DeferredToolCalls { get; set; } = [];
    public bool DeferredToolDirectivePending { get; set; }
    public int DeferredToolRecoveryAttempts { get; set; }
    public int InvalidToolCallRecoveryAttempts { get; set; }
    public int OpenTaskCompletionRecoveryAttempts { get; set; }
    // A tool may have produced side effects even if result/checkpoint
    // persistence failed. This state prevents automatic replay on resume.
    public Guid? UnknownOutcomeInvocationId { get; set; }
    public string? UnknownOutcomeSummary { get; set; }
    public int LastCompactedStep { get; set; } = -100;
    public int LastCompactedTokenEstimate { get; set; }
    public bool CompactionDisabled { get; set; }
    // M4.9.0: Plan mode — read-only exploration, write tools intercepted.
    public bool IsPlanMode { get; set; }
    public string? PrePlanMode { get; set; }
    // M4.9.8: Persist the active permission mode so a resumed run cannot lose
    // its Plan/approval transition after a checkpoint boundary.
    public string? EffectivePermissionMode { get; set; }
    public bool? EffectiveAutoApproveTools { get; set; }
    public bool? PrePlanAutoApproveTools { get; set; }
    // M4.9.0: Track sent skill names so they survive compaction/resume.
    public HashSet<string> SentSkillNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // M4.14.0: tool_search results are promoted into the next provider turn.
    // The set is checkpointed so approval, compaction, and app restart do not
    // silently unload a capability the model already discovered.
    public HashSet<string> LoadedDeferredToolNames { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Deep-clones the message list for safe mutation in the engine.
    /// </summary>
    public AgentRunState DeepClone() => this with
    {
        Messages = Messages.Select(m => m with
        {
            ToolCalls = m.ToolCalls?.Select(tc => tc with { }).ToList(),
            ToolCallId = m.ToolCallId
        }).ToList(),
        DeferredToolCalls = DeferredToolCalls.Select(tc => tc with { }).ToList(),
        // M4.9.0: Deep-copy SentSkillNames so resume doesn't share state.
        SentSkillNames = new HashSet<string>(SentSkillNames, SentSkillNames.Comparer),
        LoadedDeferredToolNames = new HashSet<string>(
            LoadedDeferredToolNames,
            LoadedDeferredToolNames.Comparer)
    };
}

/// <summary>
/// A single frame emitted by the engine during execution.
/// Consumed by the UI or SDK to render live progress.
/// </summary>
public sealed record AgentRunFrame(
    int StepNumber,
    string Kind,
    IReadOnlyList<AgentEvent> Events,
    object? Data = null)
{
    public static AgentRunFrame Empty(int step, string kind = "idle") =>
        new(step, kind, Array.Empty<AgentEvent>());
}

public static class AgentRunFrameKinds
{
    public const string ModelRequest = "model_request";
    public const string ModelResponse = "model_response";
    public const string ModelStreamStarted = "model_stream_started";
    public const string ThinkingDelta = "thinking_delta";
    public const string TextDelta = "text_delta";
    public const string ToolCallDelta = "tool_call_delta";
    public const string ToolBatchPlanned = "tool_batch_planned";
    public const string ToolExecuting = "tool_executing";
    public const string ToolProgress = "tool_progress";
    public const string ToolResult = "tool_result";
    public const string ApprovalNeeded = "approval_needed";
    public const string Error = "error";
    public const string Completed = "completed";
    public const string Paused = "paused";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Result of a model turn within the agent loop.
/// </summary>
public sealed record AgentModelTurn(
    LlmResponse Response,
    IReadOnlyList<LlmToolCall> ToolCalls,
    StreamingMetricsSnapshot? StreamingMetrics,
    bool WasCompacted);

/// <summary>
/// Batch of tool calls to execute (concurrent or sequential).
/// </summary>
public sealed record AgentToolBatch(
    IReadOnlyList<ToolBatchItem> Items,
    bool Concurrent);

public sealed record ToolBatchItem(
    LlmToolCall ToolCall,
    IAgentTool Tool,
    ToolInvocation Invocation,
    ToolSafetyAssessment Safety,
    ToolEffectPlan? EffectPlan = null);

/// <summary>
/// Streaming metrics captured during a model call.
/// </summary>
public sealed record StreamingMetricsSnapshot(
    int TokensReceived,
    int TokensPerSecond,
    long FirstByteLatencyMs,
    long DurationMs,
    bool WasStreaming);

/// <summary>
/// Options for the agent run engine.
/// </summary>
public sealed record AgentEngineOptions(
    int MaxSteps = 48,
    int CommandTimeoutSeconds = 120,
    int MaxCommandOutputChars = 12000,
    bool AutoApproveTools = false,
    int ContextBudgetTokens = 32_000,
    int AutoCompactTriggerTokens = 24_000,
    int MaxToolResultCharsInContext = 6_000,
    IProgress<LlmStreamUpdate>? OutputStream = null,
    IProgress<AgentProgressUpdate>? Progress = null,
    string PermissionMode = AgentPermissionModes.RequestApproval);
