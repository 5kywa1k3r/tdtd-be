using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class StatRunExportService
{
    private static ExportTable BuildTable(
        string resultKind,
        IReadOnlyList<object> sourceRows)
    {
        var documents = sourceRows
            .Select(row => JsonSerializer.SerializeToElement(row, WebJson))
            .ToList();
        var headers = new List<string> { "ordinal" };
        var seen = new HashSet<string>(headers, StringComparer.Ordinal);
        foreach (var document in documents.Where(item => item.ValueKind == JsonValueKind.Object))
        {
            foreach (var property in document.EnumerateObject())
            {
                if (seen.Add(property.Name))
                    headers.Add(property.Name);
            }
        }

        var rows = new List<IReadOnlyList<ExportCell>>(documents.Count);
        for (var index = 0; index < documents.Count; index++)
        {
            var values = documents[index].ValueKind == JsonValueKind.Object
                ? documents[index].EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => property.Value.Clone(),
                    StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            rows.Add(headers.Select(header =>
            {
                if (header == "ordinal")
                    return ExportCell.NumberValue(index + 1);
                return values.TryGetValue(header, out var value)
                    ? ToExportCell(header, value)
                    : ExportCell.Null;
            }).ToList());
        }
        var sidecar = BuildColumnManifest(resultKind, headers, rows);
        return new ExportTable(
            headers,
            rows,
            sidecar?.CanonicalJson,
            sidecar?.Sha256);
    }

    private static ExportCell ToExportCell(string header, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return ExportCell.Null;
            case JsonValueKind.True:
            case JsonValueKind.False:
                return ExportCell.BooleanValue(value.GetBoolean());
            case JsonValueKind.Number:
                return value.TryGetDecimal(out var number)
                    ? ExportCell.NumberValue(number)
                    : ExportCell.OpaqueTextValue(value.GetRawText());
            case JsonValueKind.String:
            {
                var text = value.GetString() ?? string.Empty;
                if ((header.EndsWith("Utc", StringComparison.Ordinal) ||
                     header.EndsWith("Date", StringComparison.Ordinal)) &&
                    DateTime.TryParse(
                        text,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var date))
                {
                    return ExportCell.DateValue(DateTime.SpecifyKind(date, DateTimeKind.Utc));
                }
                return ExportCell.TextValue(text);
            }
            default:
                return ExportCell.JsonValue(value.GetRawText());
        }
    }

    private static ExportRendering RenderCsv(ExportTable table)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(",", table.Headers.Select(CsvField)));
        builder.Append("\r\n");
        foreach (var row in table.Rows)
        {
            builder.Append(string.Join(",", row.Select(cell => CsvField(CellText(cell)))));
            builder.Append("\r\n");
        }
        var preamble = Encoding.UTF8.GetPreamble();
        var payload = Encoding.UTF8.GetBytes(builder.ToString());
        var content = new byte[preamble.Length + payload.Length];
        Buffer.BlockCopy(preamble, 0, content, 0, preamble.Length);
        Buffer.BlockCopy(payload, 0, content, preamble.Length, payload.Length);
        return new ExportRendering(content, "text/csv; charset=utf-8");
    }

    private static ExportRendering RenderXlsx(
        ExportTable table,
        CanonicalExportResult canonical,
        string semanticHash,
        DateTime completedAtUtc)
    {
        using var workbook = new XLWorkbook();
        var result = workbook.Worksheets.Add("Result");
        for (var column = 0; column < table.Headers.Count; column++)
        {
            result.Cell(1, column + 1).SetValue(SafeText(table.Headers[column]));
            result.Cell(1, column + 1).Style.Font.Bold = true;
        }
        for (var row = 0; row < table.Rows.Count; row++)
        {
            for (var column = 0; column < table.Headers.Count; column++)
                SetTypedCell(result.Cell(row + 2, column + 1), table.Rows[row][column]);
        }
        result.SheetView.FreezeRows(1);
        if (table.Headers.Count > 0)
            result.Range(1, 1, Math.Max(1, table.Rows.Count + 1), table.Headers.Count).CreateTable();
        result.Columns().AdjustToContents(1, Math.Min(table.Rows.Count + 1, 200));

        var metadata = workbook.Worksheets.Add("Metadata");
        var pairs = new (string Key, string Value)[]
        {
            ("schemaVersion", StatRunExportContract.SchemaVersion),
            ("resultId", canonical.ResultId),
            ("resultHash", canonical.ResultHash),
            ("configHash", canonical.ConfigHash),
            ("sourceHash", canonical.SourceHash),
            ("lifecycleRevision", canonical.LifecycleRevision.ToString(CultureInfo.InvariantCulture)),
            ("catalogVersion", canonical.CatalogVersion),
            ("catalogRawSha256", canonical.CatalogRawSha256),
            ("catalogSemanticSha256", canonical.CatalogSemanticSha256),
            ("semanticHash", semanticHash),
            ("completedAtUtc", completedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("rowCount", table.Rows.Count.ToString(CultureInfo.InvariantCulture)),
            ("columnCount", table.Headers.Count.ToString(CultureInfo.InvariantCulture))
        };
        for (var index = 0; index < pairs.Length; index++)
        {
            metadata.Cell(index + 1, 1).SetValue(pairs[index].Key);
            metadata.Cell(index + 1, 2).SetValue(SafeText(pairs[index].Value));
        }
        metadata.Column(1).Style.Font.Bold = true;
        metadata.Columns().AdjustToContents();

        foreach (var cell in workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed()))
        {
            if (!string.IsNullOrEmpty(cell.FormulaA1) || !string.IsNullOrEmpty(cell.FormulaR1C1))
                throw Invalid("EXPORT_XLSX_FORMULA_DETECTED");
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream, validate: true, evaluateFormulae: false);
        return new ExportRendering(
            stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    private static void SetTypedCell(IXLCell cell, ExportCell value)
    {
        switch (value.Kind)
        {
            case ExportCellKinds.Number:
                cell.SetValue(value.Number!.Value);
                cell.Style.NumberFormat.Format = "0.############################";
                break;
            case ExportCellKinds.Boolean:
                cell.SetValue(value.Boolean!.Value);
                break;
            case ExportCellKinds.Date:
                cell.SetValue(value.Date!.Value);
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                break;
            case ExportCellKinds.Text:
            case ExportCellKinds.OpaqueText:
                cell.SetValue(SafeText(value.Text ?? string.Empty));
                break;
            case ExportCellKinds.Json:
                cell.SetValue(value.Text ?? string.Empty);
                break;
            default:
                cell.SetValue(string.Empty);
                break;
        }
    }

    private static string CellText(ExportCell value)
        => value.Kind switch
        {
            ExportCellKinds.Number => value.Number!.Value.ToString(CultureInfo.InvariantCulture),
            ExportCellKinds.Boolean => value.Boolean!.Value ? "true" : "false",
            ExportCellKinds.Date => value.Date!.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ExportCellKinds.Text => SafeText(value.Text ?? string.Empty),
            ExportCellKinds.OpaqueText => SafeText(value.Text ?? string.Empty),
            ExportCellKinds.Json => value.Text ?? string.Empty,
            _ => string.Empty
        };

    private static string SafeText(string value)
        => value.Length > 0 && value[0] is '=' or '+' or '-' or '@'
            ? $"'{value}"
            : value;

    private static string CsvField(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private static class ExportCellKinds
    {
        public const string Null = "NULL";
        public const string Text = "TEXT";
        public const string OpaqueText = "OPAQUE_TEXT";
        public const string Number = "NUMBER";
        public const string Boolean = "BOOLEAN";
        public const string Date = "DATE";
        public const string Json = "JSON";
    }

    private sealed record ExportCell(
        string Kind,
        string? Text = null,
        decimal? Number = null,
        bool? Boolean = null,
        DateTime? Date = null)
    {
        public static readonly ExportCell Null = new(ExportCellKinds.Null);
        public static ExportCell TextValue(string value) => new(ExportCellKinds.Text, Text: value);
        public static ExportCell OpaqueTextValue(string value) => new(ExportCellKinds.OpaqueText, Text: value);
        public static ExportCell NumberValue(decimal value) => new(ExportCellKinds.Number, Number: value);
        public static ExportCell BooleanValue(bool value) => new(ExportCellKinds.Boolean, Boolean: value);
        public static ExportCell DateValue(DateTime value) => new(ExportCellKinds.Date, Date: value);
        public static ExportCell JsonValue(string value) => new(ExportCellKinds.Json, Text: value);
    }

    private sealed record ExportTable(
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyList<ExportCell>> Rows,
        string? ColumnManifestJson,
        string? ColumnManifestSha256);

    private sealed record ExportRendering(byte[] Content, string ContentType);
}
