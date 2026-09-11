using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.Services.StatisticsReconciliation.SummaryReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

var cases = new (string Id, Action Run)[]
{
    ("P10-BASIC-01", BasicExact),
    ("P10-BASIC-02", BasicCountsIndependent),
    ("P10-BASIC-03", BasicMeanDenominator),
    ("P10-BASIC-04", BasicNegativeAndZero),
    ("P10-BASIC-05", BasicAggregateRootRecovery),
    ("P10-BASIC-06", BasicResultRootRecovery),
    ("P10-BASIC-07", BasicApiTotalsCannotHide),
    ("P10-BASIC-08", BasicExportBindingRecovery),
    ("P10-FLOW-01", () => FlowExact(StatisticReconciliationSummaryFlowScopes.Branch)),
    ("P10-FLOW-02", () => FlowExact(StatisticReconciliationSummaryFlowScopes.Step)),
    ("P10-FLOW-03", () => FlowExact(StatisticReconciliationSummaryFlowScopes.EffectivePath)),
    ("P10-FLOW-04", () => FlowExact(StatisticReconciliationSummaryFlowScopes.Final)),
    ("P10-FLOW-05", FlowEpochDriftIsStale),
    ("P10-FLOW-06", FlowProfileBlockedZeroWrite),
    ("P10-FLOW-07", FlowMissingMembershipRecovery),
    ("P10-FLOW-08", FlowAllFourScopesExact),
    ("P10-ADVANCED-01", () => AdvancedExact(StatisticReconciliationSummaryGrains.Day, "DAY:2026-08-11")),
    ("P10-ADVANCED-02", () => AdvancedExact(StatisticReconciliationSummaryGrains.Month, "MONTH:2026-08")),
    ("P10-ADVANCED-03", () => AdvancedExact(StatisticReconciliationSummaryGrains.Year, "YEAR:2026")),
    ("P10-ADVANCED-04", AdvancedHierarchyRollupExact),
    ("P10-ADVANCED-05", AdvancedHierarchyWrongRollupRejects),
    ("P10-ADVANCED-06", AdvancedLocaleRejectedAndRecovery),
    ("P10-DIFF-01", () => DiffExact(StatisticReconciliationSummaryKinds.Field,
        StatisticReconciliationSummaryDifferenceKinds.Added)),
    ("P10-DIFF-02", () => DiffExact(StatisticReconciliationSummaryKinds.TableMetric,
        StatisticReconciliationSummaryDifferenceKinds.Removed)),
    ("P10-DIFF-03", DiffChangedNullEmptyDistinct),
    ("P10-DIFF-04", DiffKindsStayTyped),
    ("P10-DIFF-05", DiffResultMismatchRecovery),
    ("P10-DIFF-06", DiffApiExportBindingAndHashGuard)
};

var expected = Enumerable.Range(1, 8).Select(i => $"P10-BASIC-{i:00}")
    .Concat(Enumerable.Range(1, 8).Select(i => $"P10-FLOW-{i:00}"))
    .Concat(Enumerable.Range(1, 6).Select(i => $"P10-ADVANCED-{i:00}"))
    .Concat(Enumerable.Range(1, 6).Select(i => $"P10-DIFF-{i:00}"))
    .ToArray();
Equal(expected, cases.Select(x => x.Id).ToArray(), "EXACT_P10_SUMMARY_REGISTRY");

var passed = 0;
foreach (var test in cases)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Id}");
        passed++;
    }
    catch (Exception error)
    {
        Console.WriteLine($"FAIL {test.Id} {error.GetType().Name}:{error.Message}");
        return 1;
    }
}

Require(passed == 28, "EXACT_28_REQUIRED");
Console.WriteLine(
    "P10_T23_SUMMARY_RECONCILIATION_OK cases=28 basic=8 flow=8 advanced=6 diff=6 " +
    "flowScopes=4 profileBlocked=true meanDenominator=true hierarchy=true typedDiff=true " +
    "apiExportBinding=true earliestRoot=true recovery=true p9Writes=0 cumulative=164 stopBefore=P10-T24");
return 0;

