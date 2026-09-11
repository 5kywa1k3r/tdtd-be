using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualExtendedRawSourceSchemas
{
    internal const string Command = "P10_ACTUAL_EXTENDED_RAW_SOURCE_COMMAND_V3";
    internal const string Collect = "P10_ACTUAL_EXTENDED_RAW_SOURCE_COLLECT_V3";
    internal const string Resolution = "P10_ACTUAL_EXTENDED_RAW_SOURCE_RESOLUTION_V3";
    internal const string Complete = "COMPLETE";
    internal const string Incomplete = "INCOMPLETE";
}

internal static class StatisticReconciliationActualExtendedRawSourceFailures
{
    internal const string None = "NONE";
    internal const string CommandInvalid = "EXTENDED_RAW_SOURCE_COMMAND_INVALID";
    internal const string CaptureRequired = "EXTENDED_RAW_SOURCE_CAPTURE_REQUIRED";
    internal const string SummaryPlanInvalid = "EXTENDED_RAW_SUMMARY_PLAN_INVALID";
    internal const string AdvancedSelectorUnavailable =
        "ADVANCED_SOURCE_SELECTOR_UNAVAILABLE";
    internal const string AdvancedSourceInvalid = "ADVANCED_SOURCE_INVALID";
    internal const string AdvancedDescriptorMappingInvalid =
        "ADVANCED_DESCRIPTOR_MAPPING_INVALID";
    internal const string AdvancedDateTimeCanonicalUnprovable =
        "ADVANCED_DATETIME_CANONICAL_UNPROVABLE";
    internal const string DiffSelectorUnavailable = "DIFF_SOURCE_SELECTOR_UNAVAILABLE";
    internal const string DiffScanLimitPreimageRequired =
        "DIFF_SCAN_LIMIT_PREIMAGE_REQUIRED";
    internal const string DiffSourcePinMismatch = "DIFF_SOURCE_PIN_MISMATCH";
    internal const string DiffSourceInvalid = "DIFF_SOURCE_INVALID";
    internal const string DiffProjectionPin =
        "DIFF_PROJECTION_PIN_INVALID";
    internal const string DiffDescriptorMappingInvalid =
        "DIFF_DESCRIPTOR_MAPPING_INVALID";
    internal const string DiffPairRelationRequired =
        "DIFF_SOURCE_PAIR_RELATION_REQUIRED";
    internal const string PayloadPreimageRequired = "SOURCE_PAYLOAD_PREIMAGE_REQUIRED";
    internal const string TypedAtomInvalid = "EXTENDED_TYPED_ATOM_INVALID";
    internal const string DoubleCollectDrift = "EXTENDED_RAW_SOURCE_DOUBLE_COLLECT_DRIFT";
    internal const string OwnerReadFailed = "EXTENDED_RAW_SOURCE_OWNER_READ_FAILED";
    internal const string LockedMetricFamilyNotApplicable =
        "LOCKED_METRIC_FAMILY_NOT_APPLICABLE";
}

internal static class StatisticReconciliationActualExtendedRawSourceFields
{
    internal const string SummaryPlanBinding = "summaryPlan.exactBinding";
    internal const string AdvancedLockedConfig =
        "advancedConfig.lockedCanonicalConfigJson";
    internal const string AdvancedGrainIdentity =
        "advancedOwner.exactGrainIdentity";
    internal const string DiffLockedConfig =
        "diffConfig.lockedCanonicalConfigJson";
    internal const string DiffNormalizedLimit =
        "diffResult.normalizedLimit";
    internal const string DiffProjectionPin =
        "diffProjection.directProjectionPin";
    internal const string DiffPairRelation =
        "diffSource.assignmentPairRelation";
    internal const string ExternalPayload =
        "workReportPayload.externalReadyDocument";
}

internal sealed record StatisticReconciliationActualExtendedRawSourceCommand(
    string SchemaVersion,
    StatisticReconciliationActualTrustedCaptureMaterial Material,
    StatisticReconciliationActualSummaryPlanBinding SummaryPlan,
    ActualAdvancedCapture? Advanced,
    ActualP9DiffCapture? Diff);

internal sealed record StatisticReconciliationActualExtendedRawSourceEnvelope(
    string Family,
    string? Side,
    string? Grain,
    string? GrainKey,
    int SourceOrdinal,
    string WorkId,
    string WorkAssignmentId,
    string ReportId,
    string PayloadDocumentId,
    int PayloadRevision,
    string PayloadOwnerSha256,
    string CanonicalPayloadSha256,
    string CanonicalPayloadJson,
    int LifecycleRevision,
    string LifecycleSha256,
    string? DirectRunId,
    string? DirectGenerationId,
    string? DirectGenerationSha256,
    string EnvelopeSemanticSha256);

