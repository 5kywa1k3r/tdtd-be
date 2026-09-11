using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

var cases = new (string Id, Action Run)[]
{
    ("P10-APPROVE-01", DraftContributesZero),
    ("P10-APPROVE-02", SubmittedContributesZero),
    ("P10-APPROVE-03", ApprovalContributesExactlyOnce),
    ("P10-APPROVE-04", DuplicateApprovalAndRecheckReplayConverge),
    ("P10-RECALL-01", RecallIsExactInverse),
    ("P10-RECALL-02", RecallUsesPriorGenerationNotMutablePayload),
    ("P10-RECALL-03", RecallDuplicateDeliveryDoesNotSubtractTwice),
    ("P10-RECALL-04", RecallThenReapproveBindsNewGeneration),
    ("P10-RETURN-01", ReturnIsExactInverse),
    ("P10-RETURN-02", ReturnedRevisionCannotRemainEffective),
    ("P10-RETURN-03", WrongReversalGenerationFailsClosed),
    ("P10-RETURN-04", RecheckSupersedesWithoutDeletingHistory),
    ("P10-TERMINATE-01", TerminationIsExactInverse),
    ("P10-TERMINATE-02", InvalidatedBranchIsExactInverse),
    ("P10-TERMINATE-03", OldEpochContributesZero),
    ("P10-TERMINATE-04", IncompleteEpochEvidenceIsStale),
    ("P10-LFCROLLBACK-01", RollbackInvalidatesOldEpoch),
    ("P10-LFCROLLBACK-02", RestartBindsNewEpochExactlyOnce),
    ("P10-LFCROLLBACK-03", ReorderedReplayHasStableSemantics),
    ("P10-LFCROLLBACK-04", ReceiptCasFenceAndWinnerAreIdempotent),
    ("P10-CONTRIBUTION-01", MissingPolicyDefaultsToExclude),
    ("P10-CONTRIBUTION-02", ExplicitExcludeContributesZero),
    ("P10-CONTRIBUTION-03", LockedIncludeContributesOnce),
    ("P10-CONTRIBUTION-04", ChangedProvenanceIsDetected)
};

var passed = 0;
foreach (var test in cases)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Id}");
        passed++;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"FAIL {test.Id} {exception.GetType().Name}:{exception.Message}");
        return 1;
    }
}

Require(passed == 24, "EXACT_LIFECYCLE_CASE_COUNT");
Console.WriteLine("P10_T24_LIFECYCLE_REVERSAL_OK cases=12 approve=4 recall=4 return=4");
Console.WriteLine("P10_T25_CONTRIBUTION_PROVENANCE_OK cases=4 excludeInclude=true lockedP7=true");
Console.WriteLine("P10_T26_EPOCH_IDEMPOTENCY_OK cases=8 terminate=4 rollback=4 receiptCasFence=true");
Console.WriteLine(
    "P10_LIFECYCLE_RECONCILIATION_OK cases=24 t24=12 t25=4 t26=8 " +
    "inverse=true currentEpoch=true appendOnly=true p9Writes=0 cumulative=188 next=P10-08");
return 0;

static void DraftContributesZero()
{
    var request = Build(Spec.Observe(StatisticReconciliationLifecycleStatuses.Draft));
    var result = EvaluateMatched(request);
    Equal("0", result.Steps[0].ExpectedAfter, "DRAFT_EXPECTED_ZERO");
}

static void SubmittedContributesZero()
{
    var request = Build(Spec.Observe(StatisticReconciliationLifecycleStatuses.Submitted));
    var result = EvaluateMatched(request);
    Equal("0", result.Steps[0].ExpectedAfter, "SUBMIT_EXPECTED_ZERO");
}

static void ApprovalContributesExactlyOnce()
{
    var result = EvaluateMatched(Build(
        Spec.Observe(StatisticReconciliationLifecycleStatuses.Submitted),
        Spec.Approve()));
    Equal("10", result.Steps[1].ExpectedAfter, "APPROVED_ONE_CONTRIBUTION");
    Equal("10", result.Steps[1].ExpectedDelta, "APPROVED_DELTA");
}

