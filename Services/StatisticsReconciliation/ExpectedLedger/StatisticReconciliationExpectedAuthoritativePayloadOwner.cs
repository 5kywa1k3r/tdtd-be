using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal sealed record StatisticReconciliationExpectedCanonicalPayloadOwnerSnapshot(
    string CanonicalPayloadJson,
    string PayloadOwnerSha256,
    int TableBlockCount,
    string TableBlockManifestSha256);

internal sealed class StatisticReconciliationExpectedAuthoritativePayloadException(
    string reason)
    : InvalidOperationException(reason)
{
    internal string Reason { get; } = reason;
}

/// <summary>
/// Reconstructs one exact immutable report payload from the payload owner and all
/// split table blocks. It never reads a P9 projection/result. Every block and the
/// aggregate payload hash are verified before any canonical bytes are returned.
/// </summary>
internal static class StatisticReconciliationExpectedAuthoritativePayloadOwner
{
    private const int MaxTableBlocks = 100_000;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    internal static async Task<
        StatisticReconciliationExpectedCanonicalPayloadOwnerSnapshot> LoadAsync(
            MongoDbContext context,
            WorkReportPayload payload,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);
        var blocks = await context.WorkReportTableValues
            .Find(item =>
                item.ReportId == payload.ReportId &&
                item.PayloadRevision == payload.PayloadRevision &&
                !item.IsDeleted)
            .SortBy(item => item.BlockOrder)
            .ThenBy(item => item.BlockId)
            .Limit(MaxTableBlocks + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (blocks.Count > MaxTableBlocks)
            throw Fail("TABLE_BLOCK_LIMIT_EXCEEDED");
        return BuildValidated(payload, blocks);
    }

    internal static StatisticReconciliationExpectedCanonicalPayloadOwnerSnapshot
        BuildValidated(
            WorkReportPayload payload,
            IReadOnlyList<WorkReportTableValue> blocks)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(blocks);
        if (payload.IsDeleted ||
            payload.PayloadRevision <= 0 ||
            string.IsNullOrWhiteSpace(payload.ReportId) ||
            !StringComparer.Ordinal.Equals(
                payload.Status,
                WorkReportPayloadStatus.Ready) ||
            !IsLowerSha256(payload.PayloadHash))
            throw Fail("PAYLOAD_OWNER_INVALID");
        if (blocks.Count > MaxTableBlocks)
            throw Fail("TABLE_BLOCK_LIMIT_EXCEEDED");

