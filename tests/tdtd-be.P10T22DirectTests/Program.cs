using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.Services.StatisticsReconciliation.DirectReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

var cases = new (string Id, Action Run)[]
{
    ("P10-FIELD-01", FieldExactNumericChain),
    ("P10-FIELD-02", FieldMeanUsesNumericDenominator),
    ("P10-FIELD-03", FieldNullAndNonNumericExcluded),
    ("P10-FIELD-04", FieldProjectionMutationLocalizesAndRecovers),
    ("P10-FIELD-05", FieldAggregateMutationLocalizes),
    ("P10-FIELD-06", FieldResultMutationLocalizesAndRecovers),
    ("P10-TABLE-01", TableMetricExactChain),
    ("P10-TABLE-02", TableDuplicateLookingRowsStayDistinct),
    ("P10-TABLE-03", TableCountsRemainIndependent),
    ("P10-TABLE-04", TableMissingRowIsExplicit),
    ("P10-TABLE-05", TableExtraRowIsExplicit),
    ("P10-TABLE-06", TableExportMutationLocalizesAndRecovers),
    ("P10-LABEL-01", LabelIdentityIgnoresLocalizedRendering),
    ("P10-LABEL-02", LabelMissingIdentityIsExplicit),
    ("P10-LABEL-03", LabelEmptyStateIsExact),
    ("P10-LABEL-04", LabelDuplicateTextKeepsStableMultiplicity),
    ("P10-LABEL-05", LabelOrderingIsDeterministic),
    ("P10-LABEL-06", LabelAuthorizationBoundariesAreOpaque),
    ("P10-PAGE-01", PageSizeOneKeepsFullTotals),
    ("P10-PAGE-02", PageSizeFiftyKeepsFullTotals),
    ("P10-PAGE-03", PageNumberChangesRowsOnly),
    ("P10-PAGE-04", PageSortChangesOrderOnly),
    ("P10-PAGE-05", PageTotalMutationCannotHide),
    ("P10-PAGE-06", PageRowMutationCannotHideAndRecovers)
};

var passed = 0;
foreach (var item in cases)
{
    try
    {
        item.Run();
        Console.WriteLine($"PASS {item.Id}");
        passed++;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL {item.Id} {exception.GetType().Name}:{exception.Message}");
        return 1;
    }
}

Require(passed == 24, "EXACT_DIRECT_CASE_COUNT");
Console.WriteLine(
    "P10_T22_DIRECT_RECONCILIATION_OK cases=24 field=6 table=6 label=6 page=6 " +
    "layers=6 pageSizes=1,50 fullTotals=true earliestRoot=true recovery=true " +
    "p9Writes=0 cumulative=136 stopBefore=P10-T23");
return 0;

static void FieldExactNumericChain()
{
    var fixture = Build(StatisticReconciliationDirectKinds.Field);
    Matched(fixture.Reconciler.Reconcile(fixture.Request), "FIELD_EXACT");
    Require(fixture.Request.Layers.All(layer => layer.Observations.Any(value =>
        value.AtomKind == StatisticReconciliationDirectAtomKinds.Mean)), "FIELD_MEAN_PRESENT");
}

static void FieldMeanUsesNumericDenominator()
{
    var fixture = Build(StatisticReconciliationDirectKinds.Field);
    var mutated = MutateAtoms(fixture, 1, rows => rows.Select(value =>
    {
        if (value.Identity.IdentitySha256 != rows[0].Identity.IdentitySha256)
            return value;
        return value.AtomKind switch
        {
            StatisticReconciliationDirectAtomKinds.NumericValueCount =>
                Recreate(fixture, value, "1", numericValueCount: 1),
            StatisticReconciliationDirectAtomKinds.Sum =>
                Recreate(fixture, value, "10", numericValueCount: 1),
            StatisticReconciliationDirectAtomKinds.Mean =>
                Recreate(fixture, value, "10", numericValueCount: 1),
            _ => value
        };
    }).ToList());
    var result = fixture.Reconciler.Reconcile(mutated);
    Mismatched(result, StatisticReconciliationDirectLayers.Projection, "FIELD_DENOMINATOR");
    Require(result.Layers[1].NonzeroCount >= 2, "SUM_COUNT_MEAN_BOUND");
    Matched(fixture.Reconciler.Reconcile(Rebind(fixture, 'f')), "FIELD_DENOMINATOR_RECOVERY");
}