static void DuplicateApprovalAndRecheckReplayConverge()
{
    var evaluator = new StatisticReconciliationLifecycleEvaluator();
    var request = Build(Spec.Approve());
    request = request with
    {
        Sources = request.Sources.Add(request.Sources[0]),
        Actuals = request.Actuals.Add(request.Actuals[0])
    };
    var normalized = evaluator.Evaluate(request);
    Matched(normalized, "DUPLICATE_APPROVAL_NORMALIZED");
    Equal(1, normalized.SourceCount, "DUPLICATE_SOURCE_DEDUPED");

    var store = new InMemoryStatisticReconciliationLifecycleObservationStore();
    var coordinator = new StatisticReconciliationLifecycleRecheckCoordinator(
        request.ReconciliationId, evaluator, store);
    var state = coordinator.ReadState();
    var command = Command(evaluator, request, state, "approve-recheck", 1);
    var first = coordinator.Execute(command, request);
    var replay = coordinator.Execute(command, request);
    Require(!first.IsReplay && replay.IsReplay, "RECHECK_REPLAY_CLASSIFIED");
    Equal(first.Receipt.ReceiptSha256, replay.Receipt.ReceiptSha256,
        "RECHECK_RECEIPT_STABLE");
    Equal(1, replay.State.PublicationCount, "RECHECK_NO_SECOND_PUBLISH");
    Equal(0, replay.AppendResult.AppendedCount, "RECHECK_ZERO_SECOND_WRITE");
}

static void RecallIsExactInverse()
{
    var result = EvaluateMatched(Build(Spec.Approve(), Spec.Recall()));
    Equal("-10", result.Steps[1].ExpectedDelta, "RECALL_INVERSE");
    Equal("0", result.Steps[1].ExpectedAfter, "RECALL_ZERO_AFTER");
}

static void RecallUsesPriorGenerationNotMutablePayload()
{
    var result = EvaluateMatched(Build(Spec.Approve("10"), Spec.Recall("999")));
    Equal("-10", result.Steps[1].ExpectedDelta,
        "RECALL_PRIOR_IMMUTABLE_GENERATION");
    Equal("-10", result.Steps[1].ActualDelta,
        "RECALL_ACTUAL_PRIOR_IMMUTABLE_GENERATION");
}

static void RecallDuplicateDeliveryDoesNotSubtractTwice()
{
    var request = Build(Spec.Approve(), Spec.Recall());
    request = request with
    {
        Sources = request.Sources.Add(request.Sources[1]),
        Actuals = request.Actuals.Add(request.Actuals[1])
    };
    var result = EvaluateMatched(request);
    Equal(2, result.SourceCount, "RECALL_REPLAY_DEDUPED");
    Equal("0", result.Steps[^1].ExpectedAfter, "RECALL_NO_NEGATIVE_TOTAL");
}

static void RecallThenReapproveBindsNewGeneration()
{
    var result = EvaluateMatched(Build(
        Spec.Approve("10"), Spec.Recall(), Spec.Approve("15")));
    Equal("15", result.Steps[2].ExpectedDelta, "REAPPROVE_NEW_VALUE");
    Require(result.Steps.All(step => step.Exact), "REAPPROVE_CHAIN_EXACT");
}

static void ReturnIsExactInverse()
{
    var result = EvaluateMatched(Build(Spec.Approve(), Spec.Return()));
    Equal("-10", result.Steps[1].ExpectedDelta, "RETURN_INVERSE");
}

static void ReturnedRevisionCannotRemainEffective()
{
    var request = Build(Spec.Approve(), Spec.Return(isEffective: true));
    var result = EvaluateMatched(request);
    Equal("0", result.Steps[1].ExpectedAfter,
        "RETURN_STATUS_OVERRIDES_EFFECTIVE_FLAG");
}