static void BasicExact() => Matched(BuildBasic().Result(), "BASIC_EXACT");

static void BasicCountsIndependent()
{
    var f = BuildBasic();
    var value = f.Request.Layers[0].Observations.First(x => x.AtomKind == StatisticReconciliationSummaryAtomKinds.Value);
    Require(value.ReportCount == 2 && value.RowCount == 3 && value.NumericValueCount == 2,
        "BASIC_COUNTS_SEPARATE");
    Matched(f.Result(), "BASIC_COUNTS_MATCH");
}

static void BasicMeanDenominator()
{
    var f = BuildBasic();
    var layers = f.Request.Layers.ToArray();
    var rows = layers[1].Observations.Select(x =>
        x.AtomKind == StatisticReconciliationSummaryAtomKinds.Mean
            ? f.R.CreateObservation(x.Identity, x.AtomKind, x.ValueType, x.ValueState,
                "10", 0, x.ReportCount, x.RowCount, 1, x.SourceLineageSha256)
            : x.AtomKind is StatisticReconciliationSummaryAtomKinds.Sum or
                StatisticReconciliationSummaryAtomKinds.NumericValueCount
                ? f.R.CreateObservation(x.Identity, x.AtomKind, x.ValueType, x.ValueState,
                    x.AtomKind == StatisticReconciliationSummaryAtomKinds.Sum ? "10" : "1", 0,
                    x.ReportCount, x.RowCount, 1, x.SourceLineageSha256)
                : x).ToArray();
    layers[1] = f.R.CreateLayer(1, StatisticReconciliationSummaryLayers.Aggregate, f.Binding,
        Sha("aggregate"), f.Filter, true, rows, layers[1].FullFilterTotals);
    Mismatched(f.Result(f.Request with { Layers = layers.ToImmutableArray() }),
        StatisticReconciliationSummaryLayers.Aggregate,
        StatisticReconciliationSummaryRootCauses.Aggregate, "WRONG_MEAN_DENOMINATOR");
    Matched(f.Result(), "MEAN_RECOVERY");
}

static void BasicNegativeAndZero()
{
    var f = BuildBasic(sum: "0", min: "-5", max: "5", mean: "0");
    Matched(f.Result(), "NEGATIVE_ZERO_EXACT");
}

static void BasicAggregateRootRecovery()
{
    var f = BuildBasic();
    var mismatch = f.Result(MutateValue(f, 1, "99"));
    Mismatched(mismatch, StatisticReconciliationSummaryLayers.Aggregate,
        StatisticReconciliationSummaryRootCauses.Aggregate, "BASIC_AGGREGATE_ROOT");
    Matched(f.Result(Rebind(f, "basic-aggregate-recheck")), "BASIC_AGGREGATE_RECOVERY");
}

static void BasicResultRootRecovery()
{
    var f = BuildBasic();
    Mismatched(f.Result(MutateValue(f, 2, "98")), StatisticReconciliationSummaryLayers.Result,
        StatisticReconciliationSummaryRootCauses.Result, "BASIC_RESULT_ROOT");
    Matched(f.Result(Rebind(f, "basic-result-recheck")), "BASIC_RESULT_RECOVERY");
}

static void BasicApiTotalsCannotHide()
{
    var f = BuildBasic();
    var layers = f.Request.Layers.ToArray();
    layers[3] = f.R.CreateLayer(3, StatisticReconciliationSummaryLayers.Api, f.Binding,
        Sha("api"), f.Filter, true, layers[3].Observations,
        [f.R.CreateTotal("totalRows", StatisticReconciliationStatefulValueTypes.Number, "999", 0)]);
    var result = f.Result(f.Request with { Layers = layers.ToImmutableArray() });
    Mismatched(result, StatisticReconciliationSummaryLayers.Api,
        StatisticReconciliationSummaryRootCauses.Api, "API_TOTAL_ROOT");
    Matched(f.Result(), "API_TOTAL_RECOVERY");
}

