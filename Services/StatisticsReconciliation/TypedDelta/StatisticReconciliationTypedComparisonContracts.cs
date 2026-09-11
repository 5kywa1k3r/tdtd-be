using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.TypedDelta;

public static class StatisticReconciliationTypedValueTypes
{
    public const string Date = "DATE";
    public const string FullDate = "FULL_DATE";
    public const string Period = "PERIOD";
    public const string Boolean = "BOOLEAN";
    public const string Enum = "ENUM";
    public const string StringList = "STRING_LIST";
    public const string Text = "TEXT";
    public const string Bucket = "BUCKET";
}

public static class StatisticReconciliationCollectionSemantics
{
    public const string Ordered = "ORDERED";
    public const string Unordered = "UNORDERED";
}

public static class StatisticReconciliationStableIdentityKinds
{
    public const string Table = "TABLE_IDENTITY";
    public const string Row = "ROW_IDENTITY";
    public const string Label = "LABEL_IDENTITY";
}

public static class StatisticReconciliationTypedComparisonFailureCodes
{
    public const string NumericInvalid = "P10_DELTA_NUMERIC_INVALID";
    public const string NumericOverflow = "P10_DELTA_NUMERIC_OVERFLOW";
    public const string TypedValueInvalid = "P10_DELTA_TYPED_VALUE_INVALID";
    public const string CollectionInvalid = "P10_DELTA_COLLECTION_INVALID";
    public const string IdentityInvalid = "P10_DELTA_IDENTITY_INVALID";
}

public sealed class StatisticReconciliationTypedComparisonException : Exception
{
    public StatisticReconciliationTypedComparisonException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record StatisticReconciliationCanonicalNumber(
    string CanonicalValue,
    int DecimalScale);

public sealed record StatisticReconciliationNumericAggregate(
    StatisticReconciliationCanonicalNumber Sum,
    long NumericValueCount);

public sealed record StatisticReconciliationNumericComparison(
    StatisticReconciliationCanonicalNumber Expected,
    StatisticReconciliationCanonicalNumber Actual,
    StatisticReconciliationCanonicalNumber Delta,
    bool Equal,
    string ComparisonSha256);

public sealed record StatisticReconciliationMeanComparison(
    StatisticReconciliationNumericAggregate Expected,
    StatisticReconciliationNumericAggregate Actual,
    StatisticReconciliationCanonicalNumber ExpectedMean,
    StatisticReconciliationCanonicalNumber ActualMean,
    bool SumEqual,
    bool NumericValueCountEqual,
    bool Equal,
    string ComparisonSha256);

public sealed record StatisticReconciliationCanonicalTypedValue(
    string ValueType,
    string CanonicalValue);

public sealed record StatisticReconciliationCanonicalStringList(
    string CanonicalJson,
    string Semantics);

public sealed record StatisticReconciliationTypedComparison(
    string ExpectedType,
    string ActualType,
    string ExpectedCanonicalValue,
    string ActualCanonicalValue,
    bool Equal,
    string ComparisonSha256);

public sealed record StatisticReconciliationStableIdentity(
    string IdentityKind,
    IReadOnlyList<string> Components);

public sealed record StatisticReconciliationStableIdentityComparison(
    string ExpectedKind,
    string ActualKind,
    ImmutableArray<string> ExpectedComponents,
    ImmutableArray<string> ActualComponents,
    string ExpectedIdentitySha256,
    string ActualIdentitySha256,
    bool Equal,
    string ComparisonSha256);