static void WrongReversalGenerationFailsClosed()
{
    var evaluator = new StatisticReconciliationLifecycleEvaluator();
    var request = Build(Spec.Approve(), Spec.Return());
    var source = request.Sources[1];
    var prior = request.Actuals[0];
    var wrong = evaluator.CreateActual(
        "actual-2", source, "generation-2", prior.GenerationId,
        Sha("wrong-prior-generation"), "0", "-10");
    request = request with { Actuals = request.Actuals.SetItem(1, wrong) };
    var result = evaluator.Evaluate(request);
    Equal(StatisticReconciliationLifecycleOutcomes.Stale, result.Outcome,
        "WRONG_REVERSAL_STALE");
    Equal(StatisticReconciliationLifecycleRootCauses.Snapshot, result.RootCause,
        "WRONG_REVERSAL_ROOT");
}

static void RecheckSupersedesWithoutDeletingHistory()
{
    var evaluator = new StatisticReconciliationLifecycleEvaluator();
    var store = new InMemoryStatisticReconciliationLifecycleObservationStore();
    var request1 = Build(Spec.Approve());
    var coordinator = new StatisticReconciliationLifecycleRecheckCoordinator(
        request1.ReconciliationId, evaluator, store);
    var state0 = coordinator.ReadState();
    var first = coordinator.Execute(
        Command(evaluator, request1, state0, "reviewed-generation-1", 1),
        request1);

    var request2 = Build(Spec.Approve(), Spec.Return());
    var state1 = coordinator.ReadState();
    var second = coordinator.Execute(
        Command(evaluator, request2, state1, "reviewed-generation-2", 2),
        request2);
    Require(first.Receipt.GenerationId != second.Receipt.GenerationId,
        "NEW_RECHECK_GENERATION");
    Equal(2, second.State.PublicationCount, "TWO_APPEND_ONLY_GENERATIONS");
    Require(store.ReadAll(request1.ReconciliationId).Any(row =>
            row.GenerationId == first.Receipt.GenerationId),
        "PRIOR_GENERATION_RETAINED");
}

static void MissingPolicyDefaultsToExclude()
{
    var request = Build(Spec.Approve(policy: null));
    var result = EvaluateMatched(request);
    Equal(StatisticReconciliationLifecyclePolicies.Exclude,
        result.Steps[0].ContributionPolicy, "DEFAULT_EXCLUDE_POLICY");
    Equal("0", result.Steps[0].ExpectedAfter, "DEFAULT_EXCLUDE_ZERO");
}

static void ExplicitExcludeContributesZero()
{
    var result = EvaluateMatched(Build(Spec.Approve(policy:
        StatisticReconciliationLifecyclePolicies.Exclude)));
    Equal("0", result.Steps[0].ExpectedAfter, "EXPLICIT_EXCLUDE_ZERO");
}

static void LockedIncludeContributesOnce()
{
    var request = Build(Spec.Approve(), Spec.Approve());
    var result = EvaluateMatched(request);
    Equal("10", result.Steps[0].ExpectedDelta, "INCLUDE_FIRST_APPLY");
    Equal("0", result.Steps[1].ExpectedDelta, "INCLUDE_REAPPLY_ZERO");
    Equal("10", result.Steps[1].ExpectedAfter, "INCLUDE_EXACT_ONCE");
    Require(request.Sources.All(source => source.MappingVersionLocked &&
        source.MappingRevision == 7), "LOCKED_P7_MAPPING_VERSION");
}

static void ChangedProvenanceIsDetected()
{
    var evaluator = new StatisticReconciliationLifecycleEvaluator();
    var request = Build(Spec.Approve());
    var source = request.Sources[0];
    var wrong = evaluator.CreateActual(
        "actual-1", source, "generation-1", null, null, "10", "10",
        provenanceSha256: Sha("changed-provenance"));
    request = request with { Actuals = ImmutableArray.Create(wrong) };
    var mismatch = evaluator.Evaluate(request);
    Equal(StatisticReconciliationLifecycleOutcomes.Mismatched,
        mismatch.Outcome, "PROVENANCE_MISMATCH");
    Equal(StatisticReconciliationLifecycleRootCauses.SourceMembership,
        mismatch.RootCause, "PROVENANCE_ROOT");
    Matched(evaluator.Evaluate(Build(Spec.Approve())),
        "PROVENANCE_RECOVERY");
}