static void BasicExportBindingRecovery()
{
    var f = BuildBasic();
    var layers = f.Request.Layers.ToArray();
    layers[4] = f.R.CreateLayer(4, StatisticReconciliationSummaryLayers.Export,
        Sha("foreign-binding"), Sha("export"), f.Filter, true,
        layers[4].Observations, layers[4].FullFilterTotals);
    Equal(StatisticReconciliationSummaryOutcomes.Stale,
        f.Result(f.Request with { Layers = layers.ToImmutableArray() }).Outcome,
        "EXPORT_BINDING_STALE");
    Matched(f.Result(Rebind(f, "basic-export-recheck")), "EXPORT_BINDING_RECOVERY");
}

static void FlowExact(string scope)
{
    var f = BuildFlow(scope);
    Matched(f.Result(), scope);
    Require(f.Request.Layers.SelectMany(x => x.Observations).All(x =>
        x.Identity.FlowScope == scope && x.Identity.FlowEpoch == "epoch-7"), "FLOW_SCOPE_EPOCH_BOUND");
}

static void FlowEpochDriftIsStale()
{
    var f = BuildFlow(StatisticReconciliationSummaryFlowScopes.Step);
    Equal(StatisticReconciliationSummaryOutcomes.Stale,
        f.Result(f.Request with { CurrentFlowEpoch = "epoch-8" }).Outcome,
        "FLOW_EPOCH_DRIFT_STALE");
    Matched(f.Result(Rebind(f, "flow-epoch-recheck")), "FLOW_EPOCH_RECOVERY");
}

static void FlowProfileBlockedZeroWrite()
{
    var calls = 0;
    var r = new StatisticReconciliationSummaryReconciler();
    ExpectCode(() =>
    {
        _ = r.CreateIdentity(StatisticReconciliationSummaryFamilies.Flow,
            StatisticReconciliationSummaryKinds.Field, "metric", "MONTH:2026-08", "sort",
            fieldId: "field", flowScope: StatisticReconciliationSummaryFlowScopes.StatisticProfile,
            flowScopeId: "profile", flowEpoch: "epoch-7");
        calls++;
    }, StatisticReconciliationSummaryFailureCodes.ProfileBlocked, "PROFILE_INTENTIONAL_BLOCK");
    Equal(0, calls, "PROFILE_ZERO_WRITE");
}

static void FlowMissingMembershipRecovery()
{
    var f = BuildFlow(StatisticReconciliationSummaryFlowScopes.Branch);
    var layers = f.Request.Layers.ToArray();
    var rows = layers[1].Observations.Where(x => x.AtomKind != StatisticReconciliationSummaryAtomKinds.Value);
    layers[1] = f.R.CreateLayer(1, layers[1].Layer, f.Binding, Sha("aggregate"), f.Filter,
        true, rows, layers[1].FullFilterTotals);
    var result = f.Result(f.Request with { Layers = layers.ToImmutableArray() });
    Mismatched(result, StatisticReconciliationSummaryLayers.Aggregate,
        StatisticReconciliationSummaryRootCauses.Missing, "FLOW_MISSING_MEMBERSHIP");
    Matched(f.Result(Rebind(f, "flow-membership-recheck")), "FLOW_MEMBERSHIP_RECOVERY");
}

static void FlowAllFourScopesExact()
{
    foreach (var scope in StatisticReconciliationSummaryFlowScopes.Supported.OrderBy(x => x, StringComparer.Ordinal))
        Matched(BuildFlow(scope).Result(), $"ALL_SCOPES_{scope}");
    Equal(4, StatisticReconciliationSummaryFlowScopes.Supported.Count, "EXACT_FOUR_FLOW_SCOPES");
}

static void AdvancedExact(string grain, string period)
{
    var f = BuildAdvanced(grain, period);
    Matched(f.Result(), $"ADVANCED_{grain}");
    Require(f.Request.Layers[0].Observations.All(x => x.Identity.PeriodIdentity == period &&
        x.Identity.Grain == grain), "CANONICAL_GRAIN_PERIOD");
}

static void AdvancedHierarchyRollupExact()
{
    var f = BuildHierarchy(wrongMonth: false);
    Matched(f.Result(), "HIERARCHY_ROLLUP_EXACT");
    Equal(3, f.Request.Hierarchy.Length, "DAY_MONTH_YEAR_EDGES");
}

