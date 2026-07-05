using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLAHStudio.App.Models;

namespace TLAHStudio.App.Views.Controls;

/// <summary>
/// M4.9.4: Streaming markdown renderer — the WinUI analog of Claude Code's
/// StreamingMarkdown (stable-prefix memoization).
///
/// During streaming the answer grows character-by-character. Re-parsing and
/// re-rendering the whole thing every ~50ms is both expensive and visually
/// janky (full reflow each tick). Instead:
///
///   1. Split the current answer at the last stable block boundary. A boundary
///      is the end of a closed fenced code block (```) or a blank line that
///      terminates a paragraph/table/quote. Everything up to that boundary is
///      "stable" — it won't change as more tokens arrive.
///   2. The stable prefix is parsed once into ChatMessageBlocks and rendered
///      via <see cref="ChatBlockRenderer"/>; the rendered elements are cached
///      and only rebuilt when the stable length grows past the last cached
///      boundary.
///   3. The unstable suffix (the tail after the last boundary — the block
///      currently being typed) is shown as a single plain TextBlock that's
///      cheaply updated each tick.
///
/// Result: completed paragraphs/code/tables snap into rich markdown instantly
/// and never reflow; the in-flight tail streams as plain text and is swapped
/// for rich markdown the moment it closes. No per-token full re-render.
/// </summary>
internal sealed class StreamingAnswerRenderer
{
    private readonly Panel _panel;
    private readonly bool _isCompact;
    private readonly bool _isUser;

    // Cached stable prefix: the elements in _panel[0.._stableElementCount) are
    // the rendered stable blocks; the element at _stableElementCount is the
    // unstable-suffix TextBlock (recreated each update).
    private int _stableElementCount;
    private int _stableTextLength;   // length of the stable prefix in source chars

    public StreamingAnswerRenderer(Panel panel, bool isCompact, bool isUser)
    {
        _panel = panel;
        _isCompact = isCompact;
        _isUser = isUser;
    }

    /// <summary>Update the rendered content for the current answer text.</summary>
    public void Update(string answer)
    {
        if (string.IsNullOrEmpty(answer))
        {
            EnsureSuffixSlot();
            if (_panel.Children[_stableElementCount] is TextBlock tb)
                tb.Text = string.Empty;
            return;
        }

        var boundary = FindStableBoundary(answer);
        // If the stable prefix has grown, rebuild the stable region.
        if (boundary > _stableTextLength)
        {
            RebuildStable(answer[..boundary]);
            _stableTextLength = boundary;
        }
        else if (boundary < _stableTextLength)
        {
            // The source shrank (e.g. content was trimmed/regenerated) — rebuild.
            RebuildStable(answer[..boundary]);
            _stableTextLength = boundary;
        }

        EnsureSuffixSlot();
        var suffix = boundary < answer.Length ? answer[boundary..] : string.Empty;
        if (_panel.Children[_stableElementCount] is TextBlock suffixTb)
        {
            suffixTb.Text = suffix;
            suffixTb.Visibility = string.IsNullOrEmpty(suffix) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// Find the last stable block boundary in <paramref name="text"/> — the
    /// index just past the last closed block. Everything before this is safe to
    /// render as final markdown. Rules:
    ///   - after a closing ``` fence: that code block is complete (stable).
    ///   - after a blank line: the preceding paragraph/table/quote is complete.
    ///   - if NO fence is currently open and NO blank line exists (a single
    ///     in-flight paragraph with no code blocks), the whole text is stable —
    ///     plain paragraphs are cheap to re-render and this keeps headings/lists
    ///     live during streaming. An open code fence (``` seen, not yet closed)
    ///     forces the boundary back to before that fence so the half-typed code
    ///     stays as plain suffix.
    /// </summary>
    private static int FindStableBoundary(string text)
    {
        // Detect an open code fence: count ``` fence-openings; if odd, the last
        // one is unclosed. The stable boundary must be before it.
        int lastOpenFenceStart = -1;
        int fenceCount = 0;
        var lines = text.Split('\n');
        int pos = 0;
        int bestAfterFence = 0;
        int bestAfterBlank = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            // A fence is any line whose trimmed form starts with ``` (opening
            // fences carry a language tag like ```csharp; closing fences are
            // bare ```). Open vs close is determined by pairing below.
            bool isFence = trimmed.StartsWith("```", StringComparison.Ordinal);
            int lineLen = line.Length + (i < lines.Length - 1 ? 1 : 0);
            int lineStart = pos;
            pos += lineLen;

            if (isFence)
            {
                fenceCount++;
                if (fenceCount % 2 == 1)
                {
                    // opening fence — remember its start so we can clamp boundary
                    lastOpenFenceStart = lineStart;
                }
                else
                {
                    // closing fence — boundary is end of this line.
                    bestAfterFence = pos;
                }
            }
            else if (fenceCount % 2 == 0 && string.IsNullOrWhiteSpace(line))
            {
                bestAfterBlank = pos;
            }
        }

        // If a code fence is open, clamp the boundary to before it.
        int boundary = System.Math.Max(bestAfterFence, bestAfterBlank);
        if (fenceCount % 2 == 1 && lastOpenFenceStart >= 0)
            boundary = System.Math.Min(boundary, lastOpenFenceStart);

        // No boundary found and no open fence → render the whole paragraph as
        // stable (cheap re-render, keeps inline markdown live).
        if (boundary == 0 && fenceCount == 0 && !string.IsNullOrWhiteSpace(text))
            return text.Length;

        return boundary;
    }

    private void RebuildStable(string stableText)
    {
        // Full rebuild of the stable region. The panel layout is:
        //   [stable_0 .. stable_{n-1}] [suffix_TextBlock]
        // We clear everything and re-insert stable blocks; the suffix slot is
        // re-created by EnsureSuffixSlot() right after this returns.
        _panel.Children.Clear();
        _stableElementCount = 0;

        var blocks = MarkdownBlockParser.Parse(Guid.Empty, "assistant", stableText);
        foreach (var block in blocks)
        {
            var el = ChatBlockRenderer.Render(block, _isUser, _isCompact);
            if (el != null)
            {
                _panel.Children.Add(el);
                _stableElementCount++;
            }
        }
    }

    private void EnsureSuffixSlot()
    {
        while (_panel.Children.Count < _stableElementCount + 1)
        {
            _panel.Children.Add(new TextBlock
            {
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _isUser
                    ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBrush"]
                    : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
                FontSize = _isCompact ? 13 : 14,
                LineHeight = _isCompact ? 20 : 22
            });
        }
        while (_panel.Children.Count > _stableElementCount + 1)
            _panel.Children.RemoveAt(_panel.Children.Count - 1);
    }
}
