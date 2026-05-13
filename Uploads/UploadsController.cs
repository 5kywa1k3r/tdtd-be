using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.WorkDocuments;
using tdtd_be.Uploads;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/uploads")]
public sealed class UploadsController : ControllerBase
{
    private readonly IMinioClient _minio;
    private readonly IConfiguration _cfg;
    private readonly UploadOptions _opt;
    private readonly MeAccessor _me;
    private readonly MongoDbContext _ctx;
    private readonly IWorkDocumentPermissionService _documentPermission;

    public UploadsController(IMinioClient minio, IConfiguration cfg, MongoDbContext ctx,
        Microsoft.Extensions.Options.IOptions<UploadOptions> opt,
        MeAccessor me,
        IWorkDocumentPermissionService documentPermission)
    {
        _minio = minio;
        _cfg = cfg;
        _ctx = ctx;
        _opt = opt.Value;
        _me = me;
        _documentPermission = documentPermission;

    }

    private string Bucket => _cfg["Minio:Bucket"] ?? "tdtd-attachments";

    private static string Sanitize(string name)
        => name.Replace("\\", "_").Replace("/", "_").Trim();

    private string BuildObjectKey(string uploadId, string fileName)
        => $"uploads/{uploadId}/{Sanitize(fileName)}";

    // ===============================
    // 1️⃣ VERIFY
    // ===============================
    [HttpGet("verify")]
    public async Task<IActionResult> Verify([FromQuery] string uploadId, [FromQuery] string fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_ID_REQUIRED);

        var me = _me.RequireMe();

        var filter = Builders<FileDoc>.Filter.And(
            Builders<FileDoc>.Filter.Eq(x => x.UploadId, uploadId),
            Builders<FileDoc>.Filter.Eq(x => x.CreatedByUserId, me.Id),
            Builders<FileDoc>.Filter.Eq(x => x.IsDeleted, false)
        );

        var doc = await _ctx.Files.Find(filter).FirstOrDefaultAsync(ct);
        if (doc == null) return Ok(new { ok = false, reason = "not-committed" });

        // optional: double-check MinIO exists
        try
        {
            var stat = await _minio.StatObjectAsync(
                new StatObjectArgs().WithBucket(doc.Bucket).WithObject(doc.ObjectKey),
                ct);

            return Ok(new
            {
                ok = true,
                fileId = doc.Id,
                bucket = doc.Bucket,
                objectKey = doc.ObjectKey,
                size = stat.Size,
                mime = stat.ContentType,
                etag = stat.ETag
            });
        }
        catch
        {
            return Ok(new { ok = false, reason = "minio-missing", fileId = doc.Id, bucket = doc.Bucket, objectKey = doc.ObjectKey });
        }
    }

    // ===============================
    // 2️⃣ PRESIGNED URL (RECOMMENDED)
    // ===============================
    [HttpGet("presign")]
    public async Task<IActionResult> Presign([FromQuery] string fileId, [FromQuery] int? ttlSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_FILE_ID_REQUIRED);

        var me = _me.RequireMe();

        var doc = await _ctx.Files.Find(x => x.Id == fileId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        if (doc == null)
            throw AppExceptionFactory.NotFound(AppErrorCode.UPLOAD_FILE_NOT_FOUND, new { fileId });

        await _documentPermission.EnsureCanReadFileAsync(doc, me.Id, ct);

        var ttl = ttlSeconds ?? _opt.PresignTtlSecondsDefault;
        ttl = Math.Clamp(ttl, 30, _opt.PresignTtlSecondsMax);

        // MinIO SDK: không có ct ở PresignedGetObjectAsync
        var url = await _minio.PresignedGetObjectAsync(
            new PresignedGetObjectArgs()
                .WithBucket(doc.Bucket)
                .WithObject(doc.ObjectKey)
                .WithExpiry(ttl));

        return Ok(new { ok = true, url, expiresIn = ttl, fileId = doc.Id });
    }

    // ===============================
    // 3️⃣ STREAM QUA BE (OPTIONAL)
    // ===============================
    [HttpGet("download")]
    public async Task<IActionResult> Download(
        [FromQuery] string uploadId,
        [FromQuery] string fileName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uploadId) ||
            string.IsNullOrWhiteSpace(fileName))
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_DOWNLOAD_ARGUMENTS_REQUIRED);

        var objectKey = BuildObjectKey(uploadId, fileName);

        try
        {
            var memory = new MemoryStream();

            await _minio.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(Bucket)
                    .WithObject(objectKey)
                    .WithCallbackStream(stream =>
                    {
                        stream.CopyTo(memory);
                    }),
                ct);

            memory.Position = 0;

            return File(
                memory,
                "application/octet-stream",
                fileName);
        }
        catch
        {
            throw AppExceptionFactory.NotFound(AppErrorCode.UPLOAD_FILE_NOT_FOUND, new { uploadId, fileName });
        }
    }
}
