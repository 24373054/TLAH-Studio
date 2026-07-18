using System.Text;
using System.Text.Json;
using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Services.Tooling;

/// <summary>
/// Context used to choose a compact, task-relevant provider tool set.
/// Explicitly loaded names (for example from tool_search) take precedence.
/// </summary>
public sealed record ToolSelectionContext(
    string UserRequest,
    string? RecentContext = null,
    IReadOnlyCollection<string>? ExplicitlyLoadedNames = null,
    string? LastFailedToolName = null,
    int MaxTools = ToolContextSelector.DefaultMaxTools);

public sealed record ToolSelectionResult(
    IReadOnlyList<LlmToolDefinition> Definitions,
    IReadOnlyList<LlmToolDefinition> DeferredDefinitions)
{
    public IReadOnlySet<string> SelectedNames =>
        Definitions.Select(tool => tool.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public interface IToolContextSelector
{
    ToolSelectionResult Select(ToolSelectionContext context);
    IReadOnlyList<ToolCatalogMatch> Search(string query, int limit = 12);
    IReadOnlyList<LlmToolDefinition> Catalog { get; }
}

public sealed record ToolCatalogMatch(
    string Name,
    string Namespace,
    string Category,
    string Description,
    bool ReadOnly,
    bool Destructive,
    bool OpenWorld);

/// <summary>
/// Local provider-independent deferred tool loader. It keeps the initial
/// provider schema set small while still working with OpenAI-compatible,
/// Anthropic, plugin, and MCP-backed tool definitions.
/// </summary>
public sealed class ToolContextSelector : IToolContextSelector
{
    public const int DefaultMaxTools = 15;
    public const int AlwaysAvailableToolCount = 4;
    private const int MinimumSelectionScore = 50;

    private static readonly HashSet<string> AlwaysAvailable = new(
        [
            AgentToolNames.ToolSearch,
            AgentToolNames.AskUserQuestion,
            AgentToolNames.Skill,
            AgentToolNames.FileSend
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AmbiguousBareToolNames = new(
        [
            AgentToolNames.CodeRead,
            AgentToolNames.CodeEdit,
            AgentToolNames.CodeDiff,
            AgentToolNames.CodeGlob,
            AgentToolNames.CodeGrep
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string[]> CategoryTerms =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["code"] =
            [
                "code", "source", "repo", "repository", "bug", "refactor", "compile", "build",
                "test", "diagnostic", "symbol", "patch", "program", "c#", ".net", "代码", "源码",
                "grep", "glob", "diff", "lsp", "仓库", "项目", "漏洞", "编译", "构建", "测试", "重构", "诊断", "修改"
            ],
            ["file"] =
            [
                "file", "folder", "directory", "path", "attachment", "upload", "download", "save",
                "open text", "read text", "文件", "目录", "路径", "附件", "上传", "下载", "保存",
                "打开文本", "读取文本"
            ],
            ["research"] =
            [
                "web", "internet", "search", "research", "source", "citation", "verify", "latest",
                "news", "website", "url", "网页", "网络", "联网", "搜索", "检索", "研究", "来源",
                "引用", "验证", "最新", "新闻", "链接"
            ],
            ["mcp"] =
            [
                "mcp", "connector", "integration", "plugin", "server", "resource", "连接器", "集成",
                "插件", "服务器", "资源"
            ],
            ["git"] =
            [
                "git", "commit", "branch", "merge", "pull request", "push", "tag", "release",
                "提交", "分支", "合并", "推送", "标签", "发布"
            ],
            ["task"] =
            [
                "task", "todo", "plan", "background", "worker", "long-running", "任务", "待办",
                "计划", "后台", "长期", "多步骤"
            ],
            ["memory"] =
            [
                "memory", "remember", "recall", "earlier", "saved context", "preference", "context",
                "记忆", "记住", "回忆", "之前", "偏好", "上下文"
            ],
            ["terminal"] =
            [
                "terminal", "shell", "powershell", "command", "execute", "run", "install", "package",
                "终端", "命令", "执行", "运行", "安装", "包管理"
            ],
            ["document"] =
            [
                "document", "docx", "word", "markdown", "pdf", "report", "文档", "报告", "论文",
                "合同", "简历"
            ],
            ["spreadsheet"] =
            [
                "spreadsheet", "xlsx", "excel", "csv", "table", "formula", "workbook", "表格",
                "制表", "电子表格", "公式", "工作簿"
            ],
            ["diagram"] =
            [
                "diagram", "chart", "graph", "svg", "png", "draw", "plot", "architecture",
                "流程图", "架构图", "图表", "绘图", "可视化", "统计图"
            ]
        };

    private readonly IAgentToolRegistry _registry;

    public ToolContextSelector(IAgentToolRegistry registry)
    {
        _registry = registry;
    }

    public IReadOnlyList<LlmToolDefinition> Catalog => BuildCatalog();

    public IReadOnlyList<ToolCatalogMatch> Search(string query, int limit = 12)
    {
        var text = NormalizeText(query);
        return BuildCatalog()
            .Select(definition => new
            {
                Definition = definition,
                Score = SearchScore(definition, text)
            })
            .Where(item => string.IsNullOrWhiteSpace(text) || item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 40))
            .Select(item => new ToolCatalogMatch(
                item.Definition.Name,
                item.Definition.Namespace,
                item.Definition.Category,
                item.Definition.Description,
                item.Definition.Annotations?.ReadOnly ?? false,
                item.Definition.Annotations?.Destructive ?? true,
                item.Definition.Annotations?.OpenWorld ?? false))
            .ToArray();
    }

    public ToolSelectionResult Select(ToolSelectionContext context)
    {
        var maxTools = Math.Clamp(context.MaxTools, 1, DefaultMaxTools);
        var catalog = BuildCatalog();
        var explicitNames = (context.ExplicitlyLoadedNames ?? Array.Empty<string>())
            .Select(AgentToolNames.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(context.LastFailedToolName))
            explicitNames.Add(AgentToolNames.Normalize(context.LastFailedToolName));

        var text = NormalizeText($"{context.UserRequest}\n{context.RecentContext}");
        var scores = catalog.Select(definition => new
            {
                Definition = definition,
                Score = Score(definition, text, explicitNames)
            })
            // Description token overlap is only a weak tie-breaker. Requiring
            // a category/name/explicit match prevents generic words such as
            // "use" from filling all 15 slots with unrelated tools.
            .Where(item => item.Score >= MinimumSelectionScore)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxTools)
            .Select(item => item.Definition with { Deferred = false })
            .ToArray();

        var selectedNames = scores
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deferred = catalog
            .Where(definition => !selectedNames.Contains(definition.Name))
            .Select(definition => definition with { Deferred = true })
            .ToArray();

        return new ToolSelectionResult(scores, deferred);
    }

    private IReadOnlyList<LlmToolDefinition> BuildCatalog()
    {
        var metadata = _registry.Metadata.ToDictionary(
            item => AgentToolNames.Normalize(item.Name),
            StringComparer.OrdinalIgnoreCase);

        return _registry.Definitions
            .Select(definition =>
            {
                metadata.TryGetValue(AgentToolNames.Normalize(definition.Name), out var meta);
                var category = definition.Category == "general"
                    ? InferCategory(definition.Name)
                    : definition.Category;
                var toolNamespace = definition.Namespace == "core"
                    ? InferNamespace(category)
                    : definition.Namespace;
                var annotations = definition.Annotations ?? (meta == null
                    ? null
                    : new LlmToolAnnotations(
                        meta.IsReadOnly,
                        meta.IsDestructive,
                        meta.IsReadOnly,
                        meta.IsOpenWorld,
                        meta.IsConcurrencySafe));

                return definition with
                {
                    Name = AgentToolNames.Normalize(definition.Name),
                    Namespace = toolNamespace,
                    Category = category,
                    Strict = definition.Strict || LlmToolSchema.IsStrictNormalizable(definition.InputSchema),
                    Deferred = !AlwaysAvailable.Contains(definition.Name),
                    Annotations = annotations
                };
            })
            .OrderBy(definition => definition.Namespace, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int Score(
        LlmToolDefinition definition,
        string text,
        IReadOnlySet<string> explicitNames)
    {
        var normalizedName = AgentToolNames.Normalize(definition.Name);
        if (explicitNames.Contains(normalizedName))
            return 10_000;
        if (AlwaysAvailable.Contains(normalizedName))
            return 5_000;

        var score = 0;
        var categoryMatchCount = CategoryTerms.TryGetValue(definition.Category, out var terms)
            ? terms.Count(term => ContainsTerm(text, term))
            : 0;
        if ((!AmbiguousBareToolNames.Contains(normalizedName) || categoryMatchCount > 0) &&
            (ContainsTerm(text, normalizedName) ||
             ContainsTerm(text, normalizedName.Replace('_', ' '))))
            score += 1_000;

        score += categoryMatchCount * 90;

        var description = NormalizeText(definition.Description);
        foreach (var token in Tokenize(text))
        {
            if (token.Length >= 3 && description.Contains(token, StringComparison.Ordinal))
                score += 4;
        }

        // A few paired tools are much more useful together than alone.
        if (definition.Category == "research" &&
            CategoryTerms["research"].Any(term => ContainsTerm(text, term)))
            score += normalizedName == AgentToolNames.WebSearch ? 80 :
                normalizedName == AgentToolNames.BrowserRead ? 70 : 0;
        if (definition.Category == "code" &&
            CategoryTerms["code"].Any(term => ContainsTerm(text, term)))
            score += normalizedName is AgentToolNames.CodeRead or AgentToolNames.CodeGrep or
                AgentToolNames.CodeGlob or AgentToolNames.CodeEdit or AgentToolNames.CodeDiagnostics
                ? 60
                : 0;

        return score;
    }

    private static int SearchScore(LlmToolDefinition definition, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 1;

        var normalizedName = NormalizeText(definition.Name);
        if (string.Equals(text, normalizedName, StringComparison.Ordinal))
            return 10_000;

        var score = 0;
        if (ContainsTerm(text, normalizedName) ||
            ContainsTerm(text, normalizedName.Replace('_', ' ')))
            score += 1_000;
        if (ContainsTerm(text, definition.Namespace))
            score += 250;
        if (ContainsTerm(text, definition.Category))
            score += 300;

        if (CategoryTerms.TryGetValue(definition.Category, out var terms))
            score += terms.Count(term => ContainsTerm(text, term)) * 90;

        var haystack = NormalizeText(
            $"{definition.Name} {definition.Namespace} {definition.Category} {definition.Description}");
        score += Tokenize(text).Count(token =>
            token.Length >= 2 && haystack.Contains(token, StringComparison.Ordinal)) * 12;
        return score;
    }

    private static string InferCategory(string name)
    {
        var normalized = AgentToolNames.Normalize(name);
        if (normalized.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase))
            return "mcp";
        if (normalized.StartsWith("research_", StringComparison.OrdinalIgnoreCase))
            return "research";
        if (normalized.StartsWith("task_", StringComparison.OrdinalIgnoreCase) ||
            normalized is AgentToolNames.TodoWrite or AgentToolNames.EnterPlanMode or AgentToolNames.ExitPlanMode)
            return "task";
        if (normalized.StartsWith("memory_", StringComparison.OrdinalIgnoreCase))
            return "memory";
        if (normalized.StartsWith("document_", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("docx", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("pdf", StringComparison.OrdinalIgnoreCase))
            return "document";
        if (normalized.StartsWith("spreadsheet_", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("xlsx", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("excel", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("csv", StringComparison.OrdinalIgnoreCase))
            return "spreadsheet";
        if (normalized.StartsWith("diagram_", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("chart", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("image", StringComparison.OrdinalIgnoreCase))
            return "diagram";

        return normalized switch
        {
            AgentToolNames.WebSearch or AgentToolNames.BrowserRead or AgentToolNames.HttpRequest => "research",
            AgentToolNames.Git => "git",
            AgentToolNames.TerminalExec or AgentToolNames.SandboxExec => "terminal",
            AgentToolNames.FileList or AgentToolNames.FileRead or AgentToolNames.FileWrite or
                AgentToolNames.FileSend or AgentToolNames.FileSearch or AgentToolNames.FileInfo or
                AgentToolNames.FileMkdir or AgentToolNames.FileMove or AgentToolNames.FileDelete or
                AgentToolNames.ReadPersistedOutput => "file",
            AgentToolNames.CodeRead or AgentToolNames.CodeGrep or AgentToolNames.CodeGlob or
                AgentToolNames.CodeEdit or AgentToolNames.CodeMultiEdit or AgentToolNames.CodeDiff or
                AgentToolNames.CodeApplyPatch or AgentToolNames.CodeRollback or
                AgentToolNames.CodeDiagnostics or AgentToolNames.CodeSymbols => "code",
            _ => "general"
        };
    }

    private static string InferNamespace(string category) => category switch
    {
        "research" => "research",
        "document" or "spreadsheet" or "diagram" => "artifact",
        "mcp" => "mcp",
        _ => "core"
    };

    private static bool ContainsTerm(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return false;

        var normalized = NormalizeText(term);
        // English single-word keywords must match a token, otherwise "test"
        // incorrectly routes "latest" to code tools. Phrases, CJK text, and
        // symbolic technology names retain substring matching.
        if (normalized.Any(character => character > 127) ||
            normalized.Any(character => !char.IsLetterOrDigit(character)))
            return text.Contains(normalized, StringComparison.Ordinal);

        var searchFrom = 0;
        while (searchFrom < text.Length)
        {
            var index = text.IndexOf(normalized, searchFrom, StringComparison.Ordinal);
            if (index < 0)
                return false;
            var beforeIsAsciiWord = index > 0 && IsAsciiWordCharacter(text[index - 1]);
            var afterIndex = index + normalized.Length;
            var afterIsAsciiWord = afterIndex < text.Length && IsAsciiWordCharacter(text[afterIndex]);
            if (!beforeIsAsciiWord && !afterIsAsciiWord)
                return true;
            searchFrom = index + 1;
        }
        return false;
    }

    private static bool IsAsciiWordCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static IEnumerable<string> Tokenize(string value) =>
        value.Split(
            [' ', '\r', '\n', '\t', ',', '.', ':', ';', '/', '\\', '-', '_', '(', ')', '[', ']'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal);

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormKC))
            builder.Append(char.IsWhiteSpace(character) ? ' ' : character);
        return builder.ToString();
    }
}

public static class ToolCatalogPromotion
{
    public static IReadOnlyList<string> ExtractRegisteredNames(
        AgentToolResult result,
        IAgentToolRegistry registry,
        int limit = 40)
    {
        try
        {
            var json = result.StructuredContent == null
                ? result.Output
                : JsonSerializer.Serialize(result.StructuredContent);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var match in document.RootElement.EnumerateArray().Take(Math.Clamp(limit, 1, 40)))
            {
                if (match.ValueKind != JsonValueKind.Object)
                    continue;
                if ((!match.TryGetProperty("name", out var nameElement) &&
                     !match.TryGetProperty("Name", out nameElement)) ||
                    nameElement.ValueKind != JsonValueKind.String)
                    continue;
                var name = AgentToolNames.Normalize(nameElement.GetString() ?? string.Empty);
                if (registry.TryGet(name, out _) && seen.Add(name))
                    names.Add(name);
            }
            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
