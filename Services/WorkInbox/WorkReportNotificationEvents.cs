using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Notifications;
using tdtd_be.Services.WorkAssignmentReports.Runtime;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkInbox;

// Consume the events committed in the report CAS, including transitions that
// have already been followed by another transition before the scheduled scan.
public static class WorkReportNotificationEvents
{
    public static List<NotificationCommand> Build(WorkAssignmentReport report, WorkAssignment assignment,
        Work work, bool notifyReviewRequired)
    {
        var result = new List<NotificationCommand>();
        var returned = false;
        var hasSubmit = false;
        var hasApprove = false;
        foreach (var entry in (report.LifecycleProjectionOutbox ?? [])
            .OrderBy(x => x.LifecycleRevision).ThenBy(x => x.EntryKey, StringComparer.Ordinal))
        {
            foreach (var item in WorkReportLifecycleOutboxContract.ResolveBusinessEvents(entry))
            {
                var submitted = item.UserAction == UserActionLogActions.ReportSubmitted;
                var approved = item.UserAction == UserActionLogActions.ReportApproved;
                var isReturn = item.UserAction == UserActionLogActions.ReportReturned;
                hasSubmit |= submitted;
                hasApprove |= approved;
                if (isReturn) returned = true;
                if (!submitted && !approved && !isReturn) continue;
                // Auto-approval has no review obligation; retain its approval event.
                if (submitted && (!notifyReviewRequired || entry.ToStatus.Equals("APPROVED", StringComparison.OrdinalIgnoreCase))) continue;
                var recipient = submitted ? WorkAssignmentCurrentAuthority.ReviewerId(assignment) : report.AssigneeUserId;
                if (string.IsNullOrWhiteSpace(recipient)) continue;
                var type = isReturn ? "REPORT_RETURNED" : approved ? "REPORT_APPROVED"
                    : returned ? "REPORT_RESUBMITTED" : "REPORT_REVIEW_REQUIRED";
                var autoApproved = item.ReportLogAction == "AUTO_APPROVE";
                result.Add(Command(report, assignment, work, recipient, type, entry.CreatedAtUtc,
                    $"inbox:lifecycle:{report.Id}:{item.EventKey}:user:{recipient}",
                    isReturn ? item.Reason : autoApproved ? "Báo cáo được tự duyệt theo cấu hình của phần việc." : null,
                    autoApproved ? "Báo cáo đã được tự duyệt" : null));
            }
        }
        // Compatibility for reports saved before durable lifecycle events existed.
        // Once an event exists, never infer that transition from mutable status.
        if (!hasSubmit && report.Status == WorkAssignmentReportStatus.Submitted && notifyReviewRequired && report.SubmittedAtUtc.HasValue)
        {
            var recipient = WorkAssignmentCurrentAuthority.ReviewerId(assignment);
            if (!string.IsNullOrWhiteSpace(recipient)) result.Add(Command(report, assignment, work, recipient,
                "REPORT_REVIEW_REQUIRED", report.SubmittedAtUtc.Value,
                LegacyKey(report.Id, "submitted", report.SubmittedAtUtc.Value, recipient)));
        }
        if (!hasApprove && report.Status == WorkAssignmentReportStatus.Approved && (report.ApprovedAtUtc ?? report.AutoApprovedAtUtc) is DateTime approvedAt)
            result.Add(Command(report, assignment, work, report.AssigneeUserId, "REPORT_APPROVED", approvedAt,
                LegacyKey(report.Id, "approved", approvedAt, report.AssigneeUserId)));
        return result;
    }

    public static string? LegacyKey(NotificationCommand command) => command.Type switch {
        "REPORT_REVIEW_REQUIRED" or "REPORT_RESUBMITTED" => LegacyKey(command.WorkAssignmentReportId!, "submitted", command.OccurredAtUtc, command.RecipientUserId),
        "REPORT_APPROVED" => LegacyKey(command.WorkAssignmentReportId!, "approved", command.OccurredAtUtc, command.RecipientUserId),
        _ => null
    };

    // Preserve pre-upgrade history. A legacy timestamp key can cover at most one
    // event (the latest at that timestamp), never two distinct lifecycle revisions.
    public static List<NotificationCommand> ExcludeAlreadyDeliveredLegacy(List<NotificationCommand> commands, IReadOnlySet<string> legacyKeys)
    {
        var covered = commands.Where(x => LegacyKey(x) is string key && legacyKeys.Contains(key))
            .GroupBy(x => LegacyKey(x)).Select(g => g.Last().EventKey).ToHashSet(StringComparer.Ordinal);
        return commands.Where(x => !covered.Contains(x.EventKey)).ToList();
    }

    private static string LegacyKey(string reportId, string kind, DateTime occurred, string recipient)
        => $"inbox:{kind}:{reportId}:{occurred.Ticks}:user:{recipient}";

    private static NotificationCommand Command(WorkAssignmentReport report, WorkAssignment assignment, Work work,
        string recipient, string type, DateTime occurred, string key, string? reason = null, string? title = null)
        => new() {
            RecipientUserId = recipient, Type = type,
            Title = title ?? (type == "REPORT_RETURNED" ? "Báo cáo được trả lại để bổ sung"
                : type == "REPORT_APPROVED" ? "Báo cáo đã được duyệt"
                : type == "REPORT_RESUBMITTED" ? "Báo cáo đã nộp lại, cần duyệt" : "Có báo cáo cần duyệt"),
            Body = reason ?? report.ReportTitle ?? assignment.Name,
            WorkId = work.Id, WorkName = work.Name, WorkType = work.Type,
            WorkAssignmentId = assignment.Id, AssignmentName = assignment.Name,
            WorkReportPeriodId = report.WorkReportPeriodId, WorkAssignmentReportId = report.Id,
            RequiresAction = type != "REPORT_APPROVED", Category = type == "REPORT_APPROVED" ? "STATUS" : "ACTION",
            OccurredAtUtc = occurred, EventKey = key
        };
}
