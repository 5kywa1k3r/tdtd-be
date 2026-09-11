using System.Collections.Immutable;
using System.Globalization;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.SummaryReconciliation;

public sealed class StatisticReconciliationSummaryReconciler
{
    private static readonly ImmutableHashSet<string> Families =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            StatisticReconciliationSummaryFamilies.Basic,
            StatisticReconciliationSummaryFamilies.Flow,
            StatisticReconciliationSummaryFamilies.Advanced,
            StatisticReconciliationSummaryFamilies.Diff);

    private static readonly ImmutableHashSet<string> Kinds =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            StatisticReconciliationSummaryKinds.Field,
            StatisticReconciliationSummaryKinds.TableMetric,
            StatisticReconciliationSummaryKinds.RowLabel);

    private static readonly ImmutableHashSet<string> Atoms =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            StatisticReconciliationSummaryAtomKinds.ReportCount,
            StatisticReconciliationSummaryAtomKinds.RowCount,
            StatisticReconciliationSummaryAtomKinds.Count,
            StatisticReconciliationSummaryAtomKinds.NumericValueCount,
            StatisticReconciliationSummaryAtomKinds.Sum,
            StatisticReconciliationSummaryAtomKinds.Min,
            StatisticReconciliationSummaryAtomKinds.Max,
            StatisticReconciliationSummaryAtomKinds.Mean,
            StatisticReconciliationSummaryAtomKinds.Value);

    private readonly StatisticReconciliationStatefulComparator _stateful = new();
    private readonly StatisticReconciliationTypedComparator _typed = new();

    public StatisticReconciliationSummaryIdentity CreateIdentity(
        string family,
        string kind,
        string metricId,
        string periodIdentity,
        string stableSortKey,
        string? fieldId = null,
        string? tableId = null,
        string? rowId = null,
        string? labelId = null,
        string? flowScope = null,
        string? flowScopeId = null,
        string? flowEpoch = null,
        string? grain = null,
        string? diffKind = null)
    {
        family = StatisticReconciliationSummaryCanonical.Required(family, "family");
        flowScope = StatisticReconciliationSummaryCanonical.Optional(flowScope, "flowScope");
        if (family == StatisticReconciliationSummaryFamilies.Flow &&
            flowScope == StatisticReconciliationSummaryFlowScopes.StatisticProfile)
            throw StatisticReconciliationSummaryCanonical.Fail(
                "FLOW_STATISTIC_PROFILE.targetPhase=null",
                StatisticReconciliationSummaryFailureCodes.ProfileBlocked);

        kind = StatisticReconciliationSummaryCanonical.Required(kind, "kind");
        metricId = StatisticReconciliationSummaryCanonical.Required(metricId, "metricId");
        periodIdentity = StatisticReconciliationSummaryCanonical.Required(periodIdentity, "periodIdentity");
        stableSortKey = StatisticReconciliationSummaryCanonical.Required(stableSortKey, "stableSortKey");
        fieldId = StatisticReconciliationSummaryCanonical.Optional(fieldId, "fieldId");
        tableId = StatisticReconciliationSummaryCanonical.Optional(tableId, "tableId");
        rowId = StatisticReconciliationSummaryCanonical.Optional(rowId, "rowId");
        labelId = StatisticReconciliationSummaryCanonical.Optional(labelId, "labelId");
        flowScopeId = StatisticReconciliationSummaryCanonical.Optional(flowScopeId, "flowScopeId");
        flowEpoch = StatisticReconciliationSummaryCanonical.Optional(flowEpoch, "flowEpoch");
        grain = StatisticReconciliationSummaryCanonical.Optional(grain, "grain");
        diffKind = StatisticReconciliationSummaryCanonical.Optional(diffKind, "diffKind");

        if (!Families.Contains(family) || !Kinds.Contains(kind) || !KindShape(kind, fieldId, tableId, rowId, labelId))
            throw StatisticReconciliationSummaryCanonical.Fail("identity.shape");

        switch (family)
        {
            case StatisticReconciliationSummaryFamilies.Basic:
                Require(flowScope is null && flowScopeId is null && flowEpoch is null &&
                        grain is null && diffKind is null, "basic.scope");
                break;
            case StatisticReconciliationSummaryFamilies.Flow:
                Require(flowScope is not null &&
                        StatisticReconciliationSummaryFlowScopes.Supported.Contains(flowScope) &&
                        flowScopeId is not null && flowEpoch is not null &&
                        grain is null && diffKind is null, "flow.scope");
                break;
            case StatisticReconciliationSummaryFamilies.Advanced:
                Require(flowScope is null && flowScopeId is null && flowEpoch is null &&
                        grain is not null && StatisticReconciliationSummaryGrains.Supported.Contains(grain) &&
                        diffKind is null && CanonicalPeriod(grain, periodIdentity), "advanced.period");
                break;
            case StatisticReconciliationSummaryFamilies.Diff:
                Require(flowScope is null && flowScopeId is null && flowEpoch is null && grain is null &&
                        DiffMatches(kind, diffKind), "diff.kind");
                break;
        }

        var sha = StatisticReconciliationSummaryCanonical.Hash(
            "P10_SUMMARY_IDENTITY_V1", family, kind, metricId, periodIdentity,
            fieldId, tableId, rowId, labelId, flowScope, flowScopeId, flowEpoch,
            grain, diffKind, stableSortKey);
        return new StatisticReconciliationSummaryIdentity(
            family, kind, metricId, periodIdentity, fieldId, tableId, rowId, labelId,
            flowScope, flowScopeId, flowEpoch, grain, diffKind, stableSortKey, sha);
    }

    public StatisticReconciliationSummaryObservation CreateObservation(
        StatisticReconciliationSummaryIdentity identity,
        string atomKind,
        string valueType,
        string valueState,
        string? canonicalValue,
        int decimalScale,
        long reportCount,
        long rowCount,
        long numericValueCount,
        string sourceLineageSha256,
        string? collectionSemantics = null,
        string? beforeState = null,
        string? beforeCanonicalValue = null,
        string? afterState = null,
        string? afterCanonicalValue = null,
        string? differenceKind = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _ = NormalizeIdentity(identity);
        atomKind = StatisticReconciliationSummaryCanonical.Required(atomKind, "atomKind");
        valueType = StatisticReconciliationSummaryCanonical.Required(valueType, "valueType");
        valueState = StatisticReconciliationSummaryCanonical.Required(valueState, "valueState");
        sourceLineageSha256 = StatisticReconciliationSummaryCanonical.Sha(sourceLineageSha256, "lineage");
        collectionSemantics = StatisticReconciliationSummaryCanonical.Optional(collectionSemantics, "collectionSemantics");
        if (!Atoms.Contains(atomKind) || decimalScale < 0 || reportCount < 0 || rowCount < 0 || numericValueCount < 0)
            throw StatisticReconciliationSummaryCanonical.Fail("observation.shape");

        var stateful = new StatisticReconciliationStatefulObservation(
            Stable(identity), valueType, valueState, canonicalValue, decimalScale, collectionSemantics);
        _ = _stateful.Compare(stateful, stateful);

        beforeState = StatisticReconciliationSummaryCanonical.Optional(beforeState, "beforeState");
        afterState = StatisticReconciliationSummaryCanonical.Optional(afterState, "afterState");
        differenceKind = StatisticReconciliationSummaryCanonical.Optional(differenceKind, "differenceKind");
        if (identity.Family == StatisticReconciliationSummaryFamilies.Diff)
            ValidateDiff(beforeState, beforeCanonicalValue, afterState, afterCanonicalValue, differenceKind);
        else if (beforeState is not null || beforeCanonicalValue is not null || afterState is not null ||
                 afterCanonicalValue is not null || differenceKind is not null)
            throw StatisticReconciliationSummaryCanonical.Fail("nonDiff.diffMetadata");

        var sha = StatisticReconciliationSummaryCanonical.Hash(
            "P10_SUMMARY_OBSERVATION_V1", identity.IdentitySha256, atomKind, valueType,
            valueState, canonicalValue, StatisticReconciliationSummaryCanonical.I(decimalScale),
            collectionSemantics, StatisticReconciliationSummaryCanonical.I(reportCount),
            StatisticReconciliationSummaryCanonical.I(rowCount),
            StatisticReconciliationSummaryCanonical.I(numericValueCount),
            beforeState, beforeCanonicalValue, afterState, afterCanonicalValue, differenceKind,
            sourceLineageSha256);
        return new StatisticReconciliationSummaryObservation(
            identity, atomKind, valueType, valueState, canonicalValue, decimalScale,
            collectionSemantics, reportCount, rowCount, numericValueCount,
            beforeState, beforeCanonicalValue, afterState, afterCanonicalValue,
            differenceKind, sourceLineageSha256, sha);
    }

    public StatisticReconciliationSummaryNamedTotal CreateTotal(
        string name, string valueType, string canonicalValue, int decimalScale)
    {
        name = StatisticReconciliationSummaryCanonical.Required(name, "total.name");
        valueType = StatisticReconciliationSummaryCanonical.Required(valueType, "total.type");
        canonicalValue = StatisticReconciliationSummaryCanonical.Required(canonicalValue, "total.value");
        if (decimalScale < 0)
            throw StatisticReconciliationSummaryCanonical.Fail("total.scale");
        var probe = new StatisticReconciliationStatefulObservation(
            new StatisticReconciliationStableIdentity(StatisticReconciliationStableIdentityKinds.Row,
                ["total", name]), valueType, StatisticReconciliationObservationValueStates.Value,
            canonicalValue, decimalScale);
        _ = _stateful.Compare(probe, probe);
        return new StatisticReconciliationSummaryNamedTotal(
            name, valueType, canonicalValue, decimalScale,
            StatisticReconciliationSummaryCanonical.Hash("P10_SUMMARY_TOTAL_V1", name,
                valueType, canonicalValue, StatisticReconciliationSummaryCanonical.I(decimalScale)));
    }

    public StatisticReconciliationSummaryLayer CreateLayer(
        int ordinal,
        string layer,
        string comparisonBindingSha256,
        string ownerGenerationSha256,
        string filterSha256,
        bool evidenceComplete,
        IEnumerable<StatisticReconciliationSummaryObservation> observations,
        IEnumerable<StatisticReconciliationSummaryNamedTotal> totals)
    {
        if (ordinal < 0 || ordinal >= StatisticReconciliationSummaryLayers.Ordered.Length ||
            layer != StatisticReconciliationSummaryLayers.Ordered[ordinal])
            throw StatisticReconciliationSummaryCanonical.Fail("layer.order");
        comparisonBindingSha256 = StatisticReconciliationSummaryCanonical.Sha(comparisonBindingSha256, "layer.binding");
        ownerGenerationSha256 = StatisticReconciliationSummaryCanonical.Sha(ownerGenerationSha256, "layer.generation");
        filterSha256 = StatisticReconciliationSummaryCanonical.Sha(filterSha256, "layer.filter");
        var rows = observations.Select(NormalizeObservation)
            .OrderBy(x => x.Identity.StableSortKey, StringComparer.Ordinal)
            .ThenBy(x => x.Identity.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(x => x.AtomKind, StringComparer.Ordinal).ToImmutableArray();
        if (rows.Select(Key).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw StatisticReconciliationSummaryCanonical.Fail("layer.duplicate");
        ValidateMeans(rows);
        var named = totals.Select(NormalizeTotal).OrderBy(x => x.Name, StringComparer.Ordinal).ToImmutableArray();
        if (named.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != named.Length)
            throw StatisticReconciliationSummaryCanonical.Fail("totals.duplicate");
        var hash = StatisticReconciliationSummaryCanonical.Hash(
            "P10_SUMMARY_LAYER_V1", StatisticReconciliationSummaryCanonical.I(ordinal), layer,
            comparisonBindingSha256, ownerGenerationSha256, filterSha256, evidenceComplete ? "1" : "0",
            StatisticReconciliationSummaryCanonical.HashSequence("P10_SUMMARY_ROWS_V1", rows.Select(x => x.ObservationSemanticSha256)),
            StatisticReconciliationSummaryCanonical.HashSequence("P10_SUMMARY_TOTALS_V1", named.Select(x => x.SemanticSha256)));
        return new StatisticReconciliationSummaryLayer(
            ordinal, layer, comparisonBindingSha256, ownerGenerationSha256, filterSha256,
            evidenceComplete, rows, named, hash);
    }

    public StatisticReconciliationSummaryHierarchyEdge CreateHierarchyEdge(
        string childPeriodIdentity, string parentPeriodIdentity, string childGrain, string parentGrain)
    {
        childPeriodIdentity = StatisticReconciliationSummaryCanonical.Required(childPeriodIdentity, "hierarchy.child");
        parentPeriodIdentity = StatisticReconciliationSummaryCanonical.Required(parentPeriodIdentity, "hierarchy.parent");
        childGrain = StatisticReconciliationSummaryCanonical.Required(childGrain, "hierarchy.childGrain");
        parentGrain = StatisticReconciliationSummaryCanonical.Required(parentGrain, "hierarchy.parentGrain");
        Require((childGrain == StatisticReconciliationSummaryGrains.Day && parentGrain == StatisticReconciliationSummaryGrains.Month) ||
                (childGrain == StatisticReconciliationSummaryGrains.Month && parentGrain == StatisticReconciliationSummaryGrains.Year),
            "hierarchy.grain");
        Require(CanonicalPeriod(childGrain, childPeriodIdentity) && CanonicalPeriod(parentGrain, parentPeriodIdentity),
            "hierarchy.period");
        return new StatisticReconciliationSummaryHierarchyEdge(
            childPeriodIdentity, parentPeriodIdentity, childGrain, parentGrain,
            StatisticReconciliationSummaryCanonical.Hash("P10_SUMMARY_HIERARCHY_EDGE_V1",
                childPeriodIdentity, parentPeriodIdentity, childGrain, parentGrain));
    }

    public StatisticReconciliationSummaryResult Reconcile(StatisticReconciliationSummaryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = StatisticReconciliationSummaryCanonical.Required(request.ReconciliationId, "request.id");
        var binding = StatisticReconciliationSummaryCanonical.Sha(request.ComparisonBindingSha256, "request.binding");
        var filter = StatisticReconciliationSummaryCanonical.Sha(request.FilterSha256, "request.filter");
        var expectedGeneration = StatisticReconciliationSummaryCanonical.Required(request.ExpectedGenerationId, "request.expectedGeneration");
        var actualGeneration = StatisticReconciliationSummaryCanonical.Required(request.ActualGenerationId, "request.actualGeneration");
        var layers = NormalizeLayers(request.Layers);
        var complete = layers.All(x => x.EvidenceComplete && x.ComparisonBindingSha256 == binding && x.FilterSha256 == filter);
        var currentEpoch = StatisticReconciliationSummaryCanonical.Optional(request.CurrentFlowEpoch, "request.currentEpoch");
        var epochCoherent = layers.SelectMany(x => x.Observations)
            .Where(x => x.Identity.Family == StatisticReconciliationSummaryFamilies.Flow)
            .All(x => currentEpoch is not null && x.Identity.FlowEpoch == currentEpoch);
        var apiExportBinding = layers.Where(x => x.Layer is StatisticReconciliationSummaryLayers.Api or StatisticReconciliationSummaryLayers.Export)
            .All(x => x.ComparisonBindingSha256 == binding && x.FilterSha256 == filter && x.EvidenceComplete);

        ValidateHierarchy(layers[0], request.Hierarchy);
        if (!complete || !epochCoherent || !apiExportBinding)
            return BuildResult(StatisticReconciliationSummaryOutcomes.Stale, id, expectedGeneration,
                actualGeneration, false, false, apiExportBinding, null, null, [], binding);

        var source = layers[0];
        var results = layers.Select(layer => CompareLayer(source, layer)).ToImmutableArray();
        var divergent = results.FirstOrDefault(x => x.NonzeroCount > 0 || !x.TotalsEqual);
        var zero = divergent is null;
        var root = divergent is null ? null : Root(divergent);
        return BuildResult(zero ? StatisticReconciliationSummaryOutcomes.Matched : StatisticReconciliationSummaryOutcomes.Mismatched,
            id, expectedGeneration, actualGeneration, true, zero, apiExportBinding,
            divergent?.Layer, root, results, binding);
    }

    private StatisticReconciliationSummaryLayerResult CompareLayer(
        StatisticReconciliationSummaryLayer expected, StatisticReconciliationSummaryLayer actual)
    {
        var left = expected.Observations.ToDictionary(Key, StringComparer.Ordinal);
        var right = actual.Observations.ToDictionary(Key, StringComparer.Ordinal);
        var keys = left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        var deltas = ImmutableArray.CreateBuilder<StatisticReconciliationSummaryDelta>();
        var count = 0;
        foreach (var key in keys)
        {
            left.TryGetValue(key, out var e);
            right.TryGetValue(key, out var a);
            var comparison = _stateful.Compare(e is null ? null : Stateful(e), a is null ? null : Stateful(a));
            var metadataEqual = e is null || a is null || MetadataEqual(e, a);
            var deltaCode = comparison.Equal && metadataEqual
                ? StatisticReconciliationIdentityDeltaCodes.None
                : comparison.Equal ? StatisticReconciliationIdentityDeltaCodes.ValueMismatch : comparison.DeltaCode;
            deltas.Add(new StatisticReconciliationSummaryDelta(actual.Layer,
                e?.Identity.IdentitySha256 ?? a!.Identity.IdentitySha256,
                e?.AtomKind ?? a!.AtomKind, deltaCode,
                StatisticReconciliationSummaryCanonical.Hash("P10_SUMMARY_COMPARISON_V1",
                    comparison.ComparisonSha256, metadataEqual ? "1" : "0")));
            count++;
        }
        var totalsEqual = TotalsEqual(expected.FullFilterTotals, actual.FullFilterTotals);
        var built = deltas.ToImmutable();
        return new StatisticReconciliationSummaryLayerResult(
            actual.Ordinal, actual.Layer, count,
            built.Count(x => x.DeltaCode != StatisticReconciliationIdentityDeltaCodes.None),
            built.Count(x => x.DeltaCode == StatisticReconciliationIdentityDeltaCodes.MissingIdentity),
            built.Count(x => x.DeltaCode == StatisticReconciliationIdentityDeltaCodes.ExtraIdentity),
            totalsEqual, built,
            StatisticReconciliationSummaryCanonical.HashSequence("P10_SUMMARY_DELTA_MANIFEST_V1",
                built.Select(x => x.ComparisonSha256).Append(totalsEqual ? "1" : "0")));
    }

    private void ValidateMeans(ImmutableArray<StatisticReconciliationSummaryObservation> rows)
    {
        foreach (var group in rows.GroupBy(x => x.Identity.IdentitySha256, StringComparer.Ordinal))
        {
            var mean = group.SingleOrDefault(x => x.AtomKind == StatisticReconciliationSummaryAtomKinds.Mean);
            if (mean is null) continue;
            var sum = group.SingleOrDefault(x => x.AtomKind == StatisticReconciliationSummaryAtomKinds.Sum);
            var count = group.SingleOrDefault(x => x.AtomKind == StatisticReconciliationSummaryAtomKinds.NumericValueCount);
            if (sum is null || count is null || sum.CanonicalValue is null || count.CanonicalValue is null ||
                !long.TryParse(count.CanonicalValue, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0 ||
                mean.NumericValueCount != n || sum.NumericValueCount != n || count.NumericValueCount != n)
                throw StatisticReconciliationSummaryCanonical.Fail("mean.companions", StatisticReconciliationSummaryFailureCodes.MeanInvalid);
            var comparison = _typed.CompareMean(
                new StatisticReconciliationNumericAggregate(new StatisticReconciliationCanonicalNumber(sum.CanonicalValue, sum.DecimalScale), n),
                new StatisticReconciliationNumericAggregate(new StatisticReconciliationCanonicalNumber(sum.CanonicalValue, sum.DecimalScale), n));
            if (comparison.ExpectedMean.CanonicalValue != mean.CanonicalValue ||
                comparison.ExpectedMean.DecimalScale != mean.DecimalScale)
                throw StatisticReconciliationSummaryCanonical.Fail("mean.value", StatisticReconciliationSummaryFailureCodes.MeanInvalid);
        }
    }

    private static void ValidateHierarchy(
        StatisticReconciliationSummaryLayer source,
        ImmutableArray<StatisticReconciliationSummaryHierarchyEdge> hierarchy)
    {
        var edges = hierarchy.Select(edge =>
        {
            var canonical = StatisticReconciliationSummaryCanonical.Hash("P10_SUMMARY_HIERARCHY_EDGE_V1",
                edge.ChildPeriodIdentity, edge.ParentPeriodIdentity, edge.ChildGrain, edge.ParentGrain);
            if (canonical != edge.SemanticSha256)
                throw StatisticReconciliationSummaryCanonical.Fail("hierarchy.hash", StatisticReconciliationSummaryFailureCodes.HierarchyInvalid);
            return edge;
        }).OrderBy(x => x.ChildPeriodIdentity, StringComparer.Ordinal).ToArray();
        if (edges.Select(x => $"{x.ChildPeriodIdentity}\u001f{x.ParentPeriodIdentity}").Distinct(StringComparer.Ordinal).Count() != edges.Length)
            throw StatisticReconciliationSummaryCanonical.Fail("hierarchy.duplicate", StatisticReconciliationSummaryFailureCodes.HierarchyInvalid);

        foreach (var group in edges.GroupBy(x => x.ParentPeriodIdentity, StringComparer.Ordinal))
        {
            var childPeriods = group.Select(x => x.ChildPeriodIdentity).ToHashSet(StringComparer.Ordinal);
            var candidates = source.Observations.Where(x => x.Identity.Family == StatisticReconciliationSummaryFamilies.Advanced &&
                childPeriods.Contains(x.Identity.PeriodIdentity) && RollupAtom(x.AtomKind) &&
                x.ValueState == StatisticReconciliationObservationValueStates.Value && x.CanonicalValue is not null)
                .GroupBy(x => $"{x.Identity.Kind}\u001f{x.Identity.MetricId}\u001f{x.AtomKind}", StringComparer.Ordinal);
            foreach (var children in candidates)
            {
                decimal sum;
                try { sum = children.Sum(x => decimal.Parse(x.CanonicalValue!, CultureInfo.InvariantCulture)); }
                catch { throw StatisticReconciliationSummaryCanonical.Fail("hierarchy.numeric", StatisticReconciliationSummaryFailureCodes.HierarchyInvalid); }
                var sample = children.First();
                var parent = source.Observations.SingleOrDefault(x =>
                    x.Identity.Family == StatisticReconciliationSummaryFamilies.Advanced &&
                    x.Identity.PeriodIdentity == group.Key && x.Identity.Kind == sample.Identity.Kind &&
                    x.Identity.MetricId == sample.Identity.MetricId && x.AtomKind == sample.AtomKind);
                if (parent?.CanonicalValue is null || decimal.Parse(parent.CanonicalValue, CultureInfo.InvariantCulture) != sum)
                    throw StatisticReconciliationSummaryCanonical.Fail("hierarchy.rollup", StatisticReconciliationSummaryFailureCodes.HierarchyInvalid);
            }
        }
    }

    private static StatisticReconciliationSummaryResult BuildResult(
        string outcome, string id, string expectedGeneration, string actualGeneration,
        bool complete, bool zero, bool binding, string? layer, string? root,
        ImmutableArray<StatisticReconciliationSummaryLayerResult> layers, string comparisonBinding)
    {
        var evidence = StatisticReconciliationSummaryCanonical.HashSequence("P10_SUMMARY_EVIDENCE_V1",
            layers.Select(x => x.DeltaManifestSha256));
        var semantic = StatisticReconciliationSummaryCanonical.Hash("P10_SUMMARY_RESULT_V1",
            outcome, id, expectedGeneration, actualGeneration, complete ? "1" : "0", zero ? "1" : "0",
            binding ? "1" : "0", layer, root, comparisonBinding, evidence);
        return new StatisticReconciliationSummaryResult(outcome, id, expectedGeneration,
            actualGeneration, complete, zero, binding, false, layer, root, layers, evidence, semantic);
    }

    private static ImmutableArray<StatisticReconciliationSummaryLayer> NormalizeLayers(
        ImmutableArray<StatisticReconciliationSummaryLayer> values)
    {
        if (values.Length != StatisticReconciliationSummaryLayers.Ordered.Length)
            throw StatisticReconciliationSummaryCanonical.Fail("layers.count");
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value.Ordinal != i || value.Layer != StatisticReconciliationSummaryLayers.Ordered[i])
                throw StatisticReconciliationSummaryCanonical.Fail("layers.order");
            var expected = new StatisticReconciliationSummaryReconciler().CreateLayer(
                value.Ordinal, value.Layer, value.ComparisonBindingSha256, value.OwnerGenerationSha256,
                value.FilterSha256, value.EvidenceComplete, value.Observations, value.FullFilterTotals);
            if (expected.LayerSemanticSha256 != value.LayerSemanticSha256)
                throw StatisticReconciliationSummaryCanonical.Fail("layer.hash");
        }
        return values;
    }

    private static StatisticReconciliationSummaryIdentity NormalizeIdentity(StatisticReconciliationSummaryIdentity value)
    {
        var expected = new StatisticReconciliationSummaryReconciler().CreateIdentity(
            value.Family, value.Kind, value.MetricId, value.PeriodIdentity, value.StableSortKey,
            value.FieldId, value.TableId, value.RowId, value.LabelId, value.FlowScope,
            value.FlowScopeId, value.FlowEpoch, value.Grain, value.DiffKind);
        if (expected.IdentitySha256 != value.IdentitySha256)
            throw StatisticReconciliationSummaryCanonical.Fail("identity.hash");
        return value;
    }

    private StatisticReconciliationSummaryObservation NormalizeObservation(StatisticReconciliationSummaryObservation value)
    {
        var expected = CreateObservation(value.Identity, value.AtomKind, value.ValueType, value.ValueState,
            value.CanonicalValue, value.DecimalScale, value.ReportCount, value.RowCount,
            value.NumericValueCount, value.SourceLineageSha256, value.CollectionSemantics,
            value.BeforeState, value.BeforeCanonicalValue, value.AfterState,
            value.AfterCanonicalValue, value.DifferenceKind);
        if (expected.ObservationSemanticSha256 != value.ObservationSemanticSha256)
            throw StatisticReconciliationSummaryCanonical.Fail("observation.hash");
        return value;
    }

    private StatisticReconciliationSummaryNamedTotal NormalizeTotal(StatisticReconciliationSummaryNamedTotal value)
    {
        var expected = CreateTotal(value.Name, value.ValueType, value.CanonicalValue, value.DecimalScale);
        if (expected.SemanticSha256 != value.SemanticSha256)
            throw StatisticReconciliationSummaryCanonical.Fail("total.hash");
        return value;
    }

    private static bool MetadataEqual(StatisticReconciliationSummaryObservation x, StatisticReconciliationSummaryObservation y)
        => x.ReportCount == y.ReportCount && x.RowCount == y.RowCount &&
           x.NumericValueCount == y.NumericValueCount && x.BeforeState == y.BeforeState &&
           x.BeforeCanonicalValue == y.BeforeCanonicalValue && x.AfterState == y.AfterState &&
           x.AfterCanonicalValue == y.AfterCanonicalValue && x.DifferenceKind == y.DifferenceKind &&
           x.SourceLineageSha256 == y.SourceLineageSha256;

    private static bool TotalsEqual(ImmutableArray<StatisticReconciliationSummaryNamedTotal> x,
        ImmutableArray<StatisticReconciliationSummaryNamedTotal> y)
        => x.Select(v => v.SemanticSha256).SequenceEqual(y.Select(v => v.SemanticSha256), StringComparer.Ordinal);

    private static string Root(StatisticReconciliationSummaryLayerResult result)
    {
        if (result.MissingCount > 0) return StatisticReconciliationSummaryRootCauses.Missing;
        if (result.ExtraCount > 0) return StatisticReconciliationSummaryRootCauses.Extra;
        return result.Layer switch
        {
            StatisticReconciliationSummaryLayers.SourceLedger => StatisticReconciliationSummaryRootCauses.Source,
            StatisticReconciliationSummaryLayers.Aggregate => StatisticReconciliationSummaryRootCauses.Aggregate,
            StatisticReconciliationSummaryLayers.Result => StatisticReconciliationSummaryRootCauses.Result,
            StatisticReconciliationSummaryLayers.Api => StatisticReconciliationSummaryRootCauses.Api,
            StatisticReconciliationSummaryLayers.Export => StatisticReconciliationSummaryRootCauses.Export,
            _ => throw StatisticReconciliationSummaryCanonical.Fail("root.layer")
        };
    }

    private static void ValidateDiff(string? beforeState, string? beforeValue, string? afterState,
        string? afterValue, string? differenceKind)
    {
        var missing = StatisticReconciliationObservationValueStates.Missing;
        var valid = differenceKind switch
        {
            StatisticReconciliationSummaryDifferenceKinds.Added => beforeState == missing && afterState is not null && afterState != missing,
            StatisticReconciliationSummaryDifferenceKinds.Removed => beforeState is not null && beforeState != missing && afterState == missing,
            StatisticReconciliationSummaryDifferenceKinds.Changed => beforeState is not null && afterState is not null &&
                beforeState != missing && afterState != missing && (beforeState != afterState || beforeValue != afterValue),
            StatisticReconciliationSummaryDifferenceKinds.Unchanged => beforeState is not null && beforeState == afterState && beforeValue == afterValue,
            _ => false
        };
        if (!valid)
            throw StatisticReconciliationSummaryCanonical.Fail("diff.transition", StatisticReconciliationSummaryFailureCodes.DiffInvalid);
    }

    private static bool KindShape(string kind, string? field, string? table, string? row, string? label)
        => kind switch
        {
            StatisticReconciliationSummaryKinds.Field => field is not null && table is null && row is null && label is null,
            StatisticReconciliationSummaryKinds.TableMetric => field is null && table is not null && row is null && label is null,
            StatisticReconciliationSummaryKinds.RowLabel => field is null && table is not null && row is not null && label is not null,
            _ => false
        };

    private static bool DiffMatches(string kind, string? diffKind)
        => (kind, diffKind) is
            (StatisticReconciliationSummaryKinds.Field, StatisticReconciliationSummaryDiffKinds.Field) or
            (StatisticReconciliationSummaryKinds.TableMetric, StatisticReconciliationSummaryDiffKinds.TableMetric) or
            (StatisticReconciliationSummaryKinds.RowLabel, StatisticReconciliationSummaryDiffKinds.RowLabel);

    private static bool CanonicalPeriod(string grain, string period)
        => grain switch
        {
            StatisticReconciliationSummaryGrains.Day => period.Length == 14 && period.StartsWith("DAY:", StringComparison.Ordinal) &&
                DateOnly.TryParseExact(period[4..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            StatisticReconciliationSummaryGrains.Month => period.Length == 13 && period.StartsWith("MONTH:", StringComparison.Ordinal) &&
                DateOnly.TryParseExact(period[6..] + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            StatisticReconciliationSummaryGrains.Year => period.Length == 9 && period.StartsWith("YEAR:", StringComparison.Ordinal) &&
                int.TryParse(period[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1 and <= 9999,
            _ => false
        };

    private static bool RollupAtom(string atom) => atom is
        StatisticReconciliationSummaryAtomKinds.ReportCount or
        StatisticReconciliationSummaryAtomKinds.RowCount or
        StatisticReconciliationSummaryAtomKinds.Count or
        StatisticReconciliationSummaryAtomKinds.NumericValueCount or
        StatisticReconciliationSummaryAtomKinds.Sum;

    private static string Key(StatisticReconciliationSummaryObservation value)
        => $"{value.Identity.IdentitySha256}\u001f{value.AtomKind}";

    private static StatisticReconciliationStatefulObservation Stateful(StatisticReconciliationSummaryObservation value)
        => new(Stable(value.Identity), value.ValueType, value.ValueState,
            value.CanonicalValue, value.DecimalScale, value.CollectionSemantics);

    private static StatisticReconciliationStableIdentity Stable(StatisticReconciliationSummaryIdentity value)
        => new(value.Kind switch
        {
            StatisticReconciliationSummaryKinds.TableMetric => StatisticReconciliationStableIdentityKinds.Table,
            StatisticReconciliationSummaryKinds.RowLabel => StatisticReconciliationStableIdentityKinds.Label,
            _ => StatisticReconciliationStableIdentityKinds.Row
        }, [value.Family, value.Kind, value.MetricId, value.PeriodIdentity,
            value.FieldId ?? "~", value.TableId ?? "~", value.RowId ?? "~", value.LabelId ?? "~",
            value.FlowScope ?? "~", value.FlowScopeId ?? "~", value.FlowEpoch ?? "~",
            value.Grain ?? "~", value.DiffKind ?? "~"]);

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw StatisticReconciliationSummaryCanonical.Fail(detail);
    }
}
