using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

public sealed record AgentContextOptions(
    int ContextBudgetTokens = 32_000,
    int AutoCompactTriggerTokens = 24_000,
    int PreserveHeadMessages = 4,
    int PreserveTailMessages = 14,
    int MaxToolResultCharsInContext = 6_000);

public sealed record AgentContextPreparationResult(
    List<MessagePayload> Messages,
    bool WasCompacted,
    int EstimatedTokensBefore,
    int EstimatedTokensAfter,
    string Summary);

public interface IAgentContextManager
{
    AgentContextPreparationResult Prepare(
        IReadOnlyList<MessagePayload> messages,
        AgentContextOptions options,
        bool forceCompact = false);

    bool IsContextLimitError(LlmResponse response);
}

public sealed class AgentContextManager : IAgentContextManager
{
    public AgentContextPreparationResult Prepare(
        IReadOnlyList<MessagePayload> messages,
        AgentContextOptions options,
        bool forceCompact = false)
    {
        var before = EstimateTokens(messages);
        var trigger = Math.Min(options.ContextBudgetTokens, options.AutoCompactTriggerTokens);
        if (!forceCompact && before <= trigger)
        {
            return new AgentContextPreparationResult(
                messages.ToList(),
                WasCompacted: false,
                before,
                before,
                "Context is within the token budget.");
        }

        if (messages.Count <= options.PreserveHeadMessages + options.PreserveTailMessages + 1)
        {
            var trimmed = TrimLargeToolResults(messages, options.MaxToolResultCharsInContext);
            var afterTrim = EstimateTokens(trimmed);
            return new AgentContextPreparationResult(
                trimmed,
                WasCompacted: afterTrim < before,
                before,
                afterTrim,
                "Large tool outputs were trimmed; message count was too small for summary compaction.");
        }

        var head = messages.Take(options.PreserveHeadMessages).ToList();
        var tail = messages.TakeLast(options.PreserveTailMessages).ToList();
        var middle = messages
            .Skip(options.PreserveHeadMessages)
            .Take(messages.Count - options.PreserveHeadMessages - options.PreserveTailMessages)
            .ToList();
        var summary = BuildDeterministicSummary(middle);
        var compacted = new List<MessagePayload>(head.Count + tail.Count + 1);
        compacted.AddRange(TrimLargeToolResults(head, options.MaxToolResultCharsInContext));
        compacted.Add(new MessagePayload(
            "user",
            $"""
            [context summary boundary]
            The following summary replaces {middle.Count} older messages so the long-running task can continue without exceeding the context window.
            {summary}
            [/context summary boundary]
            """));
        compacted.AddRange(TrimLargeToolResults(tail, options.MaxToolResultCharsInContext));

        var after = EstimateTokens(compacted);
        return new AgentContextPreparationResult(
            compacted,
            WasCompacted: true,
            before,
            after,
            $"Compacted {middle.Count} middle messages from about {before} to {after} tokens.");
    }

