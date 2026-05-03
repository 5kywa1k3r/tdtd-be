namespace tdtd_be.Models.Enums;

public static class WorkReportPeriodKind
{
    public const string Scheduled = "SCHEDULED";
    public const string UserCreated = "USER_CREATED";

    public static bool IsScheduled(string? value)
        => string.IsNullOrWhiteSpace(value)
           || string.Equals(value, Scheduled, StringComparison.OrdinalIgnoreCase);

    public static bool IsUserCreated(string? value)
        => string.Equals(value, UserCreated, StringComparison.OrdinalIgnoreCase);
}
