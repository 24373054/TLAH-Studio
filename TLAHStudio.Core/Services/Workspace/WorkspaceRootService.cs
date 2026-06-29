namespace TLAHStudio.Core.Services.Workspace;

public sealed record WorkspaceRoot(string RootPath, bool IsConfigured, IReadOnlyList<string> AllowedRoots, IReadOnlyList<string> IgnoredPatterns);

public interface IWorkspaceRootService
{
    Task<WorkspaceRoot> GetRootAsync(Guid chatId, CancellationToken ct = default);
    Task SetRootAsync(Guid chatId, string rootPath, CancellationToken ct = default);
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
        var sandboxRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio", "sandbox", chatId.ToString("N"));
        return Task.FromResult(new WorkspaceRoot(sandboxRoot, Directory.Exists(sandboxRoot), [], [.. DefaultIgnorePatterns]));
    }

    public Task SetRootAsync(Guid chatId, string rootPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(rootPath);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetAllowedRootsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]);

    public Task AddAllowedRootAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public bool ShouldIgnore(string relativePath, Guid? chatId = null)
    {
        var parts = relativePath.Split('/', '\\');
        return parts.Any(p => DefaultIgnorePatterns.Contains(p) ||
            DefaultIgnorePatterns.Any(pat => pat.StartsWith('*') && p.EndsWith(pat[1..], StringComparison.OrdinalIgnoreCase)));
    }
}
