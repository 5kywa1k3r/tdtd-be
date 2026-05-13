namespace tdtd_be.Models.Enums;

public static class WorkReportCumulativeContributionMode
{
    public const string Include = "INCLUDE";
    public const string Exclude = "EXCLUDE";

    public static string Normalize(string? value)
    {
        var mode = value?.Trim().ToUpperInvariant();
        return string.Equals(mode, Exclude, StringComparison.Ordinal)
            ? Exclude
            : Include;
    }

    public static bool IsIncluded(string? value)
        => string.Equals(Normalize(value), Include, StringComparison.Ordinal);
}
