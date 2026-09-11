using System.Collections.Immutable;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record ActualAdvancedOwnerBoundary(
    string WorkId,
    string AssignmentId,
    string DynamicFormTemplateId,
    string SectionId,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    long ConfigRevision,
    string ConfigSha256,
    ImmutableArray<string> DependencyPins,
    string TimeAxis,
    string CandidateChainId,
    string CandidatePromptId,
    int CandidateStage,
    string CandidateCatalogRawSha256,
    string CandidateCatalogSemanticSha256,
    string CandidateStageLockSha256,
    ImmutableArray<string> DayNodeIds,
    ImmutableArray<string> MonthNodeIds,
    ImmutableArray<string> YearNodeIds);

internal interface IStatisticReconciliationActualAdvancedOwnerReader
{
    Task<IReadOnlyList<WorkAssignmentAdvancedSummaryDayNode>> ReadDayNodesAsync(
        ActualAdvancedOwnerBoundary boundary,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkAssignmentAdvancedSummaryMonthNode>> ReadMonthNodesAsync(
        ActualAdvancedOwnerBoundary boundary,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkAssignmentAdvancedSummaryYearNode>> ReadYearNodesAsync(
        ActualAdvancedOwnerBoundary boundary,
        CancellationToken cancellationToken);
}

internal sealed record ActualAdvancedNodeState(
    string Status,
    bool IsDirty,
    string? DirtyReason,
    bool IsDeleted,
    bool IsCleanResult,
    bool ValueJsonShapeValid,
    bool ValueHashMatches,
    bool SourceSignatureValid,
    bool TypedValueShapeValid,
    bool InnerValueMatchesOuter,
    bool SourceReportOrderCanonical,
    bool InputNodeOrderCanonical,
    bool GrainCoverageShapeValid,
    bool CleanLifecycleValid,
    string BuildLifecycleSemanticSha256);

internal sealed record ActualAdvancedFieldObservation(
    int OwnerOrdinal,
    string FieldId,
    string FieldKey,
    string Label,
    string DataType,
    string Method,
    long ValueCount,
    long SourceReportCount,
    string ResultCanonicalJson,
    ImmutableArray<string> SampleValues,
    string SemanticSha256);

internal sealed record ActualAdvancedValueObservation(
    int SchemaVersion,
    string Kind,
    DateTime GeneratedAtUtc,
    string InnerConfigId,
    string ConfigSha256,
    string Grain,
    string GrainKey,
    string? DayKey,
    string? MonthKey,
    string? YearKey,
    DateTime WindowStartUtc,
    DateTime WindowEndExclusiveUtc,
    string SourceScopeMode,
    string? SourceFlowInstanceId,
    string? SourceFlowStepId,
    string? SourceFlowBranchId,
    string? SourceFlowEffectiveStatus,
    long SourceAssignmentCount,
    long SourceReportCount,
    long SectionReportCount,
    long SectionFieldCount,
    long TargetFieldCount,
    long InputNodeCount,
    ImmutableArray<string> Warnings,
    ImmutableArray<ActualAdvancedFieldObservation> Fields,
    string SemanticSha256);

internal sealed record ActualAdvancedNodeObservation(
    string Store,
    string OwnerNodeId,
    string Grain,
    string GrainKey,
    string? DayKey,
    string? MonthKey,
    string? YearKey,
    DateTime WindowStartUtc,
    DateTime WindowEndExclusiveUtc,
    string? SourceSignatureSha256,
    long SourceReportCount,
    ImmutableArray<string> SourceReportIds,
    ImmutableArray<string> InputNodeKeys,
    string ValueJson,
    string? StoredValueSha256,
    string ObservedValueSha256,
    string CanonicalValueSha256,
    DateTime? BuiltAtUtc,
    ActualAdvancedValueObservation? Value,
    ActualAdvancedNodeState OwnerState,
    string SemanticSha256);

internal sealed record ActualAdvancedCapture(
    ActualAdvancedOwnerBoundary Boundary,
    ImmutableArray<ActualAdvancedNodeObservation> Nodes,
    int TotalNodeCount,
    string CaptureSemanticSha256);

internal sealed record ActualP9DiffOwnerBoundary(
    string ResultId,
    string RunId,
    string WorkId,
    string AssignmentId,
    string DynamicFormTemplateId,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    long ConfigRevision,
    string ConfigSha256,
    ImmutableArray<string> DependencyPins,
    string CandidateChainId,
    string CandidatePromptId,
    int CandidateStage,
    string CandidateCatalogRawSha256,
    string CandidateCatalogSemanticSha256,
    string CandidateStageLockSha256);

internal interface IStatisticReconciliationActualP9DiffOwnerReader
{
    Task<WorkReportStatisticDiffResult?> ReadResultAsync(
        ActualP9DiffOwnerBoundary boundary,
        CancellationToken cancellationToken);
}

internal sealed record ActualP9DiffTypedObservation(
    string State,
    string DataType,
    string? CanonicalValue,
    decimal? NumericValue,
    bool? BooleanValue,
    DateTime? DateValueUtc,
    ImmutableArray<string> ChoiceIds,
    bool TypedShapeValid,
    string SemanticSha256);

internal sealed record ActualP9DiffRowObservation(
    string OwnerRowId,
    int OwnerOrdinal,
    string Key,
    string ConceptKind,
    string ConceptKey,
    ActualP9DiffTypedObservation Left,
    ActualP9DiffTypedObservation Right,
    bool P9Equal,
    string P9DifferenceKind,
    decimal? P9NumericDelta,
    bool P9ComparisonConsistent,
    bool DeclaredTypesMatch,
    string P10DeltaState,
    string? P10Verdict,
    string SemanticSha256);

internal sealed record ActualP9DiffSourcePinObservation(
    int OwnerOrdinal,
    string Side,
    string SourceReportId,
    int SourcePayloadRevision,
    string SourcePayloadSha256,
    int SourceLifecycleRevision,
    string DirectRunId,
    string DirectGenerationId,
    string SemanticSha256);

internal sealed record ActualP9DiffOwnerState(
    string Status,
    bool IsCurrent,
    bool IsFresh,
    bool IsDirty,
    bool IsDeleted,
    bool IsCompletedResult,
    bool ResultHashMatches,
    bool TotalsMatchRows,
    bool RowsCanonical,
    bool SourcePinsCanonical,
    bool OwnerMetadataValid,
    bool PeriodsValid,
    bool TypedRowsValid,
    bool ComparisonsConsistent,
    bool DeclaredTypesMatch,
    bool IsUsableResult,
    string OwnerLifecycleSemanticSha256);

internal sealed record ActualP9DiffCapture(
    ActualP9DiffOwnerBoundary Boundary,
    string LeftConceptKind,
    string LeftConceptKey,
    string LeftConceptCode,
    string LeftDataType,
    string LeftPeriodCanonicalJson,
    string RightConceptKind,
    string RightConceptKey,
    string RightConceptCode,
    string RightDataType,
    string RightPeriodCanonicalJson,
    string Direction,
    string MissingPolicy,
    string EmptyPolicy,
    string TimeAxis,
    ImmutableArray<ActualP9DiffSourcePinObservation> SourcePins,
    ImmutableArray<ActualP9DiffRowObservation> Rows,
    string? StoredResultSha256,
    string ObservedResultSha256,
    ActualP9DiffOwnerState OwnerState,
    string P10DeltaState,
    string? P10Verdict,
    string CaptureSemanticSha256);