static void FieldNullAndNonNumericExcluded()
{
    var fixture = Build(StatisticReconciliationDirectKinds.Field, firstValueState:
        StatisticReconciliationObservationValueStates.Null);
    var result = fixture.Reconciler.Reconcile(fixture.Request);
    Matched(result, "FIELD_NULL_EXCLUDED");
    var first = fixture.Request.Layers[0].Observations.Single(value =>
        value.AtomKind == StatisticReconciliationDirectAtomKinds.Value &&
        value.Identity.StableSortKey == "sort-0");
    Require(first.ValueState == StatisticReconciliationObservationValueStates.Null &&
            first.NumericValueCount == 2 && first.CanonicalValue is null,
        "NULL_NOT_NUMERIC_BUT_DENOMINATOR_EXPLICIT");
}

static void FieldProjectionMutationLocalizesAndRecovers()
{
    var fixture = Build(StatisticReconciliationDirectKinds.Field);
    var bad = MutateValue(fixture, 1, "999");
    var mismatch = fixture.Reconciler.Reconcile(bad);
    Mismatched(mismatch, StatisticReconciliationDirectLayers.Projection, "FIELD_PROJECTION_ROOT");
    var recovered = Rebind(fixture, 'b');
    Matched(fixture.Reconciler.Reconcile(recovered), "FIELD_PROJECTION_RECOVERY");
    Console.WriteLine($"TRACE P10-FIELD-04 mismatch={mismatch.ResultSemanticSha256} recovery={fixture.Reconciler.Reconcile(recovered).ResultSemanticSha256}");
}

static void FieldAggregateMutationLocalizes()
{
    var fixture = Build(StatisticReconciliationDirectKinds.Field);
    Mismatched(fixture.Reconciler.Reconcile(MutateValue(fixture, 2, "998")),
        StatisticReconciliationDirectLayers.Aggregate, "FIELD_AGGREGATE_ROOT");
    Matched(fixture.Reconciler.Reconcile(Rebind(fixture, 'g')), "FIELD_AGGREGATE_RECOVERY");
}

static void FieldResultMutationLocalizesAndRecovers()
{
    var fixture = Build(StatisticReconciliationDirectKinds.Field);
    var mismatch = fixture.Reconciler.Reconcile(MutateValue(fixture, 3, "997"));
    Mismatched(mismatch, StatisticReconciliationDirectLayers.Result, "FIELD_RESULT_ROOT");
    Matched(fixture.Reconciler.Reconcile(Rebind(fixture, 'c')), "FIELD_RESULT_RECOVERY");
    var staleLayers = fixture.Request.Layers.ToArray();
    staleLayers[3] = fixture.Reconciler.CreateLayer(
        3, staleLayers[3].Layer, Sha("foreign-binding"), Sha("foreign-owner"),
        true, staleLayers[3].Observations, staleLayers[3].FullFilterTotals);
    var stale = fixture.Reconciler.Reconcile(fixture.Request with
    {
        ActualGenerationId = "actual-stale",
        Layers = staleLayers.ToImmutableArray()
    });
    Equal(StatisticReconciliationDirectOutcomes.Stale, stale.Outcome, "FIELD_SOURCE_DRIFT_STALE");
}

static void TableMetricExactChain()
{
    var fixture = Build(StatisticReconciliationDirectKinds.TableMetric);
    Matched(fixture.Reconciler.Reconcile(fixture.Request), "TABLE_EXACT");
}

static void TableDuplicateLookingRowsStayDistinct()
{
    var fixture = Build(StatisticReconciliationDirectKinds.TableMetric, identityCount: 3,
        duplicateSortKeys: true);
    Matched(fixture.Reconciler.Reconcile(fixture.Request), "TABLE_DUPLICATE_LOOKING");
    var identities = fixture.Request.Layers[0].Observations.Select(value => value.Identity)
        .DistinctBy(value => value.IdentitySha256).ToArray();
    Require(identities.Length == 3 && identities.Select(value => value.RowId)
        .Distinct(StringComparer.Ordinal).Count() == 3, "TABLE_STABLE_ROW_MULTIPLICITY");
}

static void TableCountsRemainIndependent()
{
    var fixture = Build(StatisticReconciliationDirectKinds.TableMetric);
    var source = fixture.Request.Layers[0].Observations;
    var sample = source.First(value => value.AtomKind == StatisticReconciliationDirectAtomKinds.Value);
    Require(sample.ReportCount == 2 && sample.RowCount == 3 && sample.NumericValueCount == 2,
        "TABLE_COUNTS_DISTINCT");
    Matched(fixture.Reconciler.Reconcile(fixture.Request), "TABLE_COUNTS_MATCH");
}

