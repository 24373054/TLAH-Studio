using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

namespace TLAHStudio.Core.Services.Artifacts;

public sealed partial class ArtifactWorkbenchService
{
    private static readonly HashSet<string> SpreadsheetFormats = new(
        [".csv", ".xlsx"],
        StringComparer.OrdinalIgnoreCase);

    public async Task<ArtifactWorkbenchResult> CreateSpreadsheetAsync(
        ArtifactExecutionScope scope,
        SpreadsheetCreateRequest request,
        CancellationToken ct = default)
    {
        try
        {
            ValidateTabularInput(request.Headers, request.Rows);
            var finalPath = ResolveOutputPath(
                scope,
                request.Path,
                "artifacts/workbook.xlsx",
                SpreadsheetFormats,
                request.Overwrite);
            var extension = Path.GetExtension(finalPath).ToLowerInvariant();

            if (extension == ".csv")
            {
                await GenerateAtomicAsync(
                    finalPath,
                    path => WriteCsvAsync(path, request.Headers, request.Rows, ct),
                    ValidateCsvFileAsync,
                    ct);
            }
            else
            {
                await GenerateAtomicAsync(
                    finalPath,
                    path =>
                    {
                        CreateXlsx(path, request);
                        return Task.CompletedTask;
                    },
                    ValidateXlsxFileAsync,
                    ct);
            }

            var artifacts = new List<AgentToolArtifact>
            {
                await BuildArtifactAsync(scope, finalPath, CancellationToken.None)
            };
            ArtifactWorkbenchResult? chartResult = null;
            if (request.Chart != null)
            {
                chartResult = await CreateSpreadsheetChartAsync(
                    scope,
                    finalPath,
                    request.Headers,
                    request.Rows,
                    request.Chart,
                    request.Overwrite,
                    CancellationToken.None);
                if (chartResult.Success)
                    artifacts.AddRange(chartResult.Artifacts);
            }

            return Success(
                $"Created {extension.TrimStart('.').ToUpperInvariant()} spreadsheet with {request.Rows.Count} data row(s).",
                new
                {
                    path = artifacts[0].RelativePath,
                    format = extension.TrimStart('.'),
                    sheet_name = extension == ".xlsx" ? NormalizeSheetName(request.SheetName) : null,
                    column_count = request.Headers.Count,
                    row_count = request.Rows.Count,
                    headers = request.Headers,
                    formulas = extension == ".xlsx" ? request.Formulas.Count : 0,
                    freeze_header = extension == ".xlsx" && request.FreezeHeader,
                    chart = chartResult?.Success == true
                        ? chartResult.StructuredData
                        : (JsonElement?)null,
                    chart_error = chartResult?.Success == false ? chartResult.Error : null
                },
                artifacts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or InvalidDataException or
                                   ArgumentException)
        {
            return Failure(ex);
        }
    }

    public async Task<ArtifactWorkbenchResult> InspectSpreadsheetAsync(
        ArtifactExecutionScope scope,
        SpreadsheetInspectRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var path = ResolveExistingPath(scope, request.Path);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (!SpreadsheetFormats.Contains(extension))
                throw new InvalidOperationException("Spreadsheet inspection supports .csv and .xlsx files.");

            var previewRows = Math.Clamp(request.PreviewRows, 1, 100);
            object details = extension == ".csv"
                ? InspectCsv(path, previewRows)
                : InspectXlsx(path, request.SheetName, previewRows);
            var artifact = await BuildArtifactAsync(scope, path, ct);

            return Success(
                $"Inspected {extension.TrimStart('.').ToUpperInvariant()} spreadsheet.",
                details,
                [artifact]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or InvalidDataException or
                                   ArgumentException)
        {
            return Failure(ex);
        }
    }

    public async Task<ArtifactWorkbenchResult> UpdateSpreadsheetAsync(
        ArtifactExecutionScope scope,
        SpreadsheetUpdateRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var sourcePath = ResolveExistingPath(scope, request.Path);
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (!SpreadsheetFormats.Contains(extension))
                throw new InvalidOperationException("Spreadsheet updates support .csv and .xlsx files.");

            var requestedOutput = string.IsNullOrWhiteSpace(request.OutputPath)
                ? request.Path
                : request.OutputPath!;
            if (!Path.GetExtension(requestedOutput).Equals(extension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The updated spreadsheet must keep the original file format.");

            var finalPath = ResolveOutputPath(
                scope,
                requestedOutput,
                requestedOutput,
                SpreadsheetFormats,
                request.Overwrite);
            if (extension == ".csv")
            {
                await GenerateAtomicAsync(
                    finalPath,
                    path => UpdateCsvAsync(sourcePath, path, request, ct),
                    ValidateCsvFileAsync,
                    ct);
            }
            else
            {
                await GenerateAtomicAsync(
                    finalPath,
                    path =>
                    {
                        UpdateXlsx(sourcePath, path, request);
                        return Task.CompletedTask;
                    },
                    ValidateXlsxFileAsync,
                    ct);
            }

            var artifacts = new List<AgentToolArtifact>
            {
                await BuildArtifactAsync(scope, finalPath, CancellationToken.None)
            };
            ArtifactWorkbenchResult? chartResult = null;
            if (request.Chart != null)
            {
                var extracted = extension == ".csv"
                    ? ReadCsvTable(finalPath)
                    : ReadXlsxTable(finalPath, request.SheetName);
                chartResult = await CreateSpreadsheetChartAsync(
                    scope,
                    finalPath,
                    extracted.Headers,
                    extracted.Rows,
                    request.Chart,
                    request.Overwrite,
                    CancellationToken.None);
                if (chartResult.Success)
                    artifacts.AddRange(chartResult.Artifacts);
            }

            return Success(
                $"Updated {extension.TrimStart('.').ToUpperInvariant()} spreadsheet.",
                new
                {
                    path = artifacts[0].RelativePath,
                    format = extension.TrimStart('.'),
                    updated_cells = request.SetCells.Count,
                    appended_rows = request.AppendRows.Count,
                    chart = chartResult?.Success == true
                        ? chartResult.StructuredData
                        : (JsonElement?)null,
                    chart_error = chartResult?.Success == false ? chartResult.Error : null
                },
                artifacts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or InvalidDataException or
                                   ArgumentException)
        {
            return Failure(ex);
        }
    }

    private static void ValidateTabularInput(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<JsonElement>> rows)
    {
        if (headers.Count == 0)
            throw new InvalidOperationException("A spreadsheet requires at least one header.");
        if (headers.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Spreadsheet headers cannot be empty.");
        if (headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count)
            throw new InvalidOperationException("Spreadsheet headers must be unique.");
        if (rows.Any(row => row.Count > headers.Count))
            throw new InvalidOperationException("A row contains more values than the header count.");
    }

    private static async Task WriteCsvAsync(
        string path,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<JsonElement>> rows,
        CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        foreach (var header in headers)
            csv.WriteField(header);
        await csv.NextRecordAsync();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            for (var column = 0; column < headers.Count; column++)
                csv.WriteField(column < row.Count ? JsonValueForCsv(row[column]) : null);
            await csv.NextRecordAsync();
        }
        await writer.FlushAsync(ct);
    }

    private static void CreateXlsx(string path, SpreadsheetCreateRequest request)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(NormalizeSheetName(request.SheetName));
        var headerRow = string.IsNullOrWhiteSpace(request.Title) ? 1 : 3;
        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            worksheet.Cell(1, 1).Value = request.Title;
            worksheet.Range(1, 1, 1, Math.Max(1, request.Headers.Count)).Merge();
            worksheet.Cell(1, 1).Style.Font.Bold = true;
            worksheet.Cell(1, 1).Style.Font.FontSize = 18;
            worksheet.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#232634");
            worksheet.Row(1).Height = 28;
        }

        for (var column = 0; column < request.Headers.Count; column++)
            worksheet.Cell(headerRow, column + 1).Value = request.Headers[column];
        ApplyHeaderStyle(worksheet.Range(headerRow, 1, headerRow, request.Headers.Count));

        for (var row = 0; row < request.Rows.Count; row++)
        {
            for (var column = 0; column < request.Headers.Count; column++)
            {
                if (column < request.Rows[row].Count)
                    SetCellValue(worksheet.Cell(headerRow + row + 1, column + 1), request.Rows[row][column]);
            }
        }

        foreach (var formula in request.Formulas)
        {
            if (!IsA1Address(formula.Cell) || string.IsNullOrWhiteSpace(formula.Formula))
                throw new InvalidOperationException($"Invalid spreadsheet formula target: '{formula.Cell}'.");
            worksheet.Cell(formula.Cell).FormulaA1 = formula.Formula.Trim().TrimStart('=');
        }

        ApplyColumnStyles(worksheet, request.ColumnStyles);
        if (request.ZebraRows && request.Rows.Count > 1)
        {
            for (var row = headerRow + 2; row <= headerRow + request.Rows.Count; row += 2)
                worksheet.Range(row, 1, row, request.Headers.Count).Style.Fill.BackgroundColor =
                    XLColor.FromHtml("#F5F6FB");
        }

        if (request.FreezeHeader)
            worksheet.SheetView.FreezeRows(headerRow);
        if (request.AutoFit)
            AdjustColumns(worksheet);

        var used = worksheet.RangeUsed();
        if (used != null)
        {
            used.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
            used.Style.Border.InsideBorderColor = XLColor.FromHtml("#E2E5ED");
            used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            used.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CDD2DE");
            used.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        workbook.SaveAs(path);
    }

    private static void UpdateXlsx(
        string sourcePath,
        string outputPath,
        SpreadsheetUpdateRequest request)
    {
        using var workbook = new XLWorkbook(sourcePath);
        var worksheet = string.IsNullOrWhiteSpace(request.SheetName)
            ? workbook.Worksheets.First()
            : workbook.Worksheets.FirstOrDefault(
                  sheet => sheet.Name.Equals(request.SheetName, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"Worksheet not found: {request.SheetName}.");

        foreach (var update in request.SetCells)
        {
            if (!IsA1Address(update.Cell))
                throw new InvalidOperationException($"Invalid cell address: '{update.Cell}'.");
            var cell = worksheet.Cell(update.Cell);
            if (!string.IsNullOrWhiteSpace(update.Formula))
                cell.FormulaA1 = update.Formula.Trim().TrimStart('=');
            else if (update.Value is { } value)
                SetCellValue(cell, value);
            else
                cell.Clear(XLClearOptions.Contents);
        }

        if (request.AppendRows.Count > 0)
        {
            var columnCount = Math.Max(
                worksheet.LastColumnUsed()?.ColumnNumber() ?? 0,
                request.AppendRows.Max(row => row.Count));
            var nextRow = (worksheet.LastRowUsed()?.RowNumber() ?? 0) + 1;
            foreach (var row in request.AppendRows)
            {
                for (var column = 0; column < Math.Min(columnCount, row.Count); column++)
                    SetCellValue(worksheet.Cell(nextRow, column + 1), row[column]);
                nextRow++;
            }
        }

        if (request.FreezeHeader.HasValue)
        {
            worksheet.SheetView.Freeze(0, 0);
            if (request.FreezeHeader.Value)
                worksheet.SheetView.FreezeRows(1);
        }
        if (request.AutoFit)
            AdjustColumns(worksheet);
        workbook.SaveAs(outputPath);
    }

    private static async Task UpdateCsvAsync(
        string sourcePath,
        string outputPath,
        SpreadsheetUpdateRequest request,
        CancellationToken ct)
    {
        var table = ReadCsvTable(sourcePath);
        var rows = new List<List<string?>>
        {
            table.Headers.Select(header => (string?)header).ToList()
        };
        rows.AddRange(table.Rows.Select(row =>
            row.Select(JsonValueForCsv).ToList()));

        foreach (var update in request.SetCells)
        {
            var (row, column) = ParseA1Address(update.Cell);
            EnsureCsvSize(rows, row, column);
            rows[row - 1][column - 1] = !string.IsNullOrWhiteSpace(update.Formula)
                ? "=" + update.Formula.Trim().TrimStart('=')
                : update.Value is { } value
                    ? JsonValueForCsv(value)
                    : null;
        }
        foreach (var append in request.AppendRows)
            rows.Add(append.Select(JsonValueForCsv).ToList());

        await using var stream = File.Create(outputPath);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        var width = rows.Max(row => row.Count);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            for (var column = 0; column < width; column++)
                csv.WriteField(column < row.Count ? row[column] : null);
            await csv.NextRecordAsync();
        }
        await writer.FlushAsync(ct);
    }

    private static object InspectCsv(string path, int previewRows)
    {
        var table = ReadCsvTable(path);
        return new
        {
            format = "csv",
            row_count = table.Rows.Count,
            column_count = table.Headers.Count,
            headers = table.Headers,
            inferred_types = InferColumnTypes(table.Headers, table.Rows),
            preview = table.Rows.Take(previewRows).Select(RowToPlainValues).ToArray()
        };
    }

    private static object InspectXlsx(string path, string? selectedSheet, int previewRows)
    {
        using var workbook = new XLWorkbook(path);
        var sheets = workbook.Worksheets.Select(sheet =>
        {
            var used = sheet.RangeUsed();
            return new
            {
                name = sheet.Name,
                rows = used?.RowCount() ?? 0,
                columns = used?.ColumnCount() ?? 0,
                formulas = used?.Cells().Count(cell => !string.IsNullOrWhiteSpace(cell.FormulaA1)) ?? 0
            };
        }).ToArray();
        var worksheet = string.IsNullOrWhiteSpace(selectedSheet)
            ? workbook.Worksheets.First()
            : workbook.Worksheets.FirstOrDefault(
                  sheet => sheet.Name.Equals(selectedSheet, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"Worksheet not found: {selectedSheet}.");
        var range = worksheet.RangeUsed();
        var preview = range == null
            ? Array.Empty<string[]>()
            : range.Rows()
                .Take(previewRows)
                .Select(row => row.Cells().Select(CellDisplayValue).ToArray())
                .ToArray();
        return new
        {
            format = "xlsx",
            sheet_count = workbook.Worksheets.Count,
            sheets,
            selected_sheet = worksheet.Name,
            preview
        };
    }

    private async Task<ArtifactWorkbenchResult> CreateSpreadsheetChartAsync(
        ArtifactExecutionScope scope,
        string spreadsheetPath,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<JsonElement>> rows,
        SpreadsheetChartRequest chart,
        bool overwrite,
        CancellationToken ct)
    {
        var categoryIndex = FindHeaderIndex(headers, chart.CategoryColumn);
        if (categoryIndex < 0)
            return ArtifactWorkbenchResult.Failed(
                $"Chart category column not found: {chart.CategoryColumn}.");
        var valueColumns = chart.ValueColumns.Count == 0
            ? headers.Where((_, index) => index != categoryIndex).Take(3).ToArray()
            : chart.ValueColumns.ToArray();
        var series = new List<DiagramSeriesRequest>();
        foreach (var valueColumn in valueColumns)
        {
            var valueIndex = FindHeaderIndex(headers, valueColumn);
            if (valueIndex < 0)
                return ArtifactWorkbenchResult.Failed($"Chart value column not found: {valueColumn}.");
            series.Add(new DiagramSeriesRequest
            {
                Name = headers[valueIndex],
                Values = rows
                    .Select(row => valueIndex < row.Count ? JsonNumber(row[valueIndex]) : 0)
                    .ToArray()
            });
        }

        var labels = rows
            .Select(row => categoryIndex < row.Count ? JsonValueForCsv(row[categoryIndex]) ?? string.Empty : string.Empty)
            .ToArray();
        var relativeSpreadsheet = Path.GetRelativePath(
            _sandbox.GetSandboxRoot(scope.ChatId),
            spreadsheetPath);
        var chartBase = Path.ChangeExtension(relativeSpreadsheet, null) + "-chart";
        return await CreateDiagramAsync(
            scope,
            new DiagramCreateRequest
            {
                Path = chartBase,
                Type = chart.Type,
                Title = chart.Title,
                Labels = labels,
                Series = series,
                Formats = ["png"],
                Theme = chart.Theme,
                Width = 1200,
                Height = 720,
                Scale = 2,
                Overwrite = overwrite
            },
            ct);
    }

    private static CsvTable ReadCsvTable(string path)
    {
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BadDataFound = null,
            MissingFieldFound = null,
            DetectDelimiter = true,
            PrepareHeaderForMatch = args => args.Header.Trim()
        };
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, configuration);
        if (!csv.Read())
            throw new InvalidDataException("CSV file is empty.");
        csv.ReadHeader();
        var headers = (csv.HeaderRecord ?? [])
            .Select((header, index) =>
                string.IsNullOrWhiteSpace(header) ? $"Column{index + 1}" : header.Trim())
            .ToArray();
        var rows = new List<IReadOnlyList<JsonElement>>();
        while (csv.Read())
        {
            var values = new List<JsonElement>(headers.Length);
            for (var column = 0; column < headers.Length; column++)
                values.Add(ParseCsvValue(csv.GetField(column)));
            rows.Add(values);
        }
        return new CsvTable(headers, rows);
    }

    private static CsvTable ReadXlsxTable(string path, string? selectedSheet)
    {
        using var workbook = new XLWorkbook(path);
        var worksheet = string.IsNullOrWhiteSpace(selectedSheet)
            ? workbook.Worksheets.First()
            : workbook.Worksheets.FirstOrDefault(
                  sheet => sheet.Name.Equals(selectedSheet, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"Worksheet not found: {selectedSheet}.");
        var range = worksheet.RangeUsed();
        if (range == null)
            throw new InvalidDataException("Worksheet is empty.");
        var rows = range.Rows().ToArray();
        var headers = rows[0].Cells().Select(CellDisplayValue).ToArray();
        var values = rows.Skip(1)
            .Select(row => (IReadOnlyList<JsonElement>)row.Cells().Select(CellToJson).ToArray())
            .ToArray();
        return new CsvTable(headers, values);
    }

    private static async Task ValidateCsvFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length == 0)
            throw new InvalidDataException("Generated CSV is empty.");
        _ = ReadCsvTable(path);
    }

