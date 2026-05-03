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

    public async Task SyncFromAssignmentAsync(string workAssignmentId, CancellationToken ct = default)
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
                }, startedAtUtc, ct);
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
            }, startedAtUtc, ct);
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
            }, startedAtUtc, ct);

            throw;
        }
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
                .Set(x => x.WorstPeriodStatus, worstChild?.WorstPeriodStatus)
                .Set(x => x.WorstOverdueReasonCode, worstChild?.WorstOverdueReasonCode)
                .Set(x => x.WorstOverdueReasonLabel, worstChild?.WorstOverdueReasonLabel)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedByUserId, (string?)null),
            cancellationToken: ct);

        await _progress.RecomputeSingleAsync(parentAssignmentId, ct);
        await _docRoleReadModelProjection.RebuildAssignmentAsync(parentAssignmentId, "system", ct);
    }

    private async Task<WorkStatus> RebuildWorkAggregateAsync(string workId, CancellationToken ct)
    {
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

        var mappedWorkStatus = MapToWorkStatus(snapshot);

        await _ctx.Works.UpdateOneAsync(
            x => x.Id == workId && !x.IsDeleted,
            Builders<Work>.Update
                .Set(x => x.ActiveRootAssignmentCount, roots.Count)
                .Set(x => x.RootAssignmentProgressCounts, snapshot)
                .Set(x => x.Status, mappedWorkStatus)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedByUserId, (string?)null),
            cancellationToken: ct);

        await _docRoleReadModelProjection.RebuildWorkAsync(workId, "system", ct);
        return mappedWorkStatus;
    }

    private async Task WriteStatusLogAsync(
        WorkStatusOperationLog log,
        DateTime startedAtUtc,
        CancellationToken ct)
    {
        var completedAtUtc = DateTime.UtcNow;
        log.CompletedAtUtc = completedAtUtc;
        log.DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        await _statusLog.WriteAsync(log, ct);
    }

    private static WorkStatus MapToWorkStatus(WorkProgressCountSnapshot snapshot)
    {
        if (snapshot.Overdue > 0) return WorkStatus.S5;
        if (snapshot.AtRiskOverdue > 0) return WorkStatus.S4;
        if (snapshot.InProgress > 0) return WorkStatus.S2;
        if (snapshot.NotStarted > 0) return WorkStatus.S1;
        if (snapshot.Completed > 0) return WorkStatus.S3;
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
