using System.Diagnostics;
using System.Text.Json;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.Research;

namespace TLAHStudio.Core.Services;

/// <summary>
/// Builds a multi-source evidence pack. The same capability is also exposed
/// through IResearchWorkbenchService for direct WinUI usage.
/// </summary>
public sealed class ResearchVerifyAgentTool : IAgentTool
{
    public const string ToolName = AgentToolNames.ResearchVerify;
    private readonly IResearchWorkbenchService _research;
    private readonly ISandboxCommandService _sandbox;

    public ResearchVerifyAgentTool(
        IResearchWorkbenchService research,
        ISandboxCommandService sandbox)
    {
        _research = research;
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        ToolName,
        "Research a claim or question across independent public sources. Returns an evidence pack with exact excerpts, dates, heuristic quality signals, retrieval failures, coverage gaps, and potential discrepancies. It never invents a conclusion.",
        new Dictionary<string, object>
        {
            ["query"] = AgentToolSupport.StringProperty("Question or claim to investigate."),
            ["reason"] = AgentToolSupport.StringProperty("Why cross-source research is needed."),
            ["mode"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "quick", "balanced", "deep" },
                ["description"] = "quick fetches up to 3 sources, balanced up to 6, and deep up to 10."
            },
            ["allowed_domains"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = AgentToolSupport.StringProperty("Allowed domain."),
                ["description"] = "Optional source domains to include."
            },
            ["blocked_domains"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = AgentToolSupport.StringProperty("Blocked domain."),
                ["description"] = "Optional source domains to exclude."
            },
            ["recency"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "any", "day", "week", "month", "year" },
                ["description"] = "Preferred source recency."
            },
            ["language"] = AgentToolSupport.StringProperty("Optional language/locale such as en-US or zh-CN."),
            ["max_sources"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Maximum candidate sources (1-20)."
            },
            ["create_report"] = new Dictionary<string, object>
            {
                ["type"] = "boolean",
                ["description"] = "Create an attachable Markdown evidence report in the chat workspace. Defaults to true."
            }
        },
        ["query"]) with
    {
        Namespace = "research",
        Category = "research",
        Strict = true,
        Deferred = true,
        InputExamples =
        [
            new Dictionary<string, object>
            {
                ["query"] = "Compare the current Windows App SDK support policy across official sources",
                ["mode"] = "deep",
                ["allowed_domains"] = new[] { "learn.microsoft.com", "github.com" },
                ["create_report"] = true
            }
        ],
        OutputSchema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["query"] = new Dictionary<string, object> { ["type"] = "string" },
                ["mode"] = new Dictionary<string, object> { ["type"] = "string" },
                ["coverage"] = new Dictionary<string, object> { ["type"] = "string" },
                ["independentDomainCount"] = new Dictionary<string, object> { ["type"] = "integer" },
                ["sources"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "object" }
                },
                ["conflicts"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "object" }
                },
                ["failures"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "object" }
                },
                ["warnings"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "string" }
                },
                ["reportPath"] = new Dictionary<string, object> { ["type"] = new[] { "string", "null" } }
            },
            ["required"] = new[]
            {
                "query", "mode", "coverage", "independentDomainCount",
                "sources", "conflicts", "failures", "warnings", "reportPath"
            },
            ["additionalProperties"] = false
        },
        Annotations = new LlmToolAnnotations(
            ReadOnly: false,
            Destructive: false,
            Idempotent: false,
            OpenWorld: true,
            ConcurrencySafe: false)
    };

    public bool RequiresApproval => true;

    public AgentToolMetadata Metadata { get; } = new(
        ToolName,
        RequiresApproval: true,
        IsReadOnly: false,
        IsConcurrencySafe: false,
        IsDestructive: false,
        AgentToolRenderHints.Network,
        MaxResultSizeChars: 50_000,
        AgentToolResultPersistenceModes.Artifact,
        IsOpenWorld: true,
        UserFacingName: "Verify with sources",
        ActivityDescription: "Cross-checking public sources");

    public async Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var query = AgentToolSupport.GetString(root, "query").Trim();
            if (query.Length == 0)
                return new AgentToolResult(false, string.Empty, "The query argument is required.");
            var mode = ReadEnum(root, "mode", ResearchMode.Balanced);
            var recency = ReadEnum(root, "recency", ResearchRecency.Any);
            var defaultSources = mode switch
            {
                ResearchMode.Quick => 6,
                ResearchMode.Balanced => 10,
                _ => 16
            };
            var maxSources = root.TryGetProperty("max_sources", out var maxElement) &&
                             maxElement.TryGetInt32(out var requestedMax)
                ? Math.Clamp(requestedMax, 1, 20)
                : defaultSources;
            var createReport = !root.TryGetProperty("create_report", out var reportElement) ||
                               reportElement.ValueKind != JsonValueKind.False;
            var sandboxRoot = _sandbox.GetSandboxRoot(context.ChatId);
            var reportDirectory = Path.Combine(sandboxRoot, ".tlah_research");
            var result = await _research.ResearchAsync(
                query,
                mode,
                new ResearchFilters(
                    ReadStringArray(root, "allowed_domains"),
                    ReadStringArray(root, "blocked_domains"),
                    recency,
                    AgentToolSupport.GetString(root, "language"),
                    maxSources),
                new ResearchWorkspace(
                    context.EffectivePermissionMode,
                    ArtifactDirectory: reportDirectory,
                    CreateReportArtifact: createReport,
                    ReportFileName: $"research-{context.InvocationId:N}.md",
                    TimeoutSeconds: context.TimeoutSeconds),
                ct);

            IReadOnlyList<AgentToolArtifact>? artifacts = null;
            if (result.ReportArtifact != null)
            {
                artifacts =
                [
                    new AgentToolArtifact(
                        Path.GetRelativePath(sandboxRoot, result.ReportArtifact.FullPath),
                        result.ReportArtifact.ContentType,
                        result.ReportArtifact.SizeBytes,
                        result.ReportArtifact.Sha256)
                ];
            }

            var warning = result.Warnings.Count == 0
                ? null
                : string.Join(" ", result.Warnings);
            var structured = new
            {
                query = result.Query,
                mode = result.Mode.ToString().ToLowerInvariant(),
                coverage = result.Coverage.ToString().ToLowerInvariant(),
                independentDomainCount = result.IndependentDomainCount,
                sources = result.Sources.Select((source, index) => new
                {
                    citationId = $"source-{index + 1}",
                    source.Title,
                    url = source.Url.AbsoluteUri,
                    source.Domain,
                    source.Excerpt,
                    publishedAt = source.PublishedAt?.ToString("O"),
                    source.AuthoritySignalScore,
                    source.RecencyScore,
                    source.RelevanceScore,
                    source.OverallScore,
                    contentKind = source.ContentKind.ToString().ToLowerInvariant(),
                    provider = source.SearchProvider,
                    providerUrl = source.SearchProviderUrl?.AbsoluteUri,
                    license = source.LicenseName,
                    licenseUrl = source.LicenseUrl?.AbsoluteUri
                }),
                result.Conflicts,
                result.Failures,
                result.Warnings,
                reportPath = result.ReportArtifact?.FullPath
            };
            var toolSources = result.Sources.Select((source, index) =>
                new AgentToolSource(
                    source.Url.AbsoluteUri,
                    source.Title,
                    source.SearchProvider,
                    DateTime.UtcNow,
                    $"source-{index + 1}",
                    source.SearchProviderUrl?.AbsoluteUri,
                    source.LicenseName,
                    source.LicenseUrl?.AbsoluteUri)).ToArray();
            return new AgentToolResult(
                result.Sources.Count > 0,
                AgentToolSupport.Limit(result.ReportMarkdown, context.MaxOutputChars),
                result.Sources.Count > 0
                    ? null
                    : "No independently fetched evidence was available. Review the retrieval failures in the report.",
                artifacts,
                warning,
                StructuredContent: structured,
                ErrorCode: result.Sources.Count > 0 ? null : "insufficient_evidence",
                Retryable: result.Sources.Count == 0 && result.Failures.Any(failure => failure.Retryable),
                Sources: toolSources,
                DurationMs: stopwatch.ElapsedMilliseconds,
                Diagnostics: new Dictionary<string, object>
                {
                    ["coverage"] = result.Coverage.ToString().ToLowerInvariant(),
                    ["independent_domains"] = result.IndependentDomainCount,
                    ["retrieval_failures"] = result.Failures.Count
                });
        }
        catch (ResearchServiceException ex)
        {
            return new AgentToolResult(
                false,
                string.Empty,
                $"[{ex.Failure.Kind}] {ex.Failure.Message} Retryable: {ex.Failure.Retryable}.",
                ErrorCode: ex.Failure.Kind.ToString().ToLowerInvariant(),
                Retryable: ex.Failure.Retryable,
                DurationMs: stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentToolResult(
                false,
                string.Empty,
                ex.Message,
                ErrorCode: "research_failed",
                DurationMs: stopwatch.ElapsedMilliseconds);
        }
    }

    private static TEnum ReadEnum<TEnum>(JsonElement root, string name, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(AgentToolSupport.GetString(root, name), true, out var value)
            ? value
            : fallback;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray()
            : [];
}
