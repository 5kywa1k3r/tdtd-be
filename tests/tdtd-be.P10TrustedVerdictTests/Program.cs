using System.Collections.Immutable;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

var tests = new (string Id, Action Run)[]
{
    ("P10-TRUSTED-CURRENT-01", ExactFamilySelected),
    ("P10-TRUSTED-CURRENT-02", CrossTriggerSameFamilySelected),
    ("P10-TRUSTED-CURRENT-03", OtherFamilyExcluded),
    ("P10-FAMILY-LOOKUP-01", ClientLookupRequiresTriggerAssignment),
    ("P10-FAMILY-LOOKUP-02", ServerRecheckLookupUsesExactFamily),
    ("P10-FAMILY-CAPTURE-01", CrossTriggerCaptureBindingMatches),
    ("P10-FAMILY-CAPTURE-02", CaptureBindingRejectsOtherFamily),
    ("P10-TRUSTED-CURRENT-04", SourceReplayMutationFails),
    ("P10-TRUSTED-CURRENT-05", BoundaryReplayMutationFails),
    ("P10-TRUSTED-CURRENT-06", ApiReplayMutationFails),
    ("P10-TRUSTED-CURRENT-07", ExportReplayMutationFails),
    ("P10-BASIC-PLAN-01", LegacyFlowPlanRemainsValid),
    ("P10-BASIC-PLAN-02", NewNonFlowPlanIsValid),
    ("P10-BASIC-PLAN-03", LegacyNonFlowPlanIsRejected),
    ("P10-BASIC-PLAN-04", ProfileAndUnknownAreRejected),
    ("P10-LIFECYCLE-PROOF-01", ExactLifecycleReplayIsStable),
    ("P10-LIFECYCLE-PROOF-02", LifecycleMutationFailsClosed),
    ("P10-TYPED-V3-01", CollectionSemanticsAreHashBound),
    ("P10-TYPED-V3-02", UnorderedCollectionRequiresCanonicalSet),
    ("P10-TYPED-V3-03", TransitionLegsAreHashBound),
    ("P10-TYPED-V3-04", TransitionKindCanonicalMustMatch),
    ("P10-TYPED-V3-05", MissingAggregatesAreCanonical),
    ("P10-TYPED-V3-06", MissingAggregateTamperIsRejected),
    ("P10-TYPED-V3-07", NonNumericMinMaxAreCanonical),
    ("P10-FRESHNESS-V2-01", NeutralMembershipBridgesOwnerDomains),
    ("P10-FRESHNESS-V2-02", CurrentSourceLineageDriftIsStale),
    ("P10-FRESHNESS-V2-03", NeutralMembershipDriftIsStale),
    ("P10-DIRECT-VIEW-01", DirectViewsUseTypedRelationalEvidence),
    ("P10-DIRECT-VIEW-02", DirectFieldExpectedScopeProjectsCanonicalFilter),
    ("P10-DIRECT-VIEW-03", DirectFieldExpectedScopeFailsClosed),
    ("P10-DIRECT-VIEW-04", DirectFieldScopeDoesNotIntersectActualCapture)
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Id}");
}

RelationalReplayMutationFails();
Console.WriteLine(
    "P10_TRUSTED_CURRENT_RELATIONAL_REPLAY_OK checks=4 exact=true " +
    "committedDriftRejected=true firstDriftRejected=true " +
    "finalDriftRejected=true");

Console.WriteLine(
    "P10_TRUSTED_CURRENT_OWNER_OK cases=31 exactFamilyScope=true " +
    "crossTriggerSuccessorVisible=true clientTriggerStrict=true fullOwnerReplay=true " +
    "capturePlanV1V2Separated=true basicExactSix=true " +
    "lifecycleStableReplay=true lifecycleMutationFailClosed=true " +
    "typedV3Transitions=true collectionSemanticsBound=true " +
    "missingAggregatesCanonical=true neutralMembershipV2=true " +
    "relationalReplay=true directViewTypedRelational=true");
return 0;

