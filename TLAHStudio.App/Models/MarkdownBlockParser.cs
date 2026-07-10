using System.Text;
using System.Text.RegularExpressions;
using TLAHStudio.App.Models;

namespace TLAHStudio.App.Models;

/// <summary>
/// M4.9.4: Splits an assistant answer into structured ChatMessageBlocks
/// (MarkdownText / CodeBlock / Table / Quote) so each can be rendered by a
/// dedicated control in Phase C.
///
/// Implementation: a single left-to-right line scan. Fenced code blocks are
/// detected by opening/closing ``` fences (with correct language capture and
/// full multi-line body). GFM tables are detected by a header row followed by
/// a `---|---` separator. Blockquotes are runs of `>`-prefixed lines. All
/// other text accumulates into MarkdownText runs, flushed at each boundary.
///
/// This is deliberately a line-oriented state machine rather than a regex
/// replace+placeholder scheme — the latter had a bug where the placeholder
/// wasn't at a line start and got swallowed into a MarkdownText run, losing
/// the code block entirely.
/// </summary>
internal static class MarkdownBlockParser
{
    private const int MaxMarkdownTextBlockChars = 50_000;
    private const int MarkdownTextBlockCutTarget = 49_500;
    private const int MaxCodeBlockChars = 50_000;
    private const int MaxCodeBlockLines = 3_000;
    private const int MaxMarkdownLines = 10_000;

    private static readonly Regex TableSeparatorRegex = new(
        @"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex TableRowRegex = new(
        @"^\|.+\|$|^[^|\n]+(\|[^|\n]+)+$",
        RegexOptions.Compiled);

    public static List<ChatMessageBlock> Parse(Guid messageId, string role, string markdown)
    {
        var blocks = new List<ChatMessageBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
            return blocks;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var textBuf = new StringBuilder();
        int idx = 0;

        void FlushText()
        {
            if (textBuf.Length > 0)
            {
                var t = textBuf.ToString();
                if (!string.IsNullOrWhiteSpace(t))
                    blocks.Add(ChatMessageBlock.MarkdownTextBlock(messageId, role, TruncateLines(t.TrimEnd('\n')), idx++));
                textBuf.Clear();
            }
        }

        int i = 0;
        while (i < lines.Length && i < MaxMarkdownLines)
        {
            var line = lines[i];

            // ── Fenced code block: ```lang ... ``` ──────────────────
            var fenceMatch = Regex.Match(line, @"^\s*```([^\s`]*)\s*$");
            if (fenceMatch.Success)
            {
                FlushText();
                var lang = fenceMatch.Groups[1].Value ?? "";
                var code = new StringBuilder();
                i++;
                // Consume until the closing fence (a line that is ``` optionally
                // preceded by whitespace). If no closing fence, the rest is code.
                // Cap code blocks to prevent runaway rendering. Once the cap is
                // reached, consume through the closing fence so discarded code
                // is not parsed a second time as ordinary Markdown.
                var codeStartLine = i;
                var truncated = false;
                while (i < lines.Length)
                {
                    var bodyLine = lines[i];
                    if (Regex.IsMatch(bodyLine, @"^\s*```\s*$"))
                    {
                        i++;
                        break;
                    }

                    var separatorChars = code.Length > 0 ? 1 : 0;
                    if (i - codeStartLine >= MaxCodeBlockLines ||
                        code.Length + separatorChars + bodyLine.Length > MaxCodeBlockChars)
                    {
                        truncated = true;
                        while (i < lines.Length && !Regex.IsMatch(lines[i], @"^\s*```\s*$"))
                            i++;
                        if (i < lines.Length)
                            i++;
                        break;
                    }

                    if (code.Length > 0) code.Append('\n');
                    code.Append(bodyLine);
                    i++;
                }
                if (truncated)
                    code.Append("\n[code block truncated for rendering]");
                blocks.Add(ChatMessageBlock.CodeBlockItem(messageId, role, lang, code.ToString(), idx++));
                continue;
            }

            // ── GFM table: header row + separator + body rows ───────
            if (IsTableRow(line) && i + 1 < lines.Length && TableSeparatorRegex.IsMatch(lines[i + 1].Trim()))
            {
                FlushText();
                var headers = SplitRow(line);
                var rows = new List<IReadOnlyList<string>>();
                i += 2; // skip header + separator
                while (i < lines.Length && IsTableRow(lines[i]) && lines[i].Trim().Length > 0)
                {
                    rows.Add(SplitRow(lines[i]));
                    i++;
                }
                blocks.Add(ChatMessageBlock.TableBlock(messageId, role, headers, rows, idx++));
                continue;
            }

            // ── Blockquote: consecutive `>`-prefixed lines ──────────
            if (line.StartsWith(">", StringComparison.Ordinal))
            {
                FlushText();
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].StartsWith(">", StringComparison.Ordinal))
                {
                    if (quote.Length > 0) quote.Append('\n');
                    quote.Append(lines[i]);
                    i++;
                }
                blocks.Add(ChatMessageBlock.QuoteBlock(messageId, role, quote.ToString(), idx++));
                continue;
            }

            // ── Otherwise: accumulate as markdown text ──────────────
            textBuf.Append(line);
            textBuf.Append('\n');
            i++;
        }

        if (i < lines.Length)
            textBuf.AppendLine("[content truncated for rendering]");
        FlushText();
        return blocks;
    }

    private static bool IsTableRow(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var t = line.Trim();
        // A table row contains at least one pipe, or is a single cell (no pipe
        // but the separator check above gates multi-column tables).
        return t.Contains('|') || TableRowRegex.IsMatch(line);
    }

    private static string TruncateLines(string text)
    {
        if (text.Length <= MaxMarkdownTextBlockChars) return text;
        // Keep the UI responsive on runaway model output while preserving normal long answers.
        var cut = text.LastIndexOf('\n', MarkdownTextBlockCutTarget);
        return cut > 0
            ? text[..cut] + "\n[text truncated for rendering]"
            : text[..MarkdownTextBlockCutTarget] + "...";
    }

    private static List<string> SplitRow(string line)
    {
        var t = line.Trim();
        // Strip leading/trailing pipes for consistent splitting.
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|', StringSplitOptions.None)
            .Select(c => c.Trim())
            .ToList();
    }
}
