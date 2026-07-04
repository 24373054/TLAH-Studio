using TLAHStudio.Core.Models;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services;

/// <summary>
/// LLM service interface — handles the send-message orchestration.
/// Maps from services/llm_service.py.
/// </summary>
public interface ILlmService
{
    /// <summary>
    /// THE CORE ORCHESTRATION FUNCTION. Executes the full send-message flow:
    /// 1. Load chat + history + effective settings + agent file
    /// 2. Create Turn record
    /// 3. Save the outgoing message
    /// 4. Build raw request + call LLM via raw HTTP
    /// 5. Store RawRequest + RawResponse
    /// 6. Save assistant message
    /// 7. Commit and return result
    /// </summary>
    Task<SendMessageResult> SendMessageAsync(
        Guid chatId,
        string userContent,
        string? role = null,
        CancellationToken ct = default,
        IProgress<LlmStreamUpdate>? stream = null);

    Task<SendMessageResult> RunAgentTaskAsync(
        Guid chatId,
        string userContent,
        string? role = null,
        AgentRunOptions? options = null,
        CancellationToken ct = default);

    Task<SendMessageResult> ResumeAgentTaskAsync(
        Guid agentRunId,
        AgentRunOptions? options = null,
        CancellationToken ct = default);

    Task<AgentRunSnapshot?> GetLatestAgentRunAsync(
        Guid chatId,
        CancellationToken ct = default);

    Task<IReadOnlyList<AgentActivityRunSnapshot>> GetAgentActivityAsync(
        Guid chatId,
        CancellationToken ct = default);

    Task<ContextUsageSnapshot> GetContextUsageAsync(
        Guid chatId,
        CancellationToken ct = default);

    Task SetAgentToolApprovalAsync(
        Guid invocationId,
        bool approved,
        string policyScope = "once",
        CancellationToken ct = default,
        string? updatedArgumentsJson = null);  // M4.9.0

    Task CancelAgentRunAsync(
        Guid agentRunId,
        CancellationToken ct = default);

    Task<SendMessageResult> RegenerateAssistantAsync(
        Guid assistantMessageId,
        CancellationToken ct = default);

    Task<SendMessageResult> EditAndResendAsync(
        Guid messageId,
        string content,
        CancellationToken ct = default);

    Task<SendMessageResult> ContinueFromMessageAsync(
        Guid messageId,
        CancellationToken ct = default);

    Task<SendMessageResult> ReplayTurnAsync(
        Guid turnId,
        CancellationToken ct = default);

    Task<ConnectionTestResult> TestConnectionAsync(
        string provider,
        string apiKey,
        string baseUrl,
        string model,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListModelsAsync(
        string provider,
        string apiKey,
        string baseUrl,
        CancellationToken ct = default);
}

public record SendMessageResult(
    Turn Turn,
    Message UserMessage,
    Message AssistantMessage,
    RawRequest RawRequest,
    RawResponse RawResponse,
    AgentRunSnapshot? AgentRun = null
);

public record ConnectionTestResult(bool Success, string Message, int? StatusCode = null, int? LatencyMs = null);

public sealed record AgentRunOptions(
    int MaxSteps = 48,
    int CommandTimeoutSeconds = 20,
    int MaxCommandOutputChars = 12000,
    bool AutoApproveTools = false,
    IProgress<LlmStreamUpdate>? OutputStream = null,
    IProgress<AgentProgressUpdate>? Progress = null,
    int ContextBudgetTokens = 32_000,
    int AutoCompactTriggerTokens = 24_000,
    int MaxToolResultCharsInContext = 6_000,
    string PermissionMode = AgentPermissionModes.BypassPermissions);

public sealed record ContextUsageSnapshot(
    int TotalTokens,
    int AvailableTokens,
    double PercentUsed,
    int ConversationTokens,
    int ToolsTokens,
    int McpTokens,
    int ExecutionResultTokens,
    int FilesTokens,
    string Provider,
    string Model);

public sealed record AgentProgressUpdate(
    Guid AgentRunId,
    int SequenceNumber,
    string EventType,
    string Severity,
    string Summary,
    DateTime CreatedAt,
    AgentRunSnapshot Run,
    Guid? AgentStepId = null,
    Guid? ToolInvocationId = null,
    string DataJson = "{}");

public sealed record AgentRunSnapshot(
    Guid Id,
    Guid ChatId,
    Guid TurnId,
    string Status,
    int CurrentStep,
    int MaxSteps,
    string? ErrorMessage,
    int ArtifactCount,
    ToolInvocationSnapshot? PendingApproval);

public sealed record ToolInvocationSnapshot(
    Guid Id,
    string ToolName,
    string ArgumentsJson,
    string Status,
    string SafetyLevel = "unknown",
    string SafetySummary = "",
    string SafetyJson = "{}",
    string? SafetyWarning = null);

public sealed record AgentActivityRunSnapshot(
    Guid Id,
    Guid ChatId,
    Guid TurnId,
    string Status,
    string UserRequest,
    int CurrentStep,
    int MaxSteps,
    string? ErrorMessage,
    int ArtifactCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    IReadOnlyList<AgentActivityEventSnapshot> Events,
    IReadOnlyList<AgentTaskSnapshot>? Tasks = null);

public sealed record AgentActivityEventSnapshot(
    Guid Id,
    Guid AgentRunId,
    Guid? AgentStepId,
    Guid? ToolInvocationId,
    int SequenceNumber,
    string EventType,
    string Severity,
    string Summary,
    string DataJson,
    DateTime CreatedAt);
