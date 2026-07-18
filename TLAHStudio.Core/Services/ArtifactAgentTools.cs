using System.Text.Json;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.Artifacts;

namespace TLAHStudio.Core.Services;

public static class ArtifactAgentToolNames
{
    public const string SpreadsheetCreate = AgentToolNames.SpreadsheetCreate;
    public const string SpreadsheetInspect = AgentToolNames.SpreadsheetInspect;
    public const string SpreadsheetUpdate = AgentToolNames.SpreadsheetUpdate;
    public const string DocumentCreate = AgentToolNames.DocumentCreate;
    public const string DocumentInspect = AgentToolNames.DocumentInspect;
    public const string DiagramCreate = AgentToolNames.DiagramCreate;
}

public abstract class ArtifactAgentToolBase<TRequest> : IAgentTool
{
    protected ArtifactAgentToolBase(IArtifactWorkbenchService workbench)
    {
        Workbench = workbench;
    }

    protected IArtifactWorkbenchService Workbench { get; }
    public abstract LlmToolDefinition Definition { get; }
    public abstract bool RequiresApproval { get; }
    public abstract AgentToolMetadata Metadata { get; }
    public abstract Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default);

    protected async Task<AgentToolResult> ExecuteRequestAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        Func<ArtifactExecutionScope, TRequest, CancellationToken, Task<ArtifactWorkbenchResult>> execute,
        CancellationToken ct)
    {
        try
        {
            var request = JsonSerializer.Deserialize<TRequest>(argumentsJson, ArtifactJson.Options);
            if (request == null)
                return new AgentToolResult(false, string.Empty, "Tool arguments could not be parsed.");
            var scope = new ArtifactExecutionScope(context.ChatId, context.EffectivePermissionMode);
            var result = await execute(scope, request, ct);
            if (!result.Success)
            {
                return new AgentToolResult(
                    false,
                    string.Empty,
                    result.Error ?? "Artifact operation failed.",
                    ErrorCode: "artifact.operation_failed",
                    Retryable: false);
            }
            var output = $"""
                {result.Summary}

                Structured result:
                {JsonSerializer.Serialize(result.StructuredData, ArtifactJson.Options)}
                """;
            return new AgentToolResult(
                true,
                output,
                Artifacts: result.Artifacts,
                StructuredContent: result.StructuredData);
        }
        catch (JsonException ex)
        {
            return new AgentToolResult(
                false,
                string.Empty,
                $"Invalid artifact arguments: {ex.Message}",
                ErrorCode: "artifact.invalid_arguments",
                Retryable: false);
        }
    }

    protected static AgentToolMetadata CreateMetadata(
        string name,
        bool requiresApproval,
        bool isReadOnly,
        string displayName,
        string activityDescription) =>
        new(
            name,
            requiresApproval,
            IsReadOnly: isReadOnly,
            IsConcurrencySafe: isReadOnly,
            IsDestructive: false,
            AgentToolRenderHints.File,
            MaxResultSizeChars: 24_000,
            AgentToolResultPersistenceModes.Artifact,
            IsOpenWorld: false,
            UserFacingName: displayName,
            ActivityDescription: activityDescription,
            InterruptBehavior: isReadOnly
                ? AgentToolInterruptBehaviors.AllowCancel
                : AgentToolInterruptBehaviors.FinishAtomicOperation);
}

public sealed class SpreadsheetCreateAgentTool : ArtifactAgentToolBase<SpreadsheetCreateRequest>
{
    public const string ToolName = ArtifactAgentToolNames.SpreadsheetCreate;

    public SpreadsheetCreateAgentTool(IArtifactWorkbenchService workbench) : base(workbench)
    {
    }

