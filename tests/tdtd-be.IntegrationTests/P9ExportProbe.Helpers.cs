using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using MongoDB.Driver;
using tdtd_be.Models.Statistics;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private JsonObject ExportRequest(
        P9ExportCanonicalFixture fixture,
        string format,
        string commandId)
    {
        var filters = new JsonObject();
        if (fixture.ResultKind.StartsWith("DIRECT_", StringComparison.Ordinal))
            filters["dynamicFormTemplateId"] = Fixture().TemplateId;
        return new JsonObject
        {
            ["commandId"] = commandId,
            ["format"] = format,
            ["resultKind"] = fixture.ResultKind,
            ["workId"] = fixture.WorkId,
            ["scopeType"] = fixture.ScopeType,
            ["scopeId"] = fixture.ScopeId,
            ["periodInstanceKey"] = fixture.PeriodInstanceKey,
            ["resultId"] = fixture.ResultId,
            ["expectedResultHash"] = fixture.ResultHash,
            ["expectedConfigHash"] = fixture.ConfigHash,
            ["expectedSourceHash"] = fixture.SourceHash,
            ["expectedLifecycleRevision"] = fixture.LifecycleRevision,
            ["filters"] = filters
        };
    }

    private async Task<JsonObject> CreateExportAsync(
        P9ExportCanonicalFixture fixture,
        string format,
        string commandId,
        P9Actor actor,
        HttpStatusCode expectedStatus,
        CancellationToken ct)
    {
        var response = await RequireApi().PostAsync(
            "api/stat-runs/exports",
            ExportRequest(fixture, format, commandId),
            actor.Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, expectedStatus, $"P9-EXP create {format}/{fixture.ResultKind}");
        var root = ApiHarnessClient.RequiredObject(response.Json, "P9-EXP create response");
        var trace = new P9ExportReceiptTrace(
            commandId,
            RequiredString(root, "exportId"),
            RequiredString(root, "requestHash"),
            RequiredString(root, "receiptId"),
            RequiredBool(root, "isReplay"),
            RequiredString(root, "contentHash"));
        _exportReceipts[commandId] = trace;
        return root;
    }

    private async Task<P9ExportDownloaded> CreateAndDownloadExportAsync(
        P9ExportCanonicalFixture fixture,
        string format,
        string commandId,
        P9Actor actor,
        CancellationToken ct)
    {
        var created = await CreateExportAsync(
            fixture, format, commandId, actor, HttpStatusCode.Created, ct);
        var exportId = RequiredString(created, "exportId");
        var response = await RequireApi().GetBytesAsync(
            $"api/stat-runs/exports/{exportId}/download?{ExportQuery(exportId, fixture)}",
            actor.Token,
            ct: ct);
        HarnessAssert.Equal(HttpStatusCode.OK, response.StatusCode, "P9-EXP download status");
        var contentHash = RequiredString(created, "contentHash");
        var actualHash = Convert.ToHexString(SHA256.HashData(response.Content)).ToLowerInvariant();
        HarnessAssert.Equal(contentHash, actualHash, "P9-EXP downloaded content hash");
        HarnessAssert.Equal(contentHash, response.Header("X-Content-SHA256"), "P9-EXP hash header");
        var downloaded = new P9ExportDownloaded(
            exportId,
            commandId,
            RequiredString(created, "receiptId"),
            RequiredString(created, "requestHash"),
            contentHash,
            RequiredString(created, "fileName"),
            RequiredString(created, "contentType"),
            RequiredInt(created, "rowCount"),
            RequiredInt(created, "columnCount"),
            response.Content);
        _exportParseTrace.Add(new P9ExportParseTrace(
            HarnessCaseRunner.ActiveCaseId ?? "SETUP",
            format,
            exportId,
            downloaded.RowCount,
            downloaded.ColumnCount,
            downloaded.ContentHash,
            $"kind={fixture.ResultKind};bytes={downloaded.Content.Length}"));
        return downloaded;
    }

    private static string ExportQuery(
        P9ExportDownloaded export,
        P9ExportCanonicalFixture fixture)
        => ExportQuery(export.ExportId, fixture);

    private static string ExportQuery(
        string exportId,
        P9ExportCanonicalFixture fixture)
        => string.Join("&", new[]
        {
            $"workId={Uri.EscapeDataString(fixture.WorkId)}",
            $"scopeType={Uri.EscapeDataString(fixture.ScopeType)}",
            $"scopeId={Uri.EscapeDataString(fixture.ScopeId)}",
            $"capabilityId={Uri.EscapeDataString(fixture.CapabilityId)}"
        });

    private static List<List<string>> ParseCsv(byte[] content)
    {
        var offset = content.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        var text = Encoding.UTF8.GetString(content, offset, content.Length - offset);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }
                continue;
            }
            if (character == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<string>();
                index++;
            }
            else
            {
                field.Append(character);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        HarnessAssert.True(!quoted, "CSV parser ended inside quoted field");
        return rows;
    }

    private static string Column(IReadOnlyList<List<string>> table, int dataRow, string header)
    {
        var column = table[0].FindIndex(value => string.Equals(value, header, StringComparison.Ordinal));
        HarnessAssert.True(column >= 0, $"CSV header missing: {header}");
        HarnessAssert.True(dataRow + 1 < table.Count, $"CSV data row missing: {dataRow}");
        return table[dataRow + 1][column];
    }

    private static XLWorkbook OpenWorkbook(byte[] content)
        => new(new MemoryStream(content));

    private static int FindColumn(IXLWorksheet worksheet, string header)
    {
        var last = worksheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (var column = 1; column <= last; column++)
        {
            if (string.Equals(worksheet.Cell(1, column).GetString(), header, StringComparison.Ordinal))
                return column;
        }
        throw new InvalidOperationException($"XLSX header missing: {header}");
    }

    private P9ExportDownloaded RequireCsvExport()
        => _csvExport ?? throw new HarnessCaseNotRunnableException("Missing prerequisite: CSV export.");

    private P9ExportDownloaded RequireXlsxExport()
        => _xlsxExport ?? throw new HarnessCaseNotRunnableException("Missing prerequisite: XLSX export.");

    private async Task<long> CountExportMetadataAsync(CancellationToken ct)
    {
        var total = 0L;
        foreach (var collection in ExportMetadataCollections())
            total += await collection.CountDocumentsAsync(item => !item.IsDeleted, cancellationToken: ct);
        return total;
    }

    private IEnumerable<IMongoCollection<StatRunExportArtifact>> ExportMetadataCollections()
    {
        yield return RequireDatabase().GetCollection<StatRunExportArtifact>("work_report_statistic_exports");
        yield return RequireDatabase().GetCollection<StatRunExportArtifact>("work_report_statistic_diff_exports");
    }

    private async Task SetExportExpiryAsync(string exportId, DateTime expiresAtUtc, CancellationToken ct)
    {
        foreach (var collection in ExportMetadataCollections())
        {
            await collection.UpdateOneAsync(
                item => item.Id == exportId && !item.IsDeleted,
                Builders<StatRunExportArtifact>.Update.Set(item => item.ExpiresAtUtc, expiresAtUtc),
                cancellationToken: ct);
        }
    }

    private async Task ExpireAllExportsAsync(CancellationToken ct)
    {
        foreach (var collection in ExportMetadataCollections())
        {
            await collection.UpdateManyAsync(
                item => !item.IsDeleted,
                Builders<StatRunExportArtifact>.Update.Set(
                    item => item.ExpiresAtUtc,
                    DateTime.UtcNow.AddMinutes(-1)),
                cancellationToken: ct);
        }
    }

    private async Task<JsonObject> CleanupExportsAsync(bool dryRun, CancellationToken ct)
    {
        var response = await RequireApi().PostAsync(
            "api/operations/stat-runs/exports/cleanup",
            new JsonObject
            {
                ["limit"] = 1000,
                ["dryRun"] = dryRun,
                ["expiredBeforeUtc"] = DateTime.UtcNow
            },
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, "P9-EXP cleanup");
        return ApiHarnessClient.RequiredObject(response.Json, "P9-EXP cleanup response");
    }

    private int CountExportFiles()
    {
        var root = Path.Combine(
            _paths.WorkspaceRoot,
            "tdtd-be",
            ".build",
            "stat-run-exports",
            RequireMongo().DatabaseName);
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

    private static int RequiredInt(JsonNode? node, string property)
        => ApiHarnessClient.RequiredInt(node, property);
}