static void DirectViewsUseTypedRelationalEvidence()
{
    var relative = Path.Combine(
        "tdtd-be",
        "Services",
        "StatisticsReconciliation",
        "ActualObservation",
        "StatisticReconciliationTrustedRelationalVerdictDeriver.cs");
    var explicitPath = Environment.GetEnvironmentVariable(
        "P10_PREPARED_TRUSTED_DERIVER");
    string? path = !string.IsNullOrWhiteSpace(explicitPath) &&
                   File.Exists(explicitPath)
        ? Path.GetFullPath(explicitPath)
        : null;
    if (path is null)
    {
        foreach (var candidate in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            var directory = new DirectoryInfo(candidate);
            while (directory is not null)
            {
                var file = Path.Combine(directory.FullName, relative);
                if (File.Exists(file))
                {
                    path = file;
                    break;
                }
                directory = directory.Parent;
            }
            if (path is not null)
                break;
        }
    }
    Require(path is not null, "TRUSTED_RELATIONAL_DERIVER_SOURCE_MISSING");
    var source = File.ReadAllText(path!);
    Require(!source.Contains(
                "API_VALUE_PARITY_NOT_PROVEN",
                StringComparison.Ordinal) &&
            !source.Contains(
                "EXPORT_VALUE_PARITY_NOT_PROVEN",
                StringComparison.Ordinal),
        "DIRECT_VIEW_UNCONDITIONAL_INCOMPLETE_FORBIDDEN");
    Require(source.Contains(
                "StatisticReconciliationActualApiSurfaces.DirectLabel => 2",
                StringComparison.Ordinal) &&
            source.Contains(
                "aggregateEvidence.NonzeroCount != 0",
                StringComparison.Ordinal),
        "DIRECT_VIEW_AGGREGATE_ZERO_PREREQUISITE_REQUIRED");
    Require(source.Contains(
                "return RelationalTypedLayer(6",
                StringComparison.Ordinal) &&
            source.Contains(
                "return RelationalTypedLayer(7",
                StringComparison.Ordinal),
        "DIRECT_VIEW_TYPED_COMPARISON_REQUIRED");
    Require(source.Contains(
                "RequireRelationalProof(",
                StringComparison.Ordinal) &&
            source.Contains(
                "facts.ApiCaptureSha256 != layers[6].CaptureSemanticSha256",
                StringComparison.Ordinal) &&
            source.Contains(
                "facts.ExportCaptureSha256 != layers[7].CaptureSemanticSha256",
                StringComparison.Ordinal),
        "DIRECT_VIEW_RELATIONAL_PROVENANCE_REQUIRED");
    var summaryStart = source.IndexOf(
        "private static StatisticReconciliationRootCauseLayerEvidence SummaryLayer(",
        StringComparison.Ordinal);
    var apiStart = source.IndexOf(
        "private static StatisticReconciliationRootCauseLayerEvidence ApiLayer(",
        StringComparison.Ordinal);
    Require(summaryStart >= 0 && apiStart > summaryStart,
        "SUMMARY_LAYER_SOURCE_BOUNDARY_MISSING");
    var summarySource = source[summaryStart..apiStart];
    Require(source.Contains(
                "RequireSummaryPlanBinding(actual, relationalProof)",
                StringComparison.Ordinal) &&
            summarySource.Contains(
                "FamilyNotApplicableProven(family, facts)",
                StringComparison.Ordinal) &&
            summarySource.Contains(
                "descriptorCount != 0",
                StringComparison.Ordinal) &&
            !summarySource.Contains(
                "actualAtoms.Length == 0",
                StringComparison.Ordinal),
        "SUMMARY_FAMILY_NONAPPLICABILITY_PROOF_REQUIRED");
    Require(source.Contains(
                "facts.CrossViewAuthorizationRelationSha256",
                StringComparison.Ordinal) &&
            !source.Contains(
                "export.AuthorizationSnapshotSha256 == run.AuthorizationSnapshotHash",
                StringComparison.Ordinal),
        "EXPORT_AUTHORIZATION_DOMAIN_RELATION_REQUIRED");

    var directStart = source.IndexOf(
        "private static StatisticReconciliationRootCauseLayerEvidence DirectLayer(",
        StringComparison.Ordinal);
    var aggregateStart = source.IndexOf(
        "private static StatisticReconciliationRootCauseLayerEvidence AggregateLayer(",
        StringComparison.Ordinal);
    var exportStart = source.IndexOf(
        "private static StatisticReconciliationRootCauseLayerEvidence ExportLayer(",
        StringComparison.Ordinal);
    var proofStart = source.IndexOf(
        "private static StatisticReconciliationActualRelationalProofBinding",
        exportStart,
        StringComparison.Ordinal);
    Require(directStart >= 0 && aggregateStart > directStart &&
            summaryStart > aggregateStart && apiStart > summaryStart &&
            exportStart > apiStart && proofStart > exportStart,
        "DIRECT_FIELD_EXPECTED_SCOPE_SOURCE_BOUNDARIES_MISSING");
    var directSource = source[directStart..aggregateStart];
    var aggregateSource = source[aggregateStart..summaryStart];
    var apiSource = source[apiStart..exportStart];
    var exportSource = source[exportStart..proofStart];
    Require(!directSource.Contains(
                "ProjectDirectFieldExpectedAtoms",
                StringComparison.Ordinal) &&
            !aggregateSource.Contains(
                "ProjectDirectFieldExpectedAtoms",
                StringComparison.Ordinal) &&
            apiSource.Contains(
                "ProjectDirectFieldExpectedAtoms",
                StringComparison.Ordinal) &&
            exportSource.Contains(
                "ProjectDirectFieldExpectedAtoms",
                StringComparison.Ordinal),
        "DIRECT_FIELD_EXPECTED_SCOPE_MUST_ONLY_APPLY_TO_API_EXPORT");
    Require(apiSource.Contains(
                "StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan)",
                StringComparison.Ordinal) &&
            exportSource.Contains(
                "StatisticReconciliationActualCapturePlanIntegrity.IsV4(plan)",
                StringComparison.Ordinal) &&
            exportSource.Contains(
                "plan.Api.Surface ==",
                StringComparison.Ordinal) &&
            apiSource.Contains(
                "expectedAtoms.Where(value => value.Family",
                StringComparison.Ordinal) &&
            exportSource.Contains(
                "expectedAtoms.Where(value => value.Family",
                StringComparison.Ordinal),
        "DIRECT_FIELD_EXPECTED_SCOPE_V4_GATE_AND_LEGACY_FULL_PATH_REQUIRED");
}