    public override LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        ArtifactAgentToolNames.SpreadsheetCreate,
        "Create a real CSV or XLSX file in the current workspace and return it as an attachment. Use this directly whenever the user asks for a spreadsheet, table export, workbook, or data chart; no terminal command or external app is needed. XLSX supports typed cells, formulas, styles, frozen headers, automatic widths, and an attached chart image.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Workspace-relative output path ending in .csv or .xlsx. Defaults to artifacts/workbook.xlsx."),
            ["sheet_name"] = AgentToolSupport.StringProperty("Worksheet name for XLSX. Defaults to Data."),
            ["title"] = AgentToolSupport.StringProperty("Optional workbook title displayed above the table."),
            ["headers"] = ArrayOf("Column headers.", StringSchema()),
            ["rows"] = ArrayOf("Data rows. Values may be strings, numbers, booleans, dates, or null.", ArrayOfSchema(AnyValueSchema())),
            ["formulas"] = ArrayOf(
                "Optional XLSX formulas.",
                ObjectSchema(
                    new Dictionary<string, object>
                    {
                        ["cell"] = StringSchema("A1 cell address."),
                        ["formula"] = StringSchema("Excel A1 formula, with or without a leading equals sign.")
                    },
                    ["cell", "formula"])),
            ["column_styles"] = ArrayOf(
                "Optional XLSX column formatting.",
                ObjectSchema(
                    new Dictionary<string, object>
                    {
                        ["column"] = StringSchema("Column letter or one-based number."),
                        ["number_format"] = StringSchema("Excel number format, for example #,##0.00 or yyyy-mm-dd."),
                        ["width"] = NumberSchema("Optional width from 3 to 80."),
                        ["horizontal_alignment"] = EnumSchema(["left", "center", "right", "justify"], "Cell alignment.")
                    },
                    ["column"])),
            ["freeze_header"] = BooleanSchema("Freeze the header row in XLSX. Defaults to true."),
            ["auto_fit"] = BooleanSchema("Automatically size XLSX columns. Defaults to true."),
            ["zebra_rows"] = BooleanSchema("Apply alternating row shading in XLSX. Defaults to true."),
            ["chart"] = ChartSchema(),
            ["overwrite"] = BooleanSchema("Replace the requested path. Defaults to false; conflicts otherwise receive a safe numeric suffix.")
        },
        ["headers", "rows"]);

    public override bool RequiresApproval => true;
    public override AgentToolMetadata Metadata { get; } = CreateMetadata(
        ArtifactAgentToolNames.SpreadsheetCreate,
        true,
        false,
        "Create spreadsheet",
        "Creating spreadsheet attachment");

    public override Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default) =>
        ExecuteRequestAsync(context, argumentsJson, Workbench.CreateSpreadsheetAsync, ct);

    internal static Dictionary<string, object> ChartSchema() =>
        ObjectSchema(
            new Dictionary<string, object>
            {
                ["type"] = EnumSchema(["bar", "line"], "Chart type."),
                ["title"] = StringSchema("Chart title."),
                ["category_column"] = StringSchema("Header containing category labels."),
                ["value_columns"] = ArrayOf("Headers containing numeric values.", StringSchema()),
                ["theme"] = EnumSchema(["light", "dark", "nocturne"], "Chart theme.")
            },
            ["type", "category_column"]);

    internal static Dictionary<string, object> StringSchema(string? description = null)
    {
        var schema = new Dictionary<string, object> { ["type"] = "string" };
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return schema;
    }

    internal static Dictionary<string, object> NumberSchema(string? description = null)
    {
        var schema = new Dictionary<string, object> { ["type"] = "number" };
        if (!string.IsNullOrWhiteSpace(description))
            schema["description"] = description;
        return schema;
    }

    internal static Dictionary<string, object> BooleanSchema(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };

    internal static Dictionary<string, object> EnumSchema(string[] values, string description) =>
        new() { ["type"] = "string", ["enum"] = values, ["description"] = description };

    internal static Dictionary<string, object> AnyValueSchema() =>
        new() { ["type"] = new[] { "string", "number", "boolean", "object", "array", "null" } };

    internal static Dictionary<string, object> ArrayOf(string description, Dictionary<string, object> items) =>
        new() { ["type"] = "array", ["description"] = description, ["items"] = items };

    internal static Dictionary<string, object> ArrayOfSchema(Dictionary<string, object> items) =>
        new() { ["type"] = "array", ["items"] = items };

    internal static Dictionary<string, object> ObjectSchema(
        Dictionary<string, object> properties,
        string[]? required = null) =>
        new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required ?? [],
            ["additionalProperties"] = false
        };
}

public sealed class SpreadsheetInspectAgentTool : ArtifactAgentToolBase<SpreadsheetInspectRequest>
{
    public const string ToolName = ArtifactAgentToolNames.SpreadsheetInspect;

    public SpreadsheetInspectAgentTool(IArtifactWorkbenchService workbench) : base(workbench)
    {
    }