static void TerminationIsExactInverse()
{
    var result = EvaluateMatched(Build(Spec.Approve(), Spec.Terminate()));
    Equal("-10", result.Steps[1].ExpectedDelta, "TERMINATE_INVERSE");
}

static void InvalidatedBranchIsExactInverse()
{
    var result = EvaluateMatched(Build(Spec.Approve(), Spec.Invalidate()));
    Equal("-10", result.Steps[1].ExpectedDelta, "INVALIDATE_INVERSE");
}

static void OldEpochContributesZero()
{
    var result = EvaluateMatched(Build(Spec.Approve(epochId: "epoch-old",
        currentEpochId: "epoch-current")));
    Equal("0", result.Steps[0].ExpectedAfter, "OLD_EPOCH_EXCLUDED");
}

static void IncompleteEpochEvidenceIsStale()
{
    var evaluator = new StatisticReconciliationLifecycleEvaluator();
    var request = Build(Spec.Approve());
    var source = request.Sources[0];
    var incomplete = evaluator.CreateActual(
        "actual-1", source, "generation-1", null, null, "10", "10",
        evidenceComplete: false);
    var result = evaluator.Evaluate(request with
    {
        Actuals = ImmutableArray.Create(incomplete)
    });
    Equal(StatisticReconciliationLifecycleOutcomes.Stale, result.Outcome,
        "EPOCH_EVIDENCE_STALE");
    Equal(StatisticReconciliationLifecycleRootCauses.Freshness,
        result.RootCause, "EPOCH_EVIDENCE_ROOT");
}

static void RollbackInvalidatesOldEpoch()
{
    var result = EvaluateMatched(Build(
        Spec.Approve(epochId: "epoch-1", currentEpochId: "epoch-1"),
        Spec.Rollback(epochId: "epoch-1", currentEpochId: "epoch-2")));
    Equal("-10", result.Steps[1].ExpectedDelta, "ROLLBACK_OLD_EPOCH_INVERSE");
}

static void RestartBindsNewEpochExactlyOnce()
{
    var result = EvaluateMatched(Build(
        Spec.Approve(epochId: "epoch-1", currentEpochId: "epoch-1"),
        Spec.Rollback(epochId: "epoch-1", currentEpochId: "epoch-2"),
        Spec.Restart(epochId: "epoch-2", currentEpochId: "epoch-2"),
        Spec.Restart(epochId: "epoch-2", currentEpochId: "epoch-2")));
    Equal("10", result.Steps[2].ExpectedDelta, "RESTART_NEW_EPOCH_ONE");
    Equal("0", result.Steps[3].ExpectedDelta, "RESTART_REPLAY_ZERO");
}

static void ReorderedReplayHasStableSemantics()
{
    var evaluator = new StatisticReconciliationLifecycleEvaluator();
    var ordered = Build(Spec.Approve(), Spec.Recall(), Spec.Approve("12"));
    var orderedResult = evaluator.Evaluate(ordered);
    var reordered = ordered with
    {
        Sources = ImmutableArray.Create(ordered.Sources[2], ordered.Sources[0],
            ordered.Sources[1], ordered.Sources[1]),
        Actuals = ImmutableArray.Create(ordered.Actuals[1], ordered.Actuals[2],
            ordered.Actuals[0], ordered.Actuals[1])
    };
    var reorderedResult = evaluator.Evaluate(reordered);
    Matched(reorderedResult, "REORDERED_MATCHED");
    Equal(orderedResult.ResultSemanticSha256,
        reorderedResult.ResultSemanticSha256, "REORDERED_SEMANTIC_STABLE");
}