static void AdvancedHierarchyWrongRollupRejects()
{
    var f = BuildHierarchy(wrongMonth: true);
    ExpectCode(() => f.Result(), StatisticReconciliationSummaryFailureCodes.HierarchyInvalid,
        "WRONG_HIERARCHY_ROLLUP");
    Matched(BuildHierarchy(false).Result(), "HIERARCHY_RECOVERY");
}

static void AdvancedLocaleRejectedAndRecovery()
{
    var r = new StatisticReconciliationSummaryReconciler();
    ExpectCode(() => _ = r.CreateIdentity(StatisticReconciliationSummaryFamilies.Advanced,
        StatisticReconciliationSummaryKinds.Field, "metric", "DAY:11/08/2026", "sort",
        fieldId: "field", grain: StatisticReconciliationSummaryGrains.Day),
        StatisticReconciliationSummaryFailureCodes.Invalid, "LOCALE_PERIOD_REJECTED");
    var f = BuildAdvanced(StatisticReconciliationSummaryGrains.Day, "DAY:2026-08-11");
    Mismatched(f.Result(MutateValue(f, 2, "97")), StatisticReconciliationSummaryLayers.Result,
        StatisticReconciliationSummaryRootCauses.Result, "ADVANCED_RESULT_ROOT");
    Matched(f.Result(Rebind(f, "advanced-recheck")), "ADVANCED_RECOVERY");
}

static void DiffExact(string kind, string transition)
{
    var f = BuildDiff(kind, transition);
    Matched(f.Result(), $"DIFF_{kind}_{transition}");
}

static void DiffChangedNullEmptyDistinct()
{
    var f = BuildDiff(StatisticReconciliationSummaryKinds.Field,
        StatisticReconciliationSummaryDifferenceKinds.Changed,
        StatisticReconciliationObservationValueStates.Null, null,
        StatisticReconciliationObservationValueStates.Empty, "");
    Matched(f.Result(), "NULL_EMPTY_CHANGED");
    var row = f.Request.Layers[0].Observations.Single();
    Require(row.BeforeState == StatisticReconciliationObservationValueStates.Null &&
        row.AfterState == StatisticReconciliationObservationValueStates.Empty,
        "NULL_EMPTY_DISTINCT");
}

static void DiffKindsStayTyped()
{
    foreach (var kind in new[]
             {
                 StatisticReconciliationSummaryKinds.Field,
                 StatisticReconciliationSummaryKinds.TableMetric,
                 StatisticReconciliationSummaryKinds.RowLabel
             })
        Matched(BuildDiff(kind, StatisticReconciliationSummaryDifferenceKinds.Changed).Result(),
            $"TYPED_DIFF_{kind}");
}

static void DiffResultMismatchRecovery()
{
    var f = BuildDiff(StatisticReconciliationSummaryKinds.RowLabel,
        StatisticReconciliationSummaryDifferenceKinds.Changed);
    Mismatched(f.Result(MutateValue(f, 2, "forged")), StatisticReconciliationSummaryLayers.Result,
        StatisticReconciliationSummaryRootCauses.Result, "DIFF_RESULT_ROOT");
    Matched(f.Result(Rebind(f, "diff-result-recheck")), "DIFF_RESULT_RECOVERY");
}

static void DiffApiExportBindingAndHashGuard()
{
    var f = BuildDiff(StatisticReconciliationSummaryKinds.TableMetric,
        StatisticReconciliationSummaryDifferenceKinds.Changed);
    var layers = f.Request.Layers.ToArray();
    layers[3] = f.R.CreateLayer(3, layers[3].Layer, f.Binding, Sha("api"),
        Sha("foreign-filter"), true, layers[3].Observations, layers[3].FullFilterTotals);
    Equal(StatisticReconciliationSummaryOutcomes.Stale,
        f.Result(f.Request with { Layers = layers.ToImmutableArray() }).Outcome,
        "DIFF_API_FILTER_STALE");
    var forged = f.Request.Layers.ToArray();
    forged[4] = forged[4] with { LayerSemanticSha256 = Sha("forged-layer") };
    ExpectCode(() => f.Result(f.Request with { Layers = forged.ToImmutableArray() }),
        StatisticReconciliationSummaryFailureCodes.Invalid, "EXPORT_HASH_FAILS_CLOSED");
    Matched(f.Result(Rebind(f, "diff-api-export-recheck")), "DIFF_API_EXPORT_RECOVERY");
}

