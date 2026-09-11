using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.TypedDelta;

public sealed class StatisticReconciliationTypedComparator
{
    private const int MaxCanonicalLength = 4096;
    private const int MaxCollectionItems = 2048;
    private const int MaxIdentityComponents = 32;

    private static readonly IReadOnlySet<string> ScalarTypes = new HashSet<string>(
        [
            StatisticReconciliationTypedValueTypes.Date,
            StatisticReconciliationTypedValueTypes.FullDate,
            StatisticReconciliationTypedValueTypes.Period,
            StatisticReconciliationTypedValueTypes.Boolean,
            StatisticReconciliationTypedValueTypes.Enum,
            StatisticReconciliationTypedValueTypes.Text,
            StatisticReconciliationTypedValueTypes.Bucket
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> IdentityKinds = new HashSet<string>(
        [
            StatisticReconciliationStableIdentityKinds.Table,
            StatisticReconciliationStableIdentityKinds.Row,
            StatisticReconciliationStableIdentityKinds.Label
        ],
        StringComparer.Ordinal);

    public StatisticReconciliationNumericComparison CompareNumeric(
        StatisticReconciliationCanonicalNumber expected,
        StatisticReconciliationCanonicalNumber actual)
    {
        var expectedValue = NormalizeNumber(expected, "expected");
        var actualValue = NormalizeNumber(actual, "actual");
        decimal deltaValue;
        try
        {
            deltaValue = checked(actualValue - expectedValue);
        }
        catch (OverflowException)
        {
            throw Failure(
                StatisticReconciliationTypedComparisonFailureCodes.NumericOverflow,
                "Exact numeric delta overflowed the decimal domain.");
        }

        var delta = CanonicalNumber(deltaValue);
        var equal = expectedValue == actualValue &&
                    expected.DecimalScale == actual.DecimalScale;
        return new StatisticReconciliationNumericComparison(
            expected,
            actual,
            delta,
            equal,
            HashFields(
                "P10_TYPED_NUMERIC_COMPARISON_V1",
                expected.CanonicalValue,
                I(expected.DecimalScale),
                actual.CanonicalValue,
                I(actual.DecimalScale),
                delta.CanonicalValue,
                I(delta.DecimalScale),
                equal ? "1" : "0"));
    }

    public StatisticReconciliationMeanComparison CompareMean(
        StatisticReconciliationNumericAggregate expected,
        StatisticReconciliationNumericAggregate actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var expectedSum = NormalizeNumber(expected.Sum, "expected.sum");
        var actualSum = NormalizeNumber(actual.Sum, "actual.sum");
        RequirePositiveCount(expected.NumericValueCount, "expected.numericValueCount");
        RequirePositiveCount(actual.NumericValueCount, "actual.numericValueCount");

        var expectedMean = DivideMean(expectedSum, expected.NumericValueCount);
        var actualMean = DivideMean(actualSum, actual.NumericValueCount);
        var sumEqual = expectedSum == actualSum &&
                       expected.Sum.DecimalScale == actual.Sum.DecimalScale;
        var countEqual = expected.NumericValueCount == actual.NumericValueCount;
        var equal = sumEqual && countEqual &&
                    expectedMean.CanonicalValue == actualMean.CanonicalValue &&
                    expectedMean.DecimalScale == actualMean.DecimalScale;
        return new StatisticReconciliationMeanComparison(
            expected,
            actual,
            expectedMean,
            actualMean,
            sumEqual,
            countEqual,
            equal,
            HashFields(
                "P10_TYPED_MEAN_COMPARISON_V1",
                expected.Sum.CanonicalValue,
                I(expected.Sum.DecimalScale),
                I(expected.NumericValueCount),
                actual.Sum.CanonicalValue,
                I(actual.Sum.DecimalScale),
                I(actual.NumericValueCount),
                expectedMean.CanonicalValue,
                I(expectedMean.DecimalScale),
                actualMean.CanonicalValue,
                I(actualMean.DecimalScale),
                equal ? "1" : "0"));
    }

    public StatisticReconciliationTypedComparison CompareScalar(
        StatisticReconciliationCanonicalTypedValue expected,
        StatisticReconciliationCanonicalTypedValue actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var expectedCanonical = NormalizeScalar(expected, "expected");
        var actualCanonical = NormalizeScalar(actual, "actual");
        var equal = expected.ValueType == actual.ValueType &&
                    StringComparer.Ordinal.Equals(expectedCanonical, actualCanonical);
        return TypedResult(
            expected.ValueType,
            actual.ValueType,
            expectedCanonical,
            actualCanonical,
            equal,
            "P10_TYPED_SCALAR_COMPARISON_V1");
    }

    public StatisticReconciliationTypedComparison CompareStringList(
        StatisticReconciliationCanonicalStringList expected,
        StatisticReconciliationCanonicalStringList actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var expectedCanonical = NormalizeStringList(expected, "expected");
        var actualCanonical = NormalizeStringList(actual, "actual");
        var equal = expected.Semantics == actual.Semantics &&
                    StringComparer.Ordinal.Equals(expectedCanonical, actualCanonical);
        return TypedResult(
            $"{StatisticReconciliationTypedValueTypes.StringList}:{expected.Semantics}",
            $"{StatisticReconciliationTypedValueTypes.StringList}:{actual.Semantics}",
            expectedCanonical,
            actualCanonical,
            equal,
            "P10_TYPED_STRING_LIST_COMPARISON_V1");
    }

    public StatisticReconciliationStableIdentityComparison CompareIdentity(
        StatisticReconciliationStableIdentity expected,
        StatisticReconciliationStableIdentity actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var expectedComponents = NormalizeIdentity(expected, "expected");
        var actualComponents = NormalizeIdentity(actual, "actual");
        var expectedSha = HashFields(
            "P10_TYPED_STABLE_IDENTITY_V1",
            [expected.IdentityKind, .. expectedComponents]);
        var actualSha = HashFields(
            "P10_TYPED_STABLE_IDENTITY_V1",
            [actual.IdentityKind, .. actualComponents]);
        var equal = expected.IdentityKind == actual.IdentityKind &&
                    expectedComponents.SequenceEqual(actualComponents, StringComparer.Ordinal);
        return new StatisticReconciliationStableIdentityComparison(
            expected.IdentityKind,
            actual.IdentityKind,
            expectedComponents,
            actualComponents,
            expectedSha,
            actualSha,
            equal,
            HashFields(
                "P10_TYPED_STABLE_IDENTITY_COMPARISON_V1",
                expectedSha,
                actualSha,
                equal ? "1" : "0"));
    }

    private static decimal NormalizeNumber(
        StatisticReconciliationCanonicalNumber number,
        string path)
    {
        ArgumentNullException.ThrowIfNull(number);
        if (number.CanonicalValue is null ||
            number.CanonicalValue.Length is < 1 or > MaxCanonicalLength ||
            number.DecimalScale is < 0 or > 28 ||
            !decimal.TryParse(
                number.CanonicalValue,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw NumericInvalid(path);
        }

        var canonical = CanonicalDecimal(parsed);
        var scale = DecimalScale(canonical);
        if (!StringComparer.Ordinal.Equals(canonical, number.CanonicalValue) ||
            scale != number.DecimalScale)
        {
            throw NumericInvalid(path);
        }
        return parsed;
    }

    private static string NormalizeScalar(
        StatisticReconciliationCanonicalTypedValue value,
        string path)
    {
        if (!ScalarTypes.Contains(value.ValueType) ||
            value.CanonicalValue is null ||
            value.CanonicalValue.Length is < 1 or > MaxCanonicalLength)
        {
            throw TypedInvalid(path);
        }

        var valid = value.ValueType switch
        {
            StatisticReconciliationTypedValueTypes.Date =>
                CanonicalPeriodLike(value.CanonicalValue, fullDate: false),
            StatisticReconciliationTypedValueTypes.FullDate =>
                CanonicalPeriodLike(value.CanonicalValue, fullDate: true),
            StatisticReconciliationTypedValueTypes.Period =>
                CanonicalPeriodLike(value.CanonicalValue, fullDate: false),
            StatisticReconciliationTypedValueTypes.Boolean =>
                value.CanonicalValue is "true" or "false",
            StatisticReconciliationTypedValueTypes.Enum or
                StatisticReconciliationTypedValueTypes.Text => true,
            StatisticReconciliationTypedValueTypes.Bucket =>
                CanonicalBucket(value.CanonicalValue),
            _ => false
        };
        if (!valid)
            throw TypedInvalid(path);
        return value.CanonicalValue;
    }

    private static string NormalizeStringList(
        StatisticReconciliationCanonicalStringList value,
        string path)
    {
        if (value.Semantics is not (
                StatisticReconciliationCollectionSemantics.Ordered or
                StatisticReconciliationCollectionSemantics.Unordered) ||
            value.CanonicalJson is null ||
            value.CanonicalJson.Length is < 2 or > MaxCanonicalLength)
        {
            throw CollectionInvalid(path);
        }

        try
        {
            using var document = JsonDocument.Parse(
                value.CanonicalJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                });
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() is < 1 or > MaxCollectionItems)
            {
                throw CollectionInvalid(path);
            }

            var items = document.RootElement.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString()!
                    : throw CollectionInvalid(path))
                .ToArray();
            if (items.Any(item => item.Length > MaxCanonicalLength))
                throw CollectionInvalid(path);

            var rawCanonical = JsonSerializer.Serialize(items);
            if (!StringComparer.Ordinal.Equals(rawCanonical, value.CanonicalJson))
                throw CollectionInvalid(path);
            if (value.Semantics == StatisticReconciliationCollectionSemantics.Unordered)
            {
                items = items
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
            }
            return JsonSerializer.Serialize(items);
        }
        catch (StatisticReconciliationTypedComparisonException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw CollectionInvalid(path);
        }
    }

