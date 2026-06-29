using System.Text;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services.Context;

/// <summary>
/// M2.10.0: Compaction strategies ordered by aggressiveness.
/// </summary>
public enum CompactionStrategy
{
    TrimToolOutputs,
    Microcompact,
    SummarizeMiddle,
    ModelAssistedSummarize,
    EmergencyTruncate
}

/// <summary>
/// Result of a compaction operation.
/// </summary>
public sealed record CompactionResult(
    List<MessagePayload> Messages,
    bool WasCompacted,
    int EstimatedTokensBefore,
    int EstimatedTokensAfter,
    string Summary
);

/// <summary>
/// M2.10.0: Reactive context compactor.
/// Applies progressively more aggressive strategies to keep context within budget.
/// </summary>
public interface IReactiveCompactor
{
    Task<CompactionResult> CompactAsync(
        IReadOnlyList<MessagePayload> messages,
        TokenBudgetState state,
        CompactionStrategy strategy,
        ITokenBudgetService tokenBudget,
        CancellationToken ct = default);
}

public class ReactiveCompactor : IReactiveCompactor
{
    private static readonly int KeepHeadMessages = 4;
    private static readonly int KeepTailMessages = 12;

    public Task<CompactionResult> CompactAsync(
        IReadOnlyList<MessagePayload> messages,
        TokenBudgetState state,
        CompactionStrategy strategy,
        ITokenBudgetService tokenBudget,
        CancellationToken ct = default)
    {
        var before = tokenBudget.EstimateTokens(messages);

        return strategy switch
        {
            CompactionStrategy.TrimToolOutputs => Task.FromResult(TrimToolOutputs(messages.ToList(), tokenBudget, before)),
            CompactionStrategy.Microcompact => Task.FromResult(Microcompact(messages.ToList(), tokenBudget, before)),
            CompactionStrategy.SummarizeMiddle => Task.FromResult(SummarizeMiddle(messages.ToList(), tokenBudget, before)),
            CompactionStrategy.EmergencyTruncate => Task.FromResult(EmergencyTruncate(messages.ToList(), tokenBudget, before)),
            _ => Task.FromResult(new CompactionResult(messages.ToList(), false, before, before, "No compaction applied."))
        };
    }

    private static CompactionResult TrimToolOutputs(
        List<MessagePayload> messages, ITokenBudgetService tokenBudget, int before)
    {
        var compacted = messages.Select(m =>
        {
            if (m.Role != "tool" || string.IsNullOrEmpty(m.Content))
                return m;
            var maxChars = 2_000;
            if (m.Content.Length <= maxChars)
                return m;
            return m with
            {
                Content = m.Content[..maxChars] + "\n[tool output trimmed by compactor]"
            };
        }).ToList();

        var after = tokenBudget.EstimateTokens(compacted);
        return new CompactionResult(compacted, before != after, before, after,
            $"Trimmed large tool outputs ({before}→{after} estimated tokens).");
    }

