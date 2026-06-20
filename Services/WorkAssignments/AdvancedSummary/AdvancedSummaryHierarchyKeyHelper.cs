using System.Globalization;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public static class AdvancedSummaryHierarchyKeyHelper
{
    private const string DayFormat = "yyyy-MM-dd";
    private const string MonthFormat = "yyyy-MM";
    private const string YearFormat = "yyyy";

    public static string ToDayKey(DateTime value)
        => ToUtcDate(value).ToString(DayFormat, CultureInfo.InvariantCulture);

    public static string ToMonthKey(DateTime value)
        => ToUtcDate(value).ToString(MonthFormat, CultureInfo.InvariantCulture);

    public static string ToMonthKey(string dayKey)
    {
        var day = ParseDayKey(dayKey);
        return ToMonthKey(day);
    }

    public static string ToYearKey(DateTime value)
        => ToUtcDate(value).ToString(YearFormat, CultureInfo.InvariantCulture);

    public static string ToYearKeyFromDay(string dayKey)
    {
        var day = ParseDayKey(dayKey);
        return ToYearKey(day);
    }

    public static string ToYearKeyFromMonth(string monthKey)
    {
        var monthStart = ParseMonthKey(monthKey);
        return ToYearKey(monthStart);
    }

    public static DateTime ParseDayKey(string dayKey)
    {
        if (DateTime.TryParseExact(
                dayKey?.Trim(),
                DayFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
        {
            return DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);
        }

        throw new ArgumentException("Invalid day key. Expected yyyy-MM-dd.", nameof(dayKey));
    }

    public static DateTime ParseMonthKey(string monthKey)
    {
        if (DateTime.TryParseExact(
                monthKey?.Trim(),
                MonthFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
        {
            return DateTime.SpecifyKind(new DateTime(value.Year, value.Month, 1), DateTimeKind.Utc);
        }

        throw new ArgumentException("Invalid month key. Expected yyyy-MM.", nameof(monthKey));
    }

    public static DateTime ParseYearKey(string yearKey)
    {
        if (DateTime.TryParseExact(
                yearKey?.Trim(),
                YearFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
        {
            return DateTime.SpecifyKind(new DateTime(value.Year, 1, 1), DateTimeKind.Utc);
        }

        throw new ArgumentException("Invalid year key. Expected yyyy.", nameof(yearKey));
    }

    public static (DateTime StartUtc, DateTime EndExclusiveUtc) GetDayBoundsUtc(string dayKey)
    {
        var start = ParseDayKey(dayKey);
        return (start, start.AddDays(1));
    }

    public static (DateTime StartUtc, DateTime EndExclusiveUtc) GetMonthBoundsUtc(string monthKey)
    {
        var start = ParseMonthKey(monthKey);
        return (start, start.AddMonths(1));
    }

    public static (DateTime StartUtc, DateTime EndExclusiveUtc) GetYearBoundsUtc(string yearKey)
    {
        var start = ParseYearKey(yearKey);
        return (start, start.AddYears(1));
    }

    private static DateTime ToUtcDate(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value.Date, DateTimeKind.Utc)
            : value.ToUniversalTime().Date;
}
