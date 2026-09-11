using System.Collections.Immutable;
using System.Text.Json.Serialization;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;

internal static class P10RaceContract
{
    internal const string RegistrySchemaVersion =
        "P10_RACE_CASE_REGISTRY_V1";
    internal const string WrongLedgerReason =
        "EXPECTED_SEMANTIC_HASH_MISMATCH";
    internal const int WrongLedgerExitCode = 42;
    internal static readonly DateTime FixedUtc =
        new(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly ImmutableArray<string> ExactCaseIds =
    [
        "P10-CAS-01", "P10-CAS-02", "P10-CAS-03", "P10-CAS-04",
        "P10-CAS-05",
        "P10-CRASH-01", "P10-CRASH-02", "P10-CRASH-03",
        "P10-CRASH-04", "P10-CRASH-05",
        "P10-SECURITY-01", "P10-SECURITY-02", "P10-SECURITY-03",
        "P10-SECURITY-04", "P10-SECURITY-05",
        "P10-CLEAN-01", "P10-CLEAN-02", "P10-CLEAN-03",
        "P10-CLEAN-04", "P10-CLEAN-05"
    ];

    internal static readonly ImmutableArray<string> ExactEvidenceColumns =
    [
        "Identity", "Config", "Expected", "Actual", "Delta",
        "Freshness", "Permission", "Verdict"
    ];

    internal static StatisticReconciliationEightColumnRecord Evidence(
        string identity,
        string config,
        string expected,
        string actual,
        string delta = "0",
        string freshness = "FIXED_UTC",
        string permission = "SERVER_DERIVED",
        string verdict = "PASS")
        => new(identity, config, expected, actual, delta, freshness,
            permission, verdict);

    internal static void RequireExactEvidence(
        StatisticReconciliationEightColumnRecord evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var columns = evidence.Columns;
        P10Assert.Equal(8, columns.Count, "EVIDENCE_EXACT_COLUMN_COUNT");
        P10Assert.SequenceEqual(ExactEvidenceColumns,
            columns.Select(value => value.Key),
            "EVIDENCE_EXACT_COLUMN_ORDER");
        P10Assert.True(columns.All(value =>
                !string.IsNullOrWhiteSpace(value.Value)),
            "EVIDENCE_VALUES_REQUIRED");
    }

    internal static string SemanticSha(
        IEnumerable<P10RaceCaseResult> results)
        => StatisticReconciliationEvidenceCanonical.HashSequence(
            "P10_RACE_CASE_REGISTRY_SEMANTIC_V1",
            results.SelectMany(result => new[]
            {
                result.Id,
                result.Status
            }.Concat(result.Evidence.Columns.Select(column =>
                $"{column.Key}={column.Value}"))));
}

internal sealed record P10RaceCaseDefinition(
    string Id,
    Func<P10RaceContext, Task<StatisticReconciliationEightColumnRecord>> Run);

internal sealed record P10RaceCaseResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("evidence")]
    P10RaceEvidence Evidence);

internal sealed record P10RaceCleanupProof(
    [property: JsonPropertyName("bounded")] bool Bounded,
    [property: JsonPropertyName("removedCount")] int RemovedCount,
    [property: JsonPropertyName("secondDryRunCount")] int SecondDryRunCount,
    [property: JsonPropertyName("outOfScopeUntouched")] bool OutOfScopeUntouched);

internal sealed record P10RaceSecurityProof(
    [property: JsonPropertyName("caseCount")] int CaseCount,
    [property: JsonPropertyName("allPassed")] bool AllPassed,
    [property: JsonPropertyName("sensitiveValuesFound")] int SensitiveValuesFound);

internal sealed record P10RaceP9RegressionProof(
    [property: JsonPropertyName("semanticWrites")] int SemanticWrites,
    [property: JsonPropertyName("unchanged")] bool Unchanged);

internal sealed record P10RaceRootRegistry(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("rootId")] string RootId,
    [property: JsonPropertyName("fixedUtc")] DateTime FixedUtc,
    [property: JsonPropertyName("caseCount")] int CaseCount,
    [property: JsonPropertyName("cases")]
    IReadOnlyList<P10RaceCaseResult> Cases,
    [property: JsonPropertyName("cleanup")] P10RaceCleanupProof Cleanup,
    [property: JsonPropertyName("security")] P10RaceSecurityProof Security,
    [property: JsonPropertyName("p9Regression")]
    P10RaceP9RegressionProof P9Regression,
    [property: JsonPropertyName("sourceFingerprintSha256")]
    string SourceFingerprintSha256,
    [property: JsonPropertyName("semanticSha256")]
    string SemanticSha256);