static Fixture BuildBasic(string sum = "10", string min = "0", string max = "10", string mean = "5")
{
    var r = new StatisticReconciliationSummaryReconciler();
    var identity = r.CreateIdentity(StatisticReconciliationSummaryFamilies.Basic,
        StatisticReconciliationSummaryKinds.Field, "basic-metric", "MONTH:2026-08", "basic-sort",
        fieldId: "field-basic");
    return Build(r, [..NumericRows(r, identity, sum, min, max, mean)], null, []);
}

static Fixture BuildFlow(string scope)
{
    var r = new StatisticReconciliationSummaryReconciler();
    var identity = r.CreateIdentity(StatisticReconciliationSummaryFamilies.Flow,
        StatisticReconciliationSummaryKinds.Field, "flow-metric", "MONTH:2026-08", $"flow-{scope}",
        fieldId: "field-flow", flowScope: scope, flowScopeId: $"scope-{scope}", flowEpoch: "epoch-7");
    return Build(r, [..NumericRows(r, identity, "10", "0", "10", "5")], "epoch-7", []);
}

static Fixture BuildAdvanced(string grain, string period)
{
    var r = new StatisticReconciliationSummaryReconciler();
    var identity = r.CreateIdentity(StatisticReconciliationSummaryFamilies.Advanced,
        StatisticReconciliationSummaryKinds.Field, "advanced-metric", period, $"advanced-{period}",
        fieldId: "field-advanced", grain: grain);
    return Build(r, [..NumericRows(r, identity, "10", "0", "10", "5")], null, []);
}

static Fixture BuildDiff(string kind, string transition, string? beforeState = null,
    string? beforeValue = null, string? afterState = null, string? afterValue = null)
{
    var r = new StatisticReconciliationSummaryReconciler();
    var diffKind = kind;
    var identity = kind switch
    {
        StatisticReconciliationSummaryKinds.Field => r.CreateIdentity(
            StatisticReconciliationSummaryFamilies.Diff, kind, "diff-metric", "MONTH:2026-08", "diff-field",
            fieldId: "field-diff", diffKind: diffKind),
        StatisticReconciliationSummaryKinds.TableMetric => r.CreateIdentity(
            StatisticReconciliationSummaryFamilies.Diff, kind, "diff-metric", "MONTH:2026-08", "diff-table",
            tableId: "table-diff", diffKind: diffKind),
        _ => r.CreateIdentity(StatisticReconciliationSummaryFamilies.Diff, kind, "diff-metric",
            "MONTH:2026-08", "diff-label", tableId: "table-diff", rowId: "row-diff",
            labelId: "label-diff", diffKind: diffKind)
    };
    beforeState ??= transition == StatisticReconciliationSummaryDifferenceKinds.Added
        ? StatisticReconciliationObservationValueStates.Missing
        : StatisticReconciliationObservationValueStates.Value;
    afterState ??= transition == StatisticReconciliationSummaryDifferenceKinds.Removed
        ? StatisticReconciliationObservationValueStates.Missing
        : StatisticReconciliationObservationValueStates.Value;
    if (beforeState == StatisticReconciliationObservationValueStates.Value) beforeValue ??= "before";
    if (afterState == StatisticReconciliationObservationValueStates.Value) afterValue ??=
        transition == StatisticReconciliationSummaryDifferenceKinds.Unchanged ? beforeValue : "after";
    var row = r.CreateObservation(identity, StatisticReconciliationSummaryAtomKinds.Value,
        StatisticReconciliationTypedValueTypes.Text, StatisticReconciliationObservationValueStates.Value,
        $"{transition}:{beforeState}:{beforeValue ?? "~"}:{afterState}:{afterValue ?? "~"}", 0,
        2, 2, 0, Sha("lineage"), beforeState: beforeState,
        beforeCanonicalValue: beforeValue, afterState: afterState,
        afterCanonicalValue: afterValue, differenceKind: transition);
    return Build(r, [row], null, []);
}