internal sealed record StatisticReconciliationActualExtendedAdvancedDescriptorContributionProof(
    string DescriptorSemanticSha256,
    string IdentitySha256,
    string ValueSourceManifestSha256,
    int ValueSourceReportCount,
    string ContributionSemanticSha256);

internal sealed record StatisticReconciliationActualExtendedAdvancedOptionProjection(
    string Code,
    string Label,
    string OptionSemanticSha256);

internal sealed record StatisticReconciliationActualExtendedAdvancedDescriptorProjection(
    string DescriptorSemanticSha256,
    string IdentitySha256,
    string FieldId,
    string FieldKey,
    string RawFieldType,
    ImmutableArray<StatisticReconciliationActualExtendedAdvancedOptionProjection>
        Options,
    string OptionManifestSha256,
    string FieldDependencyPin,
    string ProjectionSemanticSha256);

internal sealed record StatisticReconciliationActualExtendedAdvancedSchemaOptionBinding(
    string PublishedSchemaSha256,
    string SectionContentSha256,
    ImmutableArray<StatisticReconciliationActualExtendedAdvancedDescriptorProjection>
        DescriptorProjections,
    string DescriptorProjectionManifestSha256,
    string SchemaOptionBindingSha256);

internal sealed record StatisticReconciliationActualExtendedAdvancedGrainProof(
    string OwnerNodeId,
    string Grain,
    string GrainKey,
    DateTime WindowStartUtc,
    DateTime WindowEndExclusiveUtc,
    string SourceAssignmentManifestSha256,
    int SourceAssignmentCount,
    string SourceEnvelopeManifestSha256,
    int SourceEnvelopeCount,
    string DescriptorManifestSha256,
    int DescriptorCount,
    string SchemaOptionBindingSha256,
    ImmutableArray<StatisticReconciliationActualExtendedAdvancedDescriptorProjection>
        DescriptorProjections,
    string DescriptorProjectionManifestSha256,
    int DescriptorProjectionCount,
    ImmutableArray<StatisticReconciliationActualExtendedAdvancedDescriptorContributionProof>
        DescriptorContributions,
    string DescriptorContributionManifestSha256,
    int DescriptorContributionCount,
    ImmutableArray<StatisticReconciliationActualRawSummaryAtom> TypedAtoms,
    string TypedAtomManifestSha256,
    int TypedAtomCount,
    string GrainSemanticSha256);

internal sealed record StatisticReconciliationActualExtendedAdvancedSourceProof(
    bool Applicable,
    bool Complete,
    string ProofCode,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    long ConfigRevision,
    string ConfigSha256,
    string SummaryPlanBindingSha256,
    string SourceScopeSemanticSha256,
    string SchemaOptionBindingSha256,
    ImmutableArray<StatisticReconciliationActualExtendedAdvancedGrainProof> Grains,
    string GrainManifestSha256,
    ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope> Envelopes,
    string EnvelopeManifestSha256,
    string TypedAtomManifestSha256,
    int TypedAtomCount,
    string ProofSha256);

internal sealed record StatisticReconciliationActualExtendedDiffSourcePin(
    string Side,
    string SourceReportId,
    int SourcePayloadRevision,
    string SourcePayloadSha256,
    int SourceLifecycleRevision,
    string DirectRunId,
    string DirectGenerationId,
    string DirectGenerationSha256,
    string PinSemanticSha256);

internal sealed record StatisticReconciliationActualExtendedDiffSourcePairProof(
    string DescriptorSemanticSha256,
    string IdentitySha256,
    string RelationKind,
    string LeftPinManifestSha256,
    int LeftPinCount,
    string LeftEnvelopeManifestSha256,
    int LeftEnvelopeCount,
    string RightPinManifestSha256,
    int RightPinCount,
    string RightEnvelopeManifestSha256,
    int RightEnvelopeCount,
    string PairSemanticSha256);

