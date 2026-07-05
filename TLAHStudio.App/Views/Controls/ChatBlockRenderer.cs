using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLAHStudio.App.Models;

namespace TLAHStudio.App.Views.Controls;

/// <summary>
/// M4.9.4: Maps a structured <see cref="ChatMessageBlock"/> to its dedicated
/// UI element. Phase B produced the block structure; Phase C renders each
/// type with a purpose-built control:
///   - MarkdownText → CommunityToolkit MarkdownTextBlock (headings/lists/links/inline code)
///   - CodeBlock    → CodeBlockControl (language tag + copy + TextMate syntax highlight)
///   - Table        → Grid with dynamic columns (narrow-screen degrades to key-value)
///   - Quote        → left accent bar + dimmed text
///   - Text/Thinking/ToolUse/etc → fall back to the legacy plain rendering path
///
/// Callers (<c>ChatPage.BuildMessageBody</c>) build a StackPanel of these
/// elements instead of one monolithic TextBlock, so each block renders
/// independently and can be cached/streamed per-block.
/// </summary>
internal static class ChatBlockRenderer
{
    /// <param name="isDark">
    /// M4.9.4: Whether the hosting window is in Dark theme. The app sets theme
    /// on the window root (root.RequestedTheme), NOT on Application — so
    /// Application.Current.RequestedTheme is always Light and can't be used.
    /// Callers (ChatPage) pass their own ActualTheme-derived value.
    /// </param>
    public static UIElement? Render(ChatMessageBlock block, bool isUser, bool isCompact, bool isDark)
    {
        return block.BlockType switch
        {
            ChatBlockType.MarkdownText => RenderMarkdown(block, isUser, isCompact, isDark),
            ChatBlockType.CodeBlock => RenderCode(block, isCompact),
            ChatBlockType.Table => RenderTable(block, isCompact, isDark),
            ChatBlockType.Quote => RenderQuote(block, isCompact, isDark),
            // Legacy block types are rendered by ChatPage's existing path;
            // returning null signals "caller handles".
            _ => null
        };
    }

    // ── MarkdownText ───────────────────────────────────────────────

    private static UIElement RenderMarkdown(ChatMessageBlock block, bool isUser, bool isCompact, bool isDark)
    {
        // M4.9.4: CommunityToolkit MarkdownTextBlock renders GFM markdown
        // (headings, lists, links, inline code, bold/italic, blockquotes).
        //
        // Decompilation of CommunityToolkit 7.1.2 shows the body RichTextBlock's
        // Foreground comes from the render context, which is seeded from the
        // control's Foreground property (markdownRenderer.Foreground = ((Control)this).Foreground).
        // So setting md.Foreground IS the right lever for body text.
        //
        // The earlier bug: brushes were pulled from Application.Current.Resources,
        // which resolve against the APPLICATION theme, not the control's visual
        // theme. When the window runs Dark but Application.RequestedTheme is
        // Light (or vice versa), TextPrimaryBrush resolved to the Light value
        // (dark text) and rendered invisible on a Dark background. Fix: derive
        // every color directly from isDark with concrete SolidColorBrush values.
        var textPrimary = Solid(isDark ? 0xFFFFFFFF : 0xFF172033);
        var textSecondary = Solid(isDark ? 0xFFE0E8F4 : 0xFF56657A);
        var accent = Solid(isDark ? 0xFF71A7FF : 0xFF2F5FEA);
        var surface = Solid(isDark ? 0xFF192434 : 0xFFF6F8FC);
        var borderSubtle = Solid(isDark ? 0xFF718195 : 0xFFD2DCEB);

        var md = new CommunityToolkit.WinUI.UI.Controls.MarkdownTextBlock
        {
            Text = block.Content,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            IsTextSelectionEnabled = true,
            Background = Solid(0x00000000),
            RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light,
            // Body + headings use the primary text color (honors theme).
            Foreground = isUser ? accent : textPrimary,
            FontSize = isCompact ? 13 : 14,
            // Links: accent in both themes.
            LinkForeground = accent,
            // Inline code: subtle surface chip with primary text.
            InlineCodeBackground = surface,
            InlineCodeBorderBrush = borderSubtle,
            InlineCodeForeground = textPrimary,
            // Fenced code (MarkdownTextBlock's own): same as our CodeBlockControl
            // palette — surface bg, primary text. (Our parser already extracts
            // fenced code into CodeBlockControl, but inline ``` inside a single
            // MarkdownText block still routes here.)
            CodeBackground = surface,
            CodeBorderBrush = borderSubtle,
            CodeForeground = textPrimary,
            // Blockquotes: secondary text, subtle border.
            QuoteBackground = Solid(0x00000000),
            QuoteBorderBrush = accent,
            QuoteForeground = textSecondary,
            // Headings: primary (slightly heavier weight is automatic).
            Header1Foreground = textPrimary,
            Header2Foreground = textPrimary,
            Header3Foreground = textPrimary,
            Header4Foreground = textPrimary,
            Header5Foreground = textPrimary,
            Header6Foreground = textSecondary,
            HorizontalRuleBrush = borderSubtle,
            TableBorderBrush = borderSubtle
        };
        return md;
    }

