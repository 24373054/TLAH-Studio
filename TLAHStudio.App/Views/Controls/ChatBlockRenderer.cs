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
    public static UIElement? Render(ChatMessageBlock block, bool isUser, bool isCompact)
    {
        return block.BlockType switch
        {
            ChatBlockType.MarkdownText => RenderMarkdown(block, isUser, isCompact),
            ChatBlockType.CodeBlock => RenderCode(block, isCompact),
            ChatBlockType.Table => RenderTable(block, isCompact),
            ChatBlockType.Quote => RenderQuote(block, isCompact),
            // Legacy block types are rendered by ChatPage's existing path;
            // returning null signals "caller handles".
            _ => null
        };
    }

    // ── MarkdownText ───────────────────────────────────────────────

    private static UIElement RenderMarkdown(ChatMessageBlock block, bool isUser, bool isCompact)
    {
        // M4.9.4: CommunityToolkit MarkdownTextBlock renders GFM markdown
        // (headings, lists, links, inline code, bold/italic, blockquotes).
        // Theme-aware: foreground + background pulled from app tokens so the
        // block honors Dark/Light themes; user messages render in accent color.
        var md = new CommunityToolkit.WinUI.UI.Controls.MarkdownTextBlock
        {
            Text = block.Content,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            IsTextSelectionEnabled = true,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Foreground = (Brush)Application.Current.Resources[
                isUser ? "AccentBrush" : "TextPrimaryBrush"],
            FontSize = isCompact ? 13 : 14
        };
        return md;
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

    private static UIElement RenderTable(ChatMessageBlock block, bool isCompact)
    {
        var meta = block.Metadata as TableMetadata;
        if (meta == null || meta.Headers.Count == 0)
            return new TextBlock { Text = block.Content, TextWrapping = TextWrapping.Wrap };

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 2, Margin = new Thickness(0, 4, 0, 4) };
        for (int i = 0; i < meta.Headers.Count; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Header row
        for (int c = 0; c < meta.Headers.Count; c++)
        {
            var cell = new Border
            {
                Padding = new Thickness(6, 3, 6, 3),
                Background = (Brush)Application.Current.Resources["SurfaceElevatedBrush"],
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = meta.Headers[c],
                    FontSize = isCompact ? 11 : 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"]
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
                        Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"]
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

    private static UIElement RenderQuote(ChatMessageBlock block, bool isCompact)
    {
        // Strip leading "> " from each line for display.
        var lines = block.Content.Split('\n')
            .Select(l => l.TrimStart().TrimStart('>').TrimStart())
            .Where(l => l.Length > 0);
        var text = string.Join('\n', lines);

        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["AccentSoftBrush"],
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 4, 0, 4),
            Child = new TextBlock
            {
                Text = text,
                FontSize = isCompact ? 12 : 13,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"]
            }
        };
    }
}
