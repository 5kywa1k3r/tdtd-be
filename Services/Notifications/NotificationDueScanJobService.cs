using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;

namespace tdtd_be.Services.Notifications;

public sealed class NotificationDueScanJobService : INotificationDueScanJobService
{
    private readonly MongoDbContext _ctx;
    private readonly INotificationService _notifications;
    private readonly IWorkStatusOperationLogService _statusLog;
    private readonly ILogger<NotificationDueScanJobService> _log;
    private readonly int _lookbackDays;
    private readonly int _workCap;
    private readonly int _assignmentCap;
    private readonly int _reportPeriodCap;

    public NotificationDueScanJobService(
        MongoDbContext ctx,
        INotificationService notifications,
        IWorkStatusOperationLogService statusLog,
        IConfiguration cfg,
        ILogger<NotificationDueScanJobService> log)
    {
        _ctx = ctx;
        _notifications = notifications;
        _statusLog = statusLog;
        _log = log;
        _lookbackDays = Math.Clamp(cfg.GetValue<int?>("Notifications:DueScanLookbackDays") ?? 31, 1, 366);
        _workCap = Math.Clamp(cfg.GetValue<int?>("Notifications:DueScanWorkCap") ?? 1000, 1, 10000);
        _assignmentCap = Math.Clamp(cfg.GetValue<int?>("Notifications:DueScanAssignmentCap") ?? 1000, 1, 10000);
        _reportPeriodCap = Math.Clamp(cfg.GetValue<int?>("Notifications:DueScanReportPeriodCap") ?? 2000, 1, 20000);
    }

    public async Task ScanDueNotificationsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var minDueAt = now.AddDays(-_lookbackDays);
        var startedAt = now;
        var created = 0;
        var scannedWorks = 0;
        var scannedAssignments = 0;
        var scannedReportPeriods = 0;

