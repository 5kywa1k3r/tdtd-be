using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.SummaryReconciliation;

public static class StatisticReconciliationSummaryFamilies
{
    public const string Basic = "BASIC";
    public const string Flow = "FLOW";
    public const string Advanced = "ADVANCED";
    public const string Diff = "DIFF";
}

public static class StatisticReconciliationSummaryLayers
{
    public const string SourceLedger = "SOURCE_LEDGER";
    public const string Aggregate = "AGGREGATE";
    public const string Result = "P9_RESULT";
    public const string Api = "API";
    public const string Export = "EXPORT";

    public static readonly ImmutableArray<string> Ordered =
        [SourceLedger, Aggregate, Result, Api, Export];
}

public static class StatisticReconciliationSummaryKinds
{
    public const string Field = "FIELD";
    public const string TableMetric = "TABLE_METRIC";
    public const string RowLabel = "ROW_LABEL";
}

public static class StatisticReconciliationSummaryFlowScopes
{
    public const string Branch = "FLOW_BRANCH";
    public const string Step = "FLOW_STEP";
    public const string EffectivePath = "FLOW_EFFECTIVE_PATH";
    public const string Final = "FLOW_FINAL";
    public const string StatisticProfile = "FLOW_STATISTIC_PROFILE";

    public static readonly ImmutableHashSet<string> Supported =
        ImmutableHashSet.Create(StringComparer.Ordinal, Branch, Step, EffectivePath, Final);
}

public static class StatisticReconciliationSummaryGrains
{
    public const string Day = "DAY";
    public const string Month = "MONTH";
    public const string Year = "YEAR";

    public static readonly ImmutableHashSet<string> Supported =
        ImmutableHashSet.Create(StringComparer.Ordinal, Day, Month, Year);
}

public static class StatisticReconciliationSummaryDiffKinds
{
    public const string Field = "FIELD";
    public const string TableMetric = "TABLE_METRIC";
    public const string RowLabel = "ROW_LABEL";
}

public static class StatisticReconciliationSummaryDifferenceKinds
{
    public const string Added = "ADDED";
    public const string Removed = "REMOVED";
    public const string Changed = "CHANGED";
    public const string Unchanged = "UNCHANGED";
}

public static class StatisticReconciliationSummaryAtomKinds
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

public static class StatisticReconciliationSummaryOutcomes
{
    public const string Matched = "MATCHED";
    public const string Mismatched = "MISMATCHED";
    public const string Stale = "STALE";
}

public static class StatisticReconciliationSummaryRootCauses
{
    public const string Source = "SOURCE_MEMBERSHIP";
    public const string Aggregate = "AGGREGATE";
    public const string Result = "SNAPSHOT";
    public const string Api = "API_TOTAL";
    public const string Export = "EXPORT";
    public const string Missing = "MISSING_IDENTITY";
    public const string Extra = "EXTRA_IDENTITY";
}

public static class StatisticReconciliationSummaryFailureCodes
{
    public const string Invalid = "P10_SUMMARY_INVALID";
    public const string ProfileBlocked = "P10_SUMMARY_PROFILE_BLOCKED";
    public const string MeanInvalid = "P10_SUMMARY_MEAN_INVALID";
    public const string HierarchyInvalid = "P10_SUMMARY_HIERARCHY_INVALID";
    public const string DiffInvalid = "P10_SUMMARY_DIFF_INVALID";
}

public sealed class StatisticReconciliationSummaryException : Exception
{
    public StatisticReconciliationSummaryException(string code, string detail)
        : base($"{code}:{detail}") => Code = code;

    public string Code { get; }
}

public sealed record StatisticReconciliationSummaryIdentity(
    string Family,
    string Kind,
    string MetricId,
    string PeriodIdentity,
    string? FieldId,
    string? TableId,
    string? RowId,
    string? LabelId,
    string? FlowScope,
    string? FlowScopeId,
    string? FlowEpoch,
    string? Grain,
    string? DiffKind,
    string StableSortKey,
    string IdentitySha256);

public sealed record StatisticReconciliationSummaryObservation(
    StatisticReconciliationSummaryIdentity Identity,
    string AtomKind,
    string ValueType,
    string ValueState,
    string? CanonicalValue,
    int DecimalScale,
    string? CollectionSemantics,
    long ReportCount,
    long RowCount,
    long NumericValueCount,
    string? BeforeState,
    string? BeforeCanonicalValue,
    string? AfterState,
    string? AfterCanonicalValue,
    string? DifferenceKind,
    string SourceLineageSha256,
    string ObservationSemanticSha256);

public sealed record StatisticReconciliationSummaryNamedTotal(
    string Name,
    string ValueType,
    string CanonicalValue,
    int DecimalScale,
    string SemanticSha256);

public sealed record StatisticReconciliationSummaryLayer(
    int Ordinal,
    string Layer,
    string ComparisonBindingSha256,
    string OwnerGenerationSha256,
    string FilterSha256,
    bool EvidenceComplete,
    ImmutableArray<StatisticReconciliationSummaryObservation> Observations,
    ImmutableArray<StatisticReconciliationSummaryNamedTotal> FullFilterTotals,
    string LayerSemanticSha256);

public sealed record StatisticReconciliationSummaryHierarchyEdge(
    string ChildPeriodIdentity,
    string ParentPeriodIdentity,
    string ChildGrain,
    string ParentGrain,
    string SemanticSha256);

public sealed record StatisticReconciliationSummaryRequest(
    string ReconciliationId,
    string ComparisonBindingSha256,
    string ExpectedGenerationId,
    string ActualGenerationId,
    string FilterSha256,
    string? CurrentFlowEpoch,
    ImmutableArray<StatisticReconciliationSummaryLayer> Layers,
    ImmutableArray<StatisticReconciliationSummaryHierarchyEdge> Hierarchy);

public sealed record StatisticReconciliationSummaryDelta(
    string Layer,
    string IdentitySha256,
    string AtomKind,
    string DeltaCode,
    string ComparisonSha256);

public sealed record StatisticReconciliationSummaryLayerResult(
    int Ordinal,
    string Layer,
    int ComparisonCount,
    int NonzeroCount,
    int MissingCount,
    int ExtraCount,
    bool TotalsEqual,
    ImmutableArray<StatisticReconciliationSummaryDelta> Deltas,
    string DeltaManifestSha256);

public sealed record StatisticReconciliationSummaryResult(
    string Outcome,
    string ReconciliationId,
    string ExpectedGenerationId,
    string ActualGenerationId,
    bool EvidenceComplete,
    bool AllRequiredLayersZero,
    bool ApiExportBindingExact,
    bool ProfileBlocked,
    string? EarliestDivergentLayer,
    string? RootCause,
    ImmutableArray<StatisticReconciliationSummaryLayerResult> Layers,
    string EvidenceManifestSha256,
    string ResultSemanticSha256);
