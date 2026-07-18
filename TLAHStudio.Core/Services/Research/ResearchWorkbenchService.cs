using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services.Research;

public sealed partial class ResearchWorkbenchService : IResearchWorkbenchService
{
    private const int MaxRedirects = 5;
    private const int MaxAttempts = 3;
    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;

    public ResearchWorkbenchService(
        IToolPlatformService platform,
        INetworkSecurityService network,
        IHttpClientFactory httpClientFactory)
    {
        _platform = platform;
        _network = network;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ResearchSearchResult> SearchAsync(
        string query,
        ResearchMode mode,
        ResearchFilters? filters,
        ResearchWorkspace? workspace,
        CancellationToken ct = default)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return new ResearchSearchResult(
                query,
                mode,
                [],
                [new ResearchFailure(ResearchErrorKind.InvalidRequest, "A non-empty research query is required.")],
                0);
        }

        filters = NormalizeFilters(filters, mode);
        workspace ??= new ResearchWorkspace();
        var settings = await _platform.GetSettingsAsync(ct);
        var failures = new List<ResearchFailure>();
        var sources = new List<ResearchSearchSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attempts = 0;

        foreach (var searchQuery in BuildQueryVariants(query, mode))
        {
            var foundForVariant = false;
            foreach (var searchUrl in BuildSearchUrls(searchQuery, filters))
            {
                try
                {
                    var fetched = await FetchAsync(
                        searchUrl,
                        "text/html,application/xhtml+xml;q=0.9,*/*;q=0.5",
                        settings,
                        workspace,
                        Math.Clamp(settings.MaxFileBytes, 1_024, 2 * 1024 * 1024),
                        ct);
                    attempts += fetched.Attempts;
                    var html = DecodeText(fetched.Bytes, fetched.Charset);
                    var parsed = ParseSearchResults(html, fetched.FinalUri, filters.MaxResults * 2);
                    foreach (var result in parsed)
                    {
                        if (!PassesDomainFilters(result.Url, filters))
                            continue;
                        var canonical = ResearchText.CanonicalizeUrl(result.Url);
                        if (!seen.Add(canonical.AbsoluteUri))
                            continue;
                        sources.Add(result with { Url = canonical, Domain = canonical.IdnHost });
                        foundForVariant = true;
                        if (sources.Count >= filters.MaxResults)
                            break;
                    }
                }
                catch (ResearchServiceException ex)
                {
                    attempts += ex.Failure.Attempts;
                    failures.Add(ex.Failure);
                }

                if (foundForVariant || sources.Count >= filters.MaxResults)
                    break;
            }

            if (sources.Count >= filters.MaxResults)
                break;
        }

