using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace TLAHStudio.App.Views.Controls;

/// <summary>
/// M4.9.4: A code block with a language tag, line-count, line-number gutter,
/// copy button, fold toggle, and full TextMate syntax highlighting (VS Code
/// grammar engine via TextMateSharp — see <see cref="TextMateHighlighter"/>).
///
/// Grammars + the dark_plus theme are bundled under Assets/grammars/. The
/// body is a RichTextBlock with colored Runs produced by the highlighter, so
/// native text selection and copy work. A left gutter renders 1-based line
/// numbers dimmed; long blocks (>= 20 lines) start collapsed with a MaxHeight
/// and a "expand" affordance via the chevron. Construction is fully defensive:
/// any failure in the highlight path falls back to a plain monospace run so
/// the block is never empty.
/// </summary>
public sealed partial class CodeBlockControl : UserControl
{
    private const int CollapseThreshold = 20;       // lines before auto-collapse
    private const double CollapsedMaxHeight = 320;  // ~18 lines * 18px
    private const int DeferredHighlightThreshold = 300;
    private const int MaxHighlightLines = 2_000;

    private string _code = string.Empty;
    private string _language = "text";
    private bool _isCollapsed;
    private bool _needsDeferredHighlight;
    private bool _highlighting;

    public CodeBlockControl(string language, string code, bool isCompact)
    {
        InitializeComponent();
        LanguageTag.Text = string.IsNullOrWhiteSpace(language) ? "text" : language.ToLowerInvariant();

        if (isCompact)
        {
            CodeText.FontSize = 12;
            CodeText.LineHeight = 17;
            LineNumberGutter.FontSize = 12;
            LineNumberGutter.LineHeight = 17;
        }

        _code = code ?? string.Empty;
        _language = language;
        RenderCode(_language, _code);
        ConfigureFold();
    }

    private void RenderCode(string language, string code)
    {
        // Line-count tag + gutter.
        var lineCount = string.IsNullOrEmpty(code) ? 0
            : code.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length;
        LineCountTag.Text = lineCount == 1 ? "1 line" : $"{lineCount} lines";
        LineNumberGutter.Text = BuildLineNumbers(lineCount);

        _needsDeferredHighlight = lineCount >= DeferredHighlightThreshold && lineCount <= MaxHighlightLines;
        if (lineCount > MaxHighlightLines)
        {
            LineCountTag.Text += " · plain";
            RenderPlainCode(code);
            return;
        }

        if (_needsDeferredHighlight)
        {
            // Long code remains immediately selectable and copyable, but skips
            // synchronous TextMate tokenization until the user expands it.
            RenderPlainCode(code);
            return;
        }

        RenderHighlightedCode(language, code);
    }

    private void RenderHighlightedCode(string language, string code)
    {
        CodeText.Blocks.Clear();

        var paragraph = new Paragraph();
        try
        {
            TextMateHighlighter.AppendHighlighted(paragraph, language, code);
        }
        catch (System.Exception ex)
        {
            paragraph.Inlines.Clear();
            paragraph.Inlines.Add(new Run
            {
                Text = code,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"]
            });
            System.Diagnostics.Debug.WriteLine($"[CodeBlockControl] highlight failed: {ex}");
        }

        if (paragraph.Inlines.Count == 0 && code.Length > 0)
        {
            paragraph.Inlines.Add(new Run
            {
                Text = code,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"]
            });
        }

        CodeText.Blocks.Add(paragraph);
    }

    private void RenderPlainCode(string code)
    {
        CodeText.Blocks.Clear();
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run
        {
            Text = code,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"]
        });
        CodeText.Blocks.Add(paragraph);
    }

    private static string BuildLineNumbers(int lineCount)
    {
        if (lineCount <= 0) return string.Empty;
        var sb = new StringBuilder();
        for (int i = 1; i <= lineCount; i++)
        {
            if (i > 1) sb.Append('\n');
            sb.Append(i);
        }
        return sb.ToString();
    }

    private void ConfigureFold()
    {
        var lineCount = string.IsNullOrEmpty(_code) ? 0
            : _code.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length;
        if (lineCount < CollapseThreshold)
        {
            FoldButton.Visibility = Visibility.Collapsed;
            _isCollapsed = false;
            return;
        }
        // Auto-collapse long blocks.
        _isCollapsed = true;
        ApplyFoldState();
    }

    private void ApplyFoldState()
    {
        if (_isCollapsed)
        {
            CodeScroll.MaxHeight = CollapsedMaxHeight;
            FoldIcon.Glyph = ""; // ChevronRight (collapsed)
        }
        else
        {
            CodeScroll.MaxHeight = double.PositiveInfinity;
            FoldIcon.Glyph = ""; // ChevronDown (expanded)
        }
    }

    private void FoldButton_Click(object sender, RoutedEventArgs e)
    {
        _isCollapsed = !_isCollapsed;
        ApplyFoldState();
        if (!_isCollapsed && _needsDeferredHighlight)
            _ = HighlightWhenIdleAsync();
    }

    private async Task HighlightWhenIdleAsync()
    {
        if (_highlighting || !_needsDeferredHighlight)
            return;

        _highlighting = true;
        try
        {
            // Yield a frame first so expanding the block never feels blocked.
            await Task.Delay(16);
            if (!_isCollapsed && _needsDeferredHighlight)
            {
                RenderHighlightedCode(_language, _code);
                _needsDeferredHighlight = false;
            }
        }
        finally
        {
            _highlighting = false;
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(_code);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        }
        catch { /* clipboard may be unavailable in some hosting contexts */ }
    }
}
