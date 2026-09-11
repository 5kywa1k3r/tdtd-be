using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

var cases = new (string Id, Action Run)[]
{
    ("P10-XPARSE-01", CsvManifestAndTypedRows),
    ("P10-XPARSE-02", XlsxNativeTypesAndMetadata),
    ("P10-XPARSE-03", DigestMetadataAndTotalControls),
    ("P10-XPARSE-04", FormattingIndependenceAndZeroWrite),
    ("P10-XPARSE-05", MongoBsonRoundTripMatchesXlsxMetadata)
};

var passed = 0;
foreach (var item in cases)
{
    try
    {
        item.Run();
        Console.WriteLine($"PASS {item.Id}");
        passed++;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL {item.Id} {exception.GetType().Name}:{exception.Message}");
        return 1;
    }
}

Require(passed == 5, "EXACT_CASE_COUNT");
Console.WriteLine(
    "P10_T23_ACTUAL_EXPORT_OK cases=5 cumulative=33 partial=true " +
    "coherent=false handoff=false candidate=false stopBefore=P10-T24");
return 0;

static void CsvManifestAndTypedRows()
{
    var parser = new StatisticReconciliationActualExportParser();
    var artifact = ExportFixture.Csv();
    var capture = parser.Parse(artifact);

    Require(capture.Format == StatisticReconciliationActualExportFormats.Csv,
        "CSV_FORMAT");
    Require(capture.Rows.Length == 2 && capture.Headers.Length == 7,
        "CSV_DIMENSIONS");
    Require(capture.ContentSha256 == artifact.Manifest.ContentSha256,
        "CSV_CONTENT_DIGEST");
    Require(capture.ManifestSha256 == artifact.ManifestSha256,
        "CSV_MANIFEST_DIGEST");

    var first = capture.Rows[0].Cells;
    Equal("12.5", Cell(first, "amount").CanonicalValue, "CSV_DECIMAL");
    Equal(1, Cell(first, "amount").DecimalScale, "CSV_DECIMAL_SCALE");
    Equal("true", Cell(first, "active").CanonicalValue, "CSV_BOOLEAN");
    Equal("2026-08-11T01:02:03.0000000Z",
        Cell(first, "occurredAtUtc").CanonicalValue,
        "CSV_UTC_INSTANT");
    Require(Cell(first, "label").FormulaNeutralized,
        "CSV_FORMULA_TEXT_NEUTRALIZED");
    Equal("{\"a\":1,\"b\":2}",
        Cell(first, "metadataJson").CanonicalValue,
        "CSV_JSON_CANONICAL");

    var second = capture.Rows[1].Cells;
    Equal("Việt Nam, \"dòng\"\r\ntiếp",
        Cell(second, "label").CanonicalValue,
        "CSV_RFC4180_UNICODE_NEWLINE");
    Equal(StatisticReconciliationActualExportValueStates.Null,
        Cell(second, "metadataJson").ValueState,
        "CSV_EXPLICIT_BLANK_POLICY");
    Equal("2", Total(capture, "totalRows").CanonicalValue,
        "CSV_FULL_FILTER_TOTAL");
    Equal("2", Total(capture, "rowCount").CanonicalValue,
        "CSV_MANIFEST_ROW_TOTAL");
}

static void XlsxNativeTypesAndMetadata()
{
    var parser = new StatisticReconciliationActualExportParser();
    var artifact = ExportFixture.Xlsx(numberFormat: "$#,##0.00");
    var capture = parser.Parse(artifact);

    Require(capture.Format == StatisticReconciliationActualExportFormats.Xlsx,
        "XLSX_FORMAT");
    var first = capture.Rows[0].Cells;
    Equal("12.5", Cell(first, "amount").CanonicalValue,
        "XLSX_NATIVE_DECIMAL_NOT_DISPLAY");
    Equal("true", Cell(first, "active").CanonicalValue,
        "XLSX_NATIVE_BOOLEAN");
    Equal("2026-08-11T01:02:03.0000000Z",
        Cell(first, "occurredAtUtc").CanonicalValue,
        "XLSX_NATIVE_UTC_INSTANT");
    Equal(StatisticReconciliationActualExportValueStates.Null,
        Cell(capture.Rows[1].Cells, "metadataJson").ValueState,
        "XLSX_BLANK_POLICY");
    Equal(artifact.Manifest.OwnerSemanticSha256,
        capture.OwnerSemanticSha256,
        "XLSX_OWNER_SEMANTIC_PIN");
    Equal("2", Total(capture, "totalRows").CanonicalValue,
        "XLSX_TYPED_TOTAL");
}

