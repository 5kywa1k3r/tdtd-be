namespace tdtd_be.Models.Enums;

public static class WorkReportDataOrigin
{
    public const string ManualInput = "MANUAL_INPUT";
    public const string AutoSummary = "AUTO_SUMMARY";
    public const string CopiedSummary = "COPIED_SUMMARY";
    public const string PartialMapping = "PARTIAL_MAPPING";

    public static string Normalize(string? value)
    {
        var origin = value?.Trim().ToUpperInvariant();
        return origin switch
        {
            AutoSummary => AutoSummary,
            CopiedSummary => CopiedSummary,
            PartialMapping => PartialMapping,
            _ => ManualInput
        };
    }

    public static string DefaultContributionMode(string? value)
    {
        var origin = Normalize(value);
        return origin is AutoSummary or CopiedSummary
            ? WorkReportCumulativeContributionMode.Exclude
            : WorkReportCumulativeContributionMode.Include;
    }

    public static bool IsWholeReportSummary(string? value)
    {
        var origin = Normalize(value);
        return origin is AutoSummary or CopiedSummary;
    }
}
