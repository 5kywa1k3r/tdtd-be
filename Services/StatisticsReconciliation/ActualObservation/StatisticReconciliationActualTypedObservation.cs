using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Immutable typed actual row. All vocabularies and hashes are owned locally;
/// comparison joins the neutral identity tuple, never another ledger's hash.
/// </summary>
internal sealed record StatisticReconciliationActualTypedObservation(
    int Ordinal,
    string Layer,
    string OwnerId,
    string OwnerVersionSha256,
    string ProvenanceSha256,
    string IdentitySha256,
    string Family,
    string Kind,
    string MetricId,
    string? FieldId,
    string? TableId,
    string? RowId,
    string? LabelId,
    string? BasicScope,
    string? BasicScopeId,
    string? FlowBranchId,
    string? FlowStepId,
    string? AdvancedGrain,
    string? DiffKind,
    string PeriodKey,
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
    string TransitionLeg = "NONE",
    string? TransitionKind = null,
    string? CollectionSemantics = null);

internal static partial class StatisticReconciliationActualTypedObservationCanonical
{
    internal const int MaximumGenerationAtomCount = 99_991;
    internal const long MaximumGenerationCanonicalUtf8Bytes =
        256L * 1024 * 1024;
    private const int MaximumCollectionItems = 4096;
    private static readonly ImmutableHashSet<string> AllowedFamilies =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "DIRECT", "BASIC", "ADVANCED", "DIFF", "API", "EXPORT");
    private static readonly ImmutableHashSet<string> AllowedMetricKinds =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "FIELD", "TABLE", "ROW_LABEL");
    private static readonly ImmutableHashSet<string> AllowedBasicScopes =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "DIRECT_CHILDREN_OR_SELF", "DIRECT_CHILDREN",
            "FLOW_BRANCH", "FLOW_STEP", "FLOW_EFFECTIVE_PATH", "FLOW_FINAL");
    private static readonly ImmutableHashSet<string> AllowedAdvancedGrains =
        ImmutableHashSet.Create(StringComparer.Ordinal, "DAY", "MONTH", "YEAR");
    private static readonly ImmutableHashSet<string> AllowedDiffKinds =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "FIELD", "TABLE_METRIC", "ROW_LABEL");
    private static readonly ImmutableHashSet<string> AllowedValueTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "NUMBER", "BUCKET", "DATE", "FULL_DATE", "PERIOD", "BOOLEAN",
            "ENUM", "STRING_LIST", "TEXT");
    private static readonly ImmutableHashSet<string> AllowedAtomKinds =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "REPORT_COUNT", "ROW_COUNT", "COUNT", "NUMERIC_VALUE_COUNT",
            "SUM", "MIN", "MAX", "MEAN", "BUCKET", "DATE", "FULL_DATE",
            "PERIOD", "BOOLEAN", "ENUM",
            "STRING_LIST_ORDERED", "STRING_LIST_UNORDERED", "TEXT");

    internal static StatisticReconciliationActualTypedObservation Create(
        int ordinal,
        string layer,
        string ownerId,
        string ownerVersionSha256,
        string family,
        string kind,
        string metricId,
        string periodKey,
        string atomKind,
        string valueType,
        string canonicalValue,
        int decimalScale = 0,
        long occurrenceCount = 1,
        long reportCount = 1,
        long rowCount = 0,
        long numericValueCount = 0,
        string valueState = "VALUE",
        string? fieldId = null,
        string? tableId = null,
        string? rowId = null,
        string? labelId = null,
        string? basicScope = null,
        string? basicScopeId = null,
        string? flowBranchId = null,
        string? flowStepId = null,
        string? advancedGrain = null,
        string? diffKind = null,
        string transitionLeg = "NONE",
        string? transitionKind = null,
        string? collectionSemantics = null)
    {
        if (ordinal < 0)
            throw Fail("TYPED_ORDINAL_INVALID");
        layer = Upper(layer, "TYPED_LAYER");
        if (!StatisticReconciliationActualCoherentLayers.RequiredOrder.Contains(
                layer,
                StringComparer.Ordinal))
            throw Fail("TYPED_LAYER_INVALID");
        ownerId = Required(ownerId, "TYPED_OWNER_ID");
        ownerVersionSha256 = Sha(ownerVersionSha256, "TYPED_OWNER_VERSION_SHA256");
        family = Upper(family, "TYPED_FAMILY");
        kind = Upper(kind, "TYPED_KIND");
        metricId = Required(metricId, "TYPED_METRIC_ID");
        periodKey = Required(periodKey, "TYPED_PERIOD_KEY");
        atomKind = Upper(atomKind, "TYPED_ATOM_KIND");
        valueType = Upper(valueType, "TYPED_VALUE_TYPE");
        valueState = Upper(valueState, "TYPED_VALUE_STATE");
        if (!AllowedAtomKinds.Contains(atomKind))
            throw Fail("TYPED_ATOM_KIND_INVALID");
        if (!AllowedValueTypes.Contains(valueType))
            throw Fail("TYPED_VALUE_TYPE_INVALID");
        if (valueState != "VALUE")
            throw Fail("VALUE_STATE_UNSUPPORTED");
        canonicalValue = StatisticReconciliationActualCanonical.Required(
            canonicalValue,
            "TYPED_CANONICAL_VALUE",
            65536);
        fieldId = Optional(fieldId, "TYPED_FIELD_ID");
        tableId = Optional(tableId, "TYPED_TABLE_ID");
        rowId = Optional(rowId, "TYPED_ROW_ID");
        labelId = Optional(labelId, "TYPED_LABEL_ID");
        basicScope = OptionalUpper(basicScope, "TYPED_BASIC_SCOPE");
        basicScopeId = Optional(basicScopeId, "TYPED_BASIC_SCOPE_ID");
        flowBranchId = Optional(flowBranchId, "TYPED_FLOW_BRANCH_ID");
        flowStepId = Optional(flowStepId, "TYPED_FLOW_STEP_ID");
        advancedGrain = OptionalUpper(advancedGrain, "TYPED_ADVANCED_GRAIN");
        diffKind = OptionalUpper(diffKind, "TYPED_DIFF_KIND");
        transitionLeg = Upper(transitionLeg, "TYPED_TRANSITION_LEG");
        transitionKind = OptionalUpper(
            transitionKind, "TYPED_TRANSITION_KIND");
        collectionSemantics = OptionalUpper(
            collectionSemantics, "TYPED_COLLECTION_SEMANTICS");
        if (transitionLeg != "NONE" || transitionKind is not null ||
            collectionSemantics is not null)
            throw Fail("TYPED_V3_REQUIRES_SUMMARY_V3");
        if (decimalScale is < 0 or > 28 || occurrenceCount <= 0 ||
            reportCount < 0 || rowCount < 0 || numericValueCount < 0)
            throw Fail("TYPED_COUNTS_OR_SCALE_INVALID");

        ValidateValueChannel(atomKind, valueType, canonicalValue, decimalScale);
        if (family is "API" or "EXPORT" &&
            (atomKind != "TEXT" || valueType != "TEXT"))
            throw Fail("TYPED_OPAQUE_CHANNEL_INVALID");
        ValidateIdentityShape(
            family,
            kind,
            fieldId,
            tableId,
            rowId,
            labelId,
            basicScope,
            basicScopeId,
            flowBranchId,
            flowStepId,
            advancedGrain,
            diffKind);

        var identitySha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_TYPED_IDENTITY_V1",
            family,
            kind,
            metricId,
            periodKey,
            fieldId ?? "~",
            tableId ?? "~",
            rowId ?? "~",
            labelId ?? "~",
            basicScope ?? "~",
            basicScopeId ?? "~",
            advancedGrain ?? "~",
            diffKind ?? "~");
        var valueIdentitySha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_TYPED_VALUE_IDENTITY_V1",
            identitySha256,
            atomKind,
            valueType,
            valueState,
            canonicalValue,
            StatisticReconciliationActualCanonical.Integer(decimalScale));
        var provenanceSha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_TYPED_PROVENANCE_V1",
            StatisticReconciliationActualCanonical.Integer(ordinal),
            layer,
            ownerId,
            ownerVersionSha256,
            identitySha256,
            valueIdentitySha256);
        var atomSemanticSha256 = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_TYPED_ATOM_V1",
            identitySha256,
            atomKind,
            valueType,
            valueState,
            canonicalValue,
            StatisticReconciliationActualCanonical.Integer(decimalScale),
            StatisticReconciliationActualCanonical.Integer(occurrenceCount),
            StatisticReconciliationActualCanonical.Integer(reportCount),
            StatisticReconciliationActualCanonical.Integer(rowCount),
            StatisticReconciliationActualCanonical.Integer(numericValueCount),
            valueIdentitySha256,
            provenanceSha256);
        return new StatisticReconciliationActualTypedObservation(
            ordinal,
            layer,
            ownerId,
            ownerVersionSha256,
            provenanceSha256,
            identitySha256,
            family,
            kind,
            metricId,
            fieldId,
            tableId,
            rowId,
            labelId,
            basicScope,
            basicScopeId,
            flowBranchId,
            flowStepId,
            advancedGrain,
            diffKind,
            periodKey,
            atomKind,
            valueType,
            valueState,
            canonicalValue,
            decimalScale,
            occurrenceCount,
            reportCount,
            rowCount,
            numericValueCount,
            valueIdentitySha256,
            atomSemanticSha256,
            transitionLeg,
            transitionKind,
            collectionSemantics);
    }

    internal static StatisticReconciliationActualTypedObservation Normalize(
        StatisticReconciliationActualTypedObservation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = RequiresSummaryV2(value)
            ? CreateSummaryV2(
                value.Ordinal,
                value.Layer,
                value.OwnerId,
                value.OwnerVersionSha256,
                value.Family,
                value.Kind,
                value.MetricId,
                value.PeriodKey,
                value.AtomKind,
                value.ValueType,
                value.ValueState,
                value.CanonicalValue,
                value.DecimalScale,
                value.OccurrenceCount,
                value.ReportCount,
                value.RowCount,
                value.NumericValueCount,
                value.FieldId,
                value.TableId,
                value.RowId,
                value.LabelId,
                value.BasicScope,
                value.BasicScopeId,
                value.FlowBranchId,
                value.FlowStepId,
                value.AdvancedGrain,
                value.DiffKind,
                value.TransitionLeg,
                value.TransitionKind,
                value.CollectionSemantics)
            : Create(
                value.Ordinal,
                value.Layer,
                value.OwnerId,
                value.OwnerVersionSha256,
                value.Family,
                value.Kind,
                value.MetricId,
                value.PeriodKey,
                value.AtomKind,
                value.ValueType,
                value.CanonicalValue,
                value.DecimalScale,
                value.OccurrenceCount,
                value.ReportCount,
                value.RowCount,
                value.NumericValueCount,
                value.ValueState,
                value.FieldId,
                value.TableId,
                value.RowId,
                value.LabelId,
                value.BasicScope,
                value.BasicScopeId,
                value.FlowBranchId,
                value.FlowStepId,
                value.AdvancedGrain,
                value.DiffKind,
                value.TransitionLeg,
                value.TransitionKind,
                value.CollectionSemantics);
        if (normalized != value)
            throw Fail("TYPED_OBSERVATION_SEMANTIC_MISMATCH");
        return normalized;
    }

    internal static ImmutableArray<StatisticReconciliationActualTypedObservation>
        NormalizeSet(IEnumerable<StatisticReconciliationActualTypedObservation> values)
        => NormalizeSet(values, generationBudget: null);

    internal static ImmutableArray<StatisticReconciliationActualTypedObservation>
        NormalizeSet(
            IEnumerable<StatisticReconciliationActualTypedObservation> values,
            GenerationBudget? generationBudget)
    {
        ArgumentNullException.ThrowIfNull(values);
        var bounded = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualTypedObservation>();
        long canonicalBytes = 0;
        foreach (var value in values)
        {
            if (bounded.Count == MaximumGenerationAtomCount)
                throw Fail("TYPED_OBSERVATION_SET_TOO_LARGE");
            var normalizedValue = Normalize(value);
            canonicalBytes = AddCanonicalBytes(
                canonicalBytes,
                normalizedValue.CanonicalValue,
                "TYPED_OBSERVATION_SET_BYTES_TOO_LARGE");
            generationBudget?.Observe(normalizedValue);
            bounded.Add(normalizedValue);
        }
        var normalized = bounded
            .OrderBy(item => item.Ordinal)
            .ThenBy(item => item.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(item => item.AtomKind, StringComparer.Ordinal)
            .ThenBy(item => item.ValueIdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!normalized.Select(item => item.Ordinal)
                .SequenceEqual(Enumerable.Range(0, normalized.Length)))
            throw Fail("TYPED_OBSERVATION_ORDINALS_INVALID");
        if (normalized.Select(Key).Distinct(StringComparer.Ordinal).Count() !=
            normalized.Length)
            throw Fail("TYPED_OBSERVATION_DUPLICATE");
        return normalized;
    }

    internal static void RequireGenerationBounds(
        IEnumerable<StatisticReconciliationActualTypedObservation> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var budget = new GenerationBudget();
        foreach (var value in values)
            budget.Observe(value);
    }

    internal static string ManifestSha256(
        IEnumerable<StatisticReconciliationActualTypedObservation> values)
    {
        var normalized = NormalizeSet(values);
        return StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_TYPED_LAYER_MANIFEST_V1",
            normalized.Select(item => item.AtomSemanticSha256));
    }

    private static void ValidateIdentityShape(
        string family,
        string kind,
        string? fieldId,
        string? tableId,
        string? rowId,
        string? labelId,
        string? basicScope,
        string? basicScopeId,
        string? flowBranchId,
        string? flowStepId,
        string? advancedGrain,
        string? diffKind)
    {
        if (!AllowedFamilies.Contains(family) ||
            !AllowedMetricKinds.Contains(kind))
            throw Fail("TYPED_IDENTITY_VOCABULARY_INVALID");
        var kindValid = kind switch
        {
            "FIELD" =>
                fieldId is not null && tableId is null && rowId is null &&
                labelId is null,
            "TABLE" =>
                fieldId is null && tableId is not null && rowId is null &&
                labelId is null,
            "ROW_LABEL" =>
                fieldId is null && tableId is not null && rowId is not null &&
                labelId is not null,
            _ => false
        };
        if (!kindValid)
            throw Fail("TYPED_IDENTITY_KIND_SHAPE_INVALID");

        switch (family)
        {
            case "DIRECT":
            case "API":
            case "EXPORT":
                RequireAbsent(basicScope, basicScopeId, advancedGrain, diffKind);
                RequireAbsent(flowBranchId, flowStepId);
                break;
            case "BASIC":
                if (basicScope is null || basicScopeId is null ||
                    !AllowedBasicScopes.Contains(basicScope) ||
                    advancedGrain is not null || diffKind is not null)
                    throw Fail("TYPED_BASIC_SCOPE_INVALID");
                if (basicScope == "FLOW_BRANCH")
                {
                    if (flowBranchId != basicScopeId || flowStepId is not null)
                        throw Fail("TYPED_FLOW_SCOPE_INVALID");
                }
                else if (basicScope ==
                         "FLOW_STEP")
                {
                    if (flowStepId != basicScopeId || flowBranchId is not null)
                        throw Fail("TYPED_FLOW_SCOPE_INVALID");
                }
                else
                {
                    RequireAbsent(flowBranchId, flowStepId);
                }
                break;
            case "ADVANCED":
                if (advancedGrain is null ||
                    !AllowedAdvancedGrains.Contains(
                        advancedGrain) ||
                    basicScope is not null || basicScopeId is not null ||
                    diffKind is not null)
                    throw Fail("TYPED_ADVANCED_SCOPE_INVALID");
                RequireAbsent(flowBranchId, flowStepId);
                break;
            case "DIFF":
                if (diffKind is null ||
                    !AllowedDiffKinds.Contains(diffKind) ||
                    basicScope is not null || basicScopeId is not null ||
                    advancedGrain is not null ||
                    !DiffMatchesKind(diffKind, kind))
                    throw Fail("TYPED_DIFF_SCOPE_INVALID");
                RequireAbsent(flowBranchId, flowStepId);
                break;
        }
    }

    private static void ValidateValueChannel(
        string atomKind,
        string valueType,
        string canonicalValue,
        int decimalScale)
    {
        var expectedType = atomKind switch
        {
            "REPORT_COUNT" or "ROW_COUNT" or "COUNT" or
                "NUMERIC_VALUE_COUNT" or "SUM" or "MIN" or "MAX" or
                "MEAN" => "NUMBER",
            "BUCKET" => "BUCKET",
            "DATE" => "DATE",
            "FULL_DATE" => "FULL_DATE",
            "PERIOD" => "PERIOD",
            "BOOLEAN" => "BOOLEAN",
            "ENUM" => "ENUM",
            "STRING_LIST_ORDERED" or "STRING_LIST_UNORDERED" =>
                "STRING_LIST",
            "TEXT" => "TEXT",
            _ => throw Fail("TYPED_ATOM_KIND_INVALID")
        };
        if (!StringComparer.Ordinal.Equals(expectedType, valueType))
            throw Fail("TYPED_ATOM_VALUE_TYPE_MISMATCH");

        if (valueType == "NUMBER")
        {
            ValidateCanonicalNumber(atomKind, canonicalValue, decimalScale);
            return;
        }
        if (decimalScale != 0)
            throw Fail("TYPED_NON_NUMERIC_SCALE_INVALID");

        var canonical = valueType switch
        {
            "BOOLEAN" => canonicalValue is "true" or "false",
            "DATE" or "PERIOD" => CanonicalPeriodLike(
                canonicalValue,
                fullDate: false),
            "FULL_DATE" => CanonicalPeriodLike(
                canonicalValue,
                fullDate: true),
            "BUCKET" => CanonicalBucket(canonicalValue),
            "STRING_LIST" => CanonicalStringList(atomKind, canonicalValue),
            "ENUM" or "TEXT" => true,
            _ => false
        };
        if (!canonical)
            throw Fail("TYPED_CANONICAL_VALUE_INVALID");
    }

    private static void ValidateCanonicalNumber(
        string atomKind,
        string canonicalValue,
        int decimalScale)
    {
        if (!decimal.TryParse(
                canonicalValue,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed))
            throw Fail("TYPED_NUMBER_INVALID");
        var canonical = StatisticReconciliationActualCanonical.Number(parsed);
        var point = canonical.IndexOf('.');
        var actualScale = point < 0 ? 0 : canonical.Length - point - 1;
        if (!StringComparer.Ordinal.Equals(canonical, canonicalValue) ||
            decimalScale != actualScale)
            throw Fail("TYPED_NUMBER_NON_CANONICAL");

        if (atomKind is "REPORT_COUNT" or "ROW_COUNT" or "COUNT" or
            "NUMERIC_VALUE_COUNT")
        {
            if (decimalScale != 0 ||
                !long.TryParse(
                    canonicalValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _))
                throw Fail("TYPED_COUNT_VALUE_INVALID");
        }
    }

    private static bool CanonicalBucket(string value)
    {
        if (value.StartsWith("S:", StringComparison.Ordinal))
            return value.Length > 2;
        if (value is "B:true" or "B:false")
            return true;
        if (!value.StartsWith("N:", StringComparison.Ordinal))
            return false;
        var numeric = value[2..];
        if (!decimal.TryParse(
                numeric,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed))
            return false;
        return StringComparer.Ordinal.Equals(
            StatisticReconciliationActualCanonical.Number(parsed),
            numeric);
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

    private static bool CanonicalStringList(string atomKind, string value)
    {
        try
        {
            using var document = JsonDocument.Parse(
                value,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                });
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() is < 1 or
                    > MaximumCollectionItems)
                return false;
            var items = document.RootElement.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString()!
                    : throw new JsonException())
                .ToArray();
            if (items.Any(item =>
                    item.Length > 65536 || item.Any(char.IsControl)))
                return false;
            var rawCanonical = JsonSerializer.Serialize(items);
            if (!StringComparer.Ordinal.Equals(rawCanonical, value))
                return false;
            if (atomKind == "STRING_LIST_UNORDERED")
            {
                var normalized = items
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                return StringComparer.Ordinal.Equals(
                    JsonSerializer.Serialize(normalized),
                    value);
            }
            return atomKind == "STRING_LIST_ORDERED";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long AddCanonicalBytes(
        long current,
        string canonicalValue,
        string reason)
    {
        var bytes = Encoding.UTF8.GetByteCount(canonicalValue);
        if (current > MaximumGenerationCanonicalUtf8Bytes - bytes)
            throw Fail(reason);
        return current + bytes;
    }

    internal sealed class GenerationBudget
    {
        private int _count;
        private long _canonicalBytes;

        internal void Observe(
            StatisticReconciliationActualTypedObservation value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_count == MaximumGenerationAtomCount)
                throw Fail("TYPED_GENERATION_TOO_LARGE");
            _count++;
            _canonicalBytes = AddCanonicalBytes(
                _canonicalBytes,
                value.CanonicalValue,
                "TYPED_GENERATION_BYTES_TOO_LARGE");
        }
    }
    private static bool DiffMatchesKind(string diffKind, string kind)
        => (diffKind, kind) switch
        {
            ("FIELD",
                "FIELD") => true,
            ("TABLE_METRIC",
                "TABLE") => true,
            ("ROW_LABEL",
                "ROW_LABEL") => true,
            _ => false
        };

    private static string Key(
        StatisticReconciliationActualTypedObservation value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_TYPED_UNIQUE_KEY_V1",
            StatisticReconciliationActualCanonical.Integer(value.Ordinal),
            value.ProvenanceSha256,
            value.IdentitySha256,
            value.AtomKind,
            value.ValueIdentitySha256);

    private static void RequireAbsent(params string?[] values)
    {
        if (values.Any(value => value is not null))
            throw Fail("TYPED_IDENTITY_SCOPE_MIXED");
    }

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);

    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);

    private static string Upper(string? value, string name)
    {
        var normalized = Required(value, name);
        if (!StringComparer.Ordinal.Equals(normalized, normalized.ToUpperInvariant()))
            throw Fail($"{name}_NON_CANONICAL");
        return normalized;
    }

    private static string? Optional(string? value, string name)
        => StatisticReconciliationActualCanonical.Optional(value, name);

    private static string? OptionalUpper(string? value, string name)
    {
        var normalized = Optional(value, name);
        if (normalized is not null && !StringComparer.Ordinal.Equals(
                normalized,
                normalized.ToUpperInvariant()))
            throw Fail($"{name}_NON_CANONICAL");
        return normalized;
    }

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_TYPED_{reason}");
}