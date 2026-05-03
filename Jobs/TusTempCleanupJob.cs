using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using tdtd_be.Jobs;

namespace tdtd_be.Uploads;

public sealed class TusTempCleanupJob : ITusTempCleanupJob
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<TusTempCleanupJob> _log;

    public TusTempCleanupJob(
        IConfiguration cfg,
        ILogger<TusTempCleanupJob> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public Task RunAsync(CancellationToken ct = default)
    {
        var enabled = _cfg.GetValue<bool?>("UploadCleanup:Enabled") ?? true;
        if (!enabled) return Task.CompletedTask;

        var tz = HangfireJobTimeHelper.ResolveBangkokTimeZone();
        if (!HangfireJobTimeHelper.IsLastSundayOfMonth(DateTime.UtcNow, tz))
        {
            _log.LogInformation("TusTempCleanup skipped. Today is not last Sunday in timezone {timeZone}.", tz.Id);
            return Task.CompletedTask;
        }

        var tempPath = _cfg["Tus:TempPath"] ?? "App_Data/tus";
        if (!Directory.Exists(tempPath))
        {
            _log.LogInformation("TusTempCleanup skipped. Temp path not found: {tempPath}", tempPath);
            return Task.CompletedTask;
        }

        var olderDays = Math.Clamp(_cfg.GetValue<int?>("UploadCleanup:TempDeleteOlderDays") ?? 7, 1, 365);
        var cutoffUtc = DateTime.UtcNow.AddDays(-olderDays);
        var deleted = 0;
        var failed = 0;

        foreach (var path in Directory.EnumerateFiles(tempPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var fi = new FileInfo(path);
                if (fi.LastWriteTimeUtc > cutoffUtc) continue;
                fi.IsReadOnly = false;
                fi.Delete();
                deleted++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogWarning(ex, "TusTempCleanup failed to delete path={path}", path);
            }
        }

        _log.LogInformation(
            "TusTempCleanup done. Deleted={deleted} Failed={failed} CutoffUtc={cutoffUtc}",
            deleted, failed, cutoffUtc);

        return Task.CompletedTask;
    }
}
