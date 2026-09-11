using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Actual-side raw compiler.  This deliberately does not call the expected
/// compiler and receives no expected atom/value.  The only shared shape is the
/// value-free, integrity-bound metric descriptor.
/// </summary>
internal sealed class StatisticReconciliationActualRawSummaryCompiler
    : IStatisticReconciliationActualRawSummaryCompiler
{
    private const long MaximumEvaluations = 5_000_000;
    private const int MaximumAtoms = 80_000;

    public ImmutableArray<StatisticReconciliationActualRawSummaryAtom> Compile(
        StatisticReconciliationActualSummaryPlanBinding exactPlan,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources)
    {
        var plan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
            exactPlan ?? throw new ArgumentNullException(nameof(exactPlan)));
        if (sources.IsDefault || sources.Length >
            ActualSourceMembershipAdapterLimits.MaxSourceCandidates)
            throw Fail("RAW_SOURCE_CARDINALITY_INVALID");
        if ((long)plan.IdentityDescriptors.Length *
            Math.Max(1, sources.Length) > MaximumEvaluations)
            throw Fail("RAW_EVALUATION_BUDGET_EXCEEDED");

        var documents = new List<JsonDocument>(sources.Length);
        try
        {
            foreach (var source in sources)
            {
                var document = StatisticReconciliationActualJson.ParseStrict(
                    source.CanonicalPayloadJson,
                    "RAW_SUMMARY_PAYLOAD_JSON");
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw Fail("RAW_PAYLOAD_OBJECT_REQUIRED");
                documents.Add(document);
            }

            var atoms = ImmutableArray.CreateBuilder<
                StatisticReconciliationActualRawSummaryAtom>();
            foreach (var rawDescriptor in plan.IdentityDescriptors)
            {
                var descriptor =
                    StatisticReconciliationActualSummaryIdentityDescriptor
                        .Normalize(rawDescriptor);
                var compiled = CompileMetric(descriptor, documents);
                foreach (var atom in compiled)
                {
                    if (atoms.Count == MaximumAtoms)
                        throw Fail("RAW_ATOM_CARDINALITY_EXCEEDED");
                    atoms.Add(atom);
                }
                RequireDescriptorPartition(descriptor, compiled);
            }

            var ordered = atoms
                .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
                .ThenBy(value => value.TransitionLeg, StringComparer.Ordinal)
                .ThenBy(value => value.AtomKind, StringComparer.Ordinal)
                .ThenBy(value => value.CanonicalValue, StringComparer.Ordinal)
                .ToImmutableArray();
            if (ordered.Select(Key).Distinct(StringComparer.Ordinal).Count() !=
                ordered.Length)
                throw Fail("RAW_ATOM_DUPLICATE");
            return ordered;
        }
        finally
        {
            foreach (var document in documents)
                document.Dispose();
        }
    }

    private static ImmutableArray<StatisticReconciliationActualRawSummaryAtom>
        CompileMetric(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            IReadOnlyList<JsonDocument> payloads)
    {
        var atoms = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualRawSummaryAtom>();
        if (descriptor.Family == "DIFF")
        {
            atoms.AddRange(CompileLeg(
                descriptor, payloads, descriptor.BeforeJsonPointer!, "BEFORE"));
            atoms.AddRange(CompileLeg(
                descriptor, payloads, descriptor.AfterJsonPointer!, "AFTER"));
            atoms.AddRange(CompileTransition(descriptor, payloads));
        }
        else
        {
            atoms.AddRange(CompileLeg(
                descriptor, payloads, descriptor.JsonPointer, "NONE"));
        }
        return atoms.ToImmutable();
    }

    private static ImmutableArray<StatisticReconciliationActualRawSummaryAtom>
        CompileLeg(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            IReadOnlyList<JsonDocument> payloads,
            string pointer,
            string transitionLeg)
    {
        var samples = new List<RawSample>();
        long rowCount = 0;
        foreach (var payload in payloads)
        {
            if (!TryResolvePointer(payload.RootElement, pointer, out var value))
            {
                samples.Add(RawSample.Missing);
                continue;
            }
            if (descriptor.ExpandArray && value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() == 0)
                {
                    samples.Add(RawSample.Empty);
                    continue;
                }
                rowCount = CheckedAdd(
                    rowCount, value.GetArrayLength(), "RAW_ROW_COUNT_OVERFLOW");
                foreach (var item in value.EnumerateArray())
                    samples.Add(NormalizeSample(item, descriptor));
                continue;
            }
            samples.Add(NormalizeSample(value, descriptor));
        }

        var reportCount = (long)payloads.Count;
        var valueSamples = samples
            .Where(value => value.State == "VALUE")
            .ToArray();
        var numeric = descriptor.ValueType == "NUMBER"
            ? valueSamples.Select(value => decimal.Parse(
                    value.CanonicalValue,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture))
                .ToArray()
            : [];
        var numericCount = (long)numeric.Length;
        var atoms = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualRawSummaryAtom>();
        atoms.Add(Count(descriptor, "REPORT_COUNT", reportCount,
            reportCount, rowCount, numericCount, transitionLeg));
        atoms.Add(Count(descriptor, "ROW_COUNT", rowCount,
            reportCount, rowCount, numericCount, transitionLeg));
        atoms.Add(Count(descriptor, "COUNT", valueSamples.LongLength,
            reportCount, rowCount, numericCount, transitionLeg));
        atoms.Add(Count(descriptor, "NUMERIC_VALUE_COUNT", numericCount,
            reportCount, rowCount, numericCount, transitionLeg));
        atoms.Add(State(descriptor, "MISSING",
            samples.LongCount(value => value.State == "MISSING"),
            reportCount, rowCount, numericCount, transitionLeg));
        atoms.Add(State(descriptor, "NULL",
            samples.LongCount(value => value.State == "NULL"),
            reportCount, rowCount, numericCount, transitionLeg));
        atoms.Add(State(descriptor, "EMPTY",
            samples.LongCount(value => value.State == "EMPTY"),
            reportCount, rowCount, numericCount, transitionLeg));

        decimal? sum = null;
        if (numeric.Length > 0 &&
            (descriptor.Operations.Contains("SUM") ||
             descriptor.Operations.Contains("MEAN")))
            sum = Sum(numeric);
        if (descriptor.Operations.Contains("SUM"))
            atoms.Add(Aggregate(descriptor, "SUM", sum,
                reportCount, rowCount, numericCount, transitionLeg));
        if (descriptor.Operations.Contains("MIN"))
            atoms.Add(MinMax(descriptor, minimum: true, valueSamples, numeric,
                reportCount, rowCount, numericCount, transitionLeg));
        if (descriptor.Operations.Contains("MAX"))
            atoms.Add(MinMax(descriptor, minimum: false, valueSamples, numeric,
                reportCount, rowCount, numericCount, transitionLeg));
        if (descriptor.Operations.Contains("MEAN"))
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
                    throw Fail("RAW_MEAN_OVERFLOW");
                }
            }
            atoms.Add(Aggregate(descriptor, "MEAN", mean,
                reportCount, rowCount, numericCount, transitionLeg));
        }

        if (descriptor.Operations.Contains("VALUES") &&
            descriptor.ValueType != "NUMBER")
        {
            var kind = ValueAtomKind(descriptor.ValueType);
            foreach (var group in valueSamples
                         .GroupBy(value => value.CanonicalValue,
                             StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                atoms.Add(Atom(
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
                    descriptor.TransitionKind,
                    descriptor.CollectionSemantics));
            }
        }
        return atoms.ToImmutable();
    }

    private static ImmutableArray<StatisticReconciliationActualRawSummaryAtom>
        CompileTransition(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            IReadOnlyList<JsonDocument> payloads)
    {
        long added = 0;
        long removed = 0;
        long changed = 0;
        long unchanged = 0;
        long numericPairs = 0;
        decimal delta = 0;
        foreach (var payload in payloads)
        {
            var before = ResolveTransition(
                payload.RootElement, descriptor.BeforeJsonPointer!, descriptor);
            var after = ResolveTransition(
                payload.RootElement, descriptor.AfterJsonPointer!, descriptor);
            if (before.State == "MISSING" && after.State != "MISSING")
                added++;
            else if (before.State != "MISSING" && after.State == "MISSING")
                removed++;
            else if (before != after)
                changed++;
            else
                unchanged++;

            if (descriptor.DifferenceOperation == "SUBTRACT" &&
                before.State == "VALUE" && after.State == "VALUE")
            {
                try
                {
                    delta += decimal.Parse(after.CanonicalValue,
                                 NumberStyles.AllowLeadingSign |
                                 NumberStyles.AllowDecimalPoint,
                                 CultureInfo.InvariantCulture) -
                             decimal.Parse(before.CanonicalValue,
                                 NumberStyles.AllowLeadingSign |
                                 NumberStyles.AllowDecimalPoint,
                                 CultureInfo.InvariantCulture);
                }
                catch (OverflowException)
                {
                    throw Fail("RAW_DIFFERENCE_OVERFLOW");
                }
                numericPairs++;
            }
        }

        var observed = added > 0 && removed == 0 && changed == 0
            ? "ADDED"
            : removed > 0 && added == 0 && changed == 0
                ? "REMOVED"
                : "CHANGED";
        if (descriptor.TransitionKind != observed)
            throw Fail("RAW_TRANSITION_KIND_PLAN_MISMATCH");

        var atoms = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualRawSummaryAtom>();
        atoms.Add(Atom(descriptor, "TRANSITION_KIND", "TEXT", "VALUE",
            observed, 0, 1, payloads.Count, 0, numericPairs,
            "CHANGE_STATE", observed, null));
        foreach (var pair in new[]
                 {
                     (Kind: "ADDED_COUNT", Value: added),
                     (Kind: "REMOVED_COUNT", Value: removed),
                     (Kind: "CHANGED_COUNT", Value: changed),
                     (Kind: "UNCHANGED_COUNT", Value: unchanged)
                 })
        {
            atoms.Add(Atom(descriptor, pair.Kind, "NUMBER", "VALUE",
                I(pair.Value), 0, 1, payloads.Count, 0, numericPairs,
                "CHANGE_STATE", observed, null));
        }
        if (descriptor.DifferenceOperation == "SUBTRACT")
        {
            var hasValue = numericPairs > 0;
            var canonical = hasValue
                ? StatisticReconciliationActualCanonical.Number(delta)
                : string.Empty;
            atoms.Add(Atom(descriptor, "DIFFERENCE", "NUMBER",
                hasValue ? "VALUE" : "MISSING",
                canonical,
                hasValue ? DecimalScale(canonical) : 0,
                hasValue ? 1 : 0,
                payloads.Count,
                0,
                numericPairs,
                "DELTA",
                observed,
                null));
        }
        return atoms.ToImmutable();
    }

    private static StatisticReconciliationActualRawSummaryAtom Count(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string kind,
        long value,
        long reportCount,
        long rowCount,
        long numericCount,
        string transitionLeg)
        => Atom(descriptor, kind, "NUMBER", "VALUE", I(value), 0, 1,
            reportCount, rowCount, numericCount, transitionLeg,
            descriptor.TransitionKind, null);

    private static StatisticReconciliationActualRawSummaryAtom State(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string kind,
        long value,
        long reportCount,
        long rowCount,
        long numericCount,
        string transitionLeg)
        => Atom(descriptor, kind, descriptor.ValueType, kind, I(value), 0,
            value, reportCount, rowCount, numericCount, transitionLeg,
            descriptor.TransitionKind, descriptor.CollectionSemantics);

    private static StatisticReconciliationActualRawSummaryAtom Aggregate(
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
        return Atom(descriptor, kind, "NUMBER",
            value.HasValue ? "VALUE" : "MISSING",
            canonical,
            value.HasValue ? DecimalScale(canonical) : 0,
            value.HasValue ? 1 : 0,
            reportCount, rowCount, numericCount, transitionLeg,
            descriptor.TransitionKind, null);
    }

    private static StatisticReconciliationActualRawSummaryAtom MinMax(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        bool minimum,
        IReadOnlyList<RawSample> values,
        IReadOnlyList<decimal> numeric,
        long reportCount,
        long rowCount,
        long numericCount,
        string transitionLeg)
    {
        if (descriptor.ValueType == "NUMBER")
        {
            decimal? value = numeric.Count == 0
                ? null
                : minimum ? numeric.Min() : numeric.Max();
            return Aggregate(descriptor, minimum ? "MIN" : "MAX", value,
                reportCount, rowCount, numericCount, transitionLeg);
        }
        var canonical = values.Count == 0
            ? null
            : minimum
                ? values.MinBy(value => value.CanonicalValue,
                    StringComparer.Ordinal)!.CanonicalValue
                : values.MaxBy(value => value.CanonicalValue,
                    StringComparer.Ordinal)!.CanonicalValue;
        return Atom(descriptor, minimum ? "MIN" : "MAX",
            descriptor.ValueType,
            canonical is null ? "MISSING" : "VALUE",
            canonical ?? string.Empty,
            0,
            canonical is null ? 0 : 1,
            reportCount, rowCount, numericCount, transitionLeg,
            descriptor.TransitionKind, descriptor.CollectionSemantics);
    }

    private static StatisticReconciliationActualRawSummaryAtom Atom(
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
        string? transitionKind,
        string? collectionSemantics)
    {
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_RAW_SUMMARY_ATOM_V1",
            descriptor.SemanticSha256,
            descriptor.IdentitySha256,
            transitionLeg,
            transitionKind ?? "~",
            collectionSemantics ?? "~",
            atomKind,
            valueType,
            valueState,
            canonicalValue,
            I(decimalScale),
            I(occurrenceCount),
            I(reportCount),
            I(rowCount),
            I(numericCount));
        return new StatisticReconciliationActualRawSummaryAtom(
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
            transitionKind,
            collectionSemantics,
            semantic);
    }

    private static RawSample ResolveTransition(
        JsonElement root,
        string pointer,
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor)
        => TryResolvePointer(root, pointer, out var value)
            ? NormalizeSample(value, descriptor)
            : RawSample.Missing;

    private static RawSample NormalizeSample(
        JsonElement value,
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return RawSample.Null;
        if (value.ValueKind == JsonValueKind.String &&
            value.GetString()!.Length == 0)
            return RawSample.Empty;
        if (value.ValueKind == JsonValueKind.Array &&
            value.GetArrayLength() == 0)
            return RawSample.Empty;
        return descriptor.ValueType switch
        {
            "NUMBER" => Number(value),
            "BUCKET" => Bucket(value),
            "DATE" => Date(value, fullDate: false),
            "FULL_DATE" => Date(value, fullDate: true),
            "PERIOD" => Period(value),
            "BOOLEAN" => Boolean(value),
            "ENUM" => String(value, "RAW_ENUM"),
            "STRING_LIST" => StringList(value, descriptor.Unordered),
            "TEXT" => String(value, "RAW_TEXT"),
            _ => throw Fail("RAW_VALUE_TYPE_INVALID")
        };
    }

    private static RawSample Number(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out var number))
            throw Fail("RAW_NUMBER_INVALID");
        var canonical = StatisticReconciliationActualCanonical.Number(number);
        return new RawSample("VALUE", canonical, DecimalScale(canonical));
    }

    private static RawSample Bucket(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return new RawSample("VALUE", "S:" + value.GetString(), 0);
        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDecimal(out var number))
        {
            var canonical = StatisticReconciliationActualCanonical.Number(number);
            return new RawSample("VALUE", "N:" + canonical,
                DecimalScale(canonical));
        }
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return new RawSample("VALUE",
                value.ValueKind == JsonValueKind.True ? "B:true" : "B:false", 0);
        throw Fail("RAW_BUCKET_INVALID");
    }

    private static RawSample Boolean(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.True => new RawSample("VALUE", "true", 0),
            JsonValueKind.False => new RawSample("VALUE", "false", 0),
            _ => throw Fail("RAW_BOOLEAN_INVALID")
        };

    private static RawSample String(JsonElement value, string reason)
        => value.ValueKind == JsonValueKind.String
            ? new RawSample("VALUE", value.GetString()!, 0)
            : throw Fail(reason);

    private static RawSample StringList(JsonElement value, bool unordered)
    {
        if (value.ValueKind != JsonValueKind.Array ||
            value.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String))
            throw Fail("RAW_STRING_LIST_INVALID");
        IEnumerable<string> items = value.EnumerateArray()
            .Select(item => item.GetString()!);
        if (unordered)
            items = items.Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal);
        return new RawSample("VALUE", JsonSerializer.Serialize(items.ToArray()), 0);
    }

    private static RawSample Date(JsonElement value, bool fullDate)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Fail("RAW_DATE_INVALID");
        var raw = value.GetString()!;
        string canonical;
        if (fullDate && DateTime.TryParseExact(raw, "dd/MM/yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var full))
            canonical = "DAY:" + full.ToString("yyyy-MM-dd",
                CultureInfo.InvariantCulture);
        else if (!fullDate && DateTime.TryParseExact(raw, "yyyy",
                     CultureInfo.InvariantCulture, DateTimeStyles.None,
                     out var year))
            canonical = "YEAR:" + year.ToString("yyyy",
                CultureInfo.InvariantCulture);
        else if (!fullDate && DateTime.TryParseExact(raw, "MM/yyyy",
                     CultureInfo.InvariantCulture, DateTimeStyles.None,
                     out var month))
            canonical = "MONTH:" + month.ToString("yyyy-MM",
                CultureInfo.InvariantCulture);
        else if (!fullDate && DateTime.TryParseExact(raw, "dd/MM/yyyy",
                     CultureInfo.InvariantCulture, DateTimeStyles.None,
                     out var day))
            canonical = "DAY:" + day.ToString("yyyy-MM-dd",
                CultureInfo.InvariantCulture);
        else
            throw Fail("RAW_DATE_INVALID");
        return new RawSample("VALUE", canonical, 0);
    }

    private static RawSample Period(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Fail("RAW_PERIOD_INVALID");
        var raw = value.GetString()!;
        var valid = raw.StartsWith("YEAR:", StringComparison.Ordinal)
            ? DateTime.TryParseExact(raw[5..], "yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)
            : raw.StartsWith("MONTH:", StringComparison.Ordinal)
                ? DateTime.TryParseExact(raw[6..], "yyyy-MM",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                : raw.StartsWith("DAY:", StringComparison.Ordinal) &&
                  DateTime.TryParseExact(raw[4..], "yyyy-MM-dd",
                      CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        if (!valid)
            throw Fail("RAW_PERIOD_INVALID");
        return new RawSample("VALUE", raw, 0);
    }

    private static bool TryResolvePointer(
        JsonElement root,
        string pointer,
        out JsonElement value)
    {
        value = root;
        if (pointer.Length == 0)
            return true;
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
                int.TryParse(segment, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var index) &&
                index >= 0 && (segment == "0" || !segment.StartsWith('0')) &&
                index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }
            return false;
        }
        return true;
    }

    private static void RequireDescriptorPartition(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms)
    {
        if (atoms.Length == 0 || atoms.Any(value =>
                value.DescriptorSemanticSha256 != descriptor.SemanticSha256 ||
                value.IdentitySha256 != descriptor.IdentitySha256))
            throw Fail("RAW_DESCRIPTOR_PARTITION_INVALID");
        var actual = atoms.Select(value => value.AtomKind)
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (!actual.IsSubsetOf(descriptor.AtomKinds.ToImmutableHashSet(
                StringComparer.Ordinal)))
            throw Fail("RAW_DESCRIPTOR_ATOM_KIND_UNDECLARED");
        foreach (var required in new[]
                 {
                     "REPORT_COUNT", "ROW_COUNT", "COUNT",
                     "NUMERIC_VALUE_COUNT", "MISSING", "NULL", "EMPTY"
                 })
        {
            var legs = descriptor.Family == "DIFF"
                ? new[] { "BEFORE", "AFTER" }
                : new[] { "NONE" };
            foreach (var leg in legs)
                if (atoms.Count(value => value.AtomKind == required &&
                        value.TransitionLeg == leg) != 1)
                    throw Fail("RAW_DESCRIPTOR_MANDATORY_ATOM_MISSING");
        }
    }

    private static string ValueAtomKind(string valueType)
        => valueType switch
        {
            "BUCKET" => "BUCKET",
            "DATE" => "DATE",
            "FULL_DATE" => "FULL_DATE",
            "PERIOD" => "PERIOD",
            "BOOLEAN" => "BOOLEAN",
            "ENUM" => "ENUM",
            "STRING_LIST" => "STRING_LIST",
            "TEXT" => "TEXT",
            _ => throw Fail("RAW_VALUES_OPERATION_TYPE_INVALID")
        };

    private static decimal Sum(IEnumerable<decimal> values)
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
            throw Fail("RAW_SUM_OVERFLOW");
        }
    }

    private static long CheckedAdd(long left, long right, string reason)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            throw Fail(reason);
        }
    }

    private static int DecimalScale(string canonical)
    {
        var point = canonical.IndexOf('.');
        return point < 0 ? 0 : canonical.Length - point - 1;
    }

    private static string Key(StatisticReconciliationActualRawSummaryAtom value)
        => string.Join('\u001f',
            value.IdentitySha256,
            value.TransitionLeg,
            value.AtomKind,
            value.ValueType,
            value.ValueState,
            value.CanonicalValue);

    private static string I(long value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_RAW_SUMMARY:{reason}");

    private sealed record RawSample(
        string State,
        string CanonicalValue,
        int DecimalScale)
    {
        internal static readonly RawSample Missing = new("MISSING", string.Empty, 0);
        internal static readonly RawSample Null = new("NULL", string.Empty, 0);
        internal static readonly RawSample Empty = new("EMPTY", string.Empty, 0);
    }

    private static class ActualSourceMembershipAdapterLimits
    {
        internal const int MaxSourceCandidates = 100_000;
    }
}
