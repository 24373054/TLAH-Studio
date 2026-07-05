using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace TLAHStudio.App.Views.Controls;

/// <summary>
/// M4.9.4: A code block with a language tag, copy button, and full TextMate
/// syntax highlighting (VS Code grammar engine via TextMateSharp — see
/// <see cref="TextMateHighlighter"/>). Grammars + the dark_plus theme are
/// bundled under Assets/grammars/.
///
/// The body is a RichTextBlock with colored Runs produced by the highlighter,
/// so native text selection and copy work. Horizontal scroll keeps long lines
/// intact instead of wrapping.
/// </summary>
public sealed partial class CodeBlockControl : UserControl
{
    private string _code = string.Empty;

    public CodeBlockControl(string language, string code, bool isCompact)
    {
        InitializeComponent();
        LanguageTag.Text = string.IsNullOrWhiteSpace(language) ? "text" : language.ToLowerInvariant();

        if (isCompact)
        {
            CodeText.FontSize = 12;
            CodeText.LineHeight = 17;
        }

        _code = code ?? string.Empty;
        var paragraph = new Paragraph();
        TextMateHighlighter.AppendHighlighted(paragraph, language, _code);
        CodeText.Blocks.Add(paragraph);
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
