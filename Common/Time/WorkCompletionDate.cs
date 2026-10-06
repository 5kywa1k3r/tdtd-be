namespace tdtd_be.Common.Time;

/// <summary>A civil completion day, encoded at UTC midnight for BSON; not an instant.</summary>
internal static class WorkCompletionDate
{
    internal static DateTime FromUtc(DateTime instant)
    {
        if (instant.Kind != DateTimeKind.Utc) throw new ArgumentException("Completion instant must be UTC.", nameof(instant));
        var day = TimeZoneInfo.ConvertTimeFromUtc(instant, AppTimeService.ResolveApplicationTimeZone()).Date;
        // An unspecified midnight is converted using the host timezone by the BSON serializer.
        return DateTime.SpecifyKind(day, DateTimeKind.Utc);
    }

    internal static DateTime? Read(DateTime? storedDay, DateTime? completedAtUtc, string? mode)
    {
        // Only these workflows derive the day from the committed decision instant. Historical
        // or manually entered completion days must not be replaced by their entry timestamp.
        // Reconstruct old workflow values on read without migrating or rewriting their history.
        if (storedDay.HasValue && completedAtUtc is { Kind: DateTimeKind.Utc } instant &&
            mode is "APPROVED_REQUEST" or "AUTO_REPORTS_AND_DEADLINE")
            return FromUtc(instant);
        return storedDay;
    }
}
