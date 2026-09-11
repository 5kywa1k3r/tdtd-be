using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

public static class StatisticReconciliationExpectedValueTypes
{
    public const string Number = "NUMBER";
    public const string Bucket = "BUCKET";
    public const string Date = "DATE";
    public const string FullDate = "FULL_DATE";
    public const string Period = "PERIOD";
    public const string Boolean = "BOOLEAN";
    public const string Enum = "ENUM";
    public const string StringList = "STRING_LIST";
    public const string Text = "TEXT";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            Number,
            Bucket,
            Date,
            FullDate,
            Period,
            Boolean,
            Enum,
            StringList,
            Text);
}

public static class StatisticReconciliationExpectedAtomKinds
{
    public const string ReportCount = "REPORT_COUNT";
    public const string RowCount = "ROW_COUNT";
    public const string Count = "COUNT";
    public const string NumericValueCount = "NUMERIC_VALUE_COUNT";
    public const string Sum = "SUM";
    public const string Min = "MIN";
    public const string Max = "MAX";
    public const string Mean = "MEAN";
    public const string Bucket = "BUCKET";
    public const string Date = "DATE";
    public const string FullDate = "FULL_DATE";
    public const string Period = "PERIOD";
    public const string Boolean = "BOOLEAN";
    public const string Enum = "ENUM";
    public const string StringList = "STRING_LIST";
    public const string Text = "TEXT";
    public const string Missing = "MISSING";
    public const string Null = "NULL";
    public const string Empty = "EMPTY";
}

public static class StatisticReconciliationExpectedValueStates
{
    public const string Value = "VALUE";
    public const string Missing = "MISSING";
    public const string Null = "NULL";
    public const string Empty = "EMPTY";
}

public static class StatisticReconciliationExpectedMetricOperations
{
    public const string ReportCount = "REPORT_COUNT";
    public const string RowCount = "ROW_COUNT";
    public const string Count = "COUNT";
    public const string Sum = "SUM";
    public const string Min = "MIN";
    public const string Max = "MAX";
    public const string Mean = "MEAN";
    public const string Values = "VALUES";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            ReportCount,
            RowCount,
            Count,
            Sum,
            Min,
            Max,
            Mean,
            Values);
}

public static class StatisticReconciliationExpectedTypedFailureReasons
{
    public const string MetricPlanInvalid = "P10_EXPECTED_METRIC_PLAN_INVALID";
    public const string MetricPlanMissing = "P10_EXPECTED_METRIC_PLAN_MISSING";
    public const string JsonPointerInvalid = "P10_EXPECTED_JSON_POINTER_INVALID";
    public const string ValueTypeInvalid = "P10_EXPECTED_VALUE_TYPE_INVALID";
    public const string NumericInvalid = "P10_EXPECTED_NUMERIC_INVALID";
    public const string DateInvalid = "P10_EXPECTED_DATE_INVALID";
    public const string PeriodInvalid = "P10_EXPECTED_PERIOD_INVALID";
    public const string StringListInvalid = "P10_EXPECTED_STRING_LIST_INVALID";
    public const string DuplicateIdentity = "P10_EXPECTED_DUPLICATE_IDENTITY";
}

public static class StatisticReconciliationExpectedMetricPlanEntrySchemaVersions
{
    public const string V2 = "P10_EXPECTED_METRIC_PLAN_ENTRY_V2";
}

public static class StatisticReconciliationExpectedCollectionSemantics
{
    public const string Ordered = "ORDERED";
    public const string Unordered = "UNORDERED";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(StringComparer.Ordinal, Ordered, Unordered);
}
public static class StatisticReconciliationExpectedTransitionLegs
{
    public const string None = "NONE";
    public const string Before = "BEFORE";
    public const string After = "AFTER";
    public const string Delta = "DELTA";
    public const string ChangeState = "CHANGE_STATE";
}

public static class StatisticReconciliationExpectedTransitionKinds
{
    public const string Added = "ADDED";
    public const string Removed = "REMOVED";
    public const string Changed = "CHANGED";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(StringComparer.Ordinal, Added, Removed, Changed);
}
public static class StatisticReconciliationExpectedDiffTransitionModes
{
    public const string None = "NONE";
    public const string BeforeAfterDifference = "BEFORE_AFTER_DIFFERENCE";
}

public static class StatisticReconciliationExpectedDifferenceOperations
{
    public const string Subtract = "SUBTRACT";
    public const string Changed = "CHANGED";
    public const string Before = "BEFORE";
    public const string After = "AFTER";

    public static readonly ImmutableHashSet<string> All =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            Subtract,
            Changed,
            Before,
            After);
}

public sealed record StatisticReconciliationExpectedMetricPlanEntry(
    StatisticReconciliationExpectedMetricIdentity Identity,
    string JsonPointer,
    string ValueType,
    bool Unordered,
    bool ExpandArray,
    ImmutableArray<string> Operations,
    string PlanEntrySha256,
    string SchemaVersion =
        StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2,
    string TransitionMode =
        StatisticReconciliationExpectedDiffTransitionModes.None,
    string? BeforeJsonPointer = null,
    string? AfterJsonPointer = null,
    string? DifferenceOperation = null,
    string? TransitionKind = null);

