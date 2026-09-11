using System.Collections.Immutable;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

const string ReconciliationId = "0123456789abcdef01234567";
const string Binding =
    "615bdb6f6ddee2c0acb6ab68089dc387a52604c705f2af977330e2362fa6db3f";

var cases = new (string Id, Func<Task> Run)[]
{
    ("P10-VERDICT-01", MatchedRequiresAllZeroFreshComplete),
    ("P10-VERDICT-02", WrongLedgerMismatchesThenRecovers),
    ("P10-VERDICT-03", FailedStatesNeverFalseGreen),
    ("P10-VERDICT-04", MissingExtraRequireControlledRemediation),
    ("P10-VERDICT-05", AppendOnlyReplayAndSupersessionAreImmutable)
};

var passed = 0;
foreach (var item in cases)
{
    try
    {
        await item.Run();
        Console.WriteLine($"PASS {item.Id}");
        passed++;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL {item.Id} {exception.GetType().Name}:{exception.Message}");
        return 1;
    }
}

Require(passed == 5, "EXACT_VERDICT_COUNT");
Console.WriteLine(
    "P10_T32_FINAL_VERDICT_OK cases=5 registeredCases=5 cumulative=28 " +
    "matched=true mismatched=true failed=true remediationRecheck=true " +
    "appendOnly=true next=P10-05");
return 0;

static Task MatchedRequiresAllZeroFreshComplete()
{
    var evaluator = new StatisticReconciliationFinalVerdictEvaluator();
    var matched = evaluator.Evaluate(Request(LayerKind.Zero, '8'));
    Equal(StatisticReconciliationFinalVerdicts.Matched, matched.Verdict,
        "ALL_ZERO_MATCHED");
    Require(matched.CompleteEvidence && matched.AllRequiredLayersZero &&
            matched.CloseoutAllowed && matched.Signable &&
            !matched.MissingOrExtraIdentity && !matched.UnknownBlocksCloseout,
        "MATCHED_GATES");

    var nonzero = evaluator.Evaluate(Request(LayerKind.Native, '9'));
    Equal(StatisticReconciliationFinalVerdicts.Mismatched, nonzero.Verdict,
        "NONZERO_NEVER_MATCHED");
    Require(!nonzero.CloseoutAllowed && !nonzero.Signable,
        "MISMATCH_BLOCKS_CLOSEOUT");
    Console.WriteLine(
        $"TRACE P10-VERDICT-01 matched={matched.DecisionSemanticSha256} " +
        $"nonzero={nonzero.DecisionSemanticSha256}");
    return Task.CompletedTask;
}

static async Task WrongLedgerMismatchesThenRecovers()
{
    var backend = new MemoryReviewBackend();
    var publisher = new StatisticReconciliationFinalVerdictPublisher(backend);
    var before = await publisher.PublishAsync(
        Request(LayerKind.Native, 'a'),
        Utc(1));
    Equal(StatisticReconciliationFinalVerdicts.Mismatched, before.Verdict,
        "WRONG_LEDGER_MISMATCHED");
    Require(before.RootCauseClass == StatisticReconciliationRootCauseClasses.Projection &&
            !before.CloseoutAllowed,
        "WRONG_LEDGER_ROOT_BOUND");

    var recovered = await publisher.PublishSupersedingRecheckAsync(
        ReconciliationId,
        before.VerdictGenerationId,
        Request(LayerKind.Zero, 'b'),
        null,
        Utc(2));
    Equal(StatisticReconciliationFinalVerdicts.Matched, recovered.Verdict,
        "CLEAN_RECHECK_MATCHED");
    Equal(before.VerdictGenerationId,
        recovered.SupersedesVerdictGenerationId!,
        "RECOVERY_SUPERSEDES_WRONG_LEDGER");
    Require(recovered.ActualGenerationId != before.ActualGenerationId &&
            recovered.CloseoutAllowed && recovered.Signable,
        "RECOVERY_NEW_GENERATION");
    var original = await backend.ReadAsync(
        ReconciliationId,
        before.VerdictGenerationId);
    Equal(StatisticReconciliationFinalVerdicts.Mismatched, original!.Verdict,
        "ORIGINAL_REMAINS_MISMATCHED");
    Require(original.SupersedesVerdictGenerationId is null && backend.ReviewWrites == 2,
        "APPEND_ONLY_RECOVERY");
    Console.WriteLine(
        $"TRACE P10-VERDICT-02 mismatch={before.VerdictGenerationSha256} " +
        $"recovery={recovered.VerdictGenerationSha256}");
}

