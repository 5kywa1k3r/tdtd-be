namespace tdtd_be.Common.Time;

public sealed class AppUtcDateRange
{
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public DateTime FromDate => FromUtc.Date;
    public DateTime ToDate => ToUtc.Date;
    public string Label { get; init; } = string.Empty;
}

public interface IAppTimeService
{
    DateTime UtcNow { get; }
    TimeZoneInfo ApplicationTimeZone { get; }
    DateTime ToUtc(DateTime value);
    DateTime? ToUtc(DateTime? value);
    DateTime NormalizeUtcDate(DateTime value);
    DateTime EndOfUtcDate(DateTime value);
    DateTime NextLocalMidnightUtc(DateTime utcNow);
    bool IsLastSundayOfMonth(DateTime utcNow);
    AppUtcDateRange NormalizeMonthRange(DateTime? fromUtc, DateTime? toUtc);
}

public sealed class AppTimeService : IAppTimeService
{
    public DateTime UtcNow => DateTime.UtcNow;
    public TimeZoneInfo ApplicationTimeZone { get; } = ResolveApplicationTimeZone();

    public DateTime ToUtc(DateTime value)
        => AppTimeRangeHelper.ToUtc(value);

    public DateTime? ToUtc(DateTime? value)
        => AppTimeRangeHelper.ToUtc(value);

    public DateTime NormalizeUtcDate(DateTime value)
        => AppTimeRangeHelper.EnsureUtcDate(value);

    public DateTime EndOfUtcDate(DateTime value)
        => AppTimeRangeHelper.EndOfUtcDate(value);

    public DateTime NextLocalMidnightUtc(DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, ApplicationTimeZone);
        var nextMidnight = local.Date.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(nextMidnight, ApplicationTimeZone);
    }

    public bool IsLastSundayOfMonth(DateTime utcNow)
    {
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(utcNow, ApplicationTimeZone).Date;
        if (localDate.DayOfWeek != DayOfWeek.Sunday)
            return false;

        var last = new DateTime(
            localDate.Year,
            localDate.Month,
            DateTime.DaysInMonth(localDate.Year, localDate.Month));

        while (last.DayOfWeek != DayOfWeek.Sunday)
            last = last.AddDays(-1);

        return localDate == last.Date;
    }

    public AppUtcDateRange NormalizeMonthRange(DateTime? fromUtc, DateTime? toUtc)
        => AppTimeRangeHelper.NormalizeMonthRange(fromUtc, toUtc, UtcNow);

    public static TimeZoneInfo ResolveApplicationTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
        catch
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok"); }
            catch { return TimeZoneInfo.Utc; }
        }
    }
}

public static class AppTimeRangeHelper
{
    public static AppUtcDateRange NormalizeMonthRange(
        DateTime? fromUtc,
        DateTime? toUtc,
        DateTime utcNow)
    {
        if (!fromUtc.HasValue && !toUtc.HasValue)
        {
            var now = EnsureUtcDate(utcNow);
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd = monthStart.AddMonths(1).AddTicks(-1);

            return new AppUtcDateRange
            {
                FromUtc = monthStart,
                ToUtc = monthEnd,
                Label = $"Tháng {monthStart:MM/yyyy}"
            };
        }

        if (fromUtc.HasValue && !toUtc.HasValue)
        {
            var from = EnsureUtcDate(fromUtc.Value);
            var to = EndOfMonthUtc(from);

            return new AppUtcDateRange
            {
                FromUtc = from,
                ToUtc = to,
                Label = $"Tháng {from:MM/yyyy}"
            };
        }

        if (!fromUtc.HasValue && toUtc.HasValue)
        {
            var toDate = EnsureUtcDate(toUtc.Value);
            var from = new DateTime(toDate.Year, toDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = EndOfMonthUtc(toDate);

            return new AppUtcDateRange
            {
                FromUtc = from,
                ToUtc = to,
                Label = $"Tháng {from:MM/yyyy}"
            };
        }

        var rangeFrom = EnsureUtcDate(fromUtc!.Value);
        var rangeTo = EnsureUtcDate(toUtc!.Value).Date.AddDays(1).AddTicks(-1);

        if (rangeTo < rangeFrom)
            throw tdtd_be.Common.Errors.AppExceptionFactory.BadRequest(
                tdtd_be.Common.Errors.AppErrorCode.COMMON_TIME_RANGE_INVALID,
                new { fromUtc = rangeFrom, toUtc = rangeTo });

        return new AppUtcDateRange
        {
            FromUtc = rangeFrom,
            ToUtc = rangeTo,
            Label = $"Từ {rangeFrom:dd/MM/yyyy} đến {rangeTo:dd/MM/yyyy}"
        };
    }

    public static DateTime EnsureUtcDate(DateTime value)
    {
        var date = value.Date;
        return value.Kind == DateTimeKind.Utc
            ? date
            : DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }

    public static DateTime ToUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    public static DateTime? ToUtc(DateTime? value)
        => value.HasValue ? ToUtc(value.Value) : null;

    public static DateTime EndOfUtcDate(DateTime value)
        => EnsureUtcDate(ToUtc(value)).Date.AddDays(1).AddTicks(-1);

    private static DateTime EndOfMonthUtc(DateTime value)
        => new DateTime(
                value.Year,
                value.Month,
                DateTime.DaysInMonth(value.Year, value.Month),
                23,
                59,
                59,
                999,
                DateTimeKind.Utc)
            .AddTicks(9999);
}
