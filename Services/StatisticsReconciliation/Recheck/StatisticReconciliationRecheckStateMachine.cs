using System.Security.Cryptography;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.Recheck;

internal sealed record StatisticReconciliationRecheckState(
    string ReconciliationId,
    string Status,
    long StateRevision,
    string StateHash,
    int RetryCount,
    int MaxRetryCount,
    DateTime? NextRetryAtUtc,
    string? LeaseOwnerId,
    string? ClaimToken,
    DateTime? LeaseUntilUtc,
    DateTime? LastHeartbeatAtUtc,
    DateTime DeadlineAtUtc,
    string? DiagnosticCode,
    string? PendingGenerationId,
    string? PendingGenerationHash,
    DateTime? PendingGenerationPublishedAtUtc,
    string CurrentGenerationId,
    string CurrentGenerationHash,
    long GenerationPublishRevision,
    StatisticReconciliationRecheckMarker? Recheck);

/// <summary>
/// Deterministic transition owner for a same-run recheck. Mongo owners use the
/// same predicates in an atomic CAS; this executable form keeps concurrency,
/// retry and crash-window behavior independently testable.
/// </summary>
internal sealed class StatisticReconciliationRecheckStateMachine
{
    private readonly object _gate = new();
    private readonly Func<string> _claimTokenFactory;
    private readonly Dictionary<string, DurableBeginReceipt> _beginReceipts =
        new(StringComparer.Ordinal);
    private StatisticReconciliationRecheckState _state;

    internal StatisticReconciliationRecheckStateMachine(
        StatisticReconciliationRecheckState initial,
        Func<string>? claimTokenFactory = null)
    {
        _claimTokenFactory = claimTokenFactory ?? (() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                .ToLowerInvariant());
        RequireValid(initial);
        _state = Copy(initial);
    }

    internal static StatisticReconciliationRecheckState CreateTerminal(
        string reconciliationId,
        string terminalStatus,
        string currentGenerationId,
        string currentGenerationHash,
        long stateRevision,
        long generationPublishRevision,
        int maxRetryCount,
        DateTime deadlineAtUtc)
    {
        reconciliationId = StatisticReconciliationRecheckCanonical.Required(
            reconciliationId, "reconciliationId", 128);
        terminalStatus = StatisticReconciliationRecheckCanonical
            .RequireTerminalStatus(terminalStatus);
        currentGenerationId = StatisticReconciliationRecheckCanonical.Sha(
            currentGenerationId, "currentGenerationId");
        currentGenerationHash = StatisticReconciliationRecheckCanonical.Sha(
            currentGenerationHash, "currentGenerationHash");
        deadlineAtUtc = StatisticReconciliationRecheckCanonical.Utc(
            deadlineAtUtc, "deadlineAtUtc");
        if (stateRevision < 1 || generationPublishRevision < 1 ||
            maxRetryCount is < 1 or > 20)
            Fail(StatisticReconciliationRecheckFailureCodes.Invalid,
                "terminalCounters");
        var state = new StatisticReconciliationRecheckState(
            reconciliationId, terminalStatus, stateRevision, string.Empty,
            0, maxRetryCount, null, null, null, null, null, deadlineAtUtc,
            null, null, null, null, currentGenerationId,
            currentGenerationHash, generationPublishRevision, null);
        state = state with { StateHash = StateHash(state) };
        RequireValid(state);
        return state;
    }

    internal StatisticReconciliationRecheckState Read()
    {
        lock (_gate)
            return Copy(_state);
    }