static void DirectFieldExpectedScopeProjectsCanonicalFilter()
{
    var filter = DirectFieldFilter();
    filter["statisticLabelCode"] = "AMOUNT";
    var run = DirectFieldScopeRun(filter);
    var allExpected = ImmutableArray.Create(
        DirectFieldAtom("field_amount", "amount", "2026-08", "SUM", "10"),
        DirectFieldAtom("field_amount", "amount", "2026-08", "COUNT", "1"),
        DirectFieldAtom("field_amount", "other", "2026-08", "SUM", "11"),
        DirectFieldAtom("field_amount", "amount", "2026-07", "SUM", "9"),
        DirectFieldAtom("field_units", "units", "2026-08", "SUM", "20"),
        DirectFieldAtom("field_amount", "amount", "2026-08", "MEAN", "10",
            family: "BASIC"));

    Require(StatisticReconciliationRunService
            .TryResolveExactDirectFieldExpectedScope(
                run.CanonicalFilterJson,
                run.FilterHash,
                run.PeriodInstanceKey,
                run.ConceptKey,
                out var scope) &&
            scope is not null &&
            scope.FieldId == "field_amount" &&
            scope.FieldKey == "amount" &&
            scope.StatisticLabelCode == "amount",
        "DIRECT_FIELD_SCOPE_MUST_RESOLVE_FIELD_ID_AND_CANONICAL_LABEL");

    var projected = StatisticReconciliationTrustedVerdictDeriver
        .ProjectDirectFieldExpectedAtoms(run, allExpected);
    Require(projected.Length == 2 &&
            projected.All(atom =>
                atom.Family == "DIRECT" &&
                atom.Kind == "FIELD" &&
                atom.FieldId == "field_amount" &&
                atom.MetricId == "amount" &&
                atom.PeriodKey == "2026-08") &&
            allExpected.Length == 6,
        "DIRECT_FIELD_EXPECTED_SCOPE_MUST_PROJECT_FIELD_LABEL_PERIOD_ONLY");
}

static void DirectFieldExpectedScopeFailsClosed()
{
    var expected = ImmutableArray.Create(
        DirectFieldAtom("field_amount", "amount", "2026-08", "SUM", "10"));

    var missingFieldId = DirectFieldFilter();
    missingFieldId.Remove("fieldId");
    RequireDirectFieldScopeRejected(DirectFieldScopeRun(missingFieldId), expected);

    var missingFieldKey = DirectFieldFilter();
    missingFieldKey.Remove("fieldKey");
    RequireDirectFieldScopeRejected(DirectFieldScopeRun(missingFieldKey), expected);

    RequireDirectFieldScopeRejected(
        DirectFieldScopeRun(DirectFieldFilter(), conceptKey: "other"),
        expected);

    foreach (var selector in new (string Name, object Value)[]
             {
                 ("fieldType", "number"),
                 ("bucketKey", "bucket"),
                 ("showInTree", true),
                 ("showInDetail", false),
                 ("reportStatus", 1)
             })
    {
        var filter = DirectFieldFilter();
        filter[selector.Name] = selector.Value;
        RequireDirectFieldScopeRejected(DirectFieldScopeRun(filter), expected);
    }

    var canonical = CanonicalDirectFieldFilter(DirectFieldFilter());
    RequireDirectFieldScopeRejected(
        DirectFieldScopeRunFromCanonical(canonical + " "),
        expected);
    RequireDirectFieldScopeRejected(
        DirectFieldScopeRunFromCanonical(canonical, filterHash: new string('a', 64)),
        expected);
    RequireDirectFieldScopeRejected(
        DirectFieldScopeRun(DirectFieldFilter()),
        ImmutableArray.Create(
            DirectFieldAtom("field_units", "units", "2026-08", "SUM", "20")));
    RequireDirectFieldScopeRejected(
        DirectFieldScopeRun(DirectFieldFilter()),
        default);
}

static void DirectFieldScopeDoesNotIntersectActualCapture()
{
    var run = DirectFieldScopeRun(DirectFieldFilter());
    var selectedExpected = StatisticReconciliationTrustedVerdictDeriver
        .ProjectDirectFieldExpectedAtoms(
            run,
            ImmutableArray.Create(
                DirectFieldAtom(
                    "field_amount", "amount", "2026-08", "SUM", "10"),
                DirectFieldAtom(
                    "field_units", "units", "2026-08", "SUM", "20")));
    var capturedActual =
        ImmutableArray<StatisticReconciliationActualTypedObservation>.Empty;

    Require(selectedExpected.Length == 1 &&
            selectedExpected[0].FieldId == "field_amount" &&
            capturedActual.IsEmpty,
        "DIRECT_FIELD_EXPECTED_SCOPE_MUST_BE_INDEPENDENT_OF_ACTUAL_CAPTURE");

    var expectedState = new StatisticReconciliationStatefulObservation(
        new StatisticReconciliationStableIdentity(
            StatisticReconciliationStableIdentityKinds.Row,
            ["DIRECT", "FIELD", "amount", "2026-08", "field_amount", "SUM"]),
        "NUMBER",
        StatisticReconciliationObservationValueStates.Value,
        "10",
        0);
    var comparison = new StatisticReconciliationStatefulComparator()
        .Compare(expectedState, null);
    Require(!comparison.Equal &&
            comparison.DeltaCode ==
                StatisticReconciliationIdentityDeltaCodes.MissingIdentity,
        "MISSING_SELECTED_DIRECT_FIELD_MUST_REMAIN_MISSING_IDENTITY");
}

static Dictionary<string, object?> DirectFieldFilter()
    => new(StringComparer.Ordinal)
    {
        ["periodInstanceKey"] = "MONTH:2026-08",
        ["fieldId"] = "field_amount",
        ["fieldKey"] = "amount",
        ["bucketKey"] = null,
        ["periodKey"] = "2026-08"
    };

