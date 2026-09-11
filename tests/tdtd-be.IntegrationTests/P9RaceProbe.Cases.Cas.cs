using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task RunRaceCasesAsync(CancellationToken ct)
    {
        await RunRaceCasCasesAsync(ct);
        await RunRaceCrashCasesAsync(ct);
        await SeedRaceExportFixturesAsync(ct);
        await RunRaceSecurityCasesAsync(ct);
        await RunRaceCleanCasesAsync(ct);
    }

    private async Task RunRaceCasCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P9-RACE-CAS-01", async () =>
        {
            var before = await CountJobsAsync(ct);
            var request = await BuildCurrentRaceCreateRequestAsync(
                "p9-race-cas-equal-001", ct);
            var responses = await Task.WhenAll(
                CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL", request,
                    Actor("executor").Token, ct),
                CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL", request,
                    Actor("executor").Token, ct));
            var statuses = responses
                .Select(response => response.StatusCode)
                .OrderBy(status => (int)status)
                .ToArray();
            HarnessAssert.True(
                statuses.SequenceEqual(
                    new[] { HttpStatusCode.OK, HttpStatusCode.Accepted }),
                "Concurrent identical create did not yield one winner and one replay.");
            var jobIds = responses
                .Select(response => RequiredString(response.Json, "jobId"))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var receiptIds = responses
                .Select(response => RequiredString(response.Json, "receiptId"))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            HarnessAssert.Equal(1, jobIds.Length, "Concurrent identical job identity");
            HarnessAssert.Equal(1, receiptIds.Length, "Concurrent identical receipt identity");
            HarnessAssert.Equal(before + 1, await CountJobsAsync(ct), "Concurrent identical durable delta");
            HarnessAssert.Equal(1, responses.Count(response => RequiredBool(response.Json, "isReplay")),
                "Concurrent identical replay count");

            _raceSecurityJobId = jobIds[0];
            var durable = await LoadJobAsync(jobIds[0], ct);
            HarnessAssert.Equal(receiptIds[0], BsonString(durable, "receiptId"),
                "Concurrent identical durable receipt");
            _raceReceipts.Add(new P9RaceReceiptEvidence(
                "P9-RACE-CAS-01",
                "CREATE",
                receiptIds[0],
                BsonString(durable, "requestHash"),
                true,
                true));
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-CAS-01", "IDENTICAL_CREATE",
                statuses.Select(status => (int)status).ToArray(),
                "winner=1;replay=1;jobDelta=1;sameJob=true;sameReceipt=true"));
            await CompleteNextRaceJobAsync(jobIds[0], "p9-race-cas01-worker", ct);
            _raceReceiptVerified = true;
            return new CaseObservation(
                "Two simultaneous identical creates converged on one durable job and receipt; the loser was an exact replay.",
                "http=200+202;winner=1;replay=1;jobDelta=1;sameJob=true;sameReceipt=true");
        }, ct);

        await RunCaseAsync("P9-RACE-CAS-02", async () =>
        {
            var before = await CountJobsAsync(ct);
            var valid = await BuildCurrentRaceCreateRequestAsync(
                "p9-race-cas-mismatch-002", ct);
            var changed = valid.DeepClone() as JsonObject
                          ?? throw new InvalidOperationException("P9-RACE request clone failed.");
            changed["expectedSourceHash"] = new string('b', 64);
            var validTask = CreateJobAsync(
                "DIRECT_FIELD_TABLE_LABEL", valid,
                Actor("executor").Token, ct);
            var changedTask = Task.Run(async () =>
            {
                await Task.Delay(25, ct);
                return await CreateJobAsync(
                    "DIRECT_FIELD_TABLE_LABEL", changed,
                    Actor("executor").Token, ct);
            }, ct);
            var responses = await Task.WhenAll(validTask, changedTask);
            var winner = responses.Single(response =>
                response.StatusCode == HttpStatusCode.Accepted);
            var loser = responses.Single(response =>
                response.StatusCode == HttpStatusCode.Conflict);
            ExpectError(
                loser,
                HttpStatusCode.Conflict,
                "STAT_RUN_COMMAND_REPLAY_MISMATCH",
                "P9-RACE changed-payload create loser");
            HarnessAssert.Equal(before + 1, await CountJobsAsync(ct),
                "Changed-payload race durable delta");
            var jobId = RequiredString(winner.Json, "jobId");
            var replay = await CreateJobAsync(
                "DIRECT_FIELD_TABLE_LABEL", valid,
                Actor("executor").Token, ct);
            ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK,
                "P9-RACE changed-payload winner replay");
            HarnessAssert.Equal(jobId, RequiredString(replay.Json, "jobId"),
                "Changed-payload winner replay identity");
            await CompleteNextRaceJobAsync(jobId, "p9-race-cas02-worker", ct);
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-CAS-02", "CHANGED_PAYLOAD_CREATE",
                responses.Select(response => (int)response.StatusCode).Order().ToArray(),
                "winner=1;loser=REPLAY_MISMATCH;jobDelta=1;winnerReplayStable=true"));
            return new CaseObservation(
                "A changed payload using the same command lost to the canonical winner, stayed zero-write, and could not poison later replay.",
                "http=202+409;reason=STAT_RUN_COMMAND_REPLAY_MISMATCH;jobDelta=1;winnerReplay=200");
        }, ct);

        await RunCaseAsync("P9-RACE-CAS-03", async () =>
        {
            var before = await CountJobsAsync(ct);
            await SetRaceAssignmentOwnerAsync("executor", ct);
            ApiHarnessResponse[] responses;
            try
            {
                responses = await Task.WhenAll(
                    CreateJobAsync(
                        "DIRECT_FIELD_TABLE_LABEL",
                        await BuildCurrentRaceCreateRequestAsync(
                            "p9-race-active-dedupe-a-003", ct),
                        Actor("executor").Token,
                        ct),
                    CreateJobAsync(
                        "DIRECT_FIELD_TABLE_LABEL",
                        await BuildCurrentRaceCreateRequestAsync(
                            "p9-race-active-dedupe-b-003", ct),
                        Actor("executor2").Token,
                        ct));
            }
            finally
            {
                await SetRaceAssignmentOwnerAsync("executor", ct);
            }
            var winner = responses.Single(response =>
                response.StatusCode == HttpStatusCode.Accepted);
            var loser = responses.Single(response =>
                response.StatusCode == HttpStatusCode.Conflict);
            ExpectError(
                loser,
                HttpStatusCode.Conflict,
                "STAT_RUN_JOB_CONFLICT",
                "P9-RACE active-dedupe loser");
            HarnessAssert.Equal(
                "ACTIVE_RUN_ALREADY_EXISTS",
                ApiHarnessClient.FindStringRecursive(loser.Json, "reason"),
                "P9-RACE active-dedupe reason");
            var winnerId = RequiredString(winner.Json, "jobId");
            HarnessAssert.True(
                !loser.Body.Contains(winnerId, StringComparison.Ordinal),
                "Active-dedupe loser leaked the other actor's job ID.");
            HarnessAssert.Equal(before + 1, await CountJobsAsync(ct),
                "Active-dedupe race durable delta");
            await CompleteNextRaceJobAsync(
                winnerId, "p9-race-cas03-worker", ct);
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-CAS-03", "CROSS_ACTOR_ACTIVE_DEDUPE",
                responses.Select(response => (int)response.StatusCode).Order().ToArray(),
                "winner=1;loser=ACTIVE_RUN_ALREADY_EXISTS;jobDelta=1;winnerIdLeak=false"));
            return new CaseObservation(
                "Distinct cross-actor commands sharing one active dedupe key produced one winner and a non-leaking conflict.",
                "http=202+409;reason=ACTIVE_RUN_ALREADY_EXISTS;jobDelta=1;winnerIdLeak=false");
        }, ct);

        await RunCaseAsync("P9-RACE-CAS-04", async () =>
        {
            var created = await CreateJobAsync(
                "DIRECT_FIELD_TABLE_LABEL",
                await BuildCurrentRaceCreateRequestAsync(
                    "p9-race-claim-004", ct),
                Actor("executor").Token,
                ct);
            ApiHarnessClient.ExpectStatus(created, HttpStatusCode.Accepted,
                "P9-RACE concurrent claim fixture");
            var jobId = RequiredString(created.Json, "jobId");
            var claims = await Task.WhenAll(
                RequireApi().PostAsync(
                    "api/testing/p9/stat-runs/jobs/claim",
                    new { workerId = "p9-race-claim-a" },
                    Actor("admin").Token,
                    ct: ct),
                RequireApi().PostAsync(
                    "api/testing/p9/stat-runs/jobs/claim",
                    new { workerId = "p9-race-claim-b" },
                    Actor("admin").Token,
                    ct: ct));
            var winnerIndex = Array.FindIndex(
                claims, response => response.StatusCode == HttpStatusCode.OK);
            HarnessAssert.True(winnerIndex >= 0,
                "Concurrent claim did not produce a winner.");
            var winner = claims[winnerIndex];
            var root = ApiHarnessClient.RequiredObject(winner.Json, "P9-RACE claim winner");
            var job = ApiHarnessClient.RequiredObject(root["job"], "P9-RACE claimed job");
            var workerId = winnerIndex == 0
                ? "p9-race-claim-a"
                : "p9-race-claim-b";
            var token = RequiredString(root, "claimToken");
            try
            {
                HarnessAssert.Equal(1, claims.Count(response => response.StatusCode == HttpStatusCode.OK),
                    "Concurrent claim winner count");
                HarnessAssert.Equal(1, claims.Count(response => response.StatusCode == HttpStatusCode.NoContent),
                    "Concurrent claim loser count");
                HarnessAssert.Equal(jobId, RequiredString(job, "jobId"),
                    "Concurrent claim job identity");
            }
            finally
            {
                await CompleteRaceJobAsync(jobId, workerId, token, ct);
            }
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-CAS-04", "WORKER_CLAIM",
                claims.Select(response => (int)response.StatusCode).Order().ToArray(),
                "winner=1;empty=1;leaseToken=unique;completion=DONE"));
            return new CaseObservation(
                "Two workers claimed one queued job concurrently; one acquired the lease and one observed an empty queue.",
                "http=200+204;winner=1;lease=unique;completion=DONE");
        }, ct);

        await RunCaseAsync("P9-RACE-CAS-05", async () =>
        {
            var created = await CreateJobAsync(
                "DIRECT_FIELD_TABLE_LABEL",
                await BuildCurrentRaceCreateRequestAsync(
                    "p9-race-operation-cas-005", ct),
                Actor("executor").Token,
                ct);
            ApiHarnessClient.ExpectStatus(created, HttpStatusCode.Accepted,
                "P9-RACE operations CAS fixture");
            var jobId = RequiredString(created.Json, "jobId");
            var claim = await ClaimAsync("p9-race-cas05-failing-worker", ct);
            var claimToken = RequiredString(claim, "claimToken");
            var failed = await RequireApi().PostAsync(
                $"api/testing/p9/stat-runs/jobs/{jobId}/retry",
                new
                {
                    workerId = "p9-race-cas05-failing-worker",
                    claimToken,
                    failureCode = "TERMINAL_TEST"
                },
                Actor("admin").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(failed, HttpStatusCode.OK,
                "P9-RACE terminal failure fixture");
            var terminal = await LoadJobAsync(jobId, ct);
            HarnessAssert.Equal("DEAD_LETTER", BsonString(terminal, "status"),
                "P9-RACE CAS source state");
            var revision = BsonLong(terminal, "stateRevision");
            var responses = await Task.WhenAll(
                PostOperationsMutationAsync(
                    jobId, "reset",
                    BuildOperationsCas(terminal, "p9-race-reset-a-005"), ct),
                PostOperationsMutationAsync(
                    jobId, "reset",
                    BuildOperationsCas(terminal, "p9-race-reset-b-005"), ct));
            var winner = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
            var loser = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
            ExpectError(loser, HttpStatusCode.Conflict, "STAT_RUN_JOB_CONFLICT",
                "P9-RACE operations CAS loser");
            HarnessAssert.Equal(
                "OPERATION_CAS_STALE",
                ApiHarnessClient.FindStringRecursive(loser.Json, "reason"),
                "P9-RACE operations CAS loser reason");
            var durable = await LoadJobAsync(jobId, ct);
            HarnessAssert.Equal(revision + 1, BsonLong(durable, "stateRevision"),
                "P9-RACE operations CAS revision delta");
            HarnessAssert.Equal(1,
                durable.GetValue("operationReceipts", new BsonArray()).AsBsonArray.Count,
                "P9-RACE operations CAS receipt count");
            var receipt = OperationsMutationReceipt(winner, "P9-RACE CAS winner");
            _raceReceipts.Add(new P9RaceReceiptEvidence(
                "P9-RACE-CAS-05",
                "RESET",
                RequiredString(receipt, "receiptId"),
                RequiredString(receipt, "requestHash"),
                true,
                false));
            await CompleteNextRaceJobAsync(jobId, "p9-race-cas05-complete", ct);
            _raceTimeline.Add(new P9RaceTimelineEntry(
                "P9-RACE-CAS-05", "OPERATION_STATE_CAS",
                responses.Select(response => (int)response.StatusCode).Order().ToArray(),
                "winner=1;loser=OPERATION_CAS_STALE;revisionDelta=1;receiptDelta=1"));
            _raceCasVerified = true;
            return new CaseObservation(
                "Two different reset commands raced on one terminal CAS; exactly one advanced state and wrote one operation receipt.",
                "http=200+409;reason=OPERATION_CAS_STALE;revisionDelta=1;receiptDelta=1");
        }, ct);
    }

    private async Task SetRaceAssignmentOwnerAsync(
        string actorKey,
        CancellationToken ct)
    {
        var actor = Actor(actorKey);
        var result = await RequireDatabase().GetCollection<BsonDocument>("work_assignments")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(Fixture().AssignmentId)),
                Builders<BsonDocument>.Update.Set(
                    "createdByUserId", ObjectId.Parse(actor.Id)),
                cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            result.MatchedCount,
            $"P9-RACE assignment owner fixture {actorKey}");
    }

    private async Task<ApiHarnessResponse> CompleteNextRaceJobAsync(
        string expectedJobId,
        string workerId,
        CancellationToken ct)
    {
        var claim = await ClaimAsync(workerId, ct);
        var job = ApiHarnessClient.RequiredObject(claim["job"],
            $"P9-RACE claim {workerId}");
        HarnessAssert.Equal(expectedJobId, RequiredString(job, "jobId"),
            $"P9-RACE claim identity {workerId}");
        return await CompleteRaceJobAsync(
            expectedJobId,
            workerId,
            RequiredString(claim, "claimToken"),
            ct);
    }

    private async Task<ApiHarnessResponse> CompleteRaceJobAsync(
        string jobId,
        string workerId,
        string claimToken,
        CancellationToken ct)
    {
        var response = await RequireApi().PostAsync(
            $"api/testing/p9/stat-runs/jobs/{jobId}/complete",
            new { workerId, claimToken },
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK,
            $"P9-RACE complete {jobId}");
        HarnessAssert.Equal("DONE", RequiredString(response.Json, "status"),
            $"P9-RACE completed state {jobId}");
        return response;
    }
}
