using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.WorkDocuments;
using tdtd_be.Models;
using tdtd_be.Services.WorkDocuments;
using tdtd_be.Uploads;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/works/{workId}/documents")]
[Authorize]
public sealed class WorkDocumentsController : ControllerBase
{
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly UploadTokenService _tokens;
    private readonly UploadOptions _opt;
    private readonly IWorkDocumentPermissionService _permission;

    public WorkDocumentsController(
        MongoDbContext ctx,
        MeAccessor me,
        UploadTokenService tokens,
        IOptions<UploadOptions> opt,
        IWorkDocumentPermissionService permission)
    {
        _ctx = ctx;
        _me = me;
        _tokens = tokens;
        _opt = opt.Value;
        _permission = permission;
    }

    [HttpGet]
    public async Task<ActionResult<List<WorkDocumentRow>>> List(
        [FromRoute] string workId,
        [FromQuery] string? scope,
        [FromQuery] string? assignmentId,
        [FromQuery] string? keyword,
        CancellationToken ct)
    {
        var me = _me.RequireMe();

        var fb = Builders<FileDoc>.Filter;
        var newMetadataFilter = fb.Eq(x => x.WorkId, workId);
        var legacyWorkBasisFilter =
            fb.Eq(x => x.SourceType, WorkDocumentConstants.SourceTypeWorkBasis) &
            fb.Eq(x => x.SourceId, workId);

        var filter = (newMetadataFilter | legacyWorkBasisFilter) & fb.Eq(x => x.IsDeleted, false);

        var rows = await _ctx.Files
            .Find(filter)
            .SortByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        var normalizedScope = NormalizeScope(scope);
        var normalizedAssignmentId = NullIfWhiteSpace(assignmentId);
        var normalizedKeyword = NullIfWhiteSpace(keyword);

        if (!string.IsNullOrWhiteSpace(normalizedKeyword))
        {
            rows = rows
                .Where(x => x.OriginalName.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(normalizedScope) && normalizedScope != "ALL")
        {
            rows = rows
                .Where(x => ScopeMatches(WorkDocumentScopeResolver.Resolve(x), normalizedScope))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(normalizedAssignmentId))
        {
            rows = rows
                .Where(x => string.Equals(WorkDocumentScopeResolver.Resolve(x).AssignmentId, normalizedAssignmentId, StringComparison.Ordinal))
                .ToList();
        }

        var visible = new List<FileDoc>();
        foreach (var file in rows)
        {
            if (await _permission.CanReadFileAsync(file, me.Id, ct))
                visible.Add(file);
        }

        var users = await LoadUsersAsync(visible.Select(x => x.CreatedByUserId), ct);
        var result = new List<WorkDocumentRow>();

        foreach (var file in visible)
        {
            var scopeInfo = WorkDocumentScopeResolver.Resolve(file);
            result.Add(new WorkDocumentRow
            {
                Id = file.Id,
                OriginalName = file.OriginalName,
                MimeType = file.MimeType,
                Size = file.Size,
                CreatedAtUtc = file.CreatedAtUtc,
                Scope = scopeInfo.Scope,
                SourceType = file.SourceType,
                WorkId = scopeInfo.WorkId,
                AssignmentId = scopeInfo.AssignmentId,
                AssignmentCode = scopeInfo.AssignmentCode,
                AssignmentPath = scopeInfo.AssignmentPath,
                CreatedByUserId = file.CreatedByUserId,
                CreatedByName = ResolveUserName(users, file.CreatedByUserId),
                CanUpdate = await _permission.CanUpdateFileAsync(file, me.Id, ct),
                CanDelete = await _permission.CanDeleteFileAsync(file, me.Id, ct)
            });
        }

        return Ok(result);
    }

    [HttpGet("upload-options")]
    public async Task<ActionResult<WorkDocumentUploadOptions>> GetUploadOptions(
        [FromRoute] string workId,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        var options = new WorkDocumentUploadOptions
        {
            CanUploadWork = await CanUploadWorkDocumentAsync(workId, me.Id, ct)
        };

        var assignments = await _permission.GetAssignmentUploadTargetsAsync(workId, me.Id, ct);
        options.AssignmentTargets = assignments
            .Select(x => new WorkDocumentUploadTarget
            {
                AssignmentId = x.Id,
                Code = x.Code,
                Path = x.Path,
                Label = BuildAssignmentLabel(x)
            })
            .ToList();

        return Ok(options);
    }

    [HttpPost("upload-session")]
    public async Task<ActionResult<CreateWorkDocumentUploadSessionResp>> CreateWorkSession(
        [FromRoute] string workId,
        [FromBody] CreateWorkDocumentUploadSessionReq req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        ValidateUploadRequest(req);

        await _permission.EnsureCanCreateWorkDocumentAsync(workId, me.Id, ct);

        return Ok(CreateSessionResponse(
            fileName: req.FileName,
            mime: req.Mime,
            size: req.Size,
            sourceType: WorkDocumentConstants.SourceTypeWorkDocument,
            sourceId: workId,
            userId: me.Id));
    }

    [HttpPost("~/api/works/{workId}/assignments/{assignmentId}/documents/upload-session")]
    public async Task<ActionResult<CreateWorkDocumentUploadSessionResp>> CreateAssignmentSession(
        [FromRoute] string workId,
        [FromRoute] string assignmentId,
        [FromBody] CreateWorkDocumentUploadSessionReq req,
        CancellationToken ct)
    {
        var me = _me.RequireMe();
        ValidateUploadRequest(req);

        await _permission.EnsureCanCreateAssignmentDocumentAsync(workId, assignmentId, me.Id, ct);

        return Ok(CreateSessionResponse(
            fileName: req.FileName,
            mime: req.Mime,
            size: req.Size,
            sourceType: WorkDocumentConstants.SourceTypeAssignmentDocument,
            sourceId: assignmentId,
            userId: me.Id));
    }

    [HttpPatch("{fileId}")]
    public async Task<ActionResult<WorkDocumentRow>> Update(
        [FromRoute] string workId,
        [FromRoute] string fileId,
        [FromBody] UpdateWorkDocumentReq req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_FILE_ID_REQUIRED);

        req ??= new UpdateWorkDocumentReq();

        var me = _me.RequireMe();
        var file = await _ctx.Files
            .Find(x => x.Id == fileId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (file is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.UPLOAD_FILE_NOT_FOUND, new { workId, fileId });

        var currentScope = WorkDocumentScopeResolver.Resolve(file);
        if (!string.Equals(currentScope.WorkId, workId, StringComparison.Ordinal))
            throw AppExceptionFactory.NotFound(AppErrorCode.UPLOAD_FILE_NOT_FOUND, new { workId, fileId });

        await _permission.EnsureCanUpdateFileAsync(file, me.Id, ct);

        var update = Builders<FileDoc>.Update
            .Set(x => x.UpdatedAtUtc, DateTime.UtcNow)
            .Set(x => x.UpdatedByUserId, me.Id);

        var nextName = NullIfWhiteSpace(req.OriginalName);
        if (nextName is not null)
        {
            update = update.Set(x => x.OriginalName, nextName);
            file.OriginalName = nextName;
        }

        var targetProvided =
            !string.IsNullOrWhiteSpace(req.Scope) ||
            !string.IsNullOrWhiteSpace(req.AssignmentId);

        if (targetProvided)
        {
            var targetScope = NormalizeScope(req.Scope) ?? currentScope.Scope;
            var targetAssignmentId = NullIfWhiteSpace(req.AssignmentId);
            var sameScope = string.Equals(targetScope, currentScope.Scope, StringComparison.OrdinalIgnoreCase);
            var sameTarget =
                sameScope &&
                (targetScope != WorkDocumentConstants.ScopeAssignmentBranch ||
                 string.Equals(targetAssignmentId ?? currentScope.AssignmentId, currentScope.AssignmentId, StringComparison.Ordinal));

            if (!sameTarget)
            {
                if (string.Equals(targetScope, WorkDocumentConstants.ScopeWork, StringComparison.OrdinalIgnoreCase))
                {
                    await _permission.EnsureCanCreateWorkDocumentAsync(workId, me.Id, ct);

                    update = update
                        .Set(x => x.SourceType, WorkDocumentConstants.SourceTypeWorkDocument)
                        .Set(x => x.SourceId, workId)
                        .Set(x => x.WorkId, workId)
                        .Set(x => x.AssignmentId, (string?)null)
                        .Set(x => x.DocumentScope, WorkDocumentConstants.ScopeWork)
                        .Set(x => x.AssignmentCode, (string?)null)
                        .Set(x => x.AssignmentPath, (string?)null);

                    file.SourceType = WorkDocumentConstants.SourceTypeWorkDocument;
                    file.SourceId = workId;
                    file.WorkId = workId;
                    file.AssignmentId = null;
                    file.DocumentScope = WorkDocumentConstants.ScopeWork;
                    file.AssignmentCode = null;
                    file.AssignmentPath = null;
                }
                else if (string.Equals(targetScope, WorkDocumentConstants.ScopeAssignmentBranch, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(targetAssignmentId))
                        throw AppExceptionFactory.BadRequest(
                            AppErrorCode.WORK_ASSIGNMENT_ID_REQUIRED,
                            new { workId, fileId, assignmentId = targetAssignmentId });

                    var assignment = await _permission.EnsureCanCreateAssignmentDocumentAsync(workId, targetAssignmentId, me.Id, ct);

                    update = update
                        .Set(x => x.SourceType, WorkDocumentConstants.SourceTypeAssignmentDocument)
                        .Set(x => x.SourceId, assignment.Id)
                        .Set(x => x.WorkId, workId)
                        .Set(x => x.AssignmentId, assignment.Id)
                        .Set(x => x.DocumentScope, WorkDocumentConstants.ScopeAssignmentBranch)
                        .Set(x => x.AssignmentCode, assignment.Code)
                        .Set(x => x.AssignmentPath, assignment.Path);

                    file.SourceType = WorkDocumentConstants.SourceTypeAssignmentDocument;
                    file.SourceId = assignment.Id;
                    file.WorkId = workId;
                    file.AssignmentId = assignment.Id;
                    file.DocumentScope = WorkDocumentConstants.ScopeAssignmentBranch;
                    file.AssignmentCode = assignment.Code;
                    file.AssignmentPath = assignment.Path;
                }
                else
                {
                    throw AppExceptionFactory.BadRequest(
                        AppErrorCode.COMMON_VALIDATION_FAILED,
                        new { scope = req.Scope });
                }
            }
        }

        await _ctx.Files.UpdateOneAsync(
            x => x.Id == fileId && !x.IsDeleted,
            update,
            cancellationToken: ct);

        var users = await LoadUsersAsync(new[] { file.CreatedByUserId }, ct);
        var nextScope = WorkDocumentScopeResolver.Resolve(file);

        return Ok(new WorkDocumentRow
        {
            Id = file.Id,
            OriginalName = file.OriginalName,
            MimeType = file.MimeType,
            Size = file.Size,
            CreatedAtUtc = file.CreatedAtUtc,
            Scope = nextScope.Scope,
            SourceType = file.SourceType,
            WorkId = nextScope.WorkId,
            AssignmentId = nextScope.AssignmentId,
            AssignmentCode = nextScope.AssignmentCode,
            AssignmentPath = nextScope.AssignmentPath,
            CreatedByUserId = file.CreatedByUserId,
            CreatedByName = ResolveUserName(users, file.CreatedByUserId),
            CanUpdate = await _permission.CanUpdateFileAsync(file, me.Id, ct),
            CanDelete = await _permission.CanDeleteFileAsync(file, me.Id, ct)
        });
    }

    [HttpDelete("{fileId}")]
    public async Task<IActionResult> Delete(
        [FromRoute] string workId,
        [FromRoute] string fileId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_FILE_ID_REQUIRED);

        var me = _me.RequireMe();
        var file = await _ctx.Files
            .Find(x => x.Id == fileId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (file is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.UPLOAD_FILE_NOT_FOUND, new { workId, fileId });

        var scope = WorkDocumentScopeResolver.Resolve(file);
        if (!string.Equals(scope.WorkId, workId, StringComparison.Ordinal))
            throw AppExceptionFactory.NotFound(AppErrorCode.UPLOAD_FILE_NOT_FOUND, new { workId, fileId });

        await _permission.EnsureCanDeleteFileAsync(file, me.Id, ct);

        var now = DateTime.UtcNow;
        var update = Builders<FileDoc>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        await _ctx.Files.UpdateOneAsync(
            x => x.Id == fileId && !x.IsDeleted,
            update,
            cancellationToken: ct);

        return NoContent();
    }

    private CreateWorkDocumentUploadSessionResp CreateSessionResponse(
        string fileName,
        string? mime,
        long size,
        string sourceType,
        string sourceId,
        string userId)
    {
        var uploadToken = _tokens.Issue(
            userId: userId,
            fileName: fileName,
            mime: string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime,
            size: size,
            sourceType: sourceType,
            sourceId: sourceId,
            ttlSeconds: _opt.UploadTokenTtlSeconds);

        return new CreateWorkDocumentUploadSessionResp
        {
            Endpoint = UploadEndpointBuilder.BuildUploadsEndpoint(Request, _opt),
            UploadToken = uploadToken,
            ChunkSize = _opt.ChunkSizeBytes,
            MaxSize = _opt.MaxUploadBytes
        };
    }

    private void ValidateUploadRequest(CreateWorkDocumentUploadSessionReq req)
    {
        if (string.IsNullOrWhiteSpace(req.FileName))
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_FILE_NAME_REQUIRED);

        if (req.Size <= 0)
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_SIZE_INVALID, new { req.Size });

