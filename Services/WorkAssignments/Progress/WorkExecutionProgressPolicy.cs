using tdtd_be.Common.Time;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.Common.Time;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignments.Progress;

// Execution, obligations and deadlines are independent. This policy never reads descendants.
public sealed record WorkExecutionFacts(
    DateTime? FirstApprovedAtUtc, DateTime? DeadlineUtc, bool ScheduleComplete,
    int ExpectedReports, int ApprovedReports, bool SubmissionOverdue, bool ReviewOverdue,
    bool ReopenHold)
{
    public bool AllReportsApproved => ScheduleComplete && ExpectedReports > 0 && ExpectedReports == ApprovedReports;
    public bool CanAutoComplete(DateTime now) => !ReopenHold && AllReportsApproved && DeadlineUtc.HasValue && now >= DeadlineUtc.Value;
    public int ExecutionStatus(bool completed) => completed ? 2 : FirstApprovedAtUtc.HasValue ? 1 : 0;
}

public static class WorkExecutionProgressPolicy
{
    private static readonly TimeZoneInfo BusinessZone = AppTimeService.ResolveApplicationTimeZone();

    public static bool IsCompleted(WorkAssignment a) => a.CompletedAtUtc.HasValue || (a.ProgressStatus == 2 && a.CompletedDate.HasValue);

    public static DateTime? Deadline(WorkAssignment a, Work w, WorkAssignment? parent)
    {
        if (IsOnce(a) && a.DueAtUtc.HasValue) return a.DueAtUtc;
        return EndOfDay(WorkAssignmentDatePolicy.ResolveEffectiveDueDate(a, w, parent));
    }

