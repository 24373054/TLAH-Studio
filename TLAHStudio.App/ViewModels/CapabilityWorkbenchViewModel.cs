using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Artifacts;
using TLAHStudio.Core.Services.Observability;
using TLAHStudio.Core.Services.Research;
using TLAHStudio.Core.Services.Workspace;

namespace TLAHStudio.App.ViewModels;

public partial class CapabilityWorkbenchViewModel : ObservableObject
{
    private readonly IArtifactWorkbenchService _artifacts;
    private readonly IResearchWorkbenchService _research;
    private readonly IWorkspaceRootService _workspaceRoots;
    private readonly IAppStateService _appState;
    private readonly IToolQualityService _toolQuality;
    private Guid? _loadedChatId;

    public CapabilityWorkbenchViewModel(
        IArtifactWorkbenchService artifacts,
        IResearchWorkbenchService research,
        IWorkspaceRootService workspaceRoots,
        IAppStateService appState,
        IToolQualityService toolQuality)
    {
        _artifacts = artifacts;
        _research = research;
        _workspaceRoots = workspaceRoots;
        _appState = appState;
        _toolQuality = toolQuality;
    }

    public IReadOnlyList<string> ResearchModes { get; } = ["Quick", "Balanced", "Deep"];
    public IReadOnlyList<string> ResearchRecencyOptions { get; } = ["Any time", "Past day", "Past week", "Past month", "Past year"];
    public IReadOnlyList<string> DocumentFormats { get; } = ["DOCX", "PDF", "Markdown"];
    public IReadOnlyList<string> SpreadsheetChartTypes { get; } = ["bar", "line"];
    public IReadOnlyList<string> DiagramTypes { get; } = ["Flowchart", "Architecture", "Bar chart", "Line chart"];
    public IReadOnlyList<string> DiagramThemes { get; } = ["Light", "Dark"];

    public ObservableCollection<WorkbenchArtifactItem> ResultArtifacts { get; } = [];
    public ObservableCollection<ResearchSourceItem> ResearchSources { get; } = [];
    public ObservableCollection<ToolQualityRow> ToolQualityRows { get; } = [];
    public bool HasCurrentChat => _appState.CurrentChatId != null;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string workspaceDisplay = "Preparing workspace…";
    [ObservableProperty] private string statusMessage = "Choose a capability to begin.";
    [ObservableProperty] private string resultSummary = string.Empty;
    [ObservableProperty] private string resultPreview = string.Empty;
    [ObservableProperty] private string? primaryResultPath;
    [ObservableProperty] private string toolQualitySummary = "No local tool calls yet.";
    [ObservableProperty] private string toolQualityDetail = string.Empty;

    [ObservableProperty] private string researchQuery = string.Empty;
    [ObservableProperty] private string selectedResearchMode = "Balanced";
    [ObservableProperty] private string selectedResearchRecency = "Any time";
    [ObservableProperty] private string researchAllowedDomains = string.Empty;
    [ObservableProperty] private string researchBlockedDomains = string.Empty;
    [ObservableProperty] private string researchLanguage = string.Empty;

    [ObservableProperty] private string spreadsheetTitle = "Data workbook";
    [ObservableProperty] private string spreadsheetFileName = "workbook.xlsx";
    [ObservableProperty] private string spreadsheetSheetName = "Data";
    [ObservableProperty] private string spreadsheetData =
        "Category\tValue\nDesign\t42\nEngineering\t68\nResearch\t54";
    [ObservableProperty] private bool spreadsheetCreateChart = true;
    [ObservableProperty] private string spreadsheetChartType = "bar";
    [ObservableProperty] private string spreadsheetChartTheme = "Light";

    [ObservableProperty] private string documentTitle = "Project brief";
    [ObservableProperty] private string documentFileName = "project-brief.docx";
    [ObservableProperty] private string selectedDocumentFormat = "DOCX";
    [ObservableProperty] private string documentAuthor = string.Empty;
    [ObservableProperty] private string documentContent =
        "# Overview\nWrite the main purpose here.\n\n# Highlights\n- First highlight\n- Second highlight";

