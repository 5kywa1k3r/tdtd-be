using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkDocuments;
using tdtd_be.Services.Works;
using tdtd_be.Uploads;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/works/{workId}/files")]
[Authorize]
public sealed class WorksFilesController : ControllerBase
{
    private const string SOURCE_TYPE_WORK_BASIS = "WORK_BASIS";

    private readonly UploadTokenService _tokens;
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly UploadOptions _opt;
    private readonly IWorkPermissionService _permission;

    public WorksFilesController(
        UploadTokenService tokens,
        MongoDbContext ctx,
        MeAccessor me,
        IOptions<UploadOptions> opt,
        IWorkPermissionService permission)
    {
        _tokens = tokens;
        _ctx = ctx;
        _me = me;
        _opt = opt.Value;
        _permission = permission;
    }

    private async Task<Work> RequireWorkExistsAsync(string workId, CancellationToken ct)
    {
        var work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (work is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_NOT_FOUND, new { workId });

        return work;
    }

    public sealed record WorkFileRow(
        string Id,
        string OriginalName,
        string MimeType,
        long Size,
        DateTime CreatedAtUtc
    );

    [HttpGet]
    public async Task<ActionResult<List<WorkFileRow>>> List(
        [FromRoute] string workId,
        CancellationToken ct)
    {
        var me = _me.RequireMe();

        await RequireWorkExistsAsync(workId, ct);
        await _permission.EnsureCanReadAsync(workId, me.Id, ct);

        var fb = Builders<FileDoc>.Filter;
        var workDocumentFilter = fb.Or(
            fb.And(
                fb.Eq(x => x.SourceId, workId),
                fb.Eq(x => x.SourceType, SOURCE_TYPE_WORK_BASIS)
            ),
            fb.And(
                fb.Eq(x => x.WorkId, workId),
                fb.Eq(x => x.DocumentScope, WorkDocumentConstants.ScopeWork)
            ),
            fb.And(
                fb.Eq(x => x.SourceId, workId),
                fb.Eq(x => x.SourceType, WorkDocumentConstants.SourceTypeWorkDocument)
            )
        );

        var rows = await _ctx.Files.Find(fb.And(
                workDocumentFilter,
                fb.Eq(x => x.IsDeleted, false)))
            .SortByDescending(x => x.CreatedAtUtc)
            .Project(x => new WorkFileRow(
                x.Id,
                x.OriginalName,
                x.MimeType,
                x.Size,
                x.CreatedAtUtc
            ))
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpPost("upload-session")]
    public async Task<IActionResult> CreateSession(
        [FromRoute] string workId,
        [FromBody] CreateUploadSessionReq req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();

        await RequireWorkExistsAsync(workId, ct);
        await _permission.EnsureCanUpdateRootAsync(workId, me.Id, ct);

        if (string.IsNullOrWhiteSpace(req.FileName))
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_FILE_NAME_REQUIRED);

        if (req.Size <= 0)
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_SIZE_INVALID, new { req.Size });

        var uploadToken = _tokens.Issue(
            userId: me.Id,
            fileName: req.FileName,
            mime: req.Mime ?? "application/octet-stream",
            size: req.Size,
            sourceType: SOURCE_TYPE_WORK_BASIS,
            sourceId: workId,
            ttlSeconds: _opt.UploadTokenTtlSeconds
        );

        var endpoint = UploadEndpointBuilder.BuildUploadsEndpoint(Request, _opt);

        return Ok(new
        {
            endpoint,
            uploadToken,
            chunkSize = _opt.ChunkSizeBytes,
            maxSize = _opt.MaxUploadBytes
        });
    }

    [HttpDelete("{fileId}")]
    public async Task<IActionResult> DeleteFile(
        [FromRoute] string workId,
        [FromRoute] string fileId,
        CancellationToken ct)
    {
        var me = _me.RequireMe();

        await RequireWorkExistsAsync(workId, ct);
        await _permission.EnsureCanUpdateRootAsync(workId, me.Id, ct);

        if (string.IsNullOrWhiteSpace(fileId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_FILE_ID_REQUIRED);

        var fb = Builders<FileDoc>.Filter;
        var filter = fb.And(
            fb.Eq(x => x.Id, fileId),
            fb.Or(
                fb.And(
                    fb.Eq(x => x.SourceId, workId),
                    fb.Eq(x => x.SourceType, SOURCE_TYPE_WORK_BASIS)
                ),
                fb.And(
                    fb.Eq(x => x.WorkId, workId),
                    fb.Eq(x => x.DocumentScope, WorkDocumentConstants.ScopeWork)
                ),
                fb.And(
                    fb.Eq(x => x.SourceId, workId),
                    fb.Eq(x => x.SourceType, WorkDocumentConstants.SourceTypeWorkDocument)
                )
            ),
            fb.Eq(x => x.IsDeleted, false)
        );

        var update = Builders<FileDoc>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.Files.UpdateOneAsync(filter, update, cancellationToken: ct);

        if (res.MatchedCount == 0)
            throw AppExceptionFactory.NotFound(AppErrorCode.UPLOAD_FILE_NOT_FOUND, new { workId, fileId });

        return Ok(new { ok = true });
    }
}
