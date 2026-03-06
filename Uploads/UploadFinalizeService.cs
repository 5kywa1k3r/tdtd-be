using Minio;
using Minio.DataModel.Args;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tusdotnet.Interfaces;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;

namespace tdtd_be.Uploads;

public sealed class UploadFinalizeService
{
    private readonly MongoDbContext _ctx;
    private readonly IMinioClient _minio;
    private readonly UploadOptions _opt;
    private readonly ITusTerminationStore _termination;
    private readonly UploadTokenService _tokens;
    private readonly ILogger<UploadFinalizeService> _log;

    public UploadFinalizeService(
        MongoDbContext ctx,
        IMinioClient minio,
        Microsoft.Extensions.Options.IOptions<UploadOptions> opt,
        ITusTerminationStore termination,
        UploadTokenService tokens,
        ILogger<UploadFinalizeService> log)
    {
        _ctx = ctx;
        _minio = minio;
        _opt = opt.Value;
        _termination = termination;
        _tokens = tokens;
        _log = log;
    }

    public async Task FinalizeAsync(FileCompleteContext ctx)
    {
        var uploadId = ctx.FileId;

        // 1) validate upload token
        if (!ctx.HttpContext.Request.Headers.TryGetValue("Upload-Token", out var tok) || string.IsNullOrWhiteSpace(tok))
            throw new InvalidOperationException("Missing Upload-Token on finalize");

        var payload = _tokens.Validate(tok!);
        if (payload == null)
            throw new InvalidOperationException("Invalid Upload-Token on finalize");
        var sourceId = payload.SourceId;
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new InvalidOperationException("Upload missing SourceId");

        var tusFile = await ctx.GetFileAsync();
        var meta = await tusFile.GetMetadataAsync(ctx.CancellationToken);

        var fileName = GetMeta(meta, "filename") ?? payload.FileName ?? "file.bin";
        var mime = GetMeta(meta, "mime") ?? payload.Mime ?? "application/octet-stream";

        var bucket = _opt.Bucket;
        var safeName = Sanitize(fileName);

        // objectKey: không bắt đầu bằng "/" để tránh lỗi lặt vặt
        var objectKey = $"uploads/{sourceId}/{uploadId}/{safeName}".TrimStart('/');

        await EnsureBucketAsync(bucket, ctx.CancellationToken);

        // 2) PUT MinIO
        try
        {
            await using var content = await tusFile.GetContentAsync(ctx.CancellationToken);

            // ✅ Fix #1: reset stream position nếu seek được (đọc trước đó có thể đẩy position)
            long putSize;
            if (content.CanSeek)
            {
                if (content.Position != 0)
                {
                    _log.LogWarning(
                        "Finalize: content stream position not zero. Reset to 0. uploadId={uploadId}, pos={pos}, len={len}",
                        uploadId, content.Position, content.Length);
                    content.Position = 0;
                }

                // ✅ Fix #2: size dùng Length (đúng nhất), không dùng payload.Length/declared size
                putSize = content.Length;
            }
            else
            {
                // stream không seek được -> fallback payload.Length
                putSize = payload.Length;
            }

            if (putSize <= 0)
            {
                _log.LogError(
                    "Finalize: invalid putSize. uploadId={uploadId}, canSeek={canSeek}, declaredLen={declaredLen}",
                    uploadId, content.CanSeek, payload.Length);
                throw new InvalidOperationException($"Invalid upload size: {putSize}");
            }

            _log.LogInformation(
                "Finalize: putting object to MinIO. uploadId={uploadId}, bucket={bucket}, key={key}, size={size}, mime={mime}, fileName={fileName}",
                uploadId, bucket, objectKey, putSize, mime, fileName);

            await _minio.PutObjectAsync(
                new PutObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(objectKey)
                    .WithStreamData(content)
                    .WithObjectSize(putSize)
                    .WithContentType(string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime),
                ctx.CancellationToken);

            // 3) Insert FileDoc (bằng chứng)
            var doc = new FileDoc
            {
                Id = ObjectId.GenerateNewId().ToString(),
                Bucket = bucket,
                ObjectKey = objectKey,
                UploadId = uploadId,
                OriginalName = fileName,
                MimeType = string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime,
                Size = putSize,
                // MinIO ETag không bắt buộc → để null
                ETag = null,
                CreatedByUserId = payload.UserId,
                CreatedAtUtc = DateTime.UtcNow,
                SourceType = payload.SourceType,
                SourceId = sourceId,
                IsDeleted = false
            };

            try
            {
                var existing = await _ctx.Files
                    .Find(x => x.UploadId == uploadId)
                    .FirstOrDefaultAsync(ctx.CancellationToken);
                if (existing == null)
                {
                    await _ctx.Files.InsertOneAsync(doc, cancellationToken: ctx.CancellationToken);
                }
                else
                {
                    // đã có rồi thì giữ id cũ
                    doc = existing;
                }
            }
            catch (Exception ex)
            {
                // rollback best-effort MinIO (tránh orphan)
                _log.LogError(ex,
                    "Insert FileDoc failed after MinIO put. rollback object. uploadId={uploadId}, bucket={bucket}, key={key}",
                    uploadId, bucket, objectKey);

                try
                {
                    await _minio.RemoveObjectAsync(
                        new RemoveObjectArgs().WithBucket(bucket).WithObject(objectKey),
                        ctx.CancellationToken);
                }
                catch (Exception rex)
                {
                    _log.LogWarning(rex,
                        "Rollback MinIO remove failed. uploadId={uploadId}, bucket={bucket}, key={key}",
                        uploadId, bucket, objectKey);
                }

                throw;
            }
        }
        catch (Minio.Exceptions.MinioException mex)
        {
            // ✅ Log đầy đủ để biết MinIO chết vì gì (bucket/permission/size mismatch/endpoint…)
            _log.LogError(mex,
                "MinIO Put/Stat failed in Finalize. uploadId={uploadId}, bucket={bucket}, key={key}, fileName={fileName}, mime={mime}, payloadLen={len}, sourceType={st}, sourceId={sid}",
                uploadId, bucket, objectKey, fileName, mime, payload.Length, payload.SourceType, sourceId);

            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Finalize failed. uploadId={uploadId}, bucket={bucket}, key={key}, fileName={fileName}",
                uploadId, bucket, objectKey, fileName);

            throw;
        }

        // 5) delete tus temp
        try
        {
            await _termination.DeleteFileAsync(uploadId, ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Delete tus temp failed. uploadId={uploadId}", uploadId);
        }
    }

    private async Task EnsureBucketAsync(string bucket, CancellationToken ct)
    {
        var exists = await _minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), ct);
        if (!exists)
            await _minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), ct);
    }

    private static string? GetMeta(IDictionary<string, Metadata> meta, string key)
        => meta.TryGetValue(key, out var v) ? v.GetString(System.Text.Encoding.UTF8) : null;

    private static string Sanitize(string name)
        => name.Replace("\\", "_").Replace("/", "_").Trim();
}