static StatisticReconciliationRun DirectFieldScopeRun(
    IReadOnlyDictionary<string, object?> filter,
    string conceptKey = "amount")
    => DirectFieldScopeRunFromCanonical(CanonicalDirectFieldFilter(filter), conceptKey);

static StatisticReconciliationRun DirectFieldScopeRunFromCanonical(
    string canonicalFilterJson,
    string conceptKey = "amount",
    string? filterHash = null)
    => new()
    {
        CanonicalFilterJson = canonicalFilterJson,
        FilterHash = filterHash ??
            StatisticReconciliationCanonicalJson.HashText(canonicalFilterJson),
        PeriodInstanceKey = "MONTH:2026-08",
        ConceptKey = conceptKey
    };

static string CanonicalDirectFieldFilter(
    IReadOnlyDictionary<string, object?> filter)
{
    var element = JsonSerializer.SerializeToElement(filter);
    return StatisticReconciliationCanonicalJson.Canonicalize(element);
}

static StatisticReconciliationObservationAtom DirectFieldAtom(
    string fieldId,
    string metricId,
    string periodKey,
    string atomKind,
    string canonicalValue,
    string family = "DIRECT")
    => new()
    {
        IdentitySha256 = new string('1', 64),
        Family = family,
        Kind = "FIELD",
        MetricId = metricId,
        FieldId = fieldId,
        PeriodKey = periodKey,
        AtomKind = atomKind,
        ValueType = "NUMBER",
        ValueState = StatisticReconciliationObservationValueStates.Value,
        CanonicalValue = canonicalValue,
        DecimalScale = 0,
        OccurrenceCount = 1,
        ReportCount = 1,
        NumericValueCount = 1,
        ValueIdentitySha256 = new string('2', 64),
        AtomSemanticSha256 = new string('3', 64)
    };

static void RequireDirectFieldScopeRejected(
    StatisticReconciliationRun run,
    ImmutableArray<StatisticReconciliationObservationAtom> expected)
{
    try
    {
        _ = StatisticReconciliationTrustedVerdictDeriver
            .ProjectDirectFieldExpectedAtoms(run, expected);
    }
    catch (StatisticReconciliationTrustedVerdictPipelineException error)
    {
        Require(error.Reason == "DIRECT_FIELD_EXPECTED_SCOPE_NOT_EXACT",
            "DIRECT_FIELD_EXPECTED_SCOPE_FAILURE_REASON_MUST_BE_STABLE");
        return;
    }

    throw new InvalidOperationException(
        "DIRECT_FIELD_EXPECTED_SCOPE_MUST_FAIL_CLOSED");
}

static void CollectionSemanticsAreHashBound()
{
    var unordered = CollectionAtom("UNORDERED", "[\"a\",\"b\"]");
    var ordered = CollectionAtom("ORDERED", "[\"a\",\"b\"]");
    Require(unordered.IdentitySha256 != ordered.IdentitySha256 &&
            unordered.ValueIdentitySha256 != ordered.ValueIdentitySha256 &&
            unordered.AtomSemanticSha256 != ordered.AtomSemanticSha256,
        "COLLECTION_SEMANTICS_MUST_BIND_IDENTITY_VALUE_AND_ATOM");
    StatisticReconciliationActualTypedObservationCanonical.Normalize(unordered);
    StatisticReconciliationActualTypedObservationCanonical.Normalize(ordered);
    Throws(() => StatisticReconciliationActualTypedObservationCanonical.Normalize(
        unordered with { CollectionSemantics = "ORDERED" }));
}

static void UnorderedCollectionRequiresCanonicalSet()
{
    Throws(() => CollectionAtom("UNORDERED", "[\"b\",\"a\"]"));
    Throws(() => CollectionAtom("UNORDERED", "[\"a\",\"a\"]"));
    StatisticReconciliationActualTypedObservationCanonical.Normalize(
        CollectionAtom("ORDERED", "[\"b\",\"a\"]"));
}

static void TransitionLegsAreHashBound()
{
    var before = TransitionAtom("BEFORE", "TEXT", "TEXT", "old");
    var after = TransitionAtom("AFTER", "TEXT", "TEXT", "old");
    Require(before.IdentitySha256 != after.IdentitySha256 &&
            before.AtomSemanticSha256 != after.AtomSemanticSha256,
        "BEFORE_AFTER_MUST_NOT_COLLIDE");
    StatisticReconciliationActualTypedObservationCanonical.Normalize(before);
    StatisticReconciliationActualTypedObservationCanonical.Normalize(after);
    Throws(() => StatisticReconciliationActualTypedObservationCanonical.Normalize(
        before with { TransitionLeg = "AFTER" }));
}

static void TransitionKindCanonicalMustMatch()
{
    var changed = TransitionAtom(
        "CHANGE_STATE", "TRANSITION_KIND", "TEXT", "CHANGED");
    StatisticReconciliationActualTypedObservationCanonical.Normalize(changed);
    Throws(() => TransitionAtom(
        "CHANGE_STATE", "TRANSITION_KIND", "TEXT", "ADDED",
        transitionKind: "CHANGED"));
    Throws(() => StatisticReconciliationActualTypedObservationCanonical.Normalize(
        changed with { TransitionKind = "ADDED" }));
}

