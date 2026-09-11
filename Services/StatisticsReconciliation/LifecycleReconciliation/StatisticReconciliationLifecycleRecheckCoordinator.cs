namespace tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

public sealed class StatisticReconciliationLifecycleRecheckCoordinator
{
    private readonly object _gate = new();
    private readonly StatisticReconciliationLifecycleEvaluator _evaluator;
    private readonly IStatisticReconciliationLifecycleObservationStore _store;
    private readonly Dictionary<string, PublishedReceipt> _receipts =
        new(StringComparer.Ordinal);
    private StatisticReconciliationLifecycleRecheckState _state;

    public StatisticReconciliationLifecycleRecheckCoordinator(
        string reconciliationId,
        StatisticReconciliationLifecycleEvaluator evaluator,
        IStatisticReconciliationLifecycleObservationStore store)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        reconciliationId = StatisticReconciliationLifecycleCanonical.Required(
            reconciliationId, "RECONCILIATION_ID");
        var initialSha = StateHash(reconciliationId, 0,
            StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_STATE_GENESIS_V1", reconciliationId),
            0, null, null, 0, 0);
        _state = new StatisticReconciliationLifecycleRecheckState(
            reconciliationId, 0, initialSha, 0, null, null, 0, 0);
    }

    public StatisticReconciliationLifecycleRecheckState ReadState()
    {
        lock (_gate)
            return _state;
    }

    public StatisticReconciliationLifecycleRecheckResult Execute(
        StatisticReconciliationLifecycleRecheckCommand command,
        StatisticReconciliationLifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(request);
        if (!command.AuthorizationGranted)
            Throw(StatisticReconciliationLifecycleFailureCodes.Forbidden,
                "AUTHORIZATION_PRECEDES_EXISTENCE");

        var reconciliationId = StatisticReconciliationLifecycleCanonical.Required(
            command.ReconciliationId, "COMMAND_RECONCILIATION_ID");
        var commandId = StatisticReconciliationLifecycleCanonical.Required(
            command.CommandId, "COMMAND_ID", 128);
        var workerId = StatisticReconciliationLifecycleCanonical.Required(
            command.WorkerId, "WORKER_ID", 128);
        var requestSha = _evaluator.ComputeRequestSemanticSha256(request);
        var suppliedRequestSha = StatisticReconciliationLifecycleCanonical.Sha(
            command.RequestSha256, "REQUEST_SHA256");
        if (!Same(requestSha, suppliedRequestSha))
            Throw(StatisticReconciliationLifecycleFailureCodes.ReplayMismatch,
                "REQUEST_HASH_NOT_CANONICAL");
        if (!Same(reconciliationId, request.ReconciliationId))
            Throw(StatisticReconciliationLifecycleFailureCodes.EvidenceInvalid,
                "REQUEST_RECONCILIATION_CONFLICT");

        lock (_gate)
        {
            if (!Same(reconciliationId, _state.ReconciliationId))
                Throw(StatisticReconciliationLifecycleFailureCodes.Forbidden,
                    "OPAQUE_RECONCILIATION");

            if (_receipts.TryGetValue(commandId, out var published))
            {
                if (!Same(published.Receipt.RequestSha256, requestSha))
                    Throw(StatisticReconciliationLifecycleFailureCodes.ReplayMismatch,
                        "SAME_COMMAND_DIFFERENT_REQUEST");
                var replayAppend = _store.AppendBatch(published.StoredRows);
                if (replayAppend.AppendedCount != 0)
                    Throw(StatisticReconciliationLifecycleFailureCodes
                        .ObservationConflict, "REPLAY_CREATED_SECOND_EFFECT");
                return new StatisticReconciliationLifecycleRecheckResult(
                    true, published.Receipt, _state, published.Result,
                    replayAppend);
            }

            if (command.ExpectedStateRevision != _state.StateRevision ||
                !Same(command.ExpectedStateSha256, _state.StateSha256))
                Throw(StatisticReconciliationLifecycleFailureCodes.CasStale,
                    "EXPECTED_STATE_MISMATCH");
            if (command.FencingToken <= _state.HighestFencingToken)
                Throw(StatisticReconciliationLifecycleFailureCodes.FenceStale,
                    "WORKER_TOKEN_EXPIRED");

            var result = _evaluator.Evaluate(request);
            var generationId = "p10_lifecycle_gen_" +
                StatisticReconciliationLifecycleCanonical.Hash(
                    reconciliationId, commandId, requestSha,
                    command.FencingToken.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))[..24];
            var generationSha = StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_PUBLISHED_GENERATION_V1", generationId,
                _state.CurrentGenerationId, _state.CurrentGenerationSha256,
                result.ResultSemanticSha256, workerId,
                command.FencingToken.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            var rows = BuildRows(request, result, generationId).ToArray();
            var appendResult = _store.AppendBatch(rows);
            if (appendResult.AppendedCount != rows.Length ||
                appendResult.ReplayCount != 0)
                Throw(StatisticReconciliationLifecycleFailureCodes
                    .ObservationConflict, "NEW_GENERATION_NOT_ATOMIC");

            var nextRevision = _state.StateRevision + 1;
            var nextReceiptCount = _state.ReceiptCount + 1;
            var nextStateSha = StateHash(reconciliationId, nextRevision,
                _state.StateSha256, command.FencingToken, generationId,
                generationSha, _state.PublicationCount + 1,
                nextReceiptCount);
            var nextState = new StatisticReconciliationLifecycleRecheckState(
                reconciliationId, nextRevision, nextStateSha,
                command.FencingToken, generationId, generationSha,
                _state.PublicationCount + 1, nextReceiptCount);
            var receiptId = "p10_lifecycle_receipt_" +
                StatisticReconciliationLifecycleCanonical.Hash(
                    reconciliationId, commandId, requestSha)[..24];
            var receiptSha = StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_RECHECK_RECEIPT_V1", receiptId,
                reconciliationId, commandId, requestSha,
                nextRevision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                nextStateSha, generationId, generationSha,
                result.ResultSemanticSha256,
                command.FencingToken.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            var receipt = new StatisticReconciliationLifecycleRecheckReceipt(
                receiptId, reconciliationId, commandId, requestSha,
                nextRevision, nextStateSha, generationId, generationSha,
                result.ResultSemanticSha256, command.FencingToken, receiptSha);
            _state = nextState;
            _receipts.Add(commandId,
                new PublishedReceipt(receipt, result, rows));
            return new StatisticReconciliationLifecycleRecheckResult(
                false, receipt, nextState, result, appendResult);
        }
    }

    private static IEnumerable<StatisticReconciliationLifecycleStoredObservation>
        BuildRows(StatisticReconciliationLifecycleRequest request,
            StatisticReconciliationLifecycleResult result, string generationId)
    {
        foreach (var source in request.Sources
                     .DistinctBy(value => value.ObservationId))
            yield return new StatisticReconciliationLifecycleStoredObservation(
                request.ReconciliationId, generationId,
                StatisticReconciliationLifecycleRecordKinds.Source,
                source.ObservationId, source.SemanticSha256,
                StatisticReconciliationLifecycleCanonical.Hash(
                    "P10_LIFECYCLE_SOURCE_PAYLOAD_V1", source.SemanticSha256));
        foreach (var actual in request.Actuals
                     .DistinctBy(value => value.ObservationId))
            yield return new StatisticReconciliationLifecycleStoredObservation(
                request.ReconciliationId, generationId,
                StatisticReconciliationLifecycleRecordKinds.Actual,
                actual.ObservationId, actual.SemanticSha256,
                StatisticReconciliationLifecycleCanonical.Hash(
                    "P10_LIFECYCLE_ACTUAL_PAYLOAD_V1", actual.SemanticSha256));
        yield return new StatisticReconciliationLifecycleStoredObservation(
            request.ReconciliationId, generationId,
            StatisticReconciliationLifecycleRecordKinds.Result,
            generationId, result.ResultSemanticSha256,
            StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_RESULT_PAYLOAD_V1",
                result.ResultSemanticSha256));
    }

    private static string StateHash(string reconciliationId, long revision,
        string priorStateSha, long fence, string? generationId,
        string? generationSha, int publicationCount, int receiptCount)
        => StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_RECHECK_STATE_V1", reconciliationId,
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            priorStateSha,
            fence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            generationId, generationSha,
            publicationCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            receiptCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

    private static bool Same(string? left, string? right)
        => StatisticReconciliationLifecycleCanonical.Same(left, right);

    private static void Throw(string code, string detail)
        => throw new StatisticReconciliationLifecycleException(code, detail);

    private sealed record PublishedReceipt(
        StatisticReconciliationLifecycleRecheckReceipt Receipt,
        StatisticReconciliationLifecycleResult Result,
        StatisticReconciliationLifecycleStoredObservation[] StoredRows);
}
