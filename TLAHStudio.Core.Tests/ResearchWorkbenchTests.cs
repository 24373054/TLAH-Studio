using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Research;

namespace TLAHStudio.Core.Tests;

public sealed class ResearchWorkbenchTests
{
    [Fact]
    public async Task ReadPageAsync_ExtractsMetadataMainTextAndAbsoluteLinks()
    {
        await using var db = TestDb.Create();
        var html = """
            <!doctype html>
            <html lang="en">
            <head>
              <title>Fallback title</title>
              <meta property="og:title" content="Primary research title">
              <meta name="description" content="A focused description.">
              <meta property="article:published_time" content="2026-07-12T09:30:00Z">
            </head>
            <body>
              <nav>Navigation noise</nav>
              <main>
                <h1>Primary research title</h1>
                <p>The verified finding is inside the main article.</p>
                <a href="/supporting-evidence">Supporting evidence</a>
              </main>
              <footer>Footer noise</footer>
            </body>
            </html>
            """;
        using var http = new HttpClient(new MapHttpMessageHandler(_ =>
        {
            var content = new ByteArrayContent(Encoding.Unicode.GetBytes(html));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-16" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var service = CreateService(db, http);

        var page = await service.ReadPageAsync(
            "https://example.test/articles/item",
            "verified finding",
            AuthorizedWorkspace());

        Assert.Equal("Primary research title", page.Title);
        Assert.Equal("A focused description.", page.Description);
        Assert.Equal("en", page.Language);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 9, 30, 0, TimeSpan.Zero), page.PublishedAt);
        Assert.Contains("verified finding", page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Navigation noise", page.Text);
        Assert.Contains(page.Links, link =>
            link.Url.AbsoluteUri == "https://example.test/supporting-evidence");
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesTrackingUrlsAndAppliesDomainFilters()
    {
        await using var db = TestDb.Create();
        var html = """
            <html><body>
              <div class="result">
                <a class="result__a" href="/l/?uddg=https%3A%2F%2Fdocs.example.com%2Fguide%3Futm_source%3Done">Guide A</a>
                <a class="result__snippet">Official guide.</a>
              </div>
              <div class="result">
                <a class="result__a" href="https://docs.example.com/guide?utm_campaign=two">Guide duplicate</a>
              </div>
              <div class="result">
                <a class="result__a" href="https://blocked.example.net/story">Blocked story</a>
              </div>
              <div class="result">
                <a class="result__a" href="https://api.docs.example.com/reference">Reference</a>
              </div>
            </body></html>
            """;
        using var http = new HttpClient(new MapHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "example documentation",
            ResearchMode.Quick,
            new ResearchFilters(
                AllowedDomains: ["example.com"],
                BlockedDomains: ["blocked.example.net"],
                MaxResults: 10),
            AuthorizedWorkspace());

        Assert.Equal(2, result.Sources.Count);
        Assert.Single(result.Sources, source =>
            source.Url.AbsoluteUri == "https://docs.example.com/guide");
        Assert.Contains(result.Sources, source => source.Domain == "api.docs.example.com");
        Assert.DoesNotContain(result.Sources, source => source.Domain == "blocked.example.net");
    }

    [Fact]
    public async Task SearchAsync_RetriesRateLimitAndReturnsRecoveredResults()
    {
        await using var db = TestDb.Create();
        var calls = 0;
        using var http = new HttpClient(new MapHttpMessageHandler(_ =>
        {
            calls++;
            if (calls == 1)
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<a class="result__a" href="https://example.org/recovered">Recovered source</a>""",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "retry source",
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 3),
            AuthorizedWorkspace());

        Assert.Single(result.Sources);
        Assert.Equal("Recovered source", result.Sources[0].Title);
        Assert.True(result.Attempts >= 2);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task SearchAsync_StopsBeforeSlowFallbacksWhenRequestedResultCountIsSatisfied()
    {
        await using var db = TestDb.Create();
        var requestedHosts = new List<string>();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            requestedHosts.Add(request.RequestUri!.IdnHost);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    <div class="result"><a class="result__a" href="https://one.example/a">One</a></div>
                    <div class="result"><a class="result__a" href="https://two.example/b">Two</a></div>
                    """,
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "latest AI news",
            ResearchMode.Deep,
            new ResearchFilters(MaxResults: 2),
            AuthorizedWorkspace());

        Assert.Equal(2, result.Sources.Count);
        Assert.Equal(["html.duckduckgo.com"], requestedHosts);
    }

    [Fact]
    public async Task SearchAsync_FallsBackToGdeltWhenDuckDuckGoReturnsChallenge()
    {
        await using var db = TestDb.Create();
        var requestedHosts = new List<string>();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            requestedHosts.Add(request.RequestUri!.IdnHost);
            if (request.RequestUri.IdnHost == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(
                        """<html><form id="challenge-form"><div class="anomaly-modal">Bots use DuckDuckGo too.</div></form></html>""",
                        Encoding.UTF8,
                        "text/html")
                };
            }

            var json = """
                {"articles":[
                  {
                    "title":"Kimi K3 official guide",
                    "url":"https://platform.kimi.com/docs/guide/kimi-k3-quickstart?utm_source=gdelt",
                    "domain":"platform.kimi.com",
                    "language":"Chinese",
                    "sourcecountry":"China",
                    "seendate":"20260717T100000Z"
                  },
                  {
                    "title":"Independent K3 report",
                    "url":"https://example.org/research/kimi-k3",
                    "domain":"example.org",
                    "language":"English",
                    "sourcecountry":"United States",
                    "seendate":"20260716T083000Z"
                  }
                ]}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "Kimi K3 latest news",
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace());

        Assert.Equal(["html.duckduckgo.com", "api.gdeltproject.org"], requestedHosts);
        Assert.Equal(2, result.Sources.Count);
        Assert.All(result.Sources, source =>
        {
            Assert.Equal("GDELT Project", source.SearchProvider);
            Assert.Equal("https://www.gdeltproject.org/", source.SearchProviderUrl?.AbsoluteUri);
        });
        Assert.Equal(
            "https://platform.kimi.com/docs/guide/kimi-k3-quickstart",
            result.Sources[0].Url.AbsoluteUri);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero),
            result.Sources[0].PublishedAt);
        Assert.Contains("English", result.Sources[1].Snippet);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ResearchErrorKind.RateLimited &&
            failure.HttpStatus == (int)HttpStatusCode.Accepted);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task SearchAsync_AppliesDomainFiltersToStructuredFallbackResults()
    {
        await using var db = TestDb.Create();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            if (request.RequestUri!.IdnHost == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("<form id=\"challenge-form\"></form>")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"articles":[
                      {"title":"Allowed","url":"https://docs.example.com/latest","seendate":"20260718T120000Z"},
                      {"title":"Blocked","url":"https://blocked.example.com/latest","seendate":"20260718T120000Z"},
                      {"title":"Outside","url":"https://outside.test/latest","seendate":"20260718T120000Z"}
                    ]}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "product latest news",
            ResearchMode.Quick,
            new ResearchFilters(
                AllowedDomains: ["example.com"],
                BlockedDomains: ["blocked.example.com"],
                MaxResults: 5),
            AuthorizedWorkspace());

        var source = Assert.Single(result.Sources);
        Assert.Equal("Allowed", source.Title);
        Assert.Equal("docs.example.com", source.Domain);
    }

    [Fact]
    public async Task SearchAsync_FallsBackToChineseWikipediaWhenGdeltIsRateLimited()
    {
        await using var db = TestDb.Create();
        var requestedHosts = new List<string>();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            requestedHosts.Add(request.RequestUri!.IdnHost);
            if (request.RequestUri.IdnHost == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(
                        """<html><script src="https://duckduckgo.com/anomaly.js"></script></html>""",
                        Encoding.UTF8,
                        "text/html")
                };
            }
            if (request.RequestUri.IdnHost == "api.gdeltproject.org")
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"batchcomplete":true,"query":{"search":[{
                      "pageid":123,
                      "title":"Kimi",
                      "snippet":"Kimi 是月之暗面开发的<span class=\"searchmatch\">人工智能助手</span>。"
                    }]}}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "Kimi 最新消息",
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace());

        var source = Assert.Single(result.Sources);
        Assert.Equal("Wikipedia (zh)", source.SearchProvider);
        Assert.Equal("zh.wikipedia.org", source.Domain);
        Assert.Contains("人工智能助手", source.Snippet);
        Assert.Equal("CC BY-SA 4.0", source.LicenseName);
        Assert.Equal(
            "https://creativecommons.org/licenses/by-sa/4.0/",
            source.LicenseUrl?.AbsoluteUri);
        Assert.StartsWith("https://zh.wikipedia.org/wiki/", source.Url.AbsoluteUri);
        Assert.Equal(
            [
                "html.duckduckgo.com",
                "api.gdeltproject.org",
                "zh.wikipedia.org"
            ],
            requestedHosts);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ResearchErrorKind.RateLimited &&
            failure.HttpStatus == (int)HttpStatusCode.TooManyRequests &&
            failure.Attempts == 1);
        Assert.Equal(3, result.Attempts);
    }

    [Fact]
    public async Task SearchAsync_GdeltRateLimitIsNotRetriedAndThrottlesLaterQueryVariants()
    {
        await using var db = TestDb.Create();
        var gdeltCalls = 0;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            var host = request.RequestUri!.IdnHost;
            if (host == "api.gdeltproject.org")
            {
                gdeltCalls++;
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }
            if (host.EndsWith("wikipedia.org", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"batchcomplete":true,"query":{"search":[]}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<html><div class="result--no-result">No results.</div></html>""",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "latest artificial intelligence news",
            ResearchMode.Deep,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace());

        Assert.Empty(result.Sources);
        Assert.Equal(1, gdeltCalls);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ResearchErrorKind.RateLimited &&
            failure.HttpStatus == (int)HttpStatusCode.TooManyRequests &&
            failure.Attempts == 1);
        Assert.Contains(result.Failures, failure =>
            failure.Message.Contains("local provider throttle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchAsync_Gdelt5xxIsAttemptedOnceBeforeWikipediaFallback()
    {
        await using var db = TestDb.Create();
        var gdeltCalls = 0;
        var wikipediaCalls = 0;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            var host = request.RequestUri!.IdnHost;
            if (host == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("<form id=\"challenge-form\"></form>")
                };
            }
            if (host == "api.gdeltproject.org")
            {
                gdeltCalls++;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            if (host == "en.wikipedia.org")
            {
                wikipediaCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"batchcomplete":true,"query":{"search":[{"pageid":7,"title":"Artificial intelligence","snippet":"Current AI research"}]}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            throw new InvalidOperationException($"Unexpected host {host}");
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "latest artificial intelligence news",
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace());

        var source = Assert.Single(result.Sources);
        Assert.Equal("Wikipedia (en)", source.SearchProvider);
        Assert.Equal(1, gdeltCalls);
        Assert.Equal(1, wikipediaCalls);
        Assert.Equal(3, result.Attempts);
        Assert.Contains(result.Failures, failure =>
            failure.Url?.IdnHost == "api.gdeltproject.org" &&
            failure.HttpStatus == (int)HttpStatusCode.ServiceUnavailable &&
            failure.Attempts == 1);
    }

    [Fact]
    public async Task SearchAsync_Wikipedia5xxIsAttemptedOnceBeforeGdeltFallback()
    {
        await using var db = TestDb.Create();
        var wikipediaCalls = 0;
        var gdeltCalls = 0;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            var host = request.RequestUri!.IdnHost;
            if (host == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("<form id=\"challenge-form\"></form>")
                };
            }
            if (host == "en.wikipedia.org")
            {
                wikipediaCalls++;
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }
            if (host == "api.gdeltproject.org")
            {
                gdeltCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"articles":[{"title":"Moonshot AI","url":"https://example.org/moonshot","seendate":"20260718T120000Z"}]}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            throw new InvalidOperationException($"Unexpected host {host}");
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "Moonshot AI",
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace());

        var source = Assert.Single(result.Sources);
        Assert.Equal("GDELT Project", source.SearchProvider);
        Assert.Equal(1, wikipediaCalls);
        Assert.Equal(1, gdeltCalls);
        Assert.Equal(3, result.Attempts);
        Assert.Contains(result.Failures, failure =>
            failure.Url?.IdnHost == "en.wikipedia.org" &&
            failure.HttpStatus == (int)HttpStatusCode.BadGateway &&
            failure.Attempts == 1);
    }

    [Fact]
    public async Task SearchAsync_DeepModeDoesNotRepeatFailingStructuredProviderAcrossQueryVariants()
    {
        await using var db = TestDb.Create();
        var wikipediaCalls = 0;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            var host = request.RequestUri!.IdnHost;
            if (host == "en.wikipedia.org")
            {
                wikipediaCalls++;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            if (host == "api.gdeltproject.org")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"articles":[]}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<html><div class="result--no-result">No results.</div></html>""",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "Moonshot AI",
            ResearchMode.Deep,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace());

        Assert.Empty(result.Sources);
        Assert.Equal(1, wikipediaCalls);
        Assert.Contains(result.Failures, failure =>
            failure.Url?.IdnHost == "en.wikipedia.org" &&
            failure.HttpStatus == (int)HttpStatusCode.ServiceUnavailable);
        Assert.Contains(result.Failures, failure =>
            failure.Message.Contains("local provider throttle", StringComparison.OrdinalIgnoreCase) &&
            failure.Url?.IdnHost == "en.wikipedia.org");
    }

    [Fact]
    public async Task SearchAsync_GdeltTimeoutIsAttemptedOnceBeforeWikipediaFallback()
    {
        await using var db = TestDb.Create();
        var gdeltCalls = 0;
        using var http = new HttpClient(new AsyncMapHttpMessageHandler(async (request, cancellationToken) =>
        {
            var host = request.RequestUri!.IdnHost;
            if (host == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("<form id=\"challenge-form\"></form>")
                };
            }
            if (host == "api.gdeltproject.org")
            {
                gdeltCalls++;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (host == "en.wikipedia.org")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"batchcomplete":true,"query":{"search":[{"pageid":7,"title":"Artificial intelligence","snippet":"Current AI research"}]}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            throw new InvalidOperationException($"Unexpected host {host}");
        }));
        var service = CreateService(db, http);
        var stopwatch = Stopwatch.StartNew();

        var result = await service.SearchAsync(
            "latest artificial intelligence news",
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace() with { TimeoutSeconds = 1 });

        stopwatch.Stop();
        Assert.Single(result.Sources);
        Assert.Equal(1, gdeltCalls);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(4));
        Assert.Contains(result.Failures, failure =>
            failure.Url?.IdnHost == "api.gdeltproject.org" &&
            failure.Kind == ResearchErrorKind.Timeout &&
            failure.Attempts == 1);
    }

    [Theory]
    [InlineData("maxlag")]
    [InlineData("ratelimited")]
    public async Task SearchAsync_WikipediaApiRateEnvelopeFallsBackWithoutRetry(string errorCode)
    {
        await using var db = TestDb.Create();
        var wikipediaCalls = 0;
        var gdeltCalls = 0;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            var host = request.RequestUri!.IdnHost;
            if (host == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("<form id=\"challenge-form\"></form>")
                };
            }
            if (host.EndsWith("wikipedia.org", StringComparison.Ordinal))
            {
                wikipediaCalls++;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"error\":{\"code\":\"" + errorCode + "\",\"info\":\"Please retry later\"}}",
                        Encoding.UTF8,
                        "application/json")
                };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
                return response;
            }
            if (host == "api.gdeltproject.org")
            {
                gdeltCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"articles":[{"title":"Moonshot AI","url":"https://example.org/moonshot","seendate":"20260718T120000Z"}]}""",
                        Encoding.UTF8,
                    "application/json")
                };
            }
            if (host == "lite.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """<html><div class="result--no-result">No results.</div></html>""",
                        Encoding.UTF8,
                        "text/html")
                };
            }
            throw new InvalidOperationException($"Unexpected host {host}");
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "Moonshot AI",
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace());

        Assert.Single(result.Sources);
        Assert.Equal(1, wikipediaCalls);
        Assert.Equal(1, gdeltCalls);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ResearchErrorKind.RateLimited &&
            failure.Message.Contains(errorCode, StringComparison.OrdinalIgnoreCase));

        var followUp = await service.SearchAsync(
            "Moonshot AI follow-up",
            ResearchMode.Quick,
            new ResearchFilters(Language: "en-US", MaxResults: 5),
            AuthorizedWorkspace());

        Assert.Empty(followUp.Sources);
        Assert.Equal(1, wikipediaCalls);
        Assert.Contains(followUp.Failures, failure =>
            failure.Message.Contains("local provider throttle", StringComparison.OrdinalIgnoreCase) &&
            failure.Attempts == 0);
    }

    [Fact]
    public async Task SearchAsync_GenericEntityQueryPrefersLanguageAwareWikimediaBeforeGdelt()
    {
        await using var db = TestDb.Create();
        var requestedHosts = new List<string>();
        Uri? wikipediaRequest = null;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            requestedHosts.Add(request.RequestUri!.IdnHost);
            if (request.RequestUri.IdnHost == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("<form id=\"challenge-form\"></form>")
                };
            }
            wikipediaRequest = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"batchcomplete":true,"query":{"search":[{
                      "pageid":123,"title":"月之暗面",
                      "snippet":"一家<span class=\"searchmatch\">人工智能</span>公司。"
                    }]}}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "月之暗面",
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace());

        var source = Assert.Single(result.Sources);
        Assert.Equal("Wikipedia (zh)", source.SearchProvider);
        Assert.Equal(["html.duckduckgo.com", "zh.wikipedia.org"], requestedHosts);
        Assert.NotNull(wikipediaRequest);
        Assert.Contains("list=search", wikipediaRequest!.Query, StringComparison.Ordinal);
        Assert.Contains("srprop=snippet", wikipediaRequest.Query, StringComparison.Ordinal);
        Assert.Contains("maxlag=5", wikipediaRequest.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("extract", wikipediaRequest.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("月之暗面", "zh-CN", "zh.wikipedia.org", "Wikipedia (zh)")]
    [InlineData("月の裏側 AI", "ja-JP", "ja.wikipedia.org", "Wikipedia (ja)")]
    [InlineData("Moonshot AI", "ko-KR", "ko.wikipedia.org", "Wikipedia (ko)")]
    [InlineData("Moonshot AI", "de-DE", "de.wikipedia.org", "Wikipedia (de)")]
    [InlineData("Moonshot AI", "fr-FR", "fr.wikipedia.org", "Wikipedia (fr)")]
    [InlineData("Moonshot AI", "en-US", "en.wikipedia.org", "Wikipedia (en)")]
    public async Task SearchAsync_ExplicitLanguageUsesMatchingWikipediaEditionAndSkipsGdelt(
        string query,
        string language,
        string expectedHost,
        string expectedProvider)
    {
        await using var db = TestDb.Create();
        var requestedHosts = new List<string>();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            requestedHosts.Add(request.RequestUri!.IdnHost);
            if (request.RequestUri.IdnHost == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("<form id=\"challenge-form\"></form>")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"batchcomplete":true,"query":{"search":[{"pageid":7,"title":"Moonshot AI","snippet":"Artificial intelligence company"}]}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            query,
            ResearchMode.Quick,
            new ResearchFilters(Language: language, MaxResults: 5),
            AuthorizedWorkspace());

        var source = Assert.Single(result.Sources);
        Assert.Equal(expectedHost, source.Domain);
        Assert.Equal(expectedProvider, source.SearchProvider);
        Assert.Equal(["html.duckduckgo.com", expectedHost], requestedHosts);
        Assert.DoesNotContain("api.gdeltproject.org", requestedHosts);
    }

    [Fact]
    public async Task SearchAsync_UnsupportedExplicitLanguageDoesNotSilentlyUseEnglishWikipedia()
    {
        await using var db = TestDb.Create();
        var requestedHosts = new List<string>();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            requestedHosts.Add(request.RequestUri!.IdnHost);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<html><div class="result--no-result">No results.</div></html>""",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "Moonshot AI",
            ResearchMode.Quick,
            new ResearchFilters(Language: "es-ES", MaxResults: 5),
            AuthorizedWorkspace());

        Assert.Empty(result.Sources);
        Assert.Equal(["html.duckduckgo.com", "lite.duckduckgo.com"], requestedHosts);
        Assert.DoesNotContain(requestedHosts, host => host.EndsWith("wikipedia.org", StringComparison.Ordinal));
        Assert.DoesNotContain("api.gdeltproject.org", requestedHosts);
    }