static async Task FailedStatesNeverFalseGreen()
{
    var evaluator = new StatisticReconciliationFinalVerdictEvaluator();
    foreach (var request in new[]
             {
                 Request(LayerKind.Ambiguous, 'c'),
                 Request(LayerKind.Incomplete, 'd'),
                 StaleRequest('e'),
                 FailureRequest(
                     StatisticReconciliationFinalVerdictFailureKinds.Timeout,
                     'f')
             })
    {
        var failed = evaluator.Evaluate(request);
        Equal(StatisticReconciliationFinalVerdicts.Failed, failed.Verdict,
            "FAILED_NEVER_GREEN");
        Require(!failed.CloseoutAllowed && !failed.Signable,
            "FAILED_BLOCKS_CLOSEOUT");
    }

    var backend = new MemoryReviewBackend();
    var publisher = new StatisticReconciliationFinalVerdictPublisher(backend);
    var denied = DeniedRequest("malformed-hidden-binding");
    var deniedAlternate = evaluator.Evaluate(
        DeniedRequest("different-hidden-binding"));
    var deniedDecision = evaluator.Evaluate(denied);
    Equal(deniedDecision.DecisionSemanticSha256,
        deniedAlternate.DecisionSemanticSha256,
        "DENIED_RESULT_OPAQUE");
    await ExpectCodeAsync(
        () => publisher.PublishAsync(denied, Utc(3)),
        StatisticReconciliationFinalVerdictFailureCodes.PermissionDenied);
    Require(backend.ReviewWrites == 0 && backend.P5P9Writes == 0,
        "AUTH_BEFORE_EXISTENCE_ZERO_WRITE");
    Console.WriteLine(
        $"TRACE P10-VERDICT-03 denied={deniedDecision.DecisionSemanticSha256} " +
        "failed=true zeroWrite=true");
}

