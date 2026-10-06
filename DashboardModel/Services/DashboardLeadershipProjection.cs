using System.Globalization;
using tdtd_be.Common.Errors;
using tdtd_be.DashboardModel.DTOs;
using tdtd_be.Models;

namespace tdtd_be.DashboardModel.Services;

// Pure read projection, shared by chart and list. Source dates and lifecycle are never mutated.
internal static class DashboardLeadershipProjection
{
    internal static IReadOnlyList<string> DeadlineBucketKeys { get; } = new[]
    {
        "OVERDUE", "TODAY", "NEXT_7_DAYS", "NEXT_8_30_DAYS", "AFTER_30_DAYS", "NO_DEADLINE",
    };
    internal static DateTime BusinessToday(DateTime utcNow) => utcNow.AddHours(7).Date;
    internal static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static DateTime ParseDay(string? value)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day.Year > 9000)
            throw Invalid("date", value);
        return day.Date;
    }
    internal static Exception Invalid(string field, object? value) =>
        AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { field, value });
    internal static (DateTime From, DateTime To) Range(DashboardOverviewRequest req, DateTime now)
    {
        if ((req.FromDate is null) != (req.ToDate is null)) throw Invalid("dateRange", "both dates required");
        var today = BusinessToday(now);
        var from = req.FromDate is null ? new DateTime(today.Year, today.Month, 1) : ParseDay(req.FromDate);
        var to = req.ToDate is null ? from.AddMonths(1).AddDays(-1) : ParseDay(req.ToDate);
        if (from > to) throw Invalid("dateRange", "fromDate > toDate");
        return (from, to);
    }
    internal static string Section(DashboardOverviewRequest req)
    {
        var section = (req.Section ?? "CURRENT").ToUpperInvariant();
        if (section is not ("CURRENT" or "PERIOD")) throw Invalid("section", req.Section);
        if ((req.UnitIds?.Count ?? 0) > 0 || req.AssignmentId is not null || req.FromUtc is not null || req.ToUtc is not null)
            throw Invalid("context", "LEADERSHIP accepts business dates and server authority only");
        if (req.Mode is not null || section == "CURRENT" && (req.FromDate is not null || req.ToDate is not null))
            throw Invalid("context", "CURRENT is unfiltered; legacy mode is not a leadership work-type selector");
        return section;
    }
    internal static void ValidateSelectionFilters(DashboardLeadershipItemsRequest req, string selection)
    {
        if (selection.StartsWith("WORK_DEADLINE_", StringComparison.Ordinal))
        {
            if (!DeadlineBucketKeys.Contains(selection[14..])) throw Invalid("selection", selection);
            if (Section(req.Context) != "CURRENT") throw Invalid("selection", "deadline selection requires CURRENT");
            if (req.Status is not null || req.Priority is not null || req.WorkId is not null || req.BucketFromDate is not null || req.BucketToDate is not null)
                throw Invalid("filters", "deadline selection accepts paging only");
            return;
        }
        if (req.Status is { } status)
        {
            var valid = selection == "WORK_STATUS" ? status is >= 1 and <= 5 : selection == "ASSIGNMENT_STATUS" ? status is >= 0 and <= 4 : selection == "OPEN" && status is 1 or 2 or 4 or 5;
            if (!valid) throw Invalid("status", "status does not belong to this object/selection");
        }
        if (req.Priority is not null && selection is not ("OPEN" or "WORK_STATUS" or "WORK_ALL")) throw Invalid("priority", "work selection required");
        if ((req.BucketFromDate is null) != (req.BucketToDate is null)) throw Invalid("bucketRange", "both dates required");
        if (req.BucketFromDate is not null && selection is not ("WORK_CREATED" or "WORK_COMPLETED" or "ASSIGNMENT_CREATED" or "ASSIGNMENT_COMPLETED")) throw Invalid("bucketRange", "event selection required");
    }
    internal static int CurrentStatus(Work w, DateTime today) => (int)w.Status;
    internal static string WorkTiming(Work w, DateTime today) => Timing(w.Status == WorkStatus.S3, w.CompletedDate, w.DueDate ?? w.EndDate, (int)w.Status == 4, (int)w.Status == 5, today);
    internal static DateTime? AssignmentDeadline(WorkAssignment a) => a.DueAtUtc.HasValue ? BusinessToday(a.DueAtUtc.Value) : a.DueDate;
    internal static string AssignmentTiming(WorkAssignment a, DateTime today) => Timing(a.ProgressStatus == 2 || a.CompletedAtUtc.HasValue, a.CompletedDate, AssignmentDeadline(a), a.ProgressStatus == 3, a.ProgressStatus == 4, today);
    private static string Timing(bool completed, DateTime? completedDate, DateTime? dueDate, bool risk, bool overdue, DateTime today)
    {
        if (completed) return completedDate.HasValue && dueDate.HasValue ? (completedDate.Value.Date > dueDate.Value.Date ? "LATE" : "ON_TIME") : "UNASSESSED";
        if (overdue || dueDate?.Date < today) return "LATE";
        if (risk) return "AT_RISK";
        return dueDate.HasValue ? "ON_TIME" : "UNASSESSED";
    }
    internal static bool Open(Work w) => !w.IsDeleted && w.Status != WorkStatus.S3;
    internal static string? DeadlineBucket(Work w, DateTime today)
    {
        if (!Open(w)) return null;
        var due = (w.DueDate ?? w.EndDate)?.Date;
        if (due is null) return "NO_DEADLINE";
        var days = (due.Value - today.Date).Days;
        return days < 0 ? "OVERDUE" : days == 0 ? "TODAY" : days <= 7 ? "NEXT_7_DAYS" : days <= 30 ? "NEXT_8_30_DAYS" : "AFTER_30_DAYS";
    }
    internal static List<DashboardLeadershipCountDto> DeadlineCounts(IReadOnlyList<Work> works, DateTime today)
    {
        var counts = works.Select(w => DeadlineBucket(w, today)).Where(key => key is not null)
            .GroupBy(key => key!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return DeadlineBucketKeys.Select(key => new DashboardLeadershipCountDto { Key = key, Count = counts.GetValueOrDefault(key) }).ToList();
    }
    internal static IEnumerable<DashboardLeadershipItemDto> DeadlineItems(IReadOnlyList<Work> works,
        IReadOnlyDictionary<string, DashboardLeadershipItemDto> items, DateTime today, string bucket) =>
        works.Where(w => DeadlineBucket(w, today) == bucket).Select(w => items[w.Id]);
    internal static DateTime? WorkCompletion(Work w) => w.Status == WorkStatus.S3 ? w.CompletedDate : null;
    internal static DateTime? AssignmentCompletion(WorkAssignment a) => a.ProgressStatus == 2 || a.CompletedAtUtc.HasValue ? a.CompletedDate : null;
    internal static Dictionary<string, DashboardLeadershipItemDto> WorkItems(IReadOnlyList<Work> works, IReadOnlyList<WorkAssignment> assignments, DateTime today)
    {
        var groups = assignments.GroupBy(a => a.WorkId).ToDictionary(g => g.Key, g => g.ToList());
        return works.ToDictionary(w => w.Id, w => WorkItem(w, groups.GetValueOrDefault(w.Id) ?? new(), today));
    }
    internal static DashboardLeadershipItemDto WorkItem(Work w, IReadOnlyList<WorkAssignment> assignments, DateTime today)
    {
        var entries = Entries(assignments.Where(a => a.WorkId == w.Id)).ToList();
        return new()
        {
            Id = w.Id, WorkId = w.Id, WorkType = (int)w.Type, WorkCode = string.IsNullOrWhiteSpace(w.Code) ? w.AutoCode : w.Code,
            WorkName = w.Name, WorkStatus = (int)w.Status, Status = CurrentStatus(w, today), Priority = (int)w.Priority,
            Timeliness = WorkTiming(w, today),
            StartDate = w.StartDate.HasValue ? Day(w.StartDate.Value) : null,
            DueDate = (w.DueDate ?? w.EndDate) is { } due ? Day(due) : null,
            CompletedDate = WorkCompletion(w) is { } completed ? Day(completed) : null,
            AssignmentCount = entries.Count, CompletedAssignmentCount = entries.Count(a => a.ProgressStatus == 2),
        };
    }
    internal static IEnumerable<WorkAssignment> Entries(IEnumerable<WorkAssignment> nodes)
    {
        var rows = nodes.Where(a => a.IsActive && !a.IsDeleted).ToList();
        var ids = rows.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        return rows.Where(a => string.IsNullOrEmpty(a.ParentAssignmentId) || !ids.Contains(a.ParentAssignmentId));
    }
    internal static bool Timeline(DashboardLeadershipItemDto item, DateTime from, DateTime to) =>
        item.DueDate is { } end && ParseDay(end) >= from && (item.StartDate is null || ParseDay(item.StartDate) <= to);
    internal static string Bucket(DateTime from, DateTime to)
    {
        DateTime Monday(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        return (to - from).TotalDays < 60 ? "DAY" :
            (Monday(to) - Monday(from)).TotalDays / 7 < 60 ? "WEEK" :
            (to.Year - from.Year) * 12 + to.Month - from.Month < 60 ? "MONTH" : "YEAR";
    }
    internal static List<DashboardLeadershipActivityDto> Activity(DateTime from, DateTime to,
        IEnumerable<DateTime> createdUtc, IEnumerable<DateTime?> completedDays)
    {
        var kind = Bucket(from, to);
        // For very long spans, group calendar years into at most 60 buckets, keeping the entire range.
        var yearStep = Math.Max(1, (int)Math.Ceiling((to.Year - from.Year + 1) / 60d));
        DateTime Start(DateTime d) => kind switch
        {
            "DAY" => d.Date, "WEEK" => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
            "MONTH" => new(d.Year, d.Month, 1), _ => new(from.Year + ((d.Year - from.Year) / yearStep) * yearStep, 1, 1),
        };
        var result = new List<DashboardLeadershipActivityDto>();
        var created = createdUtc.Where(d => d != default).Select(d => BusinessToday(d)).Where(d => d >= from && d <= to).GroupBy(Start).ToDictionary(g => g.Key, g => g.Count());
        var completed = completedDays.Where(d => d.HasValue).Select(d => d!.Value.Date).Where(d => d >= from && d <= to).GroupBy(Start).ToDictionary(g => g.Key, g => g.Count());
        var cursor = Start(from);
        while (cursor <= to)
        {
            var next = kind switch { "DAY" => cursor.AddDays(1), "WEEK" => cursor.AddDays(7), "MONTH" => cursor.AddMonths(1), _ => cursor.AddYears(yearStep) };
            result.Add(new() { FromDate = Day(cursor < from ? from : cursor), ToDate = Day(next.AddDays(-1) > to ? to : next.AddDays(-1)), CreatedCount = created.GetValueOrDefault(cursor), CompletedCount = completed.GetValueOrDefault(cursor) });
            cursor = next;
        }
        return result;
    }
    internal static (string Status, string Timeliness) ReportState(WorkAssignmentReport? report, WorkReportPeriod period, DateTime now)
    {
        var status = report?.Status == tdtd_be.Models.Enums.WorkAssignmentReportStatus.Approved ? "APPROVED" :
            report?.ReturnedAtUtc is not null && report.Status == tdtd_be.Models.Enums.WorkAssignmentReportStatus.Draft ? "RETURNED" :
            report?.Status == tdtd_be.Models.Enums.WorkAssignmentReportStatus.Submitted ? "SUBMITTED" : report is not null ? "DRAFT" : "PENDING";
        var due = report?.DueAtUtc ?? period.DueAtUtc;
        // Submitted/approved are timed against submission, not approval latency. Future pending is unassessed.
        var timing = report?.IsLateSubmission == true ? "LATE" : due is null ? "UNASSESSED" :
            report?.SubmittedAtUtc is { } submitted ? (submitted > due ? "LATE" : "ON_TIME") :
            (status is "APPROVED" or "SUBMITTED") ? "UNASSESSED" : due < now ? "LATE" : "UNASSESSED";
        return (status, timing);
    }
    internal static DashboardLeadershipDto Current(IReadOnlyList<Work> works, IReadOnlyList<WorkAssignment> assignments, DateTime now)
    {
        var today = BusinessToday(now); var from = new DateTime(today.Year, today.Month, 1); var to = from.AddMonths(1).AddDays(-1);
        var allRows = WorkItems(works, assignments, today).Values.ToList();
        var openIds = works.Where(Open).Select(w => w.Id).ToHashSet();
        var rows = allRows.Where(w => openIds.Contains(w.Id)).OrderByDescending(w => w.Timeliness == "LATE").ThenByDescending(w => w.Priority).ThenBy(w => w.DueDate ?? "9999").ThenBy(w => w.Id).ToList();
        var timeline = rows.Where(r => Timeline(r, from, to)).ToList();
        return new()
        {
            Section = "CURRENT", CohortKind = "CURRENT_READABLE_SCOPE", GeneratedAtUtc = now, AsOfDate = Day(today), FromDate = Day(from), ToDate = Day(to),
            TotalWorkCount = allRows.Count, TotalOpenWorkCount = rows.Count, TotalAssignmentCount = assignments.Count,
            InProgressWorkCount = rows.Count(w => w.Status == 2), OverdueWorkCount = rows.Count(w => w.Timeliness == "LATE"),
            NoDeadlineWorkCount = rows.Count(w => w.DueDate is null), OverdueBeforeWindowCount = rows.Count(w => w.Timeliness == "LATE" && w.DueDate is { } d && ParseDay(d) < from),
            WorkStatus = new[] {1, 2, 3, 4, 5}.Select(s => new DashboardLeadershipCountDto { Key = s.ToString(), Count = allRows.Count(w => w.Status == s) }).ToList(),
            AssignmentStatus = new[] {0, 1, 2, 3, 4}.Select(s => new DashboardLeadershipCountDto { Key = s.ToString(), Count = assignments.Count(a => a.ProgressStatus == s) }).ToList(),
            Timeliness = new[] {"ON_TIME", "LATE", "AT_RISK", "UNASSESSED"}.SelectMany(key => new[] {
                new DashboardLeadershipTimingDto { ObjectKind = "WORK", Key = key, Count = allRows.Count(w => w.Timeliness == key) },
                new DashboardLeadershipTimingDto { ObjectKind = "ASSIGNMENT", Key = key, Count = assignments.Count(a => AssignmentTiming(a, today) == key) },
            }).ToList(),
            PriorityStatus = (from p in new[] {3, 2, 1} from s in new[] {1, 2, 4, 5} select new DashboardLeadershipMatrixDto { Priority = p, Status = s, Count = rows.Count(w => w.Priority == p && w.Status == s) }).ToList(),
            DeadlineBuckets = DeadlineCounts(works, today),
            WorkProgress = rows.Take(10).ToList(), Timeline = timeline.Take(10).ToList(), TimelineWorkCount = timeline.Count,
        };
    }
}
