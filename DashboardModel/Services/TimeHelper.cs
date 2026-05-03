namespace tdtd_be.DashboardModel.Services;

public sealed class DashboardNormalizedRange
{
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }

    public DateTime FromDate => FromUtc.Date;
    public DateTime ToDate => ToUtc.Date;

    public string Label { get; init; } = string.Empty;
}

public static class DashboardTimeRangeHelper
{
    public static DashboardNormalizedRange NormalizeMonthRange(DateTime? fromUtc, DateTime? toUtc)
    {
        if (!fromUtc.HasValue && !toUtc.HasValue)
        {
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd = monthStart.AddMonths(1).AddTicks(-1);

            return new DashboardNormalizedRange
            {
                FromUtc = monthStart,
                ToUtc = monthEnd,
                Label = $"Tháng {monthStart:MM/yyyy}"
            };
        }

        if (fromUtc.HasValue && !toUtc.HasValue)
        {
            var from = EnsureUtcDate(fromUtc.Value);
            var to = new DateTime(from.Year, from.Month, DateTime.DaysInMonth(from.Year, from.Month), 23, 59, 59, 999, DateTimeKind.Utc)
                .AddTicks(9999);

            return new DashboardNormalizedRange
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
            var to = new DateTime(toDate.Year, toDate.Month, DateTime.DaysInMonth(toDate.Year, toDate.Month), 23, 59, 59, 999, DateTimeKind.Utc)
                .AddTicks(9999);

            return new DashboardNormalizedRange
            {
                FromUtc = from,
                ToUtc = to,
                Label = $"Tháng {from:MM/yyyy}"
            };
        }

        {
            var from = EnsureUtcDate(fromUtc!.Value);
            var to = EnsureUtcDate(toUtc!.Value).Date.AddDays(1).AddTicks(-1);

            if (to < from)
                throw new InvalidOperationException("Khoảng thời gian không hợp lệ.");

            return new DashboardNormalizedRange
            {
                FromUtc = from,
                ToUtc = to,
                Label = $"Từ {from:dd/MM/yyyy} đến {to:dd/MM/yyyy}"
            };
        }
    }

    private static DateTime EnsureUtcDate(DateTime value)
    {
        var date = value.Date;
        return value.Kind == DateTimeKind.Utc
            ? date
            : DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }
}