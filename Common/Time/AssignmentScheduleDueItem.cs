using tdtd_be.Common.Time;
using tdtd_be.Models;

namespace tdtd_be.Services.Common.Time;

public sealed class AssignmentScheduleDueItem
{
    public DateTime DueAtUtc { get; set; }
    public string PeriodKey { get; set; } = default!;
}

public static class AssignmentScheduleDueHelper
{
    public static List<AssignmentScheduleDueItem> GetDueItemsInRange(
    AssignmentSchedule? schedule,
    DateTime fromUtc,
    DateTime toUtc)
    {
        var result = new List<AssignmentScheduleDueItem>();

        if (schedule == null || toUtc < fromUtc)
            return result;

        var dueDates = AssignmentScheduleTimeHelper.GetDueDatesInRange(schedule, fromUtc, toUtc);
        if (dueDates == null || dueDates.Count == 0)
            return result;

        // NOTE:
        // Không collapse/group theo PeriodKey nữa.
        // Mỗi due occurrence sinh ra 1 item riêng.
        // PeriodKey hiện được dùng như DayKey / DueOccurrenceKey để tránh mất kỳ
        // trong các case weekly nhiều ngày / monthly nhiều ngày.
        result = dueDates
            .Select(x => new AssignmentScheduleDueItem
            {
                DueAtUtc = x,
                PeriodKey = AssignmentScheduleTimeHelper.GetPeriodKey(schedule, x) ?? string.Empty
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.PeriodKey))
            .OrderBy(x => x.DueAtUtc)
            .ToList();

        return result;
    }
}