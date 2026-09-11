namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal static class StatisticReconciliationExpectedMembershipIntegrity
{
    internal const string StableIdentityDomain =
        "P10_SOURCE_STABLE_IDENTITY_V1";
    internal const string ManifestDomain =
        "P10_INCLUDED_SOURCE_MEMBERSHIP_V1";

    internal static string BuildStableIdentitySha256(
        string workId,
        string workAssignmentId,
        string reportId)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            StableIdentityDomain,
            [workId, workAssignmentId, reportId]);

    internal static string BuildManifestSha256(
        IEnumerable<string> includedStableIdentitySha256)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            ManifestDomain,
            includedStableIdentitySha256.OrderBy(item => item, StringComparer.Ordinal));
}