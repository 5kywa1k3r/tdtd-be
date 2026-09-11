using System.Collections.Immutable;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

internal static class StatisticReconciliationActualLifecycleLedgerSchema
{
    internal const string Version = "P10_LIFECYCLE_OBSERVATION_V3";
    internal const string SourceKind = "LIFECYCLE_SOURCE_V3";
    internal const string ActualKind = "LIFECYCLE_ACTUAL_V3";
    internal const string ResultKind = "LIFECYCLE_RESULT_V3";
    internal const string ManifestKind = "LIFECYCLE_MANIFEST_V3";
    internal const string MembershipUnit = "LIFECYCLE_MEMBERSHIP_UNIT_V1";
    internal const string CompactedPriorBaseline =
        "P10_LIFECYCLE_COMPACTED_PRIOR_BASELINE_V1";

    internal static readonly ImmutableArray<string> Kinds =
    [
        SourceKind,
        ActualKind,
        ResultKind,
        ManifestKind
    ];

    internal static bool IsOwnedKind(string? value) =>
        Kinds.Contains(value ?? string.Empty, StringComparer.Ordinal);
}

internal sealed record StatisticReconciliationActualLifecyclePriorGeneration(
    string BaseCoherentGenerationId,
    string BaseCoherentGenerationSha256,
    string CommittedGenerationId,
    string CommittedGenerationSha256,
    string FinalVerdictGenerationId,
    string FinalVerdictGenerationSha256,
    string LifecycleManifestSha256,
    int LifecycleObservationCount,
    string LifecycleRowSetSha256,
    StatisticReconciliationLifecycleResult Result,
    ImmutableArray<StatisticReconciliationLifecycleSourceState> Sources,
    ImmutableArray<StatisticReconciliationLifecycleActualContribution> Actuals,
    string P9RunId,
    string P9GenerationId,
    string P9GenerationSha256,
    string? P9ContributionLedgerSha256,
    string? P9ReversalBaselineSha256,
    string P9AuditSnapshotSha256,
    StatRunLifecycleContributionAuditSnapshot P9ContributionAudit,
    ImmutableArray<string> P9SourceAuditHashes,
    ImmutableArray<string> P9TargetLedgerHashes);

internal sealed record StatisticReconciliationActualLifecycleBuildInput(
    string ReconciliationId,
    string BaseCoherentGenerationId,
    string BaseCoherentGenerationSha256,
    string ComparisonBindingSha256,
    StatisticReconciliationExpectedAuthoritativeLifecycleProjection Expected,
    ActualSourceMembershipCapture Source,
    ActualDirectProjectionCapture Direct,
    StatRunLifecycleContributionAuditSnapshot P9ContributionAudit,
    StatisticReconciliationActualLifecyclePriorGeneration? Prior);

internal sealed record StatisticReconciliationActualLifecyclePriorCompaction(
    ImmutableArray<StatisticReconciliationLifecycleSourceState> Sources,
    ImmutableArray<StatisticReconciliationLifecycleActualContribution> Actuals,
    string? ProofSha256,
    int IdentityCount)
{
    internal static StatisticReconciliationActualLifecyclePriorCompaction Empty { get; } =
        new([], [], null, 0);
}

