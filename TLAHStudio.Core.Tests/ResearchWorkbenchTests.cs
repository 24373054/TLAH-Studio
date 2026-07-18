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