static void TableMissingRowIsExplicit()
{
    var fixture = Build(StatisticReconciliationDirectKinds.TableMetric);
    var missingIdentity = fixture.Request.Layers[0].Observations[0].Identity.IdentitySha256;
    var request = MutateAtoms(fixture, 1, rows => rows.Where(value =>
        value.Identity.IdentitySha256 != missingIdentity).ToList());
    var result = fixture.Reconciler.Reconcile(request);
    Mismatched(result, StatisticReconciliationDirectLayers.Projection, "TABLE_MISSING_LAYER");
    Equal(StatisticReconciliationDirectRootCauses.MissingIdentity, result.RootCause, "TABLE_MISSING_ROOT");
    Matched(fixture.Reconciler.Reconcile(Rebind(fixture, 'h')), "TABLE_MISSING_RECOVERY");
}

static void TableExtraRowIsExplicit()
{
    var fixture = Build(StatisticReconciliationDirectKinds.TableMetric);
    var extraIdentity = fixture.Reconciler.CreateIdentity(
        StatisticReconciliationDirectKinds.TableMetric, "form-1", "2026-08", "metric-1",
        "sort-z", tableId: "table-1", rowId: "row-extra");
    var extra = fixture.Reconciler.CreateObservation(
        extraIdentity, StatisticReconciliationDirectAtomKinds.Value, "NUMBER",
        StatisticReconciliationObservationValueStates.Value, "1", 0, 1, 1, 1, Sha("extra-lineage"));
    var request = MutateAtoms(fixture, 1, rows => [.. rows, extra]);
    var result = fixture.Reconciler.Reconcile(request);
    Mismatched(result, StatisticReconciliationDirectLayers.Projection, "TABLE_EXTRA_LAYER");
    Equal(StatisticReconciliationDirectRootCauses.ExtraIdentity, result.RootCause, "TABLE_EXTRA_ROOT");
    Matched(fixture.Reconciler.Reconcile(Rebind(fixture, 'i')), "TABLE_EXTRA_RECOVERY");
}

static void TableExportMutationLocalizesAndRecovers()
{
    var fixture = Build(StatisticReconciliationDirectKinds.TableMetric);
    var mismatch = fixture.Reconciler.Reconcile(MutateValue(fixture, 5, "996"));
    Mismatched(mismatch, StatisticReconciliationDirectLayers.Export, "TABLE_EXPORT_ROOT");
    Matched(fixture.Reconciler.Reconcile(Rebind(fixture, 'd')), "TABLE_EXPORT_RECOVERY");
    var forgedRows = fixture.Request.ExportEvidence!.Rows.ToArray();
    forgedRows[0] = forgedRows[0] with { CanonicalRowJson = "{\"forged\":true}" };
    ExpectCode(
        () => fixture.Reconciler.Reconcile(fixture.Request with
        {
            ExportEvidence = fixture.Request.ExportEvidence with
            {
                Rows = forgedRows.ToImmutableArray()
            }
        }),
        StatisticReconciliationDirectFailureCodes.EvidenceInvalid,
        "EXPORT_FORGED_HASH_FAILS_CLOSED");
}

static void LabelIdentityIgnoresLocalizedRendering()
{
    var fixture = Build(StatisticReconciliationDirectKinds.RowLabel,
        exportRenderedText: "Nhãn hiển thị");
    Matched(fixture.Reconciler.Reconcile(fixture.Request), "LABEL_LOCALIZED_RENDERING");
    Require(fixture.Request.Layers[0].Observations.All(value =>
        value.Identity.LabelId is not null), "LABEL_CANONICAL_IDENTITY");
}

static void LabelMissingIdentityIsExplicit()
{
    var fixture = Build(StatisticReconciliationDirectKinds.RowLabel);
    var missing = fixture.Request.Layers[0].Observations[0].Identity.IdentitySha256;
    var result = fixture.Reconciler.Reconcile(MutateAtoms(fixture, 1,
        rows => rows.Where(value => value.Identity.IdentitySha256 != missing).ToList()));
    Equal(StatisticReconciliationDirectRootCauses.MissingIdentity, result.RootCause, "LABEL_MISSING_ROOT");
    Matched(fixture.Reconciler.Reconcile(Rebind(fixture, 'j')), "LABEL_MISSING_RECOVERY");
}

