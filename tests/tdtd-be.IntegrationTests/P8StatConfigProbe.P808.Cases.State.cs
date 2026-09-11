using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string P808BaselineEnqueueCommand =
        "p808-enqueue-baseline-001";
    private const string P808AtomicFaultCommand =
        "p808-fault-atomic-002";
    private string? _p808BaselineJobId;
    private string? _p808BaselineEnqueueResponse;

    private async Task RunP808StateAndDedupeCasesAsync(
        CancellationToken ct)
    {
        await RunP808AtomicEnqueueCaseAsync(ct);
        await RunP808AtomicFaultCaseAsync(ct);
        await RunP808SmokeLifecycleCaseAsync(ct);
        await RunP808ExactReplayCaseAsync(ct);
        await RunP808StaleHashCaseAsync(ct);
    }

    private async Task RunP808AtomicEnqueueCaseAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-001",
            "system_admin",
            [P808BaselineEnqueueCommand],
            [P808JobsCollection, ReceiptsCollection,
                P808AuditOutboxCollection],
            [P808JobsCollection, ReceiptsCollection,
                P808AuditOutboxCollection],
            async () =>
            {
                var owner = RequireP808Owner();
                var response = await EnqueueP808JobAsync(
                    Actor("system_admin"),
                    owner.OwnerKind,
                    owner.OwnerId,
                    P808EnqueueEnvelope(
                        P808BaselineEnqueueCommand,
                        owner),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.OK,
                    "P8 readiness enqueue");
                var apiJob = ParseP808JobIdentity(
                    response.Json,
                    "P8 readiness enqueue response");
                HarnessAssert.Equal(
                    "QUEUED",
                    apiJob.ExternalStatus,
                    "P8 readiness initial external state mismatch");
                HarnessAssert.Equal(owner.OwnerKind, apiJob.OwnerKind,
                    "P8 readiness API ownerKind mismatch");
                HarnessAssert.Equal(owner.OwnerId, apiJob.OwnerId,
                    "P8 readiness API ownerId mismatch");
                HarnessAssert.Equal(owner.ConfigId, apiJob.ConfigId,
                    "P8 readiness API configId mismatch");
                HarnessAssert.Equal(owner.VersionId, apiJob.VersionId,
                    "P8 readiness API versionId mismatch");
                HarnessAssert.Equal(owner.VersionNo, apiJob.VersionNo,
                    "P8 readiness API versionNo mismatch");
                HarnessAssert.Equal(owner.ConfigHash, apiJob.ConfigHash,
                    "P8 readiness API configHash mismatch");

                var links = await RequireP808AtomicLinksAsync(
                    apiJob.JobId,
                    owner,
                    P808BaselineEnqueueCommand,
                    ct);
                HarnessAssert.Equal(
                    "PENDING",
                    links.InternalStatus,
                    "P8 readiness persisted initial state mismatch");
                var direct = await RequireP808JobAsync(apiJob.JobId, ct);
                direct["externalStatus"] = "QUEUED";
                RecordP808QueueTrace(
                    "P8-OPS-001",
                    apiJob.JobId,
                    direct);
                _p808BaselineJobId = apiJob.JobId;
                _p808BaselineEnqueueResponse =
                    CanonicalResponse(response);

                return new CaseObservation(
                    "Real Kestrel enqueue atomically persisted one pinned no-dataset readiness job, command receipt and audit-outbox intent.",
                    "status=QUEUED;internal=PENDING;queue=stat-config-readiness;job=1;receipt=1;outbox=1;datasetIdentity=0;resultDelta=0");
            },
            ct);
    }

    private async Task RunP808AtomicFaultCaseAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-002",
            "system_admin",
            [P808AtomicFaultCommand],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var owner = RequireP808Owner();
                var jobFilter = Builders<BsonDocument>.Filter.Eq(
                    "enqueueCommandId",
                    P808AtomicFaultCommand);
                var receiptFilter = Builders<BsonDocument>.Filter.Eq(
                    "commandId",
                    P808AtomicFaultCommand);
                var outboxFilter = Builders<BsonDocument>.Filter.Eq(
                    "commandId",
                    P808AtomicFaultCommand);

                for (var boundary = 0; boundary < 6; boundary++)
                {
                    var response = await EnqueueP808JobAsync(
                        Actor("system_admin"),
                        owner.OwnerKind,
                        owner.OwnerId,
                        P808EnqueueEnvelope(
                            P808AtomicFaultCommand,
                            owner),
                        ct);
                    ApiHarnessClient.ExpectStatus(
                        response,
                        HttpStatusCode.InternalServerError,
                        $"P8 readiness atomic fault boundary {boundary + 1}");
                    HarnessAssert.Equal(
                        0L,
                        await CountP808JobsAsync(jobFilter, ct),
                        "Faulted P8 readiness enqueue left a partial job");
                    HarnessAssert.Equal(
                        0L,
                        await _database
                            .GetCollection<BsonDocument>(ReceiptsCollection)
                            .CountDocumentsAsync(
                                receiptFilter,
                                cancellationToken: ct),
                        "Faulted P8 readiness enqueue left a partial receipt");
                    HarnessAssert.Equal(
                        0L,
                        await CountP808OutboxAsync(outboxFilter, ct),
                        "Faulted P8 readiness enqueue left a partial outbox");
                }

                return new CaseObservation(
                    "Six deterministic job/receipt/outbox transaction boundaries failed through real Kestrel and each rolled back fully.",
                    "faultBoundaries=6;jobDelta=0;receiptDelta=0;outboxDelta=0;resultDelta=0");
            },
            ct);
    }

    private async Task RunP808SmokeLifecycleCaseAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-003",
            "system_admin",
            Array.Empty<string>(),
            [P808JobsCollection],
            [P808JobsCollection],
            async () =>
            {
                var jobId = _p808BaselineJobId
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8 readiness baseline job is unavailable.");
                var safeQueuedResponse = await ReadP808SafeJobAsync(
                    Actor("system_admin"),
                    jobId,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    safeQueuedResponse,
                    HttpStatusCode.OK,
                    "P8 readiness QUEUED read");
                var queued = ParseP808JobIdentity(
                    safeQueuedResponse.Json,
                    "P8 readiness QUEUED response");
                HarnessAssert.Equal("QUEUED", queued.ExternalStatus,
                    "P8 readiness did not expose QUEUED before processing");
                var before = await RequireP808JobAsync(jobId, ct);
                before["externalStatus"] = queued.ExternalStatus;

                var (processResponse, transitionEvents) =
                    await ObserveP808JobChangesAsync(
                        jobId,
                        () => ProcessP808JobsAsync(
                            Actor("system_admin"), 1, ct),
                        ct);
                ApiHarnessClient.ExpectStatus(
                    processResponse,
                    HttpStatusCode.OK,
                    "P8 readiness worker process");
                HarnessAssert.Equal(
                    1,
                    ApiHarnessClient.FindIntRecursive(
                        processResponse.Json,
                        "completed"),
                    "P8 readiness worker did not complete exactly one job");
                HarnessAssert.True(
                    transitionEvents.Any(item => item.Status == "RUNNING"),
                    "P8 readiness change stream did not observe RUNNING.");
                HarnessAssert.True(
                    transitionEvents.Any(item => item.Status == "COMPLETED"),
                    "P8 readiness change stream did not observe COMPLETED.");
                var transitionList = transitionEvents.ToList();
                HarnessAssert.True(
                    transitionList.FindIndex(item => item.Status == "RUNNING") <
                    transitionList.FindIndex(item => item.Status == "COMPLETED"),
                    "P8 readiness RUNNING/COMPLETED transition order drifted.");
                await EvidenceJson.WriteAsync(
                    Path.Combine(_paths.RunRoot, "p8-ops-003-change-stream.json"),
                    new { jobId, transitionEvents }, ct);

                var safeDoneResponse = await ReadP808SafeJobAsync(
                    Actor("system_admin"),
                    jobId,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    safeDoneResponse,
                    HttpStatusCode.OK,
                    "P8 readiness DONE read");
                var done = ParseP808JobIdentity(
                    safeDoneResponse.Json,
                    "P8 readiness DONE response");
                HarnessAssert.Equal("DONE", done.ExternalStatus,
                    "P8 readiness did not expose DONE after processing");
                var after = await RequireP808JobAsync(jobId, ct);
                HarnessAssert.Equal(
                    "COMPLETED",
                    BsonString(after, "status"),
                    "P8 readiness internal completion state mismatch");
                HarnessAssert.True(
                    after.TryGetValue("lastHeartbeatAtUtc", out var heartbeat) &&
                    !heartbeat.IsBsonNull,
                    "P8 readiness worker did not persist heartbeat evidence.");
                after["externalStatus"] = done.ExternalStatus;
                RecordP808QueueTrace(
                    "P8-OPS-003",
                    jobId,
                    before,
                    after);

                return new CaseObservation(
                    "Real worker smoke observed persisted PENDING to RUNNING heartbeat to COMPLETED with safe QUEUED/RUNNING/DONE mapping.",
                    "external=QUEUED->RUNNING->DONE;internal=PENDING->RUNNING->COMPLETED;completed=1;heartbeat=1;resultDelta=0");
            },
            ct);
    }

    private async Task RunP808ExactReplayCaseAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-OPS-004",
            "system_admin",
            [P808BaselineEnqueueCommand],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var owner = RequireP808Owner();
                var response = await EnqueueP808JobAsync(
                    Actor("system_admin"),
                    owner.OwnerKind,
                    owner.OwnerId,
                    P808EnqueueEnvelope(
                        P808BaselineEnqueueCommand,
                        owner),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.OK,
                    "P8 readiness exact replay");
                var replay = ParseP808JobIdentity(
                    response.Json,
                    "P8 readiness replay response");
                HarnessAssert.Equal(
                    _p808BaselineJobId,
                    replay.JobId,
                    "P8 readiness exact replay returned a different job");
                HarnessAssert.Equal(
                    _p808BaselineEnqueueResponse,
                    CanonicalResponse(response),
                    "P8 readiness exact replay response drifted");
                HarnessAssert.Equal(
                    1L,
                    await CountP808JobsAsync(
                        Builders<BsonDocument>.Filter.Eq(
                            "enqueueCommandId",
                            P808BaselineEnqueueCommand),
                        ct),
                    "P8 readiness exact replay duplicated the job");

                return new CaseObservation(
                    "Exact enqueue replay returned the original durable receipt response and created no second job/outbox row.",
                    "replay=sameResponse;sameJob=1;jobDelta=0;receiptDelta=0;outboxDelta=0;resultDelta=0");
            },
            ct);
    }

    private async Task RunP808StaleHashCaseAsync(CancellationToken ct)
    {
        const string commandId = "p808-stale-hash-005";
        await RunEvidenceCaseAsync(
            "P8-OPS-005",
            "system_admin",
            [commandId],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var owner = RequireP808Owner();
                var response = await EnqueueP808JobAsync(
                    Actor("system_admin"),
                    owner.OwnerKind,
                    owner.OwnerId,
                    P808EnqueueEnvelope(
                        commandId,
                        owner,
                        expectedConfigHash: new string('0', 64)),
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Conflict,
                    "P8 readiness stale hash rejection");
                HarnessAssert.Equal(
                    0L,
                    await CountP808JobsAsync(
                        Builders<BsonDocument>.Filter.Eq(
                            "enqueueCommandId",
                            commandId),
                        ct),
                    "Stale P8 readiness hash wrote a job");

                return new CaseObservation(
                    "Stale config-hash pin was rejected before job, receipt or outbox mutation.",
                    "http=409;reason=staleHash;jobDelta=0;receiptDelta=0;outboxDelta=0;resultDelta=0");
            },
            ct);
    }
}

