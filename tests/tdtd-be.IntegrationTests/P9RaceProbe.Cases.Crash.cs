using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunRaceCrashCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-RACE-CRASH-01", async () =>
        {
            var created = await CreateJobAsync(
                "DIRECT_FIELD_TABLE_LABEL",
                await BuildCurrentRaceCreateRequestAsync(
                    "p9-race-crash-reclaim-001", ct),
                Actor("executor").Token,
                ct);
            ApiHarnessClient.ExpectStatus(created, HttpStatusCode.Accepted,
                "P9-RACE crash/reclaim enqueue");
            _raceCrashJobId = RequiredString(created.Json, "jobId");
            _raceCrashOldWorker = "p9-race-crashed-worker";
            var firstClaim = await ClaimAsync(_raceCrashOldWorker, ct);
            _raceCrashOldToken = RequiredString(firstClaim, "claimToken");
            var firstJob = ApiHarnessClient.RequiredObject(
                firstClaim["job"], "P9-RACE pre-crash claim");
            HarnessAssert.Equal(_raceCrashJobId, RequiredString(firstJob, "jobId"),
                "P9-RACE pre-crash job identity");
            var persistedBeforeCrash = await LoadJobAsync(_raceCrashJobId, ct);
            HarnessAssert.Equal("RUNNING", BsonString(persistedBeforeCrash, "status"),
                "P9-RACE durable pre-crash state");

            await RestartBackendForRaceAsync(ct);
            var persistedAfterRestart = await LoadJobAsync(_raceCrashJobId, ct);
            HarnessAssert.Equal("RUNNING", BsonString(persistedAfterRestart, "status"),
                "P9-RACE durable state after Kestrel restart");
            HarnessAssert.Equal(_raceCrashOldToken,
                BsonString(persistedAfterRestart, "claimToken"),
                "P9-RACE lease token survived process crash");
            await ForceRaceJobUtcFieldAsync(
                _raceCrashJobId,
                "leaseUntilUtc",
                DateTime.UtcNow.AddSeconds(-2),
                ct);

            _raceCrashLiveWorker = "p9-race-recovery-worker";
            var secondClaim = await ClaimAsync(_raceCrashLiveWorker, ct);
            _raceCrashLiveToken = RequiredString(secondClaim, "claimToken");
            var secondJob = ApiHarnessClient.RequiredObject(
                secondClaim["job"], "P9-RACE recovery claim");
            HarnessAssert.Equal(_raceCrashJobId, RequiredString(secondJob, "jobId"),
                "P9-RACE recovery job identity");
            HarnessAssert.True(
                !string.Equals(_raceCrashOldToken, _raceCrashLiveToken,
                    StringComparison.Ordinal),
                "P9-RACE reclaim reused the crashed worker token.");
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-CRASH-01", "PROCESS_CRASH_LEASE_RECLAIM",
                [200, 200],
                "kestrelRestarted=true;leasePersisted=true;expired=true;reclaimed=true;tokenRotated=true"));
            return new CaseObservation(
                "A claimed job survived a real Kestrel stop/start, then an expired durable lease was reclaimed with a new token.",
                "kestrelRestart=1;leasePersisted=true;reclaim=200;tokenRotated=true");
        }, ct);

        await RunCaseAsync("P9-RACE-CRASH-02", async () =>
        {
            var jobId = RequireRaceValue(_raceCrashJobId, "crash job id");
            var before = await CaptureDatabaseSnapshotAsync(ct);
            var stale = await RequireApi().PostAsync(
                $"api/testing/p9/stat-runs/jobs/{jobId}/heartbeat",
                new
                {
                    workerId = RequireRaceValue(_raceCrashOldWorker, "old worker"),
                    claimToken = RequireRaceValue(_raceCrashOldToken, "old token")
                },
                Actor("admin").Token,
                ct: ct);
            ExpectError(stale, HttpStatusCode.Conflict, "STAT_RUN_JOB_CONFLICT",
                "P9-RACE stale heartbeat after restart");
            var after = await CaptureDatabaseSnapshotAsync(ct);
            HarnessAssert.Equal(SnapshotSha256(before), SnapshotSha256(after),
                "P9-RACE stale heartbeat database snapshot");
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-CRASH-02", "STALE_HEARTBEAT",
                [(int)stale.StatusCode],
                "staleWorkerFenced=true;databaseDelta=0"));
            return new CaseObservation(
                "The pre-crash worker heartbeat was fenced after lease transfer and produced an exact zero-write snapshot.",
                "http=409;code=STAT_RUN_JOB_CONFLICT;databaseDelta=0");
        }, ct);

        await RunCaseAsync("P9-RACE-CRASH-03", async () =>
        {
            var jobId = RequireRaceValue(_raceCrashJobId, "crash job id");
            var staleTask = RequireApi().PostAsync(
                $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
                new
                {
                    workerId = RequireRaceValue(_raceCrashOldWorker, "old worker"),
                    claimToken = RequireRaceValue(_raceCrashOldToken, "old token")
                },
                Actor("admin").Token,
                ct: ct);
            var liveTask = RequireApi().PostAsync(
                $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
                new
                {
                    workerId = RequireRaceValue(_raceCrashLiveWorker, "live worker"),
                    claimToken = RequireRaceValue(_raceCrashLiveToken, "live token")
                },
                Actor("admin").Token,
                ct: ct);
            var responses = await Task.WhenAll(staleTask, liveTask);
            var winner = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
            var loser = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
            ExpectError(loser, HttpStatusCode.Conflict, "STAT_RUN_JOB_CONFLICT",
                "P9-RACE stale completion loser");
            HarnessAssert.Equal("DONE", RequiredString(winner.Json, "status"),
                "P9-RACE live completion state");
            var durable = await LoadJobAsync(jobId, ct);
            HarnessAssert.Equal("COMPLETED", BsonString(durable, "status"),
                "P9-RACE durable completion state");
            HarnessAssert.True(IsCanonicalSha(BsonString(durable, "generationId")),
                "P9-RACE durable generation id");
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-CRASH-03", "STALE_LIVE_COMPLETE_RACE",
                responses.Select(response => (int)response.StatusCode).Order().ToArray(),
                "liveWinner=1;staleLoser=1;terminal=DONE;generationCount=1"));
            return new CaseObservation(
                "Stale and current completions raced; only the live fence published one terminal generation.",
                "http=200+409;liveWinner=1;terminal=DONE;generationCount=1");
        }, ct);

        await RunCaseAsync("P9-RACE-CRASH-04", async () =>
        {
            var created = await CreateJobAsync(
                "DIRECT_FIELD_TABLE_LABEL",
                await BuildCurrentRaceCreateRequestAsync(
                    "p9-race-retry-due-004", ct),
                Actor("executor").Token,
                ct);
            ApiHarnessClient.ExpectStatus(created, HttpStatusCode.Accepted,
                "P9-RACE retry enqueue");
            var jobId = RequiredString(created.Json, "jobId");
            const string oldWorker = "p9-race-retry-old-worker";
            var claim = await ClaimAsync(oldWorker, ct);
            var oldToken = RequiredString(claim, "claimToken");
            var retry = await RequireApi().PostAsync(
                $"api/testing/p9/stat-runs/jobs/{jobId}/retry",
                new { workerId = oldWorker, claimToken = oldToken, failureCode = "TRANSIENT_TEST" },
                Actor("admin").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(retry, HttpStatusCode.OK,
                "P9-RACE transient retry");
            HarnessAssert.Equal("RETRYING", RequiredString(retry.Json, "status"),
                "P9-RACE retry common state");
            await ForceRaceJobUtcFieldAsync(
                jobId, "nextRetryAtUtc", DateTime.UtcNow.AddSeconds(-2), ct);

            var claims = await Task.WhenAll(
                RequireApi().PostAsync(
                    "api/testing/p9/stat-runs/jobs/claim",
                    new { workerId = "p9-race-retry-a" },
                    Actor("admin").Token,
                    ct: ct),
                RequireApi().PostAsync(
                    "api/testing/p9/stat-runs/jobs/claim",
                    new { workerId = "p9-race-retry-b" },
                    Actor("admin").Token,
                    ct: ct));
            var winnerIndex = Array.FindIndex(
                claims, response => response.StatusCode == HttpStatusCode.OK);
            HarnessAssert.True(winnerIndex >= 0,
                "P9-RACE retry concurrent claim did not produce a winner.");
            var winner = claims[winnerIndex];
            var root = ApiHarnessClient.RequiredObject(winner.Json,
                "P9-RACE retry claim winner");
            var liveJob = ApiHarnessClient.RequiredObject(root["job"],
                "P9-RACE retry live job");
            var liveWorker = winnerIndex == 0
                ? "p9-race-retry-a"
                : "p9-race-retry-b";
            var liveToken = RequiredString(root, "claimToken");
            try
            {
                HarnessAssert.Equal(1, claims.Count(response => response.StatusCode == HttpStatusCode.NoContent),
                    "P9-RACE retry concurrent claim loser count");
                HarnessAssert.Equal(jobId, RequiredString(liveJob, "jobId"),
                    "P9-RACE retry concurrent claim identity");
                var stale = await RequireApi().PostAsync(
                    $"api/testing/p9/stat-runs/jobs/{jobId}/heartbeat",
                    new { workerId = oldWorker, claimToken = oldToken },
                    Actor("admin").Token,
                    ct: ct);
                ExpectError(stale, HttpStatusCode.Conflict, "STAT_RUN_JOB_CONFLICT",
                    "P9-RACE old retry worker fence");
            }
            finally
            {
                await CompleteRaceJobAsync(jobId, liveWorker, liveToken, ct);
            }
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-CRASH-04", "RETRY_DUE_RECLAIM",
                claims.Select(response => (int)response.StatusCode).Order().ToArray(),
                "retryCount=1;dueInjected=true;claimWinner=1;oldTokenFenced=true;terminal=DONE"));
            return new CaseObservation(
                "A transient failure entered retry wait; once due, concurrent reclaim had one winner and permanently fenced the old token.",
                "retryCount=1;claim=200+204;oldToken=409;terminal=DONE");
        }, ct);

        await RunCaseAsync("P9-RACE-CRASH-05", async () =>
        {
            await SetRaceAssignmentOwnerAsync("admin", ct);
            try
            {
            var report = await LoadLifecycleReportAsync(ct);
            HarnessAssert.Equal(2, BsonInt(report, "status"),
                "P9-RACE duplicate approve fixture must already be approved.");
            var reorderedBeforeApprove = await ProcessLifecycleOutboxAsync(20, ct);
            HarnessAssert.True(_lfcApproveRequest is not null,
                "P9-RACE approved fixture request is unavailable.");
            var firstApprove = RequireApi().PostAsync(
                $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
                _lfcApproveRequest.DeepClone(),
                Actor("admin").Token,
                ct: ct);
            var duplicateApprove = Task.Run(async () =>
            {
                await Task.Delay(25, ct);
                return await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
                    _lfcApproveRequest.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
            }, ct);
            var approveResponses = await Task.WhenAll(firstApprove, duplicateApprove);
            foreach (var response in approveResponses)
                ExpectSuccess(response, "P9-RACE duplicate approve");
            var approveWorkers = await Task.WhenAll(
                ProcessLifecycleOutboxAsync(20, ct),
                ProcessLifecycleOutboxAsync(20, ct));
            report = await LoadLifecycleReportAsync(ct);
            var approveEntry = FindOutboxEntry(report, "REVIEW_APPROVE");
            HarnessAssert.Equal("COMPLETED", BsonString(approveEntry, "state"),
                "P9-RACE approve outbox state");
            HarnessAssert.Equal("PUBLISHED", BsonString(approveEntry, "directProjectionState"),
                "P9-RACE approve projection state");
            var approvedRunId = BsonString(approveEntry, "directProjectionRunId");

            var recallRequest = new JsonObject
            {
                ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                ["commandId"] = "p9-race-recall-approved-005",
                ["comment"] = "P9-RACE canonical reversal"
            };
            var recallTask = RequireApi().PostAsync(
                $"api/work-assignment-review/reports/{Fixture().ReportId}/recall-approved",
                recallRequest.DeepClone(),
                Actor("admin").Token,
                ct: ct);
            var oldApproveReplayTask = Task.Run(async () =>
            {
                await Task.Delay(25, ct);
                return await RequireApi().PostAsync(
                    $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
                    _lfcApproveRequest.DeepClone(),
                    Actor("admin").Token,
                    ct: ct);
            }, ct);
            var lifecycleRace = await Task.WhenAll(recallTask, oldApproveReplayTask);
            ExpectSuccess(lifecycleRace[0], "P9-RACE recall-approved");
            HarnessAssert.True(
                lifecycleRace[1].StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                "P9-RACE old approve replay returned an unexpected status.");
            var reversalWorkers = await Task.WhenAll(
                ProcessLifecycleOutboxAsync(20, ct),
                ProcessLifecycleOutboxAsync(20, ct),
                ProcessLifecycleOutboxAsync(20, ct));
            report = await LoadLifecycleReportAsync(ct);
            var reversalEntry = FindOutboxEntry(report, "REVIEW_RECALL_APPROVED");
            HarnessAssert.Equal("COMPLETED", BsonString(reversalEntry, "state"),
                "P9-RACE reversal outbox state");
            HarnessAssert.Equal("PUBLISHED", BsonString(reversalEntry, "directProjectionState"),
                "P9-RACE reversal projection state");
            var reversalRunId = BsonString(reversalEntry, "directProjectionRunId");
            HarnessAssert.True(
                !string.Equals(approvedRunId, reversalRunId, StringComparison.Ordinal),
                "P9-RACE reversal reused the approval publication root.");
            var approvedJob = await LoadJobAsync(approvedRunId, ct);
            var reversalJob = await LoadJobAsync(reversalRunId, ct);
            HarnessAssert.True(!BsonBool(approvedJob, "isCurrentPublication"),
                "P9-RACE approval publication stayed current after reversal.");
            HarnessAssert.True(BsonBool(reversalJob, "isCurrentPublication"),
                "P9-RACE reversal is not the current publication.");
            await SeedCanonicalP7MappingLineageAsync(
                ct,
                "p9-race-p7-reapprove-005",
                _flwIncludeVersionId);
            report = await LoadLifecycleReportAsync(ct);
            const string reapproveCommand = "p9-race-reapprove-current-005";
            var reapproveRequest = new JsonObject
            {
                ["expectedPayloadRevision"] = BsonInt(report, "payloadRevision"),
                ["expectedLifecycleRevision"] = BsonInt(report, "lifecycleRevision"),
                ["commandId"] = reapproveCommand,
                ["comment"] = "P9-RACE restore canonical current source after reversal"
            };
            var reapprove = await RequireApi().PostAsync(
                $"api/work-assignment-review/reports/{Fixture().ReportId}/approve",
                reapproveRequest,
                Actor("admin").Token,
                ct: ct);
            ExpectOperationsSuccess(reapprove, "P9-RACE reapprove after reversal");
            var reapproveEntry = await DrainOperationsLifecycleEntryAsync(
                "REVIEW_APPROVE",
                "PUBLISHED",
                reapproveCommand,
                ct);
            var reapprovedRunId = BsonString(reapproveEntry, "directProjectionRunId");
            var reversalAfterReapprove = await LoadJobAsync(reversalRunId, ct);
            HarnessAssert.True(!BsonBool(reversalAfterReapprove, "isCurrentPublication"),
                "P9-RACE reversal stayed current after canonical reapproval.");
            HarnessAssert.True(
                !string.Equals(reversalRunId, reapprovedRunId, StringComparison.Ordinal),
                "P9-RACE reapproval reused the reversal publication root.");
            _lfcRunId = reapprovedRunId;
            _lfcGenerationId = BsonString(reapproveEntry, "directProjectionGenerationId");
            _lfcGenerationHash = BsonString(reapproveEntry, "directProjectionGenerationHash");
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-CRASH-05", "LIFECYCLE_APPROVE_REVERSE_REORDER",
                approveResponses.Concat(lifecycleRace)
                    .Append(reapprove)
                    .Select(response => (int)response.StatusCode).ToArray(),
                $"preApproveProcessed={ReadProcessed(reorderedBeforeApprove)};approveWorkers={approveWorkers.Sum(ReadProcessed)};reversalWorkers={reversalWorkers.Sum(ReadProcessed)};reapproved=true;oneCurrent=true;priorImmutable=true"));
            _raceCrashVerified = true;
            _raceQueueVerified = true;
            return new CaseObservation(
                "Duplicate/reordered approval, an old approve replay, reversal, and canonical reapproval converged with one current publication and immutable predecessors.",
                "approve=duplicate/recovered;reversal=published;reapprove=published;workers=2+3;oneCurrent=true;priorImmutable=true");
            }
            finally
            {
                await SetRaceAssignmentOwnerAsync("executor", ct);
            }
        }, ct);
    }

    private async Task ForceRaceJobUtcFieldAsync(
        string jobId,
        string field,
        DateTime value,
        CancellationToken ct)
    {
        var rounded = new DateTime(
            value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
        var job = await LoadJobAsync(jobId, ct);
        var originalStateHash = BsonString(job, "stateHash");
        var mutated = (BsonDocument)job.DeepClone();
        mutated[field] = rounded;
        var nextStateHash = ComputeJobStateHash(mutated);
        var result = await RequireDatabase()
            .GetCollection<BsonDocument>("work_report_statistic_rebuild_jobs")
            .UpdateOneAsync(
                new BsonDocument
                {
                    ["_id"] = ObjectId.Parse(jobId),
                    ["stateRevision"] = BsonLong(job, "stateRevision"),
                    ["stateHash"] = originalStateHash
                },
                Builders<BsonDocument>.Update
                    .Set(field, rounded)
                    .Set("stateHash", nextStateHash),
                cancellationToken: ct);
        HarnessAssert.Equal(1L, result.ModifiedCount,
            $"P9-RACE due-time CAS injection {field}");
    }

    private static string RequireRaceValue(string? value, string dependency)
        => !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new HarnessCaseNotRunnableException(
                $"Missing P9-RACE prerequisite: {dependency}.");
}