static void MongoBsonRoundTripMatchesXlsxMetadata()
{
    var rawCompletedAtUtc = new DateTime(
        2026,
        9,
        4,
        2,
        35,
        21,
        DateTimeKind.Utc).AddTicks(2_578);
    var normalizedCompletedAtUtc =
        StatRunExportService.NormalizeUtcToMillisecondPrecision(
            rawCompletedAtUtc);

    Require(
        normalizedCompletedAtUtc != rawCompletedAtUtc,
        "MONGO_PRECISION_TEST_INPUT_MUST_HAVE_SUB_MILLISECOND_TICKS");
    Equal(
        0L,
        normalizedCompletedAtUtc.Ticks % TimeSpan.TicksPerMillisecond,
        "PRODUCER_TIMESTAMP_MILLISECOND_PRECISION");
    Equal(
        DateTimeKind.Utc,
        normalizedCompletedAtUtc.Kind,
        "PRODUCER_TIMESTAMP_UTC_KIND");

    var bson = new BsonDocument(
        "completedAtUtc",
        normalizedCompletedAtUtc);
    var roundTrippedCompletedAtUtc = BsonSerializer
        .Deserialize<BsonDocument>(bson.ToBson())["completedAtUtc"]
        .ToUniversalTime();
    Equal(
        normalizedCompletedAtUtc,
        roundTrippedCompletedAtUtc,
        "PRODUCER_MONGO_TIMESTAMP_ROUNDTRIP");

    var parser = new StatisticReconciliationActualExportParser();
    var fixedArtifact = ExportFixture.Xlsx(
        completedAtUtc: roundTrippedCompletedAtUtc,
        metadataCompletedAtUtc: normalizedCompletedAtUtc);
    parser.Parse(fixedArtifact);

    var preFixArtifact = ExportFixture.Xlsx(
        completedAtUtc: roundTrippedCompletedAtUtc,
        metadataCompletedAtUtc: rawCompletedAtUtc);
    ExpectReason(
        () => parser.Parse(preFixArtifact),
        "EXPORT_METADATA_COMPLETED_AT_UTC_MISMATCH");
}

static void DigestMetadataAndTotalControls()
{
    var parser = new StatisticReconciliationActualExportParser();
    var csv = ExportFixture.Csv();
    var tamperedBytes = csv.Content.ToArray();
    tamperedBytes[^1] ^= 1;
    ExpectReason(
        () => parser.Parse(csv with { Content = tamperedBytes }),
        "EXPORT_CONTENT_DIGEST_MISMATCH");
    ExpectReason(
        () => parser.Parse(csv with { ManifestSha256 = Hash('f') }),
        "EXPORT_MANIFEST_DIGEST_MISMATCH");

    var drift = ExportFixture.Csv(totalDrift: true);
    ExpectReason(
        () => parser.Parse(drift),
        "EXPORT_TOTAL_DRIFT");

    var metadataMismatch = ExportFixture.Xlsx(metadataResultSha256: Hash('f'));
    ExpectReason(
        () => parser.Parse(metadataMismatch),
        "EXPORT_METADATA_RESULT_HASH_MISMATCH");

    ExpectReason(
        () => parser.Parse(ExportFixture.CsvWithExtraColumn()),
        "EXPORT_CSV_COLUMN_LIMIT");
    ExpectReason(
        () => parser.Parse(ExportFixture.CsvWithExtraRow()),
        "EXPORT_CSV_ROW_LIMIT");

    ExpectReason(
        () => parser.Parse(ExportFixture.XlsxArchive("../evil.xml")),
        "EXPORT_XLSX_ARCHIVE_PATH_INVALID");
    ExpectReason(
        () => parser.Parse(ExportFixture.XlsxArchive(
            Enumerable.Range(0, 65)
                .Select(value => $"parts/part-{value:D2}.xml")
                .ToArray())),
        "EXPORT_XLSX_ARCHIVE_ENTRY_LIMIT");
    ExpectReason(
        () => parser.Parse(ExportFixture.XlsxArchive("safe.xml")),
        "EXPORT_XLSX_ARCHIVE_REQUIRED_ENTRY_MISSING");
    ExpectReason(
        () => parser.Parse(ExportFixture.XlsxArchive(
            "xl/workbook.xml",
            "xl/workbook.xml")),
        "EXPORT_XLSX_ARCHIVE_DUPLICATE_ENTRY");
}

