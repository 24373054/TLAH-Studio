namespace TLAHStudio.Core.Services.Workspace;

public sealed record WorkspaceRoot(string RootPath, bool IsConfigured, IReadOnlyList<string> AllowedRoots, IReadOnlyList<string> IgnoredPatterns);

public interface IWorkspaceRootService
{
    Task<WorkspaceRoot> GetRootAsync(Guid chatId, CancellationToken ct = default);
    Task SetRootAsync(Guid chatId, string rootPath, CancellationToken ct = default);
    Task ClearRootAsync(Guid chatId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAllowedRootsAsync(CancellationToken ct = default);
    Task AddAllowedRootAsync(string path, CancellationToken ct = default);
    bool ShouldIgnore(string relativePath, Guid? chatId = null);
}

public class WorkspaceRootService : IWorkspaceRootService
{
    private static readonly HashSet<string> DefaultIgnorePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", "packages", ".vs", ".idea",
        "__pycache__", ".venv", "venv", "dist", "build", ".next", ".nuget",
        "*.user", "*.suo", "*.cache", ".DS_Store", "Thumbs.db"
    };

    public Task<WorkspaceRoot> GetRootAsync(Guid chatId, CancellationToken ct = default)
    {
        var root = WorkspaceRootStore.GetRoot(chatId, out var isConfigured);
        return Task.FromResult(new WorkspaceRoot(root, isConfigured, WorkspaceRootStore.GetRecentRoots(), [.. DefaultIgnorePatterns]));
    }

    public Task SetRootAsync(Guid chatId, string rootPath, CancellationToken ct = default)
    {
        WorkspaceRootStore.SetRoot(chatId, rootPath);
        return Task.CompletedTask;
    }

    public Task ClearRootAsync(Guid chatId, CancellationToken ct = default)
    {
        WorkspaceRootStore.ClearRoot(chatId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetAllowedRootsAsync(CancellationToken ct = default)
    {
        var roots = WorkspaceRootStore.GetRecentRoots()
            .Concat([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(roots);
    }

    public Task AddAllowedRootAsync(string path, CancellationToken ct = default)
    {
        WorkspaceRootStore.AddRecentRoot(path);
        return Task.CompletedTask;
    }

    public bool ShouldIgnore(string relativePath, Guid? chatId = null)
    {
        var parts = relativePath.Split('/', '\\');
        return parts.Any(p => DefaultIgnorePatterns.Contains(p) ||
            DefaultIgnorePatterns.Any(pat => pat.StartsWith('*') && p.EndsWith(pat[1..], StringComparison.OrdinalIgnoreCase)));
    }
}
