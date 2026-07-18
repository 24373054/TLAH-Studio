using System.Text.Json;
using System.Text.Json.Serialization;

namespace TLAHStudio.Core.Services.Artifacts;

public interface IArtifactWorkbenchService
{
    Task<ArtifactWorkbenchResult> CreateSpreadsheetAsync(
        ArtifactExecutionScope scope,
        SpreadsheetCreateRequest request,
        CancellationToken ct = default);

    Task<ArtifactWorkbenchResult> InspectSpreadsheetAsync(
        ArtifactExecutionScope scope,
        SpreadsheetInspectRequest request,
        CancellationToken ct = default);

    Task<ArtifactWorkbenchResult> UpdateSpreadsheetAsync(
        ArtifactExecutionScope scope,
        SpreadsheetUpdateRequest request,
        CancellationToken ct = default);

    Task<ArtifactWorkbenchResult> CreateDocumentAsync(
        ArtifactExecutionScope scope,
        DocumentCreateRequest request,
        CancellationToken ct = default);

    Task<ArtifactWorkbenchResult> InspectDocumentAsync(
        ArtifactExecutionScope scope,
        DocumentInspectRequest request,
        CancellationToken ct = default);

    Task<ArtifactWorkbenchResult> CreateDiagramAsync(
        ArtifactExecutionScope scope,
        DiagramCreateRequest request,
        CancellationToken ct = default);
}

public sealed record ArtifactExecutionScope(
    Guid ChatId,
    string PermissionMode = AgentPermissionModes.RequestApproval);

public sealed record ArtifactWorkbenchResult(
    bool Success,
    string Summary,
    JsonElement StructuredData,
    IReadOnlyList<AgentToolArtifact> Artifacts,
    string? Error = null)
{
    public static ArtifactWorkbenchResult Failed(string error) =>
        new(false, string.Empty, EmptyData(), [], error);

    private static JsonElement EmptyData() =>
        JsonSerializer.SerializeToElement(new { });
}

public sealed record SpreadsheetCreateRequest
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "artifacts/workbook.xlsx";

    [JsonPropertyName("sheet_name")]
    public string SheetName { get; init; } = "Data";

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("headers")]
    public IReadOnlyList<string> Headers { get; init; } = [];

    [JsonPropertyName("rows")]
    public IReadOnlyList<IReadOnlyList<JsonElement>> Rows { get; init; } = [];

    [JsonPropertyName("formulas")]
    public IReadOnlyList<SpreadsheetFormula> Formulas { get; init; } = [];

    [JsonPropertyName("column_styles")]
    public IReadOnlyList<SpreadsheetColumnStyle> ColumnStyles { get; init; } = [];

    [JsonPropertyName("freeze_header")]
    public bool FreezeHeader { get; init; } = true;

    [JsonPropertyName("auto_fit")]
    public bool AutoFit { get; init; } = true;

    [JsonPropertyName("zebra_rows")]
    public bool ZebraRows { get; init; } = true;

    [JsonPropertyName("chart")]
    public SpreadsheetChartRequest? Chart { get; init; }

    [JsonPropertyName("overwrite")]
    public bool Overwrite { get; init; }
}

public sealed record SpreadsheetInspectRequest
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("sheet_name")]
    public string? SheetName { get; init; }

    [JsonPropertyName("preview_rows")]
    public int PreviewRows { get; init; } = 10;
}

public sealed record SpreadsheetUpdateRequest
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("output_path")]
    public string? OutputPath { get; init; }

    [JsonPropertyName("sheet_name")]
    public string? SheetName { get; init; }

    [JsonPropertyName("set_cells")]
    public IReadOnlyList<SpreadsheetCellUpdate> SetCells { get; init; } = [];

    [JsonPropertyName("append_rows")]
    public IReadOnlyList<IReadOnlyList<JsonElement>> AppendRows { get; init; } = [];

    [JsonPropertyName("freeze_header")]
    public bool? FreezeHeader { get; init; }

    [JsonPropertyName("auto_fit")]
    public bool AutoFit { get; init; } = true;

    [JsonPropertyName("chart")]
    public SpreadsheetChartRequest? Chart { get; init; }

    [JsonPropertyName("overwrite")]
    public bool Overwrite { get; init; } = true;
}

public sealed record SpreadsheetFormula
{
    [JsonPropertyName("cell")]
    public string Cell { get; init; } = string.Empty;

    [JsonPropertyName("formula")]
    public string Formula { get; init; } = string.Empty;
}