static void MissingAggregatesAreCanonical()
{
    foreach (var atomKind in new[] { "SUM", "MIN", "MAX", "MEAN" })
    {
        var valueType = atomKind is "SUM" or "MEAN" ? "NUMBER" : "TEXT";
        var atom = MissingAggregate(atomKind, valueType);
        StatisticReconciliationActualTypedObservationCanonical.Normalize(atom);
        Require(atom.ValueState == "MISSING" && atom.CanonicalValue.Length == 0 &&
                atom.DecimalScale == 0 && atom.OccurrenceCount == 0,
            $"{atomKind}_MISSING_AGGREGATE_MUST_BE_CANONICAL");
    }
}

static void MissingAggregateTamperIsRejected()
{
    var atom = MissingAggregate("SUM", "NUMBER");
    Throws(() => StatisticReconciliationActualTypedObservationCanonical.Normalize(
        atom with { CanonicalValue = "0" }));
    Throws(() => StatisticReconciliationActualTypedObservationCanonical.Normalize(
        atom with { OccurrenceCount = 1 }));
    Throws(() => StatisticReconciliationActualTypedObservationCanonical.Normalize(
        atom with { DecimalScale = 1 }));
}

static void NonNumericMinMaxAreCanonical()
{
    var min = SummaryAtom("MIN", "TEXT", "VALUE", "alpha", occurrence: 1);
    var max = SummaryAtom("MAX", "BOOLEAN", "VALUE", "true", occurrence: 1);
    StatisticReconciliationActualTypedObservationCanonical.Normalize(min);
    StatisticReconciliationActualTypedObservationCanonical.Normalize(max);
    Throws(() => SummaryAtom("MIN", "BOOLEAN", "VALUE", "TRUE", 1));
}

static StatisticReconciliationActualTypedObservation MissingAggregate(
    string atomKind,
    string valueType)
    => SummaryAtom(atomKind, valueType, "MISSING", "", occurrence: 0);

static StatisticReconciliationActualTypedObservation SummaryAtom(
    string atomKind,
    string valueType,
    string valueState,
    string canonical,
    long occurrence)
    => StatisticReconciliationActualTypedObservationCanonical
        .CreateSummaryCompatible(
            0,
            StatisticReconciliationActualCoherentLayers.DirectProjection,
            "owner-direct",
            new string('c', 64),
            "DIRECT",
            "FIELD",
            "metric-summary",
            "period-1",
            atomKind,
            valueType,
            valueState,
            canonical,
            occurrenceCount: occurrence,
            reportCount: 1,
            rowCount: 1,
            fieldId: "field-summary");

static void NeutralMembershipBridgesOwnerDomains()
{
    var request = Freshness(
        expectedSource: '1', capturedSource: '2', currentSource: '2',
        expectedMembership: 'a', capturedMembership: 'a', currentMembership: 'a');
    var result = new StatisticReconciliationFreshnessEvaluator().Evaluate(request);
    Require(result.State == StatisticReconciliationFreshnessStates.Fresh &&
            result.Signable,
        "EQUIVALENT_NEUTRAL_MEMBERSHIP_MUST_BRIDGE_OWNER_HASH_DOMAINS");
}

static void CurrentSourceLineageDriftIsStale()
{
    var request = Freshness(
        expectedSource: '1', capturedSource: '2', currentSource: '3',
        expectedMembership: 'a', capturedMembership: 'a', currentMembership: 'a');
    var result = new StatisticReconciliationFreshnessEvaluator().Evaluate(request);
    Require(result.State == StatisticReconciliationFreshnessStates.Stale &&
            result.DriftDomain == StatisticReconciliationFreshnessDomains.Source &&
            !result.Signable,
        "CURRENT_SOURCE_LINEAGE_DRIFT_MUST_BE_STALE");
}

static void NeutralMembershipDriftIsStale()
{
    var request = Freshness(
        expectedSource: '1', capturedSource: '2', currentSource: '2',
        expectedMembership: 'a', capturedMembership: 'b', currentMembership: 'b');
    var result = new StatisticReconciliationFreshnessEvaluator().Evaluate(request);
    Require(result.State == StatisticReconciliationFreshnessStates.Stale &&
            result.DriftDomain == StatisticReconciliationFreshnessDomains.Source &&
            !result.Signable,
        "NEUTRAL_MEMBERSHIP_DRIFT_MUST_BE_STALE");
}

static StatisticReconciliationFreshnessRequest Freshness(
    char expectedSource,
    char capturedSource,
    char currentSource,
    char expectedMembership,
    char capturedMembership,
    char currentMembership)
{
    var expected = Binding(expectedSource, expectedMembership);
    var captured = new StatisticReconciliationActualFreshnessPins(
        Binding(capturedSource, capturedMembership),
        new string('6', 64), new string('7', 64), new string('8', 64));
    var current = new StatisticReconciliationActualFreshnessPins(
        Binding(currentSource, currentMembership),
        new string('6', 64), new string('7', 64), new string('8', 64));
    return new(expected, captured, current, true, true, true);
}

static StatisticReconciliationComparisonBindingPins Binding(
    char source,
    char membership)
    => new(
        new string(source, 64),
        new string('c', 64),
        new string('d', 64),
        new string('e', 64),
        new string('f', 64),
        new string(membership, 64));

