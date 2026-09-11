using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace tdtd_be.Services.StatisticsReconciliation.TypedDelta;

public sealed class StatisticReconciliationStatefulComparator
{
    private const int MaximumCanonicalLength = 4096;

    private static readonly IReadOnlySet<string> ValueTypes = new HashSet<string>(
        [
            StatisticReconciliationStatefulValueTypes.Number,
            StatisticReconciliationTypedValueTypes.Date,
            StatisticReconciliationTypedValueTypes.FullDate,
            StatisticReconciliationTypedValueTypes.Period,
            StatisticReconciliationTypedValueTypes.Boolean,
            StatisticReconciliationTypedValueTypes.Enum,
            StatisticReconciliationTypedValueTypes.StringList,
            StatisticReconciliationTypedValueTypes.Text,
            StatisticReconciliationTypedValueTypes.Bucket
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> PresentStates = new HashSet<string>(
        [
            StatisticReconciliationObservationValueStates.Null,
            StatisticReconciliationObservationValueStates.Empty,
            StatisticReconciliationObservationValueStates.Redacted,
            StatisticReconciliationObservationValueStates.Value
        ],
        StringComparer.Ordinal);

    private readonly StatisticReconciliationTypedComparator _typed = new();

    public StatisticReconciliationStatefulComparison Compare(
        StatisticReconciliationStatefulObservation? expected,
        StatisticReconciliationStatefulObservation? actual)
    {
        if (expected is null && actual is null)
            throw Failure(
                StatisticReconciliationStatefulDeltaFailureCodes.ObservationInvalid,
                "At least one typed observation is required.");

        var normalizedExpected = expected is null ? null : Normalize(expected, "expected");
        var normalizedActual = actual is null ? null : Normalize(actual, "actual");
        var identity = normalizedExpected?.Identity ?? normalizedActual!.Identity;
        var identityValidation = _typed.CompareIdentity(identity, identity);

        if (normalizedExpected is not null && normalizedActual is not null)
        {
            var pair = _typed.CompareIdentity(
                normalizedExpected.Identity,
                normalizedActual.Identity);
            if (!pair.Equal)
            {
                throw Failure(
                    StatisticReconciliationStatefulDeltaFailureCodes.IdentityPairInvalid,
                    "Different stable identities must be emitted as MISSING and EXTRA rows.");
            }
        }

        var expectedState = normalizedExpected?.ValueState ??
            StatisticReconciliationObservationValueStates.Missing;
        var actualState = normalizedActual?.ValueState ??
            StatisticReconciliationObservationValueStates.Missing;
        string deltaState;
        string deltaCode;
        string? numericDelta = null;
        int? numericDeltaScale = null;
        bool equal;

        if (normalizedActual is null)
        {
            deltaState = StatisticReconciliationObservationValueStates.Missing;
            deltaCode = StatisticReconciliationIdentityDeltaCodes.MissingIdentity;
            equal = false;
        }
        else if (normalizedExpected is null)
        {
            deltaState = StatisticReconciliationObservationValueStates.Extra;
            deltaCode = StatisticReconciliationIdentityDeltaCodes.ExtraIdentity;
            equal = false;
        }
        else if (!StringComparer.Ordinal.Equals(expectedState, actualState))
        {
            deltaState = actualState;
            deltaCode = StatisticReconciliationIdentityDeltaCodes.StateMismatch;
            equal = false;
        }
        else if (!StringComparer.Ordinal.Equals(
                     normalizedExpected.ValueType,
                     normalizedActual.ValueType))
        {
            deltaState = actualState;
            deltaCode = StatisticReconciliationIdentityDeltaCodes.ValueTypeMismatch;
            equal = false;
        }
        else if (actualState != StatisticReconciliationObservationValueStates.Value)
        {
            deltaState = actualState;
            deltaCode = StatisticReconciliationIdentityDeltaCodes.None;
            equal = true;
        }
        else
        {
            deltaState = StatisticReconciliationObservationValueStates.Value;
            equal = CompareValue(
                normalizedExpected,
                normalizedActual,
                out numericDelta,
                out numericDeltaScale);
            deltaCode = equal
                ? StatisticReconciliationIdentityDeltaCodes.None
                : StatisticReconciliationIdentityDeltaCodes.ValueMismatch;
        }

        var layerVerdict = equal
            ? StatisticReconciliationLayerDeltaVerdicts.ZeroDelta
            : StatisticReconciliationLayerDeltaVerdicts.NonzeroDelta;
        var expectedCanonical = normalizedExpected?.ValueState ==
            StatisticReconciliationObservationValueStates.Value
            ? normalizedExpected.CanonicalValue
            : null;
        var actualCanonical = normalizedActual?.ValueState ==
            StatisticReconciliationObservationValueStates.Value
            ? normalizedActual.CanonicalValue
            : null;
        var comparisonSha256 = HashFields(
            "P10_STATEFUL_IDENTITY_COMPARISON_V2",
            identityValidation.ExpectedIdentitySha256,
            expectedState,
            normalizedExpected?.ValueType ?? "~",
            normalizedExpected?.CollectionSemantics ?? "~",
            expectedCanonical ?? "~",
            normalizedExpected is null
                ? "~"
                : I(normalizedExpected.DecimalScale),
            actualState,
            normalizedActual?.ValueType ?? "~",
            normalizedActual?.CollectionSemantics ?? "~",
            actualCanonical ?? "~",
            normalizedActual is null
                ? "~"
                : I(normalizedActual.DecimalScale),
            deltaState,
            numericDelta ?? "~",
            numericDeltaScale is null ? "~" : I(numericDeltaScale.Value),
            deltaCode,
            layerVerdict,
            equal ? "1" : "0");

        return new StatisticReconciliationStatefulComparison(
            identity.IdentityKind,
            identityValidation.ExpectedComponents,
            identityValidation.ExpectedIdentitySha256,
            expectedState,
            actualState,
            deltaState,
            normalizedExpected?.ValueType,
            normalizedActual?.ValueType,
            normalizedExpected?.CollectionSemantics,
            normalizedActual?.CollectionSemantics,
            expectedCanonical,
            actualCanonical,
            numericDelta,
            numericDeltaScale,
            deltaCode,
            layerVerdict,
            equal,
            comparisonSha256);
    }

    private StatisticReconciliationStatefulObservation Normalize(
        StatisticReconciliationStatefulObservation value,
        string path)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = _typed.CompareIdentity(value.Identity, value.Identity);
        if (!ValueTypes.Contains(value.ValueType) ||
            !PresentStates.Contains(value.ValueState) ||
            value.DecimalScale is < 0 or > 28)
        {
            throw ObservationInvalid(path);
        }

        if (value.ValueState != StatisticReconciliationObservationValueStates.Value)
        {
            if (value.CanonicalValue is not null || value.DecimalScale != 0 ||
                value.CollectionSemantics is not null)
            {
                throw ObservationInvalid(path);
            }
            return value;
        }

        if (value.CanonicalValue is null ||
            value.CanonicalValue.Length is < 1 or > MaximumCanonicalLength)
        {
            throw ObservationInvalid(path);
        }

        if (value.ValueType == StatisticReconciliationStatefulValueTypes.Number)
        {
            if (value.CollectionSemantics is not null)
                throw ObservationInvalid(path);
            _ = _typed.CompareNumeric(
                new StatisticReconciliationCanonicalNumber(
                    value.CanonicalValue,
                    value.DecimalScale),
                new StatisticReconciliationCanonicalNumber(
                    value.CanonicalValue,
                    value.DecimalScale));
        }
        else if (value.ValueType == StatisticReconciliationTypedValueTypes.StringList)
        {
            if (value.DecimalScale != 0 || value.CollectionSemantics is null)
                throw ObservationInvalid(path);
            _ = _typed.CompareStringList(
                new StatisticReconciliationCanonicalStringList(
                    value.CanonicalValue,
                    value.CollectionSemantics),
                new StatisticReconciliationCanonicalStringList(
                    value.CanonicalValue,
                    value.CollectionSemantics));
        }
        else
        {
            if (value.DecimalScale != 0 || value.CollectionSemantics is not null)
                throw ObservationInvalid(path);
            _ = _typed.CompareScalar(
                new StatisticReconciliationCanonicalTypedValue(
                    value.ValueType,
                    value.CanonicalValue),
                new StatisticReconciliationCanonicalTypedValue(
                    value.ValueType,
                    value.CanonicalValue));
        }
        return value;
    }

    private bool CompareValue(
        StatisticReconciliationStatefulObservation expected,
        StatisticReconciliationStatefulObservation actual,
        out string? numericDelta,
        out int? numericDeltaScale)
    {
        numericDelta = null;
        numericDeltaScale = null;
        if (expected.ValueType == StatisticReconciliationStatefulValueTypes.Number)
        {
            var result = _typed.CompareNumeric(
                new StatisticReconciliationCanonicalNumber(
                    expected.CanonicalValue!, expected.DecimalScale),
                new StatisticReconciliationCanonicalNumber(
                    actual.CanonicalValue!, actual.DecimalScale));
            numericDelta = result.Delta.CanonicalValue;
            numericDeltaScale = result.Delta.DecimalScale;
            return result.Equal;
        }
        if (expected.ValueType == StatisticReconciliationTypedValueTypes.StringList)
        {
            if (!StringComparer.Ordinal.Equals(
                    expected.CollectionSemantics,
                    actual.CollectionSemantics))
                return false;
            return _typed.CompareStringList(
                new StatisticReconciliationCanonicalStringList(
                    expected.CanonicalValue!, expected.CollectionSemantics!),
                new StatisticReconciliationCanonicalStringList(
                    actual.CanonicalValue!, actual.CollectionSemantics!)).Equal;
        }
        return _typed.CompareScalar(
            new StatisticReconciliationCanonicalTypedValue(
                expected.ValueType, expected.CanonicalValue!),
            new StatisticReconciliationCanonicalTypedValue(
                actual.ValueType, actual.CanonicalValue!)).Equal;
    }

    private static StatisticReconciliationTypedComparisonException ObservationInvalid(
        string path)
        => Failure(
            StatisticReconciliationStatefulDeltaFailureCodes.ObservationInvalid,
            $"Canonical stateful observation required at {path}.");

    private static StatisticReconciliationTypedComparisonException Failure(
        string code,
        string message)
        => new(code, message);

    private static string HashFields(string domain, params string[] fields)
    {
        var builder = new StringBuilder();
        AppendHashField(builder, domain);
        foreach (var field in fields)
            AppendHashField(builder, field);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendHashField(StringBuilder builder, string value)
    {
        builder.Append(Encoding.UTF8.GetByteCount(value)
                .ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);
    }

    private static string I(long value)
        => value.ToString(CultureInfo.InvariantCulture);
}

