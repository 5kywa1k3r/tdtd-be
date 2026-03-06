using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Uploads;

public interface IMinioObjectDeleter
{
    Task RemoveAsync(string bucket, string objectKey, CancellationToken ct);
}

public sealed class MinioFileDocCleanupHostedService : BackgroundService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<MinioFileDocCleanupHostedService> _log;
    private readonly MongoDbContext _ctx;
    private readonly IMinioObjectDeleter _minio;
    private readonly TimeZoneInfo _tz;

    public MinioFileDocCleanupHostedService(
        IConfiguration cfg,
        ILogger<MinioFileDocCleanupHostedService> log,
        MongoDbContext ctx,
        IMinioObjectDeleter minio)
    {
        _cfg = cfg;
        _log = log;
        _ctx = ctx;
        _minio = minio;

        _tz = TryGetTimeZone("SE Asia Standard Time") ?? TryGetTimeZone("Asia/Bangkok") ?? TimeZoneInfo.Utc;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = bool.TryParse(_cfg["UploadCleanup:Enabled"], out var e) ? e : true;
        var minioEnabled = bool.TryParse(_cfg["UploadCleanup:MinioCleanupEnabled"], out var m) ? m : true;
        if (!enabled || !minioEnabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var nextUtc = NextLastSundayRunUtc(nowUtc);
            _log.LogInformation("MinioFileDocCleanup next run at {nextUtc} UTC", nextUtc);

            var delay = nextUtc - nowUtc;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            await Task.Delay(delay, stoppingToken);

            try
            {
                await RunCleanupAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "MinioFileDocCleanup failed");
            }
        }
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        var olderDays = int.TryParse(_cfg["UploadCleanup:MinioDeleteOlderDays"], out var d) ? d : 7;
        olderDays = Math.Clamp(olderDays, 1, 365);

        var cutoffUtc = DateTime.UtcNow.AddDays(-olderDays);

        var batchSize = int.TryParse(_cfg["UploadCleanup:MinioBatchSize"], out var bs) ? bs : 300;
        batchSize = Math.Clamp(batchSize, 50, 2000);

        var maxPerRun = int.TryParse(_cfg["UploadCleanup:MinioMaxPerRun"], out var mx) ? mx : 3000;
        maxPerRun = Math.Clamp(maxPerRun, 100, 50000);

        _log.LogInformation(
            "MinioFileDocCleanup start. CutoffUtc={cutoffUtc} BatchSize={batchSize} MaxPerRun={maxPerRun}",
            cutoffUtc, batchSize, maxPerRun);

        // ========= Nhóm A: FileDoc đã soft-delete (work đã bỏ) =========
        var aFilter = Builders<FileDoc>.Filter.And(
            Builders<FileDoc>.Filter.Eq(x => x.IsDeleted, true),
            Builders<FileDoc>.Filter.Lt(x => x.UpdatedAtUtc, cutoffUtc),
            Builders<FileDoc>.Filter.Ne(x => x.Bucket, null),
            Builders<FileDoc>.Filter.Ne(x => x.ObjectKey, null),
            Builders<FileDoc>.Filter.Ne(x => x.ObjectKey, "")
        );

        // ========= Nhóm B: FileDoc chưa committed (SourceId null) =========
        var bFilter = Builders<FileDoc>.Filter.And(
            Builders<FileDoc>.Filter.Eq(x => x.IsDeleted, false),
            Builders<FileDoc>.Filter.Or(
                Builders<FileDoc>.Filter.Eq(x => x.SourceId, null),
                Builders<FileDoc>.Filter.Eq(x => x.SourceId, "")
            ),
            Builders<FileDoc>.Filter.Lt(x => x.CreatedAtUtc, cutoffUtc),
            Builders<FileDoc>.Filter.Ne(x => x.Bucket, null),
            Builders<FileDoc>.Filter.Ne(x => x.ObjectKey, null),
            Builders<FileDoc>.Filter.Ne(x => x.ObjectKey, "")
        );

        var deletedOk = 0;
        var deletedFail = 0;
        var pickedA = 0;
        var pickedB = 0;

        async Task ProcessAsync(FileDoc f, bool isGroupB)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(f.Bucket) || string.IsNullOrWhiteSpace(f.ObjectKey))
                return;

            var ok = await TryRemoveMinioAsync(f.Bucket, f.ObjectKey, ct);
            if (!ok)
            {
                deletedFail++;
                return;
            }

            deletedOk++;

            // Group A: đã isDeleted=true => không cần update gì
            if (!isGroupB) return;

            // Group B: chưa committed => soft delete để khỏi quét lại
            var upd = Builders<FileDoc>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedByUserId, "system:cleanup");

            await _ctx.Files.UpdateOneAsync(x => x.Id == f.Id, upd, cancellationToken: ct);
        }

        // ====== A: batch loop ======
        while (pickedA + pickedB < maxPerRun)
        {
            var remain = maxPerRun - (pickedA + pickedB);
            var take = Math.Min(batchSize, remain);

            var docs = await _ctx.Files.Find(aFilter)
                .SortBy(x => x.UpdatedAtUtc)
                .Limit(take)
                .ToListAsync(ct);

            if (docs.Count == 0) break;

            pickedA += docs.Count;

            foreach (var f in docs)
                await ProcessAsync(f, isGroupB: false);

            _log.LogInformation(
                "MinioFileDocCleanup progress A: pickedA={pickedA} deletedOk={deletedOk} deletedFail={deletedFail}",
                pickedA, deletedOk, deletedFail);

            if (docs.Count < take) break; // hết
        }

        // ====== B: batch loop (nếu còn quota) ======
        while (pickedA + pickedB < maxPerRun)
        {
            var remain = maxPerRun - (pickedA + pickedB);
            var take = Math.Min(batchSize, remain);

            var docs = await _ctx.Files.Find(bFilter)
                .SortBy(x => x.CreatedAtUtc)
                .Limit(take)
                .ToListAsync(ct);

            if (docs.Count == 0) break;

            pickedB += docs.Count;

            foreach (var f in docs)
                await ProcessAsync(f, isGroupB: true);

            _log.LogInformation(
                "MinioFileDocCleanup progress B: pickedB={pickedB} deletedOk={deletedOk} deletedFail={deletedFail}",
                pickedB, deletedOk, deletedFail);

            if (docs.Count < take) break; // hết
        }

        if (pickedA + pickedB == 0)
        {
            _log.LogInformation("MinioFileDocCleanup: nothing to clean.");
            return;
        }

        _log.LogInformation(
            "MinioFileDocCleanup done. A={a} B={b} deletedOk={deletedOk} deletedFail={deletedFail} cutoffUtc={cutoffUtc}",
            pickedA, pickedB, deletedOk, deletedFail, cutoffUtc);
    }

    private async Task<bool> TryRemoveMinioAsync(string bucket, string objectKey, CancellationToken ct)
    {
        var delays = new[] { 0, 1000, 3000, 5000 };
        Exception? last = null;

        for (int i = 0; i < delays.Length; i++)
        {
            try
            {
                if (delays[i] > 0) await Task.Delay(delays[i], ct);
                await _minio.RemoveAsync(bucket, objectKey, ct);
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        _log.LogWarning(last, "MinioFileDocCleanup remove failed bucket={bucket} objectKey={objectKey}", bucket, objectKey);
        return false;
    }

    private DateTime NextLastSundayRunUtc(DateTime fromUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, _tz);

        var hour = int.TryParse(_cfg["UploadCleanup:LocalHour"], out var h) ? h : 21;
        var minute = int.TryParse(_cfg["UploadCleanup:LocalMinute"], out var m) ? m : 0;
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);

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