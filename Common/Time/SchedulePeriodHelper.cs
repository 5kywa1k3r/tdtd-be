using tdtd_be.Enum;

namespace tdtd_be.Common.Time;

public static class SchedulePeriodHelper
{
    public static string GetPeriodKey(string? cycleType, DateTime date)
    {
        //var type = NormalizeCycleType(cycleType);

        //return type switch
        //{
        //    ReportCycleTypes.Daily => date.ToString("yyyy-MM-dd"),
        //    ReportCycleTypes.Weekly => ScheduleWeekHelper.GetWeekPeriodKey(date),
        //    ReportCycleTypes.Monthly => $"{date:yyyy-MM}",
        //    ReportCycleTypes.Quarterly => $"{date.Year}-Q{GetQuarter(date)}",
        //    ReportCycleTypes.SemiAnnual => $"{date.Year}-H{GetHalf(date)}",
        //    _ => date.ToString("yyyy-MM-dd")
        //};
        return date.Date.ToString("yyyyMMdd");
    }

    public static (DateTime start, DateTime end) GetPeriodRange(string? cycleType, DateTime date)
    {
        var type = NormalizeCycleType(cycleType);
        var d = date.Date;

        return type switch
        {
            ReportCycleTypes.Daily => (d, d),
            ReportCycleTypes.Weekly => GetWeekRange(d),
            ReportCycleTypes.Monthly => GetMonthRange(d),
            ReportCycleTypes.Quarterly => GetQuarterRange(d),
            ReportCycleTypes.SemiAnnual => GetHalfRange(d),
            _ => (d, d)
        };
    }

    public static int GetQuarter(DateTime date) => ((date.Month - 1) / 3) + 1;
    public static int GetHalf(DateTime date) => date.Month <= 6 ? 1 : 2;

    public static DateTime GetQuarterStartDate(int year, int quarter) => quarter switch
    {
        1 => new DateTime(year, 1, 1),
        2 => new DateTime(year, 4, 1),
        3 => new DateTime(year, 7, 1),
        4 => new DateTime(year, 10, 1),
        _ => throw new InvalidOperationException("Quý không hợp lệ.")
    };

    public static DateTime GetHalfStartDate(int year, int half) => half switch
    {
        1 => new DateTime(year, 1, 1),
        2 => new DateTime(year, 7, 1),
        _ => throw new InvalidOperationException("Nửa năm không hợp lệ.")
    };

    private static string NormalizeCycleType(string? cycleType)
        => (cycleType ?? string.Empty).Trim().ToUpperInvariant();

    private static (DateTime start, DateTime end) GetWeekRange(DateTime d)
    {
        var start = ScheduleWeekHelper.StartOfWeek(d);
        return (start, start.AddDays(6));
    }

    private static (DateTime start, DateTime end) GetMonthRange(DateTime d)
    {
        var start = new DateTime(d.Year, d.Month, 1);
        return (start, start.AddMonths(1).AddDays(-1));
    }

    private static (DateTime start, DateTime end) GetQuarterRange(DateTime d)
    {
        var start = GetQuarterStartDate(d.Year, GetQuarter(d));
        return (start, start.AddMonths(3).AddDays(-1));
    }

    private static (DateTime start, DateTime end) GetHalfRange(DateTime d)
    {
        var start = GetHalfStartDate(d.Year, GetHalf(d));
        return (start, start.AddMonths(6).AddDays(-1));
    }
}