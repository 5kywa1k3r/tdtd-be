using System.Collections.Immutable;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Exact value-free expected metric-plan descriptor. It is derived only from
/// the integrity-validated EXPECTED_METRIC_PLAN document, never from expected
/// atoms or expected values. The actual adapters may use this shape to project
/// independently observed owner data, but cannot read expected values/counts.
/// </summary>
internal sealed record StatisticReconciliationActualSummaryIdentityDescriptor(
    string Family,
    string Kind,
    string MetricId,
    string? FieldId,
    string? TableId,
    string? RowId,
    string? LabelId,
    string? BasicScope,
    string? BasicScopeId,
    string? AdvancedGrain,
    string? DiffKind,
    string PeriodKey,
    string IdentitySha256,
    string ValueType,
    ImmutableArray<string> AtomKinds,
    string SemanticSha256,
    string IdentitySchemaVersion,
    string PlanEntrySchemaVersion,
    string JsonPointer,
    bool Unordered,
    bool ExpandArray,
    ImmutableArray<string> Operations,
    string TransitionMode,
    string? BeforeJsonPointer,
    string? AfterJsonPointer,
    string? DifferenceOperation,
    string? TransitionKind,
    ImmutableArray<string> TransitionLegs,
    string? CollectionSemantics,
    string PlanEntrySha256)
{
    internal const string DescriptorSchemaVersion =
        "P10_ACTUAL_SUMMARY_IDENTITY_DESCRIPTOR_V3";

    internal static StatisticReconciliationActualSummaryIdentityDescriptor
        Create(StatisticReconciliationExpectedValueFreeMetricPlanDescriptor plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var atomKinds = ExpectedAtomKinds(plan);
        var collection = plan.ValueType ==
                StatisticReconciliationExpectedValueTypes.StringList
            ? plan.Unordered
                ? StatisticReconciliationExpectedCollectionSemantics.Unordered
                : StatisticReconciliationExpectedCollectionSemantics.Ordered
            : null;
        var value = new StatisticReconciliationActualSummaryIdentityDescriptor(
            plan.Identity.Family,
            plan.Identity.Kind,
            plan.Identity.MetricId,
            plan.Identity.FieldId,
            plan.Identity.TableId,
            plan.Identity.RowId,
            plan.Identity.LabelId,
            plan.Identity.ScopeKind,
            plan.Identity.ScopeId,
            plan.Identity.Grain,
            plan.Identity.DiffKind,
            plan.Identity.PeriodKey,
            plan.Identity.IdentitySha256,
            plan.ValueType,
            atomKinds,
            string.Empty,
            plan.Identity.SchemaVersion,
            plan.SchemaVersion,
            plan.JsonPointer,
            plan.Unordered,
            plan.ExpandArray,
            plan.Operations,
            plan.TransitionMode,
            plan.BeforeJsonPointer,
            plan.AfterJsonPointer,
            plan.DifferenceOperation,
            plan.TransitionKind,
            plan.TransitionLegs,
            collection,
            plan.PlanEntrySha256);
        return Normalize(value with { SemanticSha256 = ComputeSemantic(value) });
    }

    internal static StatisticReconciliationActualSummaryIdentityDescriptor
        Normalize(StatisticReconciliationActualSummaryIdentityDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var operations = NormalizeSet(
            value.Operations,
            StatisticReconciliationExpectedMetricOperations.All,
            8,
            "SUMMARY_OPERATION");
        var atomKinds = NormalizeSet(
            value.AtomKinds,
            FrozenAtomKinds,
            32,
            "SUMMARY_ATOM_KIND");
        var legs = value.TransitionLegs.IsDefault
            ? ImmutableArray<string>.Empty
            : value.TransitionLegs
                .Select(item => Upper(item, "SUMMARY_TRANSITION_LEG"))
                .ToImmutableArray();
        var normalized = value with
        {
            Family = Upper(value.Family, "SUMMARY_FAMILY"),
            Kind = Upper(value.Kind, "SUMMARY_KIND"),
            MetricId = Required(value.MetricId, "SUMMARY_METRIC_ID"),
            FieldId = Optional(value.FieldId, "SUMMARY_FIELD_ID"),
            TableId = Optional(value.TableId, "SUMMARY_TABLE_ID"),
            RowId = Optional(value.RowId, "SUMMARY_ROW_ID"),
            LabelId = Optional(value.LabelId, "SUMMARY_LABEL_ID"),
            BasicScope = OptionalUpper(
                value.BasicScope, "SUMMARY_BASIC_SCOPE"),
            BasicScopeId = Optional(
                value.BasicScopeId, "SUMMARY_BASIC_SCOPE_ID"),
            AdvancedGrain = OptionalUpper(
                value.AdvancedGrain, "SUMMARY_ADVANCED_GRAIN"),
            DiffKind = OptionalUpper(value.DiffKind, "SUMMARY_DIFF_KIND"),
            PeriodKey = Required(value.PeriodKey, "SUMMARY_PERIOD_KEY"),
            IdentitySha256 = Sha(
                value.IdentitySha256, "SUMMARY_IDENTITY_SHA256"),
            ValueType = Upper(value.ValueType, "SUMMARY_VALUE_TYPE"),
            AtomKinds = atomKinds,
            SemanticSha256 = Sha(
                value.SemanticSha256, "SUMMARY_DESCRIPTOR_SHA256"),
            IdentitySchemaVersion = Required(
                value.IdentitySchemaVersion,
                "SUMMARY_IDENTITY_SCHEMA_VERSION"),
            PlanEntrySchemaVersion = Required(
                value.PlanEntrySchemaVersion,
                "SUMMARY_PLAN_ENTRY_SCHEMA_VERSION"),
            JsonPointer = Pointer(value.JsonPointer, "SUMMARY_JSON_POINTER"),
            Operations = operations,
            TransitionMode = Upper(
                value.TransitionMode, "SUMMARY_TRANSITION_MODE"),
            BeforeJsonPointer = OptionalPointer(
                value.BeforeJsonPointer, "SUMMARY_BEFORE_JSON_POINTER"),
            AfterJsonPointer = OptionalPointer(
                value.AfterJsonPointer, "SUMMARY_AFTER_JSON_POINTER"),
            DifferenceOperation = OptionalUpper(
                value.DifferenceOperation, "SUMMARY_DIFFERENCE_OPERATION"),
            TransitionKind = OptionalUpper(
                value.TransitionKind, "SUMMARY_TRANSITION_KIND"),
            TransitionLegs = legs,
            CollectionSemantics = OptionalUpper(
                value.CollectionSemantics,
                "SUMMARY_COLLECTION_SEMANTICS"),
            PlanEntrySha256 = Sha(
                value.PlanEntrySha256, "SUMMARY_PLAN_ENTRY_SHA256")
        };

        if (normalized.IdentitySchemaVersion !=
                StatisticReconciliationExpectedMetricIdentityCompiler
                    .SchemaVersion ||
            normalized.PlanEntrySchemaVersion !=
                StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2 ||
            !StatisticReconciliationExpectedMetricFamilies.All.Contains(
                normalized.Family) ||
            !StatisticReconciliationExpectedMetricKinds.All.Contains(
                normalized.Kind) ||
            !StatisticReconciliationExpectedValueTypes.All.Contains(
                normalized.ValueType))
            throw Fail("SUMMARY_DESCRIPTOR_VOCAB_INVALID");

        var identity = new StatisticReconciliationExpectedMetricIdentityCompiler()
            .Compile(new ExpectedMetricIdentityRequest(
                normalized.Family,
                normalized.Kind,
                normalized.MetricId,
                normalized.PeriodKey,
                normalized.FieldId,
                normalized.TableId,
                normalized.RowId,
                normalized.LabelId,
                normalized.BasicScope,
                normalized.BasicScopeId,
                normalized.AdvancedGrain,
                normalized.DiffKind));
        if (identity.SchemaVersion != normalized.IdentitySchemaVersion ||
            identity.IdentitySha256 != normalized.IdentitySha256)
            throw Fail("SUMMARY_DESCRIPTOR_IDENTITY_MISMATCH");

        ValidatePlanShape(normalized);
        var expectedEntry =
            StatisticReconciliationExpectedMetricPlanIntegrity
                .BuildEntrySha256(
                    identity,
                    normalized.JsonPointer,
                    normalized.ValueType,
                    normalized.Unordered,
                    normalized.ExpandArray,
                    normalized.Operations,
                    normalized.TransitionMode,
                    normalized.BeforeJsonPointer,
                    normalized.AfterJsonPointer,
                    normalized.DifferenceOperation,
                    normalized.TransitionKind);
        if (expectedEntry != normalized.PlanEntrySha256 ||
            !ExpectedAtomKinds(normalized).SequenceEqual(
                normalized.AtomKinds, StringComparer.Ordinal) ||
            ComputeSemantic(normalized) != normalized.SemanticSha256)
            throw Fail("SUMMARY_DESCRIPTOR_SEMANTIC_MISMATCH");
        return normalized;
    }

    internal static string ComputeSemantic(
        StatisticReconciliationActualSummaryIdentityDescriptor value)
        => StatisticReconciliationActualCanonical.Hash(
            DescriptorSchemaVersion,
            value.IdentitySchemaVersion,
            value.IdentitySha256,
            value.PlanEntrySchemaVersion,
            value.PlanEntrySha256,
            value.JsonPointer,
            value.ValueType,
            StatisticReconciliationActualCanonical.Boolean(value.Unordered),
            StatisticReconciliationActualCanonical.Boolean(value.ExpandArray),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_SUMMARY_DESCRIPTOR_OPERATIONS_V3",
                value.Operations),
            value.TransitionMode,
            value.BeforeJsonPointer ?? "~",
            value.AfterJsonPointer ?? "~",
            value.DifferenceOperation ?? "~",
            value.TransitionKind ?? "~",
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_SUMMARY_DESCRIPTOR_TRANSITION_LEGS_V3",
                value.TransitionLegs),
            value.CollectionSemantics ?? "~",
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_SUMMARY_DESCRIPTOR_ATOM_KINDS_V3",
                value.AtomKinds));

    private static void ValidatePlanShape(
        StatisticReconciliationActualSummaryIdentityDescriptor value)
    {
        if (value.Operations.Length == 0 ||
            (value.Unordered && value.ValueType !=
                StatisticReconciliationExpectedValueTypes.StringList) ||
            (value.ValueType ==
                StatisticReconciliationExpectedValueTypes.StringList &&
             value.ExpandArray) ||
            ((value.Operations.Contains(
                  StatisticReconciliationExpectedMetricOperations.Sum) ||
              value.Operations.Contains(
                  StatisticReconciliationExpectedMetricOperations.Mean)) &&
             value.ValueType != StatisticReconciliationExpectedValueTypes.Number))
            throw Fail("SUMMARY_PLAN_SHAPE_INVALID");
        var expectedCollection = value.ValueType ==
                StatisticReconciliationExpectedValueTypes.StringList
            ? value.Unordered
                ? StatisticReconciliationExpectedCollectionSemantics.Unordered
                : StatisticReconciliationExpectedCollectionSemantics.Ordered
            : null;
        if (value.CollectionSemantics != expectedCollection)
            throw Fail("SUMMARY_COLLECTION_SEMANTICS_MISMATCH");

        if (value.Family == StatisticReconciliationExpectedMetricFamilies.Diff)
        {
            var expectedLegs = ImmutableArray.Create(
                StatisticReconciliationExpectedTransitionLegs.Before,
                StatisticReconciliationExpectedTransitionLegs.After,
                StatisticReconciliationExpectedTransitionLegs.ChangeState,
                StatisticReconciliationExpectedTransitionLegs.Delta);
            if (value.TransitionMode !=
                    StatisticReconciliationExpectedDiffTransitionModes
                        .BeforeAfterDifference ||
                value.BeforeJsonPointer is null ||
                value.AfterJsonPointer is null ||
                value.DifferenceOperation is null ||
                !StatisticReconciliationExpectedDifferenceOperations.All
                    .Contains(value.DifferenceOperation) ||
                value.TransitionKind is null ||
                !StatisticReconciliationExpectedTransitionKinds.All
                    .Contains(value.TransitionKind) ||
                !value.TransitionLegs.SequenceEqual(
                    expectedLegs, StringComparer.Ordinal))
                throw Fail("SUMMARY_DIFF_TRANSITION_SHAPE_INVALID");
            return;
        }

        if (value.TransitionMode !=
                StatisticReconciliationExpectedDiffTransitionModes.None ||
            value.BeforeJsonPointer is not null ||
            value.AfterJsonPointer is not null ||
            value.DifferenceOperation is not null ||
            value.TransitionKind is not null ||
            !value.TransitionLegs.SequenceEqual(
                [StatisticReconciliationExpectedTransitionLegs.None],
                StringComparer.Ordinal))
            throw Fail("SUMMARY_NON_DIFF_TRANSITION_MIXED");
    }

    private static ImmutableArray<string> ExpectedAtomKinds(
        StatisticReconciliationExpectedValueFreeMetricPlanDescriptor value)
        => ExpectedAtomKinds(
            value.Identity.Family,
            value.ValueType,
            value.Operations,
            value.DifferenceOperation);

    private static ImmutableArray<string> ExpectedAtomKinds(
        StatisticReconciliationActualSummaryIdentityDescriptor value)
        => ExpectedAtomKinds(
            value.Family,
            value.ValueType,
            value.Operations,
            value.DifferenceOperation);

    private static ImmutableArray<string> ExpectedAtomKinds(
        string family,
        string valueType,
        IEnumerable<string> operations,
        string? differenceOperation)
    {
        var values = new HashSet<string>(StringComparer.Ordinal)
        {
            StatisticReconciliationExpectedAtomKinds.ReportCount,
            StatisticReconciliationExpectedAtomKinds.RowCount,
            StatisticReconciliationExpectedAtomKinds.Count,
            StatisticReconciliationExpectedAtomKinds.NumericValueCount,
            StatisticReconciliationExpectedAtomKinds.Missing,
            StatisticReconciliationExpectedAtomKinds.Null,
            StatisticReconciliationExpectedAtomKinds.Empty
        };
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case StatisticReconciliationExpectedMetricOperations.Sum:
                    values.Add(StatisticReconciliationExpectedAtomKinds.Sum);
                    break;
                case StatisticReconciliationExpectedMetricOperations.Min:
                    values.Add(StatisticReconciliationExpectedAtomKinds.Min);
                    break;
                case StatisticReconciliationExpectedMetricOperations.Max:
                    values.Add(StatisticReconciliationExpectedAtomKinds.Max);
                    break;
                case StatisticReconciliationExpectedMetricOperations.Mean:
                    values.Add(StatisticReconciliationExpectedAtomKinds.Mean);
                    break;
                case StatisticReconciliationExpectedMetricOperations.Values
                    when valueType !=
                        StatisticReconciliationExpectedValueTypes.Number:
                    values.Add(ValueAtomKind(valueType));
                    break;
            }
        }
        if (family == StatisticReconciliationExpectedMetricFamilies.Diff)
        {
            values.UnionWith([
                "TRANSITION_KIND",
                "ADDED_COUNT",
                "REMOVED_COUNT",
                "CHANGED_COUNT",
                "UNCHANGED_COUNT"
            ]);
            if (differenceOperation ==
                StatisticReconciliationExpectedDifferenceOperations.Subtract)
                values.Add("DIFFERENCE");
        }
        return values.OrderBy(item => item, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static string ValueAtomKind(string valueType)
        => valueType switch
        {
            StatisticReconciliationExpectedValueTypes.Bucket =>
                StatisticReconciliationExpectedAtomKinds.Bucket,
            StatisticReconciliationExpectedValueTypes.Date =>
                StatisticReconciliationExpectedAtomKinds.Date,
            StatisticReconciliationExpectedValueTypes.FullDate =>
                StatisticReconciliationExpectedAtomKinds.FullDate,
            StatisticReconciliationExpectedValueTypes.Period =>
                StatisticReconciliationExpectedAtomKinds.Period,
            StatisticReconciliationExpectedValueTypes.Boolean =>
                StatisticReconciliationExpectedAtomKinds.Boolean,
            StatisticReconciliationExpectedValueTypes.Enum =>
                StatisticReconciliationExpectedAtomKinds.Enum,
            StatisticReconciliationExpectedValueTypes.StringList =>
                StatisticReconciliationExpectedAtomKinds.StringList,
            StatisticReconciliationExpectedValueTypes.Text =>
                StatisticReconciliationExpectedAtomKinds.Text,
            _ => throw Fail("SUMMARY_VALUES_OPERATION_TYPE_INVALID")
        };

    private static readonly ImmutableHashSet<string> FrozenAtomKinds =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            StatisticReconciliationExpectedAtomKinds.ReportCount,
            StatisticReconciliationExpectedAtomKinds.RowCount,
            StatisticReconciliationExpectedAtomKinds.Count,
            StatisticReconciliationExpectedAtomKinds.NumericValueCount,
            StatisticReconciliationExpectedAtomKinds.Sum,
            StatisticReconciliationExpectedAtomKinds.Min,
            StatisticReconciliationExpectedAtomKinds.Max,
            StatisticReconciliationExpectedAtomKinds.Mean,
            StatisticReconciliationExpectedAtomKinds.Bucket,
            StatisticReconciliationExpectedAtomKinds.Date,
            StatisticReconciliationExpectedAtomKinds.FullDate,
            StatisticReconciliationExpectedAtomKinds.Period,
            StatisticReconciliationExpectedAtomKinds.Boolean,
            StatisticReconciliationExpectedAtomKinds.Enum,
            StatisticReconciliationExpectedAtomKinds.StringList,
            StatisticReconciliationExpectedAtomKinds.Text,
            StatisticReconciliationExpectedAtomKinds.Missing,
            StatisticReconciliationExpectedAtomKinds.Null,
            StatisticReconciliationExpectedAtomKinds.Empty,
            "TRANSITION_KIND",
            "ADDED_COUNT",
            "REMOVED_COUNT",
            "CHANGED_COUNT",
            "UNCHANGED_COUNT",
            "DIFFERENCE");

    private static ImmutableArray<string> NormalizeSet(
        ImmutableArray<string> source,
        ImmutableHashSet<string> allowed,
        int maximum,
        string name)
    {
        var values = (source.IsDefault ? [] : source)
            .Select(item => Upper(item, name))
            .ToImmutableArray();
        if (values.Length is < 1 || values.Length > maximum ||
            values.Any(item => !allowed.Contains(item)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length ||
            !values.SequenceEqual(
                values.OrderBy(item => item, StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw Fail($"{name}_SET_INVALID");
        return values;
    }

    private static string Pointer(string? value, string name)
    {
        if (value is null || value.Length > 2048 ||
            value.Any(char.IsControl) ||
            (value.Length > 0 && !value.StartsWith("/", StringComparison.Ordinal)))
            throw Fail($"{name}_INVALID");
        if (value.Length == 0)
            return value;
        foreach (var segment in value[1..].Split('/'))
            for (var index = 0; index < segment.Length; index++)
                if (segment[index] == '~' &&
                    (++index >= segment.Length || segment[index] is not ('0' or '1')))
                    throw Fail($"{name}_INVALID");
        return value;
    }

    private static string? OptionalPointer(string? value, string name)
        => value is null ? null : Pointer(value, name);
    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static string Upper(string? value, string name)
        => StatisticReconciliationActualCanonical.Upper(value, name);
    private static string? Optional(string? value, string name)
        => value is null ? null : Required(value, name);
    private static string? OptionalUpper(string? value, string name)
        => value is null ? null : Upper(value, name);
    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);
    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_SUMMARY_PLAN:{reason}");
}

/// <summary>
/// Value-free server-derived binding to one exact validated expected generation.
/// Every scalar returned by ExpectedGenerationOwner is retained; selection by
/// latest/global cardinality is forbidden.
/// </summary>
internal sealed record StatisticReconciliationActualSummaryPlanBinding(
    string ExpectedReconciliationId,
    string ExpectedGenerationId,
    string ExpectedGenerationSha256,
    string ExpectedMetricPlanSha256,
    int ExpectedMetricPlanEntryCount,
    string ExpectedManifestSha256,
    int ExpectedDocumentCount,
    string ExpectedMembershipSemanticSha256,
    string ExpectedRuntimeKind,
    string ExpectedIdentitySetSha256,
    int MetricIdentityCount,
    ImmutableArray<StatisticReconciliationActualSummaryIdentityDescriptor>
        IdentityDescriptors,
    string SemanticSha256)
{
    internal static StatisticReconciliationActualSummaryPlanBinding Create(
        StatisticReconciliationExpectedGenerationBinding exact,
        IEnumerable<StatisticReconciliationActualSummaryIdentityDescriptor>
            identityDescriptors)
    {
        ArgumentNullException.ThrowIfNull(exact);
        var descriptors = identityDescriptors
            ?.Select(StatisticReconciliationActualSummaryIdentityDescriptor.Normalize)
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray()
            ?? throw new ArgumentNullException(nameof(identityDescriptors));
        if (descriptors.Length is < 1 or > 10_000 ||
            descriptors.Length != exact.MetricPlanEntryCount ||
            descriptors.Select(value => value.IdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != descriptors.Length)
            throw Fail("SUMMARY_EXPECTED_IDENTITY_CARDINALITY_INVALID");
        var identitySet = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_SUMMARY_EXPECTED_IDENTITY_SET_V3",
            descriptors.Select(value => value.SemanticSha256));
        var reconciliationId = Required(
            exact.ReconciliationId,
            "SUMMARY_EXPECTED_RECONCILIATION_ID");
        var generationId = Sha(
            exact.GenerationId, "SUMMARY_EXPECTED_GENERATION_ID");
        var generationSha = Sha(
            exact.GenerationSemanticSha256,
            "SUMMARY_EXPECTED_GENERATION_SHA256");
        var metricPlan = Sha(
            exact.MetricPlanSha256,
            "SUMMARY_EXPECTED_METRIC_PLAN_SHA256");
        var manifest = Sha(
            exact.ManifestSha256, "SUMMARY_EXPECTED_MANIFEST_SHA256");
        var membership = Sha(
            exact.MembershipSemanticSha256,
            "SUMMARY_EXPECTED_MEMBERSHIP_SHA256");
        var runtimeKind = Upper(
            exact.RuntimeKind, "SUMMARY_EXPECTED_RUNTIME_KIND");
        if (exact.MetricPlanEntryCount < 1 || exact.DocumentCount < 1 ||
            runtimeKind is not (
                StatisticReconciliationExpectedRuntimeKinds.Flow or
                StatisticReconciliationExpectedRuntimeKinds.NonFlow))
            throw Fail("SUMMARY_EXPECTED_BINDING_COUNTS_INVALID");
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_SUMMARY_PLAN_BINDING_V3",
            reconciliationId,
            generationId,
            generationSha,
            metricPlan,
            StatisticReconciliationActualCanonical.Integer(
                exact.MetricPlanEntryCount),
            manifest,
            StatisticReconciliationActualCanonical.Integer(exact.DocumentCount),
            membership,
            runtimeKind,
            identitySet,
            StatisticReconciliationActualCanonical.Integer(descriptors.Length));
        return new StatisticReconciliationActualSummaryPlanBinding(
            reconciliationId,
            generationId,
            generationSha,
            metricPlan,
            exact.MetricPlanEntryCount,
            manifest,
            exact.DocumentCount,
            membership,
            runtimeKind,
            identitySet,
            descriptors.Length,
            descriptors,
            semantic);
    }

    internal static StatisticReconciliationActualSummaryPlanBinding Normalize(
        StatisticReconciliationActualSummaryPlanBinding value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var exact = new StatisticReconciliationExpectedGenerationBinding(
            Required(value.ExpectedReconciliationId,
                "SUMMARY_EXPECTED_RECONCILIATION_ID"),
            value.ExpectedGenerationId,
            value.ExpectedGenerationSha256,
            value.ExpectedMetricPlanSha256,
            value.ExpectedMetricPlanEntryCount,
            value.ExpectedManifestSha256,
            value.ExpectedDocumentCount,
            value.ExpectedMembershipSemanticSha256,
            value.ExpectedRuntimeKind);
        var recomputed = Create(exact, value.IdentityDescriptors);
        var suppliedSemantic = Sha(
            value.SemanticSha256, "SUMMARY_PLAN_BINDING_SHA256");
        if (recomputed.ExpectedIdentitySetSha256 !=
                Sha(value.ExpectedIdentitySetSha256,
                    "SUMMARY_EXPECTED_IDENTITY_SET_SHA256") ||
            recomputed.MetricIdentityCount != value.MetricIdentityCount ||
            recomputed.SemanticSha256 != suppliedSemantic)
            throw Fail("SUMMARY_PLAN_BINDING_SEMANTIC_MISMATCH");
        return recomputed;
    }

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static string Upper(string? value, string name)
        => StatisticReconciliationActualCanonical.Upper(value, name);
    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);
    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_SUMMARY_PLAN:{reason}");
}

