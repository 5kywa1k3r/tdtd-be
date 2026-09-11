using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using tdtd_be.Models.Statistics;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunOperationsRetryCasesCoreAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-OPS-RETRY-01",
            async () =>
            {
                await AssertOperationsFoundationExactReportPinAsync(ct);
                var request = await BuildOperationsFoundationCreateRequestAsync(
                    "p9-ops-foundation-007",
                    ct);
                var created = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    request,
                    Actor("admin").Token,
                    ct);
                ApiHarnessClient.ExpectStatus(
                    created,
                    HttpStatusCode.Accepted,
                    "P9-OPS Foundation enqueue");
                _opsFoundationJobId = RequiredString(created.Json, "jobId");
                HarnessAssert.Equal("QUEUED", RequiredString(created.Json, "status"), "P9-OPS enqueue status");

                var detail = await RequireApi().GetAsync(
                    $"api/operations/jobs/{_opsFoundationJobId}",
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(detail, HttpStatusCode.OK, "P9-OPS operation detail");
                HarnessAssert.Equal(_opsFoundationJobId, RequiredString(detail.Json, "jobId"), "operation detail job");
                HarnessAssert.Equal(
                    WorkReportStatisticRebuildJobRunKinds.Foundation,
                    RequiredString(detail.Json, "runKind"),
                    "operation detail run kind");
                HarnessAssert.Equal("QUEUED", RequiredString(detail.Json, "status"), "operation detail status");

                var list = await RequireApi().GetAsync(
                    $"api/operations/jobs?limit=50&status=QUEUED&runKind={WorkReportStatisticRebuildJobRunKinds.Foundation}&workId={Fixture().WorkId}",
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(list, HttpStatusCode.OK, "P9-OPS operation list");
                var items = OperationsRequiredArray(list.Json, "items", "P9-OPS operation list");
                HarnessAssert.True(
                    items.OfType<JsonObject>().Any(item => string.Equals(
                        RequiredString(item, "jobId"),
                        _opsFoundationJobId,
                        StringComparison.Ordinal)),
                    "P9-OPS list omitted queued Foundation job");
                HarnessAssert.Equal(items.Count, ApiHarnessClient.RequiredInt(list.Json, "count"), "P9-OPS list count");
                await TraceOperationsJobAsync("FOUNDATION_QUEUED", _opsFoundationJobId, ct);
                return new CaseObservation(
                    "A Foundation job was accepted through Kestrel and immediately visible through canonical operations detail/list filters.",
                    $"job={_opsFoundationJobId};create=202;detail=200;list=200;status=QUEUED;count={items.Count}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-RETRY-02",
            async () =>
            {
                var jobId = RequireOperationsFoundationJobId();
                _opsWorkerId = "p9-ops-worker-stale";
                var claim = await ClaimAsync(_opsWorkerId, ct);
                _opsClaimToken = RequiredString(claim, "claimToken");
                var claimedJob = ApiHarnessClient.RequiredObject(claim["job"], "P9-OPS claimed job");
                HarnessAssert.Equal(jobId, RequiredString(claimedJob, "jobId"), "P9-OPS claimed job id");
                HarnessAssert.Equal("RUNNING", RequiredString(claimedJob, "status"), "P9-OPS claimed status");

                var running = await LoadJobAsync(jobId, ct);
                var activeRetry = BuildOperationsCas(running, "p9-ops-active-retry-008");
                var before = await CaptureDatabaseSnapshotAsync(ct);
                var rejected = await PostOperationsMutationAsync(jobId, "retry", activeRetry, ct);
                ExpectError(
                    rejected,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_JOB_CONFLICT",
                    "P9-OPS active lease retry");
                HarnessAssert.Equal(
                    "ACTIVE_LEASE_FENCED",
                    ApiHarnessClient.FindStringRecursive(rejected.Json, "reason"),
                    "P9-OPS active lease reason");
                var after = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(before, after, "P9-OPS active lease retry zero-write");

                var expired = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/expire-lease",
                    new { workerId = _opsWorkerId, claimToken = _opsClaimToken },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(expired, HttpStatusCode.OK, "P9-OPS expire lease");
                HarnessAssert.Equal("RUNNING", RequiredString(expired.Json, "status"), "P9-OPS expired lease status");
                var expiredJob = await TraceOperationsJobAsync("LEASE_EXPIRED", jobId, ct);
                HarnessAssert.True(
                    BsonNullableUtc(expiredJob, "leaseUntilUtc") <= DateTime.UtcNow,
                    "P9-OPS lease was not durably expired");
                return new CaseObservation(
                    "An active lease fenced operator retry with a full-database zero-write; the test worker then expired that same lease for recovery.",
                    $"job={jobId};worker={_opsWorkerId};activeRetry=409/ACTIVE_LEASE_FENCED;zeroWrite={SnapshotSha256(after)};lease=EXPIRED");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-RETRY-03",
            async () =>
            {
                var jobId = RequireOperationsFoundationJobId();
                var expired = await LoadJobAsync(jobId, ct);
                _opsRetryRequest = BuildOperationsCas(expired, "p9-ops-retry-009");
                var response = await PostOperationsMutationAsync(
                    jobId,
                    "retry",
                    _opsRetryRequest,
                    ct);
                var receipt = await VerifyOperationsReceiptAsync(
                    "P9-OPS-RETRY-03",
                    "RETRY",
                    jobId,
                    "p9-ops-retry-009",
                    response,
                    false,
                    ct);
                var job = OperationsMutationJob(response, "P9-OPS-RETRY-03");
                HarnessAssert.Equal("RETRYING", RequiredString(job, "status"), "P9-OPS retry status");
                HarnessAssert.Equal(1, ApiHarnessClient.RequiredInt(job, "retryCount"), "P9-OPS retry count");
                var acceptedAt = BsonNullableUtc(receipt, "acceptedAtUtc")!.Value;
                var nextRetry = BsonNullableUtc(receipt, "acceptedNextRetryAtUtc")!.Value;
                HarnessAssert.True(
                    (nextRetry - acceptedAt).TotalMinutes is >= 4.9 and <= 5.1,
                    "P9-OPS retry backoff is not five minutes");
                await TraceOperationsJobAsync("RETRY_1", jobId, ct);
                return new CaseObservation(
                    "Retrying an expired RUNNING job produced one durable RETRY receipt and the bounded five-minute retry schedule.",
                    $"job={jobId};status=RETRYING;retryCount=1;backoffMinutes={(nextRetry - acceptedAt).TotalMinutes:0.###};receipt={BsonString(receipt, "receiptId")}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-RETRY-04",
            async () =>
            {
                var jobId = RequireOperationsFoundationJobId();
                var request = _opsRetryRequest
                              ?? throw new HarnessCaseNotRunnableException(
                                  "P9-OPS retry request is unavailable.");
                var before = await CaptureDatabaseSnapshotAsync(ct);
                var replay = await PostOperationsMutationAsync(jobId, "retry", request, ct);
                await VerifyOperationsReceiptAsync(
                    "P9-OPS-RETRY-04",
                    "RETRY",
                    jobId,
                    "p9-ops-retry-009",
                    replay,
                    true,
                    ct);
                var afterReplay = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(before, afterReplay, "P9-OPS exact retry replay zero-write");

                var current = await LoadJobAsync(jobId, ct);
                var mismatch = BuildOperationsCas(current, "p9-ops-retry-009");
                var rejected = await PostOperationsMutationAsync(jobId, "retry", mismatch, ct);
                ExpectError(
                    rejected,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_COMMAND_REPLAY_MISMATCH",
                    "P9-OPS changed retry replay");
                var afterMismatch = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(before, afterMismatch, "P9-OPS retry mismatch zero-write");
                return new CaseObservation(
                    "Exact retry replay returned the immutable receipt after state progression, while the same command with changed CAS payload failed before writes.",
                    $"job={jobId};exactReplay=200/isReplay;changedReplay=409/STAT_RUN_COMMAND_REPLAY_MISMATCH;db={SnapshotSha256(afterMismatch)}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-RETRY-05",
            async () =>
            {
                var jobId = RequireOperationsFoundationJobId();
                var job = await LoadJobAsync(jobId, ct);
                HarnessAssert.Equal(1, BsonInt(job, "retryCount"), "P9-OPS exhaustion starting count");
                for (var retryCount = 2; retryCount <= 5; retryCount++)
                {
                    var commandId = $"p9-ops-retry-exhaust-{retryCount:00}";
                    var request = BuildOperationsCas(job, commandId);
                    var response = await PostOperationsMutationAsync(jobId, "retry", request, ct);
                    var receipt = await VerifyOperationsReceiptAsync(
                        "P9-OPS-RETRY-05",
                        "RETRY",
                        jobId,
                        commandId,
                        response,
                        false,
                        ct);
                    var responseJob = OperationsMutationJob(response, "P9-OPS retry exhaustion");
                    HarnessAssert.Equal(
                        retryCount == 5 ? "FAILED" : "RETRYING",
                        RequiredString(responseJob, "status"),
                        $"P9-OPS retry {retryCount} status");
                    HarnessAssert.Equal(
                        retryCount,
                        ApiHarnessClient.RequiredInt(responseJob, "retryCount"),
                        $"P9-OPS retry {retryCount} count");
                    if (retryCount < 5)
                    {
                        var accepted = BsonNullableUtc(receipt, "acceptedAtUtc")!.Value;
                        var scheduled = BsonNullableUtc(receipt, "acceptedNextRetryAtUtc")!.Value;
                        var expectedMinutes = Math.Min(5 * retryCount, 60);
                        HarnessAssert.True(
                            (scheduled - accepted).TotalMinutes >= expectedMinutes - 0.1 &&
                            (scheduled - accepted).TotalMinutes <= expectedMinutes + 0.1,
                            $"P9-OPS retry {retryCount} backoff");
                    }
                    else
                    {
                        HarnessAssert.True(
                            !receipt.TryGetValue("acceptedNextRetryAtUtc", out var next) || next.IsBsonNull,
                            "P9-OPS exhausted retry retained next schedule");
                    }
                    job = await TraceOperationsJobAsync($"RETRY_{retryCount}", jobId, ct);
                }

                HarnessAssert.Equal("DEAD_LETTER", BsonString(job, "status"), "P9-OPS exhausted durable state");
                HarnessAssert.Equal(5, BsonInt(job, "retryCount"), "P9-OPS exhausted durable count");
                HarnessAssert.True(!BsonBool(job, "isActive"), "P9-OPS exhausted job stayed active");
                HarnessAssert.Equal(
                    5,
                    job.GetValue("operationReceipts", new BsonArray()).AsBsonArray.Count,
                    "P9-OPS retry receipt count");
                return new CaseObservation(
                    "Bounded retry backoff advanced 5/10/15/20 minutes and the fifth attempt deterministically dead-lettered the job with five receipts.",
                    $"job={jobId};retryCount=5;status=FAILED;active=false;receipts=5;history={BsonString(job, "operationReceiptHistoryHash")}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-RETRY-06",
            async () =>
            {
                var jobId = RequireOperationsFoundationJobId();
                var workerId = _opsWorkerId
                               ?? throw new HarnessCaseNotRunnableException("P9-OPS stale worker id is unavailable.");
                var claimToken = _opsClaimToken
                                 ?? throw new HarnessCaseNotRunnableException("P9-OPS stale claim token is unavailable.");
                var before = await CaptureDatabaseSnapshotAsync(ct);
                var staleComplete = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
                    new { workerId, claimToken },
                    Actor("admin").Token,
                    ct: ct);
                ExpectError(
                    staleComplete,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_JOB_CONFLICT",
                    "P9-OPS stale completion after retry exhaustion");
                var after = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(before, after, "P9-OPS stale completion zero-write");

                var request = await BuildOperationsFoundationCreateRequestAsync(
                    "p9-ops-cancel-foundation-012",
                    ct);
                var created = await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL",
                    request,
                    Actor("admin").Token,
                    ct);
                ApiHarnessClient.ExpectStatus(created, HttpStatusCode.Accepted, "P9-OPS cancel fixture enqueue");
                _opsCancelledJobId = RequiredString(created.Json, "jobId");
                var queued = await LoadJobAsync(_opsCancelledJobId, ct);
                var cancelRequest = BuildOperationsCas(queued, "p9-ops-cancel-012");
                var cancelled = await PostOperationsMutationAsync(
                    _opsCancelledJobId,
                    "cancel",
                    cancelRequest,
                    ct);
                await VerifyOperationsReceiptAsync(
                    "P9-OPS-RETRY-06",
                    "CANCEL",
                    _opsCancelledJobId,
                    "p9-ops-cancel-012",
                    cancelled,
                    false,
                    ct);
                var cancelledJob = OperationsMutationJob(cancelled, "P9-OPS cancel");
                HarnessAssert.Equal("FAILED", RequiredString(cancelledJob, "status"), "P9-OPS cancel status");
                HarnessAssert.Equal(
                    "STAT_RUN_JOB_CANCELLED",
                    RequiredString(cancelledJob, "diagnosticCode"),
                    "P9-OPS cancel diagnostic");
                await TraceOperationsJobAsync("CANCELLED", _opsCancelledJobId, ct);
                return new CaseObservation(
                    "The stale pre-retry worker remained fenced with a zero-write, and canonical cancel dead-lettered a separate queued job with a durable cancellation receipt.",
                    $"staleJob={jobId};staleComplete=409/zeroWrite;cancelledJob={_opsCancelledJobId};status=FAILED;diagnostic=STAT_RUN_JOB_CANCELLED");
            },
            ct);
    }

    private async Task RunOperationsResetCasesCoreAsync(CancellationToken ct)
    {
        await RunCaseAsync(
            "P9-OPS-RESET-01",
            async () =>
            {
                var jobId = RequireOperationsFoundationJobId();
                var failed = await LoadJobAsync(jobId, ct);
                HarnessAssert.Equal("DEAD_LETTER", BsonString(failed, "status"), "P9-OPS reset source state");
                _opsResetRequest = BuildOperationsCas(failed, "p9-ops-reset-013");
                var response = await PostOperationsMutationAsync(jobId, "reset", _opsResetRequest, ct);
                await VerifyOperationsReceiptAsync(
                    "P9-OPS-RESET-01",
                    "RESET",
                    jobId,
                    "p9-ops-reset-013",
                    response,
                    false,
                    ct);
                var responseJob = OperationsMutationJob(response, "P9-OPS reset");
                HarnessAssert.Equal("QUEUED", RequiredString(responseJob, "status"), "P9-OPS reset status");
                HarnessAssert.Equal(0, ApiHarnessClient.RequiredInt(responseJob, "retryCount"), "P9-OPS reset retry count");
                HarnessAssert.True(
                    !string.Equals(RequiredString(responseJob, "status"), "DONE", StringComparison.Ordinal),
                    "P9-OPS reset fabricated DONE");
                var reset = await TraceOperationsJobAsync("RESET_QUEUED", jobId, ct);
                HarnessAssert.Equal("PENDING", BsonString(reset, "status"), "P9-OPS reset durable status");
                HarnessAssert.True(BsonBool(reset, "isActive"), "P9-OPS reset job inactive");
                return new CaseObservation(
                    "Reset transformed the exhausted job into one active QUEUED attempt with retryCount zero and did not fabricate computation success.",
                    $"job={jobId};FAILED->QUEUED;retryCount=0;active=true;revision={BsonLong(reset, "stateRevision")}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-RESET-02",
            async () =>
            {
                var jobId = RequireOperationsFoundationJobId();
                var request = _opsResetRequest
                              ?? throw new HarnessCaseNotRunnableException("P9-OPS reset request is unavailable.");
                var before = await CaptureDatabaseSnapshotAsync(ct);
                var replay = await PostOperationsMutationAsync(jobId, "reset", request, ct);
                await VerifyOperationsReceiptAsync(
                    "P9-OPS-RESET-02",
                    "RESET",
                    jobId,
                    "p9-ops-reset-013",
                    replay,
                    true,
                    ct);
                var afterReplay = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(before, afterReplay, "P9-OPS reset exact replay zero-write");

                var current = await LoadJobAsync(jobId, ct);
                var mismatch = BuildOperationsCas(current, "p9-ops-reset-013");
                var rejected = await PostOperationsMutationAsync(jobId, "reset", mismatch, ct);
                ExpectError(
                    rejected,
                    HttpStatusCode.Conflict,
                    "STAT_RUN_COMMAND_REPLAY_MISMATCH",
                    "P9-OPS reset changed replay");
                var afterMismatch = await CaptureDatabaseSnapshotAsync(ct);
                AssertOperationsSnapshotEqual(before, afterMismatch, "P9-OPS reset mismatch zero-write");
                return new CaseObservation(
                    "Reset replay stayed stable after the durable state advanced; a changed request under the same command identity was rejected without writes.",
                    $"job={jobId};exactReplay=true;changedReplay=409/STAT_RUN_COMMAND_REPLAY_MISMATCH;db={SnapshotSha256(afterMismatch)}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-RESET-03",
            async () =>
            {
                var jobId = RequireOperationsFoundationJobId();
                var queued = await LoadJobAsync(jobId, ct);
                var cancelRequest = BuildOperationsCas(queued, "p9-ops-reset-race-cancel-015");
                var cancelled = await PostOperationsMutationAsync(jobId, "cancel", cancelRequest, ct);
                await VerifyOperationsReceiptAsync(
                    "P9-OPS-RESET-03",
                    "CANCEL",
                    jobId,
                    "p9-ops-reset-race-cancel-015",
                    cancelled,
                    false,
                    ct);
                var failed = await LoadJobAsync(jobId, ct);
                HarnessAssert.Equal("DEAD_LETTER", BsonString(failed, "status"), "P9-OPS reset race failed state");
                var failedRevision = BsonLong(failed, "stateRevision");
                var first = BuildOperationsCas(failed, "p9-ops-reset-race-a-015");
                var second = BuildOperationsCas(failed, "p9-ops-reset-race-b-015");
                var responses = await Task.WhenAll(
                    PostOperationsMutationAsync(jobId, "reset", first, ct),
                    PostOperationsMutationAsync(jobId, "reset", second, ct));
                var winners = responses.Where(item => item.StatusCode == HttpStatusCode.OK).ToArray();
                var losers = responses.Where(item => item.StatusCode == HttpStatusCode.Conflict).ToArray();
                HarnessAssert.Equal(1, winners.Length, "P9-OPS reset race winner count");
                HarnessAssert.Equal(1, losers.Length, "P9-OPS reset race loser count");
                ExpectError(
                    losers[0],
                    HttpStatusCode.Conflict,
                    "STAT_RUN_JOB_CONFLICT",
                    "P9-OPS reset race loser");
                HarnessAssert.Equal(
                    "OPERATION_CAS_STALE",
                    ApiHarnessClient.FindStringRecursive(losers[0].Json, "reason"),
                    "P9-OPS reset race loser reason");
                var winnerReceipt = OperationsMutationReceipt(winners[0], "P9-OPS reset race winner");
                var winningCommand = RequiredString(winnerReceipt, "commandId");
                await VerifyOperationsReceiptAsync(
                    "P9-OPS-RESET-03",
                    "RESET",
                    jobId,
                    winningCommand,
                    winners[0],
                    false,
                    ct);
                var durable = await TraceOperationsJobAsync("RESET_RACE_WINNER", jobId, ct);
                HarnessAssert.Equal(failedRevision + 1, BsonLong(durable, "stateRevision"), "P9-OPS reset race revision");
                HarnessAssert.Equal("PENDING", BsonString(durable, "status"), "P9-OPS reset race durable status");
                HarnessAssert.Equal(0, BsonInt(durable, "retryCount"), "P9-OPS reset race retry count");
                return new CaseObservation(
                    "Two different reset commands raced on one failed CAS: exactly one wrote the QUEUED transition and the loser received OPERATION_CAS_STALE.",
                    $"job={jobId};winner={winningCommand};winnerHttp=200;loserHttp=409/OPERATION_CAS_STALE;revision={failedRevision}->{BsonLong(durable, "stateRevision")}");
            },
            ct);

        await RunCaseAsync(
            "P9-OPS-RESET-04",
            async () =>
            {
                var jobId = RequireOperationsFoundationJobId();
                var workerId = "p9-ops-reset-complete-worker";
                var claim = await ClaimAsync(workerId, ct);
                var claimToken = RequiredString(claim, "claimToken");
                var claimed = ApiHarnessClient.RequiredObject(claim["job"], "P9-OPS reset completion claim");
                HarnessAssert.Equal(jobId, RequiredString(claimed, "jobId"), "P9-OPS reset completion claim id");
                var complete = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
                    new { workerId, claimToken },
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(complete, HttpStatusCode.OK, "P9-OPS reset completion");
                HarnessAssert.Equal("DONE", RequiredString(complete.Json, "status"), "P9-OPS reset completion status");
                HarnessAssert.Equal("FRESH", RequiredString(complete.Json, "freshnessState"), "P9-OPS reset freshness");
                var completed = await TraceOperationsJobAsync("RESET_DONE", jobId, ct);
                HarnessAssert.Equal("COMPLETED", BsonString(completed, "status"), "P9-OPS reset completed durable state");
                HarnessAssert.Equal("FRESH", BsonString(completed, "freshnessState"), "P9-OPS reset completed durable freshness");
                HarnessAssert.True(
                    completed.GetValue("operationReceipts", new BsonArray()).AsBsonArray.Count >= 8,
                    "P9-OPS operation receipts were not retained through completion");
                _opsReceiptVerified = true;
                _opsQueueTraceVerified = true;
                return new CaseObservation(
                    "The sole reset winner was claimed and completed normally; DONE/FRESH convergence retained the complete operator receipt history.",
                    $"job={jobId};status=DONE;freshness=FRESH;receipts={completed.GetValue("operationReceipts", new BsonArray()).AsBsonArray.Count};generation={BsonString(completed, "generationId")}");
            },
            ct);
    }
}
