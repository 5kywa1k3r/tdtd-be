using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

internal static class P10CrashCases
{
    internal static IReadOnlyList<P10RaceCaseDefinition> Definitions { get; } =
    [
        new("P10-CRASH-01", UncommittedGenerationIsInvisible),
        new("P10-CRASH-02", LostLeaseFencesStaleWorker),
        new("P10-CRASH-03", PromotionCrashReplayConverges),
        new("P10-CRASH-04", FifthGateRecoveryIsIdempotent),
        new("P10-CRASH-05", ArtifactPublicationRecoveryHasNoOrphan)
    ];

    private static Task<StatisticReconciliationEightColumnRecord>
        UncommittedGenerationIsInvisible(P10RaceContext context)
    {
        _ = context;
        var store = new P10GenerationCommitStore();
        store.Stage("generation-01", P10RaceFixture.H('1'));
        P10Assert.Equal<string?>(null,
            store.ReadVisible("generation-01"),
            "CRASH01_PARTIAL_GENERATION_VISIBLE");
        P10Assert.Equal(1, store.RecoverUncommitted(),
            "CRASH01_RECOVER_ONE_STAGED_GENERATION");
        P10Assert.Equal(0, store.OrphanCount,
            "CRASH01_NO_ORPHAN_AFTER_RECOVERY");
        store.Stage("generation-01", P10RaceFixture.H('1'));
        store.Commit("generation-01");
        P10Assert.Equal(P10RaceFixture.H('1'),
            store.ReadVisible("generation-01"),
            "CRASH01_RECOVERY_COMMIT_VISIBLE");
        return Task.FromResult(P10RaceContract.Evidence("P10-CRASH-01",
            "window=content-before-commit",
            "partialVisible=0;recovered=1;orphan=0",
            "partialVisible=0;recovered=1;orphan=0"));
    }

    private static Task<StatisticReconciliationEightColumnRecord>
        LostLeaseFencesStaleWorker(P10RaceContext context)
    {
        _ = context;
        var tokens = new Queue<string>(
            [P10RaceFixture.H('1'), P10RaceFixture.H('2')]);
        var fixture = P10RaceFixture.Recheck('b', claimTokenFactory: () =>
            tokens.Dequeue());
        var begin = fixture.Machine.Begin(fixture.Command,
            P10RaceFixture.ActorId, P10RaceFixture.H('3'),
            P10RaceFixture.H('4'), fixture.Binding,
            P10RaceContract.FixedUtc, TimeSpan.FromHours(1));
        var staleLease = fixture.Machine.Claim("stale-worker",
            P10RaceContract.FixedUtc, TimeSpan.FromMinutes(1));
        fixture.Machine.ReleaseCapture(staleLease.WorkerId,
            staleLease.ClaimToken,
            P10RaceContract.FixedUtc.AddSeconds(30), transient: true);
        var winner = fixture.Machine.Claim("recovery-worker",
            P10RaceContract.FixedUtc.AddSeconds(30),
            TimeSpan.FromMinutes(1));
        var stale = P10Assert.Throws<
            StatisticReconciliationRecheckException>(() =>
                fixture.Machine.RequireCapture(staleLease.WorkerId,
                    staleLease.ClaimToken,
                    P10RaceContract.FixedUtc.AddSeconds(31)),
            "CRASH02_STALE_WORKER_MUST_FAIL");
        P10Assert.Equal(
            StatisticReconciliationRecheckFailureCodes.FenceStale,
            stale.Code, "CRASH02_STALE_FENCE_CODE");
        var published = fixture.Machine.PublishTrusted(new(
                P10RaceFixture.RunId, begin.MarkerId, winner.WorkerId,
                winner.ClaimToken, P10RaceFixture.H('5'),
                P10RaceFixture.H('6')),
            P10RaceContract.FixedUtc.AddSeconds(31));
        P10Assert.Equal(P10RaceFixture.H('5'),
            published.PendingGenerationId,
            "CRASH02_RECOVERY_WORKER_PUBLISHES");
        return Task.FromResult(P10RaceContract.Evidence("P10-CRASH-02",
            "production=RecheckStateMachine;leaseLoss=bounded",
            "staleWrites=0;recoveryWinner=1;pending=1",
            "staleWrites=0;recoveryWinner=1;pending=1"));
    }

