using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

const string Binding =
    "615bdb6f6ddee2c0acb6ab68089dc387a52604c705f2af977330e2362fa6db3f";

var controls = new (string Id, Action Run)[]
{
    ("P10-T31-ROOT-01", ExactClassRegistryAndLayerMapping),
    ("P10-T31-ROOT-02", EarliestLayerRequiresOrderedZeroProof),
    ("P10-T31-ROOT-03", IdentityTieBreakIsMissingThenExtra),
    ("P10-T31-ROOT-04", AuthorizationAndFreshnessPrecedeData),
    ("P10-T31-ROOT-05", ZeroUnknownIncompleteAndRedactedFailClosed)
};

var passed = 0;
foreach (var control in controls)
{
    try
    {
        control.Run();
        Console.WriteLine($"PASS {control.Id}");
        passed++;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL {control.Id} {exception.GetType().Name}:{exception.Message}");
        return 1;
    }
}

Require(passed == 5, "EXACT_CONTROL_COUNT");
Console.WriteLine(
    "P10_T31_ROOT_CAUSE_OK controls=5 registeredCases=0 cumulative=23 " +
    "stopBefore=P10-T32");
return 0;

static void ExactClassRegistryAndLayerMapping()
{
    EqualSequence(new[]
    {
        "SOURCE_MEMBERSHIP", "PROJECTION", "AGGREGATE", "SNAPSHOT",
        "API_TOTAL", "EXPORT", "FRESHNESS", "PERMISSION",
        "MISSING_IDENTITY", "EXTRA_IDENTITY", "UNKNOWN"
    }, StatisticReconciliationRootCauseClasses.Ordered, "EXACT_CLASS_REGISTRY");

    var expected = new[]
    {
        "SOURCE_MEMBERSHIP", "PROJECTION", "AGGREGATE", "SNAPSHOT",
        "SNAPSHOT", "SNAPSHOT", "API_TOTAL", "EXPORT"
    };
    for (var index = 0; index < expected.Length; index++)
    {
        var result = Classify(Layers(index, LayerKind.ProvenNative));
        Equal(expected[index], result.PrimaryClass!, $"LAYER_CLASS_{index}");
        Equal(index, result.EarliestLayerOrdinal!.Value, $"LAYER_ORDINAL_{index}");
        Equal(StatisticReconciliationRootCauseClassificationStates.Proven,
            result.State, $"LAYER_PROVEN_{index}");
    }
}

