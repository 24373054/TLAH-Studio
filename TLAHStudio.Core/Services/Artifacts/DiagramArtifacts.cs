using System.Globalization;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;

namespace TLAHStudio.Core.Services.Artifacts;

public sealed partial class ArtifactWorkbenchService
{
    private static readonly HashSet<string> DiagramFormats = new(
        [".svg", ".png"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<ArtifactWorkbenchResult> CreateDiagramAsync(
        ArtifactExecutionScope scope,
        DiagramCreateRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var type = NormalizeDiagramType(request.Type);
            var formats = request.Formats
                .Select(format => "." + format.Trim().TrimStart('.').ToLowerInvariant())
                .Where(DiagramFormats.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (formats.Length == 0)
                throw new InvalidOperationException("At least one output format is required: svg or png.");

            var width = Math.Clamp(request.Width, 640, 4096);
            var height = Math.Clamp(request.Height, 420, 3072);
            var requestedScale = Math.Clamp(request.Scale, 1, 4);
            var scale = formats.Contains(".png", StringComparer.OrdinalIgnoreCase)
                ? DiagramRenderer.ClampPngScaleToPixelBudget(width, height, requestedScale)
                : requestedScale;
            ValidateDiagramInput(type, request);

            var requestedBase = string.IsNullOrWhiteSpace(request.Path)
                ? "artifacts/diagram"
                : request.Path.Trim();
            if (DiagramFormats.Contains(Path.GetExtension(requestedBase)))
                requestedBase = Path.ChangeExtension(requestedBase, null)!;

            var artifacts = new List<AgentToolArtifact>();
            var outputs = new List<object>();
            foreach (var extension in formats)
            {
                var operationToken = artifacts.Count == 0
                    ? ct
                    : CancellationToken.None;
                operationToken.ThrowIfCancellationRequested();
                var finalPath = ResolveOutputPath(
                    scope,
                    requestedBase + extension,
                    "artifacts/diagram" + extension,
                    DiagramFormats,
                    request.Overwrite);

                if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    var svg = DiagramRenderer.RenderSvg(request, type, width, height);
                    await WriteAllTextAtomicAsync(
                        finalPath,
                        svg,
                        path =>
                        {
                            var document = XDocument.Load(path);
                            if (document.Root?.Name.LocalName != "svg")
                                throw new InvalidDataException("Generated SVG has no svg root element.");
                            return Task.CompletedTask;
                        },
                        operationToken);
                }
                else
                {
                    await GenerateAtomicAsync(
                        finalPath,
                        path =>
                        {
                            DiagramRenderer.RenderPng(request, type, width, height, scale, path);
                            return Task.CompletedTask;
                        },
                        path =>
                        {
                            var bytes = File.ReadAllBytes(path);
                            using var bitmap = SKBitmap.Decode(bytes)
                                ?? throw new InvalidDataException("Generated PNG could not be decoded.");
                            if (bitmap.Width != width * scale || bitmap.Height != height * scale)
                                throw new InvalidDataException("Generated PNG dimensions are incorrect.");
                            return Task.CompletedTask;
                        },
                        operationToken);
                }

                var artifact = await BuildArtifactAsync(
                    scope,
                    finalPath,
                    CancellationToken.None);
                artifacts.Add(artifact);
                outputs.Add(new
                {
                    artifact.RelativePath,
                    format = extension.TrimStart('.'),
                    width = extension == ".png" ? width * scale : width,
                    height = extension == ".png" ? height * scale : height
                });
            }

            return Success(
                scale == requestedScale
                    ? $"Created {type} diagram in {artifacts.Count} format(s)."
                    : $"Created {type} diagram in {artifacts.Count} format(s); PNG scale was reduced from {requestedScale}x to {scale}x to stay within the safe rendering budget.",
                new
                {
                    type,
                    request.Title,
                    logical_width = width,
                    logical_height = height,
                    requested_scale = requestedScale,
                    scale,
                    png_pixel_budget = DiagramRenderer.MaxPngPixelCount,
                    node_count = request.Nodes.Count,
                    edge_count = request.Edges.Count,
                    series_count = request.Series.Count,
                    outputs
                },
                artifacts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or InvalidDataException or
                                   ArgumentException or OutOfMemoryException)
        {
            return Failure(ex);
        }
    }

    private static string NormalizeDiagramType(string? type)
    {
        var normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "flow" or "flow_chart" => "flowchart",
            "arch" => "architecture",
            "column" => "bar",
            _ => normalized
        };
    }

    private static void ValidateDiagramInput(string type, DiagramCreateRequest request)
    {
        if (type is not ("flowchart" or "architecture" or "bar" or "line"))
            throw new InvalidOperationException("Diagram type must be flowchart, architecture, bar, or line.");

        if (type is "flowchart" or "architecture")
        {
            if (request.Nodes.Count == 0)
                throw new InvalidOperationException($"{type} diagrams require at least one node.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in request.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Label))
                    throw new InvalidOperationException("Every diagram node requires a non-empty id and label.");
                if (!ids.Add(node.Id))
                    throw new InvalidOperationException($"Duplicate diagram node id: {node.Id}.");
            }

            foreach (var edge in request.Edges)
            {
                if (!ids.Contains(edge.From) || !ids.Contains(edge.To))
                    throw new InvalidOperationException(
                        $"Diagram edge '{edge.From}' -> '{edge.To}' references an unknown node.");
            }
        }
        else
        {
            if (request.Labels.Count == 0 || request.Series.Count == 0)
                throw new InvalidOperationException($"{type} charts require labels and at least one series.");
            if (request.Series.Any(series => series.Values.Count != request.Labels.Count))
                throw new InvalidOperationException("Every chart series must contain one value per label.");
        }
    }
}

internal static class DiagramRenderer
{
    private const float Margin = 64;
    // A 24 MP RGBA surface is about 96 MB before the Skia snapshot/encoder.
    // Keeping the primary surface below this bound avoids multi-hundred-MB
    // allocations while preserving high-DPI output for normal canvas sizes.
    internal const long MaxPngPixelCount = 24_000_000;
    private static readonly string[] SeriesColors =
        ["#5B5CE2", "#00A892", "#EF8354", "#3D8BFF", "#A85CF0", "#D0A021"];

