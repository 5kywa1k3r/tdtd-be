using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

internal static class P10CasCases
{
    internal static IReadOnlyList<P10RaceCaseDefinition> Definitions { get; } =
    [
        new("P10-CAS-01", DurableReceiptHasOneWinner),
        new("P10-CAS-02", PublicationRevisionHasOneWinner),
        new("P10-CAS-03", RecheckBeginAndClaimHaveOneWinner),
        new("P10-CAS-04", ReviewDecisionHasOneWinner),
        new("P10-CAS-05", EvidenceAppendHasOneWinner)
    ];

    private static async Task<StatisticReconciliationEightColumnRecord>
        DurableReceiptHasOneWinner(P10RaceContext context)
    {
        _ = context;
        var store = new P10AtomicReceiptStore();
        var request = StatisticReconciliationEvidenceCanonical.Hash(
            "P10_RACE_RECEIPT_REQUEST_V1", "command-01");
        var payload = StatisticReconciliationEvidenceCanonical.Hash(
            "P10_RACE_RECEIPT_PAYLOAD_V1", "generation-01");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
                store.Append("command-01", request, payload))));
        var conflict = store.Append("command-01",
            StatisticReconciliationEvidenceCanonical.Hash(
                "P10_RACE_RECEIPT_REQUEST_V1", "changed"),
            payload);
        P10Assert.Equal(1,
            outcomes.Count(value => value == P10AppendOutcome.Appended),
            "CAS01_ONE_DURABLE_WINNER");
        P10Assert.Equal(15,
            outcomes.Count(value => value == P10AppendOutcome.Replayed),
            "CAS01_EXACT_REPLAY_COUNT");
        P10Assert.Equal(P10AppendOutcome.Conflict, conflict,
            "CAS01_CHANGED_REPLAY_CONFLICT");
        P10Assert.Equal(1, store.Count, "CAS01_ONE_RECEIPT");
        return P10RaceContract.Evidence("P10-CAS-01",
            "workers=16;command=command-01",
            "winner=1;replay=15;conflict=1",
            "winner=1;replay=15;conflict=1;receipts=1");
    }

    private static async Task<StatisticReconciliationEightColumnRecord>
        PublicationRevisionHasOneWinner(P10RaceContext context)
    {
        _ = context;
        var store = new P10RevisionPublicationStore(7);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(index => Task.Run(() => store.TryPublish(7,
                StatisticReconciliationEvidenceCanonical.Hash(
                    "P10_RACE_PUBLICATION_V1", index)))));
        var post = store.Read();
        P10Assert.Equal(1, outcomes.Count(value => value),
            "CAS02_ONE_PUBLICATION_WINNER");
        P10Assert.Equal(8L, post.Revision,
            "CAS02_REVISION_INCREMENTED_ONCE");
        P10Assert.True(post.Published is { Length: 64 },
            "CAS02_PUBLISHED_SEMANTIC_SHA");
        return P10RaceContract.Evidence("P10-CAS-02",
            "workers=12;expectedRevision=7",
            "winner=1;loser=11;revision=8",
            "winner=1;loser=11;revision=8");
    }

    private static async Task<StatisticReconciliationEightColumnRecord>
        RecheckBeginAndClaimHaveOneWinner(P10RaceContext context)
    {
        _ = context;
        var fixture = P10RaceFixture.Recheck('a');
        var begins = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => fixture.Machine.Begin(
                fixture.Command, P10RaceFixture.ActorId,
                P10RaceFixture.H('b'), P10RaceFixture.H('c'),
                fixture.Binding, P10RaceContract.FixedUtc,
                TimeSpan.FromHours(1)))));
        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => Task.Run(() =>
            {
                try
                {
                    return fixture.Machine.Claim($"worker-{index}",
                        P10RaceContract.FixedUtc,
                        TimeSpan.FromMinutes(1));
                }
                catch (StatisticReconciliationRecheckException)
                {
                    return null;
                }
            })));
        P10Assert.Equal(1, begins.Count(value => !value.Replayed),
            "CAS03_ONE_BEGIN_WINNER");
        P10Assert.Equal(1,
            begins.Select(value => value.MarkerId).Distinct().Count(),
            "CAS03_ONE_MARKER");
        P10Assert.Equal(1, claims.Count(value => value is not null),
            "CAS03_ONE_CLAIM_WINNER");
        return P10RaceContract.Evidence("P10-CAS-03",
            "production=RecheckStateMachine;workers=8",
            "beginWinner=1;marker=1;claimWinner=1",
            "beginWinner=1;marker=1;claimWinner=1");
    }

    private static async Task<StatisticReconciliationEightColumnRecord>
        ReviewDecisionHasOneWinner(P10RaceContext context)
    {
        _ = context;
        var backend =
            new StatisticReconciliationIndependentReviewInMemoryBackend();
        var service =
            new StatisticReconciliationIndependentReviewService(backend);
        var generation = P10RaceFixture.ReviewGeneration();
        var permission = P10RaceFixture.Permission();
        var command = P10RaceFixture.ReviewCommand(generation,
            "review-race-command", StatisticReconciliationReviewGates.Form);
        var submissions = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => service.SubmitAsync(permission,
                generation, command, P10RaceContract.FixedUtc))).ToArray());
        var lineage = await backend.ReadLineageAsync(
            generation.ReconciliationId);
        P10Assert.Equal(1, submissions.Count(value => !value.Replayed),
            "CAS04_ONE_REVIEW_WINNER");
        P10Assert.Equal(9, submissions.Count(value => value.Replayed),
            "CAS04_REVIEW_REPLAYS");
        P10Assert.Equal(1, lineage.Count,
            "CAS04_ONE_DURABLE_DECISION");
        P10Assert.Equal(1,
            submissions.Select(value => value.Decision.DocumentSha256)
                .Distinct().Count(),
            "CAS04_ONE_DOCUMENT_SHA");
        return P10RaceContract.Evidence("P10-CAS-04",
            "production=IndependentReview;gate=FORM;workers=10",
            "winner=1;replay=9;decisions=1",
            "winner=1;replay=9;decisions=1");
    }

    private static async Task<StatisticReconciliationEightColumnRecord>
        EvidenceAppendHasOneWinner(P10RaceContext context)
    {
        _ = context;
        var store = new P10AtomicEvidenceStore();
        var artifact = P10RaceFixture.CompileEvidence();
        var appended = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => store.TryAppendAsync(artifact)))
            .ToArray());
        var replay = await store.FindByCommandAsync(artifact.WorkId,
            artifact.ScopeAssignmentId, artifact.ReconciliationId,
            artifact.CommandId);
        P10Assert.Equal(1, appended.Count(value => value),
            "CAS05_ONE_EXPORT_WINNER");
        P10Assert.Equal(1, store.Count, "CAS05_ONE_ARTIFACT");
        P10Assert.Equal(artifact.DocumentSha256, replay?.DocumentSha256,
            "CAS05_EXACT_EXPORT_REPLAY");
        return P10RaceContract.Evidence("P10-CAS-05",
            "production=EvidenceCanonical;workers=10",
            "appendWinner=1;artifacts=1;orphan=0",
            "appendWinner=1;artifacts=1;orphan=0");
    }
}
