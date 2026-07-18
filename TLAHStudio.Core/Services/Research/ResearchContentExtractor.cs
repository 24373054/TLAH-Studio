using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using UglyToad.PdfPig;

namespace TLAHStudio.Core.Services.Research;

internal static partial class ResearchContentExtractor
{
    public static ResearchPage ExtractHtml(
        Uri requestedUrl,
        Uri finalUrl,
        int status,
        string contentType,
        string html,
        int maxChars,
        int attemptCount)
    {
        var document = new HtmlParser().ParseDocument(html);
        foreach (var selector in new[]
                 {
                     "script", "style", "noscript", "template", "svg", "canvas",
                     "nav", "footer", "form", "[aria-hidden=true]", "[hidden]"
                 })
        {
            foreach (var element in document.QuerySelectorAll(selector).ToArray())
                element.Remove();
        }

        var title = FirstNonEmpty(
            MetaContent(document, "property", "og:title"),
            MetaContent(document, "name", "twitter:title"),
            document.Title,
            document.QuerySelector("h1")?.TextContent,
            finalUrl.Host);
        var description = FirstNonEmpty(
            MetaContent(document, "property", "og:description"),
            MetaContent(document, "name", "description"));
        var language = document.DocumentElement?.GetAttribute("lang")?.Trim() ?? string.Empty;
        var publishedAt = ExtractPublishedAt(document);
        var contentRoot = SelectContentRoot(document);
        var text = ExtractReadableText(contentRoot, maxChars, out var truncated);
        var links = ExtractLinks(document, finalUrl);

        return new ResearchPage(
            requestedUrl,
            finalUrl,
            status,
            ResearchContentKind.Html,
            contentType,
            Clean(title),
            Clean(description),
            language,
            publishedAt,
            text,
            links,
            truncated,
            attemptCount);
    }

    public static ResearchPage ExtractText(
        Uri requestedUrl,
        Uri finalUrl,
        int status,
        string contentType,
        string text,
        int maxChars,
        int attemptCount)
    {
        var clean = NormalizeLines(text);
        var truncated = clean.Length > maxChars;
        if (truncated)
            clean = clean[..maxChars];
        var title = Uri.UnescapeDataString(finalUrl.Segments.LastOrDefault()?.Trim('/') ?? finalUrl.Host);
        return new ResearchPage(
            requestedUrl,
            finalUrl,
            status,
            ResearchContentKind.Text,
            contentType,
            title,
            string.Empty,
            string.Empty,
            null,
            clean,
            [],
            truncated,
            attemptCount);
    }

    public static ResearchPage ExtractPdf(
        Uri requestedUrl,
        Uri finalUrl,
        int status,
        string contentType,
        byte[] bytes,
        int maxChars,
        int attemptCount)
    {
        using var document = PdfDocument.Open(bytes);
        var output = new StringBuilder(Math.Min(maxChars, 16_384));
        var truncated = false;
        var maxPages = Math.Min(document.NumberOfPages, 50);
        for (var pageNumber = 1; pageNumber <= maxPages; pageNumber++)
        {
            var pageText = NormalizeLines(document.GetPage(pageNumber).Text);
            if (pageText.Length == 0)
                continue;
            var heading = $"\n\n[Page {pageNumber}]\n";
            if (output.Length + heading.Length + pageText.Length > maxChars)
            {
                var available = Math.Max(0, maxChars - output.Length - heading.Length);
                output.Append(heading);
                if (available > 0)
                    output.Append(pageText.AsSpan(0, Math.Min(available, pageText.Length)));
                truncated = true;
                break;
            }
            output.Append(heading).Append(pageText);
        }
        if (document.NumberOfPages > maxPages)
            truncated = true;

        var title = Uri.UnescapeDataString(finalUrl.Segments.LastOrDefault()?.Trim('/') ?? "PDF document");
        return new ResearchPage(
            requestedUrl,
            finalUrl,
            status,
            ResearchContentKind.Pdf,
            contentType,
            title,
            string.Empty,
            string.Empty,
            null,
            output.ToString().Trim(),
            [],
            truncated,
            attemptCount);
    }

