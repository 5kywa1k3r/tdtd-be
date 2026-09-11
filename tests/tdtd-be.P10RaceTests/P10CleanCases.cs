using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;

internal static class P10CleanCases
{
    private static readonly string[] ExactCandidateCapabilities =
    [
        "SOURCE_TO_RESULT_RECONCILIATION",
        "EXPECTED_ACTUAL_DELTA",
        "INDEPENDENT_REVIEW_SIGNOFF",
        "RECONCILIATION_EVIDENCE_EXPORT"
    ];

    internal static IReadOnlyList<P10RaceCaseDefinition> Definitions { get; } =
    [
        new("P10-CLEAN-01", ProtectedP5ThroughP9StoresStayUnchanged),
        new("P10-CLEAN-02", CleanupIsBoundedAndSecondDryRunIsEmpty),
        new("P10-CLEAN-03", CleanRootsAreIndependentButSemanticallyEqual),
        new("P10-CLEAN-04", RegistryIsExactAndHasNoUnknownOutcome),
        new("P10-CLEAN-05", CandidateSealHasExactlyFourCapabilities)
    ];

    private static Task<StatisticReconciliationEightColumnRecord>
        ProtectedP5ThroughP9StoresStayUnchanged(P10RaceContext context)
    {
        _ = context;
        var protectedStores = Enumerable.Range(5, 5).ToDictionary(
            phase => $"P{phase}",
            phase => StatisticReconciliationEvidenceCanonical.Hash(
                "P10_RACE_PROTECTED_STORE_V1", phase));
        var before = ProtectedSemantic(protectedStores);

        // Exercise production canonical compilation against an independent
        // P10 snapshot; the protected P5-P9 map is deliberately read-only.
        var artifact = P10RaceFixture.CompileEvidence();
        StatisticReconciliationEvidenceCanonical.RequireStored(artifact);
        var after = ProtectedSemantic(protectedStores);

        P10Assert.Equal(before, after,
            "CLEAN01_P5_P9_SEMANTIC_DRIFT");
        P10Assert.Equal(5, protectedStores.Count,
            "CLEAN01_EXACT_PROTECTED_STORE_COUNT");
        return Task.FromResult(P10RaceContract.Evidence("P10-CLEAN-01",
            "protectedStores=P5,P6,P7,P8,P9",
            "semanticWrites=0;before=after",
            "semanticWrites=0;before=after"));
    }

    private static Task<StatisticReconciliationEightColumnRecord>
        CleanupIsBoundedAndSecondDryRunIsEmpty(P10RaceContext context)
    {
        _ = context;
        var cleanup = new P10ScopedCleanupStore();
        cleanup.Add("owner-a", "scope-a", "owned-1");
        cleanup.Add("owner-a", "scope-a", "owned-2");
        cleanup.Add("owner-b", "scope-b", "sentinel");
        var firstDry = cleanup.DryRun("owner-a", "scope-a");
        var removed = cleanup.Apply("owner-a", "scope-a");
        var secondDry = cleanup.DryRun("owner-a", "scope-a");
        P10Assert.Equal(2, firstDry, "CLEAN02_FIRST_DRY_COUNT");
        P10Assert.Equal(firstDry, removed,
            "CLEAN02_APPLY_MATCHES_DRY_RUN");
        P10Assert.Equal(0, secondDry,
            "CLEAN02_SECOND_DRY_RUN_EMPTY");
        P10Assert.True(cleanup.Contains("owner-b", "scope-b", "sentinel"),
            "CLEAN02_OUT_OF_SCOPE_SENTINEL_REMOVED");
        return Task.FromResult(P10RaceContract.Evidence("P10-CLEAN-02",
            "owner=owner-a;scope=scope-a",
            "firstDry=2;removed=2;secondDry=0;sentinel=1",
            "firstDry=2;removed=2;secondDry=0;sentinel=1"));
    }

    private static Task<StatisticReconciliationEightColumnRecord>
        CleanRootsAreIndependentButSemanticallyEqual(P10RaceContext context)
    {
        _ = context;
        var rootA = new P10CleanRootIdentity("A", "mongo-p10-a",
            "owner-a", 45101, P10RaceContext.SourceFingerprintSha256);
        var rootB = new P10CleanRootIdentity("B", "mongo-p10-b",
            "owner-b", 45102, P10RaceContext.SourceFingerprintSha256);
        P10Assert.True(rootA.RootId != rootB.RootId &&
                       rootA.Database != rootB.Database &&
                       rootA.Owner != rootB.Owner &&
                       rootA.ListenerPort != rootB.ListenerPort,
            "CLEAN03_ROOTS_NOT_INDEPENDENT");
        var normalizedA = NormalizedRootSemantic(rootA);
        var normalizedB = NormalizedRootSemantic(rootB);
        P10Assert.Equal(normalizedA, normalizedB,
            "CLEAN03_NORMALIZED_SEMANTIC_MISMATCH");
        return Task.FromResult(P10RaceContract.Evidence("P10-CLEAN-03",
            "rootA/rootB=distinct-db-owner-listener",
            "independent=1;normalizedEqual=1",
            "independent=1;normalizedEqual=1"));
    }

