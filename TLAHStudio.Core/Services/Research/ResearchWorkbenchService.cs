using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services.Research;

public sealed partial class ResearchWorkbenchService : IResearchWorkbenchService
{
    private const int MaxRedirects = 5;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan StructuredProviderMinimumInterval = TimeSpan.FromSeconds(5);
    private static readonly Uri DuckDuckGoProviderUrl = new("https://duckduckgo.com/");
    private static readonly Uri GdeltProviderUrl = new("https://www.gdeltproject.org/");
    private static readonly Uri WikipediaLicenseUrl = new("https://creativecommons.org/licenses/by-sa/4.0/");
    private readonly IToolPlatformService _platform;
    private readonly INetworkSecurityService _network;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly object _providerThrottleSync = new();
    private readonly Dictionary<string, DateTimeOffset> _providerBlockedUntil =
        new(StringComparer.OrdinalIgnoreCase);

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
            foreach (var endpoint in BuildSearchEndpoints(searchQuery, filters))
            {
                if (!TryReserveSearchProvider(endpoint, out var retryAt))
                {
                    failures.Add(new ResearchFailure(
                        ResearchErrorKind.RateLimited,
                        $"{endpoint.Provider} was skipped by the local provider throttle until {retryAt:O}; trying another provider.",
                        new Uri(endpoint.Url),
                        Retryable: true,
                        Attempts: 0));
                    continue;
                }

                try
                {
                    var fetched = await FetchAsync(
                        endpoint.Url,
                        endpoint.ResponseKind == SearchResponseKind.Html
                            ? "text/html,application/xhtml+xml;q=0.9,*/*;q=0.5"
                            : "application/json;q=1.0,*/*;q=0.2",
                        settings,
                        workspace,
                        Math.Clamp(settings.MaxFileBytes, 1_024, 2 * 1024 * 1024),
                        ct,
                        retryRateLimited: endpoint.ResponseKind == SearchResponseKind.Html,
                        maxAttempts: endpoint.ResponseKind == SearchResponseKind.Html ? MaxAttempts : 1);
                    attempts += fetched.Attempts;
                    var payload = DecodeText(fetched.Bytes, fetched.Charset);
                    if (IsSearchProviderChallenge(endpoint, fetched.StatusCode, payload))
                    {
                        failures.Add(new ResearchFailure(
                            ResearchErrorKind.RateLimited,
                            $"{endpoint.Provider} returned an automated-traffic challenge; trying another search provider.",
                            fetched.FinalUri,
                            fetched.StatusCode,
                            true,
                            fetched.Attempts));
                        continue;
                    }

                    var parsedPayload = endpoint.ResponseKind switch
                    {
                        SearchResponseKind.GdeltJson => ParseGdeltSearchResults(
                            payload,
                            filters.MaxResults * 2,
                            endpoint),
                        SearchResponseKind.WikimediaJson => ParseWikimediaSearchResults(
                            payload,
                            fetched.FinalUri,
                            filters.MaxResults * 2,
                            endpoint,
                            fetched.RetryAfter),
                        _ => new SearchPayloadParseResult(
                            ParseSearchResults(
                                payload,
                                fetched.FinalUri,
                                filters.MaxResults * 2,
                                endpoint),
                            IsExplicitEmptySearchResponse(payload))
                    };
                    if (parsedPayload.Failure != null)
                    {
                        failures.Add(parsedPayload.Failure);
                        ApplyProviderCooldown(endpoint, parsedPayload.RetryAfter);
                        continue;
                    }
                    var parsed = parsedPayload.Sources;
                    if (parsed.Count == 0 && !parsedPayload.IsRecognizedEmpty)
                    {
                        failures.Add(new ResearchFailure(
                            ResearchErrorKind.Parse,
                            $"{endpoint.Provider} returned a search response, but its result format was not recognized; trying another provider.",
                            fetched.FinalUri,
                            fetched.StatusCode,
                            true,
                            fetched.Attempts));
                        if (endpoint.ResponseKind != SearchResponseKind.Html)
                            ApplyProviderCooldown(endpoint, fetched.RetryAfter);
                        continue;
                    }
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
                    if (endpoint.ResponseKind != SearchResponseKind.Html && ex.Failure.Retryable)
                        ApplyProviderCooldown(endpoint, ex.RetryAfter);
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
                page.ContentKind,
                item.Source.SearchProvider,
                item.Source.SearchProviderUrl,
                item.Source.LicenseName,
                item.Source.LicenseUrl));
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
        CancellationToken ct,
        bool retryRateLimited = true,
        int maxAttempts = MaxAttempts)
    {
        maxAttempts = Math.Clamp(maxAttempts, 1, MaxAttempts);
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
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
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
                    if (ShouldRetryStatus(response.StatusCode, retryRateLimited) && attempt < maxAttempts)
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
                    if (attempt == maxAttempts)
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
                    if (attempt == maxAttempts)
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
                        totalAttempts),
                        retryAfter: ReadRetryAfter(response));
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
                    totalAttempts,
                    ReadRetryAfter(response));
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

    private static IEnumerable<SearchEndpoint> BuildSearchEndpoints(string query, ResearchFilters filters)
    {
        var providerQuery = query;
        var hasExplicitLanguage = !string.IsNullOrWhiteSpace(filters.Language);
        var effectiveLanguage = !hasExplicitLanguage
            ? InferSearchLanguage(providerQuery)
            : filters.Language!;
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
        var language = LanguageParameter(effectiveLanguage);
        yield return new SearchEndpoint(
            $"https://html.duckduckgo.com/html/?q={escaped}{recency}{language}",
            "DuckDuckGo",
            SearchResponseKind.Html,
            DuckDuckGoProviderUrl);

        // DuckDuckGo can return an anti-automation page after only a few requests.
        // GDELT and Wikimedia expose public, structured JSON APIs, providing
        // independent zero-configuration fallbacks without scraping another HTML
        // result page. Domain restrictions are enforced again after parsing.
        var structuredQuery = Uri.EscapeDataString(providerQuery);
        var gdeltRecency = GdeltTimespanParameter(filters.Recency);
        var gdeltEndpoint = new SearchEndpoint(
            $"https://api.gdeltproject.org/api/v2/doc/doc?query={structuredQuery}&mode=artlist&maxrecords={Math.Clamp(filters.MaxResults * 2, 1, 40)}&format=json&sort=HybridRel{gdeltRecency}",
            "GDELT Project",
            SearchResponseKind.GdeltJson,
            GdeltProviderUrl);

        var wikipediaHost = WikimediaHost(effectiveLanguage);
        var wikipediaLanguage = wikipediaHost.Split('.', 2)[0];
        var wikimediaEndpoint = new SearchEndpoint(
            $"https://{wikipediaHost}/w/api.php?action=query&list=search&srsearch={structuredQuery}&srnamespace=0&srlimit={Math.Clamp(filters.MaxResults * 2, 1, 20)}&srprop=snippet&utf8=1&maxlag=5&format=json&formatversion=2",
            $"Wikipedia ({wikipediaLanguage})",
            SearchResponseKind.WikimediaJson,
            new Uri($"https://{wikipediaHost}/"),
            "CC BY-SA 4.0",
            WikipediaLicenseUrl);

        var canUseGdelt = !hasExplicitLanguage;
        var canUseWikipedia = filters.Recency == ResearchRecency.Any &&
                              (!hasExplicitLanguage || SupportsWikimediaLanguage(effectiveLanguage));
        if (ShouldPreferGdelt(providerQuery, filters.Recency))
        {
            if (canUseGdelt)
                yield return gdeltEndpoint;
            if (canUseWikipedia)
                yield return wikimediaEndpoint;
        }
        else
        {
            if (canUseWikipedia)
                yield return wikimediaEndpoint;
            if (canUseGdelt)
                yield return gdeltEndpoint;
        }

        yield return new SearchEndpoint(
            $"https://lite.duckduckgo.com/lite/?q={escaped}{recency}{language}",
            "DuckDuckGo Lite",
            SearchResponseKind.Html,
            DuckDuckGoProviderUrl);
    }

    private static string GdeltTimespanParameter(ResearchRecency recency) => recency switch
    {
        ResearchRecency.Day => "&timespan=1day",
        ResearchRecency.Week => "&timespan=1week",
        ResearchRecency.Month => "&timespan=1month",
        ResearchRecency.Year => "&timespan=1year",
        _ => string.Empty
    };

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
            "ko" or "ko-kr" => "kr-kr",
            "de" or "de-de" => "de-de",
            "fr" or "fr-fr" => "fr-fr",
            _ => normalized
        };
        return $"&kl={Uri.EscapeDataString(region)}";
    }

    private static string WikimediaHost(string language)
    {
        var normalized = language.Trim().ToLowerInvariant();
        var primary = normalized.Split(['-', '_'], 2)[0];
        return primary switch
        {
            "zh" or "cn" => "zh.wikipedia.org",
            "ja" => "ja.wikipedia.org",
            "ko" => "ko.wikipedia.org",
            "de" => "de.wikipedia.org",
            "fr" => "fr.wikipedia.org",
            _ => "en.wikipedia.org"
        };
    }

    private static bool SupportsWikimediaLanguage(string language)
    {
        var primary = language.Trim().ToLowerInvariant().Split(['-', '_'], 2)[0];
        return primary is "en" or "zh" or "cn" or "ja" or "ko" or "de" or "fr";
    }

    private bool TryReserveSearchProvider(SearchEndpoint endpoint, out DateTimeOffset retryAt)
    {
        retryAt = DateTimeOffset.MinValue;
        if (endpoint.ResponseKind == SearchResponseKind.Html)
            return true;

        var key = ProviderThrottleKey(endpoint);
        lock (_providerThrottleSync)
        {
            var now = DateTimeOffset.UtcNow;
            if (_providerBlockedUntil.TryGetValue(key, out retryAt) && retryAt > now)
                return false;

            // GDELT is deliberately paced even after a successful response. This
            // prevents balanced/deep query variants from issuing a burst of DOC
            // API requests when earlier providers return no results.
            if (endpoint.ResponseKind == SearchResponseKind.GdeltJson)
                _providerBlockedUntil[key] = retryAt = now + StructuredProviderMinimumInterval;
            return true;
        }
    }

    private void ApplyProviderCooldown(SearchEndpoint endpoint, TimeSpan? retryAfter)
    {
        if (endpoint.ResponseKind == SearchResponseKind.Html)
            return;
        var duration = retryAfter is { } requested && requested > StructuredProviderMinimumInterval
            ? requested
            : StructuredProviderMinimumInterval;
        lock (_providerThrottleSync)
        {
            var until = DateTimeOffset.UtcNow + duration;
            var key = ProviderThrottleKey(endpoint);
            if (!_providerBlockedUntil.TryGetValue(key, out var current) || until > current)
                _providerBlockedUntil[key] = until;
        }
    }

    private static string ProviderThrottleKey(SearchEndpoint endpoint) =>
        new Uri(endpoint.Url).IdnHost;

    private static string InferSearchLanguage(string query)
    {
        if (query.Any(character => character is >= '\u3040' and <= '\u30ff'))
            return "ja-JP";
        if (query.Any(character => character is >= '\uac00' and <= '\ud7af'))
            return "ko-KR";
        if (query.Any(character => character is >= '\u3400' and <= '\u9fff'))
            return "zh-CN";
        return "en-US";
    }

    private static bool ShouldPreferGdelt(string query, ResearchRecency recency)
    {
        if (recency != ResearchRecency.Any)
            return true;
        var signals = new[]
        {
            "news", "latest", "today", "current", "recent", "update", "release",
            "announcement", "breaking", "新闻", "最新", "今日", "今天", "动态", "近况",
            "更新", "发布", "公告", "消息", "ニュース", "오늘", "최신", "뉴스"
        };
        return signals.Any(signal => query.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ResearchSearchSource> ParseSearchResults(
        string html,
        Uri searchPage,
        int maxResults,
        SearchEndpoint endpoint)
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
                ParseSnippetDate(snippet),
                endpoint.Provider,
                endpoint.ProviderUrl,
                endpoint.LicenseName,
                endpoint.LicenseUrl));
            if (results.Count >= maxResults)
                break;
        }
        return results;
    }

    private static SearchPayloadParseResult ParseGdeltSearchResults(
        string json,
        int maxResults,
        SearchEndpoint endpoint)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("articles", out var articles) ||
                articles.ValueKind != JsonValueKind.Array)
                return new SearchPayloadParseResult([], false);

            var results = new List<ResearchSearchSource>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var article in articles.EnumerateArray())
            {
                if (article.ValueKind != JsonValueKind.Object)
                    continue;
                var title = CleanText(JsonString(article, "title"));
                var rawUrl = CleanText(JsonString(article, "url"));
                if (title.Length == 0 ||
                    !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) ||
                    !IsUsefulResult(url))
                    continue;
                url = ResearchText.CanonicalizeUrl(url);
                if (!seen.Add(url.AbsoluteUri))
                    continue;
                var snippet = BuildGdeltSnippet(article);
                var publishedAt = TryParseGdeltDate(JsonString(article, "seendate"));
                results.Add(new ResearchSearchSource(
                    title,
                    url,
                    url.IdnHost,
                    snippet,
                    publishedAt,
                    endpoint.Provider,
                    endpoint.ProviderUrl,
                    endpoint.LicenseName,
                    endpoint.LicenseUrl));
                if (results.Count >= maxResults)
                    break;
            }
            return new SearchPayloadParseResult(results, articles.GetArrayLength() == 0);
        }
        catch (JsonException)
        {
            return new SearchPayloadParseResult([], false);
        }
    }

    private static SearchPayloadParseResult ParseWikimediaSearchResults(
        string json,
        Uri searchPage,
        int maxResults,
        SearchEndpoint endpoint,
        TimeSpan? retryAfter)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new SearchPayloadParseResult([], false);
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = CleanText(JsonString(error, "code"));
                var info = CleanText(JsonString(error, "info"));
                var rateLimited = code.Equals("ratelimited", StringComparison.OrdinalIgnoreCase) ||
                                  code.Equals("maxlag", StringComparison.OrdinalIgnoreCase);
                var kind = rateLimited ? ResearchErrorKind.RateLimited : ResearchErrorKind.Parse;
                var message = $"{endpoint.Provider} returned API error {code}: {info}".TrimEnd(' ', ':');
                return new SearchPayloadParseResult(
                    [],
                    false,
                    new ResearchFailure(
                        kind,
                        message,
                        searchPage,
                        (int)HttpStatusCode.OK,
                        rateLimited,
                        1),
                    rateLimited ? retryAfter ?? StructuredProviderMinimumInterval : null);
            }
            if (!root.TryGetProperty("batchcomplete", out _))
                return new SearchPayloadParseResult([], false);
            if (!root.TryGetProperty("query", out var query))
                return new SearchPayloadParseResult([], true);
            if (query.ValueKind != JsonValueKind.Object ||
                !query.TryGetProperty("search", out var search) ||
                search.ValueKind != JsonValueKind.Array)
                return new SearchPayloadParseResult([], false);

            var results = new List<ResearchSearchSource>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in search.EnumerateArray())
            {
                if (page.ValueKind != JsonValueKind.Object)
                    continue;
                var title = CleanText(JsonString(page, "title"));
                var rawUrl = title.Length == 0
                    ? string.Empty
                    : $"https://{searchPage.IdnHost}/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
                if (title.Length == 0 ||
                    !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) ||
                    !IsUsefulResult(url))
                    continue;
                url = ResearchText.CanonicalizeUrl(url);
                if (!seen.Add(url.AbsoluteUri))
                    continue;
                results.Add(new ResearchSearchSource(
                    title,
                    url,
                    url.IdnHost,
                    CleanWikipediaSnippet(JsonString(page, "snippet")),
                    null,
                    endpoint.Provider,
                    endpoint.ProviderUrl,
                    endpoint.LicenseName,
                    endpoint.LicenseUrl));
                if (results.Count >= maxResults)
                    break;
            }
            return new SearchPayloadParseResult(results, search.GetArrayLength() == 0);
        }
        catch (JsonException)
        {
            return new SearchPayloadParseResult([], false);
        }
    }

    private static string JsonString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string BuildGdeltSnippet(JsonElement article)
    {
        var values = new[]
        {
            CleanText(JsonString(article, "domain")),
            CleanText(JsonString(article, "language")),
            CleanText(JsonString(article, "sourcecountry"))
        };
        return string.Join(" · ", values.Where(value => value.Length > 0));
    }

    private static string CleanWikipediaSnippet(string value) =>
        CleanText(HtmlTagRegex().Replace(value, string.Empty));

    private static DateTimeOffset? TryParseGdeltDate(string value)
    {
        var formats = new[] { "yyyyMMdd'T'HHmmss'Z'", "yyyyMMddHHmmss" };
        return DateTimeOffset.TryParseExact(
            value,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static bool IsSearchProviderChallenge(
        SearchEndpoint endpoint,
        int statusCode,
        string payload)
    {
        if (!endpoint.Provider.StartsWith("DuckDuckGo", StringComparison.Ordinal))
            return false;
        return statusCode == 202 ||
               payload.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase) ||
               payload.Contains("challenge-form", StringComparison.OrdinalIgnoreCase) ||
               payload.Contains("duckduckgo.com/anomaly.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplicitEmptySearchResponse(string payload) =>
        payload.Contains("No results.", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("No more results", StringComparison.OrdinalIgnoreCase) ||
        payload.Contains("result--no-result", StringComparison.OrdinalIgnoreCase);

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
                .AppendLine($"- Discovered via: {item.SearchProvider}{(item.SearchProviderUrl == null ? string.Empty : $" ({item.SearchProviderUrl})")}")
                .AppendLine($"- License: {(item.LicenseName == null ? "source-site terms apply" : $"{item.LicenseName}{(item.LicenseUrl == null ? string.Empty : $" ({item.LicenseUrl})")}")}")
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
                return first with { Attempts = group.Sum(item => Math.Max(0, item.Attempts)) };
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

    private static bool ShouldRetryStatus(HttpStatusCode status, bool retryRateLimited) =>
        IsRetryableStatus(status) &&
        (status != HttpStatusCode.TooManyRequests || retryRateLimited);

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } requested)
            return requested > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : requested;
        return TimeSpan.FromMilliseconds(100 * attempt);
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var duration = date - DateTimeOffset.UtcNow;
            return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        }
        return null;
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
        int Attempts,
        TimeSpan? RetryAfter);

    private sealed record SearchEndpoint(
        string Url,
        string Provider,
        SearchResponseKind ResponseKind,
        Uri ProviderUrl,
        string? LicenseName = null,
        Uri? LicenseUrl = null);

    private sealed record SearchPayloadParseResult(
        IReadOnlyList<ResearchSearchSource> Sources,
        bool IsRecognizedEmpty,
        ResearchFailure? Failure = null,
        TimeSpan? RetryAfter = null);

    private enum SearchResponseKind
    {
        Html,
        GdeltJson,
        WikimediaJson
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

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
    public ResearchServiceException(
        ResearchFailure failure,
        Exception? innerException = null,
        TimeSpan? retryAfter = null)
        : base(failure.Message, innerException)
    {
        Failure = failure;
        RetryAfter = retryAfter;
    }

    public ResearchFailure Failure { get; }
    public TimeSpan? RetryAfter { get; }
}
