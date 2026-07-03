using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services.SessionMemory;

/// <summary>
/// M4.5.0: Session memory configuration.
/// </summary>
public sealed record SessionMemoryConfig(
    int MinMessageTokensToInit = 10_000,
    int MaxSectionChars = 2_000,
    int MaxTotalChars = 12_000);

/// <summary>
/// M4.5.0: Persistent, cross-compaction session memory.
///
/// After each agent step, deterministic extraction writes a structured
/// markdown file ({sandbox}/.tlah_context/session-memory.md). When
/// compaction fires, the file content is read and injected into the
/// summary boundary so the model retains accumulated context across
/// multiple compaction cycles.
///
/// Unlike Claude Code's LLM-based approach, TLAH uses deterministic
/// extraction from message history and DB metadata — zero API cost.
/// </summary>
public interface ISessionMemoryService
{
    /// <summary>
    /// Fire-and-forget: extract current session state from messages
    /// and metadata, then atomically write the memory file.
    /// </summary>
    Task ExtractAsync(
        Guid chatId,
        Guid runId,
        IReadOnlyList<MessagePayload> messages,
        string sandboxRoot,
        IReadOnlyList<string> filesChanged,
        IReadOnlyList<string> commandsRun,
        IReadOnlyList<string> recentFailures,
        IReadOnlyList<string> openQuestions,
        IReadOnlyList<string> nextActions,
        CancellationToken ct);

    /// <summary>
    /// Read the session memory file for compaction injection.
    /// Returns null if the file doesn't exist or is empty.
    /// Sections exceeding MaxSectionChars are truncated.
    /// </summary>
    Task<string?> ReadForCompactAsync(string sandboxRoot, CancellationToken ct);

    /// <summary>Full path to the session memory file.</summary>
    string GetPath(string sandboxRoot);

    /// <summary>
    /// Wait for any in-progress extraction to complete (with timeout).
    /// Called before compaction to ensure the file is current.
    /// </summary>
    Task WaitForExtractionAsync(TimeSpan timeout, CancellationToken ct);
}