    public static string RenderSvg(
        DiagramCreateRequest request,
        string type,
        int width,
        int height)
    {
        var palette = DiagramPalette.For(request.Theme);
        var builder = new StringBuilder();
        builder.AppendLine(
            $"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img" aria-label="{Escape(request.Title)}">""");
        builder.AppendLine("<defs>");
        builder.AppendLine(
            $"""<linearGradient id="accent" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{palette.Accent}"/><stop offset="1" stop-color="{palette.Accent2}"/></linearGradient>""");
        builder.AppendLine(
            $"""<filter id="shadow" x="-20%" y="-20%" width="140%" height="150%"><feDropShadow dx="0" dy="8" stdDeviation="10" flood-color="{palette.Shadow}" flood-opacity=".20"/></filter>""");
        builder.AppendLine(
            $"""<marker id="arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="{palette.Muted}"/></marker>""");
        builder.AppendLine("</defs>");
        builder.AppendLine($"""<rect width="100%" height="100%" rx="28" fill="{palette.Background}"/>""");
        AddSvgTitle(builder, request.Title, palette, width);

        if (type is "flowchart" or "architecture")
            AddSvgNodeDiagram(builder, request, type, palette, width, height);
        else
            AddSvgChart(builder, request, type, palette, width, height);

        builder.AppendLine("</svg>");
        return builder.ToString();
    }

