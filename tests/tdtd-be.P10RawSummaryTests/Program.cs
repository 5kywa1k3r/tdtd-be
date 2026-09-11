using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

static string Sha(string value) => Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static string CanonicalHash(string domain, params string[] values)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var value in new[] { domain }.Concat(values))
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(Encoding.ASCII.GetBytes(bytes.Length.ToString(
            System.Globalization.CultureInfo.InvariantCulture)));
        hash.AppendData([(byte)':']);
        hash.AppendData(bytes);
        hash.AppendData([(byte)'\n']);
    }
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}

static string OneBitText(string value)
{
    var characters = value.ToCharArray();
    characters[^1] = (char)(characters[^1] ^ 1);
    return new string(characters);
}

static string OneBitSha256(string value)
    => value[..^1] +
        (Convert.ToInt32(value[^1].ToString(), 16) ^ 1).ToString("x");

static StatisticReconciliationActualSummaryPlanBinding Plan(
    string valueType,
    string pointer,
    string[] operations,
    bool unordered = false,
    bool expandArray = false,
    string family = "BASIC",
    string? before = null,
    string? after = null,
    string? difference = null,
    string? transitionKind = null)
{
    operations = operations.OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
    var identity = new StatisticReconciliationExpectedMetricIdentityCompiler()
        .Compile(new ExpectedMetricIdentityRequest(
            family,
            "FIELD",
            "metric-main",
            "2026-08",
            "field-main",
            null,
            null,
            null,
            family == "BASIC" ? "DIRECT_CHILDREN_OR_SELF" : null,
            family == "BASIC" ? "assignment-root" : null,
            family == "ADVANCED" ? "DAY" : null,
            family == "DIFF" ? "FIELD" : null));
    var transitionMode = family == "DIFF"
        ? StatisticReconciliationExpectedDiffTransitionModes.BeforeAfterDifference
        : StatisticReconciliationExpectedDiffTransitionModes.None;
    var entrySha = StatisticReconciliationExpectedMetricPlanIntegrity
        .BuildEntrySha256(identity, pointer, valueType, unordered, expandArray,
            operations.ToImmutableArray(), transitionMode, before, after,
            difference, transitionKind);
    var valueFree = new StatisticReconciliationExpectedValueFreeMetricPlanDescriptor(
        StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2,
        identity,
        pointer,
        valueType,
        unordered,
        valueType == "STRING_LIST"
            ? unordered ? "UNORDERED" : "ORDERED"
            : null,
        expandArray,
        operations.ToImmutableArray(),
        transitionMode,
        before,
        after,
        difference,
        transitionKind,
        family == "DIFF"
            ? difference == "SUBTRACT"
                ? ["BEFORE", "AFTER", "CHANGE_STATE", "DELTA"]
                : ["BEFORE", "AFTER", "CHANGE_STATE"]
            : ["NONE"],
        entrySha);
    var descriptor = StatisticReconciliationActualSummaryIdentityDescriptor
        .Create(valueFree);
    var exact = new StatisticReconciliationExpectedGenerationBinding(
        "reconciliation-raw",
        Sha("generation"),
        Sha("generation-semantic"),
        Sha("metric-plan"),
        1,
        Sha("manifest"),
        8,
        Sha("membership"),
        "NON_FLOW");
    return StatisticReconciliationActualSummaryPlanBinding.Create(exact,
        [descriptor]);
}

static StatisticReconciliationActualRawPayloadEnvelope Source(
    string id,
    string payload)
    => new(
        Sha("stable-" + id),
        "work",
        "assignment-" + id,
        "report-" + id,
        "payload-" + id,
        1,
        Sha("payload-owner-" + id),
        Sha(payload),
        payload,
        Sha("envelope-" + id + payload));

static StatisticReconciliationActualRawSummaryAtom One(
    ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms,
    string kind,
    string leg = "NONE")
    => atoms.Single(value => value.AtomKind == kind &&
        value.TransitionLeg == leg);

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"{name}: expected={expected}; actual={actual}");
}