        if (req.Size > _opt.MaxUploadBytes)
            throw AppExceptionFactory.BadRequest(AppErrorCode.UPLOAD_FILE_TOO_LARGE, new { req.Size, maxBytes = _opt.MaxUploadBytes });
    }

    private async Task<bool> CanUploadWorkDocumentAsync(string workId, string userId, CancellationToken ct)
    {
        try
        {
            await _permission.EnsureCanCreateWorkDocumentAsync(workId, userId, ct);
            return true;
        }
        catch (AppException)
        {
            return false;
        }
    }

    private async Task<Dictionary<string, AppUser>> LoadUsersAsync(IEnumerable<string?> ids, CancellationToken ct)
    {
        var userIds = ids
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (userIds.Count == 0)
            return new Dictionary<string, AppUser>(StringComparer.Ordinal);

        var users = await _ctx.Users
            .Find(x => userIds.Contains(x.Id) && !x.IsDeleted)
            .ToListAsync(ct);

        return users.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    private static string? ResolveUserName(Dictionary<string, AppUser> users, string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        return users.TryGetValue(userId, out var user)
            ? (string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName)
            : userId;
    }

    private static string BuildAssignmentLabel(WorkAssignment assignment)
    {
        var code = string.IsNullOrWhiteSpace(assignment.Code) ? assignment.Id : assignment.Code;
        var name = assignment.DynamicFormTemplateName;
        return string.IsNullOrWhiteSpace(name) ? code : $"{code} - {name}";
    }

    private static string? NormalizeScope(string? value)
    {
        var trimmed = NullIfWhiteSpace(value)?.ToUpperInvariant();
        if (trimmed == "ASSIGNMENT")
            return WorkDocumentConstants.ScopeAssignmentBranch;

        return trimmed;
    }

    private static bool ScopeMatches(WorkDocumentScopeInfo scope, string targetScope)
    {
        return string.Equals(scope.Scope, targetScope, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