    public bool IsContextLimitError(LlmResponse response)
    {
        // M4.4.1: Only inspect the provider error field when the HTTP status
        // indicates a client error. The old code concatenated AssistantText and
        // the serialized RawResponse, which caused false positives whenever the
        // agent's natural conversation mentioned "context", "token", or "length".
        if (response.HttpStatus is >= 200 and < 400)
            return false;
        if (string.IsNullOrWhiteSpace(response.Error))
            return false;

        var error = response.Error;
        return error.Contains("context", StringComparison.OrdinalIgnoreCase) &&
               (error.Contains("length", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("window", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("too long", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("maximum", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("reduce", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("exceed", StringComparison.OrdinalIgnoreCase));
    }

    private static int EstimateTokens(IEnumerable<MessagePayload> messages) =>
        Math.Max(1, messages.Sum(m =>
            EstimateTokens(m.Role) +
            EstimateTokens(m.Content) +
            EstimateTokens(m.ReasoningContent ?? string.Empty) +
            (m.ToolCalls?.Sum(t => EstimateTokens(t.Name) + EstimateTokens(t.ArgumentsJson)) ?? 0)));

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        // M4.4.0: CJK-aware estimation matching TokenBudgetService logic
        // but with the legacy 3.6 chars/token ratio for non-CJK text.
        int cjk = 0;
        foreach (char c in text)
        {
            if (c >= '⺀' && c <= '鿿' ||   // CJK Radicals → Ideographs
                c >= '가' && c <= '힯' ||    // Hangul Syllables
                c >= '豈' && c <= '﫿' ||    // CJK Compatibility Ideographs
                c >= '＀' && c <= '￯' ||    // Fullwidth Forms
                c >= '　' && c <= 'ヿ')      // CJK Symbols + Kana
                cjk++;
        }
        int nonCjk = text.Length - cjk;
        return Math.Max(1, (int)Math.Ceiling(cjk * 1.5 + nonCjk / 3.6));
    }

    private static List<MessagePayload> TrimLargeToolResults(
        IEnumerable<MessagePayload> messages,
        int maxToolResultChars)
    {
        return messages.Select(message =>
        {
            if (!string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) ||
                message.Content.Length <= maxToolResultChars)
            {
                return message;
            }

            return message with
            {
                Content = message.Content[..maxToolResultChars] +
                          "\n[tool result preview truncated for context budget]"
            };
        }).ToList();
    }

    private static string BuildDeterministicSummary(IReadOnlyList<MessagePayload> messages)
    {
        var sb = new StringBuilder();
        var roleCounts = messages
            .GroupBy(m => m.Role, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key}: {g.Count()}")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        sb.AppendLine($"Message role counts: {string.Join(", ", roleCounts)}.");

        foreach (var item in messages.TakeLast(12))
        {
            var label = item.ToolCalls is { Count: > 0 }
                ? $"{item.Role} requested {string.Join(", ", item.ToolCalls.Select(t => t.Name))}"
                : item.Role;
            var preview = item.Content.Replace("\r", " ").Replace("\n", " ").Trim();
            if (preview.Length > 320)
                preview = preview[..320] + "...";
            if (!string.IsNullOrWhiteSpace(preview))
                sb.AppendLine($"- {label}: {preview}");
        }

        return sb.ToString().TrimEnd();
    }
}

public interface IProjectMemoryService
{
    Task<string> GetMemoryPathAsync(Guid chatId, CancellationToken ct = default);
    Task<string> ReadAsync(Guid chatId, CancellationToken ct = default);
    Task WriteAsync(Guid chatId, string content, bool append, CancellationToken ct = default);
}

public sealed class ProjectMemoryService : IProjectMemoryService
{
    private const string InitialMemoryContent =
        "# Project Memory\n\nUse this file for stable project facts, preferences, and recurring instructions.\n";

    private readonly DbContext _db;
    private readonly string _appDataRoot;

    public ProjectMemoryService(DbContext db, string? appDataRoot = null)
    {
        _db = db;
        _appDataRoot = ResolveAppDataRoot(appDataRoot);
    }

    public async Task<string> GetMemoryPathAsync(Guid chatId, CancellationToken ct = default)
    {
        var chat = await _db.Set<Chat>().FirstOrDefaultAsync(c => c.Id == chatId, ct);
        var projectId = chat?.ProjectSpaceId ?? Guid.Empty;
        var root = Path.Combine(
            _appDataRoot,
            "memory",
            projectId == Guid.Empty ? "personal" : projectId.ToString("D"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "MEMORY.md");
    }

    public async Task<string> ReadAsync(Guid chatId, CancellationToken ct = default)
    {
        var path = await GetMemoryPathAsync(chatId, ct);
        await EnsureInitializedAsync(path, ct);

        return await ReadAllTextSharedAsync(path, ct);
    }

    public async Task WriteAsync(
        Guid chatId,
        string content,
        bool append,
        CancellationToken ct = default)
    {
        var path = await GetMemoryPathAsync(chatId, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        content = SecretRedactor.RedactText(content ?? string.Empty).TrimEnd();
        if (append && File.Exists(path))
        {
            await File.AppendAllTextAsync(
                path,
                $"{Environment.NewLine}{Environment.NewLine}{content}{Environment.NewLine}",
                new UTF8Encoding(false),
                ct);
        }
        else
        {
            await File.WriteAllTextAsync(path, content + Environment.NewLine, new UTF8Encoding(false), ct);
        }
    }

    private static string ResolveAppDataRoot(string? appDataRoot)
    {
        if (!string.IsNullOrWhiteSpace(appDataRoot))
            return appDataRoot;

        var overrideRoot = Environment.GetEnvironmentVariable("TLAH_STUDIO_APPDATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return overrideRoot;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio");
    }

    private static async Task<string> ReadAllTextSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task EnsureInitializedAsync(string path, CancellationToken ct)
    {
        if (File.Exists(path))
            return;

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                InitialMemoryContent,
                new UTF8Encoding(false),
                ct);
            ct.ThrowIfCancellationRequested();

            try
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another service or process completed the atomic initialization first.
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Cleanup is best-effort and must not hide the operation's result.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best-effort and must not hide the operation's result.
            }
        }
    }
}

public sealed record ToolResultPersistenceResult(
    AgentToolResult ContextResult,
    AgentToolArtifact? PersistedArtifact,
    bool Persisted,
    string? PersistedPath);

public interface IToolResultPersistenceService
{
    Task<ToolResultPersistenceResult> PersistForContextAsync(
        ISandboxCommandService sandbox,
        Guid chatId,
        ToolInvocation invocation,
        AgentToolResult result,
        int maxContextChars,
        CancellationToken ct = default);
}

public sealed class ToolResultPersistenceService : IToolResultPersistenceService
{
    public async Task<ToolResultPersistenceResult> PersistForContextAsync(
        ISandboxCommandService sandbox,
        Guid chatId,
        ToolInvocation invocation,
        AgentToolResult result,
        int maxContextChars,
        CancellationToken ct = default)
    {
        if (result.Output.Length <= maxContextChars)
            return new ToolResultPersistenceResult(result, null, false, null);

        var root = sandbox.GetSandboxRoot(chatId);
        var dir = Path.Combine(root, ".tlah_context", "tool-results");
        Directory.CreateDirectory(dir);
        var fileName = $"{invocation.Id:N}-{SafeFileName(invocation.ToolName)}.txt";
        var path = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(path, result.Output, new UTF8Encoding(false), ct);

        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        var relativePath = Path.GetRelativePath(root, path);
        var preview = result.Output[..maxContextChars] +
                      $"\n[persisted-output: {relativePath}; sha256={hash}; full output omitted from model context]";
        var artifact = new AgentToolArtifact(relativePath, "text/plain", new FileInfo(path).Length, hash);
        var artifacts = (result.Artifacts ?? [])
            .Concat([artifact])
            .ToArray();

        return new ToolResultPersistenceResult(
            result with { Output = preview, Artifacts = artifacts },
            artifact,
            true,
            relativePath);
    }

    private static string SafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '-');
        return string.IsNullOrWhiteSpace(value) ? "tool" : value;
    }
}
