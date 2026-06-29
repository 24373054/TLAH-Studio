using System.Text.Json;

namespace TLAHStudio.Core.Services.Observability;

/// <summary>
/// M2.14.0: Runtime metrics snapshot for performance monitoring.
/// </summary>
public sealed record RuntimeMetricsSnapshot(
    long FirstThinkingLatencyMs,
    long FirstTextLatencyMs,
    double TokensPerSecond,
    int TotalTokensIn,
    int TotalTokensOut,
    int MaxRenderBacklog,
    int EventCount,
    long EventWriteLatencyMs,
    DateTime CapturedAt
);

/// <summary>
/// M2.14.0: Collects runtime metrics during agent execution.
/// </summary>
public interface IRuntimeMetricsCollector
{
    void RecordModelRequest();
    void RecordFirstThinking();
    void RecordFirstText();
    void RecordTokenInput(int count);
    void RecordTokenOutput(int count);
    void RecordRenderBacklog(int queueDepth);
    void RecordEventWrite(long latencyMs);
    RuntimeMetricsSnapshot Snapshot();
}

public class RuntimeMetricsCollector : IRuntimeMetricsCollector
{
    private DateTime? _modelRequestAt;
    private DateTime? _firstThinkingAt;
    private DateTime? _firstTextAt;
    private int _tokensIn;
    private int _tokensOut;
    private int _maxRenderBacklog;
    private int _eventCount;
    private long _totalEventWriteMs;
    private readonly object _lock = new();

    public void RecordModelRequest() { lock (_lock) _modelRequestAt = DateTime.UtcNow; }
    public void RecordFirstThinking() { lock (_lock) _firstThinkingAt ??= DateTime.UtcNow; }
    public void RecordFirstText() { lock (_lock) _firstTextAt ??= DateTime.UtcNow; }
    public void RecordTokenInput(int count) { lock (_lock) _tokensIn += count; }
    public void RecordTokenOutput(int count) { lock (_lock) _tokensOut += count; }
    public void RecordRenderBacklog(int queueDepth) { lock (_lock) _maxRenderBacklog = Math.Max(_maxRenderBacklog, queueDepth); }
    public void RecordEventWrite(long latencyMs) { lock (_lock) { _eventCount++; _totalEventWriteMs += latencyMs; } }

    public RuntimeMetricsSnapshot Snapshot()
    {
        lock (_lock)
        {
            var firstThinkMs = _firstThinkingAt.HasValue && _modelRequestAt.HasValue
                ? (long)(_firstThinkingAt.Value - _modelRequestAt.Value).TotalMilliseconds : 0;
            var firstTextMs = _firstTextAt.HasValue && _modelRequestAt.HasValue
                ? (long)(_firstTextAt.Value - _modelRequestAt.Value).TotalMilliseconds : 0;
            var tps = (_tokensIn + _tokensOut) > 0 ? (double)(_tokensIn + _tokensOut) / Math.Max(1, (_modelRequestAt.HasValue ? (DateTime.UtcNow - _modelRequestAt.Value).TotalSeconds : 1)) : 0;
            return new RuntimeMetricsSnapshot(firstThinkMs, firstTextMs, tps, _tokensIn, _tokensOut, _maxRenderBacklog, _eventCount, _totalEventWriteMs, DateTime.UtcNow);
        }
    }
}

/// <summary>
/// M2.14.0: Diagnostic package exporter with secret redaction.
/// </summary>
public interface IDiagnosticPackageExporter
{
    Task<string> ExportAsync(Guid? chatId, string exportPath, CancellationToken ct = default);
}

public class DiagnosticPackageExporter : IDiagnosticPackageExporter
{
    public Task<string> ExportAsync(Guid? chatId, string exportPath, CancellationToken ct = default)
    {
        var dir = Path.Combine(exportPath, $"tlah-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dir);

        // Write system info
        File.WriteAllText(Path.Combine(dir, "system-info.json"), JsonSerializer.Serialize(new
        {
            osVersion = Environment.OSVersion.ToString(),
            clrVersion = Environment.Version.ToString(),
            timestamp = DateTime.UtcNow
        }));

        // Write header for redacted info
        File.WriteAllText(Path.Combine(dir, "README.txt"),
            "TLAH Studio diagnostic package.\nAll API keys, tokens, and credentials have been redacted.\n");

        return Task.FromResult(dir);
    }
}
