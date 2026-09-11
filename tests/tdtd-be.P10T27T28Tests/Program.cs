using System.Globalization;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

var cases = new (string Id, Action Run)[]
{
    ("P10-NUMERIC-01", ExactDecimalAndScale),
    ("P10-NUMERIC-02", NoApproximateEquality),
    ("P10-NUMERIC-03", NegativeAndZeroDelta),
    ("P10-NUMERIC-04", LocaleCannotChangeNumericMeaning),
    ("P10-NUMERIC-05", MeanUsesSumAndNumericCount),
    ("P10-NUMERIC-06", SameRenderedMeanDifferentComponents),
    ("P10-DTYPED-01", CanonicalDatesAndPeriods),
    ("P10-DTYPED-02", BooleanAndEnumAreTyped),
    ("P10-DTYPED-03", TextIsOrdinalAndCultureInvariant),
    ("P10-DTYPED-04", OrderedListPreservesOrderAndMultiplicity),
    ("P10-DTYPED-05", UnorderedListUsesSetIdentity),
    ("P10-DTYPED-06", BucketsAndStableIdentitiesStayTyped)
};

var passed = 0;
foreach (var item in cases)
{
    try
    {
        item.Run();
        Console.WriteLine($"PASS {item.Id}");
        passed++;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL {item.Id} {exception.GetType().Name}:{exception.Message}");
        return 1;
    }
}

Require(passed == 12, "EXACT_CASE_COUNT");
Console.WriteLine(
    "P10_T27_T28_TYPED_COMPARISON_OK cases=12 numeric=6 typed=6 " +
    "cumulative=12 stopBefore=P10-T29");
return 0;

static void ExactDecimalAndScale()
{
    var comparator = new StatisticReconciliationTypedComparator();
    var result = comparator.CompareNumeric(Number("123.45", 2), Number("123.45", 2));
    Require(result.Equal, "EXACT_DECIMAL_MATCH");
    Equal("0", result.Delta.CanonicalValue, "ZERO_DELTA");
    Equal(0, result.Delta.DecimalScale, "ZERO_DELTA_SCALE");
    ExpectCode(
        () => comparator.CompareNumeric(Number("123.450", 3), Number("123.45", 2)),
        StatisticReconciliationTypedComparisonFailureCodes.NumericInvalid);
}

static void NoApproximateEquality()
{
    var comparator = new StatisticReconciliationTypedComparator();
    var result = comparator.CompareNumeric(
        Number("1", 0),
        Number("1.000000000000000000000000001", 27));
    Require(!result.Equal, "NO_EPSILON_MATCH");
    Equal("0.000000000000000000000000001", result.Delta.CanonicalValue,
        "EXACT_SMALL_DELTA");
}

static void NegativeAndZeroDelta()
{
    var comparator = new StatisticReconciliationTypedComparator();
    var negative = comparator.CompareNumeric(Number("-3.5", 1), Number("0", 0));
    Require(!negative.Equal, "NEGATIVE_DIFFERS_FROM_ZERO");
    Equal("3.5", negative.Delta.CanonicalValue, "NEGATIVE_TO_ZERO_DELTA");
    Require(comparator.CompareNumeric(Number("0", 0), Number("0", 0)).Equal,
        "ZERO_MATCHES_EXACTLY");
    ExpectCode(
        () => comparator.CompareNumeric(Number("-0", 0), Number("0", 0)),
        StatisticReconciliationTypedComparisonFailureCodes.NumericInvalid);
}

static void LocaleCannotChangeNumericMeaning()
{
    var original = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("vi-VN");
        var comparator = new StatisticReconciliationTypedComparator();
        Require(comparator.CompareNumeric(Number("1234.5", 1), Number("1234.5", 1)).Equal,
            "INVARIANT_DECIMAL");
        ExpectCode(
            () => comparator.CompareNumeric(Number("1234,5", 1), Number("1234.5", 1)),
            StatisticReconciliationTypedComparisonFailureCodes.NumericInvalid);
    }
    finally
    {
        CultureInfo.CurrentCulture = original;
    }
}

static void MeanUsesSumAndNumericCount()
{
    var comparator = new StatisticReconciliationTypedComparator();
    var expected = new StatisticReconciliationNumericAggregate(Number("10", 0), 4);
    var actual = new StatisticReconciliationNumericAggregate(Number("10", 0), 4);
    var result = comparator.CompareMean(expected, actual);
    Require(result.Equal, "MEAN_COMPONENTS_MATCH");
    Equal("2.5", result.ExpectedMean.CanonicalValue, "EXPECTED_MEAN");
    Equal("2.5", result.ActualMean.CanonicalValue, "ACTUAL_MEAN");
    Require(result.SumEqual && result.NumericValueCountEqual,
        "SUM_AND_NUMERIC_COUNT_PRESERVED");
}