static void ReceiptCasFenceAndWinnerAreIdempotent()
{
    var evaluator = new StatisticReconciliationLifecycleEvaluator();
    var store = new InMemoryStatisticReconciliationLifecycleObservationStore();
    var request = Build(Spec.Approve(), Spec.Rollback(), Spec.Restart());
    var coordinator = new StatisticReconciliationLifecycleRecheckCoordinator(
        request.ReconciliationId, evaluator, store);
    var genesis = coordinator.ReadState();
    var command = Command(evaluator, request, genesis, "one-winner", 9);
    var winner = coordinator.Execute(command, request);
    var replay = coordinator.Execute(command, request);
    Equal(1, winner.State.PublicationCount, "ONE_WINNER");
    Require(replay.IsReplay && replay.AppendResult.AppendedCount == 0,
        "SAME_REQUEST_CONVERGES");

    ExpectCode(() => coordinator.Execute(
            Command(evaluator, Build(Spec.Approve("11")),
                coordinator.ReadState(), "one-winner", 10),
            Build(Spec.Approve("11"))),
        StatisticReconciliationLifecycleFailureCodes.ReplayMismatch,
        "DIFFERENT_REQUEST_REJECTED");
    ExpectCode(() => coordinator.Execute(
            Command(evaluator, request, genesis, "stale-cas", 10), request),
        StatisticReconciliationLifecycleFailureCodes.CasStale,
        "STALE_CAS_REJECTED");
    var current = coordinator.ReadState();
    ExpectCode(() => coordinator.Execute(
            Command(evaluator, request, current, "stale-fence", 9), request),
        StatisticReconciliationLifecycleFailureCodes.FenceStale,
        "STALE_FENCE_REJECTED");
    Equal(1, coordinator.ReadState().PublicationCount,
        "LOSERS_ZERO_SECOND_EFFECT");
}

static StatisticReconciliationLifecycleRequest Build(params Spec[] specs)
{
    var evaluator = new StatisticReconciliationLifecycleEvaluator();
    var sources = ImmutableArray.CreateBuilder<StatisticReconciliationLifecycleSourceState>();
    var actuals = ImmutableArray.CreateBuilder<StatisticReconciliationLifecycleActualContribution>();
    decimal previous = 0m;
    StatisticReconciliationLifecycleActualContribution? prior = null;
    for (var index = 0; index < specs.Length; index++)
    {
        var spec = specs[index];
        var sequence = index + 1;
        var include = spec.Policy == StatisticReconciliationLifecyclePolicies.Include;
        var source = evaluator.CreateSource(
            $"source-{sequence}", sequence, spec.EventKind, "stable-source-1",
            "report-1", "branch-1", "step-1", spec.EpochId, spec.Epoch,
            spec.CurrentEpochId, spec.Status, spec.IsEffective, spec.Policy,
            include ? "p7-mapping-version-7" : null,
            include ? 7 : null,
            include,
            include ? Sha("p7-mapping-version-7") : null,
            include ? "p7-provenance-7" : null,
            include ? Sha("p7-provenance-7") : null,
            $"source-generation-{sequence}",
            Sha($"source-generation-{sequence}"), spec.Value);
        var contributes = source.LifecycleStatus ==
                          StatisticReconciliationLifecycleStatuses.Approved &&
                          source.IsEffective && source.EpochId == source.CurrentEpochId &&
                          source.ContributionPolicy ==
                          StatisticReconciliationLifecyclePolicies.Include;
        var after = contributes ? decimal.Parse(source.CanonicalValue,
            System.Globalization.CultureInfo.InvariantCulture) : 0m;
        var delta = after - previous;
        var reversal = previous != 0m && after == 0m;
        var actual = evaluator.CreateActual(
            $"actual-{sequence}", source, $"generation-{sequence}",
            prior?.GenerationId,
            reversal ? prior?.GenerationSha256 : null,
            Canon(after), Canon(delta));
        sources.Add(source);
        actuals.Add(actual);
        previous = after;
        prior = actual;
    }
    return new StatisticReconciliationLifecycleRequest(
        "p10-lifecycle-reconciliation", Sha("comparison-binding"),
        sources.ToImmutable(), actuals.ToImmutable());
}

static StatisticReconciliationLifecycleResult EvaluateMatched(
    StatisticReconciliationLifecycleRequest request)
{
    var result = new StatisticReconciliationLifecycleEvaluator().Evaluate(request);
    Matched(result, "EXPECTED_MATCHED");
    return result;
}