    public static void RenderPng(
        DiagramCreateRequest request,
        string type,
        int width,
        int height,
        int scale,
        string path)
    {
        var pixelWidth = checked(width * scale);
        var pixelHeight = checked(height * scale);
        var pixelCount = (long)pixelWidth * pixelHeight;
        if (pixelCount > MaxPngPixelCount)
        {
            throw new InvalidOperationException(
                $"PNG dimensions {pixelWidth}x{pixelHeight} exceed the safe {MaxPngPixelCount:N0}-pixel rendering budget.");
        }

        var palette = DiagramPalette.For(request.Theme);
        var imageInfo = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(imageInfo)
            ?? throw new InvalidOperationException("Unable to allocate PNG drawing surface.");
        var canvas = surface.Canvas;
        canvas.Scale(scale);
        canvas.Clear(ParseColor(palette.Background));

        using var titleStyle = TextStyle.Create(ParseColor(palette.Foreground), 28, bold: true);
        DrawText(canvas, request.Title, Margin, 58, titleStyle);

        if (type is "flowchart" or "architecture")
            DrawNodeDiagram(canvas, request, type, palette, width, height);
        else
            DrawChart(canvas, request, type, palette, width, height);

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 95);
        File.WriteAllBytes(path, encoded.ToArray());
    }

