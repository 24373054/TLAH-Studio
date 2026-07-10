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
        // M4.9.0: Deep-copy SentSkillNames so resume doesn't share state.
        SentSkillNames = new HashSet<string>(SentSkillNames, SentSkillNames.Comparer)
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
    int CommandTimeoutSeconds = 20,
    int MaxCommandOutputChars = 12000,
    bool AutoApproveTools = false,
    int ContextBudgetTokens = 32_000,
    int AutoCompactTriggerTokens = 24_000,
    int MaxToolResultCharsInContext = 6_000,
    IProgress<LlmStreamUpdate>? OutputStream = null,
    IProgress<AgentProgressUpdate>? Progress = null,
    string PermissionMode = AgentPermissionModes.RequestApproval);
