namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

// Inclusive UTC calendar dates, independent of scalar hierarchy rollups.
public static class AdvancedNativeRange
{
    public const int MaximumDays = 366;

    public static (DateTime StartUtc, DateTime EndExclusiveUtc) Bounds(string key)
    {
        if (key is null || key.Length != 22 || key.Substring(10, 2) != "..")
            throw new ArgumentException("Expected yyyy-MM-dd..yyyy-MM-dd.");
        var start = AdvancedSummaryHierarchyKeyHelper.ParseDayKey(key[..10]);
        var last = AdvancedSummaryHierarchyKeyHelper.ParseDayKey(key[12..]);
        if (AdvancedSummaryHierarchyKeyHelper.ToDayKey(start) != key[..10]
            || AdvancedSummaryHierarchyKeyHelper.ToDayKey(last) != key[12..]
            || last < start || (last - start).Days >= MaximumDays)
            throw new ArgumentException("Invalid native range.");
        return (start, last.AddDays(1));
    }
}
