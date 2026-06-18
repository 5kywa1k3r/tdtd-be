namespace tdtd_be.Models.Enums;

public static class WorkReportPeriodKind
{
    public const string Scheduled = "SCHEDULED";

    public static bool IsScheduled(string? value)
        => string.IsNullOrWhiteSpace(value)
           || string.Equals(value, Scheduled, StringComparison.OrdinalIgnoreCase);
}