    public override LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        ArtifactAgentToolNames.SpreadsheetInspect,
        "Open and inspect a real CSV or XLSX file in the current workspace. Returns sheet names, dimensions, formulas, inferred column types, and a row preview. Use this instead of reading binary workbook bytes or launching an external spreadsheet app.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Workspace-relative .csv or .xlsx path."),
            ["sheet_name"] = AgentToolSupport.StringProperty("Optional XLSX worksheet to preview."),
            ["preview_rows"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Number of rows to preview, from 1 to 100. Defaults to 10."
            }
        },
        ["path"]);

    public override bool RequiresApproval => true;
    public override AgentToolMetadata Metadata { get; } = CreateMetadata(
        ArtifactAgentToolNames.SpreadsheetInspect,
        true,
        true,
        "Inspect spreadsheet",
        "Inspecting spreadsheet structure");

    public override Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default) =>
        ExecuteRequestAsync(context, argumentsJson, Workbench.InspectSpreadsheetAsync, ct);
}

public sealed class SpreadsheetUpdateAgentTool : ArtifactAgentToolBase<SpreadsheetUpdateRequest>
{
    public const string ToolName = ArtifactAgentToolNames.SpreadsheetUpdate;

    public SpreadsheetUpdateAgentTool(IArtifactWorkbenchService workbench) : base(workbench)
    {
    }

    public override LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        ArtifactAgentToolNames.SpreadsheetUpdate,
        "Update an existing CSV or XLSX file in the workspace using typed cell edits and appended rows, then return the validated updated file as an attachment. Use this for workbook edits instead of terminal scripts or asking the user to open Excel.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Workspace-relative source .csv or .xlsx path."),
            ["output_path"] = AgentToolSupport.StringProperty("Optional output path using the same format. Omit to update atomically in place."),
            ["sheet_name"] = AgentToolSupport.StringProperty("Optional XLSX worksheet name."),
            ["set_cells"] = SpreadsheetCreateAgentTool.ArrayOf(
                "Cell updates.",
                SpreadsheetCreateAgentTool.ObjectSchema(
                    new Dictionary<string, object>
                    {
                        ["cell"] = SpreadsheetCreateAgentTool.StringSchema("A1 cell address."),
                        ["value"] = SpreadsheetCreateAgentTool.AnyValueSchema(),
                        ["formula"] = SpreadsheetCreateAgentTool.StringSchema("Optional XLSX formula; takes precedence over value.")
                    },
                    ["cell"])),
            ["append_rows"] = SpreadsheetCreateAgentTool.ArrayOf(
                "Rows appended after the current data.",
                SpreadsheetCreateAgentTool.ArrayOfSchema(SpreadsheetCreateAgentTool.AnyValueSchema())),
            ["freeze_header"] = SpreadsheetCreateAgentTool.BooleanSchema("Optional XLSX freeze-header setting."),
            ["auto_fit"] = SpreadsheetCreateAgentTool.BooleanSchema("Automatically resize XLSX columns. Defaults to true."),
            ["chart"] = SpreadsheetCreateAgentTool.ChartSchema(),
            ["overwrite"] = SpreadsheetCreateAgentTool.BooleanSchema("Allow replacement of the output path. Defaults to true.")
        },
        ["path"]);

    public override bool RequiresApproval => true;
    public override AgentToolMetadata Metadata { get; } = CreateMetadata(
        ArtifactAgentToolNames.SpreadsheetUpdate,
        true,
        false,
        "Update spreadsheet",
        "Updating spreadsheet attachment");

    public override Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default) =>
        ExecuteRequestAsync(context, argumentsJson, Workbench.UpdateSpreadsheetAsync, ct);
}

public sealed class DocumentCreateAgentTool : ArtifactAgentToolBase<DocumentCreateRequest>
{
    public const string ToolName = ArtifactAgentToolNames.DocumentCreate;

    public DocumentCreateAgentTool(IArtifactWorkbenchService workbench) : base(workbench)
    {
    }