static void StableIdentityMismatch(Action action, string name)
{
    try
    {
        action();
        throw new InvalidOperationException($"{name}: expected rejection");
    }
    catch (StatisticReconciliationActualObservationException exception)
        when (exception.Reason ==
            "ACTUAL_RAW_SUMMARY_OWNER:RAW_STABLE_IDENTITY_MISMATCH")
    {
    }
}

var compiler = new StatisticReconciliationActualRawSummaryCompiler();
var numericPlan = Plan("NUMBER", "/fieldValues/values/amount",
    ["SUM", "MIN", "MAX", "MEAN"]);
var numeric = compiler.Compile(numericPlan,
[
    Source("1", "{\"fieldValues\":{\"values\":{\"amount\":10}}}"),
    Source("2", "{\"fieldValues\":{\"values\":{\"amount\":20}}}"),
    Source("3", "{\"fieldValues\":{\"values\":{\"amount\":null}}}"),
    Source("4", "{\"fieldValues\":{\"values\":{\"amount\":\"\"}}}"),
    Source("5", "{\"fieldValues\":{\"values\":{}}}")
]);
Equal("5", One(numeric, "REPORT_COUNT").CanonicalValue, "report-count");
Equal("2", One(numeric, "COUNT").CanonicalValue, "value-count");
Equal("2", One(numeric, "NUMERIC_VALUE_COUNT").CanonicalValue,
    "numeric-count");
Equal("1", One(numeric, "MISSING").CanonicalValue, "missing");
Equal("1", One(numeric, "NULL").CanonicalValue, "null");
Equal("1", One(numeric, "EMPTY").CanonicalValue, "empty");
Equal("30", One(numeric, "SUM").CanonicalValue, "sum");
Equal("10", One(numeric, "MIN").CanonicalValue, "min");
Equal("20", One(numeric, "MAX").CanonicalValue, "max");
Equal("15", One(numeric, "MEAN").CanonicalValue, "mean-denominator");
Console.WriteLine("PASS P10-RAW-01 numeric/states/mean");

var arrayPlan = Plan("NUMBER", "/fieldValues/values/rows", ["SUM"],
    expandArray: true);
var arrayAtoms = compiler.Compile(arrayPlan,
[
    Source("6", "{\"fieldValues\":{\"values\":{\"rows\":[1,2,3]}}}"),
    Source("7", "{\"fieldValues\":{\"values\":{\"rows\":[]}}}")
]);
Equal("3", One(arrayAtoms, "ROW_COUNT").CanonicalValue, "row-count");
Equal("1", One(arrayAtoms, "EMPTY").CanonicalValue, "empty-array");
Equal("6", One(arrayAtoms, "SUM").CanonicalValue, "array-sum");
Console.WriteLine("PASS P10-RAW-02 expanded-row-count");

var orderedPlan = Plan("STRING_LIST", "/fieldValues/values/tags",
    ["VALUES"]);
var unorderedPlan = Plan("STRING_LIST", "/fieldValues/values/tags",
    ["VALUES"], unordered: true);
var listPayload = Source("8",
    "{\"fieldValues\":{\"values\":{\"tags\":[\"b\",\"a\",\"b\"]}}}");
var ordered = One(compiler.Compile(orderedPlan, [listPayload]),
    "STRING_LIST");
var unordered = One(compiler.Compile(unorderedPlan, [listPayload]),
    "STRING_LIST");
Equal("[\"b\",\"a\",\"b\"]", ordered.CanonicalValue,
    "ordered-list");
Equal("ORDERED", ordered.CollectionSemantics, "ordered-semantics");
Equal("[\"a\",\"b\"]", unordered.CanonicalValue, "unordered-list");
Equal("UNORDERED", unordered.CollectionSemantics, "unordered-semantics");
Console.WriteLine("PASS P10-RAW-03 collection-semantics");

