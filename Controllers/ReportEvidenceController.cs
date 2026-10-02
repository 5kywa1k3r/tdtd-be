using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports;

namespace tdtd_be.Controllers;

[ApiController, Authorize, Route("api/reports/{reportId}/evidence/{fieldId}")]
public sealed class ReportEvidenceController(MongoDbContext db, IWorkAssignmentReportService reports,
    MeAccessor me, IMinioClient minio, IConfiguration cfg) : ControllerBase
{
    private const long MaxBytes = 5 * 1024 * 1024;

    [HttpPost, RequestSizeLimit(MaxBytes + 65536)]
    public async Task<IActionResult> Upload(string reportId, string fieldId, IFormFile file, CancellationToken ct)
    {
        var actor = me.RequireMe();
        await reports.AuthorizeEvidenceAsync(reportId, fieldId, actor.Id, true, ct);
        if (file.Length <= 0 || file.Length > MaxBytes)
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_FILE_TOO_LARGE, new { maxBytes = MaxBytes });
        var id = ObjectId.GenerateNewId().ToString();
        var bucket = cfg["Minio:Bucket"] ?? "tdtd-attachments";
        var key = $"report-evidence/{reportId}/{id}";
        await using var stream = file.OpenReadStream();
        await minio.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(key)
            .WithStreamData(stream).WithObjectSize(file.Length).WithContentType("application/octet-stream"), ct);
        try
        {
            // Recheck after upload, including submission/locks that occurred while sending bytes.
            await reports.AuthorizeEvidenceAsync(reportId, fieldId, actor.Id, true, ct);
            var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
            var doc = new FileDoc { Id = id, Bucket = bucket, ObjectKey = key, UploadId = id,
                OriginalName = name, MimeType = "application/octet-stream", Size = file.Length,
                SourceType = "REPORT_EVIDENCE", SourceId = reportId + ":" + fieldId,
                CreatedByUserId = actor.Id, UpdatedByUserId = actor.Id,
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
            await db.Files.InsertOneAsync(doc, cancellationToken: ct);
            return Ok(new { id, name, size = doc.Size });
        }
        catch
        {
            await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(key), CancellationToken.None);
            throw;
        }
    }

    private async Task<FileDoc> Read(string reportId, string fieldId, string fileId, CancellationToken ct)
    {
        var actor = me.RequireMe();
        var report = await reports.AuthorizeEvidenceAsync(reportId, fieldId, actor.Id, false, ct);
        if (!ObjectId.TryParse(fileId, out _))
            throw AppExceptionFactory.NotFound(AppErrorCode.UPLOAD_FILE_NOT_FOUND);
        var doc = await db.Files.Find(f => f.Id == fileId && !f.IsDeleted && f.SourceType == "REPORT_EVIDENCE"
            && f.SourceId == reportId + ":" + fieldId).FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.UPLOAD_FILE_NOT_FOUND);
        using var parsed = JsonDocument.Parse(report.FieldValuesJson ?? "{}");
        var values = parsed.RootElement.TryGetProperty("values", out var nested) ? nested : parsed.RootElement;
        var linked = values.TryGetProperty(fieldId, out var ids) && ids.ValueKind == JsonValueKind.Array &&
            ids.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == fileId);
        if (!linked && !(report.Status == tdtd_be.Models.Enums.WorkAssignmentReportStatus.Draft &&
            report.AssigneeUserId == actor.Id && doc.CreatedByUserId == actor.Id))
            throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_FORBIDDEN);
        return doc;
    }

    [HttpGet("{fileId}")]
    public async Task<IActionResult> Metadata(string reportId, string fieldId, string fileId, CancellationToken ct)
    {
        var doc = await Read(reportId, fieldId, fileId, ct);
        return Ok(new { id = doc.Id, name = doc.OriginalName, size = doc.Size });
    }

    [HttpGet("{fileId}/download")]
    public async Task<IActionResult> Download(string reportId, string fieldId, string fileId, CancellationToken ct)
    {
        var doc = await Read(reportId, fieldId, fileId, ct);
        using var bytes = new MemoryStream();
        await minio.GetObjectAsync(new GetObjectArgs().WithBucket(doc.Bucket).WithObject(doc.ObjectKey)
            .WithCallbackStream(stream => stream.CopyTo(bytes)), ct);
        return File(bytes.ToArray(), "application/octet-stream", doc.OriginalName);
    }
}