    public override LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        ArtifactAgentToolNames.DocumentCreate,
        "Create a polished Markdown, DOCX, or PDF document directly in the current workspace and return it as an attachment. Supports headings, sections, paragraphs, bullet and numbered lists, tables, workspace images, and basic headers/footers. Use this whenever the user asks for a report, specification, proposal, Word document, or PDF; no external conversion step is needed.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Workspace-relative output path ending in .md, .docx, or .pdf."),
            ["title"] = AgentToolSupport.StringProperty("Document title."),
            ["subtitle"] = AgentToolSupport.StringProperty("Optional subtitle."),
            ["author"] = AgentToolSupport.StringProperty("Optional author."),
            ["header"] = AgentToolSupport.StringProperty("Optional repeating DOCX/PDF header."),
            ["footer"] = AgentToolSupport.StringProperty("Optional repeating DOCX/PDF footer."),
            ["sections"] = SpreadsheetCreateAgentTool.ArrayOf(
                "Ordered document sections.",
                SpreadsheetCreateAgentTool.ObjectSchema(
                    new Dictionary<string, object>
                    {
                        ["heading"] = SpreadsheetCreateAgentTool.StringSchema("Optional section heading."),
                        ["level"] = new Dictionary<string, object>
                        {
                            ["type"] = "integer",
                            ["description"] = "Heading level from 1 to 3."
                        },
                        ["page_break_before"] = SpreadsheetCreateAgentTool.BooleanSchema("Start the section on a new page."),
                        ["paragraphs"] = SpreadsheetCreateAgentTool.ArrayOf("Paragraph text.", SpreadsheetCreateAgentTool.StringSchema()),
                        ["bullets"] = SpreadsheetCreateAgentTool.ArrayOf("Bullet list items.", SpreadsheetCreateAgentTool.StringSchema()),
                        ["numbered"] = SpreadsheetCreateAgentTool.ArrayOf("Numbered list items.", SpreadsheetCreateAgentTool.StringSchema()),
                        ["tables"] = SpreadsheetCreateAgentTool.ArrayOf(
                            "Structured tables.",
                            SpreadsheetCreateAgentTool.ObjectSchema(
                                new Dictionary<string, object>
                                {
                                    ["caption"] = SpreadsheetCreateAgentTool.StringSchema("Optional table caption."),
                                    ["headers"] = SpreadsheetCreateAgentTool.ArrayOf("Table headers.", SpreadsheetCreateAgentTool.StringSchema()),
                                    ["rows"] = SpreadsheetCreateAgentTool.ArrayOf(
                                        "Table rows.",
                                        SpreadsheetCreateAgentTool.ArrayOfSchema(SpreadsheetCreateAgentTool.StringSchema()))
                                },
                                ["headers", "rows"])),
                        ["images"] = SpreadsheetCreateAgentTool.ArrayOf(
                            "Workspace images to embed.",
                            SpreadsheetCreateAgentTool.ObjectSchema(
                                new Dictionary<string, object>
                                {
                                    ["path"] = SpreadsheetCreateAgentTool.StringSchema("Workspace-relative PNG, JPEG, GIF, or BMP path."),
                                    ["caption"] = SpreadsheetCreateAgentTool.StringSchema("Optional image caption."),
                                    ["width_pixels"] = new Dictionary<string, object>
                                    {
                                        ["type"] = "integer",
                                        ["description"] = "Rendered image width in pixels."
                                    }
                                },
                                ["path"]))
                    }))
            ,
            ["overwrite"] = SpreadsheetCreateAgentTool.BooleanSchema("Replace the requested path. Defaults to false; conflicts otherwise receive a safe suffix.")
        },
        ["title", "sections"]);

    public override bool RequiresApproval => true;
    public override AgentToolMetadata Metadata { get; } = CreateMetadata(
        ArtifactAgentToolNames.DocumentCreate,
        true,
        false,
        "Create document",
        "Creating document attachment");

    public override Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default) =>
        ExecuteRequestAsync(context, argumentsJson, Workbench.CreateDocumentAsync, ct);
}

public sealed class DocumentInspectAgentTool : ArtifactAgentToolBase<DocumentInspectRequest>
{
    public const string ToolName = ArtifactAgentToolNames.DocumentInspect;

    public DocumentInspectAgentTool(IArtifactWorkbenchService workbench) : base(workbench)
    {
    }

    public override LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        ArtifactAgentToolNames.DocumentInspect,
        "Open and inspect a Markdown, DOCX, or PDF file in the workspace. Returns title/author metadata, page or paragraph counts, tables, images, headings, word count, and a text preview. Use this instead of reading binary bytes or launching an external document application.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Workspace-relative .md, .docx, or .pdf path."),
            ["preview_chars"] = new Dictionary<string, object>
            {
                ["type"] = "integer",
                ["description"] = "Maximum preview length from 200 to 20000 characters."
            }
        },
        ["path"]);

    public override bool RequiresApproval => true;
    public override AgentToolMetadata Metadata { get; } = CreateMetadata(
        ArtifactAgentToolNames.DocumentInspect,
        true,
        true,
        "Inspect document",
        "Inspecting document structure");

    public override Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default) =>
        ExecuteRequestAsync(context, argumentsJson, Workbench.InspectDocumentAsync, ct);
}

