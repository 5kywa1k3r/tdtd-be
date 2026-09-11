using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunJobCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-CORE-JOB-01",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                _jobCommandId = "p9-core-job-enqueue-001";
                _jobRequest = BuildCreateRequest(_jobCommandId);
                var response = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    _jobRequest,
                    Actor("executor").Token,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.Accepted,
                    "P9 job enqueue");
                _jobAcceptedResponse = ApiHarnessClient.RequiredObject(
                    response.Json,
                    "P9 accepted job").DeepClone() as JsonObject;
                var jobId = RequiredString(response.Json, "jobId");
                var receiptId = RequiredString(response.Json, "receiptId");
                HarnessAssert.Equal(
                    64,
                    receiptId.Length,
                    "Durable receipt ID length");
                HarnessAssert.Equal(
                    before + 1,
                    await CountJobsAsync(ct),
                    "P9 enqueue count");
                var document = await LoadJobAsync(jobId, ct);
                HarnessAssert.Equal(
                    receiptId,
                    BsonText(document, "receiptId"),
                    "Embedded durable receipt ID");
                HarnessAssert.True(
                    BsonText(document, "receiptResponseHash").Length == 64,
                    "Receipt response hash is missing.");
                HarnessAssert.Equal(
                    "PENDING",
                    BsonText(document, "status"),
                    "Internal queued state");

                var indexes = await RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "work_report_statistic_rebuild_jobs")
                    .Indexes.ListAsync(ct);
                var names = (await indexes.ToListAsync(ct))
                    .Select(index => index["name"].AsString)
                    .ToHashSet(StringComparer.Ordinal);
                HarnessAssert.True(
                    names.Contains(
                        "ux_workReportStatisticRebuildJobs_receipt"),
                    "Unique durable receipt index is missing.");
                HarnessAssert.True(
                    names.Contains(
                        "ix_workReportStatisticRebuildJobs_p9_claim"),
                    "P9 claim index is missing.");
                return new CaseObservation(
                    "One accepted command created one canonical queued job with embedded durable receipt and declared indexes.",
                    "http=202;jobDelta=1;receipt=durable;queue=PENDING;indexes=2");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-JOB-02",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var response = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    RequireJobRequest(),
                    Actor("executor").Token,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    response,
                    HttpStatusCode.OK,
                    "P9 exact replay");
                HarnessAssert.True(
                    RequiredBool(response.Json, "isReplay"),
                    "Exact replay flag is false.");
                foreach (var field in new[]
                         {
                             "jobId", "runId", "receiptId", "commandId",
                             "capabilityId", "stateRevision", "stateHash",
                             "createdAtUtc"
                         })
                {
                    HarnessAssert.Equal(
                        RequireJobAcceptedResponse()[field]?.ToJsonString(),
                        ApiHarnessClient.RequiredObject(
                            response.Json,
                            "P9 replay response")[field]?.ToJsonString(),
                        $"Exact replay field {field}");
                }
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "Exact replay duplicated a job");
                return new CaseObservation(
                    "Same actor, command and canonical payload converged to the original receipt/run with HTTP 200.",
                    "http=200;isReplay=true;identity=exact;jobDelta=0");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-JOB-03",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var changed = RequireJobRequest().DeepClone() as JsonObject
                              ?? throw new InvalidOperationException(
                                  "Job request clone failed.");
                changed["expectedSourceHash"] = new string('a', 64);
                var response = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    changed,
                    Actor("executor").Token,
                    ct);
                ExpectError(
                    response,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_COMMAND_REPLAY_MISMATCH",
                    "Changed-payload replay");
                HarnessAssert.Equal(
                    before,
                    await CountJobsAsync(ct),
                    "Changed replay duplicated a job");
                return new CaseObservation(
                    "Same command with a different canonical request hash returned replay mismatch and zero writes.",
                    "http=409;code=STAT_RUN_COMMAND_REPLAY_MISMATCH;jobDelta=0");
            },
            ct);

        await DrainFoundationJobsAsync(
            "p9-job03-foundation-drain",
            ct);

        await RunCaseAsync(
            "P9-CORE-JOB-04",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var request = BuildCreateRequest(
                    "p9-core-concurrent-equal-004");
                var tasks = Enumerable.Range(0, 2)
                    .Select(_ => CreateJobAsync(
                        "DIRECT_FIELD_TABLE_LABEL",
                        request,
                        Actor("executor").Token,
                        ct))
                    .ToArray();
                var responses = await Task.WhenAll(tasks);
                var statuses = responses
                    .Select(response => response.StatusCode)
                    .OrderBy(status => (int)status)
                    .ToArray();
                HarnessAssert.True(
                    statuses.SequenceEqual(
                        new[] { HttpStatusCode.OK, HttpStatusCode.Accepted }),
                    "Concurrent equal commands did not produce one winner and one replay.");
                var ids = responses
                    .Select(response => RequiredString(
                        response.Json,
                        "jobId"))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                HarnessAssert.Equal(
                    1,
                    ids.Length,
                    "Concurrent equal commands produced multiple job IDs");
                HarnessAssert.Equal(
                    before + 1,
                    await CountJobsAsync(ct),
                    "Concurrent equal command job delta");
                await DrainFoundationJobsAsync(
                    "p9-job04-equal-drain",
                    ct);

                var competingRequests = new[]
                {
                    BuildCreateRequest("p9-core-concurrent-dedupe-a-004"),
                    BuildCreateRequest("p9-core-concurrent-dedupe-b-004")
                };
                var competingResponses = await Task.WhenAll(
                    CreateJobAsync(
                        "DIRECT_FIELD_TABLE_LABEL",
                        competingRequests[0],
                        Actor("executor").Token,
                        ct),
                    CreateJobAsync(
                        "DIRECT_FIELD_TABLE_LABEL",
                        competingRequests[1],
                        Actor("executor2").Token,
                        ct));
                HarnessAssert.Equal(
                    1,
                    competingResponses.Count(response =>
                        response.StatusCode == HttpStatusCode.Accepted),
                    "Concurrent distinct commands accepted count");
                HarnessAssert.Equal(
                    1,
                    competingResponses.Count(response =>
                        response.StatusCode == HttpStatusCode.Conflict),
                    "Concurrent distinct commands active-dedupe conflict count");
                var conflict = competingResponses.Single(response =>
                    response.StatusCode == HttpStatusCode.Conflict);
                ExpectError(
                    conflict,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_JOB_CONFLICT",
                    "Concurrent distinct command active-dedupe loser");
                HarnessAssert.Equal(
                    "ACTIVE_RUN_ALREADY_EXISTS",
                    ApiHarnessClient.FindStringRecursive(
                        conflict.Json,
                        "reason"),
                    "Concurrent distinct command conflict reason");
                var competingWinner = competingResponses.Single(response =>
                    response.StatusCode == HttpStatusCode.Accepted);
                var competingWinnerJobId = RequiredString(
                    competingWinner.Json,
                    "jobId");
                HarnessAssert.True(
                    !conflict.Body.Contains(
                        competingWinnerJobId,
                        StringComparison.Ordinal),
                    "Cross-actor active-dedupe conflict leaked the winner job ID.");
                HarnessAssert.Equal(
                    before + 2,
                    await CountJobsAsync(ct),
                    "Two concurrency scenarios durable job delta");
                await DrainFoundationJobsAsync(
                    "p9-job04-dedupe-drain",
                    ct);

                var mismatchRequests = new[]
                {
                    BuildCreateRequest(
                        "p9-core-concurrent-mismatch-004"),
                    BuildCreateRequest(
                        "p9-core-concurrent-mismatch-004")
                };
                mismatchRequests[1]["expectedSourceHash"] =
                    new string('b', 64);
                var validTask = CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    mismatchRequests[0],
                    Actor("executor").Token,
                    ct);
                var mismatchedTask = Task.Run(
                    async () =>
                    {
                        await Task.Delay(15, ct);
                        return await CreateJobAsync(
                            "DIRECT_FIELD_TABLE_LABEL",
                            mismatchRequests[1],
                            Actor("executor").Token,
                            ct);
                    },
                    ct);
                var mismatchResponses = await Task.WhenAll(
                    validTask,
                    mismatchedTask);
                HarnessAssert.Equal(
                    1,
                    mismatchResponses.Count(response =>
                        response.StatusCode == HttpStatusCode.Accepted),
                    "Concurrent changed-payload accepted count");
                HarnessAssert.Equal(
                    1,
                    mismatchResponses.Count(response =>
                        response.StatusCode == HttpStatusCode.Conflict),
                    "Concurrent changed-payload conflict count");
                var mismatchConflict = mismatchResponses.Single(response =>
                    response.StatusCode == HttpStatusCode.Conflict);
                ExpectError(
                    mismatchConflict,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_COMMAND_REPLAY_MISMATCH",
                    "Concurrent changed-payload replay loser");
                var mismatchWinner = mismatchResponses.Single(response =>
                    response.StatusCode == HttpStatusCode.Accepted);
                var mismatchJobId = RequiredString(
                    mismatchWinner.Json,
                    "jobId");
                HarnessAssert.Equal(
                    before + 3,
                    await CountJobsAsync(ct),
                    "Three concurrency scenarios durable job delta");
                var winnerReplay = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    mismatchRequests[0],
                    Actor("executor").Token,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    winnerReplay,
                    HttpStatusCode.OK,
                    "Changed-payload race winner replay");
                HarnessAssert.True(
                    RequiredBool(winnerReplay.Json, "isReplay"),
                    "Changed-payload race winner retry was not a replay.");
                HarnessAssert.Equal(
                    mismatchJobId,
                    RequiredString(winnerReplay.Json, "jobId"),
                    "Changed-payload race winner replay job ID");
                var loserRetry = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    mismatchRequests[1],
                    Actor("executor").Token,
                    ct);
                ExpectError(
                    loserRetry,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_COMMAND_REPLAY_MISMATCH",
                    "Changed-payload race loser retry");
                HarnessAssert.Equal(
                    before + 3,
                    await CountJobsAsync(ct),
                    "Changed-payload retries wrote a job");
                await DrainFoundationJobsAsync(
                    "p9-job04-mismatch-drain",
                    ct);
                return new CaseObservation(
                    "Concurrent equal commands converged to one receipt; cross-actor distinct receipts sharing canonical dedupe exposed no winner ID; and a changed-payload receipt race remained replay-stable.",
                    "equal=202+200/sameJob;crossActorDistinct=202+409/ACTIVE_RUN_ALREADY_EXISTS/noLeak;mismatch=202+409/REPLAY_MISMATCH/retriesStable;winnerPerScenario=1;jobDelta=3");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-JOB-05",
            async () =>
            {
                var before = await CountJobsAsync(ct);
                var created = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    BuildCreateRequest("p9-core-stale-fence-005"),
                    Actor("executor").Token,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    created,
                    HttpStatusCode.Accepted,
                    "P9 stale-fence enqueue");
                var jobId = RequiredString(created.Json, "jobId");

                var firstClaim = await ClaimAsync(
                    "p9-worker-one",
                    ct);
                var firstToken = RequiredString(
                    firstClaim,
                    "claimToken");
                var firstJob = ApiHarnessClient.RequiredObject(
                    firstClaim["job"],
                    "First claimed job");
                HarnessAssert.Equal(
                    jobId,
                    RequiredString(firstJob, "jobId"),
                    "First claim job ID");
                HarnessAssert.Equal(
                    "RUNNING",
                    RequiredString(firstJob, "status"),
                    "First claim state");
                var firstRevision = RequiredLong(
                    firstJob,
                    "stateRevision");

                var heartbeat = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/heartbeat",
                    new
                    {
                        workerId = "p9-worker-one",
                        claimToken = firstToken
                    },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    heartbeat,
                    HttpStatusCode.OK,
                    "P9 worker heartbeat");
                HarnessAssert.True(
                    RequiredLong(heartbeat.Json, "stateRevision") >
                    firstRevision,
                    "Heartbeat did not advance state revision.");

                var expire = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/expire-lease",
                    new
                    {
                        workerId = "p9-worker-one",
                        claimToken = firstToken
                    },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    expire,
                    HttpStatusCode.OK,
                    "P9 expire lease");

                var secondClaim = await ClaimAsync(
                    "p9-worker-two",
                    ct);
                var secondToken = RequiredString(
                    secondClaim,
                    "claimToken");
                HarnessAssert.True(
                    !string.Equals(
                        firstToken,
                        secondToken,
                        StringComparison.Ordinal),
                    "Reclaim reused the stale claim token.");
                var secondJob = ApiHarnessClient.RequiredObject(
                    secondClaim["job"],
                    "Second claimed job");
                HarnessAssert.Equal(
                    jobId,
                    RequiredString(secondJob, "jobId"),
                    "Reclaimed job ID");

                var staleHeartbeat = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/heartbeat",
                    new
                    {
                        workerId = "p9-worker-one",
                        claimToken = firstToken
                    },
                    Actor("admin").Token,
                    ct: ct);
                ExpectError(
                    staleHeartbeat,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_JOB_CONFLICT",
                    "Stale worker heartbeat");
                var staleComplete = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
                    new
                    {
                        workerId = "p9-worker-one",
                        claimToken = firstToken
                    },
                    Actor("admin").Token,
                    ct: ct);
                ExpectError(
                    staleComplete,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_JOB_CONFLICT",
                    "Stale worker completion");

                var complete = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
                    new
                    {
                        workerId = "p9-worker-two",
                        claimToken = secondToken
                    },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    complete,
                    HttpStatusCode.OK,
                    "Current worker completion");
                HarnessAssert.Equal(
                    "DONE",
                    RequiredString(complete.Json, "status"),
                    "Completed common state");
                HarnessAssert.True(
                    RequiredString(complete.Json, "generationId").Length ==
                    64,
                    "Completed generation ID is missing.");

                var deadlineCreated = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    BuildCreateRequest("p9-core-deadline-fence-005"),
                    Actor("executor").Token,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    deadlineCreated,
                    HttpStatusCode.Accepted,
                    "P9 deadline enqueue");
                var deadlineJobId = RequiredString(
                    deadlineCreated.Json,
                    "jobId");
                var deadlineClaim = await ClaimAsync(
                    "p9-deadline-worker",
                    ct);
                var deadlineToken = RequiredString(
                    deadlineClaim,
                    "claimToken");
                HarnessAssert.Equal(
                    deadlineJobId,
                    RequiredString(
                        ApiHarnessClient.RequiredObject(
                            deadlineClaim["job"],
                            "Deadline claimed job"),
                        "jobId"),
                    "Deadline claim job ID");
                var directDeadlineJob = await LoadJobAsync(
                    deadlineJobId,
                    ct);
                var expiredDeadline = new DateTime(
                    DateTime.UtcNow.AddSeconds(-1).Ticks /
                    TimeSpan.TicksPerMillisecond *
                    TimeSpan.TicksPerMillisecond,
                    DateTimeKind.Utc);
                var originalStateHash = BsonText(
                    directDeadlineJob,
                    "stateHash");
                var injectedStateHash = ComputeJobStateHash(
                    directDeadlineJob,
                    expiredDeadline);
                var injected = await RequireDatabase()
                    .GetCollection<BsonDocument>(
                        "work_report_statistic_rebuild_jobs")
                    .UpdateOneAsync(
                        new BsonDocument
                        {
                            ["_id"] = ObjectId.Parse(deadlineJobId),
                            ["stateRevision"] = BsonLong(
                                directDeadlineJob,
                                "stateRevision"),
                            ["stateHash"] = originalStateHash
                        },
                        Builders<BsonDocument>.Update
                            .Set("deadlineAtUtc", expiredDeadline)
                            .Set("stateHash", injectedStateHash),
                        cancellationToken: ct);
                HarnessAssert.Equal(
                    1L,
                    injected.ModifiedCount,
                    "Deadline injection CAS");

                var deadlineRead = await RequireApi().GetAsync(
                    $"api/stat-runs/jobs/{deadlineJobId}",
                    Actor("executor").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    deadlineRead,
                    HttpStatusCode.OK,
                    "P9 deadline terminal read");
                HarnessAssert.Equal(
                    "FAILED",
                    RequiredString(deadlineRead.Json, "status"),
                    "Deadline API terminal status");
                HarnessAssert.Equal(
                    "STAT_RUN_JOB_TIMEOUT",
                    RequiredString(deadlineRead.Json, "diagnosticCode"),
                    "Deadline API diagnostic");
                var terminalDeadlineJob = await LoadJobAsync(
                    deadlineJobId,
                    ct);
                HarnessAssert.Equal(
                    BsonLong(directDeadlineJob, "stateRevision") + 1,
                    BsonLong(terminalDeadlineJob, "stateRevision"),
                    "GET deadline transition revision delta");
                HarnessAssert.Equal(
                    "DEAD_LETTER",
                    BsonText(terminalDeadlineJob, "status"),
                    "Deadline durable terminal state");
                HarnessAssert.True(
                    !terminalDeadlineJob["isActive"].AsBoolean,
                    "Deadline terminal job remained active.");
                var terminalDeadlineSha = HashBytes(
                    terminalDeadlineJob.ToBson());

                var lateCalls = await Task.WhenAll(
                    RequireApi().PostAsync(
                        $"api/testing/p9/stat-runs/jobs/{deadlineJobId}/heartbeat",
                        new
                        {
                            workerId = "p9-deadline-worker",
                            claimToken = deadlineToken
                        },
                        Actor("admin").Token,
                        ct: ct),
                    RequireApi().PostAsync(
                        $"api/testing/p9/stat-runs/jobs/{deadlineJobId}/complete",
                        new
                        {
                            workerId = "p9-deadline-worker",
                            claimToken = deadlineToken
                        },
                        Actor("admin").Token,
                        ct: ct));
                foreach (var late in lateCalls)
                {
                    ExpectError(
                        late,
                        HttpStatusCode.Conflict,
                        "STAT_RUN_JOB_CONFLICT",
                        "Late deadline worker mutation");
                    HarnessAssert.Equal(
                        "JOB_DEADLINE_EXPIRED",
                        ApiHarnessClient.FindStringRecursive(
                            late.Json,
                            "reason"),
                        "Late deadline worker conflict reason");
                }
                var afterLateCalls = await LoadJobAsync(
                    deadlineJobId,
                    ct);
                HarnessAssert.Equal(
                    terminalDeadlineSha,
                    HashBytes(afterLateCalls.ToBson()),
                    "Late heartbeat/complete changed terminal job");
                HarnessAssert.Equal(
                    before + 2,
                    await CountJobsAsync(ct),
                    "Stale-fence case job delta");
                return new CaseObservation(
                    "Lease expiry/reclaim fenced stale workers; after integrity-preserving DeadlineAtUtc injection, GET itself materialized DEAD_LETTER/TIMEOUT and both later worker mutations were conflict/zero-write.",
                    "leaseClaim=2;staleHeartbeat=409;staleComplete=409;winner=DONE;deadlineInjected=true;GETMaterialized=FAILED/TIMEOUT/revisionDelta1;lateHeartbeatComplete=409/zeroWrite;jobDelta=2");
            },
            ct);

        await RunCaseAsync(
            "P9-CORE-JOB-06",
            async () =>
            {
                var created = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    BuildCreateRequest("p9-core-terminal-reset-006"),
                    Actor("executor").Token,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    created,
                    HttpStatusCode.Accepted,
                    "P9 terminal enqueue");
                var jobId = RequiredString(created.Json, "jobId");
                var claim = await ClaimAsync(
                    "p9-terminal-worker",
                    ct);
                var claimToken = RequiredString(claim, "claimToken");
                HarnessAssert.Equal(
                    jobId,
                    RequiredString(
                        ApiHarnessClient.RequiredObject(
                            claim["job"],
                            "Terminal claimed job"),
                        "jobId"),
                    "Terminal claim job ID");

                var failed = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/retry",
                    new
                    {
                        workerId = "p9-terminal-worker",
                        claimToken,
                        failureCode = "TERMINAL_TEST"
                    },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    failed,
                    HttpStatusCode.OK,
                    "P9 terminal retry");
                HarnessAssert.Equal(
                    "FAILED",
                    RequiredString(failed.Json, "status"),
                    "Terminal common state");
                HarnessAssert.Equal(
                    "TERMINAL_TEST",
                    RequiredString(failed.Json, "diagnosticCode"),
                    "Allowlisted terminal diagnostic");
                var failedRevision = RequiredLong(
                    failed.Json,
                    "stateRevision");
                var failedHash = RequiredString(
                    failed.Json,
                    "stateHash");

                var staleReset = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/reset",
                    new
                    {
                        commandId = "p9-core-reset-stale-006",
                        expectedStateRevision = failedRevision - 1,
                        expectedStateHash = failedHash
                    },
                    Actor("admin").Token,
                    ct: ct);
                ExpectError(
                    staleReset,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_JOB_CONFLICT",
                    "P9 stale reset CAS");

                var resetRequest = new
                {
                    commandId = "p9-core-reset-valid-006",
                    expectedStateRevision = failedRevision,
                    expectedStateHash = failedHash
                };
                var concurrentResets = await Task.WhenAll(
                    Enumerable.Range(0, 2).Select(_ =>
                        RequireApi().PostAsync(
                            $"api/testing/p9/stat-runs/jobs/{jobId}/reset",
                            resetRequest,
                            Actor("admin").Token,
                            ct: ct)));
                foreach (var reset in concurrentResets)
                {
                    ApiHarnessClient.ExpectStatus(
                        reset,
                        HttpStatusCode.OK,
                        "P9 concurrent valid reset");
                    HarnessAssert.Equal(
                        "QUEUED",
                        RequiredString(reset.Json, "status"),
                        "Reset common state");
                    HarnessAssert.Equal(
                        0,
                        ApiHarnessClient.RequiredInt(
                            reset.Json,
                            "retryCount"),
                        "Reset retry count");
                    HarnessAssert.Equal(
                        failedRevision + 1,
                        RequiredLong(reset.Json, "stateRevision"),
                        "Concurrent reset revision delta");
                }
                HarnessAssert.Equal(
                    1,
                    concurrentResets.Count(response =>
                        RequiredBool(response.Json, "isReplay")),
                    "Concurrent reset replay count");
                HarnessAssert.Equal(
                    1,
                    concurrentResets.Count(response =>
                        !RequiredBool(response.Json, "isReplay")),
                    "Concurrent reset non-replay count");
                var resetDocument = await LoadJobAsync(jobId, ct);
                HarnessAssert.Equal(
                    failedRevision + 1,
                    BsonLong(resetDocument, "stateRevision"),
                    "Durable concurrent reset revision delta");
                HarnessAssert.Equal(
                    1,
                    resetDocument["resetReceipts"].AsBsonArray.Count,
                    "Durable reset receipt count");

                var finalClaim = await ClaimAsync(
                    "p9-reset-worker",
                    ct);
                var finalToken = RequiredString(
                    finalClaim,
                    "claimToken");
                var finalComplete = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
                    new
                    {
                        workerId = "p9-reset-worker",
                        claimToken = finalToken
                    },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    finalComplete,
                    HttpStatusCode.OK,
                    "P9 reset completion");
                HarnessAssert.Equal(
                    "DONE",
                    RequiredString(finalComplete.Json, "status"),
                    "Reset terminal completion");

                var progressedDocument = await LoadJobAsync(jobId, ct);
                var progressedRevision = BsonLong(
                    progressedDocument,
                    "stateRevision");
                var progressedDocumentSha = HashBytes(
                    progressedDocument.ToBson());
                var progressedReplay = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/reset",
                    resetRequest,
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    progressedReplay,
                    HttpStatusCode.OK,
                    "P9 progressed-state reset replay");
                HarnessAssert.True(
                    RequiredBool(progressedReplay.Json, "isReplay"),
                    "Progressed-state reset replay flag is false.");
                HarnessAssert.Equal(
                    "DONE",
                    RequiredString(progressedReplay.Json, "status"),
                    "Progressed-state reset replay regressed state");
                var afterProgressedReplay = await LoadJobAsync(jobId, ct);
                HarnessAssert.Equal(
                    progressedRevision,
                    BsonLong(afterProgressedReplay, "stateRevision"),
                    "Progressed-state reset replay advanced revision");
                HarnessAssert.Equal(
                    progressedDocumentSha,
                    HashBytes(afterProgressedReplay.ToBson()),
                    "Progressed-state reset replay wrote the job");

                await RunRepeatedFixtureCyclesAsync(ct);
                HarnessAssert.Equal(
                    2,
                    _fixtureCycles.Count,
                    "Repeated fixture cycle count");
                HarnessAssert.True(
                    _fixtureCycles.All(cycle =>
                        cycle.ReturnedToBaseline),
                    "A repeated fixture cycle leaked owned records.");
                return new CaseObservation(
                    "Terminal retry and stale CAS were fenced; two identical reset commands produced one durable transition/receipt plus one replay, and replay after DONE remained zero-write before two autonomous fixture cycles returned to baseline.",
                    "deadLetter=1;staleReset=409;concurrentReset=200+200/nonReplay1/replay1/revisionDelta1;final=DONE;progressedReplay=true/zeroWrite;fixtureCycles=2");
            },
            ct);
    }

    private async Task<JsonObject> ClaimAsync(
        string workerId,
        CancellationToken ct)
    {
        var response = await RequireApi().PostAsync(
            "api/testing/p9/stat-runs/jobs/claim",
            new { workerId },
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P9 claim {workerId}");
        return ApiHarnessClient.RequiredObject(
            response.Json,
            $"P9 claim {workerId}");
    }

    private JsonObject RequireJobRequest()
        => _jobRequest ??
           throw new HarnessCaseNotRunnableException(
               "P9 JOB-01 request is unavailable.");

    private JsonObject RequireJobAcceptedResponse()
        => _jobAcceptedResponse ??
           throw new HarnessCaseNotRunnableException(
               "P9 JOB-01 accepted response is unavailable.");

    private async Task RunRepeatedFixtureCyclesAsync(
        CancellationToken ct)
    {
        for (var cycle = 1; cycle <= 2; cycle++)
            await RunFixtureCycleAsync(cycle, ct);
    }

    private async Task RunFixtureCycleAsync(
        int cycle,
        CancellationToken ct)
    {
        var baseline = await CaptureDatabaseSnapshotAsync(ct);
        var baselineSha = SnapshotSha256(baseline);
        var workId = ObjectId.GenerateNewId();
        var assignmentId = ObjectId.GenerateNewId();
        var reportId = ObjectId.GenerateNewId();
        var periodId = ObjectId.GenerateNewId();
        var templateId = ObjectId.GenerateNewId();
        var configId = ObjectId.GenerateNewId();
        var configVersionId = ObjectId.GenerateNewId();
        var at = new DateTime(
            2026,
            8,
            4,
            cycle,
            0,
            0,
            DateTimeKind.Utc);
        var handles = new List<P9CleanupHandle>
        {
            new("works", workId.ToString()),
            new("work_report_periods", periodId.ToString()),
            new("work_assignments", assignmentId.ToString()),
            new("work_assignment_report", reportId.ToString()),
            new("dynamic_form_templates", templateId.ToString())
        };
        var database = RequireDatabase();

        await database.GetCollection<BsonDocument>("works")
            .InsertOneAsync(
                new BsonDocument
                {
                    ["_id"] = workId,
                    ["autoCode"] = $"P9REPEAT{cycle}",
                    ["code"] = $"P9-REPEAT-{cycle}",
                    ["name"] = $"P9 Repeated Work {cycle}",
                    ["issuedByUnitId"] =
                        ObjectId.Parse(Fixture().UnitAId),
                    ["createdByUserId"] =
                        ObjectId.Parse(Actor("executor").Id),
                    ["updatedByUserId"] =
                        ObjectId.Parse(Actor("admin").Id),
                    ["createdAtUtc"] = at,
                    ["updatedAtUtc"] = at,
                    ["isDeleted"] = false
                },
                cancellationToken: ct);
        await database.GetCollection<BsonDocument>(
                "work_report_periods")
            .InsertOneAsync(
                new BsonDocument
                {
                    ["_id"] = periodId,
                    ["workId"] = workId,
                    ["workAssignmentId"] = assignmentId,
                    ["periodKey"] = Fixture().PeriodKey,
                    ["periodInstanceKey"] =
                        $"{Fixture().PeriodInstanceKey}:C{cycle}",
                    ["periodKind"] = Fixture().PeriodKind,
                    ["periodStart"] = Fixture().PeriodStartUtc,
                    ["periodEnd"] = Fixture().PeriodEndUtc,
                    ["isActive"] = true,
                    ["createdAtUtc"] = at,
                    ["updatedAtUtc"] = at,
                    ["isDeleted"] = false
                },
                cancellationToken: ct);
        await database.GetCollection<BsonDocument>(
                "dynamic_form_templates")
            .InsertOneAsync(
                new BsonDocument
                {
                    ["_id"] = templateId,
                    ["code"] = $"P9_REPEAT_FORM_{cycle}",
                    ["name"] = $"P9 Repeat Form {cycle}",
                    ["tagCodes"] = new BsonArray(),
                    ["createdByUsername"] = "admin",
                    ["schemaVersion"] = 1,
                    ["versionNo"] = 1,
                    ["revision"] = 1,
                    ["isActive"] = true,
                    ["isPublished"] = true,
                    ["sectionsJson"] = "[]",
                    ["fieldsJson"] = "[]",
                    ["blocksJson"] = "[]",
                    ["statisticConfigId"] = configId,
                    ["statisticConfigVersionId"] = configVersionId,
                    ["statisticConfigVersionNo"] = 1,
                    ["statisticConfigRevision"] = 1L,
                    ["statisticConfigStatus"] = "LOCKED",
                    ["statisticConfigHash"] = Fixture().ConfigHash,
                    ["p9CanonicalConfigPayloadJson"] =
                        Fixture().ConfigPayloadJson,
                    ["createdAtUtc"] = at,
                    ["updatedAtUtc"] = at,
                    ["isDeleted"] = false
                },
                cancellationToken: ct);
        await database.GetCollection<BsonDocument>("work_assignments")
            .InsertOneAsync(
                new BsonDocument
                {
                    ["_id"] = assignmentId,
                    ["workId"] = workId,
                    ["dynamicFormTemplateId"] = templateId,
                    ["dynamicFormTemplateCode"] =
                        $"P9_REPEAT_FORM_{cycle}",
                    ["dynamicFormTemplateName"] =
                        $"P9 Repeat Form {cycle}",
                    ["workType"] = "REPORT",
                    ["assignmentType"] = "USER",
                    ["aggregationType"] = "NONE",
                    ["assignees"] = new BsonArray(),
                    ["isActive"] = true,
                    ["rootAssignmentId"] = assignmentId.ToString(),
                    ["level"] = 0,
                    ["code"] = $"P9-REPEAT-A-{cycle}",
                    ["name"] = $"P9 Repeat Assignment {cycle}",
                    ["path"] = assignmentId.ToString(),
                    ["issuedByUnitId"] =
                        ObjectId.Parse(Fixture().UnitAId),
                    ["targetUnitIds"] = new BsonArray
                    {
                        ObjectId.Parse(Fixture().UnitAId)
                    },
                    ["leaderWatcherUserIds"] = new BsonArray(),
                    ["leaderWatchers"] = new BsonArray(),
                    ["createdByUserId"] =
                        ObjectId.Parse(Actor("executor").Id),
                    ["updatedByUserId"] =
                        ObjectId.Parse(Actor("admin").Id),
                    ["createdAtUtc"] = at,
                    ["updatedAtUtc"] = at,
                    ["isDeleted"] = false
                },
                cancellationToken: ct);
        var reports = database.GetCollection<BsonDocument>(
            "work_assignment_report");
        await reports.InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = reportId,
                ["workId"] = workId,
                ["workAssignmentId"] = assignmentId,
                ["workReportPeriodId"] = periodId,
                ["dynamicFormTemplateId"] = templateId,
                ["dynamicFormTemplateCode"] =
                    $"P9_REPEAT_FORM_{cycle}",
                ["dynamicFormTemplateName"] =
                    $"P9 Repeat Form {cycle}",
                ["assigneeUserId"] =
                    ObjectId.Parse(Actor("executor").Id),
                ["periodKey"] = Fixture().PeriodKey,
                ["periodInstanceKey"] =
                    $"{Fixture().PeriodInstanceKey}:C{cycle}",
                ["periodKind"] = Fixture().PeriodKind,
                ["periodStart"] = Fixture().PeriodStartUtc,
                ["periodEnd"] = Fixture().PeriodEndUtc,
                ["status"] = 0,
                ["payloadRevision"] = 1,
                ["payloadHash"] = Fixture().SourceHash,
                ["payloadSizeBytes"] =
                    Encoding.UTF8.GetByteCount(
                        Fixture().SourcePayloadJson),
                ["lifecycleRevision"] = 1,
                ["isCurrent"] = true,
                ["isActive"] = true,
                ["scheduleSnapshotJson"] = "{}",
                ["dynamicExcelCode"] = string.Empty,
                ["dynamicExcelName"] = string.Empty,
                ["specJson"] = "{}",
                ["values1DJson"] = Fixture().SourcePayloadJson,
                ["fieldValuesJson"] = Fixture().SourcePayloadJson,
                ["p9CanonicalSourcePayloadJson"] =
                    Fixture().SourcePayloadJson,
                ["createdAtUtc"] = at,
                ["updatedAtUtc"] = at,
                ["isDeleted"] = false
            },
            cancellationToken: ct);
        await reports.UpdateOneAsync(
            new BsonDocument("_id", reportId),
            Builders<BsonDocument>.Update
                .Set("status", 1)
                .Set("lifecycleRevision", 2)
                .Set("submittedAtUtc", at.AddMinutes(1)),
            cancellationToken: ct);
        await reports.UpdateOneAsync(
            new BsonDocument("_id", reportId),
            Builders<BsonDocument>.Update
                .Set("status", 2)
                .Set("lifecycleRevision", 3)
                .Set("approvedAtUtc", at.AddMinutes(2))
                .Set(
                    "approvedByUserId",
                    ObjectId.Parse(Actor("admin").Id)),
            cancellationToken: ct);

        var commandId = $"p9-core-repeat-cycle-{cycle}";
        var request = new JsonObject
        {
            ["commandId"] = commandId,
            ["workId"] = workId.ToString(),
            ["scopeType"] = "ASSIGNMENT",
            ["scopeId"] = assignmentId.ToString(),
            ["sourceReportId"] = reportId.ToString(),
            ["dynamicFormTemplateId"] = templateId.ToString(),
            ["expectedConfigRevision"] = 1,
            ["expectedConfigHash"] = Fixture().ConfigHash,
            ["expectedSourceRevision"] = 1,
            ["expectedSourceHash"] = Fixture().SourceHash,
            ["expectedLifecycleRevision"] = 3,
            ["period"] = new JsonObject
            {
                ["periodKey"] = Fixture().PeriodKey,
                ["periodInstanceKey"] =
                    $"{Fixture().PeriodInstanceKey}:C{cycle}",
                ["periodKind"] = Fixture().PeriodKind,
                ["periodStart"] = Fixture().PeriodStartUtc,
                ["periodEnd"] = Fixture().PeriodEndUtc
            }
        };
        var created = await CreateJobAsync(
            "DIRECT_FIELD_TABLE_LABEL",
            request,
            Actor("executor").Token,
            ct);
        ApiHarnessClient.ExpectStatus(
            created,
            HttpStatusCode.Accepted,
            $"Repeated fixture cycle {cycle} enqueue");
        var jobId = RequiredString(created.Json, "jobId");
        handles.Add(new P9CleanupHandle(
            "work_report_statistic_rebuild_jobs",
            jobId));
        var claim = await ClaimAsync(
            $"p9-repeat-worker-{cycle}",
            ct);
        var claimToken = RequiredString(claim, "claimToken");
        HarnessAssert.Equal(
            jobId,
            RequiredString(
                ApiHarnessClient.RequiredObject(
                    claim["job"],
                    $"Repeated fixture cycle {cycle} claim"),
                "jobId"),
            $"Repeated fixture cycle {cycle} claim job");
        var completed = await RequireApi().PostAsync(
            $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
            new
            {
                workerId = $"p9-repeat-worker-{cycle}",
                claimToken
            },
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            completed,
            HttpStatusCode.OK,
            $"Repeated fixture cycle {cycle} completion");
        HarnessAssert.Equal(
            "DONE",
            RequiredString(completed.Json, "status"),
            $"Repeated fixture cycle {cycle} state");
        var seeded = await CaptureDatabaseSnapshotAsync(ct);
        var seededSha = SnapshotSha256(seeded);
        HarnessAssert.True(
            !string.Equals(
                baselineSha,
                seededSha,
                StringComparison.Ordinal),
            $"Repeated fixture cycle {cycle} did not change the owned snapshot.");

        foreach (var handle in handles.AsEnumerable().Reverse())
        {
            var deleted = await database
                .GetCollection<BsonDocument>(handle.Collection)
                .DeleteOneAsync(
                    new BsonDocument(
                        "_id",
                        ObjectId.Parse(handle.Id)),
                    ct);
            HarnessAssert.Equal(
                1L,
                deleted.DeletedCount,
                $"Repeated fixture cycle {cycle} cleanup {handle.Collection}/{handle.Id}");
        }
        var cleaned = await CaptureDatabaseSnapshotAsync(ct);
        var cleanedSha = SnapshotSha256(cleaned);
        var returned = string.Equals(
            baselineSha,
            cleanedSha,
            StringComparison.Ordinal);
        HarnessAssert.True(
            returned,
            $"Repeated fixture cycle {cycle} did not return to baseline.");
        _fixtureCycles.Add(new P9FixtureCycleEvidence(
            cycle,
            handles,
            baselineSha,
            seededSha,
            cleanedSha,
            returned));
    }
}
