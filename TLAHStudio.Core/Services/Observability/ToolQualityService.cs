using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services.Observability;

public sealed record ToolQualityRow(
    string ToolName,
    int Calls,
    int Completed,
    int Failed,
    int Denied,
    int Unknown,
    double SuccessRate,
    double AverageDurationMs)
{
    public string SuccessRateText => $"{SuccessRate:0.0}%";
    public string AverageDurationText => $"{AverageDurationMs:0} ms";
}

public sealed record ToolQualitySnapshot(
    DateTime FromUtc,
    DateTime ToUtc,
    int TotalCalls,
    int Completed,
    int Failed,
    int Denied,
    int Unknown,
    double SuccessRate,
    double ShellFallbackRate,
    double ToolSearchRate,
    IReadOnlyList<ToolQualityRow> Tools);

public interface IToolQualityService
{
    Task<ToolQualitySnapshot> LoadAsync(int days = 30, CancellationToken ct = default);
}

/// <summary>
/// Computes local, content-free tool quality metrics. Arguments, results,
/// prompts, paths, and message text are deliberately never selected.
/// </summary>
public sealed class ToolQualityService(DbContext db) : IToolQualityService
{
    public async Task<ToolQualitySnapshot> LoadAsync(
        int days = 30,
        CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650);
        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc.AddDays(-days);

        var rows = await db.Set<ToolInvocation>()
            .AsNoTracking()
            .Where(invocation => invocation.CreatedAt >= fromUtc)
            .Select(invocation => new InvocationMetric(
                invocation.ToolName,
                invocation.Status,
                invocation.StartedAt,
                invocation.CompletedAt))
            .ToListAsync(ct);

        var toolRows = rows
            .GroupBy(row => row.ToolName, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildRow(group.Key, group))
            .OrderByDescending(row => row.Calls)
            .ThenBy(row => row.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var completed = rows.Count(IsCompleted);
        var failed = rows.Count(IsFailed);
        var denied = rows.Count(IsDenied);
        var unknown = rows.Count(IsUnknown);
        var terminal = completed + failed + unknown;
        var total = rows.Count;
        var shellCalls = rows.Count(row =>
            string.Equals(row.ToolName, AgentToolNames.TerminalExec, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.ToolName, AgentToolNames.SandboxExec, StringComparison.OrdinalIgnoreCase));
        var searchCalls = rows.Count(row =>
            string.Equals(row.ToolName, AgentToolNames.ToolSearch, StringComparison.OrdinalIgnoreCase));

        return new ToolQualitySnapshot(
            fromUtc,
            toUtc,
            total,
            completed,
            failed,
            denied,
            unknown,
            Rate(completed, terminal),
            Rate(shellCalls, total),
            Rate(searchCalls, total),
            toolRows);
    }

    private static ToolQualityRow BuildRow(
        string toolName,
        IEnumerable<InvocationMetric> metrics)
    {
        var rows = metrics.ToArray();
        var completed = rows.Count(IsCompleted);
        var failed = rows.Count(IsFailed);
        var denied = rows.Count(IsDenied);
        var unknown = rows.Count(IsUnknown);
        var terminal = completed + failed + unknown;
        var durations = rows
            .Where(row => row.StartedAt.HasValue && row.CompletedAt.HasValue)
            .Select(row => Math.Max(
                0,
                (row.CompletedAt!.Value - row.StartedAt!.Value).TotalMilliseconds))
            .ToArray();

        return new ToolQualityRow(
            toolName,
            rows.Length,
            completed,
            failed,
            denied,
            unknown,
            Rate(completed, terminal),
            durations.Length == 0 ? 0 : durations.Average());
    }

    private static bool IsCompleted(InvocationMetric row) =>
        string.Equals(row.Status, ToolInvocationStatuses.Completed, StringComparison.OrdinalIgnoreCase);

    private static bool IsFailed(InvocationMetric row) =>
        string.Equals(row.Status, ToolInvocationStatuses.Failed, StringComparison.OrdinalIgnoreCase);

    private static bool IsDenied(InvocationMetric row) =>
        string.Equals(row.Status, ToolInvocationStatuses.Denied, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnknown(InvocationMetric row) =>
        string.Equals(row.Status, ToolInvocationStatuses.UnknownOutcome, StringComparison.OrdinalIgnoreCase);

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0 : Math.Round(numerator * 100d / denominator, 1);

    private sealed record InvocationMetric(
        string ToolName,
        string Status,
        DateTime? StartedAt,
        DateTime? CompletedAt);
}