    public static DateTime? EndOfDay(DateTime? day) => day.HasValue
        ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.Value.Date.AddDays(1), DateTimeKind.Unspecified), BusinessZone).AddTicks(-1)
        : null;

    public static DateTime? FirstApproval(WorkAssignment a, IEnumerable<WorkAssignmentReport> reports)
    {
        var times = new List<DateTime>();
        if (a.FirstApprovedAtUtc.HasValue) times.Add(a.FirstApprovedAtUtc.Value);
        // Outbox entries are durable history even when a report is returned or deactivated.
        foreach (var r in reports.Where(r => r.WorkAssignmentId == a.Id && !r.IsDeleted))
        {
            if (r.ApprovedAtUtc.HasValue) times.Add(r.ApprovedAtUtc.Value);
            if (r.AutoApprovedAtUtc.HasValue) times.Add(r.AutoApprovedAtUtc.Value);
            times.AddRange((r.LifecycleProjectionOutbox ?? new()).Where(e =>
                e.ToIsActive && string.Equals(e.ToStatus, "APPROVED", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.CreatedAtUtc));
        }
        return times.Count == 0 ? null : times.Min();
    }

    public static WorkExecutionFacts Evaluate(WorkAssignment a, Work w, WorkAssignment? parent,
        IReadOnlyCollection<WorkTemplateAssignee> bindings, IReadOnlyCollection<WorkReportPeriod> periods,
        IReadOnlyCollection<WorkAssignmentReport> reports, DateTime now)
    {
        var deadline = Deadline(a, w, parent);
        var ownPeriods = periods.Where(p => p.WorkAssignmentId == a.Id && !p.IsDeleted && p.IsActive &&
            (p.PeriodKind == null || p.PeriodKind == WorkReportPeriodKind.Scheduled)).ToList();
        var current = reports.Where(r => r.WorkAssignmentId == a.Id && !r.IsDeleted && r.IsActive && r.IsCurrent).ToList();
        WorkAssignmentReport? Report(WorkReportPeriod p)
        {
            var rows = current.Where(r => r.WorkReportPeriodId == p.Id).ToList();
            return rows.Count == 1 && rows[0].Id == p.CurrentReportId ? rows[0] : null;
        }
        bool Approved(WorkReportPeriod p) => Report(p)?.Status == WorkAssignmentReportStatus.Approved;
        var recipients = (a.Assignees ?? new()).Select(u => u.UserId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        var boundUsers = bindings.Where(b => b.WorkAssignmentId == a.Id && b.IsActive && !b.IsDeleted)
            .Select(b => b.AssigneeUserId).ToHashSet(StringComparer.Ordinal);
        var complete = a.IsActive && !a.IsDeleted && recipients.Count > 0 && recipients.All(boundUsers.Contains);
        var expected = new List<AssignmentScheduleDueItem>();
        if (IsOnce(a))
        {
            // The materializer reuses the recipient's single occurrence even when its date/key changes.
            // A deadline is not the identity of that occurrence (legacy rows may also use "ONCE").
            expected.Add(new() { PeriodKey = "ONCE" });
        }
        else if (!IsOnce(a) && a.Schedule != null && ScheduleValidator.IsValid(a.Schedule) && deadline.HasValue &&
                 (a.StartDate ?? a.Schedule.StartDate ?? w.StartDate) is { } start)
        {
            var end = TimeZoneInfo.ConvertTimeFromUtc(deadline.Value, BusinessZone).Date;
            if (a.Schedule.StartDate is { } scheduleStart && scheduleStart.Date > start.Date) start = scheduleStart.Date;
            expected = AssignmentScheduleDueHelper.GetDueItemsInRange(a.Schedule, start.Date, end);
        }
        else complete = false;
        if (expected.Count == 0) complete = false; // Never treat a missing obligation set as success.

        var keys = expected.Select(d => d.PeriodKey).Distinct(StringComparer.Ordinal).ToList();
        var approved = 0;
        foreach (var user in recipients)
            foreach (var key in keys)
            {
                var candidates = ownPeriods.Where(p => p.AssigneeUserId == user && (IsOnce(a) || p.PeriodKey == key)).ToList();
                if (candidates.Count == 1 && Approved(candidates[0])) approved++;
                if (candidates.Count != 1) complete = false;
            }
        // Retained periods are still obligations; missing schedule rows must not hide them either.
        foreach (var p in ownPeriods.Where(p => recipients.Contains(p.AssigneeUserId) && !keys.Contains(p.PeriodKey)))
            if (!Approved(p)) complete = false;

        var hold = a.CompletionReopenedAtUtc.HasValue && !string.IsNullOrEmpty(a.CompletionReviewPeriodId);
        if (hold)
        {
            var target = periods.SingleOrDefault(p => p.WorkAssignmentId == a.Id && p.IsActive && !p.IsDeleted && p.Id == a.CompletionReviewPeriodId);
            var r = target == null ? null : Report(target);
            hold = r?.Status != WorkAssignmentReportStatus.Approved || !(r.LifecycleProjectionOutbox ?? new()).Any(e =>
                e.CreatedAtUtc > a.CompletionReopenedAtUtc && e.ToIsActive &&
                string.Equals(e.ToStatus, "APPROVED", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(e.FromStatus, "DRAFT", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(e.FromStatus, "SUBMITTED", StringComparison.OrdinalIgnoreCase)));
        }
        var notApproved = ownPeriods.Where(p => recipients.Contains(p.AssigneeUserId) && !Approved(p)).ToList();
        var submissionLate = notApproved.Any(p => p.DueAtUtc < now && (Report(p)?.Status is null or WorkAssignmentReportStatus.Draft));
        var reviewLate = deadline < now && notApproved.Any(p => Report(p)?.Status == WorkAssignmentReportStatus.Submitted);
        return new(FirstApproval(a, reports), deadline, complete, keys.Count * recipients.Count, approved,
            submissionLate, reviewLate, hold);
    }

    private static bool IsOnce(WorkAssignment a) => string.Equals(a.AssignmentType, WorkAssignmentTypes.Once, StringComparison.OrdinalIgnoreCase);
}
