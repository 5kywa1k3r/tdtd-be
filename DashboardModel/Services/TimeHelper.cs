using tdtd_be.Common.Time;

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
        var range = AppTimeRangeHelper.NormalizeMonthRange(fromUtc, toUtc, DateTime.UtcNow);
        return new DashboardNormalizedRange
        {
            FromUtc = range.FromUtc,
            ToUtc = range.ToUtc,
            Label = range.Label
        };
    }
}
