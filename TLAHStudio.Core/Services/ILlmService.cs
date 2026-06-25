using TLAHStudio.Core.Models;

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
        CancellationToken ct = default);

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

    Task SetAgentToolApprovalAsync(
        Guid invocationId,
        bool approved,
        string policyScope = "once",
        CancellationToken ct = default);

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
    int MaxSteps = 6,
    int CommandTimeoutSeconds = 20,
    int MaxCommandOutputChars = 12000,
    bool AutoApproveTools = false);

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
    string Status);
