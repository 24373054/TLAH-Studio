using System.Text;

namespace TLAHStudio.Core.Llm;

public static class AssistantContentFormatter
{
    public const string ThinkingExpanded = "<tlah-thinking expanded>";
    public const string ThinkingCollapsed = "<tlah-thinking collapsed>";
    public const string ThinkingEnd = "</tlah-thinking>";

    public static string Compose(string? answer, string? thinking, bool isThinkingExpanded = false)
    {
        var safeAnswer = answer ?? string.Empty;
        var safeThinking = thinking ?? string.Empty;
        if (string.IsNullOrWhiteSpace(safeThinking))
            return safeAnswer;

        var builder = new StringBuilder();
        builder.AppendLine(isThinkingExpanded ? ThinkingExpanded : ThinkingCollapsed);
        builder.AppendLine(safeThinking.TrimEnd('\r', '\n'));
        builder.AppendLine(ThinkingEnd);
        builder.Append(safeAnswer);
        return builder.ToString();
    }

    public static bool TryParse(
        string? content,
        out string thinking,
        out string answer,
        out bool isThinkingExpanded)
    {
        thinking = string.Empty;
        answer = content ?? string.Empty;
        isThinkingExpanded = false;

        if (string.IsNullOrEmpty(content))
            return false;

        if (content.StartsWith(ThinkingExpanded, StringComparison.Ordinal))
        {
            isThinkingExpanded = true;
        }
        else if (!content.StartsWith(ThinkingCollapsed, StringComparison.Ordinal))
        {
            return false;
        }

        var firstLineEnd = content.IndexOf('\n');
        if (firstLineEnd < 0)
            return false;

        var end = content.IndexOf(ThinkingEnd, firstLineEnd + 1, StringComparison.Ordinal);
        if (end < 0)
            return false;

        thinking = content[(firstLineEnd + 1)..end].Trim();
        answer = content[(end + ThinkingEnd.Length)..].TrimStart('\r', '\n');
        return true;
    }

    public static string StripThinking(string? content)
    {
        if (!TryParse(content, out _, out var answer, out _))
            return content ?? string.Empty;
        return answer;
    }

    public static string CollapseThinking(string? content)
    {
        if (!TryParse(content, out var thinking, out var answer, out _))
            return content ?? string.Empty;
        return Compose(answer, thinking, isThinkingExpanded: false);
    }

    public static string Preview(string thinking, int maxCharacters = 96)
    {
        var normalized = string.Join(
            " ",
            thinking
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= maxCharacters)
            return normalized;
        return normalized[..Math.Max(0, maxCharacters)].TrimEnd() + "...";
    }
}
