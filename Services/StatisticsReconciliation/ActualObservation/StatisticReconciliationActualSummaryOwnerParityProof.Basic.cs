using System.Collections.Immutable;
using System.Text.Json;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static partial class StatisticReconciliationActualSummaryOwnerParity
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false
        };

    private static readonly ImmutableHashSet<string> BasicFieldProperties =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "targetKind", "targetKey", "fieldId", "fieldKey", "blockId",
            "tableMode", "metricKey", "rowKey", "columnKey", "index",
            "label", "dataType", "operation", "value", "valueCount",
            "reportCount", "sum", "min", "max", "mean", "trueCount",
            "falseCount", "minDateUtc", "maxDateUtc", "text",
            "textCharCount", "textTruncated", "buckets");

    private static OwnerDigest OwnerManifest(
        string family,
        StatisticReconciliationActualSummaryOwnerParityActual actual)
        => family switch
        {
            "BASIC" when actual.Basic is not null => new OwnerDigest(
                Hash("P10_ACTUAL_SUMMARY_OWNER_BASIC_MANIFEST_V1",
                    actual.Basic.CaptureSemanticSha256,
                    actual.Basic.RequestRawSha256,
                    actual.Basic.RequestCanonicalSha256,
                    actual.Basic.SnapshotRawSha256,
                    actual.Basic.SnapshotCanonicalSha256,
                    actual.Basic.OwnerState.OwnerLifecycleSemanticSha256,
                    HashSequence("P10_ACTUAL_SUMMARY_OWNER_BASIC_ITEMS_V1",
                        actual.Basic.ResultItems.Select(value =>
                            value.SemanticSha256))),
                actual.Basic.ResultItems.IsDefault
                    ? 0 : actual.Basic.ResultItems.Length),
            "ADVANCED" when actual.Advanced is not null => new OwnerDigest(
                Hash("P10_ACTUAL_SUMMARY_OWNER_ADVANCED_MANIFEST_V1",
                    actual.Advanced.CaptureSemanticSha256,
                    HashSequence(
                        "P10_ACTUAL_SUMMARY_OWNER_ADVANCED_NODES_V1",
                        actual.Advanced.Nodes.IsDefault
                            ? []
                            : actual.Advanced.Nodes.Select(value =>
                                value.SemanticSha256))),
                actual.Advanced.Nodes.IsDefault
                    ? 0
                    : actual.Advanced.Nodes.Sum(value =>
                        value.Value?.Fields.Length ?? 0)),
            "DIFF" when actual.Diff is not null => new OwnerDigest(
                Hash("P10_ACTUAL_SUMMARY_OWNER_DIFF_MANIFEST_V1",
                    actual.Diff.CaptureSemanticSha256,
                    actual.Diff.ObservedResultSha256,
                    HashSequence("P10_ACTUAL_SUMMARY_OWNER_DIFF_ROWS_V1",
                        actual.Diff.Rows.IsDefault
                            ? []
                            : actual.Diff.Rows.Select(value =>
                                value.SemanticSha256))),
                actual.Diff.Rows.IsDefault ? 0 : actual.Diff.Rows.Length),
            _ => new OwnerDigest(Hash(
                "P10_ACTUAL_SUMMARY_OWNER_ABSENT_MANIFEST_V1", family), 0)
        };

    private static ImmutableArray<string> ProveBasic(
        ImmutableArray<StatisticReconciliationActualSummaryIdentityDescriptor>
            descriptors,
        ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources,
        ActualBasicResultObservation owner)
    {
        ValidateBasicCaptureRoot(owner);
        if (owner.ResultItems.IsDefault ||
            !owner.OwnerState.OwnerCompleteAndClean ||
            !owner.OwnerState.InnerMetaMatchesOuter ||
            !owner.OwnerState.SourceIdsUnique)
            throw Fail("BASIC_OWNER_STATE_INCOMPLETE");
        var view = ValidateBasicJson(owner);
        RequireBasicRawSourceIdentity(atoms, sources, owner);
        var items = view.Items;
        if (items.Length != descriptors.Length ||
            items.Select(value => value.Dto.TargetKey)
                .Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw Fail("BASIC_OWNER_PARTITION_CARDINALITY");

        var relations = ImmutableArray.CreateBuilder<string>(descriptors.Length);
        var consumed = new HashSet<int>();
        foreach (var descriptor in descriptors)
        {
            ValidateBasicDescriptorBoundary(descriptor, view.Period, owner);
            var matches = items.Select((value, index) => (value, index))
                .Where(value => BasicItemMatchesDescriptor(
                    value.value, descriptor)).ToArray();
            if (matches.Length != 1 || !consumed.Add(matches[0].index))
                throw Fail("BASIC_DESCRIPTOR_OWNER_BIJECTION");
            var partition = atoms.Where(value =>
                value.IdentitySha256 == descriptor.IdentitySha256).ToArray();
            relations.Add(ProveBasicLogicalItem(descriptor, partition,
                matches[0].value, sources, owner, view.MaxTextChars));
        }
        if (consumed.Count != items.Length)
            throw Fail("BASIC_DESCRIPTOR_OWNER_BIJECTION");
        return relations.MoveToImmutable();
    }

    private static BasicOwnerView ValidateBasicJson(
        ActualBasicResultObservation owner)
        => ReadBasicOwnerView(owner);

    private static ActualBasicPayloadItemObservation Item(
        string section,
        int ordinal,
        JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Fail("BASIC_OWNER_ITEM_OBJECT_REQUIRED");
        var canonical = StatisticReconciliationActualJson.Canonicalize(value);
        var identity = FirstString(value, "fieldKey", "targetKey", "b",
            "blockId", "metricKey") ?? $"ordinal:{ordinal}";
        return new ActualBasicPayloadItemObservation(section, ordinal, identity,
            canonical, Hash("P10_ACTUAL_BASIC_RESULT_ITEM_V1", section,
                I(ordinal), identity,
                StatisticReconciliationActualJson.RawSha256(canonical)));
    }

    private static BasicItem ParseBasicItem(
        ActualBasicPayloadItemObservation observation)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            observation.CanonicalJson, "SUMMARY_PARITY_BASIC_ITEM");
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            StatisticReconciliationActualJson.Canonicalize(
                document.RootElement) != observation.CanonicalJson ||
            document.RootElement.EnumerateObject().Any(value =>
                !BasicFieldProperties.Contains(value.Name)))
            throw Fail("BASIC_OWNER_ITEM_SCHEMA_INVALID");
        var dto = JsonSerializer.Deserialize<WorkAssignmentBasicSummaryItemDto>(
            observation.CanonicalJson, WebJson)
            ?? throw Fail("BASIC_OWNER_ITEM_DESERIALIZE_FAILED");
        if (string.IsNullOrEmpty(dto.TargetKey) || string.IsNullOrEmpty(dto.Label) ||
            string.IsNullOrEmpty(dto.DataType) ||
            string.IsNullOrEmpty(dto.Operation) || dto.ValueCount < 0 ||
            dto.ReportCount < 0 || dto.Buckets is null)
            throw Fail("BASIC_OWNER_ITEM_REQUIRED_FIELD_INVALID");
        JsonElement? valueElement = document.RootElement.TryGetProperty(
            "value", out var value) ? value.Clone() : null;
        return new BasicItem(observation, dto, valueElement);
    }

    private static bool BasicValue(
        JsonElement? value,
        string operation,
        long count,
        decimal? sum,
        decimal? min,
        decimal? max,
        decimal? mean)
    {
        decimal? expected = operation switch
        {
            "COUNT" => count,
            "SUM" => sum,
            "MIN" => min,
            "MAX" => max,
            "MEAN" => mean,
            _ => throw Fail("BASIC_OPERATION_UNSUPPORTED")
        };
        if (!expected.HasValue)
            return value is null || value.Value.ValueKind == JsonValueKind.Null;
        return value is { ValueKind: JsonValueKind.Number } &&
            value.Value.TryGetDecimal(out var found) && found == expected.Value;
    }

    private static (long ReportCount, long AssignmentCount) BasicRawSources(
        ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms)
    {
        var reportCounts = atoms.Where(value => value.TransitionLeg == "NONE" &&
                value.AtomKind == "REPORT_COUNT")
            .Select(value => value.ReportCount).Distinct().ToArray();
        if (reportCounts.Length != 1)
            throw Fail("BASIC_RAW_SOURCE_COUNT_AMBIGUOUS");
        return (reportCounts[0], reportCounts[0]);
    }

    private static string BasicScopeId(ActualBasicOwnerBoundary boundary)
        => boundary.SourceScopeMode switch
        {
            "DIRECT_CHILDREN_OR_SELF" or "DIRECT_CHILDREN" =>
                boundary.ScopeAssignmentId,
            "FLOW_BRANCH" => boundary.SourceFlowBranchId ??
                throw Fail("BASIC_FLOW_BRANCH_REQUIRED"),
            "FLOW_STEP" => boundary.SourceFlowStepId ??
                throw Fail("BASIC_FLOW_STEP_REQUIRED"),
            "FLOW_EFFECTIVE_PATH" or "FLOW_FINAL" =>
                boundary.SourceFlowInstanceId ??
                throw Fail("BASIC_FLOW_INSTANCE_REQUIRED"),
            _ => throw Fail("BASIC_SCOPE_UNSUPPORTED")
        };

    private static bool PeriodMatches(
        string descriptor,
        string key,
        string? grain)
    {
        if (descriptor == key)
            return true;
        var prefix = grain ?? (key.Length switch
        {
            10 when key[4] == '-' && key[7] == '-' => "DAY",
            7 when key[4] == '-' => "MONTH",
            4 => "YEAR",
            _ => null
        });
        return prefix is not null && descriptor == $"{prefix}:{key}";
    }

    private static IEnumerable<JsonElement> ReadArray(
        JsonElement root,
        string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
            throw Fail("BASIC_OWNER_ARRAY_INVALID");
        return value.EnumerateArray().Select(item => item.Clone());
    }

    private static string? ExactOptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool NonNullProperty(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) &&
           value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string? FirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(value.GetString()))
                return value.GetString();
        return null;
    }

    private sealed record BasicItem(
        ActualBasicPayloadItemObservation Observation,
        WorkAssignmentBasicSummaryItemDto Dto,
        JsonElement? ValueElement);
}
