using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using UglyToad.PdfPig;

namespace TLAHStudio.Core.Services.Artifacts;

public sealed partial class ArtifactWorkbenchService
{
    private static readonly HashSet<string> DocumentFormats = new(
        [".md", ".docx", ".pdf"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> EmbeddableImageFormats = new(
        [".png", ".jpg", ".jpeg", ".gif", ".bmp"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly object PdfFontInitializationLock = new();
    private static bool _pdfFontsInitialized;

    public async Task<ArtifactWorkbenchResult> CreateDocumentAsync(
        ArtifactExecutionScope scope,
        DocumentCreateRequest request,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Title))
                throw new InvalidOperationException("Document title is required.");
            var imagePaths = ResolveDocumentImages(scope, request);
            var finalPath = ResolveOutputPath(
                scope,
                request.Path,
                "artifacts/document.docx",
                DocumentFormats,
                request.Overwrite);
            var extension = Path.GetExtension(finalPath).ToLowerInvariant();

            if (extension == ".md")
            {
                var markdown = BuildMarkdown(request, finalPath, imagePaths);
                await WriteAllTextAtomicAsync(
                    finalPath,
                    markdown,
                    ValidateMarkdownFileAsync,
                    ct);
            }
            else if (extension == ".docx")
            {
                await GenerateAtomicAsync(
                    finalPath,
                    path =>
                    {
                        CreateDocx(path, request, imagePaths);
                        return Task.CompletedTask;
                    },
                    ValidateDocxFileAsync,
                    ct);
            }
            else
            {
                await GenerateAtomicAsync(
                    finalPath,
                    path =>
                    {
                        CreatePdf(path, request, imagePaths);
                        return Task.CompletedTask;
                    },
                    ValidatePdfFileAsync,
                    ct);
            }

            var artifact = await BuildArtifactAsync(scope, finalPath, CancellationToken.None);
            var inspection = await InspectDocumentAsync(
                scope,
                new DocumentInspectRequest
                {
                    Path = artifact.RelativePath,
                    PreviewChars = 1000
                },
                CancellationToken.None);
            if (!inspection.Success)
                throw new InvalidDataException($"Generated document failed structural inspection: {inspection.Error}");

            return Success(
                $"Created {extension.TrimStart('.').ToUpperInvariant()} document with {request.Sections.Count} section(s).",
                new
                {
                    path = artifact.RelativePath,
                    format = extension.TrimStart('.'),
                    request.Title,
                    section_count = request.Sections.Count,
                    paragraph_count = request.Sections.Sum(section => section.Paragraphs.Count),
                    list_item_count = request.Sections.Sum(section =>
                        section.Bullets.Count + section.Numbered.Count),
                    table_count = request.Sections.Sum(section => section.Tables.Count),
                    image_count = imagePaths.Count,
                    structure = inspection.StructuredData
                },
                [artifact]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or InvalidDataException or
                                   ArgumentException or OpenXmlPackageException)
        {
            return Failure(ex);
        }
    }

    public async Task<ArtifactWorkbenchResult> InspectDocumentAsync(
        ArtifactExecutionScope scope,
        DocumentInspectRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var path = ResolveExistingPath(scope, request.Path);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (!DocumentFormats.Contains(extension))
                throw new InvalidOperationException("Document inspection supports .md, .docx, and .pdf files.");
            var previewChars = Math.Clamp(request.PreviewChars, 200, 20_000);
            object details = extension switch
            {
                ".md" => await InspectMarkdownAsync(path, previewChars, ct),
                ".docx" => InspectDocx(path, previewChars),
                ".pdf" => InspectPdf(path, previewChars),
                _ => throw new InvalidOperationException("Unsupported document format.")
            };
            var artifact = await BuildArtifactAsync(scope, path, ct);

            return Success(
                $"Inspected {extension.TrimStart('.').ToUpperInvariant()} document.",
                details,
                [artifact]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or InvalidDataException or
                                   ArgumentException or OpenXmlPackageException)
        {
            return Failure(ex);
        }
    }

    private Dictionary<string, string> ResolveDocumentImages(
        ArtifactExecutionScope scope,
        DocumentCreateRequest request)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in request.Sections.SelectMany(section => section.Images))
        {
            if (string.IsNullOrWhiteSpace(image.Path))
                throw new InvalidOperationException("Document image path cannot be empty.");
            var fullPath = ResolveExistingPath(scope, image.Path);
            if (!EmbeddableImageFormats.Contains(Path.GetExtension(fullPath)))
            {
                throw new InvalidOperationException(
                    $"Document image format is not supported: {Path.GetExtension(fullPath)}.");
            }
            paths[image.Path] = fullPath;
        }
        return paths;
    }

