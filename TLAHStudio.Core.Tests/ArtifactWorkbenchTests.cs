using System.Text.Json;
using System.Xml.Linq;
using System.IO.Compression;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using SkiaSharp;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Artifacts;
using UglyToad.PdfPig;

namespace TLAHStudio.Core.Tests;

public sealed class ArtifactWorkbenchTests
{
    [Fact]
    public async Task SpreadsheetCreateAndUpdate_ProducesReopenableXlsxCsvAndChart()
    {
        var fixture = CreateFixture();
        try
        {
            var create = await fixture.Service.CreateSpreadsheetAsync(
                fixture.Scope,
                new SpreadsheetCreateRequest
                {
                    Path = "artifacts/sales.xlsx",
                    SheetName = "Sales",
                    Title = "Quarterly Sales",
                    Headers = ["Quarter", "Revenue", "Target", "Approved"],
                    Rows =
                    [
                        Row("Q1", 120_500, 110_000, true),
                        Row("Q2", 132_000, 125_000, true),
                        Row("Q3", 118_750, 130_000, false)
                    ],
                    Formulas =
                    [
                        new SpreadsheetFormula { Cell = "B7", Formula = "SUM(B4:B6)" }
                    ],
                    ColumnStyles =
                    [
                        new SpreadsheetColumnStyle
                        {
                            Column = "B",
                            NumberFormat = "$#,##0.00",
                            HorizontalAlignment = "right"
                        }
                    ],
                    Chart = new SpreadsheetChartRequest
                    {
                        Type = "bar",
                        Title = "Revenue vs Target",
                        CategoryColumn = "Quarter",
                        ValueColumns = ["Revenue", "Target"]
                    }
                });

            Assert.True(create.Success, create.Error);
            Assert.Equal(2, create.Artifacts.Count);
            var workbookPath = fixture.Resolve(create.Artifacts[0].RelativePath);
            var chartPath = fixture.Resolve(create.Artifacts[1].RelativePath);
            using (var workbook = new XLWorkbook(workbookPath))
            {
                var sheet = workbook.Worksheet("Sales");
                Assert.Equal("Quarter", sheet.Cell("A3").GetString());
                Assert.Equal(120_500d, sheet.Cell("B4").GetDouble());
                Assert.Equal("SUM(B4:B6)", sheet.Cell("B7").FormulaA1);
            }
            using (var archive = ZipFile.OpenRead(workbookPath))
            using (var reader = new StreamReader(
                       archive.GetEntry("xl/worksheets/sheet1.xml")!.Open()))
                Assert.Contains("pane", await reader.ReadToEndAsync(), StringComparison.OrdinalIgnoreCase);
            using (var chart = SKBitmap.Decode(await File.ReadAllBytesAsync(chartPath)))
            {
                Assert.NotNull(chart);
                Assert.True(chart!.Width >= 2400);
                Assert.True(chart.Height >= 1400);
            }

            var updated = await fixture.Service.UpdateSpreadsheetAsync(
                fixture.Scope,
                new SpreadsheetUpdateRequest
                {
                    Path = create.Artifacts[0].RelativePath,
                    SetCells =
                    [
                        new SpreadsheetCellUpdate
                        {
                            Cell = "D6",
                            Value = JsonSerializer.SerializeToElement(true)
                        }
                    ],
                    AppendRows = [Row("Q4", 150_000, 140_000, true)]
                });
            Assert.True(updated.Success, updated.Error);
            using (var workbook = new XLWorkbook(fixture.Resolve(updated.Artifacts[0].RelativePath)))
            {
                var sheet = workbook.Worksheet("Sales");
                Assert.True(sheet.Cell("D6").GetBoolean());
                Assert.Equal("Q4", sheet.Cell("A8").GetString());
            }

            var csv = await fixture.Service.CreateSpreadsheetAsync(
                fixture.Scope,
                new SpreadsheetCreateRequest
                {
                    Path = "artifacts/data.csv",
                    Headers = ["Name", "Score", "Active"],
                    Rows = [Row("Alpha", 9.5, true), Row("Beta", 8, false)]
                });
            Assert.True(csv.Success, csv.Error);
            var inspected = await fixture.Service.InspectSpreadsheetAsync(
                fixture.Scope,
                new SpreadsheetInspectRequest { Path = csv.Artifacts[0].RelativePath });
            Assert.True(inspected.Success, inspected.Error);
            Assert.Equal(2, inspected.StructuredData.GetProperty("row_count").GetInt32());
            Assert.Equal("number", inspected.StructuredData
                .GetProperty("inferred_types")
                .GetProperty("Score")
                .GetString());

            var updatedCsv = await fixture.Service.UpdateSpreadsheetAsync(
                fixture.Scope,
                new SpreadsheetUpdateRequest
                {
                    Path = csv.Artifacts[0].RelativePath,
                    SetCells =
                    [
                        new SpreadsheetCellUpdate
                        {
                            Cell = "B2",
                            Value = JsonSerializer.SerializeToElement(10)
                        }
                    ],
                    AppendRows = [Row("Gamma", 7.25, true)]
                });
            Assert.True(updatedCsv.Success, updatedCsv.Error);
            var csvText = await File.ReadAllTextAsync(
                fixture.Resolve(updatedCsv.Artifacts[0].RelativePath));
            Assert.Contains("Alpha,10,true", csvText);
            Assert.Contains("Gamma,7.25,true", csvText);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task DocumentCreate_ProducesReopenableMarkdownDocxAndPdfWithStructure()
    {
        var fixture = CreateFixture();
        try
        {
            var image = await fixture.Service.CreateDiagramAsync(
                fixture.Scope,
                new DiagramCreateRequest
                {
                    Path = "artifacts/system-overview",
                    Type = "architecture",
                    Title = "System Overview",
                    Nodes =
                    [
                        new DiagramNodeRequest { Id = "ui", Label = "WinUI", Group = "Client" },
                        new DiagramNodeRequest { Id = "core", Label = "Agent Core", Group = "Runtime" },
                        new DiagramNodeRequest { Id = "data", Label = "SQLite", Group = "Storage" }
                    ],
                    Edges =
                    [
                        new DiagramEdgeRequest { From = "ui", To = "core", Label = "commands" },
                        new DiagramEdgeRequest { From = "core", To = "data", Label = "state" }
                    ],
                    Formats = ["png"]
                });
            Assert.True(image.Success, image.Error);

            var sections = new[]
            {
                new DocumentSectionRequest
                {
                    Heading = "Executive summary",
                    Paragraphs = ["TLAH Studio provides a local-first agent workspace."],
                    Bullets = ["Reliable tools", "Structured artifacts"],
                    Numbered = ["Inspect", "Generate", "Validate"],
                    Tables =
                    [
                        new DocumentTableRequest
                        {
                            Caption = "Release gates",
                            Headers = ["Gate", "Status"],
                            Rows = [["Build", "Pass"], ["Tests", "Pass"]]
                        }
                    ],
                    Images =
                    [
                        new DocumentImageRequest
                        {
                            Path = image.Artifacts[0].RelativePath,
                            Caption = "Runtime architecture",
                            WidthPixels = 640
                        }
                    ]
                },
                new DocumentSectionRequest
                {
                    Heading = "Next steps",
                    Level = 2,
                    PageBreakBefore = true,
                    Paragraphs = ["Ship the validated release."]
                }
            };

            var markdown = await fixture.Service.CreateDocumentAsync(
                fixture.Scope,
                DocumentRequest("artifacts/report.md", sections));
            var docx = await fixture.Service.CreateDocumentAsync(
                fixture.Scope,
                DocumentRequest("artifacts/report.docx", sections));
            var pdf = await fixture.Service.CreateDocumentAsync(
                fixture.Scope,
                DocumentRequest("artifacts/report.pdf", sections));

            Assert.True(markdown.Success, markdown.Error);
            Assert.True(docx.Success, docx.Error);
            Assert.True(pdf.Success, pdf.Error);
            var markdownText = await File.ReadAllTextAsync(fixture.Resolve(markdown.Artifacts[0].RelativePath));
            Assert.Contains("# Release Report", markdownText);
            Assert.Contains("| Gate | Status |", markdownText);
            Assert.Contains("](system-overview.png)", markdownText);

            using (var package = WordprocessingDocument.Open(
                       fixture.Resolve(docx.Artifacts[0].RelativePath),
                       false))
            {
                var mainPart = Assert.IsType<MainDocumentPart>(package.MainDocumentPart);
                var document = Assert.IsType<DocumentFormat.OpenXml.Wordprocessing.Document>(
                    mainPart.Document);
                var body = Assert.IsType<DocumentFormat.OpenXml.Wordprocessing.Body>(
                    document.Body);
                Assert.True(body
                    .Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>()
                    .Any());
                Assert.Single(mainPart.ImageParts);
                Assert.Single(mainPart.HeaderParts);
                Assert.Single(mainPart.FooterParts);
            }

            using (var document = PdfDocument.Open(fixture.Resolve(pdf.Artifacts[0].RelativePath)))
            {
                Assert.True(document.NumberOfPages >= 2);
                Assert.Contains("Release", document.GetPage(1).Text);
                Assert.Contains("Report", document.GetPage(1).Text);
                Assert.False(string.IsNullOrWhiteSpace(document.Information.Title));
            }
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Theory]
    [InlineData("bar")]
    [InlineData("line")]
    public async Task DiagramCreate_ProducesReopenableDataCharts(string type)
    {
        var fixture = CreateFixture();
        try
        {
            var result = await fixture.Service.CreateDiagramAsync(
                fixture.Scope,
                new DiagramCreateRequest
                {
                    Path = $"artifacts/{type}-chart",
                    Type = type,
                    Title = "Usage Trend",
                    Labels = ["Jan", "Feb", "Mar", "Apr"],
                    Series =
                    [
                        new DiagramSeriesRequest
                        {
                            Name = "Calls",
                            Values = [12, 18, 15, 26]
                        },
                        new DiagramSeriesRequest
                        {
                            Name = "Success",
                            Values = [10, 16, 14, 24]
                        }
                    ],
                    Formats = ["svg", "png"],
                    Theme = "dark",
                    Width = 960,
                    Height = 600,
                    Scale = 2
                });

            Assert.True(result.Success, result.Error);
            var svg = result.Artifacts.Single(artifact => artifact.ContentType == "image/svg+xml");
            var png = result.Artifacts.Single(artifact => artifact.ContentType == "image/png");
            var xml = XDocument.Load(fixture.Resolve(svg.RelativePath));
            Assert.Equal("svg", xml.Root?.Name.LocalName);
            Assert.Contains(
                xml.Descendants(),
                element => element.Name.LocalName == (type == "bar" ? "rect" : "polyline"));
            using var bitmap = SKBitmap.Decode(
                await File.ReadAllBytesAsync(fixture.Resolve(png.RelativePath)));
            Assert.NotNull(bitmap);
            Assert.Equal(1920, bitmap!.Width);
            Assert.Equal(1200, bitmap.Height);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Theory]
    [InlineData("flowchart")]
    [InlineData("architecture")]
    public async Task DiagramCreate_ProducesValidSvgAndHighDpiPng(string type)
    {
        var fixture = CreateFixture();
        try
        {
            var result = await fixture.Service.CreateDiagramAsync(
                fixture.Scope,
                new DiagramCreateRequest
                {
                    Path = $"artifacts/{type}",
                    Type = type,
                    Title = "Validated Diagram",
                    Nodes =
                    [
                        new DiagramNodeRequest
                        {
                            Id = "one",
                            Label = "Discover",
                            Group = "Input",
                            Description = "Collect requirements"
                        },
                        new DiagramNodeRequest
                        {
                            Id = "two",
                            Label = "Build",
                            Group = "Runtime",
                            Description = "Generate artifact"
                        },
                        new DiagramNodeRequest
                        {
                            Id = "three",
                            Label = "Verify",
                            Group = "Quality",
                            Description = "Reopen output"
                        }
                    ],
                    Edges =
                    [
                        new DiagramEdgeRequest { From = "one", To = "two" },
                        new DiagramEdgeRequest { From = "two", To = "three" }
                    ],
                    Width = 900,
                    Height = 600,
                    Scale = 2
                });

            Assert.True(result.Success, result.Error);
            Assert.Equal(2, result.Artifacts.Count);
            var svg = result.Artifacts.Single(artifact => artifact.ContentType == "image/svg+xml");
            var png = result.Artifacts.Single(artifact => artifact.ContentType == "image/png");
            var xml = XDocument.Load(fixture.Resolve(svg.RelativePath));
            Assert.Equal("svg", xml.Root?.Name.LocalName);
            Assert.True(xml.Descendants().Count(element => element.Name.LocalName == "rect") >= 4);
            using var bitmap = SKBitmap.Decode(
                await File.ReadAllBytesAsync(fixture.Resolve(png.RelativePath)));
            Assert.NotNull(bitmap);
            Assert.Equal(1800, bitmap!.Width);
            Assert.Equal(1200, bitmap.Height);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void DiagramPngScale_StaysInsideMemoryBudgetAtSchemaMaximums()
    {
        var maximumScale = DiagramRenderer.ClampPngScaleToPixelBudget(
            width: 4096,
            height: 3072,
            requestedScale: 4);
        var normalScale = DiagramRenderer.ClampPngScaleToPixelBudget(
            width: 900,
            height: 600,
            requestedScale: 4);

        Assert.Equal(1, maximumScale);
        Assert.Equal(4, normalScale);
        Assert.True(
            (long)4096 * 3072 * maximumScale * maximumScale <=
            DiagramRenderer.MaxPngPixelCount);
    }

    [Fact]
    public async Task ArtifactAgentTools_ReturnDownloadableAttachmentsAndRejectTraversal()
    {
        var fixture = CreateFixture();
        try
        {
            var context = new AgentToolExecutionContext(
                fixture.Scope.ChatId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                30,
                40_000);
            var tool = new SpreadsheetCreateAgentTool(fixture.Service);
            var result = await tool.ExecuteAsync(
                context,
                """
                {
                  "path":"artifacts/tool-created.xlsx",
                  "headers":["Task","Done"],
                  "rows":[["Build",true]]
                }
                """);

            Assert.True(result.Success, result.Error);
            var artifact = Assert.Single(result.Artifacts!);
            Assert.True(File.Exists(fixture.Resolve(artifact.RelativePath)));
            using var workbook = new XLWorkbook(fixture.Resolve(artifact.RelativePath));
            Assert.Equal("Build", workbook.Worksheet(1).Cell("A2").GetString());

            var inspectTool = new SpreadsheetInspectAgentTool(fixture.Service);
            var inspected = await inspectTool.ExecuteAsync(
                context,
                JsonSerializer.Serialize(new
                {
                    path = artifact.RelativePath,
                    sheet_name = "Data",
                    preview_rows = 5
                }));
            Assert.True(inspected.Success, inspected.Error);
            var inspectionData = Assert.IsType<JsonElement>(inspected.StructuredContent);
            Assert.Equal(1, inspectionData.GetProperty("sheet_count").GetInt32());

            var updateTool = new SpreadsheetUpdateAgentTool(fixture.Service);
            var updated = await updateTool.ExecuteAsync(
                context,
                JsonSerializer.Serialize(new
                {
                    path = artifact.RelativePath,
                    set_cells = new[] { new { cell = "B2", value = false } },
                    append_rows = new object[][] { ["Verify", true] }
                }));
            Assert.True(updated.Success, updated.Error);
            var updatedArtifact = Assert.Single(updated.Artifacts!);
            using (var updatedWorkbook = new XLWorkbook(fixture.Resolve(updatedArtifact.RelativePath)))
            {
                Assert.False(updatedWorkbook.Worksheet(1).Cell("B2").GetBoolean());
                Assert.Equal("Verify", updatedWorkbook.Worksheet(1).Cell("A3").GetString());
            }

            var documentTool = new DocumentCreateAgentTool(fixture.Service);
            var document = await documentTool.ExecuteAsync(
                context,
                """
                {
                  "path":"artifacts/tool-report.docx",
                  "title":"Tool wrapper report",
                  "author":"TLAH Studio",
                  "sections":[{
                    "heading":"Verified output",
                    "paragraphs":["Created directly through the document tool wrapper."],
                    "bullets":["Reopenable","Attachable"]
                  }]
                }
                """);
            Assert.True(document.Success, document.Error);
            var documentArtifact = Assert.Single(document.Artifacts!);
            using (var package = WordprocessingDocument.Open(
                       fixture.Resolve(documentArtifact.RelativePath),
                       false))
                Assert.NotNull(package.MainDocumentPart?.Document?.Body);

            var documentInspectTool = new DocumentInspectAgentTool(fixture.Service);
            var documentInspection = await documentInspectTool.ExecuteAsync(
                context,
                JsonSerializer.Serialize(new
                {
                    path = documentArtifact.RelativePath,
                    preview_chars = 2_000
                }));
            Assert.True(documentInspection.Success, documentInspection.Error);
            Assert.Contains("Tool wrapper report", documentInspection.Output);

            var diagramTool = new DiagramCreateAgentTool(fixture.Service);
            var diagram = await diagramTool.ExecuteAsync(
                context,
                """
                {
                  "path":"artifacts/tool-flow",
                  "type":"flowchart",
                  "title":"Wrapper flow",
                  "nodes":[
                    {"id":"start","label":"Start"},
                    {"id":"finish","label":"Finish"}
                  ],
                  "edges":[{"from":"start","to":"finish","label":"complete"}],
                  "formats":["svg","png"],
                  "width":800,
                  "height":500,
                  "scale":1
                }
                """);
            Assert.True(diagram.Success, diagram.Error);
            Assert.Equal(2, diagram.Artifacts!.Count);
            var diagramSvg = diagram.Artifacts.Single(item => item.ContentType == "image/svg+xml");
            var diagramPng = diagram.Artifacts.Single(item => item.ContentType == "image/png");
            Assert.Equal("svg", XDocument.Load(fixture.Resolve(diagramSvg.RelativePath)).Root?.Name.LocalName);
            using (var bitmap = SKBitmap.Decode(
                       await File.ReadAllBytesAsync(fixture.Resolve(diagramPng.RelativePath))))
                Assert.NotNull(bitmap);

            var traversal = await tool.ExecuteAsync(
                context,
                """
                {
                  "path":"../escape.xlsx",
                  "headers":["Unsafe"],
                  "rows":[["blocked"]]
                }
                """);
            Assert.False(traversal.Success);
            Assert.Contains("escapes", traversal.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task CancelledArtifactCreation_DoesNotPublishPartialFiles()
    {
        var fixture = CreateFixture();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Service.CreateSpreadsheetAsync(
                    fixture.Scope,
                    new SpreadsheetCreateRequest
                    {
                        Path = "artifacts/cancelled.xlsx",
                        Headers = ["Name", "Value"],
                        Rows = [Row("Alpha", 1)]
                    },
                    cancellation.Token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Service.CreateDiagramAsync(
                    fixture.Scope,
                    new DiagramCreateRequest
                    {
                        Path = "artifacts/cancelled-diagram",
                        Type = "flowchart",
                        Nodes =
                        [
                            new DiagramNodeRequest { Id = "a", Label = "A" },
                            new DiagramNodeRequest { Id = "b", Label = "B" }
                        ],
                        Edges = [new DiagramEdgeRequest { From = "a", To = "b" }],
                        Formats = ["svg", "png"]
                    },
                    cancellation.Token));

            Assert.False(File.Exists(fixture.Resolve("artifacts/cancelled.xlsx")));
            Assert.False(File.Exists(fixture.Resolve("artifacts/cancelled-diagram.svg")));
            Assert.False(File.Exists(fixture.Resolve("artifacts/cancelled-diagram.png")));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static DocumentCreateRequest DocumentRequest(
        string path,
        IReadOnlyList<DocumentSectionRequest> sections) =>
        new()
        {
            Path = path,
            Title = "Release Report",
            Subtitle = "Validated structured output",
            Author = "TLAH Studio",
            Header = "TLAH Studio · 4.14.0",
            Footer = "Generated locally",
            Sections = sections
        };

    private static IReadOnlyList<JsonElement> Row(params object?[] values) =>
        values.Select(value => JsonSerializer.SerializeToElement(value)).ToArray();

    private static Fixture CreateFixture()
    {
        var basePath = Path.Combine(
            Path.GetTempPath(),
            "TLAHStudio.Artifact.Tests",
            Guid.NewGuid().ToString("N"));
        var sandbox = new SandboxCommandService(basePath);
        var chatId = Guid.NewGuid();
        return new Fixture(
            basePath,
            sandbox.GetSandboxRoot(chatId),
            new ArtifactExecutionScope(chatId),
            new ArtifactWorkbenchService(sandbox));
    }

    private sealed record Fixture(
        string BasePath,
        string Workspace,
        ArtifactExecutionScope Scope,
        ArtifactWorkbenchService Service) : IDisposable
    {
        public string Resolve(string relativePath) =>
            Path.GetFullPath(Path.Combine(Workspace, relativePath));

        public void Dispose()
        {
            if (Directory.Exists(BasePath))
                Directory.Delete(BasePath, recursive: true);
        }
    }
}