    public static string BuildExcerpt(string text, string query, int maxChars = 700)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        maxChars = Math.Clamp(maxChars, 120, 2_000);
        var normalized = NormalizeLines(text);
        var terms = ResearchText.Tokenize(query).Where(term => term.Length >= 3).ToArray();
        var index = terms
            .Select(term => normalized.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(position => position >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Max(0, index - maxChars / 4);
        if (start > 0)
        {
            var boundary = normalized.LastIndexOfAny(['.', '!', '?', '\n', '。', '！', '？'], start);
            if (boundary >= 0 && start - boundary < 180)
                start = boundary + 1;
        }
        var length = Math.Min(maxChars, normalized.Length - start);
        var excerpt = normalized.Substring(start, length).Trim();
        if (start > 0)
            excerpt = "…" + excerpt;
        if (start + length < normalized.Length)
            excerpt += "…";
        return excerpt;
    }

    private static IElement SelectContentRoot(IDocument document)
    {
        var candidates = document
            .QuerySelectorAll("article, main, [role=main], .article, .post, .entry-content, .content")
            .Where(element => !string.IsNullOrWhiteSpace(element.TextContent))
            .OrderByDescending(element => element.TextContent.Length)
            .ToArray();
        return candidates.FirstOrDefault() ?? document.Body ?? document.DocumentElement!;
    }

    private static string ExtractReadableText(IElement root, int maxChars, out bool truncated)
    {
        var blocks = root.QuerySelectorAll("h1, h2, h3, h4, p, li, pre, blockquote, figcaption, td, th")
            .Select(element => Clean(element.TextContent))
            .Where(text => text.Length > 1)
            .ToArray();
        if (blocks.Length == 0)
            blocks = [Clean(root.TextContent)];

        var output = new StringBuilder(Math.Min(maxChars, 16_384));
        string? previous = null;
        truncated = false;
        foreach (var block in blocks)
        {
            if (string.Equals(block, previous, StringComparison.Ordinal))
                continue;
            var separator = output.Length == 0 ? string.Empty : Environment.NewLine + Environment.NewLine;
            if (output.Length + separator.Length + block.Length > maxChars)
            {
                var available = Math.Max(0, maxChars - output.Length - separator.Length);
                output.Append(separator);
                if (available > 0)
                    output.Append(block.AsSpan(0, Math.Min(available, block.Length)));
                truncated = true;
                break;
            }
            output.Append(separator).Append(block);
            previous = block;
        }
        return output.ToString().Trim();
    }

    private static IReadOnlyList<ResearchLink> ExtractLinks(IDocument document, Uri baseUri)
    {
        var links = new List<ResearchLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var raw = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(baseUri, raw, out var resolved))
                continue;
            if (resolved.Scheme is not ("http" or "https"))
                continue;
            var canonical = ResearchText.CanonicalizeUrl(resolved);
            if (!seen.Add(canonical.AbsoluteUri))
                continue;
            links.Add(new ResearchLink(Clean(anchor.TextContent), canonical));
            if (links.Count >= 100)
                break;
        }
        return links;
    }

    private static DateTimeOffset? ExtractPublishedAt(IDocument document)
    {
        foreach (var value in new[]
                 {
                     MetaContent(document, "property", "article:published_time"),
                     MetaContent(document, "name", "date"),
                     MetaContent(document, "name", "pubdate"),
                     MetaContent(document, "itemprop", "datePublished"),
                     document.QuerySelector("time[datetime]")?.GetAttribute("datetime")
                 })
        {
            if (DateTimeOffset.TryParse(value, out var parsed))
                return parsed;
        }

        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent);
                var date = FindJsonString(json.RootElement, "datePublished");
                if (DateTimeOffset.TryParse(date, out var parsed))
                    return parsed;
            }
            catch (JsonException)
            {
                // Invalid embedded metadata must not prevent readable extraction.
            }
        }
        return null;
    }

    private static string? FindJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = FindJsonString(property.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindJsonString(item, propertyName);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        return null;
    }

    private static string MetaContent(IDocument document, string attribute, string value) =>
        document.QuerySelector($"meta[{attribute}='{value}' i]")?.GetAttribute("content")?.Trim() ?? string.Empty;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string Clean(string? value) =>
        WhitespaceRegex().Replace(value ?? string.Empty, " ").Trim();

    private static string NormalizeLines(string value)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(Clean)
            .Where(line => line.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

internal static partial class ResearchText
{
    private static readonly HashSet<string> StopWords = new(
        ["the", "and", "for", "with", "that", "this", "from", "into", "about", "what",
         "when", "where", "which", "how", "are", "was", "were", "has", "have", "had",
         "的", "了", "是", "在", "和", "与", "及", "对", "中"],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Tokenize(string value) =>
        TokenRegex().Matches(value.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(token => !StopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static Uri CanonicalizeUrl(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        var retained = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair =>
            {
                var name = Uri.UnescapeDataString(pair.Split('=', 2)[0]);
                return !name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("gclid", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("fbclid", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("mc_cid", StringComparison.OrdinalIgnoreCase) &&
                       !name.Equals("mc_eid", StringComparison.OrdinalIgnoreCase);
            });
        builder.Query = string.Join('&', retained);
        return builder.Uri;
    }

    public static bool DomainMatches(string host, string pattern)
    {
        var normalized = NormalizeDomain(pattern);
        return normalized.Length > 0 &&
               (host.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeDomain(string value)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            value = uri.IdnHost;
        value = value.TrimStart('*', '.').TrimEnd('.');
        var slash = value.IndexOf('/');
        return (slash >= 0 ? value[..slash] : value).ToLowerInvariant();
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_\-.]*", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