static void EarliestLayerRequiresOrderedZeroProof()
{
    var layers = Layers(1, LayerKind.ProvenNative)
        .SetItem(4, Layer(4, LayerKind.ProvenNative));
    var result = Classify(layers);
    Equal("PROJECTION", result.PrimaryClass!, "EARLIEST_PROJECTION");
    Equal("DIRECT_PROJECTION", result.EarliestLayer!, "EARLIEST_LAYER");

    var reordered = layers.ToArray();
    (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
    ExpectEvidenceInvalid(() => Classify([.. reordered]));
    ExpectEvidenceInvalid(() => Classify([.. layers.RemoveAt(0)]));
}

static void IdentityTieBreakIsMissingThenExtra()
{
    var classifier = new StatisticReconciliationRootCauseClassifier();
    var missing = classifier.CreateIdentityEvidence(
        StatisticReconciliationIdentityDeltaCodes.MissingIdentity,
        Sha('1'), Sha('2'));
    var extra = classifier.CreateIdentityEvidence(
        StatisticReconciliationIdentityDeltaCodes.ExtraIdentity,
        Sha('3'), Sha('4'));
    var layer = classifier.CreateLayerEvidence(
        2, "AGGREGATE", Binding, true,
        Sha('5'), Sha('6'), Sha('7'),
        3, 3, 1, 1, 0,
        "NONZERO_DELTA", "PROVEN", Sha('8'), null,
        new[] { missing, extra });
    var result = Classify(Layers().SetItem(2, layer));
    Equal("MISSING_IDENTITY", result.PrimaryClass!, "MISSING_WINS");
    Require(result.MissingIdentityRemediationRequired &&
            !result.ExtraIdentityRemediationRequired,
        "MISSING_REMEDIATION_FLAG_ONLY");

    var extraOnly = classifier.CreateLayerEvidence(
        2, "AGGREGATE", Binding, true,
        Sha('5'), Sha('6'), Sha('7'),
        1, 1, 0, 1, 0,
        "NONZERO_DELTA", "PROVEN", Sha('8'), null,
        new[] { extra });
    var extraResult = Classify(Layers().SetItem(2, extraOnly));
    Equal("EXTRA_IDENTITY", extraResult.PrimaryClass!, "EXTRA_SELECTED");
    Require(extraResult.ExtraIdentityRemediationRequired,
        "EXTRA_REMEDIATION_FLAG");
    ExpectEvidenceInvalid(() => classifier.CreateLayerEvidence(
        2, "AGGREGATE", Binding, true,
        Sha('5'), Sha('6'), Sha('7'),
        3, 3, 1, 1, 0,
        "NONZERO_DELTA", "PROVEN", Sha('8'), null,
        new[] { extra, missing }));
    var duplicateIdentity = classifier.CreateIdentityEvidence(
        StatisticReconciliationIdentityDeltaCodes.MissingIdentity,
        Sha('1'), Sha('5'));
    ExpectEvidenceInvalid(() => classifier.CreateLayerEvidence(
        2, "AGGREGATE", Binding, true,
        Sha('5'), Sha('6'), Sha('7'),
        2, 2, 2, 0, 0,
        "NONZERO_DELTA", "PROVEN", null, null,
        new[] { missing, duplicateIdentity }));
}

static void AuthorizationAndFreshnessPrecedeData()
{
    var classifier = new StatisticReconciliationRootCauseClassifier();
    var permission = classifier.CreatePermissionEvidence("DENIED", Sha('9'));
    var denied = classifier.Classify(new StatisticReconciliationRootCauseRequest(
        "not-a-binding-sha", permission, null, []));
    var deniedAlternate = classifier.Classify(
        new StatisticReconciliationRootCauseRequest(
            "DIFFERENT-MALFORMED-BINDING", permission, null, []));
    Equal("PERMISSION", denied.PrimaryClass!, "PERMISSION_FIRST");
    Equal(denied.ClassificationSha256, deniedAlternate.ClassificationSha256,
        "PERMISSION_BINDING_OPAQUE");
    Require(denied.OrderedLayerEvidence.Length == 0 &&
            denied.FreshnessAssessmentSha256 is null &&
            denied.EarliestLayer is null,
        "PERMISSION_TRACE_OPAQUE");
    ExpectEvidenceInvalid(() => classifier.Classify(
        new StatisticReconciliationRootCauseRequest(
            Binding,
            classifier.CreatePermissionEvidence("DENIED", Sha('9')),
            Fresh(),
            Layers(0, LayerKind.ProvenNative))));
    ExpectEvidenceInvalid(() => classifier.Classify(
        new StatisticReconciliationRootCauseRequest(
            Sha('0'),
            classifier.CreatePermissionEvidence("AUTHORIZED", Sha('9')),
            Fresh(),
            Layers())));

    var stale = Stale();
    var freshness = classifier.Classify(new StatisticReconciliationRootCauseRequest(
        Binding,
        classifier.CreatePermissionEvidence("AUTHORIZED", Sha('9')),
        stale,
        []));
    Equal("FRESHNESS", freshness.PrimaryClass!, "FRESHNESS_SECOND");
    Equal(stale.AssessmentSha256, freshness.SupportingEvidenceSha256!,
        "FRESHNESS_ASSESSMENT_BOUND");
    Require(freshness.OrderedLayerEvidence.Length == 0,
        "STALE_DOES_NOT_INSPECT_DATA");

    var inconsistent = stale with
    {
        DriftDomain = StatisticReconciliationFreshnessDomains.Source,
        ReasonCode = StatisticReconciliationFreshnessReasons.ExportDrift,
        CompleteEvidence = true
    };
    inconsistent = inconsistent with
    {
        AssessmentSha256 = FreshnessAssessmentSha(inconsistent)
    };
    ExpectEvidenceInvalid(() => classifier.Classify(
        new StatisticReconciliationRootCauseRequest(
            Binding,
            classifier.CreatePermissionEvidence("AUTHORIZED", Sha('9')),
            inconsistent,
            [])));
}

static void ZeroUnknownIncompleteAndRedactedFailClosed()
{
    var zero = Classify(Layers());
    Equal(StatisticReconciliationRootCauseClassificationStates.NoDivergence,
        zero.State, "ZERO_NO_DIVERGENCE");
    Require(zero.PrimaryClass is null && !zero.CauseRequired &&
            !zero.UnknownBlocksCloseout,
        "ZERO_NOT_UNKNOWN");

    var unknown = Classify(Layers(3, LayerKind.Ambiguous));
    Equal("UNKNOWN", unknown.PrimaryClass!, "AMBIGUOUS_UNKNOWN");
    Equal(StatisticReconciliationRootCauseClassificationStates.Unknown,
        unknown.State, "UNKNOWN_STATE");
    Require(unknown.UnknownBlocksCloseout && !unknown.Provable,
        "UNKNOWN_BLOCKS");

    var incomplete = Classify(Layers()
        .SetItem(1, Layer(1, LayerKind.Incomplete))
        .SetItem(4, Layer(4, LayerKind.ProvenNative)));
    Equal(StatisticReconciliationRootCauseClassificationStates.InsufficientEvidence,
        incomplete.State, "INCOMPLETE_STATE");
    Equal(1, incomplete.EarliestLayerOrdinal!.Value,
        "INCOMPLETE_BLOCKS_LATER_LAYER");
    Require(incomplete.UnknownBlocksCloseout,
        "INCOMPLETE_BLOCKS_CLOSEOUT");

    ExpectEvidenceInvalid(() => Layer(0, LayerKind.Zero, redactedCount: 1));
    var classifier = new StatisticReconciliationRootCauseClassifier();
    var proof = classifier.CreatePreRedactionProof(
        0,
        StatisticReconciliationRootCauseLayers.SourceMembership,
        Binding,
        Sha('b'),
        Sha('c'),
        new[]
        {
            new StatisticReconciliationRootCausePreRedactionPair(
                ClearObservation("redacted-identity", "secret"),
                ClearObservation("redacted-identity", "secret"),
                true)
        });
    var trustedRedacted = classifier.CreateLayerEvidence(
        0,
        StatisticReconciliationRootCauseLayers.SourceMembership,
        Binding,
        true,
        Sha('b'),
        Sha('c'),
        proof.ComparisonManifestSha256,
        proof.ComparisonCount,
        proof.NonzeroCount,
        proof.MissingCount,
        proof.ExtraCount,
        proof.RedactedCount,
        "ZERO_DELTA",
        "NOT_REQUIRED",
        null,
        proof,
        []);
    var trusted = Classify(Layers().SetItem(0, trustedRedacted));
    Equal(StatisticReconciliationRootCauseClassificationStates.NoDivergence,
        trusted.State, "DERIVED_PRE_REDACTION_PROOF_ZERO");
    var wrongBindingProof = classifier.CreatePreRedactionProof(
        0,
        StatisticReconciliationRootCauseLayers.SourceMembership,
        Sha('f'),
        Sha('b'),
        Sha('c'),
        new[]
        {
            new StatisticReconciliationRootCausePreRedactionPair(
                ClearObservation("redacted-identity", "secret"),
                ClearObservation("redacted-identity", "secret"),
                true)
        });
    ExpectEvidenceInvalid(() => classifier.CreateLayerEvidence(
        0,
        StatisticReconciliationRootCauseLayers.SourceMembership,
        Binding,
        true,
        Sha('b'),
        Sha('c'),
        wrongBindingProof.ComparisonManifestSha256,
        1, 0, 0, 0, 1,
        "ZERO_DELTA", "NOT_REQUIRED", null, wrongBindingProof, []));
    ExpectEvidenceInvalid(() => classifier.CreatePreRedactionProof(
        0,
        StatisticReconciliationRootCauseLayers.SourceMembership,
        Binding,
        Sha('b'),
        Sha('c'),
        new[]
        {
            new StatisticReconciliationRootCausePreRedactionPair(null, null, true)
        }));
    var mismatchProof = classifier.CreatePreRedactionProof(
        0,
        StatisticReconciliationRootCauseLayers.SourceMembership,
        Binding,
        Sha('b'),
        Sha('c'),
        new[]
        {
            new StatisticReconciliationRootCausePreRedactionPair(
                ClearObservation("mismatch-redacted-identity", "secret-a"),
                ClearObservation("mismatch-redacted-identity", "secret-b"),
                true)
        });
    ExpectEvidenceInvalid(() => classifier.CreateLayerEvidence(
        0,
        StatisticReconciliationRootCauseLayers.SourceMembership,
        Binding,
        true,
        Sha('b'),
        Sha('c'),
        mismatchProof.ComparisonManifestSha256,
        1, 0, 0, 0, 1,
        "ZERO_DELTA", "NOT_REQUIRED", null, mismatchProof, []));

    var missingProof = classifier.CreatePreRedactionProof(
        0,
        StatisticReconciliationRootCauseLayers.SourceMembership,
        Binding,
        Sha('b'),
        Sha('c'),
        new[]
        {
            new StatisticReconciliationRootCausePreRedactionPair(
                ClearObservation("missing-redacted-identity", "secret"),
                null,
                true)
        });
    var missingLayer = classifier.CreateLayerEvidence(
        0,
        StatisticReconciliationRootCauseLayers.SourceMembership,
        Binding,
        true,
        Sha('b'),
        Sha('c'),
        missingProof.ComparisonManifestSha256,
        missingProof.ComparisonCount,
        missingProof.NonzeroCount,
        missingProof.MissingCount,
        missingProof.ExtraCount,
        missingProof.RedactedCount,
        "NONZERO_DELTA",
        "PROVEN",
        null,
        missingProof,
        missingProof.IdentityEvidence);
    Equal("MISSING_IDENTITY",
        Classify(Layers().SetItem(0, missingLayer)).PrimaryClass!,
        "PRE_REDACTION_IDENTITY_LINKED");
    var unrelatedIdentity = classifier.CreateIdentityEvidence(
        StatisticReconciliationIdentityDeltaCodes.MissingIdentity,
        Sha('a'),
        Sha('b'));
    ExpectEvidenceInvalid(() => classifier.CreateLayerEvidence(
        0,
        StatisticReconciliationRootCauseLayers.SourceMembership,
        Binding,
        true,
        Sha('b'),
        Sha('c'),
        missingProof.ComparisonManifestSha256,
        1, 1, 1, 0, 1,
        "NONZERO_DELTA", "PROVEN", null, missingProof,
        new[] { unrelatedIdentity }));
}

static StatisticReconciliationRootCauseClassification Classify(
    ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> layers)
{
    var classifier = new StatisticReconciliationRootCauseClassifier();
    return classifier.Classify(new StatisticReconciliationRootCauseRequest(
        Binding,
        classifier.CreatePermissionEvidence("AUTHORIZED", Sha('9')),
        Fresh(),
        layers));
}

static ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> Layers(
    int nonzeroOrdinal = -1,
    LayerKind kind = LayerKind.Zero)
    => [.. Enumerable.Range(0, 8)
        .Select(index => Layer(index,
            index == nonzeroOrdinal ? kind : LayerKind.Zero))];

static StatisticReconciliationRootCauseLayerEvidence Layer(
    int ordinal,
    LayerKind kind,
    int redactedCount = 0,
    StatisticReconciliationRootCausePreRedactionProof? preRedactionProof = null)
{
    var classifier = new StatisticReconciliationRootCauseClassifier();
    return kind switch
    {
        LayerKind.Zero => classifier.CreateLayerEvidence(
            ordinal, StatisticReconciliationRootCauseLayers.Ordered[ordinal], Binding,
            true, Sha('b'), Sha('c'), Sha('d'),
            redactedCount == 0 ? 1 : redactedCount,
            0, 0, 0, redactedCount,
            "ZERO_DELTA", "NOT_REQUIRED", null, preRedactionProof, []),
        LayerKind.ProvenNative => classifier.CreateLayerEvidence(
            ordinal, StatisticReconciliationRootCauseLayers.Ordered[ordinal], Binding,
            true, Sha('b'), Sha('c'), Sha('d'),
            1, 1, 0, 0, 0,
            "NONZERO_DELTA", "PROVEN", Sha('e'), null, []),
        LayerKind.Ambiguous => classifier.CreateLayerEvidence(
            ordinal, StatisticReconciliationRootCauseLayers.Ordered[ordinal], Binding,
            true, Sha('b'), Sha('c'), Sha('d'),
            1, 1, 0, 0, 0,
            "NONZERO_DELTA", "AMBIGUOUS", null, null, []),
        LayerKind.Incomplete => classifier.CreateLayerEvidence(
            ordinal, StatisticReconciliationRootCauseLayers.Ordered[ordinal], Binding,
            false, Sha('b'), Sha('c'), Sha('d'),
            0, 0, 0, 0, 0,
            "UNDETERMINED", "AMBIGUOUS", null, null, []),
        _ => throw new InvalidOperationException("Unsupported layer kind.")
    };
}

static StatisticReconciliationFreshnessAssessment Fresh()
{
    var pins = Pins();
    return new StatisticReconciliationFreshnessEvaluator().Evaluate(
        new StatisticReconciliationFreshnessRequest(
            pins.Binding, pins, pins, true, true, true));
}

static StatisticReconciliationFreshnessAssessment Stale()
{
    var pins = Pins();
    return new StatisticReconciliationFreshnessEvaluator().Evaluate(
        new StatisticReconciliationFreshnessRequest(
            pins.Binding,
            pins,
            pins with { ResultOwnerSha256 = Sha('f') },
            true,
            true,
            true));
}

static StatisticReconciliationActualFreshnessPins Pins()
    => new(
        new StatisticReconciliationComparisonBindingPins(
            Sha('1'), Sha('2'), Sha('3'), Sha('4'), Sha('5')),
        Sha('6'), Sha('7'), Sha('8'));

static StatisticReconciliationStatefulObservation ClearObservation(
    string component,
    string canonicalValue)
    => new(
        new StatisticReconciliationStableIdentity(
            StatisticReconciliationStableIdentityKinds.Table,
            new[] { component }),
        StatisticReconciliationTypedValueTypes.Text,
        StatisticReconciliationObservationValueStates.Value,
        canonicalValue);

static string FreshnessAssessmentSha(
    StatisticReconciliationFreshnessAssessment value)
    => HashFields(
        "P10_FRESHNESS_ASSESSMENT_V1",
        value.State,
        value.DriftDomain,
        value.ReasonCode,
        value.CompleteEvidence ? "1" : "0",
        value.ExpectedBindingSha256,
        value.CapturedPinsSha256,
        value.CurrentPinsSha256);

static string HashFields(string domain, params string[] fields)
{
    var builder = new StringBuilder();
    foreach (var field in new[] { domain }.Concat(fields))
    {
        builder.Append(Encoding.UTF8.GetByteCount(field)
                .ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(field);
    }
    return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
        .ToLowerInvariant();
}

static string Sha(char value) => new(value, 64);

static void ExpectEvidenceInvalid(Action action)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationTypedComparisonException exception)
    {
        Equal(StatisticReconciliationRootCauseFailureCodes.EvidenceInvalid,
            exception.Code, "EVIDENCE_FAILURE_CODE");
        return;
    }
    throw new InvalidOperationException("Expected root-cause evidence failure.");
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

static void EqualSequence<T>(
    IReadOnlyList<T> expected,
    IReadOnlyList<T> actual,
    string name)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException(
            $"{name}: expected={string.Join(',', expected)}; " +
            $"actual={string.Join(',', actual)}");
}

enum LayerKind
{
    Zero,
    ProvenNative,
    Ambiguous,
    Incomplete
}