static async Task MissingExtraRequireControlledRemediation()
{
    var backend = new MemoryReviewBackend();
    var publisher = new StatisticReconciliationFinalVerdictPublisher(backend);
    var missing = await publisher.PublishAsync(
        Request(LayerKind.Missing, '1'),
        Utc(4));
    var extra = await publisher.PublishAsync(
        Request(LayerKind.Extra, '2'),
        Utc(5));
    Require(missing.RootCauseClass == StatisticReconciliationRootCauseClasses.MissingIdentity &&
            extra.RootCauseClass == StatisticReconciliationRootCauseClasses.ExtraIdentity &&
            missing.MissingOrExtraIdentity && extra.MissingOrExtraIdentity &&
            !missing.CloseoutAllowed && !extra.CloseoutAllowed,
        "IDENTITY_MISMATCH_BLOCKS");

    var missingRecheck = Request(LayerKind.Zero, '3');
    await ExpectCodeAsync(
        () => publisher.PublishSupersedingRecheckAsync(
            ReconciliationId,
            missing.VerdictGenerationId,
            missingRecheck,
            null,
            Utc(6)),
        StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid);

    var fixedMissing = await publisher.PublishSupersedingRecheckAsync(
        ReconciliationId,
        missing.VerdictGenerationId,
        missingRecheck,
        Remediation(
            StatisticReconciliationRemediationReferenceTypes.ImplementationPatch,
            "PATCH-REC-017",
            missingRecheck,
            '4'),
        Utc(6));
    Equal(StatisticReconciliationFinalVerdicts.Matched, fixedMissing.Verdict,
        "PATCH_RECHECK_MATCHED");
    Require(fixedMissing.Remediation is not null &&
            fixedMissing.Remediation.ReferenceType ==
                StatisticReconciliationRemediationReferenceTypes.ImplementationPatch &&
            fixedMissing.Remediation.BeforeSourceSha256 !=
                fixedMissing.Remediation.AfterSourceSha256 &&
            fixedMissing.Remediation.BeforeResultSha256 !=
                fixedMissing.Remediation.AfterResultSha256,
        "PATCH_EXACT_BEFORE_AFTER");

    var extraRecheck = Request(LayerKind.Zero, '5');
    var fixedExtra = await publisher.PublishSupersedingRecheckAsync(
        ReconciliationId,
        extra.VerdictGenerationId,
        extraRecheck,
        Remediation(
            StatisticReconciliationRemediationReferenceTypes.AuthorizedP9Operation,
            "P9:REBUILD:receipt-001",
            extraRecheck,
            '6'),
        Utc(7));
    Equal(StatisticReconciliationFinalVerdicts.Matched, fixedExtra.Verdict,
        "AUTHORIZED_P9_RECHECK_MATCHED");
    Require(fixedExtra.Remediation!.ReferenceType ==
            StatisticReconciliationRemediationReferenceTypes.AuthorizedP9Operation &&
            backend.P5P9Writes == 0 && backend.ReviewWrites == 4,
        "REFERENCE_ONLY_ZERO_DOMAIN_WRITE");

    var invalidRecheck = Request(LayerKind.Zero, '7');
    await ExpectCodeAsync(
        () => publisher.PublishSupersedingRecheckAsync(
            ReconciliationId,
            missing.VerdictGenerationId,
            invalidRecheck,
            Remediation("DIRECT_P5_PATCH", "forbidden", invalidRecheck, '8'),
            Utc(8)),
        StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid);
    Console.WriteLine(
        $"TRACE P10-VERDICT-04 missing={missing.VerdictGenerationSha256} " +
        $"patchRecheck={fixedMissing.VerdictGenerationSha256} " +
        $"extra={extra.VerdictGenerationSha256} " +
        $"p9Recheck={fixedExtra.VerdictGenerationSha256}");
}

static async Task AppendOnlyReplayAndSupersessionAreImmutable()
{
    var backend = new MemoryReviewBackend();
    var publisher = new StatisticReconciliationFinalVerdictPublisher(backend);
    var request = Request(LayerKind.Native, '9');
    var original = await publisher.PublishAsync(request, Utc(9));
    var replay = await publisher.PublishAsync(request, Utc(10));
    Equal(original.DocumentSemanticSha256, replay.DocumentSemanticSha256,
        "EXACT_REPLAY_STABLE");
    Equal(Utc(9), replay.CreatedAtUtc, "REPLAY_PRESERVES_ORIGINAL_BYTES");
    Require(backend.ReviewWrites == 1, "REPLAY_ZERO_SECOND_EFFECT");

    var successorRequest = Request(LayerKind.Zero, 'a');
    var successor = await publisher.PublishSupersedingRecheckAsync(
        ReconciliationId,
        original.VerdictGenerationId,
        successorRequest,
        null,
        Utc(11));
    var successorReplay = await publisher.PublishSupersedingRecheckAsync(
        ReconciliationId,
        original.VerdictGenerationId,
        successorRequest,
        null,
        Utc(12));
    Equal(successor.DocumentSemanticSha256,
        successorReplay.DocumentSemanticSha256,
        "SUPERSESSION_REPLAY_STABLE");

    await ExpectCodeAsync(
        () => publisher.PublishSupersedingRecheckAsync(
            ReconciliationId,
            original.VerdictGenerationId,
            Request(LayerKind.Zero, 'b'),
            null,
            Utc(13)),
        StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid);
    Require(backend.ReviewWrites == 2 && backend.P5P9Writes == 0,
        "ONE_SUCCESSOR_NO_OVERWRITE");

    var beforeHash = original.DocumentSemanticSha256;
    original.CloseoutAllowed = true;
    ExpectCode(
        () => StatisticReconciliationFinalVerdictPublisher.ValidateStored(original),
        StatisticReconciliationFinalVerdictFailureCodes.PersistenceInvalid);
    original.CloseoutAllowed = false;
    Equal(beforeHash, original.DocumentSemanticSha256,
        "TAMPER_DOES_NOT_REHASH_ORIGINAL");
    Console.WriteLine(
        $"TRACE P10-VERDICT-05 original={original.VerdictGenerationSha256} " +
        $"successor={successor.VerdictGenerationSha256} appendOnly=true");
}