    [ObservableProperty] private string diagramTitle = "System overview";
    [ObservableProperty] private string diagramFileName = "system-overview";
    [ObservableProperty] private string selectedDiagramType = "Flowchart";
    [ObservableProperty] private string selectedDiagramTheme = "Light";
    [ObservableProperty] private string diagramData =
        "User -> TLAH Studio\nTLAH Studio -> Agent runtime\nAgent runtime -> Tools\nTools -> Workspace";

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var (chatId, root) = await EnsureWorkspaceAsync(ct);
        if (_loadedChatId != chatId)
        {
            _loadedChatId = chatId;
            ResultArtifacts.Clear();
            ResearchSources.Clear();
            ResultSummary = string.Empty;
            ResultPreview = string.Empty;
            PrimaryResultPath = null;
        }
        WorkspaceDisplay = root;
        await RefreshToolQualityAsync(ct);
        StatusMessage = "Ready. Results will be saved inside this workspace.";
    }

    public async Task RefreshToolQualityAsync(CancellationToken ct = default)
    {
        var snapshot = await _toolQuality.LoadAsync(30, ct);
        ToolQualityRows.Clear();
        foreach (var row in snapshot.Tools.Take(20))
            ToolQualityRows.Add(row);
        ToolQualitySummary = snapshot.TotalCalls == 0
            ? "No local tool calls in the last 30 days."
            : $"{snapshot.TotalCalls} calls · {snapshot.SuccessRate:0.0}% execution success";
        ToolQualityDetail = snapshot.TotalCalls == 0
            ? "Metrics appear after the agent uses tools."
            : $"Completed {snapshot.Completed} · Failed {snapshot.Failed} · Denied {snapshot.Denied} · " +
              $"Shell fallback {snapshot.ShellFallbackRate:0.0}% · Catalog search {snapshot.ToolSearchRate:0.0}%";
    }

    public async Task RunResearchAsync(CancellationToken ct = default)
    {
        ResearchSources.Clear();
        await RunBusyAsync("Researching across independent sources…", async token =>
        {
            if (string.IsNullOrWhiteSpace(ResearchQuery))
                throw new InvalidOperationException("Enter a research question first.");

            var (_, root) = await EnsureWorkspaceAsync(token);
            var mode = SelectedResearchMode switch
            {
                "Quick" => ResearchMode.Quick,
                "Deep" => ResearchMode.Deep,
                _ => ResearchMode.Balanced
            };
            var filters = new ResearchFilters(
                SplitLines(ResearchAllowedDomains),
                SplitLines(ResearchBlockedDomains),
                SelectedResearchRecency switch
                {
                    "Past day" => ResearchRecency.Day,
                    "Past week" => ResearchRecency.Week,
                    "Past month" => ResearchRecency.Month,
                    "Past year" => ResearchRecency.Year,
                    _ => ResearchRecency.Any
                },
                NullIfWhiteSpace(ResearchLanguage),
                mode == ResearchMode.Deep ? 16 : mode == ResearchMode.Quick ? 6 : 10);
            var workspace = new ResearchWorkspace(
                AgentPermissionModes.BypassPermissions,
                HasAuthorization: true,
                ArtifactDirectory: Path.Combine(root, "artifacts"),
                CreateReportArtifact: true,
                ReportFileName: SafeFileName($"research-{DateTime.Now:yyyyMMdd-HHmmss}.md"),
                TimeoutSeconds: mode == ResearchMode.Deep ? 240 : 120);

            var result = await _research.ResearchAsync(
                ResearchQuery.Trim(),
                mode,
                filters,
                workspace,
                token);

            ResearchSources.Clear();
            foreach (var source in result.Sources)
            {
                ResearchSources.Add(new ResearchSourceItem(
                    source.Title,
                    source.Domain,
                    source.Url,
                    source.Excerpt,
                    source.OverallScore,
                    source.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd")));
            }

            ResultArtifacts.Clear();
            PrimaryResultPath = result.ReportArtifact?.FullPath;
            if (result.ReportArtifact is { } report)
            {
                ResultArtifacts.Add(new WorkbenchArtifactItem(
                    report.FileName,
                    report.FullPath,
                    report.ContentType,
                    report.SizeBytes));
            }

            ResultSummary =
                $"{result.Coverage} evidence · {result.IndependentDomainCount} independent domains · " +
                $"{result.Sources.Count} sources";
            ResultPreview = result.ReportMarkdown;
            StatusMessage = result.Coverage == ResearchCoverage.Insufficient
                ? "Research finished, but the evidence was insufficient. Review the warnings and sources."
                : "Research finished. The evidence report and full source list are ready.";
        }, ct);
    }

    public async Task CreateSpreadsheetAsync(CancellationToken ct = default)
    {
        await RunBusyAsync("Building and validating the workbook…", async token =>
        {
            var (chatId, root) = await EnsureWorkspaceAsync(token);
            var table = ParseTable(SpreadsheetData);
            if (table.Count < 2)
                throw new InvalidOperationException("Paste a header row and at least one data row.");

            var headers = table[0];
            var rows = table.Skip(1)
                .Select(row => (IReadOnlyList<JsonElement>)row.Select(ToJsonValue).ToArray())
                .ToArray();
            var output = RelativeArtifactPath(SpreadsheetFileName, ".xlsx");
            SpreadsheetChartRequest? chart = null;
            if (SpreadsheetCreateChart && headers.Count >= 2)
            {
                chart = new SpreadsheetChartRequest
                {
                    Type = SpreadsheetChartType,
                    Title = SpreadsheetTitle,
                    CategoryColumn = headers[0],
                    ValueColumns = headers.Skip(1).Take(3).ToArray(),
                    Theme = SpreadsheetChartTheme.ToLowerInvariant()
                };
            }

            var result = await _artifacts.CreateSpreadsheetAsync(
                new ArtifactExecutionScope(chatId, AgentPermissionModes.BypassPermissions),
                new SpreadsheetCreateRequest
                {
                    Path = output,
                    SheetName = string.IsNullOrWhiteSpace(SpreadsheetSheetName) ? "Data" : SpreadsheetSheetName.Trim(),
                    Title = NullIfWhiteSpace(SpreadsheetTitle),
                    Headers = headers,
                    Rows = rows,
                    FreezeHeader = true,
                    AutoFit = true,
                    ZebraRows = true,
                    Chart = chart,
                    Overwrite = false
                },
                token);
            ApplyArtifactResult(result, root, BuildTablePreview(table));
        }, ct);
    }

    public async Task CreateDocumentAsync(CancellationToken ct = default)
    {
        await RunBusyAsync("Creating and validating the document…", async token =>
        {
            var (chatId, root) = await EnsureWorkspaceAsync(token);
            var extension = SelectedDocumentFormat switch
            {
                "PDF" => ".pdf",
                "Markdown" => ".md",
                _ => ".docx"
            };
            var result = await _artifacts.CreateDocumentAsync(
                new ArtifactExecutionScope(chatId, AgentPermissionModes.BypassPermissions),
                new DocumentCreateRequest
                {
                    Path = RelativeArtifactPath(DocumentFileName, extension),
                    Title = string.IsNullOrWhiteSpace(DocumentTitle) ? "Document" : DocumentTitle.Trim(),
                    Author = NullIfWhiteSpace(DocumentAuthor),
                    Header = NullIfWhiteSpace(DocumentTitle),
                    Footer = $"Created with TLAH Studio · {DateTime.Now:yyyy-MM-dd}",
                    Sections = ParseDocumentSections(DocumentContent),
                    Overwrite = false
                },
                token);
            ApplyArtifactResult(
                result,
                root,
                $"{(string.IsNullOrWhiteSpace(DocumentTitle) ? "Document" : DocumentTitle.Trim())}\n\n{DocumentContent.Trim()}");
        }, ct);
    }

    public async Task CreateDiagramAsync(CancellationToken ct = default)
    {
        await RunBusyAsync("Laying out and rendering the diagram…", async token =>
        {
            var (chatId, root) = await EnsureWorkspaceAsync(token);
            var type = SelectedDiagramType switch
            {
                "Architecture" => "architecture",
                "Bar chart" => "bar",
                "Line chart" => "line",
                _ => "flowchart"
            };
            var nodes = new List<DiagramNodeRequest>();
            var edges = new List<DiagramEdgeRequest>();
            var labels = new List<string>();
            var values = new List<double>();

            if (type is "bar" or "line")
            {
                foreach (var line in NonEmptyLines(DiagramData))
                {
                    var pair = ParseDelimitedLine(line, ',');
                    if (pair.Count < 2 ||
                        !double.TryParse(pair[^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                        continue;
                    labels.Add(pair[0]);
                    values.Add(value);
                }
                if (labels.Count == 0)
                    throw new InvalidOperationException("Enter chart data as one “Label, value” pair per line.");
            }
            else
            {
                var nodeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in NonEmptyLines(DiagramData))
                {
                    var parts = line.Split("->", 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
                        continue;
                    var from = AddDiagramNode(parts[0], nodeIds, nodes);
                    var to = AddDiagramNode(parts[1], nodeIds, nodes);
                    edges.Add(new DiagramEdgeRequest { From = from, To = to });
                }
                if (edges.Count == 0)
                    throw new InvalidOperationException("Enter at least one relationship such as “Research -> Report”.");
            }

            var result = await _artifacts.CreateDiagramAsync(
                new ArtifactExecutionScope(chatId, AgentPermissionModes.BypassPermissions),
                new DiagramCreateRequest
                {
                    Path = RelativeArtifactBase(DiagramFileName),
                    Type = type,
                    Title = string.IsNullOrWhiteSpace(DiagramTitle) ? "Diagram" : DiagramTitle.Trim(),
                    Nodes = nodes,
                    Edges = edges,
                    Labels = labels,
                    Series = values.Count == 0
                        ? []
                        : [new DiagramSeriesRequest { Name = DiagramTitle, Values = values }],
                    Formats = ["svg", "png"],
                    Theme = SelectedDiagramTheme.ToLowerInvariant(),
                    Width = 1200,
                    Height = 800,
                    Scale = 2,
                    Overwrite = false
                },
                token);
            ApplyArtifactResult(
                result,
                root,
                $"{(string.IsNullOrWhiteSpace(DiagramTitle) ? "Diagram" : DiagramTitle.Trim())}\n\n{DiagramData.Trim()}");
        }, ct);
    }

    private async Task RunBusyAsync(
        string activity,
        Func<CancellationToken, Task> action,
        CancellationToken ct)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusMessage = activity;
        ResultSummary = string.Empty;
        ResultPreview = string.Empty;
        ResultArtifacts.Clear();
        PrimaryResultPath = null;
        try
        {
            await action(ct);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Operation cancelled. No unfinished result was published.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not complete this operation: {ex.Message}";
            ResultPreview =
                "Check the active workspace and input, then try again. Existing files were left unchanged.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyArtifactResult(
        ArtifactWorkbenchResult result,
        string root,
        string? readablePreview = null)
    {
        if (!result.Success)
            throw new InvalidOperationException(result.Error ?? "The artifact could not be created.");

        ResultArtifacts.Clear();
        foreach (var artifact in result.Artifacts)
        {
            var path = Path.IsPathRooted(artifact.RelativePath)
                ? artifact.RelativePath
                : Path.GetFullPath(Path.Combine(root, artifact.RelativePath));
            ResultArtifacts.Add(new WorkbenchArtifactItem(
                Path.GetFileName(path),
                path,
                artifact.ContentType,
                artifact.SizeBytes));
        }
        PrimaryResultPath = ResultArtifacts.FirstOrDefault()?.FullPath;
        ResultSummary = result.Summary;
        ResultPreview = string.IsNullOrWhiteSpace(readablePreview)
            ? JsonSerializer.Serialize(
                result.StructuredData,
                new JsonSerializerOptions { WriteIndented = true })
            : readablePreview;
        StatusMessage = "Created and validated successfully. Open the result or its folder below.";
    }

    private async Task<(Guid ChatId, string Root)> EnsureWorkspaceAsync(CancellationToken ct)
    {
        var chatId = _appState.CurrentChatId
            ?? throw new InvalidOperationException("Create or select a chat before opening the workbench.");
        var workspace = await _workspaceRoots.GetRootAsync(chatId, ct);
        Directory.CreateDirectory(workspace.RootPath);
        return (chatId, workspace.RootPath);
    }

    private static IReadOnlyList<DocumentSectionRequest> ParseDocumentSections(string text)
    {
        var sections = new List<DocumentSectionRequest>();
        string? heading = null;
        var level = 1;
        var paragraphs = new List<string>();
        var bullets = new List<string>();
        var numbered = new List<string>();

        void Flush()
        {
            if (heading == null && paragraphs.Count == 0 && bullets.Count == 0 && numbered.Count == 0)
                return;
            sections.Add(new DocumentSectionRequest
            {
                Heading = heading,
                Level = level,
                Paragraphs = paragraphs.ToArray(),
                Bullets = bullets.ToArray(),
                Numbered = numbered.ToArray()
            });
            heading = null;
            level = 1;
            paragraphs = [];
            bullets = [];
            numbered = [];
        }

        foreach (var raw in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                Flush();
                level = Math.Clamp(line.TakeWhile(ch => ch == '#').Count(), 1, 6);
                heading = line[level..].Trim();
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                bullets.Add(line[2..].Trim());
            }
            else if (line.Length > 2 &&
                     char.IsDigit(line[0]) &&
                     (line[1] == '.' || line[1] == ')'))
            {
                numbered.Add(line[2..].Trim());
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                paragraphs.Add(line);
            }
        }
        Flush();
        return sections.Count == 0
            ? [new DocumentSectionRequest { Paragraphs = [text.Trim()] }]
            : sections;
    }

    private static List<IReadOnlyList<string>> ParseTable(string text)
    {
        var lines = NonEmptyLines(text).ToArray();
        if (lines.Length == 0)
            return [];
        var delimiter = new[] { '\t', ',', ';', '|' }
            .Select(candidate => new
            {
                Delimiter = candidate,
                Counts = lines
                    .Take(20)
                    .Select(line => CountUnquotedDelimiters(line, candidate))
                    .ToArray()
            })
            .Where(candidate => candidate.Counts.Any(count => count > 0))
            .OrderByDescending(candidate =>
                candidate.Counts.Count(count => count == candidate.Counts.Max()))
            .ThenByDescending(candidate => candidate.Counts.Sum())
            .ThenBy(candidate => candidate.Delimiter == '\t' ? 0 : 1)
            .Select(candidate => candidate.Delimiter)
            .FirstOrDefault('|');
        return lines.Select(line => (IReadOnlyList<string>)ParseDelimitedLine(line, delimiter)).ToList();
    }

    private static int CountUnquotedDelimiters(string line, char delimiter)
    {
        var quoted = false;
        var count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                    i++;
                else
                    quoted = !quoted;
            }
            else if (!quoted && line[i] == delimiter)
            {
                count++;
            }
        }
        return count;
    }

    private static List<string> ParseDelimitedLine(string line, char delimiter)
    {
        var values = new List<string>();
        var buffer = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    buffer.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == delimiter && !quoted)
            {
                values.Add(buffer.ToString().Trim());
                buffer.Clear();
            }
            else
            {
                buffer.Append(ch);
            }
        }
        values.Add(buffer.ToString().Trim());
        return values;
    }

    private static JsonElement ToJsonValue(string value)
    {
        var unsigned = value.TrimStart('+', '-');
        var preserveAsText =
            (unsigned.Length > 1 && unsigned[0] == '0' && char.IsDigit(unsigned[1])) ||
            (unsigned.Length > 15 && unsigned.All(char.IsDigit));
        if (preserveAsText)
            return JsonSerializer.SerializeToElement(value);
        if (bool.TryParse(value, out var boolean))
            return JsonSerializer.SerializeToElement(boolean);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return JsonSerializer.SerializeToElement(integer);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonSerializer.SerializeToElement(number);
        if (DateTime.TryParseExact(
                value,
                ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssK"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var date))
            return JsonSerializer.SerializeToElement(date);
        return JsonSerializer.SerializeToElement(value);
    }

    private static string BuildTablePreview(IReadOnlyList<IReadOnlyList<string>> table)
    {
        var preview = new StringBuilder();
        foreach (var row in table.Take(16))
            preview.AppendLine(string.Join("  |  ", row));
        if (table.Count > 16)
            preview.AppendLine($"… {table.Count - 16} more row(s) in the workbook");
        return preview.ToString().TrimEnd();
    }

    private static string AddDiagramNode(
        string label,
        IDictionary<string, string> ids,
        ICollection<DiagramNodeRequest> nodes)
    {
        if (ids.TryGetValue(label, out var existing))
            return existing;
        var id = $"node-{ids.Count + 1}";
        ids[label] = id;
        nodes.Add(new DiagramNodeRequest { Id = id, Label = label });
        return id;
    }

    private static IReadOnlyList<string> SplitLines(string value) =>
        value.Replace(",", Environment.NewLine)
            .Replace(";", Environment.NewLine)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<string> NonEmptyLines(string value) =>
        value.Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string RelativeArtifactPath(string fileName, string requiredExtension)
    {
        var safe = SafeFileName(fileName);
        if (!safe.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
            safe = Path.GetFileNameWithoutExtension(safe) + requiredExtension;
        return Path.Combine("artifacts", safe).Replace('\\', '/');
    }

    private static string RelativeArtifactBase(string fileName)
    {
        var safe = Path.GetFileNameWithoutExtension(SafeFileName(fileName));
        if (string.IsNullOrWhiteSpace(safe))
            safe = "diagram";
        return Path.Combine("artifacts", safe).Replace('\\', '/');
    }

    private static string SafeFileName(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "result" : value.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars())
            value = value.Replace(ch, '-');
        return value;
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record WorkbenchArtifactItem(
    string Name,
    string FullPath,
    string ContentType,
    long SizeBytes)
{
    public string SizeText => SizeBytes switch
    {
        >= 1024 * 1024 => $"{SizeBytes / 1024d / 1024d:0.0} MB",
        >= 1024 => $"{SizeBytes / 1024d:0.0} KB",
        _ => $"{SizeBytes} B"
    };
}

public sealed record ResearchSourceItem(
    string Title,
    string Domain,
    Uri Uri,
    string Excerpt,
    double Score,
    string? Published)
{
    public string Url => Uri.ToString();
    public string Metadata => string.IsNullOrWhiteSpace(Published)
        ? $"{Domain} · relevance {Score:0.00}"
        : $"{Domain} · {Published} · relevance {Score:0.00}";
}