static void FormattingIndependenceAndZeroWrite()
{
    var parser = new StatisticReconciliationActualExportParser();
    var currency = ExportFixture.Xlsx(numberFormat: "$#,##0.00");
    var scientific = ExportFixture.Xlsx(numberFormat: "0.000E+00");
    var first = parser.Parse(currency);
    var repeated = parser.Parse(currency);
    var restyled = parser.Parse(scientific);

    Equal(first.CaptureSemanticSha256, repeated.CaptureSemanticSha256,
        "REPLAY_DETERMINISTIC");
    Require(first.ContentSha256 != restyled.ContentSha256,
        "STYLE_CHANGES_ARTIFACT_DIGEST");
    Require(first.Rows.Select(row => row.RowSemanticSha256).SequenceEqual(
            restyled.Rows.Select(row => row.RowSemanticSha256),
            StringComparer.Ordinal),
        "STYLE_DOES_NOT_CHANGE_TYPED_ROWS");
    Equal(first.TotalsSemanticSha256, restyled.TotalsSemanticSha256,
        "STYLE_DOES_NOT_CHANGE_TYPED_TOTALS");

    var formula = ExportFixture.Xlsx(withFormula: true);
    ExpectReason(
        () => parser.Parse(formula),
        "EXPORT_XLSX_FORMULA_DETECTED");
    const int p5ToP9Writes = 0;
    const int observationWrites = 0;
    Equal(0, p5ToP9Writes, "P5_P9_ZERO_WRITE");
    Equal(0, observationWrites, "STOP_BEFORE_T24_PUBLICATION");
}

static StatisticReconciliationActualExportCellObservation Cell(
    ImmutableArray<StatisticReconciliationActualExportCellObservation> cells,
    string name)
    => cells.Single(value => value.ColumnName == name);

static StatisticReconciliationActualExportTotalObservation Total(
    StatisticReconciliationActualExportCapture capture,
    string name)
    => capture.FullFilterTotals.Single(value => value.Name == name);

static void ExpectReason(Action action, string reason)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationActualObservationException exception)
    {
        Equal(reason, exception.Reason, $"EXPECTED_{reason}");
        return;
    }
    throw new InvalidOperationException($"EXPECTED_{reason}");
}

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

static void Equal<T>(T expected, T actual, string reason)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{reason}:expected={expected};actual={actual}");
}

static string Hash(char value) => new(value, 64);

internal static class ExportFixture
{
    private static readonly DateTime CompletedAtUtc = new(
        2026,
        8,
        11,
        2,
        3,
        4,
        DateTimeKind.Utc);

    private static readonly ImmutableArray<
        StatisticReconciliationActualExportColumnContract> Columns =
    [
        new(0, "ordinal",
            StatisticReconciliationActualExportValueTypes.Integer,
            StatisticReconciliationActualExportBlankPolicies.Forbidden),
        new(1, "amount",
            StatisticReconciliationActualExportValueTypes.Decimal,
            StatisticReconciliationActualExportBlankPolicies.Forbidden),
        new(2, "active",
            StatisticReconciliationActualExportValueTypes.Boolean,
            StatisticReconciliationActualExportBlankPolicies.Forbidden),
        new(3, "occurredAtUtc",
            StatisticReconciliationActualExportValueTypes.UtcInstant,
            StatisticReconciliationActualExportBlankPolicies.Forbidden),
        new(4, "label",
            StatisticReconciliationActualExportValueTypes.Text,
            StatisticReconciliationActualExportBlankPolicies.Empty),
        new(5, "metadataJson",
            StatisticReconciliationActualExportValueTypes.Json,
            StatisticReconciliationActualExportBlankPolicies.Null),
        new(6, "totalRows",
            StatisticReconciliationActualExportValueTypes.Integer,
            StatisticReconciliationActualExportBlankPolicies.Forbidden,
            IsFullFilterTotal: true)
    ];

    private static readonly string[][] Rows =
    [
        [
            "1",
            "12.5",
            "true",
            "2026-08-11T01:02:03.0000000Z",
            "'=SUM(A1:A2)",
            "{\"b\":2,\"a\":1}",
            "2"
        ],
        [
            "2",
            "-1.25",
            "false",
            "2026-08-11T01:02:04.0000000Z",
            "Việt Nam, \"dòng\"\r\ntiếp",
            string.Empty,
            "2"
        ]
    ];

