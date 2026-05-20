using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignmentReports.Payloads;

public sealed class WorkReportPayloadService : IWorkReportPayloadReader, IWorkReportPayloadWriter
{
    private const int PayloadTargetBytes = 4 * 1024 * 1024;
    private const int TableBlockTargetBytes = 4 * 1024 * 1024;
    private const int MongoHardGuardBytes = 12 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly MongoDbContext _ctx;

    public WorkReportPayloadService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<WorkReportPayloadSnapshot> LoadReportPayloadAsync(
        WorkAssignmentReport report,
        CancellationToken ct = default)
    {
        var external = await TryLoadExternalPayloadAsync(report, ct);
        if (external is not null)
            return external;

        return BuildEmbeddedSnapshot(report);
    }

    public async Task<string?> LoadReportTableBlockAsync(
        WorkAssignmentReport report,
        string blockId,
        CancellationToken ct = default)
    {
        var normalizedBlockId = NormalizeBlockId(blockId);
        if (CanReadExternalPayload(report))
        {
            var block = await _ctx.WorkReportTableValues
                .Find(x =>
                    x.ReportId == report.Id &&
                    x.PayloadRevision == report.PayloadRevision &&
                    x.BlockId == normalizedBlockId &&
                    x.Status == WorkReportPayloadStatus.Ready &&
                    !x.IsDeleted)
                .FirstOrDefaultAsync(ct);

            if (block is not null)
                return block.ValuesJson;
        }

        return TryExtractEmbeddedTableBlock(report.TableValuesJson, normalizedBlockId);
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadReportTableBlocksAsync(
        WorkAssignmentReport report,
        IEnumerable<string> blockIds,
        CancellationToken ct = default)
    {
        var normalizedBlockIds = blockIds
            .Select(NormalizeBlockId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalizedBlockIds.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        if (CanReadExternalPayload(report))
        {
            var rows = await _ctx.WorkReportTableValues
                .Find(x =>
                    x.ReportId == report.Id &&
                    x.PayloadRevision == report.PayloadRevision &&
                    normalizedBlockIds.Contains(x.BlockId) &&
                    x.Status == WorkReportPayloadStatus.Ready &&
                    !x.IsDeleted)
                .ToListAsync(ct);

            if (rows.Count > 0)
            {
                return rows
                    .GroupBy(x => x.BlockId, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.First().ValuesJson, StringComparer.Ordinal);
            }
        }

        return TryExtractEmbeddedTableBlocks(report.TableValuesJson, normalizedBlockIds);
    }

    public async Task<WorkReportPayloadWriteResult> SaveReportPayloadAsync(
        WorkAssignmentReport report,
        string values1DJson,
        string? fieldValuesJson,
        string? tableValuesJson,
        string? summarySourceJson,
        string? actorUserId,
        DateTime now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(report.Id))
            report.Id = ObjectId.GenerateNewId().ToString();

        var revision = Math.Max(0, report.PayloadRevision) + 1;
        var tableParts = SplitTableValues(tableValuesJson);
        var payloadHash = WorkReportPayloadHash.Compute(
            values1DJson,
            fieldValuesJson,
            tableParts.RootJson,
            summarySourceJson,
            tableParts.Blocks.Select(x => new WorkReportPayloadBlockHash(x.BlockId, x.BlockOrder, x.PayloadHash)));
        var payloadSizeBytes = Utf8Size(values1DJson)
            + Utf8Size(fieldValuesJson)
            + Utf8Size(tableParts.RootJson)
            + Utf8Size(summarySourceJson);

        GuardPayloadSize("reportPayload", report.Id, payloadSizeBytes, PayloadTargetBytes);

        var existingPayloadId = await _ctx.WorkReportPayloads
            .Find(x => x.ReportId == report.Id && !x.IsDeleted)
            .Project(x => x.Id)
            .FirstOrDefaultAsync(ct);

        var payload = new WorkReportPayload
        {
            Id = string.IsNullOrWhiteSpace(existingPayloadId) ? ObjectId.GenerateNewId().ToString() : existingPayloadId,
            ReportId = report.Id,
            PayloadRevision = revision,
            Values1DJson = values1DJson,
            FieldValuesJson = fieldValuesJson,
            TableValuesRootJson = tableParts.RootJson,
            SummarySourceJson = summarySourceJson,
            PayloadHash = payloadHash,
            PayloadSizeBytes = payloadSizeBytes,
            Status = WorkReportPayloadStatus.Ready,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actorUserId,
            UpdatedByUserId = actorUserId,
            IsDeleted = false
        };

        await _ctx.WorkReportPayloads.ReplaceOneAsync(
            x => x.ReportId == report.Id && !x.IsDeleted,
            payload,
            new ReplaceOptions { IsUpsert = true },
            ct);

        await SaveTableBlocksAsync(report.Id, revision, tableParts.Blocks, actorUserId, now, ct);

        return new WorkReportPayloadWriteResult(
            revision,
            payloadHash,
            payloadSizeBytes + tableParts.Blocks.Sum(x => x.SizeBytes),
            WorkReportPayloadStatus.Ready);
    }

    private async Task SaveTableBlocksAsync(
        string reportId,
        int revision,
        IReadOnlyList<TableBlockPayload> blocks,
        string? actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        var existingIds = await _ctx.WorkReportTableValues
            .Find(x => x.ReportId == reportId && !x.IsDeleted)
            .Project(x => new { x.Id, x.BlockId })
            .ToListAsync(ct);
        var existingByBlockId = existingIds
            .GroupBy(x => x.BlockId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.Ordinal);

        foreach (var block in blocks)
        {
            GuardPayloadSize("reportTableBlock", $"{reportId}:{block.BlockId}", block.SizeBytes, TableBlockTargetBytes);
            existingByBlockId.TryGetValue(block.BlockId, out var existingId);
            var row = new WorkReportTableValue
            {
                Id = string.IsNullOrWhiteSpace(existingId) ? ObjectId.GenerateNewId().ToString() : existingId,
                ReportId = reportId,
                BlockId = block.BlockId,
                PayloadRevision = revision,
                BlockOrder = block.BlockOrder,
                TableMode = block.TableMode,
                ValuesJson = block.ValuesJson,
                RowCount = block.RowCount,
                ColumnCount = block.ColumnCount,
                SizeBytes = block.SizeBytes,
                PayloadHash = block.PayloadHash,
                Status = WorkReportPayloadStatus.Ready,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedByUserId = actorUserId,
                UpdatedByUserId = actorUserId,
                IsDeleted = false
            };

            await _ctx.WorkReportTableValues.ReplaceOneAsync(
                x => x.ReportId == reportId && x.BlockId == block.BlockId && !x.IsDeleted,
                row,
                new ReplaceOptions { IsUpsert = true },
                ct);
        }

        var fb = Builders<WorkReportTableValue>.Filter;
        var staleFilter = fb.Eq(x => x.ReportId, reportId) & fb.Eq(x => x.IsDeleted, false);
        var currentBlockIds = blocks.Select(x => x.BlockId).ToList();
        if (currentBlockIds.Count > 0)
            staleFilter &= fb.Nin(x => x.BlockId, currentBlockIds);

        await _ctx.WorkReportTableValues.UpdateManyAsync(
            staleFilter,
            Builders<WorkReportTableValue>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.DeletedAtUtc, now)
                .Set(x => x.DeletedByUserId, actorUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);
    }

    private async Task<WorkReportPayloadSnapshot?> TryLoadExternalPayloadAsync(
        WorkAssignmentReport report,
        CancellationToken ct)
    {
        if (!CanReadExternalPayload(report))
            return null;

        var payload = await _ctx.WorkReportPayloads
            .Find(x =>
                x.ReportId == report.Id &&
                x.PayloadRevision == report.PayloadRevision &&
                x.Status == WorkReportPayloadStatus.Ready &&
                !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (payload is null)
            return null;

        var blocks = await _ctx.WorkReportTableValues
            .Find(x =>
                x.ReportId == report.Id &&
                x.PayloadRevision == report.PayloadRevision &&
                x.Status == WorkReportPayloadStatus.Ready &&
                !x.IsDeleted)
            .SortBy(x => x.BlockOrder)
            .ThenBy(x => x.BlockId)
            .ToListAsync(ct);

        var actualPayloadHash = WorkReportPayloadHash.Compute(
            payload.Values1DJson,
            payload.FieldValuesJson,
            payload.TableValuesRootJson,
            payload.SummarySourceJson,
            blocks.Select(x => new WorkReportPayloadBlockHash(x.BlockId, x.BlockOrder, x.PayloadHash)));

        return new WorkReportPayloadSnapshot(
            payload.Values1DJson,
            payload.FieldValuesJson,
            RebuildTableValuesJson(payload.TableValuesRootJson, blocks),
            payload.SummarySourceJson,
            payload.PayloadRevision,
            payload.PayloadHash,
            payload.PayloadSizeBytes + blocks.Sum(x => x.SizeBytes),
            payload.Status,
            IsExternalPayload: true,
            PayloadHashVerified: string.Equals(actualPayloadHash, payload.PayloadHash, StringComparison.Ordinal));
    }

    private static WorkReportPayloadSnapshot BuildEmbeddedSnapshot(WorkAssignmentReport report)
        => new(
            string.IsNullOrWhiteSpace(report.Values1DJson) ? "[]" : report.Values1DJson,
            report.FieldValuesJson,
            report.TableValuesJson,
            report.SummarySourceJson,
            report.PayloadRevision,
            report.PayloadHash,
            report.PayloadSizeBytes,
            report.PayloadStatus,
            IsExternalPayload: false,
            PayloadHashVerified: true);

    private static bool CanReadExternalPayload(WorkAssignmentReport report)
        => !string.IsNullOrWhiteSpace(report.Id)
           && report.PayloadRevision > 0
           && string.Equals(report.PayloadStatus, WorkReportPayloadStatus.Ready, StringComparison.Ordinal);

    private static TablePayloadParts SplitTableValues(string? tableValuesJson)
    {
        if (string.IsNullOrWhiteSpace(tableValuesJson))
            return new TablePayloadParts(null, Array.Empty<TableBlockPayload>());

        var root = ParseTableValuesRoot(tableValuesJson);
        if (root is null)
            return new TablePayloadParts(tableValuesJson, Array.Empty<TableBlockPayload>());

        var blocks = new List<TableBlockPayload>();
        if (root.TryGetPropertyValue("blocks", out var blocksNode) &&
            blocksNode is JsonArray blocksArray)
        {
            var order = 0;
            foreach (var blockNode in blocksArray)
            {
                if (blockNode is not JsonObject blockObject)
                    continue;

                var blockId = NormalizeBlockId(ReadString(blockObject, "blockId"));
                var valuesJson = blockObject.ToJsonString(JsonOptions);
                blocks.Add(new TableBlockPayload(
                    blockId,
                    order++,
                    NormalizeTableMode(ReadString(blockObject, "tableMode")),
                    valuesJson,
                    ResolveRowCount(blockObject),
                    ResolveColumnCount(blockObject),
                    Utf8Size(valuesJson),
                    Sha256Hex(valuesJson)));
            }

            root["blocks"] = new JsonArray();
        }

        return new TablePayloadParts(root.ToJsonString(JsonOptions), blocks);
    }

    private static JsonObject? ParseTableValuesRoot(string tableValuesJson)
    {
        try
        {
            return JsonNode.Parse(tableValuesJson) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new { field = "tableValuesJson", reason = ex.Message },
                "tableValuesJson must be a JSON object.");
        }
    }

    private static string? RebuildTableValuesJson(
        string? rootJson,
        IReadOnlyList<WorkReportTableValue> blocks)
    {
        if (string.IsNullOrWhiteSpace(rootJson) && blocks.Count == 0)
            return null;

        var root = string.IsNullOrWhiteSpace(rootJson)
            ? new JsonObject()
            : JsonNode.Parse(rootJson) as JsonObject ?? new JsonObject();

        var blockArray = new JsonArray();
        foreach (var block in blocks)
        {
            var blockNode = JsonNode.Parse(block.ValuesJson);
            if (blockNode is not null)
                blockArray.Add(blockNode);
        }

        root["blocks"] = blockArray;
        return root.ToJsonString(JsonOptions);
    }

    private static string? TryExtractEmbeddedTableBlock(string? tableValuesJson, string blockId)
    {
        var blocks = TryExtractEmbeddedTableBlocks(tableValuesJson, new[] { blockId });
        return blocks.TryGetValue(blockId, out var block) ? block : null;
    }

    private static IReadOnlyDictionary<string, string> TryExtractEmbeddedTableBlocks(
        string? tableValuesJson,
        IReadOnlyCollection<string> blockIds)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(tableValuesJson) || blockIds.Count == 0)
            return result;

