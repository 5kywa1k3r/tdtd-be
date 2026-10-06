using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.Progress;

namespace tdtd_be.Services.WorkAssignments.Runtime;

public sealed class WorkAssignmentStatusSyncService : IWorkAssignmentStatusSyncService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentProgressService _progress;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly ILogger<WorkAssignmentStatusSyncService> _log;

    public WorkAssignmentStatusSyncService(
        MongoDbContext ctx,
        IWorkAssignmentProgressService progress,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IWorkStatusOperationLogService statusLog,
        ILogger<WorkAssignmentStatusSyncService> log)
    {
        _ctx = ctx;
        _progress = progress;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _statusLog = statusLog;
        _log = log;
    }

    public Task SyncFromAssignmentAsync(
        string workAssignmentId,
        CancellationToken ct = default)
        => SyncFromAssignmentCoreAsync(
            workAssignmentId,
            idempotencyKey: null,
            ct);

    public Task SyncFromAssignmentIdempotentAsync(
        string workAssignmentId,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException(
                "An idempotency key is required.",
                nameof(idempotencyKey));
        }

        return SyncFromAssignmentCoreAsync(
            workAssignmentId,
            idempotencyKey.Trim(),
            ct);
    }

    private async Task SyncFromAssignmentCoreAsync(
        string workAssignmentId,
        string? idempotencyKey,
        CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        string? workId = null;
        string? assignmentFromStatus = null;
        string? assignmentToStatus = null;
        string? workFromStatus = null;
        string? workToStatus = null;
        var rebuiltAssignmentCount = 0;
        var parentDepth = 0;

        try
        {
            var current = await _ctx.WorkAssignments
                .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);

            if (current is null)
            {
                _log.LogInformation(
                    "WorkAssignment status sync skipped. assignmentId={assignmentId} reason=missing-or-deleted",
                    workAssignmentId);

                await WriteStatusLogAsync(new WorkStatusOperationLog
                {
                    Operation = "ASSIGNMENT_STATUS_SYNC",
                    Scope = "assignment",
                    Result = "SKIPPED",
                    WorkAssignmentId = workAssignmentId,
                    Summary = "Assignment not found or deleted.",
                    StartedAtUtc = startedAtUtc
                }, startedAtUtc, idempotencyKey, ct);
                return;
            }

            workId = current.WorkId;
            assignmentFromStatus = current.ProgressStatus.ToString();

            await _progress.RecomputeSingleAsync(current, ct);
            await _docRoleReadModelProjection.RebuildAssignmentAsync(current.Id, "system", ct);
            rebuiltAssignmentCount++;

            var refreshed = await _ctx.WorkAssignments
                .Find(x => x.Id == current.Id && !x.IsDeleted)
                .FirstOrDefaultAsync(ct);

            assignmentToStatus = refreshed?.ProgressStatus.ToString();

            while (!string.IsNullOrWhiteSpace(current.ParentAssignmentId))
            {
                var parentId = current.ParentAssignmentId!;
                await RebuildParentAggregateAsync(parentId, ct);
                rebuiltAssignmentCount++;
                parentDepth++;

                current = await _ctx.WorkAssignments
                    .Find(x => x.Id == parentId && !x.IsDeleted)
                    .FirstOrDefaultAsync(ct);

                if (current is null)
                    break;

                await _docRoleReadModelProjection.RebuildAssignmentAsync(current.Id, "system", ct);
            }

            if (current is not null)
            {
                var workBefore = await _ctx.Works
                    .Find(x => x.Id == current.WorkId && !x.IsDeleted)
                    .FirstOrDefaultAsync(ct);
                workFromStatus = workBefore is null ? null : ((int)workBefore.Status).ToString();

                workToStatus = ((int)await RebuildWorkAggregateAsync(current.WorkId, ct)).ToString();
            }

            _log.LogInformation(
                "WorkAssignment status sync completed. assignmentId={assignmentId} workId={workId} rebuiltAssignments={rebuiltAssignments} parentDepth={parentDepth}",
                workAssignmentId,
                workId,
                rebuiltAssignmentCount,
                parentDepth);

            await WriteStatusLogAsync(new WorkStatusOperationLog
            {
                Operation = "ASSIGNMENT_STATUS_SYNC",
                Scope = "assignment",
                Result = "SUCCESS",
                WorkId = workId,
                WorkAssignmentId = workAssignmentId,
                AssignmentFromStatus = assignmentFromStatus,
                AssignmentToStatus = assignmentToStatus,
                WorkFromStatus = workFromStatus,
                WorkToStatus = workToStatus,
                Summary = $"rebuiltAssignments={rebuiltAssignmentCount};parentDepth={parentDepth}",
                StartedAtUtc = startedAtUtc
            }, startedAtUtc, idempotencyKey, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "WorkAssignment status sync failed. assignmentId={assignmentId} workId={workId} assignmentFromStatus={assignmentFromStatus} assignmentToStatus={assignmentToStatus} workFromStatus={workFromStatus} workToStatus={workToStatus} rebuiltAssignments={rebuiltAssignments} parentDepth={parentDepth}",
                workAssignmentId,
                workId,
                assignmentFromStatus,
                assignmentToStatus,
                workFromStatus,
                workToStatus,
                rebuiltAssignmentCount,
                parentDepth);

            await WriteStatusLogAsync(new WorkStatusOperationLog
            {
                Operation = "ASSIGNMENT_STATUS_SYNC",
                Scope = "assignment",
                Result = "FAILED",
                WorkId = workId,
                WorkAssignmentId = workAssignmentId,
                AssignmentFromStatus = assignmentFromStatus,
                AssignmentToStatus = assignmentToStatus,
                WorkFromStatus = workFromStatus,
                WorkToStatus = workToStatus,
                Summary = $"rebuiltAssignments={rebuiltAssignmentCount};parentDepth={parentDepth}",
                ErrorType = ex.GetType().FullName,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.ToString(),
                StartedAtUtc = startedAtUtc
            }, startedAtUtc, idempotencyKey, ct);

            throw;
        }
    }

    public async Task RebuildWorkSnapshotsAsync(string workId, CancellationToken ct = default)
    {
        var nodes = await _ctx.WorkAssignments.Find(x => x.WorkId == workId && !x.IsDeleted)
            .SortByDescending(x => x.Level).ToListAsync(ct);
        foreach (var node in nodes) await RebuildParentAggregateAsync(node.Id, ct);
        await RebuildWorkAggregateAsync(workId, ct); // Including zero active roots.
    }

    private async Task RebuildParentAggregateAsync(string parentAssignmentId, CancellationToken ct)
    {
        var children = await _ctx.WorkAssignments
            .Find(x => x.ParentAssignmentId == parentAssignmentId && x.IsActive && !x.IsDeleted)
            .ToListAsync(ct);

        var snapshot = new WorkProgressCountSnapshot();
        foreach (var child in children)
        {
            snapshot.Add((WorkAssignmentProgressStatus)child.ProgressStatus);
        }

        var worstChildProgress = children.Count == 0 ? (int?)null : (int)snapshot.GetWorstStatus();
        var worstChild = children
            .OrderByDescending(GetWorstPeriodRank)
            .ThenByDescending(GetWorstReasonRank)
            .ThenByDescending(x => x.ProgressStatus)
            .ThenByDescending(x => x.LatestDueAtUtc)
            .FirstOrDefault();

        await _ctx.WorkAssignments.UpdateOneAsync(
            x => x.Id == parentAssignmentId && !x.IsDeleted,
            Builders<WorkAssignment>.Update
                .Set(x => x.ActiveChildCount, children.Count)
                .Set(x => x.ChildProgressCounts, snapshot)
                .Set(x => x.WorstChildProgressStatus, worstChildProgress)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedByUserId, (string?)null),
            cancellationToken: ct);

        await _progress.RecomputeSingleAsync(parentAssignmentId, ct);
        await _docRoleReadModelProjection.RebuildAssignmentAsync(parentAssignmentId, "system", ct);
    }

    private async Task<WorkStatus> RebuildWorkAggregateAsync(string workId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
        // Read the fence before roots. Child lifecycle/relationship transactions advance it.
        var work = await _ctx.Works.Find(x => x.Id == workId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        if (work is null) return WorkStatus.S1;
        var roots = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId &&
                       x.ParentAssignmentId == null &&
                       x.IsActive &&
                       !x.IsDeleted)
            .ToListAsync(ct);

        var snapshot = new WorkProgressCountSnapshot();
        foreach (var root in roots)
        {
            snapshot.Add((WorkAssignmentProgressStatus)root.ProgressStatus);
        }

        var firstApproval = work.FirstApprovedAtUtc ?? roots.Where(x => x.FirstApprovedAtUtc.HasValue)
            .Select(x => x.FirstApprovedAtUtc).Min();
        var mappedWorkStatus = (work.CompletedAtUtc.HasValue || work.Status == WorkStatus.S3)
            ? WorkStatus.S3
            : string.IsNullOrWhiteSpace(work.DynamicFlowRuntimeInstanceId)
                ? (firstApproval.HasValue ? WorkStatus.S2 : WorkStatus.S1)
                : MapToWorkStatus(snapshot);

        var filter = Builders<Work>.Filter;
        var sourceFence = work.DirectSourceRevision == 0
            ? filter.Eq(x => x.DirectSourceRevision, 0) | filter.Exists(x => x.DirectSourceRevision, false)
            : filter.Eq(x => x.DirectSourceRevision, work.DirectSourceRevision);
        var result = await _ctx.Works.UpdateOneAsync(
            filter.Eq(x => x.Id, workId) & filter.Eq(x => x.IsDeleted, false) &
            filter.Eq(x => x.UpdatedAtUtc, work.UpdatedAtUtc) &
            filter.Eq(x => x.CompletedAtUtc, work.CompletedAtUtc) & sourceFence,
            Builders<Work>.Update
                .Set(x => x.ActiveRootAssignmentCount, roots.Count)
                .Set(x => x.RootAssignmentProgressCounts, snapshot)
                .Set(x => x.Status, mappedWorkStatus)
                .Set(x => x.FirstApprovedAtUtc, firstApproval)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedByUserId, (string?)null),
            cancellationToken: ct);

        if (result.MatchedCount == 0) continue;

        await _docRoleReadModelProjection.RebuildWorkAsync(workId, "system", ct);
        return mappedWorkStatus;
        }
        throw new InvalidOperationException("WORK_PROGRESS_SNAPSHOT_CONFLICT");
    }

    private async Task WriteStatusLogAsync(
        WorkStatusOperationLog log,
        DateTime startedAtUtc,
        string? idempotencyKey,
        CancellationToken ct)
    {
        var completedAtUtc = DateTime.UtcNow;
        log.CompletedAtUtc = completedAtUtc;
        log.DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _statusLog.WriteAsync(log, ct);
            return;
        }

        await _statusLog.WriteIdempotentAsync(
            $"{idempotencyKey}:{log.Result.Trim().ToUpperInvariant()}",
            log,
            ct);
    }

    private static WorkStatus MapToWorkStatus(WorkProgressCountSnapshot snapshot)
    {
        if (snapshot.Overdue > 0) return WorkStatus.S5;
        if (snapshot.AtRiskOverdue > 0) return WorkStatus.S4;
        if (snapshot.InProgress > 0) return WorkStatus.S2;
        if (snapshot.Completed > 0) return WorkStatus.S2;
        if (snapshot.NotStarted > 0) return WorkStatus.S1;
        return WorkStatus.S1;
    }

    private static int GetWorstPeriodRank(WorkAssignment assignment)
    {
        return assignment.WorstPeriodStatus.HasValue
            ? WorkReportPeriodStatusHelper.GetPeriodRiskRank((WorkReportPeriodStatus)assignment.WorstPeriodStatus.Value)
            : -1;
    }

    private static int GetWorstReasonRank(WorkAssignment assignment)
    {
        return assignment.WorstOverdueReasonCode switch
        {
            "OVERDUE_SUBMITTED_WAITING_REVIEW" => 3,
            "OVERDUE_DRAFT" => 2,
            "OVERDUE_NOT_STARTED" => 1,
            _ => 0
        };
    }
}
