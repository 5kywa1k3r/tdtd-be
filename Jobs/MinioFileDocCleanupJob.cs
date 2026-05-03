using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System;
using System.Threading;
using System.Threading.Tasks;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Uploads;

namespace tdtd_be.Jobs;

public sealed class MinioFileDocCleanupJob : IMinioFileDocCleanupJob
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<MinioFileDocCleanupJob> _log;
    private readonly MongoDbContext _ctx;
    private readonly IMinioObjectDeleter _minio;
    private readonly TimeZoneInfo _tz;

    public MinioFileDocCleanupJob(
        IConfiguration cfg,
        ILogger<MinioFileDocCleanupJob> log,
        MongoDbContext ctx,
        IMinioObjectDeleter minio)
    {
        _cfg = cfg;
        _log = log;
        _ctx = ctx;
        _minio = minio;
        _tz = HangfireJobTimeHelper.ResolveBangkokTimeZone();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var enabled = bool.TryParse(_cfg["UploadCleanup:Enabled"], out var e) ? e : true;
        var minioEnabled = bool.TryParse(_cfg["UploadCleanup:MinioCleanupEnabled"], out var m) ? m : true;
        if (!enabled || !minioEnabled)
        {
            _log.LogInformation("MinioFileDocCleanup skipped because feature is disabled.");
            return;
        }

        // Chạy recurring theo mỗi Chủ nhật, nhưng chỉ thực thi thật ở Chủ nhật cuối tháng
        if (!HangfireJobTimeHelper.IsLastSundayOfMonth(DateTime.UtcNow, _tz))
        {
            _log.LogInformation("MinioFileDocCleanup skipped because today is not the last Sunday of the month in {timeZone}.", _tz.Id);
            return;
        }

        await RunCleanupAsync(cancellationToken);
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

        var aFilter = Builders<FileDoc>.Filter.And(
            Builders<FileDoc>.Filter.Eq(x => x.IsDeleted, true),
            Builders<FileDoc>.Filter.Lt(x => x.UpdatedAtUtc, cutoffUtc),
            Builders<FileDoc>.Filter.Ne(x => x.Bucket, null),
            Builders<FileDoc>.Filter.Ne(x => x.ObjectKey, null),
            Builders<FileDoc>.Filter.Ne(x => x.ObjectKey, "")
        );

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

            if (!isGroupB) return;

            var upd = Builders<FileDoc>.Update
                .Set(x => x.IsDeleted, true)
                .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
                .Set(x => x.UpdatedByUserId, null);

            await _ctx.Files.UpdateOneAsync(x => x.Id == f.Id, upd, cancellationToken: ct);
        }

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

            if (docs.Count < take) break;
        }

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

            if (docs.Count < take) break;
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
}
