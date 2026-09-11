using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class StatRunService : IStatRunFoundationWorkerStateOwner
{

    async Task<StatRunWorkerLeaseResponse?>
        IStatRunFoundationWorkerStateOwner.ClaimDirectAsync(
            StatRunFoundationWorkerIdentity identity,
            CancellationToken ct)
    {
        identity.RequireFoundationPurpose();
        RequireProductionDirectActivation();
        await FailExpiredProductionDirectJobsAsync(ct);

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var now = MongoUtcNow();
            var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
            var eligible = ProductionDirectIdentityFilter() &
                           fb.Eq(x => x.IsActive, true) &
                           (fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Pending) |
                            fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.RetryWaiting) |
                            (fb.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Running) &
                             fb.Lte(x => x.LeaseUntilUtc, now))) &
                           (fb.Eq(x => x.NextRetryAtUtc, null) |
                            fb.Lte(x => x.NextRetryAtUtc, now)) &
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

            RequireProductionDirectJob(candidate);
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
                identity.WorkerId,
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
                    .Set(x => x.LeaseOwnerId, identity.WorkerId)
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
                    .Set(x => x.UpdatedByUserId, null),
                cancellationToken: ct);
            if (result.ModifiedCount != 1)
                continue;

            var claimed = await ReloadAdminJobAsync(candidate.Id, ct);
            RequireProductionDirectJob(claimed);
            return new StatRunWorkerLeaseResponse
            {
                WorkerId = identity.WorkerId,
                ClaimToken = claimToken,
                LeaseUntilUtc = leaseUntil,
                Job = await ToResponseAsync(claimed, false, ct)
            };
        }

        throw JobConflict("CLAIM_CONTENTION");
    }

    async Task IStatRunFoundationWorkerStateOwner.HeartbeatAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        CancellationToken ct)
    {
        identity.RequireFoundationPurpose();
        RequireLeaseIdentity(identity, lease);
        var job = await ReloadAdminJobAsync(lease.Job.JobId, ct);
        RequireProductionDirectJob(job);
        var now = MongoUtcNow();
        await RequireProductionLiveFenceAsync(job, identity, lease.ClaimToken, now, ct);
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
            lease.ClaimToken,
            identity.WorkerId,
            now,
            job.GenerationId,
            job.GenerationHash,
            job.ResetReceiptHistoryHash,
            job.FreshnessState ?? WorkReportStatisticRebuildJobFreshnessStates.Pending,
            job.DiagnosticCode);
        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            BuildProductionFenceFilter(job, identity, lease.ClaimToken, now),
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.LeaseUntilUtc, leaseUntil)
                .Set(x => x.LastHeartbeatAtUtc, now)
                .Set(x => x.StateRevision, nextRevision)
                .Set(x => x.StateHash, nextHash)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);
        if (result.ModifiedCount != 1)
            throw JobConflict("STALE_WORKER_FENCE");
    }

    async Task IStatRunFoundationWorkerStateOwner.CompleteAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        StatRunFoundationProjectionReceipt projection,
        CancellationToken ct)
    {
        identity.RequireFoundationPurpose();
        RequireLeaseIdentity(identity, lease);
        ArgumentNullException.ThrowIfNull(projection);
        if (!ObjectId.TryParse(projection.ProjectionRunId, out _) ||
            !StatRunCanonicalJson.IsCanonicalSha256(projection.GenerationId) ||
            !StatRunCanonicalJson.IsCanonicalSha256(projection.GenerationHash))
        {
            throw JobConflict("DIRECT_PROJECTION_RECEIPT_INVALID");
        }

        var job = await ReloadAdminJobAsync(lease.Job.JobId, ct);
        RequireProductionDirectJob(job);
        var now = MongoUtcNow();
        await RequireProductionLiveFenceAsync(job, identity, lease.ClaimToken, now, ct);
        var nextRevision = job.StateRevision + 1;
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
            projection.GenerationId,
            projection.GenerationHash,
            job.ResetReceiptHistoryHash,
            WorkReportStatisticRebuildJobFreshnessStates.Fresh,
            null);
        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            BuildProductionFenceFilter(job, identity, lease.ClaimToken, now),
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
                .Set(x => x.GenerationId, projection.GenerationId)
                .Set(x => x.GenerationHash, projection.GenerationHash)
                .Set(x => x.FreshnessState, WorkReportStatisticRebuildJobFreshnessStates.Fresh)
                .Set(x => x.StaleReason, null)
                .Set(x => x.DiagnosticCode, null)
                .Set(x => x.StateRevision, nextRevision)
                .Set(x => x.StateHash, nextHash)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);
        if (result.ModifiedCount != 1)
            throw JobConflict("STALE_WORKER_FENCE");
    }

    async Task IStatRunFoundationWorkerStateOwner.ReleaseFailureAsync(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease,
        StatRunFoundationFailure failure,
        CancellationToken ct)
    {
        identity.RequireFoundationPurpose();
        RequireLeaseIdentity(identity, lease);
        if (!StatRunFoundationDiagnostics.IsAcceptedFailure(failure))
            throw JobConflict("FOUNDATION_DIAGNOSTIC_INVALID");
        var job = await ReloadAdminJobAsync(lease.Job.JobId, ct);
        RequireProductionDirectJob(job);
        var now = MongoUtcNow();
        await RequireProductionLiveFenceAsync(job, identity, lease.ClaimToken, now, ct);
        var transition = PlanFoundationFailure(
            job.RetryCount,
            _maxRetryCount,
            failure.Disposition,
            now);
        var nextRevision = job.StateRevision + 1;
        var nextHash = BuildStateHash(
            job.Id,
            transition.Status,
            nextRevision,
            transition.RetryCount,
            transition.NextRetryAtUtc,
            null,
            job.DeadlineAtUtc,
            null,
            null,
            job.LastHeartbeatAtUtc,
            job.GenerationId,
            job.GenerationHash,
            job.ResetReceiptHistoryHash,
            WorkReportStatisticRebuildJobFreshnessStates.Pending,
            failure.DiagnosticCode);
        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            BuildProductionFenceFilter(job, identity, lease.ClaimToken, now),
            Builders<WorkReportStatisticRebuildJob>.Update
                .Set(x => x.Status, transition.Status)
                .Set(x => x.IsActive, transition.IsActive)
                .Set(x => x.RetryCount, transition.RetryCount)
                .Set(x => x.NextRetryAtUtc, transition.NextRetryAtUtc)
                .Set(x => x.LeaseUntilUtc, null)
                .Set(x => x.ClaimToken, null)
                .Set(x => x.LeaseOwnerId, null)
                .Set(x => x.DiagnosticCode, failure.DiagnosticCode)
                .Set(x => x.LastErrorType, null)
                .Set(x => x.LastError, null)
                .Set(x => x.LastErrorAtUtc, now)
                .Set(x => x.CompletedAtUtc, transition.CompletedAtUtc)
                .Inc(x => x.FailedReportCount, 1)
                .Set(x => x.FreshnessState, WorkReportStatisticRebuildJobFreshnessStates.Pending)
                .Set(x => x.StateRevision, nextRevision)
                .Set(x => x.StateHash, nextHash)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);
        if (result.ModifiedCount != 1)
            throw JobConflict("STALE_WORKER_FENCE");
    }

    internal static FoundationFailureTransition PlanFoundationFailure(
        int currentRetryCount,
        int maxRetryCount,
        StatRunFoundationFailureDisposition disposition,
        DateTime now)
    {
        if (currentRetryCount < 0 || maxRetryCount < 1 || currentRetryCount > maxRetryCount)
            throw new ArgumentOutOfRangeException(nameof(currentRetryCount));
        now = DateTime.SpecifyKind(now, DateTimeKind.Utc);
        var retryCount = currentRetryCount < maxRetryCount
            ? checked(currentRetryCount + 1)
            : currentRetryCount;
        var terminal = disposition == StatRunFoundationFailureDisposition.Terminal ||
                       retryCount >= maxRetryCount;
        return new FoundationFailureTransition(
            terminal
                ? WorkReportStatisticRebuildJobStatuses.DeadLetter
                : WorkReportStatisticRebuildJobStatuses.RetryWaiting,
            retryCount,
            terminal ? null : now.AddMinutes(Math.Min(60, retryCount * 5)),
            !terminal,
            terminal ? now : null);
    }

    private void RequireProductionDirectActivation()
        => _activation.RequireFoundation(
            StatRunCapabilities.DirectFieldTableLabel,
            StatRunRouteRegistry.CoreJob(StatRunCapabilities.DirectFieldTableLabel));

    private void RequireProductionDirectJob(WorkReportStatisticRebuildJob job)
    {
        RequireJobActivation(job);
        if (!string.Equals(
                job.CapabilityId,
                StatRunCapabilities.DirectFieldTableLabel,
                StringComparison.Ordinal))
        {
            throw JobConflict("FOUNDATION_CAPABILITY_NOT_OWNED");
        }
    }

    private static void RequireLeaseIdentity(
        StatRunFoundationWorkerIdentity identity,
        StatRunWorkerLeaseResponse lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!string.Equals(lease.WorkerId, identity.WorkerId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(lease.ClaimToken) ||
            lease.Job is null)
        {
            throw JobConflict("STALE_WORKER_FENCE");
        }
    }

    private FilterDefinition<WorkReportStatisticRebuildJob>
        ProductionDirectIdentityFilter()
    {
        var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
        return fb.Eq(x => x.IsDeleted, false) &
               fb.Eq(x => x.RunKind, WorkReportStatisticRebuildJobRunKinds.Foundation) &
               fb.Ne(x => x.ReceiptId, null) &
               fb.Ne(x => x.ReceiptId, string.Empty) &
               fb.Eq(x => x.CandidateChainId, StatRunCapabilityActivation.RequiredChainId) &
               fb.Eq(x => x.CapabilityId, StatRunCapabilities.DirectFieldTableLabel);
    }

    private FilterDefinition<WorkReportStatisticRebuildJob>
        BuildProductionFenceFilter(
            WorkReportStatisticRebuildJob job,
            StatRunFoundationWorkerIdentity identity,
            string claimToken,
            DateTime now)
        => ProductionDirectIdentityFilter() &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.Id, job.Id) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.CapabilityId, job.CapabilityId) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.RouteId, job.RouteId) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.CatalogVersion, job.CatalogVersion) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.CatalogRawSha256, job.CatalogRawSha256) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.CatalogSemanticSha256, job.CatalogSemanticSha256) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.SchemaRawSha256, job.SchemaRawSha256) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.SchemaSemanticSha256, job.SchemaSemanticSha256) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.StageLockSha256, job.StageLockSha256) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.ImmutableHeaderHash, job.ImmutableHeaderHash) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.Status, WorkReportStatisticRebuildJobStatuses.Running) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.LeaseOwnerId, identity.WorkerId) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.ClaimToken, claimToken) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Gt(x => x.LeaseUntilUtc, now) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Gt(x => x.DeadlineAtUtc, now) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.StateRevision, job.StateRevision) &
           Builders<WorkReportStatisticRebuildJob>.Filter.Eq(x => x.StateHash, job.StateHash);

    private async Task RequireProductionLiveFenceAsync(
        WorkReportStatisticRebuildJob job,
        StatRunFoundationWorkerIdentity identity,
        string claimToken,
        DateTime now,
        CancellationToken ct)
    {
        if (!job.DeadlineAtUtc.HasValue)
            throw JobConflict("JOB_DEADLINE_INVALID");
        if (job.DeadlineAtUtc <= now)
        {
            var transitioned = await FailExpiredProductionDirectJobAsync(job, now, ct);
            throw JobConflict("JOB_DEADLINE_EXPIRED", transitioned ? 1 : 0);
        }
        RequireLiveFence(job, identity.WorkerId, claimToken, now);
    }

    private async Task FailExpiredProductionDirectJobsAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var now = MongoUtcNow();
            var fb = Builders<WorkReportStatisticRebuildJob>.Filter;
            var filter = ProductionDirectIdentityFilter() &
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
            RequireProductionDirectJob(job);
            await FailExpiredProductionDirectJobAsync(job, now, ct);
        }
    }

    private async Task<bool> FailExpiredProductionDirectJobAsync(
        WorkReportStatisticRebuildJob job,
        DateTime now,
        CancellationToken ct)
    {
        var retryCount = CountDeadlineAttempts(job, _maxRetryCount);
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
        var result = await _ctx.WorkReportStatisticRebuildJobs.UpdateOneAsync(
            ProductionDirectIdentityFilter() &
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
            fb.Eq(x => x.CandidateChainId, job.CandidateChainId) &
            fb.Eq(x => x.IsActive, true) &
            fb.Lte(x => x.DeadlineAtUtc, now) &
            fb.In(x => x.Status, new[]
            {
                WorkReportStatisticRebuildJobStatuses.Pending,
                WorkReportStatisticRebuildJobStatuses.Running,
                WorkReportStatisticRebuildJobStatuses.RetryWaiting
            }) &
            fb.Eq(x => x.StateRevision, job.StateRevision) &
            fb.Eq(x => x.StateHash, job.StateHash),
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
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    internal static int CountDeadlineAttempts(
        WorkReportStatisticRebuildJob job,
        int maxRetryCount)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.RetryCount < 0 || maxRetryCount < 1 || job.RetryCount > maxRetryCount)
            throw new ArgumentOutOfRangeException(nameof(job));
        return string.Equals(
                job.Status,
                WorkReportStatisticRebuildJobStatuses.Running,
                StringComparison.Ordinal) && job.RetryCount < maxRetryCount
            ? checked(job.RetryCount + 1)
            : job.RetryCount;
    }
}

internal sealed record FoundationFailureTransition(
    string Status,
    int RetryCount,
    DateTime? NextRetryAtUtc,
    bool IsActive,
    DateTime? CompletedAtUtc);