public sealed class DiagramCreateAgentTool : ArtifactAgentToolBase<DiagramCreateRequest>
{
    public const string ToolName = ArtifactAgentToolNames.DiagramCreate;

    public DiagramCreateAgentTool(IArtifactWorkbenchService workbench) : base(workbench)
    {
    }

    public override LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        ArtifactAgentToolNames.DiagramCreate,
        "Create a polished flowchart, architecture diagram, bar chart, or line chart directly in the workspace and return real SVG/PNG attachments. Use this whenever the user asks to draw, visualize, diagram, chart, or explain an architecture; do not answer with only Mermaid text unless the user explicitly requests Mermaid source.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Workspace-relative output base path. Extensions are added for requested formats."),
            ["type"] = SpreadsheetCreateAgentTool.EnumSchema(
                ["flowchart", "architecture", "bar", "line"],
                "Diagram type."),
            ["title"] = AgentToolSupport.StringProperty("Visible diagram title."),
            ["nodes"] = SpreadsheetCreateAgentTool.ArrayOf(
                "Flowchart or architecture nodes.",
                SpreadsheetCreateAgentTool.ObjectSchema(
                    new Dictionary<string, object>
                    {
                        ["id"] = SpreadsheetCreateAgentTool.StringSchema("Stable node id."),
                        ["label"] = SpreadsheetCreateAgentTool.StringSchema("Visible node label."),
                        ["group"] = SpreadsheetCreateAgentTool.StringSchema("Optional architecture layer or group."),
                        ["description"] = SpreadsheetCreateAgentTool.StringSchema("Optional concise description.")
                    },
                    ["id", "label"])),
            ["edges"] = SpreadsheetCreateAgentTool.ArrayOf(
                "Connections between nodes.",
                SpreadsheetCreateAgentTool.ObjectSchema(
                    new Dictionary<string, object>
                    {
                        ["from"] = SpreadsheetCreateAgentTool.StringSchema("Source node id."),
                        ["to"] = SpreadsheetCreateAgentTool.StringSchema("Target node id."),
                        ["label"] = SpreadsheetCreateAgentTool.StringSchema("Optional relationship label.")
                    },
                    ["from", "to"])),
            ["labels"] = SpreadsheetCreateAgentTool.ArrayOf("Bar/line chart category labels.", SpreadsheetCreateAgentTool.StringSchema()),
            ["series"] = SpreadsheetCreateAgentTool.ArrayOf(
                "Bar/line chart data series.",
                SpreadsheetCreateAgentTool.ObjectSchema(
                    new Dictionary<string, object>
                    {
                        ["name"] = SpreadsheetCreateAgentTool.StringSchema("Series name."),
                        ["values"] = SpreadsheetCreateAgentTool.ArrayOf(
                            "One numeric value per label.",
                            SpreadsheetCreateAgentTool.NumberSchema())
                    },
                    ["name", "values"])),
            ["formats"] = SpreadsheetCreateAgentTool.ArrayOf(
                "Output formats: svg, png, or both. Defaults to both.",
                SpreadsheetCreateAgentTool.EnumSchema(["svg", "png"], "Output format.")),
            ["theme"] = SpreadsheetCreateAgentTool.EnumSchema(["light", "dark", "nocturne"], "Visual theme."),
            ["width"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Logical width from 640 to 4096." },
            ["height"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Logical height from 420 to 3072." },
            ["scale"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "PNG high-DPI scale from 1 to 4." },
            ["overwrite"] = SpreadsheetCreateAgentTool.BooleanSchema("Replace requested output paths. Defaults to false.")
        },
        ["type", "title"]);

    public override bool RequiresApproval => true;
    public override AgentToolMetadata Metadata { get; } = CreateMetadata(
        ArtifactAgentToolNames.DiagramCreate,
        true,
        false,
        "Create diagram",
        "Rendering diagram attachments");

    public override Task<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        string argumentsJson,
        CancellationToken ct = default) =>
        ExecuteRequestAsync(context, argumentsJson, Workbench.CreateDiagramAsync, ct);
}
