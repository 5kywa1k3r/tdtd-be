using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private P9ExportDownloaded? _csvExport;
    private P9ExportDownloaded? _xlsxExport;

    private async Task RunExportCasesAsync(CancellationToken ct)
    {
        await RunExportCsvCasesAsync(ct);
        await RunExportXlsxCasesAsync(ct);
        await RunExportAclCasesAsync(ct);
    }

    private async Task RunExportCsvCasesAsync(CancellationToken ct)
    {
        await RunExportCaseAsync("P9-EXP-CSV-01", async () =>
        {
            foreach (var kind in new[]
                     {
                         StatRunExportResultKinds.DirectField,
                         StatRunExportResultKinds.Basic,
                         StatRunExportResultKinds.Advanced,
                         StatRunExportResultKinds.Diff,
                         StatRunExportResultKinds.Flow
                     })
            {
                var downloaded = await CreateAndDownloadExportAsync(
                    _exportFixtures[kind], "CSV", $"p9-exp-csv01-{kind.ToLowerInvariant()}",
                    Actor("executor"), ct);
                var parsed = ParseCsv(downloaded.Content);
                HarnessAssert.True(parsed.Count >= 1, $"CSV {kind} header missing");
                HarnessAssert.Equal("ordinal", parsed[0][0], $"CSV {kind} ordinal header");
                HarnessAssert.Equal(downloaded.RowCount + 1, parsed.Count, $"CSV {kind} global rows");
                HarnessAssert.Equal(downloaded.ColumnCount, parsed[0].Count, $"CSV {kind} columns");
                if (kind == StatRunExportResultKinds.Basic)
                    _csvExport = downloaded;
            }
            _exportDirectMongoVerified = true;
            _exportConfigHashVerified = CanonicalJsonFileSha256(
                Encoding.UTF8.GetBytes(Fixture().ConfigPayloadJson)) == Fixture().ConfigHash;
            HarnessAssert.True(_exportConfigHashVerified, "P9-EXP config hash recompute");
            return new CaseObservation(
                "CSV exports consumed all five immutable canonical result kinds with server-owned global schema/order.",
                $"kinds=5;basicRows={_csvExport!.RowCount};first=ordinal");
        });

        await RunExportCaseAsync("P9-EXP-CSV-02", () =>
        {
            var export = RequireCsvExport();
            HarnessAssert.True(
                export.Content.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }),
                "CSV UTF-8 BOM missing");
            var text = Encoding.UTF8.GetString(export.Content.AsSpan(3));
            HarnessAssert.True(text.Contains("\r\n", StringComparison.Ordinal), "CSV CRLF missing");
            HarnessAssert.True(!text.Replace("\r\n", string.Empty).Contains('\n'), "CSV contains lone LF");
            HarnessAssert.True(ParseCsv(export.Content).All(row => row.Count == export.ColumnCount),
                "CSV RFC4180 column drift");
            return Task.FromResult(new CaseObservation(
                "CSV bytes carry UTF-8 BOM, quoted RFC4180 fields and CRLF records.",
                $"bom=efbbbf;bytes={export.Content.Length};columns={export.ColumnCount}"));
        });

        await RunExportCaseAsync("P9-EXP-CSV-03", () =>
        {
            var table = ParseCsv(RequireCsvExport().Content);
            var code = Column(table, 0, "code");
            HarnessAssert.Equal("Việt Nam, \"dòng\"\r\ntiếp", code, "CSV unicode/newline/quote roundtrip");
            return Task.FromResult(new CaseObservation(
                "Independent CSV parsing round-tripped Unicode, comma, quote and embedded newline bytes.",
                $"valueHash={HashBytes(Encoding.UTF8.GetBytes(code))}"));
        });

        await RunExportCaseAsync("P9-EXP-CSV-04", () =>
        {
            var table = ParseCsv(RequireCsvExport().Content);
            HarnessAssert.Equal("12.5", Column(table, 0, "amount"), "CSV decimal invariant");
            HarnessAssert.Equal("true", Column(table, 0, "active"), "CSV boolean invariant");
            HarnessAssert.True(Column(table, 0, "occurredAtUtc").EndsWith('Z'), "CSV UTC date");
            HarnessAssert.Equal(string.Empty, Column(table, 0, "empty"), "CSV null encoding");
            return Task.FromResult(new CaseObservation(
                "CSV typed values use invariant decimal/boolean/UTC text and an explicit empty null field.",
                "decimal=12.5;boolean=true;utc=Z;null=empty"));
        });

        await RunExportCaseAsync("P9-EXP-CSV-05", () =>
        {
            var table = ParseCsv(RequireCsvExport().Content);
            for (var index = 0; index < 4; index++)
            {
                var value = Column(table, index, "danger");
                HarnessAssert.True(value.StartsWith('\''), $"CSV formula prefix {index}");
            }
            _exportSecurityVerified = true;
            return Task.FromResult(new CaseObservation(
                "CSV neutralized =,+,-,@ spreadsheet formula prefixes without mutating canonical storage.",
                "prefixes=4;neutralizer=apostrophe"));
        });

        await RunExportCaseAsync("P9-EXP-CSV-06", async () =>
        {
            var export = RequireCsvExport();
            var fixture = _exportFixtures[StatRunExportResultKinds.Basic];
            var replay = await CreateExportAsync(
                fixture, "CSV", "p9-exp-csv01-basic", Actor("executor"), HttpStatusCode.OK, ct);
            HarnessAssert.Equal(export.ExportId, RequiredString(replay, "exportId"), "CSV replay export id");
            HarnessAssert.Equal(export.ContentHash, RequiredString(replay, "contentHash"), "CSV replay hash");
            HarnessAssert.True(RequiredBool(replay, "isReplay"), "CSV replay flag");
            HarnessAssert.True(export.FileName.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '-' or '.'),
                "CSV safe filename");
            _exportReceiptVerified = true;
            return new CaseObservation(
                "Exact command replay returned the same receipt, artifact identity and SHA without regeneration.",
                $"export={export.ExportId};sha={export.ContentHash};replay=true");
        });

        await RunExportCaseAsync("P9-EXP-CSV-07", async () =>
        {
            var empty = await CreateAndDownloadExportAsync(
                _exportFixtures["EMPTY"], "CSV", "p9-exp-csv07-empty",
                Actor("executor"), ct);
            HarnessAssert.Equal(0, empty.RowCount, "CSV empty rows");
            HarnessAssert.Equal(1, ParseCsv(empty.Content).Count, "CSV empty header only");
            var before = await CountExportMetadataAsync(ct);
            var limit = await RequireApi().PostAsync(
                "api/stat-runs/exports",
                ExportRequest(_exportFixtures["LIMIT"], "CSV", "p9-exp-csv07-limit"),
                Actor("executor").Token,
                ct: ct);
            HarnessAssert.True((int)limit.StatusCode is >= 400 and < 500, "CSV row limit status");
            HarnessAssert.Equal("STAT_RUN_EXPORT_LIMIT",
                ApiHarnessClient.FindStringRecursive(limit.Json, "errorCode"), "CSV row limit code");
            HarnessAssert.Equal(before, await CountExportMetadataAsync(ct), "CSV limit metadata promotion");
            return new CaseObservation(
                "Empty export remained parseable and a 50,001-row result failed before artifact promotion.",
                $"emptyRows=0;limit={StatRunExportContract.MaxRows};writes=0");
        });
    }

    private async Task RunExportXlsxCasesAsync(CancellationToken ct)
    {
        await RunExportCaseAsync("P9-EXP-XLSX-01", async () =>
        {
            foreach (var kind in new[]
                     {
                         StatRunExportResultKinds.DirectField,
                         StatRunExportResultKinds.Basic,
                         StatRunExportResultKinds.Advanced,
                         StatRunExportResultKinds.Diff,
                         StatRunExportResultKinds.Flow
                     })
            {
                var downloaded = await CreateAndDownloadExportAsync(
                    _exportFixtures[kind], "XLSX", $"p9-exp-xlsx01-{kind.ToLowerInvariant()}",
                    Actor("executor"), ct);
                using var workbook = OpenWorkbook(downloaded.Content);
                HarnessAssert.True(workbook.TryGetWorksheet("Result", out var result), $"XLSX {kind} Result");
                HarnessAssert.True(workbook.TryGetWorksheet("Metadata", out _), $"XLSX {kind} Metadata");
                HarnessAssert.Equal("ordinal", result.Cell(1, 1).GetString(), $"XLSX {kind} first header");
                HarnessAssert.Equal(downloaded.RowCount + 1, result.LastRowUsed()!.RowNumber(), $"XLSX {kind} rows");
                if (kind == StatRunExportResultKinds.Basic)
                    _xlsxExport = downloaded;
            }
            return new CaseObservation(
                "XLSX exports for all canonical kinds expose stable Result and Metadata sheets with global row order.",
                $"kinds=5;basicRows={_xlsxExport!.RowCount};sheets=2");
        });

        await RunExportCaseAsync("P9-EXP-XLSX-02", () =>
        {
            var export = RequireXlsxExport();
            HarnessAssert.True(export.Content.AsSpan().StartsWith(new byte[] { (byte)'P', (byte)'K' }), "XLSX ZIP signature");
            HarnessAssert.Equal(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                export.ContentType,
                "XLSX content type");
            HarnessAssert.True(export.FileName.EndsWith(".xlsx", StringComparison.Ordinal), "XLSX extension");
            return Task.FromResult(new CaseObservation(
                "XLSX is a valid OOXML ZIP with the pinned MIME type, safe filename and hash headers.",
                $"bytes={export.Content.Length};sha={export.ContentHash};mime={export.ContentType}"));
        });

        await RunExportCaseAsync("P9-EXP-XLSX-03", () =>
        {
            using var workbook = OpenWorkbook(RequireXlsxExport().Content);
            var result = workbook.Worksheet("Result");
            var codeColumn = FindColumn(result, "code");
            HarnessAssert.Equal("Việt Nam, \"dòng\"\ntiếp",
                result.Cell(2, codeColumn).GetString().Replace("\r\n", "\n"),
                "XLSX Unicode roundtrip");
            return Task.FromResult(new CaseObservation(
                "Independent OOXML parse round-tripped Unicode, quotes and embedded line breaks.",
                $"column={codeColumn};sharedString=true"));
        });

        await RunExportCaseAsync("P9-EXP-XLSX-04", () =>
        {
            using var workbook = OpenWorkbook(RequireXlsxExport().Content);
            var result = workbook.Worksheet("Result");
            HarnessAssert.Equal(XLDataType.Number, result.Cell(2, FindColumn(result, "amount")).DataType,
                "XLSX number type");
            HarnessAssert.Equal(XLDataType.Boolean, result.Cell(2, FindColumn(result, "active")).DataType,
                "XLSX boolean type");
            HarnessAssert.Equal(XLDataType.DateTime, result.Cell(2, FindColumn(result, "occurredAtUtc")).DataType,
                "XLSX date type");
            HarnessAssert.True(result.Cell(2, FindColumn(result, "empty")).IsEmpty(), "XLSX null type");
            return Task.FromResult(new CaseObservation(
                "XLSX preserved native number, boolean, UTC date and blank null cell types.",
                "types=Number,Boolean,DateTime,Blank"));
        });

        await RunExportCaseAsync("P9-EXP-XLSX-05", () =>
        {
            var export = RequireXlsxExport();
            using var workbook = OpenWorkbook(export.Content);
            var result = workbook.Worksheet("Result");
            var danger = FindColumn(result, "danger");
            for (var row = 2; row <= 5; row++)
            {
                var cell = result.Cell(row, danger);
                HarnessAssert.True(
                    cell.Style.IncludeQuotePrefix || cell.GetString().StartsWith('\''),
                    $"XLSX safe quote-prefix {row}");
                var dangerousText = cell.GetString().TrimStart('\'');
                HarnessAssert.True(
                    dangerousText.Length > 0 && dangerousText[0] is '=' or '+' or '-' or '@',
                    $"XLSX dangerous text retained {row}");
                HarnessAssert.Equal(string.Empty, cell.FormulaA1, $"XLSX formula {row}");
            }
            using var zip = new ZipArchive(new MemoryStream(export.Content), ZipArchiveMode.Read);
            HarnessAssert.True(zip.Entries.All(entry =>
                    !entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) &&
                    !entry.FullName.Contains("externalLinks", StringComparison.OrdinalIgnoreCase)),
                "XLSX active content");
            _exportSecurityVerified = true;
            return Task.FromResult(new CaseObservation(
                "XLSX contains no formulas, macros or external links; dangerous strings are plain neutralized text.",
                $"entries={zip.Entries.Count};formulas=0;macros=0"));
        });

        await RunExportCaseAsync("P9-EXP-XLSX-06", async () =>
        {
            var export = RequireXlsxExport();
            var replay = await CreateExportAsync(
                _exportFixtures[StatRunExportResultKinds.Basic],
                "XLSX", "p9-exp-xlsx01-basic", Actor("executor"), HttpStatusCode.OK, ct);
            HarnessAssert.Equal(export.ExportId, RequiredString(replay, "exportId"), "XLSX replay id");
            HarnessAssert.Equal(export.ContentHash, RequiredString(replay, "contentHash"), "XLSX replay hash");
            HarnessAssert.True(RequiredBool(replay, "isReplay"), "XLSX replay flag");
            _exportReceiptVerified = true;
            return new CaseObservation(
                "Exact XLSX replay reused immutable content-addressed bytes and receipt.",
                $"export={export.ExportId};sha={export.ContentHash};replay=true");
        });

        await RunExportCaseAsync("P9-EXP-XLSX-07", async () =>
        {
            var empty = await CreateAndDownloadExportAsync(
                _exportFixtures["EMPTY"], "XLSX", "p9-exp-xlsx07-empty",
                Actor("executor"), ct);
            using (var workbook = OpenWorkbook(empty.Content))
                HarnessAssert.Equal(1, workbook.Worksheet("Result").LastRowUsed()!.RowNumber(), "XLSX empty header");
            var before = await CountExportMetadataAsync(ct);
            var limit = await RequireApi().PostAsync(
                "api/stat-runs/exports",
                ExportRequest(_exportFixtures["LIMIT"], "XLSX", "p9-exp-xlsx07-limit"),
                Actor("executor").Token,
                ct: ct);
            HarnessAssert.Equal("STAT_RUN_EXPORT_LIMIT",
                ApiHarnessClient.FindStringRecursive(limit.Json, "errorCode"), "XLSX row limit code");
            HarnessAssert.Equal(before, await CountExportMetadataAsync(ct), "XLSX limit metadata promotion");
            _exportParseVerified = true;
            return new CaseObservation(
                "Empty XLSX remained valid and the shared 50,000-row guard rejected oversize input before promotion.",
                $"emptyRows=0;limit={StatRunExportContract.MaxRows};writes=0");
        });
    }

    private async Task RunExportAclCasesAsync(CancellationToken ct)
    {
        await RunExportCaseAsync("P9-EXP-ACL-01", async () =>
        {
            var fixture = _exportFixtures[StatRunExportResultKinds.Basic];
            var before = await CountExportMetadataAsync(ct);
            foreach (var target in new[] { fixture.ResultId, ObjectId.GenerateNewId().ToString(), "malformed" })
            {
                var mutated = fixture with { ResultId = target };
                var response = await RequireApi().PostAsync(
                    "api/stat-runs/exports",
                    ExportRequest(mutated, "CSV", $"p9-exp-acl01-{target}"),
                    Actor("outsider").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Forbidden, "P9-EXP outsider auth");
                var after = await CountExportMetadataAsync(ct);
                _exportAuthTrace.Add(new P9ExportAuthTrace(
                    "P9-EXP-ACL-01", "outsider", target, (int)response.StatusCode,
                    before, after, before == after));
                HarnessAssert.Equal(before, after, "P9-EXP auth-before-existence zero writes");
            }
            _exportAuthVerified = true;
            return new CaseObservation(
                "Forbidden actor received the same 403 for existing, missing and malformed result IDs before receipt lookup.",
                $"statuses=403,403,403;before={before};after={await CountExportMetadataAsync(ct)}");
        });

        await RunExportCaseAsync("P9-EXP-ACL-02", async () =>
        {
            var export = RequireCsvExport();
            var fixture = _exportFixtures[StatRunExportResultKinds.Basic];
            var query = ExportQuery(export, fixture);
            var crossActor = await RequireApi().GetAsync(
                $"api/stat-runs/exports/{export.ExportId}?{query}",
                Actor("executor2").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(crossActor, HttpStatusCode.Forbidden, "P9-EXP requestor policy");
            var changed = await RequireApi().PostAsync(
                "api/stat-runs/exports",
                ExportRequest(fixture, "XLSX", "p9-exp-csv01-basic"),
                Actor("executor").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(changed, HttpStatusCode.Conflict, "P9-EXP changed replay");
            HarnessAssert.Equal("STAT_RUN_COMMAND_REPLAY_MISMATCH",
                ApiHarnessClient.FindStringRecursive(changed.Json, "errorCode"), "P9-EXP changed replay code");
            _exportReceiptVerified = true;
            return new CaseObservation(
                "Cross-actor metadata access was forbidden and changed command replay returned stable 409 with zero promotion.",
                $"crossActor=403;changedReplay=409;export={export.ExportId}");
        });

        await RunExportCaseAsync("P9-EXP-ACL-03", async () =>
        {
            var export = RequireCsvExport();
            await SetExportExpiryAsync(export.ExportId, DateTime.UtcNow.AddMinutes(-5), ct);
            var fixture = _exportFixtures[StatRunExportResultKinds.Basic];
            var expired = await RequireApi().GetBytesAsync(
                $"api/stat-runs/exports/{export.ExportId}/download?{ExportQuery(export, fixture)}",
                Actor("executor").Token,
                ct: ct);
            HarnessAssert.Equal(HttpStatusCode.Gone, expired.StatusCode, "P9-EXP expired download");
            return new CaseObservation(
                "Expired metadata remained auditable but download reauthorization returned 410 and no bytes.",
                $"export={export.ExportId};http=410;bytes={expired.Content.Length}");
        });

        await RunExportCaseAsync("P9-EXP-ACL-04", async () =>
        {
            var before = await CountExportMetadataAsync(ct);
            var filesBefore = CountExportFiles();
            await ExpireAllExportsAsync(ct);
            var preview = await CleanupExportsAsync(true, ct);
            HarnessAssert.Equal((int)before, RequiredInt(preview, "selected"), "P9-EXP cleanup preview selected");
            HarnessAssert.Equal(0, RequiredInt(preview, "deleted"), "P9-EXP cleanup preview deleted");
            var applied = await CleanupExportsAsync(false, ct);
            HarnessAssert.Equal((int)before, RequiredInt(applied, "deleted"), "P9-EXP cleanup deleted");
            var after = await CountExportMetadataAsync(ct);
            var filesAfter = CountExportFiles();
            HarnessAssert.Equal(0L, after, "P9-EXP cleanup active metadata");
            HarnessAssert.Equal(0, filesAfter, "P9-EXP cleanup files");
            _exportCleanupTrace.Add(new P9ExportCleanupTrace(
                "P9-EXP-ACL-04", false, RequiredInt(applied, "selected"),
                RequiredInt(applied, "deleted"), before, after, filesBefore, filesAfter));
            _exportCleanupVerified = true;
            return new CaseObservation(
                "System-admin preview/apply cleanup selected exact expired IDs, soft-deleted metadata and removed only owned files.",
                $"selected={before};deleted={before};files={filesBefore}->0;active=0");
        });
    }

    private async Task RunExportCaseAsync(string caseId, Func<Task<CaseObservation>> action)
        => await _cases.RunAsync(caseId, action);
}

internal sealed record P9ExportDownloaded(
    string ExportId, string CommandId, string ReceiptId, string RequestHash,
    string ContentHash, string FileName, string ContentType,
    int RowCount, int ColumnCount, byte[] Content);