static Fixture BuildHierarchy(bool wrongMonth)
{
    var r = new StatisticReconciliationSummaryReconciler();
    var specs = new[]
    {
        (StatisticReconciliationSummaryGrains.Day, "DAY:2026-08-01", "2"),
        (StatisticReconciliationSummaryGrains.Day, "DAY:2026-08-02", "3"),
        (StatisticReconciliationSummaryGrains.Month, "MONTH:2026-08", wrongMonth ? "6" : "5"),
        (StatisticReconciliationSummaryGrains.Year, "YEAR:2026", "5")
    };
    var rows = specs.Select(spec =>
    {
        var id = r.CreateIdentity(StatisticReconciliationSummaryFamilies.Advanced,
            StatisticReconciliationSummaryKinds.Field, "rollup-metric", spec.Item2,
            $"rollup-{spec.Item2}", fieldId: "field-rollup", grain: spec.Item1);
        return r.CreateObservation(id, StatisticReconciliationSummaryAtomKinds.Sum,
            StatisticReconciliationStatefulValueTypes.Number, StatisticReconciliationObservationValueStates.Value,
            spec.Item3, 0, 1, 1, 1, Sha("lineage"));
    }).ToArray();
    var edges = new[]
    {
        r.CreateHierarchyEdge("DAY:2026-08-01", "MONTH:2026-08", "DAY", "MONTH"),
        r.CreateHierarchyEdge("DAY:2026-08-02", "MONTH:2026-08", "DAY", "MONTH"),
        r.CreateHierarchyEdge("MONTH:2026-08", "YEAR:2026", "MONTH", "YEAR")
    };
    return Build(r, rows, null, edges);
}

static IEnumerable<StatisticReconciliationSummaryObservation> NumericRows(
    StatisticReconciliationSummaryReconciler r, StatisticReconciliationSummaryIdentity id,
    string sum, string min, string max, string mean)
{
    var lineage = Sha("lineage");
    yield return r.CreateObservation(id, StatisticReconciliationSummaryAtomKinds.ReportCount,
        "NUMBER", "VALUE", "2", 0, 2, 3, 2, lineage);
    yield return r.CreateObservation(id, StatisticReconciliationSummaryAtomKinds.RowCount,
        "NUMBER", "VALUE", "3", 0, 2, 3, 2, lineage);
    yield return r.CreateObservation(id, StatisticReconciliationSummaryAtomKinds.NumericValueCount,
        "NUMBER", "VALUE", "2", 0, 2, 3, 2, lineage);
    yield return r.CreateObservation(id, StatisticReconciliationSummaryAtomKinds.Sum,
        "NUMBER", "VALUE", sum, 0, 2, 3, 2, lineage);
    yield return r.CreateObservation(id, StatisticReconciliationSummaryAtomKinds.Min,
        "NUMBER", "VALUE", min, 0, 2, 3, 2, lineage);
    yield return r.CreateObservation(id, StatisticReconciliationSummaryAtomKinds.Max,
        "NUMBER", "VALUE", max, 0, 2, 3, 2, lineage);
    yield return r.CreateObservation(id, StatisticReconciliationSummaryAtomKinds.Mean,
        "NUMBER", "VALUE", mean, 0, 2, 3, 2, lineage);
    yield return r.CreateObservation(id, StatisticReconciliationSummaryAtomKinds.Value,
        "NUMBER", "VALUE", sum, 0, 2, 3, 2, lineage);
}

static Fixture Build(StatisticReconciliationSummaryReconciler r,
    IEnumerable<StatisticReconciliationSummaryObservation> rows, string? epoch,
    IEnumerable<StatisticReconciliationSummaryHierarchyEdge> hierarchy)
{
    var binding = Sha("binding");
    var filter = Sha("filter");
    var sourceRows = rows.ToImmutableArray();
    var totals = new[] { r.CreateTotal("totalRows", "NUMBER", sourceRows.Length.ToString(), 0) };
    var layers = StatisticReconciliationSummaryLayers.Ordered.Select((name, ordinal) =>
        r.CreateLayer(ordinal, name, binding, Sha($"owner-{ordinal}"), filter, true,
            sourceRows, totals)).ToImmutableArray();
    var request = new StatisticReconciliationSummaryRequest(
        "reconciliation-summary", binding, "expected-001", "actual-001", filter,
        epoch, layers, hierarchy.ToImmutableArray());
    return new Fixture(r, request, binding, filter);
}

