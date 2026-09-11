using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal static class StatisticReconciliationExpectedMetricPlanIntegrity
{
    internal const string MetricPlanSchemaVersion =
        "P10_EXPECTED_METRIC_PLAN_V2";
    private const string OperationsSchemaVersion =
        "P10_EXPECTED_OPERATIONS_V2";

    internal static string BuildEntrySha256(
        StatisticReconciliationExpectedMetricIdentity identity,
        string jsonPointer,
        string valueType,
        bool unordered,
        bool expandArray,
        ImmutableArray<string> operations,
        string transitionMode,
        string? beforeJsonPointer,
        string? afterJsonPointer,
        string? differenceOperation,
        string? transitionKind)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2,
            [
                identity.IdentitySha256,
                identity.PeriodKey,
                jsonPointer,
                valueType,
                unordered ? "1" : "0",
                expandArray ? "1" : "0",
                StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
                    OperationsSchemaVersion,
                    operations),
                transitionMode,
                beforeJsonPointer ?? "~",
                afterJsonPointer ?? "~",
                differenceOperation ?? "~",
                transitionKind ?? "~"
            ]);

    internal static string BuildPlanSha256(
        IEnumerable<StatisticReconciliationExpectedMetricPlanEntry> plan)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            MetricPlanSchemaVersion,
            plan
                .OrderBy(item => item.Identity.IdentitySha256, StringComparer.Ordinal)
                .Select(item => item.PlanEntrySha256));
}