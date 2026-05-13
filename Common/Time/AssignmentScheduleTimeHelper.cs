using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Common.Time;

public static class AssignmentScheduleTimeHelper
{
    public static List<DateTime> GetDueDatesInRange(
        AssignmentSchedule? schedule,
        DateTime from,
        DateTime to)
    {
        if (!ScheduleValidator.IsValid(schedule))
            return new List<DateTime>();

        var start = from.Date;
        var end = to.Date;

        if (end < start)
            return new List<DateTime>();

        return AssignmentScheduleOccurrenceHelper.GenerateOccurrences(schedule!, start, end);
    }

    public static string GetPeriodKey(AssignmentSchedule? schedule, DateTime date)
    {
        // NOTE:
        // Tạm thời unify toàn hệ thống theo DayKey.
        // Mỗi due occurrence có 1 key riêng để tránh collapse nhiều ngày trong cùng tuần/tháng.
        return date.Date.ToString("yyyyMMdd");
    }

    public static (DateTime start, DateTime end) GetPeriodRange(
        AssignmentSchedule? schedule,
        DateTime date)
    {
        return SchedulePeriodHelper.GetPeriodRange(schedule?.CycleType, date);
    }
}