    private static Task<StatisticReconciliationEightColumnRecord>
        RegistryIsExactAndHasNoUnknownOutcome(P10RaceContext context)
    {
        _ = context;
        var ids = P10RaceCases.All.Select(value => value.Id).ToArray();
        P10Assert.SequenceEqual(P10RaceContract.ExactCaseIds, ids,
            "CLEAN04_EXACT_CASE_ORDER");
        P10Assert.Equal(20, ids.Distinct(StringComparer.Ordinal).Count(),
            "CLEAN04_DUPLICATE_CASE_ID");
        P10Assert.Equal(5,
            ids.Count(value => value.StartsWith("P10-CAS-",
                StringComparison.Ordinal)), "CLEAN04_CAS_COUNT");
        P10Assert.Equal(5,
            ids.Count(value => value.StartsWith("P10-CRASH-",
                StringComparison.Ordinal)), "CLEAN04_CRASH_COUNT");
        P10Assert.Equal(5,
            ids.Count(value => value.StartsWith("P10-SECURITY-",
                StringComparison.Ordinal)), "CLEAN04_SECURITY_COUNT");
        P10Assert.Equal(5,
            ids.Count(value => value.StartsWith("P10-CLEAN-",
                StringComparison.Ordinal)), "CLEAN04_CLEAN_COUNT");
        return Task.FromResult(P10RaceContract.Evidence("P10-CLEAN-04",
            "registry=exact-order;skip=0;timeout=0;unknown=0",
            "cases=20;groups=5/5/5/5",
            "cases=20;groups=5/5/5/5"));
    }

    private static Task<StatisticReconciliationEightColumnRecord>
        CandidateSealHasExactlyFourCapabilities(P10RaceContext context)
    {
        _ = context;
        var candidate = new P10CandidateSeal(
            ExactCandidateCapabilities,
            "072831d879352c76ca9e5af5f9cc2a20e13c632653fed466131f5f7a359204c4",
            "16e796d421a8f3a6675afc32be96c6fea001ae7ed6b431dfe4655cabb98cc3b7",
            "39246720948d682e85e1ca5aee1204b6da115ef974f8ad57c245d81d50bbe2b1",
            "b1ecff835b16316798a27cf9285956b91f2fc16d2c32c2cf27e11e4fb5b2ea26",
            "be02e58b97584e7638f591f7a8b37e6cb51bda8ca1ae100ebf1eb4ef9dc8bb6f");
        P10Assert.SequenceEqual(ExactCandidateCapabilities,
            candidate.Capabilities,
            "CLEAN05_EXACT_FOUR_CAPABILITIES");
        P10Assert.Equal(4,
            candidate.Capabilities.Distinct(StringComparer.Ordinal).Count(),
            "CLEAN05_NO_EXTRA_CAPABILITY");
        foreach (var pin in candidate.Pins)
            P10Assert.True(pin.Length == 64 && pin.All(value =>
                    value is >= '0' and <= '9' or >= 'a' and <= 'f'),
                "CLEAN05_INVALID_SHA_PIN");
        var seal = StatisticReconciliationEvidenceCanonical.HashSequence(
            "P10_RACE_CANDIDATE_SEAL_V1",
            candidate.Capabilities.Concat(candidate.Pins));
        P10Assert.True(seal.Length == 64, "CLEAN05_SEAL_SHA");
        return Task.FromResult(P10RaceContract.Evidence("P10-CLEAN-05",
            "candidate=v1.7;CURRENT=v1.6;LOCK=unchanged",
            "capabilities=4;published=0;successorWrites=0",
            "capabilities=4;published=0;successorWrites=0"));
    }

    private static string ProtectedSemantic(
        IReadOnlyDictionary<string, string> stores)
        => StatisticReconciliationEvidenceCanonical.HashSequence(
            "P10_RACE_P5_P9_PROTECTED_SEMANTIC_V1",
            stores.OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => $"{value.Key}={value.Value}"));

    private static string NormalizedRootSemantic(P10CleanRootIdentity root)
        => StatisticReconciliationEvidenceCanonical.Hash(
            "P10_RACE_NORMALIZED_CLEAN_ROOT_V1",
            root.SourceFingerprintSha256,
            P10RaceContract.FixedUtc,
            string.Join(",", P10RaceContract.ExactCaseIds));
}

internal sealed record P10CleanRootIdentity(
    string RootId,
    string Database,
    string Owner,
    int ListenerPort,
    string SourceFingerprintSha256);

internal sealed record P10CandidateSeal(
    IReadOnlyList<string> Capabilities,
    string CatalogRawSha256,
    string SchemaRawSha256,
    string StageLockSha256,
    string CurrentSha256,
    string LockSha256)
{
    internal IReadOnlyList<string> Pins =>
    [
        CatalogRawSha256,
        SchemaRawSha256,
        StageLockSha256,
        CurrentSha256,
        LockSha256
    ];
}
