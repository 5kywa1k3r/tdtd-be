using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationTrustedSourceDecisionProof(
    string ManifestSha256,
    int Count);

/// <summary>
/// Canonical full decision-set proof. Unlike neutral included membership this
/// binds excluded/draft owner rows as well.
/// </summary>
internal static class StatisticReconciliationTrustedSourceDecisionFence
{
    internal static StatisticReconciliationTrustedSourceDecisionProof Current(
        ActualSourceMembershipCapture source)
    {
        ArgumentNullException.ThrowIfNull(source);
        StatisticReconciliationActualSourceMembershipAdapter.RequireIntegrity(
            source);
        var decisions = source.Decisions
            .Select((value, index) =>
                StatisticReconciliationActualPublishedSourceDecision.Create(
                    index, value))
            .ToImmutableArray();
        return new StatisticReconciliationTrustedSourceDecisionProof(
            Manifest(decisions), decisions.Length);
    }

    internal static StatisticReconciliationTrustedSourceDecisionProof Committed(
        StatisticReconciliationActualAppendResult actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var values = actual.CommittedRunBinding.ActualSourceDecisions;
        if (values.IsDefault ||
            values.Length >
                StatisticReconciliationActualSourceMembershipAdapter
                    .MaxSourceCandidates)
            throw Fail("COMMITTED_SOURCE_DECISION_SET_INVALID");
        var decisions = values
            .Select(StatisticReconciliationActualPublishedSourceDecision
                .Normalize)
            .OrderBy(value => value.Ordinal)
            .ToImmutableArray();
        if (!decisions.Select(value => value.Ordinal)
                .SequenceEqual(Enumerable.Range(0, decisions.Length)) ||
            decisions.Select(value => value.SourceStableIdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != decisions.Length ||
            decisions.Select(value => value.NeutralStableIdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != decisions.Length ||
            decisions.Select(value => value.ReportId)
                .Distinct(StringComparer.Ordinal).Count() != decisions.Length ||
            decisions.Select(value => value.OwnerOrdinal)
                .Distinct().Count() != decisions.Length)
            throw Fail("COMMITTED_SOURCE_DECISION_SET_AMBIGUOUS");
        var proof = new StatisticReconciliationTrustedSourceDecisionProof(
            Manifest(decisions), decisions.Length);
        if (actual.CapturedSourceDecisionCount is not >= 0 ||
            actual.CapturedSourceDecisionCount != proof.Count ||
            Sha(actual.CapturedSourceDecisionManifestSha256,
                "CAPTURED_SOURCE_DECISION_MANIFEST") != proof.ManifestSha256 ||
            Sha(actual.CommittedRunBinding.ActualSourceDecisionManifestSha256,
                "COMMITTED_SOURCE_DECISION_MANIFEST") != proof.ManifestSha256)
            throw Fail("COMMITTED_SOURCE_DECISION_BINDING_INVALID");
        return proof;
    }

    internal static string DriftSha256(
        string topologySha256,
        StatisticReconciliationTrustedSourceDecisionProof committed,
        StatisticReconciliationTrustedSourceDecisionProof current,
        StatisticReconciliationTrustedSourceDecisionProof? final = null)
        => StatisticReconciliationActualCanonical.Hash(
            final is null
                ? "P10_TRUSTED_CURRENT_SOURCE_DECISION_DRIFT_V1"
                : "P10_TRUSTED_CURRENT_SOURCE_DECISION_REPLAY_DRIFT_V1",
            Sha(topologySha256, "SOURCE_DECISION_TOPOLOGY"),
            committed.ManifestSha256,
            StatisticReconciliationActualCanonical.Integer(committed.Count),
            current.ManifestSha256,
            StatisticReconciliationActualCanonical.Integer(current.Count),
            final?.ManifestSha256 ?? "~",
            final is null
                ? "~"
                : StatisticReconciliationActualCanonical.Integer(final.Count));

    private static string Manifest(
        ImmutableArray<StatisticReconciliationActualPublishedSourceDecision>
            decisions)
        => StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_PUBLICATION_SOURCE_DECISION_MANIFEST_V1",
            decisions.Select(value => value.SemanticSha256));

    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"TRUSTED_SOURCE_DECISION_FENCE:{reason}");
}