public sealed record StatisticReconciliationExpectedTypedAtom(
    string SchemaVersion,
    StatisticReconciliationExpectedMetricIdentity Identity,
    string AtomKind,
    string ValueType,
    string ValueState,
    string CanonicalValue,
    int DecimalScale,
    long OccurrenceCount,
    long ReportCount,
    long RowCount,
    long NumericValueCount,
    string ValueIdentitySha256,
    string AtomSemanticSha256,
    string TransitionLeg = StatisticReconciliationExpectedTransitionLegs.None,
    string? TransitionKind = null,
    string? CollectionSemantics = null);

public sealed record StatisticReconciliationExpectedCatalogPins(
    string P9CatalogVersion,
    string P9CatalogRawSha256,
    string P9CatalogSemanticSha256,
    string P9SchemaRawSha256,
    string P9SchemaSemanticSha256,
    string P9StageLockSha256,
    string CandidateChainId,
    string CandidatePromptId,
    string CandidateCatalogVersion,
    string CandidateCatalogRawSha256,
    string CandidateCatalogSemanticSha256,
    string CandidateSchemaRawSha256,
    string CandidateSchemaSemanticSha256,
    string CandidateStageLockSha256,
    string CatalogPinSetSha256)
{
    internal static StatisticReconciliationExpectedCatalogPins Create(
        string p9CatalogVersion,
        string p9CatalogRawSha256,
        string p9CatalogSemanticSha256,
        string p9SchemaRawSha256,
        string p9SchemaSemanticSha256,
        string p9StageLockSha256,
        string candidateChainId,
        string candidatePromptId,
        string candidateCatalogVersion,
        string candidateCatalogRawSha256,
        string candidateCatalogSemanticSha256,
        string candidateSchemaRawSha256,
        string candidateSchemaSemanticSha256,
        string candidateStageLockSha256)
    {
        var fields = new[]
        {
            p9CatalogVersion,
            p9CatalogRawSha256,
            p9CatalogSemanticSha256,
            p9SchemaRawSha256,
            p9SchemaSemanticSha256,
            p9StageLockSha256,
            candidateChainId,
            candidatePromptId,
            candidateCatalogVersion,
            candidateCatalogRawSha256,
            candidateCatalogSemanticSha256,
            candidateSchemaRawSha256,
            candidateSchemaSemanticSha256,
            candidateStageLockSha256
        };
        if (fields.Any(string.IsNullOrWhiteSpace))
            throw new StatisticReconciliationExpectedLedgerInputException(
                StatisticReconciliationExpectedTypedFailureReasons.MetricPlanInvalid,
                "$.catalogPins",
                "Complete catalog pins required.");
        foreach (var hash in fields.Skip(1).Take(5)
                     .Concat(fields.Skip(9).Take(5)))
        {
            if (hash.Length != 64 ||
                hash.Any(character => character is not (>= '0' and <= '9') and
                                                 not (>= 'a' and <= 'f')))
                throw new StatisticReconciliationExpectedLedgerInputException(
                    StatisticReconciliationExpectedLedgerInputFailureReasons.Sha256Invalid,
                    "$.catalogPins",
                    "Canonical SHA-256 catalog pins required.");
        }
        var semanticSha256 = StatisticReconciliationExpectedLedgerCanonicalizer
            .HashSequence("P10_EXPECTED_CATALOG_PINS_V1", fields);
        return new StatisticReconciliationExpectedCatalogPins(
            p9CatalogVersion,
            p9CatalogRawSha256,
            p9CatalogSemanticSha256,
            p9SchemaRawSha256,
            p9SchemaSemanticSha256,
            p9StageLockSha256,
            candidateChainId,
            candidatePromptId,
            candidateCatalogVersion,
            candidateCatalogRawSha256,
            candidateCatalogSemanticSha256,
            candidateSchemaRawSha256,
            candidateSchemaSemanticSha256,
            candidateStageLockSha256,
            semanticSha256);
    }
}

public sealed record StatisticReconciliationExpectedCompiledGeneration(
    string SchemaVersion,
    ExpectedLedgerCompilationContextPin ContextPin,
    string GenerationId,
    string GenerationSemanticSha256,
    string AlgorithmRevision,
    string AlgorithmSha256,
    string SourceSetSha256,
    string MembershipSemanticSha256,
    string TypedSemanticSha256,
    string MetricPlanSha256,
    StatisticReconciliationExpectedCatalogPins CatalogPins,
    StatisticReconciliationExpectedSourcePlan SourcePlan,
    ImmutableArray<StatisticReconciliationExpectedMetricPlanEntry> MetricPlan,
    ImmutableArray<StatisticReconciliationExpectedTypedAtom> Atoms);

internal interface IStatisticReconciliationExpectedTypedCompiler
{
    StatisticReconciliationExpectedCompiledGeneration Compile(
        StatisticReconciliationExpectedSourcePlan sourcePlan,
        StatisticReconciliationExpectedCatalogPins catalogPins);
}
