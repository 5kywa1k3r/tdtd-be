using System.Globalization;
using System.Text.RegularExpressions;

namespace tdtd_be.Services.AggregateMapping;

internal sealed record AggregatePartialDate(string Text, string Precision, int Key)
{
    // Included in preview/job fingerprints so a preview from the prior rules cannot be reused.
    internal const string FilterSemantics = "FILTER_PRECISION_OPEN_BOUNDS_20261001_V1";
    internal static AggregatePartialDate Parse(string text)
    {
        text = text.Trim();
        if (DateOnly.TryParseExact(text, ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return new(day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), "DAY", day.Year * 10000 + day.Month * 100 + day.Day);
        if (Regex.IsMatch(text, @"^\d{2}/\d{4}$") && int.TryParse(text[..2], out var month)
            && int.TryParse(text[3..], out var year) && year is >= 1 and <= 9999 && month is >= 1 and <= 12)
            return new(text, "MONTH", year * 100 + month);
        if (Regex.IsMatch(text, @"^\d{4}$") && int.TryParse(text, out var y) && y is >= 1 and <= 9999)
            return new(text, "YEAR", y);
        throw new AggregatePreviewException("AGG_DATE_INVALID");
    }
    internal static int Compare(string left, string right)
    {
        var a = Parse(left); var b = Parse(right);
        var key = (a.Precision, b.Precision) switch {
            ("DAY", "MONTH") => a.Key / 100,
            ("DAY", "YEAR") => a.Key / 10000,
            ("MONTH", "YEAR") => a.Key / 100,
            _ when a.Precision == b.Precision => a.Key,
            _ => throw new AggregatePreviewException("AGG_DATE_PRECISION_UNRESOLVED") };
        return key.CompareTo(b.Key);
    }
    internal static void CheckBounds(string start, string end)
    {
        if (start.Length == 0 || end.Length == 0) return;
        var a = Parse(start); var b = Parse(end);
        if (a.Precision != b.Precision) throw new AggregatePreviewException("AGG_DATE_PRECISION_UNRESOLVED");
        if (a.Key > b.Key) throw new AggregatePreviewException("AGG_LIST_PREDICATE_OPERAND");
    }
}