    internal StatisticReconciliationBeginRecheckReceipt Begin(
        StatisticReconciliationBeginRecheckCommand command,
        string actorUserId,
        string baseVerdictGenerationId,
        string baseVerdictGenerationHash,
        StatisticReconciliationRecheckCaptureBinding captureBinding,
        DateTime nowUtc,
        TimeSpan recheckSla)
    {
        ArgumentNullException.ThrowIfNull(command);
        nowUtc = StatisticReconciliationRecheckCanonical.Utc(nowUtc,
            "nowUtc");
        actorUserId = StatisticReconciliationRecheckCanonical.Required(
            actorUserId, "actorUserId", 128);
        baseVerdictGenerationId =
            StatisticReconciliationRecheckCanonical.Sha(
                baseVerdictGenerationId, "baseVerdictGenerationId");
        StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(
            captureBinding);
        baseVerdictGenerationHash =
            StatisticReconciliationRecheckCanonical.Sha(
                baseVerdictGenerationHash, "baseVerdictGenerationHash");
        if (recheckSla < TimeSpan.FromSeconds(30) ||
            recheckSla > TimeSpan.FromHours(24))
            Fail(StatisticReconciliationRecheckFailureCodes.Invalid,
                "recheckSla");

        lock (_gate)
        {
            var receiptKey = ReceiptKey(actorUserId, command.CommandId);
            if (_beginReceipts.TryGetValue(receiptKey, out var durable))
            {
                var replayHash = StatisticReconciliationRecheckCanonical
                    .BeginRequestHash(_state.ReconciliationId, actorUserId,
                        command, durable.BaseTerminalStatus,
                        durable.BaseCurrentGenerationId,
                        durable.BaseCurrentGenerationHash,
                        durable.BaseVerdictGenerationId,
                        durable.BaseVerdictGenerationHash);
                if (!Same(replayHash, durable.RequestHash))
                    Fail(StatisticReconciliationRecheckFailureCodes
                        .ReplayMismatch, "sameCommandDifferentRequest");
                return durable.Receipt with { Replayed = true };
            }

            if (_state.Recheck is { } active)
            {
                var replayHash = StatisticReconciliationRecheckCanonical
                    .BeginRequestHash(_state.ReconciliationId, actorUserId,
                        command, active.BaseTerminalStatus,
                        active.BaseCurrentGenerationId,
                        active.BaseCurrentGenerationHash,
                        active.BaseVerdictGenerationId,
                        active.BaseVerdictGenerationHash);
                if (!Same(actorUserId, active.BeginActorUserId) ||
                    !Same(command.CommandId, active.BeginCommandId) ||
                    !Same(replayHash, active.BeginRequestHash))
                    Fail(StatisticReconciliationRecheckFailureCodes
                        .AlreadyActive, "differentRecheckAlreadyActive");
                return new(true, _state.ReconciliationId, active.MarkerId,
                    active.BeginExpectedStateRevision + 1,
                    StatisticReconciliationRunStatuses.Queued);
            }

            if (command.ExpectedStateRevision != _state.StateRevision ||
                !Same(command.ExpectedStateHash, _state.StateHash))
                Fail(StatisticReconciliationRecheckFailureCodes.CasStale,
                    "expectedStateMismatch");
            if (!StatisticReconciliationRecheckTerminalStatuses.All.Contains(
                    _state.Status) ||
                _state.PendingGenerationId is not null)
                Fail(StatisticReconciliationRecheckFailureCodes.PhaseInvalid,
                    "terminalCurrentRequired");

            var marker = StatisticReconciliationRecheckCanonical.NewMarker(
                _state.ReconciliationId, actorUserId, command, _state.Status,
                _state.CurrentGenerationId, _state.CurrentGenerationHash,
                baseVerdictGenerationId, baseVerdictGenerationHash,
                captureBinding, nowUtc);
            var next = _state with
            {
                Status = StatisticReconciliationRunStatuses.Queued,
                StateRevision = _state.StateRevision + 1,
                RetryCount = 0,
                NextRetryAtUtc = nowUtc,
                DeadlineAtUtc = nowUtc.Add(recheckSla),
                DiagnosticCode = null,
                Recheck = marker
            };
            next = next with { StateHash = StateHash(next) };
            RequireValid(next);
            _state = next;
            var receipt = new StatisticReconciliationBeginRecheckReceipt(
                false, next.ReconciliationId, marker.MarkerId,
                next.StateRevision, next.Status);
            _beginReceipts.Add(receiptKey, new(marker.BeginRequestHash,
                marker.BaseTerminalStatus, marker.BaseCurrentGenerationId,
                marker.BaseCurrentGenerationHash,
                marker.BaseVerdictGenerationId,
                marker.BaseVerdictGenerationHash, receipt));
            return receipt;
        }
    }

