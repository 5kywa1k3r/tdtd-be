using tdtd_be.Common.Time;
using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Services.Common.Time;

public sealed class AssignmentScheduleDueItem
{
    public DateTime DueAtUtc { get; set; }
    public string PeriodKey { get; set; } = default!;
}

public static class AssignmentScheduleDueHelper
{
    public static List<AssignmentScheduleDueItem> GetDueItemsForRollingOccurrenceWindow(
        AssignmentSchedule? schedule,
        DateTime startUtc,
        DateTime nowUtc,
        DateTime? endUtc,
        int count)
    {
        if (schedule == null || count <= 0)
            return new List<AssignmentScheduleDueItem>();

        var start = startUtc.Date;
        var hardEnd = endUtc?.Date;
        if (hardEnd.HasValue && hardEnd.Value < start)
            return new List<AssignmentScheduleDueItem>();

        var anchor = ResolveRollingOccurrenceAnchor(schedule, start, nowUtc.Date, hardEnd);
        if (hardEnd.HasValue && hardEnd.Value < anchor)
            return new List<AssignmentScheduleDueItem>();

        var windowEnd = ResolveRollingWindowEnd(schedule, anchor, count);
        if (hardEnd.HasValue && windowEnd > hardEnd.Value)
            windowEnd = hardEnd.Value;

        if (windowEnd < anchor)
            return new List<AssignmentScheduleDueItem>();

        return GetDueItemsInRange(schedule, anchor, windowEnd)
            .Take(count)
            .ToList();
    }

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

    private static DateTime ResolveRollingAnchorStart(AssignmentSchedule schedule, DateTime nowUtc)
    {
        var today = nowUtc.Date;
        var type = NormalizeCycleType(schedule.CycleType);

        return type switch
        {
            ReportCycleTypes.Weekly => ScheduleWeekHelper.StartOfWeek(today),
            ReportCycleTypes.Monthly => new DateTime(today.Year, today.Month, 1),
            ReportCycleTypes.Quarterly => SchedulePeriodHelper.GetQuarterStartDate(
                today.Year,
                SchedulePeriodHelper.GetQuarter(today)),
            ReportCycleTypes.SemiAnnual => SchedulePeriodHelper.GetHalfStartDate(
                today.Year,
                SchedulePeriodHelper.GetHalf(today)),
            _ => today
        };
    }

    private static DateTime ResolveRollingOccurrenceAnchor(
        AssignmentSchedule schedule,
        DateTime start,
        DateTime today,
        DateTime? hardEnd)
    {
        var cycleStart = ResolveRollingAnchorStart(schedule, today);
        var searchStart = cycleStart < start ? start : cycleStart;
        var searchEnd = today;
        if (hardEnd.HasValue && hardEnd.Value < searchEnd)
            searchEnd = hardEnd.Value;

        if (searchEnd >= searchStart)
        {
            var currentDue = GetDueItemsInRange(schedule, searchStart, searchEnd);
            var lastDue = currentDue.LastOrDefault();
            if (lastDue is not null)
                return lastDue.DueAtUtc.Date;
        }

        return today < start ? start : today;
    }

    private static DateTime ResolveRollingWindowEnd(
        AssignmentSchedule schedule,
        DateTime anchor,
        int count)
    {
        var cycleCount = Math.Max(1, count) + 1;
        var type = NormalizeCycleType(schedule.CycleType);
        var cycleStart = ResolveCycleStart(type, anchor.Date);

        return type switch
        {
            ReportCycleTypes.Weekly => cycleStart.AddDays(cycleCount * 7 - 1),
            ReportCycleTypes.Monthly => cycleStart.AddMonths(cycleCount).AddDays(-1),
            ReportCycleTypes.Quarterly => cycleStart.AddMonths(cycleCount * 3).AddDays(-1),
            ReportCycleTypes.SemiAnnual => cycleStart.AddMonths(cycleCount * 6).AddDays(-1),
            _ => anchor.Date.AddDays(cycleCount - 1)
        };
    }

    private static DateTime ResolveCycleStart(string type, DateTime date)
    {
        return type switch
        {
            ReportCycleTypes.Weekly => ScheduleWeekHelper.StartOfWeek(date),
            ReportCycleTypes.Monthly => new DateTime(date.Year, date.Month, 1),
            ReportCycleTypes.Quarterly => SchedulePeriodHelper.GetQuarterStartDate(
                date.Year,
                SchedulePeriodHelper.GetQuarter(date)),
            ReportCycleTypes.SemiAnnual => SchedulePeriodHelper.GetHalfStartDate(
                date.Year,
                SchedulePeriodHelper.GetHalf(date)),
            _ => date.Date
        };
    }

    private static string NormalizeCycleType(string? cycleType)
        => (cycleType ?? string.Empty).Trim().ToUpperInvariant();
}
