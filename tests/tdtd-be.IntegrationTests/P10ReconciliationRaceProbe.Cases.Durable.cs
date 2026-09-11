using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Models.Statistics;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationRaceProbe
{
    private async Task RunCasCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P10-CAS-01", ConcurrentReceiptOneWinnerAsync, ct);
        await RunCaseAsync("P10-CAS-02", PublicationCasOneWinnerAsync, ct);
        await RunCaseAsync("P10-CAS-03", RecheckMarkerCasOneWinnerAsync, ct);
        await RunCaseAsync("P10-CAS-04", ReviewDecisionMongoCasAsync, ct);
        await RunCaseAsync("P10-CAS-05", EvidenceMongoAppendCasAsync, ct);
    }

    private async Task RunCrashCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P10-CRASH-01", ExpectedCommitLastRecoveryAsync, ct);
        await RunCaseAsync("P10-CRASH-02", LostLeaseFencesStaleWorkerAsync, ct);
        await RunCaseAsync("P10-CRASH-03", TrustedFinalizerCrashReplayAsync, ct);
        await RunCaseAsync("P10-CRASH-04", FifthReviewGateCrashReplayAsync, ct);
        await RunCaseAsync("P10-CRASH-05", EvidenceResponseLossRecoveryAsync, ct);
    }

    private async Task<P10RaceEightColumnEvidence> ConcurrentReceiptOneWinnerAsync(
        CancellationToken ct)
    {
        var collection = Database().GetCollection<BsonDocument>(
            P10ReconciliationCoreProbe.RunCollection);
        var request = CoreCreateRequest("p10-race-cas-01");
        request["conceptKey"] = "RACE_RECEIPT_ONE_WINNER";
        var before = await collection.CountDocumentsAsync(
            FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Api().PostAsync(ReconciliationBasePath(), request.DeepClone(),
                _executorToken, ct: ct)));
        HarnessAssert.Equal(1, responses.Count(value =>
                value.StatusCode == HttpStatusCode.Accepted),
            "P10 CAS-01 durable winner count");
        HarnessAssert.Equal(15, responses.Count(value =>
                value.StatusCode == HttpStatusCode.OK),
            "P10 CAS-01 replay count");
        var ids = responses.Select(value => ResponseString(
                value, "reconciliationId"))
            .Distinct(StringComparer.Ordinal).ToArray();
        HarnessAssert.Equal(1, ids.Length,
            "P10 CAS-01 durable identity cardinality");
        _cas01RunId = ids[0];
        HarnessAssert.Equal(before + 1,
            await collection.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty, cancellationToken: ct),
            "P10 CAS-01 receipt cardinality");

        var changed = (JsonObject)request.DeepClone();
        changed["conceptKey"] = "RACE_RECEIPT_CHANGED_BODY";
        var changedResponse = await Api().PostAsync(
            ReconciliationBasePath(), changed, _executorToken, ct: ct);
        ExpectRaceError(changedResponse, HttpStatusCode.Conflict,
            "STAT_RECONCILIATION_COMMAND_REPLAY_MISMATCH",
            "P10 CAS-01 changed replay");
        HarnessAssert.Equal(before + 1,
            await collection.CountDocumentsAsync(
                FilterDefinition<BsonDocument>.Empty, cancellationToken: ct),
            "P10 CAS-01 changed replay write count");

        var run = await LoadRaceRunAsync(_cas01RunId, ct);
        var cancel = await Api().PostAsync(
            $"{ReconciliationBasePath()}/{_cas01RunId}/cancel",
            new
            {
                commandId = "p10-race-cas-01-cancel",
                expectedStateRevision = run["stateRevision"].ToInt64(),
                expectedStateHash = run["stateHash"].AsString
            },
            _executorToken,
            ct: ct);
        ApiHarnessClient.ExpectStatus(cancel, HttpStatusCode.OK,
            "P10 CAS-01 cleanup cancel");
        var post = await LoadRaceRunAsync(_cas01RunId, ct);
        HarnessAssert.Equal("CANCELLED", post["status"].AsString,
            "P10 CAS-01 cleanup terminal status");
        return P10RaceEightColumnEvidence.Pass("P10-CAS-01",
            new { productionRoute = "CreateAsync", workers = 16, sameCommand = true },
            new { winnerCount = 1, replayCount = 15, changedReplayConflict = true },
            new { winnerCount = 1, replayCount = 15, changedReplayConflict = true },
            new { durableDocumentDelta = 1, conflictingReplayWrites = 0, orphanCount = 0 });
    }

    private async Task<P10RaceEightColumnEvidence> PublicationCasOneWinnerAsync(
        CancellationToken ct)
    {
        var activation = new P10RaceActivation(RaceCandidateBinding());
        var time = new P10RaceFixedTimeService(FixedUtc);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = RaceRunConfiguration();
        var runService = new StatisticReconciliationRunService(
            Context(), activation, new P10RaceStatRunActivation(), time,
            configuration, cache);
        var actor = RaceActor(ObjectId.GenerateNewId().ToString(),
            "p10-race-cas-02");
        var run = InvokeTrustedFixture<StatisticReconciliationRun>(
            "BuildRunningRun", activation.Binding, FixedUtc, actor);
        await Context().StatisticReconciliationRuns.InsertOneAsync(run,
            cancellationToken: ct);
        var boundary = InvokeTrustedFixture<
            StatisticReconciliationActualCoherentBoundary>(
            "BuildBoundary", run);
        var steps = InvokeTrustedFixture<ImmutableArray<
            StatisticReconciliationActualCoherentCaptureStep>>(
            "BuildCaptureSteps", boundary);
        var coherent = await new
            StatisticReconciliationActualCoherentCaptureCoordinator()
            .CaptureAsync(run.Id,
                new P10RaceStableBoundaryReader(boundary), steps, ct);
        var publicationContext = InvokeTrustedFixture<
            StatisticReconciliationActualPublicationContext>(
            "BuildPublicationContext", run, boundary, coherent);
        var noPublish = new P10NoopActualGenerationCas();
        var publisher = new StatisticReconciliationActualGenerationPublisher(
            new StatisticReconciliationActualObservationMongoBackend(Context()),
            noPublish);
        var generation = await publisher.PublishCoherentAsync(
            coherent, publicationContext, FixedUtc,
            run.LeaseOwnerId!, run.ClaimToken!, actor, ct);
        HarnessAssert.Equal(1, noPublish.Calls,
            "P10 CAS-02 committed-generation staging calls");

        var request = new tdtd_be.DTOs.StatisticsReconciliation
            .StatisticReconciliationPendingPublishRequest
        {
            WorkerId = run.LeaseOwnerId!,
            ClaimToken = run.ClaimToken!,
            GenerationId = generation.GenerationId,
            GenerationHash = generation.GenerationSemanticSha256
        };
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            runService.PublishPendingAsync(run.Id, request, actor, ct)));
        var post = await Context().StatisticReconciliationRuns
            .Find(value => value.Id == run.Id).SingleAsync(ct);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(post);
        var commitCount = await Context().StatisticReconciliationObservations
            .CountDocumentsAsync(value => value.ReconciliationId == run.Id &&
                value.GenerationId == generation.GenerationId &&
                value.RecordKind ==
                    StatisticReconciliationActualPublicationRecordKinds.Commit,
                cancellationToken: ct);
        HarnessAssert.True(responses.All(value =>
                value.StateRevision == run.StateRevision + 1 &&
                value.StateHash == post.StateHash),
            "P10 CAS-02 concurrent production responses did not converge");
        HarnessAssert.True(post.PendingGenerationId == generation.GenerationId &&
                           post.PendingGenerationHash ==
                           generation.GenerationSemanticSha256 &&
                           post.StateRevision == run.StateRevision + 1 &&
                           post.GenerationPublishRevision == 1 &&
                           commitCount == 1,
            "P10 CAS-02 durable one-CAS publication invalid");
        return P10RaceEightColumnEvidence.Pass("P10-CAS-02",
            new { productionService = "PublishPendingAsync", workers = 12 },
            new { winnerCount = 1, convergedReplayCount = 11, commitCount = 1 },
            new { winnerCount = 1, convergedReplayCount = 11, commitCount },
            new { stateRevisionDelta = 1, publicationRevisionDelta = 1, orphanCount = 0 });
    }

    private async Task<P10RaceEightColumnEvidence> RecheckMarkerCasOneWinnerAsync(
        CancellationToken ct)
    {
        try
        {
        var terminal = await CreateTerminalCoreRunAsync("cas-03", ct);
        HarnessAssert.Equal(StatisticReconciliationRunStatuses.Matched,
            terminal.Status, "P10 CAS-03 terminal base status");
        HarnessAssert.True(
            terminal.InitialDeadlineAtUtc == terminal.DeadlineAtUtc,
            "P10 CAS-03 new row initial deadline pin");
        var legacyUnset = await Context().StatisticReconciliationRuns
            .UpdateOneAsync(
                value =>
                    value.Id == terminal.Id &&
                    value.StateRevision == terminal.StateRevision &&
                    value.StateHash == terminal.StateHash &&
                    value.InitialDeadlineAtUtc ==
                        terminal.InitialDeadlineAtUtc,
                Builders<StatisticReconciliationRun>.Update.Unset(
                    value => value.InitialDeadlineAtUtc),
                cancellationToken: ct);
        HarnessAssert.True(
            legacyUnset.MatchedCount == 1 &&
            legacyUnset.ModifiedCount == 1,
            "P10 CAS-03 legacy deadline pin fixture CAS");
        terminal = await Context().StatisticReconciliationRuns
            .Find(value => value.Id == terminal.Id)
            .SingleAsync(ct);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            terminal);
        HarnessAssert.True(terminal.InitialDeadlineAtUtc is null,
            "P10 CAS-03 legacy row must rely on deadline fallback");
        await InstallRecheckSuccessorP9Async(ct);
        _cas03RunId = terminal.Id;
        var beginBody = new
        {
            commandId = "p10-race-cas-03-begin",
            expectedStateRevision = terminal.StateRevision,
            expectedStateHash = terminal.StateHash
        };
        var beginResponses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Api().PostAsync(
                $"{ReconciliationBasePath()}/{terminal.Id}/recheck",
                beginBody, _adminToken, ct: ct)));
        HarnessAssert.True(beginResponses.All(value =>
                value.StatusCode == HttpStatusCode.Accepted),
            "P10 CAS-03 begin responses: " + string.Join(" | ",
                beginResponses.Select(value =>
                    $"{(int)value.StatusCode}:{value.Body}")));
        var beginReplayCount = beginResponses.Count(value =>
            ApiHarnessClient.RequiredBool(
                ApiHarnessClient.RequiredObject(value.Json,
                    "P10 CAS-03 begin response"), "isReplay"));
        HarnessAssert.Equal(7, beginReplayCount,
            "P10 CAS-03 begin replay count");

        var claimResponses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(index =>
                ClaimRecheckAsync(terminal.Id,
                    $"p10-race-recheck-{index}", ct)));
        HarnessAssert.Equal(1, claimResponses.Count(value =>
                value.Response.StatusCode == HttpStatusCode.OK),
            "P10 CAS-03 recheck claim winner count");
        HarnessAssert.Equal(7, claimResponses.Count(value =>
                value.Response.StatusCode == HttpStatusCode.Conflict),
            "P10 CAS-03 recheck claim loser count");
        var winner = claimResponses.Single(value =>
            value.Response.StatusCode == HttpStatusCode.OK);
        _cas03WorkerId = winner.WorkerId;
        _cas03ClaimToken = ResponseString(winner.Response, "claimToken");
        var post = await LoadRaceRunAsync(terminal.Id, ct);
        HarnessAssert.True(post["recheck"].IsBsonDocument &&
                           post.Contains("initialDeadlineAtUtc") &&
                           post["initialDeadlineAtUtc"].ToUniversalTime() ==
                               terminal.DeadlineAtUtc &&
                           post["recheck"].AsBsonDocument["phase"].AsString ==
                           "CAPTURE_RUNNING" &&
                           post["leaseOwnerId"].AsString == winner.WorkerId &&
                           post["recheckBeginReceipts"].AsBsonArray.Count == 1,
            "P10 CAS-03 durable production marker/lease missing");
        return P10RaceEightColumnEvidence.Pass("P10-CAS-03",
            new { productionServices = new[] { "BeginRecheckAsync", "ClaimRecheckAsync" }, workers = 8 },
            new { markerCount = 1, beginReplayCount = 7, claimWinnerCount = 1 },
            new { markerCount = 1, beginReplayCount, claimWinnerCount = 1 },
            new { stateRevisionDelta = 2, beginReceiptDelta = 1, orphanCount = 0 });
    }
        catch
        {
            try { await RestoreRecheckSuccessorP9Async(CancellationToken.None); }
            finally { await CleanupRecheckCaptureOwnersAsync(CancellationToken.None); }
            throw;
        }

    }
    private async Task<P10RaceEightColumnEvidence> ReviewDecisionMongoCasAsync(
        CancellationToken ct)
    {
        var fixture = await CreateReviewFixtureAsync("cas-04", ct);
        var reviewerId = ObjectId.GenerateNewId().ToString();
        var permission = ReviewPermission(reviewerId);
        var command = ReviewCommand(fixture.Generation,
            "p10-race-review-cas-04", StatisticReconciliationReviewGates.Form);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            try
            {
                var value = await fixture.Service.SubmitAsync(
                    permission, fixture.Generation, command, FixedUtc, ct);
                return value.Replayed ? "REPLAY" : "WIN";
            }
            catch (StatisticReconciliationIndependentReviewException error)
                when (error.Code is
                      StatisticReconciliationIndependentReviewFailureCodes.StaleCas or
                      StatisticReconciliationIndependentReviewFailureCodes.DuplicateGate or
                      StatisticReconciliationIndependentReviewFailureCodes.ReplayMismatch)
            {
                return "FENCED";
            }
        }));
        var lineage = await fixture.Backend.ReadLineageAsync(fixture.Run.Id, ct);
        HarnessAssert.Equal(1, lineage.Count(value => value.RecordKind ==
            StatisticReconciliationReviewRecordKinds.Decision),
            "P10 CAS-04 durable review decision cardinality");
        HarnessAssert.Equal(1, outcomes.Count(value => value == "WIN"),
            "P10 CAS-04 review winner count");
        HarnessAssert.Equal(9, outcomes.Count(value => value is "REPLAY" or "FENCED"),
            "P10 CAS-04 converged loser count");
        return P10RaceEightColumnEvidence.Pass("P10-CAS-04",
            new { productionBackend = "IndependentReviewMongo", workers = 10 },
            new { winnerCount = 1, durableDecisionCount = 1, loserCount = 9 },
            new { winnerCount = 1, durableDecisionCount = 1, loserCount = 9 },
            new { reviewDocumentDelta = 1, orphanCount = 0 },
            permission: new { separationOfDuties = true, reviewerDistinct = true });
    }

    private async Task<P10RaceEightColumnEvidence> EvidenceMongoAppendCasAsync(
        CancellationToken ct)
    {
        var store = new StatisticReconciliationEvidenceMongoStore(Context());
        var artifact = CompileEvidence("cas-05");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => store.TryAppendAsync(artifact, ct)));
        var replay = await store.FindByCommandAsync(
            artifact.WorkId,
            artifact.ScopeAssignmentId,
            artifact.ReconciliationId,
            artifact.CommandId,
            ct);
        HarnessAssert.Equal(1, outcomes.Count(value => value),
            "P10 CAS-05 evidence winner count");
        HarnessAssert.Equal(9, outcomes.Count(value => !value),
            "P10 CAS-05 evidence loser count");
        HarnessAssert.Equal(artifact.DocumentSha256, replay?.DocumentSha256,
            "P10 CAS-05 exact durable replay");
        var count = await Database().GetCollection<BsonDocument>(
                StatisticReconciliationEvidenceMongoStore.CollectionName)
            .CountDocumentsAsync(new BsonDocument("_id", artifact.Id),
                cancellationToken: ct);
        HarnessAssert.Equal(1L, count, "P10 CAS-05 durable artifact count");
        return P10RaceEightColumnEvidence.Pass("P10-CAS-05",
            new { productionBackend = "EvidenceMongo", workers = 10 },
            new { winnerCount = 1, loserCount = 9, artifactCount = 1 },
            new { winnerCount = 1, loserCount = 9, artifactCount = 1 },
            new { exportDocumentDelta = 1, orphanCount = 0 });
    }

    private async Task<P10RaceEightColumnEvidence> ExpectedCommitLastRecoveryAsync(
        CancellationToken ct)
    {
        var generation = BuildExpectedGeneration();
        var crashBackend = new P10CommitDroppingExpectedMongoBackend(Context());
        var crashStore = new StatisticReconciliationExpectedObservationStore(
            crashBackend,
            new StatisticReconciliationExpectedMetricIdentityCompiler());
        var failedClosed = false;
        try
        {
            _ = await crashStore.AppendGenerationAsync(generation, FixedUtc, ct);
        }
        catch (StatisticReconciliationExpectedObservationException error)
            when (error.ReasonCode ==
                  StatisticReconciliationExpectedObservationFailureReasons.GenerationIncomplete)
        {
            failedClosed = true;
        }
        var collection = Context().StatisticReconciliationObservations;
        var filter = Builders<StatisticReconciliationObservation>.Filter.And(
            Builders<StatisticReconciliationObservation>.Filter.Eq(
                value => value.ReconciliationId,
                generation.ContextPin.ReconciliationId),
            Builders<StatisticReconciliationObservation>.Filter.Eq(
                value => value.GenerationId,
                generation.GenerationId));
        var before = await collection.Find(filter).ToListAsync(ct);
        HarnessAssert.True(failedClosed && before.Count > 0 && before.All(value =>
                value.RecordKind !=
                StatisticReconciliationObservationRecordKinds.GenerationCommit),
            "P10 CRASH-01 content-before-commit must stay invisible/incomplete");
        var recoveryStore = new StatisticReconciliationExpectedObservationStore(
            new StatisticReconciliationExpectedObservationMongoBackend(Context()),
            new StatisticReconciliationExpectedMetricIdentityCompiler());
        var recovered = await recoveryStore.AppendGenerationAsync(generation, FixedUtc, ct);
        var completed = await collection.Find(filter).ToListAsync(ct);
        HarnessAssert.Equal(1, completed.Count(value => value.RecordKind ==
            StatisticReconciliationObservationRecordKinds.GenerationCommit),
            "P10 CRASH-01 commit cardinality after recovery");
        HarnessAssert.True(recovered.ExactReplay,
            "P10 CRASH-01 recovery must detect preexisting content");
        return P10RaceEightColumnEvidence.Pass("P10-CRASH-01",
            new { productionStore = "ExpectedObservationStore", crashWindow = "content-before-commit" },
            new { partialVisible = false, commitCount = 1, recovered = true },
            new { partialVisible = false, commitCount = 1, recovered = true },
            new { orphanCount = 0, duplicateCommitCount = 0 });
    }

    private async Task<P10RaceEightColumnEvidence> LostLeaseFencesStaleWorkerAsync(
        CancellationToken ct)
    {
        try
        {
            var id = _cas03RunId ?? throw new InvalidOperationException(
                "P10_CRASH_02_RECHECK_RUN_MISSING");
            var workerA = _cas03WorkerId ?? throw new InvalidOperationException(
                "P10_CRASH_02_WORKER_A_MISSING");
            var tokenA = _cas03ClaimToken ?? throw new InvalidOperationException(
                "P10_CRASH_02_TOKEN_A_MISSING");
            var runs = Context().StatisticReconciliationRuns;
            var coreAdmin = CoreActor("admin");
            var adminActor = RaceActor(coreAdmin.Id, coreAdmin.Username);
            var activation = new P10RaceActivation(RaceCandidateBinding());
            using var requeueCache = new MemoryCache(new MemoryCacheOptions());
            var requeueService = new StatisticReconciliationRunService(
                Context(), activation, new P10RaceStatRunActivation(),
                new P10RaceFixedTimeService(FixedUtc.AddSeconds(61)),
                RaceRunConfiguration(), requeueCache);
            var requeuedByProduct = false;
            await using (var requeueBody = RaceJsonBody(new
                         {
                             workerId = "p10-race-recovery-worker"
                         }))
            {
                try
                {
                    _ = await requeueService.ClaimRecheckAsync(
                        id, requeueBody, adminActor, ct);
                }
                catch (AppException error) when (
                    error.Code ==
                        AppErrorCode.STAT_RECONCILIATION_JOB_CONFLICT &&
                    AppReason(error, "RECHECK_EXPIRED_REQUEUED"))
                {
                    requeuedByProduct = true;
                }
            }
            HarnessAssert.True(requeuedByProduct,
                "P10 CRASH-02 production expiry did not requeue");
            var requeued = await runs.Find(value => value.Id == id).SingleAsync(ct);
            HarnessAssert.True(requeued.Status ==
                               StatisticReconciliationRunStatuses.Queued &&
                               requeued.RetryCount == 1 &&
                               requeued.NextRetryAtUtc ==
                                   FixedUtc.AddSeconds(62) &&
                               requeued.LeaseOwnerId is null &&
                               requeued.ClaimToken is null,
                "P10 CRASH-02 production backoff/requeue postimage");

            using var reclaimCache = new MemoryCache(new MemoryCacheOptions());
            var reclaimService = new StatisticReconciliationRunService(
                Context(), activation, new P10RaceStatRunActivation(),
                new P10RaceFixedTimeService(FixedUtc.AddSeconds(62)),
                RaceRunConfiguration(), reclaimCache);
            string tokenB;
            await using (var reclaimBody = RaceJsonBody(new
                         {
                             workerId = "p10-race-recovery-worker"
                         }))
            {
                var reclaim = await reclaimService.ClaimRecheckAsync(
                    id, reclaimBody, adminActor, ct);
                tokenB = reclaim.ClaimToken;
            }
            HarnessAssert.True(!string.Equals(tokenA, tokenB,
                    StringComparison.Ordinal),
                "P10 CRASH-02 reclaim reused stale token");

            var observations = Context().StatisticReconciliationObservations;
            var beforeStale = await observations.CountDocumentsAsync(
                value => value.ReconciliationId == id,
                cancellationToken: ct);
            var beforeStaleRun = await runs.Find(value => value.Id == id)
                .SingleAsync(ct);
            var staleFenced = false;
            await using (var staleBody = RaceJsonBody(new
                         {
                             workerId = workerA,
                             claimToken = tokenA
                         }))
            {
                try
                {
                    _ = await reclaimService.HeartbeatRecheckAsync(
                        id, staleBody, adminActor, ct);
                }
                catch (AppException error) when (
                    error.Code ==
                        AppErrorCode.STAT_RECONCILIATION_JOB_CONFLICT &&
                    AppReason(error, "RECHECK_ACTIVE_FENCE_REQUIRED"))
                {
                    staleFenced = true;
                }
            }
            var afterStale = await observations.CountDocumentsAsync(
                value => value.ReconciliationId == id,
                cancellationToken: ct);
            var afterStaleRun = await runs.Find(value => value.Id == id)
                .SingleAsync(ct);
            HarnessAssert.True(staleFenced &&
                               beforeStaleRun.StateRevision ==
                                   afterStaleRun.StateRevision &&
                               beforeStaleRun.StateHash == afterStaleRun.StateHash &&
                               beforeStale == afterStale,
                "P10 CRASH-02 stale heartbeat produced a second effect");

            await using (var winnerBody = RaceJsonBody(new
                         {
                             workerId = "p10-race-recovery-worker",
                             claimToken = tokenB
                         }))
            {
                _ = await reclaimService.HeartbeatRecheckAsync(
                    id, winnerBody, adminActor, ct);
            }
            var post = await runs.Find(value => value.Id == id).SingleAsync(ct);
            StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(post);
            HarnessAssert.True(post.LeaseOwnerId ==
                                   "p10-race-recovery-worker" &&
                               post.ClaimToken == tokenB &&
                               post.Recheck?.Phase ==
                                   StatisticReconciliationRecheckPhases
                                       .CaptureRunning &&
                               post.StateRevision ==
                                   afterStaleRun.StateRevision + 1,
                "P10 CRASH-02 recovery heartbeat postimage");
            StatisticReconciliationRecheckCanonical.RequireValidMarker(
                post.Recheck!);
            return P10RaceEightColumnEvidence.Pass("P10-CRASH-02",
                new { productionServices = new[] { "ClaimRecheckAsync", "HeartbeatRecheckAsync" }, leaseExpired = true },
                new { requeueCount = 1, reclaimWinner = 1, staleWrites = 0, winnerHeartbeat = 1 },
                new { requeueCount = 1, reclaimWinner = 1, staleWrites = afterStale - beforeStale, winnerHeartbeat = post.StateRevision - afterStaleRun.StateRevision },
                new { stateRevisionDelta = 1, observationDelta = 0, orphanCount = 0 });
        }
        finally
        {
            try { await RestoreRecheckSuccessorP9Async(CancellationToken.None); }
            finally { await CleanupRecheckCaptureOwnersAsync(CancellationToken.None); }
        }
    }

    private async Task<P10RaceEightColumnEvidence> TrustedFinalizerCrashReplayAsync(
        CancellationToken ct)
    {
        var activation = new P10RaceActivation(RaceCandidateBinding());
        var time = new P10RaceFixedTimeService(FixedUtc);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "p10-race-finalizer-signing-key-32-bytes-minimum",
                ["StatisticReconciliation:MaxRetryCount"] = "5",
                ["StatisticReconciliation:LeaseSeconds"] = "60",
                ["StatisticReconciliation:SlaSeconds"] = "600"
            }).Build();
        var runService = new StatisticReconciliationRunService(
            Context(), activation, new P10RaceStatRunActivation(), time,
            configuration, cache);
        var actor = RaceActor(ObjectId.GenerateNewId().ToString(),
            "p10-race-finalizer");
        var pendingFixture = await InvokeCommittedPendingFixtureAsync(
            runService, activation.Binding, actor, ct);
        var verdictBackend = new StatisticReconciliationReviewMongoBackend(Context());
        var publisher = new StatisticReconciliationFinalVerdictPublisher(verdictBackend);
        var finalizer = new StatisticReconciliationTrustedFinalizer(
            Context(), activation, time, publisher, verdictBackend);
        var verdict = InvokeTrustedFixture<
            StatisticReconciliationFinalVerdictRequest>(
            "MatchedVerdict",
            pendingFixture.Run.Id,
            pendingFixture.Generation.GenerationId,
            pendingFixture.Generation.GenerationSemanticSha256);
        var command = new StatisticReconciliationTrustedFinalizeCommand(
            pendingFixture.Run.Id,
            pendingFixture.Pending.StateRevision,
            pendingFixture.Pending.StateHash,
            pendingFixture.Pending.GenerationPublishRevision,
            verdict);
        await publisher.PublishAsync(verdict, FixedUtc, ct);
        var durableWindow = await Context().StatisticReconciliationRuns
            .Find(value => value.Id == pendingFixture.Run.Id).SingleAsync(ct);
        HarnessAssert.True(durableWindow.CurrentGenerationId is null &&
                           durableWindow.PendingGenerationId ==
                           pendingFixture.Generation.GenerationId,
            "P10 CRASH-03 verdict-before-CAS window invalid");
        var outcomes = await Task.WhenAll(
            finalizer.FinalizePendingAsync(command, actor, ct),
            finalizer.FinalizePendingAsync(command, actor, ct));
        var post = await Context().StatisticReconciliationRuns
            .Find(value => value.Id == pendingFixture.Run.Id).SingleAsync(ct);
        var verdictCount = await Database().GetCollection<BsonDocument>(
                "work_report_statistic_reconciliation_reviews")
            .CountDocumentsAsync(new BsonDocument
            {
                ["reconciliationId"] = pendingFixture.Run.Id,
                ["recordKind"] = StatisticReconciliationReviewKinds.FinalVerdict
            }, cancellationToken: ct);
        HarnessAssert.Equal(1, outcomes.Count(value => !value.IsReplay),
            "P10 CRASH-03 finalizer CAS winner");
        HarnessAssert.Equal(1, outcomes.Count(value => value.IsReplay),
            "P10 CRASH-03 finalizer replay");
        HarnessAssert.True(post.PendingGenerationId is null &&
                           post.CurrentGenerationId ==
                           pendingFixture.Generation.GenerationId &&
                           verdictCount == 1,
            "P10 CRASH-03 convergence invalid");
        return P10RaceEightColumnEvidence.Pass("P10-CRASH-03",
            new { productionFinalizer = true, crashWindow = "verdict-before-run-cas", workers = 2 },
            new { winnerCount = 1, replayCount = 1, verdictCount = 1, currentVisible = true },
            new { winnerCount = 1, replayCount = 1, verdictCount = 1, currentVisible = true },
            new { orphanCount = 0, duplicateVerdictCount = 0 });
    }

    private async Task<P10RaceEightColumnEvidence> FifthReviewGateCrashReplayAsync(
        CancellationToken ct)
    {
        var fixture = await CreateReviewFixtureAsync("crash-04", ct);
        StatisticReconciliationReviewSubmissionResult? fifth = null;
        StatisticReconciliationReviewSubmissionResult? replay = null;
        for (var index = 0; index < StatisticReconciliationReviewGates.All.Count; index++)
        {
            var generation = await RefreshReviewGenerationAsync(fixture, ct);
            var reviewer = ReviewPermission(ObjectId.GenerateNewId().ToString());
            var command = ReviewCommand(
                generation,
                "p10-race-crash-04-" + index,
                StatisticReconciliationReviewGates.All[index]);
            var submitted = await fixture.Service.SubmitAsync(
                reviewer, generation, command, FixedUtc.AddSeconds(index), ct);
            if (index == 4)
            {
                fifth = submitted;
                replay = await fixture.Service.SubmitAsync(
                    reviewer, generation, command, FixedUtc.AddSeconds(10), ct);
            }
        }
        var readGeneration = await RefreshReviewGenerationAsync(fixture, ct);
        var approval = await fixture.Service.GetFinalApprovalAsync(
            ReviewPermission(ObjectId.GenerateNewId().ToString(), canReview: false),
            readGeneration,
            ct);
        var lineage = await fixture.Backend.ReadLineageAsync(fixture.Run.Id, ct);
        HarnessAssert.True(fifth is { Replayed: false } &&
                           replay is { Replayed: true } && approval.Approved,
            "P10 CRASH-04 fifth gate replay did not converge");
        HarnessAssert.Equal(5, lineage.Count(value => value.RecordKind ==
            StatisticReconciliationReviewRecordKinds.Decision),
            "P10 CRASH-04 exact five decisions");
        return P10RaceEightColumnEvidence.Pass("P10-CRASH-04",
            new { productionBackend = "IndependentReviewMongo", crashWindow = "fifth-gate-before-response" },
            new { approved = true, decisions = 5, replayed = true },
            new { approved = true, decisions = 5, replayed = true },
            new { duplicateDecisionCount = 0, orphanCount = 0 },
            permission: new { independentReviewers = 5, separationOfDuties = true });
    }

    private async Task<P10RaceEightColumnEvidence> EvidenceResponseLossRecoveryAsync(
        CancellationToken ct)
    {
        var artifact = CompileEvidence("crash-05");
        var firstStore = new StatisticReconciliationEvidenceMongoStore(Context());
        var appended = await firstStore.TryAppendAsync(artifact, ct);
        var restartedStore = new StatisticReconciliationEvidenceMongoStore(Context());
        var replayAppend = await restartedStore.TryAppendAsync(artifact, ct);
        var recovered = await restartedStore.FindByIdAsync(artifact.Id, ct);
        var count = await Database().GetCollection<BsonDocument>(
                StatisticReconciliationEvidenceMongoStore.CollectionName)
            .CountDocumentsAsync(new BsonDocument("_id", artifact.Id),
                cancellationToken: ct);
        HarnessAssert.True(appended && !replayAppend && recovered is not null,
            "P10 CRASH-05 append/restart/replay invalid");
        HarnessAssert.Equal(artifact.ContentSha256, recovered!.ContentSha256,
            "P10 CRASH-05 recovered content hash");
        HarnessAssert.Equal(1L, count,
            "P10 CRASH-05 durable artifact cardinality");
        return P10RaceEightColumnEvidence.Pass("P10-CRASH-05",
            new { productionBackend = "EvidenceMongo", crashWindow = "append-before-response" },
            new { appended = true, replayAppend = false, recovered = true, artifactCount = 1 },
            new { appended = true, replayAppend = false, recovered = true, artifactCount = 1 },
            new { orphanCount = 0, duplicateArtifactCount = 0 });
    }

    private async Task<P10CommittedPendingFixture> InvokeCommittedPendingFixtureAsync(
        IStatisticReconciliationRunService runService,
        StatisticReconciliationCandidateBinding binding,
        MeResponse actor,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var method = typeof(P10TrustedFinalizerProbe).GetMethods(
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)
            .Single(value => value.Name == "CreateCommittedPendingAsync");
        var invoked = method.Invoke(null,
            [Context(), runService, binding, FixedUtc, actor]) as Task
            ?? throw new InvalidOperationException("P10_RACE_PENDING_FIXTURE_TASK_MISSING");
        await invoked.ConfigureAwait(false);
        var result = invoked.GetType().GetProperty("Result")?.GetValue(invoked)
            ?? throw new InvalidOperationException("P10_RACE_PENDING_FIXTURE_RESULT_MISSING");
        T Property<T>(string name) => (T)(result.GetType().GetProperty(name)?.GetValue(result)
            ?? throw new InvalidOperationException("P10_RACE_PENDING_FIXTURE_PROPERTY:" + name));
        return new(
            Property<StatisticReconciliationRun>("Run"),
            Property<StatisticReconciliationRun>("Pending"),
            Property<StatisticReconciliationActualAppendResult>("Generation"));
    }

    private JsonObject CoreCreateRequest(string commandId)
    {
        var fixture = CoreFixture();
        return new JsonObject
        {
            ["commandId"] = commandId,
            ["p9ResultKind"] = "DIRECT",
            ["p9ResultId"] = fixture.P9ResultId,
            ["p9RunId"] = fixture.P9RunId,
            ["conceptKey"] = fixture.ConceptKey,
            ["grain"] = fixture.Grain,
            ["filter"] = new JsonObject { ["periodKey"] = fixture.PeriodKey }
        };
    }

    private string ReconciliationBasePath() =>
        $"api/works/{WorkId()}/statistics/{ScopeId()}/reconciliations";

    private static string ResponseString(
        ApiHarnessResponse response,
        string property) => ApiHarnessClient.FindStringRecursive(
            response.Json, property) ?? throw new InvalidOperationException(
            $"P10_RACE_RESPONSE_FIELD_MISSING:{property}:{response.Body}");

    private static void ExpectRaceError(
        ApiHarnessResponse response,
        HttpStatusCode status,
        string errorCode,
        string context)
    {
        ApiHarnessClient.ExpectStatus(response, status, context);
        var actual = ApiHarnessClient.FindStringRecursive(
            response.Json, "errorCode") ??
            ApiHarnessClient.FindStringRecursive(response.Json, "code");
        HarnessAssert.Equal(errorCode, actual, context + " error code");
    }

    private IConfigurationRoot RaceRunConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "p10-race-production-service-signing-key-32-bytes",
                ["StatisticReconciliation:MaxRetryCount"] = "5",
                ["StatisticReconciliation:LeaseSeconds"] = "60",
                ["StatisticReconciliation:RetryBaseSeconds"] = "1",
                ["StatisticReconciliation:RetryMaxSeconds"] = "2",
                ["StatisticReconciliation:SlaSeconds"] = "600"
            }).Build();

    private async Task<StatisticReconciliationRun> CreateTerminalCoreRunAsync(
        string label,
        CancellationToken ct)
    {
        var captureFixture = await SeedRecheckCaptureOwnersAsync(ct);
        _recheckCaptureFixture = captureFixture;
        var request = CoreCreateRequest("p10-race-terminal-" + label);
        ApplyRecheckCapturePlan(request, captureFixture);

        var activation = new P10RaceActivation(RaceCandidateBinding());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new StatisticReconciliationRunService(
            Context(), activation, new P10RaceStatRunActivation(),
            new P10RaceFixedTimeService(FixedUtc),
            RaceRunConfiguration(), cache);
        var actor = RaceActor(ObjectId.GenerateNewId().ToString(),
            "p10-race-terminal-" + label);
        var created = await service.CreateAsync(
            WorkId(), ScopeId(), JsonSerializer.SerializeToElement(request),
            actor, ct);
        HarnessAssert.True(!created.IsReplay,
            "P10 race terminal production create replayed");
        var id = created.Run.ReconciliationId;
        var workerId = "p10-race-terminal-worker-" + label;
        var claim = await service.ClaimAsync(
            new StatisticReconciliationWorkerClaimRequest
            {
                WorkerId = workerId
            },
            actor,
            ct) ?? throw new InvalidOperationException(
                "P10_RACE_TERMINAL_CLAIM_MISSING");
        HarnessAssert.Equal(id, claim.Run.ReconciliationId,
            "P10 race terminal claim identity");
        var token = claim.ClaimToken;
        var claimed = await Context().StatisticReconciliationRuns
            .Find(value => value.Id == id).SingleAsync(ct);

        var boundary = InvokeTrustedFixture<
            StatisticReconciliationActualCoherentBoundary>(
            "BuildBoundary", claimed);
        var steps = InvokeTrustedFixture<ImmutableArray<
            StatisticReconciliationActualCoherentCaptureStep>>(
            "BuildCaptureSteps", boundary);
        var coherent = await new
            StatisticReconciliationActualCoherentCaptureCoordinator()
            .CaptureAsync(id, new P10RaceStableBoundaryReader(boundary),
                steps, ct);
        var publicationContext = InvokeTrustedFixture<
            StatisticReconciliationActualPublicationContext>(
            "BuildPublicationContext", claimed, boundary, coherent);
        var stagedCas = new P10NoopActualGenerationCas();
        var actualPublisher =
            new StatisticReconciliationActualGenerationPublisher(
                new StatisticReconciliationActualObservationMongoBackend(
                    Context()),
                stagedCas);
        var generation = await actualPublisher.PublishCoherentAsync(
            coherent, publicationContext, FixedUtc, workerId, token,
            actor, ct);
        HarnessAssert.Equal(1, stagedCas.Calls,
            "P10 race terminal generation staging");

        _ = await service.PublishPendingAsync(
            id,
            new StatisticReconciliationPendingPublishRequest
            {
                WorkerId = workerId,
                ClaimToken = token,
                GenerationId = generation.GenerationId,
                GenerationHash = generation.GenerationSemanticSha256
            },
            actor,
            ct);
        var pending = await Context().StatisticReconciliationRuns
            .Find(value => value.Id == id).SingleAsync(ct);
        var verdictBackend =
            new StatisticReconciliationReviewMongoBackend(Context());
        var verdictPublisher =
            new StatisticReconciliationFinalVerdictPublisher(verdictBackend);
        var finalizer = new StatisticReconciliationTrustedFinalizer(
            Context(), activation, new P10RaceFixedTimeService(FixedUtc),
            verdictPublisher, verdictBackend);
        var verdictRequest = InvokeTrustedFixture<
            StatisticReconciliationFinalVerdictRequest>(
            "MatchedVerdict", id, generation.GenerationId,
            generation.GenerationSemanticSha256);
        _ = await finalizer.FinalizePendingAsync(
            new StatisticReconciliationTrustedFinalizeCommand(
                id,
                pending.StateRevision,
                pending.StateHash,
                pending.GenerationPublishRevision,
                verdictRequest),
            actor,
            ct);

        var run = await Context().StatisticReconciliationRuns
            .Find(value => value.Id == id).SingleAsync(ct);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        HarnessAssert.True(
            run.Status == StatisticReconciliationRunStatuses.Matched &&
            run.CurrentGenerationId == generation.GenerationId &&
            run.CurrentGenerationHash ==
                generation.GenerationSemanticSha256 &&
            run.PendingGenerationId is null,
            "P10 race terminal current publication");
        return run;
    }

    private async Task<P10RecheckClaimExchange> ClaimRecheckAsync(
        string reconciliationId,
        string workerId,
        CancellationToken ct)
    {
        var response = await Api().PostAsync(
            "api/admin/internal/p10/statistic-reconciliations/jobs/" +
            $"{reconciliationId}/recheck/claim",
            new { workerId }, AdminToken(), ct: ct);
        return new(workerId, response);

    }
    private static MemoryStream RaceJsonBody(object value) => new(
        JsonSerializer.SerializeToUtf8Bytes(value), writable: false);

    private static bool AppReason(AppException error, string expected) =>
        JsonSerializer.Serialize(error.Details).Contains(
            $"\"reason\":\"{expected}\"", StringComparison.Ordinal);

    private async Task<BsonDocument> LoadRaceRunAsync(
        string id,
        CancellationToken ct) => await Database()
        .GetCollection<BsonDocument>(P10ReconciliationCoreProbe.RunCollection)
        .Find(new BsonDocument("_id", ObjectId.Parse(id))).SingleAsync(ct);

    private async Task InstallRecheckSuccessorP9Async(CancellationToken ct)
    {
        if (_recheckP9Original is not null)
            throw new InvalidOperationException(
                "P10_RACE_RECHECK_P9_ALREADY_REPLACED");
        var fixture = CoreFixture();
        var bsonCollection = Database().GetCollection<BsonDocument>(
            "work_report_statistic_rebuild_jobs");
        _recheckP9Original = await bsonCollection.Find(
                new BsonDocument("_id", ObjectId.Parse(fixture.P9RunId)))
            .SingleAsync(ct);
        var successor = BsonSerializer.Deserialize<
            WorkReportStatisticRebuildJob>(_recheckP9Original);
        successor.GenerationId = Sha("P10-RACE-RECHECK-SUCCESSOR-ID");
        // Preserve the valid direct-store digest hash while changing the
        // generation identity. The production owner requires the successor
        // tuple to differ, not an unrelated synthetic data mutation.
        successor.StateHash = StatisticReconciliationCanonicalJson.HashObject(
            new
            {
                version = "P9_LIFECYCLE_DIRECT_STATE_V1",
                runId = successor.Id,
                status = successor.Status,
                revision = successor.StateRevision,
                claimToken = (string?)null,
                workerId = (string?)null,
                generationId = successor.GenerationId,
                generationHash = successor.GenerationHash
            });
        await Context().WorkReportStatisticRebuildJobs.ReplaceOneAsync(
            value => value.Id == fixture.P9RunId, successor,
            cancellationToken: ct);
    }

    private async Task RestoreRecheckSuccessorP9Async(CancellationToken ct)
    {
        if (_recheckP9Original is null) return;
        var fixture = CoreFixture();
        var original = _recheckP9Original;
        await Database().GetCollection<BsonDocument>(
                "work_report_statistic_rebuild_jobs")
            .ReplaceOneAsync(
                new BsonDocument("_id", ObjectId.Parse(fixture.P9RunId)),
                original,
                cancellationToken: ct);
        _recheckP9Original = null;
    }

}

