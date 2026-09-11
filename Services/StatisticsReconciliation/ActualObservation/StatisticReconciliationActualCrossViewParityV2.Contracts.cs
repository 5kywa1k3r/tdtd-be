using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualCrossViewParityV2Schemas
{
    internal const string Plan = "P10_ACTUAL_CROSS_VIEW_PARITY_PLAN_V2";
    internal const string Base = "P10_ACTUAL_CROSS_VIEW_PARITY_BASE_V2";
    internal const string Actual = "P10_ACTUAL_CROSS_VIEW_PARITY_ACTUAL_V2";
    internal const string Proof = "P10_ACTUAL_CROSS_VIEW_PARITY_PROOF_V2";
}

internal static class StatisticReconciliationActualCrossViewFamilies
{
    internal const string Direct = "DIRECT";
    internal const string Basic = "BASIC";
    internal const string Flow = "FLOW";
    internal const string Advanced = "ADVANCED";
    internal const string Diff = "DIFF";
}

internal static class StatisticReconciliationActualCrossViewApiApplicability
{
    internal const string Required = "REQUIRED";
    internal const string NoProductionApi =
        "NOT_APPLICABLE_NO_PRODUCTION_API";
}

internal static class StatisticReconciliationActualCrossViewParityV2Failures
{
    internal const string None = "NONE";
    internal const string InputRequired = "CROSS_VIEW_INPUT_REQUIRED";
    internal const string SchemaUnsupported = "CROSS_VIEW_SCHEMA_UNSUPPORTED";
    internal const string FamilySurfaceInvalid =
        "CROSS_VIEW_FAMILY_SURFACE_INVALID";
    internal const string PlanInvalid = "CROSS_VIEW_PLAN_INVALID";
    internal const string PreimageMissing = "CROSS_VIEW_PREIMAGE_MISSING";
    internal const string TargetMismatch = "CROSS_VIEW_TARGET_MISMATCH";
    internal const string ResultMismatch = "CROSS_VIEW_RESULT_MISMATCH";
    internal const string FilterMismatch = "CROSS_VIEW_FILTER_MISMATCH";
    internal const string BaseInvalid = "CROSS_VIEW_BASE_INVALID";
    internal const string ApiInvalid = "CROSS_VIEW_API_INVALID";
    internal const string ApiPartitionIncomplete =
        "CROSS_VIEW_API_PARTITION_INCOMPLETE";
    internal const string ExportInvalid = "CROSS_VIEW_EXPORT_INVALID";
    internal const string RowCountMismatch = "CROSS_VIEW_ROW_COUNT_MISMATCH";
    internal const string RowOrderMismatch = "CROSS_VIEW_ROW_ORDER_MISMATCH";
    internal const string RowCellMismatch = "CROSS_VIEW_ROW_CELL_MISMATCH";
    internal const string TotalsMismatch = "CROSS_VIEW_TOTALS_MISMATCH";
    internal const string SharedRelationMismatch =
        "CROSS_VIEW_SHARED_RELATION_MISMATCH";
    internal const string NumericOverflow = "CROSS_VIEW_NUMERIC_OVERFLOW";
}

/// <summary>
/// Server-created immutable plan. API and export filters intentionally have
/// separate preimages and digests because their protocols use different
/// schemas. No client-supplied semantic selector is accepted by the prover.
/// </summary>
internal sealed record StatisticReconciliationActualCrossViewParityV2Plan(
    string SchemaVersion,
    string Family,
    string ApiApplicability,
    string? ApiSurface,
    string ExportResultKind,
    bool ViewsShareOrderedRows,
    string WorkId,
    string ScopeType,
    string ScopeId,
    string DynamicFormTemplateId,
    string PeriodInstanceKey,
    string AuthorizationSnapshotSha256,
    string? ApiOwnerResultId,
    string? ApiGenerationId,
    string? ApiGenerationSha256,
    string? DirectPublicationGenerationSha256,
    long? DirectSourceRevision,
    long? DirectPublicationRevision,
    StatisticReconciliationActualCrossViewBasicGenerationPreimage?
        BasicGeneration,
    string? CanonicalApiFilterJson,
    string? ApiFilterSha256,
    long ApiExpectedTotalRows,
    int ApiPageSize,
    int ApiPageCount,
    string ExportId,
    string ExportResultId,
    string ExportResultSha256,
    string ExportConfigSha256,
    string ExportSourceSha256,
    int ExportLifecycleRevision,
    string ExportRequestSha256,
    string ExportContentSha256,
    string ExportColumnManifestSha256,
    string ExportOwnerSemanticSha256,
    string CanonicalExportFilterJson,
    string ExportFilterSha256);

