using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// One independently read, integrity-checked actual payload.  The record is
/// intentionally owned by the actual side: it is selected from the frozen P9
/// source-membership decisions and never from an expected source row.
/// </summary>
internal sealed record StatisticReconciliationActualRawPayloadEnvelope(
    string SourceStableIdentitySha256,
    string WorkId,
    string WorkAssignmentId,
    string ReportId,
    string PayloadDocumentId,
    int PayloadRevision,
    string PayloadOwnerSha256,
    string CanonicalPayloadSha256,
    string CanonicalPayloadJson,
    string EnvelopeSemanticSha256);

/// <summary>
/// Neutral pre-materialization atom produced directly from actual payload
/// bytes and a value-free metric descriptor.  It has no P9 result owner yet;
/// the layer parity verifier may materialize it only after proving that the
/// corresponding Basic/Advanced/Diff output is a complete projection.
/// </summary>
internal sealed record StatisticReconciliationActualRawSummaryAtom(
    string DescriptorSemanticSha256,
    string IdentitySha256,
    string Family,
    string Kind,
    string MetricId,
    string? FieldId,
    string? TableId,
    string? RowId,
    string? LabelId,
    string? BasicScope,
    string? BasicScopeId,
    string? AdvancedGrain,
    string? DiffKind,
    string PeriodKey,
    string AtomKind,
    string ValueType,
    string ValueState,
    string CanonicalValue,
    int DecimalScale,
    long OccurrenceCount,
    long ReportCount,
    long RowCount,
    long NumericValueCount,
    string TransitionLeg,
    string? TransitionKind,
    string? CollectionSemantics,
    string AtomSemanticSha256);

internal sealed record StatisticReconciliationActualRawSummaryProof(
    string SchemaVersion,
    string SummaryPlanBindingSha256,
    string ActualSourceCaptureSha256,
    string ActualMembershipSemanticSha256,
    ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> Sources,
    string SourceManifestSha256,
    ImmutableArray<StatisticReconciliationActualRawSummaryAtom> Atoms,
    string AtomManifestSha256,
    string FirstCollectSha256,
    string SecondCollectSha256,
    string DoubleCollectProofSha256,
    string ProofSha256);

internal interface IStatisticReconciliationActualRawSummaryOwner
{
    Task<StatisticReconciliationActualRawSummaryProof> ResolveAsync(
        StatisticReconciliationActualSummaryPlanBinding exactPlan,
        ActualSourceMembershipCapture actualSources,
        CancellationToken cancellationToken,
        bool projectDynamicFormDirectFields = false);
}

internal interface IStatisticReconciliationActualRawSummaryCompiler
{
    ImmutableArray<StatisticReconciliationActualRawSummaryAtom> Compile(
        StatisticReconciliationActualSummaryPlanBinding exactPlan,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources);
}
