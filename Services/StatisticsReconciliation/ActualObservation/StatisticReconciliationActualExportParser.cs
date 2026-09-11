using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Read-only parser for immutable P9 canonical export artifacts. The parser
/// never calls a P9 result/export service and never derives a value from a
/// rendered number/date format. Artifact bytes, the external owner manifest,
/// native XLSX cell types and explicit CSV column contracts are the only inputs.
/// </summary>
internal sealed class StatisticReconciliationActualExportParser
{
    internal const string RequiredSchemaVersion = "P9_CANONICAL_EXPORT_V1";
    internal const int MaxRows = 50_000;
    internal const int MaxColumns = 512;
    internal const int MaxBytes = 20 * 1024 * 1024;
    internal const int MaxXlsxArchiveEntries = 64;
    internal const long MaxXlsxEntryBytes = 64L * 1024 * 1024;
    internal const long MaxXlsxExpandedBytes = 64L * 1024 * 1024;
    internal const int MaxXlsxEntryPathLength = 256;

    private const string CsvContentType = "text/csv; charset=utf-8";
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly byte[] Utf8Preamble = [0xef, 0xbb, 0xbf];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal string ComputeManifestSha256(
        StatisticReconciliationActualExportManifest manifest)
    {
        var value = NormalizeManifest(manifest);
        var columnSha256 = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXPORT_MANIFEST_COLUMNS_V1",
            value.Columns.Select(ColumnSha256));
        return StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXPORT_MANIFEST_V2",
            value.SchemaVersion,
            value.ExportId,
            value.RequestSha256,
            value.AuthorizationSnapshotSha256,
            value.Format,
            value.ResultKind,
            value.ContentType,
            value.FileName,
            value.ContentSha256,
            StatisticReconciliationActualCanonical.Integer(value.ByteCount),
            StatisticReconciliationActualCanonical.Integer(value.RowCount),
            StatisticReconciliationActualCanonical.Integer(value.ColumnCount),
            value.WorkId,
            value.ScopeType,
            value.ScopeId,
            value.ResultId,
            value.ResultSha256,
            value.ConfigSha256,
            value.SourceSha256,
            value.FilterSha256,
            value.PeriodInstanceKey ?? "~",
            value.CanonicalFilterJson ?? "~",
            StatisticReconciliationActualCanonical.Integer(value.LifecycleRevision),
            value.CatalogVersion,
            value.CatalogRawSha256,
            value.CatalogSemanticSha256,
            value.StageLockSha256,
            value.CandidateChainId,
            value.CandidatePromptId,
            StatisticReconciliationActualCanonical.Integer(value.CandidateStage),
            value.OwnerSemanticSha256,
            StatisticReconciliationActualCanonical.Instant(value.CompletedAtUtc),
            columnSha256);
    }

    internal StatisticReconciliationActualExportCapture Parse(
        StatisticReconciliationActualExportArtifact artifact)
    {
        if (artifact is null)
            throw Fail("EXPORT_ARTIFACT_REQUIRED");
        var manifest = NormalizeManifest(artifact.Manifest);
        var declaredManifestSha256 = StatisticReconciliationActualCanonical.Sha256(
            artifact.ManifestSha256,
            "EXPORT_MANIFEST_SHA256");
        var computedManifestSha256 = ComputeManifestSha256(manifest);
        if (!Eq(declaredManifestSha256, computedManifestSha256))
            throw Fail("EXPORT_MANIFEST_DIGEST_MISMATCH");

        if (artifact.Content.Length != manifest.ByteCount)
            throw Fail("EXPORT_BYTE_COUNT_MISMATCH");
        if (artifact.Content.Length > MaxBytes)
            throw Fail("EXPORT_CONTENT_TOO_LARGE");
        var contentSha256 = Convert.ToHexString(
                SHA256.HashData(artifact.Content.Span))
            .ToLowerInvariant();
        if (!Eq(contentSha256, manifest.ContentSha256))
            throw Fail("EXPORT_CONTENT_DIGEST_MISMATCH");

        var parsed = manifest.Format == StatisticReconciliationActualExportFormats.Csv
            ? ParseCsv(manifest, artifact.Content.Span)
            : ParseXlsx(manifest, artifact.Content);
        ValidateTable(manifest, parsed);

        var rows = BuildRows(manifest.Columns, parsed.Rows);
        var totals = BuildTotals(manifest, rows);
        var rowsSha256 = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXPORT_ROWS_V1",
            rows.Select(row => row.RowSemanticSha256));
        var totalsSha256 = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXPORT_TOTALS_V1",
            totals.Select(total => total.TotalSemanticSha256));
        var captureSha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXPORT_CAPTURE_V2",
            manifest.ExportId,
            manifest.Format,
            manifest.ResultKind,
            manifest.ResultId,
            manifest.ResultSha256,
            manifest.ConfigSha256,
            manifest.SourceSha256,
            manifest.FilterSha256,
            manifest.PeriodInstanceKey ?? "~",
            manifest.CanonicalFilterJson ?? "~",
            StatisticReconciliationActualCanonical.Integer(manifest.LifecycleRevision),
            manifest.ContentSha256,
            declaredManifestSha256,
            manifest.OwnerSemanticSha256,
            rowsSha256,
            totalsSha256);

        return new StatisticReconciliationActualExportCapture(
            manifest.ExportId,
            manifest.Format,
            manifest.ResultKind,
            manifest.ResultId,
            manifest.ResultSha256,
            manifest.ConfigSha256,
            manifest.SourceSha256,
            manifest.FilterSha256,
            manifest.LifecycleRevision,
            manifest.ContentSha256,
            declaredManifestSha256,
            manifest.OwnerSemanticSha256,
            parsed.Headers,
            rows,
            totals,
            rowsSha256,
            totalsSha256,
            captureSha256,
            manifest.PeriodInstanceKey,
            manifest.CanonicalFilterJson);
    }

    private static StatisticReconciliationActualExportManifest NormalizeManifest(
        StatisticReconciliationActualExportManifest? manifest)
    {
        if (manifest is null)
            throw Fail("EXPORT_MANIFEST_REQUIRED");
        var format = StatisticReconciliationActualCanonical.Upper(
            manifest.Format,
            "EXPORT_FORMAT");
        if (format is not (StatisticReconciliationActualExportFormats.Csv or
                           StatisticReconciliationActualExportFormats.Xlsx))
        {
            throw Fail("EXPORT_FORMAT_INVALID");
        }
        var expectedContentType = format == StatisticReconciliationActualExportFormats.Csv
            ? CsvContentType
            : XlsxContentType;
        var contentType = StatisticReconciliationActualCanonical.Required(
            manifest.ContentType,
            "EXPORT_CONTENT_TYPE");
        if (!Eq(contentType, expectedContentType))
            throw Fail("EXPORT_CONTENT_TYPE_INVALID");

        var fileName = StatisticReconciliationActualCanonical.Required(
            manifest.FileName,
            "EXPORT_FILE_NAME",
            255);
        if (!Eq(fileName, Path.GetFileName(fileName)) ||
            fileName.Contains('/') || fileName.Contains('\\') ||
            !fileName.EndsWith(
                format == StatisticReconciliationActualExportFormats.Csv ? ".csv" : ".xlsx",
                StringComparison.Ordinal))
        {
            throw Fail("EXPORT_FILE_NAME_INVALID");
        }
        if (manifest.ByteCount < 0 || manifest.ByteCount > MaxBytes)
            throw Fail("EXPORT_BYTE_COUNT_INVALID");
        if (manifest.RowCount < 0 || manifest.RowCount > MaxRows)
            throw Fail("EXPORT_ROW_COUNT_INVALID");
        if (manifest.ColumnCount <= 0 || manifest.ColumnCount > MaxColumns)
            throw Fail("EXPORT_COLUMN_COUNT_INVALID");
        if (manifest.LifecycleRevision < 0 || manifest.CandidateStage < 0)
            throw Fail("EXPORT_REVISION_INVALID");
        if (manifest.CompletedAtUtc.Kind != DateTimeKind.Utc)
            throw Fail("EXPORT_COMPLETED_AT_NOT_UTC");

        var periodInstanceKey = manifest.PeriodInstanceKey;
        var canonicalFilterJson = manifest.CanonicalFilterJson;
        if ((periodInstanceKey is null) != (canonicalFilterJson is null))
            throw Fail("EXPORT_FILTER_PREIMAGE_PARTIAL");
        if (periodInstanceKey is not null)
        {
            periodInstanceKey = StatisticReconciliationActualCanonical.Required(
                periodInstanceKey,
                "EXPORT_PERIOD_INSTANCE_KEY");
            using var filterDocument =
                StatisticReconciliationActualJson.ParseStrict(
                    canonicalFilterJson!,
                    "EXPORT_CANONICAL_FILTER_JSON");
            if (filterDocument.RootElement.ValueKind !=
                System.Text.Json.JsonValueKind.Object)
                throw Fail("EXPORT_FILTER_PREIMAGE_INVALID");
            var recomputedFilterJson =
                StatisticReconciliationActualJson.Canonicalize(
                    filterDocument.RootElement);
            if (!Eq(recomputedFilterJson, canonicalFilterJson) ||
                !Eq(
                    StatisticReconciliationActualJson.RawSha256(
                        recomputedFilterJson),
                    manifest.FilterSha256))
                throw Fail("EXPORT_FILTER_PREIMAGE_HASH_MISMATCH");
            canonicalFilterJson = recomputedFilterJson;
        }

        var columns = NormalizeColumns(manifest.Columns, manifest.ColumnCount);
        return manifest with
        {
            SchemaVersion = Exact(
                manifest.SchemaVersion,
                RequiredSchemaVersion,
                "EXPORT_SCHEMA_VERSION"),
            ExportId = StatisticReconciliationActualCanonical.Required(
                manifest.ExportId,
                "EXPORT_ID"),
            RequestSha256 = Sha(manifest.RequestSha256, "EXPORT_REQUEST_SHA256"),
            AuthorizationSnapshotSha256 = Sha(
                manifest.AuthorizationSnapshotSha256,
                "EXPORT_AUTHORIZATION_SHA256"),
            Format = format,
            ResultKind = NormalizeResultKind(manifest.ResultKind),
            ContentType = contentType,
            FileName = fileName,
            ContentSha256 = Sha(manifest.ContentSha256, "EXPORT_CONTENT_SHA256"),
            WorkId = StatisticReconciliationActualCanonical.Required(
                manifest.WorkId,
                "EXPORT_WORK_ID"),
            ScopeType = StatisticReconciliationActualCanonical.Upper(
                manifest.ScopeType,
                "EXPORT_SCOPE_TYPE"),
            ScopeId = StatisticReconciliationActualCanonical.Required(
                manifest.ScopeId,
                "EXPORT_SCOPE_ID"),
            ResultId = StatisticReconciliationActualCanonical.Required(
                manifest.ResultId,
                "EXPORT_RESULT_ID"),
            ResultSha256 = Sha(manifest.ResultSha256, "EXPORT_RESULT_SHA256"),
            ConfigSha256 = Sha(manifest.ConfigSha256, "EXPORT_CONFIG_SHA256"),
            SourceSha256 = Sha(manifest.SourceSha256, "EXPORT_SOURCE_SHA256"),
            FilterSha256 = Sha(manifest.FilterSha256, "EXPORT_FILTER_SHA256"),
            CatalogVersion = StatisticReconciliationActualCanonical.Required(
                manifest.CatalogVersion,
                "EXPORT_CATALOG_VERSION"),
            CatalogRawSha256 = Sha(
                manifest.CatalogRawSha256,
                "EXPORT_CATALOG_RAW_SHA256"),
            CatalogSemanticSha256 = Sha(
                manifest.CatalogSemanticSha256,
                "EXPORT_CATALOG_SEMANTIC_SHA256"),
            StageLockSha256 = Sha(
                manifest.StageLockSha256,
                "EXPORT_STAGE_LOCK_SHA256"),
            CandidateChainId = StatisticReconciliationActualCanonical.Required(
                manifest.CandidateChainId,
                "EXPORT_CANDIDATE_CHAIN_ID"),
            CandidatePromptId = StatisticReconciliationActualCanonical.Required(
                manifest.CandidatePromptId,
                "EXPORT_CANDIDATE_PROMPT_ID"),
            OwnerSemanticSha256 = Sha(
                manifest.OwnerSemanticSha256,
                "EXPORT_OWNER_SEMANTIC_SHA256"),
            Columns = columns,
            PeriodInstanceKey = periodInstanceKey,
            CanonicalFilterJson = canonicalFilterJson
        };
    }

    private static ImmutableArray<StatisticReconciliationActualExportColumnContract>
        NormalizeColumns(
            ImmutableArray<StatisticReconciliationActualExportColumnContract> columns,
            int columnCount)
    {
        if (columns.IsDefault || columns.Length != columnCount)
            throw Fail("EXPORT_COLUMN_CONTRACT_COUNT_MISMATCH");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualExportColumnContract>(columns.Length);
        for (var index = 0; index < columns.Length; index++)
        {
            var value = columns[index] ?? throw Fail("EXPORT_COLUMN_CONTRACT_REQUIRED");
            if (value.Ordinal != index)
                throw Fail("EXPORT_COLUMN_ORDINAL_INVALID");
            var name = StatisticReconciliationActualCanonical.Required(
                value.Name,
                "EXPORT_COLUMN_NAME",
                256);
            if (!names.Add(name))
                throw Fail("EXPORT_COLUMN_NAME_DUPLICATE");
            var valueType = StatisticReconciliationActualCanonical.Upper(
                value.ValueType,
                "EXPORT_COLUMN_VALUE_TYPE");
            if (!StatisticReconciliationActualExportValueTypes.IsSupported(valueType))
                throw Fail("EXPORT_COLUMN_VALUE_TYPE_INVALID");
            var blankPolicy = StatisticReconciliationActualCanonical.Upper(
                value.BlankPolicy,
                "EXPORT_COLUMN_BLANK_POLICY");
            if (!StatisticReconciliationActualExportBlankPolicies.IsSupported(blankPolicy))
                throw Fail("EXPORT_COLUMN_BLANK_POLICY_INVALID");
            builder.Add(value with
            {
                Name = name,
                ValueType = valueType,
                BlankPolicy = blankPolicy
            });
        }
        if (builder[0].Name != "ordinal" ||
            builder[0].ValueType != StatisticReconciliationActualExportValueTypes.Integer ||
            builder[0].BlankPolicy != StatisticReconciliationActualExportBlankPolicies.Forbidden ||
            builder[0].IsFullFilterTotal)
        {
            throw Fail("EXPORT_ORDINAL_COLUMN_INVALID");
        }
        return builder.MoveToImmutable();
    }

    private static string NormalizeResultKind(string? value)
    {
        var result = StatisticReconciliationActualCanonical.Upper(
            value,
            "EXPORT_RESULT_KIND");
        return result is "DIRECT_FIELD" or "DIRECT_TABLE" or "DIRECT_LABEL" or
            "BASIC" or "ADVANCED" or "DIFF" or "FLOW"
            ? result
            : throw Fail("EXPORT_RESULT_KIND_INVALID");
    }

    private static ParsedExportTable ParseCsv(
        StatisticReconciliationActualExportManifest manifest,
        ReadOnlySpan<byte> content)
    {
        if (!content.StartsWith(Utf8Preamble))
            throw Fail("EXPORT_CSV_BOM_REQUIRED");
        string text;
        try
        {
            text = StrictUtf8.GetString(content[Utf8Preamble.Length..]);
        }
        catch (DecoderFallbackException)
        {
            throw Fail("EXPORT_CSV_UTF8_INVALID");
        }
        var table = ParseStrictRfc4180(
            text,
            checked(manifest.RowCount + 1),
            manifest.ColumnCount);
        if (table.Count == 0)
            throw Fail("EXPORT_HEADER_REQUIRED");
        var headers = table[0].ToImmutableArray();
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<RawExportCell>>(
            Math.Max(0, table.Count - 1));
        for (var row = 1; row < table.Count; row++)
        {
            var values = table[row];
            var cells = ImmutableArray.CreateBuilder<RawExportCell>(values.Count);
            foreach (var value in values)
                cells.Add(new RawExportCell(value, null));
            rows.Add(cells.MoveToImmutable());
        }
        return new ParsedExportTable(headers, rows.MoveToImmutable());
    }

    private static ParsedExportTable ParseXlsx(
        StatisticReconciliationActualExportManifest manifest,
        ReadOnlyMemory<byte> content)
    {
        try
        {
            var bytes = content.ToArray();
            ValidateXlsxArchive(bytes);
            using var stream = new MemoryStream(bytes, writable: false);
            using var workbook = new XLWorkbook(stream);
            if (workbook.Worksheets.Count != 2 ||
                !workbook.TryGetWorksheet("Result", out var result) ||
                !workbook.TryGetWorksheet("Metadata", out var metadata))
            {
                throw Fail("EXPORT_XLSX_SHEET_CONTRACT_INVALID");
            }
            foreach (var cell in workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed()))
            {
                if (!string.IsNullOrEmpty(cell.FormulaA1) ||
                    !string.IsNullOrEmpty(cell.FormulaR1C1))
                {
                    throw Fail("EXPORT_XLSX_FORMULA_DETECTED");
                }
            }
            ValidateXlsxMetadata(manifest, metadata);

            var lastColumn = result.LastColumnUsed()?.ColumnNumber() ?? 0;
            var lastRow = result.LastRowUsed()?.RowNumber() ?? 0;
            if (lastColumn != manifest.ColumnCount ||
                lastRow != manifest.RowCount + 1)
            {
                throw Fail("EXPORT_XLSX_DIMENSION_MISMATCH");
            }
            var headers = Enumerable.Range(1, manifest.ColumnCount)
                .Select(column => result.Cell(1, column).GetString())
                .ToImmutableArray();
            var rows = ImmutableArray.CreateBuilder<ImmutableArray<RawExportCell>>(
                manifest.RowCount);
            for (var row = 0; row < manifest.RowCount; row++)
            {
                var cells = ImmutableArray.CreateBuilder<RawExportCell>(
                    manifest.ColumnCount);
                for (var column = 0; column < manifest.ColumnCount; column++)
                {
                    var cell = result.Cell(row + 2, column + 1);
                    cells.Add(new RawExportCell(
                        cell.IsEmpty() ? string.Empty : NativeXlsxValue(cell),
                        cell.IsEmpty() ? null : cell.DataType));
                }
                rows.Add(cells.MoveToImmutable());
            }
            return new ParsedExportTable(headers, rows.MoveToImmutable());
        }
        catch (StatisticReconciliationActualObservationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException &&
            exception is not StackOverflowException)
        {
            throw Fail("EXPORT_XLSX_INVALID");
        }
    }

    internal static void ValidateXlsxArchive(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(
                stream,
                ZipArchiveMode.Read,
                leaveOpen: false);
            if (archive.Entries.Count == 0 ||
                archive.Entries.Count > MaxXlsxArchiveEntries)
            {
                throw Fail("EXPORT_XLSX_ARCHIVE_ENTRY_LIMIT");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (!ValidXlsxEntryPath(entry.FullName))
                    throw Fail("EXPORT_XLSX_ARCHIVE_PATH_INVALID");
                if (!names.Add(entry.FullName))
                    throw Fail("EXPORT_XLSX_ARCHIVE_DUPLICATE_ENTRY");
                if (entry.Length > MaxXlsxEntryBytes)
                    throw Fail("EXPORT_XLSX_ARCHIVE_ENTRY_SIZE_LIMIT");
                if (entry.Length > MaxXlsxExpandedBytes - expandedBytes)
                    throw Fail("EXPORT_XLSX_ARCHIVE_EXPANDED_LIMIT");
                expandedBytes += entry.Length;
            }

            var required = new[]
            {
                "[Content_Types].xml",
                "_rels/.rels",
                "xl/workbook.xml",
                "xl/_rels/workbook.xml.rels",
                "xl/worksheets/sheet1.xml",
                "xl/worksheets/sheet2.xml"
            };
            if (required.Any(name => !names.Contains(name)))
                throw Fail("EXPORT_XLSX_ARCHIVE_REQUIRED_ENTRY_MISSING");
        }
        catch (StatisticReconciliationActualObservationException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw Fail("EXPORT_XLSX_ARCHIVE_INVALID");
        }
    }

    private static bool ValidXlsxEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaxXlsxEntryPathLength ||
            value[0] == '/' ||
            value.Contains('\\') ||
            value.Contains(':') ||
            value.Contains('%'))
        {
            return false;
        }
        var path = value.EndsWith("/", StringComparison.Ordinal)
            ? value[..^1]
            : value;
        if (path.Length == 0)
            return false;
        return path.Split('/').All(segment =>
            segment.Length > 0 &&
            segment is not "." and not "..");
    }

    private static string NativeXlsxValue(IXLCell cell)
        => cell.DataType switch
        {
            XLDataType.Number => cell.GetValue<decimal>().ToString(
                "0.#############################",
                CultureInfo.InvariantCulture),
            XLDataType.Boolean => cell.GetBoolean() ? "true" : "false",
            XLDataType.DateTime => DateTime.SpecifyKind(
                    cell.GetDateTime(),
                    DateTimeKind.Utc)
                .ToString("O", CultureInfo.InvariantCulture),
            XLDataType.Text => cell.GetString(),
            _ => throw Fail("EXPORT_XLSX_CELL_TYPE_UNSUPPORTED")
        };

    private static void ValidateXlsxMetadata(
        StatisticReconciliationActualExportManifest manifest,
        IXLWorksheet metadata)
    {
        var lastRow = metadata.LastRowUsed()?.RowNumber() ?? 0;
        var lastColumn = metadata.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastRow != 13 || lastColumn != 2)
            throw Fail("EXPORT_METADATA_SHAPE_INVALID");
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var row = 1; row <= lastRow; row++)
        {
            var key = metadata.Cell(row, 1).GetString();
            if (!pairs.TryAdd(key, metadata.Cell(row, 2).GetString()))
                throw Fail("EXPORT_METADATA_KEY_DUPLICATE");
        }
        var expected = new (string Key, string Value)[]
        {
            ("schemaVersion", manifest.SchemaVersion),
            ("resultId", manifest.ResultId),
            ("resultHash", manifest.ResultSha256),
            ("configHash", manifest.ConfigSha256),
            ("sourceHash", manifest.SourceSha256),
            ("lifecycleRevision", StatisticReconciliationActualCanonical.Integer(
                manifest.LifecycleRevision)),
            ("catalogVersion", manifest.CatalogVersion),
            ("catalogRawSha256", manifest.CatalogRawSha256),
            ("catalogSemanticSha256", manifest.CatalogSemanticSha256),
            ("semanticHash", manifest.OwnerSemanticSha256),
            ("completedAtUtc", StatisticReconciliationActualCanonical.Instant(
                manifest.CompletedAtUtc)),
            ("rowCount", StatisticReconciliationActualCanonical.Integer(
                manifest.RowCount)),
            ("columnCount", StatisticReconciliationActualCanonical.Integer(
                manifest.ColumnCount))
        };
        if (pairs.Count != expected.Length)
            throw Fail("EXPORT_METADATA_KEY_SET_INVALID");
        foreach (var item in expected)
        {
            if (!pairs.TryGetValue(item.Key, out var actual) || !Eq(actual, item.Value))
                throw Fail($"EXPORT_METADATA_{MetadataReason(item.Key)}_MISMATCH");
        }
    }

    private static string MetadataReason(string key)
        => string.Concat(key.Select(character => char.IsUpper(character)
                ? $"_{character}"
                : character.ToString()))
            .ToUpperInvariant();

    private static List<List<string>> ParseStrictRfc4180(
        string text,
        int expectedRows,
        int expectedColumns)
    {
        if (text.Length == 0 || !text.EndsWith("\r\n", StringComparison.Ordinal))
            throw Fail("EXPORT_CSV_TERMINATOR_INVALID");
        var rows = new List<List<string>>();
        var index = 0;
        while (index < text.Length)
        {
            if (rows.Count >= expectedRows)
                throw Fail("EXPORT_CSV_ROW_LIMIT");
            var row = new List<string>(expectedColumns);
            while (true)
            {
                if (row.Count >= expectedColumns)
                    throw Fail("EXPORT_CSV_COLUMN_LIMIT");
                if (index >= text.Length || text[index] != '"')
                    throw Fail("EXPORT_CSV_FIELD_QUOTE_REQUIRED");
                index++;
                var value = new StringBuilder();
                var closed = false;
                while (index < text.Length)
                {
                    var character = text[index++];
                    if (character != '"')
                    {
                        value.Append(character);
                        continue;
                    }
                    if (index < text.Length && text[index] == '"')
                    {
                        value.Append('"');
                        index++;
                        continue;
                    }
                    closed = true;
                    break;
                }
                if (!closed)
                    throw Fail("EXPORT_CSV_QUOTE_UNCLOSED");
                row.Add(value.ToString());
                if (index < text.Length && text[index] == ',')
                {
                    index++;
                    continue;
                }
                if (index + 1 < text.Length &&
                    text[index] == '\r' && text[index + 1] == '\n')
                {
                    index += 2;
                    break;
                }
                throw Fail("EXPORT_CSV_RECORD_SEPARATOR_INVALID");
            }
            rows.Add(row);
        }
        return rows;
    }

    private static void ValidateTable(
        StatisticReconciliationActualExportManifest manifest,
        ParsedExportTable table)
    {
        if (table.Headers.Length != manifest.ColumnCount ||
            table.Rows.Length != manifest.RowCount)
        {
            throw Fail("EXPORT_TABLE_DIMENSION_MISMATCH");
        }
        for (var column = 0; column < manifest.ColumnCount; column++)
        {
            if (!Eq(table.Headers[column], manifest.Columns[column].Name))
                throw Fail("EXPORT_HEADER_CONTRACT_MISMATCH");
        }
        if (table.Headers.Distinct(StringComparer.Ordinal).Count() != table.Headers.Length)
            throw Fail("EXPORT_HEADER_DUPLICATE");
        if (table.Rows.Any(row => row.Length != manifest.ColumnCount))
            throw Fail("EXPORT_ROW_WIDTH_MISMATCH");
    }

    private static ImmutableArray<StatisticReconciliationActualExportRowObservation>
        BuildRows(
            ImmutableArray<StatisticReconciliationActualExportColumnContract> columns,
            ImmutableArray<ImmutableArray<RawExportCell>> rawRows)
    {
        var rows = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualExportRowObservation>(rawRows.Length);
        for (var rowIndex = 0; rowIndex < rawRows.Length; rowIndex++)
        {
            var cells = ImmutableArray.CreateBuilder<
                StatisticReconciliationActualExportCellObservation>(columns.Length);
            for (var column = 0; column < columns.Length; column++)
                cells.Add(ParseCell(columns[column], rawRows[rowIndex][column]));
            var immutableCells = cells.MoveToImmutable();
            var expectedOrdinal = rowIndex + 1;
            var ordinal = immutableCells[0];
            if (ordinal.ValueState != StatisticReconciliationActualExportValueStates.Value ||
                ordinal.CanonicalValue != StatisticReconciliationActualCanonical.Integer(
                    expectedOrdinal))
            {
                throw Fail("EXPORT_ROW_ORDINAL_INVALID");
            }
            var rowSha256 = StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_EXPORT_ROW_V1",
                immutableCells.Select(cell => cell.CellSemanticSha256));
            rows.Add(new StatisticReconciliationActualExportRowObservation(
                expectedOrdinal,
                immutableCells,
                rowSha256));
        }
        return rows.MoveToImmutable();
    }

    private static StatisticReconciliationActualExportCellObservation ParseCell(
        StatisticReconciliationActualExportColumnContract column,
        RawExportCell raw)
    {
        if (raw.Value.Length == 0)
            return BlankCell(column);

        var valueState = StatisticReconciliationActualExportValueStates.Value;
        var canonical = raw.Value;
        var scale = 0;
        switch (column.ValueType)
        {
            case StatisticReconciliationActualExportValueTypes.Integer:
                RequireNativeType(raw, XLDataType.Number);
                if (!long.TryParse(
                        raw.Value,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var integer) ||
                    raw.Value != StatisticReconciliationActualCanonical.Integer(integer))
                {
                    throw Fail("EXPORT_INTEGER_NON_CANONICAL");
                }
                canonical = StatisticReconciliationActualCanonical.Integer(integer);
                break;
            case StatisticReconciliationActualExportValueTypes.Decimal:
                RequireNativeType(raw, XLDataType.Number);
                (canonical, scale) = ParseDecimal(raw.Value);
                break;
            case StatisticReconciliationActualExportValueTypes.Boolean:
                RequireNativeType(raw, XLDataType.Boolean);
                canonical = raw.Value switch
                {
                    "true" => "true",
                    "false" => "false",
                    _ => throw Fail("EXPORT_BOOLEAN_NON_CANONICAL")
                };
                break;
            case StatisticReconciliationActualExportValueTypes.UtcInstant:
                RequireNativeType(raw, XLDataType.DateTime);
                canonical = ParseUtcInstant(raw.Value);
                break;
            case StatisticReconciliationActualExportValueTypes.Text:
                RequireNativeType(raw, XLDataType.Text);
                break;
            case StatisticReconciliationActualExportValueTypes.Json:
                RequireNativeType(raw, XLDataType.Text);
                using (var document = StatisticReconciliationActualJson.ParseStrict(
                           raw.Value,
                           "EXPORT_JSON_CELL"))
                {
                    canonical = StatisticReconciliationActualJson.Canonicalize(
                        document.RootElement);
                }
                break;
            default:
                throw Fail("EXPORT_CELL_VALUE_TYPE_INVALID");
        }
        var neutralized = column.ValueType ==
                          StatisticReconciliationActualExportValueTypes.Text &&
                          canonical.Length > 1 && canonical[0] == '\'' &&
                          canonical[1] is '=' or '+' or '-' or '@';
        return Cell(column, valueState, canonical, scale, neutralized);
    }

    private static void RequireNativeType(RawExportCell raw, XLDataType expected)
    {
        if (raw.NativeType.HasValue && raw.NativeType.Value != expected)
            throw Fail("EXPORT_XLSX_NATIVE_TYPE_MISMATCH");
    }

    private static StatisticReconciliationActualExportCellObservation BlankCell(
        StatisticReconciliationActualExportColumnContract column)
        => column.BlankPolicy switch
        {
            StatisticReconciliationActualExportBlankPolicies.Null => Cell(
                column,
                StatisticReconciliationActualExportValueStates.Null,
                string.Empty,
                0,
                false),
            StatisticReconciliationActualExportBlankPolicies.Empty => Cell(
                column,
                StatisticReconciliationActualExportValueStates.Empty,
                string.Empty,
                0,
                false),
            _ => throw Fail("EXPORT_BLANK_FORBIDDEN")
        };

    private static StatisticReconciliationActualExportCellObservation Cell(
        StatisticReconciliationActualExportColumnContract column,
        string state,
        string canonical,
        int scale,
        bool formulaNeutralized)
    {
        var sha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXPORT_CELL_V1",
            StatisticReconciliationActualCanonical.Integer(column.Ordinal),
            column.Name,
            column.ValueType,
            state,
            canonical,
            StatisticReconciliationActualCanonical.Integer(scale),
            StatisticReconciliationActualCanonical.Boolean(formulaNeutralized));
        return new StatisticReconciliationActualExportCellObservation(
            column.Ordinal,
            column.Name,
            column.ValueType,
            state,
            canonical,
            scale,
            formulaNeutralized,
            sha256);
    }

    private static (string Canonical, int Scale) ParseDecimal(string value)
    {
        var index = value.Length > 0 && value[0] == '-' ? 1 : 0;
        if (index >= value.Length || !char.IsAsciiDigit(value[index]))
            throw Fail("EXPORT_DECIMAL_NON_CANONICAL");
        var integerStart = index;
        while (index < value.Length && char.IsAsciiDigit(value[index]))
            index++;
        if (index - integerStart > 1 && value[integerStart] == '0')
            throw Fail("EXPORT_DECIMAL_NON_CANONICAL");
        var scale = 0;
        if (index < value.Length)
        {
            if (value[index++] != '.' || index >= value.Length)
                throw Fail("EXPORT_DECIMAL_NON_CANONICAL");
            var fractionStart = index;
            while (index < value.Length && char.IsAsciiDigit(value[index]))
                index++;
            scale = index - fractionStart;
        }
        if (index != value.Length ||
            !decimal.TryParse(
                value,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var number))
        {
            throw Fail("EXPORT_DECIMAL_NON_CANONICAL");
        }
        var canonical = number.ToString(
            scale == 0 ? "0" : $"0.{new string('0', scale)}",
            CultureInfo.InvariantCulture);
        if (canonical != value)
            throw Fail("EXPORT_DECIMAL_NON_CANONICAL");
        return (canonical, scale);
    }

    private static string ParseUtcInstant(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var instant) || instant.Offset != TimeSpan.Zero)
        {
            throw Fail("EXPORT_INSTANT_NON_CANONICAL");
        }
        var canonical = instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        if (canonical != value)
            throw Fail("EXPORT_INSTANT_NON_CANONICAL");
        return canonical;
    }

    private static ImmutableArray<StatisticReconciliationActualExportTotalObservation>
        BuildTotals(
            StatisticReconciliationActualExportManifest manifest,
            ImmutableArray<StatisticReconciliationActualExportRowObservation> rows)
    {
        var totals = new Dictionary<string,
            StatisticReconciliationActualExportTotalObservation>(StringComparer.Ordinal)
        {
            ["columnCount"] = Total(
                "columnCount",
                StatisticReconciliationActualExportValueTypes.Integer,
                StatisticReconciliationActualExportValueStates.Value,
                StatisticReconciliationActualCanonical.Integer(manifest.ColumnCount),
                0),
            ["rowCount"] = Total(
                "rowCount",
                StatisticReconciliationActualExportValueTypes.Integer,
                StatisticReconciliationActualExportValueStates.Value,
                StatisticReconciliationActualCanonical.Integer(manifest.RowCount),
                0)
        };
        foreach (var column in manifest.Columns.Where(value => value.IsFullFilterTotal))
        {
            if (rows.Length == 0)
                throw Fail("EXPORT_TOTAL_VALUE_REQUIRED");
            var values = rows.Select(row => row.Cells[column.Ordinal]).ToArray();
            var first = values[0];
            if (first.ValueState != StatisticReconciliationActualExportValueStates.Value ||
                values.Any(value =>
                    value.ValueType != first.ValueType ||
                    value.ValueState != first.ValueState ||
                    value.CanonicalValue != first.CanonicalValue ||
                    value.DecimalScale != first.DecimalScale))
            {
                throw Fail("EXPORT_TOTAL_DRIFT");
            }
            if (!totals.TryAdd(column.Name, Total(
                    column.Name,
                    first.ValueType,
                    first.ValueState,
                    first.CanonicalValue,
                    first.DecimalScale)))
            {
                throw Fail("EXPORT_TOTAL_NAME_DUPLICATE");
            }
        }
        return totals.Values
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static StatisticReconciliationActualExportTotalObservation Total(
        string name,
        string valueType,
        string state,
        string canonical,
        int scale)
    {
        var sha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXPORT_TOTAL_V1",
            name,
            valueType,
            state,
            canonical,
            StatisticReconciliationActualCanonical.Integer(scale));
        return new StatisticReconciliationActualExportTotalObservation(
            name,
            valueType,
            state,
            canonical,
            scale,
            sha256);
    }

    private static string ColumnSha256(
        StatisticReconciliationActualExportColumnContract column)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXPORT_COLUMN_V1",
            StatisticReconciliationActualCanonical.Integer(column.Ordinal),
            column.Name,
            column.ValueType,
            column.BlankPolicy,
            StatisticReconciliationActualCanonical.Boolean(column.IsFullFilterTotal));

    private static string Exact(string? value, string expected, string name)
    {
        var normalized = StatisticReconciliationActualCanonical.Required(value, name);
        return Eq(normalized, expected) ? normalized : throw Fail($"{name}_INVALID");
    }

    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);

    private static bool Eq(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);

    private static StatisticReconciliationActualObservationException Fail(string reason)
        => new(reason);

    private sealed record RawExportCell(string Value, XLDataType? NativeType);

    private sealed record ParsedExportTable(
        ImmutableArray<string> Headers,
        ImmutableArray<ImmutableArray<RawExportCell>> Rows);
}
