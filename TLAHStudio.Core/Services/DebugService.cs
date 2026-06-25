using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

/// <summary>
/// Debug service for retrieving raw LLM request/response data.
/// Maps 1:1 from services/debug_service.py.
/// </summary>
public class DebugService : IDebugService
{
    private readonly DbContext _db;

    public DebugService(DbContext db)
    {
        _db = db;
    }

    public async Task<RawRequest?> GetRawRequestAsync(Guid turnId, CancellationToken ct = default)
    {
        return await _db.Set<RawRequest>()
            .FirstOrDefaultAsync(r => r.TurnId == turnId, ct);
    }

    public async Task<RawResponse?> GetRawResponseAsync(Guid turnId, CancellationToken ct = default)
    {
        return await _db.Set<RawResponse>()
            .FirstOrDefaultAsync(r => r.TurnId == turnId, ct);
    }

    public async Task<DebugTurnBundle?> GetTurnBundleAsync(Guid turnId, CancellationToken ct = default)
    {
        var turn = await _db.Set<Turn>()
            .Include(t => t.RawRequest)
            .Include(t => t.RawResponse)
            .FirstOrDefaultAsync(t => t.Id == turnId, ct);
        if (turn == null)
            return null;

        var messages = await _db.Set<Message>()
            .Where(m => m.TurnId == turnId)
            .OrderBy(m => m.SequenceNum)
            .AsNoTracking()
            .ToListAsync(ct);

        return new DebugTurnBundle(
            turn.Id,
            turn.ChatId,
            turn.TurnNumber,
            turn.CreatedAt,
            turn.RawRequest,
            turn.RawResponse,
            messages);
    }

    public async Task<IReadOnlyList<DebugTurnSummary>> ListTurnsAsync(Guid chatId, CancellationToken ct = default)
    {
        var turns = await _db.Set<Turn>()
            .Where(t => t.ChatId == chatId)
            .Include(t => t.RawRequest)
            .Include(t => t.RawResponse)
            .OrderBy(t => t.TurnNumber)
            .AsNoTracking()
            .ToListAsync(ct);

        var turnIds = turns.Select(t => t.Id).ToArray();
        var messages = await _db.Set<Message>()
            .Where(m => m.TurnId != null && turnIds.Contains(m.TurnId.Value))
            .OrderBy(m => m.SequenceNum)
            .AsNoTracking()
            .ToListAsync(ct);
        var byTurn = messages
            .Where(m => m.TurnId != null)
            .GroupBy(m => m.TurnId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        return turns.Select(t =>
        {
            byTurn.TryGetValue(t.Id, out var turnMessages);
            var prompt = turnMessages?.FirstOrDefault(m => !string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
            var assistant = turnMessages?.FirstOrDefault(m => string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase));
            return new DebugTurnSummary(
                t.Id,
                t.ChatId,
                t.TurnNumber,
                t.CreatedAt,
                t.RawRequest?.Provider ?? string.Empty,
                t.RawResponse?.HttpStatusCode ?? 0,
                t.RawResponse?.LatencyMs ?? 0,
                t.RawResponse?.TokenUsageJson ?? string.Empty,
                Preview(prompt?.Content),
                Preview(assistant?.Content));
        }).ToList();
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var normalized = text.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
    }
}