    internal StatisticReconciliationRecheckWorkerLease Claim(
        string workerId,
        DateTime nowUtc,
        TimeSpan leaseDuration)
    {
        workerId = StatisticReconciliationRecheckCanonical.Required(workerId,
            "workerId", 128);
        nowUtc = StatisticReconciliationRecheckCanonical.Utc(nowUtc,
            "nowUtc");
        if (leaseDuration <= TimeSpan.Zero ||
            leaseDuration > TimeSpan.FromMinutes(10))
            Fail(StatisticReconciliationRecheckFailureCodes.Invalid,
                "leaseDuration");
        lock (_gate)
        {
            var marker = RequirePhase(
                StatisticReconciliationRecheckPhases.ReadyToClaim);
            RequireBaseCurrent(marker);
            if (_state.Status != StatisticReconciliationRunStatuses.Queued ||
                _state.PendingGenerationId is not null ||
                _state.NextRetryAtUtc > nowUtc ||
                _state.DeadlineAtUtc <= nowUtc)
                Fail(StatisticReconciliationRecheckFailureCodes.PhaseInvalid,
                    "notClaimable");

            var claimToken = StatisticReconciliationRecheckCanonical.Sha(
                _claimTokenFactory(), "claimToken");
            var leaseUntil = nowUtc.Add(leaseDuration);
            if (leaseUntil > _state.DeadlineAtUtc)
                leaseUntil = _state.DeadlineAtUtc;
            marker.Phase =
                StatisticReconciliationRecheckPhases.CaptureRunning;
            StatisticReconciliationRecheckCanonical.RefreshMarkerHash(marker);
            var next = _state with
            {
                Status = StatisticReconciliationRunStatuses.Running,
                StateRevision = _state.StateRevision + 1,
                NextRetryAtUtc = null,
                LeaseOwnerId = workerId,
                ClaimToken = claimToken,
                LeaseUntilUtc = leaseUntil,
                LastHeartbeatAtUtc = nowUtc,
                Recheck = marker
            };
            next = next with { StateHash = StateHash(next) };
            RequireValid(next);
            _state = next;
            return new(next.ReconciliationId, marker.MarkerId, workerId,
                claimToken, leaseUntil, next.StateRevision);
        }
    }

    internal StatisticReconciliationRecheckMarker RequireCapture(
        string workerId,
        string claimToken,
        DateTime nowUtc)
    {
        lock (_gate)
        {
            var marker = RequireLiveFence(workerId, claimToken, nowUtc);
            RequireBaseCurrent(marker);
            return StatisticReconciliationRecheckCanonical.Clone(marker);
        }
    }