    private static string BuildMarkdown(
        DocumentCreateRequest request,
        string outputPath,
        IReadOnlyDictionary<string, string> imagePaths)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(request.Header))
            builder.AppendLine($"*{request.Header.Trim()}*").AppendLine();
        builder.AppendLine($"# {request.Title.Trim()}").AppendLine();
        if (!string.IsNullOrWhiteSpace(request.Subtitle))
            builder.AppendLine($"> {request.Subtitle.Trim()}").AppendLine();
        if (!string.IsNullOrWhiteSpace(request.Author))
            builder.AppendLine($"**Author:** {request.Author.Trim()}").AppendLine();

        foreach (var section in request.Sections)
        {
            if (section.PageBreakBefore)
                builder.AppendLine("<div style=\"page-break-before: always;\"></div>").AppendLine();
            if (!string.IsNullOrWhiteSpace(section.Heading))
                builder.AppendLine($"{new string('#', Math.Clamp(section.Level + 1, 2, 6))} {section.Heading.Trim()}").AppendLine();
            foreach (var paragraph in section.Paragraphs)
                builder.AppendLine(paragraph.Trim()).AppendLine();
            foreach (var item in section.Bullets)
                builder.AppendLine($"- {item.Trim()}");
            if (section.Bullets.Count > 0)
                builder.AppendLine();
            for (var index = 0; index < section.Numbered.Count; index++)
                builder.AppendLine($"{index + 1}. {section.Numbered[index].Trim()}");
            if (section.Numbered.Count > 0)
                builder.AppendLine();
            foreach (var table in section.Tables)
            {
                if (!string.IsNullOrWhiteSpace(table.Caption))
                    builder.AppendLine($"**{table.Caption.Trim()}**").AppendLine();
                var width = Math.Max(table.Headers.Count, table.Rows.DefaultIfEmpty([]).Max(row => row.Count));
                if (width == 0)
                    continue;
                var headers = Enumerable.Range(0, width)
                    .Select(index => index < table.Headers.Count ? EscapeMarkdownCell(table.Headers[index]) : $"Column {index + 1}")
                    .ToArray();
                builder.AppendLine("| " + string.Join(" | ", headers) + " |");
                builder.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", width)) + " |");
                foreach (var row in table.Rows)
                {
                    builder.AppendLine("| " + string.Join(" | ",
                        Enumerable.Range(0, width)
                            .Select(index => index < row.Count ? EscapeMarkdownCell(row[index]) : string.Empty)) + " |");
                }
                builder.AppendLine();
            }
            foreach (var image in section.Images)
            {
                var relativeImagePath = Path.GetRelativePath(
                        Path.GetDirectoryName(outputPath)!,
                        imagePaths[image.Path])
                    .Replace('\\', '/');
                builder.AppendLine($"![{EscapeMarkdownCell(image.Caption ?? "Image")}]({relativeImagePath})");
                if (!string.IsNullOrWhiteSpace(image.Caption))
                    builder.AppendLine($"*{image.Caption.Trim()}*");
                builder.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Footer))
            builder.AppendLine("---").AppendLine().AppendLine($"*{request.Footer.Trim()}*");
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void CreateDocx(
        string path,
        DocumentCreateRequest request,
        IReadOnlyDictionary<string, string> imagePaths)
    {
        using var package = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        package.PackageProperties.Title = request.Title;
        package.PackageProperties.Creator = request.Author ?? "TLAH Studio";
        var mainPart = package.AddMainDocumentPart();
        mainPart.Document = new W.Document(new W.Body());
        AddDocxStyles(mainPart);
        AddDocxNumbering(mainPart);
        var body = mainPart.Document.Body!;

        body.Append(CreateStyledParagraph(request.Title, "Title"));
        if (!string.IsNullOrWhiteSpace(request.Subtitle))
            body.Append(CreateStyledParagraph(request.Subtitle!, "Subtitle"));
        if (!string.IsNullOrWhiteSpace(request.Author))
            body.Append(CreateStyledParagraph(request.Author!, "Author"));

        uint imageId = 1;
        foreach (var section in request.Sections)
        {
            if (section.PageBreakBefore)
                body.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
            if (!string.IsNullOrWhiteSpace(section.Heading))
            {
                body.Append(CreateStyledParagraph(
                    section.Heading!,
                    $"Heading{Math.Clamp(section.Level, 1, 3)}"));
            }
            foreach (var paragraph in section.Paragraphs)
                body.Append(CreateBodyParagraph(paragraph));
            foreach (var bullet in section.Bullets)
                body.Append(CreateListParagraph(bullet, numberingId: 1));
            foreach (var numbered in section.Numbered)
                body.Append(CreateListParagraph(numbered, numberingId: 2));
            foreach (var table in section.Tables)
                body.Append(CreateDocxTable(table));
            foreach (var image in section.Images)
            {
                body.Append(CreateImageParagraph(
                    mainPart,
                    imagePaths[image.Path],
                    image.WidthPixels,
                    image.Caption ?? "Image",
                    imageId++));
                if (!string.IsNullOrWhiteSpace(image.Caption))
                    body.Append(CreateStyledParagraph(image.Caption!, "Caption"));
            }
        }

        var sectionProperties = new W.SectionProperties();
        if (!string.IsNullOrWhiteSpace(request.Header))
        {
            var headerPart = mainPart.AddNewPart<HeaderPart>();
            headerPart.Header = new W.Header(CreateStyledParagraph(request.Header!, "Header"));
            sectionProperties.Append(new W.HeaderReference
            {
                Type = W.HeaderFooterValues.Default,
                Id = mainPart.GetIdOfPart(headerPart)
            });
        }
        if (!string.IsNullOrWhiteSpace(request.Footer))
        {
            var footerPart = mainPart.AddNewPart<FooterPart>();
            footerPart.Footer = new W.Footer(CreateStyledParagraph(request.Footer!, "Footer"));
            sectionProperties.Append(new W.FooterReference
            {
                Type = W.HeaderFooterValues.Default,
                Id = mainPart.GetIdOfPart(footerPart)
            });
        }
        sectionProperties.Append(new W.PageMargin
        {
            Top = 1080,
            Right = 1080,
            Bottom = 1080,
            Left = 1080,
            Header = 540,
            Footer = 540,
            Gutter = 0
        });
        body.Append(sectionProperties);
        mainPart.Document.Save();
    }

    private static void AddDocxStyles(MainDocumentPart mainPart)
    {
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        var styles = new W.Styles();
        styles.Append(CreateParagraphStyle("Normal", "Normal", 22, "232634"));
        styles.Append(CreateParagraphStyle("Title", "Title", 38, "232634", bold: true, after: 240));
        styles.Append(CreateParagraphStyle("Subtitle", "Subtitle", 24, "6B7280", after: 200));
        styles.Append(CreateParagraphStyle("Author", "Author", 20, "6B7280", after: 280));
        styles.Append(CreateParagraphStyle("Heading1", "Heading 1", 30, "4143B8", bold: true, before: 320, after: 140));
        styles.Append(CreateParagraphStyle("Heading2", "Heading 2", 26, "4A4BA8", bold: true, before: 260, after: 120));
        styles.Append(CreateParagraphStyle("Heading3", "Heading 3", 23, "56577F", bold: true, before: 220, after: 100));
        styles.Append(CreateParagraphStyle("Caption", "Caption", 18, "6B7280", italic: true, after: 160));
        styles.Append(CreateParagraphStyle("Header", "Header", 18, "7A8090"));
        styles.Append(CreateParagraphStyle("Footer", "Footer", 18, "7A8090"));
        stylesPart.Styles = styles;
        stylesPart.Styles.Save();
    }

    private static W.Style CreateParagraphStyle(
        string id,
        string name,
        int halfPoints,
        string color,
        bool bold = false,
        bool italic = false,
        int before = 0,
        int after = 100)
    {
        var runProperties = new W.StyleRunProperties(
            new W.RunFonts { Ascii = "Aptos", HighAnsi = "Aptos", EastAsia = "Microsoft YaHei" },
            new W.Color { Val = color },
            new W.FontSize { Val = halfPoints.ToString() });
        if (bold)
            runProperties.Append(new W.Bold());
        if (italic)
            runProperties.Append(new W.Italic());
        return new W.Style(
            new W.StyleName { Val = name },
            new W.BasedOn { Val = "Normal" },
            new W.NextParagraphStyle { Val = "Normal" },
            new W.StyleParagraphProperties(
                new W.SpacingBetweenLines
                {
                    Before = before.ToString(),
                    After = after.ToString(),
                    Line = "300",
                    LineRule = W.LineSpacingRuleValues.Auto
                }),
            runProperties)
        {
            Type = W.StyleValues.Paragraph,
            StyleId = id,
            CustomStyle = true
        };
    }

    private static void AddDocxNumbering(MainDocumentPart mainPart)
    {
        var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
        numberingPart.Numbering = new W.Numbering(
            CreateAbstractNumbering(1, W.NumberFormatValues.Bullet, "•", "Symbol"),
            CreateAbstractNumbering(2, W.NumberFormatValues.Decimal, "%1.", "Aptos"),
            new W.NumberingInstance(new W.AbstractNumId { Val = 1 }) { NumberID = 1 },
            new W.NumberingInstance(new W.AbstractNumId { Val = 2 }) { NumberID = 2 });
        numberingPart.Numbering.Save();
    }

    private static W.AbstractNum CreateAbstractNumbering(
        int id,
        W.NumberFormatValues format,
        string text,
        string font)
    {
        var level = new W.Level(
            new W.StartNumberingValue { Val = 1 },
            new W.NumberingFormat { Val = format },
            new W.LevelText { Val = text },
            new W.LevelJustification { Val = W.LevelJustificationValues.Left },
            new W.PreviousParagraphProperties(
                new W.Indentation { Left = "720", Hanging = "360" }),
            new W.NumberingSymbolRunProperties(
                new W.RunFonts { Ascii = font, HighAnsi = font }))
        {
            LevelIndex = 0
        };
        return new W.AbstractNum(level) { AbstractNumberId = id };
    }

    private static W.Paragraph CreateStyledParagraph(string text, string styleId) =>
        new(
            new W.ParagraphProperties(new W.ParagraphStyleId { Val = styleId }),
            new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static W.Paragraph CreateBodyParagraph(string text) =>
        new(
            new W.ParagraphProperties(
                new W.ParagraphStyleId { Val = "Normal" },
                new W.Justification { Val = W.JustificationValues.Both }),
            new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static W.Paragraph CreateListParagraph(string text, int numberingId) =>
        new(
            new W.ParagraphProperties(
                new W.ParagraphStyleId { Val = "Normal" },
                new W.NumberingProperties(
                    new W.NumberingLevelReference { Val = 0 },
                    new W.NumberingId { Val = numberingId })),
            new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static W.Table CreateDocxTable(DocumentTableRequest request)
    {
        var width = Math.Max(request.Headers.Count, request.Rows.DefaultIfEmpty([]).Max(row => row.Count));
        if (width == 0)
            throw new InvalidOperationException("Document tables require at least one column.");
        var table = new W.Table(
            new W.TableProperties(
                new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 6, Color = "CDD2DE" },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 6, Color = "CDD2DE" },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 6, Color = "CDD2DE" },
                    new W.RightBorder { Val = W.BorderValues.Single, Size = 6, Color = "CDD2DE" },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4, Color = "E2E5ED" },
                    new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4, Color = "E2E5ED" })));
        var header = new W.TableRow();
        for (var column = 0; column < width; column++)
        {
            var cell = new W.TableCell(
                new W.TableCellProperties(
                    new W.Shading { Fill = "5657E5" }),
                new W.Paragraph(
                    new W.Run(
                        new W.RunProperties(
                            new W.Bold(),
                            new W.Color { Val = "FFFFFF" }),
                        new W.Text(column < request.Headers.Count
                            ? request.Headers[column]
                            : $"Column {column + 1}"))));
            header.Append(cell);
        }
        table.Append(header);
        foreach (var sourceRow in request.Rows)
        {
            var row = new W.TableRow();
            for (var column = 0; column < width; column++)
            {
                row.Append(new W.TableCell(
                    new W.Paragraph(
                        new W.Run(
                            new W.Text(column < sourceRow.Count ? sourceRow[column] : string.Empty)
                            {
                                Space = SpaceProcessingModeValues.Preserve
                            }))));
            }
            table.Append(row);
        }
        return table;
    }

    private static W.Paragraph CreateImageParagraph(
        MainDocumentPart mainPart,
        string path,
        int widthPixels,
        string description,
        uint imageId)
    {
        var imagePart = mainPart.AddImagePart(ImageContentTypeFor(path));
        using (var stream = File.OpenRead(path))
            imagePart.FeedData(stream);
        var relationshipId = mainPart.GetIdOfPart(imagePart);
        var width = Math.Clamp(widthPixels, 120, 1600);
        var dimensions = ReadImageDimensions(path);
        var height = Math.Max(1, (int)Math.Round(width * dimensions.Height / (double)dimensions.Width));
        const long emusPerPixel = 9525;
        var drawing = new W.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = width * emusPerPixel, Cy = height * emusPerPixel },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = imageId, Name = $"Image {imageId}", Description = description },
                new DW.NonVisualGraphicFrameDrawingProperties(
                    new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = imageId, Name = Path.GetFileName(path) },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId, CompressionState = A.BlipCompressionValues.Print },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0, Y = 0 },
                                    new A.Extents { Cx = width * emusPerPixel, Cy = height * emusPerPixel }),
                                new A.PresetGeometry(new A.AdjustValueList())
                                {
                                    Preset = A.ShapeTypeValues.Rectangle
                                })))
                    {
                        Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture"
                    }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U
            });
        return new W.Paragraph(
            new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }),
            new W.Run(drawing));
    }

    private static string ImageContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => throw new InvalidOperationException("Unsupported image type.")
        };

    private static (int Width, int Height) ReadImageDimensions(string path)
    {
        using var codec = SkiaSharp.SKCodec.Create(path)
            ?? throw new InvalidDataException($"Could not decode image: {path}.");
        return (codec.Info.Width, codec.Info.Height);
    }

    private static void CreatePdf(
        string path,
        DocumentCreateRequest request,
        IReadOnlyDictionary<string, string> imagePaths)
    {
        EnsurePdfFonts();
        var document = new MigraDoc.DocumentObjectModel.Document();
        document.Info.Title = request.Title;
        document.Info.Author = request.Author ?? "TLAH Studio";
        DefinePdfStyles(document);
        var section = document.AddSection();
        section.PageSetup.TopMargin = Unit.FromCentimeter(2.1);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2.1);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2.1);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2.1);
        if (!string.IsNullOrWhiteSpace(request.Header))
            section.Headers.Primary.AddParagraph(request.Header);
        if (!string.IsNullOrWhiteSpace(request.Footer))
        {
            var footer = section.Footers.Primary.AddParagraph();
            footer.AddText(request.Footer);
            footer.AddText(" · ");
            footer.AddPageField();
            footer.Format.Alignment = ParagraphAlignment.Center;
        }

        section.AddParagraph(request.Title, "Title");
        if (!string.IsNullOrWhiteSpace(request.Subtitle))
            section.AddParagraph(request.Subtitle, "Subtitle");
        if (!string.IsNullOrWhiteSpace(request.Author))
            section.AddParagraph(request.Author, "Author");

        foreach (var content in request.Sections)
        {
            if (!string.IsNullOrWhiteSpace(content.Heading))
            {
                var heading = section.AddParagraph(
                    content.Heading,
                    $"Heading{Math.Clamp(content.Level, 1, 3)}");
                heading.Format.PageBreakBefore = content.PageBreakBefore;
            }
            else if (content.PageBreakBefore)
            {
                section.AddPageBreak();
            }
            foreach (var paragraph in content.Paragraphs)
            {
                var p = section.AddParagraph(paragraph, "Normal");
                p.Format.Alignment = ParagraphAlignment.Justify;
            }
            foreach (var bullet in content.Bullets)
            {
                var p = section.AddParagraph(bullet, "Normal");
                p.Format.ListInfo = new ListInfo { ListType = ListType.BulletList1 };
            }
            foreach (var numbered in content.Numbered)
            {
                var p = section.AddParagraph(numbered, "Normal");
                p.Format.ListInfo = new ListInfo { ListType = ListType.NumberList1 };
            }
            foreach (var table in content.Tables)
                AddPdfTable(section, table);
            foreach (var image in content.Images)
            {
                var pdfImage = section.AddImage(imagePaths[image.Path]);
                pdfImage.LockAspectRatio = true;
                pdfImage.Width = Unit.FromPoint(Math.Clamp(image.WidthPixels, 120, 1200) * 0.75);
                if (!string.IsNullOrWhiteSpace(image.Caption))
                {
                    var caption = section.AddParagraph(image.Caption, "Caption");
                    caption.Format.Alignment = ParagraphAlignment.Center;
                }
            }
        }

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
    }

    private static void EnsurePdfFonts()
    {
        if (_pdfFontsInitialized)
            return;
        lock (PdfFontInitializationLock)
        {
            if (_pdfFontsInitialized)
                return;
            GlobalFontSettings.FontResolver ??= WindowsArtifactFontResolver.Instance;
            _pdfFontsInitialized = true;
        }
    }

    private static void DefinePdfStyles(MigraDoc.DocumentObjectModel.Document document)
    {
        var normal = document.Styles["Normal"]!;
        normal.Font.Name = "Segoe UI";
        normal.Font.Size = Unit.FromPoint(10.5);
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(7);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.18;

        ConfigurePdfStyle(GetOrAddPdfStyle(document, "Title"), 24, Colors.DarkSlateGray, true, 16);
        ConfigurePdfStyle(GetOrAddPdfStyle(document, "Subtitle"), 13, Colors.DimGray, false, 10);
        ConfigurePdfStyle(GetOrAddPdfStyle(document, "Heading1"), 18, Color.Parse("#4143B8"), true, 9);
        ConfigurePdfStyle(GetOrAddPdfStyle(document, "Heading2"), 15, Color.Parse("#4A4BA8"), true, 7);
        ConfigurePdfStyle(GetOrAddPdfStyle(document, "Heading3"), 12, Color.Parse("#56577F"), true, 6);
        var author = GetOrAddPdfStyle(document, "Author");
        ConfigurePdfStyle(author, 10, Colors.DimGray, false, 12);
        var caption = GetOrAddPdfStyle(document, "Caption");
        ConfigurePdfStyle(caption, 9, Colors.DimGray, false, 8);
        caption.Font.Italic = true;
    }

    private static Style GetOrAddPdfStyle(
        MigraDoc.DocumentObjectModel.Document document,
        string name) =>
        document.Styles[name] ?? document.Styles.AddStyle(name, "Normal");

    private static void ConfigurePdfStyle(
        Style style,
        double size,
        Color color,
        bool bold,
        double after)
    {
        style.Font.Name = "Segoe UI";
        style.Font.Size = Unit.FromPoint(size);
        style.Font.Color = color;
        style.Font.Bold = bold;
        style.ParagraphFormat.SpaceAfter = Unit.FromPoint(after);
    }

    private static void AddPdfTable(Section section, DocumentTableRequest request)
    {
        var width = Math.Max(request.Headers.Count, request.Rows.DefaultIfEmpty([]).Max(row => row.Count));
        if (width == 0)
            throw new InvalidOperationException("Document tables require at least one column.");
        if (!string.IsNullOrWhiteSpace(request.Caption))
            section.AddParagraph(request.Caption, "Heading3");
        var table = section.AddTable();
        table.Format.Font.Size = Unit.FromPoint(9);
        table.Borders.Width = Unit.FromPoint(0.5);
        table.Borders.Color = Color.Parse("#CDD2DE");
        var usableWidth = 16.8;
        for (var column = 0; column < width; column++)
            table.AddColumn(Unit.FromCentimeter(usableWidth / width));
        var header = table.AddRow();
        header.Shading.Color = Color.Parse("#5657E5");
        for (var column = 0; column < width; column++)
        {
            var paragraph = header.Cells[column].AddParagraph(
                column < request.Headers.Count ? request.Headers[column] : $"Column {column + 1}");
            paragraph.Format.Font.Color = Colors.White;
            paragraph.Format.Font.Bold = true;
        }
        foreach (var sourceRow in request.Rows)
        {
            var row = table.AddRow();
            for (var column = 0; column < width; column++)
                row.Cells[column].AddParagraph(column < sourceRow.Count ? sourceRow[column] : string.Empty);
        }
        section.AddParagraph();
    }

    private static async Task<object> InspectMarkdownAsync(
        string path,
        int previewChars,
        CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(path, ct);
        var lines = content.Replace("\r\n", "\n").Split('\n');
        return new
        {
            format = "md",
            line_count = lines.Length,
            word_count = CountWords(content),
            headings = lines.Where(line => Regex.IsMatch(line, @"^#{1,6}\s+"))
                .Select(line => line.TrimStart('#', ' '))
                .ToArray(),
            table_count = lines.Count(line => Regex.IsMatch(line, @"^\s*\|.*\|\s*$")) > 1
                ? lines.Count(line => Regex.IsMatch(line, @"^\s*\|(?:\s*:?-+:?\s*\|)+\s*$"))
                : 0,
            image_count = Regex.Matches(content, @"!\[[^\]]*\]\([^)]+\)").Count,
            preview = LimitText(content, previewChars)
        };
    }

    private static object InspectDocx(string path, int previewChars)
    {
        using var package = WordprocessingDocument.Open(path, false);
        var mainPart = package.MainDocumentPart
            ?? throw new InvalidDataException("DOCX has no main document part.");
        var document = mainPart.Document
            ?? throw new InvalidDataException("DOCX has no main document.");
        var body = document.Body
            ?? throw new InvalidDataException("DOCX has no document body.");
        var paragraphs = body.Descendants<W.Paragraph>().ToArray();
        var text = string.Join(
            Environment.NewLine,
            paragraphs.Select(paragraph => paragraph.InnerText).Where(value => !string.IsNullOrWhiteSpace(value)));
        return new
        {
            format = "docx",
            title = package.PackageProperties.Title,
            creator = package.PackageProperties.Creator,
            paragraph_count = paragraphs.Length,
            heading_count = paragraphs.Count(paragraph =>
                paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value?.StartsWith("Heading", StringComparison.Ordinal) == true),
            table_count = body.Descendants<W.Table>().Count(),
            image_count = mainPart.ImageParts.Count(),
            header_count = mainPart.HeaderParts.Count(),
            footer_count = mainPart.FooterParts.Count(),
            word_count = CountWords(text),
            preview = LimitText(text, previewChars)
        };
    }

    private static object InspectPdf(string path, int previewChars)
    {
        using var document = PdfDocument.Open(path);
        var pages = document.GetPages().ToArray();
        var text = string.Join(Environment.NewLine, pages.Select(page => page.Text));
        return new
        {
            format = "pdf",
            page_count = pages.Length,
            word_count = pages.Sum(page => page.GetWords().Count()),
            title = document.Information.Title,
            author = document.Information.Author,
            preview = LimitText(text, previewChars)
        };
    }

    private static async Task ValidateMarkdownFileAsync(string path)
    {
        var content = await File.ReadAllTextAsync(path);
        if (string.IsNullOrWhiteSpace(content) || !content.Contains("# ", StringComparison.Ordinal))
            throw new InvalidDataException("Generated Markdown is empty or has no title.");
    }

    private static Task ValidateDocxFileAsync(string path)
    {
        using var package = WordprocessingDocument.Open(path, false);
        var document = package.MainDocumentPart?.Document
            ?? throw new InvalidDataException("Generated DOCX has no main document.");
        var body = document.Body
            ?? throw new InvalidDataException("Generated DOCX has no document body.");
        if (!body.Descendants<W.Text>().Any())
            throw new InvalidDataException("Generated DOCX has no readable text.");
        return Task.CompletedTask;
    }

    private static Task ValidatePdfFileAsync(string path)
    {
        using var document = PdfDocument.Open(path);
        if (document.NumberOfPages == 0 || string.IsNullOrWhiteSpace(document.GetPage(1).Text))
            throw new InvalidDataException("Generated PDF has no readable pages.");
        return Task.CompletedTask;
    }

    private static string EscapeMarkdownCell(string value) =>
        value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", "<br>");

    private static string LimitText(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n[preview truncated]";

    private static int CountWords(string value) =>
        Regex.Matches(value, @"[\p{L}\p{N}]+").Count;
}

internal sealed class WindowsArtifactFontResolver : IFontResolver
{
    private const string FaceName = "tlah-artifact-universal";
    private readonly Lazy<byte[]> _fontBytes = new(LoadFontBytes);

    public static WindowsArtifactFontResolver Instance { get; } = new();

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(FaceName, mustSimulateBold: isBold, mustSimulateItalic: isItalic);

    public byte[] GetFont(string faceName) =>
        faceName.Equals(FaceName, StringComparison.Ordinal)
            ? _fontBytes.Value
            : throw new InvalidOperationException($"Unknown artifact font face: {faceName}.");

    private static byte[] LoadFontBytes()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var candidates = new[]
        {
            Path.Combine(fonts, "simhei.ttf"),
            Path.Combine(fonts, "simsunb.ttf"),
            Path.Combine(fonts, "segoeui.ttf"),
            Path.Combine(fonts, "arial.ttf")
        };
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                "No compatible Windows font was found for PDF document generation.");
        return File.ReadAllBytes(path);
    }
}
