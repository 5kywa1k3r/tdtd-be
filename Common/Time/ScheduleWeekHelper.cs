using tdtd_be.Enum;

namespace tdtd_be.Common.Time;

public static class ScheduleWeekHelper
{
    /// <summary>
    /// Tuần bắt đầu từ thứ 2.
    /// </summary>
    public static DateTime StartOfWeek(DateTime date)
    {
        var d = date.Date;
        var diff = d.DayOfWeek switch
        {
            DayOfWeek.Monday => 0,
            DayOfWeek.Tuesday => 1,
            DayOfWeek.Wednesday => 2,
            DayOfWeek.Thursday => 3,
            DayOfWeek.Friday => 4,
            DayOfWeek.Saturday => 5,
            DayOfWeek.Sunday => 6,
            _ => 0
        };

        return d.AddDays(-diff);
    }

    public static int GetBusinessWeekday(DateTime date)
    {
        return date.DayOfWeek switch
        {
            DayOfWeek.Monday => ReportWeekdays.Monday,
            DayOfWeek.Tuesday => ReportWeekdays.Tuesday,
            DayOfWeek.Wednesday => ReportWeekdays.Wednesday,
            DayOfWeek.Thursday => ReportWeekdays.Thursday,
            DayOfWeek.Friday => ReportWeekdays.Friday,
            DayOfWeek.Saturday => ReportWeekdays.Saturday,
            DayOfWeek.Sunday => ReportWeekdays.Sunday,
            _ => throw tdtd_be.Common.Errors.AppExceptionFactory.BadRequest(
                tdtd_be.Common.Errors.AppErrorCode.COMMON_SCHEDULE_VALUE_INVALID,
                new { field = "dayOfWeek", value = date.DayOfWeek })
        };
    }

}
