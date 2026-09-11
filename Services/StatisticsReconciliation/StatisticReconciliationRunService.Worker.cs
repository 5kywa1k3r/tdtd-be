using System.Security.Cryptography;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    public async Task<StatisticReconciliationWorkerLeaseResponse?> ClaimAsync(
        StatisticReconciliationWorkerClaimRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(actor);
        if (request is null)
            throw RequestInvalid("body", "BODY_REQUIRED");
        var workerId = RequiredToken(request.WorkerId, "workerId", 128);
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.WorkerClaim);
        var now = MongoUtcNow();
        await FailExpiredRunsAsync(binding.ChainId, actor.Id, now, ct);

        for (var attempt = 0; attempt < 32; attempt++)
        {
            now = MongoUtcNow();
            var fb = Builders<StatisticReconciliationRun>.Filter;
            var eligible = fb.Eq(x => x.CandidateChainId, binding.ChainId) &
                           fb.Eq(x => x.IsDeleted, false) &
                           fb.Eq(x => x.PendingGenerationId, null) &
                           fb.Eq(x => x.PendingGenerationHash, null) &
                           fb.Eq(x => x.PendingGenerationPublishedAtUtc, null) &
                           fb.Eq(x => x.CurrentGenerationId, null) &
                           fb.Eq(x => x.CurrentGenerationHash, null) &
                           fb.Gt(x => x.DeadlineAtUtc, now) &
                           ((fb.Eq(x => x.Status, StatisticReconciliationRunStatuses.Queued) &
                             fb.Eq(x => x.LeaseOwnerId, null) &
                             fb.Eq(x => x.ClaimToken, null) &
                             fb.Eq(x => x.LeaseUntilUtc, null) &
                             fb.Eq(x => x.LastHeartbeatAtUtc, null) &
                             (fb.Eq(x => x.NextRetryAtUtc, null) |
                              fb.Lte(x => x.NextRetryAtUtc, now))) |
                            (fb.Eq(x => x.Status, StatisticReconciliationRunStatuses.Running) &
                             fb.Lte(x => x.LeaseUntilUtc, now)));
            var candidate = await _ctx.StatisticReconciliationRuns
                .Find(eligible)
                .SortBy(x => x.NextRetryAtUtc)
                .ThenBy(x => x.CreatedAtUtc)
                .ThenBy(x => x.Id)
                .FirstOrDefaultAsync(ct);
            if (candidate is null)
                return null;
            RequireReadIntegrity(candidate);

            var expiredLease = string.Equals(
                                   candidate.Status,
                                   StatisticReconciliationRunStatuses.Running,
                                   StringComparison.Ordinal) &&
                               candidate.LeaseUntilUtc <= now;
            if (expiredLease)
            {
                var expiredRetryCount = candidate.RetryCount + 1;
                var retryAt = now.Add(RetryDelay(expiredRetryCount));
                if (expiredRetryCount >= candidate.MaxRetryCount ||
                    retryAt >= candidate.DeadlineAtUtc)
                {
                    await TerminalizeExpiredLeaseAsync(
                        candidate,
                        actor.Id,
                        now,
                        expiredRetryCount,
                        retryAt >= candidate.DeadlineAtUtc,
                        ct);
                }
                else
                {
                    await RequeueExpiredLeaseAsync(
                        candidate,
                        actor.Id,
                        now,
                        expiredRetryCount,
                        retryAt,
                        ct);
                }
                continue;
            }

            var retryCount = candidate.RetryCount;

            var claimToken = Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(32))
                .ToLowerInvariant();
            var leaseUntil = now.Add(_leaseDuration);
            if (leaseUntil > candidate.DeadlineAtUtc)
                leaseUntil = candidate.DeadlineAtUtc;
            var nextState = StateOf(candidate) with
            {
                Status = StatisticReconciliationRunStatuses.Running,
                StateRevision = candidate.StateRevision + 1,
                RetryCount = retryCount,
                NextRetryAtUtc = null,
                LeaseOwnerId = workerId,
                ClaimToken = claimToken,
                LeaseUntilUtc = leaseUntil,
                LastHeartbeatAtUtc = now,
                DiagnosticCode = null
            };
            var stateHash = BuildStateHash(candidate.Id, nextState);
            var claimed = await _ctx.StatisticReconciliationRuns.FindOneAndUpdateAsync(
                fb.Eq(x => x.Id, candidate.Id) &
                fb.Eq(x => x.CandidateChainId, binding.ChainId) &
                fb.Eq(x => x.Status, candidate.Status) &
                fb.Eq(x => x.StateRevision, candidate.StateRevision) &
                fb.Eq(x => x.StateHash, candidate.StateHash) &
                fb.Eq(x => x.PendingGenerationId, null) &
                fb.Eq(x => x.PendingGenerationHash, null) &
                fb.Eq(x => x.PendingGenerationPublishedAtUtc, null) &
                fb.Eq(x => x.CurrentGenerationId, null) &
                fb.Eq(x => x.CurrentGenerationHash, null) &
                fb.Gt(x => x.DeadlineAtUtc, now) &
                fb.Eq(x => x.IsDeleted, false),
                Builders<StatisticReconciliationRun>.Update
                    .Set(x => x.Status, nextState.Status)
                    .Set(x => x.StateRevision, nextState.StateRevision)
                    .Set(x => x.StateHash, stateHash)
                    .Set(x => x.RetryCount, retryCount)
                    .Set(x => x.NextRetryAtUtc, null)
                    .Set(x => x.LeaseOwnerId, workerId)
                    .Set(x => x.ClaimToken, claimToken)
                    .Set(x => x.LeaseUntilUtc, leaseUntil)
                    .Set(x => x.LastHeartbeatAtUtc, now)
                    .Set(x => x.DiagnosticCode, null)
                    .Set(x => x.LatestWriterUserId, actor.Id)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actor.Id),
                new FindOneAndUpdateOptions<StatisticReconciliationRun>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);
            if (claimed is null)
                continue;
            RequireReadIntegrity(claimed);
            return new StatisticReconciliationWorkerLeaseResponse
            {
                WorkerId = workerId,
                ClaimToken = claimToken,
                LeaseUntilUtc = leaseUntil,
                Run = ToDetail(claimed)
            };
        }
        throw JobConflict("CLAIM_CONTENTION_LIMIT");
    }

    public async Task<StatisticReconciliationDetailResponse> HeartbeatAsync(
        string reconciliationId,
        StatisticReconciliationWorkerFenceRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(actor);
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.WorkerHeartbeat);
        var fence = NormalizeFence(request);
        var run = await LoadWorkerRunAsync(reconciliationId, binding.ChainId, actor, ct);
        RequireReadIntegrity(run);
        run = await RefreshTimeoutAsync(run, actor.Id, ct);
        ThrowIfTimedOut(run);
        RequireFence(run, fence);

        var now = MongoUtcNow();
        var leaseUntil = now.Add(_leaseDuration);
        if (leaseUntil > run.DeadlineAtUtc)
            leaseUntil = run.DeadlineAtUtc;
        var nextState = StateOf(run) with
        {
            StateRevision = run.StateRevision + 1,
            LeaseUntilUtc = leaseUntil,
            LastHeartbeatAtUtc = now
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var updated = await _ctx.StatisticReconciliationRuns.FindOneAndUpdateAsync(
            BuildFenceFilter(fb, run, fence, now),
            Builders<StatisticReconciliationRun>.Update
                .Set(x => x.StateRevision, nextState.StateRevision)
                .Set(x => x.StateHash, stateHash)
                .Set(x => x.LeaseUntilUtc, leaseUntil)
                .Set(x => x.LastHeartbeatAtUtc, now)
                .Set(x => x.LatestWriterUserId, actor.Id)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actor.Id),
            new FindOneAndUpdateOptions<StatisticReconciliationRun>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct);
        if (updated is null)
            throw JobConflict("STALE_WORKER_FENCE");
        RequireReadIntegrity(updated);
        return ToDetail(updated);
    }

    public async Task<StatisticReconciliationDetailResponse> FailAsync(
        string reconciliationId,
        StatisticReconciliationWorkerFailRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(actor);
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.WorkerRetry);
        if (request is null)
            throw RequestInvalid("body", "BODY_REQUIRED");
        var fence = new WorkerFence(
            RequiredToken(request.WorkerId, "workerId", 128),
            RequiredToken(request.ClaimToken, "claimToken", 128));
        var failureCode = RequiredToken(request.FailureCode, "failureCode", 128)
            .ToUpperInvariant();
        var run = await LoadWorkerRunAsync(reconciliationId, binding.ChainId, actor, ct);
        RequireReadIntegrity(run);
        run = await RefreshTimeoutAsync(run, actor.Id, ct);
        ThrowIfTimedOut(run);
        RequireFence(run, fence);

        var now = MongoUtcNow();
        var retryCount = run.RetryCount + 1;
        var retryDelay = RetryDelay(retryCount);
        var retryAt = now.Add(retryDelay);
        var terminal = !request.Transient ||
                       retryCount >= run.MaxRetryCount ||
                       retryAt >= run.DeadlineAtUtc;
        if (run.Recheck?.Phase ==
            StatisticReconciliationRecheckPhases.CaptureRunning)
        {
            return await FailActiveRecheckAsync(
                run,
                StatisticReconciliationRecheckCanonical.Clone(run.Recheck),
                fence,
                actor,
                failureCode,
                terminal,
                retryCount,
                retryAt,
                now,
                ct);
        }
        var status = terminal
            ? StatisticReconciliationRunStatuses.Failed
            : StatisticReconciliationRunStatuses.Queued;
        var diagnostic = terminal && retryAt >= run.DeadlineAtUtc
            ? "P10_RECONCILIATION_JOB_TIMEOUT"
            : failureCode;
        var nextState = StateOf(run) with
        {
            Status = status,
            StateRevision = run.StateRevision + 1,
            RetryCount = retryCount,
            NextRetryAtUtc = terminal ? null : retryAt,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = diagnostic,
            FailedAtUtc = terminal ? now : null
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var updated = await _ctx.StatisticReconciliationRuns.FindOneAndUpdateAsync(
            BuildFenceFilter(fb, run, fence, now),
            Builders<StatisticReconciliationRun>.Update
                .Set(x => x.Status, status)
                .Set(x => x.StateRevision, nextState.StateRevision)
                .Set(x => x.StateHash, stateHash)
                .Set(x => x.RetryCount, retryCount)
                .Set(x => x.NextRetryAtUtc, terminal ? null : retryAt)
                .Set(x => x.LeaseOwnerId, null)
                .Set(x => x.ClaimToken, null)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastHeartbeatAtUtc, null)
                .Set(x => x.DiagnosticCode, diagnostic)
                .Set(x => x.FailedAtUtc, terminal ? now : null)
                .Set(x => x.LatestWriterUserId, actor.Id)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actor.Id),
            new FindOneAndUpdateOptions<StatisticReconciliationRun>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct);
        if (updated is null)
            throw JobConflict("STALE_WORKER_FENCE");
        RequireReadIntegrity(updated);
        return ToDetail(updated);
    }

    public async Task<StatisticReconciliationDetailResponse> PublishPendingAsync(
        string reconciliationId,
        StatisticReconciliationPendingPublishRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(actor);
        var binding = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.WorkerPublish);
        if (request is null)
            throw RequestInvalid("body", "BODY_REQUIRED");
        var fence = new WorkerFence(
            RequiredToken(request.WorkerId, "workerId", 128),
            RequiredToken(request.ClaimToken, "claimToken", 128));
        var generationId = request.GenerationId?.Trim();
        var generationHash = request.GenerationHash?.Trim();
        if (!StatisticReconciliationCanonicalJson.IsCanonicalSha256(generationId))
            throw RequestInvalid("generationId", "SHA256_CANONICAL_INVALID");
        if (!StatisticReconciliationCanonicalJson.IsCanonicalSha256(generationHash))
            throw RequestInvalid("generationHash", "SHA256_CANONICAL_INVALID");

        var run = await LoadWorkerRunAsync(reconciliationId, binding.ChainId, actor, ct);
        RequireReadIntegrity(run);
        run = await RefreshTimeoutAsync(run, actor.Id, ct);
        ThrowIfTimedOut(run);

        StatisticReconciliationActualAppendResult? committedGeneration;
        try
        {
            committedGeneration = await
                StatisticReconciliationActualGenerationPublisher
                    .ReadCompleteFromBackendAsync(
                        new StatisticReconciliationActualObservationMongoBackend(_ctx),
                        run.Id,
                        generationId!,
                        ct);
        }
        catch (StatisticReconciliationActualObservationException)
        {
            throw JobConflict("ACTUAL_GENERATION_COMMIT_INVALID");
        }
        if (committedGeneration is null ||
            !string.Equals(
                committedGeneration.GenerationSemanticSha256,
                generationHash,
                StringComparison.Ordinal))
        {
            throw JobConflict("ACTUAL_GENERATION_NOT_COMMITTED");
        }
        if (!StatisticReconciliationActualRunBindingGuard.Matches(
                run,
                committedGeneration.CommittedRunBinding))
        {
            throw JobConflict("ACTUAL_GENERATION_RUN_BINDING_MISMATCH");
        }

        var exactPublishedReplay =
            string.Equals(run.PendingGenerationId, generationId,
                StringComparison.Ordinal) &&
            string.Equals(run.PendingGenerationHash, generationHash,
                StringComparison.Ordinal) &&
            run.PendingGenerationPublishedAtUtc.HasValue &&
            ((run.Recheck is null &&
              run.CurrentGenerationId is null &&
              run.CurrentGenerationHash is null) ||
             (run.Recheck?.Phase ==
                  StatisticReconciliationRecheckPhases.PendingPublished &&
              run.CurrentGenerationId ==
                  run.Recheck.BaseCurrentGenerationId &&
              run.CurrentGenerationHash ==
                  run.Recheck.BaseCurrentGenerationHash &&
              run.Recheck.SuccessorGenerationId == generationId &&
              run.Recheck.SuccessorGenerationHash == generationHash));
        if (exactPublishedReplay)
            return ToDetail(run);

        RequireFence(run, fence);
        if (run.PendingGenerationId is not null ||
            run.PendingGenerationHash is not null)
        {
            throw JobConflict("GENERATION_ALREADY_PUBLISHED");
        }

        StatisticReconciliationRecheckMarker? recheck = null;
        if (run.Recheck is null)
        {
            if (run.CurrentGenerationId is not null ||
                run.CurrentGenerationHash is not null)
                throw JobConflict("GENERATION_ALREADY_PUBLISHED");
        }
        else
        {
            recheck = StatisticReconciliationRecheckCanonical.Clone(
                run.Recheck);
            if (recheck.Phase !=
                    StatisticReconciliationRecheckPhases.CaptureRunning ||
                run.CurrentGenerationId !=
                    recheck.BaseCurrentGenerationId ||
                run.CurrentGenerationHash !=
                    recheck.BaseCurrentGenerationHash)
                throw JobConflict("RECHECK_CAPTURE_MARKER_INVALID");
            if (generationId == recheck.BaseCurrentGenerationId ||
                generationHash == recheck.BaseCurrentGenerationHash)
                throw JobConflict("RECHECK_SUCCESSOR_NOT_DISTINCT");
            recheck.SuccessorGenerationId = generationId;
            recheck.SuccessorGenerationHash = generationHash;
            recheck.Phase =
                StatisticReconciliationRecheckPhases.PendingPublished;
            StatisticReconciliationRecheckCanonical.RefreshMarkerHash(
                recheck);
        }

        var now = MongoUtcNow();
        var nextState = StateOf(run) with
        {
            Status = StatisticReconciliationRunStatuses.Queued,
            StateRevision = run.StateRevision + 1,
            NextRetryAtUtc = null,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = null,
            PendingGenerationId = generationId,
            PendingGenerationHash = generationHash,
            PendingGenerationPublishedAtUtc = now,
            GenerationPublishRevision = run.GenerationPublishRevision + 1,
            Recheck = recheck
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var generationShape = run.Recheck is null
            ? fb.Eq(x => x.CurrentGenerationId, null) &
              fb.Eq(x => x.CurrentGenerationHash, null) &
              fb.Eq(x => x.Recheck, null)
            : fb.Eq(x => x.CurrentGenerationId,
                  run.Recheck.BaseCurrentGenerationId) &
              fb.Eq(x => x.CurrentGenerationHash,
                  run.Recheck.BaseCurrentGenerationHash) &
              fb.Eq("recheck.markerId", run.Recheck.MarkerId) &
              fb.Eq("recheck.markerStateHash",
                  run.Recheck.MarkerStateHash) &
              fb.Eq("recheck.phase",
                  StatisticReconciliationRecheckPhases.CaptureRunning);
        var updated = await _ctx.StatisticReconciliationRuns.FindOneAndUpdateAsync(
            BuildFenceFilter(fb, run, fence, now) &
            fb.Eq(x => x.PendingGenerationId, null) &
            fb.Eq(x => x.PendingGenerationHash, null) &
            generationShape &
            fb.Eq(x => x.GenerationPublishRevision, run.GenerationPublishRevision),
            Builders<StatisticReconciliationRun>.Update
                .Set(x => x.Status, nextState.Status)
                .Set(x => x.StateRevision, nextState.StateRevision)
                .Set(x => x.StateHash, stateHash)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.LeaseOwnerId, null)
                .Set(x => x.ClaimToken, null)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastHeartbeatAtUtc, null)
                .Set(x => x.DiagnosticCode, null)
                .Set(x => x.PendingGenerationId, generationId)
                .Set(x => x.PendingGenerationHash, generationHash)
                .Set(x => x.PendingGenerationPublishedAtUtc, now)
                .Set(x => x.GenerationPublishRevision, nextState.GenerationPublishRevision)
                .Set(x => x.Recheck, recheck)
                .Set(x => x.LatestWriterUserId, actor.Id)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actor.Id),
            new FindOneAndUpdateOptions<StatisticReconciliationRun>
            {
                ReturnDocument = ReturnDocument.After
            },
            ct);
        if (updated is null)
        {
            var raced = await LoadWorkerRunAsync(run.Id, binding.ChainId, actor, ct);
            RequireReadIntegrity(raced);
            if (string.Equals(raced.PendingGenerationId, generationId,
                    StringComparison.Ordinal) &&
                string.Equals(raced.PendingGenerationHash, generationHash,
                    StringComparison.Ordinal) &&
                raced.PendingGenerationPublishedAtUtc.HasValue &&
                ((raced.Recheck is null &&
                  raced.CurrentGenerationId is null &&
                  raced.CurrentGenerationHash is null) ||
                 (raced.Recheck?.Phase ==
                      StatisticReconciliationRecheckPhases.PendingPublished &&
                  raced.CurrentGenerationId ==
                      raced.Recheck.BaseCurrentGenerationId &&
                  raced.CurrentGenerationHash ==
                      raced.Recheck.BaseCurrentGenerationHash &&
                  raced.Recheck.SuccessorGenerationId == generationId &&
                  raced.Recheck.SuccessorGenerationHash == generationHash)))
            {
                return ToDetail(raced);
            }
            throw JobConflict("GENERATION_PUBLISH_CAS_LOST");
        }
        RequireReadIntegrity(updated);
        return ToDetail(updated);
    }

    private async Task<StatisticReconciliationRun> LoadWorkerRunAsync(
        string reconciliationId,
        string chainId,
        MeResponse actor,
        CancellationToken ct)
    {
        reconciliationId = NormalizeHiddenObjectId(
            reconciliationId,
            "reconciliationId",
            actor);
        return await _ctx.StatisticReconciliationRuns
                   .Find(x => x.Id == reconciliationId &&
                              x.CandidateChainId == chainId &&
                              !x.IsDeleted)
                   .FirstOrDefaultAsync(ct)
               ?? throw NotFound();
    }

    private static WorkerFence NormalizeFence(
        StatisticReconciliationWorkerFenceRequest request)
    {
        if (request is null)
            throw RequestInvalid("body", "BODY_REQUIRED");
        return new WorkerFence(
            RequiredToken(request.WorkerId, "workerId", 128),
            RequiredToken(request.ClaimToken, "claimToken", 128));
    }

    private static void RequireFence(
        StatisticReconciliationRun run,
        WorkerFence fence)
    {
        if (!string.Equals(
                run.Status,
                StatisticReconciliationRunStatuses.Running,
                StringComparison.Ordinal) ||
            !string.Equals(run.LeaseOwnerId, fence.WorkerId, StringComparison.Ordinal) ||
            !string.Equals(run.ClaimToken, fence.ClaimToken, StringComparison.Ordinal))
        {
            throw JobConflict("STALE_WORKER_FENCE");
        }
    }

    private static FilterDefinition<StatisticReconciliationRun> BuildFenceFilter(
        FilterDefinitionBuilder<StatisticReconciliationRun> fb,
        StatisticReconciliationRun run,
        WorkerFence fence,
        DateTime now)
        => fb.Eq(x => x.Id, run.Id) &
           fb.Eq(x => x.CandidateChainId, run.CandidateChainId) &
           fb.Eq(x => x.Status, StatisticReconciliationRunStatuses.Running) &
           fb.Eq(x => x.StateRevision, run.StateRevision) &
           fb.Eq(x => x.StateHash, run.StateHash) &
           fb.Eq(x => x.LeaseOwnerId, fence.WorkerId) &
           fb.Eq(x => x.ClaimToken, fence.ClaimToken) &
           fb.Gt(x => x.LeaseUntilUtc, now) &
           fb.Gt(x => x.DeadlineAtUtc, now) &
           fb.Eq(x => x.IsDeleted, false);

    private async Task FailExpiredRunsAsync(
        string chainId,
        string writerUserId,
        DateTime now,
        CancellationToken ct)
    {
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var expired = await _ctx.StatisticReconciliationRuns
            .Find(fb.Eq(x => x.CandidateChainId, chainId) &
                  fb.In(x => x.Status, new[]
                  {
                      StatisticReconciliationRunStatuses.Queued,
                      StatisticReconciliationRunStatuses.Running
                  }) &
                  fb.Lte(x => x.DeadlineAtUtc, now) &
                  fb.Eq(x => x.IsDeleted, false))
            .SortBy(x => x.DeadlineAtUtc)
            .Limit(64)
            .ToListAsync(ct);
        foreach (var run in expired)
        {
            RequireReadIntegrity(run);
            await RefreshTimeoutAsync(run, writerUserId, ct);
        }
    }

    private async Task TerminalizeExpiredLeaseAsync(
        StatisticReconciliationRun run,
        string writerUserId,
        DateTime now,
        int retryCount,
        bool timedOut,
        CancellationToken ct)
    {
        var diagnosticCode = timedOut
            ? "P10_RECONCILIATION_JOB_TIMEOUT"
            : "P10_RECONCILIATION_JOB_FAILED";
        var nextState = StateOf(run) with
        {
            Status = StatisticReconciliationRunStatuses.Failed,
            StateRevision = run.StateRevision + 1,
            RetryCount = retryCount,
            NextRetryAtUtc = null,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = diagnosticCode,
            FailedAtUtc = now
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        await _ctx.StatisticReconciliationRuns.UpdateOneAsync(
            fb.Eq(x => x.Id, run.Id) &
            fb.Eq(x => x.Status, StatisticReconciliationRunStatuses.Running) &
            fb.Eq(x => x.StateRevision, run.StateRevision) &
            fb.Eq(x => x.StateHash, run.StateHash) &
            fb.Lte(x => x.LeaseUntilUtc, now) &
            fb.Eq(x => x.IsDeleted, false),
            Builders<StatisticReconciliationRun>.Update
                .Set(x => x.Status, nextState.Status)
                .Set(x => x.StateRevision, nextState.StateRevision)
                .Set(x => x.StateHash, stateHash)
                .Set(x => x.RetryCount, retryCount)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.LeaseOwnerId, null)
                .Set(x => x.ClaimToken, null)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastHeartbeatAtUtc, null)
                .Set(x => x.DiagnosticCode, nextState.DiagnosticCode)
                .Set(x => x.FailedAtUtc, now)
                .Set(x => x.LatestWriterUserId, writerUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, writerUserId),
            cancellationToken: ct);
    }

    private async Task RequeueExpiredLeaseAsync(
        StatisticReconciliationRun run,
        string writerUserId,
        DateTime now,
        int retryCount,
        DateTime retryAt,
        CancellationToken ct)
    {
        var nextState = StateOf(run) with
        {
            Status = StatisticReconciliationRunStatuses.Queued,
            StateRevision = run.StateRevision + 1,
            RetryCount = retryCount,
            NextRetryAtUtc = retryAt,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = "P10_RECONCILIATION_LEASE_EXPIRED",
            FailedAtUtc = null
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        await _ctx.StatisticReconciliationRuns.UpdateOneAsync(
            fb.Eq(x => x.Id, run.Id) &
            fb.Eq(x => x.CandidateChainId, run.CandidateChainId) &
            fb.Eq(x => x.Status, StatisticReconciliationRunStatuses.Running) &
            fb.Eq(x => x.StateRevision, run.StateRevision) &
            fb.Eq(x => x.StateHash, run.StateHash) &
            fb.Lte(x => x.LeaseUntilUtc, now) &
            fb.Gt(x => x.DeadlineAtUtc, now) &
            fb.Eq(x => x.PendingGenerationId, null) &
            fb.Eq(x => x.PendingGenerationHash, null) &
            fb.Eq(x => x.PendingGenerationPublishedAtUtc, null) &
            fb.Eq(x => x.CurrentGenerationId, null) &
            fb.Eq(x => x.CurrentGenerationHash, null) &
            fb.Eq(x => x.IsDeleted, false),
            Builders<StatisticReconciliationRun>.Update
                .Set(x => x.Status, nextState.Status)
                .Set(x => x.StateRevision, nextState.StateRevision)
                .Set(x => x.StateHash, stateHash)
                .Set(x => x.RetryCount, retryCount)
                .Set(x => x.NextRetryAtUtc, retryAt)
                .Set(x => x.LeaseOwnerId, null)
                .Set(x => x.ClaimToken, null)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.LastHeartbeatAtUtc, null)
                .Set(x => x.DiagnosticCode, nextState.DiagnosticCode)
                .Set(x => x.FailedAtUtc, null)
                .Set(x => x.LatestWriterUserId, writerUserId)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, writerUserId),
            cancellationToken: ct);
    }

    private async Task<StatisticReconciliationDetailResponse>
        FailActiveRecheckAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationRecheckMarker marker,
            WorkerFence fence,
            MeResponse actor,
            string failureCode,
            bool terminal,
            int retryCount,
            DateTime retryAt,
            DateTime now,
            CancellationToken ct)
    {
        StatisticReconciliationRecheckCanonical.RequireValidMarker(marker);
        var nextMarker = terminal
            ? null
            : StatisticReconciliationRecheckCanonical.Clone(marker);
        if (nextMarker is not null)
        {
            nextMarker.Phase =
                StatisticReconciliationRecheckPhases.ReadyToClaim;
            StatisticReconciliationRecheckCanonical.RefreshMarkerHash(
                nextMarker);
        }
        var nextStatus = terminal
            ? marker.BaseTerminalStatus
            : StatisticReconciliationRunStatuses.Queued;
        var failedAt = nextStatus ==
            StatisticReconciliationRunStatuses.Failed
                ? now
                : (DateTime?)null;
        var nextState = StateOf(run) with
        {
            Status = nextStatus,
            StateRevision = checked(run.StateRevision + 1),
            RetryCount = retryCount,
            NextRetryAtUtc = terminal ? null : retryAt,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = terminal ? null : failureCode,
            PendingGenerationId = null,
            PendingGenerationHash = null,
            PendingGenerationPublishedAtUtc = null,
            CurrentGenerationId = marker.BaseCurrentGenerationId,
            CurrentGenerationHash = marker.BaseCurrentGenerationHash,
            Recheck = nextMarker,
            FailedAtUtc = failedAt
        };
        var stateHash = BuildStateHash(run.Id, nextState);
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var updated = await _ctx.StatisticReconciliationRuns
            .FindOneAndUpdateAsync(
                BuildFenceFilter(fb, run, fence, now) &
                fb.Eq(value => value.CurrentGenerationId,
                    marker.BaseCurrentGenerationId) &
                fb.Eq(value => value.CurrentGenerationHash,
                    marker.BaseCurrentGenerationHash) &
                fb.Eq(value => value.PendingGenerationId, null) &
                fb.Eq(value => value.PendingGenerationHash, null) &
                fb.Eq("recheck.markerId", marker.MarkerId) &
                fb.Eq("recheck.markerStateHash",
                    marker.MarkerStateHash) &
                fb.Eq("recheck.phase",
                    StatisticReconciliationRecheckPhases.CaptureRunning),
                Builders<StatisticReconciliationRun>.Update
                    .Set(value => value.Status, nextStatus)
                    .Set(value => value.StateRevision,
                        nextState.StateRevision)
                    .Set(value => value.StateHash, stateHash)
                    .Set(value => value.RetryCount, retryCount)
                    .Set(value => value.NextRetryAtUtc,
                        terminal ? null : retryAt)
                    .Set(value => value.LeaseOwnerId, null)
                    .Set(value => value.ClaimToken, null)
                    .Set(value => value.LeaseUntilUtc, null)
                    .Set(value => value.LastHeartbeatAtUtc, null)
                    .Set(value => value.DiagnosticCode,
                        terminal ? null : failureCode)
                    .Set(value => value.Recheck, nextMarker)
                    .Set(value => value.FailedAtUtc, failedAt)
                    .Set(value => value.LatestWriterUserId,
                        actor.Id)
                    .Set(value => value.UpdatedAtUtc, now)
                    .Set(value => value.UpdatedByUserId,
                        actor.Id),
                new FindOneAndUpdateOptions<StatisticReconciliationRun>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);
        if (updated is null)
            throw JobConflict("STALE_WORKER_FENCE");
        RequireReadIntegrity(updated);
        return ToDetail(updated);
    }
    private TimeSpan RetryDelay(int retryCount)
    {
        var multiplier = Math.Pow(2, Math.Clamp(retryCount - 1, 0, 20));
        var milliseconds = Math.Min(
            _retryBaseDelay.TotalMilliseconds * multiplier,
            _retryMaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static void ThrowIfTimedOut(StatisticReconciliationRun run)
    {
        if (string.Equals(
                run.DiagnosticCode,
                "P10_RECONCILIATION_JOB_TIMEOUT",
                StringComparison.Ordinal))
        {
            throw new AppException(
                AppErrorCode.STAT_RECONCILIATION_JOB_TIMEOUT,
                new { writes = 0 });
        }
    }

    private sealed record WorkerFence(string WorkerId, string ClaimToken);
}
