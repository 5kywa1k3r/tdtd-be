using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.Recheck;

internal interface IStatisticReconciliationTrustedRecheckFinalizer
{
    Task<StatisticReconciliationTrustedFinalizeResult>
        FinalizePendingRecheckAsync(
            StatisticReconciliationTrustedFinalizeCommand command,
            MeResponse systemActor,
            CancellationToken ct = default);

    Task<StatisticReconciliationTrustedFinalizeResult>
        ReplayPromotedRecheckAsync(
            string reconciliationId,
            MeResponse systemActor,
            CancellationToken ct = default);
}

/// <summary>
/// Trusted, phase-aware same-run recheck completion owner. A verdict is
/// append-only before the exact current swap. The swap persists a durable
/// receipt, so retries converge after every crash boundary, including after
/// the active marker has been cleared.
/// </summary>
internal sealed class StatisticReconciliationTrustedRecheckFinalizer(
    MongoDbContext context,
    IStatisticReconciliationCandidateActivation activation,
    IAppTimeService time,
    StatisticReconciliationFinalVerdictPublisher verdictPublisher,
    IStatisticReconciliationReviewBackend verdictBackend,
    IStatisticReconciliationRecheckReviewSupersessionAppender reviewAppender)
    : IStatisticReconciliationTrustedRecheckFinalizer
{
    public async Task<StatisticReconciliationTrustedFinalizeResult>
        FinalizePendingRecheckAsync(
            StatisticReconciliationTrustedFinalizeCommand command,
            MeResponse systemActor,
            CancellationToken ct = default)
    {
        global::tdtd_be.Services.StatisticsReconciliation.Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(systemActor);
        ArgumentNullException.ThrowIfNull(command);
        var candidate = activation.RequireFoundation(
            StatisticReconciliationCapabilities.ExpectedActualDelta,
            StatisticReconciliationRouteRegistry.RecheckFinalize);
        if (!ObjectId.TryParse(command.ReconciliationId, out _))
            throw NotFound();

        var run = await LoadAsync(command.ReconciliationId,
            candidate.ChainId, ct);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        var now = MongoUtc(time.UtcNow);

        if (run.Recheck is null)
            return await ResolveClearedReplayAsync(
                run, command.TrustedVerdict, systemActor, ct);

        var marker = StatisticReconciliationRecheckCanonical.Clone(run.Recheck);
        StatisticReconciliationRecheckCanonical.RequireValidMarker(marker);
        return marker.Phase switch
        {
            StatisticReconciliationRecheckPhases.PendingPublished =>
                await PromotePendingAsync(run, marker, command, systemActor,
                    now, ct),
            StatisticReconciliationRecheckPhases.ReviewSupersessionPending =>
                await CompleteReviewSupersessionAsync(run, marker,
                    command.TrustedVerdict, systemActor, now,
                    isReplay: true, ct),
            _ => throw Conflict("RECHECK_FINALIZE_PHASE_INVALID")
        };
    }

    public async Task<StatisticReconciliationTrustedFinalizeResult>
        ReplayPromotedRecheckAsync(
            string reconciliationId,
            MeResponse systemActor,
            CancellationToken ct = default)
    {
        global::tdtd_be.Services.StatisticsReconciliation.Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(systemActor);
        var candidate = activation.RequireFoundation(
            StatisticReconciliationCapabilities.ExpectedActualDelta,
            StatisticReconciliationRouteRegistry.RecheckFinalize);
        if (!ObjectId.TryParse(reconciliationId, out _))
            throw NotFound();
        var run = await LoadAsync(reconciliationId, candidate.ChainId, ct);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        var receipt = run.CurrentRecheckFinalizeReceipt;
        if (receipt is null ||
            !StatisticReconciliationRunService
                .HasValidRecheckFinalizeReceipt(run) ||
            run.PendingGenerationId is not null ||
            run.PendingGenerationHash is not null ||
            run.PendingGenerationPublishedAtUtc.HasValue ||
            run.CurrentGenerationId !=
                receipt.SuccessorActualGenerationId ||
            run.CurrentGenerationHash !=
                receipt.SuccessorActualGenerationSha256)
            throw Conflict("RECHECK_DURABLE_REPLAY_MISMATCH");

        var baseVerdict = await verdictBackend.ReadAsync(run.Id,
            receipt.BaseVerdictGenerationId, ct);
        if (baseVerdict is null)
            throw Conflict("RECHECK_DURABLE_BASE_VERDICT_MISSING");
        StatisticReconciliationFinalVerdictPublisher.ValidateStored(
            baseVerdict);
        if (baseVerdict.VerdictGenerationSha256 !=
                receipt.BaseVerdictGenerationSha256 ||
            baseVerdict.ActualGenerationId !=
                receipt.BaseActualGenerationId ||
            baseVerdict.ActualGenerationSha256 !=
                receipt.BaseActualGenerationSha256 ||
            (receipt.RemediationBinding is not null &&
             (baseVerdict.RootCauseClass !=
                  receipt.RemediationBinding.RootCauseClass ||
              !baseVerdict.MissingOrExtraIdentity)))
            throw Conflict("RECHECK_DURABLE_BASE_VERDICT_MISMATCH");
        var verdicts = (await verdictBackend.ReadLineageAsync(run.Id, ct))
            .Where(value =>
                value.VerdictGenerationId ==
                    receipt.SuccessorVerdictGenerationId &&
                value.VerdictGenerationSha256 ==
                    receipt.SuccessorVerdictGenerationSha256 &&
                value.ActualGenerationId == run.CurrentGenerationId &&
                value.ActualGenerationSha256 == run.CurrentGenerationHash &&
                value.SupersedesVerdictGenerationId ==
                    receipt.BaseVerdictGenerationId &&
                value.SupersedesVerdictGenerationSha256 ==
                    receipt.BaseVerdictGenerationSha256)
            .ToArray();
        if (verdicts.Length != 1)
            throw Conflict("RECHECK_DURABLE_VERDICT_CARDINALITY_INVALID");
        var verdict = verdicts[0];
        StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);
        if (run.Status != receipt.TerminalStatus ||
            run.Status != StatisticReconciliationRunService
                .FinalRunStatus(verdict) ||
            !StatisticReconciliationTrustedRemediationEvidenceCanonical
                .MatchesStored(receipt.RemediationBinding,
                    verdict.Remediation, run.CurrentGenerationId!,
                    run.CurrentGenerationHash!) ||
            receipt.RemediationSemanticSha256 !=
                verdict.Remediation?.RemediationSemanticSha256)
            throw Conflict("RECHECK_DURABLE_VERDICT_MISMATCH");

        if (run.Recheck is null)
            return Result(true, run, verdict);
        var marker = StatisticReconciliationRecheckCanonical.Clone(
            run.Recheck);
        StatisticReconciliationRecheckCanonical.RequireValidMarker(marker);
        if (marker.Phase != StatisticReconciliationRecheckPhases
                .ReviewSupersessionPending ||
            marker.MarkerId != receipt.MarkerId ||
            marker.SuccessorGenerationId != run.CurrentGenerationId ||
            marker.SuccessorGenerationHash != run.CurrentGenerationHash ||
            marker.SuccessorVerdictGenerationId !=
                verdict.VerdictGenerationId ||
            marker.SuccessorVerdictGenerationHash !=
                verdict.VerdictGenerationSha256)
            throw Conflict("RECHECK_DURABLE_MARKER_MISMATCH");

        await reviewAppender.AppendOrReplayAsync(
            run.Id, receipt.BaseVerdictGenerationId,
            verdict.VerdictGenerationId,
            receipt.ReviewSupersessionCommandId, systemActor, ct);
        var postSupersession = await LoadAsync(
            run.Id, run.CandidateChainId, ct);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            postSupersession);
        var cleared = await ClearMarkerAsync(postSupersession, marker,
            systemActor, MongoUtc(time.UtcNow), ct);
        return Result(true, cleared, verdict);
    }
    private async Task<StatisticReconciliationTrustedFinalizeResult>
        PromotePendingAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationRecheckMarker marker,
            StatisticReconciliationTrustedFinalizeCommand command,
            MeResponse actor,
            DateTime now,
            CancellationToken ct)
    {
        RequirePending(run, marker, command);
        var decision = Evaluate(command.TrustedVerdict);
        await RequireBaseVerdictAsync(run.Id, marker, ct);
        var (verdict, verdictReplay) = await ResolveOrPublishVerdictAsync(
            run.Id, marker, command.TrustedVerdict, decision, now, ct);

        marker.SuccessorVerdictGenerationId = verdict.VerdictGenerationId;
        marker.SuccessorVerdictGenerationHash =
            verdict.VerdictGenerationSha256;
        marker.Phase =
            StatisticReconciliationRecheckPhases.ReviewSupersessionPending;
        StatisticReconciliationRecheckCanonical.RefreshMarkerHash(marker);

        var status = StatisticReconciliationRunService.FinalRunStatus(verdict);
        var failedAt = status == StatisticReconciliationRunStatuses.Failed
            ? now
            : (DateTime?)null;
        var diagnostic = status == StatisticReconciliationRunStatuses.Failed
            ? "P10_RECONCILIATION_FINAL_VERDICT_FAILED"
            : null;
        var nextRevision = checked(run.StateRevision + 1);
        var receipt = StatisticReconciliationRunService
            .BuildRecheckFinalizeReceipt(
                marker, verdict.VerdictGenerationId,
                verdict.VerdictGenerationSha256, status,
                nextRevision, now,
                verdict.Remediation?.RemediationSemanticSha256,
                command.CurrentP8Configuration);
        var hashRun = Clone(run);
        hashRun.CurrentRecheckFinalizeReceipt = receipt;
        var stateHash = StatisticReconciliationRunService
            .BuildTrustedRecheckPromotedStateHash(
                hashRun, marker, status, nextRevision,
                marker.SuccessorGenerationId!, marker.SuccessorGenerationHash!,
                diagnostic, failedAt);

        var fb = Builders<StatisticReconciliationRun>.Filter;
        var updated = await context.StatisticReconciliationRuns
            .FindOneAndUpdateAsync(
                fb.Eq(value => value.Id, run.Id) &
                fb.Eq(value => value.CandidateChainId,
                    run.CandidateChainId) &
                fb.Eq(value => value.Status,
                    StatisticReconciliationRunStatuses.Queued) &
                fb.Eq(value => value.StateRevision, run.StateRevision) &
                fb.Eq(value => value.StateHash, run.StateHash) &
                fb.Eq(value => value.ReviewDecisionRevision,
                    run.ReviewDecisionRevision) &
                fb.Eq(value => value.GenerationPublishRevision,
                    run.GenerationPublishRevision) &
                fb.Eq(value => value.CurrentGenerationId,
                    marker.BaseCurrentGenerationId) &
                fb.Eq(value => value.CurrentGenerationHash,
                    marker.BaseCurrentGenerationHash) &
                fb.Eq(value => value.PendingGenerationId,
                    marker.SuccessorGenerationId) &
                fb.Eq(value => value.PendingGenerationHash,
                    marker.SuccessorGenerationHash) &
                fb.Eq("recheck.markerId", marker.MarkerId) &
                fb.Eq("recheck.phase",
                    StatisticReconciliationRecheckPhases.PendingPublished) &
                fb.Eq(value => value.LeaseOwnerId, null) &
                fb.Eq(value => value.ClaimToken, null) &
                fb.Eq(value => value.LeaseUntilUtc, null) &
                fb.Eq(value => value.LastHeartbeatAtUtc, null) &
                fb.Gt(value => value.DeadlineAtUtc, now) &
                fb.Eq(value => value.IsDeleted, false),
                Builders<StatisticReconciliationRun>.Update
                    .Set(value => value.Status, status)
                    .Set(value => value.StateRevision, nextRevision)
                    .Set(value => value.StateHash, stateHash)
                    .Set(value => value.NextRetryAtUtc, null)
                    .Set(value => value.DiagnosticCode, diagnostic)
                    .Set(value => value.PendingGenerationId, null)
                    .Set(value => value.PendingGenerationHash, null)
                    .Set(value => value.PendingGenerationPublishedAtUtc, null)
                    .Set(value => value.CurrentGenerationId,
                        marker.SuccessorGenerationId)
                    .Set(value => value.CurrentGenerationHash,
                        marker.SuccessorGenerationHash)
                    .Set(value => value.Recheck, marker)
                    .Set(value =>
                        value.CurrentGenerationRecheckCaptureBinding,
                        marker.CaptureBinding)
                    .Set(value => value.CurrentRecheckFinalizeReceipt,
                        receipt)
                    .Set(value => value.FailedAtUtc, failedAt)
                    .Set(value => value.LatestWriterUserId, actor.Id)
                    .Set(value => value.UpdatedAtUtc, now)
                    .Set(value => value.UpdatedByUserId, actor.Id),
                new FindOneAndUpdateOptions<StatisticReconciliationRun>
                {
                    ReturnDocument = ReturnDocument.After
                }, ct);

        if (updated is null)
        {
            var observed = await LoadAsync(run.Id, run.CandidateChainId, ct);
            if (observed.Recheck?.MarkerId == marker.MarkerId)
            {
                if (observed.Recheck.Phase ==
                    StatisticReconciliationRecheckPhases
                        .ReviewSupersessionPending)
                {
                    return await CompleteReviewSupersessionAsync(
                        observed,
                        StatisticReconciliationRecheckCanonical.Clone(
                            observed.Recheck),
                        command.TrustedVerdict, actor, now,
                        isReplay: true, ct);
                }
                if (observed.Recheck.Phase ==
                        StatisticReconciliationRecheckPhases
                            .PendingPublished &&
                    observed.PendingGenerationId ==
                        marker.SuccessorGenerationId &&
                    observed.PendingGenerationHash ==
                        marker.SuccessorGenerationHash)
                {
                    // A review decision may legally win the revision fence
                    // immediately before promotion. Rebase only the trusted
                    // state pins; marker, pending actual and verdict request
                    // remain exact, so the next CAS includes that decision in
                    // the subsequent supersession sweep.
                    var rebased = command with
                    {
                        ExpectedStateRevision = observed.StateRevision,
                        ExpectedStateHash = observed.StateHash,
                        ExpectedGenerationPublishRevision =
                            observed.GenerationPublishRevision
                    };
                    return await PromotePendingAsync(
                        observed,
                        StatisticReconciliationRecheckCanonical.Clone(
                            observed.Recheck),
                        rebased, actor, now, ct);
                }
            }
            if (observed.Recheck is null)
                return await ResolveClearedReplayAsync(
                    observed, command.TrustedVerdict, actor, ct);
            throw Conflict("RECHECK_PROMOTION_CAS_LOST");
        }

        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            updated);
        return await CompleteReviewSupersessionAsync(updated, marker,
            command.TrustedVerdict, actor, now, verdictReplay, ct);
    }

    private async Task<StatisticReconciliationTrustedFinalizeResult>
        CompleteReviewSupersessionAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationRecheckMarker marker,
            StatisticReconciliationFinalVerdictRequest request,
            MeResponse actor,
            DateTime now,
            bool isReplay,
            CancellationToken ct)
    {
        RequirePromoted(run, marker, request);
        var decision = Evaluate(request);
        var verdict = await RequireExactSuccessorAsync(
            run.Id, marker, decision, ct);
        var receipt = run.CurrentRecheckFinalizeReceipt!;
        RequireReceiptMatches(receipt, marker, verdict, run);

        await reviewAppender.AppendOrReplayAsync(
            run.Id,
            marker.BaseVerdictGenerationId,
            verdict.VerdictGenerationId,
            marker.ReviewSupersessionCommandId,
            actor,
            ct);
        // Supersession append owns the same run revision fence as review
        // decisions. Reload its committed revision before clearing the marker.
        var postSupersession = await LoadAsync(
            run.Id, run.CandidateChainId, ct);
        StatisticReconciliationRunService
            .RequireActualCaptureReadIntegrity(postSupersession);
        RequirePromoted(postSupersession, marker, request);
        RequireReceiptMatches(
            postSupersession.CurrentRecheckFinalizeReceipt!, marker, verdict,
            postSupersession);
        var cleared = await ClearMarkerAsync(
            postSupersession, marker, actor, now, ct);
        return Result(isReplay, cleared, verdict);
    }

    private async Task<StatisticReconciliationTrustedFinalizeResult>
        ResolveClearedReplayAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationFinalVerdictRequest request,
            MeResponse actor,
            CancellationToken ct)
    {
        var receipt = run.CurrentRecheckFinalizeReceipt;
        if (receipt is null ||
            !StatisticReconciliationRunService
                .HasValidRecheckFinalizeReceipt(run) ||
            run.PendingGenerationId is not null ||
            run.PendingGenerationHash is not null ||
            run.PendingGenerationPublishedAtUtc.HasValue ||
            request.ReconciliationId != run.Id ||
            request.ActualGenerationId != run.CurrentGenerationId ||
            request.ActualGenerationSha256 != run.CurrentGenerationHash)
            throw Conflict("RECHECK_FINALIZE_REPLAY_MISMATCH");

        var decision = Evaluate(request);
        var candidates = (await verdictBackend.ReadLineageAsync(run.Id, ct))
            .Where(value =>
                value.VerdictGenerationId ==
                    receipt.SuccessorVerdictGenerationId &&
                value.ActualGenerationId == run.CurrentGenerationId &&
                value.ActualGenerationSha256 == run.CurrentGenerationHash)
            .ToArray();
        if (candidates.Length != 1)
            throw Conflict("RECHECK_SUCCESSOR_VERDICT_CARDINALITY_INVALID");
        var verdict = candidates[0];
        RequireExactVerdict(verdict, decision,
            receipt.BaseVerdictGenerationId,
            receipt.BaseVerdictGenerationSha256,
            receipt.RemediationBinding);
        if (verdict.VerdictGenerationSha256 !=
                receipt.SuccessorVerdictGenerationSha256 ||
            run.Status != receipt.TerminalStatus ||
            run.Status != StatisticReconciliationRunService.FinalRunStatus(
                verdict))
            throw Conflict("RECHECK_FINALIZE_REPLAY_MISMATCH");

        await reviewAppender.AppendOrReplayAsync(
            run.Id,
            receipt.BaseVerdictGenerationId,
            receipt.SuccessorVerdictGenerationId,
            receipt.ReviewSupersessionCommandId,
            actor,
            ct);
        return Result(true, run, verdict);
    }

    private async Task<(StatisticReconciliationReview Verdict, bool Replay)>
        ResolveOrPublishVerdictAsync(
            string reconciliationId,
            StatisticReconciliationRecheckMarker marker,
            StatisticReconciliationFinalVerdictRequest request,
            StatisticReconciliationFinalVerdictDecision decision,
            DateTime now,
            CancellationToken ct)
    {
        var successors = (await verdictBackend.ReadLineageAsync(
                reconciliationId, ct))
            .Where(value => value.SupersedesVerdictGenerationId ==
                marker.BaseVerdictGenerationId)
            .ToArray();
        if (successors.Length > 1)
            throw Conflict("RECHECK_SUCCESSOR_VERDICT_AMBIGUOUS");
        if (successors.Length == 1)
        {
            RequireExactVerdict(successors[0], decision,
                marker.BaseVerdictGenerationId,
                marker.BaseVerdictGenerationHash,
                marker.RemediationBinding);
            return (successors[0], true);
        }

        var remediation = marker.RemediationBinding is null
            ? null
            : StatisticReconciliationTrustedRemediationEvidenceCanonical
                .BuildVerdictRequest(marker.RemediationBinding,
                    request.ActualGenerationId,
                    request.ActualGenerationSha256);
        StatisticReconciliationReview verdict;
        try
        {
            verdict = await verdictPublisher.PublishSupersedingRecheckAsync(
                reconciliationId,
                marker.BaseVerdictGenerationId,
                request,
                remediation,
                now,
                ct);
        }
        catch (StatisticReconciliationFinalVerdictException error)
        {
            throw Conflict(error.Code);
        }
        RequireExactVerdict(verdict, decision,
            marker.BaseVerdictGenerationId,
            marker.BaseVerdictGenerationHash,
            marker.RemediationBinding);
        return (verdict, false);
    }

    private async Task RequireBaseVerdictAsync(
        string reconciliationId,
        StatisticReconciliationRecheckMarker marker,
        CancellationToken ct)
    {
        var previous = await verdictBackend.ReadAsync(
            reconciliationId, marker.BaseVerdictGenerationId, ct);
        if (previous is null)
            throw Conflict("RECHECK_BASE_VERDICT_MISSING");
        StatisticReconciliationFinalVerdictPublisher.ValidateStored(previous);
        if (previous.VerdictGenerationSha256 !=
                marker.BaseVerdictGenerationHash ||
            previous.ActualGenerationId != marker.BaseCurrentGenerationId ||
            previous.ActualGenerationSha256 !=
                marker.BaseCurrentGenerationHash ||
            (previous.RootCauseClass is
                StatisticReconciliationRootCauseClasses.MissingIdentity or
                StatisticReconciliationRootCauseClasses.ExtraIdentity) !=
                (marker.RemediationBinding is not null) ||
            marker.RemediationBinding is not null &&
            (marker.RemediationBinding.RootCauseClass !=
                 previous.RootCauseClass ||
             !previous.MissingOrExtraIdentity))
            throw Conflict("RECHECK_BASE_VERDICT_MISMATCH");
    }

    private async Task<StatisticReconciliationReview>
        RequireExactSuccessorAsync(
            string reconciliationId,
            StatisticReconciliationRecheckMarker marker,
            StatisticReconciliationFinalVerdictDecision decision,
            CancellationToken ct)
    {
        var candidates = (await verdictBackend.ReadLineageAsync(
                reconciliationId, ct))
            .Where(value => value.VerdictGenerationId ==
                marker.SuccessorVerdictGenerationId)
            .ToArray();
        if (candidates.Length != 1)
            throw Conflict("RECHECK_SUCCESSOR_VERDICT_CARDINALITY_INVALID");
        RequireExactVerdict(candidates[0], decision,
            marker.BaseVerdictGenerationId,
            marker.BaseVerdictGenerationHash,
            marker.RemediationBinding);
        if (candidates[0].VerdictGenerationSha256 !=
            marker.SuccessorVerdictGenerationHash)
            throw Conflict("RECHECK_SUCCESSOR_VERDICT_REPLAY_MISMATCH");
        return candidates[0];
    }

    private async Task<StatisticReconciliationRun> ClearMarkerAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationRecheckMarker marker,
        MeResponse actor,
        DateTime now,
        CancellationToken ct)
    {
        if (run.Recheck is null)
            return run;
        var clone = Clone(run);
        clone.Recheck = null;
        var nextRevision = checked(run.StateRevision + 1);
        var stateHash = StatisticReconciliationRunService
            .BuildTrustedFinalizedStateHash(
                clone, clone.Status, nextRevision,
                clone.CurrentGenerationId!, clone.CurrentGenerationHash!,
                clone.DiagnosticCode, clone.FailedAtUtc);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var updated = await context.StatisticReconciliationRuns
            .FindOneAndUpdateAsync(
                fb.Eq(value => value.Id, run.Id) &
                fb.Eq(value => value.StateRevision, run.StateRevision) &
                fb.Eq(value => value.StateHash, run.StateHash) &
                fb.Eq(value => value.ReviewDecisionRevision,
                    run.ReviewDecisionRevision) &
                fb.Eq("recheck.markerId", marker.MarkerId) &
                fb.Eq("recheck.phase",
                    StatisticReconciliationRecheckPhases
                        .ReviewSupersessionPending) &
                fb.Eq(value => value.CurrentGenerationId,
                    marker.SuccessorGenerationId) &
                fb.Eq(value => value.CurrentGenerationHash,
                    marker.SuccessorGenerationHash) &
                fb.Eq("currentRecheckFinalizeReceipt.receiptSha256",
                    run.CurrentRecheckFinalizeReceipt!.ReceiptSha256) &
                fb.Eq(value => value.IsDeleted, false),
                Builders<StatisticReconciliationRun>.Update
                    .Set(value => value.StateRevision, nextRevision)
                    .Set(value => value.StateHash, stateHash)
                    .Set(value => value.Recheck, null)
                    .Set(value => value.LatestWriterUserId, actor.Id)
                    .Set(value => value.UpdatedAtUtc, now)
                    .Set(value => value.UpdatedByUserId, actor.Id),
                new FindOneAndUpdateOptions<StatisticReconciliationRun>
                {
                    ReturnDocument = ReturnDocument.After
                }, ct);
        if (updated is not null)
        {
            StatisticReconciliationRunService
                .RequireActualCaptureReadIntegrity(updated);
            return updated;
        }

        var observed = await LoadAsync(run.Id, run.CandidateChainId, ct);
        if (observed.Recheck is null &&
            observed.CurrentRecheckFinalizeReceipt?.ReceiptSha256 ==
                run.CurrentRecheckFinalizeReceipt!.ReceiptSha256 &&
            observed.CurrentGenerationId == marker.SuccessorGenerationId &&
            observed.CurrentGenerationHash == marker.SuccessorGenerationHash)
            return observed;
        throw Conflict("RECHECK_MARKER_CLEAR_CAS_LOST");
    }

    private static void RequirePending(
        StatisticReconciliationRun run,
        StatisticReconciliationRecheckMarker marker,
        StatisticReconciliationTrustedFinalizeCommand command)
    {
        if (marker.Phase !=
                StatisticReconciliationRecheckPhases.PendingPublished ||
            run.Status != StatisticReconciliationRunStatuses.Queued ||
            run.StateRevision != command.ExpectedStateRevision ||
            run.StateHash != command.ExpectedStateHash ||
            run.GenerationPublishRevision !=
                command.ExpectedGenerationPublishRevision ||
            run.CurrentGenerationId != marker.BaseCurrentGenerationId ||
            run.CurrentGenerationHash != marker.BaseCurrentGenerationHash ||
            run.PendingGenerationId != marker.SuccessorGenerationId ||
            run.PendingGenerationHash != marker.SuccessorGenerationHash ||
            !run.PendingGenerationPublishedAtUtc.HasValue ||
            command.TrustedVerdict.ReconciliationId != run.Id ||
            command.TrustedVerdict.ActualGenerationId !=
                run.PendingGenerationId ||
            command.TrustedVerdict.ActualGenerationSha256 !=
                run.PendingGenerationHash ||
            run.LeaseOwnerId is not null ||
            run.ClaimToken is not null ||
            run.LeaseUntilUtc.HasValue ||
            run.LastHeartbeatAtUtc.HasValue)
            throw Conflict("RECHECK_FINALIZE_CAS_MISMATCH");
    }

    private static void RequirePromoted(
        StatisticReconciliationRun run,
        StatisticReconciliationRecheckMarker marker,
        StatisticReconciliationFinalVerdictRequest request)
    {
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        if (marker.Phase !=
                StatisticReconciliationRecheckPhases
                    .ReviewSupersessionPending ||
            run.Recheck?.MarkerId != marker.MarkerId ||
            run.PendingGenerationId is not null ||
            run.PendingGenerationHash is not null ||
            run.PendingGenerationPublishedAtUtc.HasValue ||
            run.CurrentGenerationId != marker.SuccessorGenerationId ||
            run.CurrentGenerationHash != marker.SuccessorGenerationHash ||
            request.ReconciliationId != run.Id ||
            request.ActualGenerationId != run.CurrentGenerationId ||
            request.ActualGenerationSha256 != run.CurrentGenerationHash ||
            run.Status != run.CurrentRecheckFinalizeReceipt?.TerminalStatus)
            throw Conflict("RECHECK_PROMOTED_STATE_MISMATCH");
    }

    private static void RequireReceiptMatches(
        StatisticReconciliationRecheckFinalizeReceipt receipt,
        StatisticReconciliationRecheckMarker marker,
        StatisticReconciliationReview verdict,
        StatisticReconciliationRun run)
    {
        if (!StatisticReconciliationRunService
                .HasValidRecheckFinalizeReceipt(run) ||
            receipt.MarkerId != marker.MarkerId ||
            receipt.BeginCommandId != marker.BeginCommandId ||
            receipt.BeginRequestHash != marker.BeginRequestHash ||
            receipt.BaseActualGenerationId !=
                marker.BaseCurrentGenerationId ||
            receipt.BaseActualGenerationSha256 !=
                marker.BaseCurrentGenerationHash ||
            receipt.BaseVerdictGenerationId !=
                marker.BaseVerdictGenerationId ||
            receipt.BaseVerdictGenerationSha256 !=
                marker.BaseVerdictGenerationHash ||
            receipt.SuccessorActualGenerationId !=
                marker.SuccessorGenerationId ||
            receipt.SuccessorActualGenerationSha256 !=
                marker.SuccessorGenerationHash ||
            receipt.SuccessorVerdictGenerationId !=
                verdict.VerdictGenerationId ||
            receipt.SuccessorVerdictGenerationSha256 !=
                verdict.VerdictGenerationSha256 ||
            receipt.ReviewSupersessionCommandId !=
                marker.ReviewSupersessionCommandId ||
            receipt.CaptureBindingSha256 !=
                marker.CaptureBinding.BindingSha256 ||
            receipt.RemediationBinding?.BindingSha256 !=
                marker.RemediationBinding?.BindingSha256 ||
            receipt.RemediationSemanticSha256 !=
                verdict.Remediation?.RemediationSemanticSha256)
            throw Conflict("RECHECK_FINALIZE_RECEIPT_MISMATCH");
    }

    private static void RequireExactVerdict(
        StatisticReconciliationReview value,
        StatisticReconciliationFinalVerdictDecision decision,
        string baseVerdictGenerationId,
        string baseVerdictGenerationSha256,
        StatisticReconciliationRecheckRemediationBinding?
            remediationBinding)
    {
        StatisticReconciliationFinalVerdictPublisher.ValidateStored(value);
        if (!Matches(value, decision) ||
            value.SupersedesVerdictGenerationId !=
                baseVerdictGenerationId ||
            value.SupersedesVerdictGenerationSha256 !=
                baseVerdictGenerationSha256 ||
            !StatisticReconciliationTrustedRemediationEvidenceCanonical
                .MatchesStored(remediationBinding, value.Remediation,
                    decision.ActualGenerationId,
                    decision.ActualGenerationSha256))
            throw Conflict("RECHECK_SUCCESSOR_VERDICT_REPLAY_MISMATCH");
    }

    private static StatisticReconciliationFinalVerdictDecision Evaluate(
        StatisticReconciliationFinalVerdictRequest request)
    {
        try
        {
            return new StatisticReconciliationFinalVerdictEvaluator()
                .Evaluate(request);
        }
        catch (StatisticReconciliationFinalVerdictException error)
        {
            throw Conflict(error.Code);
        }
    }

    private static bool Matches(
        StatisticReconciliationReview value,
        StatisticReconciliationFinalVerdictDecision decision)
        => value.ReconciliationId == decision.ReconciliationId &&
           value.ComparisonBindingSha256 ==
               decision.ComparisonBindingSha256 &&
           value.ExpectedGenerationId == decision.ExpectedGenerationId &&
           value.ExpectedGenerationSha256 ==
               decision.ExpectedGenerationSha256 &&
           value.ActualGenerationId == decision.ActualGenerationId &&
           value.ActualGenerationSha256 ==
               decision.ActualGenerationSha256 &&
           value.DeltaManifestSha256 == decision.DeltaManifestSha256 &&
           value.AuthorizationEvidenceSha256 ==
               decision.AuthorizationEvidenceSha256 &&
           value.FreshnessAssessmentSha256 ==
               decision.FreshnessAssessmentSha256 &&
           value.RootCauseClassificationSha256 ==
               decision.RootCauseClassificationSha256 &&
           value.RootCauseClass == decision.RootCauseClass &&
           value.FailureKind == decision.FailureKind &&
           value.FailureEvidenceSha256 ==
               decision.FailureEvidenceSha256 &&
           value.Verdict == decision.Verdict &&
           value.CompleteEvidence == decision.CompleteEvidence &&
           value.AllRequiredLayersZero ==
               decision.AllRequiredLayersZero &&
           value.MissingOrExtraIdentity ==
               decision.MissingOrExtraIdentity &&
           value.UnknownBlocksCloseout ==
               decision.UnknownBlocksCloseout &&
           value.CloseoutAllowed == decision.CloseoutAllowed &&
           value.Signable == decision.Signable;

    private async Task<StatisticReconciliationRun> LoadAsync(
        string id, string chainId, CancellationToken ct)
        => await context.StatisticReconciliationRuns.Find(value =>
                value.Id == id && value.CandidateChainId == chainId &&
                !value.IsDeleted)
            .FirstOrDefaultAsync(ct) ?? throw NotFound();

    private static StatisticReconciliationRun Clone(
        StatisticReconciliationRun run)
        => BsonSerializer.Deserialize<StatisticReconciliationRun>(
            run.ToBson());

    private static StatisticReconciliationTrustedFinalizeResult Result(
        bool isReplay,
        StatisticReconciliationRun run,
        StatisticReconciliationReview verdict)
        => new(
            isReplay, run.Id, run.Status, run.StateRevision,
            run.StateHash, run.GenerationPublishRevision,
            run.CurrentGenerationId!, run.CurrentGenerationHash!, verdict);

    private static DateTime MongoUtc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
        return new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
    }

    private static AppException Conflict(string reason) => new(
        AppErrorCode.STAT_RECONCILIATION_JOB_CONFLICT,
        new { reason, writes = 0 });

    private static AppException NotFound() => new(
        AppErrorCode.STAT_RECONCILIATION_NOT_FOUND,
        new { writes = 0 });
}