    private static Task<StatisticReconciliationEightColumnRecord>
        PromotionCrashReplayConverges(P10RaceContext context)
    {
        _ = context;
        var fixture = P10RaceFixture.Recheck('c');
        var begin = fixture.Machine.Begin(fixture.Command,
            P10RaceFixture.ActorId, P10RaceFixture.H('d'),
            P10RaceFixture.H('e'), fixture.Binding,
            P10RaceContract.FixedUtc, TimeSpan.FromHours(1));
        var lease = fixture.Machine.Claim("worker",
            P10RaceContract.FixedUtc, TimeSpan.FromMinutes(1));
        fixture.Machine.PublishTrusted(new(P10RaceFixture.RunId,
                begin.MarkerId, lease.WorkerId, lease.ClaimToken,
                P10RaceFixture.H('f'), P10RaceFixture.H('0')),
            P10RaceContract.FixedUtc.AddSeconds(1));
        var promotion = fixture.Machine.PreparePromotion(
            StatisticReconciliationRunStatuses.Stale,
            P10RaceFixture.H('1'), P10RaceFixture.H('2'));
        var first = fixture.Machine.RecordTrustedPromotion(promotion);
        var replay = fixture.Machine.RecordTrustedPromotion(promotion);
        P10Assert.True(!first.Replayed && replay.Replayed,
            "CRASH03_EXACT_PROMOTION_REPLAY");
        P10Assert.Equal(first.StateHash, replay.StateHash,
            "CRASH03_REPLAY_POSTIMAGE_HASH");
        var complete = fixture.Machine.CompleteReviewSupersession(
            begin.MarkerId, replay.StateRevision, replay.StateHash);
        P10Assert.True(complete.Recheck is null &&
                       complete.CurrentGenerationId ==
                       P10RaceFixture.H('f'),
            "CRASH03_CONVERGED_CURRENT");
        return Task.FromResult(P10RaceContract.Evidence("P10-CRASH-03",
            "production=RecheckStateMachine;window=promotion-response",
            "promotion=1;replay=1;markerCleared=1",
            "promotion=1;replay=1;markerCleared=1"));
    }

    private static async Task<StatisticReconciliationEightColumnRecord>
        FifthGateRecoveryIsIdempotent(P10RaceContext context)
    {
        _ = context;
        var backend =
            new StatisticReconciliationIndependentReviewInMemoryBackend();
        var service =
            new StatisticReconciliationIndependentReviewService(backend);
        var generation = P10RaceFixture.ReviewGeneration();
        var gates = StatisticReconciliationReviewGates.All;
        for (var index = 0; index < 4; index++)
        {
            var permission = P10RaceFixture.Permission(
                $"reviewer-{index}", permissionSeed: "1234"[index]);
            var command = P10RaceFixture.ReviewCommand(generation,
                $"review-command-{index}", gates[index]);
            _ = await service.SubmitAsync(permission, generation, command,
                P10RaceContract.FixedUtc.AddSeconds(index));
        }

        var reader = P10RaceFixture.Permission("reader",
            canReview: false, permissionSeed: '6');
        var before = await service.GetFinalApprovalAsync(reader, generation);
        P10Assert.True(!before.Approved && before.ApprovedGateCount == 4,
            "CRASH04_FOUR_GATES_NOT_FINAL");
        var fifthPermission = P10RaceFixture.Permission("reviewer-4",
            permissionSeed: '5');
        var fifthCommand = P10RaceFixture.ReviewCommand(generation,
            "review-command-4", gates[4]);
        var fifth = await service.SubmitAsync(fifthPermission, generation,
            fifthCommand, P10RaceContract.FixedUtc.AddSeconds(4));
        var replay = await service.SubmitAsync(fifthPermission, generation,
            fifthCommand, P10RaceContract.FixedUtc.AddSeconds(5));
        var after = await service.GetFinalApprovalAsync(reader, generation);
        var lineage = await backend.ReadLineageAsync(
            generation.ReconciliationId);
        P10Assert.True(!fifth.Replayed && replay.Replayed && after.Approved,
            "CRASH04_FIFTH_GATE_RECOVERY");
        P10Assert.Equal(5, lineage.Count(value => value.RecordKind ==
            StatisticReconciliationReviewRecordKinds.Decision),
            "CRASH04_NO_DUPLICATE_DECISION");
        return P10RaceContract.Evidence("P10-CRASH-04",
            "production=IndependentReview;window=fifth-gate-before-response",
            "beforeApproved=0;afterApproved=1;decisions=5",
            "beforeApproved=0;afterApproved=1;decisions=5");
    }

    private static async Task<StatisticReconciliationEightColumnRecord>
        ArtifactPublicationRecoveryHasNoOrphan(P10RaceContext context)
    {
        _ = context;
        var store = new P10AtomicEvidenceStore();
        var compiled = P10RaceFixture.CompileEvidence();
        P10Assert.Equal(0, store.Count,
            "CRASH05_COMPILED_ARTIFACT_NOT_PUBLISHED");
        var appended = await store.TryAppendAsync(compiled);
        var responseLostReplay = await store.TryAppendAsync(compiled);
        var recovered = await store.FindByIdAsync(compiled.Id);
        P10Assert.True(appended && !responseLostReplay,
            "CRASH05_APPEND_ONCE");
        P10Assert.Equal(1, store.Count,
            "CRASH05_ONE_DOWNLOADABLE_ARTIFACT");
        P10Assert.Equal(compiled.ContentSha256,
            recovered?.ContentSha256,
            "CRASH05_RECOVERED_ARTIFACT_EXACT");
        return P10RaceContract.Evidence("P10-CRASH-05",
            "production=EvidenceCanonical;window=append-before-response",
            "published=1;replayAppend=0;orphan=0",
            "published=1;replayAppend=0;orphan=0");
    }
}