static StatisticReconciliationActualTypedObservation CollectionAtom(
    string semantics,
    string canonical)
    => StatisticReconciliationActualTypedObservationCanonical
        .CreateSummaryCompatible(
            0,
            StatisticReconciliationActualCoherentLayers.DirectProjection,
            "owner-direct",
            new string('a', 64),
            "DIRECT",
            "FIELD",
            "metric-list",
            "period-1",
            "STRING_LIST",
            "STRING_LIST",
            "VALUE",
            canonical,
            reportCount: 1,
            rowCount: 1,
            fieldId: "field-list",
            collectionSemantics: semantics);

static StatisticReconciliationActualTypedObservation TransitionAtom(
    string leg,
    string atomKind,
    string valueType,
    string canonical,
    string transitionKind = "CHANGED")
    => StatisticReconciliationActualTypedObservationCanonical
        .CreateSummaryCompatible(
            0,
            StatisticReconciliationActualCoherentLayers.Diff,
            "owner-diff",
            new string('b', 64),
            "DIFF",
            "FIELD",
            "metric-diff",
            "period-1",
            atomKind,
            valueType,
            "VALUE",
            canonical,
            reportCount: 1,
            rowCount: 1,
            fieldId: "field-diff",
            diffKind: "FIELD",
            transitionLeg: leg,
            transitionKind: transitionKind);
static void ExactLifecycleReplayIsStable()
{
    var proof = LifecycleProof(
        StatisticReconciliationLifecycleOutcomes.Matched,
        new string('b', 64), new string('d', 64));
    var verifier = new StatisticReconciliationTrustedLifecycleVerifier(null!);
    var stable = verifier.RequireStable(proof, proof);
    Require(stable.EvidenceComplete && stable.CaptureCoherent &&
            stable.Outcome == StatisticReconciliationLifecycleOutcomes.Matched &&
            stable.State == StatisticReconciliationTrustedLifecycleProofStates.Valid,
        "EXACT_LIFECYCLE_REPLAY_MUST_REMAIN_COMPLETE");
}

static void LifecycleMutationFailsClosed()
{
    var before = LifecycleProof(
        StatisticReconciliationLifecycleOutcomes.Matched,
        new string('b', 64), new string('d', 64));
    var after = LifecycleProof(
        StatisticReconciliationLifecycleOutcomes.Matched,
        new string('c', 64), new string('e', 64));
    var verifier = new StatisticReconciliationTrustedLifecycleVerifier(null!);
    var changed = verifier.RequireStable(before, after);
    Require(!changed.EvidenceComplete && !changed.CaptureCoherent &&
            changed.State ==
                StatisticReconciliationTrustedLifecycleProofStates.ReplayChanged,
        "LIFECYCLE_ROWSET_MUTATION_MUST_FAIL_CLOSED");
}

static StatisticReconciliationTrustedLifecycleProof LifecycleProof(
    string outcome,
    string rowSetSha256,
    string proofSha256)
    => new(
        StatisticReconciliationTrustedLifecycleProofStates.Valid,
        outcome,
        null,
        0,
        0,
        true,
        outcome != StatisticReconciliationLifecycleOutcomes.Stale,
        new string('a', 64),
        7,
        rowSetSha256,
        new string('f', 64),
        new string('9', 64),
        [],
        proofSha256);

static void ExactFamilySelected()
{
    var run = Run();
    var filter = StatisticReconciliationTrustedCurrentOwnerReader
        .CurrentTopologyFilter(run).Compile();
    Require(filter(Job(run, run.SourceReportId, run.P9RunId)),
        "EXACT_FAMILY_REQUIRED");
}

static void CrossTriggerSameFamilySelected()
{
    var run = Run();
    var filter = StatisticReconciliationTrustedCurrentOwnerReader
        .CurrentTopologyFilter(run).Compile();
    var successor = Job(
        run,
        "777777777777777777777777",
        "888888888888888888888888");
    successor.WorkAssignmentId = "999999999999999999999999";
    successor.ConfigVersionId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    Require(filter(successor),
        "CROSS_TRIGGER_SAME_FAMILY_SUCCESSOR_MUST_REMAIN_VISIBLE");
}

static void OtherFamilyExcluded()
{
    var run = Run();
    var filter = StatisticReconciliationTrustedCurrentOwnerReader
        .CurrentTopologyFilter(run).Compile();

    var otherFamily = Job(run, run.SourceReportId, run.P9RunId);
    otherFamily.DynamicFormFamilyId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    Require(!filter(otherFamily), "OTHER_FORM_FAMILY_MUST_BE_EXCLUDED");

    var otherTemplate = Job(run, run.SourceReportId, run.P9RunId);
    otherTemplate.DynamicFormTemplateId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    Require(!filter(otherTemplate), "OTHER_FORM_TEMPLATE_MUST_BE_EXCLUDED");

    var otherPeriod = Job(run, run.SourceReportId, run.P9RunId);
    otherPeriod.PeriodInstanceKey = "period-instance-2";
    Require(!filter(otherPeriod), "OTHER_PERIOD_MUST_BE_EXCLUDED");
}

static void ClientLookupRequiresTriggerAssignment()
{
    var run = Run();
    var rendered = RenderFilter(
        StatisticReconciliationRunService.BuildP9PublicationLookupFilter(
            Builders<WorkReportStatisticRebuildJob>.Filter,
            run.P9RunId,
            run.WorkId,
            run.ScopeAssignmentId,
            serverRecheckFamily: null));
    Require(rendered.Contains("workAssignmentId", StringComparison.Ordinal),
        "CLIENT_LOOKUP_MUST_BIND_TRIGGER_ASSIGNMENT");
}

