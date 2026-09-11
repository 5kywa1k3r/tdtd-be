using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private string RequireOperationsFoundationJobId()
        => _opsFoundationJobId
           ?? throw new HarnessCaseNotRunnableException(
               "P9-OPS Foundation job is unavailable.");

    private string RequireOperationsCancelledJobId()
        => _opsCancelledJobId
           ?? throw new HarnessCaseNotRunnableException(
               "P9-OPS cancelled Foundation job is unavailable.");

    private static JsonObject BuildOperationsCas(
        BsonDocument job,
        string commandId)
        => new()
        {
            ["commandId"] = commandId,
            ["expectedStateRevision"] = BsonLong(job, "stateRevision"),
            ["expectedStateHash"] = BsonString(job, "stateHash")
        };

    private Task<ApiHarnessResponse> PostOperationsMutationAsync(
        string jobId,
        string operation,
        JsonObject request,
        CancellationToken ct)
        => RequireApi().PostAsync(
            $"api/operations/jobs/{jobId}/{operation}",
            request.DeepClone(),
            Actor("admin").Token,
            ct: ct);

    private static JsonObject OperationsMutationJob(
        ApiHarnessResponse response,
        string context)
    {
        var root = ApiHarnessClient.RequiredObject(response.Json, context);
        return ApiHarnessClient.RequiredObject(root["job"], $"{context} job");
    }

    private static JsonObject OperationsMutationReceipt(
        ApiHarnessResponse response,
        string context)
    {
        var root = ApiHarnessClient.RequiredObject(response.Json, context);
        return ApiHarnessClient.RequiredObject(root["receipt"], $"{context} receipt");
    }

    private static JsonArray OperationsRequiredArray(
        JsonNode? node,
        string property,
        string context)
    {
        if (node is JsonObject root && root[property] is JsonArray values)
            return values;
        throw new InvalidOperationException(
            $"{context} lacks JSON array '{property}'. Body={node?.ToJsonString() ?? "<null>"}");
    }

    private static string? OperationsOptionalString(
        JsonNode? node,
        string property)
    {
        if (node is not JsonObject root || root[property] is null)
            return null;
        if (root[property] is JsonValue value &&
            value.TryGetValue<string>(out var text))
        {
            return text;
        }
        return null;
    }

    private static DateTime OperationsRequiredUtc(
        JsonNode? node,
        string property)
    {
        var text = RequiredString(node, property);
        return DateTimeOffset.Parse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            .UtcDateTime;
    }

    private static string OperationsApiStatus(string internalStatus)
        => internalStatus switch
        {
            "PENDING" => "QUEUED",
            "RUNNING" => "RUNNING",
            "RETRY_WAITING" => "RETRYING",
            "COMPLETED" => "DONE",
            "DEAD_LETTER" => "FAILED",
            _ => internalStatus
        };

    private async Task<BsonDocument> TraceOperationsJobAsync(
        string step,
        string jobId,
        CancellationToken ct)
    {
        var job = await LoadJobAsync(jobId, ct);
        var claimToken = BsonNullableString(job, "claimToken");
        var internalStatus = BsonString(job, "status");
        _opsJobTrace.Add(new P9OperationsJobTrace(
            step,
            jobId,
            internalStatus,
            OperationsApiStatus(internalStatus),
            BsonLong(job, "stateRevision"),
            BsonInt(job, "retryCount"),
            BsonString(job, "stateHash"),
            claimToken is null ? null : OperationsSecretSafeHash(claimToken),
            BsonNullableString(job, "diagnosticCode"),
            BsonNullableUtc(job, "updatedAtUtc") ?? DateTime.UtcNow));
        return job;
    }

    private async Task<BsonDocument> VerifyOperationsReceiptAsync(
        string caseId,
        string operation,
        string jobId,
        string commandId,
        ApiHarnessResponse response,
        bool replay,
        CancellationToken ct)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"{caseId} {operation}");
        var responseJob = OperationsMutationJob(response, caseId);
        var responseReceipt = OperationsMutationReceipt(response, caseId);
        HarnessAssert.Equal(jobId, RequiredString(responseJob, "jobId"), $"{caseId} job id");
        HarnessAssert.Equal(replay, RequiredBool(responseJob, "isReplay"), $"{caseId} job replay");
        HarnessAssert.Equal(replay, RequiredBool(responseReceipt, "isReplay"), $"{caseId} receipt replay");
        HarnessAssert.Equal(operation, RequiredString(responseReceipt, "operation"), $"{caseId} operation");
        HarnessAssert.Equal(commandId, RequiredString(responseReceipt, "commandId"), $"{caseId} command");

        var job = await LoadJobAsync(jobId, ct);
        var receipts = job.GetValue("operationReceipts", new BsonArray())
            .AsBsonArray
            .Select(value => value.AsBsonDocument)
            .Where(item => string.Equals(
                BsonString(item, "operation"),
                operation,
                StringComparison.Ordinal))
            .Where(item => string.Equals(
                BsonString(item, "commandId"),
                commandId,
                StringComparison.Ordinal))
            .ToArray();
        HarnessAssert.Equal(1, receipts.Length, $"{caseId} durable receipt count");
        var receipt = receipts[0];
        HarnessAssert.Equal(jobId, BsonString(receipt, "jobId"), $"{caseId} durable receipt job");
        HarnessAssert.Equal(
            RequiredString(responseReceipt, "receiptId"),
            BsonString(receipt, "receiptId"),
            $"{caseId} receipt id");
        HarnessAssert.Equal(
            RequiredString(responseReceipt, "requestHash"),
            BsonString(receipt, "requestHash"),
            $"{caseId} request hash");
        HarnessAssert.Equal(
            RequiredLong(responseReceipt, "acceptedStateRevision"),
            BsonLong(receipt, "acceptedStateRevision"),
            $"{caseId} accepted revision");
        HarnessAssert.Equal(
            RequiredString(responseReceipt, "acceptedStateHash"),
            BsonString(receipt, "acceptedStateHash"),
            $"{caseId} accepted state hash");
        HarnessAssert.True(
            IsCanonicalSha(BsonString(receipt, "receiptId")) &&
            IsCanonicalSha(BsonString(receipt, "requestHash")) &&
            IsCanonicalSha(BsonString(receipt, "receiptHash")) &&
            IsCanonicalSha(BsonString(job, "operationReceiptHistoryHash")),
            $"{caseId} durable receipt hashes are not canonical");
        _opsReceiptTrace.Add(new P9OperationsReceiptTrace(
            caseId,
            operation,
            jobId,
            commandId,
            BsonString(receipt, "requestHash"),
            BsonString(receipt, "receiptHash"),
            replay,
            true));
        return receipt;
    }

    private async Task<BsonDocument> VerifyOperationsCleanupReceiptAsync(
        string caseId,
        string jobId,
        string commandId,
        ApiHarnessResponse response,
        bool replay,
        CancellationToken ct)
    {
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"{caseId} cleanup");
        HarnessAssert.Equal(replay, RequiredBool(response.Json, "isReplay"), $"{caseId} cleanup replay");
        var jobIds = OperationsRequiredArray(response.Json, "jobIds", caseId)
            .Select(value => value?.GetValue<string>() ?? string.Empty)
            .ToArray();
        var receiptIds = OperationsRequiredArray(response.Json, "receiptIds", caseId)
            .Select(value => value?.GetValue<string>() ?? string.Empty)
            .ToArray();
        HarnessAssert.True(jobIds.Contains(jobId, StringComparer.Ordinal), $"{caseId} cleanup job id");
        HarnessAssert.Equal(jobIds.Length, receiptIds.Length, $"{caseId} cleanup receipt cardinality");

        var job = await LoadJobAsync(jobId, ct);
        var receipts = job.GetValue("operationReceipts", new BsonArray())
            .AsBsonArray
            .Select(value => value.AsBsonDocument)
            .Where(item => string.Equals(BsonString(item, "operation"), "CLEANUP", StringComparison.Ordinal))
            .Where(item => string.Equals(BsonString(item, "commandId"), commandId, StringComparison.Ordinal))
            .ToArray();
        HarnessAssert.Equal(1, receipts.Length, $"{caseId} cleanup durable receipt count");
        var receipt = receipts[0];
        HarnessAssert.True(
            receiptIds.Contains(BsonString(receipt, "receiptId"), StringComparer.Ordinal),
            $"{caseId} cleanup receipt id");
        HarnessAssert.True(
            IsCanonicalSha(BsonString(receipt, "requestHash")) &&
            IsCanonicalSha(BsonString(receipt, "receiptHash")) &&
            IsCanonicalSha(BsonString(job, "operationReceiptHistoryHash")),
            $"{caseId} cleanup durable receipt hashes");
        _opsReceiptTrace.Add(new P9OperationsReceiptTrace(
            caseId,
            "CLEANUP",
            jobId,
            commandId,
            BsonString(receipt, "requestHash"),
            BsonString(receipt, "receiptHash"),
            replay,
            true));
        return receipt;
    }

    private static void AssertOperationsSnapshotEqual(
        IReadOnlyDictionary<string, P9CollectionState> before,
        IReadOnlyDictionary<string, P9CollectionState> after,
        string context)
        => HarnessAssert.Equal(
            SnapshotSha256(before),
            SnapshotSha256(after),
            context);

    private async Task<BsonDocument> LoadOperationsJobIncludingDeletedAsync(
        string jobId,
        CancellationToken ct)
        => await RequireDatabase()
            .GetCollection<BsonDocument>(LifecycleJobCollection)
            .Find(new BsonDocument("_id", ObjectId.Parse(jobId)))
            .SingleAsync(ct);
}