    internal static int ClampPngScaleToPixelBudget(
        int width,
        int height,
        int requestedScale)
    {
        var logicalPixels = (long)width * height;
        if (logicalPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");

        var budgetScale = (int)Math.Floor(Math.Sqrt(
            MaxPngPixelCount / (double)logicalPixels));
        return Math.Clamp(Math.Min(requestedScale, budgetScale), 1, 4);
    }

    private static void AddSvgTitle(StringBuilder builder, string title, DiagramPalette palette, int width)
    {
        builder.AppendLine(
            $"""<text x="{Margin}" y="58" fill="{palette.Foreground}" font-family="Segoe UI, sans-serif" font-size="28" font-weight="700">{Escape(title)}</text>""");
        builder.AppendLine(
            $"""<line x1="{Margin}" y1="78" x2="{width - Margin}" y2="78" stroke="{palette.Border}" stroke-width="1"/>""");
    }

    private static void AddSvgNodeDiagram(
        StringBuilder builder,
        DiagramCreateRequest request,
        string type,
        DiagramPalette palette,
        int width,
        int height)
    {
        var layouts = LayoutNodes(request, type, width, height);
        var lookup = layouts.ToDictionary(item => item.Node.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in request.Edges)
        {
            var from = lookup[edge.From];
            var to = lookup[edge.To];
            var fromCenter = new SKPoint(from.X + from.Width / 2, from.Y + from.Height / 2);
            var toCenter = new SKPoint(to.X + to.Width / 2, to.Y + to.Height / 2);
            var start = ConnectorPoint(from, toCenter);
            var end = ConnectorPoint(to, fromCenter);
            builder.AppendLine(
                $"""<path d="M {F(start.X)} {F(start.Y)} C {F(start.X)} {F((start.Y + end.Y) / 2)}, {F(end.X)} {F((start.Y + end.Y) / 2)}, {F(end.X)} {F(end.Y)}" fill="none" stroke="{palette.Muted}" stroke-width="2" marker-end="url(#arrow)"/>""");
            if (!string.IsNullOrWhiteSpace(edge.Label))
            {
                builder.AppendLine(
                    $"""<text x="{F((start.X + end.X) / 2)}" y="{F((start.Y + end.Y) / 2 - 7)}" text-anchor="middle" fill="{palette.Muted}" font-family="Segoe UI, sans-serif" font-size="13">{Escape(edge.Label!)}</text>""");
            }
        }

        foreach (var item in layouts)
        {
            builder.AppendLine(
                $"""<rect x="{F(item.X)}" y="{F(item.Y)}" width="{F(item.Width)}" height="{F(item.Height)}" rx="18" fill="{palette.Surface}" stroke="{palette.Border}" filter="url(#shadow)"/>""");
            builder.AppendLine(
                $"""<rect x="{F(item.X)}" y="{F(item.Y)}" width="7" height="{F(item.Height)}" rx="3.5" fill="url(#accent)"/>""");
            builder.AppendLine(
                $"""<text x="{F(item.X + 24)}" y="{F(item.Y + 39)}" fill="{palette.Foreground}" font-family="Segoe UI, sans-serif" font-size="18" font-weight="650">{Escape(Trim(item.Node.Label, 30))}</text>""");
            if (!string.IsNullOrWhiteSpace(item.Node.Description))
            {
                builder.AppendLine(
                    $"""<text x="{F(item.X + 24)}" y="{F(item.Y + 66)}" fill="{palette.Muted}" font-family="Segoe UI, sans-serif" font-size="13">{Escape(Trim(item.Node.Description!, 44))}</text>""");
            }
            if (!string.IsNullOrWhiteSpace(item.Node.Group))
            {
                builder.AppendLine(
                    $"""<text x="{F(item.X + item.Width - 16)}" y="{F(item.Y + 24)}" text-anchor="end" fill="{palette.Accent}" font-family="Segoe UI, sans-serif" font-size="11" font-weight="650">{Escape(Trim(item.Node.Group!, 20).ToUpperInvariant())}</text>""");
            }
        }
    }

    private static void DrawNodeDiagram(
        SKCanvas canvas,
        DiagramCreateRequest request,
        string type,
        DiagramPalette palette,
        int width,
        int height)
    {
        var layouts = LayoutNodes(request, type, width, height);
        var lookup = layouts.ToDictionary(item => item.Node.Id, StringComparer.OrdinalIgnoreCase);
        using var edgePaint = StrokePaint(ParseColor(palette.Muted), 2);
        using var arrowPaint = FillPaint(ParseColor(palette.Muted));
        foreach (var edge in request.Edges)
        {
            var from = lookup[edge.From];
            var to = lookup[edge.To];
            var fromCenter = new SKPoint(from.X + from.Width / 2, from.Y + from.Height / 2);
            var toCenter = new SKPoint(to.X + to.Width / 2, to.Y + to.Height / 2);
            var start = ConnectorPoint(from, toCenter);
            var end = ConnectorPoint(to, fromCenter);
            using var pathBuilder = new SKPathBuilder();
            pathBuilder.MoveTo(start);
            pathBuilder.CubicTo(start.X, (start.Y + end.Y) / 2, end.X, (start.Y + end.Y) / 2, end.X, end.Y);
            using var path = pathBuilder.Detach();
            canvas.DrawPath(path, edgePaint);
            var arrowOrigin = Math.Abs(start.Y - end.Y) < 0.001f
                ? start
                : new SKPoint(end.X, (start.Y + end.Y) / 2);
            DrawArrow(canvas, arrowOrigin, end, arrowPaint);
            if (!string.IsNullOrWhiteSpace(edge.Label))
            {
                using var labelStyle = TextStyle.Create(ParseColor(palette.Muted), 13);
                DrawCenteredText(canvas, edge.Label!, (start.X + end.X) / 2, (start.Y + end.Y) / 2 - 8, labelStyle);
            }
        }

        using var surfacePaint = FillPaint(ParseColor(palette.Surface));
        using var borderPaint = StrokePaint(ParseColor(palette.Border), 1);
        using var accentPaint = FillPaint(ParseColor(palette.Accent));
        using var headingStyle = TextStyle.Create(ParseColor(palette.Foreground), 18, bold: true);
        using var bodyStyle = TextStyle.Create(ParseColor(palette.Muted), 13);
        using var groupStyle = TextStyle.Create(ParseColor(palette.Accent), 11, bold: true);
        foreach (var item in layouts)
        {
            var rect = new SKRoundRect(new SKRect(item.X, item.Y, item.X + item.Width, item.Y + item.Height), 18);
            canvas.DrawRoundRect(rect, surfacePaint);
            canvas.DrawRoundRect(rect, borderPaint);
            canvas.DrawRoundRect(
                new SKRoundRect(new SKRect(item.X, item.Y, item.X + 7, item.Y + item.Height), 3.5f),
                accentPaint);
            DrawText(canvas, Trim(item.Node.Label, 30), item.X + 24, item.Y + 39, headingStyle);
            if (!string.IsNullOrWhiteSpace(item.Node.Description))
                DrawText(canvas, Trim(item.Node.Description!, 44), item.X + 24, item.Y + 66, bodyStyle);
            if (!string.IsNullOrWhiteSpace(item.Node.Group))
            {
                var text = Trim(item.Node.Group!, 20).ToUpperInvariant();
                DrawText(
                    canvas,
                    text,
                    item.X + item.Width - 16 - groupStyle.Font.MeasureText(text, groupStyle.Paint),
                    item.Y + 24,
                    groupStyle);
            }
        }
    }

    private static SKPoint ConnectorPoint(NodeLayout node, SKPoint toward)
    {
        var center = new SKPoint(node.X + node.Width / 2, node.Y + node.Height / 2);
        var dx = toward.X - center.X;
        var dy = toward.Y - center.Y;
        if (Math.Abs(dx) < 0.001f && Math.Abs(dy) < 0.001f)
            return center;

        var horizontalScale = Math.Abs(dx) < 0.001f
            ? float.PositiveInfinity
            : node.Width / 2 / Math.Abs(dx);
        var verticalScale = Math.Abs(dy) < 0.001f
            ? float.PositiveInfinity
            : node.Height / 2 / Math.Abs(dy);
        var scale = Math.Min(horizontalScale, verticalScale);
        return new SKPoint(center.X + dx * scale, center.Y + dy * scale);
    }

    private static IReadOnlyList<NodeLayout> LayoutNodes(
        DiagramCreateRequest request,
        string type,
        int width,
        int height)
    {
        const float nodeWidth = 276;
        const float nodeHeight = 92;
        var top = 116f;
        var availableWidth = width - Margin * 2;
        var availableHeight = height - top - Margin;
        var result = new List<NodeLayout>();

        if (type == "architecture")
        {
            var groups = request.Nodes
                .GroupBy(node => string.IsNullOrWhiteSpace(node.Group) ? "Services" : node.Group!)
                .ToArray();
            var columns = Math.Max(1, groups.Length);
            var columnWidth = availableWidth / columns;
            for (var column = 0; column < groups.Length; column++)
            {
                var nodes = groups[column].ToArray();
                var rowGap = Math.Max(24, (availableHeight - nodes.Length * nodeHeight) / (nodes.Length + 1));
                for (var row = 0; row < nodes.Length; row++)
                {
                    var x = Margin + column * columnWidth + (columnWidth - nodeWidth) / 2;
                    var y = top + rowGap + row * (nodeHeight + rowGap);
                    result.Add(new NodeLayout(nodes[row], x, y, nodeWidth, nodeHeight));
                }
            }
        }
        else
        {
            var columns = request.Nodes.Count > 6 ? 3 : request.Nodes.Count > 2 ? 2 : 1;
            var rows = (int)Math.Ceiling(request.Nodes.Count / (double)columns);
            var columnWidth = availableWidth / columns;
            var rowHeight = availableHeight / Math.Max(rows, 1);
            for (var index = 0; index < request.Nodes.Count; index++)
            {
                var row = index / columns;
                var column = index % columns;
                var x = Margin + column * columnWidth + (columnWidth - nodeWidth) / 2;
                var y = top + row * rowHeight + (rowHeight - nodeHeight) / 2;
                result.Add(new NodeLayout(request.Nodes[index], x, y, nodeWidth, nodeHeight));
            }
        }

        return result;
    }

    private static void AddSvgChart(
        StringBuilder builder,
        DiagramCreateRequest request,
        string type,
        DiagramPalette palette,
        int width,
        int height)
    {
        var chart = ChartGeometry.Create(request, width, height);
        AddSvgAxes(builder, chart, palette);
        for (var seriesIndex = 0; seriesIndex < request.Series.Count; seriesIndex++)
        {
            var series = request.Series[seriesIndex];
            var color = SeriesColors[seriesIndex % SeriesColors.Length];
            if (type == "bar")
            {
                var groupWidth = chart.PlotWidth / request.Labels.Count;
                var barWidth = Math.Min(56, groupWidth * 0.72f / request.Series.Count);
                for (var index = 0; index < series.Values.Count; index++)
                {
                    var value = series.Values[index];
                    var valueY = chart.ValueY(value);
                    var barHeight = Math.Abs(chart.ZeroY - valueY);
                    var x = chart.Left + index * groupWidth +
                            (groupWidth - barWidth * request.Series.Count) / 2 +
                            seriesIndex * barWidth;
                    var y = Math.Min(chart.ZeroY, valueY);
                    builder.AppendLine(
                        $"""<rect x="{F(x)}" y="{F(y)}" width="{F(Math.Max(2, barWidth - 4))}" height="{F(barHeight)}" rx="6" fill="{color}"/>""");
                }
            }
            else
            {
                var points = series.Values
                    .Select((value, index) =>
                        $"{F(chart.LabelX(index))},{F(chart.ValueY(value))}")
                    .ToArray();
                builder.AppendLine(
                    $"""<polyline points="{string.Join(" ", points)}" fill="none" stroke="{color}" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>""");
                foreach (var point in points)
                {
                    var parts = point.Split(',');
                    builder.AppendLine(
                        $"""<circle cx="{parts[0]}" cy="{parts[1]}" r="5" fill="{palette.Surface}" stroke="{color}" stroke-width="3"/>""");
                }
            }

            var legendX = chart.Left + seriesIndex * 160;
            builder.AppendLine($"""<circle cx="{F(legendX)}" cy="{height - 26}" r="6" fill="{color}"/>""");
            builder.AppendLine(
                $"""<text x="{F(legendX + 13)}" y="{height - 21}" fill="{palette.Muted}" font-family="Segoe UI, sans-serif" font-size="13">{Escape(Trim(series.Name, 18))}</text>""");
        }
    }

    private static void AddSvgAxes(StringBuilder builder, ChartGeometry chart, DiagramPalette palette)
    {
        for (var tick = 0; tick <= 5; tick++)
        {
            var y = chart.Top + tick * chart.PlotHeight / 5;
            var value = chart.MaxValue - (chart.MaxValue - chart.MinValue) * tick / 5d;
            builder.AppendLine(
                $"""<line x1="{F(chart.Left)}" y1="{F(y)}" x2="{F(chart.Right)}" y2="{F(y)}" stroke="{palette.Border}" stroke-width="1"/>""");
            builder.AppendLine(
                $"""<text x="{F(chart.Left - 12)}" y="{F(y + 5)}" text-anchor="end" fill="{palette.Muted}" font-family="Segoe UI, sans-serif" font-size="12">{F(value)}</text>""");
        }

        for (var index = 0; index < chart.Request.Labels.Count; index++)
        {
            builder.AppendLine(
                $"""<text x="{F(chart.LabelX(index))}" y="{F(chart.Bottom + 26)}" text-anchor="middle" fill="{palette.Muted}" font-family="Segoe UI, sans-serif" font-size="12">{Escape(Trim(chart.Request.Labels[index], 14))}</text>""");
        }
    }

    private static void DrawChart(
        SKCanvas canvas,
        DiagramCreateRequest request,
        string type,
        DiagramPalette palette,
        int width,
        int height)
    {
        var chart = ChartGeometry.Create(request, width, height);
        using var gridPaint = StrokePaint(ParseColor(palette.Border), 1);
        using var labelStyle = TextStyle.Create(ParseColor(palette.Muted), 12);
        for (var tick = 0; tick <= 5; tick++)
        {
            var y = chart.Top + tick * chart.PlotHeight / 5;
            canvas.DrawLine(chart.Left, y, chart.Right, y, gridPaint);
            var value = chart.MaxValue * (1 - tick / 5d);
            var text = F(value);
            DrawText(
                canvas,
                text,
                chart.Left - 12 - labelStyle.Font.MeasureText(text, labelStyle.Paint),
                y + 5,
                labelStyle);
        }

        for (var index = 0; index < request.Labels.Count; index++)
            DrawCenteredText(canvas, Trim(request.Labels[index], 14), chart.LabelX(index), chart.Bottom + 26, labelStyle);

        for (var seriesIndex = 0; seriesIndex < request.Series.Count; seriesIndex++)
        {
            var series = request.Series[seriesIndex];
            var color = ParseColor(SeriesColors[seriesIndex % SeriesColors.Length]);
            if (type == "bar")
            {
                using var barPaint = FillPaint(color);
                var groupWidth = chart.PlotWidth / request.Labels.Count;
                var barWidth = Math.Min(56, groupWidth * 0.72f / request.Series.Count);
                for (var index = 0; index < series.Values.Count; index++)
                {
                    var valueY = chart.ValueY(series.Values[index]);
                    var barHeight = Math.Abs(chart.ZeroY - valueY);
                    var x = chart.Left + index * groupWidth +
                            (groupWidth - barWidth * request.Series.Count) / 2 +
                            seriesIndex * barWidth;
                    canvas.DrawRoundRect(
                        new SKRoundRect(
                            new SKRect(
                                x,
                                Math.Min(chart.ZeroY, valueY),
                                x + Math.Max(2, barWidth - 4),
                                Math.Max(chart.ZeroY, valueY)),
                            6),
                        barPaint);
                }
            }
            else
            {
                using var linePaint = StrokePaint(color, 4);
                using var pointFill = FillPaint(ParseColor(palette.Surface));
                using var pointStroke = StrokePaint(color, 3);
                using var pathBuilder = new SKPathBuilder();
                for (var index = 0; index < series.Values.Count; index++)
                {
                    var x = chart.LabelX(index);
                    var y = chart.ValueY(series.Values[index]);
                    if (index == 0)
                        pathBuilder.MoveTo(x, y);
                    else
                        pathBuilder.LineTo(x, y);
                }
                using var path = pathBuilder.Detach();
                canvas.DrawPath(path, linePaint);
                for (var index = 0; index < series.Values.Count; index++)
                {
                    var x = chart.LabelX(index);
                    var y = chart.ValueY(series.Values[index]);
                    canvas.DrawCircle(x, y, 5, pointFill);
                    canvas.DrawCircle(x, y, 5, pointStroke);
                }
            }

            var legendX = chart.Left + seriesIndex * 160;
            using var legendPaint = FillPaint(color);
            canvas.DrawCircle(legendX, height - 26, 6, legendPaint);
            DrawText(canvas, Trim(series.Name, 18), legendX + 13, height - 21, labelStyle);
        }
    }

    private static void DrawArrow(SKCanvas canvas, SKPoint start, SKPoint end, SKPaint paint)
    {
        var angle = MathF.Atan2(end.Y - start.Y, end.X - start.X);
        const float size = 10;
        using var pathBuilder = new SKPathBuilder();
        pathBuilder.MoveTo(end);
        pathBuilder.LineTo(
            end.X - size * MathF.Cos(angle - MathF.PI / 6),
            end.Y - size * MathF.Sin(angle - MathF.PI / 6));
        pathBuilder.LineTo(
            end.X - size * MathF.Cos(angle + MathF.PI / 6),
            end.Y - size * MathF.Sin(angle + MathF.PI / 6));
        pathBuilder.Close();
        using var path = pathBuilder.Detach();
        canvas.DrawPath(path, paint);
    }

    private static SKPaint FillPaint(SKColor color) =>
        new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

    private static SKPaint StrokePaint(SKColor color, float width) =>
        new()
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

    private static void DrawText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        TextStyle style) =>
        canvas.DrawText(text, x, y, SKTextAlign.Left, style.Font, style.Paint);