static void ServerRecheckLookupUsesExactFamily()
{
    var run = Run();
    var rendered = RenderFilter(
        StatisticReconciliationRunService.BuildP9PublicationLookupFilter(
            Builders<WorkReportStatisticRebuildJob>.Filter,
            run.P9RunId,
            run.WorkId,
            run.ScopeAssignmentId,
            run));
    Require(!rendered.Contains("workAssignmentId", StringComparison.Ordinal) &&
            !rendered.Contains("sourceReportId", StringComparison.Ordinal),
        "SERVER_RECHECK_LOOKUP_MUST_NOT_BIND_TRIGGER_IDENTITY");
    foreach (var field in new[]
             {
                 "workId", "periodInstanceKey", "periodKind",
                 "dynamicFormFamilyId", "dynamicFormTemplateId",
                 "dynamicFormVersionNo", "dynamicFormSchemaHash",
                 "candidateChainId", "candidatePromptId"
             })
    {
        Require(rendered.Contains(field, StringComparison.Ordinal),
            "SERVER_RECHECK_FAMILY_PIN_MISSING:" + field);
    }

    var otherScope = Run();
    otherScope.ScopeAssignmentId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    Throws(() => StatisticReconciliationRunService
        .BuildP9PublicationLookupFilter(
            Builders<WorkReportStatisticRebuildJob>.Filter,
            run.P9RunId,
            run.WorkId,
            run.ScopeAssignmentId,
            otherScope));
}

static void CrossTriggerCaptureBindingMatches()
{
    var run = Run();
    var job = Job(run, run.SourceReportId, run.P9RunId);
    job.WorkAssignmentId = "999999999999999999999999";
    var authorizedScope = run.ScopeAssignmentId;
    Require(StatisticReconciliationActualMongoClaimedCaptureMaterialOwner
            .P9RunBindingMatches(run, job),
        "CROSS_TRIGGER_SAME_FAMILY_CAPTURE_MUST_MATCH");
    Require(run.ScopeAssignmentId == authorizedScope,
        "CAPTURE_MUST_PRESERVE_AUTHORIZED_SCOPE_ASSIGNMENT");
}

static void CaptureBindingRejectsOtherFamily()
{
    var run = Run();

    var otherFamily = Job(run, run.SourceReportId, run.P9RunId);
    otherFamily.DynamicFormFamilyId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    Require(!StatisticReconciliationActualMongoClaimedCaptureMaterialOwner
            .P9RunBindingMatches(run, otherFamily),
        "CAPTURE_MUST_REJECT_OTHER_FORM_FAMILY");

    var otherTemplate = Job(run, run.SourceReportId, run.P9RunId);
    otherTemplate.DynamicFormTemplateId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    Require(!StatisticReconciliationActualMongoClaimedCaptureMaterialOwner
            .P9RunBindingMatches(run, otherTemplate),
        "CAPTURE_MUST_REJECT_OTHER_FORM_TEMPLATE");

    var otherPeriod = Job(run, run.SourceReportId, run.P9RunId);
    otherPeriod.PeriodKind = "AD_HOC";
    Require(!StatisticReconciliationActualMongoClaimedCaptureMaterialOwner
            .P9RunBindingMatches(run, otherPeriod),
        "CAPTURE_MUST_REJECT_OTHER_PERIOD");
}

static string RenderFilter(
    FilterDefinition<WorkReportStatisticRebuildJob> filter)
    => filter.Render(new RenderArgs<WorkReportStatisticRebuildJob>(
            BsonSerializer.LookupSerializer<WorkReportStatisticRebuildJob>(),
            BsonSerializer.SerializerRegistry))
        .ToJson();

static void RelationalReplayMutationFails()
{
    var committed = new string('a', 64);
    Require(StatisticReconciliationTrustedCurrentOwnerReader
            .RelationalReplayExact(committed, committed, committed),
        "EXACT_RELATIONAL_REPLAY_REQUIRED");
    Require(!StatisticReconciliationTrustedCurrentOwnerReader
            .RelationalReplayExact(new string('b', 64), committed, committed),
        "COMMITTED_RELATIONAL_DRIFT_MUST_FAIL");
    Require(!StatisticReconciliationTrustedCurrentOwnerReader
            .RelationalReplayExact(committed, new string('b', 64), committed),
        "FIRST_RELATIONAL_DRIFT_MUST_FAIL");
    Require(!StatisticReconciliationTrustedCurrentOwnerReader
            .RelationalReplayExact(committed, committed, new string('b', 64)),
        "FINAL_RELATIONAL_DRIFT_MUST_FAIL");
}
static void SourceReplayMutationFails() => ReplayMutation(0);
static void BoundaryReplayMutationFails() => ReplayMutation(1);
static void ApiReplayMutationFails() => ReplayMutation(2);
static void ExportReplayMutationFails() => ReplayMutation(3);

static void ReplayMutation(int channel)
{
    var first = new[] { "source", "boundary", "api", "export" };
    var final = first.ToArray();
    final[channel] += "-mutated";
    Require(!StatisticReconciliationTrustedCurrentOwnerReader.OwnerReplayExact(
            first[0], final[0], first[1], final[1], first[2], final[2],
            first[3], final[3]),
        $"OWNER_REPLAY_CHANNEL_{channel}_MUST_FAIL");
    Require(StatisticReconciliationTrustedCurrentOwnerReader.OwnerReplayExact(
            first[0], first[0], first[1], first[1], first[2], first[2],
            first[3], first[3]),
        "EXACT_OWNER_REPLAY_REQUIRED");
}

