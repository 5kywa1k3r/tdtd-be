using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.Works;

namespace tdtd_be.Services.WorkInbox;

// Reuses the existing scheduled-job lease. Cursor batches do not truncate older overdue work.
public sealed class WorkInboxNotificationJob(MongoDbContext ctx, INotificationService notifications,
    WorkInboxService inbox, ILogger<WorkInboxNotificationJob> logger) : INotificationDueScanJobService
{
    public async Task ScanDueNotificationsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var horizon = now.AddHours(inbox.DueSoonHours ?? 0);
        await Scan(ctx.WorkReportPeriods, x => !x.IsDeleted && x.IsActive && x.DueAtUtc != null && x.DueAtUtc <= horizon,
            async p => {
                if (!new[] { 0, 1, 4, 5 }.Contains((int)p.Status)) return;
                var a = await Assignment(p.WorkAssignmentId, ct);
                if (a is null || a.ProgressStatus == 2 || a.CompletedAtUtc is not null || a.CompletedDate is not null || !(a.Assignees ?? new()).Any(x => x.UserId == p.AssigneeUserId)) return;
                if (p.CurrentReportId is not null) {
                    var report = await ctx.WorkAssignmentReports.Find(x => x.Id == p.CurrentReportId && !x.IsDeleted && x.IsActive && x.IsCurrent).FirstOrDefaultAsync(ct);
                    if (report is null || report.Status != WorkAssignmentReportStatus.Draft) return;
                }
                var w = await Work(a.WorkId, ct); if (w is null) return;
                await Due("REPORT", p.Id, p.DueAtUtc!.Value, p.AssigneeUserId, w, a, p.Id, p.ReportTitle, now, ct);
            }, ct);
        await Scan(ctx.WorkAssignments, x => !x.IsDeleted && x.IsActive && x.DueAtUtc != null && x.DueAtUtc <= horizon,
            async a => {
                if (a.FlowInstanceId is not null || a.ProgressStatus == 2 || a.CompletedAtUtc is not null || a.CompletedDate is not null ||
                    a.DynamicFormTemplateId is not null || a.DynamicExcelId is not null) return;
                var w = await Work(a.WorkId, ct); if (w is null) return;
                foreach (var user in (a.Assignees ?? new()).Select(x => x.UserId).Distinct())
                    await Due("ASSIGNMENT", a.Id, a.DueAtUtc!.Value, user, w, a, null, a.Name, now, ct);
            }, ct);
        await Scan(ctx.Works, x => !x.IsDeleted && x.Status != WorkStatus.S3 &&
            ((x.DueDate != null && x.DueDate <= horizon) || (x.DueDate == null && x.EndDate != null && x.EndDate <= horizon)),
            async w => {
                var due = WorkDatePolicy.EffectiveDueDate(w); if (due is null) return;
                foreach (var actor in new[] { w.CreatedByUserId, w.LeaderDirectiveUserId }.Concat(w.LeaderWatchUserIds ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
                    await Due("WORK", w.Id, due.Value, actor!, w, null, null, w.Name, now, ct);
            }, ct);
        // Walk current facts without a lookback cutoff so downtime cannot lose older approvals.
        await Scan(ctx.WorkAssignmentReports, x => !x.IsDeleted && x.IsActive && x.IsCurrent &&
            (x.Status == WorkAssignmentReportStatus.Approved || x.Status == WorkAssignmentReportStatus.Submitted), async r => {
                var a = await Assignment(r.WorkAssignmentId, ct); if (a is null) return;
                if (!(a.Assignees ?? new()).Any(x => x.UserId == r.AssigneeUserId)) return;
                var w = await ctx.Works.Find(x => x.Id == a.WorkId && !x.IsDeleted).FirstOrDefaultAsync(ct); if (w is null) return;
                var approved = r.Status == WorkAssignmentReportStatus.Approved;
                if (!approved && !inbox.NotifyReviewRequired) return;
                var occurred = approved ? r.ApprovedAtUtc ?? r.AutoApprovedAtUtc : r.SubmittedAtUtc;
                if (occurred is null) return;
                var recipient = approved ? r.AssigneeUserId : WorkAssignmentCurrentAuthority.ReviewerId(a);
                if (string.IsNullOrWhiteSpace(recipient)) return;
                await notifications.CreateManyAsync(new[] { new NotificationCommand {
                    RecipientUserId = recipient, Type = approved ? "REPORT_APPROVED" : "REPORT_REVIEW_REQUIRED",
                    Title = approved ? "Báo cáo đã được duyệt" : "Có báo cáo cần duyệt", Body = r.ReportTitle ?? a.Name,
                    WorkId = w.Id, WorkName = w.Name, WorkType = w.Type, WorkAssignmentId = a.Id,
                    WorkReportPeriodId = r.WorkReportPeriodId, WorkAssignmentReportId = r.Id,
                    RequiresAction = !approved, Category = approved ? "STATUS" : "ACTION",
                    OccurredAtUtc = occurred.Value,
                    EventKey = $"inbox:{(approved ? "approved" : "submitted")}:{r.Id}:{occurred.Value.Ticks}:user:{recipient}"
                } }, ct);
            }, ct);
        logger.LogInformation("Work Inbox notification scan completed");
    }

    private async Task<WorkAssignment?> Assignment(string id, CancellationToken ct)
        => await ctx.WorkAssignments.Find(x => x.Id == id && !x.IsDeleted && x.IsActive && x.FlowInstanceId == null).FirstOrDefaultAsync(ct);
    private async Task<Work?> Work(string id, CancellationToken ct)
        => await ctx.Works.Find(x => x.Id == id && !x.IsDeleted && x.Status != WorkStatus.S3).FirstOrDefaultAsync(ct);

    private Task<List<UserNotification>> Due(string kind, string id, DateTime due, string recipient,
        Work w, WorkAssignment? a, string? period, string? body, DateTime now, CancellationToken ct)
    {
        var overdue = due <= now;
        return notifications.CreateManyAsync(new[] { new NotificationCommand {
            RecipientUserId = recipient, Type = kind + (overdue ? "_DUE" : "_DUE_SOON"),
            Severity = overdue ? "DUE" : "WARNING", Title = overdue ? "Công việc trễ hạn" : "Công việc sắp đến hạn",
            Body = body, WorkId = w.Id, WorkName = w.Name, WorkType = w.Type, WorkAssignmentId = a?.Id,
            WorkReportPeriodId = period, DueAtUtc = due, RequiresAction = kind != "WORK", Category = "STATUS",
            EventKey = overdue ? (kind == "REPORT" ? $"due:report-period:{id}:user:{recipient}" : $"due:{kind.ToLowerInvariant()}:{id}:{due.Ticks}:user:{recipient}") : $"inbox:soon:{kind}:{id}:{due.Ticks}:user:{recipient}"
        } }, ct);
    }

    private static async Task Scan<T>(IMongoCollection<T> collection, System.Linq.Expressions.Expression<Func<T, bool>> filter,
        Func<T, Task> visit, CancellationToken ct)
    {
        using var cursor = await collection.Find(filter, new FindOptions { BatchSize = 100 }).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) { ct.ThrowIfCancellationRequested(); await visit(row); }
    }
}