var diffPlan = Plan("NUMBER", "/fieldValues/values/current", ["SUM"],
    family: "DIFF",
    before: "/fieldValues/values/before",
    after: "/fieldValues/values/after",
    difference: "SUBTRACT",
    transitionKind: "CHANGED");
var diff = compiler.Compile(diffPlan,
[
    Source("9", "{\"fieldValues\":{\"values\":{\"before\":1,\"after\":4}}}"),
    Source("10", "{\"fieldValues\":{\"values\":{\"after\":2}}}"),
    Source("11", "{\"fieldValues\":{\"values\":{\"before\":7}}}")
]);
Equal("CHANGED", One(diff, "TRANSITION_KIND", "CHANGE_STATE").CanonicalValue,
    "transition-kind");
Equal("1", One(diff, "ADDED_COUNT", "CHANGE_STATE").CanonicalValue,
    "added");
Equal("1", One(diff, "REMOVED_COUNT", "CHANGE_STATE").CanonicalValue,
    "removed");
Equal("1", One(diff, "CHANGED_COUNT", "CHANGE_STATE").CanonicalValue,
    "changed");
Equal("3", One(diff, "DIFFERENCE", "DELTA").CanonicalValue,
    "difference");
Console.WriteLine("PASS P10-RAW-04 diff-transition");

var first = compiler.Compile(numericPlan,
    [Source("12", "{\"fieldValues\":{\"values\":{\"amount\":1}}}")]);
var second = compiler.Compile(numericPlan,
    [Source("12", "{\"fieldValues\":{\"values\":{\"amount\":2}}}")]);
if (first.Select(value => value.AtomSemanticSha256)
    .SequenceEqual(second.Select(value => value.AtomSemanticSha256),
        StringComparer.Ordinal))
    throw new InvalidOperationException("one-bit raw mutation was invisible");
Console.WriteLine("PASS P10-RAW-05 one-bit-mutation");

const string identityWork = "work-actual";
const string identityAssignment = "assignment-actual";
const string identityReport = "report-actual";
var actualStableIdentity = CanonicalHash(
    "P10_ACTUAL_SOURCE_STABLE_IDENTITY_V1",
    identityWork,
    identityAssignment,
    identityReport);
Equal(actualStableIdentity,
    StatisticReconciliationActualMongoRawSummaryOwner
        .RequireActualStableIdentitySha256(
            actualStableIdentity,
            identityWork,
            identityAssignment,
            identityReport),
    "actual-domain-stable-identity");
StableIdentityMismatch(() =>
    StatisticReconciliationActualMongoRawSummaryOwner
        .RequireActualStableIdentitySha256(
            actualStableIdentity,
            OneBitText(identityWork),
            identityAssignment,
            identityReport),
    "work-mutation");
StableIdentityMismatch(() =>
    StatisticReconciliationActualMongoRawSummaryOwner
        .RequireActualStableIdentitySha256(
            actualStableIdentity,
            identityWork,
            OneBitText(identityAssignment),
            identityReport),
    "assignment-mutation");
StableIdentityMismatch(() =>
    StatisticReconciliationActualMongoRawSummaryOwner
        .RequireActualStableIdentitySha256(
            actualStableIdentity,
            identityWork,
            identityAssignment,
            OneBitText(identityReport)),
    "report-mutation");
var storedMutation = OneBitSha256(actualStableIdentity);
StableIdentityMismatch(() =>
    StatisticReconciliationActualMongoRawSummaryOwner
        .RequireActualStableIdentitySha256(
            storedMutation,
            identityWork,
            identityAssignment,
            identityReport),
    "stored-mutation");
Console.WriteLine(
    "PASS P10-RAW-06 actual-domain-stable-identity/mutations");

var legacyCanonical = StatisticReconciliationActualMongoRawSummaryOwner
    .CanonicalPayload(
        values1DJson: null,
        fieldValuesJson: "{\"values\":{\"field-main\":10}}",
        tableValuesJson: null,
        summarySourceJson: null,
        projectDynamicFormDirectFields: false);