    private static SolidColorBrush Solid(uint argb)
    {
        var c = Windows.UI.Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));
        return new SolidColorBrush(c);
    }

    // ── CodeBlock ──────────────────────────────────────────────────

    private static UIElement RenderCode(ChatMessageBlock block, bool isCompact)
    {
        var meta = block.Metadata as CodeBlockMetadata
            ?? new CodeBlockMetadata("text", block.Content);
        return new CodeBlockControl(meta.Language, meta.Code, isCompact)
        {
            Margin = new Thickness(0, 4, 0, 4)
        };
    }

    // ── Table ──────────────────────────────────────────────────────

    private static UIElement RenderTable(ChatMessageBlock block, bool isCompact, bool isDark)
    {
        var meta = block.Metadata as TableMetadata;
        if (meta == null || meta.Headers.Count == 0)
            return new TextBlock { Text = block.Content, TextWrapping = TextWrapping.Wrap };

        var headerBg = Solid(isDark ? 0xFF243348 : 0xFFEEF2F8);
        var headerFg = Solid(isDark ? 0xFFFFFFFF : 0xFF172033);
        var bodyFg = Solid(isDark ? 0xFFE0E8F4 : 0xFF56657A);

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 2, Margin = new Thickness(0, 4, 0, 4) };
        for (int i = 0; i < meta.Headers.Count; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Header row
        for (int c = 0; c < meta.Headers.Count; c++)
        {
            var cell = new Border
            {
                Padding = new Thickness(6, 3, 6, 3),
                Background = headerBg,
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = meta.Headers[c],
                    FontSize = isCompact ? 11 : 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = headerFg
                }
            };
            Grid.SetRow(cell, 0);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Body rows
        for (int r = 0; r < meta.Rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = meta.Rows[r];
            for (int c = 0; c < meta.Headers.Count && c < row.Count; c++)
            {
                var cell = new Border
                {
                    Padding = new Thickness(6, 3, 6, 3),
                    Child = new TextBlock
                    {
                        Text = row[c],
                        FontSize = isCompact ? 11 : 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = bodyFg
                    }
                };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    // ── Quote ──────────────────────────────────────────────────────

    private static UIElement RenderQuote(ChatMessageBlock block, bool isCompact, bool isDark)
    {
        // Strip leading "> " from each line for display.
        var lines = block.Content.Split('\n')
            .Select(l => l.TrimStart().TrimStart('>').TrimStart())
            .Where(l => l.Length > 0);
        var text = string.Join('\n', lines);

        var accentSoft = Solid(isDark ? 0x9971A7FF : 0xFFBFD2FF);
        var secondary = Solid(isDark ? 0xFFE0E8F4 : 0xFF56657A);

        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = accentSoft,
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 4, 0, 4),
            Child = new TextBlock
            {
                Text = text,
                FontSize = isCompact ? 12 : 13,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                Foreground = secondary
            }
        };
    }
}
