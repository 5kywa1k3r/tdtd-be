namespace tdtd_be.Uploads;

public sealed class TusTempCleanupHostedService : BackgroundService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<TusTempCleanupHostedService> _log;
    private readonly TimeZoneInfo _tz;

    public TusTempCleanupHostedService(IConfiguration cfg, ILogger<TusTempCleanupHostedService> log)
    {
        _cfg = cfg;
        _log = log;

        // Windows vs Linux timezone id
        _tz = TryGetTimeZone("SE Asia Standard Time") ?? TryGetTimeZone("Asia/Bangkok") ?? TimeZoneInfo.Utc;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = bool.Parse(_cfg["UploadCleanup:Enabled"] ?? "true");
        if (!enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var nextUtc = NextLastSundayRunUtc(nowUtc);

            _log.LogInformation("TusTempCleanup next run at {nextUtc} UTC", nextUtc);

            var delay = nextUtc - nowUtc;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            await Task.Delay(delay, stoppingToken);

            try { RunCleanup(); }
            catch (Exception ex) { _log.LogError(ex, "TusTempCleanup failed"); }
        }
    }

    private void RunCleanup()
    {
        var tempPath = _cfg["Tus:TempPath"] ?? "App_Data/tus";
        if (!Directory.Exists(tempPath)) return;

        var olderDays = int.Parse(_cfg["UploadCleanup:TempDeleteOlderDays"] ?? "7");
        var cutoffUtc = DateTime.UtcNow.AddDays(-olderDays);

        var deleted = 0;

        foreach (var path in Directory.EnumerateFiles(tempPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.LastWriteTimeUtc > cutoffUtc) continue;
                fi.IsReadOnly = false;
                fi.Delete();
                deleted++;
            }
            catch { /* ignore per file */ }
        }

        _log.LogInformation("TusTempCleanup done. Deleted={deleted} CutoffUtc={cutoffUtc}", deleted, cutoffUtc);
    }

    private DateTime NextLastSundayRunUtc(DateTime fromUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, _tz);
        var hour = int.Parse(_cfg["UploadCleanup:LocalHour"] ?? "21");
        var minute = int.Parse(_cfg["UploadCleanup:LocalMinute"] ?? "0");

        var candidateLocal = LastSundayOfMonth(local.Year, local.Month).Date
            .AddHours(hour).AddMinutes(minute);

        if (candidateLocal <= local)
        {
            var next = local.AddMonths(1);
            candidateLocal = LastSundayOfMonth(next.Year, next.Month).Date
                .AddHours(hour).AddMinutes(minute);
        }

        return TimeZoneInfo.ConvertTimeToUtc(candidateLocal, _tz);
    }

    private static DateTime LastSundayOfMonth(int year, int month)
    {
        var d = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(-1);
        return d;
    }

    private static TimeZoneInfo? TryGetTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return null; }
    }
}