internal sealed record StatisticReconciliationActualCrossViewApiBaseRow(
    int AbsoluteOrdinal,
    string Identity,
    string CanonicalRowJson);

/// <summary>
/// Exact value-bearing API projection independently produced from the pinned
/// authoritative owner. It is not reconstructed from API response hashes.
/// </summary>
internal sealed record StatisticReconciliationActualCrossViewApiBaseProjection(
    string SchemaVersion,
    string WorkId,
    string ScopeAssignmentId,
    string DynamicFormTemplateId,
    string PeriodInstanceKey,
    string AuthorizationSnapshotSha256,
    string Surface,
    string OwnerResultId,
    string GenerationId,
    string GenerationSha256,
    string CanonicalFilterJson,
    string FilterSha256,
    ImmutableArray<StatisticReconciliationActualCrossViewApiBaseRow> Rows,
    ImmutableArray<StatisticReconciliationActualApiTotalValue>
        FullFilterTotals);

internal sealed record StatisticReconciliationActualCrossViewExportBaseRow(
    int Ordinal,
    string CanonicalSourceRowJson,
    ImmutableArray<StatisticReconciliationActualExportCellObservation> Cells,
    string RowSemanticSha256);

/// <summary>
/// Exact export projection independently produced from the pinned owner value.
/// Source-row JSON is retained so isomorphic Direct/Diff views can be compared
/// without inferring equality from ResultSha256 or SourceSha256.
/// </summary>
internal sealed record StatisticReconciliationActualCrossViewExportBaseProjection(
    string SchemaVersion,
    string WorkId,
    string ScopeType,
    string ScopeId,
    string DynamicFormTemplateId,
    string AuthorizationSnapshotSha256,
    string ResultKind,
    string ResultId,
    string ResultSha256,
    string ConfigSha256,
    string SourceSha256,
    int LifecycleRevision,
    string PeriodInstanceKey,
    string CanonicalFilterJson,
    string FilterSha256,
    ImmutableArray<StatisticReconciliationActualExportColumnContract> Columns,
    ImmutableArray<StatisticReconciliationActualCrossViewExportBaseRow> Rows,
    ImmutableArray<StatisticReconciliationActualExportTotalObservation>
        FullFilterTotals);

internal sealed record StatisticReconciliationActualCrossViewParityV2Base(
    string SchemaVersion,
    StatisticReconciliationActualCrossViewApiBaseProjection? Api,
    StatisticReconciliationActualCrossViewExportBaseProjection Export);

internal sealed record StatisticReconciliationActualCrossViewParityV2Actual(
    string SchemaVersion,
    StatisticReconciliationActualApiCapture? Api,
    StatisticReconciliationActualExportManifest ExportManifest,
    StatisticReconciliationActualExportCapture ExportCapture);

internal sealed record StatisticReconciliationActualCrossViewParityV2Proof(
    string SchemaVersion,
    bool Complete,
    string FailureCode,
    string PlanSemanticSha256,
    string BaseSemanticSha256,
    string ApiSemanticSha256,
    long ApiRowCount,
    string ExportSemanticSha256,
    long ExportRowCount,
    string OrderedRowRelationSha256,
    long OrderedRowRelationCount,
    string CellRelationSha256,
    long CellRelationCount,
    string TotalsRelationSha256,
    int TotalsRelationCount,
    string FilterRelationSha256,
    string ResultRelationSha256,
    string ProofSha256);