        return new ResearchSearchResult(
            query,
            mode,
            sources
                .OrderByDescending(source => SearchRelevance(query, source))
                .Take(filters.MaxResults)
                .ToArray(),
            CollapseFailures(failures),
            attempts);
    }

    public async Task<ResearchPage> ReadPageAsync(
        string url,
        string? relevanceQuery,
        ResearchWorkspace? workspace,
        CancellationToken ct = default)
    {
        workspace ??= new ResearchWorkspace();
        var settings = await _platform.GetSettingsAsync(ct);
        var maxBytes = Math.Clamp(settings.MaxFileBytes, 1_024, 20 * 1024 * 1024);
        var maxChars = Math.Clamp(settings.MaxOutputChars * 4, 20_000, 120_000);
        var fetched = await FetchAsync(
            url,
            "text/html,application/xhtml+xml,application/pdf,text/plain;q=0.9,*/*;q=0.2",
            settings,
            workspace,
            maxBytes,
            ct);
        var contentType = NormalizeContentType(fetched.ContentType, fetched.FinalUri, fetched.Bytes);

        try
        {
            if (contentType is "application/pdf")
            {
                return ResearchContentExtractor.ExtractPdf(
                    fetched.RequestedUri,
                    fetched.FinalUri,
                    fetched.StatusCode,
                    contentType,
                    fetched.Bytes,
                    maxChars,
                    fetched.Attempts);
            }

            var text = DecodeText(fetched.Bytes, fetched.Charset);
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                contentType is "application/xhtml+xml")
            {
                return ResearchContentExtractor.ExtractHtml(
                    fetched.RequestedUri,
                    fetched.FinalUri,
                    fetched.StatusCode,
                    contentType,
                    text,
                    maxChars,
                    fetched.Attempts);
            }

            if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                contentType is "application/json" or "application/xml")
            {
                return ResearchContentExtractor.ExtractText(
                    fetched.RequestedUri,
                    fetched.FinalUri,
                    fetched.StatusCode,
                    contentType,
                    text,
                    maxChars,
                    fetched.Attempts);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ResearchServiceException(new ResearchFailure(
                ResearchErrorKind.Parse,
                $"Unable to extract readable content: {ex.Message}",
                fetched.FinalUri,
                fetched.StatusCode,
                false,
                fetched.Attempts), ex);
        }

        throw new ResearchServiceException(new ResearchFailure(
            ResearchErrorKind.UnsupportedContent,
            $"Unsupported research content type: {contentType}",
            fetched.FinalUri,
            fetched.StatusCode,
            false,
            fetched.Attempts));
    }

    public async Task<ResearchWorkbenchResult> ResearchAsync(
        string query,
        ResearchMode mode,
        ResearchFilters? filters,
        ResearchWorkspace? workspace,
        CancellationToken ct = default)
    {
        workspace ??= new ResearchWorkspace();
        filters = NormalizeFilters(filters, mode);
        var search = await SearchAsync(query, mode, filters, workspace, ct);
        var fetchLimit = mode switch
        {
            ResearchMode.Quick => Math.Min(3, filters.MaxResults),
            ResearchMode.Balanced => Math.Min(6, filters.MaxResults),
            _ => Math.Min(10, filters.MaxResults)
        };
        var selected = SelectDiverseSources(search.Sources, fetchLimit);
        var evidence = new List<ResearchEvidence>();
        var failures = new List<ResearchFailure>(search.Failures);

        using var concurrency = new SemaphoreSlim(mode == ResearchMode.Deep ? 4 : 3);
        var tasks = selected.Select(async source =>
        {
            await concurrency.WaitAsync(ct);
            try
            {
                var page = await ReadPageAsync(source.Url.AbsoluteUri, query, workspace, ct);
                return (Source: source, Page: page, Failure: (ResearchFailure?)null);
            }
            catch (ResearchServiceException ex)
            {
                return (Source: source, Page: (ResearchPage?)null, Failure: ex.Failure);
            }
            finally
            {
                concurrency.Release();
            }
        });

        foreach (var item in await Task.WhenAll(tasks))
        {
            if (item.Failure != null)
            {
                failures.Add(item.Failure);
                continue;
            }

            var page = item.Page!;
            var excerpt = ResearchContentExtractor.BuildExcerpt(page.Text, query);
            if (excerpt.Length == 0)
                continue;
            var (authority, signals) = AuthoritySignals(page.FinalUrl);
            var relevance = RelevanceScore(query, page.Title, item.Source.Snippet, page.Text);
            var recency = RecencyScore(page.PublishedAt ?? item.Source.PublishedAt);
            evidence.Add(new ResearchEvidence(
                string.IsNullOrWhiteSpace(page.Title) ? item.Source.Title : page.Title,
                ResearchText.CanonicalizeUrl(page.FinalUrl),
                page.FinalUrl.IdnHost,
                excerpt,
                page.PublishedAt ?? item.Source.PublishedAt,
                authority,
                recency,
                relevance,
                Math.Round(authority * 0.35 + relevance * 0.45 + recency * 0.20, 3),
                signals,
                page.ContentKind));
        }

        evidence = evidence
            .GroupBy(item => item.Url.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.OverallScore).First())
            .OrderByDescending(item => item.OverallScore)
            .ToList();
        var independentDomains = evidence.Select(item => item.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var coverage = DetermineCoverage(evidence.Count, independentDomains);
        var warnings = BuildWarnings(coverage, evidence, failures, selected.Count);
        var conflicts = FindPotentialConflicts(evidence);
        var report = BuildReport(query, mode, coverage, evidence, conflicts, failures, warnings);
        var artifact = await CreateReportArtifactAsync(report, workspace, ct);

        return new ResearchWorkbenchResult(
            query,
            mode,
            coverage,
            evidence,
            conflicts,
            CollapseFailures(failures),
            warnings,
            independentDomains,
            report,
            artifact);
    }

    private async Task<RawFetch> FetchAsync(
        string rawUrl,
        string accept,
        ToolPlatformSettings settings,
        ResearchWorkspace workspace,
        int maxBytes,
        CancellationToken ct)
    {
        Uri requested;
        try
        {
            requested = await _network.ValidateAsync(
                rawUrl,
                settings,
                ct,
                workspace.BypassNetworkRestrictions);
            await ValidatePublicResearchTargetAsync(requested, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ResearchServiceException(ClassifyValidationFailure(rawUrl, ex), ex);
        }

        var current = requested;
        var totalAttempts = 0;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            HttpResponseMessage? response = null;
            var responseDeadline = DateTimeOffset.MinValue;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                totalAttempts++;
                var requestTimeout = TimeSpan.FromSeconds(Math.Clamp(
                    workspace.TimeoutSeconds,
                    1,
                    Math.Max(1, settings.MaxRuntimeSeconds)));
                responseDeadline = DateTimeOffset.UtcNow.Add(requestTimeout);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(requestTimeout);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, current);
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; TLAHStudio-Research/4.14; +https://matrixlabs.cn)");
                    request.Headers.Accept.ParseAdd(accept);
                    request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8,zh-CN;q=0.7,zh;q=0.6");
                    response = await _httpClientFactory.CreateClient("Tools")
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (IsRetryableStatus(response.StatusCode) && attempt < MaxAttempts)
                    {
                        var delay = RetryDelay(response, attempt);
                        response.Dispose();
                        response = null;
                        if (delay > TimeSpan.Zero)
                            await Task.Delay(delay, ct);
                        continue;
                    }
                    break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (attempt == MaxAttempts)
                    {
                        throw new ResearchServiceException(new ResearchFailure(
                            ResearchErrorKind.Timeout,
                            $"Research request timed out after {attempt} attempts.",
                            current,
                            null,
                            true,
                            attempt));
                    }
                }
                catch (HttpRequestException ex)
                {
                    if (attempt == MaxAttempts)
                        throw new ResearchServiceException(ClassifyNetworkFailure(current, ex, attempt), ex);
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
                }
            }

            if (response == null)
            {
                throw new ResearchServiceException(new ResearchFailure(
                    ResearchErrorKind.Network,
                    "The research request failed without a response.",
                    current,
                    null,
                    true,
                    totalAttempts));
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode) && response.Headers.Location != null)
                {
                    if (redirect == MaxRedirects)
                    {
                        throw new ResearchServiceException(new ResearchFailure(
                            ResearchErrorKind.HttpStatus,
                            "Too many HTTP redirects.",
                            current,
                            (int)response.StatusCode,
                            false,
                            totalAttempts));
                    }
                    var next = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    try
                    {
                        current = await _network.ValidateAsync(
                            next.AbsoluteUri,
                            settings,
                            ct,
                            workspace.BypassNetworkRestrictions);
                        await ValidatePublicResearchTargetAsync(current, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new ResearchServiceException(ClassifyValidationFailure(next.AbsoluteUri, ex), ex);
                    }
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var kind = response.StatusCode == HttpStatusCode.TooManyRequests
                        ? ResearchErrorKind.RateLimited
                        : ResearchErrorKind.HttpStatus;
                    throw new ResearchServiceException(new ResearchFailure(
                        kind,
                        $"Research request returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
                        current,
                        (int)response.StatusCode,
                        IsRetryableStatus(response.StatusCode),
                        totalAttempts));
                }

                if (response.Content.Headers.ContentLength is > 0 &&
                    response.Content.Headers.ContentLength > maxBytes)
                {
                    throw new ResearchServiceException(new ResearchFailure(
                        ResearchErrorKind.TooLarge,
                        $"Remote content exceeds the {maxBytes:N0}-byte research limit.",
                        current,
                        (int)response.StatusCode,
                        false,
                        totalAttempts));
                }

                var remaining = responseDeadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new ResearchServiceException(new ResearchFailure(
                        ResearchErrorKind.Timeout,
                        "Research response body exceeded the request time limit.",
                        current,
                        (int)response.StatusCode,
                        true,
                        totalAttempts));
                }

                using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bodyTimeout.CancelAfter(remaining);
                byte[] bytes;
                try
                {
                    bytes = await ReadBoundedAsync(
                        response.Content,
                        maxBytes,
                        bodyTimeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ResearchServiceException(new ResearchFailure(
                        ResearchErrorKind.Timeout,
                        "Research response body exceeded the request time limit.",
                        current,
                        (int)response.StatusCode,
                        true,
                        totalAttempts));
                }
                return new RawFetch(
                    requested,
                    current,
                    (int)response.StatusCode,
                    response.Content.Headers.ContentType?.MediaType ?? string.Empty,
                    response.Content.Headers.ContentType?.CharSet,
                    bytes,
                    totalAttempts);
            }
        }

        throw new ResearchServiceException(new ResearchFailure(
            ResearchErrorKind.HttpStatus,
            "Research redirect handling did not reach a terminal response.",
            current,
            null,
            false,
            totalAttempts));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            if (output.Length + read > maxBytes)
            {
                throw new ResearchServiceException(new ResearchFailure(
                    ResearchErrorKind.TooLarge,
                    $"Remote content exceeds the {maxBytes:N0}-byte research limit.",
                    Retryable: false));
            }
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }

    private static ResearchFilters NormalizeFilters(ResearchFilters? filters, ResearchMode mode)
    {
        var defaultCount = mode switch
        {
            ResearchMode.Quick => 6,
            ResearchMode.Balanced => 10,
            _ => 16
        };
        filters ??= new ResearchFilters(MaxResults: defaultCount);
        return filters with
        {
            AllowedDomains = NormalizeDomains(filters.AllowedDomains),
            BlockedDomains = NormalizeDomains(filters.BlockedDomains),
            Language = filters.Language?.Trim(),
            MaxResults = Math.Clamp(filters.MaxResults <= 0 ? defaultCount : filters.MaxResults, 1, 20)
        };
    }

    private static IReadOnlyList<string> NormalizeDomains(IReadOnlyList<string>? domains) =>
        (domains ?? [])
        .Select(ResearchText.NormalizeDomain)
        .Where(domain => domain.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(100)
        .ToArray();

    private static IReadOnlyList<string> BuildQueryVariants(string query, ResearchMode mode)
    {
        query = query.Length > 500 ? query[..500] : query;
        return mode switch
        {
            ResearchMode.Quick => [query],
            ResearchMode.Balanced => [query, $"{query} official source"],
            _ => [query, $"{query} official source", $"{query} research report evidence"]
        };
    }

    private static IEnumerable<string> BuildSearchUrls(string query, ResearchFilters filters)
    {
        if (filters.AllowedDomains is { Count: > 0 })
        {
            var sites = string.Join(" OR ", filters.AllowedDomains.Select(domain => $"site:{domain}"));
            query = $"{query} ({sites})";
        }
        var escaped = Uri.EscapeDataString(query);
        var recency = filters.Recency switch
        {
            ResearchRecency.Day => "&df=d",
            ResearchRecency.Week => "&df=w",
            ResearchRecency.Month => "&df=m",
            ResearchRecency.Year => "&df=y",
            _ => string.Empty
        };
        var language = LanguageParameter(filters.Language);
        yield return $"https://html.duckduckgo.com/html/?q={escaped}{recency}{language}";
        yield return $"https://lite.duckduckgo.com/lite/?q={escaped}{recency}{language}";
    }

    private static string LanguageParameter(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return string.Empty;
        var normalized = language.Trim().ToLowerInvariant();
        var region = normalized switch
        {
            "en" or "en-us" => "us-en",
            "en-gb" => "uk-en",
            "zh" or "zh-cn" or "cn" => "cn-zh",
            "zh-tw" => "tw-zh",
            "ja" or "ja-jp" => "jp-jp",
            "de" or "de-de" => "de-de",
            "fr" or "fr-fr" => "fr-fr",
            _ => normalized
        };
        return $"&kl={Uri.EscapeDataString(region)}";
    }

    private static IReadOnlyList<ResearchSearchSource> ParseSearchResults(
        string html,
        Uri searchPage,
        int maxResults)
    {
        var document = new HtmlParser().ParseDocument(html);
        var results = new List<ResearchSearchSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resultAnchors = document.QuerySelectorAll("a.result__a, a.result-link").ToArray();
        if (resultAnchors.Length == 0)
            resultAnchors = document.QuerySelectorAll("a[href]").ToArray();

        foreach (var anchor in resultAnchors)
        {
            var rawUrl = anchor.GetAttribute("href");
            var url = NormalizeSearchResultUrl(rawUrl, searchPage);
            if (url == null || !IsUsefulResult(url))
                continue;
            url = ResearchText.CanonicalizeUrl(url);
            if (!seen.Add(url.AbsoluteUri))
                continue;
            var container = anchor.Closest(".result") ?? anchor.ParentElement?.ParentElement ?? anchor.ParentElement;
            var snippet = container?.QuerySelector(".result__snippet, .result-snippet")?.TextContent ??
                          container?.QuerySelector("p")?.TextContent ?? string.Empty;
            var title = CleanText(anchor.TextContent);
            if (title.Length == 0)
                continue;
            snippet = CleanText(snippet);
            results.Add(new ResearchSearchSource(
                title,
                url,
                url.IdnHost,
                snippet,
                ParseSnippetDate(snippet)));
            if (results.Count >= maxResults)
                break;
        }
        return results;
    }

    private static Uri? NormalizeSearchResultUrl(string? value, Uri searchPage)
    {
        value = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        if (value.Length == 0)
            return null;
        if (value.StartsWith("//", StringComparison.Ordinal))
            value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            !Uri.TryCreate(searchPage, value, out uri))
            return null;
        var redirected = ReadQueryValue(uri.Query, "uddg");
        if (!string.IsNullOrWhiteSpace(redirected) &&
            Uri.TryCreate(WebUtility.UrlDecode(redirected), UriKind.Absolute, out var destination))
            uri = destination;
        return uri;
    }

    private static bool IsUsefulResult(Uri uri) =>
        uri.Scheme is "http" or "https" &&
        !ResearchText.DomainMatches(uri.IdnHost, "duckduckgo.com") &&
        !uri.AbsoluteUri.Contains("/y.js?", StringComparison.OrdinalIgnoreCase);

    private static bool PassesDomainFilters(Uri url, ResearchFilters filters)
    {
        if (filters.BlockedDomains is { Count: > 0 } &&
            filters.BlockedDomains.Any(domain => ResearchText.DomainMatches(url.IdnHost, domain)))
            return false;
        return filters.AllowedDomains is not { Count: > 0 } ||
               filters.AllowedDomains.Any(domain => ResearchText.DomainMatches(url.IdnHost, domain));
    }

    private static IReadOnlyList<ResearchSearchSource> SelectDiverseSources(
        IReadOnlyList<ResearchSearchSource> sources,
        int max)
    {
        var selected = new List<ResearchSearchSource>();
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (domains.Add(source.Domain))
                selected.Add(source);
            if (selected.Count >= max)
                return selected;
        }
        foreach (var source in sources)
        {
            if (selected.Any(item => item.Url == source.Url))
                continue;
            selected.Add(source);
            if (selected.Count >= max)
                break;
        }
        return selected;
    }

    private static (double Score, IReadOnlyList<string> Signals) AuthoritySignals(Uri url)
    {
        var host = url.IdnHost.ToLowerInvariant();
        var signals = new List<string>();
        var score = 0.58;
        if (host.EndsWith(".gov", StringComparison.Ordinal) ||
            host.Contains(".gov.", StringComparison.Ordinal))
        {
            score = 0.96;
            signals.Add("government domain");
        }
        else if (host.EndsWith(".edu", StringComparison.Ordinal) ||
                 host.Contains(".edu.", StringComparison.Ordinal))
        {
            score = 0.91;
            signals.Add("education domain");
        }
        else if (host.StartsWith("docs.", StringComparison.Ordinal) ||
                 host.StartsWith("developer.", StringComparison.Ordinal) ||
                 host.StartsWith("learn.", StringComparison.Ordinal))
        {
            score = 0.84;
            signals.Add("documentation subdomain");
        }
        else if (host.EndsWith(".org", StringComparison.Ordinal))
        {
            score = 0.70;
            signals.Add("organization domain");
        }
        if (url.Scheme == Uri.UriSchemeHttps)
        {
            score = Math.Min(1, score + 0.03);
            signals.Add("HTTPS");
        }
        signals.Add("heuristic signal only; verify publisher identity");
        return (Math.Round(score, 3), signals);
    }

    private static double RelevanceScore(string query, params string[] textParts)
    {
        var terms = ResearchText.Tokenize(query);
        if (terms.Count == 0)
            return 0.5;
        var haystack = string.Join(' ', textParts).ToLowerInvariant();
        var matches = terms.Count(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
        return Math.Round(Math.Clamp((double)matches / terms.Count, 0.05, 1), 3);
    }

    private static double SearchRelevance(string query, ResearchSearchSource source) =>
        RelevanceScore(query, source.Title, source.Snippet);

    private static double RecencyScore(DateTimeOffset? publishedAt)
    {
        if (publishedAt == null)
            return 0.5;
        var age = DateTimeOffset.UtcNow - publishedAt.Value.ToUniversalTime();
        if (age < TimeSpan.Zero)
            return 0.45;
        if (age <= TimeSpan.FromDays(30))
            return 1;
        if (age <= TimeSpan.FromDays(180))
            return 0.85;
        if (age <= TimeSpan.FromDays(365))
            return 0.70;
        if (age <= TimeSpan.FromDays(365 * 3))
            return 0.52;
        return 0.35;
    }

    private static ResearchCoverage DetermineCoverage(int sourceCount, int domains) =>
        sourceCount >= 4 && domains >= 3
            ? ResearchCoverage.Strong
            : sourceCount >= 2 && domains >= 2
                ? ResearchCoverage.Partial
                : ResearchCoverage.Insufficient;

    private static IReadOnlyList<string> BuildWarnings(
        ResearchCoverage coverage,
        IReadOnlyList<ResearchEvidence> evidence,
        IReadOnlyList<ResearchFailure> failures,
        int attemptedSources)
    {
        var warnings = new List<string>();
        if (coverage == ResearchCoverage.Insufficient)
            warnings.Add("Evidence is insufficient for a dependable conclusion; broaden the query or source permissions.");
        if (coverage == ResearchCoverage.Partial)
            warnings.Add("Evidence has multiple sources but does not yet meet strong cross-verification coverage.");
        if (failures.Count > 0)
            warnings.Add($"{failures.Count} retrieval path(s) failed; the evidence pack is partial.");
        if (attemptedSources > 0 && evidence.Count == 0)
            warnings.Add("Search results were found, but none could be independently fetched and extracted.");
        if (evidence.Count > 0 && evidence.All(item => item.PublishedAt == null))
            warnings.Add("Publication dates were unavailable, so recency scores use a neutral value.");
        warnings.Add("Authority and conflict indicators are heuristic signals, not factual conclusions.");
        return warnings;
    }

    private static IReadOnlyList<ResearchConflict> FindPotentialConflicts(
        IReadOnlyList<ResearchEvidence> evidence)
    {
        var claims = evidence.Select(item => new
        {
            Evidence = item,
            Values = MeasurementRegex().Matches(item.Excerpt)
                .Select(match => new
                {
                    Number = match.Groups["number"].Value,
                    Unit = match.Groups["unit"].Value.ToLowerInvariant()
                })
                .ToArray()
        }).ToArray();
        var conflicts = new List<ResearchConflict>();
        foreach (var unitGroup in claims
                     .SelectMany(item => item.Values.Select(value => new { item.Evidence, value.Number, value.Unit }))
                     .GroupBy(item => item.Unit, StringComparer.OrdinalIgnoreCase))
        {
            var values = unitGroup.Select(item => item.Number).Distinct(StringComparer.Ordinal).ToArray();
            var domains = unitGroup.Select(item => item.Evidence.Domain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (values.Length < 2 || domains.Length < 2)
                continue;
            conflicts.Add(new ResearchConflict(
                "potential_measurement_discrepancy",
                $"Sources contain different {unitGroup.Key} measurements ({string.Join(", ", values.Take(5))}). Context may differ; compare the cited excerpts before using either value.",
                unitGroup.Select(item => item.Evidence.Url).Distinct().Take(5).ToArray()));
            if (conflicts.Count >= 5)
                break;
        }
        return conflicts;
    }

    private static string BuildReport(
        string query,
        ResearchMode mode,
        ResearchCoverage coverage,
        IReadOnlyList<ResearchEvidence> evidence,
        IReadOnlyList<ResearchConflict> conflicts,
        IReadOnlyList<ResearchFailure> failures,
        IReadOnlyList<string> warnings)
    {
        var output = new StringBuilder();
        output.AppendLine("# Research evidence pack")
            .AppendLine()
            .AppendLine($"- Query: {query}")
            .AppendLine($"- Mode: {mode.ToString().ToLowerInvariant()}")
            .AppendLine($"- Coverage: {coverage.ToString().ToLowerInvariant()}")
            .AppendLine($"- Independent domains: {evidence.Select(item => item.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count()}")
            .AppendLine($"- Generated (UTC): {DateTimeOffset.UtcNow:O}")
            .AppendLine()
            .AppendLine("> This report packages retrieved evidence. It does not invent or assert a factual conclusion.");

        if (warnings.Count > 0)
        {
            output.AppendLine().AppendLine("## Limitations");
            foreach (var warning in warnings)
                output.AppendLine($"- {warning}");
        }

        output.AppendLine().AppendLine("## Sources");
        if (evidence.Count == 0)
            output.AppendLine("No independently fetched evidence was available.");
        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            output.AppendLine()
                .AppendLine($"### {index + 1}. {item.Title}")
                .AppendLine()
                .AppendLine($"- URL: {item.Url}")
                .AppendLine($"- Domain: {item.Domain}")
                .AppendLine($"- Published: {(item.PublishedAt?.ToString("O") ?? "unknown")}")
                .AppendLine($"- Content: {item.ContentKind.ToString().ToLowerInvariant()}")
                .AppendLine($"- Signals: authority {item.AuthoritySignalScore:0.000}, recency {item.RecencyScore:0.000}, relevance {item.RelevanceScore:0.000}, combined {item.OverallScore:0.000}")
                .AppendLine()
                .AppendLine("> " + item.Excerpt.Replace(Environment.NewLine, Environment.NewLine + "> "));
        }

        if (conflicts.Count > 0)
        {
            output.AppendLine().AppendLine("## Potential discrepancies");
            foreach (var conflict in conflicts)
            {
                output.AppendLine($"- {conflict.Description}");
                foreach (var source in conflict.Sources)
                    output.AppendLine($"  - {source}");
            }
        }

        if (failures.Count > 0)
        {
            output.AppendLine().AppendLine("## Retrieval failures");
            foreach (var failure in CollapseFailures(failures))
                output.AppendLine($"- {failure.Kind}: {failure.Message} {(failure.Url == null ? string.Empty : $"({failure.Url})")}");
        }
        return output.ToString().TrimEnd();
    }

    private static async Task<ResearchReportArtifact?> CreateReportArtifactAsync(
        string report,
        ResearchWorkspace workspace,
        CancellationToken ct)
    {
        if (!workspace.CreateReportArtifact || string.IsNullOrWhiteSpace(workspace.ArtifactDirectory))
            return null;
        var root = Path.GetFullPath(workspace.ArtifactDirectory);
        Directory.CreateDirectory(root);
        var fileName = Path.GetFileName(workspace.ReportFileName);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "research-report.md";
        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            fileName += ".md";
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The research report path escapes its workspace.");
        var temporaryPath = Path.Combine(
            root,
            $".{Path.GetFileNameWithoutExtension(fileName)}.{Guid.NewGuid():N}.tmp.md");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, report, new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        var bytes = await File.ReadAllBytesAsync(path, CancellationToken.None);
        return new ResearchReportArtifact(
            path,
            fileName,
            "text/markdown",
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static IReadOnlyList<ResearchFailure> CollapseFailures(
        IReadOnlyList<ResearchFailure> failures) =>
        failures.GroupBy(
                failure => $"{failure.Kind}|{failure.HttpStatus}|{failure.Url?.IdnHost}|{failure.Message}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return first with { Attempts = group.Sum(item => Math.Max(1, item.Attempts)) };
            })
            .ToArray();

    private static ResearchFailure ClassifyValidationFailure(string rawUrl, Exception ex)
    {
        Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri);
        var message = ex.Message;
        var kind = message.Contains("resolve", StringComparison.OrdinalIgnoreCase)
            ? ResearchErrorKind.Dns
            : message.Contains("allowlist", StringComparison.OrdinalIgnoreCase) ||
              message.Contains("private", StringComparison.OrdinalIgnoreCase) ||
              message.Contains("HTTPS", StringComparison.OrdinalIgnoreCase)
                ? ResearchErrorKind.SecurityPolicy
                : ResearchErrorKind.InvalidRequest;
        return new ResearchFailure(kind, message, uri, Retryable: kind == ResearchErrorKind.Dns);
    }

    private async Task ValidatePublicResearchTargetAsync(Uri uri, CancellationToken ct)
    {
        // The standard service normally enforces these constraints itself.
        // Full-access/explicit UI authorization may bypass the user's domain
        // allowlist, but research must still never become an SSRF path into
        // local or private networks.
        if (_network is not NetworkSecurityService)
            return;
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Research sources require HTTPS.");
        if (IPAddress.TryParse(uri.IdnHost, out var literal))
        {
            if (NetworkSecurityService.IsPrivateOrLocal(literal))
                throw new InvalidOperationException("Private, loopback, and link-local research targets are blocked.");
            return;
        }
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, ct);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"Unable to resolve {uri.IdnHost}: {ex.Message}", ex);
        }
        if (addresses.Length == 0 || addresses.Any(NetworkSecurityService.IsPrivateOrLocal))
            throw new InvalidOperationException("The research domain resolves to a private, loopback, or link-local address.");
    }

    private static ResearchFailure ClassifyNetworkFailure(Uri uri, HttpRequestException ex, int attempts)
    {
        var kind = ex.InnerException is SocketException ||
                   ex.Message.Contains("resolve", StringComparison.OrdinalIgnoreCase)
            ? ResearchErrorKind.Dns
            : ResearchErrorKind.Network;
        return new ResearchFailure(kind, ex.Message, uri, Retryable: true, Attempts: attempts);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Moved or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } requested)
            return requested > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : requested;
        return TimeSpan.FromMilliseconds(100 * attempt);
    }

    private static string NormalizeContentType(string contentType, Uri uri, byte[] bytes)
    {
        contentType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (contentType == "application/octet-stream" || contentType.Length == 0)
        {
            if (uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                bytes.AsSpan().StartsWith("%PDF"u8))
                return "application/pdf";
            var prefix = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 256))).TrimStart();
            if (prefix.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
                prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                return "text/html";
        }
        return contentType;
    }

    private static string DecodeText(byte[] bytes, string? charset)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // Fall through to UTF-8, which is the web default for this workbench.
            }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string CleanText(string value) =>
        WhitespaceRegex().Replace(WebUtility.HtmlDecode(value), " ").Trim();

    private static string? ReadQueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 &&
                Uri.UnescapeDataString(parts[0]).Equals(name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(parts[1]);
        }
        return null;
    }

    private static DateTimeOffset? ParseSnippetDate(string snippet)
    {
        var match = SnippetDateRegex().Match(snippet);
        if (!match.Success)
            return null;
        return DateTimeOffset.TryParse(
            match.Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private sealed record RawFetch(
        Uri RequestedUri,
        Uri FinalUri,
        int StatusCode,
        string ContentType,
        string? Charset,
        byte[] Bytes,
        int Attempts);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        @"(?<number>\d+(?:\.\d+)?)\s*(?<unit>%|percent|million|billion|ms|milliseconds?|seconds?|minutes?|hours?|days?|years?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeasurementRegex();

    [GeneratedRegex(
        @"(?:\d{4}-\d{1,2}-\d{1,2}|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{1,2},\s+\d{4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SnippetDateRegex();
}

public sealed class ResearchServiceException : Exception
{
    public ResearchServiceException(ResearchFailure failure, Exception? innerException = null)
        : base(failure.Message, innerException)
    {
        Failure = failure;
    }

    public ResearchFailure Failure { get; }
}