static void LabelEmptyStateIsExact()
{
    var fixture = Build(StatisticReconciliationDirectKinds.RowLabel,
        firstValueState: StatisticReconciliationObservationValueStates.Empty);
    Matched(fixture.Reconciler.Reconcile(fixture.Request), "LABEL_EMPTY_EXACT");
    Require(fixture.Request.Layers.SelectMany(value => value.Observations).Where(value =>
        value.AtomKind == StatisticReconciliationDirectAtomKinds.Value &&
        value.Identity.StableSortKey == "sort-0").All(value =>
            value.ValueState == StatisticReconciliationObservationValueStates.Empty &&
            value.CanonicalValue is null), "LABEL_EMPTY_NOT_MISSING");
}

static void LabelDuplicateTextKeepsStableMultiplicity()
{
    var fixture = Build(StatisticReconciliationDirectKinds.RowLabel,
        identityCount: 3, duplicateSortKeys: true, exportRenderedText: "Same label");
    Matched(fixture.Reconciler.Reconcile(fixture.Request), "LABEL_DUPLICATE_TEXT");
    Require(fixture.Request.ExportEvidence!.Rows.Length == 3 &&
            fixture.Request.ExportEvidence.Rows.Select(value => value.IdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() == 3,
        "LABEL_MULTIPLICITY_BY_IDENTITY");
}

static void LabelOrderingIsDeterministic()
{
    var fixture = Build(StatisticReconciliationDirectKinds.RowLabel);
    var result = fixture.Reconciler.Reconcile(fixture.Request);
    Matched(result, "LABEL_ORDER");
    var asc = fixture.Request.ApiPages.Where(value => value.PageSize == 50 && value.SortDirection == "ASC")
        .Single().Rows.Select(value => value.IdentitySha256).ToArray();
    var desc = fixture.Request.ApiPages.Where(value => value.PageSize == 50 && value.SortDirection == "DESC")
        .Single().Rows.Select(value => value.IdentitySha256).ToArray();
    Require(asc.SequenceEqual(desc.Reverse()), "LABEL_ORDER_REVERSES_ONLY_ROWS");
}

static void LabelAuthorizationBoundariesAreOpaque()
{
    var fixture = Build(StatisticReconciliationDirectKinds.RowLabel);
    var redacted = fixture.Request with
    {
        Permission = fixture.Reconciler.CreatePermissionEvidence(
            StatisticReconciliationDirectPermissionStates.AuthorizedRedacted,
            Sha("redacted-auth"), 3, 1)
    };
    var redactedResult = fixture.Reconciler.Reconcile(redacted);
    Matched(redactedResult, "LABEL_REDACTED_MATCH");
    Require(!redactedResult.DetailedEvidenceVisible &&
            redactedResult.Layers.All(value => value.DetailedDeltas.Length == 0),
        "REDACTED_NO_IDENTITY_DETAILS");

    var denied = new StatisticReconciliationDirectRequest(
        "malformed-hidden-id", "malformed-hidden-binding", "hidden", "hidden",
        fixture.Reconciler.CreatePermissionEvidence(
            StatisticReconciliationDirectPermissionStates.Denied, Sha("denied-auth"), 3, 0),
        default, default, null);
    var deniedOther = denied with { ReconciliationId = "different-hidden-id" };
    var first = fixture.Reconciler.Reconcile(denied);
    var second = fixture.Reconciler.Reconcile(deniedOther);
    Equal(StatisticReconciliationDirectOutcomes.Forbidden, first.Outcome, "DENIED_FORBIDDEN");
    Equal(first.ResultSemanticSha256, second.ResultSemanticSha256, "DENIED_OPAQUE_SHAPE");
    Require(first.Layers.Length == 0 && !first.DetailedEvidenceVisible, "AUTH_BEFORE_EVIDENCE");
}

static void PageSizeOneKeepsFullTotals()
{
    var fixture = Build(StatisticReconciliationDirectKinds.Field);
    Matched(fixture.Reconciler.Reconcile(fixture.Request), "PAGE_ONE_MATCH");
    var pages = fixture.Request.ApiPages.Where(value => value.PageSize == 1).ToArray();
    Require(pages.Length == 6 && pages.All(value =>
        value.FullFilterTotals.TotalsSemanticSha256 == fixture.Totals.TotalsSemanticSha256),
        "PAGE_ONE_FULL_TOTALS");
}

static void PageSizeFiftyKeepsFullTotals()
{
    var fixture = Build(StatisticReconciliationDirectKinds.Field);
    var pages = fixture.Request.ApiPages.Where(value => value.PageSize == 50).ToArray();
    Require(pages.Length == 2 && pages.All(value => value.TotalRows == 3 && value.TotalPages == 1),
        "PAGE_FIFTY_FULL_SCOPE");
    Matched(fixture.Reconciler.Reconcile(fixture.Request), "PAGE_FIFTY_MATCH");
}

static void PageNumberChangesRowsOnly()
{
    var fixture = Build(StatisticReconciliationDirectKinds.TableMetric);
    var pages = fixture.Request.ApiPages.Where(value => value.PageSize == 1 && value.SortDirection == "ASC")
        .OrderBy(value => value.Page).ToArray();
    Require(pages.Select(value => value.Rows.Single().IdentitySha256).Distinct().Count() == 3 &&
            pages.Select(value => value.FullFilterTotals.TotalsSemanticSha256).Distinct().Count() == 1,
        "PAGE_NUMBER_ROWS_ONLY");
    Matched(fixture.Reconciler.Reconcile(fixture.Request), "PAGE_NUMBER_MATCH");
}

static void PageSortChangesOrderOnly()
{
    var fixture = Build(StatisticReconciliationDirectKinds.RowLabel);
    var asc = fixture.Request.ApiPages.Where(value => value.PageSize == 1 && value.SortDirection == "ASC")
        .OrderBy(value => value.Page).Select(value => value.Rows.Single().IdentitySha256).ToArray();
    var desc = fixture.Request.ApiPages.Where(value => value.PageSize == 1 && value.SortDirection == "DESC")
        .OrderBy(value => value.Page).Select(value => value.Rows.Single().IdentitySha256).ToArray();
    Require(asc.SequenceEqual(desc.Reverse()) &&
            fixture.Request.ApiPages.Select(value => value.FullFilterTotals.TotalsSemanticSha256)
                .Distinct().Count() == 1, "PAGE_SORT_ROWS_ONLY");
}

static void PageTotalMutationCannotHide()
{
    var fixture = Build(StatisticReconciliationDirectKinds.Field);
    var wrongTotals = fixture.Reconciler.CreateTotals([
        ("TOTAL_ROWS", "NUMBER", "999", 0),
        ("TOTAL_REPORT_COUNT", "NUMBER", "6", 0),
        ("TOTAL_ROW_COUNT", "NUMBER", "9", 0),
        ("TOTAL_NUMERIC_VALUE_COUNT", "NUMBER", "6", 0),
        ("TOTAL_SUM", "NUMBER", "60", 0),
        ("TOTAL_MEAN", "NUMBER", "10", 0)
    ]);
    var pages = fixture.Request.ApiPages.ToArray();
    var original = pages[0];
    pages[0] = fixture.Reconciler.CreateApiPage(
        fixture.Request.Layers[4],
        original.Page, original.PageSize, original.SortDirection, original.TotalPages,
        original.TotalRows, original.Rows.Select(value => value.IdentitySha256), wrongTotals);
    var result = fixture.Reconciler.Reconcile(fixture.Request with { ApiPages = pages.ToImmutableArray() });
    Mismatched(result, StatisticReconciliationDirectLayers.Api, "PAGE_TOTAL_ROOT");
    Require(!result.PagingTotalsStable, "PAGE_TOTAL_DRIFT_DETECTED");
    Matched(fixture.Reconciler.Reconcile(Rebind(fixture, 'k')), "PAGE_TOTAL_RECOVERY");
}

static void PageRowMutationCannotHideAndRecovers()
{
    var fixture = Build(StatisticReconciliationDirectKinds.TableMetric);
    var pages = fixture.Request.ApiPages.ToArray();
    var original = pages.First(value => value.PageSize == 50 && value.SortDirection == "ASC");
    var index = Array.IndexOf(pages, original);
    var reversed = original.Rows.Reverse().Select(value =>
        (value.IdentitySha256, value.StableSortKey, value.RowSemanticSha256));
    pages[index] = fixture.Reconciler.CreateApiPage(
        fixture.Request.Layers[4],
        original.Page, original.PageSize, original.SortDirection, original.TotalPages,
        original.TotalRows, reversed.Select(value => value.IdentitySha256), original.FullFilterTotals);
    var mismatch = fixture.Reconciler.Reconcile(fixture.Request with { ApiPages = pages.ToImmutableArray() });
    Mismatched(mismatch, StatisticReconciliationDirectLayers.Api, "PAGE_ROW_ROOT");
    Require(!mismatch.PagingRowsExact, "PAGE_ROW_DRIFT_DETECTED");
    Matched(fixture.Reconciler.Reconcile(Rebind(fixture, 'e')), "PAGE_ROW_RECOVERY");
    var forgedPageRows = original.Rows.ToArray();
    forgedPageRows[0] = forgedPageRows[0] with { RowSemanticSha256 = Sha("forged-page-row") };
    var forgedPages = fixture.Request.ApiPages.ToArray();
    forgedPages[index] = original with { Rows = forgedPageRows.ToImmutableArray() };
    ExpectCode(
        () => fixture.Reconciler.Reconcile(fixture.Request with
        {
            ApiPages = forgedPages.ToImmutableArray()
        }),
        StatisticReconciliationDirectFailureCodes.PageInvalid,
        "PAGE_FORGED_HASH_FAILS_CLOSED");
    Console.WriteLine($"TRACE P10-PAGE-06 mismatch={mismatch.ResultSemanticSha256} recovery={fixture.Reconciler.Reconcile(Rebind(fixture, 'e')).ResultSemanticSha256}");
}

static Fixture Build(
    string kind,
    int identityCount = 3,
    bool duplicateSortKeys = false,
    string? firstValueState = null,
    string exportRenderedText = "Rendered label")
{
    var reconciler = new StatisticReconciliationDirectReconciler();
    var binding = Sha($"binding:{kind}:{identityCount}:{firstValueState}:{exportRenderedText}");
    var identities = Enumerable.Range(0, identityCount).Select(index =>
    {
        var sort = duplicateSortKeys ? "same-sort" : $"sort-{index}";
        return kind switch
        {
            StatisticReconciliationDirectKinds.Field => reconciler.CreateIdentity(
                kind, "form-1", "2026-08", "metric-1", sort, fieldId: $"field-{index}"),
            StatisticReconciliationDirectKinds.TableMetric => reconciler.CreateIdentity(
                kind, "form-1", "2026-08", "metric-1", sort,
                tableId: "table-1", rowId: $"row-{index}"),
            _ => reconciler.CreateIdentity(
                kind, "form-1", "2026-08", "label-metric", sort,
                tableId: "table-1", rowId: $"row-{index}", labelId: $"label-{index}")
        };
    }).ToArray();
    var observations = new List<StatisticReconciliationDirectObservation>();
    foreach (var (identity, index) in identities.Select((value, index) => (value, index)))
    {
        var lineage = Sha($"lineage:{kind}:{index}");
        if (kind == StatisticReconciliationDirectKinds.RowLabel)
        {
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.ReportCount, "NUMBER",
                StatisticReconciliationObservationValueStates.Value, "2", 0, 2, 1, 0, lineage));
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.RowCount, "NUMBER",
                StatisticReconciliationObservationValueStates.Value, "1", 0, 2, 1, 0, lineage));
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.Count, "NUMBER",
                StatisticReconciliationObservationValueStates.Value, "1", 0, 2, 1, 0, lineage));
            var state = index == 0 && firstValueState is not null
                ? firstValueState
                : StatisticReconciliationObservationValueStates.Value;
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.Value, "TEXT", state,
                state == StatisticReconciliationObservationValueStates.Value ? identity.LabelId : null,
                0, 2, 1, 0, lineage));
        }
        else
        {
            var sum = (index + 1) * 10;
            var state = index == 0 && firstValueState is not null
                ? firstValueState
                : StatisticReconciliationObservationValueStates.Value;
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.ReportCount, "NUMBER",
                StatisticReconciliationObservationValueStates.Value, "2", 0, 2, 3, 2, lineage));
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.RowCount, "NUMBER",
                StatisticReconciliationObservationValueStates.Value, "3", 0, 2, 3, 2, lineage));
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.Count, "NUMBER",
                StatisticReconciliationObservationValueStates.Value, "3", 0, 2, 3, 2, lineage));
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.NumericValueCount, "NUMBER",
                StatisticReconciliationObservationValueStates.Value, "2", 0, 2, 3, 2, lineage));
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.Sum, "NUMBER",
                StatisticReconciliationObservationValueStates.Value, sum.ToString(), 0, 2, 3, 2, lineage));
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.Min, "NUMBER",
                StatisticReconciliationObservationValueStates.Value, (sum / 2 - 1).ToString(), 0, 2, 3, 2, lineage));
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.Max, "NUMBER",
                StatisticReconciliationObservationValueStates.Value, (sum / 2 + 1).ToString(), 0, 2, 3, 2, lineage));
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.Mean, "NUMBER",
                StatisticReconciliationObservationValueStates.Value, (sum / 2).ToString(), 0, 2, 3, 2, lineage));
            observations.Add(reconciler.CreateObservation(identity,
                StatisticReconciliationDirectAtomKinds.Value, "NUMBER", state,
                state == StatisticReconciliationObservationValueStates.Value ? (sum / 2).ToString() : null,
                0, 2, 3, 2, lineage));
        }
    }
    var totals = reconciler.CreateTotals([
        ("TOTAL_ROWS", "NUMBER", identityCount.ToString(), 0),
        ("TOTAL_REPORT_COUNT", "NUMBER", (identityCount * 2).ToString(), 0),
        ("TOTAL_ROW_COUNT", "NUMBER", (identityCount * (kind == StatisticReconciliationDirectKinds.RowLabel ? 1 : 3)).ToString(), 0),
        ("TOTAL_NUMERIC_VALUE_COUNT", "NUMBER", (identityCount * (kind == StatisticReconciliationDirectKinds.RowLabel ? 0 : 2)).ToString(), 0),
        ("TOTAL_SUM", "NUMBER", (kind == StatisticReconciliationDirectKinds.RowLabel ? 0 : Enumerable.Range(1, identityCount).Sum() * 10).ToString(), 0),
        ("TOTAL_MEAN", "NUMBER", (kind == StatisticReconciliationDirectKinds.RowLabel ? 0 : Enumerable.Range(1, identityCount).Sum() * 5 / identityCount).ToString(), 0)
    ]);
    var layers = StatisticReconciliationDirectLayers.Ordered.Select((layer, index) =>
        reconciler.CreateLayer(index, layer, binding, Sha($"owner:{layer}:{binding}"), true,
            observations, totals)).ToImmutableArray();
    var pages = Pages(reconciler, layers[4], identities, totals);
    var export = reconciler.CreateExportEvidence(layers[5], identities.Select(identity =>
        (identity.IdentitySha256,
            $"{{\"identitySha256\":\"{identity.IdentitySha256}\",\"renderedText\":\"{exportRenderedText}\"}}")), totals);
    var permission = reconciler.CreatePermissionEvidence(
        StatisticReconciliationDirectPermissionStates.AuthorizedDetail,
        Sha($"auth:{kind}"), identityCount, identityCount);
    var request = new StatisticReconciliationDirectRequest(
        "reconciliation-direct-1", binding, "expected-generation-1", "actual-generation-1",
        permission, layers, pages, export);
    return new Fixture(reconciler, request, totals);
}

