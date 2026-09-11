using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

var cases = new (string Id, Action Run)[]
{
    ("P10-IDENTITY-01", MissingIdentityIsExplicit),
    ("P10-IDENTITY-02", ExtraIdentityIsExplicit),
    ("P10-IDENTITY-03", NullIsDistinct),
    ("P10-IDENTITY-04", EmptyIsDistinct),
    ("P10-IDENTITY-05", RedactedIsDistinctAndOpaque),
    ("P10-IDENTITY-06", ValueDeltaKeepsTypedIdentityAndOrder),
    ("P10-FRESHNESS-01", CompleteStablePinsAreFresh),
    ("P10-FRESHNESS-02", SourceConfigAndCatalogDriftAreStale),
    ("P10-FRESHNESS-03", RuntimeResultAndExportDriftAreStale),
    ("P10-FRESHNESS-04", IncompleteEvidenceIsStaleAndBlocked),
    ("P10-FRESHNESS-05", MixedRevisionAndBindingDriftAreStale)
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

Require(passed == 11, "EXACT_CASE_COUNT");
Console.WriteLine(
    "P10_T29_T30_STATE_FRESHNESS_OK cases=11 identity=6 freshness=5 " +
    "cumulative=23 stopBefore=P10-T31");
return 0;

static void MissingIdentityIsExplicit()
{
    var result = new StatisticReconciliationStatefulComparator().Compare(
        Value("NUMBER", "12", 0),
        null);
    Equal(StatisticReconciliationObservationValueStates.Missing,
        result.DeltaState, "MISSING_STATE");
    Equal(StatisticReconciliationIdentityDeltaCodes.MissingIdentity,
        result.DeltaCode, "MISSING_CODE");
    Nonzero(result, "MISSING_NONZERO");
}

static void ExtraIdentityIsExplicit()
{
    var result = new StatisticReconciliationStatefulComparator().Compare(
        null,
        Value("NUMBER", "12", 0));
    Equal(StatisticReconciliationObservationValueStates.Extra,
        result.DeltaState, "EXTRA_STATE");
    Equal(StatisticReconciliationIdentityDeltaCodes.ExtraIdentity,
        result.DeltaCode, "EXTRA_CODE");
    Nonzero(result, "EXTRA_NONZERO");
}

static void NullIsDistinct()
{
    var comparator = new StatisticReconciliationStatefulComparator();
    var matched = comparator.Compare(State("NUMBER", "NULL"), State("NUMBER", "NULL"));
    Zero(matched, "NULL_MATCH");
    Equal(StatisticReconciliationObservationValueStates.Null,
        matched.DeltaState, "NULL_STATE");
    var differs = comparator.Compare(State("NUMBER", "NULL"), State("NUMBER", "EMPTY"));
    Equal(StatisticReconciliationIdentityDeltaCodes.StateMismatch,
        differs.DeltaCode, "NULL_EMPTY_DIFFER");
    Nonzero(differs, "NULL_EMPTY_NONZERO");
}

static void EmptyIsDistinct()
{
    var comparator = new StatisticReconciliationStatefulComparator();
    Zero(comparator.Compare(State("TEXT", "EMPTY"), State("TEXT", "EMPTY")),
        "EMPTY_MATCH");
    Nonzero(comparator.Compare(State("TEXT", "EMPTY"), Value("TEXT", "x", 0)),
        "EMPTY_VALUE_DIFFER");
    ExpectCode(
        () => comparator.Compare(
            new StatisticReconciliationStatefulObservation(
                Identity(), "TEXT", "EMPTY", "", 0),
            State("TEXT", "EMPTY")),
        StatisticReconciliationStatefulDeltaFailureCodes.ObservationInvalid);
}

static void RedactedIsDistinctAndOpaque()
{
    var comparator = new StatisticReconciliationStatefulComparator();
    var matched = comparator.Compare(
        State("TEXT", "REDACTED"), State("TEXT", "REDACTED"));
    Zero(matched, "REDACTED_MATCH");
    Require(matched.ExpectedCanonicalValue is null &&
            matched.ActualCanonicalValue is null,
        "REDACTED_OPAQUE");
    Nonzero(comparator.Compare(
        State("TEXT", "REDACTED"), State("TEXT", "NULL")),
        "REDACTED_NULL_DIFFER");
    ExpectCode(
        () => comparator.Compare(
            new StatisticReconciliationStatefulObservation(
                Identity(), "TEXT", "REDACTED", "secret", 0),
            State("TEXT", "REDACTED")),
        StatisticReconciliationStatefulDeltaFailureCodes.ObservationInvalid);
}

static void ValueDeltaKeepsTypedIdentityAndOrder()
{
    var comparator = new StatisticReconciliationStatefulComparator();
    var numeric = comparator.Compare(
        Value("NUMBER", "10", 0), Value("NUMBER", "12", 0));
    Equal("2", numeric.NumericDeltaCanonicalValue!, "NUMERIC_DELTA");
    Equal(0, numeric.NumericDeltaScale!.Value, "NUMERIC_DELTA_SCALE");
    Equal(StatisticReconciliationIdentityDeltaCodes.ValueMismatch,
        numeric.DeltaCode, "VALUE_MISMATCH");
    Nonzero(numeric, "VALUE_NONZERO");

    var ordered = comparator.Compare(
        Value("STRING_LIST", "[\"a\",\"b\"]", 0, "ORDERED"),
        Value("STRING_LIST", "[\"b\",\"a\"]", 0, "ORDERED"));
    Nonzero(ordered, "ORDERED_VALUE_DELTA");
    Equal("ORDERED", ordered.ExpectedCollectionSemantics!,
        "EXPECTED_COLLECTION_SEMANTICS");
    Equal("ORDERED", ordered.ActualCollectionSemantics!,
        "ACTUAL_COLLECTION_SEMANTICS");
    var semanticChange = comparator.Compare(
        Value("STRING_LIST", "[\"a\",\"b\"]", 0, "ORDERED"),
        Value("STRING_LIST", "[\"a\",\"b\"]", 0, "UNORDERED"));
    Nonzero(semanticChange, "COLLECTION_SEMANTICS_DELTA");
    Require(semanticChange.ComparisonSha256 !=
            comparator.Compare(
                Value("STRING_LIST", "[\"a\",\"b\"]", 0, "ORDERED"),
                Value("STRING_LIST", "[\"a\",\"b\"]", 0, "ORDERED"))
                .ComparisonSha256,
        "COLLECTION_SEMANTICS_HASH_BOUND");
    ExpectCode(
        () => comparator.Compare(
            Value("TEXT", "x", 0),
            Value("TEXT", "x", 0, identity: Identity("other"))),
        StatisticReconciliationStatefulDeltaFailureCodes.IdentityPairInvalid);
}

static void CompleteStablePinsAreFresh()
{
    var pins = Pins();
    var result = Evaluate(pins.Binding, pins, pins, true, true, true);
    Equal(StatisticReconciliationFreshnessStates.Fresh, result.State, "FRESH");
    Equal(StatisticReconciliationFreshnessReasons.None, result.ReasonCode,
        "NO_DRIFT_REASON");
    Require(result.CompleteEvidence && result.MatchAllowed &&
            result.MismatchAsDataAllowed && result.Signable,
        "FRESH_ACTIONS_ALLOWED");
}

static void SourceConfigAndCatalogDriftAreStale()
{
    var pins = Pins();
    var source = Evaluate(
        pins.Binding with { SourceSetSha256 = Sha('9') },
        pins,
        pins,
        true, true, true);
    Stale(source, "SOURCE", "SOURCE_DRIFT");

    var configCurrent = pins with
    {
        Binding = pins.Binding with { ActualConfigurationBundleSha256 = Sha('8') }
    };
    Stale(Evaluate(pins.Binding, pins, configCurrent, true, true, true),
        "CONFIGURATION", "CONFIGURATION_DRIFT");

    var capturedConfigurationPins = pins with
    {
        ConfigurationPinSetSha256 = Sha('3')
    };
    var currentConfigurationPins = capturedConfigurationPins with
    {
        ConfigurationPinSetSha256 = Sha('2')
    };
    var stableConfigurationPins = Evaluate(
        pins.Binding,
        capturedConfigurationPins,
        capturedConfigurationPins,
        true, true, true);
    Equal(StatisticReconciliationFreshnessStates.Fresh,
        stableConfigurationPins.State, "CONFIGURATION_PIN_SET_STABLE");
    Stale(Evaluate(pins.Binding, capturedConfigurationPins,
            currentConfigurationPins, true, true, true),
        "CONFIGURATION", "CONFIGURATION_DRIFT");

    var catalogCurrent = pins with
    {
        Binding = pins.Binding with { CatalogPinSetSha256 = Sha('7') }
    };
    Stale(Evaluate(pins.Binding, pins, catalogCurrent, true, true, true),
        "CATALOG", "CATALOG_DRIFT");

    var multiple = configCurrent with
    {
        Binding = configCurrent.Binding with { SourceSetSha256 = Sha('6') }
    };
    Stale(Evaluate(pins.Binding, pins, multiple, true, true, true),
        "SOURCE", "SOURCE_DRIFT");
}

static void RuntimeResultAndExportDriftAreStale()
{
    var pins = Pins();
    var runtime = pins with
    {
        Binding = pins.Binding with { RuntimePinSetSha256 = Sha('6') }
    };
    Stale(Evaluate(pins.Binding, pins, runtime, true, true, true),
        "RUNTIME", "RUNTIME_DRIFT");
    Stale(Evaluate(pins.Binding, pins,
            pins with { GenerationSemanticSha256 = Sha('5') }, true, true, true),
        "RESULT", "RESULT_DRIFT");
    Stale(Evaluate(pins.Binding, pins,
            pins with { ExportOwnerSha256 = Sha('4') }, true, true, true),
        "EXPORT", "EXPORT_DRIFT");
}

static void IncompleteEvidenceIsStaleAndBlocked()
{
    var pins = Pins();
    var result = Evaluate(pins.Binding, pins, pins, false, true, true);
    Stale(result, "CAPTURE", "INCOMPLETE_CAPTURE");
    Require(!result.CompleteEvidence && !result.MatchAllowed &&
            !result.MismatchAsDataAllowed && !result.Signable,
        "INCOMPLETE_BLOCKS_ALL");
    var missingLayer = Evaluate(pins.Binding, pins, pins, true, false, true);
    Stale(missingLayer, "CAPTURE", "INCOMPLETE_CAPTURE");
}

static void MixedRevisionAndBindingDriftAreStale()
{
    var pins = Pins();
    var mixed = Evaluate(pins.Binding, pins,
        pins with { ResultOwnerSha256 = Sha('3') }, true, true, false);
    Stale(mixed, "CAPTURE", "MIXED_REVISION");
    Require(!mixed.MatchAllowed && !mixed.MismatchAsDataAllowed && !mixed.Signable,
        "MIXED_BLOCKS_ALL");

    var binding = Evaluate(
        pins.Binding with { RuntimePinSetSha256 = Sha('2') },
        pins,
        pins,
        true, true, true);
    Stale(binding, "RUNTIME", "RUNTIME_DRIFT");
    ExpectCode(
        () => Evaluate(
            pins.Binding with { SourceSetSha256 = Sha('A') },
            pins,
            pins,
            true, true, true),
        StatisticReconciliationFreshnessFailureCodes.PinsInvalid);
}

static StatisticReconciliationStatefulObservation Value(
    string type,
    string canonical,
    int scale,
    string? semantics = null,
    StatisticReconciliationStableIdentity? identity = null)
    => new(identity ?? Identity(), type, "VALUE", canonical, scale, semantics);

static StatisticReconciliationStatefulObservation State(string type, string state)
    => new(Identity(), type, state, null, 0);

static StatisticReconciliationStableIdentity Identity(string metric = "metric-01")
    => new(StatisticReconciliationStableIdentityKinds.Row,
        new[] { "form-v1", "row-01", metric });

static StatisticReconciliationActualFreshnessPins Pins()
    => new(
        new StatisticReconciliationComparisonBindingPins(
            Sha('1'), Sha('2'), Sha('3'), Sha('4'), Sha('5')),
        Sha('6'),
        Sha('7'),
        Sha('8'));

static StatisticReconciliationFreshnessAssessment Evaluate(
    StatisticReconciliationComparisonBindingPins expected,
    StatisticReconciliationActualFreshnessPins captured,
    StatisticReconciliationActualFreshnessPins current,
    bool generationComplete,
    bool layersComplete,
    bool coherent)
    => new StatisticReconciliationFreshnessEvaluator().Evaluate(
        new StatisticReconciliationFreshnessRequest(
            expected,
            captured,
            current,
            generationComplete,
            layersComplete,
            coherent));

static string Sha(char value) => new(value, 64);

static void Zero(StatisticReconciliationStatefulComparison value, string name)
{
    Require(value.Equal, name);
    Equal(StatisticReconciliationLayerDeltaVerdicts.ZeroDelta,
        value.LayerVerdict, $"{name}_VERDICT");
    Equal(StatisticReconciliationIdentityDeltaCodes.None,
        value.DeltaCode, $"{name}_CODE");
}

static void Nonzero(StatisticReconciliationStatefulComparison value, string name)
{
    Require(!value.Equal, name);
    Equal(StatisticReconciliationLayerDeltaVerdicts.NonzeroDelta,
        value.LayerVerdict, $"{name}_VERDICT");
}

static void Stale(
    StatisticReconciliationFreshnessAssessment value,
    string domain,
    string reason)
{
    Equal(StatisticReconciliationFreshnessStates.Stale, value.State,
        "STALE_STATE");
    Equal(domain, value.DriftDomain, "STALE_DOMAIN");
    Equal(reason, value.ReasonCode, "STALE_REASON");
    Require(!value.MatchAllowed && !value.MismatchAsDataAllowed && !value.Signable,
        "STALE_BLOCKS_ACTIONS");
}

static void ExpectCode(Action action, string expectedCode)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationTypedComparisonException exception)
    {
        Equal(expectedCode, exception.Code, "FAILURE_CODE");
        return;
    }
    throw new InvalidOperationException($"Expected failure code {expectedCode}.");
}

static void Require(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException(name);
}

static void Equal<T>(T expected, T actual, string name)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected={expected}; actual={actual}");
}
