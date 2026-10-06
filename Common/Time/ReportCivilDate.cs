namespace tdtd_be.Common.Time;

/// <summary>Report calendar days use UTC midnight as a storage marker, not as an instant.</summary>
internal static class ReportCivilDate
{
    public static DateTime Encode(DateTime day) => DateTime.SpecifyKind(day.Date, DateTimeKind.Utc);

    public static DateTime FromInstant(DateTime utc) => Encode(TimeZoneInfo.ConvertTimeFromUtc(
        DateTime.SpecifyKind(utc, DateTimeKind.Utc), AppTimeService.ResolveApplicationTimeZone()));

    public static DateTime? ReadPeriodDay(DateTime? value)
    {
        if (!value.HasValue) return null;
        var day = value.Value;
        // Legacy generated period midnights were Unspecified and BSON converted them
        // through the server zone. Recognize only application midnight; do not shift
        // date-only UTC markers or apply this repair to deadlines/completion input.
        if (day.Kind == DateTimeKind.Utc && day.TimeOfDay != TimeSpan.Zero)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(day, AppTimeService.ResolveApplicationTimeZone());
            if (local.TimeOfDay == TimeSpan.Zero) return Encode(local);
        }
        return Encode(day);
    }
}
