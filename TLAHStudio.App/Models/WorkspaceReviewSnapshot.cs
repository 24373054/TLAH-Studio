namespace TLAHStudio.App.Models;

public sealed record WorkspaceChange(string Status, string Path, bool IsStaged)
{
    public string StatusLabel => IsStaged ? $"● {Status}" : Status;
}

public sealed record WorkspaceReviewSnapshot(
    string WorkspaceName,
    string Summary,
    IReadOnlyList<WorkspaceChange> Changes,
    IReadOnlyDictionary<string, string> DiffByPath,
    string? Error = null)
{
    public static WorkspaceReviewSnapshot Unavailable(string summary, string? error = null) =>
        new("Workspace", summary, Array.Empty<WorkspaceChange>(), new Dictionary<string, string>(), error);
}
