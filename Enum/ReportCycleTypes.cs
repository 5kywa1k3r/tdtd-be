namespace tdtd_be.Enum;

public static class ReportCycleTypes
{
    public const string Daily = "DAILY";
    public const string Weekly = "WEEKLY";
    public const string Monthly = "MONTHLY";
    public const string Quarterly = "QUARTERLY";
    public const string SemiAnnual = "SEMI_ANNUAL";

    public static readonly string[] All =
    {
        Daily,
        Weekly,
        Monthly,
        Quarterly,
        SemiAnnual
    };
}