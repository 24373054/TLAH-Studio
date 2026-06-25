namespace TLAHStudio.Core.Services;

public interface IPrivacyService
{
    Task<PrivacySummary> GetSummaryAsync(CancellationToken ct = default);
    Task<string> ExportAllDataAsync(string targetPath, CancellationToken ct = default);
    Task ImportAllDataAsync(string sourcePath, CancellationToken ct = default);
    Task ClearAllDataAsync(CancellationToken ct = default);
}

public record PrivacySummary(
    int ChatCount,
    int MessageCount,
    int TurnCount,
    int RawRequestCount,
    int RawResponseCount,
    long DatabaseSizeBytes,
    string DatabasePath,
    string ConfigDirectory,
    DateTime CheckedAtUtc)
{
    public string DatabaseSizeText => DatabaseSizeBytes < 1024 * 1024
        ? $"{DatabaseSizeBytes / 1024d:0.0} KB"
        : $"{DatabaseSizeBytes / 1024d / 1024d:0.0} MB";
}
