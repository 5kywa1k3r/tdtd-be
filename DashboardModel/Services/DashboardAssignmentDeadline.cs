using tdtd_be.Common.Time;

namespace tdtd_be.DashboardModel.Services;

internal static class DashboardAssignmentDeadline
{
    private static readonly TimeZoneInfo BusinessZone = AppTimeService.ResolveApplicationTimeZone();

    // ONCE follows execution's DueAtUtc precedence. Periodic nodes display the
    // assignment boundary, never LatestDueAtUtc (a submission-period instant).
    internal static DateTime? Day(string assignmentType, DateTime? dueAtUtc, DateTime? dueDate)
    {
        if (string.Equals(assignmentType, "ONCE", StringComparison.OrdinalIgnoreCase) && dueAtUtc.HasValue)
        {
            var day = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dueAtUtc.Value, DateTimeKind.Utc), BusinessZone).Date;
            return DateTime.SpecifyKind(day, DateTimeKind.Utc);
        }
        return dueDate.HasValue ? DateTime.SpecifyKind(dueDate.Value.Date, DateTimeKind.Utc) : null;
    }
}