    private static ImmutableArray<string> NormalizeIdentity(
        StatisticReconciliationStableIdentity value,
        string path)
    {
        if (!IdentityKinds.Contains(value.IdentityKind) ||
            value.Components is null ||
            value.Components.Count is < 1 or > MaxIdentityComponents)
        {
            throw IdentityInvalid(path);
        }

        var builder = ImmutableArray.CreateBuilder<string>(value.Components.Count);
        foreach (var component in value.Components)
        {
            if (component is null || component.Length is < 1 or > MaxCanonicalLength ||
                component.Any(character => character == '\0'))
            {
                throw IdentityInvalid(path);
            }
            builder.Add(component);
        }
        return builder.MoveToImmutable();
    }

    private static StatisticReconciliationTypedComparison TypedResult(
        string expectedType,
        string actualType,
        string expectedCanonical,
        string actualCanonical,
        bool equal,
        string domain)
        => new(
            expectedType,
            actualType,
            expectedCanonical,
            actualCanonical,
            equal,
            HashFields(
                domain,
                expectedType,
                expectedCanonical,
                actualType,
                actualCanonical,
                equal ? "1" : "0"));

    private static bool CanonicalBucket(string value)
    {
        if (value.StartsWith("S:", StringComparison.Ordinal))
            return value.Length > 2;
        if (value is "B:true" or "B:false")
            return true;
        if (!value.StartsWith("N:", StringComparison.Ordinal))
            return false;
        var numeric = value[2..];
        if (numeric.Length == 0 ||
            !decimal.TryParse(
                numeric,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }
        return StringComparer.Ordinal.Equals(CanonicalDecimal(parsed), numeric);
    }

    private static bool CanonicalPeriodLike(string value, bool fullDate)
    {
        if (value.StartsWith("DAY:", StringComparison.Ordinal))
        {
            return DateTime.TryParseExact(
                value[4..],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _);
        }
        if (fullDate)
            return false;
        if (value.StartsWith("MONTH:", StringComparison.Ordinal))
        {
            return DateTime.TryParseExact(
                value[6..],
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _);
        }
        return value.StartsWith("YEAR:", StringComparison.Ordinal) &&
               DateTime.TryParseExact(
                   value[5..],
                   "yyyy",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _);
    }

    private static StatisticReconciliationCanonicalNumber DivideMean(
        decimal sum,
        long count)
    {
        try
        {
            return CanonicalNumber(sum / count);
        }
        catch (OverflowException)
        {
            throw Failure(
                StatisticReconciliationTypedComparisonFailureCodes.NumericOverflow,
                "Exact mean overflowed the decimal domain.");
        }
    }

    private static void RequirePositiveCount(long count, string path)
    {
        if (count <= 0)
            throw NumericInvalid(path);
    }

    private static StatisticReconciliationCanonicalNumber CanonicalNumber(decimal value)
    {
        var canonical = CanonicalDecimal(value);
        return new StatisticReconciliationCanonicalNumber(canonical, DecimalScale(canonical));
    }

    private static string CanonicalDecimal(decimal value)
        => value.ToString("0.#############################", CultureInfo.InvariantCulture);

    private static int DecimalScale(string canonical)
    {
        var point = canonical.IndexOf('.');
        return point < 0 ? 0 : canonical.Length - point - 1;
    }

    private static string HashFields(string domain, params string[] fields)
    {
        var builder = new StringBuilder();
        AppendHashField(builder, domain);
        foreach (var field in fields)
            AppendHashField(builder, field);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendHashField(StringBuilder builder, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }

    private static string I(long value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static StatisticReconciliationTypedComparisonException NumericInvalid(string path)
        => Failure(
            StatisticReconciliationTypedComparisonFailureCodes.NumericInvalid,
            $"Canonical decimal and scale required at {path}.");

    private static StatisticReconciliationTypedComparisonException TypedInvalid(string path)
        => Failure(
            StatisticReconciliationTypedComparisonFailureCodes.TypedValueInvalid,
            $"Canonical typed value required at {path}.");

    private static StatisticReconciliationTypedComparisonException CollectionInvalid(string path)
        => Failure(
            StatisticReconciliationTypedComparisonFailureCodes.CollectionInvalid,
            $"Canonical string-list value required at {path}.");

    private static StatisticReconciliationTypedComparisonException IdentityInvalid(string path)
        => Failure(
            StatisticReconciliationTypedComparisonFailureCodes.IdentityInvalid,
            $"Canonical stable identity required at {path}.");

    private static StatisticReconciliationTypedComparisonException Failure(
        string code,
        string message)
        => new(code, message);
}