internal static class StatisticReconciliationActualSummaryMappingManifest
{
    internal static string Create(
        StatisticReconciliationActualSummaryPlanBinding binding,
        IEnumerable<(string Layer, string OwnerId, string OwnerVersionSha256,
            string TypedObservationManifestSha256)> layers)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(layers);
        var normalizedBinding =
            StatisticReconciliationActualSummaryPlanBinding.Normalize(binding);
        var normalizedLayers = layers.ToArray();
        if (normalizedLayers.Length !=
                StatisticReconciliationActualCoherentLayers.RequiredOrder.Length ||
            !normalizedLayers.Select(item => item.Layer.ToUpperInvariant())
                .SequenceEqual(
                    StatisticReconciliationActualCoherentLayers.RequiredOrder,
                    StringComparer.Ordinal))
            throw Fail("SUMMARY_LAYER_PROOF_SET_INVALID");
        var layerProofs = normalizedLayers.Select(value =>
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_SUMMARY_LAYER_MAPPING_PROOF_V3",
                StatisticReconciliationActualCanonical.Required(
                    value.Layer, "SUMMARY_LAYER").ToUpperInvariant(),
                StatisticReconciliationActualCanonical.Required(
                    value.OwnerId, "SUMMARY_LAYER_OWNER"),
                StatisticReconciliationActualCanonical.Sha256(
                    value.OwnerVersionSha256,
                    "SUMMARY_LAYER_OWNER_VERSION"),
                StatisticReconciliationActualCanonical.Sha256(
                    value.TypedObservationManifestSha256,
                    "SUMMARY_LAYER_TYPED_MANIFEST")))
            .ToArray();
        return StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_SUMMARY_MAPPING_MANIFEST_V3",
            normalizedBinding.SemanticSha256,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_SUMMARY_LAYER_MAPPING_PROOFS_V3", layerProofs));
    }

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_SUMMARY_MANIFEST:{reason}");
}