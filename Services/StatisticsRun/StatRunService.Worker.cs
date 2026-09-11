using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class StatRunService
{
    private static readonly IReadOnlySet<string> TestingFailureCodes = new HashSet<string>(
        ["TRANSIENT_TEST", "TIMEOUT_TEST", "TERMINAL_TEST"],
        StringComparer.Ordinal);

    public async Task<StatRunWorkerLeaseResponse?> ClaimAsync(
        string workerId,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireTestingAdmin(actor);
        RequireTestingFoundation();
        workerId = NormalizeWorkerId(workerId);
        await FailExpiredJobsAsync(actor, ct);

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var now = MongoUtcNow();
            var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
            var eligible = fb.Eq(x => x.RunKind, WorkReportStatisticRebuildJobRunKinds.Foundation) &
                           fb.Ne(x => x.ReceiptId, null) &
                           fb.Ne(x => x.ReceiptId, string.Empty) &
                           fb.Eq(x => x.CandidateChainId, StatRunCapabilityActivation.RequiredChainId) &
                           fb.Eq(x => x.IsActive, true) &
                           fb.Eq(x => x.IsDeleted, false) &
                           (fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Pending) |
                            fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.RetryWaiting) |
                            (fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Running) &
                             fb.Lte(x => x.LeaseUntilUtc, now))) &
                           (fb.Eq(x => x.NextRetryAtUtc, null) | fb.Lte(x => x.NextRetryAtUtc, now)) &
                           fb.Gt(x => x.DeadlineAtUtc, now);

            var candidate = await _ctx.WorkReportStatisticRebuildJobs
                .Find(eligible)
                .Sort(Builders<WorkReportStatisticRebuildJob>.Sort
                    .Ascending(x => x.Priority)
                    .Ascending(x => x.NextRetryAtUtc)
                    .Ascending(x => x.CreatedAtUtc))
                .FirstOrDefaultAsync(ct);
            if (candidate is null)
                return null;

            RequireJobActivation(candidate);
            var claimToken = Guid.NewGuid().ToString("N");
            var proposedLeaseUntil = now.AddMinutes(10);
            var leaseUntil = candidate.DeadlineAtUtc!.Value < proposedLeaseUntil
                ? candidate.DeadlineAtUtc.Value
                : proposedLeaseUntil;
            var nextRevision = candidate.StateRevision + 1;
            var nextHash = BuildStateHash(
                candidate.Id,
                WorkReportStatisticRebuildJobStatuses.Running,
                nextRevision,
                candidate.RetryCount,
                null,
                leaseUntil,
                candidate.DeadlineAtUtc,
                claimToken,
                workerId,
                now,
                candidate.GenerationId,
                candidate.GenerationHash,
                candidate.ResetReceiptHistoryHash,
                WorkReportStatisticRebuildJobFreshnessStates.Pending,
                null);

            var cas = eligible &
                      fb.Eq(x => x.Id, candidate.Id) &
                      fb.Eq(x => x.CapabilityId, candidate.CapabilityId) &
                      fb.Eq(x => x.RouteId, candidate.RouteId) &
                      fb.Eq(x => x.CatalogVersion, candidate.CatalogVersion) &
                      fb.Eq(x => x.CatalogRawSha256, candidate.CatalogRawSha256) &
                      fb.Eq(x => x.CatalogSemanticSha256, candidate.CatalogSemanticSha256) &
                      fb.Eq(x => x.SchemaRawSha256, candidate.SchemaRawSha256) &
                      fb.Eq(x => x.SchemaSemanticSha256, candidate.SchemaSemanticSha256) &
                      fb.Eq(x => x.StageLockSha256, candidate.StageLockSha256) &
                      fb.Eq(x => x.ImmutableHeaderHash, candidate.ImmutableHeaderHash) &
                      fb.Eq(x => x.StateRevision, candidate.StateRevision) &
                      fb.Eq(x => x.StateHash, candidate.StateHash);
            var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
                cas,
                Builders<WorkReportStatisticRebuildJob>.Update
                    .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.Running)
                    .Set(x => x.ClaimToken, claimToken)
                    .Set(x => x.LeaseOwnerId, workerId)
                    .Set(x => x.ClaimedAtUtc, now)
                    .Set(x => x.LastHeartbeatAtUtc, now)
                    .Set(x => x.LeaseUntilUtc, leaseUntil)
                    .Set(x => x.LastRunAtUtc, now)
                    .Set(x => x.NextRetryAtUtc, null)
                    .Set(x => x.DiagnosticCode, null)
                    .Set(x => x.LastErrorType, null)
                    .Set(x => x.LastError, null)
                    .Set(x => x.LastErrorAtUtc, null)
                    .Set(x => x.FreshnessState, WorkReportStatisticRebuildJobFreshnessStates.Pending)
                    .Set(x => x.StateRevision, nextRevision)
                    .Set(x => x.StateHash, nextHash)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actor.Id),
                cancellationToken: ct);
            if (result.ModifiedCount != 1)
                continue;

            var claimed = await ReloadAdminJobAsync(candidate.Id, ct);
            return new StatRunWorkerLeaseResponse
            {
                WorkerId = workerId,
                ClaimToken = claimToken,
                LeaseUntilUtc = leaseUntil,
                Job = await ToResponseAsync(claimed, false, ct)
            };
        }

        throw JobConflict("CLAIM_CONTENTION");
    }

    public async Task<StatRunJobResponse> HeartbeatAsync(
        string jobId,
        StatRunWorkerFenceRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireTestingAdmin(actor);
        RequireTestingFoundation();
        jobId = NormalizeObjectId(jobId, "jobId");
        var workerId = NormalizeWorkerId(request?.WorkerId);
        var claimToken = NormalizeClaimToken(request?.ClaimToken);
        var job = await ReloadAdminJobAsync(jobId, ct);
        RequireJobActivation(job);

        var now = MongoUtcNow();
        await RequireLiveFenceAsync(job, workerId, claimToken, actor, now, ct);
        var proposedLeaseUntil = now.AddMinutes(10);
        var leaseUntil = job.DeadlineAtUtc!.Value < proposedLeaseUntil
            ? job.DeadlineAtUtc.Value
            : proposedLeaseUntil;
        var nextRevision = job.StateRevision + 1;
        var nextHash = BuildStateHash(
            job.Id,
            job.Status,
            nextRevision,
            job.RetryCount,
            job.NextRetryAtUtc,
            leaseUntil,
            job.DeadlineAtUtc,
            claimToken,
            workerId,
            now,
            job.GenerationId,
            job.GenerationHash,
            job.ResetReceiptHistoryHash,
            job.FreshnessState ?? WorkReportStatisticRebuildJobFreshnessStates.Pending,
            job.DiagnosticCode);

        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            BuildFenceFilter(job, workerId, claimToken, now),
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.LeaseUntilUtc, leaseUntil)
                .Set(x => x.LastHeartbeatAtUtc, now)
                .Set(x => x.StateRevision, nextRevision)
                .Set(x => x.StateHash, nextHash)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actor.Id),
            cancellationToken: ct);
        if (result.ModifiedCount != 1)
            throw JobConflict("STALE_WORKER_FENCE");
        return await ToResponseAsync(await ReloadAdminJobAsync(jobId, ct), false, ct);
    }

    public async Task<StatRunJobResponse> CompleteFoundationAsync(
        string jobId,
        StatRunWorkerFenceRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireTestingAdmin(actor);
        RequireTestingFoundation();
        jobId = NormalizeObjectId(jobId, "jobId");
        var workerId = NormalizeWorkerId(request?.WorkerId);
        var claimToken = NormalizeClaimToken(request?.ClaimToken);
        var job = await ReloadAdminJobAsync(jobId, ct);
        RequireJobActivation(job);

        var now = MongoUtcNow();
        await RequireLiveFenceAsync(job, workerId, claimToken, actor, now, ct);
        var nextRevision = job.StateRevision + 1;
        var generationId = StatRunCanonicalJson.HashText(string.Join(
            "\n",
            "STAT_RUN_FOUNDATION_GENERATION_V1",
            job.Id,
            nextRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var generationHash = StatRunCanonicalJson.HashObject(new
        {
            generationId,
            job.Id,
            job.CapabilityId,
            job.RequestHash,
            job.ConfigVersionId,
            job.ConfigRevision,
            job.ConfigHash,
            job.CatalogSemanticSha256,
            job.SourceReportId,
            job.SourcePayloadRevision,
            job.SourcePayloadHash,
            job.SourceLifecycleRevision,
            job.FlowTemplateId,
            job.FlowFamilyRevision,
            job.FlowTemplateVersionId,
            job.FlowPayloadHash,
            job.FlowInstanceId,
            job.FlowInstanceRevision,
            job.FlowInstanceState,
            job.FlowExecutionEpoch,
            job.FlowExecutionEpochId,
            job.FlowExecutionEpochRevision,
            job.FlowExecutionEpochState,
            job.FlowBranchId,
            job.FlowStepId,
            job.PeriodInstanceKey,
            job.FlowAttemptNo,
            job.FlowStepInstanceId,
            job.FlowStepInstanceRevision,
            job.FlowStepInstanceState,
            computedAtUtc = now
        });
        var nextHash = BuildStateHash(
            job.Id,
            WorkReportStatisticRebuildJobStatuses.Completed,
            nextRevision,
            job.RetryCount,
            null,
            null,
            job.DeadlineAtUtc,
            null,
            null,
            job.LastHeartbeatAtUtc,
            generationId,
            generationHash,
            job.ResetReceiptHistoryHash,
            WorkReportStatisticRebuildJobFreshnessStates.Fresh,
            null);

        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            BuildFenceFilter(job, workerId, claimToken, now),
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.Completed)
                .Set(x => x.IsActive, false)
                .Set(x => x.ProcessedReportCount, job.TotalReportCount)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.ClaimToken, null)
                .Set(x => x.LeaseOwnerId, null)
                .Set(x => x.CompletedAtUtc, now)
                .Set(x => x.ComputedAtUtc, now)
                .Set(x => x.GenerationId, generationId)
                .Set(x => x.GenerationHash, generationHash)
                .Set(x => x.FreshnessState, WorkReportStatisticRebuildJobFreshnessStates.Fresh)
                .Set(x => x.StaleReason, null)
                .Set(x => x.DiagnosticCode, null)
                .Set(x => x.StateRevision, nextRevision)
                .Set(x => x.StateHash, nextHash)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actor.Id),
            cancellationToken: ct);
        if (result.ModifiedCount != 1)
            throw JobConflict("STALE_WORKER_FENCE");
        return await ToResponseAsync(await ReloadAdminJobAsync(jobId, ct), false, ct);
    }

    public async Task<StatRunJobResponse> RetryAsync(
        string jobId,
        StatRunWorkerRetryRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireTestingAdmin(actor);
        RequireTestingFoundation();
        jobId = NormalizeObjectId(jobId, "jobId");
        var workerId = NormalizeWorkerId(request?.WorkerId);
        var claimToken = NormalizeClaimToken(request?.ClaimToken);
        var failureCode = request?.FailureCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!TestingFailureCodes.Contains(failureCode))
            throw Validation("failureCode", "FAILURE_CODE_INVALID");

        var job = await ReloadAdminJobAsync(jobId, ct);
        RequireJobActivation(job);
        var now = MongoUtcNow();
        await RequireLiveFenceAsync(job, workerId, claimToken, actor, now, ct);

        var retryCount = failureCode is "TERMINAL_TEST" or "TIMEOUT_TEST"
            ? _maxRetryCount
            : job.RetryCount + 1;
        var dead = retryCount >= _maxRetryCount;
        var nextRetryAt = dead
            ? (DateTime?)null
            : now.AddMinutes(Math.Min(60, retryCount * 5));
        var nextStatus = dead
            ? WorkReportStatisticRebuildJobStatuses.DeadLetter
            : WorkReportStatisticRebuildJobStatuses.RetryWaiting;
        var nextRevision = job.StateRevision + 1;
        var nextHash = BuildStateHash(
            job.Id,
            nextStatus,
            nextRevision,
            retryCount,
            nextRetryAt,
            null,
            job.DeadlineAtUtc,
            null,
            null,
            job.LastHeartbeatAtUtc,
            job.GenerationId,
            job.GenerationHash,
            job.ResetReceiptHistoryHash,
            WorkReportStatisticRebuildJobFreshnessStates.Pending,
            failureCode);

        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            BuildFenceFilter(job, workerId, claimToken, now),
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.Status, nextStatus)
                .Set(x => x.IsActive, !dead)
                .Set(x => x.RetryCount, retryCount)
                .Set(x => x.NextRetryAtUtc, nextRetryAt)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.ClaimToken, null)
                .Set(x => x.LeaseOwnerId, null)
                .Set(x => x.DiagnosticCode, failureCode)
                .Set(x => x.LastErrorType, null)
                .Set(x => x.LastError, null)
                .Set(x => x.LastErrorAtUtc, now)
                .Set(x => x.CompletedAtUtc, dead ? now : null)
                .Inc(x => x.FailedReportCount, 1)
                .Set(x => x.FreshnessState, WorkReportStatisticRebuildJobFreshnessStates.Pending)
                .Set(x => x.StateRevision, nextRevision)
                .Set(x => x.StateHash, nextHash)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actor.Id),
            cancellationToken: ct);
        if (result.ModifiedCount != 1)
            throw JobConflict("STALE_WORKER_FENCE");
        return await ToResponseAsync(await ReloadAdminJobAsync(jobId, ct), false, ct);
    }

    public async Task<StatRunJobResponse> ExpireLeaseAsync(
        string jobId,
        StatRunWorkerFenceRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireTestingAdmin(actor);
        RequireTestingFoundation();
        jobId = NormalizeObjectId(jobId, "jobId");
        var workerId = NormalizeWorkerId(request?.WorkerId);
        var claimToken = NormalizeClaimToken(request?.ClaimToken);
        var job = await ReloadAdminJobAsync(jobId, ct);
        RequireJobActivation(job);
        var now = MongoUtcNow();
        await RequireLiveFenceAsync(job, workerId, claimToken, actor, now, ct);
        var expiredAt = now.AddMilliseconds(-1);
        var nextRevision = job.StateRevision + 1;
        var nextHash = BuildStateHash(
            job.Id,
            job.Status,
            nextRevision,
            job.RetryCount,
            job.NextRetryAtUtc,
            expiredAt,
            job.DeadlineAtUtc,
            claimToken,
            workerId,
            job.LastHeartbeatAtUtc,
            job.GenerationId,
            job.GenerationHash,
            job.ResetReceiptHistoryHash,
            job.FreshnessState ?? WorkReportStatisticRebuildJobFreshnessStates.Pending,
            job.DiagnosticCode);

        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            BuildFenceFilter(job, workerId, claimToken, now),
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.LeaseUntilUtc, expiredAt)
                .Set(x => x.StateRevision, nextRevision)
                .Set(x => x.StateHash, nextHash)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actor.Id),
            cancellationToken: ct);
        if (result.ModifiedCount != 1)
            throw JobConflict("STALE_WORKER_FENCE");
        return await ToResponseAsync(await ReloadAdminJobAsync(jobId, ct), false, ct);
    }

    public async Task<StatRunJobResponse> ResetAsync(
        string jobId,
        StatRunResetRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        RequireTestingAdmin(actor);
        RequireTestingFoundation();
        jobId = NormalizeObjectId(jobId, "jobId");
        var commandId = NormalizeCommandId(request?.CommandId);
        if (request is null || request.ExpectedStateRevision < 1)
            throw Validation("expectedStateRevision", "REVISION_INVALID");
        RequireCanonicalHash(request.ExpectedStateHash, "expectedStateHash");
        var resetHash = StatRunCanonicalJson.HashObject(new
        {
            jobId,
            actorUserId = actor.Id,
            commandId,
            request.ExpectedStateRevision,
            request.ExpectedStateHash
        });

        var job = await ReloadAdminJobAsync(jobId, ct);
        RequireJobActivation(job);
        var replay = await ResolveResetReplayAsync(
            job,
            actor,
            commandId,
            resetHash,
            ct);
        if (replay is not null)
            return replay;
        if (!string.Equals(job.Status, WorkReportStatisticRebuildJobStatuses.DeadLetter, StringComparison.Ordinal))
            throw JobConflict("RESET_REQUIRES_DEAD_LETTER");
        if (job.StateRevision != request.ExpectedStateRevision ||
            !string.Equals(job.StateHash, request.ExpectedStateHash, StringComparison.Ordinal))
        {
            throw JobConflict("RESET_CAS_STALE");
        }

        var now = MongoUtcNow();
        var resetDeadline = now.AddMinutes(10);
        var nextRevision = job.StateRevision + 1;
        var resetReceipt = new WorkReportStatisticRebuildJobResetReceipt
        {
            JobId = job.Id,
            ActorUserId = actor.Id,
            CommandId = commandId,
            RequestHash = resetHash,
            ExpectedStateRevision = request.ExpectedStateRevision,
            ExpectedStateHash = request.ExpectedStateHash,
            AcceptedStateRevision = nextRevision,
            AcceptedStateHash = string.Empty,
            AcceptedStatus = WorkReportStatisticRebuildJobStatuses.Pending,
            AcceptedAtUtc = now,
            AcceptedDeadlineAtUtc = resetDeadline
        };
        resetReceipt.ReceiptHash = BuildResetReceiptHash(resetReceipt);
        var nextResetHistoryHash = BuildResetReceiptHistoryHash(
            (job.ResetReceipts ?? []).Append(resetReceipt));
        var nextHash = BuildStateHash(
            job.Id,
            WorkReportStatisticRebuildJobStatuses.Pending,
            nextRevision,
            0,
            now,
            null,
            resetDeadline,
            null,
            null,
            null,
            null,
            null,
            nextResetHistoryHash,
            WorkReportStatisticRebuildJobFreshnessStates.Pending,
            null);
        resetReceipt.AcceptedStateHash = nextHash;
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var filter = fb.Eq(x => x.Id, job.Id) &
                     fb.Eq(x => x.IsDeleted, false) &
                     fb.Eq(x => x.RunKind, WorkReportStatisticRebuildJobRunKinds.Foundation) &
                     fb.Ne(x => x.ReceiptId, null) &
                     fb.Ne(x => x.ReceiptId, string.Empty) &
                     fb.Eq(x => x.CandidateChainId, StatRunCapabilityActivation.RequiredChainId) &
                     fb.Eq(x => x.CapabilityId, job.CapabilityId) &
                     fb.Eq(x => x.RouteId, job.RouteId) &
                     fb.Eq(x => x.CatalogVersion, job.CatalogVersion) &
                     fb.Eq(x => x.CatalogRawSha256, job.CatalogRawSha256) &
                     fb.Eq(x => x.CatalogSemanticSha256, job.CatalogSemanticSha256) &
                     fb.Eq(x => x.SchemaRawSha256, job.SchemaRawSha256) &
                     fb.Eq(x => x.SchemaSemanticSha256, job.SchemaSemanticSha256) &
                     fb.Eq(x => x.StageLockSha256, job.StageLockSha256) &
                     fb.Eq(x => x.ImmutableHeaderHash, job.ImmutableHeaderHash) &
                     fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.DeadLetter) &
                     fb.Eq(x => x.StateRevision, job.StateRevision) &
                     fb.Eq(x => x.StateHash, job.StateHash);
        UpdateResult result;
        try
        {
            result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
                filter,
                Builders<WorkReportStatisticRebuildJob>.Update
                    .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.Pending)
                    .Set(x => x.IsActive, true)
                    .Set(x => x.RetryCount, 0)
                    .Set(x => x.NextRetryAtUtc, now)
                    .Set(x => x.LeaseUntilUtc, null)
                    .Set(x => x.ClaimToken, null)
                    .Set(x => x.LeaseOwnerId, null)
                    .Set(x => x.ClaimedAtUtc, null)
                    .Set(x => x.LastHeartbeatAtUtc, null)
                    .Set(x => x.DeadlineAtUtc, resetDeadline)
                    .Set(x => x.CompletedAtUtc, null)
                    .Set(x => x.ComputedAtUtc, null)
                    .Set(x => x.GenerationId, null)
                    .Set(x => x.GenerationHash, null)
                    .Set(x => x.FreshnessState, WorkReportStatisticRebuildJobFreshnessStates.Pending)
                    .Set(x => x.StaleReason, null)
                    .Set(x => x.DiagnosticCode, null)
                    .Set(x => x.LastErrorType, null)
                    .Set(x => x.LastError, null)
                    .Set(x => x.LastErrorAtUtc, null)
                    .Set(x => x.LastResetCommandId, commandId)
                    .Set(x => x.LastResetRequestHash, resetHash)
                    .Set(x => x.LastResetAtUtc, now)
                    .Set(x => x.LastResetByUserId, actor.Id)
                    .Set(x => x.ResetReceiptHistoryHash, nextResetHistoryHash)
                    .Push(x => x.ResetReceipts, resetReceipt)
                    .Set(x => x.StateRevision, nextRevision)
                    .Set(x => x.StateHash, nextHash)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actor.Id),
                cancellationToken: ct);
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            var current = await ReloadAdminJobAsync(jobId, ct);
            RequireJobActivation(current);
            replay = await ResolveResetReplayAsync(
                current,
                actor,
                commandId,
                resetHash,
                ct);
            if (replay is not null)
                return replay;
            // A newer command may have won the same active domain identity after
            // this job entered DEAD_LETTER. Mongo rejects the reset atomically;
            // expose a stable zero-write conflict instead of a driver exception.
            throw JobConflict("ACTIVE_RUN_ALREADY_EXISTS");
        }
        if (result.ModifiedCount != 1)
        {
            var current = await ReloadAdminJobAsync(jobId, ct);
            RequireJobActivation(current);
            replay = await ResolveResetReplayAsync(
                current,
                actor,
                commandId,
                resetHash,
                ct);
            if (replay is not null)
                return replay;
            throw JobConflict("RESET_CAS_STALE");
        }
        return await ToResponseAsync(await ReloadAdminJobAsync(jobId, ct), false, ct);
    }

    private async Task<StatRunJobResponse?> ResolveResetReplayAsync(
        WorkReportStatisticRebuildJob job,
        MeResponse actor,
        string commandId,
        string requestHash,
        CancellationToken ct)
    {
        var matches = (job.ResetReceipts ?? [])
            .Where(receipt =>
                string.Equals(receipt.ActorUserId, actor.Id, StringComparison.Ordinal) &&
                string.Equals(receipt.CommandId, commandId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            return null;
        if (matches.Length != 1)
            throw JobConflict("RESET_RECEIPT_HISTORY_INTEGRITY_INVALID");
        if (!string.Equals(matches[0].RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw new AppException(
                AppErrorCode.STAT_RUN_COMMAND_REPLAY_MISMATCH,
                new { reason = "RESET_REQUEST_HASH_MISMATCH", writes = 0 });
        }
        return await ToResponseAsync(job, true, ct);
    }

    private void RequireTestingFoundation()
    {
        foreach (var capability in StatRunCapabilities.All)
            _activation.RequireFoundation(capability, StatRunRouteRegistry.CoreJob(capability));
    }

    private static void RequireTestingAdmin(MeResponse actor)
        => RoleGuard.RequireSystemAdmin(actor);

    private void RequireJobActivation(WorkReportStatisticRebuildJob job)
    {
        var binding = _activation.RequireFoundation(
            job.CapabilityId ?? string.Empty,
            job.RouteId ?? string.Empty);
        if (!string.Equals(
                job.RunKind,
                WorkReportStatisticRebuildJobRunKinds.Foundation,
                StringComparison.Ordinal) ||
            !StatRunCanonicalJson.IsCanonicalSha256(job.ReceiptId) ||
            !string.Equals(job.CandidateChainId, binding.ChainId, StringComparison.Ordinal) ||
            !string.Equals(job.CatalogVersion, binding.CatalogVersion, StringComparison.Ordinal) ||
            !string.Equals(job.CatalogRawSha256, binding.CatalogRawSha256, StringComparison.Ordinal) ||
            !string.Equals(job.CatalogSemanticSha256, binding.CatalogSemanticSha256, StringComparison.Ordinal) ||
            !string.Equals(job.SchemaRawSha256, binding.SchemaRawSha256, StringComparison.Ordinal) ||
            !string.Equals(job.SchemaSemanticSha256, binding.SchemaSemanticSha256, StringComparison.Ordinal) ||
            !string.Equals(job.StageLockSha256, binding.StageLockSha256, StringComparison.Ordinal))
        {
            throw JobConflict("JOB_ACTIVATION_PIN_MISMATCH");
        }
        RequireImmutableHeaderIntegrity(job);
        RequireStateIntegrity(job);
    }

    private async Task<WorkReportStatisticRebuildJob> ReloadAdminJobAsync(
        string jobId,
        CancellationToken ct)
        => await _ctx.WorkReportStatisticRebuildJobs
               .Find(x => x.Id == jobId &&
                          !x.IsDeleted &&
                          x.RunKind == WorkReportStatisticRebuildJobRunKinds.Foundation &&
                          x.ReceiptId != null &&
                          x.ReceiptId != string.Empty &&
                          x.CandidateChainId == StatRunCapabilityActivation.RequiredChainId &&
                          x.CapabilityId != null &&
                          x.RouteId != null)
               .FirstOrDefaultAsync(ct)
           ?? throw JobConflict("JOB_NOT_AVAILABLE");

    private async Task FailExpiredJobsAsync(MeResponse actor, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var now = MongoUtcNow();
            var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
            var filter = fb.Eq(x => x.RunKind, WorkReportStatisticRebuildJobRunKinds.Foundation) &
                         fb.Ne(x => x.ReceiptId, null) &
                         fb.Ne(x => x.ReceiptId, string.Empty) &
                         fb.Eq(x => x.CandidateChainId, StatRunCapabilityActivation.RequiredChainId) &
                         fb.Eq(x => x.IsDeleted, false) &
                         fb.Eq(x => x.IsActive, true) &
                         fb.Lte(x => x.DeadlineAtUtc, now) &
                         fb.In(x => x.Status, new[]
                         {
                             WorkReportStatisticRebuildJobStatuses.Pending,
                             WorkReportStatisticRebuildJobStatuses.Running,
                             WorkReportStatisticRebuildJobStatuses.RetryWaiting
                         });
            var job = await _ctx.WorkReportStatisticRebuildJobs
                .Find(filter)
                .SortBy(x => x.DeadlineAtUtc)
                .FirstOrDefaultAsync(ct);
            if (job is null)
                return;

            RequireJobActivation(job);
            var nextRevision = job.StateRevision + 1;
            var nextHash = BuildStateHash(
                job.Id,
                WorkReportStatisticRebuildJobStatuses.DeadLetter,
                nextRevision,
                Math.Max(job.RetryCount, _maxRetryCount),
                null,
                null,
                job.DeadlineAtUtc,
                null,
                null,
                job.LastHeartbeatAtUtc,
                job.GenerationId,
                job.GenerationHash,
                job.ResetReceiptHistoryHash,
                WorkReportStatisticRebuildJobFreshnessStates.Pending,
                "STAT_RUN_JOB_TIMEOUT");
            var cas = filter &
                      fb.Eq(x => x.Id, job.Id) &
                      fb.Eq(x => x.CapabilityId, job.CapabilityId) &
                      fb.Eq(x => x.RouteId, job.RouteId) &
                      fb.Eq(x => x.CatalogVersion, job.CatalogVersion) &
                      fb.Eq(x => x.CatalogRawSha256, job.CatalogRawSha256) &
                      fb.Eq(x => x.CatalogSemanticSha256, job.CatalogSemanticSha256) &
                      fb.Eq(x => x.SchemaRawSha256, job.SchemaRawSha256) &
                      fb.Eq(x => x.SchemaSemanticSha256, job.SchemaSemanticSha256) &
                      fb.Eq(x => x.StageLockSha256, job.StageLockSha256) &
                      fb.Eq(x => x.ImmutableHeaderHash, job.ImmutableHeaderHash) &
                      fb.Eq(x => x.StateRevision, job.StateRevision) &
                      fb.Eq(x => x.StateHash, job.StateHash);
            await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
                cas,
                Builders<WorkReportStatisticRebuildJob>.Update
                    .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.DeadLetter)
                    .Set(x => x.IsActive, false)
                    .Set(x => x.RetryCount, Math.Max(job.RetryCount, _maxRetryCount))
                    .Set(x => x.NextRetryAtUtc, null)
                    .Set(x => x.LeaseUntilUtc, null)
                    .Set(x => x.ClaimToken, null)
                    .Set(x => x.LeaseOwnerId, null)
                    .Set(x => x.CompletedAtUtc, now)
                    .Set(x => x.DiagnosticCode, "STAT_RUN_JOB_TIMEOUT")
                    .Set(x => x.LastErrorType, null)
                    .Set(x => x.LastError, null)
                    .Set(x => x.LastErrorAtUtc, now)
                    .Set(x => x.StateRevision, nextRevision)
                    .Set(x => x.StateHash, nextHash)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, actor.Id),
                cancellationToken: ct);
        }
    }

    private async Task RequireLiveFenceAsync(
        WorkReportStatisticRebuildJob job,
        string workerId,
        string claimToken,
        MeResponse actor,
        DateTime now,
        CancellationToken ct)
    {
        if (!job.DeadlineAtUtc.HasValue)
            throw JobConflict("JOB_DEADLINE_INVALID");
        if (job.DeadlineAtUtc <= now)
        {
            var transitioned = await FailExpiredJobAsync(job, actor, now, ct);
            throw JobConflict(
                "JOB_DEADLINE_EXPIRED",
                transitioned ? 1 : 0);
        }

        RequireLiveFence(job, workerId, claimToken, now);
    }

    private async Task<bool> FailExpiredJobAsync(
        WorkReportStatisticRebuildJob job,
        MeResponse actor,
        DateTime now,
        CancellationToken ct)
    {
        var retryCount = Math.Max(job.RetryCount, _maxRetryCount);
        var nextRevision = job.StateRevision + 1;
        var nextHash = BuildStateHash(
            job.Id,
            WorkReportStatisticRebuildJobStatuses.DeadLetter,
            nextRevision,
            retryCount,
            null,
            null,
            job.DeadlineAtUtc,
            null,
            null,
            job.LastHeartbeatAtUtc,
            job.GenerationId,
            job.GenerationHash,
            job.ResetReceiptHistoryHash,
            WorkReportStatisticRebuildJobFreshnessStates.Pending,
            "STAT_RUN_JOB_TIMEOUT");
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        var filter = fb.Eq(x => x.Id, job.Id) &
                     fb.Eq(x => x.RunKind, WorkReportStatisticRebuildJobRunKinds.Foundation) &
                     fb.Ne(x => x.ReceiptId, null) &
                     fb.Eq(x => x.CandidateChainId, StatRunCapabilityActivation.RequiredChainId) &
                     fb.Eq(x => x.CapabilityId, job.CapabilityId) &
                     fb.Eq(x => x.RouteId, job.RouteId) &
                     fb.Eq(x => x.CatalogVersion, job.CatalogVersion) &
                     fb.Eq(x => x.CatalogRawSha256, job.CatalogRawSha256) &
                     fb.Eq(x => x.CatalogSemanticSha256, job.CatalogSemanticSha256) &
                     fb.Eq(x => x.SchemaRawSha256, job.SchemaRawSha256) &
                     fb.Eq(x => x.SchemaSemanticSha256, job.SchemaSemanticSha256) &
                     fb.Eq(x => x.StageLockSha256, job.StageLockSha256) &
                     fb.Eq(x => x.ImmutableHeaderHash, job.ImmutableHeaderHash) &
                     fb.Eq(x => x.IsDeleted, false) &
                     fb.Eq(x => x.IsActive, true) &
                     fb.Lte(x => x.DeadlineAtUtc, now) &
                     fb.In(x => x.Status, new[]
                     {
                         WorkReportStatisticRebuildJobStatuses.Pending,
                         WorkReportStatisticRebuildJobStatuses.Running,
                         WorkReportStatisticRebuildJobStatuses.RetryWaiting
                     }) &
                     fb.Eq(x => x.StateRevision, job.StateRevision) &
                     fb.Eq(x => x.StateHash, job.StateHash);
        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            filter,
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.Status, WorkReportStatisticRebuildJobStatuses.DeadLetter)
                .Set(x => x.IsActive, false)
                .Set(x => x.RetryCount, retryCount)
                .Set(x => x.NextRetryAtUtc, null)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.ClaimToken, null)
                .Set(x => x.LeaseOwnerId, null)
                .Set(x => x.CompletedAtUtc, now)
                .Set(x => x.FreshnessState, WorkReportStatisticRebuildJobFreshnessStates.Pending)
                .Set(x => x.StaleReason, null)
                .Set(x => x.DiagnosticCode, "STAT_RUN_JOB_TIMEOUT")
                .Set(x => x.LastErrorType, null)
                .Set(x => x.LastError, null)
                .Set(x => x.LastErrorAtUtc, now)
                .Set(x => x.StateRevision, nextRevision)
                .Set(x => x.StateHash, nextHash)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, actor.Id),
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    private async Task<WorkReportStatisticRebuildJob> RefreshExpiredJobAsync(
        WorkReportStatisticRebuildJob job,
        MeResponse actor,
        CancellationToken ct)
    {
        if (!job.IsActive ||
            !job.DeadlineAtUtc.HasValue ||
            job.DeadlineAtUtc > MongoUtcNow() ||
            job.Status is not (
                WorkReportStatisticRebuildJobStatuses.Pending or
                WorkReportStatisticRebuildJobStatuses.Running or
                WorkReportStatisticRebuildJobStatuses.RetryWaiting))
        {
            return job;
        }

        var now = MongoUtcNow();
        if (job.DeadlineAtUtc <= now)
            await FailExpiredJobAsync(job, actor, now, ct);
        var refreshed = await ReloadAdminJobAsync(job.Id, ct);
        RequireJobActivation(refreshed);
        return refreshed;
    }

    private static void RequireLiveFence(
        WorkReportStatisticRebuildJob job,
        string workerId,
        string claimToken,
        DateTime now)
    {
        if (!string.Equals(job.Status, WorkReportStatisticRebuildJobStatuses.Running, StringComparison.Ordinal) ||
            !string.Equals(job.LeaseOwnerId, workerId, StringComparison.Ordinal) ||
            !string.Equals(job.ClaimToken, claimToken, StringComparison.Ordinal) ||
            !job.LeaseUntilUtc.HasValue || job.LeaseUntilUtc <= now ||
            !job.DeadlineAtUtc.HasValue || job.DeadlineAtUtc <= now)
        {
            throw JobConflict("STALE_WORKER_FENCE");
        }
    }

    private static FilterDefinition<WorkReportStatisticRebuildJob> BuildFenceFilter(
        WorkReportStatisticRebuildJob job,
        string workerId,
        string claimToken,
        DateTime now)
    {
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        return fb.Eq(x => x.Id, job.Id) &
               fb.Eq(x => x.IsDeleted, false) &
               fb.Eq(x => x.RunKind, WorkReportStatisticRebuildJobRunKinds.Foundation) &
               fb.Ne(x => x.ReceiptId, null) &
               fb.Ne(x => x.ReceiptId, string.Empty) &
               fb.Eq(x => x.CandidateChainId, StatRunCapabilityActivation.RequiredChainId) &
               fb.Eq(x => x.CapabilityId, job.CapabilityId) &
               fb.Eq(x => x.RouteId, job.RouteId) &
               fb.Eq(x => x.CatalogVersion, job.CatalogVersion) &
               fb.Eq(x => x.CatalogRawSha256, job.CatalogRawSha256) &
               fb.Eq(x => x.CatalogSemanticSha256, job.CatalogSemanticSha256) &
               fb.Eq(x => x.SchemaRawSha256, job.SchemaRawSha256) &
               fb.Eq(x => x.SchemaSemanticSha256, job.SchemaSemanticSha256) &
               fb.Eq(x => x.StageLockSha256, job.StageLockSha256) &
               fb.Eq(x => x.ImmutableHeaderHash, job.ImmutableHeaderHash) &
               fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Running) &
               fb.Eq(x => x.LeaseOwnerId, workerId) &
               fb.Eq(x => x.ClaimToken, claimToken) &
               fb.Gt(x => x.LeaseUntilUtc, now) &
               fb.Gt(x => x.DeadlineAtUtc, now) &
               fb.Eq(x => x.StateRevision, job.StateRevision) &
               fb.Eq(x => x.StateHash, job.StateHash);
    }
}
