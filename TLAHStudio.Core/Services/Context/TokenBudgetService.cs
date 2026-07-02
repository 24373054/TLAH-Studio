using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services.Context;

/// <summary>
/// M2.10.0: Context warning states for token budget management.
/// </summary>
public enum TokenBudgetState
{
    Safe, Warning, CompactSoon, CompactNow, Blocking
}

/// <summary>
/// Token budget for a model.
/// </summary>
public sealed record TokenBudget(int MaxTokens, int ReservedForResponse, int AvailableForContext);

/// <summary>
/// M2.10.0: Token budget tracking service.
/// Tracks context window usage and detects when compaction is needed.
/// </summary>
public interface ITokenBudgetService
{
    TokenBudget GetBudget(string provider, string model);
    TokenBudgetState CheckBudget(IReadOnlyList<MessagePayload> messages, TokenBudget budget, int triggerTokens);
    int EstimateTokens(IReadOnlyList<MessagePayload> messages);
}

public class TokenBudgetService : ITokenBudgetService
{
    private static readonly Dictionary<string, int> KnownContextWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4o"] = 128_000,
        ["gpt-4o-mini"] = 128_000,
        ["gpt-4.1"] = 1_000_000,
        ["gpt-4.1-mini"] = 1_000_000,
        ["deepseek-v4-pro"] = 1_000_000,
        ["deepseek-v4-flash"] = 160_000,
        ["claude-sonnet-4-6"] = 200_000,
        ["claude-opus-4-8"] = 200_000,
        ["claude-opus-4-1"] = 200_000,
        ["claude-haiku-4-5"] = 200_000,
    };

    public TokenBudget GetBudget(string provider, string model)
    {
        var maxTokens = KnownContextWindows.GetValueOrDefault(model, 128_000);
        var reserved = Math.Min(16_384, maxTokens / 4);
        return new TokenBudget(maxTokens, reserved, maxTokens - reserved);
    }

    public TokenBudgetState CheckBudget(IReadOnlyList<MessagePayload> messages, TokenBudget budget, int triggerTokens)
    {
        var estimated = EstimateTokens(messages);
        var available = Math.Max(1, budget.AvailableForContext);
        var configuredTrigger = triggerTokens > 0 ? triggerTokens : (int)(available * 0.75);
        var compactSoonAt = Math.Min(available, Math.Max(configuredTrigger, (int)(available * 0.75)));
        var compactNowAt = Math.Min(available, Math.Max((int)(compactSoonAt * 1.15), (int)(available * 0.90)));
        var warningAt = Math.Min(compactSoonAt, Math.Max((int)(available * 0.50), compactSoonAt / 2));

        if (estimated >= available)
            return TokenBudgetState.Blocking;
        if (estimated >= compactNowAt)
            return TokenBudgetState.CompactNow;
        if (estimated >= compactSoonAt)
            return TokenBudgetState.CompactSoon;
        if (estimated >= warningAt)
            return TokenBudgetState.Warning;
        return TokenBudgetState.Safe;
    }

    public int EstimateTokens(IReadOnlyList<MessagePayload> messages)
    {
        var total = 0;
        foreach (var msg in messages)
        {
            total += EstimateText(msg.Role) + EstimateText(msg.Content);
            if (msg.ToolCalls != null)
            {
                foreach (var tc in msg.ToolCalls)
                    total += EstimateText(tc.ArgumentsJson ?? "") + EstimateText(tc.Name) + 20;
            }
            if (msg.ToolCallId != null)
                total += EstimateText(msg.ToolCallId) + 10;
        }
        return total;
    }

    private static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        // M4.4.0: CJK-aware estimation. CJK characters average ~1.5 tokens each
        // in most tokenizers, while Latin text averages ~3.2 characters per token.
        // The old formula (chars/3.2) underestimated Chinese by 3–6×, causing
        // repeated context-limit errors followed by destructive force-compaction.
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
        return Math.Max(1, (int)Math.Ceiling(cjk * 1.5 + nonCjk / 3.2));
    }
}