    private static CompactionResult Microcompact(
        List<MessagePayload> messages, ITokenBudgetService tokenBudget, int before)
    {
        if (messages.Count <= KeepHeadMessages + KeepTailMessages + 2)
            return TrimToolOutputs(messages, tokenBudget, before);

        var compacted = new List<MessagePayload>();
        var toolResultIndices = new List<int>();
        var persistenceBase = ".tlah_context/tool-results/";

        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == "tool" && !string.IsNullOrEmpty(messages[i].Content))
                toolResultIndices.Add(i);
        }

        // Keep recent tool results (last 6), replace older ones with references
        var recentThreshold = Math.Max(0, toolResultIndices.Count - 6);
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role == "tool")
            {
                var toolIdx = toolResultIndices.IndexOf(i);
                if (toolIdx >= 0 && toolIdx < recentThreshold)
                {
                    // Replace with compact reference
                    compacted.Add(new MessagePayload(
                        "tool",
                        $"[persisted-output: {persistenceBase}tool-{i:D4}.json; content-length={msg.Content.Length}]",
                        msg.ToolCallId));
                    continue;
                }
            }
            compacted.Add(msg);
        }

        var after = tokenBudget.EstimateTokens(compacted);
        return new CompactionResult(compacted, before != after, before, after,
            $"Microcompacted old tool outputs — {toolResultIndices.Count} total, {Math.Max(0, toolResultIndices.Count - 6)} replaced with references.");
    }

    private static CompactionResult SummarizeMiddle(
        List<MessagePayload> messages, ITokenBudgetService tokenBudget, int before)
    {
        if (messages.Count <= KeepHeadMessages + KeepTailMessages + 3)
            return Microcompact(messages, tokenBudget, before);

        var head = messages.Take(KeepHeadMessages).ToList();
        var tail = messages.TakeLast(KeepTailMessages).ToList();
        var middle = messages.Skip(KeepHeadMessages).Take(messages.Count - KeepHeadMessages - KeepTailMessages).ToList();

        // Count role frequencies and extract key content
        var roleCounts = middle.GroupBy(m => m.Role)
            .ToDictionary(g => g.Key, g => g.Count());
        var keyLines = middle
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) && m.Content.Length > 20)
            .Take(10)
            .Select(m => m.Content.Length > 200 ? m.Content[..200] + "..." : m.Content)
            .ToList();

        var summary = new StringBuilder();
        summary.AppendLine("[context summary boundary — messages compacted]");
        summary.AppendLine($"Compacted {middle.Count} messages:");
        foreach (var (role, count) in roleCounts.OrderByDescending(kv => kv.Value))
            summary.AppendLine($"  {role}: {count}");
        if (keyLines.Count > 0)
        {
            summary.AppendLine("Key content previews:");
            foreach (var line in keyLines)
                summary.AppendLine($"  - {line}");
        }

        var compacted = new List<MessagePayload>();
        compacted.AddRange(head);
        compacted.Add(new MessagePayload("user", summary.ToString()));
        compacted.Add(new MessagePayload("assistant", "I understand. The conversation context has been summarized."));
        compacted.AddRange(tail);

        var after = tokenBudget.EstimateTokens(compacted);
        return new CompactionResult(compacted, true, before, after,
            $"Summarized {middle.Count} middle messages into a compact boundary ({before}→{after} tokens).");
    }

    private static CompactionResult EmergencyTruncate(
        List<MessagePayload> messages, ITokenBudgetService tokenBudget, int before)
    {
        if (messages.Count <= 8)
            return new CompactionResult(messages.ToList(), false, before, before,
                "Cannot emergency truncate — too few messages remain.");

        var head = messages.Take(2).ToList();
        var tail = messages.TakeLast(6).ToList();

        var compacted = new List<MessagePayload>();
        compacted.AddRange(head);
        compacted.Add(new MessagePayload("user",
            $"[EMERGENCY: {messages.Count - 8} messages truncated. Keeping only first 2 and last 6.]"));
        compacted.AddRange(tail);

        var after = tokenBudget.EstimateTokens(compacted);
        return new CompactionResult(compacted, true, before, after,
            $"Emergency truncation — kept only first 2 and last 6 of {messages.Count} messages.");
    }
}

/// <summary>
/// M2.10.0: Model-assisted context compaction.
/// Uses a lightweight model call to generate a structured summary.
/// </summary>
public interface IModelAssistedCompactor
{
    Task<string> GenerateSummaryAsync(
        IReadOnlyList<MessagePayload> messages,
        ILlmProvider provider,
        string systemPrompt,
        CancellationToken ct = default);
}

public class ModelAssistedCompactor : IModelAssistedCompactor
{
    private readonly IProviderStreamAdapter _adapter;

    public ModelAssistedCompactor(IProviderStreamAdapter adapter)
    {
        _adapter = adapter;
    }

    public async Task<string> GenerateSummaryAsync(
        IReadOnlyList<MessagePayload> messages,
        ILlmProvider provider,
        string systemPrompt,
        CancellationToken ct = default)
    {
        var compactPrompt = """
            Summarize the following conversation for an agent. Your summary will replace the middle
            of the conversation history. Preserve:
            1. Key decisions made and by whom
            2. File paths read, written, or modified
            3. Tool results and their outcomes (include exact values where relevant)
            4. Unresolved tasks or questions
            5. User preferences, constraints, and feedback
            Omit conversational filler, repeated information, and transient status messages.
            Keep the summary under 600 words.
            """;

        var compactMessages = new List<MessagePayload>
        {
            new("system", compactPrompt),
            new("user", $"Summarize these {messages.Count} messages:\n\n" +
                string.Join("\n", messages.Where(m => !string.IsNullOrWhiteSpace(m.Content))
                    .Take(20).Select(m => $"[{m.Role}] {m.Content[..Math.Min(m.Content.Length, 300)]}")))
        };

        try
        {
            var response = await _adapter.ChatAsync(new ProviderStreamRequest(
                provider, compactMessages, systemPrompt, 0.3, 1024, Tools: null), ct);

            if (!string.IsNullOrWhiteSpace(response.AssistantText))
                return $"[model-assisted compact summary]\n{response.AssistantText.Trim()}\n[end summary]";

            return "[model-assisted compact failed — empty response]";
        }
        catch
        {
            return "[model-assisted compact failed — provider error]";
        }
    }
}
