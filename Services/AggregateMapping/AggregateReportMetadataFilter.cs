using tdtd_be.DTOs.AggregateMapping;
namespace tdtd_be.Services.AggregateMapping;

internal static class AggregateReportMetadataFilter
{
    // Civil completion dates are not instants. Submission/deadline instants use Vietnam days.
    internal static DateOnly? EffectiveDate(AggregateSourceHeader header) => header.IsHistoricalData
        ? header.CompletedDate is { } completed ? DateOnly.FromDateTime(completed) : null
        : VietnamDate(header.SubmittedAtUtc);
    private static DateOnly? VietnamDate(DateTime? value) => value is { } instant
        ? DateOnly.FromDateTime(DateTime.SpecifyKind(instant, DateTimeKind.Utc).AddHours(7)) : null;
    internal static bool Matches(AggregateReportFilterDto? filter, AggregateSourceHeader header)
    {
        if (filter == null) return true;
        if (!header.WholeReportReadable) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
        if (filter.Kinds != null && !filter.Kinds.Contains(header.IsHistoricalData ? "HISTORICAL" : "CURRENT")) return false;
        if (filter.PeriodKeys != null && !filter.PeriodKeys.Contains(header.PeriodKey ?? throw new AggregatePreviewException("AGG_METADATA_PERIOD_UNAVAILABLE"))) return false;
        if (filter.UnitIds != null && !filter.UnitIds.Contains(header.UnitId)) return false;
        return Range(filter.FromDate, filter.ToDate, EffectiveDate(header)) && Range(filter.DueFromDate, filter.DueToDate, VietnamDate(header.DueAtUtc));
    }
    private static bool Range(string? from, string? to, DateOnly? value)
    {
        if (from == null && to == null) return true;
        if (value == null) throw new AggregatePreviewException("AGG_METADATA_DATE_UNAVAILABLE");
        return (from == null || value >= AggregateTimeResolver.Date(from)) && (to == null || value <= AggregateTimeResolver.Date(to));
    }
}