static void SameRenderedMeanDifferentComponents()
{
    var comparator = new StatisticReconciliationTypedComparator();
    var result = comparator.CompareMean(
        new StatisticReconciliationNumericAggregate(Number("10", 0), 2),
        new StatisticReconciliationNumericAggregate(Number("15", 0), 3));
    Equal("5", result.ExpectedMean.CanonicalValue, "EXPECTED_RENDERED_MEAN");
    Equal("5", result.ActualMean.CanonicalValue, "ACTUAL_RENDERED_MEAN");
    Require(!result.Equal && !result.SumEqual && !result.NumericValueCountEqual,
        "MEAN_COMPONENT_MISMATCH_VISIBLE");
    ExpectCode(
        () => comparator.CompareMean(
            new StatisticReconciliationNumericAggregate(Number("1", 0), 0),
            new StatisticReconciliationNumericAggregate(Number("1", 0), 1)),
        StatisticReconciliationTypedComparisonFailureCodes.NumericInvalid);
}

static void CanonicalDatesAndPeriods()
{
    var comparator = new StatisticReconciliationTypedComparator();
    Require(comparator.CompareScalar(
        Scalar(StatisticReconciliationTypedValueTypes.Date, "YEAR:2026"),
        Scalar(StatisticReconciliationTypedValueTypes.Date, "YEAR:2026")).Equal,
        "YEAR_MATCH");
    Require(comparator.CompareScalar(
        Scalar(StatisticReconciliationTypedValueTypes.FullDate, "DAY:2026-08-11"),
        Scalar(StatisticReconciliationTypedValueTypes.FullDate, "DAY:2026-08-11")).Equal,
        "FULL_DATE_MATCH");
    Require(comparator.CompareScalar(
        Scalar(StatisticReconciliationTypedValueTypes.Period, "MONTH:2026-08"),
        Scalar(StatisticReconciliationTypedValueTypes.Period, "MONTH:2026-08")).Equal,
        "PERIOD_MATCH");
    Require(!comparator.CompareScalar(
        Scalar(StatisticReconciliationTypedValueTypes.Date, "DAY:2026-08-11"),
        Scalar(StatisticReconciliationTypedValueTypes.Period, "DAY:2026-08-11")).Equal,
        "DATE_AND_PERIOD_TYPES_DIFFER");
    ExpectCode(
        () => comparator.CompareScalar(
            Scalar(StatisticReconciliationTypedValueTypes.Date, "11/08/2026"),
            Scalar(StatisticReconciliationTypedValueTypes.Date, "DAY:2026-08-11")),
        StatisticReconciliationTypedComparisonFailureCodes.TypedValueInvalid);
}

static void BooleanAndEnumAreTyped()
{
    var comparator = new StatisticReconciliationTypedComparator();
    Require(comparator.CompareScalar(
        Scalar(StatisticReconciliationTypedValueTypes.Boolean, "true"),
        Scalar(StatisticReconciliationTypedValueTypes.Boolean, "true")).Equal,
        "BOOLEAN_MATCH");
    Require(!comparator.CompareScalar(
        Scalar(StatisticReconciliationTypedValueTypes.Boolean, "true"),
        Scalar(StatisticReconciliationTypedValueTypes.Boolean, "false")).Equal,
        "BOOLEAN_DIFFERS");
    Require(!comparator.CompareScalar(
        Scalar(StatisticReconciliationTypedValueTypes.Enum, "APPROVED"),
        Scalar(StatisticReconciliationTypedValueTypes.Enum, "approved")).Equal,
        "ENUM_IS_ORDINAL");
    ExpectCode(
        () => comparator.CompareScalar(
            Scalar(StatisticReconciliationTypedValueTypes.Boolean, "True"),
            Scalar(StatisticReconciliationTypedValueTypes.Boolean, "true")),
        StatisticReconciliationTypedComparisonFailureCodes.TypedValueInvalid);
}

static void TextIsOrdinalAndCultureInvariant()
{
    var original = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        var comparator = new StatisticReconciliationTypedComparator();
        Require(comparator.CompareScalar(
            Scalar(StatisticReconciliationTypedValueTypes.Text, "Đã duyệt"),
            Scalar(StatisticReconciliationTypedValueTypes.Text, "Đã duyệt")).Equal,
            "UNICODE_TEXT_MATCH");
        Require(!comparator.CompareScalar(
            Scalar(StatisticReconciliationTypedValueTypes.Text, "I"),
            Scalar(StatisticReconciliationTypedValueTypes.Text, "ı")).Equal,
            "TEXT_NOT_LOCALE_FOLDED");
    }
    finally
    {
        CultureInfo.CurrentCulture = original;
    }
}

