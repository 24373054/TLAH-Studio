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
        var ratio = (double)estimated / budget.AvailableForContext;

        return ratio switch
        {
            >= 1.0 => TokenBudgetState.Blocking,
            >= 0.9 => TokenBudgetState.CompactNow,
            >= 0.75 => TokenBudgetState.CompactSoon,
            >= 0.5 => TokenBudgetState.Warning,
            _ => TokenBudgetState.Safe
        };
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

    private static int EstimateText(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : Math.Max(1, (int)(text.Length / 3.2));
}