internal sealed record StatisticReconciliationActualLifecycleManifest(
    string SchemaVersion,
    string MembershipUnit,
    string ReconciliationId,
    string BaseCoherentGenerationId,
    string BaseCoherentGenerationSha256,
    string ComparisonBindingSha256,
    string ExpectedLifecycleProjectionSha256,
    string ExpectedLifecycleDecisionSetSha256,
    string ExpectedDoubleCollectProofSha256,
    int ExpectedLifecycleRowCount,
    string ExpectedGenerationId,
    string ExpectedGenerationSha256,
    string ExpectedManifestSha256,
    int ExpectedDocumentCount,
    string ExpectedMetricPlanSha256,
    int ExpectedMetricPlanEntryCount,
    string ExpectedMembershipSha256,
    string ExpectedRuntimeKind,
    string SourceCaptureSemanticSha256,
    string DirectCaptureSemanticSha256,
    string SourceSetSha256,
    string DirectSourceSetSha256,
    string DirectBoundarySemanticSha256,
    string P9AuditSnapshotSha256,
    string? P9ContributionLedgerSha256,
    string? P9ReversalBaselineSha256,
    string? P9ReversalAuditSha256,
    StatRunLifecycleContributionAuditSnapshot P9ContributionAudit,
    string? PriorBaseCoherentGenerationId,
    string? PriorBaseCoherentGenerationSha256,
    string? PriorCommittedGenerationId,
    string? PriorCommittedGenerationSha256,
    string? PriorFinalVerdictGenerationId,
    string? PriorFinalVerdictGenerationSha256,
    string? PriorLifecycleManifestSha256,
    int PriorLifecycleObservationCount,
    string? PriorLifecycleRowSetSha256,
    string? PriorP9RunId,
    string? PriorP9GenerationId,
    string? PriorP9GenerationSha256,
    string? PriorP9ContributionLedgerSha256,
    string? PriorP9ReversalBaselineSha256,
    string? PriorP9AuditSnapshotSha256,
    string? PriorCompactionSchema,
    string? PriorCompactionProofSha256,
    int PriorCompactionIdentityCount,
    string RequestSemanticSha256,
    string ResultSemanticSha256,
    string? ResultRootCause,
    int ResultMissingCount,
    int ResultExtraCount,
    string ResultMismatchSetSha256,
    int SourceObservationCount,
    int ActualObservationCount,
    int ObservationCount,
    string LifecycleRowSetSha256,
    string ManifestSha256);

[BsonIgnoreExtraElements]
internal sealed record StatisticReconciliationActualLifecycleLedgerRow(
    [property: BsonId] string Id,
    [property: BsonElement("schemaVersion")] string SchemaVersion,
    [property: BsonElement("recordKind")] string RecordKind,
    [property: BsonElement("reconciliationId")] string ReconciliationId,
    [property: BsonElement("baseCoherentGenerationId")]
    string BaseCoherentGenerationId,
    [property: BsonElement("baseCoherentGenerationSha256")]
    string BaseCoherentGenerationSha256,
    [property: BsonElement("observationId")] string ObservationId,
    [property: BsonElement("payloadCanonicalJson")]
    string PayloadCanonicalJson,
    [property: BsonElement("payloadSha256")] string PayloadSha256,
    [property: BsonElement("semanticSha256")] string SemanticSha256,
    [property: BsonElement("documentSemanticSha256")]
    string DocumentSemanticSha256);

internal sealed record StatisticReconciliationActualLifecycleEvidence(
    StatisticReconciliationLifecycleRequest Request,
    StatisticReconciliationLifecycleResult Result,
    StatisticReconciliationActualLifecycleManifest Manifest,
    ImmutableArray<StatisticReconciliationActualLifecycleLedgerRow> Rows)
{
    internal string ManifestSha256 => Manifest.ManifestSha256;
    internal int ObservationCount => Manifest.ObservationCount;
}

internal sealed record StatisticReconciliationActualLifecycleAppendResult(
    bool Appended,
    bool Replayed,
    string ManifestSha256,
    int ObservationCount,
    string LifecycleRowSetSha256);

internal interface IStatisticReconciliationActualLifecycleLedger
{
    Task<StatisticReconciliationActualLifecycleAppendResult> AppendAsync(
        StatisticReconciliationActualLifecycleEvidence evidence,
        CancellationToken cancellationToken = default);

    Task<StatisticReconciliationActualLifecycleEvidence?> ReadAndValidateAsync(
        string reconciliationId,
        string baseCoherentGenerationId,
        string baseCoherentGenerationSha256,
        string manifestSha256,
        int observationCount,
        CancellationToken cancellationToken = default);

    Task<StatisticReconciliationActualLifecycleEvidence?>
        ReadAndValidateByManifestAsync(
            string reconciliationId,
            string manifestSha256,
            int observationCount,
            CancellationToken cancellationToken = default);
}

internal interface IStatisticReconciliationActualLifecycleBridge
{
    StatisticReconciliationActualLifecycleEvidence Build(
        StatisticReconciliationActualLifecycleBuildInput input);
}