static StatisticReconciliationFinalVerdictRequest Request(
    LayerKind kind,
    char actualGeneration)
{
    var classifier = new StatisticReconciliationRootCauseClassifier();
    var permission = classifier.CreatePermissionEvidence(
        StatisticReconciliationRootCausePermissionStates.Authorized,
        Sha('9'));
    var root = new StatisticReconciliationRootCauseRequest(
        Binding,
        permission,
        Fresh(),
        Layers(kind));
    return new StatisticReconciliationFinalVerdictRequest(
        ReconciliationId,
        Binding,
        Sha('6'),
        Sha('7'),
        Sha(actualGeneration),
        Sha(NextHex(actualGeneration)),
        Sha('5'),
        permission,
        root,
        StatisticReconciliationFinalVerdictFailureKinds.None,
        null);
}

static StatisticReconciliationFinalVerdictRequest StaleRequest(char actualGeneration)
{
    var request = Request(LayerKind.Zero, actualGeneration);
    var root = request.RootCauseRequest! with { Freshness = Stale(), Layers = [] };
    return request with { RootCauseRequest = root };
}

static StatisticReconciliationFinalVerdictRequest FailureRequest(
    string failureKind,
    char actualGeneration)
{
    var request = Request(LayerKind.Zero, actualGeneration);
    return request with
    {
        RootCauseRequest = null,
        FailureKind = failureKind,
        FailureEvidenceSha256 = Sha('4')
    };
}

static StatisticReconciliationFinalVerdictRequest DeniedRequest(string binding)
{
    var classifier = new StatisticReconciliationRootCauseClassifier();
    var permission = classifier.CreatePermissionEvidence(
        StatisticReconciliationRootCausePermissionStates.Denied,
        Sha('9'));
    return new StatisticReconciliationFinalVerdictRequest(
        "hidden-id",
        binding,
        "hidden",
        "hidden",
        "hidden",
        "hidden",
        "hidden",
        permission,
        null,
        StatisticReconciliationFinalVerdictFailureKinds.None,
        null);
}

static ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> Layers(
    LayerKind kind)
    => [.. Enumerable.Range(0, 8).Select(index => Layer(index,
        index == 1 ? kind : LayerKind.Zero))];

