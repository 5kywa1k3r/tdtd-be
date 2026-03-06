using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.Models;
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

    public WorksFilesController(
        UploadTokenService tokens,
        MongoDbContext ctx,
        MeAccessor me,
        IOptions<UploadOptions> opt)
    {
        _tokens = tokens;
        _ctx = ctx;
        _me = me;
        _opt = opt.Value;
    }
    private async Task<Work> RequireWorkAsync(string workId, CancellationToken ct)
    {
        var me = _me.RequireMe();
        WorkRoleGuard.RequireCanManageWork(me);

        var work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (work is null) throw new InvalidOperationException("Work not found.");
        return work;
    }

    // ===============================
    // GET: list files by workId
    // ===============================
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
        await RequireWorkAsync(workId, ct);

        var rows = await _ctx.Files.Find(x =>
                !x.IsDeleted &&
                x.SourceId == workId &&
                x.SourceType == SOURCE_TYPE_WORK_BASIS)
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

    // ===============================
    // POST: create upload session for this work
    // ===============================
    [HttpPost("upload-session")]
    public async Task<IActionResult> CreateSession(
        [FromRoute] string workId,
        [FromBody] CreateUploadSessionReq req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        await RequireWorkAsync(workId, ct);

        if (string.IsNullOrWhiteSpace(req.FileName))
            return BadRequest("FileName is required.");
        if (req.Size <= 0)
            return BadRequest("Size is invalid.");

        // ✅ server ép sourceType/sourceId, FE không được override
        var uploadToken = _tokens.Issue(
            userId: me.Id,
            fileName: req.FileName,
            mime: req.Mime ?? "application/octet-stream",
            size: req.Size,
            sourceType: SOURCE_TYPE_WORK_BASIS,
            sourceId: workId,
            ttlSeconds: _opt.UploadTokenTtlSeconds
        );
        var apiBase = $"{Request.Scheme}://{Request.Host}";
        var endpoint = $"{apiBase}/api/uploads";
        var chunkSize = _opt.ChunkSizeBytes;
        var maxBytes = _opt.MaxUploadBytes;

        // ✅ trả contract chuẩn cho FE
        return Ok(new
        {
            endpoint,
            uploadToken,
            chunkSize,
            maxSize = maxBytes
        });
    }

    // ===============================
    // DELETE: soft delete 1 file của work
    // ===============================
    [HttpDelete("{fileId}")]
    public async Task<IActionResult> DeleteFile(
        [FromRoute] string workId,
        [FromRoute] string fileId,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        await RequireWorkAsync(workId, ct);

        if (string.IsNullOrWhiteSpace(fileId))
            return BadRequest("fileId is required.");

        var filter = Builders<FileDoc>.Filter.And(
            Builders<FileDoc>.Filter.Eq(x => x.Id, fileId),
            Builders<FileDoc>.Filter.Eq(x => x.SourceId, workId),
            Builders<FileDoc>.Filter.Eq(x => x.SourceType, SOURCE_TYPE_WORK_BASIS),
            Builders<FileDoc>.Filter.Eq(x => x.IsDeleted, false)
        );

        var update = Builders<FileDoc>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
            .Set(x => x.UpdatedByUserId, me.Id);

        var res = await _ctx.Files.UpdateOneAsync(filter, update, cancellationToken: ct);

        if (res.MatchedCount == 0)
            return NotFound(new { ok = false, reason = "file-not-found" });

        return Ok(new { ok = true });
    }
}