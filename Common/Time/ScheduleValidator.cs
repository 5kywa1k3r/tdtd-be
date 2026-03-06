using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Common.Time;

public static class ScheduleValidator
{
    public static bool IsValid(AssignmentSchedule? schedule)
    {
        if (schedule == null) return false;
        if (string.IsNullOrWhiteSpace(schedule.CycleType)) return false;

        var type = schedule.CycleType.Trim().ToUpperInvariant();

        if (!ReportCycleTypes.All.Contains(type))
            return false;

        return type switch
        {
            ReportCycleTypes.Daily => true,

            ReportCycleTypes.Weekly =>
                schedule.WeekDays is { Count: > 0 } &&
                schedule.WeekDays.All(d => ReportWeekdays.All.Contains(d)),

            ReportCycleTypes.Monthly =>
                schedule.MonthDays is { Count: > 0 } &&
                schedule.MonthDays.All(IsValidMonthDay),

            ReportCycleTypes.Quarterly =>
                schedule.QuarterDays is { Length: > 0 } &&
                schedule.QuarterDays.All(IsValidQuarterDay),

            ReportCycleTypes.SemiAnnual =>
                schedule.SemiAnnualDays is { Length: > 0 } &&
                schedule.SemiAnnualDays.All(IsValidHalfDay),

            _ => false
        };
    }

    private static bool IsValidMonthDay(int day) => day >= 1 && day <= 31;
    private static bool IsValidQuarterDay(int day) => day >= 1 && day <= 92;
    private static bool IsValidHalfDay(int day) => day >= 1 && day <= 184;
}