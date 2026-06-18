using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.Services.WorkAssignmentReports;

internal readonly record struct WorkReportSourceWindow(
    DateTime? PeriodAnchorDate,
    DateTime? PeriodStartDate,
    DateTime? PeriodEndDate,
    DateTime? CompletedDate,
    string PeriodKind,
    bool IsHistoricalData);

internal static class WorkAssignmentReportTemporalPolicy
{
    public static bool ContributesToProgress(WorkReportPeriod period)
        => !IsDataOnlyHistoricalPeriod(period);

    public static bool IsDataOnlyHistoricalPeriod(WorkReportPeriod period)
        => false;

    public static WorkReportSourceWindow ResolveSourceWindow(WorkAssignmentReport report)
    {
        var periodKind = string.IsNullOrWhiteSpace(report.PeriodKind)
            ? WorkReportPeriodKind.Scheduled
            : report.PeriodKind.Trim().ToUpperInvariant();

        var start = NormalizeDate(report.PeriodStart ?? report.ReportDate ?? report.StartedDate ?? report.CompletedDate);
        var end = NormalizeDate(report.PeriodEnd ?? report.ReportDate ?? report.CompletedDate ?? report.StartedDate ?? start);
        (start, end) = NormalizeRange(start, end);

        return new WorkReportSourceWindow(
            PeriodAnchorDate: NormalizeDate(report.ReportDate ?? end ?? start ?? report.CompletedDate),
            PeriodStartDate: start,
            PeriodEndDate: end,
            CompletedDate: NormalizeDate(report.CompletedDate),
            PeriodKind: periodKind,
            IsHistoricalData: report.IsHistoricalData);
    }

    public static WorkReportSourceWindow ResolveSourceWindow(WorkReportPeriod period)
    {
        var periodKind = string.IsNullOrWhiteSpace(period.PeriodKind)
            ? WorkReportPeriodKind.Scheduled
            : period.PeriodKind.Trim().ToUpperInvariant();

        var start = NormalizeDate(period.PeriodStart ?? period.ReportDate ?? period.StartedDate ?? period.CompletedDate);
        var end = NormalizeDate(period.PeriodEnd ?? period.ReportDate ?? period.CompletedDate ?? period.StartedDate ?? start);
        (start, end) = NormalizeRange(start, end);

        return new WorkReportSourceWindow(
            PeriodAnchorDate: NormalizeDate(period.ReportDate ?? end ?? start ?? period.CompletedDate),
            PeriodStartDate: start,
            PeriodEndDate: end,
            CompletedDate: NormalizeDate(period.CompletedDate),
            PeriodKind: periodKind,
            IsHistoricalData: period.IsHistoricalData);
    }

    public static bool MatchesPeriodScope(
        WorkAssignmentReport report,
        string? periodScopeMode,
        string? periodKey,
        string? periodKeyFrom,
        string? periodKeyTo)
        => MatchesPeriodScope(
            ResolveSourceWindow(report),
            report.PeriodKey,
            periodScopeMode,
            periodKey,
            periodKeyFrom,
            periodKeyTo);

    public static bool MatchesPeriodScope(
        WorkReportSourceWindow window,
        string? fallbackPeriodKey,
        string? periodScopeMode,
        string? periodKey,
        string? periodKeyFrom,
        string? periodKeyTo)
    {
        var mode = (periodScopeMode ?? "ALL_PERIODS").Trim().ToUpperInvariant();
        if (mode == "ALL_PERIODS")
            return true;

        if (mode == "SINGLE_PERIOD")
        {
            if (TryParseDayKey(periodKey, out var date))
                return WindowOverlaps(window, date, date);

            return string.Equals(NormalizeDayKey(fallbackPeriodKey), NormalizeDayKey(periodKey), StringComparison.Ordinal);
        }

        if (mode == "PERIOD_RANGE")
        {
            if (TryParseDayKey(periodKeyFrom, out var from) && TryParseDayKey(periodKeyTo, out var to))
            {
                if (to < from)
                    (from, to) = (to, from);

                return WindowOverlaps(window, from, to);
            }

            var fallback = NormalizeDayKey(fallbackPeriodKey);
            return !string.IsNullOrWhiteSpace(fallback)
                   && string.CompareOrdinal(fallback, NormalizeDayKey(periodKeyFrom)) >= 0
                   && string.CompareOrdinal(fallback, NormalizeDayKey(periodKeyTo)) <= 0;
        }

        if (mode == "CUMULATIVE_TO_PERIOD")
        {
            if (TryParseDayKey(periodKeyTo, out var to))
                return WindowStartsOnOrBefore(window, to);

            var fallback = NormalizeDayKey(fallbackPeriodKey);
            return !string.IsNullOrWhiteSpace(fallback)
                   && string.CompareOrdinal(fallback, NormalizeDayKey(periodKeyTo)) <= 0;
        }

        return true;
    }

    public static DateTime? NormalizeDate(DateTime? value)
        => value?.Date;

    private static (DateTime? Start, DateTime? End) NormalizeRange(DateTime? start, DateTime? end)
    {
        if (start.HasValue && end.HasValue && end.Value < start.Value)
            return (end.Value, start.Value);

        return (start, end);
    }

    private static bool WindowOverlaps(WorkReportSourceWindow window, DateTime from, DateTime to)
    {
        var start = window.PeriodStartDate ?? window.PeriodAnchorDate ?? window.CompletedDate;
        var end = window.PeriodEndDate ?? window.PeriodAnchorDate ?? window.CompletedDate ?? start;
        if (!start.HasValue || !end.HasValue)
            return false;

        return start.Value.Date <= to.Date && end.Value.Date >= from.Date;
    }

    private static bool WindowStartsOnOrBefore(WorkReportSourceWindow window, DateTime to)
    {
        var start = window.PeriodStartDate ?? window.PeriodAnchorDate ?? window.CompletedDate;
        return start.HasValue && start.Value.Date <= to.Date;
    }

    private static bool TryParseDayKey(string? value, out DateTime date)
    {
        var normalized = NormalizeDayKey(value);
        return DateTime.TryParseExact(
            normalized,
            "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out date);
    }

    private static string? NormalizeDayKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == 8 ? digits : value.Trim();
    }
}
