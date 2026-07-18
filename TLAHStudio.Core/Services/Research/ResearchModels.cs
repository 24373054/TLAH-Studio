using System.Text.Json.Serialization;

namespace TLAHStudio.Core.Services.Research;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResearchMode
{
    Quick,
    Balanced,
    Deep
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResearchRecency
{
    Any,
    Day,
    Week,
    Month,
    Year
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResearchCoverage
{
    Insufficient,
    Partial,
    Strong
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResearchContentKind
{
    Html,
    Text,
    Pdf
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResearchErrorKind
{
    InvalidRequest,
    SecurityPolicy,
    Dns,
    Timeout,
    RateLimited,
    HttpStatus,
    Network,
    TooLarge,
    UnsupportedContent,
    Parse,
    Cancelled,
    Unknown
}

public sealed record ResearchFilters(
    IReadOnlyList<string>? AllowedDomains = null,
    IReadOnlyList<string>? BlockedDomains = null,
    ResearchRecency Recency = ResearchRecency.Any,
    string? Language = null,
    int MaxResults = 10);

/// <summary>
/// Invocation context shared by the in-app research UI and agent tools.
/// A caller sets <see cref="HasAuthorization"/> only after the normal approval
/// or policy flow has explicitly authorized unrestricted public-web access.
/// </summary>
public sealed record ResearchWorkspace(
    string PermissionMode = AgentPermissionModes.RequestApproval,
    bool HasAuthorization = false,
    string? ArtifactDirectory = null,
    bool CreateReportArtifact = false,
    string ReportFileName = "research-report.md",
    int TimeoutSeconds = 120)
{
    public bool BypassNetworkRestrictions =>
        HasAuthorization || AgentPermissionModes.IsBypass(PermissionMode);
}

public sealed record ResearchSearchSource(
    string Title,
    Uri Url,
    string Domain,
    string Snippet,
    DateTimeOffset? PublishedAt = null,
    string SearchProvider = "DuckDuckGo");

public sealed record ResearchSearchResult(
    string Query,
    ResearchMode Mode,
    IReadOnlyList<ResearchSearchSource> Sources,
    IReadOnlyList<ResearchFailure> Failures,
    int Attempts);

public sealed record ResearchLink(string Text, Uri Url);

public sealed record ResearchPage(
    Uri RequestedUrl,
    Uri FinalUrl,
    int HttpStatus,
    ResearchContentKind ContentKind,
    string ContentType,
    string Title,
    string Description,
    string Language,
    DateTimeOffset? PublishedAt,
    string Text,
    IReadOnlyList<ResearchLink> Links,
    bool Truncated,
    int AttemptCount);

public sealed record ResearchEvidence(
    string Title,
    Uri Url,
    string Domain,
    string Excerpt,
    DateTimeOffset? PublishedAt,
    double AuthoritySignalScore,
    double RecencyScore,
    double RelevanceScore,
    double OverallScore,
    IReadOnlyList<string> AuthoritySignals,
    ResearchContentKind ContentKind);

public sealed record ResearchConflict(
    string Kind,
    string Description,
    IReadOnlyList<Uri> Sources,
    bool RequiresManualVerification = true);

public sealed record ResearchFailure(
    ResearchErrorKind Kind,
    string Message,
    Uri? Url = null,
    int? HttpStatus = null,
    bool Retryable = false,
    int Attempts = 1);

public sealed record ResearchReportArtifact(
    string FullPath,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256);

/// <summary>
/// A structured evidence pack. It deliberately contains evidence and coverage
/// diagnostics rather than an automatically invented factual conclusion.
/// </summary>
public sealed record ResearchWorkbenchResult(
    string Query,
    ResearchMode Mode,
    ResearchCoverage Coverage,
    IReadOnlyList<ResearchEvidence> Sources,
    IReadOnlyList<ResearchConflict> Conflicts,
    IReadOnlyList<ResearchFailure> Failures,
    IReadOnlyList<string> Warnings,
    int IndependentDomainCount,
    string ReportMarkdown,
    ResearchReportArtifact? ReportArtifact = null);