        try
        {
            var root = JsonNode.Parse(tableValuesJson) as JsonObject;
            if (root is null ||
                !root.TryGetPropertyValue("blocks", out var blocksNode) ||
                blocksNode is not JsonArray blocks)
            {
                return result;
            }

            foreach (var blockNode in blocks)
            {
                if (blockNode is not JsonObject blockObject)
                    continue;

                var blockId = NormalizeBlockId(ReadString(blockObject, "blockId"));
                if (!blockIds.Contains(blockId) || result.ContainsKey(blockId))
                    continue;

                result[blockId] = blockObject.ToJsonString(JsonOptions);
            }
        }
        catch (JsonException)
        {
            return result;
        }

        return result;
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static long Utf8Size(string? value)
        => string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);

    private static void GuardPayloadSize(string kind, string id, long sizeBytes, int targetBytes)
    {
        var maxBytes = Math.Min(targetBytes, MongoHardGuardBytes);
        if (sizeBytes <= maxBytes)
            return;

        throw AppExceptionFactory.BadRequest(
            AppErrorCode.COMMON_VALIDATION_FAILED,
            new { kind, id, sizeBytes, maxBytes },
            $"{kind} exceeds the configured Mongo payload size guard.");
    }

    private static string NormalizeBlockId(string? value)
        => string.IsNullOrWhiteSpace(value) ? "excel_block" : value.Trim();

    private static string NormalizeTableMode(string? value)
    {
        var tableMode = string.IsNullOrWhiteSpace(value) ? "FIXED_GRID" : value.Trim().ToUpperInvariant();
        return tableMode is "APPEND_ROWS" or "APPEND_COLUMNS" or "MATRIX" or "SUMMARY_TEMPLATE"
            ? tableMode
            : "FIXED_GRID";
    }

    private static string? ReadString(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var node) &&
           node is JsonValue value &&
           value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static int? ReadInt(JsonObject obj, string key)
        => obj.TryGetPropertyValue(key, out var node) &&
           node is JsonValue value &&
           value.TryGetValue<int>(out var number)
            ? number
            : null;

    private static int ResolveRowCount(JsonObject block)
    {
        if (ReadInt(block, "h") is { } h && h >= 0)
            return h;

        if (block.TryGetPropertyValue("rows", out var rows) && rows is JsonArray rowArray)
            return rowArray.Count;

        if (block.TryGetPropertyValue("cells", out var cells) && cells is JsonArray cellArray)
            return cellArray.Count;

        return 0;
    }

    private static int ResolveColumnCount(JsonObject block)
    {
        if (ReadInt(block, "w") is { } w && w >= 0)
            return w;

        if (block.TryGetPropertyValue("columns", out var columns) && columns is JsonArray columnArray)
            return columnArray.Count;

        return 0;
    }

    private sealed record TablePayloadParts(
        string? RootJson,
        IReadOnlyList<TableBlockPayload> Blocks);

    private sealed record TableBlockPayload(
        string BlockId,
        int BlockOrder,
        string TableMode,
        string ValuesJson,
        int RowCount,
        int ColumnCount,
        long SizeBytes,
        string PayloadHash);
}
