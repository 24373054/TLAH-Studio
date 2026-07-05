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

        // M4.9.4: Find the thinking block anywhere in the content (not just at
        // the start). Previously TryParse required content to START with the
        // thinking tag — if any text preceded it (e.g. a leading newline, or
        // answer tokens arriving before thinking in some provider orderings),
        // parsing failed and the thinking block silently disappeared from the
        // rendered message. Now we locate the opening tag wherever it is.
        int expandedStart = content.IndexOf(ThinkingExpanded, StringComparison.Ordinal);
        int collapsedStart = content.IndexOf(ThinkingCollapsed, StringComparison.Ordinal);
        int openStart;
        if (expandedStart >= 0 && (collapsedStart < 0 || expandedStart <= collapsedStart))
        {
            isThinkingExpanded = true;
            openStart = expandedStart;
        }
        else if (collapsedStart >= 0)
        {
            isThinkingExpanded = false;
            openStart = collapsedStart;
        }
        else
        {
            return false;
        }

        // Text before the thinking tag (if any) becomes part of the answer.
        var before = openStart > 0 ? content[..openStart] : string.Empty;

        var firstLineEnd = content.IndexOf('\n', openStart);
        if (firstLineEnd < 0)
            return false;

        var end = content.IndexOf(ThinkingEnd, firstLineEnd + 1, StringComparison.Ordinal);
        if (end < 0)
            return false;

        thinking = content[(firstLineEnd + 1)..end].Trim();
        var after = content[(end + ThinkingEnd.Length)..];
        // Compose answer = before + after, trimming stray leading newlines.
        var answerBuilder = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(before))
        {
            answerBuilder.Append(before.TrimEnd('\r', '\n'));
            answerBuilder.Append('\n');
        }
        answerBuilder.Append(after.TrimStart('\r', '\n'));
        answer = answerBuilder.ToString().TrimStart('\r', '\n');
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