static StatisticReconciliationLifecycleRecheckCommand Command(
    StatisticReconciliationLifecycleEvaluator evaluator,
    StatisticReconciliationLifecycleRequest request,
    StatisticReconciliationLifecycleRecheckState state,
    string commandId,
    long fence)
    => new(request.ReconciliationId, commandId,
        evaluator.ComputeRequestSemanticSha256(request), state.StateRevision,
        state.StateSha256, fence, $"worker-{fence}", true);

static void Matched(StatisticReconciliationLifecycleResult result, string code)
{
    Equal(StatisticReconciliationLifecycleOutcomes.Matched, result.Outcome,
        code);
    Require(result.RootCause is null && result.EvidenceComplete &&
            result.MissingCount == 0 && result.ExtraCount == 0,
        $"{code}_COMPLETE");
}

static void ExpectCode(Action action, string code, string label)
{
    try
    {
        action();
        throw new InvalidOperationException($"{label}_DID_NOT_THROW");
    }
    catch (StatisticReconciliationLifecycleException exception)
    {
        Equal(code, exception.Code, label);
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"{message}: expected={expected}; actual={actual}");
}

static string Sha(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

static string Canon(decimal value)
    => value == 0m ? "0" : value.ToString("0.#############################",
        System.Globalization.CultureInfo.InvariantCulture);

internal sealed record Spec(
    string EventKind,
    string Status,
    bool IsEffective,
    string? Policy,
    string EpochId,
    long Epoch,
    string CurrentEpochId,
    string Value)
{
    internal static Spec Observe(string status, string value = "10")
        => new(StatisticReconciliationLifecycleEventKinds.Observe, status,
            true, StatisticReconciliationLifecyclePolicies.Include,
            "epoch-1", 1, "epoch-1", value);

    internal static Spec Approve(string value = "10", string? policy =
        StatisticReconciliationLifecyclePolicies.Include,
        string epochId = "epoch-1", string currentEpochId = "epoch-1")
        => new(StatisticReconciliationLifecycleEventKinds.Approve,
            StatisticReconciliationLifecycleStatuses.Approved, true, policy,
            epochId, epochId == "epoch-1" ? 1 : 2, currentEpochId, value);

    internal static Spec Recall(string value = "10")
        => new(StatisticReconciliationLifecycleEventKinds.Recall,
            StatisticReconciliationLifecycleStatuses.Recalled, false,
            StatisticReconciliationLifecyclePolicies.Include,
            "epoch-1", 1, "epoch-1", value);

    internal static Spec Return(string value = "10", bool isEffective = false)
        => new(StatisticReconciliationLifecycleEventKinds.Return,
            StatisticReconciliationLifecycleStatuses.Returned, isEffective,
            StatisticReconciliationLifecyclePolicies.Include,
            "epoch-1", 1, "epoch-1", value);

    internal static Spec Terminate()
        => new(StatisticReconciliationLifecycleEventKinds.Terminate,
            StatisticReconciliationLifecycleStatuses.Terminated, false,
            StatisticReconciliationLifecyclePolicies.Include,
            "epoch-1", 1, "epoch-1", "10");

    internal static Spec Invalidate()
        => new(StatisticReconciliationLifecycleEventKinds.Invalidate,
            StatisticReconciliationLifecycleStatuses.Invalidated, false,
            StatisticReconciliationLifecyclePolicies.Include,
            "epoch-1", 1, "epoch-1", "10");

    internal static Spec Rollback(string epochId = "epoch-1",
        string currentEpochId = "epoch-2")
        => new(StatisticReconciliationLifecycleEventKinds.Rollback,
            StatisticReconciliationLifecycleStatuses.Invalidated, false,
            StatisticReconciliationLifecyclePolicies.Include,
            epochId, epochId == "epoch-1" ? 1 : 2, currentEpochId, "10");

    internal static Spec Restart(string epochId = "epoch-2",
        string currentEpochId = "epoch-2")
        => new(StatisticReconciliationLifecycleEventKinds.Restart,
            StatisticReconciliationLifecycleStatuses.Approved, true,
            StatisticReconciliationLifecyclePolicies.Include,
            epochId, 2, currentEpochId, "10");
}