static void OrderedListPreservesOrderAndMultiplicity()
{
    var comparator = new StatisticReconciliationTypedComparator();
    var baseline = List("[\"a\",\"b\",\"b\"]",
        StatisticReconciliationCollectionSemantics.Ordered);
    Require(comparator.CompareStringList(baseline, baseline).Equal,
        "ORDERED_EXACT_MATCH");
    Require(!comparator.CompareStringList(
        baseline,
        List("[\"b\",\"a\",\"b\"]",
            StatisticReconciliationCollectionSemantics.Ordered)).Equal,
        "ORDERED_PERMUTATION_DIFFERS");
    Require(!comparator.CompareStringList(
        baseline,
        List("[\"a\",\"b\"]",
            StatisticReconciliationCollectionSemantics.Ordered)).Equal,
        "ORDERED_MULTIPLICITY_DIFFERS");
}

static void UnorderedListUsesSetIdentity()
{
    var comparator = new StatisticReconciliationTypedComparator();
    var result = comparator.CompareStringList(
        List("[\"b\",\"a\",\"a\"]",
            StatisticReconciliationCollectionSemantics.Unordered),
        List("[\"a\",\"b\"]",
            StatisticReconciliationCollectionSemantics.Unordered));
    Require(result.Equal, "UNORDERED_SET_MATCH");
    Equal("[\"a\",\"b\"]", result.ExpectedCanonicalValue,
        "UNORDERED_EXPECTED_CANONICAL");
    Equal("[\"a\",\"b\"]", result.ActualCanonicalValue,
        "UNORDERED_ACTUAL_CANONICAL");
    Require(!comparator.CompareStringList(
        List("[\"a\",\"c\"]",
            StatisticReconciliationCollectionSemantics.Unordered),
        List("[\"a\",\"b\"]",
            StatisticReconciliationCollectionSemantics.Unordered)).Equal,
        "UNORDERED_MEMBER_CHANGE_DIFFERS");
    ExpectCode(
        () => comparator.CompareStringList(
            List("[\"a\", \"b\"]", StatisticReconciliationCollectionSemantics.Unordered),
            List("[\"a\",\"b\"]", StatisticReconciliationCollectionSemantics.Unordered)),
        StatisticReconciliationTypedComparisonFailureCodes.CollectionInvalid);
}

static void BucketsAndStableIdentitiesStayTyped()
{
    var comparator = new StatisticReconciliationTypedComparator();
    Require(comparator.CompareScalar(
        Scalar(StatisticReconciliationTypedValueTypes.Bucket, "N:1.5"),
        Scalar(StatisticReconciliationTypedValueTypes.Bucket, "N:1.5")).Equal,
        "NUMERIC_BUCKET_MATCH");
    Require(!comparator.CompareScalar(
        Scalar(StatisticReconciliationTypedValueTypes.Bucket, "B:true"),
        Scalar(StatisticReconciliationTypedValueTypes.Bucket, "S:true")).Equal,
        "BUCKET_SUBTYPE_DIFFERS");

    foreach (var kind in new[]
             {
                 StatisticReconciliationStableIdentityKinds.Table,
                 StatisticReconciliationStableIdentityKinds.Row,
                 StatisticReconciliationStableIdentityKinds.Label
             })
    {
        var identity = Identity(kind, "form-v1", "metric-01");
        Require(comparator.CompareIdentity(identity, identity).Equal,
            $"{kind}_MATCH");
    }

    Require(!comparator.CompareIdentity(
        Identity(StatisticReconciliationStableIdentityKinds.Table, "form-v1", "metric-01"),
        Identity(StatisticReconciliationStableIdentityKinds.Row, "form-v1", "metric-01")).Equal,
        "IDENTITY_KIND_DIFFERS");
    var boundary = comparator.CompareIdentity(
        Identity(StatisticReconciliationStableIdentityKinds.Label, "a", "bc"),
        Identity(StatisticReconciliationStableIdentityKinds.Label, "ab", "c"));
    Require(!boundary.Equal && boundary.ExpectedIdentitySha256 != boundary.ActualIdentitySha256,
        "IDENTITY_COMPONENT_BOUNDARIES_PRESERVED");
}

static StatisticReconciliationCanonicalNumber Number(string value, int scale)
    => new(value, scale);

static StatisticReconciliationCanonicalTypedValue Scalar(string type, string value)
    => new(type, value);

static StatisticReconciliationCanonicalStringList List(string json, string semantics)
    => new(json, semantics);

static StatisticReconciliationStableIdentity Identity(string kind, params string[] components)
    => new(kind, components);

static void ExpectCode(Action action, string expectedCode)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationTypedComparisonException exception)
    {
        Equal(expectedCode, exception.Code, "FAILURE_CODE");
        return;
    }
    throw new InvalidOperationException($"Expected failure code {expectedCode}.");
}

static void Require(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
}

static void Equal<T>(T expected, T actual, string name)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected={expected}; actual={actual}");
}
