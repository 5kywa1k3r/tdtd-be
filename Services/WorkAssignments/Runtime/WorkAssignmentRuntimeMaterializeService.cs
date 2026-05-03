using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using System.Globalization;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.Common.Time;
using tdtd_be.Services.WorkAssignments.Progress;
using tdtd_be.Services.WorkAssignments.Queue;

namespace tdtd_be.Services.WorkAssignments.Runtime;

public sealed class WorkAssignmentRuntimeMaterializeService : IWorkAssignmentRuntimeMaterializeService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentQueueService _queue;
    private readonly IWorkAssignmentProgressService _progress;
    private readonly IWorkAssignmentStatusSyncService _sync;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly ILogger<WorkAssignmentRuntimeMaterializeService> _log;

    public WorkAssignmentRuntimeMaterializeService(
        MongoDbContext ctx,
        IWorkAssignmentQueueService queue,
        IWorkAssignmentProgressService progress,
        IWorkAssignmentStatusSyncService sync,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        ILogger<WorkAssignmentRuntimeMaterializeService> log)
    {
        _ctx = ctx;
        _queue = queue;
        _progress = progress;
        _sync = sync;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _log = log;
    }

    public async Task MaterializeForAssignmentAsync(string workAssignmentId, string actorUserId, CancellationToken ct = default)
    {
        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == workAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy assignment để materialize runtime.");

        if (!assignment.IsActive || assignment.Schedule is null)
            return;

        var work = await _ctx.Works
            .Find(x => x.Id == assignment.WorkId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Không tìm thấy work của assignment.");

        var bindings = await _ctx.WorkTemplateAssignees
            .Find(x =>
                x.WorkAssignmentId == assignment.Id &&
                x.IsActive &&
                !x.IsDeleted)
            .ToListAsync(ct);

        var bindingMap = bindings
            .Where(x => !string.IsNullOrWhiteSpace(x.AssigneeUserId) && !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.AssigneeUserId!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.UpdatedAtUtc).First(),
                StringComparer.Ordinal);

        var start = assignment.Schedule.StartDate ?? work.StartDate ?? DateTime.UtcNow.Date;
        var end = work.EndDate ?? assignment.Schedule.StartDate?.AddMonths(6) ?? DateTime.UtcNow.Date.AddMonths(6);

        if (end < start)
            end = start;

        var dueItems = AssignmentScheduleDueHelper.GetDueItemsInRange(
            assignment.Schedule,
            start,
            end);

        foreach (var assignee in assignment.Assignees.Where(x => !string.IsNullOrWhiteSpace(x.UserId)))
        {
            if (!bindingMap.TryGetValue(assignee.UserId!, out var binding))
                continue;

            foreach (var item in dueItems)
            {
                var existed = await _ctx.WorkReportPeriods
                    .Find(x =>
                        x.WorkAssignmentId == assignment.Id &&
                        x.AssigneeUserId == assignee.UserId &&
                        x.PeriodKey == item.PeriodKey &&
                        (x.PeriodKind == null || x.PeriodKind == WorkReportPeriodKind.Scheduled) &&
                        !x.IsDeleted)
                    .FirstOrDefaultAsync(ct);

                if (!DateTime.TryParseExact(
                    item.PeriodKey,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var periodDate))
                {
                    throw new InvalidOperationException($"PeriodKey không hợp lệ: {item.PeriodKey}");
                }

                periodDate = periodDate.Date;

                var (periodStart, periodEnd) =
                    AssignmentScheduleTimeHelper.GetPeriodRange(assignment.Schedule, periodDate);

                if (existed is null)
                {
                    var now = DateTime.UtcNow;
                    var status = WorkReportPeriodStatusHelper.ResolveInitialStatus(item.DueAtUtc, now);

                    var period = new WorkReportPeriod
                    {
                        WorkId = assignment.WorkId,
                        WorkAssignmentId = assignment.Id,
                        WorkTemplateAssigneeId = binding.Id!,
                        DynamicExcelId = assignment.DynamicExcelId,
                        DynamicExcelCode = assignment.DynamicExcelCode,
                        DynamicExcelName = assignment.DynamicExcelName,
                        DynamicFormTemplateId = assignment.DynamicFormTemplateId,
                        DynamicFormTemplateCode = assignment.DynamicFormTemplateCode,
                        DynamicFormTemplateName = assignment.DynamicFormTemplateName,
                        AssigneeUserId = assignee.UserId,
                        AssigneeUnitId = assignee.UnitId,
                        PeriodKey = item.PeriodKey,
                        PeriodInstanceKey = item.PeriodKey,
                        PeriodKind = WorkReportPeriodKind.Scheduled,
                        ReportTitle = assignment.DynamicExcelName,
                        ReportDate = periodDate,
                        PeriodStart = periodStart,
                        PeriodEnd = periodEnd,
                        DueAtUtc = item.DueAtUtc,
                        Status = status,
                        IsOverdue = WorkReportPeriodStatusHelper.IsOverdue(status),
                        IsActive = true,
                        IsDeleted = false,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                        CreatedByUserId = actorUserId,
                        UpdatedByUserId = actorUserId
                    };

                    await _ctx.WorkReportPeriods.InsertOneAsync(period, cancellationToken: ct);
                    await _queue.UpsertPeriodAsync(period, actorUserId, ct);
                    await _docRoleReadModelProjection.RebuildReportPeriodAsync(period.Id, actorUserId, ct);
                }
                else
                {
                    var now = DateTime.UtcNow;

                    await _ctx.WorkReportPeriods.UpdateOneAsync(
                        x => x.Id == existed.Id,
                        Builders<WorkReportPeriod>.Update
                            .Set(x => x.WorkTemplateAssigneeId, binding.Id!)
                            .Set(x => x.IsActive, true)
                            .Set(x => x.DueAtUtc, item.DueAtUtc)
                            .Set(x => x.DynamicFormTemplateId, assignment.DynamicFormTemplateId)
                            .Set(x => x.DynamicFormTemplateCode, assignment.DynamicFormTemplateCode)
                            .Set(x => x.DynamicFormTemplateName, assignment.DynamicFormTemplateName)
                            .Set(x => x.PeriodInstanceKey, string.IsNullOrWhiteSpace(existed.PeriodInstanceKey) ? existed.PeriodKey : existed.PeriodInstanceKey)
                            .Set(x => x.PeriodKind, WorkReportPeriodKind.Scheduled)
                            .Set(x => x.ReportDate, periodDate)
                            .Set(x => x.PeriodStart, periodStart)
                            .Set(x => x.PeriodEnd, periodEnd)
                            .Set(x => x.UpdatedAtUtc, now)
                            .Set(x => x.UpdatedByUserId, actorUserId),
                        cancellationToken: ct);

                    existed.WorkTemplateAssigneeId = binding.Id!;
                    existed.IsActive = true;
                    existed.DueAtUtc = item.DueAtUtc;
                    existed.PeriodStart = periodStart;
                    existed.PeriodEnd = periodEnd;
                    existed.UpdatedAtUtc = now;
                    existed.UpdatedByUserId = actorUserId;

                    await _queue.UpsertPeriodAsync(existed, actorUserId, ct);
                    await _docRoleReadModelProjection.RebuildReportPeriodAsync(existed.Id, actorUserId, ct);
                }
            }
        }

        await _progress.RecomputeSingleAsync(assignment.Id, ct);
        await _sync.SyncFromAssignmentAsync(assignment.Id, ct);
    }

    public async Task RematerializeForAssignmentAsync(string workAssignmentId, string actorUserId, CancellationToken ct = default)
    {
        await _queue.DisableByAssignmentAsync(workAssignmentId, actorUserId, ct);

        var disableResult = await _ctx.WorkReportPeriods.UpdateManyAsync(
            x => x.WorkAssignmentId == workAssignmentId && !x.IsDeleted,
            Builders<WorkReportPeriod>.Update
                .Set(x => x.IsActive, false)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedByUserId, actorUserId),
            cancellationToken: ct);

        var disabledPeriodIds = await _ctx.WorkReportPeriods
            .Find(x => x.WorkAssignmentId == workAssignmentId && !x.IsDeleted)
            .Project(x => x.Id)
            .ToListAsync(ct);

        _log.LogInformation(
            "WorkAssignment rematerialize requested. assignmentId={assignmentId} actorUserId={actorUserId} disabledPeriods={disabledPeriods} rebuildPeriods={rebuildPeriods}",
            workAssignmentId,
            actorUserId,
            disableResult.ModifiedCount,
            disabledPeriodIds.Count);

        foreach (var periodId in disabledPeriodIds.Where(x => !string.IsNullOrWhiteSpace(x)))
            await _docRoleReadModelProjection.RebuildReportPeriodAsync(periodId, actorUserId, ct);

        await MaterializeForAssignmentAsync(workAssignmentId, actorUserId, ct);
    }
}
