using System.Globalization;
using tdtd_be.DTOs.AggregateMapping;

namespace tdtd_be.Services.AggregateMapping;

internal static class AggregateTimeResolver
{
    internal static DateOnly Date(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var day) ? day : throw new AggregatePreviewException("AGG_DATE_INVALID");
    internal static DateOnly VietnamDay(string instant)
    {
        if (!DateTimeOffset.TryParseExact(instant, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
            throw new AggregatePreviewException("AGG_DATE_INVALID");
        return DateOnly.FromDateTime(value.ToOffset(TimeSpan.FromHours(7)).DateTime);
    }
    internal static AggregateResolvedWindowDto Resolve(AggregateTimeRuleDto rule, AggregateReadContext context)
    {
        var declaration = context.DataWindow;
        var result = AggregatePeriodContextContract.ResolveWindow(rule, declaration?.StartDate, declaration?.EndDate,
            rule.Mode == "EXPLICIT_RANGE" ? AggregateDigest.Of(rule) : declaration is null ? "" : AggregateDigest.Of(declaration));
        return result.Window ?? throw new AggregatePreviewException(result.Issue!.Code, result.Issue.Path);
    }
    internal static bool Matches(AggregateResolvedWindowDto window, DateOnly start, DateOnly end)
    {
        if (start > end) throw new AggregatePreviewException("AGG_DATA_WINDOW_UNRESOLVED");
        var lower = Date(window.StartDate); var upper = Date(window.EndDate);
        return window.Match switch
        {
            "CONTAINED" => start >= lower && end <= upper,
            "OVERLAPS_WHOLE_REPORT" => start <= upper && end >= lower,
            _ => throw new AggregatePreviewException("AGG_TIME_BASIS")
        };
    }
    internal static bool MatchesSource(AggregateTimeRuleDto rule, AggregateResolvedWindowDto window,
        AggregateSourceHeader header, AggregatePayload? payload)
    {
        if (rule.SourceDateBasis == "DECLARED_DATA_WINDOW")
        {
            if (header.DataWindow is not { Revision: > 0 } declaration)
                throw new AggregatePreviewException("AGG_SOURCE_DATE_UNAVAILABLE");
            return Matches(window, Date(declaration.StartDate), Date(declaration.EndDate));
        }
        if (rule.SourceDateMemberId == null || payload == null || !payload.Values.TryGetValue(rule.SourceDateMemberId, out var date)
            || date.State != "VALUE" || date.Text == null)
            throw new AggregatePreviewException("AGG_SOURCE_DATE_UNAVAILABLE");
        var point = rule.SourceDateBasis switch
        {
            "DATE_FIELD" when date.Type == "DATE_ONLY" => Date(date.Text),
            "INSTANT_FIELD" when date.Type == "INSTANT" => VietnamDay(date.Text),
            _ => throw new AggregatePreviewException("AGG_SOURCE_DATE_TYPE")
        };
        return Matches(window, point, point);
    }
}
