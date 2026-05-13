using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.Configuration;
using Minio;
using Minio.DataModel.Args;
using tdtd_be.Common.Time;

namespace tdtd_be.Jobs;

public sealed class HangfireHistoryArchiveJob : IHangfireHistoryArchiveJob
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IConfiguration _cfg;
    private readonly IMinioClient _minio;
    private readonly IAppTimeService _time;
    private readonly ILogger<HangfireHistoryArchiveJob> _log;

    public HangfireHistoryArchiveJob(
        IConfiguration cfg,
        IMinioClient minio,
        IAppTimeService time,
        ILogger<HangfireHistoryArchiveJob> log)
    {
        _cfg = cfg;
        _minio = minio;
        _time = time;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var enabled = _cfg.GetValue<bool?>("HangfireHistoryArchive:Enabled") ?? true;
        if (!enabled)
        {
            _log.LogInformation("Hangfire history archive skipped because feature is disabled.");
            return;
        }

        var nowUtc = _time.UtcNow;
        var retentionDays = Math.Clamp(
            _cfg.GetValue<int?>("HangfireHistoryArchive:SucceededRetentionDays") ?? 7,
            1,
            365);
        var cutoffUtc = nowUtc.AddDays(-retentionDays);
        var maxJobsPerRun = Math.Clamp(
            _cfg.GetValue<int?>("HangfireHistoryArchive:MaxJobsPerRun") ?? 30_000,
            1,
            100_000);
        var pageSize = Math.Clamp(
            _cfg.GetValue<int?>("HangfireHistoryArchive:PageSize") ?? 500,
            50,
            1_000);
        var maxScanJobs = Math.Clamp(
            _cfg.GetValue<int?>("HangfireHistoryArchive:MaxScanJobs") ?? Math.Max(maxJobsPerRun * 3, pageSize),
            pageSize,
            500_000);
        var includeDeleted = _cfg.GetValue<bool?>("HangfireHistoryArchive:IncludeDeleted") ?? false;

        var monitoring = JobStorage.Current.GetMonitoringApi();
        var archived = new List<ArchivedHangfireJobRecord>(Math.Min(maxJobsPerRun, 10_000));

        CollectSucceededJobs(monitoring, cutoffUtc, maxJobsPerRun, pageSize, maxScanJobs, archived);

        if (includeDeleted && archived.Count < maxJobsPerRun)
        {
            CollectDeletedJobs(
                monitoring,
                cutoffUtc,
                maxJobsPerRun - archived.Count,
                pageSize,
                Math.Max(pageSize, maxScanJobs - archived.Count),
                archived);
        }

        if (archived.Count == 0)
        {
            _log.LogInformation(
                "Hangfire history archive found no terminal non-failed jobs older than {cutoffUtc}.",
                cutoffUtc);
            return;
        }

        var archiveToMinio = _cfg.GetValue<bool?>("HangfireHistoryArchive:ArchiveToMinio") ?? true;
        string? objectKey = null;
        string? sha256 = null;
        long archiveBytes = 0;

        if (archiveToMinio)
        {
            var archivePayload = await BuildArchivePayloadAsync(archived, ct);
            archiveBytes = archivePayload.Length;
            sha256 = ComputeSha256(archivePayload);
            objectKey = await UploadArchiveAsync(archivePayload, nowUtc, cutoffUtc, sha256, ct);
        }

        ExpireArchivedJobs(archived.Select(x => x.JobId));

        _log.LogInformation(
            "Hangfire history archive completed. Jobs={jobCount}, cutoffUtc={cutoffUtc}, archived={archived}, bucketObject={objectKey}, bytes={bytes}, sha256={sha256}",
            archived.Count,
            cutoffUtc,
            archiveToMinio,
            objectKey,
            archiveBytes,
            sha256);
    }

    private static void CollectSucceededJobs(
        IMonitoringApi monitoring,
        DateTime cutoffUtc,
        int maxJobs,
        int pageSize,
        int maxScanJobs,
        List<ArchivedHangfireJobRecord> output)
    {
        var offset = 0;
        var scanned = 0;

        while (output.Count < maxJobs && scanned < maxScanJobs)
        {
            var page = monitoring.SucceededJobs(offset, pageSize);
            if (page.Count == 0)
                break;

            foreach (var item in page)
            {
                scanned++;
                var jobId = item.Key;
                var dto = item.Value;

                if (!dto.InSucceededState || dto.SucceededAt is null || dto.SucceededAt > cutoffUtc)
                    continue;

                var details = monitoring.JobDetails(jobId);
                if (details is null)
                    continue;

                output.Add(ToRecord(jobId, "Succeeded", dto.SucceededAt, details));
                if (output.Count >= maxJobs)
                    break;
            }

            offset += page.Count;
            if (page.Count < pageSize)
                break;
        }
    }

    private static void CollectDeletedJobs(
        IMonitoringApi monitoring,
        DateTime cutoffUtc,
        int maxJobs,
        int pageSize,
        int maxScanJobs,
        List<ArchivedHangfireJobRecord> output)
    {
        var offset = 0;
        var scanned = 0;

        while (output.Count < maxJobs && scanned < maxScanJobs)
        {
            var page = monitoring.DeletedJobs(offset, pageSize);
            if (page.Count == 0)
                break;

            foreach (var item in page)
            {
                scanned++;
                var jobId = item.Key;
                var dto = item.Value;

                if (!dto.InDeletedState || dto.DeletedAt is null || dto.DeletedAt > cutoffUtc)
                    continue;

                var details = monitoring.JobDetails(jobId);
                if (details is null)
                    continue;

                output.Add(ToRecord(jobId, "Deleted", dto.DeletedAt, details));
                if (output.Count >= maxJobs)
                    break;
            }

            offset += page.Count;
            if (page.Count < pageSize)
                break;
        }
    }

    private async Task<byte[]> BuildArchivePayloadAsync(
        IReadOnlyCollection<ArchivedHangfireJobRecord> records,
        CancellationToken ct)
    {
        await using var payload = new MemoryStream();
        await using (var gzip = new GZipStream(payload, CompressionLevel.Optimal, leaveOpen: true))
        await using (var writer = new StreamWriter(gzip, Encoding.UTF8))
        {
            foreach (var record in records)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonOptions));
            }
        }

        return payload.ToArray();
    }

    private async Task<string> UploadArchiveAsync(
        byte[] archivePayload,
        DateTime nowUtc,
        DateTime cutoffUtc,
        string sha256,
        CancellationToken ct)
    {
        var bucket = _cfg["HangfireHistoryArchive:Bucket"]
            ?? _cfg["Minio:Bucket"]
            ?? "tdtd-attachments";
        var prefix = NormalizeObjectPrefix(
            _cfg["HangfireHistoryArchive:ObjectPrefix"] ?? "ops/hangfire-history");
        var objectKey = $"{prefix}/{nowUtc:yyyy/MM/dd}/hangfire-history-{nowUtc:yyyyMMdd-HHmmss}-{cutoffUtc:yyyyMMdd-HHmmss}.jsonl.gz";

        var exists = await _minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), ct);
        if (!exists)
            await _minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), ct);

        await using var stream = new MemoryStream(archivePayload);
        await _minio.PutObjectAsync(
            new PutObjectArgs()
                .WithBucket(bucket)
                .WithObject(objectKey)
                .WithStreamData(stream)
                .WithObjectSize(archivePayload.Length)
                .WithContentType("application/gzip")
                .WithHeaders(new Dictionary<string, string>
                {
                    ["x-amz-meta-archive-kind"] = "hangfire-history",
                    ["x-amz-meta-cutoff-utc"] = cutoffUtc.ToString("O", CultureInfo.InvariantCulture),
                    ["x-amz-meta-sha256"] = sha256
                }),
            ct);

        return $"{bucket}/{objectKey}";
    }

    private static void ExpireArchivedJobs(IEnumerable<string> jobIds)
    {
        const int chunkSize = 1_000;
        using var connection = JobStorage.Current.GetConnection();

        foreach (var chunk in jobIds.Distinct(StringComparer.Ordinal).Chunk(chunkSize))
        {
            using var tx = connection.CreateWriteTransaction();
            foreach (var jobId in chunk)
                tx.ExpireJob(jobId, TimeSpan.Zero);
            tx.Commit();
        }
    }

    private static ArchivedHangfireJobRecord ToRecord(
        string jobId,
        string finalState,
        DateTime? finalizedAtUtc,
        JobDetailsDto details)
    {
        return new ArchivedHangfireJobRecord
        {
            JobId = jobId,
            ArchivedAtUtc = DateTime.UtcNow,
            FinalState = finalState,
            FinalizedAtUtc = finalizedAtUtc,
            CreatedAtUtc = details.CreatedAt,
            ExpireAtUtc = details.ExpireAt,
            Invocation = ToInvocation(details.InvocationData),
            Method = ToMethod(details.Job, details.InvocationData),
            Properties = details.Properties is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(details.Properties, StringComparer.Ordinal),
            History = details.History?
                .Select(x => new ArchivedHangfireStateRecord
                {
                    StateName = x.StateName,
                    Reason = x.Reason,
                    CreatedAtUtc = x.CreatedAt,
                    Data = x.Data is null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(x.Data, StringComparer.Ordinal)
                })
                .ToList() ?? new List<ArchivedHangfireStateRecord>()
        };
    }

    private static ArchivedHangfireInvocationRecord ToInvocation(InvocationData? invocation)
        => new()
        {
            Type = invocation?.Type,
            Method = invocation?.Method,
            ParameterTypes = Truncate(invocation?.ParameterTypes, 8_000),
            Arguments = Truncate(invocation?.Arguments, 16_000),
            Queue = invocation?.Queue
        };

    private static ArchivedHangfireMethodRecord ToMethod(Job? job, InvocationData? invocation)
        => new()
        {
            Type = job?.Type.FullName ?? invocation?.Type,
            Method = job?.Method.Name ?? invocation?.Method,
            Arguments = job?.Args?
                .Select(x => Truncate(Convert.ToString(x, CultureInfo.InvariantCulture), 2_000))
                .ToList() ?? new List<string?>()
        };

    private static string NormalizeObjectPrefix(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(normalized) ? "ops/hangfire-history" : normalized;
    }

    private static string ComputeSha256(byte[] payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value[..maxLength];

    private sealed class ArchivedHangfireJobRecord
    {
        public string JobId { get; init; } = string.Empty;
        public DateTime ArchivedAtUtc { get; init; }
        public string FinalState { get; init; } = string.Empty;
        public DateTime? FinalizedAtUtc { get; init; }
        public DateTime? CreatedAtUtc { get; init; }
        public DateTime? ExpireAtUtc { get; init; }
        public ArchivedHangfireInvocationRecord Invocation { get; init; } = new();
        public ArchivedHangfireMethodRecord Method { get; init; } = new();
        public Dictionary<string, string> Properties { get; init; } = new(StringComparer.Ordinal);
        public List<ArchivedHangfireStateRecord> History { get; init; } = new();
    }

    private sealed class ArchivedHangfireInvocationRecord
    {
        public string? Type { get; init; }
        public string? Method { get; init; }
        public string? ParameterTypes { get; init; }
        public string? Arguments { get; init; }
        public string? Queue { get; init; }
    }

    private sealed class ArchivedHangfireMethodRecord
    {
        public string? Type { get; init; }
        public string? Method { get; init; }
        public List<string?> Arguments { get; init; } = new();
    }

    private sealed class ArchivedHangfireStateRecord
    {
        public string? StateName { get; init; }
        public string? Reason { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public Dictionary<string, string> Data { get; init; } = new(StringComparer.Ordinal);
    }
}
