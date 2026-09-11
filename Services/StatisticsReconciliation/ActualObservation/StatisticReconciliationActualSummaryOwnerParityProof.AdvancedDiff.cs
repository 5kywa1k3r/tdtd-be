using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static partial class StatisticReconciliationActualSummaryOwnerParity
{
    private static ImmutableArray<string> ProveAdvanced(
        ImmutableArray<StatisticReconciliationActualSummaryIdentityDescriptor>
            descriptors,
        ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms,
        ActualAdvancedCapture owner,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources)
    {
        ValidateAdvancedCaptureRoot(owner);
        if (sources.Length != 1)
            throw Fail("ADVANCED_SOURCE_ORDER_UNPROVABLE");
        if (owner.Nodes.IsDefault || owner.TotalNodeCount != owner.Nodes.Length ||
            owner.Nodes.Length == 0)
            throw Fail("ADVANCED_OWNER_PARTITION_INVALID");
        var fields = ImmutableArray.CreateBuilder<AdvancedItem>();
        foreach (var node in owner.Nodes)
        {
            ValidateAdvancedNode(node);
            if (!node.OwnerState.IsCleanResult || node.Value is null ||
                !node.SourceReportIds.SequenceEqual(
                    [sources[0].ReportId], StringComparer.Ordinal))
                throw Fail("ADVANCED_OWNER_STATE_INCOMPLETE");
            foreach (var field in node.Value.Fields)
                fields.Add(new AdvancedItem(node, field));
        }
        var ownerFields = fields.ToImmutable();
        if (ownerFields.Length != descriptors.Length ||
            ownerFields.Select(value => Key(value.Node, value.Field))
                .Distinct(StringComparer.Ordinal).Count() != ownerFields.Length)
            throw Fail("ADVANCED_OWNER_FIELD_CARDINALITY");

        var relations = ImmutableArray.CreateBuilder<string>(descriptors.Length);
        foreach (var descriptor in descriptors)
        {
            if (descriptor.Kind != "FIELD" || descriptor.ValueType != "NUMBER" ||
                descriptor.ExpandArray || descriptor.CollectionSemantics is not null ||
                descriptor.AdvancedGrain is not ("DAY" or "MONTH" or "YEAR"))
                throw Fail("ADVANCED_DESCRIPTOR_SHAPE_UNPROVABLE");
            var matches = ownerFields.Where(value =>
                descriptor.MetricId ==
                    $"{value.Field.FieldKey}:{value.Field.Method}" &&
                descriptor.FieldId == value.Field.FieldId &&
                descriptor.AdvancedGrain == value.Node.Grain &&
                PeriodMatches(descriptor.PeriodKey, value.Node.GrainKey,
                    value.Node.Grain)).ToArray();
            if (matches.Length != 1)
                throw Fail("ADVANCED_DESCRIPTOR_OWNER_BIJECTION");
            var match = matches[0];
            var method = match.Field.Method;
            if (method is not ("COUNT" or "SUM" or "MIN" or "MAX" or
                    "MEAN") ||
                method != "COUNT" && !descriptor.Operations.Contains(method,
                    StringComparer.Ordinal) || match.Field.DataType != "NUMBER")
                throw Fail("ADVANCED_METHOD_UNPROVABLE");
            var partition = atoms.Where(value =>
                value.IdentitySha256 == descriptor.IdentitySha256).ToArray();
            var count = Integer(partition, "COUNT");
            if (match.Field.ValueCount != count ||
                match.Field.SourceReportCount != count)
                throw Fail("ADVANCED_OWNER_COUNT_MISMATCH");
            var expected = method switch
            {
                "COUNT" => (decimal?)count,
                "SUM" => Decimal(partition, "SUM"),
                "MIN" => Decimal(partition, "MIN"),
                "MAX" => Decimal(partition, "MAX"),
                "MEAN" => Decimal(partition, "MEAN"),
                _ => throw Fail("ADVANCED_METHOD_UNSUPPORTED")
            };
            if (!CanonicalDecimalJson(match.Field.ResultCanonicalJson, expected))
                throw Fail("ADVANCED_OWNER_VALUE_MISMATCH");
            var sample = ResolveNumericSample(
                sources[0].CanonicalPayloadJson, descriptor.JsonPointer);
            var expectedSamples = sample is null
                ? ImmutableArray<string>.Empty
                : [sample];
            if (!match.Field.SampleValues.SequenceEqual(expectedSamples,
                    StringComparer.Ordinal))
                throw Fail("ADVANCED_OWNER_SAMPLE_MISMATCH");
            var rawManifest = HashSequence(
                "P10_ACTUAL_SUMMARY_OWNER_ADVANCED_RAW_RELATION_V1",
                partition.Select(value => value.AtomSemanticSha256));
            relations.Add(Hash(
                "P10_ACTUAL_SUMMARY_OWNER_ADVANCED_RELATION_V1",
                descriptor.SemanticSha256, rawManifest,
                match.Node.SemanticSha256, match.Field.SemanticSha256,
                StatisticReconciliationActualJson.RawSha256(
                    match.Field.ResultCanonicalJson)));
        }
        return relations.MoveToImmutable();
    }

    private static void ValidateAdvancedNode(ActualAdvancedNodeObservation node)
    {
        if (node.Value is null || node.Value.Fields.IsDefault ||
            node.Value.Warnings.IsDefault || node.SourceReportIds.IsDefault ||
            node.InputNodeKeys.IsDefault)
            throw Fail("ADVANCED_NODE_VALUE_REQUIRED");
        using var document = StatisticReconciliationActualJson.ParseStrict(
            node.ValueJson, "SUMMARY_PARITY_ADVANCED_VALUE");
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
        if (StatisticReconciliationActualJson.RawSha256(node.ValueJson) !=
                node.ObservedValueSha256 ||
            StatisticReconciliationActualJson.RawSha256(canonical) !=
                node.CanonicalValueSha256)
            throw Fail("ADVANCED_NODE_VALUE_HASH_MISMATCH");
        string? prior = null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in node.Value.Fields)
        {
            var fieldSemantic = Hash("P10_ACTUAL_ADVANCED_FIELD_V1",
                I(field.OwnerOrdinal), field.FieldId, field.FieldKey,
                field.Label, field.DataType, field.Method,
                I(field.ValueCount), I(field.SourceReportCount),
                StatisticReconciliationActualJson.RawSha256(
                    field.ResultCanonicalJson),
                HashSequence("P10_ACTUAL_ADVANCED_FIELD_SAMPLES_V1",
                    field.SampleValues));
            if (field.OwnerOrdinal < 0 ||
                fieldSemantic != field.SemanticSha256 ||
                !ids.Add(field.FieldId) || !keys.Add(field.FieldKey) ||
                prior is not null && StringComparer.Ordinal.Compare(
                    prior, field.FieldKey) >= 0)
                throw Fail("ADVANCED_FIELD_SEMANTIC_INVALID");
            prior = field.FieldKey;
        }
        var valueSemantic = Hash("P10_ACTUAL_ADVANCED_TYPED_VALUE_V1",
            StatisticReconciliationActualJson.RawSha256(canonical),
            HashSequence("P10_ACTUAL_ADVANCED_TYPED_FIELDS_V1",
                node.Value.Fields.Select(value => value.SemanticSha256)));
        if (valueSemantic != node.Value.SemanticSha256)
            throw Fail("ADVANCED_VALUE_SEMANTIC_MISMATCH");
        var semantic = Hash("P10_ACTUAL_ADVANCED_NODE_OBSERVATION_V1",
            node.Store, node.OwnerNodeId, node.Grain, node.GrainKey,
            node.DayKey, node.MonthKey, node.YearKey,
            StatisticReconciliationActualCanonical.Instant(node.WindowStartUtc),
            StatisticReconciliationActualCanonical.Instant(
                node.WindowEndExclusiveUtc),
            node.SourceSignatureSha256, I(node.SourceReportCount),
            HashSequence("P10_ACTUAL_ADVANCED_SOURCE_REPORT_ORDER_V1",
                node.SourceReportIds),
            HashSequence("P10_ACTUAL_ADVANCED_INPUT_NODE_ORDER_V1",
                node.InputNodeKeys), node.StoredValueSha256,
            node.ObservedValueSha256, node.CanonicalValueSha256,
            node.Value.SemanticSha256,
            node.OwnerState.BuildLifecycleSemanticSha256,
            StatisticReconciliationActualCanonical.Boolean(
                node.OwnerState.IsCleanResult),
            StatisticReconciliationActualCanonical.Boolean(
                node.OwnerState.ValueHashMatches),
            StatisticReconciliationActualCanonical.Boolean(
                node.OwnerState.SourceSignatureValid),
            StatisticReconciliationActualCanonical.Boolean(
                node.OwnerState.TypedValueShapeValid),
            StatisticReconciliationActualCanonical.Boolean(
                node.OwnerState.InnerValueMatchesOuter),
            StatisticReconciliationActualCanonical.Boolean(
                node.OwnerState.CleanLifecycleValid));
        if (semantic != node.SemanticSha256)
            throw Fail("ADVANCED_NODE_SEMANTIC_MISMATCH");
    }

    private static bool CanonicalDecimalJson(string json, decimal? expected)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            json, "SUMMARY_PARITY_ADVANCED_RESULT");
        if (StatisticReconciliationActualJson.Canonicalize(document.RootElement) !=
            json)
            return false;
        if (!expected.HasValue)
            return document.RootElement.ValueKind == JsonValueKind.Null;
        return document.RootElement.ValueKind == JsonValueKind.Number &&
            document.RootElement.TryGetDecimal(out var found) &&
            found == expected.Value;
    }

    private static string? ResolveNumericSample(string json, string pointer)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            json, "SUMMARY_PARITY_RAW_SAMPLE");
        if (!TryResolvePointer(document.RootElement, pointer, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            value.ValueKind == JsonValueKind.String &&
            string.IsNullOrWhiteSpace(value.GetString()))
            return null;
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out var numeric))
            throw Fail("ADVANCED_RAW_SAMPLE_NOT_NUMERIC");
        return Number(numeric);
    }

    private static string Key(
        ActualAdvancedNodeObservation node,
        ActualAdvancedFieldObservation field)
        => string.Join('\u001f', node.Grain, node.GrainKey, field.FieldId,
            field.FieldKey, field.Method);

    private static ImmutableArray<string> ProveDiff(
        ImmutableArray<StatisticReconciliationActualSummaryIdentityDescriptor>
            descriptors,
        ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms,
        ActualP9DiffCapture owner,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources)
    {
        ValidateDiffCaptureRoot(owner);
        if (sources.Length != 1)
            throw Fail("DIFF_SOURCE_ORDER_UNPROVABLE");
        if (owner.Rows.IsDefault || owner.Rows.Length != descriptors.Length ||
            !owner.OwnerState.IsUsableResult ||
            owner.P10DeltaState !=
                StatisticReconciliationActualP9DiffAdapter.P10DeltaNotComputed ||
            owner.P10Verdict is not null ||
            owner.Rows.Select(value => value.Key)
                .Distinct(StringComparer.Ordinal).Count() != owner.Rows.Length)
            throw Fail("DIFF_OWNER_STATE_INCOMPLETE");
        var effectivePeriodJson = owner.Direction == "RIGHT_TO_LEFT"
            ? owner.RightPeriodCanonicalJson : owner.LeftPeriodCanonicalJson;
        var period = PeriodKey(effectivePeriodJson);
        var relations = ImmutableArray.CreateBuilder<string>(descriptors.Length);
        foreach (var descriptor in descriptors)
        {
            if (descriptor.ValueType != "NUMBER" || descriptor.ExpandArray ||
                descriptor.DiffKind is not ("FIELD" or "TABLE_METRIC") ||
                descriptor.Kind != descriptor.DiffKind ||
                descriptor.CollectionSemantics is not null ||
                !descriptor.Operations.Contains("SUM", StringComparer.Ordinal))
                throw Fail("DIFF_DESCRIPTOR_SHAPE_UNPROVABLE");
            var matches = owner.Rows.Where(value =>
                value.Key == descriptor.MetricId &&
                value.ConceptKind == descriptor.DiffKind &&
                DiffConceptMatches(descriptor, value.ConceptKey)).ToArray();
            if (matches.Length != 1 ||
                !PeriodMatches(descriptor.PeriodKey, period, null))
                throw Fail("DIFF_DESCRIPTOR_OWNER_BIJECTION");
            var row = matches[0];
            ValidateDiffRow(row);
            var partition = atoms.Where(value =>
                value.IdentitySha256 == descriptor.IdentitySha256).ToArray();
            var before = Leg(partition, "BEFORE");
            var after = Leg(partition, "AFTER");
            if (!TypedMatches(row.Right, before, descriptor.ValueType) ||
                !TypedMatches(row.Left, after, descriptor.ValueType))
                throw Fail("DIFF_OWNER_LEG_MISMATCH");
            var transition = One(partition, "TRANSITION_KIND", "CHANGE_STATE")
                .CanonicalValue;
            if (transition != descriptor.TransitionKind ||
                Integer(partition, "ADDED_COUNT", "CHANGE_STATE") !=
                    (transition == "ADDED" ? 1 : 0) ||
                Integer(partition, "REMOVED_COUNT", "CHANGE_STATE") !=
                    (transition == "REMOVED" ? 1 : 0) ||
                Integer(partition, "CHANGED_COUNT", "CHANGE_STATE") !=
                    (transition == "CHANGED" ? 1 : 0))
                throw Fail("DIFF_TRANSITION_MISMATCH");
            var differenceKind = before.State != after.State
                ? "STATE_CHANGED"
                : before.State != "VALUE"
                    ? "UNCHANGED"
                    : before.Canonical == after.Canonical
                        ? "UNCHANGED" : "VALUE_CHANGED";
            var equal = differenceKind == "UNCHANGED";
            var delta = before.Value.HasValue && after.Value.HasValue
                ? after.Value.Value - before.Value.Value : (decimal?)null;
            if (row.P9DifferenceKind != differenceKind ||
                row.P9Equal != equal || row.P9NumericDelta != delta ||
                !row.P9ComparisonConsistent || !row.DeclaredTypesMatch ||
                row.P10DeltaState !=
                    StatisticReconciliationActualP9DiffAdapter.P10DeltaNotComputed ||
                row.P10Verdict is not null)
                throw Fail("DIFF_OWNER_COMPARISON_MISMATCH");
            if (descriptor.DifferenceOperation == "SUBTRACT" &&
                Decimal(partition, "DIFFERENCE", "DELTA") != delta)
                throw Fail("DIFF_OWNER_DELTA_MISMATCH");
            var rawManifest = HashSequence(
                "P10_ACTUAL_SUMMARY_OWNER_DIFF_RAW_RELATION_V1",
                partition.Select(value => value.AtomSemanticSha256));
            relations.Add(Hash("P10_ACTUAL_SUMMARY_OWNER_DIFF_RELATION_V1",
                descriptor.SemanticSha256, rawManifest,
                row.SemanticSha256, transition, differenceKind,
                delta.HasValue ? Number(delta.Value) : "~"));
        }
        return relations.MoveToImmutable();
    }

    private static void ValidateDiffRow(ActualP9DiffRowObservation row)
    {
        ValidateDiffTyped(row.Left);
        ValidateDiffTyped(row.Right);
        var semantic = Hash("P10_ACTUAL_P9_DIFF_ROW_V1", row.OwnerRowId,
            I(row.OwnerOrdinal), row.Key, row.ConceptKind, row.ConceptKey,
            row.Left.SemanticSha256, row.Right.SemanticSha256,
            StatisticReconciliationActualCanonical.Boolean(row.P9Equal),
            row.P9DifferenceKind,
            row.P9NumericDelta.HasValue ? Number(row.P9NumericDelta.Value) : null,
            StatisticReconciliationActualCanonical.Boolean(
                row.P9ComparisonConsistent),
            StatisticReconciliationActualCanonical.Boolean(
                row.DeclaredTypesMatch),
            StatisticReconciliationActualP9DiffAdapter.P10DeltaNotComputed);
        if (semantic != row.SemanticSha256)
            throw Fail("DIFF_ROW_SEMANTIC_MISMATCH");
    }

    private static void ValidateDiffTyped(ActualP9DiffTypedObservation value)
    {
        var semantic = Hash("P10_ACTUAL_P9_DIFF_TYPED_VALUE_V1",
            value.State, value.DataType, value.CanonicalValue,
            value.NumericValue.HasValue ? Number(value.NumericValue.Value) : null,
            value.BooleanValue.HasValue
                ? StatisticReconciliationActualCanonical.Boolean(
                    value.BooleanValue.Value) : null,
            value.DateValueUtc.HasValue
                ? StatisticReconciliationActualCanonical.Instant(
                    value.DateValueUtc.Value) : null,
            HashSequence("P10_ACTUAL_P9_DIFF_CHOICES_V1", value.ChoiceIds),
            StatisticReconciliationActualCanonical.Boolean(
                value.TypedShapeValid));
        if (semantic != value.SemanticSha256 || !value.TypedShapeValid)
            throw Fail("DIFF_TYPED_SEMANTIC_MISMATCH");
    }

    private static LegValue Leg(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string leg)
    {
        var missing = StateCount(atoms, "MISSING", leg);
        var nulls = StateCount(atoms, "NULL", leg);
        var empty = StateCount(atoms, "EMPTY", leg);
        var count = Integer(atoms, "COUNT", leg);
        if (missing + nulls + empty + count != 1)
            throw Fail("DIFF_LEG_SAMPLE_CARDINALITY");
        if (missing == 1) return new LegValue("MISSING", null, null);
        if (nulls == 1) return new LegValue("NULL", null, null);
        if (empty == 1) return new LegValue("EMPTY", null, null);
        var value = Decimal(atoms, "SUM", leg) ??
            throw Fail("DIFF_LEG_NUMERIC_VALUE_REQUIRED");
        return new LegValue("VALUE", Number(value), value);
    }

    private static long StateCount(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind,
        string leg)
    {
        var atom = One(atoms, kind, leg);
        if (atom.ValueState != kind || atom.DecimalScale != 0 ||
            !long.TryParse(atom.CanonicalValue, NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) || value < 0 ||
            atom.OccurrenceCount != value)
            throw Fail("DIFF_STATE_ATOM_INVALID");
        return value;
    }

    private static bool TypedMatches(
        ActualP9DiffTypedObservation typed,
        LegValue raw,
        string valueType)
        => valueType == "NUMBER" && typed.DataType == "NUMBER" &&
           typed.State == raw.State && typed.ChoiceIds.Length == 0 &&
           (raw.State == "VALUE"
               ? typed.CanonicalValue == raw.Canonical &&
                 typed.NumericValue == raw.Value &&
                 typed.BooleanValue is null && typed.DateValueUtc is null
               : typed.CanonicalValue is null && typed.NumericValue is null &&
                 typed.BooleanValue is null && typed.DateValueUtc is null);

    private static bool DiffConceptMatches(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string conceptKey)
        => descriptor.DiffKind switch
        {
            "FIELD" => descriptor.FieldId == conceptKey,
            "TABLE_METRIC" => descriptor.TableId == conceptKey &&
                descriptor.RowId is null && descriptor.LabelId is null,
            _ => false
        };

    private static string PeriodKey(string canonicalJson)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            canonicalJson, "SUMMARY_PARITY_DIFF_PERIOD");
        if (StatisticReconciliationActualJson.Canonicalize(document.RootElement) !=
            canonicalJson || document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("periodKey", out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString()))
            throw Fail("DIFF_PERIOD_KEY_REQUIRED");
        return value.GetString()!;
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
                index >= 0 && index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }
            return false;
        }
        return true;
    }

    private sealed record AdvancedItem(
        ActualAdvancedNodeObservation Node,
        ActualAdvancedFieldObservation Field);
    private sealed record LegValue(
        string State,
        string? Canonical,
        decimal? Value);
}