    internal StatisticReconciliationRecheckState PublishTrusted(
        StatisticReconciliationTrustedRecheckPublishCommand command,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(command);
        nowUtc = StatisticReconciliationRecheckCanonical.Utc(nowUtc,
            "nowUtc");
        var successorId = StatisticReconciliationRecheckCanonical.Sha(
            command.SuccessorGenerationId, "successorGenerationId");
        var successorHash = StatisticReconciliationRecheckCanonical.Sha(
            command.SuccessorGenerationHash, "successorGenerationHash");
        lock (_gate)
        {
            var marker = _state.Recheck ?? throw Error(
                StatisticReconciliationRecheckFailureCodes.PhaseInvalid,
                "markerMissing");
            if (!Same(command.ReconciliationId, _state.ReconciliationId) ||
                !Same(command.MarkerId, marker.MarkerId))
                Fail(StatisticReconciliationRecheckFailureCodes
                    .BaseCurrentChanged, "markerBinding");
            if (marker.Phase ==
                StatisticReconciliationRecheckPhases.PendingPublished)
            {
                if (Same(marker.SuccessorGenerationId, successorId) &&
                    Same(marker.SuccessorGenerationHash, successorHash) &&
                    Same(_state.PendingGenerationId, successorId) &&
                    Same(_state.PendingGenerationHash, successorHash))
                    return Copy(_state);
                Fail(StatisticReconciliationRecheckFailureCodes
                    .ReplayMismatch, "differentPublishedGeneration");
            }

            marker = RequireLiveFence(command.WorkerId, command.ClaimToken,
                nowUtc);
            RequireBaseCurrent(marker);
            if (Same(successorId, marker.BaseCurrentGenerationId) ||
                Same(successorHash, marker.BaseCurrentGenerationHash))
                Fail(StatisticReconciliationRecheckFailureCodes
                    .SuccessorNotDistinct, "successorEqualsBaseCurrent");

            marker.SuccessorGenerationId = successorId;
            marker.SuccessorGenerationHash = successorHash;
            marker.Phase =
                StatisticReconciliationRecheckPhases.PendingPublished;
            StatisticReconciliationRecheckCanonical.RefreshMarkerHash(marker);
            var next = _state with
            {
                Status = StatisticReconciliationRunStatuses.Queued,
                StateRevision = _state.StateRevision + 1,
                NextRetryAtUtc = null,
                LeaseOwnerId = null,
                ClaimToken = null,
                LeaseUntilUtc = null,
                LastHeartbeatAtUtc = null,
                PendingGenerationId = successorId,
                PendingGenerationHash = successorHash,
                PendingGenerationPublishedAtUtc = nowUtc,
                GenerationPublishRevision =
                    _state.GenerationPublishRevision + 1,
                Recheck = marker
            };
            next = next with { StateHash = StateHash(next) };
            RequireValid(next);
            _state = next;
            return Copy(next);
        }
    }

    internal StatisticReconciliationTrustedRecheckPromotionCommand
        PreparePromotion(
            string terminalStatus,
            string successorVerdictGenerationId,
            string successorVerdictGenerationHash)
    {
        terminalStatus = StatisticReconciliationRecheckCanonical
            .RequireTerminalStatus(terminalStatus);
        successorVerdictGenerationId =
            StatisticReconciliationRecheckCanonical.Sha(
                successorVerdictGenerationId,
                "successorVerdictGenerationId");
        successorVerdictGenerationHash =
            StatisticReconciliationRecheckCanonical.Sha(
                successorVerdictGenerationHash,
                "successorVerdictGenerationHash");
        lock (_gate)
        {
            var marker = RequirePhase(
                StatisticReconciliationRecheckPhases.PendingPublished);
            RequireBaseCurrent(marker);
            if (!Same(_state.PendingGenerationId,
                    marker.SuccessorGenerationId) ||
                !Same(_state.PendingGenerationHash,
                    marker.SuccessorGenerationHash))
                Fail(StatisticReconciliationRecheckFailureCodes.PhaseInvalid,
                    "pendingMarkerMismatch");
            return new(_state.ReconciliationId, marker.MarkerId,
                marker.BaseCurrentGenerationId,
                marker.BaseCurrentGenerationHash,
                marker.BaseVerdictGenerationId,
                marker.BaseVerdictGenerationHash,
                marker.SuccessorGenerationId!, marker.SuccessorGenerationHash!,
                terminalStatus, successorVerdictGenerationId,
                successorVerdictGenerationHash, _state.StateRevision,
                _state.StateHash, _state.GenerationPublishRevision,
                marker.ReviewSupersessionCommandId);
        }
    }

