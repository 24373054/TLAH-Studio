using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLAHStudio.App.Models;

namespace TLAHStudio.App.Views.Controls;

/// <summary>
/// M4.9.6: Streaming markdown renderer with incremental element reuse.
///
/// During streaming the answer grows character-by-character. The previous
/// implementation rebuilt all stable UIElements whenever the stable prefix
/// grew (every blank line / closed fence). With 10 stable blocks this meant
/// ~60,000 element create/destroy cycles per response.
///
/// Optimization: when the new stableText is a prefix-extension of the old
/// (i.e. it starts with the old stableText and only appended), we parse the
/// full new stableText but REUSE the existing UIElements for unchanged blocks
/// and only APPEND new UIElements for the new blocks. The panel is never
/// cleared during append-only growth.
///
/// If the content changes non-monotonically (regenerate/edit), fall back to
/// full rebuild.
/// </summary>
internal sealed class StreamingAnswerRenderer
{
    private readonly Panel _panel;
    private readonly bool _isCompact;
    private readonly bool _isUser;
    private readonly bool _isDark;

    // M4.9.6: Cache for incremental append. _lastStableText is the previously
    // rendered stable prefix; _lastBlockCount is how many UIElements it
    // produced. When the new stable text extends the old, we only add the
    // delta blocks instead of clearing and re-rendering everything.
    private string _lastStableText = string.Empty;
    private int _lastBlockCount;
    private int _stableElementCount;
    private int _stableTextLength;

    public StreamingAnswerRenderer(Panel panel, bool isCompact, bool isUser, bool isDark)
    {
        _panel = panel;
        _isCompact = isCompact;
        _isUser = isUser;
        _isDark = isDark;
    }

    /// <summary>Update the rendered content for the current answer text.</summary>
    public void Update(string answer)
    {
        if (string.IsNullOrEmpty(answer))
        {
            // M4.9.6: reset incremental cache on empty.
            _lastStableText = string.Empty;
            _lastBlockCount = 0;
            _stableTextLength = 0;
            EnsureSuffixSlot();
            if (_panel.Children[_stableElementCount] is TextBlock tb)
                tb.Text = string.Empty;
            return;
        }

        var boundary = FindStableBoundary(answer);
        if (boundary != _stableTextLength)
        {
            // Stable prefix changed — update incrementally or full rebuild.
            var newStableText = answer[..boundary];
            UpdateStable(newStableText);
            _stableTextLength = boundary;
        }

        EnsureSuffixSlot();
        var suffix = boundary < answer.Length ? answer[boundary..] : string.Empty;
        if (_panel.Children[_stableElementCount] is TextBlock suffixTb)
            suffixTb.Text = suffix;
    }

    /// <summary>
    /// M4.9.6: Incremental stable update. If newStableText starts with the
    /// old _lastStableText, parse the full text but reuse existing UIElements
    /// for unchanged blocks; only append new blocks for the delta. Otherwise
    /// (regenerate/edit), full clear+rebuild.
    /// </summary>
    private void UpdateStable(string newStableText)
    {
        bool appendOnly = newStableText.Length > _lastStableText.Length
            && newStableText.StartsWith(_lastStableText, StringComparison.Ordinal);

        if (!appendOnly)
        {
            // Non-monotonic change (regenerate, edit, or shrink) — full rebuild.
            _panel.Children.Clear();
            _stableElementCount = 0;
            _lastBlockCount = 0;
            _lastStableText = string.Empty;
        }

        var blocks = MarkdownBlockParser.Parse(Guid.Empty, "assistant", newStableText);

        if (appendOnly && _lastBlockCount < blocks.Count)
        {
            // Append-only: reuse existing stable elements, render only the new ones.
            // Insert before the suffix slot (at index _stableElementCount).
            for (int i = _lastBlockCount; i < blocks.Count; i++)
            {
                var el = ChatBlockRenderer.Render(blocks[i], _isUser, _isCompact, _isDark);
                if (el != null)
                {
                    _panel.Children.Insert(_stableElementCount, el);
                    _stableElementCount++;
                }
            }
        }
        else if (appendOnly && blocks.Count == _lastBlockCount && blocks.Count > 0)
        {
            // The stable prefix grew within the same Markdown block (for example
            // a streaming paragraph or heading). Reusing the old element would
            // leave the UI stuck on the earlier text, so replace just the final
            // stable element and keep the suffix slot intact.
            var replacement = ChatBlockRenderer.Render(blocks[^1], _isUser, _isCompact, _isDark);
            if (replacement != null && _stableElementCount > 0)
                _panel.Children[_stableElementCount - 1] = replacement;
        }
        else if (!appendOnly)
        {
            // Full rebuild path.
            foreach (var block in blocks)
            {
                var el = ChatBlockRenderer.Render(block, _isUser, _isCompact, _isDark);
                if (el != null)
                {
                    _panel.Children.Add(el);
                    _stableElementCount++;
                }
            }
        }
        _lastStableText = newStableText;
        _lastBlockCount = blocks.Count;
    }

    /// <summary>
    /// Find the position of the last stable block boundary in the text.
    /// A boundary is:
    ///   - after a closing ``` fence: that code block is complete (stable).
    ///   - after a blank line: the preceding paragraph/table/quote is complete.
    ///   - if NO fence is currently open and NO blank line exists (a single
    ///     in-flight paragraph with no code blocks), the whole text is stable —
    ///     plain paragraphs are cheap to re-render and this keeps headings/lists
    ///     live during streaming. An open code fence (``` seen, not yet closed)
    ///     forces the boundary back to before that fence so the half-typed code
    ///     stays as plain suffix.
    ///
    /// M4.9.6: Optimized to avoid Split('\n') on every call — uses IndexOf
    /// to scan lines in-place without allocating a large array.
    /// </summary>
    internal static int FindStableBoundary(string text)
    {
        int lastOpenFenceStart = -1;
        int fenceCount = 0;
        int pos = 0;
        int bestAfterFence = 0;
        int bestAfterBlank = 0;

        // Scan line-by-line using IndexOf('\n') to avoid Split allocation.
        int lineStart = 0;
        while (lineStart <= text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;
            var line = text.AsSpan(lineStart, lineEnd - lineStart);
            var trimmed = line.Trim();
            bool isFence = trimmed.StartsWith("```", StringComparison.Ordinal);
            int lineLen = (lineEnd - lineStart) + (lineEnd < text.Length ? 1 : 0);
            int currentLineStart = pos;
            pos += lineLen;

            if (isFence)
            {
                fenceCount++;
                if (fenceCount % 2 == 1)
                    lastOpenFenceStart = currentLineStart;
                else
                    bestAfterFence = pos;
            }
            else if (fenceCount % 2 == 0 && trimmed.IsEmpty)
            {
                bestAfterBlank = pos;
            }

            lineStart = lineEnd + 1;
            if (lineEnd >= text.Length) break;
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

    /// <summary>Reset all cached state (e.g. when switching chats).</summary>
    public void Reset()
    {
        _lastStableText = string.Empty;
        _lastBlockCount = 0;
        _stableElementCount = 0;
        _stableTextLength = 0;
        _panel.Children.Clear();
    }
}