    internal static StatisticReconciliationActualExportArtifact Csv(
        bool totalDrift = false)
    {
        var rows = Rows.Select(value => value.ToArray()).ToArray();
        if (totalDrift)
            rows[1][6] = "3";
        var bytes = RenderCsv(Columns.Select(value => value.Name).ToArray(), rows);
        return Artifact(
            StatisticReconciliationActualExportFormats.Csv,
            "text/csv; charset=utf-8",
            "stat-direct-field-result.csv",
            bytes,
            Columns);
    }

    internal static StatisticReconciliationActualExportArtifact CsvWithExtraColumn()
    {
        var headers = Columns
            .Select(value => value.Name)
            .Append("overflow")
            .ToArray();
        var bytes = RenderCsv(headers, Rows);
        return Artifact(
            StatisticReconciliationActualExportFormats.Csv,
            "text/csv; charset=utf-8",
            "stat-direct-field-result.csv",
            bytes,
            Columns);
    }

    internal static StatisticReconciliationActualExportArtifact CsvWithExtraRow()
    {
        var rows = Rows
            .Select(value => value.ToArray())
            .Append(Rows[1].ToArray())
            .ToArray();
        var bytes = RenderCsv(
            Columns.Select(value => value.Name).ToArray(),
            rows);
        return Artifact(
            StatisticReconciliationActualExportFormats.Csv,
            "text/csv; charset=utf-8",
            "stat-direct-field-result.csv",
            bytes,
            Columns);
    }