static ImmutableArray<StatisticReconciliationDirectApiPage> Pages(
    StatisticReconciliationDirectReconciler reconciler,
    StatisticReconciliationDirectLayerCapture apiLayer,
    StatisticReconciliationDirectIdentity[] identities,
    StatisticReconciliationDirectFullFilterTotals totals)
{
    var result = new List<StatisticReconciliationDirectApiPage>();
    foreach (var pageSize in new[] { 1, 50 })
    foreach (var direction in new[] { "ASC", "DESC" })
    {
        var ordered = identities.OrderBy(value => value.StableSortKey, StringComparer.Ordinal)
            .ThenBy(value => value.IdentitySha256, StringComparer.Ordinal).ToArray();
        if (direction == "DESC") Array.Reverse(ordered);
        var pageCount = Math.Max(1, (ordered.Length + pageSize - 1) / pageSize);
        for (var page = 0; page < pageCount; page++)
        {
            var rows = ordered.Skip(page * pageSize).Take(pageSize)
                .Select(value => value.IdentitySha256);
            result.Add(reconciler.CreateApiPage(
                apiLayer, page, pageSize, direction, pageCount, ordered.LongLength, rows, totals));
        }
    }
    return result.ToImmutableArray();
}

static StatisticReconciliationDirectRequest MutateValue(Fixture fixture, int layerIndex, string value)
    => MutateAtoms(fixture, layerIndex, rows => rows.Select(row =>
        row.AtomKind == StatisticReconciliationDirectAtomKinds.Value
            ? Recreate(fixture, row, value)
            : row).ToList());