internal sealed record P10RecheckClaimExchange(
    string WorkerId,
    ApiHarnessResponse Response);

internal sealed class P10RaceStableBoundaryReader(
    StatisticReconciliationActualCoherentBoundary boundary)
    : IStatisticReconciliationActualCoherentBoundaryReader
{
    public Task<StatisticReconciliationActualCoherentBoundary> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(boundary);
    }
}

internal sealed class P10NoopActualGenerationCas
    : IStatisticReconciliationActualGenerationCas
{
    internal int Calls { get; private set; }
    public Task PublishAsync(string reconciliationId, string generationId,
        string generationSemanticSha256, string workerId, string claimToken,
        MeResponse actor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.CompletedTask;
    }
}

internal sealed record P10CommittedPendingFixture(
    StatisticReconciliationRun Run,
    StatisticReconciliationRun Pending,
    StatisticReconciliationActualAppendResult Generation);

internal sealed class P10CommitDroppingExpectedMongoBackend(MongoDbContext context)
    : IStatisticReconciliationExpectedObservationBackend
{
    public async Task<IReadOnlyList<StatisticReconciliationExpectedStoredObservation>>
        ReadGenerationAsync(
            string reconciliationId,
            string generationId,
            CancellationToken cancellationToken)
    {
        var values = await context.StatisticReconciliationObservations.Find(value =>
                value.ReconciliationId == reconciliationId &&
                value.GenerationId == generationId)
            .ToListAsync(cancellationToken);
        return values.Select(value => new StatisticReconciliationExpectedStoredObservation(
            value.Id, value.RecordKind, value.DocumentSemanticSha256)).ToArray();
    }

    public async Task AppendContentAsync(
        IReadOnlyList<StatisticReconciliationObservation> observations,
        CancellationToken cancellationToken)
    {
        if (observations.Count > 0)
            await context.StatisticReconciliationObservations.InsertManyAsync(
                observations,
                new InsertManyOptions { IsOrdered = false },
                cancellationToken);
    }

    public Task AppendCommitAsync(
        StatisticReconciliationObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class P10RaceActivation(
    StatisticReconciliationCandidateBinding binding)
    : IStatisticReconciliationCandidateActivation
{
    internal StatisticReconciliationCandidateBinding Binding { get; } = binding;
    public StatisticReconciliationCandidateBinding RequireFoundation(
        string capabilityId, string routeId) => Binding;
    public StatisticReconciliationCandidateEvaluation EvaluateFoundation(
        string capabilityId, string routeId) => new(true, null, routeId, capabilityId, Binding);
    public StatisticReconciliationCandidateBinding RequireCapability(
        string capabilityId, string routeId) => Binding;
    public StatisticReconciliationCandidateEvaluation EvaluateCapability(
        string capabilityId, string routeId) => new(true, null, routeId, capabilityId, Binding);
}

internal sealed class P10RaceStatRunActivation : IStatRunCandidateActivation
{
    private static readonly StatRunCandidateBinding Binding = new(
        StatRunCapabilityActivation.RequiredChainId,
        StatRunCapabilityActivation.PublishedPromptId,
        9,
        StatRunCapabilityActivation.RequiredCatalogVersion,
        StatRunCapabilityActivation.PublishedCatalogRawSha256,
        StatRunCapabilityActivation.PublishedCatalogSemanticSha256,
        StatRunCapabilityActivation.PublishedSchemaRawSha256,
        StatRunCapabilityActivation.PublishedSchemaSemanticSha256,
        StatRunCapabilityActivation.PublishedSealStageLockRawSha256,
        "tdtd_p10_race",
        [StatRunCapabilities.DirectFieldTableLabel]);
    public StatRunCandidateBinding RequireFoundation(string capabilityId, string routeId)
        => Binding;
    public StatRunCandidateEvaluation EvaluateFoundation(string capabilityId, string routeId)
        => new(true, null, routeId, capabilityId, Binding);
    public StatRunCandidateBinding RequireCapability(string capabilityId, string routeId)
        => Binding;
    public StatRunCandidateEvaluation EvaluateCapability(string capabilityId, string routeId)
        => new(true, null, routeId, capabilityId, Binding);
}

internal sealed class P10RaceFixedTimeService(DateTime utcNow) : IAppTimeService
{
    private readonly AppTimeService _inner = new();
    public DateTime UtcNow { get; } = utcNow;
    public TimeZoneInfo ApplicationTimeZone => _inner.ApplicationTimeZone;
    public DateTime ToUtc(DateTime value) => _inner.ToUtc(value);
    public DateTime? ToUtc(DateTime? value) => _inner.ToUtc(value);
    public DateTime NormalizeUtcDate(DateTime value) => _inner.NormalizeUtcDate(value);
    public DateTime EndOfUtcDate(DateTime value) => _inner.EndOfUtcDate(value);
    public DateTime NextLocalMidnightUtc(DateTime value) => _inner.NextLocalMidnightUtc(value);
    public bool IsLastSundayOfMonth(DateTime value) => _inner.IsLastSundayOfMonth(value);
    public AppUtcDateRange NormalizeMonthRange(DateTime? fromUtc, DateTime? toUtc) =>
        _inner.NormalizeMonthRange(fromUtc, toUtc);
}