static void LegacyFlowPlanRemainsValid()
{
    var plan = Plan(
        StatisticReconciliationActualCapturePlanVersions.V1,
        "FLOW_FINAL");
    StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
        plan, plan.PlanSha256);
}

static void NewNonFlowPlanIsValid()
{
    var plan = Plan(
        StatisticReconciliationActualCapturePlanVersions.V2,
        "DIRECT_CHILDREN_OR_SELF");
    StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
        plan, plan.PlanSha256);
    var flow = Plan(
        StatisticReconciliationActualCapturePlanVersions.V1,
        "FLOW_FINAL");
    Require(plan.PlanSha256 != flow.PlanSha256,
        "V1_AND_V2_DIGEST_DOMAINS_MUST_NOT_COLLIDE");
}

static void LegacyNonFlowPlanIsRejected()
{
    RequireInvalid(Plan(
        StatisticReconciliationActualCapturePlanVersions.V1,
        "DIRECT_CHILDREN"));
}

static void ProfileAndUnknownAreRejected()
{
    RequireInvalid(Plan(
        StatisticReconciliationActualCapturePlanVersions.V2,
        "FLOW_STATISTIC_PROFILE"));
    RequireInvalid(Plan(
        StatisticReconciliationActualCapturePlanVersions.V2,
        "UNKNOWN"));
}

static void RequireInvalid(StatisticReconciliationActualCapturePlan plan)
    => Throws(() =>
        StatisticReconciliationActualCapturePlanIntegrity.RequireValid(
            plan, plan.PlanSha256));

static StatisticReconciliationActualCapturePlan Plan(
    string schemaVersion,
    string mode)
{
    const string id1 = "000000000000000000000001";
    const string id2 = "000000000000000000000002";
    const string id3 = "000000000000000000000003";
    var sha = new string('a', 64);
    var plan = new StatisticReconciliationActualCapturePlan
    {
        SchemaVersion = schemaVersion,
        BoundaryRegistryVersion =
            StatisticReconciliationActualCapturePlanIntegrity
                .BoundaryRegistryVersion,
        ActualConfigurationBundleSha256 = sha,
        Basic = new()
        {
            SnapshotId = id1,
            Mode = mode,
            ImmutableSelectorSha256 = sha
        },
        Advanced = new()
        {
            SectionId = "section",
            DayNodeIds = [id1],
            MonthNodeIds = [id2],
            YearNodeIds = [id3],
            ImmutableSelectorSha256 = sha
        },
        Diff = new()
        {
            ResultId = id2,
            RunId = id3,
            ImmutableSelectorSha256 = sha
        },
        Api = new()
        {
            Surface = "BASIC_SOURCE",
            OwnerResultId = id1,
            ExpectedTotalRows = 0,
            PageSize =
                StatisticReconciliationActualCapturePlanIntegrity.ApiPageSize,
            PageCount = 1
        },
        Export = new()
        {
            ExportId = "export",
            ResultKind = "BASIC",
            WorkId = id1,
            ScopeType = "ASSIGNMENT",
            ScopeId = id2,
            ResultId = "result",
            RequestSha256 = sha,
            AuthorizationSnapshotSha256 = sha,
            ContentSha256 = sha,
            ColumnManifestSha256 = sha,
            OwnerSemanticSha256 = sha
        }
    };
    plan.PlanSha256 =
        StatisticReconciliationActualCapturePlanIntegrity.PlanSha(plan);
    return plan;
}

static void Throws(Action action)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }
    throw new InvalidOperationException("EXPECTED_REJECTION");
}

static StatisticReconciliationRun Run() => new()
{
    WorkId = "111111111111111111111111",
    ScopeAssignmentId = "222222222222222222222222",
    PeriodInstanceKey = "period-instance-1",
    PeriodKind = "SCHEDULED",
    DynamicFormFamilyId = "333333333333333333333333",
    DynamicFormVersionId = "444444444444444444444444",
    DynamicFormVersionNo = 7,
    DynamicFormSchemaHash = new string('a', 64),
    SourceReportId = "555555555555555555555555",
    P9CandidateChainId = "chain-1",
    P9CandidatePromptId = "P9-12",
    P9RunId = "666666666666666666666666"
};

static WorkReportStatisticRebuildJob Job(
    StatisticReconciliationRun run,
    string reportId,
    string runId) => new()
{
    Id = runId,
    WorkId = run.WorkId,
    WorkAssignmentId = run.ScopeAssignmentId,
    RunKind = WorkReportStatisticRebuildJobRunKinds.LifecycleDirectProjection,
    Status = WorkReportStatisticRebuildJobStatuses.Completed,
    IsCurrentPublication = true,
    IsActive = false,
    IsDeleted = false,
    PeriodInstanceKey = run.PeriodInstanceKey,
    PeriodKind = run.PeriodKind,
    DynamicFormFamilyId = run.DynamicFormFamilyId,
    DynamicFormTemplateId = run.DynamicFormVersionId,
    DynamicFormVersionNo = run.DynamicFormVersionNo,
    DynamicFormSchemaHash = run.DynamicFormSchemaHash,
    SourceReportId = reportId,
    SourcePayloadRevision = run.SourcePayloadRevision,
    SourceLifecycleRevision = run.SourceLifecycleRevision,
    CandidateChainId = run.P9CandidateChainId,
    CandidatePromptId = run.P9CandidatePromptId
};

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}