static StatisticReconciliationSummaryRequest MutateValue(Fixture f, int layerOrdinal, string value)
{
    var layers = f.Request.Layers.ToArray();
    var changed = false;
    var rows = layers[layerOrdinal].Observations.Select(row =>
    {
        if (changed || row.AtomKind != StatisticReconciliationSummaryAtomKinds.Value) return row;
        changed = true;
        return f.R.CreateObservation(row.Identity, row.AtomKind, row.ValueType, row.ValueState,
            value, row.DecimalScale, row.ReportCount, row.RowCount, row.NumericValueCount,
            row.SourceLineageSha256, row.CollectionSemantics, row.BeforeState,
            row.BeforeCanonicalValue, row.AfterState, row.AfterCanonicalValue, row.DifferenceKind);
    }).ToArray();
    Require(changed, "VALUE_ROW_REQUIRED");
    layers[layerOrdinal] = f.R.CreateLayer(layerOrdinal, layers[layerOrdinal].Layer,
        f.Binding, Sha($"owner-{layerOrdinal}"), f.Filter, true, rows,
        layers[layerOrdinal].FullFilterTotals);
    return f.Request with { Layers = layers.ToImmutableArray(), ActualGenerationId = "actual-mismatch" };
}

static StatisticReconciliationSummaryRequest Rebind(Fixture f, string seed)
{
    var binding = Sha(seed);
    var layers = f.Request.Layers.Select((layer, ordinal) => f.R.CreateLayer(
        ordinal, layer.Layer, binding, Sha($"{seed}-owner-{ordinal}"), f.Filter,
        true, layer.Observations, layer.FullFilterTotals)).ToImmutableArray();
    return f.Request with
    {
        ComparisonBindingSha256 = binding,
        ExpectedGenerationId = $"expected-{seed}",
        ActualGenerationId = $"actual-{seed}",
        Layers = layers
    };
}

static void Matched(StatisticReconciliationSummaryResult result, string reason)
{
    Equal(StatisticReconciliationSummaryOutcomes.Matched, result.Outcome, reason);
    Require(result.AllRequiredLayersZero && result.EvidenceComplete && result.ApiExportBindingExact,
        $"{reason}:MATCH_INVARIANTS");
}

static void Mismatched(StatisticReconciliationSummaryResult result, string layer,
    string root, string reason)
{
    Equal(StatisticReconciliationSummaryOutcomes.Mismatched, result.Outcome, reason);
    Equal(layer, result.EarliestDivergentLayer!, $"{reason}:LAYER");
    Equal(root, result.RootCause!, $"{reason}:ROOT");
}

static void ExpectCode(Action action, string code, string reason)
{
    try { action(); }
    catch (StatisticReconciliationSummaryException error)
    {
        Equal(code, error.Code, reason);
        return;
    }
    throw new InvalidOperationException($"{reason}:EXCEPTION_REQUIRED");
}

static string Sha(string value) => Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static void Require(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}

static void Equal<T>(T expected, T actual, string reason)
{
    if (expected is IEnumerable<string> expectedItems && actual is IEnumerable<string> actualItems)
    {
        if (!expectedItems.SequenceEqual(actualItems, StringComparer.Ordinal))
            throw new InvalidOperationException(reason);
        return;
    }
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{reason}:expected={expected};actual={actual}");
}

internal sealed record Fixture(
    StatisticReconciliationSummaryReconciler R,
    StatisticReconciliationSummaryRequest Request,
    string Binding,
    string Filter)
{
    internal StatisticReconciliationSummaryResult Result(StatisticReconciliationSummaryRequest? request = null)
        => R.Reconcile(request ?? Request);
}
