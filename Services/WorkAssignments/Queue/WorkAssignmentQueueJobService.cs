using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.Queue;

namespace tdtd_be.Services.WorkAssignments.Runtime;

public sealed class WorkAssignmentQueueJobService : IWorkAssignmentQueueJobService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentStatusSyncService _sync;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly ILogger<WorkAssignmentQueueJobService> _log;

    public WorkAssignmentQueueJobService(
        MongoDbContext ctx,
        IWorkAssignmentStatusSyncService sync,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        IWorkStatusOperationLogService statusLog,
        ILogger<WorkAssignmentQueueJobService> log)
    {
        _ctx = ctx;
        _sync = sync;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _statusLog = statusLog;
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

        var changed = 0;
        var disabled = 0;
        var missingPeriod = 0;
        var scanned = 0;
        var failed = 0;

        foreach (var item in queueItems)
        {
            scanned++;
            try
            {
                var period = await _ctx.WorkReportPeriods
                    .Find(x => x.WorkAssignmentId == item.WorkAssignmentId &&
                               x.AssigneeUserId == item.AssigneeUserId &&
                               x.PeriodKey == item.PeriodKey &&
                               (x.PeriodKind == null || x.PeriodKind == WorkReportPeriodKind.Scheduled) &&
                               !x.IsDeleted)
                    .FirstOrDefaultAsync(ct);

                if (period is null || !period.IsActive)
                {
                    await _ctx.WorkAssignmentQueueItems.UpdateOneAsync(
                        x => x.Id == item.Id,
                        Builders<WorkAssignmentQueueItem>.Update
                            .Set(x => x.IsActive, false)
                            .Set(x => x.LastScannedAtUtc, now)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, null),
                        cancellationToken: ct);
                    missingPeriod++;
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
                    await _ctx.WorkReportPeriods.UpdateOneAsync(
                        x => x.Id == period.Id,
                        Builders<WorkReportPeriod>.Update
                            .Set(x => x.Status, nextStatus)
                            .Set(x => x.IsOverdue, WorkReportPeriodStatusHelper.IsOverdue(nextStatus))
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, null),
                        cancellationToken: ct);

                    await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, "system", ct);
                    await _sync.SyncFromAssignmentAsync(period.WorkAssignmentId, ct);
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

                await _ctx.WorkAssignmentQueueItems.UpdateOneAsync(
                    x => x.Id == item.Id,
                    queueUpdate,
                    cancellationToken: ct);

                if (shouldDisableQueue)
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