    /// <summary>
    /// Models the postimage returned by the trusted Mongo promotion owner. The
    /// durable marker deliberately remains until append/replay of review
    /// supersession markers succeeds.
    /// </summary>
    internal StatisticReconciliationTrustedRecheckPromotionResult
        RecordTrustedPromotion(
            StatisticReconciliationTrustedRecheckPromotionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            var marker = _state.Recheck ?? throw Error(
                StatisticReconciliationRecheckFailureCodes.PhaseInvalid,
                "markerMissing");
            if (marker.Phase ==
                StatisticReconciliationRecheckPhases
                    .ReviewSupersessionPending)
            {
                RequirePromotionReplay(command, marker);
                return PromotionResult(marker, replayed: true);
            }
            if (marker.Phase !=
                    StatisticReconciliationRecheckPhases.PendingPublished ||
                command.ExpectedStateRevision != _state.StateRevision ||
                !Same(command.ExpectedStateHash, _state.StateHash) ||
                command.ExpectedGenerationPublishRevision !=
                    _state.GenerationPublishRevision)
                Fail(StatisticReconciliationRecheckFailureCodes.CasStale,
                    "promotionExpectedStateMismatch");
            RequirePromotionReplay(command, marker);

            marker.SuccessorVerdictGenerationId =
                command.SuccessorVerdictGenerationId;
            marker.SuccessorVerdictGenerationHash =
                command.SuccessorVerdictGenerationHash;
            marker.Phase = StatisticReconciliationRecheckPhases
                .ReviewSupersessionPending;
            StatisticReconciliationRecheckCanonical.RefreshMarkerHash(marker);
            var next = _state with
            {
                Status = command.TerminalStatus,
                StateRevision = _state.StateRevision + 1,
                PendingGenerationId = null,
                PendingGenerationHash = null,
                PendingGenerationPublishedAtUtc = null,
                CurrentGenerationId = command.SuccessorGenerationId,
                CurrentGenerationHash = command.SuccessorGenerationHash,
                Recheck = marker
            };
            next = next with { StateHash = StateHash(next) };
            RequireValid(next);
            _state = next;
            return PromotionResult(marker, replayed: false);
        }
    }

    internal StatisticReconciliationRecheckState CompleteReviewSupersession(
        string markerId,
        long expectedStateRevision,
        string expectedStateHash)
    {
        lock (_gate)
        {
            var marker = RequirePhase(StatisticReconciliationRecheckPhases
                .ReviewSupersessionPending);
            if (!Same(markerId, marker.MarkerId) ||
                expectedStateRevision != _state.StateRevision ||
                !Same(expectedStateHash, _state.StateHash))
                Fail(StatisticReconciliationRecheckFailureCodes.CasStale,
                    "reviewCompletionExpectedStateMismatch");
            var next = _state with
            {
                StateRevision = _state.StateRevision + 1,
                Recheck = null
            };
            next = next with { StateHash = StateHash(next) };
            RequireValid(next);
            _state = next;
            return Copy(next);
        }
    }

    internal StatisticReconciliationRecheckState ReleaseCapture(
        string workerId,
        string claimToken,
        DateTime nowUtc,
        bool transient)
    {
        lock (_gate)
        {
            var marker = RequireLiveFence(workerId, claimToken, nowUtc);
            RequireBaseCurrent(marker);
            StatisticReconciliationRecheckState next;
            if (transient && _state.RetryCount + 1 < _state.MaxRetryCount &&
                nowUtc < _state.DeadlineAtUtc)
            {
                marker.Phase =
                    StatisticReconciliationRecheckPhases.ReadyToClaim;
                StatisticReconciliationRecheckCanonical.RefreshMarkerHash(
                    marker);
                next = _state with
                {
                    Status = StatisticReconciliationRunStatuses.Queued,
                    StateRevision = _state.StateRevision + 1,
                    RetryCount = _state.RetryCount + 1,
                    NextRetryAtUtc = nowUtc,
                    LeaseOwnerId = null,
                    ClaimToken = null,
                    LeaseUntilUtc = null,
                    LastHeartbeatAtUtc = null,
                    DiagnosticCode = "P10_RECHECK_CAPTURE_RETRY",
                    Recheck = marker
                };
            }
            else
            {
                // Operational capture failure must not replace or hide the
                // previously current, terminal generation.
                next = _state with
                {
                    Status = marker.BaseTerminalStatus,
                    StateRevision = _state.StateRevision + 1,
                    RetryCount = _state.RetryCount + 1,
                    NextRetryAtUtc = null,
                    LeaseOwnerId = null,
                    ClaimToken = null,
                    LeaseUntilUtc = null,
                    LastHeartbeatAtUtc = null,
                    DiagnosticCode = "P10_RECHECK_CAPTURE_ABORTED",
                    Recheck = null
                };
            }
            next = next with { StateHash = StateHash(next) };
            RequireValid(next);
            _state = next;
            return Copy(next);
        }
    }

    private StatisticReconciliationRecheckMarker RequireLiveFence(
        string workerId,
        string claimToken,
        DateTime nowUtc)
    {
        nowUtc = StatisticReconciliationRecheckCanonical.Utc(nowUtc,
            "nowUtc");
        var marker = RequirePhase(
            StatisticReconciliationRecheckPhases.CaptureRunning);
        if (_state.Status != StatisticReconciliationRunStatuses.Running ||
            !Same(_state.LeaseOwnerId, workerId) ||
            !Same(_state.ClaimToken, claimToken) ||
            _state.LeaseUntilUtc is not { } lease || lease <= nowUtc ||
            _state.DeadlineAtUtc <= nowUtc)
            Fail(StatisticReconciliationRecheckFailureCodes.FenceStale,
                "workerFenceExpired");
        return marker;
    }

    private StatisticReconciliationRecheckMarker RequirePhase(string phase)
    {
        var marker = _state.Recheck ?? throw Error(
            StatisticReconciliationRecheckFailureCodes.PhaseInvalid,
            "markerMissing");
        StatisticReconciliationRecheckCanonical.RequireValidMarker(marker);
        if (!Same(marker.Phase, phase))
            Fail(StatisticReconciliationRecheckFailureCodes.PhaseInvalid,
                $"expected:{phase}");
        return StatisticReconciliationRecheckCanonical.Clone(marker);
    }

    private void RequireBaseCurrent(
        StatisticReconciliationRecheckMarker marker)
    {
        if (!Same(_state.CurrentGenerationId,
                marker.BaseCurrentGenerationId) ||
            !Same(_state.CurrentGenerationHash,
                marker.BaseCurrentGenerationHash))
            Fail(StatisticReconciliationRecheckFailureCodes
                .BaseCurrentChanged, "currentNoLongerMatchesMarker");
    }

    private void RequirePromotionReplay(
        StatisticReconciliationTrustedRecheckPromotionCommand command,
        StatisticReconciliationRecheckMarker marker)
    {
        if (!Same(command.ReconciliationId, _state.ReconciliationId) ||
            !Same(command.MarkerId, marker.MarkerId) ||
            !Same(command.BaseCurrentGenerationId,
                marker.BaseCurrentGenerationId) ||
            !Same(command.BaseCurrentGenerationHash,
                marker.BaseCurrentGenerationHash) ||
            !Same(command.BaseVerdictGenerationId,
                marker.BaseVerdictGenerationId) ||
            !Same(command.BaseVerdictGenerationHash,
                marker.BaseVerdictGenerationHash) ||
            !Same(command.SuccessorGenerationId,
                marker.SuccessorGenerationId) ||
            !Same(command.SuccessorGenerationHash,
                marker.SuccessorGenerationHash) ||
            !Same(command.ReviewSupersessionCommandId,
                marker.ReviewSupersessionCommandId) ||
            !StatisticReconciliationRecheckTerminalStatuses.All.Contains(
                command.TerminalStatus) ||
            !StatisticReconciliationRecheckCanonical.IsSha(
                command.SuccessorVerdictGenerationId) ||
            !StatisticReconciliationRecheckCanonical.IsSha(
                command.SuccessorVerdictGenerationHash))
            Fail(StatisticReconciliationRecheckFailureCodes.ReplayMismatch,
                "promotionBindingMismatch");
        if (marker.Phase == StatisticReconciliationRecheckPhases
                .ReviewSupersessionPending &&
            (!Same(marker.SuccessorVerdictGenerationId,
                 command.SuccessorVerdictGenerationId) ||
             !Same(marker.SuccessorVerdictGenerationHash,
                 command.SuccessorVerdictGenerationHash) ||
             !Same(_state.CurrentGenerationId,
                 command.SuccessorGenerationId) ||
             !Same(_state.CurrentGenerationHash,
                 command.SuccessorGenerationHash) ||
             _state.Status != command.TerminalStatus))
            Fail(StatisticReconciliationRecheckFailureCodes.ReplayMismatch,
                "promotionPostimageMismatch");
    }

    private StatisticReconciliationTrustedRecheckPromotionResult
        PromotionResult(
            StatisticReconciliationRecheckMarker marker,
            bool replayed)
        => new(_state.ReconciliationId, marker.MarkerId,
            marker.BaseCurrentGenerationId, _state.CurrentGenerationId,
            _state.CurrentGenerationHash, _state.Status,
            _state.StateRevision, _state.StateHash, replayed);

    private static void RequireValid(
        StatisticReconciliationRecheckState state)
    {
        if (state.StateRevision < 1 || state.RetryCount < 0 ||
            state.MaxRetryCount is < 1 or > 20 ||
            state.RetryCount > state.MaxRetryCount ||
            state.GenerationPublishRevision < 1 ||
            state.DeadlineAtUtc == default ||
            state.DeadlineAtUtc.Kind != DateTimeKind.Utc ||
            !StatisticReconciliationRecheckCanonical.IsSha(
                state.CurrentGenerationId) ||
            !StatisticReconciliationRecheckCanonical.IsSha(
                state.CurrentGenerationHash) ||
            !Same(state.StateHash, StateHash(state)))
            Fail(StatisticReconciliationRecheckFailureCodes.Invalid,
                "stateHeader");

        var pendingAbsent = state.PendingGenerationId is null &&
                            state.PendingGenerationHash is null &&
                            state.PendingGenerationPublishedAtUtc is null;
        var pendingComplete =
            StatisticReconciliationRecheckCanonical.IsSha(
                state.PendingGenerationId) &&
            StatisticReconciliationRecheckCanonical.IsSha(
                state.PendingGenerationHash) &&
            state.PendingGenerationPublishedAtUtc is
                { Kind: DateTimeKind.Utc };
        var fenceAbsent = state.LeaseOwnerId is null &&
                          state.ClaimToken is null &&
                          state.LeaseUntilUtc is null &&
                          state.LastHeartbeatAtUtc is null;
        var fenceComplete =
            !string.IsNullOrWhiteSpace(state.LeaseOwnerId) &&
            StatisticReconciliationRecheckCanonical.IsSha(state.ClaimToken) &&
            state.LeaseUntilUtc is { Kind: DateTimeKind.Utc } &&
            state.LastHeartbeatAtUtc is { Kind: DateTimeKind.Utc };
        if ((!pendingAbsent && !pendingComplete) ||
            (!fenceAbsent && !fenceComplete))
            Fail(StatisticReconciliationRecheckFailureCodes.Invalid,
                "stateTuple");

        if (state.Recheck is null)
        {
            if (!pendingAbsent || !fenceAbsent ||
                !StatisticReconciliationRecheckTerminalStatuses.All.Contains(
                    state.Status))
                Fail(StatisticReconciliationRecheckFailureCodes.Invalid,
                    "unmarkedState");
            return;
        }

        var marker = state.Recheck;
        StatisticReconciliationRecheckCanonical.RequireValidMarker(marker);
        var baseStillCurrent = Same(state.CurrentGenerationId,
                                   marker.BaseCurrentGenerationId) &&
                               Same(state.CurrentGenerationHash,
                                   marker.BaseCurrentGenerationHash);
        var phaseValid = marker.Phase switch
        {
            StatisticReconciliationRecheckPhases.ReadyToClaim =>
                baseStillCurrent && pendingAbsent && fenceAbsent &&
                state.Status == StatisticReconciliationRunStatuses.Queued,
            StatisticReconciliationRecheckPhases.CaptureRunning =>
                baseStillCurrent && pendingAbsent && fenceComplete &&
                state.Status == StatisticReconciliationRunStatuses.Running,
            StatisticReconciliationRecheckPhases.PendingPublished =>
                baseStillCurrent && pendingComplete && fenceAbsent &&
                state.Status == StatisticReconciliationRunStatuses.Queued &&
                Same(state.PendingGenerationId,
                    marker.SuccessorGenerationId) &&
                Same(state.PendingGenerationHash,
                    marker.SuccessorGenerationHash),
            StatisticReconciliationRecheckPhases
                .ReviewSupersessionPending =>
                pendingAbsent && fenceAbsent &&
                StatisticReconciliationRecheckTerminalStatuses.All.Contains(
                    state.Status) &&
                Same(state.CurrentGenerationId,
                    marker.SuccessorGenerationId) &&
                Same(state.CurrentGenerationHash,
                    marker.SuccessorGenerationHash),
            _ => false
        };
        if (!phaseValid)
            Fail(StatisticReconciliationRecheckFailureCodes.Invalid,
                "recheckPhaseState");
    }

    private static string StateHash(
        StatisticReconciliationRecheckState state)
        => StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_SAME_RUN_RECHECK_STATE_V1",
            state.ReconciliationId,
            state.Status,
            state.StateRevision,
            state.RetryCount,
            state.MaxRetryCount,
            state.NextRetryAtUtc,
            state.LeaseOwnerId,
            state.ClaimToken,
            state.LeaseUntilUtc,
            state.LastHeartbeatAtUtc,
            state.DeadlineAtUtc,
            state.DiagnosticCode,
            state.PendingGenerationId,
            state.PendingGenerationHash,
            state.PendingGenerationPublishedAtUtc,
            state.CurrentGenerationId,
            state.CurrentGenerationHash,
            state.GenerationPublishRevision,
            recheckMarkerStateHash = state.Recheck?.MarkerStateHash
        });

    private static StatisticReconciliationRecheckState Copy(
        StatisticReconciliationRecheckState state)
        => state with
        {
            Recheck = state.Recheck is null
                ? null
                : StatisticReconciliationRecheckCanonical.Clone(state.Recheck)
        };

    private static string ReceiptKey(string actorUserId, string commandId)
        => actorUserId + "\n" + commandId;

    private static bool Same(string? left, string? right)
        => StatisticReconciliationRecheckCanonical.Same(left, right);

    private static StatisticReconciliationRecheckException Error(
        string code,
        string detail)
        => new(code, detail);

    private static void Fail(string code, string detail)
        => throw Error(code, detail);

    private sealed record DurableBeginReceipt(
        string RequestHash,
        string BaseTerminalStatus,
        string BaseCurrentGenerationId,
        string BaseCurrentGenerationHash,
        string BaseVerdictGenerationId,
        string BaseVerdictGenerationHash,
        StatisticReconciliationBeginRecheckReceipt Receipt);
}
