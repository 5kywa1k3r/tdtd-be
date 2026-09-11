using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private string? _p808DeadLetterJobId;

    private async Task RunP808RetryLeaseAndDeadLetterCasesAsync(
        CancellationToken ct)
    {
        await RunP808TransientRetryCaseAsync(ct);
        await RunP808ExpiredLeaseReclaimCaseAsync(ct);
        await RunP808HeartbeatLeaseExclusionCaseAsync(ct);
        await RunP808DeadLetterCaseAsync(ct);
        await RunP808ResetQuotaNeutralCaseAsync(ct);
    }

    private async Task RunP808TransientRetryCaseAsync(
        CancellationToken ct)
    {
        const string cancelCommand = "p808-retry-cancel-006";
        await RunEvidenceCaseAsync(
            "P8-OPS-006",
            "system_admin",
            [P808AtomicFaultCommand, cancelCommand],
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
                var owner = RequireP808Owner("retry");
                var enqueue = await EnqueueP808JobAsync(
                    actor,
                    owner.OwnerKind,
                    owner.OwnerId,
                    P808EnqueueEnvelope(P808AtomicFaultCommand, owner),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    enqueue,
                    HttpStatusCode.OK,
                    "P8 transient retry enqueue");
                var identity = ParseP808JobIdentity(
                    enqueue.Json,
                    "P8 transient retry enqueue response");
                var pending = await RequireP808JobAsync(identity.JobId, ct);

                var process = await ProcessP808JobsAsync(actor, 1, ct);
                ApiHarnessClient.ExpectStatus(
                    process,
                    HttpStatusCode.OK,
                    "P8 transient retry worker process");
                HarnessAssert.Equal(
                    1,
                    ApiHarnessClient.FindIntRecursive(
                        process.Json,
                        "retrying"),
                    "P8 transient fault did not schedule one retry");
                var retry = await RequireP808JobAsync(identity.JobId, ct);
                HarnessAssert.Equal(
                    "RETRY_WAITING",
                    BsonString(retry, "status"),
                    "P8 transient fault did not persist RETRY_WAITING");
                HarnessAssert.Equal(
                    1,
                    BsonInt(retry, "retryCount"),
                    "P8 transient retry count mismatch");
                var lastRunAt = RequireP808UtcDate(retry, "lastRunAtUtc");
                var nextRetryAt = RequireP808UtcDate(
                    retry,
                    "nextRetryAtUtc");
                HarnessAssert.True(
                    nextRetryAt > lastRunAt,
                    "P8 retry backoff was not scheduled after last run.");
                HarnessAssert.True(
                    nextRetryAt - lastRunAt <= TimeSpan.FromMinutes(5),
                    "P8 retry backoff exceeded the frozen bounded window.");

                var stateRevision = BsonLong(retry, "stateRevision")
                                    ?? throw new InvalidOperationException(
                                        "P8 retry job lacks stateRevision.");
                var stateHash = BsonString(retry, "stateHash")
                                ?? throw new InvalidOperationException(
                                    "P8 retry job lacks stateHash.");
                var cancel = await CancelP808JobAsync(
                    actor,
                    identity.JobId,
                    P808StateEnvelope(
                        cancelCommand,
                        stateRevision,
                        stateHash,
                        new JsonObject
                        {
                            ["reason"] =
                                "P8-OPS-006 release active retry fixture"
                        }),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    cancel,
                    HttpStatusCode.OK,
                    "P8 transient retry fixture cancel");
                var cancelledIdentity = ParseP808JobIdentity(
                    cancel.Json,
                    "P8 transient retry cancel response");
                HarnessAssert.Equal(
                    "CANCELLED",
                    cancelledIdentity.ExternalStatus,
                    "P8 transient retry fixture was not cancelled");
                var cancelled = await RequireP808JobAsync(
                    identity.JobId,
                    ct);
                HarnessAssert.Equal(
                    false,
                    BsonBool(cancelled, "isActive"),
                    "Cancelled retry fixture remained active");
                pending["externalStatus"] = "QUEUED";
                retry["externalStatus"] = "RETRYING";
                cancelled["externalStatus"] = "CANCELLED";
                RecordP808QueueTrace(
                    "P8-OPS-006",
                    identity.JobId,
                    pending,
                    retry,
                    cancelled);

                return new CaseObservation(
                    "A deterministic transient validation failure persisted one RETRY_WAITING attempt with a positive bounded backoff; CAS cancellation then released the Testing active-job quota without creating result work.",
                    "fault=VALIDATION_TRANSIENT;retryCount=1;backoffPositive=1;backoffBounded=1;final=CANCELLED;resultDelta=0");
            },
            ct);
    }

    private async Task RunP808ExpiredLeaseReclaimCaseAsync(
        CancellationToken ct)
    {
        const string commandId = "p808-expired-lease-007";
        await RunEvidenceCaseAsync(
            "P8-OPS-007",
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
                var owner = RequireP808Owner("lease");
                var enqueue = await EnqueueP808JobAsync(
                    actor,
                    owner.OwnerKind,
                    owner.OwnerId,
                    P808EnqueueEnvelope(commandId, owner),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    enqueue,
                    HttpStatusCode.OK,
                    "P8 expired-lease enqueue");
                var identity = ParseP808JobIdentity(
                    enqueue.Json,
                    "P8 expired-lease enqueue response");
                var expiredAt = DateTime.UtcNow.AddMinutes(-2);
                var jobs = _database.GetCollection<BsonDocument>(
                    P808JobsCollection);
                var retiredToken = "p808-retired-claim-007";
                var retiredWorker = "p808-retired-worker-007";
                var update = await jobs.UpdateOneAsync(
                    P808IdFilter(identity.JobId),
                    Builders<BsonDocument>.Update
                        .Set("status", "RUNNING")
                        .Set("isActive", true)
                        .Set("claimToken", retiredToken)
                        .Set("leaseOwnerId", retiredWorker)
                        .Set("leaseUntilUtc", expiredAt)
                        .Set("lastHeartbeatAtUtc", expiredAt),
                    cancellationToken: ct);
                HarnessAssert.Equal(
                    1L,
                    update.ModifiedCount,
                    "P8 expired lease fixture update failed");
                var expired = await RequireP808JobAsync(
                    identity.JobId,
                    ct);

                var process = await ProcessP808JobsAsync(actor, 1, ct);
                ApiHarnessClient.ExpectStatus(
                    process,
                    HttpStatusCode.OK,
                    "P8 expired-lease reclaim");
                HarnessAssert.Equal(
                    1,
                    ApiHarnessClient.FindIntRecursive(process.Json, "claimed"),
                    "P8 expired lease was not claimed exactly once");
                HarnessAssert.Equal(
                    1,
                    ApiHarnessClient.FindIntRecursive(
                        process.Json,
                        "completed"),
                    "P8 reclaimed lease did not complete");
                var after = await RequireP808JobAsync(identity.JobId, ct);
                HarnessAssert.Equal(
                    "COMPLETED",
                    BsonString(after, "status"),
                    "P8 expired lease reclaim did not complete job");
                HarnessAssert.Equal(
                    false,
                    BsonBool(after, "isActive"),
                    "P8 reclaimed completed job remained active");
                HarnessAssert.True(
                    !string.Equals(
                        retiredToken,
                        BsonString(after, "claimToken"),
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        retiredWorker,
                        BsonString(after, "leaseOwnerId"),
                        StringComparison.Ordinal),
                    "P8 reclaim retained the retired claim identity.");
                var noSecondClaim = await ProcessP808JobsAsync(actor, 1, ct);
                ApiHarnessClient.ExpectStatus(
                    noSecondClaim,
                    HttpStatusCode.OK,
                    "P8 expired-lease second claim probe");
                HarnessAssert.Equal(
                    0,
                    ApiHarnessClient.FindIntRecursive(
                        noSecondClaim.Json,
                        "claimed"),
                    "P8 completed reclaimed job was claimed twice");
                expired["externalStatus"] = "RUNNING";
                after["externalStatus"] = "DONE";
                RecordP808QueueTrace(
                    "P8-OPS-007",
                    identity.JobId,
                    expired,
                    after);

                return new CaseObservation(
                    "A persisted RUNNING lease owned by a retired worker was reclaimed only after expiry, replaced the stale claim identity, completed once and was not claimable again.",
                    "retiredLeaseExpired=1;claimed=1;completed=1;staleClaimReplaced=1;secondClaim=0;resultDelta=0");
            },
            ct);
    }

    private async Task RunP808HeartbeatLeaseExclusionCaseAsync(
        CancellationToken ct)
    {
        const string commandId = "p808-heartbeat-lease-008";
        await RunEvidenceCaseAsync(
            "P8-OPS-008",
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
                var owner = RequireP808Owner("heartbeat");
                var enqueue = await EnqueueP808JobAsync(
                    actor,
                    owner.OwnerKind,
                    owner.OwnerId,
                    P808EnqueueEnvelope(commandId, owner),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    enqueue,
                    HttpStatusCode.OK,
                    "P8 heartbeat lease enqueue");
                var identity = ParseP808JobIdentity(
                    enqueue.Json,
                    "P8 heartbeat lease enqueue response");
                var heartbeatAt = DateTime.UtcNow;
                var leaseUntil = heartbeatAt.AddMinutes(2);
                var leaseOwner = "p808-live-worker-008";
                var claimToken = "p808-live-claim-008";
                var jobs = _database.GetCollection<BsonDocument>(
                    P808JobsCollection);
                var update = await jobs.UpdateOneAsync(
                    P808IdFilter(identity.JobId),
                    Builders<BsonDocument>.Update
                        .Set("status", "RUNNING")
                        .Set("isActive", true)
                        .Set("claimToken", claimToken)
                        .Set("leaseOwnerId", leaseOwner)
                        .Set("leaseUntilUtc", leaseUntil)
                        .Set("lastHeartbeatAtUtc", heartbeatAt),
                    cancellationToken: ct);
                HarnessAssert.Equal(
                    1L,
                    update.ModifiedCount,
                    "P8 live heartbeat fixture update failed");
                var live = await RequireP808JobAsync(identity.JobId, ct);

                var release = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var rivals = Enumerable.Range(0, 2)
                    .Select(async _ =>
                    {
                        await release.Task.WaitAsync(ct);
                        return await ProcessP808JobsAsync(actor, 1, ct);
                    })
                    .ToArray();
                release.SetResult(true);
                var rivalResponses = await Task.WhenAll(rivals);
                foreach (var response in rivalResponses)
                {
                    ApiHarnessClient.ExpectStatus(
                        response,
                        HttpStatusCode.OK,
                        "P8 live-lease rival process");
                }
                HarnessAssert.Equal(
                    0,
                    rivalResponses.Sum(response =>
                        ApiHarnessClient.FindIntRecursive(
                            response.Json,
                            "claimed") ?? -100),
                    "A rival claimed the live heartbeat lease");
                var stillLive = await RequireP808JobAsync(
                    identity.JobId,
                    ct);
                HarnessAssert.Equal(
                    "RUNNING",
                    BsonString(stillLive, "status"),
                    "Rival process changed the live lease status");
                HarnessAssert.Equal(
                    leaseOwner,
                    BsonString(stillLive, "leaseOwnerId"),
                    "Rival process replaced the live lease owner");
                HarnessAssert.Equal(
                    claimToken,
                    BsonString(stillLive, "claimToken"),
                    "Rival process replaced the live claim token");
                HarnessAssert.Equal(
                    RequireP808UtcDate(live, "lastHeartbeatAtUtc"),
                    RequireP808UtcDate(stillLive, "lastHeartbeatAtUtc"),
                    "Rival process changed the persisted heartbeat");

                await jobs.UpdateOneAsync(
                    P808IdFilter(identity.JobId),
                    Builders<BsonDocument>.Update.Set(
                        "leaseUntilUtc",
                        DateTime.UtcNow.AddSeconds(-1)),
                    cancellationToken: ct);
                var reclaim = await ProcessP808JobsAsync(actor, 1, ct);
                ApiHarnessClient.ExpectStatus(
                    reclaim,
                    HttpStatusCode.OK,
                    "P8 live lease post-expiry reclaim");
                HarnessAssert.Equal(
                    1,
                    ApiHarnessClient.FindIntRecursive(
                        reclaim.Json,
                        "completed"),
                    "P8 live lease did not complete after expiry");
                var completed = await RequireP808JobAsync(
                    identity.JobId,
                    ct);
                live["externalStatus"] = "RUNNING";
                completed["externalStatus"] = "DONE";
                RecordP808QueueTrace(
                    "P8-OPS-008",
                    identity.JobId,
                    live,
                    completed);

                return new CaseObservation(
                    "Two concurrent rival worker calls could not claim a future lease with a persisted heartbeat and single owner; after exact expiry, one worker reclaimed and completed it.",
                    "liveLeaseOwner=1;rivals=2;rivalClaims=0;heartbeatStable=1;postExpiryCompleted=1;resultDelta=0");
            },
            ct);
    }

    private async Task RunP808DeadLetterCaseAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-009",
            "system_admin",
            [P808AtomicFaultCommand],
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
                var owner = RequireP808Owner("timeout");
                var enqueue = await EnqueueP808JobAsync(
                    actor,
                    owner.OwnerKind,
                    owner.OwnerId,
                    P808EnqueueEnvelope(P808AtomicFaultCommand, owner),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    enqueue,
                    HttpStatusCode.OK,
                    "P8 timeout dead-letter enqueue");
                var identity = ParseP808JobIdentity(
                    enqueue.Json,
                    "P8 timeout dead-letter enqueue response");
                var pending = await RequireP808JobAsync(identity.JobId, ct);
                var maxRetryCount = BsonInt(pending, "maxRetryCount")
                                    ?? throw new InvalidOperationException(
                                        "P8 dead-letter job lacks maxRetryCount.");
                HarnessAssert.True(
                    maxRetryCount > 0,
                    "P8 dead-letter retry bound is not positive.");
                var jobs = _database.GetCollection<BsonDocument>(
                    P808JobsCollection);
                var setup = await jobs.UpdateOneAsync(
                    P808IdFilter(identity.JobId),
                    Builders<BsonDocument>.Update
                        .Set("status", "RETRY_WAITING")
                        .Set("isActive", true)
                        .Set("retryCount", maxRetryCount - 1)
                        .Set("nextRetryAtUtc", DateTime.UtcNow.AddSeconds(-1))
                        .Set("safeCode", "VALIDATION_TIMEOUT")
                        .Set(
                            "safeMessage",
                            "Readiness validation timed out and reached its bounded retry cap.")
                        .Set("diagnosticCode", "VALIDATION_TIMEOUT")
                        .Set("diagnosticMessage", "bounded timeout fixture")
                        .Unset("claimToken")
                        .Unset("leaseOwnerId")
                        .Unset("leaseUntilUtc"),
                    cancellationToken: ct);
                HarnessAssert.Equal(
                    1L,
                    setup.ModifiedCount,
                    "P8 bounded timeout fixture setup failed");
                var exhausted = await RequireP808JobAsync(
                    identity.JobId,
                    ct);

                var process = await ProcessP808JobsAsync(actor, 1, ct);
                ApiHarnessClient.ExpectStatus(
                    process,
                    HttpStatusCode.OK,
                    "P8 bounded timeout dead-letter process");
                HarnessAssert.Equal(
                    1,
                    ApiHarnessClient.FindIntRecursive(process.Json, "failed"),
                    "P8 bounded timeout did not dead-letter one job");
                var failed = await RequireP808JobAsync(identity.JobId, ct);
                HarnessAssert.Equal(
                    "FAILED",
                    BsonString(failed, "status"),
                    "P8 bounded timeout did not persist FAILED");
                HarnessAssert.Equal(
                    false,
                    BsonBool(failed, "isActive"),
                    "P8 dead-letter job remained active");
                HarnessAssert.Equal(
                    maxRetryCount,
                    BsonInt(failed, "retryCount"),
                    "P8 dead-letter retry count exceeded its bound");
                _ = RequireP808UtcDate(failed, "failedAtUtc");
                _ = RequireP808UtcDate(failed, "deadLetterAtUtc");
                RequireP808LowerSha(
                    BsonString(failed, "failureFingerprint"),
                    "dead-letter failureFingerprint");
                _p808DeadLetterJobId = identity.JobId;
                pending["externalStatus"] = "QUEUED";
                exhausted["externalStatus"] = "RETRYING";
                failed["externalStatus"] = "FAILED";
                RecordP808QueueTrace(
                    "P8-OPS-009",
                    identity.JobId,
                    pending,
                    exhausted,
                    failed);

                return new CaseObservation(
                    "A direct, disclosed precondition represented repeated validation timeouts at the frozen retry cap; the deterministic terminal fault then persisted inactive FAILED/dead-letter timestamps and a stable failure fingerprint.",
                    $"timeoutPrecondition=directMongo;retryCount={maxRetryCount};maxRetryCount={maxRetryCount};terminalFault=VALIDATION_PERMANENT;status=FAILED;deadLetter=1;resultDelta=0");
            },
            ct);
    }

    private async Task RunP808ResetQuotaNeutralCaseAsync(
        CancellationToken ct)
    {
        const string resetCommand = "p808-dead-letter-reset-010";
        await RunEvidenceCaseAsync(
            "P8-OPS-010",
            "system_admin",
            [resetCommand],
            [
                P808JobsCollection,
                ReceiptsCollection,
                P808AuditOutboxCollection
            ],
            [P808JobsCollection],
            async () =>
            {
                var actor = Actor("system_admin");
                var jobId = _p808DeadLetterJobId
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8-OPS-009 did not produce a dead-letter job.");
                var failed = await RequireP808JobAsync(jobId, ct);
                var priorResetCount = BsonInt(failed, "resetCount") ?? 0;
                var stateRevision = BsonLong(failed, "stateRevision")
                                    ?? throw new InvalidOperationException(
                                        "P8 dead-letter job lacks stateRevision.");
                var stateHash = BsonString(failed, "stateHash")
                                ?? throw new InvalidOperationException(
                                    "P8 dead-letter job lacks stateHash.");
                var tokenBefore = await _database
                    .GetCollection<BsonDocument>(TokenLedgersCollection)
                    .CountDocumentsAsync(
                        FilterDefinition<BsonDocument>.Empty,
                        cancellationToken: ct);
                var reset = await ResetP808JobAsync(
                    actor,
                    jobId,
                    P808StateEnvelope(
                        resetCommand,
                        stateRevision,
                        stateHash,
                        new JsonObject
                        {
                            ["reason"] =
                                "P8-OPS-010 controlled dead-letter reset"
                        }),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    reset,
                    HttpStatusCode.OK,
                    "P8 readiness dead-letter reset");
                var resetIdentity = ParseP808JobIdentity(
                    reset.Json,
                    "P8 readiness reset response");
                HarnessAssert.Equal(
                    "RESET",
                    resetIdentity.ExternalStatus,
                    "P8 dead-letter reset external state mismatch");
                var resetDirect = await RequireP808JobAsync(jobId, ct);
                HarnessAssert.Equal(
                    "RESET",
                    BsonString(resetDirect, "status"),
                    "P8 dead-letter reset internal state mismatch");
                HarnessAssert.Equal(
                    true,
                    BsonBool(resetDirect, "isActive"),
                    "P8 reset job is not active");
                HarnessAssert.Equal(
                    0,
                    BsonInt(resetDirect, "retryCount"),
                    "P8 reset did not clear retry count");
                HarnessAssert.Equal(
                    priorResetCount + 1,
                    BsonInt(resetDirect, "resetCount"),
                    "P8 reset count mismatch");

                var process = await ProcessP808JobsAsync(actor, 1, ct);
                ApiHarnessClient.ExpectStatus(
                    process,
                    HttpStatusCode.OK,
                    "P8 readiness reset worker process");
                HarnessAssert.Equal(
                    1,
                    ApiHarnessClient.FindIntRecursive(
                        process.Json,
                        "completed"),
                    "P8 reset job did not complete");
                var doneResponse = await ReadP808SafeJobAsync(
                    actor,
                    jobId,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    doneResponse,
                    HttpStatusCode.OK,
                    "P8 reset DONE read");
                var doneIdentity = ParseP808JobIdentity(
                    doneResponse.Json,
                    "P8 reset DONE response");
                HarnessAssert.Equal(
                    "DONE",
                    doneIdentity.ExternalStatus,
                    "P8 reset job did not map to DONE");
                var done = await RequireP808JobAsync(jobId, ct);
                HarnessAssert.Equal(
                    tokenBefore,
                    await _database
                        .GetCollection<BsonDocument>(TokenLedgersCollection)
                        .CountDocumentsAsync(
                            FilterDefinition<BsonDocument>.Empty,
                            cancellationToken: ct),
                    "System retry/reset consumed Advanced quota token");
                failed["externalStatus"] = "FAILED";
                resetDirect["externalStatus"] = "RESET";
                done["externalStatus"] = "DONE";
                RecordP808QueueTrace(
                    "P8-OPS-010",
                    jobId,
                    failed,
                    resetDirect,
                    done);

                return new CaseObservation(
                    "System-admin CAS reset reactivated the bounded dead-letter job with retry count zero and incremented reset metadata; it then completed without consuming an active-job enqueue slot or Advanced token quota.",
                    "FAILED->RESET->DONE;resetCountDelta=1;retryCount=0;completed=1;advancedTokenDelta=0;resultDelta=0");
            },
            ct);
    }

    private static DateTime RequireP808UtcDate(
        BsonDocument document,
        string field)
    {
        HarnessAssert.True(
            document.TryGetValue(field, out var value) &&
            value.BsonType == BsonType.DateTime,
            $"P8 readiness document lacks UTC date '{field}'.");
        return value.AsBsonDateTime.ToUniversalTime();
    }
}