public sealed class SessionMemoryService : ISessionMemoryService
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime? _extractionStartedAt;

    private const string FileName = "session-memory.md";

    public string GetPath(string sandboxRoot) =>
        Path.Combine(sandboxRoot, ".tlah_context", FileName);

    public async Task ExtractAsync(
        Guid chatId,
        Guid runId,
        IReadOnlyList<MessagePayload> messages,
        string sandboxRoot,
        IReadOnlyList<string> filesChanged,
        IReadOnlyList<string> commandsRun,
        IReadOnlyList<string> recentFailures,
        IReadOnlyList<string> openQuestions,
        IReadOnlyList<string> nextActions,
        CancellationToken ct)
    {
        if (!await _lock.WaitAsync(0, ct))
            return; // extraction already in progress — skip

        _extractionStartedAt = DateTime.UtcNow;
        try
        {
            var content = BuildContent(messages, filesChanged, commandsRun,
                recentFailures, openQuestions, nextActions);

            var dir = Path.GetDirectoryName(GetPath(sandboxRoot))!;
            Directory.CreateDirectory(dir);

            // Atomic write: temp file + rename
            var tmp = GetPath(sandboxRoot) + ".tmp";
            await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false), ct);
            File.Move(tmp, GetPath(sandboxRoot), overwrite: true);
        }
        finally
        {
            _extractionStartedAt = null;
            _lock.Release();
        }
    }

    public async Task<string?> ReadForCompactAsync(string sandboxRoot, CancellationToken ct)
    {
        var path = GetPath(sandboxRoot);
        if (!File.Exists(path))
            return null;

        var content = await ReadAllTextSharedAsync(path, ct);
        if (string.IsNullOrWhiteSpace(content))
            return null;

        return TruncateSections(content);
    }

    public async Task WaitForExtractionAsync(TimeSpan timeout, CancellationToken ct)
    {
        var started = _extractionStartedAt;
        if (started == null)
            return;

        var age = DateTime.UtcNow - started.Value;
        if (age > StaleThreshold)
            return; // abandoned — don't wait

        try
        {
            await _lock.WaitAsync(timeout, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_lock.CurrentCount == 0)
            {
                try { _lock.Release(); } catch { }
            }
        }
    }

    // ── deterministic content builder ──────────────────────────

    private static string BuildContent(
        IReadOnlyList<MessagePayload> messages,
        IReadOnlyList<string> filesChanged,
        IReadOnlyList<string> commandsRun,
        IReadOnlyList<string> recentFailures,
        IReadOnlyList<string> openQuestions,
        IReadOnlyList<string> nextActions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Session Title");
        sb.AppendLine(TitleFromMessages(messages));
        sb.AppendLine();

        sb.AppendLine("# Current State");
        sb.AppendLine(string.Join('\n', nextActions.Take(8).Select(a => $"- {a}")));
        if (openQuestions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Open questions:");
            foreach (var q in openQuestions.Take(4))
                sb.AppendLine($"- {q}");
        }
        sb.AppendLine();

        sb.AppendLine("# Task Specification");
        var userMsgs = messages.Where(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(m.Content) &&
            !m.Content.StartsWith("[runtime context]", StringComparison.OrdinalIgnoreCase) &&
            !m.Content.StartsWith("[context summary boundary", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Content.Trim())
            .Distinct()
            .Take(10);
        foreach (var msg in userMsgs)
            sb.AppendLine($"- {Truncate(msg, 240)}");
        sb.AppendLine();

        sb.AppendLine("# Files and Functions");
        foreach (var f in filesChanged.Take(15))
            sb.AppendLine($"- {f}");
        if (filesChanged.Count == 0)
            sb.AppendLine("_(none yet)_");
        sb.AppendLine();

        sb.AppendLine("# Workflow");
        foreach (var c in commandsRun.Take(10))
            sb.AppendLine($"- `{Truncate(c, 200)}`");
        if (commandsRun.Count == 0)
            sb.AppendLine("_(none yet)_");
        sb.AppendLine();

        sb.AppendLine("# Errors & Corrections");
        foreach (var e in recentFailures.Take(8))
            sb.AppendLine($"- {Truncate(e, 200)}");
        if (recentFailures.Count == 0)
            sb.AppendLine("_(none)_");
        sb.AppendLine();

        sb.AppendLine("# Learnings");
        sb.AppendLine(ExtractLearnings(messages));
        sb.AppendLine();

        sb.AppendLine("# Key Results");
        sb.AppendLine("_(to be populated as results emerge)_");
        sb.AppendLine();

        sb.AppendLine("# Worklog");
        var steps = ExtractWorklog(messages);
        foreach (var s in steps.TakeLast(20))
            sb.AppendLine($"- {Truncate(s, 200)}");
        sb.AppendLine();

        sb.AppendLine("# Codebase and System Documentation");
        sb.AppendLine("_(to be populated as the codebase is explored)_");

        var result = sb.ToString();
        if (result.Length > 12_000)
            result = result[..12_000] + "\n\n[truncated — session memory exceeded 12K char budget]";
        return result;
    }

    private static string TitleFromMessages(IReadOnlyList<MessagePayload> messages)
    {
        var first = messages.FirstOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(m.Content) &&
            !m.Content.StartsWith("[runtime context]", StringComparison.OrdinalIgnoreCase));
        if (first == null) return "_(no user message yet)_";
        return Truncate(first.Content.Trim(), 80);
    }

    private static string ExtractLearnings(IReadOnlyList<MessagePayload> messages)
    {
        var corrections = messages
            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Where(m => ContainsAny(m.Content, "纠正", "不对", "改成", "不要", "改一下", "修正", "不是这样", "错了", "correct", "wrong", "instead", "don't"))
            .Select(m => $"- User correction: {Truncate(m.Content.Trim(), 200)}")
            .Distinct()
            .Take(6)
            .ToList();
        return corrections.Count > 0
            ? string.Join('\n', corrections)
            : "_(no explicit corrections yet)_";
    }

    private static List<string> ExtractWorklog(IReadOnlyList<MessagePayload> messages)
    {
        var log = new List<string>();
        foreach (var m in messages)
        {
            if (m.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in m.ToolCalls)
                {
                    var args = TryParseJsonPreview(tc.ArgumentsJson);
                    log.Add($"[assistant] → {tc.Name}{(args != null ? $" {args}" : "")}");
                }
            }
            else if (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(m.Content))
            {
                var preview = m.Content.Length > 100 ? m.Content[..100] + "..." : m.Content;
                log.Add($"[tool result] {preview.ReplaceLineEndings(" ")}");
            }
            else if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                     m.ToolCalls is null or { Count: 0 } &&
                     !string.IsNullOrWhiteSpace(m.Content))
            {
                var preview = m.Content.Length > 100 ? m.Content[..100] + "..." : m.Content;
                log.Add($"[assistant] {preview.ReplaceLineEndings(" ")}");
            }
        }
        return log;
    }

    private static string? TryParseJsonPreview(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // extract common tool argument keys for a short preview
            var keys = new[] { "command", "file_path", "path", "pattern", "operation", "query" };
            foreach (var key in keys)
            {
                if (root.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                    return $"{key}={Truncate(val.GetString() ?? "", 60)}";
            }
            return null;
        }
        catch { return null; }
    }

    // ── section truncation for compaction ──────────────────────

    private static string TruncateSections(string content)
    {
        var sb = new StringBuilder();
        var currentSection = new StringBuilder();
        var inSection = false;

        foreach (var line in content.Split('\n'))
        {
            if (line.StartsWith("# ") && line.Length > 2)
            {
                FlushSection(sb, currentSection);
                currentSection.Clear();
                inSection = true;
                sb.AppendLine(line);
            }
            else if (inSection)
            {
                currentSection.AppendLine(line);
            }
            else
            {
                sb.AppendLine(line);
            }
        }
        FlushSection(sb, currentSection);
        return sb.ToString().TrimEnd();
    }

    private static void FlushSection(StringBuilder output, StringBuilder section)
    {
        var text = section.ToString();
        if (text.Length > 2_000)
            text = text[..2_000] + $"\n_[section truncated — {text.Length} chars]_\n";
        output.Append(text);
    }

    // ── helpers ─────────────────────────────────────────────────

    private static string Truncate(string value, int maxChars) =>
        (value?.Length ?? 0) <= maxChars ? (value ?? "") : value![..maxChars] + "...";

    private static bool ContainsAny(string text, params string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ReadAllTextSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return await reader.ReadToEndAsync(ct);
    }
}
