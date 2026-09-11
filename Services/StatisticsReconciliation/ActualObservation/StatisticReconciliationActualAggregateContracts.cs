using System.Collections.Immutable;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualAggregateStores
{
    internal const string Field = "work_report_field_stat_aggregates";
    internal const string TableMetric = "work_report_table_stat_aggregates";
    internal const string RowLabel = "work_report_label_stat_aggregates";

    internal static readonly ImmutableArray<string> ExactSet =
        [Field, RowLabel, TableMetric];
}

internal sealed record ActualAggregateStoreDigestPin(
    string Store,
    long RowCount,
    string Sha256);

internal sealed record ActualAggregatePublicationBoundary(
    ActualDirectProjectionBoundary Direct,
    string PublicationScopeKey,
    long DirectPublicationRevision,
    string FreshnessState,
    DateTime PublishedAtUtc,
    ImmutableArray<ActualAggregateStoreDigestPin> AggregateStoreDigests);

internal interface IStatisticReconciliationActualAggregateOwnerReader
{
    Task<IReadOnlyList<WorkReportFieldStatAggregate>> ReadFieldGenerationAsync(
        ActualAggregatePublicationBoundary boundary,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkReportTableStatAggregate>> ReadTableMetricGenerationAsync(
        ActualAggregatePublicationBoundary boundary,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkReportLabelStatAggregate>> ReadRowLabelGenerationAsync(
        ActualAggregatePublicationBoundary boundary,
        CancellationToken cancellationToken);
}

internal sealed record ActualAggregateCounts(
    long ReportCount,
    long RowCount,
    long NumericValueCount);

internal sealed record ActualAggregateMeasures(
    decimal Sum,
    decimal? Min,
    decimal? Max,
    decimal? Mean,
    long TrueCount,
    long FalseCount,
    DateTime? EarliestDateUtc,
    DateTime? LatestDateUtc);

internal sealed record ActualAggregateIdentityPins(
    string ConceptIdentitySha256,
    string GrainIdentitySha256,
    string TimeIdentitySha256,
    string OwnerFilterSha256,
    string ConfigIdentitySha256,
    string ResultGenerationIdentitySha256);

internal sealed record ActualAggregateTimePin(
    string PeriodKey,
    string PeriodInstanceKey,
    string PeriodKind,
    DateTime? PeriodAnchorDateUtc,
    DateTime? PeriodStartDateUtc,
    DateTime? PeriodEndDateUtc,
    DateTime? CompletedDateUtc,
    bool IsHistoricalData,
    int ReportStatus);

internal sealed record ActualAggregateRowState(
    bool SourceMembershipMatched,
    bool SourcePayloadMatched,
    bool SourceLifecycleMatched,
    string? SourceDecisionSemanticSha256,
    bool CountsNonNegative,
    bool ReportCountWithinRows,
    bool NumericCountWithinRows,
    bool BooleanCountWithinRows,
    bool NumericShapeConsistent,
    bool DateBoundsOrdered,
    string OwnerRowSemanticSha256,
    string SemanticSha256);

internal sealed record ActualFieldAggregateObservation(
    string OwnerRowId,
    string WorkId,
    string ScopeType,
    string ScopeId,
    string? RootAssignmentId,
    string DynamicFormTemplateId,
    string? DynamicFormTemplateCode,
    string? DynamicFormTemplateName,
    string FieldId,
    string FieldKey,
    string FieldLabel,
    string FieldType,
    ImmutableArray<string> StatisticLabelCodes,
    bool ShowInTree,
    bool ShowInDetail,
    string? BucketKey,
    string? BucketLabel,
    ActualAggregateTimePin Time,
    ActualAggregateCounts Counts,
    ActualAggregateMeasures Measures,
    ActualAggregateIdentityPins IdentityPins,
    ActualAggregateRowState RowState,
    ActualDirectProjectionProvenance Provenance,
    string SemanticSha256);

internal sealed record ActualTableMetricAggregateObservation(
    string OwnerRowId,
    string WorkId,
    string ScopeType,
    string ScopeId,
    string? RootAssignmentId,
    string DynamicFormTemplateId,
    string? DynamicFormTemplateCode,
    string? DynamicFormTemplateName,
    string? DynamicExcelTemplateId,
    string BlockId,
    string TableMode,
    string MetricKey,
    string? MetricLabelCode,
    string RowKey,
    string ColumnKey,
    string DataType,
    string? BucketKey,
    string? BucketLabel,
    ActualAggregateTimePin Time,
    ActualAggregateCounts Counts,
    ActualAggregateMeasures Measures,
    ActualAggregateIdentityPins IdentityPins,
    ActualAggregateRowState RowState,
    ActualDirectProjectionProvenance Provenance,
    string SemanticSha256);

internal sealed record ActualRowLabelAggregateObservation(
    string OwnerRowId,
    string WorkId,
    string ScopeType,
    string ScopeId,
    string? RootAssignmentId,
    string DynamicFormTemplateId,
    string? DynamicFormTemplateCode,
    string? DynamicFormTemplateName,
    string? DynamicExcelTemplateId,
    string BlockId,
    string LabelCode,
    ActualAggregateTimePin Time,
    ActualAggregateCounts Counts,
    ActualAggregateIdentityPins IdentityPins,
    ActualAggregateRowState RowState,
    ActualDirectProjectionProvenance Provenance,
    string SemanticSha256);

internal sealed record ActualAggregateCapture(
    ActualAggregatePublicationBoundary Boundary,
    string ActualSourceSetSha256,
    string DirectProjectionCaptureSha256,
    string OwnerFilterSha256,
    string ConfigIdentitySha256,
    string ResultGenerationIdentitySha256,
    ImmutableArray<ActualFieldAggregateObservation> FieldRows,
    ImmutableArray<ActualTableMetricAggregateObservation> TableMetricRows,
    ImmutableArray<ActualRowLabelAggregateObservation> RowLabelRows,
    int TotalRowCount,
    string CaptureSemanticSha256);
