using System;

namespace tdtd_be.Jobs;
public static class HangfireJobTimeHelper
{
    public static TimeZoneInfo ResolveBangkokTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        catch
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok"); }
            catch { return TimeZoneInfo.Utc; }
        }
    }

    public static bool IsLastSundayOfMonth(DateTime utcNow, TimeZoneInfo tz)
    {
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz).Date;
        if (localDate.DayOfWeek != DayOfWeek.Sunday) return false;

        var last = new DateTime(localDate.Year, localDate.Month, DateTime.DaysInMonth(localDate.Year, localDate.Month));
        while (last.DayOfWeek != DayOfWeek.Sunday)
            last = last.AddDays(-1);

        return localDate == last.Date;
    }
}
