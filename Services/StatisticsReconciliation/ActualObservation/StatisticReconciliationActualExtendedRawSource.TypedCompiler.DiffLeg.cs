using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class
    StatisticReconciliationActualExtendedRawSourceTypedCompiler
{
    private static ImmutableArray<StatisticReconciliationActualRawSummaryAtom>
        CompileDiffLegAtoms(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            string transitionLeg,
            ImmutableArray<
                StatisticReconciliationActualExtendedRawSourceEnvelope>
                envelopes)
    {
        if (descriptor.Family != "DIFF" ||
            transitionLeg is not ("BEFORE" or "AFTER") ||
            envelopes.IsDefault || envelopes.Length > 100_000)
            throw DiffLegInvalid();

        var samples = new List<DiffLegSample>();
        long rowCount = 0;
        foreach (var envelope in envelopes)
        {
            using var payload = StatisticReconciliationActualJson.ParseStrict(
                envelope.CanonicalPayloadJson,
                "EXTENDED_DIFF_LEG_PAYLOAD");
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
                throw DiffLegInvalid();
            if (!DiffLegResolvePointer(
                    payload.RootElement, descriptor.JsonPointer,
                    out var value))
            {
                samples.Add(DiffLegSample.Missing);
                continue;
            }
            if (descriptor.ExpandArray &&
                value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() == 0)
                {
                    samples.Add(DiffLegSample.Empty);
                    continue;
                }
                try
                {
                    rowCount = checked(rowCount + value.GetArrayLength());
                }
                catch (OverflowException)
                {
                    throw DiffLegInvalid();
                }
                foreach (var item in value.EnumerateArray())
                    samples.Add(DiffLegNormalize(item, descriptor));
                continue;
            }
            samples.Add(DiffLegNormalize(value, descriptor));
        }

        var reportCount = (long)envelopes.Length;
        var values = samples.Where(value => value.State == "VALUE")
            .ToArray();
        var numbers = descriptor.ValueType == "NUMBER"
            ? values.Select(value => decimal.Parse(
                    value.CanonicalValue,
                    NumberStyles.AllowLeadingSign |
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture))
                .ToArray()
            : [];
        var numericCount = (long)numbers.Length;
        var atoms = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualRawSummaryAtom>();
        atoms.Add(DiffLegCount(
            descriptor, "REPORT_COUNT", reportCount, reportCount,
            rowCount, numericCount, transitionLeg));
        atoms.Add(DiffLegCount(
            descriptor, "ROW_COUNT", rowCount, reportCount,
            rowCount, numericCount, transitionLeg));
        atoms.Add(DiffLegCount(
            descriptor, "COUNT", values.LongLength, reportCount,
            rowCount, numericCount, transitionLeg));
        atoms.Add(DiffLegCount(
            descriptor, "NUMERIC_VALUE_COUNT", numericCount, reportCount,
            rowCount, numericCount, transitionLeg));
        atoms.Add(DiffLegState(
            descriptor, "MISSING",
            samples.LongCount(value => value.State == "MISSING"),
            reportCount, rowCount, numericCount, transitionLeg));
        atoms.Add(DiffLegState(
            descriptor, "NULL",
            samples.LongCount(value => value.State == "NULL"),
            reportCount, rowCount, numericCount, transitionLeg));
        atoms.Add(DiffLegState(
            descriptor, "EMPTY",
            samples.LongCount(value => value.State == "EMPTY"),
            reportCount, rowCount, numericCount, transitionLeg));

        decimal? sum = null;
        if (numbers.Length > 0 &&
            (descriptor.Operations.Contains("SUM", StringComparer.Ordinal) ||
             descriptor.Operations.Contains("MEAN", StringComparer.Ordinal)))
            sum = DiffLegSum(numbers);
        if (descriptor.Operations.Contains("SUM", StringComparer.Ordinal))
            atoms.Add(DiffLegAggregate(
                descriptor, "SUM", sum, reportCount, rowCount,
                numericCount, transitionLeg));
        if (descriptor.Operations.Contains("MIN", StringComparer.Ordinal))
            atoms.Add(DiffLegMinMax(
                descriptor, true, values, numbers, reportCount, rowCount,
                numericCount, transitionLeg));
        if (descriptor.Operations.Contains("MAX", StringComparer.Ordinal))
            atoms.Add(DiffLegMinMax(
                descriptor, false, values, numbers, reportCount, rowCount,
                numericCount, transitionLeg));
        if (descriptor.Operations.Contains("MEAN", StringComparer.Ordinal))
        {
            decimal? mean = null;
            if (numericCount > 0)
            {
                try
                {
                    mean = sum!.Value / numericCount;
                }
                catch (OverflowException)
                {
                    throw DiffLegInvalid();
                }
            }
            atoms.Add(DiffLegAggregate(
                descriptor, "MEAN", mean, reportCount, rowCount,
                numericCount, transitionLeg));
        }
        if (descriptor.Operations.Contains("VALUES", StringComparer.Ordinal) &&
            descriptor.ValueType != "NUMBER")
        {
            var kind = DiffLegValueAtomKind(descriptor.ValueType);
            foreach (var group in values.GroupBy(
                         value => value.CanonicalValue,
                         StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                atoms.Add(DiffLegAtom(
                    descriptor,
                    kind,
                    descriptor.ValueType,
                    "VALUE",
                    group.Key,
                    group.First().DecimalScale,
                    group.LongCount(),
                    reportCount,
                    rowCount,
                    numericCount,
                    transitionLeg,
                    descriptor.CollectionSemantics));
            }
        }
        var result = atoms.ToImmutable();
        var declared = descriptor.AtomKinds.ToImmutableHashSet(
            StringComparer.Ordinal);
        if (result.Length == 0 || result.Any(value =>
                !declared.Contains(value.AtomKind)))
            throw DiffLegInvalid();
        return result;
    }

    private static DiffLegSample DiffLegNormalize(
        JsonElement value,
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return DiffLegSample.Null;
        if (value.ValueKind == JsonValueKind.String &&
            value.GetString()!.Length == 0)
            return DiffLegSample.Empty;
        if (value.ValueKind == JsonValueKind.Array &&
            value.GetArrayLength() == 0)
            return DiffLegSample.Empty;
        return descriptor.ValueType switch
        {
            "NUMBER" => DiffLegNumber(value),
            "DATE" => DiffLegDate(value, false),
            "FULL_DATE" => DiffLegDate(value, true),
            "BOOLEAN" => DiffLegBoolean(value),
            "ENUM" => DiffLegString(value),
            "STRING_LIST" => DiffLegStringList(
                value, descriptor.Unordered),
            "TEXT" => DiffLegString(value),
            _ => throw DiffLegInvalid()
        };
    }

    private static DiffLegSample DiffLegNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out var number))
            throw DiffLegInvalid();
        var canonical = StatisticReconciliationActualCanonical.Number(number);
        return new("VALUE", canonical, DiffLegDecimalScale(canonical));
    }

    private static DiffLegSample DiffLegBoolean(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.True => new("VALUE", "true", 0),
            JsonValueKind.False => new("VALUE", "false", 0),
            _ => throw DiffLegInvalid()
        };

    private static DiffLegSample DiffLegString(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? new("VALUE", value.GetString()!, 0)
            : throw DiffLegInvalid();

    private static DiffLegSample DiffLegStringList(
        JsonElement value,
        bool unordered)
    {
        if (value.ValueKind != JsonValueKind.Array ||
            value.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String))
            throw DiffLegInvalid();
        IEnumerable<string> items = value.EnumerateArray()
            .Select(item => item.GetString()!);
        if (unordered)
            items = items.Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal);
        return new(
            "VALUE", JsonSerializer.Serialize(items.ToArray()), 0);
    }

    private static DiffLegSample DiffLegDate(
        JsonElement value,
        bool fullDate)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw DiffLegInvalid();
        var raw = value.GetString()!;
        string canonical;
        if (fullDate && DateTime.TryParseExact(
                raw, "dd/MM/yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var full))
            canonical = "DAY:" + full.ToString(
                "yyyy-MM-dd", CultureInfo.InvariantCulture);
        else if (!fullDate && DateTime.TryParseExact(
                     raw, "yyyy", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var year))
            canonical = "YEAR:" + year.ToString(
                "yyyy", CultureInfo.InvariantCulture);
        else if (!fullDate && DateTime.TryParseExact(
                     raw, "MM/yyyy", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var month))
            canonical = "MONTH:" + month.ToString(
                "yyyy-MM", CultureInfo.InvariantCulture);
        else if (!fullDate && DateTime.TryParseExact(
                     raw, "dd/MM/yyyy", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out var day))
            canonical = "DAY:" + day.ToString(
                "yyyy-MM-dd", CultureInfo.InvariantCulture);
        else
            throw DiffLegInvalid();
        return new("VALUE", canonical, 0);
    }

    private static StatisticReconciliationActualRawSummaryAtom DiffLegCount(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string kind,
        long value,
        long reportCount,
        long rowCount,
        long numericCount,
        string transitionLeg)
        => DiffLegAtom(
            descriptor, kind, "NUMBER", "VALUE",
            StatisticReconciliationActualCanonical.Integer(value), 0, 1,
            reportCount, rowCount, numericCount, transitionLeg, null);

    private static StatisticReconciliationActualRawSummaryAtom DiffLegState(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string kind,
        long value,
        long reportCount,
        long rowCount,
        long numericCount,
        string transitionLeg)
        => DiffLegAtom(
            descriptor, kind, descriptor.ValueType, kind,
            StatisticReconciliationActualCanonical.Integer(value), 0, value,
            reportCount, rowCount, numericCount, transitionLeg,
            descriptor.CollectionSemantics);

    private static StatisticReconciliationActualRawSummaryAtom
        DiffLegAggregate(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            string kind,
            decimal? value,
            long reportCount,
            long rowCount,
            long numericCount,
            string transitionLeg)
    {
        var canonical = value.HasValue
            ? StatisticReconciliationActualCanonical.Number(value.Value)
            : string.Empty;
        return DiffLegAtom(
            descriptor, kind, "NUMBER",
            value.HasValue ? "VALUE" : "MISSING", canonical,
            value.HasValue ? DiffLegDecimalScale(canonical) : 0,
            value.HasValue ? 1 : 0, reportCount, rowCount, numericCount,
            transitionLeg, null);
    }

    private static StatisticReconciliationActualRawSummaryAtom DiffLegMinMax(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        bool minimum,
        IReadOnlyList<DiffLegSample> values,
        IReadOnlyList<decimal> numbers,
        long reportCount,
        long rowCount,
        long numericCount,
        string transitionLeg)
    {
        if (descriptor.ValueType == "NUMBER")
        {
            decimal? value = numbers.Count == 0
                ? null
                : minimum ? numbers.Min() : numbers.Max();
            return DiffLegAggregate(
                descriptor, minimum ? "MIN" : "MAX", value,
                reportCount, rowCount, numericCount, transitionLeg);
        }
        var canonical = values.Count == 0
            ? null
            : minimum
                ? values.MinBy(value => value.CanonicalValue,
                    StringComparer.Ordinal)!.CanonicalValue
                : values.MaxBy(value => value.CanonicalValue,
                    StringComparer.Ordinal)!.CanonicalValue;
        return DiffLegAtom(
            descriptor, minimum ? "MIN" : "MAX", descriptor.ValueType,
            canonical is null ? "MISSING" : "VALUE",
            canonical ?? string.Empty, 0, canonical is null ? 0 : 1,
            reportCount, rowCount, numericCount, transitionLeg,
            descriptor.CollectionSemantics);
    }

    private static StatisticReconciliationActualRawSummaryAtom DiffLegAtom(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string atomKind,
        string valueType,
        string valueState,
        string canonicalValue,
        int decimalScale,
        long occurrenceCount,
        long reportCount,
        long rowCount,
        long numericCount,
        string transitionLeg,
        string? collectionSemantics)
    {
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_RAW_SUMMARY_ATOM_V1",
            descriptor.SemanticSha256,
            descriptor.IdentitySha256,
            transitionLeg,
            descriptor.TransitionKind ?? "~",
            collectionSemantics ?? "~",
            atomKind,
            valueType,
            valueState,
            canonicalValue,
            StatisticReconciliationActualCanonical.Integer(decimalScale),
            StatisticReconciliationActualCanonical.Integer(occurrenceCount),
            StatisticReconciliationActualCanonical.Integer(reportCount),
            StatisticReconciliationActualCanonical.Integer(rowCount),
            StatisticReconciliationActualCanonical.Integer(numericCount));
        return new(
            descriptor.SemanticSha256,
            descriptor.IdentitySha256,
            descriptor.Family,
            descriptor.Kind,
            descriptor.MetricId,
            descriptor.FieldId,
            descriptor.TableId,
            descriptor.RowId,
            descriptor.LabelId,
            descriptor.BasicScope,
            descriptor.BasicScopeId,
            descriptor.AdvancedGrain,
            descriptor.DiffKind,
            descriptor.PeriodKey,
            atomKind,
            valueType,
            valueState,
            canonicalValue,
            decimalScale,
            occurrenceCount,
            reportCount,
            rowCount,
            numericCount,
            transitionLeg,
            descriptor.TransitionKind,
            collectionSemantics,
            semantic);
    }

    private static decimal DiffLegSum(IEnumerable<decimal> values)
    {
        try
        {
            decimal result = 0;
            foreach (var value in values)
                result += value;
            return result;
        }
        catch (OverflowException)
        {
            throw DiffLegInvalid();
        }
    }

    private static int DiffLegDecimalScale(string canonical)
    {
        var point = canonical.IndexOf('.');
        return point < 0 ? 0 : canonical.Length - point - 1;
    }

    private static string DiffLegValueAtomKind(string valueType)
        => valueType switch
        {
            "DATE" => "DATE",
            "FULL_DATE" => "FULL_DATE",
            "BOOLEAN" => "BOOLEAN",
            "ENUM" => "ENUM",
            "STRING_LIST" => "STRING_LIST",
            "TEXT" => "TEXT",
            _ => throw DiffLegInvalid()
        };

    private static bool DiffLegResolvePointer(
        JsonElement root,
        string pointer,
        out JsonElement value)
    {
        value = root;
        if (pointer.Length == 0)
            return true;
        if (!pointer.StartsWith("/", StringComparison.Ordinal))
            return false;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out value))
                    return false;
                continue;
            }
            if (value.ValueKind == JsonValueKind.Array &&
                int.TryParse(
                    segment, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var index) && index >= 0 &&
                (segment == "0" || !segment.StartsWith('0')) &&
                index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }
            return false;
        }
        return true;
    }

    private static StatisticReconciliationActualExtendedRawSourceIncompleteException
        DiffLegInvalid()
        => Incomplete(
            StatisticReconciliationActualExtendedRawSourceFailures
                .TypedAtomInvalid);

    private sealed record DiffLegSample(
        string State,
        string CanonicalValue,
        int DecimalScale)
    {
        internal static readonly DiffLegSample Missing =
            new("MISSING", string.Empty, 0);
        internal static readonly DiffLegSample Null =
            new("NULL", string.Empty, 0);
        internal static readonly DiffLegSample Empty =
            new("EMPTY", string.Empty, 0);
    }
}