static StatisticReconciliationRootCauseLayerEvidence Layer(
    int ordinal,
    LayerKind kind)
{
    var classifier = new StatisticReconciliationRootCauseClassifier();
    if (kind == LayerKind.Missing || kind == LayerKind.Extra)
    {
        var code = kind == LayerKind.Missing
            ? StatisticReconciliationIdentityDeltaCodes.MissingIdentity
            : StatisticReconciliationIdentityDeltaCodes.ExtraIdentity;
        var identity = classifier.CreateIdentityEvidence(code, Sha('1'), Sha('2'));
        return classifier.CreateLayerEvidence(
            ordinal,
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            Binding,
            true,
            Sha('b'),
            Sha('c'),
            Sha('d'),
            1,
            1,
            kind == LayerKind.Missing ? 1 : 0,
            kind == LayerKind.Extra ? 1 : 0,
            0,
            StatisticReconciliationRootCauseLayerDeltaStates.Nonzero,
            StatisticReconciliationRootCauseAttributionStates.Proven,
            null,
            null,
            new[] { identity });
    }
    return kind switch
    {
        LayerKind.Zero => classifier.CreateLayerEvidence(
            ordinal, StatisticReconciliationRootCauseLayers.Ordered[ordinal], Binding,
            true, Sha('b'), Sha('c'), Sha('d'),
            1, 0, 0, 0, 0, "ZERO_DELTA", "NOT_REQUIRED", null, null, []),
        LayerKind.Native => classifier.CreateLayerEvidence(
            ordinal, StatisticReconciliationRootCauseLayers.Ordered[ordinal], Binding,
            true, Sha('b'), Sha('c'), Sha('d'),
            1, 1, 0, 0, 0, "NONZERO_DELTA", "PROVEN", Sha('e'), null, []),
        LayerKind.Ambiguous => classifier.CreateLayerEvidence(
            ordinal, StatisticReconciliationRootCauseLayers.Ordered[ordinal], Binding,
            true, Sha('b'), Sha('c'), Sha('d'),
            1, 1, 0, 0, 0, "NONZERO_DELTA", "AMBIGUOUS", null, null, []),
        LayerKind.Incomplete => classifier.CreateLayerEvidence(
            ordinal, StatisticReconciliationRootCauseLayers.Ordered[ordinal], Binding,
            false, Sha('b'), Sha('c'), Sha('d'),
            0, 0, 0, 0, 0, "UNDETERMINED", "AMBIGUOUS", null, null, []),
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

static StatisticReconciliationVerdictRemediationRequest Remediation(
    string type,
    string referenceId,
    StatisticReconciliationFinalVerdictRequest recheck,
    char seed)
    => new(
        type,
        referenceId,
        Sha(seed),
        Sha('1'),
        Sha('2'),
        Sha('3'),
        Sha('4'),
        recheck.ActualGenerationId,
        recheck.ActualGenerationSha256);

static DateTime Utc(int minute)
    => new(2026, 8, 12, 0, minute, 0, DateTimeKind.Utc);

static string Sha(char value) => new(value, 64);

static char NextHex(char value)
    => value switch
    {
        >= '0' and <= '8' => (char)(value + 1),
        '9' => 'a',
        >= 'a' and <= 'e' => (char)(value + 1),
        'f' => '0',
        _ => throw new InvalidOperationException("Hex seed required.")
    };

static async Task ExpectCodeAsync(
    Func<Task> action,
    string code)
{
    try
    {
        await action();
    }
    catch (StatisticReconciliationFinalVerdictException exception)
    {
        Equal(code, exception.Code, "EXPECTED_FAILURE_CODE");
        return;
    }
    throw new InvalidOperationException($"Expected failure {code}.");
}

static void ExpectCode(Action action, string code)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationFinalVerdictException exception)
    {
        Equal(code, exception.Code, "EXPECTED_FAILURE_CODE");
        return;
    }
    throw new InvalidOperationException($"Expected failure {code}.");
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
        throw new InvalidOperationException(
            $"{name}: expected={expected}; actual={actual}");
}

enum LayerKind
{
    Zero,
    Native,
    Ambiguous,
    Incomplete,
    Missing,
    Extra
}

sealed class MemoryReviewBackend : IStatisticReconciliationReviewBackend
{
    private readonly Dictionary<string, StatisticReconciliationReview> _values =
        new(StringComparer.Ordinal);

    public int ReviewWrites { get; private set; }
    public int P5P9Writes => 0;

    public Task<StatisticReconciliationReview?> ReadAsync(
        string reconciliationId,
        string verdictGenerationId,
        CancellationToken ct = default)
        => Task.FromResult(_values.TryGetValue(verdictGenerationId, out var value) &&
                           value.ReconciliationId == reconciliationId
            ? value
            : null);

    public Task<IReadOnlyList<StatisticReconciliationReview>> ReadLineageAsync(
        string reconciliationId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StatisticReconciliationReview>>(
            _values.Values
                .Where(value => value.ReconciliationId == reconciliationId)
                .OrderBy(value => value.CreatedAtUtc)
                .ThenBy(value => value.VerdictGenerationId, StringComparer.Ordinal)
                .ToArray());

    public Task<bool> TryAppendAsync(
        StatisticReconciliationReview review,
        CancellationToken ct = default)
    {
        if (_values.ContainsKey(review.VerdictGenerationId) ||
            review.SupersedesVerdictGenerationId is not null &&
            _values.Values.Any(value =>
                value.ReconciliationId == review.ReconciliationId &&
                value.SupersedesVerdictGenerationId ==
                    review.SupersedesVerdictGenerationId))
            return Task.FromResult(false);
        _values.Add(review.VerdictGenerationId, review);
        ReviewWrites++;
        return Task.FromResult(true);
    }
}
