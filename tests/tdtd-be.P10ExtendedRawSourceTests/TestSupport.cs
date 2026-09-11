using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal static class TestSupport
{
    internal static readonly string SchemaBinding = Sha("schema-options");

    internal static StatisticReconciliationActualExtendedRawSourceTypedCompiler
        Compiler()
        => new(new StatisticReconciliationActualRawSummaryCompiler());

    internal static StatisticReconciliationActualSummaryIdentityDescriptor
        AdvancedDescriptor(
            string grain,
            string grainKey,
            string fieldId,
            string fieldKey,
            string method,
            string valueType,
            string[] operations,
            bool expandArray = false,
            bool unordered = false)
    {
        var metricId = $"{fieldKey}:{method}";
        var period = $"{grain}:{grainKey}";
        var identity = new StatisticReconciliationExpectedMetricIdentityCompiler()
            .Compile(new ExpectedMetricIdentityRequest(
                "ADVANCED", "FIELD", metricId, period, fieldId, null,
                null, null, null, null, grain, null));
        return Descriptor(
            identity,
            $"/projected/{fieldId}",
            valueType,
            operations,
            expandArray,
            unordered,
            StatisticReconciliationExpectedDiffTransitionModes.None,
            null,
            null,
            null,
            null);
    }

    internal static StatisticReconciliationActualSummaryIdentityDescriptor
        DirectDescriptor()
    {
        var identity = new StatisticReconciliationExpectedMetricIdentityCompiler()
            .Compile(new ExpectedMetricIdentityRequest(
                "DIRECT", "FIELD", "amount", "2026-08", "amount", null,
                null, null, null, null, null, null));
        return Descriptor(
            identity,
            "/fieldValues/amount",
            "NUMBER",
            ["SUM", "MIN", "MAX", "MEAN"],
            false,
            false,
            StatisticReconciliationExpectedDiffTransitionModes.None,
            null,
            null,
            null,
            null);
    }
    internal static StatisticReconciliationActualSummaryIdentityDescriptor
        DiffDescriptor(
            string kind,
            string ownerType,
            string transition,
            string direction = "LEFT_TO_RIGHT")
    {
        const string leftPeriod =
            "{\"mode\":\"EXACT\",\"periodKey\":\"2026-07\"}";
        const string rightPeriod =
            "{\"mode\":\"EXACT\",\"periodKey\":\"2026-08\"}";
        var periodBinding = H(
            "P10_ACTUAL_EXTENDED_DIFF_PERIOD_BINDING_V2",
            direction,
            RawSha(leftPeriod),
            RawSha(rightPeriod));
        var period = "P10_DIFF_PERIOD_PAIR:" + periodBinding;
        string metricId;
        string? fieldId = null;
        string? tableId = null;
        string? rowId = null;
        string? labelId = null;
        switch (kind)
        {
            case "FIELD":
                fieldId = "field-diff";
                metricId = $"FIELD:{fieldId}";
                break;
            case "TABLE_METRIC":
                tableId = "table-diff";
                metricId = "metric-diff";
                break;
            case "ROW_LABEL":
                tableId = "table-label";
                rowId = "row-label";
                labelId = "label-diff";
                metricId = $"ROW:{rowId}";
                break;
            default:
                throw new InvalidOperationException("diff kind");
        }
        var expectedKind = kind == "TABLE_METRIC" ? "TABLE" : kind;
        var identity = new StatisticReconciliationExpectedMetricIdentityCompiler()
            .Compile(new ExpectedMetricIdentityRequest(
                "DIFF", expectedKind, metricId, period, fieldId, tableId, rowId,
                labelId, null, null, null, kind));
        var valueType = ownerType switch
        {
            "NUMBER" => "NUMBER",
            "BOOLEAN" => "BOOLEAN",
            "DATE" => "DATE",
            "CHOICE" => "STRING_LIST",
            "TEXT" => "TEXT",
            _ => throw new InvalidOperationException("owner type")
        };
        var operations = ownerType switch
        {
            "NUMBER" => new[] { "SUM" },
            "DATE" => new[] { "MIN" },
            _ => new[] { "VALUES" }
        };
        var difference = ownerType == "NUMBER" ? "SUBTRACT" : "CHANGED";
        return Descriptor(
            identity,
            "/fieldValues/values/value",
            valueType,
            operations,
            false,
            false,
            StatisticReconciliationExpectedDiffTransitionModes
                .BeforeAfterDifference,
            "/pair/before",
            "/pair/after",
            difference,
            transition);
    }

    internal static StatisticReconciliationActualSummaryPlanBinding Plan(
        params StatisticReconciliationActualSummaryIdentityDescriptor[] values)
    {
        var descriptors = values
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var binding = new StatisticReconciliationExpectedGenerationBinding(
            "extended-reconciliation",
            Sha("generation-id"),
            Sha("generation-semantic"),
            Sha("metric-plan"),
            descriptors.Length,
            Sha("expected-manifest"),
            Math.Max(1, descriptors.Length),
            Sha("expected-membership"),
            "NON_FLOW");
        return StatisticReconciliationActualSummaryPlanBinding.Create(
            binding, descriptors);
    }

    internal static StatisticReconciliationActualExtendedAdvancedDescriptorProjection
        Projection(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            string fieldKey,
            string rawType,
            params (string Code, string Label)[] options)
    {
        var exactOptions = options
            .OrderBy(value => value.Code, StringComparer.Ordinal)
            .Select(value => new
                StatisticReconciliationActualExtendedAdvancedOptionProjection(
                    value.Code,
                    value.Label,
                    H("P10_ACTUAL_EXTENDED_ADVANCED_OPTION_V1",
                        value.Code, value.Label)))
            .ToImmutableArray();
        var optionManifest = Hs(
            "P10_ACTUAL_EXTENDED_ADVANCED_OPTION_MANIFEST_V1",
            exactOptions.Select(value => value.OptionSemanticSha256));
        var operation = descriptor.MetricId[(fieldKey.Length + 1)..];
        var actualType = rawType switch
        {
            "number" => "NUMBER",
            "date" or "fullDate" => "DATE",
            "boolean" => "BOOLEAN",
            "singleSelect" or "multiSelect" or "stringList" or
                "richText" => "CHOICE",
            _ => "TEXT"
        };
        var pin = $"DYNAMIC_FORM_FIELD:form:section:{descriptor.FieldId}:" +
            $"{actualType}:{Sha($"field-{descriptor.FieldId}-{rawType}")}";
        var semantic = H(
            "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_PROJECTION_V1",
            descriptor.SemanticSha256,
            descriptor.IdentitySha256,
            descriptor.FieldId,
            fieldKey,
            rawType,
            actualType,
            operation,
            optionManifest,
            pin);
        return new(
            descriptor.SemanticSha256,
            descriptor.IdentitySha256,
            descriptor.FieldId!,
            fieldKey,
            rawType,
            exactOptions,
            optionManifest,
            pin,
            semantic);
    }

    internal static StatisticReconciliationActualExtendedRawSourceEnvelope
        AdvancedEnvelope(
            int ordinal,
            string grain,
            string grainKey,
            string reportId,
            string fieldValuesJson)
        => StatisticReconciliationActualExtendedRawSourceIntegrity.Envelope(
            "ADVANCED",
            null,
            grain,
            grainKey,
            ordinal,
            "work",
            "assignment",
            reportId,
            $"payload-{reportId}",
            1,
            Sha($"payload-owner-{reportId}"),
            Canonical($"{{\"fieldValues\":{fieldValuesJson}}}"),
            1,
            Sha($"lifecycle-{reportId}"),
            null,
            null,
            null);

    internal static (
        ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin> Pins,
        ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
            Envelopes)
        DiffSide(string side, params string[] valuesJson)
        => DiffSideAt(0, side, valuesJson);

    internal static (
        ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin> Pins,
        ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
            Envelopes)
        DiffSideAt(int ordinalOffset, string side, params string[] valuesJson)
    {
        var pins = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualExtendedDiffSourcePin>();
        var envelopes = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualExtendedRawSourceEnvelope>();
        for (var index = 0; index < valuesJson.Length; index++)
        {
            var report = $"{side.ToLowerInvariant()}-report-{index}";
            var owner = Sha($"{side}-payload-{index}");
            var generation = Sha($"{side}-generation-{index}");
            pins.Add(
                StatisticReconciliationActualExtendedRawSourceIntegrity.Pin(
                    side,
                    report,
                    1,
                    owner,
                    1,
                    "direct-run",
                    $"{side.ToLowerInvariant()}-generation-{index}",
                    generation));
            envelopes.Add(
                StatisticReconciliationActualExtendedRawSourceIntegrity.Envelope(
                    "DIFF",
                    side,
                    null,
                    null,
                    checked(ordinalOffset + index),
                    "work",
                    "assignment",
                    report,
                    $"payload-{report}",
                    1,
                    owner,
                    Canonical(
                        "{\"fieldValues\":{\"values\":{\"value\":" +
                        valuesJson[index] + "}}}"),
                    1,
                    Sha($"{side}-lifecycle-{index}"),
                    "direct-run",
                    $"{side.ToLowerInvariant()}-generation-{index}",
                    generation));
        }
        return (pins.ToImmutable(), envelopes.ToImmutable());
    }

    internal static object InvokePrivate(
        Type type,
        string name,
        params object?[] arguments)
    {
        var method = type.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"missing {name}");
        try
        {
            return method.Invoke(null, arguments)
                ?? throw new InvalidOperationException($"null {name}");
        }
        catch (TargetInvocationException exception)
        {
            throw exception.InnerException ?? exception;
        }
    }

    internal static void Equal<T>(T expected, T actual, string id)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{id}: expected {expected}, got {actual}");
        Console.WriteLine($"PASS {id}");
    }

    internal static void Throws(string id, Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            Console.WriteLine($"PASS {id}");
            return;
        }
        throw new InvalidOperationException($"{id}: expected failure");
    }

    internal static string Canonical(string json)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            json, "EXTENDED_TEST_JSON");
        return StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
    }

    internal static string H(string domain, params string?[] values)
        => StatisticReconciliationActualCanonical.Hash(domain, values);
    internal static string Hs(string domain, IEnumerable<string> values)
        => StatisticReconciliationActualCanonical.HashSequence(domain, values);
    internal static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);
    internal static string RawSha(string value)
        => StatisticReconciliationActualJson.RawSha256(value);
    internal static string Instant(DateTime value)
        => StatisticReconciliationActualCanonical.Instant(value);
    internal static string Sha(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static StatisticReconciliationActualSummaryIdentityDescriptor
        Descriptor(
            StatisticReconciliationExpectedMetricIdentity identity,
            string pointer,
            string valueType,
            string[] operations,
            bool expandArray,
            bool unordered,
            string transitionMode,
            string? before,
            string? after,
            string? difference,
            string? transition)
    {
        var exactOperations = operations
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        var entrySha = StatisticReconciliationExpectedMetricPlanIntegrity
            .BuildEntrySha256(
                identity,
                pointer,
                valueType,
                unordered,
                expandArray,
                exactOperations,
                transitionMode,
                before,
                after,
                difference,
                transition);
        var legs = transitionMode ==
            StatisticReconciliationExpectedDiffTransitionModes.None
            ? ImmutableArray.Create("NONE")
            : ImmutableArray.Create(
                "BEFORE", "AFTER", "CHANGE_STATE", "DELTA");
        var valueFree =
            new StatisticReconciliationExpectedValueFreeMetricPlanDescriptor(
                StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2,
                identity,
                pointer,
                valueType,
                unordered,
                valueType == "STRING_LIST"
                    ? unordered ? "UNORDERED" : "ORDERED"
                    : null,
                expandArray,
                exactOperations,
                transitionMode,
                before,
                after,
                difference,
                transition,
                legs,
                entrySha);
        return StatisticReconciliationActualSummaryIdentityDescriptor.Create(
            valueFree);
    }
}
