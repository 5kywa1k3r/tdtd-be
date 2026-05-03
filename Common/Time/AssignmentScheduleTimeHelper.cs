using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Common.Time;

public static class AssignmentScheduleTimeHelper
{
    public static bool IsDueOnDate(AssignmentSchedule? schedule, DateTime date)
    {
        if (!ScheduleValidator.IsValid(schedule))
            return false;

        var d = date.Date;

        if (schedule!.StartDate.HasValue && d < schedule.StartDate.Value.Date)
            return false;

        var type = schedule.CycleType!.Trim().ToUpperInvariant();

        return type switch
        {
            ReportCycleTypes.Daily => true,
            ReportCycleTypes.Weekly => IsWeeklyDue(schedule, d),
            ReportCycleTypes.Monthly => IsMonthlyDue(schedule, d),
            ReportCycleTypes.Quarterly => IsQuarterlyDue(schedule, d),
            ReportCycleTypes.SemiAnnual => IsSemiAnnualDue(schedule, d),
            _ => false
        };
    }

    public static List<DateTime> GetDueDatesInRange(
        AssignmentSchedule? schedule,
        DateTime from,
        DateTime to)
    {
        var result = new List<DateTime>();

        if (!ScheduleValidator.IsValid(schedule))
            return result;

        var start = from.Date;
        var end = to.Date;

        if (end < start)
            return result;

        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (IsDueOnDate(schedule, d))
                result.Add(d);
        }

        return result;
    }

    public static string GetPeriodKey(AssignmentSchedule? schedule, DateTime date)
    {
        // NOTE:
        // Tạm thời unify toàn hệ thống theo DayKey.
        // Mỗi due occurrence có 1 key riêng để tránh collapse nhiều ngày trong cùng tuần/tháng.
        return date.Date.ToString("yyyyMMdd");
    }

    public static string GetCurrentPeriodKey(AssignmentSchedule? schedule, DateTime? now = null)
    {
        return GetPeriodKey(schedule, now ?? DateTime.UtcNow);
    }

    public static (DateTime start, DateTime end) GetPeriodRange(
        AssignmentSchedule? schedule,
        DateTime date)
    {
        return SchedulePeriodHelper.GetPeriodRange(schedule?.CycleType, date);
    }

    private static bool IsWeeklyDue(AssignmentSchedule schedule, DateTime d)
    {
        if (schedule.WeekDays is not { Count: > 0 }) return false;

        var weekday = ScheduleWeekHelper.GetBusinessWeekday(d);
        return schedule.WeekDays.Contains(weekday);
    }

    private static bool IsMonthlyDue(AssignmentSchedule schedule, DateTime d)
    {
        if (schedule.MonthDays is not { Count: > 0 }) return false;
        return schedule.MonthDays.Contains(d.Day);
    }

    private static bool IsQuarterlyDue(AssignmentSchedule schedule, DateTime d)
    {
        if (schedule.QuarterDays is not { Length: > 0 }) return false;

        var quarter = SchedulePeriodHelper.GetQuarter(d);
        var quarterStart = SchedulePeriodHelper.GetQuarterStartDate(d.Year, quarter);
        var dayOfQuarter = (d.Date - quarterStart.Date).Days + 1;

        return schedule.QuarterDays.Contains(dayOfQuarter);
    }

    private static bool IsSemiAnnualDue(AssignmentSchedule schedule, DateTime d)
    {
        if (schedule.SemiAnnualDays is not { Length: > 0 }) return false;

        var half = SchedulePeriodHelper.GetHalf(d);
        var halfStart = SchedulePeriodHelper.GetHalfStartDate(d.Year, half);
        var dayOfHalf = (d.Date - halfStart.Date).Days + 1;

        return schedule.SemiAnnualDays.Contains(dayOfHalf);
    }
}