        var ordered = blocks
            .OrderBy(item => item.BlockOrder)
            .ThenBy(item => item.BlockId, StringComparer.Ordinal)
            .ToArray();
        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        var blockOrders = new HashSet<int>();
        var blockSemantics = new List<string>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var block = ordered[index];
            if (block.IsDeleted ||
                !StringComparer.Ordinal.Equals(block.ReportId, payload.ReportId) ||
                block.PayloadRevision != payload.PayloadRevision ||
                !StringComparer.Ordinal.Equals(
                    block.Status,
                    WorkReportPayloadStatus.Ready))
                throw Fail("TABLE_BLOCK_OWNER_BINDING_INVALID");
            if (string.IsNullOrWhiteSpace(block.BlockId) ||
                block.BlockId != block.BlockId.Trim() ||
                block.BlockOrder != index ||
                !blockIds.Add(block.BlockId) ||
                !blockOrders.Add(block.BlockOrder))
                throw Fail("TABLE_BLOCK_ORDER_OR_ID_INVALID");
            if (block.RowCount < 0 || block.ColumnCount < 0 ||
                block.SizeBytes < 0 ||
                block.SizeBytes != Encoding.UTF8.GetByteCount(block.ValuesJson))
                throw Fail("TABLE_BLOCK_SHAPE_INVALID");
            var actualBlockHash = Sha256(block.ValuesJson);
            if (!StringComparer.Ordinal.Equals(
                    actualBlockHash,
                    block.PayloadHash))
                throw Fail("TABLE_BLOCK_HASH_MISMATCH");
            ValidateBlockJson(block);
            blockSemantics.Add(H(
                "P10_EXPECTED_TABLE_BLOCK_V1",
                [block.BlockId,
                 block.BlockOrder.ToString(
                     System.Globalization.CultureInfo.InvariantCulture),
                 block.TableMode,
                 block.RowCount.ToString(
                     System.Globalization.CultureInfo.InvariantCulture),
                 block.ColumnCount.ToString(
                     System.Globalization.CultureInfo.InvariantCulture),
                 block.SizeBytes.ToString(
                     System.Globalization.CultureInfo.InvariantCulture),
                 block.PayloadHash]));
        }

        var actualPayloadHash = WorkReportPayloadHash.Compute(
            payload.Values1DJson,
            payload.FieldValuesJson,
            payload.TableValuesRootJson,
            payload.SummarySourceJson,
            ordered.Select(item => new WorkReportPayloadBlockHash(
                item.BlockId,
                item.BlockOrder,
                item.PayloadHash)));
        if (!StringComparer.Ordinal.Equals(
                actualPayloadHash,
                payload.PayloadHash))
            throw Fail("PAYLOAD_OWNER_HASH_MISMATCH");

        var tableValuesJson = RebuildTableValuesJson(
            payload.TableValuesRootJson,
            ordered);
        var root = new JsonObject
        {
            ["values1D"] = Parse(payload.Values1DJson, "VALUES_1D"),
            ["fieldValues"] = Parse(payload.FieldValuesJson, "FIELD_VALUES"),
            ["tableValues"] = Parse(tableValuesJson, "TABLE_VALUES"),
            ["summarySource"] = Parse(
                payload.SummarySourceJson,
                "SUMMARY_SOURCE")
        };
        var manifest = H(
            "P10_EXPECTED_TABLE_BLOCK_MANIFEST_V1",
            blockSemantics);
        return new(
            root.ToJsonString(JsonOptions),
            payload.PayloadHash,
            ordered.Length,
            manifest);
    }

    private static void ValidateBlockJson(WorkReportTableValue block)
    {
        JsonObject value;
        try
        {
            value = JsonNode.Parse(block.ValuesJson) as JsonObject ??
                throw Fail("TABLE_BLOCK_JSON_INVALID");
        }
        catch (JsonException)
        {
            throw Fail("TABLE_BLOCK_JSON_INVALID");
        }
        if (value.TryGetPropertyValue("blockId", out var blockIdNode))
        {
            if (blockIdNode is not JsonValue blockIdValue ||
                !blockIdValue.TryGetValue<string>(out var blockId) ||
                !StringComparer.Ordinal.Equals(blockId?.Trim(), block.BlockId))
                throw Fail("TABLE_BLOCK_JSON_ID_MISMATCH");
        }
        else if (!StringComparer.Ordinal.Equals(block.BlockId, "excel_block"))
        {
            throw Fail("TABLE_BLOCK_JSON_ID_REQUIRED");
        }
    }

    private static string? RebuildTableValuesJson(
        string? rootJson,
        IReadOnlyList<WorkReportTableValue> blocks)
    {
        if (string.IsNullOrWhiteSpace(rootJson) && blocks.Count == 0)
            return null;
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(rootJson)
                ? new JsonObject()
                : JsonNode.Parse(rootJson) as JsonObject ??
                  throw Fail("TABLE_ROOT_JSON_INVALID");
        }
        catch (JsonException)
        {
            throw Fail("TABLE_ROOT_JSON_INVALID");
        }
        if (root.TryGetPropertyValue("blocks", out var existing) &&
            existing is not null &&
            (existing is not JsonArray existingArray || existingArray.Count != 0))
            throw Fail("TABLE_ROOT_BLOCKS_NOT_EMPTY");
        var values = new JsonArray();
        foreach (var block in blocks)
            values.Add(JsonNode.Parse(block.ValuesJson));
        root["blocks"] = values;
        return root.ToJsonString(JsonOptions);
    }

    private static JsonNode? Parse(string? json, string field)
    {
        if (json is null)
            return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw Fail($"PAYLOAD_{field}_JSON_INVALID");
        }
    }

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character => character is >= '0' and <= '9' or
               >= 'a' and <= 'f');

    private static string Sha256(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string H(string domain, IEnumerable<string> fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            domain,
            fields);

    private static StatisticReconciliationExpectedAuthoritativePayloadException
        Fail(string reason) => new(reason);
}