    [Fact]
    public async Task SearchAsync_RecencyFilterNeverAcceptsUndatedWikipediaResults()
    {
        await using var db = TestDb.Create();
        var requestedHosts = new List<string>();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            requestedHosts.Add(request.RequestUri!.IdnHost);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<html><div class="result--no-result">No results.</div></html>""",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "月の裏側 最新情報",
            ResearchMode.Quick,
            new ResearchFilters(
                Recency: ResearchRecency.Month,
                Language: "ja-JP",
                MaxResults: 5),
            AuthorizedWorkspace());

        Assert.Empty(result.Sources);
        Assert.Equal(["html.duckduckgo.com", "lite.duckduckgo.com"], requestedHosts);
        Assert.DoesNotContain(requestedHosts, host => host.EndsWith("wikipedia.org", StringComparison.Ordinal));
        Assert.DoesNotContain("api.gdeltproject.org", requestedHosts);
    }

    [Theory]
    [InlineData("Moonshot AI", "kl=us-en")]
    [InlineData("月之暗面", "kl=cn-zh")]
    [InlineData("月之暗面のモデル", "kl=jp-jp")]
    [InlineData("문샷 AI", "kl=kr-kr")]
    public async Task SearchAsync_InfersProviderLocaleFromQueryScript(
        string query,
        string expectedLocale)
    {
        await using var db = TestDb.Create();
        Uri? requestedUri = null;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<a class="result__a" href="https://example.org/result">Result</a>""",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            query,
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 3),
            AuthorizedWorkspace());

        Assert.Single(result.Sources);
        Assert.NotNull(requestedUri);
        Assert.Equal("html.duckduckgo.com", requestedUri!.IdnHost);
        Assert.Contains(expectedLocale, requestedUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_DistinguishesRecognizedEmptyAndMalformedStructuredResponses()
    {
        await using var db = TestDb.Create();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            if (request.RequestUri!.IdnHost == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """<html><div class="result--no-result">No results.</div></html>""",
                        Encoding.UTF8,
                        "text/html")
                };
            }
            if (request.RequestUri.IdnHost == "api.gdeltproject.org")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"articles":[]}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            if (request.RequestUri.IdnHost.EndsWith("wikipedia.org", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"batchcomplete":true,"query":{"search":"not-an-array"}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<html><div class="result--no-result">No results.</div></html>""",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "a genuinely absent phrase",
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace());

        Assert.Empty(result.Sources);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ResearchErrorKind.Parse, failure.Kind);
        Assert.Equal("en.wikipedia.org", failure.Url?.IdnHost);
        Assert.Equal(4, result.Attempts);
    }

    [Fact]
    public async Task SearchAsync_ReportsMalformedGdeltJsonAndContinuesToWikimedia()
    {
        await using var db = TestDb.Create();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            if (request.RequestUri!.IdnHost == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("<form id=\"challenge-form\"></form>")
                };
            }
            if (request.RequestUri.IdnHost == "api.gdeltproject.org")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"articles\":[", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"batchcomplete":true}""", Encoding.UTF8, "application/json")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "malformed provider payload latest news",
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace());

        Assert.Empty(result.Sources);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ResearchErrorKind.Parse &&
            failure.Url?.IdnHost == "api.gdeltproject.org");
        Assert.DoesNotContain(result.Failures, failure => failure.Url?.IdnHost == "en.wikipedia.org");
    }

    [Fact]
    public async Task SearchAsync_DeepModeDoesNotRepeatMalformedWikimediaPayloadAcrossQueryVariants()
    {
        await using var db = TestDb.Create();
        var wikipediaCalls = 0;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            var host = request.RequestUri!.IdnHost;
            if (host == "en.wikipedia.org")
            {
                wikipediaCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"batchcomplete":true,"query":{"search":"not-an-array"}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            if (host == "api.gdeltproject.org")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"articles":[]}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<html><div class="result--no-result">No results.</div></html>""",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "Moonshot AI",
            ResearchMode.Deep,
            new ResearchFilters(MaxResults: 5),
            AuthorizedWorkspace());

        Assert.Empty(result.Sources);
        Assert.Equal(1, wikipediaCalls);
        Assert.Contains(result.Failures, failure =>
            failure.Kind == ResearchErrorKind.Parse &&
            failure.Url?.IdnHost == "en.wikipedia.org");
        Assert.Contains(result.Failures, failure =>
            failure.Message.Contains("local provider throttle", StringComparison.OrdinalIgnoreCase) &&
            failure.Url?.IdnHost == "en.wikipedia.org");
    }

    [Fact]
    public async Task ExistingNetworkAllowlistIsPreservedWithoutSilentProviderMigration()
    {
        await using var db = TestDb.Create();
        var existing = await db.ToolPlatformSettings.FindAsync(1);
        Assert.NotNull(existing);
        existing.NetworkAllowlist = "html.duckduckgo.com\nwww.bing.com\nlite.duckduckgo.com";
        await db.SaveChangesAsync();

        var settings = await new ToolPlatformService(db).GetSettingsAsync();

        Assert.Equal(
            "html.duckduckgo.com\nwww.bing.com\nlite.duckduckgo.com",
            settings.NetworkAllowlist);
        Assert.True(ToolPlatformService.MatchesDomainList(settings.NetworkAllowlist, "www.bing.com"));
        Assert.False(ToolPlatformService.MatchesDomainList(settings.NetworkAllowlist, "api.gdeltproject.org"));
    }

    [Fact]
    public void NewToolPlatformSettingsIncludeEveryLanguageAwareSearchProvider()
    {
        var allowlist = new ToolPlatformSettings().NetworkAllowlist;

        foreach (var host in new[]
                 {
                     "html.duckduckgo.com",
                     "api.gdeltproject.org",
                     "en.wikipedia.org",
                     "zh.wikipedia.org",
                     "ja.wikipedia.org",
                     "ko.wikipedia.org",
                     "de.wikipedia.org",
                     "fr.wikipedia.org",
                     "lite.duckduckgo.com"
                 })
        {
            Assert.True(ToolPlatformService.MatchesDomainList(allowlist, host), host);
        }
    }

    [Fact]
    public async Task SearchAsync_AppliesRecencyAndLanguageToProviderRequest()
    {
        await using var db = TestDb.Create();
        Uri? requestedUri = null;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """<a class="result__a" href="https://example.org/current">Current result</a>""",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "current release policy",
            ResearchMode.Quick,
            new ResearchFilters(
                Recency: ResearchRecency.Month,
                Language: "zh-CN",
                MaxResults: 3),
            AuthorizedWorkspace());

        Assert.Single(result.Sources);
        Assert.NotNull(requestedUri);
        Assert.Contains("df=m", requestedUri!.Query, StringComparison.Ordinal);
        Assert.Contains("kl=cn-zh", requestedUri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ResearchRecency.Day, "1day")]
    [InlineData(ResearchRecency.Week, "1week")]
    [InlineData(ResearchRecency.Month, "1month")]
    [InlineData(ResearchRecency.Year, "1year")]
    public async Task SearchAsync_GdeltFallbackCarriesSupportedRecencyTimespan(
        ResearchRecency recency,
        string expectedTimespan)
    {
        await using var db = TestDb.Create();
        Uri? gdeltUri = null;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            if (request.RequestUri!.IdnHost == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(
                        """<html><form id="challenge-form"></form></html>""",
                        Encoding.UTF8,
                        "text/html")
                };
            }
            gdeltUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"articles":[{
                      "title":"Current result",
                      "url":"https://example.org/current",
                      "seendate":"20260718T120000Z"
                    }]}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.SearchAsync(
            "current release policy",
            ResearchMode.Quick,
            new ResearchFilters(
                Recency: recency,
                MaxResults: 3),
            AuthorizedWorkspace());

        Assert.Single(result.Sources);
        Assert.NotNull(gdeltUri);
        Assert.Equal("api.gdeltproject.org", gdeltUri!.IdnHost);
        Assert.Contains($"timespan={expectedTimespan}", gdeltUri.Query, StringComparison.Ordinal);
        Assert.Contains("format=json", gdeltUri.Query, StringComparison.Ordinal);
        Assert.Contains("sort=HybridRel", gdeltUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResearchAsync_PackagesIndependentEvidenceAndSurvivesPartialFailure()
    {
        await using var db = TestDb.Create();
        var searchHtml = """
            <html><body>
              <div class="result"><a class="result__a" href="https://alpha.example/article">Alpha report</a><span class="result__snippet">Measured adoption.</span></div>
              <div class="result"><a class="result__a" href="https://beta.example/study">Beta study</a><span class="result__snippet">Independent measurement.</span></div>
              <div class="result"><a class="result__a" href="https://gamma.example/review">Gamma review</a><span class="result__snippet">Review of adoption.</span></div>
              <div class="result"><a class="result__a" href="https://broken.example/report">Unavailable report</a></div>
            </body></html>
            """;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            var host = request.RequestUri!.Host;
            if (host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(searchHtml, Encoding.UTF8, "text/html")
                };
            }
            if (host == "broken.example")
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var body = host switch
            {
                "alpha.example" => "<article><h1>Alpha</h1><p>The adoption measurement was 10 percent in 2026.</p></article>",
                "beta.example" => "<article><h1>Beta</h1><p>The adoption measurement was 12 percent in 2026.</p></article>",
                _ => "<article><h1>Gamma</h1><p>The adoption measurement was reviewed independently.</p></article>"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            };
        }));
        var service = CreateService(db, http);
        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            "TLAH.Research.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var result = await service.ResearchAsync(
                "adoption measurement 2026",
                ResearchMode.Deep,
                new ResearchFilters(MaxResults: 10),
                AuthorizedWorkspace() with
                {
                    ArtifactDirectory = artifactRoot,
                    CreateReportArtifact = true,
                    ReportFileName = "evidence.md"
                });

            Assert.Equal(3, result.Sources.Count);
            Assert.Equal(3, result.IndependentDomainCount);
            Assert.Equal(ResearchCoverage.Partial, result.Coverage);
            Assert.Contains(result.Failures, failure =>
                failure.Url?.Host == "broken.example" &&
                failure.Kind == ResearchErrorKind.HttpStatus);
            Assert.Contains(result.Conflicts, conflict =>
                conflict.Kind == "potential_measurement_discrepancy");
            Assert.Contains("does not invent or assert", result.ReportMarkdown);
            Assert.NotNull(result.ReportArtifact);
            Assert.True(File.Exists(result.ReportArtifact!.FullPath));
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
                Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ResearchAsync_PreservesWikipediaProviderAndLicenseAttributionInReport()
    {
        await using var db = TestDb.Create();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.IdnHost == "html.duckduckgo.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("<form id=\"challenge-form\"></form>")
                };
            }
            if (uri.AbsolutePath == "/w/api.php")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"batchcomplete":true,"query":{"search":[{"pageid":123,"title":"月之暗面","snippet":"一家人工智能公司"}]}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<article><h1>月之暗面</h1><p>月之暗面是一家人工智能公司，开发了 Kimi 助手。</p></article>",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.ResearchAsync(
            "月之暗面",
            ResearchMode.Quick,
            new ResearchFilters(Language: "zh-CN", MaxResults: 5),
            AuthorizedWorkspace());

        var source = Assert.Single(result.Sources);
        Assert.Equal("Wikipedia (zh)", source.SearchProvider);
        Assert.Equal("https://zh.wikipedia.org/", source.SearchProviderUrl?.AbsoluteUri);
        Assert.Equal("CC BY-SA 4.0", source.LicenseName);
        Assert.Equal(
            "https://creativecommons.org/licenses/by-sa/4.0/",
            source.LicenseUrl?.AbsoluteUri);
        Assert.Contains("Discovered via: Wikipedia (zh) (https://zh.wikipedia.org/)", result.ReportMarkdown);
        Assert.Contains(
            "License: CC BY-SA 4.0 (https://creativecommons.org/licenses/by-sa/4.0/)",
            result.ReportMarkdown);
        Assert.Contains("URL: https://zh.wikipedia.org/wiki/", result.ReportMarkdown);
    }

    [Fact]
    public async Task ResearchAsync_LabelsSingleSourceEvidenceAsInsufficient()
    {
        await using var db = TestDb.Create();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """<a class="result__a" href="https://only.example/fact">Only source</a>""",
                        Encoding.UTF8,
                        "text/html")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<article><h1>Only source</h1><p>The requested fact appears in one source.</p></article>",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var service = CreateService(db, http);

        var result = await service.ResearchAsync(
            "requested fact",
            ResearchMode.Quick,
            new ResearchFilters(MaxResults: 3),
            AuthorizedWorkspace());

        Assert.Equal(ResearchCoverage.Insufficient, result.Coverage);
        Assert.Single(result.Sources);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("insufficient", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("insufficient", result.ReportMarkdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadPageAsync_ExtractsPdfAndRejectsUnsupportedBinary()
    {
        await using var db = TestDb.Create();
        var pdf = CreateMinimalPdf("PDF evidence text");
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var content = new ByteArrayContent(pdf);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            var image = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = image };
        }));
        var service = CreateService(db, http);

        var page = await service.ReadPageAsync(
            "https://example.test/evidence.pdf",
            "evidence",
            AuthorizedWorkspace());
        Assert.Equal(ResearchContentKind.Pdf, page.ContentKind);
        Assert.Contains("PDF evidence text", page.Text);

        var exception = await Assert.ThrowsAsync<ResearchServiceException>(() =>
            service.ReadPageAsync(
                "https://example.test/image.png",
                null,
                AuthorizedWorkspace()));
        Assert.Equal(ResearchErrorKind.UnsupportedContent, exception.Failure.Kind);
    }

    [Fact]
    public async Task ResearchVerifyAgentTool_CreatesAttachableReportWithoutHiddenCommands()
    {
        await using var db = TestDb.Create();
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """<a class="result__a" href="https://source.example/fact">Primary source</a>""",
                        Encoding.UTF8,
                        "text/html")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<article><h1>Primary source</h1><p>The requested fact is documented here.</p></article>",
                    Encoding.UTF8,
                    "text/html")
            };
        }));
        var workbench = CreateService(db, http);
        var root = Path.Combine(
            Path.GetTempPath(),
            "TLAH.Research.Tool.Tests",
            Guid.NewGuid().ToString("N"));
        var sandbox = new SandboxCommandService(root);
        var tool = new ResearchVerifyAgentTool(workbench, sandbox);
        var chatId = Guid.NewGuid();

        try
        {
            var result = await tool.ExecuteAsync(
                new AgentToolExecutionContext(
                    chatId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    15,
                    20_000,
                    AgentPermissionModes.BypassPermissions),
                """{"query":"requested fact","mode":"quick"}""");

            Assert.True(result.Success, result.Error);
            var artifact = Assert.Single(result.Artifacts!);
            var fullPath = Path.Combine(sandbox.GetSandboxRoot(chatId), artifact.RelativePath);
            Assert.True(File.Exists(fullPath));
            Assert.EndsWith(".md", fullPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Research evidence pack", await File.ReadAllTextAsync(fullPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadPageAsync_FullAccessStillBlocksPrivateNetworkTargets()
    {
        await using var db = TestDb.Create();
        var requestCount = 0;
        using var http = new HttpClient(new MapHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("private", Encoding.UTF8, "text/plain")
            };
        }));
        var service = new ResearchWorkbenchService(
            new ToolPlatformService(db),
            new NetworkSecurityService(),
            new StaticHttpClientFactory(http));

        var exception = await Assert.ThrowsAsync<ResearchServiceException>(() =>
            service.ReadPageAsync(
                "https://127.0.0.1/private",
                null,
                AuthorizedWorkspace()));

        Assert.Equal(ResearchErrorKind.SecurityPolicy, exception.Failure.Kind);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task ReadPageAsync_TimesOutWhileRemoteBodyDripsAfterHeaders()
    {
        await using var db = TestDb.Create();
        using var http = new HttpClient(new MapHttpMessageHandler(_ =>
        {
            var content = new StreamContent(new NeverCompletingReadStream());
            content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var service = CreateService(db, http);
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<ResearchServiceException>(() =>
            service.ReadPageAsync(
                "https://slow.example.test/article",
                null,
                AuthorizedWorkspace() with { TimeoutSeconds = 1 }));

        stopwatch.Stop();
        Assert.Equal(ResearchErrorKind.Timeout, exception.Failure.Kind);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
    }

    private static ResearchWorkbenchService CreateService(
        TLAHStudio.Data.TlahDbContext db,
        HttpClient http) =>
        new(
            new ToolPlatformService(db),
            new AllowAllResearchNetworkSecurity(),
            new StaticHttpClientFactory(http));

    private static ResearchWorkspace AuthorizedWorkspace() =>
        new(AgentPermissionModes.BypassPermissions, TimeoutSeconds: 15);

    private static byte[] CreateMinimalPdf(string text)
    {
        text = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        var stream = $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream"
        };
        using var output = new MemoryStream();
        using var writer = new StreamWriter(output, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        writer.Write("%PDF-1.4\n");
        writer.Flush();
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(output.Position);
            writer.Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
            writer.Flush();
        }
        var xref = output.Position;
        writer.Write($"xref\n0 {objects.Length + 1}\n");
        writer.Write("0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            writer.Write($"{offset:0000000000} 00000 n \n");
        writer.Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        writer.Flush();
        return output.ToArray();
    }

    private sealed class AllowAllResearchNetworkSecurity : INetworkSecurityService
    {
        public Task<Uri> ValidateAsync(
            string url,
            ToolPlatformSettings settings,
            CancellationToken ct = default,
            bool bypassRestrictions = false)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new InvalidOperationException("A valid absolute URL is required.");
            return Task.FromResult(uri);
        }
    }

    private sealed class AsyncMapHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class NeverCompletingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
