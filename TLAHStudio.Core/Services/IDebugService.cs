using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

/// <summary>
/// Debug service interface for retrieving raw request/response data.
/// Maps from services/debug_service.py.
/// </summary>
public interface IDebugService
{
    Task<RawRequest?> GetRawRequestAsync(Guid turnId, CancellationToken ct = default);
    Task<RawResponse?> GetRawResponseAsync(Guid turnId, CancellationToken ct = default);
    Task<DebugTurnBundle?> GetTurnBundleAsync(Guid turnId, CancellationToken ct = default);
    Task<IReadOnlyList<DebugTurnSummary>> ListTurnsAsync(Guid chatId, CancellationToken ct = default);
}

public record DebugTurnSummary(
    Guid TurnId,
    Guid ChatId,
    int TurnNumber,
    DateTime CreatedAt,
    string Provider,
    int HttpStatusCode,
    int LatencyMs,
    string TokenUsageJson,
    string FirstPromptPreview,
    string AssistantPreview);

public record DebugTurnBundle(
    Guid TurnId,
    Guid ChatId,
    int TurnNumber,
    DateTime CreatedAt,
    RawRequest? RawRequest,
    RawResponse? RawResponse,
    IReadOnlyList<Message> Messages);
