using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.DirectReconciliation;

public static class StatisticReconciliationDirectLayers
{
    public const string SourceLedger = "SOURCE_LEDGER";
    public const string Projection = "DIRECT_PROJECTION";
    public const string Aggregate = "DIRECT_AGGREGATE";
    public const string Result = "P9_RESULT";
    public const string Api = "API";
    public const string Export = "EXPORT";

    public static readonly ImmutableArray<string> Ordered =
    [SourceLedger, Projection, Aggregate, Result, Api, Export];
}

public static class StatisticReconciliationDirectKinds
{
    public const string Field = "FIELD";
    public const string TableMetric = "TABLE_METRIC";
    public const string RowLabel = "ROW_LABEL";
}

public static class StatisticReconciliationDirectAtomKinds
{
    public const string ReportCount = "REPORT_COUNT";
    public const string RowCount = "ROW_COUNT";
    public const string Count = "COUNT";
    public const string NumericValueCount = "NUMERIC_VALUE_COUNT";
    public const string Sum = "SUM";
    public const string Min = "MIN";
    public const string Max = "MAX";
    public const string Mean = "MEAN";
    public const string Value = "VALUE";
}

public static class StatisticReconciliationDirectPermissionStates
{
    public const string AuthorizedDetail = "AUTHORIZED_DETAIL";
    public const string AuthorizedRedacted = "AUTHORIZED_REDACTED";
    public const string Denied = "DENIED";
}

public static class StatisticReconciliationDirectOutcomes
{
    public const string Matched = "MATCHED";
    public const string Mismatched = "MISMATCHED";
    public const string Stale = "STALE";
    public const string Forbidden = "FORBIDDEN";
}

public static class StatisticReconciliationDirectRootCauses
{
    public const string SourceLedger = "SOURCE_MEMBERSHIP";
    public const string Projection = "PROJECTION";
    public const string Aggregate = "AGGREGATE";
    public const string Result = "SNAPSHOT";
    public const string Api = "API_TOTAL";
    public const string Export = "EXPORT";
    public const string MissingIdentity = "MISSING_IDENTITY";
    public const string ExtraIdentity = "EXTRA_IDENTITY";
}

public static class StatisticReconciliationDirectFailureCodes
{
    public const string EvidenceInvalid = "P10_DIRECT_EVIDENCE_INVALID";
    public const string PageInvalid = "P10_DIRECT_PAGE_INVALID";
    public const string MeanInvalid = "P10_DIRECT_MEAN_INVALID";
}

public sealed class StatisticReconciliationDirectException : Exception
{
    public StatisticReconciliationDirectException(string code, string detail)
        : base($"{code}:{detail}") => Code = code;

    public string Code { get; }
}

public sealed record StatisticReconciliationDirectIdentity(
    string Kind,
    string FormTemplateId,
    string PeriodKey,
    string MetricId,
    string? FieldId,
    string? TableId,
    string? RowId,
    string? LabelId,
    string StableSortKey,
    string IdentitySha256);

public sealed record StatisticReconciliationDirectObservation(
    StatisticReconciliationDirectIdentity Identity,
    string AtomKind,
    string ValueType,
    string ValueState,
    string? CanonicalValue,
    int DecimalScale,
    string? CollectionSemantics,
    long ReportCount,
    long RowCount,
    long NumericValueCount,
    string SourceLineageSha256,
    string ObservationSemanticSha256);

public sealed record StatisticReconciliationDirectNamedTotal(
    string Name,
    string ValueType,
    string CanonicalValue,
    int DecimalScale,
    string TotalSemanticSha256);

public sealed record StatisticReconciliationDirectFullFilterTotals(
    ImmutableArray<StatisticReconciliationDirectNamedTotal> Values,
    string TotalsSemanticSha256);

public sealed record StatisticReconciliationDirectLayerCapture(
    int Ordinal,
    string Layer,
    string ComparisonBindingSha256,
    string OwnerGenerationSha256,
    bool EvidenceComplete,
    ImmutableArray<StatisticReconciliationDirectObservation> Observations,
    StatisticReconciliationDirectFullFilterTotals FullFilterTotals,
    string LayerSemanticSha256);

public sealed record StatisticReconciliationDirectPageRow(
    string IdentitySha256,
    string StableSortKey,
    string RowSemanticSha256);

public sealed record StatisticReconciliationDirectApiPage(
    int Page,
    int PageSize,
    string SortDirection,
    int TotalPages,
    long TotalRows,
    ImmutableArray<StatisticReconciliationDirectPageRow> Rows,
    StatisticReconciliationDirectFullFilterTotals FullFilterTotals,
    string PageSemanticSha256);

public sealed record StatisticReconciliationDirectExportRow(
    string IdentitySha256,
    string ObservationManifestSha256,
    string CanonicalRowJson,
    string RowSemanticSha256);

public sealed record StatisticReconciliationDirectExportEvidence(
    ImmutableArray<StatisticReconciliationDirectExportRow> Rows,
    StatisticReconciliationDirectFullFilterTotals FullFilterTotals,
    string ContentSha256,
    string EvidenceSemanticSha256);

public sealed record StatisticReconciliationDirectPermissionEvidence(
    string State,
    string AuthorizationSnapshotSha256,
    long RowCountBeforeRedaction,
    long RowCountAfterRedaction,
    string EvidenceSemanticSha256);

public sealed record StatisticReconciliationDirectRequest(
    string ReconciliationId,
    string ComparisonBindingSha256,
    string ExpectedGenerationId,
    string ActualGenerationId,
    StatisticReconciliationDirectPermissionEvidence Permission,
    ImmutableArray<StatisticReconciliationDirectLayerCapture> Layers,
    ImmutableArray<StatisticReconciliationDirectApiPage> ApiPages,
    StatisticReconciliationDirectExportEvidence? ExportEvidence);

public sealed record StatisticReconciliationDirectDelta(
    string Layer,
    string IdentitySha256,
    string AtomKind,
    string DeltaCode,
    string ComparisonSha256);

public sealed record StatisticReconciliationDirectLayerResult(
    int Ordinal,
    string Layer,
    int ComparisonCount,
    int NonzeroCount,
    int MissingCount,
    int ExtraCount,
    bool TotalsEqual,
    string DeltaManifestSha256,
    ImmutableArray<StatisticReconciliationDirectDelta> DetailedDeltas);

public sealed record StatisticReconciliationDirectResult(
    string Outcome,
    string ReconciliationId,
    string ComparisonBindingSha256,
    string ExpectedGenerationId,
    string ActualGenerationId,
    string AuthorizationState,
    bool DetailedEvidenceVisible,
    bool EvidenceComplete,
    bool AllRequiredLayersZero,
    bool PagingTotalsStable,
    bool PagingRowsExact,
    bool ExportCanonical,
    string? EarliestDivergentLayer,
    string? RootCause,
    ImmutableArray<StatisticReconciliationDirectLayerResult> Layers,
    string EvidenceManifestSha256,
    string ResultSemanticSha256);