Equal(
    "{\"fieldValues\":{\"values\":{\"field-main\":10}}," +
    "\"summarySource\":null,\"tableValues\":null,\"values1D\":null}",
    legacyCanonical,
    "legacy-canonical-byte-stability");
var v4Canonical = StatisticReconciliationActualMongoRawSummaryOwner
    .CanonicalPayload(
        values1DJson: null,
        fieldValuesJson: "{\"values\":{\"field-main\":10}}",
        tableValuesJson: null,
        summarySourceJson: null,
        projectDynamicFormDirectFields: true);
Equal(
    "{\"directFieldValues\":{\"field-main\":10}," +
    "\"fieldValues\":{\"values\":{\"field-main\":10}}," +
    "\"summarySource\":null,\"tableValues\":null,\"values1D\":null}",
    v4Canonical,
    "v4-direct-field-projection");
var v4DirectPlan = Plan(
    "NUMBER",
    "/directFieldValues/field-main",
    ["SUM"],
    family: "DIRECT");
Equal(
    "10",
    One(
        compiler.Compile(v4DirectPlan, [Source("v4", v4Canonical)]),
        "SUM").CanonicalValue,
    "v4-direct-field-projected-value");
Console.WriteLine(
    "PASS P10-RAW-07 v4-direct-field-projection/legacy-byte-stability");

var genericDirectAtoms = new[]
{
    (Kind: "REPORT_COUNT", Value: "1"),
    (Kind: "ROW_COUNT", Value: "1"),
    (Kind: "COUNT", Value: "1"),
    (Kind: "NUMERIC_VALUE_COUNT", Value: "1"),
    (Kind: "SUM", Value: "10"),
    (Kind: "MIN", Value: "10"),
    (Kind: "MAX", Value: "10"),
    (Kind: "MEAN", Value: "10")
}.Select((value, ordinal) =>
    StatisticReconciliationActualTypedObservationCanonical
        .CreateSummaryCompatible(
            ordinal,
            StatisticReconciliationActualCoherentLayers.DirectProjection,
            "direct-owner",
            Sha("direct-owner-version"),
            "DIRECT",
            "FIELD",
            "metric-main",
            "2026-08",
            value.Kind,
            "NUMBER",
            "VALUE",
            value.Value,
            occurrenceCount: 1,
            reportCount: 1,
            rowCount: 1,
            numericValueCount: 1,
            fieldId: "field-main"))
    .ToImmutableArray();
var planBoundDirect = StatisticReconciliationActualAdapterTypedMapper
    .ProjectPlanBoundDirectFields(v4DirectPlan, genericDirectAtoms);
Equal(5, planBoundDirect.Length, "v4-plan-bound-operation-count");
Equal(
    0L,
    planBoundDirect.Single(value => value.AtomKind == "SUM").RowCount,
    "v4-plan-bound-scalar-row-metadata");
Equal(
    "0",
    planBoundDirect.Single(value => value.AtomKind == "ROW_COUNT")
        .CanonicalValue,
    "v4-plan-bound-row-count-value");
Equal(
    false,
    planBoundDirect.Any(value => value.AtomKind is "MIN" or "MAX" or "MEAN"),
    "v4-plan-bound-unconfigured-operations");
Equal(
    1L,
    genericDirectAtoms.Single(value => value.AtomKind == "SUM").RowCount,
    "v4-plan-bound-input-immutable");
Console.WriteLine(
    "PASS P10-RAW-08 v4-plan-bound-row-count/operation-projection");

Console.WriteLine(
    "P10_RAW_SUMMARY_OK cases=8 independentActual=true expectedValuesRead=0 " +
    "states=true rowCount=true meanDenominator=true collection=true diff=true " +
    "actualDomainStableIdentity=true identityMutations=4 " +
    "v4DirectFieldProjection=true legacyCanonicalStable=true " +
    "v4PlanBoundTypedProjection=true");
