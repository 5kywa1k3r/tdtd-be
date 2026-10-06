using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Queue;

namespace tdtd_be.Services.WorkAssignments.Runtime;

public sealed class WorkAssignmentQueueJobService : IWorkAssignmentQueueJobService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentStatusSyncService _sync;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly IWorkReportLifecycleSeriesLockService _lifecycleSeriesLock;
    private readonly IDynamicFlowDefinitionTransactionRunner _transactions;
    private readonly ILogger<WorkAssignmentQueueJobService> _log;

    public WorkAssignmentQueueJobService(
        MongoDbContext ctx,
        IWorkAssignmentStatusSyncService sync,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IWorkStatusOperationLogService statusLog,
        IWorkReportLifecycleSeriesLockService lifecycleSeriesLock,
        IDynamicFlowDefinitionTransactionRunner transactions,
        ILogger<WorkAssignmentQueueJobService> log)
    {
        _ctx = ctx;
        _sync = sync;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _statusLog = statusLog;
        _lifecycleSeriesLock = lifecycleSeriesLock;
        _transactions = transactions;
        _log = log;
    }

    public async Task ScanDuePeriodsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var startedAtUtc = now;
        var queueItems = await _ctx.WorkAssignmentQueueItems
            .Find(x => x.IsActive && !x.IsDeleted && x.NextScanAtUtc <= now)
            .SortBy(x => x.NextScanAtUtc)
            .Limit(2000)
            .ToListAsync(ct);

        var assignmentIds = queueItems
            .Select(x => x.WorkAssignmentId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var assignments = assignmentIds.Count == 0
            ? new List<WorkAssignment>()
            : await _ctx.WorkAssignments
                .Find(x => assignmentIds.Contains(x.Id) && !x.IsDeleted)
                .ToListAsync(ct);

        var assignmentById = assignments
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToDictionary(x => x.Id!, StringComparer.Ordinal);

        var ancestorIds = assignments
            .SelectMany(ResolveAncestorIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var completedAssignmentIds = ancestorIds.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await _ctx.WorkAssignments
                .Find(x =>
                    ancestorIds.Contains(x.Id) &&
                    !x.IsDeleted &&
                    (x.CompletedAtUtc != null ||
                     (x.ProgressStatus == (int)WorkAssignmentProgressStatus.Completed && x.CompletedDate != null)))
                .Project(x => x.Id)
                .ToListAsync(ct))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.Ordinal);

        var workIds = queueItems
            .Select(x => x.WorkId)
            .Concat(assignments.Select(x => x.WorkId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var works = workIds.Count == 0
            ? new List<Work>()
            : await _ctx.Works
                .Find(x => workIds.Contains(x.Id) && !x.IsDeleted)
                .ToListAsync(ct);

        var workById = works
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToDictionary(x => x.Id!, StringComparer.Ordinal);

        var changed = 0;
        var disabled = 0;
        var missingPeriod = 0;
        var scanned = 0;
        var failed = 0;

        foreach (var item in queueItems)
        {
            scanned++;
            WorkReportLifecycleSeriesLease? lifecycleSeriesLease = null;
            try
            {
                assignmentById.TryGetValue(item.WorkAssignmentId, out var assignment);
                var workId = assignment?.WorkId ?? item.WorkId;
                workById.TryGetValue(workId, out var work);

                if (assignment is null ||
                    !assignment.IsActive ||
                    IsCompletionLocked(assignment, work, completedAssignmentIds))
                {
                    if (await DisableQueueItemAsync(item, now, ct))
                        disabled++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(assignment.FlowInstanceId))
                {
                    try
                    {
                        lifecycleSeriesLease = await _lifecycleSeriesLock.AcquireAsync(
                            assignment.Id!,
                            WorkReportLifecycleSeriesOperations.QueueDueScan,
                            ct);
                    }
                    catch (AppException ex) when (
                        ex.Code ==
                        AppErrorCode.WORK_ASSIGNMENT_REPORT_LIFECYCLE_REVISION_CONFLICT)
                    {
                        // A report transition, completion, or runtime repair owns this
                        // assignment series. Leave the observed queue row untouched so the
                        // next bounded scan derives from the committed lifecycle state.
                        continue;
                    }

                    assignment = await _ctx.WorkAssignments
                        .Find(x => x.Id == item.WorkAssignmentId && !x.IsDeleted)
                        .FirstOrDefaultAsync(ct);
                    work = assignment is null
                        ? null
                        : await _ctx.Works
                            .Find(x => x.Id == assignment.WorkId && !x.IsDeleted)
                            .FirstOrDefaultAsync(ct);
                    var freshCompletedAssignmentIds = assignment is null
                        ? new HashSet<string>(StringComparer.Ordinal)
                        : await LoadCompletedAncestorIdsAsync(assignment, ct);
                    if (assignment is null ||
                        !assignment.IsActive ||
                        IsCompletionLocked(
                            assignment,
                            work,
                            freshCompletedAssignmentIds))
                    {
                        if (await DisableQueueItemAsync(item, now, ct))
                            disabled++;
                        continue;
                    }
                }

                var period = await _ctx.WorkReportPeriods
                    .Find(x => x.WorkAssignmentId == item.WorkAssignmentId &&
                               x.AssigneeUserId == item.AssigneeUserId &&
                               x.PeriodKey == item.PeriodKey &&
                               (x.PeriodKind == null || x.PeriodKind == WorkReportPeriodKind.Scheduled) &&
                               !x.IsDeleted)
                    .FirstOrDefaultAsync(ct);

                if (period is null || !period.IsActive)
                {
                    if (await DisableQueueItemAsync(item, now, ct))
                        disabled++;
                    missingPeriod++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(assignment.FlowInstanceId) &&
                    !await tdtd_be.Services.WorkAssignments.Progress.WorkExecutionScopeGuard.IsOpenAsync(_ctx, assignment, period.Id, ct))
                {
                    if (await DisableQueueItemAsync(item, now, ct)) disabled++;
                    continue;
                }

                var isHistoricalBackfill =
                    period.IsHistoricalData ||
                    WorkAssignmentBackfillPeriodPolicy.IsBackfillHistoricalPeriod(
                        assignment,
                        period.PeriodStart,
                        period.PeriodEnd,
                        period.DueAtUtc ?? period.ReportDate,
                        now);

                if (isHistoricalBackfill)
                {
                    var healedStatus = period.Status;
                    var healedIsOverdue = period.IsOverdue;
                    if (period.Status == WorkReportPeriodStatus.Pending ||
                        period.Status == WorkReportPeriodStatus.OverduePending)
                    {
                        healedStatus = WorkReportPeriodStatus.Pending;
                        healedIsOverdue = false;
                    }

                    if (!period.IsHistoricalData ||
                        period.Status != healedStatus ||
                        period.IsOverdue != healedIsOverdue)
                    {
                        var periodUpdated =
                            await UpdatePeriodWithProgressFenceAsync(
                            period,
                            Builders<WorkReportPeriod>.Update
                                .Set(x => x.IsHistoricalData, true)
                                .Set(x => x.Status, healedStatus)
                                .Set(x => x.IsOverdue, healedIsOverdue)
                                .Set(x => x.UpdatedAtUtc, now)
                                .Set(x => x.UpdatedByUserId, null),
                            ct);
                        if (!periodUpdated)
                            continue;

                        await _sync.SyncFromAssignmentAsync(period.WorkAssignmentId, ct);
                        await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, "system", ct);
                        changed++;
                    }

                    if (await DisableQueueItemAsync(item, now, ct))
                        disabled++;
                    continue;
                }

                var oldStatus = period.Status;
                var nextStatus = WorkReportPeriodStatusHelper.ResolveDueScanStatus(
                    oldStatus,
                    period.IsOverdue,
                    period.DueAtUtc,
                    now);

                if (nextStatus != oldStatus)
                {
                    var periodUpdated =
                        await UpdatePeriodWithProgressFenceAsync(
                        period,
                        Builders<WorkReportPeriod>.Update
                            .Set(x => x.Status, nextStatus)
                            .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextStatus))
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, null),
                        ct);
                    if (!periodUpdated)
                        continue;

                    await _sync.SyncFromAssignmentAsync(period.WorkAssignmentId, ct);
                    await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, "system", ct);
                    changed++;
                }

                var shouldDisableQueue = !WorkReportPeriodStatusHelper.ShouldKeepQueueActive(nextStatus);

                var queueUpdate = Builders<WorkAssignmentQueueItem>.Update
                    .Set(x => x.LastScannedAtUtc, now)
                    .Set(x => x.LastObservedPeriodStatus, (int)nextStatus)
                    .Set(x => x.UpdatedAtUtc, now)
                    .Set(x => x.UpdatedByUserId, null);

                queueUpdate = shouldDisableQueue
                    ? queueUpdate.Set(x => x.IsActive, false)
                    : queueUpdate.Set(x => x.NextScanAtUtc, period.DueAtUtc ?? now.AddHours(6));

                var queueResult = await _ctx.WorkAssignmentQueueItems.UpdateOneAsync(
                    BuildObservedQueueFilter(item),
                    queueUpdate,
                    cancellationToken: ct);

                if (shouldDisableQueue && queueResult.MatchedCount == 1)
                    disabled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;

                _log.LogError(
                    ex,
                    "WorkAssignmentQueue item scan failed. queueItemId={queueItemId} workId={workId} assignmentId={assignmentId} assigneeUserId={assigneeUserId} periodKey={periodKey} dueAtUtc={dueAtUtc}",
                    item.Id,
                    item.WorkId,
                    item.WorkAssignmentId,
                    item.AssigneeUserId,
                    item.PeriodKey,
                    item.DueAtUtc);

                await WriteStatusOperationLogAsync(new WorkStatusOperationLog
                {
                    Operation = "QUEUE_DUE_SCAN_ITEM",
                    Scope = "queue-item",
                    Result = "FAILED",
                    WorkId = item.WorkId,
                    WorkAssignmentId = item.WorkAssignmentId,
                    ActorUserId = "system",
                    Summary = $"queueItemId={item.Id};assigneeUserId={item.AssigneeUserId};periodKey={item.PeriodKey};dueAtUtc={item.DueAtUtc:O}",
                    ErrorType = ex.GetType().FullName,
                    ErrorMessage = ex.Message,
                    ErrorStackTrace = ex.ToString(),
                    StartedAtUtc = startedAtUtc
                }, startedAtUtc, ct);
            }
            finally
            {
                if (lifecycleSeriesLease is not null)
                    await lifecycleSeriesLease.DisposeAsync();
            }
        }

        if (scanned > 0)
        {
            _log.LogInformation(
                "WorkAssignmentQueue scan completed. scanned={scanned} changed={changed} disabled={disabled} missingOrInactivePeriod={missingPeriod} failed={failed} cap={cap}",
                scanned,
                changed,
                disabled,
                missingPeriod,
                failed,
                2000);

            await WriteStatusOperationLogAsync(new WorkStatusOperationLog
            {
                Operation = "QUEUE_DUE_SCAN",
                Scope = "queue-scan",
                Result = failed == 0 ? "SUCCESS" : "PARTIAL_FAILED",
                ActorUserId = "system",
                Summary = $"scanned={scanned};changed={changed};disabled={disabled};missingOrInactivePeriod={missingPeriod};failed={failed};cap=2000",
                StartedAtUtc = startedAtUtc
            }, startedAtUtc, ct);
        }
    }

    private async Task<bool> DisableQueueItemAsync(
        WorkAssignmentQueueItem item,
        DateTime now,
        CancellationToken ct)
    {
        var result = await _ctx.WorkAssignmentQueueItems.UpdateOneAsync(
            BuildObservedQueueFilter(item),
            Builders<WorkAssignmentQueueItem>.Update
                .Set(x => x.IsActive, false)
                .Set(x => x.LastScannedAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .Set(x => x.UpdatedByUserId, null),
            cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    private static FilterDefinition<WorkReportPeriod> BuildObservedPeriodFilter(
        WorkReportPeriod period)
    {
        var fb = Builders<WorkReportPeriod>.Filter;
        return fb.Eq(x => x.Id, period.Id) &
               fb.Eq(x => x.IsActive, period.IsActive) &
               fb.Eq(x => x.IsDeleted, false) &
               fb.Eq(x => x.Status, period.Status) &
               fb.Eq(x => x.IsOverdue, period.IsOverdue) &
               fb.Eq(x => x.IsHistoricalData, period.IsHistoricalData) &
               fb.Eq(x => x.CurrentReportId, period.CurrentReportId) &
               fb.Eq(x => x.SourceLifecycleReportId, period.SourceLifecycleReportId) &
               fb.Eq(x => x.SourceLifecycleRevision, period.SourceLifecycleRevision) &
               fb.Eq(x => x.UpdatedAtUtc, period.UpdatedAtUtc);
    }

    private static FilterDefinition<WorkAssignmentQueueItem> BuildObservedQueueFilter(
        WorkAssignmentQueueItem item)
    {
        var fb = Builders<WorkAssignmentQueueItem>.Filter;
        return fb.Eq(x => x.Id, item.Id) &
               fb.Eq(x => x.IsActive, item.IsActive) &
               fb.Eq(x => x.IsDeleted, false) &
               fb.Eq(x => x.NextScanAtUtc, item.NextScanAtUtc) &
               fb.Eq(x => x.LastScannedAtUtc, item.LastScannedAtUtc) &
               fb.Eq(x => x.LastObservedPeriodStatus, item.LastObservedPeriodStatus) &
               fb.Eq(x => x.UpdatedAtUtc, item.UpdatedAtUtc);
    }

    private async Task<HashSet<string>> LoadCompletedAncestorIdsAsync(
        WorkAssignment assignment,
        CancellationToken ct)
    {
        var ancestorIds = ResolveAncestorIds(assignment);
        if (ancestorIds.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        return (await _ctx.WorkAssignments
                .Find(x =>
                    ancestorIds.Contains(x.Id) &&
                    !x.IsDeleted &&
                    (x.CompletedAtUtc != null ||
                     (x.ProgressStatus == (int)WorkAssignmentProgressStatus.Completed &&
                      x.CompletedDate != null)))
                .Project(x => x.Id)
                .ToListAsync(ct))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);
    }

    private Task<bool> UpdatePeriodWithProgressFenceAsync(
        WorkReportPeriod period,
        UpdateDefinition<WorkReportPeriod> update,
        CancellationToken ct)
        => _transactions.ExecuteAsync(
            async (session, transactionCt) =>
            {
                if (!await _ctx.WorkReportPeriods.Find(session, BuildObservedPeriodFilter(period)).AnyAsync(transactionCt))
                    return false;
                var binding = await _ctx.WorkTemplateAssignees.Find(session, b => b.Id == period.WorkTemplateAssigneeId).FirstOrDefaultAsync(transactionCt);
                await tdtd_be.Services.AggregateMapping.Persistence.AggregateHostIntegration.SlotAsync(
                    _ctx, session, period.WorkId, period.WorkTemplateAssigneeId,
                    binding?.AssignmentType == "ONCE" ? "ONCE" : period.PeriodKey,
                    "queue:" + period.Id + ":" + period.UpdatedAtUtc.Ticks, transactionCt);
                var periodUpdate = await _ctx.WorkReportPeriods.UpdateOneAsync(
                    session,
                    BuildObservedPeriodFilter(period),
                    update,
                    cancellationToken: transactionCt);
                if (periodUpdate.MatchedCount != 1)
                    throw new tdtd_be.Services.AggregateMapping.AggregatePreviewException("AGG_SLOT_CHANGED");
                var sourceRevision = await _ctx.WorkAssignments.UpdateOneAsync(
                    session,
                    x =>
                        x.Id == period.WorkAssignmentId &&
                        !x.IsDeleted,
                    Builders<WorkAssignment>.Update.Inc(
                        x => x.ReportLifecycleSeriesRevision,
                        1),
                    cancellationToken: transactionCt);
                if (sourceRevision.MatchedCount != 1)
                {
                    throw new InvalidOperationException(
                        "WORK_ASSIGNMENT_QUEUE_PROGRESS_SOURCE_MISSING");
                }
                await WorkDirectSourceRevisionFence.IncrementAsync(
                    _ctx,
                    session,
                    period.WorkId,
                    transactionCt);
                return true;
            },
            ct);

    private static bool IsCompletionLocked(
        WorkAssignment assignment,
        Work? work,
        HashSet<string> completedAssignmentIds)
    {
        if (work != null && tdtd_be.Services.WorkAssignments.Progress.WorkExecutionScopeGuard.IsReopenedPeriod(
            assignment, assignment.CompletionReviewPeriodId)) return false;
        if (work is null || work.CompletedAtUtc.HasValue || work.Status == WorkStatus.S3)
            return true;

        if (assignment.CompletedAtUtc.HasValue ||
            (assignment.ProgressStatus == (int)WorkAssignmentProgressStatus.Completed && assignment.CompletedDate.HasValue))
            return true;

        return ResolveAncestorIds(assignment).Any(completedAssignmentIds.Contains);
    }

    private static List<string> ResolveAncestorIds(WorkAssignment assignment)
    {
        if (string.IsNullOrWhiteSpace(assignment.Path))
            return new List<string>();

        return assignment.Path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.Equals(x, assignment.Id, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private async Task WriteStatusOperationLogAsync(
        WorkStatusOperationLog log,
        DateTime startedAtUtc,
        CancellationToken ct)
    {
        var completedAtUtc = DateTime.UtcNow;
        log.CompletedAtUtc = completedAtUtc;
        log.DurationMs = (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        await _statusLog.WriteAsync(log, ct);
    }
}
