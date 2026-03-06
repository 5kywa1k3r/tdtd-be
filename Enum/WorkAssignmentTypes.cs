namespace tdtd_be.Enum;

public static class WorkAssignmentTypes
{
    // giao 1 lần
    public const string Once = "ONCE";

    // giao theo kỳ báo cáo
    public const string PeriodicReport = "PERIODIC_REPORT";

    public static readonly string[] All =
    {
        Once,
        PeriodicReport
    };
}