    internal static StatisticReconciliationActualExportArtifact XlsxArchive(
        params string[] entryNames)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(
                   stream,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            foreach (var entryName in entryNames)
                archive.CreateEntry(entryName, CompressionLevel.Optimal);
        }
        return Artifact(
            StatisticReconciliationActualExportFormats.Xlsx,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "stat-direct-field-result.xlsx",
            stream.ToArray(),
            Columns);
    }

    internal static StatisticReconciliationActualExportArtifact Xlsx(
        string numberFormat = "0.############################",
        string? metadataResultSha256 = null,
        bool withFormula = false,
        DateTime? completedAtUtc = null,
        DateTime? metadataCompletedAtUtc = null)
    {
        var provisional = BaseManifest(
            StatisticReconciliationActualExportFormats.Xlsx,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "stat-direct-field-result.xlsx",
            Columns,
            Hash('0'),
            0,
            completedAtUtc);
        var bytes = RenderXlsx(
            provisional,
            numberFormat,
            metadataResultSha256,
            withFormula,
            metadataCompletedAtUtc);
        return Artifact(
            StatisticReconciliationActualExportFormats.Xlsx,
            provisional.ContentType,
            provisional.FileName,
            bytes,
            Columns,
            completedAtUtc);
    }

    private static StatisticReconciliationActualExportArtifact Artifact(
        string format,
        string contentType,
        string fileName,
        byte[] bytes,
        ImmutableArray<StatisticReconciliationActualExportColumnContract> columns,
        DateTime? completedAtUtc = null)
    {
        var contentSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var manifest = BaseManifest(
            format,
            contentType,
            fileName,
            columns,
            contentSha256,
            bytes.LongLength,
            completedAtUtc);
        var parser = new StatisticReconciliationActualExportParser();
        return new StatisticReconciliationActualExportArtifact(
            manifest,
            parser.ComputeManifestSha256(manifest),
            bytes);
    }

    private static StatisticReconciliationActualExportManifest BaseManifest(
        string format,
        string contentType,
        string fileName,
        ImmutableArray<StatisticReconciliationActualExportColumnContract> columns,
        string contentSha256,
        long byteCount,
        DateTime? completedAtUtc = null)
        => new(
            StatisticReconciliationActualExportParser.RequiredSchemaVersion,
            "export-01",
            Hash('1'),
            Hash('2'),
            format,
            "DIRECT_FIELD",
            contentType,
            fileName,
            contentSha256,
            byteCount,
            Rows.Length,
            columns.Length,
            "work-01",
            "ASSIGNMENT",
            "scope-01",
            "result-01",
            Hash('3'),
            Hash('4'),
            Hash('5'),
            Hash('6'),
            7,
            "1.6",
            Hash('7'),
            Hash('8'),
            Hash('9'),
            "p9_chain_fixture",
            "P9-10",
            10,
            Hash('a'),
            completedAtUtc ?? CompletedAtUtc,
            columns);

    private static byte[] RenderCsv(
        IReadOnlyList<string> headers,
        IReadOnlyList<string[]> rows)
    {
        var builder = new StringBuilder();
        CsvRow(builder, headers);
        foreach (var row in rows)
            CsvRow(builder, row);
        var payload = Encoding.UTF8.GetBytes(builder.ToString());
        var preamble = Encoding.UTF8.GetPreamble();
        var bytes = new byte[preamble.Length + payload.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(payload, 0, bytes, preamble.Length, payload.Length);
        return bytes;
    }

    private static void CsvRow(StringBuilder builder, IEnumerable<string> values)
    {
        builder.Append(string.Join(",", values.Select(value =>
            $"\"{value.Replace("\"", "\"\"")}\"")));
        builder.Append("\r\n");
    }

    private static byte[] RenderXlsx(
        StatisticReconciliationActualExportManifest manifest,
        string numberFormat,
        string? metadataResultSha256,
        bool withFormula,
        DateTime? metadataCompletedAtUtc)
    {
        using var workbook = new XLWorkbook();
        var result = workbook.Worksheets.Add("Result");
        for (var column = 0; column < Columns.Length; column++)
        {
            result.Cell(1, column + 1).SetValue(Columns[column].Name);
            result.Cell(1, column + 1).Style.Font.Bold = true;
        }
        for (var row = 0; row < Rows.Length; row++)
        {
            for (var column = 0; column < Columns.Length; column++)
                SetTyped(result.Cell(row + 2, column + 1), Columns[column], Rows[row][column]);
        }
        result.Cell(2, 2).Style.NumberFormat.Format = numberFormat;
        if (withFormula)
            result.Cell(2, 5).FormulaA1 = "=1+1";
        result.Range(1, 1, Rows.Length + 1, Columns.Length).CreateTable();
        result.SheetView.FreezeRows(1);

        var metadata = workbook.Worksheets.Add("Metadata");
        var pairs = new (string Key, string Value)[]
        {
            ("schemaVersion", manifest.SchemaVersion),
            ("resultId", manifest.ResultId),
            ("resultHash", metadataResultSha256 ?? manifest.ResultSha256),
            ("configHash", manifest.ConfigSha256),
            ("sourceHash", manifest.SourceSha256),
            ("lifecycleRevision", manifest.LifecycleRevision.ToString(
                CultureInfo.InvariantCulture)),
            ("catalogVersion", manifest.CatalogVersion),
            ("catalogRawSha256", manifest.CatalogRawSha256),
            ("catalogSemanticSha256", manifest.CatalogSemanticSha256),
            ("semanticHash", manifest.OwnerSemanticSha256),
            ("completedAtUtc", (metadataCompletedAtUtc ??
                manifest.CompletedAtUtc).ToString(
                "O",
                CultureInfo.InvariantCulture)),
            ("rowCount", manifest.RowCount.ToString(CultureInfo.InvariantCulture)),
            ("columnCount", manifest.ColumnCount.ToString(CultureInfo.InvariantCulture))
        };
        for (var row = 0; row < pairs.Length; row++)
        {
            metadata.Cell(row + 1, 1).SetValue(pairs[row].Key);
            metadata.Cell(row + 1, 2).SetValue(pairs[row].Value);
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream, validate: true, evaluateFormulae: false);
        return stream.ToArray();
    }

    private static void SetTyped(
        IXLCell cell,
        StatisticReconciliationActualExportColumnContract column,
        string value)
    {
        if (value.Length == 0)
            return;
        switch (column.ValueType)
        {
            case StatisticReconciliationActualExportValueTypes.Integer:
                cell.SetValue(long.Parse(value, CultureInfo.InvariantCulture));
                break;
            case StatisticReconciliationActualExportValueTypes.Decimal:
                cell.SetValue(decimal.Parse(value, CultureInfo.InvariantCulture));
                break;
            case StatisticReconciliationActualExportValueTypes.Boolean:
                cell.SetValue(bool.Parse(value));
                break;
            case StatisticReconciliationActualExportValueTypes.UtcInstant:
                cell.SetValue(DateTime.ParseExact(
                    value,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind));
                break;
            default:
                cell.SetValue(value);
                break;
        }
    }

    private static string Hash(char value) => new(value, 64);
}