public sealed record SpreadsheetColumnStyle
{
    [JsonPropertyName("column")]
    public string Column { get; init; } = string.Empty;

    [JsonPropertyName("number_format")]
    public string? NumberFormat { get; init; }

    [JsonPropertyName("width")]
    public double? Width { get; init; }

    [JsonPropertyName("horizontal_alignment")]
    public string? HorizontalAlignment { get; init; }
}

public sealed record SpreadsheetCellUpdate
{
    [JsonPropertyName("cell")]
    public string Cell { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonElement? Value { get; init; }

    [JsonPropertyName("formula")]
    public string? Formula { get; init; }
}

public sealed record SpreadsheetChartRequest
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "bar";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "Chart";

    [JsonPropertyName("category_column")]
    public string CategoryColumn { get; init; } = string.Empty;

    [JsonPropertyName("value_columns")]
    public IReadOnlyList<string> ValueColumns { get; init; } = [];

    [JsonPropertyName("theme")]
    public string Theme { get; init; } = "light";
}

public sealed record DocumentCreateRequest
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "artifacts/document.docx";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "Document";

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("header")]
    public string? Header { get; init; }

    [JsonPropertyName("footer")]
    public string? Footer { get; init; }

    [JsonPropertyName("sections")]
    public IReadOnlyList<DocumentSectionRequest> Sections { get; init; } = [];

    [JsonPropertyName("overwrite")]
    public bool Overwrite { get; init; }
}

public sealed record DocumentInspectRequest
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("preview_chars")]
    public int PreviewChars { get; init; } = 4000;
}

public sealed record DocumentSectionRequest
{
    [JsonPropertyName("heading")]
    public string? Heading { get; init; }

    [JsonPropertyName("level")]
    public int Level { get; init; } = 1;

    [JsonPropertyName("page_break_before")]
    public bool PageBreakBefore { get; init; }

    [JsonPropertyName("paragraphs")]
    public IReadOnlyList<string> Paragraphs { get; init; } = [];

    [JsonPropertyName("bullets")]
    public IReadOnlyList<string> Bullets { get; init; } = [];

    [JsonPropertyName("numbered")]
    public IReadOnlyList<string> Numbered { get; init; } = [];

    [JsonPropertyName("tables")]
    public IReadOnlyList<DocumentTableRequest> Tables { get; init; } = [];

    [JsonPropertyName("images")]
    public IReadOnlyList<DocumentImageRequest> Images { get; init; } = [];
}

public sealed record DocumentTableRequest
{
    [JsonPropertyName("headers")]
    public IReadOnlyList<string> Headers { get; init; } = [];

    [JsonPropertyName("rows")]
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];

    [JsonPropertyName("caption")]
    public string? Caption { get; init; }
}

public sealed record DocumentImageRequest
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("caption")]
    public string? Caption { get; init; }

    [JsonPropertyName("width_pixels")]
    public int WidthPixels { get; init; } = 720;
}

public sealed record DiagramCreateRequest
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "artifacts/diagram";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "flowchart";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "Diagram";

    [JsonPropertyName("nodes")]
    public IReadOnlyList<DiagramNodeRequest> Nodes { get; init; } = [];

    [JsonPropertyName("edges")]
    public IReadOnlyList<DiagramEdgeRequest> Edges { get; init; } = [];

    [JsonPropertyName("labels")]
    public IReadOnlyList<string> Labels { get; init; } = [];

    [JsonPropertyName("series")]
    public IReadOnlyList<DiagramSeriesRequest> Series { get; init; } = [];

    [JsonPropertyName("formats")]
    public IReadOnlyList<string> Formats { get; init; } = ["svg", "png"];

    [JsonPropertyName("theme")]
    public string Theme { get; init; } = "light";

    [JsonPropertyName("width")]
    public int Width { get; init; } = 1200;

    [JsonPropertyName("height")]
    public int Height { get; init; } = 800;

    [JsonPropertyName("scale")]
    public int Scale { get; init; } = 2;

    [JsonPropertyName("overwrite")]
    public bool Overwrite { get; init; }
}

public sealed record DiagramNodeRequest
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("group")]
    public string? Group { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed record DiagramEdgeRequest
{
    [JsonPropertyName("from")]
    public string From { get; init; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; init; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

public sealed record DiagramSeriesRequest
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "Series";

    [JsonPropertyName("values")]
    public IReadOnlyList<double> Values { get; init; } = [];
}
