using System.IO.Compression;
using System.Net;
using System.Text;
using ClosedXML.Excel;
using MongoDB.Bson;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunRaceSecurityCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-RACE-SECURITY-01", async () =>
        {
            var jobId = RequireRaceValue(_raceSecurityJobId, "security job id");
            var before = await CaptureDatabaseSnapshotAsync(ct);
            var known = await RequireApi().GetAsync(
                $"api/stat-runs/jobs/{jobId}",
                Actor("outsider").Token,
                ct: ct);
            var unknown = await RequireApi().GetAsync(
                $"api/stat-runs/jobs/{ObjectId.GenerateNewId()}",
                Actor("outsider").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(known, HttpStatusCode.Forbidden,
                "P9-RACE outsider known job");
            ApiHarnessClient.ExpectStatus(unknown, HttpStatusCode.Forbidden,
                "P9-RACE outsider unknown job");
            var knownSemantic = UiErrorBodyWithoutTrace(known.Body);
            var unknownSemantic = UiErrorBodyWithoutTrace(unknown.Body);
            HarnessAssert.Equal(knownSemantic, unknownSemantic,
                "P9-RACE job auth-before-existence body");
            var after = await CaptureDatabaseSnapshotAsync(ct);
            HarnessAssert.Equal(SnapshotSha256(before), SnapshotSha256(after),
                "P9-RACE job auth zero-write snapshot");
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-SECURITY-01", "JOB_AUTH_BEFORE_EXISTENCE",
                [(int)known.StatusCode, (int)unknown.StatusCode],
                "knownUnknownEqual=true;databaseDelta=0;identifierLeak=false"));
            return new CaseObservation(
                "An outsider received the same semantic 403 for known and unknown jobs before existence disclosure or writes.",
                "known=403;unknown=403;semanticEqual=true;databaseDelta=0");
        }, ct);

        await RunCaseAsync("P9-RACE-SECURITY-02", async () =>
        {
            var jobId = RequireRaceValue(_raceSecurityJobId, "security job id");
            var before = await CaptureDatabaseSnapshotAsync(ct);
            var known = await RequireApi().GetAsync(
                $"api/operations/jobs/{jobId}",
                Actor("insufficient").Token,
                ct: ct);
            var unknown = await RequireApi().GetAsync(
                $"api/operations/jobs/{ObjectId.GenerateNewId()}",
                Actor("insufficient").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(known, HttpStatusCode.Forbidden,
                "P9-RACE non-operator known job");
            ApiHarnessClient.ExpectStatus(unknown, HttpStatusCode.Forbidden,
                "P9-RACE non-operator unknown job");
            HarnessAssert.Equal(
                UiErrorBodyWithoutTrace(known.Body),
                UiErrorBodyWithoutTrace(unknown.Body),
                "P9-RACE operations auth-before-existence body");
            var mutation = await RequireApi().PostAsync(
                $"api/operations/jobs/{jobId}/retry",
                new
                {
                    commandId = "p9-race-forbidden-retry-002",
                    expectedStateRevision = 0,
                    expectedStateHash = new string('0', 64)
                },
                Actor("insufficient").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(mutation, HttpStatusCode.Forbidden,
                "P9-RACE non-operator mutation");
            var after = await CaptureDatabaseSnapshotAsync(ct);
            HarnessAssert.Equal(SnapshotSha256(before), SnapshotSha256(after),
                "P9-RACE operations auth zero-write snapshot");
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-SECURITY-02", "OPERATIONS_AUTH_BEFORE_PARSE_EXISTENCE",
                [(int)known.StatusCode, (int)unknown.StatusCode, (int)mutation.StatusCode],
                "knownUnknownEqual=true;mutationForbiddenBeforeCas=true;databaseDelta=0"));
            return new CaseObservation(
                "A non-operator was denied known/unknown operations reads and a malformed stale mutation before parse, existence, or writes.",
                "known=403;unknown=403;mutation=403;semanticEqual=true;databaseDelta=0");
        }, ct);

        await RunCaseAsync("P9-RACE-SECURITY-03", async () =>
        {
            var fixture = _exportFixtures[StatRunExportResultKinds.Basic];
            var before = await CountExportMetadataAsync(ct);
            _raceCsvExport = await CreateAndDownloadExportAsync(
                fixture,
                "CSV",
                "p9-race-security-csv-003",
                Actor("executor"),
                ct);
            HarnessAssert.Equal(before + 1, await CountExportMetadataAsync(ct),
                "P9-RACE export metadata promotion");
            var query = ExportQuery(_raceCsvExport, fixture);
            var unknownId = ObjectId.GenerateNewId().ToString();
            var known = await RequireApi().GetAsync(
                $"api/stat-runs/exports/{_raceCsvExport.ExportId}?{query}",
                Actor("outsider").Token,
                ct: ct);
            var unknown = await RequireApi().GetAsync(
                $"api/stat-runs/exports/{unknownId}?{query}",
                Actor("outsider").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(known, HttpStatusCode.Forbidden,
                "P9-RACE outsider known export");
            ApiHarnessClient.ExpectStatus(unknown, HttpStatusCode.Forbidden,
                "P9-RACE outsider unknown export");
            HarnessAssert.Equal(
                UiErrorBodyWithoutTrace(known.Body),
                UiErrorBodyWithoutTrace(unknown.Body),
                "P9-RACE export auth-before-existence body");
            var download = await RequireApi().GetBytesAsync(
                $"api/stat-runs/exports/{_raceCsvExport.ExportId}/download?{query}",
                Actor("outsider").Token,
                ct: ct);
            HarnessAssert.Equal(HttpStatusCode.Forbidden, download.StatusCode,
                "P9-RACE cross-actor export download");
            HarnessAssert.Equal(before + 1, await CountExportMetadataAsync(ct),
                "P9-RACE export denial metadata delta");
            _raceAuthVerified = true;
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-SECURITY-03", "EXPORT_OWNERSHIP_AUTH",
                [(int)known.StatusCode, (int)unknown.StatusCode, (int)download.StatusCode],
                "knownUnknownEqual=true;crossActorDownloadDenied=true;denialWriteDelta=0"));
            return new CaseObservation(
                "Export metadata and download ownership were reauthorized server-side; known and unknown targets were indistinguishable to an outsider.",
                "known=403;unknown=403;download=403;semanticEqual=true;denialWriteDelta=0");
        }, ct);

        await RunCaseAsync("P9-RACE-SECURITY-04", async () =>
        {
            var csv = _raceCsvExport
                      ?? throw new HarnessCaseNotRunnableException(
                          "P9-RACE CSV export is unavailable.");
            var csvRows = ParseCsv(csv.Content);
            HarnessAssert.Equal(
                "Vi\u1ec7t Nam, \"d\u00f2ng\"\r\nti\u1ebfp", Column(csvRows, 0, "code"),
                "P9-RACE CSV UTF-8 and quoted newline round-trip");
            for (var index = 0; index < 4; index++)
            {
                HarnessAssert.True(
                    Column(csvRows, index, "danger").StartsWith('\''),
                    $"P9-RACE CSV formula neutralization {index}");
            }

            var fixture = _exportFixtures[StatRunExportResultKinds.Basic];
            _raceXlsxExport = await CreateAndDownloadExportAsync(
                fixture,
                "XLSX",
                "p9-race-security-xlsx-004",
                Actor("executor"),
                ct);
            using (var workbook = OpenWorkbook(_raceXlsxExport.Content))
            {
                var result = workbook.Worksheet("Result");
                var dangerColumn = FindColumn(result, "danger");
                for (var row = 2; row <= 5; row++)
                {
                    var cell = result.Cell(row, dangerColumn);
                    HarnessAssert.Equal(string.Empty, cell.FormulaA1,
                        $"P9-RACE XLSX formula {row}");
                    HarnessAssert.True(
                        cell.Style.IncludeQuotePrefix || cell.GetString().StartsWith('\''),
                        $"P9-RACE XLSX quote prefix {row}");
                }
            }
            using (var zip = new ZipArchive(
                       new MemoryStream(_raceXlsxExport.Content),
                       ZipArchiveMode.Read))
            {
                HarnessAssert.True(zip.Entries.All(entry =>
                        !entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) &&
                        !entry.FullName.Contains("externalLinks", StringComparison.OrdinalIgnoreCase)),
                    "P9-RACE XLSX active content");
            }
            _raceExports.Add(new P9RaceExportEvidence(
                "P9-RACE-SECURITY-04", "CSV",
                csv.RowCount, csv.ColumnCount, csv.Content.Length,
                csv.ContentHash, true, true));
            _raceExports.Add(new P9RaceExportEvidence(
                "P9-RACE-SECURITY-04", "XLSX",
                _raceXlsxExport.RowCount,
                _raceXlsxExport.ColumnCount,
                _raceXlsxExport.Content.Length,
                _raceXlsxExport.ContentHash,
                true,
                true));
            _raceExportVerified = true;
            return new CaseObservation(
                "Independent CSV and XLSX parsers confirmed =,+,-,@ values stayed text with no formula, macro, or external-link execution surface.",
                "formats=CSV+XLSX;dangerPrefixes=4;formulas=0;macros=0;externalLinks=0");
        }, ct);

        await RunCaseAsync("P9-RACE-SECURITY-05", async () =>
        {
            var beforeMetadata = await CountExportMetadataAsync(ct);
            var beforeFiles = CountExportFiles();
            var response = await RequireApi().PostAsync(
                "api/stat-runs/exports",
                ExportRequest(
                    _exportFixtures["RACE_LIMIT"],
                    "CSV",
                    "p9-race-export-limit-005"),
                Actor("executor").Token,
                ct: ct);
            HarnessAssert.True(
                (int)response.StatusCode is >= 400 and < 500,
                "P9-RACE oversized export status");
            HarnessAssert.Equal(
                "STAT_RUN_EXPORT_LIMIT",
                ApiHarnessClient.FindStringRecursive(response.Json, "errorCode"),
                "P9-RACE oversized export code");
            HarnessAssert.Equal(beforeMetadata, await CountExportMetadataAsync(ct),
                "P9-RACE oversized export metadata delta");
            HarnessAssert.Equal(beforeFiles, CountExportFiles(),
                "P9-RACE oversized export file delta");

            var logFiles = Directory.EnumerateFiles(
                    _iterationRoot, "*.log", SearchOption.AllDirectories)
                .ToArray();
            var leaked = new List<string>();
            foreach (var file in logFiles)
            {
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(file, ct);
                }
                catch (IOException)
                {
                    continue;
                }
                if (_artifactSecrets.Any(secret =>
                        text.Contains(secret, StringComparison.Ordinal)))
                {
                    leaked.Add(Path.GetRelativePath(_paths.WorkspaceRoot, file)
                        .Replace(Path.DirectorySeparatorChar, '/'));
                }
            }
            HarnessAssert.Equal(0, leaked.Count,
                "P9-RACE exact runtime secret log findings");
            _raceSecurityBoundaryVerified = true;
            return new CaseObservation(
                "The 50,001-row guard rejected before metadata/file promotion, and all owned process logs were free of exact runtime secrets.",
                $"limit={StatRunExportContract.MaxRows};http=4xx;metadataDelta=0;fileDelta=0;logs={logFiles.Length};secretFindings=0");
        }, ct);
    }
}
