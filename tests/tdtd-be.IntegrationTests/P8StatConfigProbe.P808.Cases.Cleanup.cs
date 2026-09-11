using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP808OwnedCleanupRetryCaseAsync(
        CancellationToken ct)
    {
        const string commandId = "p808-fault-cleanup-020";
        await RunEvidenceCaseAsync(
            "P8-OPS-020",
            "system_admin",
            [commandId],
            [
                P808JobsCollection,
                ReceiptsCollection,
                P808AuditOutboxCollection
            ],
            [
                P808JobsCollection,
                ReceiptsCollection,
                P808AuditOutboxCollection
            ],
            async () =>
            {
                var actor = Actor("system_admin");
                var jobId = _p808RaceJobId
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8-OPS-019 did not produce the cleanup target job.");
                var pending = await RequireP808JobAsync(jobId, ct);
                HarnessAssert.Equal(
                    "PENDING",
                    BsonString(pending, "status"),
                    "P8 cleanup target was not pending before worker completion");

                var process = await ProcessP808JobsAsync(actor, 1, ct);
                ApiHarnessClient.ExpectStatus(
                    process,
                    HttpStatusCode.OK,
                    "P8 cleanup target worker process");
                HarnessAssert.Equal(
                    1,
                    ApiHarnessClient.FindIntRecursive(process.Json, "completed"),
                    "P8 cleanup target did not complete exactly once");
                var completed = await RequireP808JobAsync(jobId, ct);
                HarnessAssert.Equal(
                    "COMPLETED",
                    BsonString(completed, "status"),
                    "P8 cleanup target did not reach terminal COMPLETED");
                HarnessAssert.Equal(
                    false,
                    BsonBool(completed, "isActive"),
                    "P8 cleanup target remained active");
                HarnessAssert.Equal(
                    P808QueueName,
                    BsonString(completed, "queueName"),
                    "P8 cleanup target queue ownership drifted");

                var cutoff = DateTime.UtcNow;
                var expiredAt = cutoff.AddSeconds(-1);
                var jobs = _database.GetCollection<BsonDocument>(
                    P808JobsCollection);
                var backdate = await jobs.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.And(
                        P808IdFilter(jobId),
                        Builders<BsonDocument>.Filter.Eq(
                            "queueName",
                            P808QueueName),
                        Builders<BsonDocument>.Filter.Eq(
                            "status",
                            "COMPLETED"),
                        Builders<BsonDocument>.Filter.Eq(
                            "isActive",
                            false),
                        Builders<BsonDocument>.Filter.Eq(
                            "isDeleted",
                            false)),
                    Builders<BsonDocument>.Update.Set(
                        "expiresAtUtc",
                        expiredAt),
                    cancellationToken: ct);
                HarnessAssert.Equal(
                    1L,
                    backdate.ModifiedCount,
                    "P8 cleanup target expiry precondition was not applied");
                var expired = await RequireP808JobAsync(jobId, ct);
                HarnessAssert.True(
                    RequireP808UtcDate(expired, "expiresAtUtc") <= cutoff,
                    "P8 cleanup target was not expired at the frozen cutoff.");

                var unrelatedBefore =
                    await FingerprintP808UnrelatedJobsAsync(jobId, ct);
                var request = Envelope(
                    commandId,
                    0,
                    EmptyConfigHash,
                    new JsonObject
                    {
                        ["completedBeforeUtc"] = JsonValue.Create(cutoff),
                        ["limit"] = 10,
                        ["dryRun"] = false
                    });

                var beforeFault = await CaptureDatabaseSnapshotAsync(ct);
                var fault = await CleanupP808JobsAsync(actor, request, ct);
                ApiHarnessClient.ExpectStatus(
                    fault,
                    HttpStatusCode.InternalServerError,
                    "P8 cleanup transient fault");
                var afterFault = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-OPS-020/cleanup-fault-rollback",
                    BuildDeltas(beforeFault, afterFault),
                    Array.Empty<string>(),
                    Array.Empty<string>());
                HarnessAssert.True(
                    await FindP808JobAsync(jobId, ct) is not null,
                    "Faulted P8 cleanup removed its target");
                await RequireP808NoCleanupArtifactsAsync(commandId, ct);

                var success = await CleanupP808JobsAsync(
                    actor,
                    request,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    success,
                    HttpStatusCode.OK,
                    "P8 cleanup retry");
                var successJson = success.Json as JsonObject
                                  ?? throw new InvalidOperationException(
                                      "P8 cleanup retry response is not an object.");
                HarnessAssert.Equal(
                    false,
                    successJson["dryRun"]?.GetValue<bool>(),
                    "P8 cleanup retry unexpectedly remained dry-run");
                HarnessAssert.Equal(
                    1,
                    ApiHarnessClient.FindIntRecursive(
                        success.Json,
                        "matchedCount"),
                    "P8 cleanup retry matched a non-owned candidate set");
                HarnessAssert.Equal(
                    1,
                    ApiHarnessClient.FindIntRecursive(
                        success.Json,
                        "selectedCount"),
                    "P8 cleanup retry selected a non-owned candidate set");
                HarnessAssert.Equal(
                    1,
                    ApiHarnessClient.FindIntRecursive(
                        success.Json,
                        "deletedCount"),
                    "P8 cleanup retry did not delete exactly one row");
                var selectedIds = successJson["jobIds"] as JsonArray
                                  ?? throw new InvalidOperationException(
                                      "P8 cleanup response lacks jobIds.");
                HarnessAssert.Equal(
                    1,
                    selectedIds.Count,
                    "P8 cleanup returned an unexpected job count");
                HarnessAssert.Equal(
                    jobId,
                    selectedIds[0]?.GetValue<string>(),
                    "P8 cleanup deleted a job outside its owned target");
                HarnessAssert.True(
                    await FindP808JobAsync(jobId, ct) is null,
                    "P8 cleanup target still exists after successful retry");
                await RequireP808SingleCleanupArtifactsAsync(commandId, ct);

                var canonicalSuccess = CanonicalResponse(success);
                var beforeReplay = await CaptureDatabaseSnapshotAsync(ct);
                var replay = await CleanupP808JobsAsync(actor, request, ct);
                ApiHarnessClient.ExpectStatus(
                    replay,
                    HttpStatusCode.OK,
                    "P8 cleanup exact replay");
                HarnessAssert.Equal(
                    canonicalSuccess,
                    CanonicalResponse(replay),
                    "P8 cleanup exact replay response drifted");
                var afterReplay = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-OPS-020/cleanup-replay",
                    BuildDeltas(beforeReplay, afterReplay),
                    Array.Empty<string>(),
                    Array.Empty<string>());
                await RequireP808SingleCleanupArtifactsAsync(commandId, ct);

                var unrelatedAfter =
                    await FingerprintP808UnrelatedJobsAsync(jobId, ct);
                HarnessAssert.Equal(
                    unrelatedBefore,
                    unrelatedAfter,
                    "P8 cleanup changed an unrelated readiness job");
                pending["externalStatus"] = "QUEUED";
                expired["externalStatus"] = "DONE";
                RecordP808QueueTrace(
                    "P8-OPS-020",
                    jobId,
                    pending,
                    expired);
                await EvidenceJson.WriteAsync(
                    Path.Combine(
                        _paths.RunRoot,
                        "p8-ops-020-cleanup-ownership.json"),
                    new
                    {
                        jobId,
                        queue = P808QueueName,
                        cutoffUtc = cutoff,
                        expiredAtUtc = expiredAt,
                        transientRollbackWholeDatabaseDeltaZero = true,
                        retryDeletedOwnedJobCount = 1,
                        exactReplayWholeDatabaseDeltaZero = true,
                        unrelatedBefore,
                        unrelatedAfter,
                        responseSha256 = Sha256(
                            Encoding.UTF8.GetBytes(canonicalSuccess))
                    },
                    ct);

                return new CaseObservation(
                    "Cleanup first rolled back on a deterministic transient fault, then the exact retry deleted only the expired terminal readiness row selected by queue ownership and cutoff; exact replay returned the durable receipt response with whole-database zero delta.",
                    "fault=500:rollback;queue=stat-config-readiness;expiredOwned=1;selected=1;deleted=1;unrelatedJobDelta=0;replay=sameResponse;replayDelta=0;resultDelta=0");
            },
            ct);
    }

    private async Task RequireP808NoCleanupArtifactsAsync(
        string commandId,
        CancellationToken ct)
    {
        HarnessAssert.Equal(
            0L,
            await _database.GetCollection<BsonDocument>(ReceiptsCollection)
                .CountDocumentsAsync(
                    Builders<BsonDocument>.Filter.Eq(
                        "commandId",
                        commandId),
                    cancellationToken: ct),
            "Faulted P8 cleanup persisted a receipt");
        HarnessAssert.Equal(
            0L,
            await CountP808OutboxAsync(
                Builders<BsonDocument>.Filter.Eq(
                    "commandId",
                    commandId),
                ct),
            "Faulted P8 cleanup persisted an audit outbox row");
    }

    private async Task RequireP808SingleCleanupArtifactsAsync(
        string commandId,
        CancellationToken ct)
    {
        HarnessAssert.Equal(
            1L,
            await _database.GetCollection<BsonDocument>(ReceiptsCollection)
                .CountDocumentsAsync(
                    Builders<BsonDocument>.Filter.Eq(
                        "commandId",
                        commandId),
                    cancellationToken: ct),
            "P8 cleanup receipt count mismatch");
        HarnessAssert.Equal(
            1L,
            await CountP808OutboxAsync(
                Builders<BsonDocument>.Filter.Eq(
                    "commandId",
                    commandId),
                ct),
            "P8 cleanup audit-outbox count mismatch");
    }

    private async Task<P808UnrelatedJobFingerprint>
        FingerprintP808UnrelatedJobsAsync(
            string excludedJobId,
            CancellationToken ct)
    {
        var documents = (await _database
                .GetCollection<BsonDocument>(P808JobsCollection)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .ToListAsync(ct))
            .Where(document => !P808DocumentHasId(
                document,
                excludedJobId))
            .ToArray();
        return new P808UnrelatedJobFingerprint(
            documents.Length,
            Sha256(new BsonDocument
            {
                { "documents", new BsonArray(documents) }
            }.ToBson()));
    }

    private static bool P808DocumentHasId(
        BsonDocument document,
        string expectedId)
    {
        if (!document.TryGetValue("_id", out var actual))
            return false;
        return actual.BsonType switch
        {
            BsonType.ObjectId => string.Equals(
                actual.AsObjectId.ToString(),
                expectedId,
                StringComparison.Ordinal),
            BsonType.String => string.Equals(
                actual.AsString,
                expectedId,
                StringComparison.Ordinal),
            _ => string.Equals(
                actual.ToString(),
                expectedId,
                StringComparison.Ordinal)
        };
    }
}

internal sealed record P808UnrelatedJobFingerprint(
    int Count,
    string DocumentSetSha256);
