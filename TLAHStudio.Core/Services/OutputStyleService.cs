using System.Text.Json;
using System.Text.RegularExpressions;

namespace TLAHStudio.Core.Services;

/// <summary>
/// M4.9.0: Output style configuration.
/// Defines a tone/style modifier appended to the system prompt.
/// Adopted from Claude Code's outputStyles.ts.
/// </summary>
public sealed record OutputStyleConfig(
    string Name,
    string Description,
    string Prompt,
    string Source, // "built-in" | "user" | "project" | "plugin"
    bool KeepCodingInstructions = true);

/// <summary>
/// M4.9.0: Output style service.
/// Provides built-in styles (default, Explanatory, Learning) and loads
/// custom styles from user and project directories.
/// </summary>
public interface IOutputStyleService
{
    /// <summary>All loaded styles, ordered by priority (highest last).</summary>
    IReadOnlyList<OutputStyleConfig> GetStyles();

    /// <summary>Get a specific style by name, or null.</summary>
    OutputStyleConfig? GetStyle(string name);

    /// <summary>Rescan custom style directories.</summary>
    Task ReloadAsync(CancellationToken ct = default);

    /// <summary>The default style name.</summary>
    string DefaultStyleName { get; }
}

public class OutputStyleService : IOutputStyleService
{
    public string DefaultStyleName => "default";

    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly string _userDir;
    private readonly string? _projectDir;
    private List<OutputStyleConfig> _styles;

    public OutputStyleService(string? projectDir = null)
    {
        _userDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio", "output-styles");
        _projectDir = projectDir != null
            ? Path.Combine(projectDir, ".tlah", "output-styles")
            : null;
        _styles = [];
    }

    // ── Built-in styles ──────────────────────────────────────────

    private static readonly OutputStyleConfig DefaultStyle = new(
        "default",
        "Default style — no modifications.",
        "",
        "built-in");

    private static readonly OutputStyleConfig ExplanatoryStyle = new(
        "Explanatory",
        "The assistant explains implementation choices and codebase patterns.",
        """
        You are an interactive tool that helps users with software engineering tasks.
        In addition to software engineering tasks, you should provide educational insights
        about the codebase along the way.

        You should be clear and educational, providing helpful explanations while remaining
        focused on the task. Balance educational content with task completion. When
        providing insights, you may exceed typical length constraints, but remain focused
        and relevant.

        # Explanatory Style Active

        ## Insights
        In order to encourage learning, before and after writing code, always provide
        brief educational explanations about implementation choices using (with backticks):

        "`* Insight ─────────────────────────────────────`
        [2-3 key educational points]
        `─────────────────────────────────────────────────`"

        These insights should be included in the conversation, not in the codebase.
        You should generally focus on interesting insights that are specific to the
        codebase or the code you just wrote, rather than general programming concepts.
        """,
        "built-in");

    private static readonly OutputStyleConfig LearningStyle = new(
        "Learning",
        "The assistant pauses and asks you to write small pieces of code for hands-on practice.",
        """
        You are an interactive tool that helps users with software engineering tasks.
        In addition to software engineering tasks, you should help users learn more about
        the codebase through hands-on practice and educational insights.

        You should be collaborative and encouraging. Balance task completion with learning
        by requesting user input for meaningful design decisions while handling routine
        implementation yourself.

        # Learning Style Active
        ## Requesting Human Contributions
        In order to encourage learning, ask the human to contribute 2-10 line code pieces
        when generating 20+ lines involving:
        - Design decisions (error handling, data structures)
        - Business logic with multiple valid approaches
        - Key algorithms or interface definitions

        ### Request Format
        ```
        * **Learn by Doing**
        **Context:** [what's built and why this decision matters]
        **Your Task:** [specific function/section in file, mention file and TODO(human)]
        **Guidance:** [trade-offs and constraints to consider]
        ```

        ### Key Guidelines
        - Frame contributions as valuable design decisions, not busy work
        - First add a TODO(human) section into the codebase before making the request
        - Don't take any action or output anything after the Learn by Doing request.
          Wait for human implementation before proceeding.

        ### After Contributions
        Share one insight connecting their code to broader patterns or system effects.
        Avoid praise or repetition.

        ## Insights
        In order to encourage learning, before and after writing code, always provide
        brief educational explanations about implementation choices.
        """,
        "built-in");

    // ── Public API ───────────────────────────────────────────────

    public IReadOnlyList<OutputStyleConfig> GetStyles()
    {
        if (_styles.Count == 0)
        {
            // Lazy build on first access
            var list = new List<OutputStyleConfig> { DefaultStyle, ExplanatoryStyle, LearningStyle };
            LoadCustomStyles(list, _userDir, "user");
            if (_projectDir != null)
                LoadCustomStyles(list, _projectDir, "project");
            _styles = list;
        }
        return _styles;
    }

    public OutputStyleConfig? GetStyle(string name)
    {
        return GetStyles().FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public Task ReloadAsync(CancellationToken ct = default)
    {
        _styles.Clear();
        GetStyles(); // rebuild
        return Task.CompletedTask;
    }

    // ── Custom style loading ─────────────────────────────────────

    private static void LoadCustomStyles(List<OutputStyleConfig> list, string dir, string source)
    {
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.GetFiles(dir, "*.md"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var match = FrontmatterRegex.Match(content);
                var name = Path.GetFileNameWithoutExtension(file);
                string description;
                if (match.Success)
                {
                    var fm = match.Groups[1].Value;
                    name = ExtractField(fm, "name") ?? name;
                    description = ExtractField(fm, "description") ?? "";
                }
                else
                {
                    description = "";
                }
                var body = FrontmatterRegex.Replace(content, "").Trim();
                if (string.IsNullOrWhiteSpace(body))
                    continue;

                // Avoid overwriting an existing style with the same name from a
                // higher-priority source (the list is built in priority order).
                if (list.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                list.Add(new OutputStyleConfig(name, description, body, source));
            }
            catch
            {
                // Skip unreadable files.
            }
        }
    }

    private static string? ExtractField(string frontmatter, string field)
    {
        var match = Regex.Match(frontmatter, $@"{field}:\s*(.+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