static StatisticReconciliationDirectRequest MutateAtoms(
    Fixture fixture,
    int layerIndex,
    Func<List<StatisticReconciliationDirectObservation>, List<StatisticReconciliationDirectObservation>> mutate)
{
    var layers = fixture.Request.Layers.ToArray();
    var original = layers[layerIndex];
    var rows = mutate(original.Observations.ToList());
    layers[layerIndex] = fixture.Reconciler.CreateLayer(
        layerIndex, original.Layer, original.ComparisonBindingSha256,
        Sha($"mutated-owner:{original.Layer}:{rows.Count}"), true, rows,
        original.FullFilterTotals);
    var request = fixture.Request with
    {
        ActualGenerationId = $"actual-mutated-{layerIndex}",
        Layers = layers.ToImmutableArray()
    };
    if (layerIndex == 4)
    {
        request = request with
        {
            ApiPages = request.ApiPages.Select(page => fixture.Reconciler.CreateApiPage(
                layers[4], page.Page, page.PageSize, page.SortDirection,
                page.TotalPages, page.TotalRows,
                page.Rows.Select(row => row.IdentitySha256), page.FullFilterTotals))
                .ToImmutableArray()
        };
    }
    if (layerIndex == 5)
    {
        request = request with
        {
            ExportEvidence = fixture.Reconciler.CreateExportEvidence(
                layers[5], request.ExportEvidence!.Rows.Select(row =>
                    (row.IdentitySha256, row.CanonicalRowJson)),
                request.ExportEvidence.FullFilterTotals)
        };
    }
    return request;
}

