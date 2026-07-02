using System.Text.Json;

namespace TLAHStudio.Core.Services.Workspace;

internal static class WorkspaceRootStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLAH Studio",
        "config",
        "workspace-roots.json");

    public static string DefaultSandboxBase => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLAH Studio",
        "sandboxes");

    public static string GetDefaultSandboxRoot(Guid chatId)
    {
        var path = Path.Combine(DefaultSandboxBase, chatId.ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static string GetRoot(Guid chatId, out bool isConfigured)
    {
        lock (Gate)
        {
            var state = ReadState();
            if (state.ChatRoots.TryGetValue(chatId.ToString("N"), out var configured) &&
                !string.IsNullOrWhiteSpace(configured))
            {
                var path = Normalize(configured);
                Directory.CreateDirectory(path);
                isConfigured = true;
                return path;
            }
        }

        isConfigured = false;
        return GetDefaultSandboxRoot(chatId);
    }

    public static void SetRoot(Guid chatId, string rootPath)
    {
        var normalized = Normalize(rootPath);
        Directory.CreateDirectory(normalized);

        lock (Gate)
        {
            var state = ReadState();
            state.ChatRoots[chatId.ToString("N")] = normalized;
            AddRecentRoot(state, normalized);
            WriteState(state);
        }
    }

    public static void ClearRoot(Guid chatId)
    {
        lock (Gate)
        {
            var state = ReadState();
            state.ChatRoots.Remove(chatId.ToString("N"));
            WriteState(state);
        }
    }

    public static IReadOnlyList<string> GetRecentRoots()
    {
        lock (Gate)
        {
            var state = ReadState();
            return state.RecentRoots
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public static void AddRecentRoot(string path)
    {
        var normalized = Normalize(path);
        Directory.CreateDirectory(normalized);
        lock (Gate)
        {
            var state = ReadState();
            AddRecentRoot(state, normalized);
            WriteState(state);
        }
    }

    private static void AddRecentRoot(WorkspaceRootState state, string root)
    {
        state.RecentRoots.RemoveAll(p => string.Equals(Normalize(p), root, StringComparison.OrdinalIgnoreCase));
        state.RecentRoots.Insert(0, root);
        if (state.RecentRoots.Count > 20)
            state.RecentRoots.RemoveRange(20, state.RecentRoots.Count - 20);
    }

    private static WorkspaceRootState ReadState()
    {
        try
        {
            if (!File.Exists(StorePath))
                return new WorkspaceRootState();
            var json = File.ReadAllText(StorePath);
            return JsonSerializer.Deserialize<WorkspaceRootState>(json) ?? new WorkspaceRootState();
        }
        catch
        {
            return new WorkspaceRootState();
        }
    }

    private static void WriteState(WorkspaceRootState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class WorkspaceRootState
    {
        public Dictionary<string, string> ChatRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> RecentRoots { get; set; } = [];
    }
}
