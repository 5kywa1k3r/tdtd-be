using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Common.Time;

namespace tdtd_be.Services.AggregateMapping;

// Metadata only: no payload reads, no progress/evaluation override of approval eligibility.
internal static class AggregateReportSetFilter
{
    internal const string Mode = "REPORT_SET";
    internal static readonly string[] DateFields = ["COMPLETION_OR_SUBMISSION", "SUBMITTED", "COMPLETED",
        "ASSIGNED", "ASSIGNMENT_START", "ASSIGNMENT_COMPLETED", "REPORT_STARTED", "PERIOD_START", "PERIOD_END",
        "WORK_START", "WORK_END", "DUE"];
    internal static readonly string[] SetFields = ["UNIT", "PERIOD", "DATA_KIND"];

    internal static void Validate(AggregateReportSetDto? filter, string path, Action<string, string, string> add)
    {
        void Error() => add("AGG_REPORT_SET_INVALID", path, "Choose supported report metadata conditions and valid operands.");
        if (filter == null || filter.Version != 1 || filter.Junction is not ("AND" or "OR")
            || filter.Conditions == null || filter.Conditions.Count > 32) { Error(); return; }
        foreach (var c in filter.Conditions)
        {
            if (c == null || c.Values == null || c.Values.Count > 1000 || c.Values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 128)) { Error(); continue; }
            if (SetFields.Contains(c.Field))
            {
                if (c.Mode != null || c.Operator is not ("IN" or "NOT_IN") || c.Values.Count == 0
                    || c.Values.Distinct(StringComparer.Ordinal).Count() != c.Values.Count
                    || c.Field == "DATA_KIND" && c.Values.Any(v => v is not ("CURRENT" or "HISTORICAL"))) Error();
                continue;
            }
            if (!DateFields.Contains(c.Field)) { Error(); continue; }
            if (c.Operator is "PRESENT" or "ABSENT")
            { if (c.Values.Count != 0 || c.Mode != null) Error(); continue; }
            if (c.Mode is "TARGET_DATA_WINDOW" or "CUMULATIVE_FROM")
            {
                if (c.Operator != "RANGE" || c.Values.Count != (c.Mode == "TARGET_DATA_WINDOW" ? 0 : 1)
                    || c.Values.Any(v => !ValidDate(v))) Error();
                continue;
            }
            if (c.Mode is not (null or "EXPLICIT_RANGE") || c.Values.Any(v => !ValidDate(v))) { Error(); continue; }
            if (c.Operator == "RANGE")
            { if (c.Values.Count != 2 || string.CompareOrdinal(c.Values[0], c.Values[1]) > 0) Error(); }
            else if (c.Operator is not ("EQ" or "LT" or "LE" or "GT" or "GE") || c.Values.Count != 1) Error();
        }
    }
    private static bool ValidDate(string s) => DateOnly.TryParseExact(s, "yyyy-MM-dd",
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
    private static DateOnly? Civil(DateTime? date) => date is { } d ? DateOnly.FromDateTime(d) : null;
    private static DateOnly? Vietnam(DateTime? date) => date is { } d ? DateOnly.FromDateTime(d.AddHours(7)) : null;
    private static DateOnly? Date(AggregateSourceHeader h, string field) => field switch
    {
        "COMPLETION_OR_SUBMISSION" => AggregateReportMetadataFilter.EffectiveDate(h),
        "SUBMITTED" => Vietnam(h.SubmittedAtUtc), "COMPLETED" => Civil(h.CompletedDate),
        "ASSIGNED" => Vietnam(h.AssignedAtUtc), "ASSIGNMENT_START" => Civil(h.AssignmentStartDate),
        "ASSIGNMENT_COMPLETED" => Civil(h.AssignmentCompletedDate), "REPORT_STARTED" => Civil(h.StartedDate),
        "PERIOD_START" => Civil(ReportCivilDate.ReadPeriodDay(h.PeriodStart)),
        "PERIOD_END" => Civil(ReportCivilDate.ReadPeriodDay(h.PeriodEnd)),
        "WORK_START" => Civil(h.WorkStartDate), "WORK_END" => Civil(h.WorkEndDate), "DUE" => Vietnam(h.DueAtUtc),
        _ => throw new AggregatePreviewException("AGG_REPORT_SET_INVALID")
    };
    // Three-valued conditions: absent metadata is not a fabricated date or a false match.
    // Explicit ABSENT/PRESENT is definitive. AND false / OR true can resolve unknown operands.
    internal static bool? Matches(AggregateReportSetDto filter, AggregateSourceHeader header, AggregateReadContext context)
    {
        if (!header.WholeReportReadable) throw new AggregatePreviewException("AGG_SOURCE_UNAVAILABLE");
        if (filter.Conditions.Count == 0) return true;
        var unknown = false;
        foreach (var c in filter.Conditions)
        {
            bool? result;
            if (SetFields.Contains(c.Field))
            {
                var value = c.Field switch { "UNIT" => header.UnitId, "PERIOD" => header.PeriodKey,
                    _ => header.IsHistoricalData ? "HISTORICAL" : "CURRENT" };
                result = value == null ? null : c.Operator == "IN" ? c.Values.Contains(value) : !c.Values.Contains(value);
            }
            else
            {
                var value = Date(header, c.Field);
                if (c.Operator == "ABSENT") result = value == null;
                else if (c.Operator == "PRESENT") result = value != null;
                else if (value == null) result = null;
                else
                {
                    var values = c.Values;
                    if (c.Mode is "TARGET_DATA_WINDOW" or "CUMULATIVE_FROM")
                    {
                        var window = context.DataWindow ?? throw new AggregatePreviewException("AGG_DATA_WINDOW_UNRESOLVED");
                        values = [c.Mode == "TARGET_DATA_WINDOW" ? window.StartDate : c.Values[0], window.EndDate];
                        if (string.CompareOrdinal(values[0], values[1]) > 0)
                            throw new AggregatePreviewException("AGG_DATA_WINDOW_UNRESOLVED");
                    }
                    var lower = AggregateTimeResolver.Date(values[0]);
                    result = c.Operator switch { "EQ" => value == lower, "LT" => value < lower, "LE" => value <= lower,
                        "GT" => value > lower, "GE" => value >= lower,
                        "RANGE" => value >= lower && value <= AggregateTimeResolver.Date(values[1]),
                        _ => throw new AggregatePreviewException("AGG_REPORT_SET_INVALID") };
                }
            }
            if (filter.Junction == "AND" && result == false) return false;
            if (filter.Junction == "OR" && result == true) return true;
            unknown |= result == null;
        }
        return unknown ? null : filter.Junction == "AND";
    }
}