        try
        {
            var workCommands = await BuildWorkDueCommandsAsync(now, minDueAt, ct);
            scannedWorks = workCommands.SourceCount;
            created += (await _notifications.CreateManyAsync(workCommands.Commands, ct)).Count;

            var assignmentCommands = await BuildAssignmentDueCommandsAsync(now, minDueAt, ct);
            scannedAssignments = assignmentCommands.SourceCount;
            created += (await _notifications.CreateManyAsync(assignmentCommands.Commands, ct)).Count;

            var reportCommands = await BuildReportDueCommandsAsync(now, minDueAt, ct);
            scannedReportPeriods = reportCommands.SourceCount;
            created += (await _notifications.CreateManyAsync(reportCommands.Commands, ct)).Count;

            if (scannedWorks + scannedAssignments + scannedReportPeriods > 0 || created > 0)
            {
                _log.LogInformation(
                    "Notification due scan completed. works={works} assignments={assignments} reportPeriods={reportPeriods} created={created} durationMs={durationMs}",
                    scannedWorks,
                    scannedAssignments,
                    scannedReportPeriods,
                    created,
                    (long)(DateTime.UtcNow - startedAt).TotalMilliseconds);
            }

            await WriteStatusOperationLogAsync(new WorkStatusOperationLog
            {
                Operation = "NOTIFICATION_DUE_SCAN",
                Scope = "notification-due-scan",
                Result = "SUCCESS",
                ActorUserId = "system",
                Summary = $"works={scannedWorks};assignments={scannedAssignments};reportPeriods={scannedReportPeriods};created={created}",
                StartedAtUtc = startedAt
            }, startedAt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(
                ex,
                "Notification due scan failed. works={works} assignments={assignments} reportPeriods={reportPeriods} created={created}",
                scannedWorks,
                scannedAssignments,
                scannedReportPeriods,
                created);

            await WriteStatusOperationLogAsync(new WorkStatusOperationLog
            {
                Operation = "NOTIFICATION_DUE_SCAN",
                Scope = "notification-due-scan",
                Result = "FAILED",
                ActorUserId = "system",
                Summary = $"works={scannedWorks};assignments={scannedAssignments};reportPeriods={scannedReportPeriods};created={created}",
                ErrorType = ex.GetType().FullName,
                ErrorMessage = ex.Message,
                ErrorStackTrace = ex.ToString(),
                StartedAtUtc = startedAt
            }, startedAt, ct);

            throw;
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

    private async Task<CommandBatch> BuildWorkDueCommandsAsync(
        DateTime now,
        DateTime minDueAt,
        CancellationToken ct)
    {
        var fb = Builders<Work>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false)
                     & fb.Ne(x => x.Status, WorkStatus.S3)
                     & fb.Ne(x => x.DueDate, null)
                     & fb.Lte(x => x.DueDate, now)
                     & fb.Gte(x => x.DueDate, minDueAt);

        var works = await _ctx.Works
            .Find(filter)
            .SortBy(x => x.DueDate)
            .Limit(_workCap)
            .ToListAsync(ct);

        var commands = works.SelectMany(work =>
        {
            var recipients = CleanRecipients(new[]
            {
                work.CreatedByUserId,
                work.LeaderDirectiveUserId
            }.Concat(work.LeaderWatchUserIds ?? new List<string>()));

            var dueTicks = work.DueDate?.Ticks.ToString() ?? "none";
            return recipients.Select(userId => new NotificationCommand
            {
                RecipientUserId = userId,
                Type = UserNotificationTypes.WorkDue,
                Severity = UserNotificationSeverities.Due,
                Title = "Công việc đến hạn",
                Body = work.Name,
                WorkId = work.Id,
                WorkType = work.Type,
                WorkName = work.Name,
                Category = UserNotificationCategories.Status,
                DueAtUtc = work.DueDate,
                EventKey = $"due:work:{work.Id}:{dueTicks}:user:{userId}"
            });
        });

        return new CommandBatch(works.Count, commands.ToList());
    }

    private async Task<CommandBatch> BuildAssignmentDueCommandsAsync(
        DateTime now,
        DateTime minDueAt,
        CancellationToken ct)
    {
        var fb = Builders<WorkAssignment>.Filter;
        var filter = fb.Eq(x => x.IsActive, true)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Ne(x => x.DueAtUtc, null)
                     & fb.Lte(x => x.DueAtUtc, now)
                     & fb.Gte(x => x.DueAtUtc, minDueAt);

        var assignments = await _ctx.WorkAssignments
            .Find(filter)
            .SortBy(x => x.DueAtUtc)
            .Limit(_assignmentCap)
            .ToListAsync(ct);

        var workMap = await LoadWorkMapAsync(assignments.Select(x => x.WorkId), ct);

        var commands = assignments.SelectMany(assignment =>
        {
            workMap.TryGetValue(assignment.WorkId, out var work);
            var recipients = CleanRecipients(
                (assignment.Assignees ?? new List<UserRef>()).Select(x => x.UserId)
                    .Concat(new[] { assignment.CreatedByUserId })
                    .Concat(assignment.LeaderWatcherUserIds ?? new List<string>()));

            var dueTicks = assignment.DueAtUtc?.Ticks.ToString() ?? "none";
            return recipients.Select(userId => new NotificationCommand
            {
                RecipientUserId = userId,
                Type = UserNotificationTypes.AssignmentDue,
                Severity = UserNotificationSeverities.Due,
                Title = "Phần việc đến hạn",
                Body = BuildAssignmentBody(assignment),
                WorkId = assignment.WorkId,
                WorkType = work?.Type,
                WorkName = work?.Name,
                WorkAssignmentId = assignment.Id,
                AssignmentCode = assignment.Code,
                Category = UserNotificationCategories.Status,
                DueAtUtc = assignment.DueAtUtc,
                EventKey = $"due:assignment:{assignment.Id}:{dueTicks}:user:{userId}"
            });
        });

        return new CommandBatch(assignments.Count, commands.ToList());
    }

    private async Task<CommandBatch> BuildReportDueCommandsAsync(
        DateTime now,
        DateTime minDueAt,
        CancellationToken ct)
    {
        var fb = Builders<WorkReportPeriod>.Filter;
        var filter = fb.Eq(x => x.IsActive, true)
                     & fb.Eq(x => x.IsDeleted, false)
                     & fb.Ne(x => x.DueAtUtc, null)
                     & fb.Lte(x => x.DueAtUtc, now)
                     & fb.Gte(x => x.DueAtUtc, minDueAt)
                     & fb.Nin(x => x.Status, new[] { WorkReportPeriodStatus.Approved, WorkReportPeriodStatus.OverdueApproved });

        var periods = await _ctx.WorkReportPeriods
            .Find(filter)
            .SortBy(x => x.DueAtUtc)
            .Limit(_reportPeriodCap)
            .ToListAsync(ct);

        var workMap = await LoadWorkMapAsync(periods.Select(x => x.WorkId), ct);
        var assignmentMap = await LoadAssignmentMapAsync(periods.Select(x => x.WorkAssignmentId), ct);

        var commands = periods
            .Where(x => !string.IsNullOrWhiteSpace(x.AssigneeUserId))
            .Select(period =>
            {
                workMap.TryGetValue(period.WorkId, out var work);
                assignmentMap.TryGetValue(period.WorkAssignmentId, out var assignment);
                var body = string.IsNullOrWhiteSpace(period.ReportTitle)
                    ? period.DynamicFormTemplateName ?? period.DynamicExcelName
                    : period.ReportTitle;

                return new NotificationCommand
                {
                    RecipientUserId = period.AssigneeUserId,
                    Type = UserNotificationTypes.ReportDue,
                    Severity = UserNotificationSeverities.Due,
                    Title = "Báo cáo đến hạn",
                    Body = body,
                    WorkId = period.WorkId,
                    WorkType = work?.Type,
                    WorkName = work?.Name,
                    WorkAssignmentId = period.WorkAssignmentId,
                    AssignmentCode = assignment?.Code,
                    WorkReportPeriodId = period.Id,
                    WorkAssignmentReportId = period.CurrentReportId,
                    Category = UserNotificationCategories.Report,
                    DueAtUtc = period.DueAtUtc,
                    EventKey = $"due:report-period:{period.Id}:user:{period.AssigneeUserId}"
                };
            })
            .ToList();

        return new CommandBatch(periods.Count, commands);
    }

    private async Task<Dictionary<string, Work>> LoadWorkMapAsync(IEnumerable<string?> workIds, CancellationToken ct)
    {
        var ids = CleanRecipients(workIds);
        if (ids.Count == 0)
            return new Dictionary<string, Work>(StringComparer.Ordinal);

        var works = await _ctx.Works
            .Find(x => ids.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

        return works.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, WorkAssignment>> LoadAssignmentMapAsync(
        IEnumerable<string?> assignmentIds,
        CancellationToken ct)
    {
        var ids = CleanRecipients(assignmentIds);
        if (ids.Count == 0)
            return new Dictionary<string, WorkAssignment>(StringComparer.Ordinal);

        var assignments = await _ctx.WorkAssignments
            .Find(x => ids.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

        return assignments.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    private static List<string> CleanRecipients(IEnumerable<string?> userIds)
        => userIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string BuildAssignmentBody(WorkAssignment assignment)
    {
        var template = assignment.DynamicFormTemplateName ?? assignment.DynamicExcelName;
        if (!string.IsNullOrWhiteSpace(template))
            return $"{assignment.Code} - {template}";

        return assignment.Code ?? assignment.Id;
    }

    private sealed record CommandBatch(int SourceCount, List<NotificationCommand> Commands);
}