    private static void DrawCenteredText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        TextStyle style) =>
        DrawText(canvas, text, x - style.Font.MeasureText(text, style.Paint) / 2, y, style);

    private static SKColor ParseColor(string value) => SKColor.Parse(value);
    private static string Escape(string value) =>
        System.Security.SecurityElement.Escape(value) ?? string.Empty;
    private static string F(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private sealed record NodeLayout(
        DiagramNodeRequest Node,
        float X,
        float Y,
        float Width,
        float Height);

    private sealed class TextStyle : IDisposable
    {
        private TextStyle(SKTypeface typeface, SKFont font, SKPaint paint)
        {
            Typeface = typeface;
            Font = font;
            Paint = paint;
        }

        public SKTypeface Typeface { get; }
        public SKFont Font { get; }
        public SKPaint Paint { get; }

        public static TextStyle Create(SKColor color, float size, bool bold = false)
        {
            var typeface = SKTypeface.FromFamilyName(
                               "Microsoft YaHei",
                               bold ? SKFontStyle.Bold : SKFontStyle.Normal)
                           ?? SKTypeface.FromFamilyName(
                               "Segoe UI",
                               bold ? SKFontStyle.Bold : SKFontStyle.Normal)
                           ?? SKTypeface.Default;
            return new TextStyle(
                typeface,
                new SKFont(typeface, size),
                new SKPaint
                {
                    Color = color,
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                });
        }

        public void Dispose()
        {
            Font.Dispose();
            Paint.Dispose();
            if (!ReferenceEquals(Typeface, SKTypeface.Default))
                Typeface.Dispose();
        }
    }

    private sealed record ChartGeometry(
        DiagramCreateRequest Request,
        float Left,
        float Top,
        float Right,
        float Bottom,
        double MinValue,
        double MaxValue)
    {
        public float PlotWidth => Right - Left;
        public float PlotHeight => Bottom - Top;

        public float LabelX(int index) =>
            Left + PlotWidth / Request.Labels.Count * (index + 0.5f);

        public float ZeroY => ValueY(0);

        public float ValueY(double value) =>
            Top + (float)((MaxValue - value) / (MaxValue - MinValue) * PlotHeight);

        public static ChartGeometry Create(DiagramCreateRequest request, int width, int height)
        {
            var values = request.Series
                .SelectMany(series => series.Values)
                .Where(double.IsFinite)
                .DefaultIfEmpty(0)
                .ToArray();
            var minimum = Math.Min(0, values.Min());
            var maximum = Math.Max(0, values.Max());
            if (minimum.Equals(maximum))
                maximum = minimum + 1;
            var range = maximum - minimum;
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(range)));
            var step = NiceStep(range / 5, magnitude);
            minimum = Math.Floor(minimum / step) * step;
            maximum = Math.Ceiling(maximum / step) * step;
            if (minimum.Equals(maximum))
                maximum = minimum + step;
            return new ChartGeometry(
                request,
                112,
                120,
                width - 64,
                height - 80,
                minimum,
                maximum);
        }

        private static double NiceStep(double value, double fallbackMagnitude)
        {
            var magnitude = value > 0
                ? Math.Pow(10, Math.Floor(Math.Log10(value)))
                : fallbackMagnitude;
            var normalized = value / magnitude;
            var nice = normalized <= 1 ? 1 :
                normalized <= 2 ? 2 :
                normalized <= 5 ? 5 : 10;
            return nice * magnitude;
        }
    }
}

internal sealed record DiagramPalette(
    string Background,
    string Surface,
    string Foreground,
    string Muted,
    string Border,
    string Accent,
    string Accent2,
    string Shadow)
{
    public static DiagramPalette For(string? theme) =>
        (theme ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "dark" or "nocturne" => new(
                "#11131A", "#1B1F2A", "#F4F4F7", "#A9AFC0",
                "#303747", "#7B6EF6", "#B45AF2", "#000000"),
            _ => new(
                "#F7F8FC", "#FFFFFF", "#232634", "#6B7280",
                "#DFE3EC", "#5657E5", "#A050E8", "#59617A")
        };
}
