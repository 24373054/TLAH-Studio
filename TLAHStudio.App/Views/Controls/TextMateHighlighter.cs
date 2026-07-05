using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using Windows.UI;

namespace TLAHStudio.App.Views.Controls;

/// <summary>
/// M4.9.4: TextMate-based syntax highlighter (VS Code grammar engine via
/// TextMateSharp). Loads grammars + the dark_plus theme from the bundled
/// Assets/grammars directory, tokenizes code line-by-line threading the
/// rule stack across lines, and maps each token's scope to a foreground
/// color via the theme's color map.
///
/// One shared <see cref="Registry"/> instance holds all loaded grammars;
/// grammars are loaded lazily on first use of a language and cached.
/// </summary>
internal static class TextMateHighlighter
{
    private static Registry? _registry;
    private static Theme? _theme;
    private static ICollection<string>? _colorMap;
    private static readonly Dictionary<string, IGrammar?> _grammarCache = new();
    private static readonly object _lock = new();
    private static readonly string _assetsDir = Path.Combine(
        AppContext.BaseDirectory, "Assets", "grammars");

    /// <summary>
    /// Tokenize <paramref name="code"/> and append colored <see cref="Run"/>s
    /// to <paramref name="paragraph"/>. Returns the colored paragraph ready
    /// for a RichTextBlock. If the language has no grammar, the code is
    /// appended as a single default-colored run.
    /// </summary>
    public static void AppendHighlighted(Paragraph paragraph, string language, string code)
    {
        var grammar = GetGrammar(language);
        if (grammar == null || EnsureTheme() == null)
        {
            paragraph.Inlines.Add(new Run { Text = code, Foreground = DefaultBrush });
            return;
        }

        var lines = code.Replace("\r\n", "\n").Split('\n');
        IStateStack? prev = null;
        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            ITokenizeLineResult res;
            try
            {
                res = prev == null
                    ? grammar.TokenizeLine(new LineText(line))
                    : grammar.TokenizeLine(new LineText(line), prev, System.TimeSpan.FromSeconds(5));
            }
            catch
            {
                // On any tokenize error, append the rest verbatim and stop.
                paragraph.Inlines.Add(new Run { Text = line, Foreground = DefaultBrush });
                if (li < lines.Length - 1)
                    paragraph.Inlines.Add(new Run { Text = "\n", Foreground = DefaultBrush });
                continue;
            }
            prev = res.RuleStack;

            var tokens = res.Tokens;
            int offset = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i];
                // Token end may exceed line length for trailing newline scopes; clamp.
                int end = System.Math.Min(t.EndIndex, line.Length);
                int start = System.Math.Min(t.StartIndex, line.Length);
                if (start > offset)
                    paragraph.Inlines.Add(new Run { Text = line[offset..start], Foreground = DefaultBrush });
                if (end > start)
                {
                    var brush = BrushForScopes(t.Scopes);
                    paragraph.Inlines.Add(new Run { Text = line[start..end], Foreground = brush });
                }
                offset = end;
            }
            if (offset < line.Length)
                paragraph.Inlines.Add(new Run { Text = line[offset..], Foreground = DefaultBrush });

            if (li < lines.Length - 1)
                paragraph.Inlines.Add(new Run { Text = "\n", Foreground = DefaultBrush });
        }
    }

    private static IGrammar? GetGrammar(string language)
    {
        var scope = LanguageToScope(language);
        if (scope == null) return null;
        lock (_lock)
        {
            if (_grammarCache.TryGetValue(scope, out var cached))
                return cached;
            var reg = EnsureRegistry();
            var g = reg?.LoadGrammar(scope);
            _grammarCache[scope] = g;
            return g;
        }
    }

    private static Registry? EnsureRegistry()
    {
        if (_registry != null) return _registry;
        if (!Directory.Exists(_assetsDir)) return null;
        var opts = new AssetsRegistryOptions(_assetsDir);
        _registry = new Registry(opts);
        try { _registry.SetTheme(opts.GetDefaultTheme()); }
        catch { /* theme set may fail if files missing; grammar still loads */ }
        _theme = _registry.GetTheme() as Theme;
        try { _colorMap = _registry.GetColorMap(); } catch { _colorMap = null; }
        return _registry;
    }

    private static Theme? EnsureTheme()
    {
        if (_theme == null) _ = EnsureRegistry();
        return _theme;
    }

    private static Brush BrushForScopes(List<string> scopes)
    {
        if (scopes == null || scopes.Count == 0 || _theme == null)
            return DefaultBrush;
        try
        {
            var matched = _theme.Match(scopes);
            if (matched == null || matched.Count == 0)
                return DefaultBrush;
            // matched is List<ThemeTrieElementRule>; fields are public.
            int fgIndex = matched[0].foreground;
            if (fgIndex < 0 || _colorMap == null || fgIndex >= _colorMap.Count)
                return DefaultBrush;
            return BrushFromHex(System.Linq.Enumerable.ElementAt(_colorMap, fgIndex));
        }
        catch
        {
            return DefaultBrush;
        }
    }

    private static readonly Dictionary<string, SolidColorBrush> _brushCache = new();
    private static Brush BrushFromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return DefaultBrush;
        lock (_lock)
        {
            if (_brushCache.TryGetValue(hex, out var b)) return b;
            var color = ParseHex(hex);
            var brush = new SolidColorBrush(color);
            _brushCache[hex] = brush;
            return brush;
        }
    }

    private static Color ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6)
            return Color.FromArgb(0xFF,
                System.Convert.ToByte(h[0..2], 16),
                System.Convert.ToByte(h[2..4], 16),
                System.Convert.ToByte(h[4..6], 16));
        if (h.Length == 8)
            return Color.FromArgb(
                System.Convert.ToByte(h[0..2], 16),
                System.Convert.ToByte(h[2..4], 16),
                System.Convert.ToByte(h[4..6], 16),
                System.Convert.ToByte(h[6..8], 16));
        return Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4);
    }

    private static string? LanguageToScope(string? lang) => lang?.ToLowerInvariant() switch
    {
        "cs" or "csharp" => "source.cs",
        "ts" or "typescript" => "source.ts",
        "tsx" => "source.tsx",
        "js" or "javascript" => "source.js",
        "jsx" => "source.js.jsx",
        "json" or "jsonc" => "source.json",
        "python" or "py" => "source.python",
        "go" or "golang" => "source.go",
        "rust" or "rs" => "source.rust",
        "java" => "source.java",
        "cpp" or "c++" => "source.cpp",
        "c" => "source.c",
        "css" => "source.css",
        "html" or "htm" => "text.html.basic",
        "xml" or "xaml" or "csproj" or "xml" => "text.xml",
        "yaml" or "yml" => "source.yaml",
        "sh" or "bash" or "shell" => "source.shell",
        "powershell" or "ps1" or "psm1" => "source.powershell",
        "sql" => "source.sql",
        "markdown" or "md" => "text.html.markdown",
        _ => null
    };

    private static readonly SolidColorBrush DefaultBrush = new(Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4));

    // ── Registry options loading grammars/themes from Assets ───────

    private sealed class AssetsRegistryOptions : IRegistryOptions
    {
        private readonly string _dir;
        public AssetsRegistryOptions(string dir) { _dir = dir; }

        public IRawTheme GetDefaultTheme()
        {
            var path = Path.Combine(_dir, "themes", "dark_plus.json");
            if (!File.Exists(path)) return null!;
            using var sr = new StreamReader(path);
            return ThemeReader.ReadThemeSync(sr);
        }

        public IRawTheme GetTheme(string scopeName) => GetDefaultTheme();

        public IRawGrammar GetGrammar(string scopeName)
        {
            var file = scopeName switch
            {
                "source.cs" => "csharp.tmLanguage.json",
                "source.ts" => "TypeScript.tmLanguage.json",
                "source.tsx" => "TypeScriptReact.tmLanguage.json",
                "source.js" => "JavaScript.tmLanguage.json",
                "source.js.jsx" => "JavaScriptReact.tmLanguage.json",
                "source.json" => "JSON.tmLanguage.json",
                "source.python" => "MagicPython.tmLanguage.json",
                "source.go" => "go.tmLanguage.json",
                "source.rust" => "rust.tmLanguage.json",
                "source.java" => "java.tmLanguage.json",
                "source.cpp" => "cpp.tmLanguage.json",
                "source.c" => "c.tmLanguage.json",
                "source.css" => "css.tmLanguage.json",
                "text.html.basic" => "html.tmLanguage.json",
                "text.xml" => "xml.tmLanguage.json",
                "source.yaml" => "yaml.tmLanguage.json",
                "source.shell" => "shell-unix-bash.tmLanguage.json",
                "source.powershell" => "powershell.tmLanguage.json",
                "source.sql" => "sql.tmLanguage.json",
                "text.html.markdown" => "markdown.tmLanguage.json",
                _ => null
            };
            if (file == null) return null!;
            var path = Path.Combine(_dir, file);
            if (!File.Exists(path)) return null!;
            using var sr = new StreamReader(path);
            return GrammarReader.ReadGrammarSync(sr);
        }

        public ICollection<string> GetInjections(string scopeName) => System.Array.Empty<string>();
    }
}
