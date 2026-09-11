using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    private static readonly string[] RequiredP10IndexNames =
    [
        "ix_statisticReconciliations_isDeleted",
        "ux_statisticReconciliations_receipt",
        "ux_statisticReconciliations_identity",
        "ix_statisticReconciliations_scope_list",
        "ix_statisticReconciliations_p9_result",
        "ix_statisticReconciliations_claim",
        "ix_statisticReconciliations_cleanup",
        "ix_statisticReconciliations_operation_receipt"
    ];

    private async Task RunJobCasesAsync(CancellationToken ct)
    {
        await CancelOpenRunsAsync("p10-pre-job", ct);

        await RunCaseAsync(
            "P10-JOB-01",
            Req("P10-REC-004", "P10-REC-005"),
            O(
                "API_KESTREL",
                "DIRECT_MONGO",
                "RECEIPT",
                "QUEUE_TRACE",
                "INDEX_EXPLAIN"),
            async () =>
            {
                var request = NewCreateRequest("p10-job-accepted-001");
                request["conceptKey"] = "JOB_ACCEPTED";
                var response = await CreateRunAsync(
                    request,
                    Actor("executor").Token,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Accepted,
                    "P10 JOB-01 accepted create");
                _jobRunId = RequireResponseString(
                    response,
                    "reconciliationId");
                var run = await LoadRunAsync(_jobRunId, ct);
                HarnessAssert.Equal(
                    "QUEUED",
                    BsonText(run, "status"),
                    "P10 JOB-01 queued status");
                HarnessAssert.Equal(
                    1L,
                    BsonLong(run, "stateRevision"),
                    "P10 JOB-01 initial state revision");

                var collection = RequireDatabase()
                    .GetCollection<BsonDocument>(RunCollection);
                var indexes = await (await collection.Indexes.ListAsync(ct))
                    .ToListAsync(ct);
                var indexNames = indexes
                    .Select(index => index["name"].AsString)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                foreach (var required in RequiredP10IndexNames)
                {
                    HarnessAssert.True(
                        indexNames.Contains(required, StringComparer.Ordinal),
                        $"P10 required index is missing: {required}");
                }
                var explain = await RequireDatabase().RunCommandAsync<BsonDocument>(
                    new BsonDocument
                    {
                        ["explain"] = new BsonDocument
                        {
                            ["find"] = RunCollection,
                            ["filter"] = new BsonDocument(
                                "receiptId",
                                BsonText(run, "receiptId")),
                            ["hint"] =
                                "ux_statisticReconciliations_receipt"
                        },
                        ["verbosity"] = "executionStats"
                    },
                    cancellationToken: ct);
                var explainJson = explain.ToJson();
                HarnessAssert.True(
                    explainJson.Contains("IXSCAN", StringComparison.Ordinal) &&
                    explainJson.Contains(
                        "ux_statisticReconciliations_receipt",
                        StringComparison.Ordinal),
                    "P10 receipt lookup explain did not use the unique receipt index.");
                _indexEvidence = new
                {
                    schemaVersion = "P10_CORE_INDEX_EVIDENCE_V1",
                    chainId = ChainId,
                    promptId = PromptId,
                    groupId = GroupId,
                    runKey = _runKey,
                    collection = RunCollection,
                    indexNames,
                    requiredIndexNames = RequiredP10IndexNames,
                    exactRequiredIndexesPresent = true,
                    receiptExplain = new
                    {
                        stage = "IXSCAN",
                        indexName = "ux_statisticReconciliations_receipt",
                        sha256 = HashText(explainJson)
                    }
                };
                _receiptLeaseTrace.Add(new
                {
                    caseId = "P10-JOB-01",
                    reconciliationId = _jobRunId,
                    receiptId = BsonText(run, "receiptId"),
                    status = "QUEUED",
                    stateRevision = 1,
                    stateHash = BsonText(run, "stateHash")
                });
                return ObserveRun(
                    response,
                    run,
                    "Accepted create persisted one QUEUED run, durable receipt and all required indexes with receipt IXSCAN.",
                    "http=202;status=QUEUED;revision=1;indexes=8;receiptPlan=IXSCAN");
            });

        await RunCaseAsync(
            "P10-JOB-02",
            Req("P10-REC-004"),
            O("API_KESTREL", "DIRECT_MONGO", "RECEIPT"),
            async () =>
            {
                var request = NewCreateRequest("p10-job-accepted-001");
                request["conceptKey"] = "JOB_ACCEPTED";
                var before = await CountRunsAsync(ct);
                var first = await CreateRunAsync(
                    request,
                    Actor("executor").Token,
                    ct);
                var second = await CreateRunAsync(
                    request,
                    Actor("executor").Token,
                    ct);
                foreach (var response in new[] { first, second })
                {
                    ApiHarnessClient.ExpectStatus(
                        response,
                        HttpStatusCode.OK,
                        "P10 exact create replay");
                    HarnessAssert.Equal(
                        JobRunId(),
                        RequireResponseString(response, "reconciliationId"),
                        "P10 exact replay id");
                    HarnessAssert.True(
                        ApiHarnessClient.RequiredBool(
                            ApiHarnessClient.RequiredObject(
                                response.Json,
                                "P10 replay response"),
                            "isReplay"),
                        "P10 exact replay flag");
                }
                HarnessAssert.Equal(
                    before,
                    await CountRunsAsync(ct),
                    "P10 exact replay created another aggregate");
                var run = await LoadRunAsync(JobRunId(), ct);
                return ObserveRun(
                    second,
                    run,
                    "Repeated same-command/same-request calls converged on the original receipt and run without another write.",
                    "replays=2;http=200;sameId=true;runDelta=0;isReplay=true");
            });

        await RunCaseAsync(
            "P10-JOB-03",
            Req("P10-REC-004"),
            O("API_KESTREL", "DIRECT_MONGO", "RECEIPT", "COLLECTION_DELTA"),
            async () =>
            {
                var request = NewCreateRequest("p10-job-accepted-001");
                request["conceptKey"] = "JOB_CONFLICTING_REQUEST";
                var before = await CountRunsAsync(ct);
                var response = await CreateRunAsync(
                    request,
                    Actor("executor").Token,
                    ct);
                ExpectError(
                    response,
                    HttpStatusCode.Conflict,
                    "STAT_RECONCILIATION_COMMAND_REPLAY_MISMATCH",
                    "P10 conflicting create replay");
                HarnessAssert.Equal(
                    before,
                    await CountRunsAsync(ct),
                    "Conflicting replay changed run count");
                var run = await LoadRunAsync(JobRunId(), ct);
                return new P10CaseObservation(
                    409,
                    "Same command with a different canonical request was rejected with zero write.",
                    "sameCommand=true;sameRequest=false;http=409;runDelta=0",
                    RequestHashSha256: EvidenceRequestHash(
                        ReconciliationBasePath(),
                        request),
                    ReceiptHashSha256: BsonText(
                        run,
                        "receiptResponseHash"),
                    StateHashSha256: BsonText(run, "stateHash"));
            });

        await RunCaseAsync(
            "P10-JOB-04",
            Req("P10-REC-004"),
            O(
                "API_KESTREL",
                "DIRECT_MONGO",
                "RECEIPT",
                "QUEUE_TRACE",
                "COLLECTION_DELTA"),
            async () =>
            {
                var request = NewCreateRequest("p10-job-race-create-004");
                request["conceptKey"] = "JOB_CREATE_RACE";
                var before = await CountRunsAsync(ct);
                var responses = await Task.WhenAll(
                    Enumerable.Range(0, 8).Select(_ =>
                        CreateRunAsync(
                            request,
                            Actor("admin").Token,
                            ct)));
                HarnessAssert.Equal(
                    1,
                    responses.Count(response =>
                        response.StatusCode == HttpStatusCode.Accepted),
                    "P10 concurrent create winner count");
                HarnessAssert.Equal(
                    7,
                    responses.Count(response =>
                        response.StatusCode == HttpStatusCode.OK),
                    "P10 concurrent create replay count");
                var ids = responses.Select(response =>
                        RequireResponseString(response, "reconciliationId"))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                HarnessAssert.Equal(
                    1,
                    ids.Length,
                    "P10 concurrent create durable identities");
                HarnessAssert.Equal(
                    before + 1,
                    await CountRunsAsync(ct),
                    "P10 concurrent create durable count");

                var raceId = ids[0];
                var initial = await LoadRunAsync(raceId, ct);
                var expectedRevision = BsonLong(initial, "stateRevision");
                var expectedHash = BsonText(initial, "stateHash");
                var cancels = await Task.WhenAll(
                    new[]
                    {
                        RequireApi().PostAsync(
                            $"{ReconciliationBasePath()}/{raceId}/cancel",
                            new
                            {
                                commandId = "p10-job-race-cancel-a-004",
                                expectedStateRevision = expectedRevision,
                                expectedStateHash = expectedHash
                            },
                            Actor("admin").Token,
                            ct: ct),
                        RequireApi().PostAsync(
                            $"{ReconciliationBasePath()}/{raceId}/cancel",
                            new
                            {
                                commandId = "p10-job-race-cancel-b-004",
                                expectedStateRevision = expectedRevision,
                                expectedStateHash = expectedHash
                            },
                            Actor("admin").Token,
                            ct: ct)
                    });
                HarnessAssert.Equal(
                    1,
                    cancels.Count(response =>
                        response.StatusCode == HttpStatusCode.OK),
                    "P10 cancel CAS winner count");
                HarnessAssert.Equal(
                    1,
                    cancels.Count(response =>
                        response.StatusCode == HttpStatusCode.Conflict),
                    "P10 cancel CAS loser count");
                var winningCancelCommandId =
                    cancels[0].StatusCode == HttpStatusCode.OK
                        ? "p10-job-race-cancel-a-004"
                        : "p10-job-race-cancel-b-004";
                var cancelReplay = await RequireApi().PostAsync(
                    $"{ReconciliationBasePath()}/{raceId}/cancel",
                    new
                    {
                        commandId = winningCancelCommandId,
                        expectedStateRevision = expectedRevision,
                        expectedStateHash = expectedHash
                    },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    cancelReplay,
                    HttpStatusCode.OK,
                    "P10 cancel winner replay");
                var final = await LoadRunAsync(raceId, ct);
                HarnessAssert.Equal(
                    "CANCELLED",
                    BsonText(final, "status"),
                    "P10 cancel CAS terminal status");
                HarnessAssert.Equal(
                    1,
                    final["operationReceipts"].AsBsonArray.Count,
                    "P10 cancel CAS effect count");
                var acceptedReceipt = final["operationReceipts"]
                    .AsBsonArray[0]
                    .AsBsonDocument;
                HarnessAssert.Equal(
                    BsonText(final, "stateHash"),
                    BsonText(acceptedReceipt, "acceptedStateHash"),
                    "P10 cancel receipt accepted-state binding");
                HarnessAssert.Equal(
                    BsonLong(final, "stateRevision"),
                    BsonLong(acceptedReceipt, "acceptedStateRevision"),
                    "P10 cancel receipt accepted-revision binding");
                HarnessAssert.Equal(
                    BsonText(final, "status"),
                    BsonText(acceptedReceipt, "acceptedStatus"),
                    "P10 cancel receipt accepted-status binding");
                _receiptLeaseTrace.Add(new
                {
                    caseId = "P10-JOB-04",
                    createAttempts = 8,
                    durableWinners = 1,
                    createReplays = 7,
                    cancelCasWinners = 1,
                    cancelCasLosers = 1,
                    cancelReplayStatus = 200,
                    cancelReplayConverged = true,
                    finalStateHash = BsonText(final, "stateHash"),
                    finalStateRevision = BsonLong(final, "stateRevision"),
                    finalStatus = BsonText(final, "status"),
                    acceptedStateHash = BsonText(
                        acceptedReceipt,
                        "acceptedStateHash"),
                    acceptedStateRevision = BsonLong(
                        acceptedReceipt,
                        "acceptedStateRevision"),
                    acceptedStatus = BsonText(
                        acceptedReceipt,
                        "acceptedStatus"),
                    acceptedStateBound = true
                });
                return new P10CaseObservation(
                    200,
                    "Eight equal creates converged on one durable winner and two competing cancel CASes produced one effect and one loser.",
                    "createAttempts=8;durableWinner=1;replays=7;cancelWinner=1;casLoser=1",
                    ActorAlias: "admin",
                    RequestHashSha256: BsonText(final, "requestHash"),
                    ReceiptHashSha256: BsonText(
                        final,
                        "receiptResponseHash"),
                    StateHashSha256: BsonText(final, "stateHash"));
            });

        await RunCaseAsync(
            "P10-JOB-05",
            Req("P10-REC-004", "P10-REC-005"),
            O("API_KESTREL", "DIRECT_MONGO", "QUEUE_TRACE", "RECEIPT"),
            async () =>
            {
                var claimA = await ClaimAsync("p10-worker-a", ct);
                ApiHarnessClient.ExpectStatus(
                    claimA,
                    HttpStatusCode.OK,
                    "P10 worker A claim");
                HarnessAssert.Equal(
                    JobRunId(),
                    RequireResponseString(claimA, "reconciliationId"),
                    "P10 worker A claimed unexpected run");
                var tokenA = RequireResponseString(claimA, "claimToken");
                var heartbeat = await RequireApi().PostAsync(
                    $"api/testing/p10/statistic-reconciliations/jobs/{JobRunId()}/heartbeat",
                    new { workerId = "p10-worker-a", claimToken = tokenA },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    heartbeat,
                    HttpStatusCode.OK,
                    "P10 worker A heartbeat");

                var collection = RequireDatabase()
                    .GetCollection<BsonDocument>(RunCollection);
                var claimed = await LoadRunAsync(JobRunId(), ct);
                var expired = (BsonDocument)claimed.DeepClone();
                expired["leaseUntilUtc"] = ProbeNowUtc().AddSeconds(-1);
                expired["stateHash"] = ComputeStateHash(expired);
                await collection.ReplaceOneAsync(
                    new BsonDocument("_id", expired["_id"]),
                    expired,
                    cancellationToken: ct);

                var claimB = await ClaimAsync("p10-worker-b", ct);
                ApiHarnessClient.ExpectStatus(
                    claimB,
                    HttpStatusCode.NoContent,
                    "P10 worker B expired-lease requeue");
                var requeued = await LoadRunAsync(JobRunId(), ct);
                HarnessAssert.Equal(
                    "QUEUED",
                    BsonText(requeued, "status"),
                    "P10 expired lease did not requeue");
                var retryAt = requeued["nextRetryAtUtc"]
                    .AsBsonDateTime
                    .ToUniversalTime();
                var probeNow = ProbeNowUtc();
                HarnessAssert.True(
                    retryAt > probeNow,
                    "P10 expired lease did not apply retry backoff.");
                if (HasFixedClock())
                {
                    var ready = (BsonDocument)requeued.DeepClone();
                    ready["nextRetryAtUtc"] = probeNow.AddSeconds(-1);
                    ready["stateHash"] = ComputeStateHash(ready);
                    await collection.ReplaceOneAsync(
                        new BsonDocument("_id", ready["_id"]),
                        ready,
                        cancellationToken: ct);
                }
                else
                {
                    var retryWait = retryAt - DateTime.UtcNow +
                                    TimeSpan.FromMilliseconds(250);
                    if (retryWait > TimeSpan.Zero)
                        await Task.Delay(retryWait, ct);
                }
                claimB = await ClaimAsync("p10-worker-b", ct);
                ApiHarnessClient.ExpectStatus(
                    claimB,
                    HttpStatusCode.OK,
                    "P10 worker B reclaim after backoff");
                HarnessAssert.Equal(
                    JobRunId(),
                    RequireResponseString(claimB, "reconciliationId"),
                    "P10 worker B reclaimed unexpected run");
                var tokenB = RequireResponseString(claimB, "claimToken");
                HarnessAssert.True(
                    !string.Equals(tokenA, tokenB, StringComparison.Ordinal),
                    "P10 lease reclaim reused a claim token.");

                var stale = await RequireApi().PostAsync(
                    $"api/testing/p10/statistic-reconciliations/jobs/{JobRunId()}/heartbeat",
                    new { workerId = "p10-worker-a", claimToken = tokenA },
                    Actor("admin").Token,
                    ct: ct);
                ExpectError(
                    stale,
                    HttpStatusCode.Conflict,
                    "STAT_RECONCILIATION_JOB_CONFLICT",
                    "P10 stale worker heartbeat");

                var committed = await CommitActualGenerationAsync(
                    JobRunId(),
                    "p10-worker-b",
                    tokenB,
                    ct);
                var generationId = committed.GenerationId;
                var generationHash = committed.GenerationSemanticSha256;
                var publish = await RequireApi().PostAsync(
                    $"api/testing/p10/statistic-reconciliations/jobs/{JobRunId()}/publish-pending",
                    new
                    {
                        workerId = "p10-worker-b",
                        claimToken = tokenB,
                        generationId,
                        generationHash
                    },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    publish,
                    HttpStatusCode.OK,
                    "P10 pending generation publish");
                var summary = await RequireApi().GetAsync(
                    $"{ReconciliationBasePath()}/{JobRunId()}",
                    Actor("executor").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    summary,
                    HttpStatusCode.OK,
                    "P10 pending summary");
                HarnessAssert.True(
                    ApiHarnessClient.RequiredBool(
                        ApiHarnessClient.RequiredObject(
                            summary.Json,
                            "P10 pending summary"),
                        "hasPendingGeneration"),
                    "P10 summary omitted pending-generation presence.");
                HarnessAssert.True(
                    ApiHarnessClient.FindStringRecursive(
                        summary.Json,
                        "pendingGenerationId") is null &&
                    ApiHarnessClient.FindStringRecursive(
                        summary.Json,
                        "pendingGenerationHash") is null,
                    "P10 summary exposed partial generation identity.");
                var final = await LoadRunAsync(JobRunId(), ct);
                HarnessAssert.True(
                    BsonText(final, "pendingGenerationId") == generationId &&
                    final.GetValue("currentGenerationId", BsonNull.Value)
                        .IsBsonNull,
                    "P10 pending/current generation boundary is invalid.");
                _receiptLeaseTrace.Add(new
                {
                    caseId = "P10-JOB-05",
                    claimTokenAHash = HashText(tokenA),
                    claimTokenBHash = HashText(tokenB),
                    reclaimChangedToken = true,
                    backoffObserved = true,
                    retryAtUtc = retryAt,
                    staleWorkerFenced = true,
                    pendingGenerationId = generationId,
                    pendingGenerationHash = generationHash,
                    currentGenerationId = (string?)null,
                    summaryRedacted = true,
                    stateHash = BsonText(final, "stateHash")
                });
                return new P10CaseObservation(
                    200,
                    "Lease expiry requeued with backoff, later reclaim changed the fence, stale worker lost, and one-CAS pending generation stayed hidden from summary/current publication.",
                    "heartbeat=true;requeue=204;backoff=true;reclaim=true;staleFence=409;pending=true;current=false;summaryRedacted=true",
                    ActorAlias: "admin",
                    RequestHashSha256: BsonText(final, "requestHash"),
                    ReceiptHashSha256: BsonText(
                        final,
                        "receiptResponseHash"),
                    StateHashSha256: BsonText(final, "stateHash"));
            });

        await RunCaseAsync(
            "P10-JOB-06",
            Req("P10-REC-004", "P10-REC-005"),
            O(
                "API_KESTREL",
                "DIRECT_MONGO",
                "QUEUE_TRACE",
                "CLEANUP",
                "P11_ZERO_WRITE",
                "P12_ZERO_WRITE"),
            async () =>
            {
                var retryResponse = await CreateJobFixtureAsync(
                    "p10-job-retry-006",
                    "JOB_RETRY_BACKOFF",
                    Actor("executor"),
                    ct);
                var retryId = RequireResponseString(
                    retryResponse,
                    "reconciliationId");
                var firstClaim = await ClaimAsync(
                    "p10-worker-retry",
                    ct);
                var firstToken = RequireResponseString(
                    firstClaim,
                    "claimToken");
                HarnessAssert.Equal(
                    retryId,
                    RequireResponseString(firstClaim, "reconciliationId"),
                    "P10 retry worker claimed unexpected run");
                var firstFail = await RequireApi().PostAsync(
                    $"api/testing/p10/statistic-reconciliations/jobs/{retryId}/fail",
                    new
                    {
                        workerId = "p10-worker-retry",
                        claimToken = firstToken,
                        failureCode = "P10_TEST_TRANSIENT",
                        transient = true
                    },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    firstFail,
                    HttpStatusCode.OK,
                    "P10 transient fail");
                HarnessAssert.Equal(
                    "QUEUED",
                    RequireResponseString(firstFail, "status"),
                    "P10 transient retry status");
                var retryAtText = RequireResponseString(
                    firstFail,
                    "nextRetryAtUtc");
                var retryAt = DateTime.Parse(
                    retryAtText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal |
                    DateTimeStyles.AdjustToUniversal);
                if (HasFixedClock())
                {
                    var retryReady = await LoadRunAsync(retryId, ct);
                    var ready = (BsonDocument)retryReady.DeepClone();
                    ready["nextRetryAtUtc"] = ProbeNowUtc().AddSeconds(-1);
                    ready["stateHash"] = ComputeStateHash(ready);
                    await RequireDatabase()
                        .GetCollection<BsonDocument>(RunCollection)
                        .ReplaceOneAsync(
                            new BsonDocument("_id", ready["_id"]),
                            ready,
                            cancellationToken: ct);
                }
                else
                {
                    var wait = retryAt - DateTime.UtcNow +
                               TimeSpan.FromMilliseconds(250);
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, ct);
                }
                var secondClaim = await ClaimAsync(
                    "p10-worker-retry",
                    ct);
                var secondToken = RequireResponseString(
                    secondClaim,
                    "claimToken");
                var secondFail = await RequireApi().PostAsync(
                    $"api/testing/p10/statistic-reconciliations/jobs/{retryId}/fail",
                    new
                    {
                        workerId = "p10-worker-retry",
                        claimToken = secondToken,
                        failureCode = "P10_TEST_TRANSIENT",
                        transient = true
                    },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    secondFail,
                    HttpStatusCode.OK,
                    "P10 bounded terminal fail");
                HarnessAssert.Equal(
                    "FAILED",
                    RequireResponseString(secondFail, "status"),
                    "P10 bounded retry terminal status");

                var cancelResponse = await CreateJobFixtureAsync(
                    "p10-job-cancel-006",
                    "JOB_CANCELLED",
                    Actor("executor"),
                    ct);
                var cancelId = RequireResponseString(
                    cancelResponse,
                    "reconciliationId");
                var cancelRun = await LoadRunAsync(cancelId, ct);
                var cancel = await RequireApi().PostAsync(
                    $"{ReconciliationBasePath()}/{cancelId}/cancel",
                    new
                    {
                        commandId = "p10-job-cancel-command-006",
                        expectedStateRevision = BsonLong(
                            cancelRun,
                            "stateRevision"),
                        expectedStateHash = BsonText(
                            cancelRun,
                            "stateHash")
                    },
                    Actor("executor").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    cancel,
                    HttpStatusCode.OK,
                    "P10 cancel foundation run");
                HarnessAssert.Equal(
                    "CANCELLED",
                    RequireResponseString(cancel, "status"),
                    "P10 cancelled status");
                var lateWorker = await RequireApi().PostAsync(
                    $"api/testing/p10/statistic-reconciliations/jobs/{cancelId}/heartbeat",
                    new
                    {
                        workerId = "p10-late-worker",
                        claimToken = HashText("P10-LATE-WORKER-TOKEN")
                    },
                    Actor("admin").Token,
                    ct: ct);
                ExpectError(
                    lateWorker,
                    HttpStatusCode.Conflict,
                    "STAT_RECONCILIATION_JOB_CONFLICT",
                    "P10 cancelled late worker");

                var timeoutCancelResponse = await CreateJobFixtureAsync(
                    "p10-job-timeout-cancel-006",
                    "JOB_TIMEOUT_CANCEL_RACE",
                    Actor("executor"),
                    ct);
                var timeoutCancelId = RequireResponseString(
                    timeoutCancelResponse,
                    "reconciliationId");
                var timeoutCancelBefore = await LoadRunAsync(
                    timeoutCancelId,
                    ct);
                var timeoutCancelForced =
                    (BsonDocument)timeoutCancelBefore.DeepClone();
                timeoutCancelForced["deadlineAtUtc"] =
                    ProbeNowUtc().AddSeconds(-1);
                timeoutCancelForced["nextRetryAtUtc"] =
                    timeoutCancelForced["deadlineAtUtc"]
                        .ToUniversalTime()
                        .AddSeconds(-1);
                timeoutCancelForced["immutableHeaderHash"] =
                    ComputePrivateRunHash(
                        "BuildImmutableHeaderHash",
                        timeoutCancelForced);
                timeoutCancelForced["receiptResponseHash"] =
                    ComputePrivateRunHash(
                        "BuildAcceptedResponseHash",
                        timeoutCancelForced);
                timeoutCancelForced["stateHash"] =
                    ComputeStateHash(timeoutCancelForced);
                await RequireDatabase()
                    .GetCollection<BsonDocument>(RunCollection)
                    .ReplaceOneAsync(
                        new BsonDocument(
                            "_id",
                            timeoutCancelForced["_id"]),
                        timeoutCancelForced,
                        cancellationToken: ct);
                var timeoutVsCancel = await RequireApi().PostAsync(
                    $"{ReconciliationBasePath()}/{timeoutCancelId}/cancel",
                    new
                    {
                        commandId = "p10-job-timeout-cancel-command-006",
                        expectedStateRevision = BsonLong(
                            timeoutCancelBefore,
                            "stateRevision"),
                        expectedStateHash = BsonText(
                            timeoutCancelBefore,
                            "stateHash")
                    },
                    Actor("executor").Token,
                    ct: ct);
                ExpectError(
                    timeoutVsCancel,
                    HttpStatusCode.GatewayTimeout,
                    "STAT_RECONCILIATION_JOB_TIMEOUT",
                    "P10 timeout wins cancel race");
                var timeoutCancelAfter = await LoadRunAsync(
                    timeoutCancelId,
                    ct);
                HarnessAssert.Equal(
                    "FAILED",
                    BsonText(timeoutCancelAfter, "status"),
                    "P10 timeout/cancel race did not durable-fail");
                HarnessAssert.Equal(
                    "P10_RECONCILIATION_JOB_TIMEOUT",
                    BsonText(timeoutCancelAfter, "diagnosticCode"),
                    "P10 timeout/cancel race diagnostic");
                HarnessAssert.Equal(
                    0,
                    timeoutCancelAfter.GetValue(
                            "operationReceipts",
                            new BsonArray())
                        .AsBsonArray.Count,
                    "P10 timeout/cancel race persisted a cancel receipt");

                var timeoutResponse = await CreateJobFixtureAsync(
                    "p10-job-timeout-006",
                    "JOB_TIMEOUT",
                    Actor("admin"),
                    ct);
                var timeoutId = RequireResponseString(
                    timeoutResponse,
                    "reconciliationId");
                var deadlineText = RequireResponseString(
                    timeoutResponse,
                    "deadlineAtUtc");
                var deadline = DateTime.Parse(
                    deadlineText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal |
                    DateTimeStyles.AdjustToUniversal);
                if (HasFixedClock())
                {
                    var timeoutBefore = await LoadRunAsync(timeoutId, ct);
                    var timeoutForced = (BsonDocument)timeoutBefore.DeepClone();
                    timeoutForced["deadlineAtUtc"] =
                        ProbeNowUtc().AddSeconds(-1);
                    timeoutForced["nextRetryAtUtc"] =
                        ProbeNowUtc().AddSeconds(-2);
                    timeoutForced["immutableHeaderHash"] =
                        ComputePrivateRunHash(
                            "BuildImmutableHeaderHash",
                            timeoutForced);
                    timeoutForced["receiptResponseHash"] =
                        ComputePrivateRunHash(
                            "BuildAcceptedResponseHash",
                            timeoutForced);
                    timeoutForced["stateHash"] =
                        ComputeStateHash(timeoutForced);
                    await RequireDatabase()
                        .GetCollection<BsonDocument>(RunCollection)
                        .ReplaceOneAsync(
                            new BsonDocument("_id", timeoutForced["_id"]),
                            timeoutForced,
                            cancellationToken: ct);
                }
                else
                {
                    var timeoutWait = deadline - DateTime.UtcNow +
                                      TimeSpan.FromMilliseconds(300);
                    if (timeoutWait > TimeSpan.Zero)
                        await Task.Delay(timeoutWait, ct);
                }
                var timeout = await RequireApi().GetAsync(
                    $"{ReconciliationBasePath()}/{timeoutId}",
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    timeout,
                    HttpStatusCode.OK,
                    "P10 timeout refresh");
                HarnessAssert.Equal(
                    "FAILED",
                    RequireResponseString(timeout, "status"),
                    "P10 timeout status");
                HarnessAssert.Equal(
                    "P10_RECONCILIATION_JOB_TIMEOUT",
                    RequireResponseString(timeout, "diagnosticCode"),
                    "P10 timeout diagnostic");
                var timeoutRun = await LoadRunAsync(timeoutId, ct);
                _cleanupDryRunBefore = await CountRunsAsync(ct);
                HarnessAssert.True(
                    _cleanupDryRunBefore >= 1,
                    "P10 cleanup dry-run found no owned records.");
                _receiptLeaseTrace.Add(new
                {
                    caseId = "P10-JOB-06",
                    retry = new
                    {
                        first = "QUEUED",
                        second = "FAILED",
                        maxRetryCount = 2,
                        backoffObserved = true
                    },
                    cancel = new
                    {
                        status = "CANCELLED",
                        lateWorkerFenced = true,
                        timeoutWinsRace = true,
                        timeoutRaceHttpStatus = 504,
                        timeoutRaceOperationReceipts = 0
                    },
                    timeout = new
                    {
                        status = "FAILED",
                        diagnosticCode =
                            "P10_RECONCILIATION_JOB_TIMEOUT"
                    },
                    cleanupDryRunCount = _cleanupDryRunBefore
                });
                return new P10CaseObservation(
                    200,
                    "Bounded retry/backoff terminalized FAILED, ordinary cancel terminalized CANCELLED, timeout won the cancel race without a receipt, late worker was fenced, natural timeout became FAILED, and cleanup dry-run enumerated exact owned records.",
                    "retry=QUEUED->FAILED;cancel=CANCELLED;timeoutVsCancel=504/FAILED/noReceipt;lateFence=409;timeout=FAILED;cleanupDryRun=true",
                    ActorAlias: "admin",
                    RequestHashSha256: BsonText(
                        timeoutRun,
                        "requestHash"),
                    ReceiptHashSha256: BsonText(
                        timeoutRun,
                        "receiptResponseHash"),
                    StateHashSha256: BsonText(timeoutRun, "stateHash"));
            });
    }

    private string JobRunId()
        => _jobRunId ?? throw new HarnessCaseNotRunnableException(
            "P10 JOB-01 run is unavailable.");

    private static bool HasFixedClock()
        => !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(
                BackendServerLease.FixedUtcEnvironmentVariable));

    private static DateTime ProbeNowUtc()
    {
        var value = Environment.GetEnvironmentVariable(
            BackendServerLease.FixedUtcEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
            return DateTime.UtcNow;
        if (!DateTime.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) || parsed.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("P10_FIXED_UTC_ENVIRONMENT_INVALID");
        return parsed;
    }
    private async Task<ApiHarnessResponse> ClaimAsync(
        string workerId,
        CancellationToken ct)
        => await RequireApi().PostAsync(
            "api/testing/p10/statistic-reconciliations/jobs/claim",
            new { workerId },
            Actor("admin").Token,
            ct: ct);

    private async Task<ApiHarnessResponse> CreateJobFixtureAsync(
        string commandId,
        string conceptKey,
        P10Actor actor,
        CancellationToken ct)
    {
        var request = NewCreateRequest(commandId);
        request["conceptKey"] = conceptKey;
        var response = await CreateRunAsync(request, actor.Token, ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Accepted,
            $"P10 job fixture {commandId}");
        return response;
    }

    private async Task CancelOpenRunsAsync(
        string commandPrefix,
        CancellationToken ct)
    {
        var collection = RequireDatabase()
            .GetCollection<BsonDocument>(RunCollection);
        var open = await collection.Find(new BsonDocument
            {
                ["status"] = new BsonDocument(
                    "$in",
                    new BsonArray { "QUEUED", "RUNNING" }),
                ["isDeleted"] = false
            })
            .ToListAsync(ct);
        var index = 0;
        foreach (var run in open)
        {
            var response = await RequireApi().PostAsync(
                $"{ReconciliationBasePath(
                    BsonText(run, "workId"),
                    BsonText(run, "scopeAssignmentId"))}/{BsonText(run, "_id")}/cancel",
                new
                {
                    commandId = $"{commandPrefix}-{++index:00}",
                    expectedStateRevision = BsonLong(
                        run,
                        "stateRevision"),
                    expectedStateHash = BsonText(run, "stateHash")
                },
                Actor("admin").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                response,
                HttpStatusCode.OK,
                "P10 pre-job cancel");
        }
    }
}
