using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    public async Task<StatisticReconciliationBeginRecheckResponse>
        BeginRecheckAsync(
            string workId,
            string scopeAssignmentId,
            string reconciliationId,
            Stream body,
            MeResponse actor,
            CancellationToken ct = default)
    {
        // Scope authorization deliberately precedes parsing the command and
        // every reconciliation/verdict existence read.
        var scope = await AuthorizeScopeAsync(workId, scopeAssignmentId,
            actor, ct);
        var bodyElement = await StatisticReconciliationBoundedJsonBody
            .ReadAsync(body, ct);
        var request = StatisticReconciliationCanonicalJson.DeserializeStrict<
            StatisticReconciliationBeginRecheckRequest>(bodyElement);
        var commandId = NormalizeCommandId(request.CommandId);
        if (request.ExpectedStateRevision < 1 ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                request.ExpectedStateHash))
            throw RequestInvalid("expectedStateHash", "CAS_INVALID");

        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.RecheckBegin);
        reconciliationId = NormalizeHiddenObjectId(reconciliationId,
            "reconciliationId", actor);
        var run = await _ctx.StatisticReconciliationRuns.Find(value =>
                value.Id == reconciliationId &&
                value.WorkId == scope.WorkId &&
                value.ScopeAssignmentId == scope.Id &&
                value.CandidateChainId == binding.ChainId &&
                !value.IsDeleted)
            .FirstOrDefaultAsync(ct) ?? throw NotFound();
        RequireReadIntegrity(run);

        var command = new StatisticReconciliationBeginRecheckCommand(
            commandId, request.ExpectedStateRevision,
            request.ExpectedStateHash);
        var durableReceipt = FindRecheckBeginReceipt(run, actor.Id,
            commandId);
        if (durableReceipt is not null)
        {
            if (durableReceipt.ExpectedStateRevision !=
                    request.ExpectedStateRevision ||
                durableReceipt.ExpectedStateHash !=
                    request.ExpectedStateHash)
                throw JobConflict("RECHECK_BEGIN_REPLAY_MISMATCH");
            return BeginResponse(true, run, durableReceipt.MarkerId);
        }
        if ((run.RecheckBeginReceipts ?? []).Count >=
            MaxRecheckBeginReceipts)
            throw JobConflict("RECHECK_BEGIN_RECEIPT_LIMIT_REACHED");
        if (run.Recheck is { } active)
        {
            StatisticReconciliationRecheckCanonical.RequireValidMarker(active);
            var replayHash = StatisticReconciliationRecheckCanonical
                .BeginRequestHash(run.Id, actor.Id, command,
                    active.BaseTerminalStatus,
                    active.BaseCurrentGenerationId,
                    active.BaseCurrentGenerationHash,
                    active.BaseVerdictGenerationId,
                    active.BaseVerdictGenerationHash);
            if (active.BeginActorUserId == actor.Id &&
                active.BeginCommandId == commandId &&
                active.BeginRequestHash == replayHash)
                return BeginResponse(true, run, active);
            throw JobConflict("RECHECK_ALREADY_ACTIVE");
        }

        if (!StatisticReconciliationRecheckTerminalStatuses.All.Contains(
                run.Status) ||
            run.StateRevision != request.ExpectedStateRevision ||
            run.StateHash != request.ExpectedStateHash ||
            run.CurrentGenerationId is null ||
            run.CurrentGenerationHash is null ||
            run.PendingGenerationId is not null ||
            run.PendingGenerationHash is not null ||
            run.PendingGenerationPublishedAtUtc.HasValue ||
            run.LeaseOwnerId is not null || run.ClaimToken is not null ||
            run.LeaseUntilUtc.HasValue || run.LastHeartbeatAtUtc.HasValue)
            throw RevisionConflict("RECHECK_BEGIN_CAS_MISMATCH");

        var verdicts = (await new StatisticReconciliationReviewMongoBackend(
                _ctx).ReadLineageAsync(run.Id, ct))
            .Where(value =>
                value.ActualGenerationId == run.CurrentGenerationId &&
                value.ActualGenerationSha256 == run.CurrentGenerationHash)
            .ToArray();
        if (verdicts.Length != 1)
            throw JobConflict("RECHECK_BASE_VERDICT_CARDINALITY_INVALID");
        var baseVerdict = verdicts[0];
        StatisticReconciliationFinalVerdictPublisher.ValidateStored(
            baseVerdict);
        if (FinalRunStatus(baseVerdict) != run.Status)
            throw JobConflict("RECHECK_BASE_STATUS_VERDICT_MISMATCH");
        var matchedBase = IsApprovedMatchedRecheckBase(run, baseVerdict);
        var identityBase = IsIdentityRemediationBase(run, baseVerdict);
        var freshnessBase = IsFreshnessRecoveryRecheckBase(run, baseVerdict);
        if (!matchedBase && !identityBase && !freshnessBase)
            throw JobConflict(
                "RECHECK_BASE_NOT_ELIGIBLE");

        var captureBinding = await ResolveRecheckCaptureBindingAsync(
            run, scope, actor, ct);
        if (freshnessBase)
            await RequireFreshnessRecoverySuccessorAsync(run, captureBinding, ct);
        StatisticReconciliationRecheckRemediationBinding?
            remediationBinding = null;
        if (identityBase)
        {
            var successorP9 = await _ctx.WorkReportStatisticRebuildJobs
                .Find(value => value.Id == captureBinding.P9RunId &&
                               value.GenerationId ==
                                   captureBinding.P9GenerationId &&
                               value.GenerationHash ==
                                   captureBinding.P9GenerationHash &&
                               !value.IsDeleted)
                .FirstOrDefaultAsync(ct) ??
                throw JobConflict("RECHECK_REMEDIATION_P9_MISSING");
            var evidence = await new
                StatisticReconciliationTrustedRemediationEvidenceStore(_ctx)
                .ResolveAsync(run, baseVerdict, captureBinding,
                    successorP9, ct);
            if (evidence is null)
                throw JobConflict(
                    "RECHECK_REMEDIATION_EVIDENCE_REQUIRED");
            remediationBinding =
                StatisticReconciliationTrustedRemediationEvidenceCanonical
                    .ToBinding(evidence);
            captureBinding.RemediationEvidenceSha256 =
                evidence.EvidenceSha256;
            StatisticReconciliationRecheckCaptureBindingCanonical
                .Refresh(captureBinding);
            StatisticReconciliationRecheckCaptureBindingCanonical
                .RequireValid(captureBinding);
        }
        var now = MongoUtcNow();
        var initialDeadlineAtUtc =
            run.InitialDeadlineAtUtc ?? run.DeadlineAtUtc;
        var marker = StatisticReconciliationRecheckCanonical.NewMarker(
            run.Id, actor.Id, command, run.Status,
            run.CurrentGenerationId, run.CurrentGenerationHash,
            baseVerdict.VerdictGenerationId,
            baseVerdict.VerdictGenerationSha256, captureBinding, now,
            remediationBinding);
        var receipt = BuildRecheckBeginReceipt(
            run, actor.Id, commandId, request.ExpectedStateRevision,
            request.ExpectedStateHash, marker.BeginRequestHash,
            marker.MarkerId, checked(run.StateRevision + 1), now);
        var nextReceipts = (run.RecheckBeginReceipts ?? [])
            .Append(receipt).ToArray();
        var receiptHistoryHash =
            BuildRecheckBeginReceiptHistoryHash(nextReceipts);
        var nextState = StateOf(run) with
        {
            Status = StatisticReconciliationRunStatuses.Queued,
            StateRevision = checked(run.StateRevision + 1),
            RetryCount = 0,
            NextRetryAtUtc = now,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DeadlineAtUtc = now.Add(_fixtureSla),
            DiagnosticCode = null,
            Recheck = marker,
            RecheckBeginReceiptHistoryHash = receiptHistoryHash,
            CancelledAtUtc = null,
            FailedAtUtc = null
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var updated = await _ctx.StatisticReconciliationRuns
            .FindOneAndUpdateAsync(
                fb.Eq(value => value.Id, run.Id) &
                fb.Eq(value => value.CandidateChainId, binding.ChainId) &
                fb.Eq(value => value.Status, run.Status) &
                fb.Eq(value => value.StateRevision, run.StateRevision) &
                fb.Eq(value => value.StateHash, run.StateHash) &
                fb.Eq(value => value.CurrentGenerationId,
                    run.CurrentGenerationId) &
                fb.Eq(value => value.CurrentGenerationHash,
                    run.CurrentGenerationHash) &
                fb.Eq(value => value.InitialDeadlineAtUtc,
                    run.InitialDeadlineAtUtc) &
                fb.Eq(value => value.PendingGenerationId, null) &
                fb.Eq(value => value.PendingGenerationHash, null) &
                fb.Eq(value => value.Recheck, null) &
                fb.Eq(value => value.RecheckBeginReceiptHistoryHash,
                    run.RecheckBeginReceiptHistoryHash) &
                fb.Eq(value => value.IsDeleted, false),
                Builders<StatisticReconciliationRun>.Update
                    .Set(value => value.Status, nextState.Status)
                    .Set(value => value.StateRevision,
                        nextState.StateRevision)
                    .Set(value => value.StateHash, stateHash)
                    .Set(value => value.RetryCount, 0)
                    .Set(value => value.NextRetryAtUtc, now)
                    .Set(value => value.InitialDeadlineAtUtc,
                        initialDeadlineAtUtc)
                    .Set(value => value.DeadlineAtUtc,
                        nextState.DeadlineAtUtc)
                    .Set(value => value.DiagnosticCode, null)
                    .Set(value => value.FailedAtUtc, null)
                    .Set(value => value.CancelledAtUtc, null)
                    .Set(value => value.Recheck, marker)
                    .Set(value => value.RecheckBeginReceiptHistoryHash,
                        receiptHistoryHash)
                    .Push(value => value.RecheckBeginReceipts, receipt)
                    .Set(value => value.LatestWriterUserId, actor.Id)
                    .Set(value => value.UpdatedAtUtc, now)
                    .Set(value => value.UpdatedByUserId, actor.Id),
                new FindOneAndUpdateOptions<StatisticReconciliationRun>
                {
                    ReturnDocument = ReturnDocument.After
                }, ct);
        if (updated is null)
        {
            var observed = await _ctx.StatisticReconciliationRuns.Find(value =>
                    value.Id == run.Id &&
                    value.CandidateChainId == binding.ChainId &&
                    !value.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (observed is not null)
            {
                RequireReadIntegrity(observed);
                var racedReceipt = FindRecheckBeginReceipt(
                    observed, actor.Id, commandId);
                if (racedReceipt is not null &&
                    racedReceipt.ExpectedStateRevision ==
                        request.ExpectedStateRevision &&
                    racedReceipt.ExpectedStateHash ==
                        request.ExpectedStateHash)
                    return BeginResponse(true, observed,
                        racedReceipt.MarkerId);
            }
            throw RevisionConflict("RECHECK_BEGIN_CAS_LOST");
        }
        RequireReadIntegrity(updated);
        return BeginResponse(false, updated, marker);
    }

    public async Task<StatisticReconciliationWorkerLeaseResponse>
        ClaimRecheckAsync(
            string reconciliationId,
            Stream body,
            MeResponse actor,
            CancellationToken ct = default)
    {
        Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(actor);
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.RecheckClaim);
        var bodyElement = await StatisticReconciliationBoundedJsonBody
            .ReadAsync(body, ct);
        var request = StatisticReconciliationCanonicalJson.DeserializeStrict<
            StatisticReconciliationRecheckClaimRequest>(bodyElement);
        var workerId = RequiredToken(request.WorkerId, "workerId", 128);
        var run = await LoadWorkerRunAsync(reconciliationId,
            binding.ChainId, actor, ct);
        RequireReadIntegrity(run);
        var now = MongoUtcNow();
        var marker = run.Recheck is null
            ? throw JobConflict("RECHECK_MARKER_REQUIRED")
            : StatisticReconciliationRecheckCanonical.Clone(run.Recheck);
        StatisticReconciliationRecheckCanonical.RequireValidMarker(marker);
        if (marker.Phase ==
                StatisticReconciliationRecheckPhases.CaptureRunning &&
            run.Status == StatisticReconciliationRunStatuses.Running &&
            run.LeaseUntilUtc <= now)
        {
            var retryCount = checked(run.RetryCount + 1);
            var retryAt = now.Add(RetryDelay(retryCount));
            if (retryCount >= run.MaxRetryCount ||
                retryAt >= run.DeadlineAtUtc)
            {
                await AbortExpiredRecheckToBaseAsync(
                    run, marker, actor.Id, now, retryCount,
                    requireRunningLease: true, ct);
                throw JobConflict("RECHECK_EXPIRED_ABORTED_TO_BASE");
            }

            await RequeueExpiredRecheckAsync(
                run, marker, actor.Id, now, retryCount, retryAt, ct);
            throw JobConflict("RECHECK_EXPIRED_REQUEUED");
        }
        if (marker.Phase ==
                StatisticReconciliationRecheckPhases.ReadyToClaim &&
            run.Status == StatisticReconciliationRunStatuses.Queued &&
            run.DeadlineAtUtc <= now)
        {
            await AbortExpiredRecheckToBaseAsync(
                run, marker, actor.Id, now, run.RetryCount,
                requireRunningLease: false, ct);
            throw JobConflict("RECHECK_DEADLINE_ABORTED_TO_BASE");
        }
        if (marker.Phase !=
                StatisticReconciliationRecheckPhases.ReadyToClaim ||
            run.Status != StatisticReconciliationRunStatuses.Queued ||
            run.CurrentGenerationId != marker.BaseCurrentGenerationId ||
            run.CurrentGenerationHash != marker.BaseCurrentGenerationHash ||
            run.PendingGenerationId is not null ||
            run.PendingGenerationHash is not null ||
            run.NextRetryAtUtc > now || run.DeadlineAtUtc <= now)
            throw JobConflict("RECHECK_NOT_CLAIMABLE");

        var claimToken = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var leaseUntil = now.Add(_leaseDuration);
        if (leaseUntil > run.DeadlineAtUtc)
            leaseUntil = run.DeadlineAtUtc;
        marker.Phase =
            StatisticReconciliationRecheckPhases.CaptureRunning;
        StatisticReconciliationRecheckCanonical.RefreshMarkerHash(marker);
        var nextState = StateOf(run) with
        {
            Status = StatisticReconciliationRunStatuses.Running,
            StateRevision = checked(run.StateRevision + 1),
            NextRetryAtUtc = null,
            LeaseOwnerId = workerId,
            ClaimToken = claimToken,
            LeaseUntilUtc = leaseUntil,
            LastHeartbeatAtUtc = now,
            DiagnosticCode = null,
            Recheck = marker
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var updated = await _ctx.StatisticReconciliationRuns
            .FindOneAndUpdateAsync(
                fb.Eq(value => value.Id, run.Id) &
                fb.Eq(value => value.CandidateChainId, binding.ChainId) &
                fb.Eq(value => value.Status,
                    StatisticReconciliationRunStatuses.Queued) &
                fb.Eq(value => value.StateRevision, run.StateRevision) &
                fb.Eq(value => value.StateHash, run.StateHash) &
                fb.Eq(value => value.CurrentGenerationId,
                    marker.BaseCurrentGenerationId) &
                fb.Eq(value => value.CurrentGenerationHash,
                    marker.BaseCurrentGenerationHash) &
                fb.Eq(value => value.PendingGenerationId, null) &
                fb.Eq(value => value.PendingGenerationHash, null) &
                fb.Eq("recheck.markerId", marker.MarkerId) &
                fb.Eq("recheck.markerStateHash",
                    run.Recheck!.MarkerStateHash) &
                fb.Eq("recheck.phase",
                    StatisticReconciliationRecheckPhases.ReadyToClaim) &
                fb.Gt(value => value.DeadlineAtUtc, now) &
                fb.Eq(value => value.IsDeleted, false),
                Builders<StatisticReconciliationRun>.Update
                    .Set(value => value.Status, nextState.Status)
                    .Set(value => value.StateRevision,
                        nextState.StateRevision)
                    .Set(value => value.StateHash, stateHash)
                    .Set(value => value.NextRetryAtUtc, null)
                    .Set(value => value.LeaseOwnerId, workerId)
                    .Set(value => value.ClaimToken, claimToken)
                    .Set(value => value.LeaseUntilUtc, leaseUntil)
                    .Set(value => value.LastHeartbeatAtUtc, now)
                    .Set(value => value.DiagnosticCode, null)
                    .Set(value => value.Recheck, marker)
                    .Set(value => value.LatestWriterUserId, actor.Id)
                    .Set(value => value.UpdatedAtUtc, now)
                    .Set(value => value.UpdatedByUserId, actor.Id),
                new FindOneAndUpdateOptions<StatisticReconciliationRun>
                {
                    ReturnDocument = ReturnDocument.After
                }, ct);
        if (updated is null)
            throw JobConflict("RECHECK_CLAIM_CAS_LOST");
        RequireReadIntegrity(updated);
        return new()
        {
            WorkerId = workerId,
            ClaimToken = claimToken,
            LeaseUntilUtc = leaseUntil,
            Run = ToDetail(updated)
        };
    }

    public async Task<StatisticReconciliationDetailResponse>
        HeartbeatRecheckAsync(
            string reconciliationId,
            Stream body,
            MeResponse actor,
            CancellationToken ct = default)
    {
        Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(actor);
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities
                .SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.WorkerHeartbeat);
        var element = await StatisticReconciliationBoundedJsonBody
            .ReadAsync(body, ct);
        var request = StatisticReconciliationCanonicalJson
            .DeserializeStrict<StatisticReconciliationWorkerFenceRequest>(
                element);
        await RequireActiveRecheckFenceTargetAsync(
            reconciliationId, binding.ChainId, actor, request.WorkerId,
            request.ClaimToken, ct);
        return await HeartbeatAsync(
            reconciliationId, request, actor, ct);
    }

    public async Task<StatisticReconciliationDetailResponse>
        FailRecheckAsync(
            string reconciliationId,
            Stream body,
            MeResponse actor,
            CancellationToken ct = default)
    {
        Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(actor);
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities
                .SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.WorkerRetry);
        var element = await StatisticReconciliationBoundedJsonBody
            .ReadAsync(body, ct);
        var request = StatisticReconciliationCanonicalJson
            .DeserializeStrict<StatisticReconciliationWorkerFailRequest>(
                element);
        await RequireActiveRecheckFenceTargetAsync(
            reconciliationId, binding.ChainId, actor, request.WorkerId,
            request.ClaimToken, ct);
        return await FailAsync(reconciliationId, request, actor, ct);
    }

    private async Task RequireActiveRecheckFenceTargetAsync(
        string reconciliationId,
        string chainId,
        MeResponse actor,
        string workerId,
        string claimToken,
        CancellationToken ct)
    {
        workerId = RequiredToken(workerId, "workerId", 128);
        claimToken = RequiredToken(claimToken, "claimToken", 128);
        var run = await LoadWorkerRunAsync(
            reconciliationId, chainId, actor, ct);
        RequireReadIntegrity(run);
        if (run.Recheck?.Phase !=
                StatisticReconciliationRecheckPhases.CaptureRunning ||
            run.Status != StatisticReconciliationRunStatuses.Running ||
            run.LeaseOwnerId != workerId ||
            run.ClaimToken != claimToken)
            throw JobConflict("RECHECK_ACTIVE_FENCE_REQUIRED");
    }
    private async Task RequeueExpiredRecheckAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationRecheckMarker marker,
        string writerUserId,
        DateTime now,
        int retryCount,
        DateTime retryAt,
        CancellationToken ct)
    {
        var nextMarker =
            StatisticReconciliationRecheckCanonical.Clone(marker);
        nextMarker.Phase =
            StatisticReconciliationRecheckPhases.ReadyToClaim;
        StatisticReconciliationRecheckCanonical.RefreshMarkerHash(nextMarker);
        var nextState = StateOf(run) with
        {
            Status = StatisticReconciliationRunStatuses.Queued,
            StateRevision = checked(run.StateRevision + 1),
            RetryCount = retryCount,
            NextRetryAtUtc = retryAt,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = "P10_RECHECK_LEASE_EXPIRED",
            Recheck = nextMarker,
            FailedAtUtc = null
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var result = await _ctx.StatisticReconciliationRuns.UpdateOneAsync(
            fb.Eq(value => value.Id, run.Id) &
            fb.Eq(value => value.CandidateChainId,
                run.CandidateChainId) &
            fb.Eq(value => value.Status,
                StatisticReconciliationRunStatuses.Running) &
            fb.Eq(value => value.StateRevision, run.StateRevision) &
            fb.Eq(value => value.StateHash, run.StateHash) &
            fb.Eq(value => value.CurrentGenerationId,
                marker.BaseCurrentGenerationId) &
            fb.Eq(value => value.CurrentGenerationHash,
                marker.BaseCurrentGenerationHash) &
            fb.Eq(value => value.PendingGenerationId, null) &
            fb.Eq(value => value.PendingGenerationHash, null) &
            fb.Eq("recheck.markerId", marker.MarkerId) &
            fb.Eq("recheck.markerStateHash", marker.MarkerStateHash) &
            fb.Eq("recheck.phase",
                StatisticReconciliationRecheckPhases.CaptureRunning) &
            fb.Lte(value => value.LeaseUntilUtc, now) &
            fb.Gt(value => value.DeadlineAtUtc, now) &
            fb.Eq(value => value.IsDeleted, false),
            Builders<StatisticReconciliationRun>.Update
                .Set(value => value.Status, nextState.Status)
                .Set(value => value.StateRevision,
                    nextState.StateRevision)
                .Set(value => value.StateHash, stateHash)
                .Set(value => value.RetryCount, retryCount)
                .Set(value => value.NextRetryAtUtc, retryAt)
                .Set(value => value.LeaseOwnerId, null)
                .Set(value => value.ClaimToken, null)
                .Set(value => value.LeaseUntilUtc, null)
                .Set(value => value.LastHeartbeatAtUtc, null)
                .Set(value => value.DiagnosticCode,
                    nextState.DiagnosticCode)
                .Set(value => value.Recheck, nextMarker)
                .Set(value => value.FailedAtUtc, null)
                .Set(value => value.LatestWriterUserId,
                    writerUserId)
                .Set(value => value.UpdatedAtUtc, now)
                .Set(value => value.UpdatedByUserId,
                    writerUserId),
            cancellationToken: ct);
        if (result.ModifiedCount != 1)
            throw JobConflict("RECHECK_EXPIRED_REQUEUE_CAS_LOST");
    }

    private async Task AbortExpiredRecheckToBaseAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationRecheckMarker marker,
        string writerUserId,
        DateTime now,
        int retryCount,
        bool requireRunningLease,
        CancellationToken ct)
    {
        var failedAt = marker.BaseTerminalStatus ==
            StatisticReconciliationRunStatuses.Failed
                ? now
                : (DateTime?)null;
        var nextState = StateOf(run) with
        {
            Status = marker.BaseTerminalStatus,
            StateRevision = checked(run.StateRevision + 1),
            RetryCount = retryCount,
            NextRetryAtUtc = null,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = null,
            PendingGenerationId = null,
            PendingGenerationHash = null,
            PendingGenerationPublishedAtUtc = null,
            CurrentGenerationId = marker.BaseCurrentGenerationId,
            CurrentGenerationHash = marker.BaseCurrentGenerationHash,
            Recheck = null,
            FailedAtUtc = failedAt
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var phase = requireRunningLease
            ? StatisticReconciliationRecheckPhases.CaptureRunning
            : StatisticReconciliationRecheckPhases.ReadyToClaim;
        var status = requireRunningLease
            ? StatisticReconciliationRunStatuses.Running
            : StatisticReconciliationRunStatuses.Queued;
        var filter =
            fb.Eq(value => value.Id, run.Id) &
            fb.Eq(value => value.CandidateChainId,
                run.CandidateChainId) &
            fb.Eq(value => value.Status, status) &
            fb.Eq(value => value.StateRevision, run.StateRevision) &
            fb.Eq(value => value.StateHash, run.StateHash) &
            fb.Eq(value => value.CurrentGenerationId,
                marker.BaseCurrentGenerationId) &
            fb.Eq(value => value.CurrentGenerationHash,
                marker.BaseCurrentGenerationHash) &
            fb.Eq(value => value.PendingGenerationId, null) &
            fb.Eq(value => value.PendingGenerationHash, null) &
            fb.Eq("recheck.markerId", marker.MarkerId) &
            fb.Eq("recheck.markerStateHash", marker.MarkerStateHash) &
            fb.Eq("recheck.phase", phase) &
            fb.Eq(value => value.IsDeleted, false);
        filter &= requireRunningLease
            ? fb.Lte(value => value.LeaseUntilUtc, now)
            : fb.Lte(value => value.DeadlineAtUtc, now);
        var result = await _ctx.StatisticReconciliationRuns.UpdateOneAsync(
            filter,
            Builders<StatisticReconciliationRun>.Update
                .Set(value => value.Status, nextState.Status)
                .Set(value => value.StateRevision,
                    nextState.StateRevision)
                .Set(value => value.StateHash, stateHash)
                .Set(value => value.RetryCount, retryCount)
                .Set(value => value.NextRetryAtUtc, null)
                .Set(value => value.LeaseOwnerId, null)
                .Set(value => value.ClaimToken, null)
                .Set(value => value.LeaseUntilUtc, null)
                .Set(value => value.LastHeartbeatAtUtc, null)
                .Set(value => value.DiagnosticCode, null)
                .Set(value => value.PendingGenerationId, null)
                .Set(value => value.PendingGenerationHash, null)
                .Set(value =>
                    value.PendingGenerationPublishedAtUtc, null)
                .Set(value => value.Recheck, null)
                .Set(value => value.FailedAtUtc, failedAt)
                .Set(value => value.LatestWriterUserId,
                    writerUserId)
                .Set(value => value.UpdatedAtUtc, now)
                .Set(value => value.UpdatedByUserId,
                    writerUserId),
            cancellationToken: ct);
        if (result.ModifiedCount != 1)
            throw JobConflict("RECHECK_EXPIRED_ABORT_CAS_LOST");
    }
    private static StatisticReconciliationBeginRecheckResponse BeginResponse(
        bool replay,
        StatisticReconciliationRun run,
        StatisticReconciliationRecheckMarker marker)
        => BeginResponse(replay, run, marker.MarkerId);

    private static StatisticReconciliationBeginRecheckResponse BeginResponse(
        bool replay,
        StatisticReconciliationRun run,
        string markerId)
        => new()
        {
            IsReplay = replay,
            ReconciliationId = run.Id,
            RecheckMarkerId = markerId,
            Status = run.Status,
            StateRevision = run.StateRevision,
            StateHash = run.StateHash
        };

    internal static string FinalRunStatus(
        StatisticReconciliationReview verdict)
        => verdict.RootCauseClass ==
           StatisticReconciliationRootCauseClasses.Freshness
            ? StatisticReconciliationRunStatuses.Stale
            : verdict.Verdict switch
            {
                StatisticReconciliationFinalVerdicts.Matched =>
                    StatisticReconciliationRunStatuses.Matched,
                StatisticReconciliationFinalVerdicts.Mismatched =>
                    StatisticReconciliationRunStatuses.Mismatched,
                _ => StatisticReconciliationRunStatuses.Failed
            };
}
