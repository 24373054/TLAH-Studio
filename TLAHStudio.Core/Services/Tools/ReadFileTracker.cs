using TLAHStudio.Core.Services.Plugins;

namespace TLAHStudio.Core.Services.Tools;

/// <summary>
/// M4.5.0: Tracks which files the agent has read during the current
/// session so write/edit tools can enforce read-before-write and
/// stale-modification detection.
///
/// Scoped per-chat-session; cleared when the chat switches.
/// </summary>
public interface IReadFileTracker
{
    /// <summary>Record that a file was read, storing its current mtime.</summary>
    void MarkRead(string path, DateTime mtimeUtc);

    /// <summary>Has this file been read in the current session?</summary>
    bool WasRead(string path);

    /// <summary>Get the mtime recorded when the file was last read.</summary>
    DateTime? GetLastReadMtimeUtc(string path);

    /// <summary>Clear all tracking state (e.g. on chat switch).</summary>
    void Clear();
}

public sealed class ReadFileTracker : IReadFileTracker
{
    private readonly Dictionary<string, DateTime> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISkillLoader? _skillLoader;

    public ReadFileTracker(ISkillLoader? skillLoader = null)
    {
        _skillLoader = skillLoader;
    }

    public void MarkRead(string path, DateTime mtimeUtc)
    {
        var normalized = NormalizePath(path);
        _files[normalized] = mtimeUtc;

        // M4.9.0: Activate conditional skills on first read of a matching file.
        if (_skillLoader != null)
            _ = _skillLoader.ActivateConditionalSkillsForPathAsync(path);
    }

    public bool WasRead(string path) =>
        _files.ContainsKey(NormalizePath(path));

    public DateTime? GetLastReadMtimeUtc(string path) =>
        _files.TryGetValue(NormalizePath(path), out var mtime) ? mtime : null;

    public void Clear() => _files.Clear();

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
