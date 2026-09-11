using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly string[] P808ForbiddenIdentityFields =
    [
        "datasetId",
        "datasetVersionId",
        "reportId",
        "workReportId",
        "workAssignmentReportId",
        "workReportPeriodId",
        "resultId",
        "exportId"
    ];

    private static readonly string[] P808SensitiveResponseMarkers =
    [
        "mongodb://",
        "mongodb+srv://",
        "connectionString",
        "stackTrace",
        "rawPayload",
        "sourceContent",
        "queuePayload",
        "leaseOwner"
    ];

    private static FilterDefinition<BsonDocument> P808IdFilter(string id)
        => ObjectId.TryParse(id, out var objectId)
            ? Builders<BsonDocument>.Filter.In(
                "_id",
                new BsonValue[] { objectId, id })
            : Builders<BsonDocument>.Filter.Eq("_id", id);

    private async Task<BsonDocument> RequireP808JobAsync(
        string jobId,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(P808JobsCollection)
            .Find(P808IdFilter(jobId))
            .SingleAsync(ct);

    private async Task<BsonDocument?> FindP808JobAsync(
        string jobId,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(P808JobsCollection)
            .Find(P808IdFilter(jobId))
            .SingleOrDefaultAsync(ct);

    private async Task<long> CountP808JobsAsync(
        FilterDefinition<BsonDocument>? filter,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(P808JobsCollection)
            .CountDocumentsAsync(
                filter ?? FilterDefinition<BsonDocument>.Empty,
                cancellationToken: ct);

    private async Task<long> CountP808OutboxAsync(
        FilterDefinition<BsonDocument>? filter,
        CancellationToken ct)
        => await _database.GetCollection<BsonDocument>(P808AuditOutboxCollection)
            .CountDocumentsAsync(
                filter ?? FilterDefinition<BsonDocument>.Empty,
                cancellationToken: ct);

    private static void RequireP808NoDatasetOrReportIdentity(
        BsonDocument document,
        string context)
    {
        foreach (var field in P808ForbiddenIdentityFields)
        {
            HarnessAssert.True(
                !ContainsBsonField(document, field),
                $"{context} contains forbidden dataset/report identity field '{field}'.");
        }
    }

    private static bool ContainsBsonField(BsonValue value, string field)
    {
        if (value is BsonDocument document)
        {
            foreach (var element in document.Elements)
            {
                if (string.Equals(element.Name, field, StringComparison.OrdinalIgnoreCase) ||
                    ContainsBsonField(element.Value, field))
                {
                    return true;
                }
            }
        }
        else if (value is BsonArray array)
        {
            return array.Any(item => ContainsBsonField(item, field));
        }

        return false;
    }

    private static void RequireP808SafeResponse(
        JsonNode? response,
        string context,
        params string[] additionallyForbidden)
    {
        var text = response?.ToJsonString() ?? string.Empty;
        foreach (var marker in P808SensitiveResponseMarkers.Concat(additionallyForbidden))
        {
            HarnessAssert.True(
                !text.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"{context} leaked sensitive marker '{marker}'.");
        }
    }

    private async Task<IReadOnlyList<BsonDocument>> ListP808IndexesAsync(
        string collection,
        CancellationToken ct)
    {
        var existing = (await (await _database.ListCollectionNamesAsync(
                    cancellationToken: ct))
                .ToListAsync(ct))
            .Contains(collection, StringComparer.Ordinal);
        if (!existing)
            return Array.Empty<BsonDocument>();

        using var cursor = await _database
            .GetCollection<BsonDocument>(collection)
            .Indexes.ListAsync(ct);
        return await cursor.ToListAsync(ct);
    }

    private static BsonDocument RequireP808Index(
        IReadOnlyCollection<BsonDocument> indexes,
        string name,
        BsonDocument key,
        bool unique,
        BsonDocument? partialFilter = null,
        long? expireAfterSeconds = null)
    {
        var index = indexes.SingleOrDefault(item =>
                        string.Equals(
                            BsonString(item, "name"),
                            name,
                            StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"Required P8-08 index '{name}' is missing.");
        HarnessAssert.True(
            index.TryGetValue("key", out var actualKey) &&
            actualKey.IsBsonDocument &&
            actualKey.AsBsonDocument.Equals(key),
            $"Index '{name}' key differs from the frozen contract.");
        HarnessAssert.Equal(
            unique,
            index.GetValue("unique", false).ToBoolean(),
            $"Index '{name}' unique flag mismatch");

        var actualPartial = index.GetValue(
            "partialFilterExpression",
            BsonNull.Value);
        if (partialFilter is null)
        {
            HarnessAssert.True(
                actualPartial.IsBsonNull,
                $"Index '{name}' has an unexpected partial filter.");
        }
        else
        {
            HarnessAssert.True(
                actualPartial.IsBsonDocument &&
                actualPartial.AsBsonDocument.Equals(partialFilter),
                $"Index '{name}' partial filter mismatch.");
        }

        if (expireAfterSeconds.HasValue)
        {
            HarnessAssert.Equal(
                expireAfterSeconds.Value,
                index.GetValue("expireAfterSeconds", -1).ToInt64(),
                $"Index '{name}' TTL mismatch");
        }

        return index;
    }

    private async Task<BsonDocument> ExplainP808FindAsync(
        string collection,
        BsonDocument filter,
        BsonDocument? sort,
        CancellationToken ct)
    {
        var find = new BsonDocument
        {
            ["find"] = collection,
            ["filter"] = filter
        };
        if (sort is not null)
            find["sort"] = sort;
        return await _database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["explain"] = find,
                ["verbosity"] = "queryPlanner"
            },
            cancellationToken: ct);
    }

    private void RecordP808QueueTrace(
        string caseId,
        string jobId,
        params BsonDocument[] states)
    {
        _p808QueueTraces.Add(new P808QueueTrace(
            caseId,
            jobId,
            P808QueueName,
            states.Select(state => new P808QueueState(
                    BsonString(state, "internalStatus") ??
                    BsonString(state, "status") ??
                    "<missing>",
                    BsonString(state, "externalStatus") ?? "<not-persisted>",
                    BsonInt(state, "retryCount") ??
                    BsonInt(state, "attemptCount"),
                    BsonString(state, "correlationId"),
                    BsonString(state, "configHash"),
                    BsonString(state, "leaseOwnerId") ??
                    BsonString(state, "leaseOwner"),
                    state.TryGetValue("leaseUntilUtc", out var leaseUntil)
                        ? leaseUntil.ToString()
                        : null,
                    Sha256(state.ToBson())))
                .ToArray()));
    }

    private async Task WriteP808OperationsEvidenceAsync(CancellationToken ct)
    {
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ops-queue-trace.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                queue = P808QueueName,
                noDatasetWorker = true,
                traces = _p808QueueTraces
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ops-index-query-oracle.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                singleStartupOwner = "MongoIndexInitializer",
                queries = _p808IndexQueries
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ops-diagnostics-security.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                scans = _p808DiagnosticScans
            },
            ct);
    }
}

internal sealed record P808QueueTrace(
    string CaseId,
    string JobId,
    string Queue,
    IReadOnlyList<P808QueueState> States);

internal sealed record P808QueueState(
    string InternalStatus,
    string ExternalStatus,
    int? AttemptCount,
    string? CorrelationId,
    string? ConfigHash,
    string? LeaseOwner,
    string? LeaseUntilUtc,
    string DocumentSha256);

internal sealed record P808IndexQueryEvidence(
    string CaseId,
    string Collection,
    string QueryShape,
    string WinningPlan,
    IReadOnlyList<string> RequiredIndexNames,
    string ExplainSha256);

internal sealed record P808DiagnosticScanEvidence(
    string CaseId,
    string Actor,
    int StatusCode,
    bool BusinessSafe,
    bool SensitiveDiagnosticsVisible,
    IReadOnlyList<string> ForbiddenMarkers,
    string ResponseSha256);

