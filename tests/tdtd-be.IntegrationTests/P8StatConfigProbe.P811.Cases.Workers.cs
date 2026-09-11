using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP811FaultBoundaryPairAsync(
        string caseId,
        int firstBoundary,
        string transition,
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            caseId,
            "system_admin",
            [P811FaultCommand],
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var owner = P811ReadinessOwner("fault");
                var jobFilter = Builders<BsonDocument>.Filter.Eq(
                    "enqueueCommandId", P811FaultCommand);
                var receiptFilter = Builders<BsonDocument>.Filter.Eq(
                    "commandId", P811FaultCommand);
                for (var offset = 0; offset < 2; offset++)
                {
                    var response = await EnqueueP808JobAsync(
                        actor,
                        owner.OwnerKind,
                        owner.OwnerId,
                        P808EnqueueEnvelope(P811FaultCommand, owner),
                        ct);
                    ApiHarnessClient.ExpectStatus(
                        response,
                        HttpStatusCode.InternalServerError,
                        $"{caseId} injected boundary {firstBoundary + offset}");
                    HarnessAssert.Equal(0L,
                        await CountP808JobsAsync(jobFilter, ct),
                        $"{caseId} left a partial readiness job");
                    HarnessAssert.Equal(0L,
                        await _database
                            .GetCollection<BsonDocument>(ReceiptsCollection)
                            .CountDocumentsAsync(
                                receiptFilter,
                                cancellationToken: ct),
                        $"{caseId} left a partial receipt");
                    HarnessAssert.Equal(0L,
                        await CountP808OutboxAsync(receiptFilter, ct),
                        $"{caseId} left a partial audit-outbox row");
                }
                return new CaseObservation(
                    $"Two consecutive real Kestrel faults around the {transition} transition rolled the Mongo transaction back to an exact zero job/receipt/audit-outbox delta.",
                    $"boundaries={firstBoundary},{firstBoundary + 1};transition={transition};jobDelta=0;receiptDelta=0;outboxDelta=0;resultDelta=0");
            },
            ct);
    }

    private async Task RunP811DuplicateEnqueueAfterFaultsAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-RACE-010",
            "system_admin",
            [P811FaultCommand],
            [P808JobsCollection, ReceiptsCollection, P808AuditOutboxCollection],
            [P808JobsCollection, ReceiptsCollection, P808AuditOutboxCollection],
            async () =>
            {
                var actor = Actor("system_admin");
                var owner = P811ReadinessOwner("fault");
                var responses = await BarrierReleasedAsync(
                    4,
                    () => EnqueueP808JobAsync(
                        actor,
                        owner.OwnerKind,
                        owner.OwnerId,
                        P808EnqueueEnvelope(P811FaultCommand, owner),
                        ct),
                    ct);
                HarnessAssert.True(responses.All(item =>
                        item.StatusCode == HttpStatusCode.OK),
                    "Post-fault duplicate enqueue returned a loser.");
                HarnessAssert.Equal(1,
                    responses.Select(CanonicalResponse)
                        .Distinct(StringComparer.Ordinal).Count(),
                    "Post-fault duplicate enqueue responses diverged");
                var identities = responses.Select(item =>
                        ParseP808JobIdentity(item.Json,
                            "P8-RACE-010 enqueue response"))
                    .ToArray();
                HarnessAssert.Equal(1,
                    identities.Select(item => item.JobId)
                        .Distinct(StringComparer.Ordinal).Count(),
                    "Duplicate enqueue persisted more than one job identity");
                HarnessAssert.Equal(1L,
                    await CountP808JobsAsync(
                        Builders<BsonDocument>.Filter.Eq(
                            "enqueueCommandId", P811FaultCommand),
                        ct),
                    "Duplicate enqueue persisted more than one job");
                HarnessAssert.Equal(1L,
                    await _database.GetCollection<BsonDocument>(
                            ReceiptsCollection)
                        .CountDocumentsAsync(
                            Builders<BsonDocument>.Filter.Eq(
                                "commandId", P811FaultCommand),
                            cancellationToken: ct),
                    "Duplicate enqueue persisted more than one receipt");
                HarnessAssert.Equal(1L,
                    await CountP808OutboxAsync(
                        Builders<BsonDocument>.Filter.Eq(
                            "commandId", P811FaultCommand),
                        ct),
                    "Duplicate enqueue persisted more than one outbox row");
                _p811FaultJobId = identities[0].JobId;
                var pending = await RequireP808JobAsync(
                    identities[0].JobId, ct);
                pending["externalStatus"] = "QUEUED";
                RecordP808QueueTrace(
                    "P8-RACE-010",
                    identities[0].JobId,
                    pending);
                return new CaseObservation(
                    "After all six transaction faults were consumed, four barrier-released duplicate enqueues converged on one atomically linked PENDING job, receipt and audit-outbox intent.",
                    "contenders=4;winnerJob=1;responses=1;job=1;receipt=1;outbox=1;status=QUEUED;resultDelta=0");
            },
            ct);
    }

    private async Task RunP811WorkerRestartRecoveryAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-RACE-011",
            "system_admin",
            [P811FaultCommand],
            [P808JobsCollection],
            [P808JobsCollection],
            async () =>
            {
                var jobId = _p811FaultJobId
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8-RACE-010 did not persist the restart job.");
                var restart = _p811BackendRestart
                              ?? throw new HarnessCaseNotRunnableException(
                                  "Replacement Kestrel evidence is absent.");
                HarnessAssert.True(
                    restart.PreviousStopVerified &&
                    restart.PreviousPortReleaseVerified &&
                    restart.ReplacementAuthenticated &&
                    restart.PreviousProcessId != restart.ReplacementProcessId,
                    "P8-RACE-011 did not establish a real replacement Kestrel process.");
                var pending = await RequireP808JobAsync(jobId, ct);
                HarnessAssert.Equal("PENDING", BsonString(pending, "status"),
                    "Restart job was not pending before replacement worker");
                var process = await ProcessP808JobsAsync(
                    Actor("system_admin"), 1, ct);
                ApiHarnessClient.ExpectStatus(
                    process, HttpStatusCode.OK,
                    "replacement Kestrel worker process");
                HarnessAssert.Equal(1,
                    ApiHarnessClient.FindIntRecursive(process.Json, "claimed"),
                    "Replacement worker did not claim exactly one job");
                HarnessAssert.Equal(1,
                    ApiHarnessClient.FindIntRecursive(process.Json, "completed"),
                    "Replacement worker did not complete exactly one job");
                var completed = await RequireP808JobAsync(jobId, ct);
                HarnessAssert.Equal("COMPLETED",
                    BsonString(completed, "status"),
                    "Restarted worker did not complete persisted job");
                HarnessAssert.Equal(false,
                    BsonBool(completed, "isActive"),
                    "Restart-completed job remained active");
                pending["externalStatus"] = "QUEUED";
                completed["externalStatus"] = "DONE";
                RecordP808QueueTrace(
                    "P8-RACE-011", jobId, pending, completed);
                return new CaseObservation(
                    "The original Kestrel stopped and released its port; a different authenticated Kestrel PID on the same isolated Mongo reclaimed and completed the persisted no-dataset job exactly once.",
                    "originalStopped=1;portReleased=1;replacementPidDistinct=1;claimed=1;completed=1;queue=stat-config-readiness;resultDelta=0");
            },
            ct);
    }

    private async Task RunP811LiveLeaseExclusionAsync(CancellationToken ct)
    {
        const string commandId = "p811-live-lease-012";
        await RunEvidenceCaseAsync(
            "P8-RACE-012",
            "system_admin",
            [commandId],
            [P808JobsCollection, ReceiptsCollection, P808AuditOutboxCollection],
            [P808JobsCollection, ReceiptsCollection, P808AuditOutboxCollection],
            async () =>
            {
                var actor = Actor("system_admin");
                var owner = P811ReadinessOwner("lease");
                var enqueue = await EnqueueP808JobAsync(
                    actor,
                    owner.OwnerKind,
                    owner.OwnerId,
                    P808EnqueueEnvelope(commandId, owner),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    enqueue, HttpStatusCode.OK,
                    "P8-RACE-012 enqueue");
                var identity = ParseP808JobIdentity(
                    enqueue.Json, "P8-RACE-012 enqueue response");
                var leaseUntil = DateTime.UtcNow.AddMinutes(3);
                var heartbeatAt = DateTime.UtcNow;
                var update = await _database
                    .GetCollection<BsonDocument>(P808JobsCollection)
                    .UpdateOneAsync(
                        P808IdFilter(identity.JobId),
                        Builders<BsonDocument>.Update
                            .Set("status", "RUNNING")
                            .Set("isActive", true)
                            .Set("claimToken", "p811-live-claim-012")
                            .Set("leaseOwnerId", "p811-live-worker-012")
                            .Set("leaseUntilUtc", leaseUntil)
                            .Set("lastHeartbeatAtUtc", heartbeatAt),
                        cancellationToken: ct);
                HarnessAssert.Equal(1L, update.ModifiedCount,
                    "P8-RACE-012 failed to establish live lease");
                var live = await RequireP808JobAsync(identity.JobId, ct);
                var responses = await BarrierReleasedAsync(
                    2,
                    () => ProcessP808JobsAsync(actor, 1, ct),
                    ct);
                foreach (var response in responses)
                {
                    ApiHarnessClient.ExpectStatus(
                        response, HttpStatusCode.OK,
                        "P8-RACE-012 rival worker");
                }
                HarnessAssert.Equal(0,
                    responses.Sum(response =>
                        ApiHarnessClient.FindIntRecursive(
                            response.Json, "claimed") ?? 0),
                    "A rival worker claimed a future live lease");
                var after = await RequireP808JobAsync(identity.JobId, ct);
                HarnessAssert.Equal("RUNNING", BsonString(after, "status"),
                    "Rival worker changed live lease status");
                HarnessAssert.Equal("p811-live-worker-012",
                    BsonString(after, "leaseOwnerId"),
                    "Rival worker replaced live lease owner");
                _p811LeaseJobId = identity.JobId;
                live["externalStatus"] = "RUNNING";
                after["externalStatus"] = "RUNNING";
                RecordP808QueueTrace(
                    "P8-RACE-012", identity.JobId, live, after);
                return new CaseObservation(
                    "Two barrier-released rival worker calls respected the future heartbeat lease and left its single persisted owner byte-stable.",
                    "rivals=2;claimed=0;status=RUNNING;leaseOwnerStable=1;heartbeatFuture=1;resultDelta=0");
            },
            ct);
    }

    private async Task RunP811ExpiredLeaseWinnerAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-RACE-013",
            "system_admin",
            Array.Empty<string>(),
            [P808JobsCollection],
            [P808JobsCollection],
            async () =>
            {
                var jobId = _p811LeaseJobId
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8-RACE-012 live lease job is absent.");
                var expiredAt = DateTime.UtcNow.AddMinutes(-2);
                var update = await _database
                    .GetCollection<BsonDocument>(P808JobsCollection)
                    .UpdateOneAsync(
                        P808IdFilter(jobId),
                        Builders<BsonDocument>.Update
                            .Set("leaseUntilUtc", expiredAt)
                            .Set("lastHeartbeatAtUtc", expiredAt),
                        cancellationToken: ct);
                HarnessAssert.Equal(1L, update.ModifiedCount,
                    "P8-RACE-013 failed to expire live lease");
                var expired = await RequireP808JobAsync(jobId, ct);
                var responses = await BarrierReleasedAsync(
                    2,
                    () => ProcessP808JobsAsync(
                        Actor("system_admin"), 1, ct),
                    ct);
                foreach (var response in responses)
                {
                    ApiHarnessClient.ExpectStatus(
                        response, HttpStatusCode.OK,
                        "P8-RACE-013 expired-lease rival");
                }
                HarnessAssert.Equal(1,
                    responses.Sum(response =>
                        ApiHarnessClient.FindIntRecursive(
                            response.Json, "claimed") ?? 0),
                    "Expired lease did not have exactly one claim winner");
                HarnessAssert.Equal(1,
                    responses.Sum(response =>
                        ApiHarnessClient.FindIntRecursive(
                            response.Json, "completed") ?? 0),
                    "Expired lease did not have exactly one completion");
                var completed = await RequireP808JobAsync(jobId, ct);
                HarnessAssert.Equal("COMPLETED",
                    BsonString(completed, "status"),
                    "Expired lease winner did not complete job");
                HarnessAssert.Equal(false,
                    BsonBool(completed, "isActive"),
                    "Expired lease completed job remained active");
                var replay = await ProcessP808JobsAsync(
                    Actor("system_admin"), 1, ct);
                ApiHarnessClient.ExpectStatus(
                    replay, HttpStatusCode.OK,
                    "P8-RACE-013 post-completion replay");
                HarnessAssert.Equal(0,
                    ApiHarnessClient.FindIntRecursive(replay.Json, "claimed"),
                    "Completed expired-lease job was claimed twice");
                expired["externalStatus"] = "RUNNING";
                completed["externalStatus"] = "DONE";
                RecordP808QueueTrace(
                    "P8-RACE-013", jobId, expired, completed);
                return new CaseObservation(
                    "After exact lease expiry, two barrier-released replacement workers produced one claim, one completion and a zero-work replay.",
                    "rivals=2;leaseExpired=1;claimed=1;completed=1;secondClaim=0;doubleResult=0;resultDelta=0");
            },
            ct);
    }

    private async Task RunP811CleanupRetryAsync(CancellationToken ct)
    {
        const string commandId = "p808-fault-p811-cleanup-014";
        await RunEvidenceCaseAsync(
            "P8-RACE-014",
            "system_admin",
            [commandId],
            [P808JobsCollection, ReceiptsCollection, P808AuditOutboxCollection],
            [P808JobsCollection, ReceiptsCollection, P808AuditOutboxCollection],
            async () =>
            {
                var actor = Actor("system_admin");
                var jobId = _p811LeaseJobId
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8-RACE-013 cleanup target is absent.");
                var cutoff = DateTime.UtcNow.AddMinutes(-5);
                var expiredAt = cutoff.AddMinutes(-5);
                var backdate = await _database
                    .GetCollection<BsonDocument>(P808JobsCollection)
                    .UpdateOneAsync(
                        P808IdFilter(jobId) &
                        Builders<BsonDocument>.Filter.Eq("queueName", P808QueueName) &
                        Builders<BsonDocument>.Filter.Eq("status", "COMPLETED") &
                        Builders<BsonDocument>.Filter.Eq("isActive", false) &
                        Builders<BsonDocument>.Filter.Eq("isDeleted", false),
                        Builders<BsonDocument>.Update.Set(
                            "expiresAtUtc", expiredAt),
                        cancellationToken: ct);
                HarnessAssert.Equal(1L, backdate.ModifiedCount,
                    "P8-RACE-014 did not backdate its owned target");
                var expired = await RequireP808JobAsync(jobId, ct);
                var unrelatedBefore =
                    await FingerprintP808UnrelatedJobsAsync(jobId, ct);
                var request = Envelope(
                    commandId,
                    0,
                    EmptyConfigHash,
                    new JsonObject
                    {
                        ["completedBeforeUtc"] = JsonValue.Create(cutoff),
                        ["limit"] = 1,
                        ["dryRun"] = false
                    });
                var beforeFault = await CaptureDatabaseSnapshotAsync(ct);
                var fault = await CleanupP808JobsAsync(actor, request, ct);
                ApiHarnessClient.ExpectStatus(
                    fault, HttpStatusCode.InternalServerError,
                    "P8-RACE-014 cleanup transient fault");
                var afterFault = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-RACE-014/transient-rollback",
                    BuildDeltas(beforeFault, afterFault),
                    NoCollectionWrites,
                    NoCollectionWrites);
                HarnessAssert.True(
                    await FindP808JobAsync(jobId, ct) is not null,
                    "Faulted cleanup deleted its target");
                await RequireP808NoCleanupArtifactsAsync(commandId, ct);

                var success = await CleanupP808JobsAsync(
                    actor, request, ct);
                ApiHarnessClient.ExpectStatus(
                    success, HttpStatusCode.OK,
                    "P8-RACE-014 cleanup retry");
                HarnessAssert.Equal(1,
                    ApiHarnessClient.FindIntRecursive(
                        success.Json, "selectedCount"),
                    "Cleanup retry selected an unexpected set");
                HarnessAssert.Equal(1,
                    ApiHarnessClient.FindIntRecursive(
                        success.Json, "deletedCount"),
                    "Cleanup retry did not delete exactly one job");
                var successObject = success.Json as JsonObject
                                    ?? throw new InvalidOperationException(
                                        "Cleanup retry response is malformed.");
                var jobIds = successObject["jobIds"] as JsonArray
                             ?? throw new InvalidOperationException(
                                 "Cleanup retry response lacks jobIds.");
                HarnessAssert.Equal(jobId,
                    jobIds.Single()?.GetValue<string>(),
                    "Cleanup retry deleted a non-owned job");
                HarnessAssert.True(
                    await FindP808JobAsync(jobId, ct) is null,
                    "Cleanup retry left its target");
                await RequireP808SingleCleanupArtifactsAsync(commandId, ct);

                var canonical = CanonicalResponse(success);
                var beforeReplay = await CaptureDatabaseSnapshotAsync(ct);
                var replay = await CleanupP808JobsAsync(actor, request, ct);
                ApiHarnessClient.ExpectStatus(
                    replay, HttpStatusCode.OK,
                    "P8-RACE-014 cleanup replay");
                HarnessAssert.Equal(canonical, CanonicalResponse(replay),
                    "Cleanup exact replay response drifted");
                var afterReplay = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-RACE-014/replay-zero-write",
                    BuildDeltas(beforeReplay, afterReplay),
                    NoCollectionWrites,
                    NoCollectionWrites);
                var unrelatedAfter =
                    await FingerprintP808UnrelatedJobsAsync(jobId, ct);
                HarnessAssert.Equal(unrelatedBefore, unrelatedAfter,
                    "Cleanup changed an unrelated job");
                expired["externalStatus"] = "DONE";
                RecordP808QueueTrace(
                    "P8-RACE-014", jobId, expired);
                await EvidenceJson.WriteAsync(
                    Path.Combine(_paths.RunRoot,
                        "p8-race-cleanup-ownership.json"),
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
                            Encoding.UTF8.GetBytes(canonical))
                    },
                    ct);
                return new CaseObservation(
                    "Cleanup rolled back a deterministic transient fault, retried against exactly one expired owned row and returned the durable response on a zero-write replay.",
                    "fault=500/rollback;expiredOwned=1;selected=1;deleted=1;unrelatedDelta=0;replay=sameResponse;replayDelta=0;resultDelta=0");
            },
            ct);
    }
}