static StatisticReconciliationDirectObservation Recreate(
    Fixture fixture,
    StatisticReconciliationDirectObservation value,
    string? canonical,
    long? numericValueCount = null)
    => fixture.Reconciler.CreateObservation(
        value.Identity, value.AtomKind, value.ValueType, value.ValueState,
        canonical, value.DecimalScale, value.ReportCount, value.RowCount,
        numericValueCount ?? value.NumericValueCount, value.SourceLineageSha256,
        value.CollectionSemantics);

static StatisticReconciliationDirectRequest Rebind(Fixture fixture, char marker)
{
    var binding = Sha($"recheck:{marker}");
    var layers = fixture.Request.Layers.Select((layer, index) =>
        fixture.Reconciler.CreateLayer(index, layer.Layer, binding,
            Sha($"recheck-owner:{marker}:{index}"), true,
            fixture.Request.Layers[0].Observations, fixture.Totals)).ToImmutableArray();
    return fixture.Request with
    {
        ComparisonBindingSha256 = binding,
        ExpectedGenerationId = $"expected-recheck-{marker}",
        ActualGenerationId = $"actual-recheck-{marker}",
        Layers = layers
    };
}

static void Matched(StatisticReconciliationDirectResult result, string code)
{
    Equal(StatisticReconciliationDirectOutcomes.Matched, result.Outcome, code);
    Require(result.AllRequiredLayersZero && result.EvidenceComplete &&
            result.PagingTotalsStable && result.PagingRowsExact && result.ExportCanonical &&
            result.EarliestDivergentLayer is null && result.RootCause is null,
        code + "_GATES");
}

static void Mismatched(StatisticReconciliationDirectResult result, string layer, string code)
{
    Equal(StatisticReconciliationDirectOutcomes.Mismatched, result.Outcome, code);
    Equal(layer, result.EarliestDivergentLayer, code + "_EARLIEST");
    Require(!result.AllRequiredLayersZero && result.RootCause is not null, code + "_ROOT");
}

static string Sha(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static void Require(bool condition, string code)
{
    if (!condition) throw new InvalidOperationException(code);
}

static void ExpectCode(Action action, string expectedCode, string code)
{
    try
    {
        action();
        throw new InvalidOperationException(code + ":NO_EXCEPTION");
    }
    catch (StatisticReconciliationDirectException exception)
    {
        Equal(expectedCode, exception.Code, code);
    }
}

static void Equal<T>(T expected, T actual, string code)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{code}: expected={expected}; actual={actual}");
}

sealed record Fixture(
    StatisticReconciliationDirectReconciler Reconciler,
    StatisticReconciliationDirectRequest Request,
    StatisticReconciliationDirectFullFilterTotals Totals);
