namespace TLAHStudio.Core.Services.Research;

/// <summary>
/// Reusable research capability for both the WinUI workbench and agent tools.
/// It performs retrieval and evidence packaging; callers remain responsible
/// for drawing conclusions from the cited evidence.
/// </summary>
public interface IResearchWorkbenchService
{
    Task<ResearchWorkbenchResult> ResearchAsync(
        string query,
        ResearchMode mode,
        ResearchFilters? filters,
        ResearchWorkspace? workspace,
        CancellationToken ct = default);

    Task<ResearchSearchResult> SearchAsync(
        string query,
        ResearchMode mode,
        ResearchFilters? filters,
        ResearchWorkspace? workspace,
        CancellationToken ct = default);

    Task<ResearchPage> ReadPageAsync(
        string url,
        string? relevanceQuery,
        ResearchWorkspace? workspace,
        CancellationToken ct = default);
}