internal sealed record StatisticReconciliationActualExtendedDiffSideProof(
    string Side,
    string TransitionLeg,
    string SelectorSemanticSha256,
    string SourceScopeSemanticSha256,
    string PeriodSemanticSha256,
    string SourceAssignmentManifestSha256,
    int SourceAssignmentCount,
    ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin> ProjectionPins,
    string ProjectionPinManifestSha256,
    int ProjectionPinCount,
    ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin> CapturedPins,
    string CapturedPinManifestSha256,
    int CapturedPinCount,
    string EnvelopeManifestSha256,
    int EnvelopeCount,
    string DescriptorManifestSha256,
    int DescriptorCount,
    ImmutableArray<StatisticReconciliationActualRawSummaryAtom> TypedAtoms,
    string TypedAtomManifestSha256,
    int TypedAtomCount,
    string SideSemanticSha256);

internal sealed record StatisticReconciliationActualExtendedDiffSourceProof(
    bool Applicable,
    bool Complete,
    string ProofCode,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    long ConfigRevision,
    string ConfigSha256,
    string SummaryPlanBindingSha256,
    string Direction,
    string PeriodBindingSha256,
    ImmutableArray<StatisticReconciliationActualExtendedDiffSideProof> Sides,
    string SideManifestSha256,
    ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope> Envelopes,
    string EnvelopeManifestSha256,
    ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePairProof> SourcePairs,
    string SourcePairManifestSha256,
    int SourcePairCount,
    ImmutableArray<StatisticReconciliationActualRawSummaryAtom> TransitionAtoms,
    string TransitionAtomManifestSha256,
    int TransitionAtomCount,
    string TypedAtomManifestSha256,
    int TypedAtomCount,
    string ProofSha256);

internal sealed record StatisticReconciliationActualExtendedRawSourceCollect(
    string SchemaVersion,
    string SummaryPlanBindingSha256,
    StatisticReconciliationActualExtendedAdvancedSourceProof Advanced,
    StatisticReconciliationActualExtendedDiffSourceProof Diff,
    string CollectSha256);

internal sealed record StatisticReconciliationActualExtendedRawSourceResolution(
    string SchemaVersion,
    string State,
    string FailureCode,
    ImmutableArray<string> RequiredPersistenceFields,
    string SummaryPlanBindingSha256,
    StatisticReconciliationActualExtendedAdvancedSourceProof? Advanced,
    StatisticReconciliationActualExtendedDiffSourceProof? Diff,
    string FirstCollectSha256,
    string SecondCollectSha256,
    string PartitionDoubleCollectManifestSha256,
    int PartitionDoubleCollectCount,
    string DoubleCollectProofSha256,
    string ProofSha256);

internal sealed record StatisticReconciliationActualExtendedAdvancedTypedPartition(
    string DescriptorManifestSha256,
    int DescriptorCount,
    string SchemaOptionBindingSha256,
    ImmutableArray<StatisticReconciliationActualExtendedAdvancedDescriptorProjection>
        DescriptorProjections,
    string DescriptorProjectionManifestSha256,
    int DescriptorProjectionCount,
    ImmutableArray<StatisticReconciliationActualExtendedAdvancedDescriptorContributionProof>
        DescriptorContributions,
    string DescriptorContributionManifestSha256,
    ImmutableArray<StatisticReconciliationActualRawSummaryAtom> Atoms,
    string AtomManifestSha256);

internal sealed record StatisticReconciliationActualExtendedDiffTypedCompilation(
    string DescriptorManifestSha256,
    int DescriptorCount,
    string PeriodBindingSha256,
    string LeftTransitionLeg,
    string RightTransitionLeg,
    ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePairProof> SourcePairs,
    ImmutableArray<StatisticReconciliationActualRawSummaryAtom> LeftAtoms,
    ImmutableArray<StatisticReconciliationActualRawSummaryAtom> RightAtoms,
    ImmutableArray<StatisticReconciliationActualRawSummaryAtom> TransitionAtoms);

internal interface IStatisticReconciliationActualExtendedRawSourceOwner
{
    Task<StatisticReconciliationActualExtendedRawSourceResolution> ResolveAsync(
        StatisticReconciliationActualExtendedRawSourceCommand command,
        CancellationToken cancellationToken = default);
}

internal interface IStatisticReconciliationActualExtendedRawSourceCollectReader
{
    Task<StatisticReconciliationActualExtendedRawSourceCollect> CollectAsync(
        StatisticReconciliationActualExtendedRawSourceCommand command,
        CancellationToken cancellationToken);
}

internal sealed class StatisticReconciliationActualExtendedRawSourceIncompleteException(
    string failureCode,
    params string[] requiredPersistenceFields) : Exception(failureCode)
{
    internal string FailureCode { get; } = failureCode;
    internal ImmutableArray<string> RequiredPersistenceFields { get; } =
        requiredPersistenceFields
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
}