    private static Task ValidateXlsxFileAsync(string path)
    {
        using var workbook = new XLWorkbook(path);
        if (workbook.Worksheets.Count == 0)
            throw new InvalidDataException("Generated workbook has no worksheets.");
        return Task.CompletedTask;
    }

    private static void SetCellValue(IXLCell cell, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                if (TryParseIsoDate(text, out var date))
                {
                    cell.Value = date;
                    cell.Style.DateFormat.Format = text.Contains('T') ? "yyyy-mm-dd hh:mm:ss" : "yyyy-mm-dd";
                }
                else
                {
                    cell.Value = text;
                }
                break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer))
                    cell.Value = integer;
                else if (value.TryGetDecimal(out var decimalValue))
                    cell.Value = decimalValue;
                else
                    cell.Value = value.GetDouble();
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                cell.Value = value.GetBoolean();
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                cell.Clear(XLClearOptions.Contents);
                break;
            default:
                cell.Value = value.GetRawText();
                break;
        }
    }

    private static void ApplyHeaderStyle(IXLRange range)
    {
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#5657E5");
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Worksheet.Row(range.FirstRow().RowNumber()).Height = 24;
    }

    private static void ApplyColumnStyles(
        IXLWorksheet worksheet,
        IReadOnlyList<SpreadsheetColumnStyle> styles)
    {
        foreach (var style in styles)
        {
            if (string.IsNullOrWhiteSpace(style.Column))
                continue;
            var column = int.TryParse(style.Column, out var number)
                ? worksheet.Column(number)
                : worksheet.Column(style.Column.Trim().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(style.NumberFormat))
                column.Style.NumberFormat.Format = style.NumberFormat;
            if (style.Width.HasValue)
                column.Width = Math.Clamp(style.Width.Value, 3, 80);
            column.Style.Alignment.Horizontal = style.HorizontalAlignment?.Trim().ToLowerInvariant() switch
            {
                "center" => XLAlignmentHorizontalValues.Center,
                "right" => XLAlignmentHorizontalValues.Right,
                "justify" => XLAlignmentHorizontalValues.Justify,
                _ => XLAlignmentHorizontalValues.Left
            };
        }
    }

    private static void AdjustColumns(IXLWorksheet worksheet)
    {
        worksheet.ColumnsUsed().AdjustToContents();
        foreach (var column in worksheet.ColumnsUsed())
            column.Width = Math.Clamp(column.Width + 1.5, 8, 60);
    }

    private static string NormalizeSheetName(string? name)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? "Data" : name.Trim();
        normalized = Regex.Replace(normalized, @"[\[\]:*?/\\]", "_");
        return normalized.Length <= 31 ? normalized : normalized[..31];
    }

    private static bool IsA1Address(string? address) =>
        !string.IsNullOrWhiteSpace(address) &&
        Regex.IsMatch(address.Trim(), @"^[A-Za-z]{1,3}[1-9]\d{0,6}$");

    private static (int Row, int Column) ParseA1Address(string address)
    {
        if (!IsA1Address(address))
            throw new InvalidOperationException($"Invalid cell address: '{address}'.");
        var match = Regex.Match(address.Trim().ToUpperInvariant(), @"^([A-Z]+)(\d+)$");
        var column = 0;
        foreach (var character in match.Groups[1].Value)
            column = column * 26 + character - 'A' + 1;
        return (int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), column);
    }

    private static void EnsureCsvSize(List<List<string?>> rows, int targetRow, int targetColumn)
    {
        while (rows.Count < targetRow)
            rows.Add([]);
        var width = Math.Max(targetColumn, rows.Max(row => row.Count));
        foreach (var row in rows)
            while (row.Count < width)
                row.Add(null);
    }

    private static string? JsonValueForCsv(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText()
    };

    private static double JsonNumber(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return double.IsFinite(number) ? number : 0;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number) &&
               double.IsFinite(number)
            ? number
            : 0;
    }

    private static JsonElement ParseCsvValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return JsonSerializer.SerializeToElement<string?>(null);
        if (bool.TryParse(value, out var boolean))
            return JsonSerializer.SerializeToElement(boolean);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return JsonSerializer.SerializeToElement(integer);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return JsonSerializer.SerializeToElement(number);
        return JsonSerializer.SerializeToElement(value);
    }

    private static JsonElement CellToJson(IXLCell cell) =>
        cell.DataType switch
        {
            XLDataType.Boolean => JsonSerializer.SerializeToElement(cell.GetBoolean()),
            XLDataType.Number => JsonSerializer.SerializeToElement(cell.GetDouble()),
            XLDataType.DateTime => JsonSerializer.SerializeToElement(cell.GetDateTime()),
            XLDataType.TimeSpan => JsonSerializer.SerializeToElement(cell.GetTimeSpan().ToString()),
            XLDataType.Blank => JsonSerializer.SerializeToElement<string?>(null),
            _ => JsonSerializer.SerializeToElement(CellDisplayValue(cell))
        };

    private static string CellDisplayValue(IXLCell cell) =>
        !string.IsNullOrWhiteSpace(cell.FormulaA1)
            ? "=" + cell.FormulaA1
            : cell.GetFormattedString(CultureInfo.InvariantCulture);

    private static bool TryParseIsoDate(string value, out DateTime date)
    {
        date = default;
        if (value.Length is < 10 or > 35)
            return false;
        return DateTime.TryParseExact(
                   value,
                   ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "O"],
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out date);
    }

    private static int FindHeaderIndex(IReadOnlyList<string> headers, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0;
        for (var index = 0; index < headers.Count; index++)
            if (headers[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return index;
        return -1;
    }

    private static IReadOnlyDictionary<string, string> InferColumnTypes(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<JsonElement>> rows)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var column = 0; column < headers.Count; column++)
        {
            var kinds = rows
                .Where(row => column < row.Count && row[column].ValueKind != JsonValueKind.Null)
                .Select(row => row[column].ValueKind)
                .Distinct()
                .ToArray();
            result[headers[column]] = kinds.Length switch
            {
                0 => "empty",
                1 when kinds[0] == JsonValueKind.Number => "number",
                1 when kinds[0] is JsonValueKind.True or JsonValueKind.False => "boolean",
                1 when kinds[0] == JsonValueKind.String &&
                       rows.Where(row => column < row.Count)
                           .All(row => row[column].ValueKind == JsonValueKind.Null ||
                                       TryParseIsoDate(row[column].GetString() ?? string.Empty, out _)) => "date",
                1 when kinds[0] == JsonValueKind.String => "string",
                _ => "mixed"
            };
        }
        return result;
    }

    private static IReadOnlyList<object?> RowToPlainValues(IReadOnlyList<JsonElement> row) =>
        row.Select(value => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => (object?)value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
            _ => value.GetRawText()
        }).ToArray();

    private sealed record CsvTable(
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyList<JsonElement>> Rows);
}
