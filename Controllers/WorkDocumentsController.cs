using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
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
        var normalizedScope = NormalizeScope(scope);
        var normalizedAssignmentId = NullIfWhiteSpace(assignmentId);
        var normalizedKeyword = NullIfWhiteSpace(keyword);

        var fb = Builders<FileDoc>.Filter;
        var newMetadataFilter = fb.Eq(x => x.WorkId, workId);
        var legacyWorkBasisFilter =
            fb.Eq(x => x.SourceType, WorkDocumentConstants.SourceTypeWorkBasis) &
            fb.Eq(x => x.SourceId, workId);

        var filter = (newMetadataFilter | legacyWorkBasisFilter) & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(normalizedKeyword))
            filter &= fb.Regex(x => x.OriginalName, new BsonRegularExpression(Regex.Escape(normalizedKeyword), "i"));

        if (!string.IsNullOrWhiteSpace(normalizedScope) && normalizedScope != "ALL")
            filter &= BuildScopeFilter(fb, normalizedScope);

        if (!string.IsNullOrWhiteSpace(normalizedAssignmentId))
            filter &= BuildAssignmentFilter(fb, normalizedAssignmentId);

        var rows = await _ctx.Files
            .Find(filter)
            .SortByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);

        var access = await LoadListAccessAsync(workId, me.Id, rows, ct);
        var visible = rows
            .Where(file => access.CanRead(file, WorkDocumentScopeResolver.Resolve(file)))
            .ToList();

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
                CanUpdate = access.CanDelete(file, scopeInfo),
                CanDelete = access.CanDelete(file, scopeInfo)
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

    private static FilterDefinition<FileDoc> BuildScopeFilter(
        FilterDefinitionBuilder<FileDoc> fb,
        string normalizedScope)
        => string.Equals(normalizedScope, WorkDocumentConstants.ScopeWork, StringComparison.OrdinalIgnoreCase)
            ? fb.Or(
                fb.Eq(x => x.DocumentScope, WorkDocumentConstants.ScopeWork),
                fb.Eq(x => x.SourceType, WorkDocumentConstants.SourceTypeWorkDocument),
                fb.Eq(x => x.SourceType, WorkDocumentConstants.SourceTypeWorkBasis))
            : string.Equals(normalizedScope, WorkDocumentConstants.ScopeAssignmentBranch, StringComparison.OrdinalIgnoreCase)
                ? fb.Or(
                    fb.Eq(x => x.DocumentScope, WorkDocumentConstants.ScopeAssignmentBranch),
                    fb.Eq(x => x.SourceType, WorkDocumentConstants.SourceTypeAssignmentDocument))
                : fb.Eq(x => x.Id, "__no_matching_work_document_scope__");

    private static FilterDefinition<FileDoc> BuildAssignmentFilter(
        FilterDefinitionBuilder<FileDoc> fb,
        string assignmentId)
        => BuildScopeFilter(fb, WorkDocumentConstants.ScopeAssignmentBranch) &
           fb.Or(
               fb.Eq(x => x.AssignmentId, assignmentId),
               fb.Eq(x => x.SourceId, assignmentId));

    private async Task<WorkDocumentListAccess> LoadListAccessAsync(
        string workId,
        string userId,
        IReadOnlyCollection<FileDoc> files,
        CancellationToken ct)
    {
        var workReadTask = _ctx.DocRoles
            .Find(x => x.DocType == DocType.WORK && x.DocId == workId && x.UserId == userId && !x.IsDeleted)
            .Limit(1)
            .AnyAsync(ct);

        var workOwnerTask = _ctx.Works
            .Find(x => x.Id == workId && x.CreatedByUserId == userId && !x.IsDeleted)
            .Limit(1)
            .AnyAsync(ct);

        var branchScopes = files
            .Select(WorkDocumentScopeResolver.Resolve)
            .Where(x => string.Equals(x.Scope, WorkDocumentConstants.ScopeAssignmentBranch, StringComparison.Ordinal))
            .ToList();

        var branchAssignmentIds = branchScopes
            .Select(x => NullIfWhiteSpace(x.AssignmentId))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var activeBranchAssignmentPathIds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var ownedBranchAssignments = new HashSet<string>(StringComparer.Ordinal);
        var branchPathIds = new List<string>();
        if (branchAssignmentIds.Count > 0)
        {
            var directAssignments = await _ctx.WorkAssignments
                .Find(x =>
                    branchAssignmentIds.Contains(x.Id) &&
                    x.WorkId == workId &&
                    x.IsActive &&
                    !x.IsDeleted)
                .Project(x => new WorkDocumentAssignmentPathProjection
                {
                    Id = x.Id,
                    Path = x.Path,
                    CreatedByUserId = x.CreatedByUserId
                })
                .ToListAsync(ct);

            foreach (var assignment in directAssignments)
            {
                var pathIds = ResolveAssignmentPathIds(assignment.Path, assignment.Id);
                activeBranchAssignmentPathIds[assignment.Id] = pathIds;

                if (string.Equals(assignment.CreatedByUserId, userId, StringComparison.Ordinal))
                    ownedBranchAssignments.Add(assignment.Id);
            }

            branchPathIds = activeBranchAssignmentPathIds.Values
                .SelectMany(x => x)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var projectedReadable = new HashSet<string>(StringComparer.Ordinal);
        if (branchPathIds.Count > 0)
        {
            var roleFb = Builders<AssignmentListDocRole>.Filter;
            var roleRows = await _ctx.AssignmentListDocRoles
                .Find(
                    roleFb.Eq(x => x.WorkId, workId) &
                    roleFb.Eq(x => x.UserId, userId) &
                    roleFb.In(x => x.AssignmentId, branchPathIds) &
                    roleFb.Eq(x => x.IsDeleted, false) &
                    roleFb.SizeGt(x => x.Roles, 0))
                .Project(x => x.AssignmentId)
                .ToListAsync(ct);

            projectedReadable = roleRows.ToHashSet(StringComparer.Ordinal);
        }

        var sourceReadable = new HashSet<string>(StringComparer.Ordinal);
        if (branchPathIds.Count > 0)
        {
            var assignmentFb = Builders<WorkAssignment>.Filter;
            var assignments = await _ctx.WorkAssignments
                .Find(
                    assignmentFb.Eq(x => x.WorkId, workId) &
                    assignmentFb.In(x => x.Id, branchPathIds) &
                    assignmentFb.Eq(x => x.IsActive, true) &
                    assignmentFb.Eq(x => x.IsDeleted, false))
                .ToListAsync(ct);

            sourceReadable = assignments
                .Where(x => IsBranchMember(x, userId))
                .Select(x => x.Id)
                .ToHashSet(StringComparer.Ordinal);
        }

        var canReadWork = await workReadTask;
        var isWorkOwner = await workOwnerTask;

        return new WorkDocumentListAccess(
            userId,
            canReadWork,
            isWorkOwner,
            activeBranchAssignmentPathIds,
            projectedReadable,
            sourceReadable,
            ownedBranchAssignments);
    }

    private static IReadOnlyList<string> ResolveAssignmentPathIds(string? assignmentPath, string? assignmentId)
    {
        var ids = (assignmentPath ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var normalizedAssignmentId = NullIfWhiteSpace(assignmentId);
        if (!string.IsNullOrWhiteSpace(normalizedAssignmentId) && !ids.Contains(normalizedAssignmentId, StringComparer.Ordinal))
            ids.Add(normalizedAssignmentId);

        return ids;
    }

    private static bool IsBranchMember(WorkAssignment assignment, string userId)
    {
        if (string.Equals(assignment.CreatedByUserId, userId, StringComparison.Ordinal))
            return true;

        if ((assignment.Assignees ?? new List<UserRef>())
            .Any(x => string.Equals(x.UserId, userId, StringComparison.Ordinal)))
            return true;

        return (assignment.LeaderWatcherUserIds ?? new List<string>())
            .Any(x => string.Equals(x, userId, StringComparison.Ordinal)) ||
            (assignment.LeaderWatchers ?? new List<UserRef>())
            .Any(x => string.Equals(x.UserId, userId, StringComparison.Ordinal));
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record WorkDocumentListAccess(
        string UserId,
        bool CanReadWorkDocuments,
        bool IsWorkOwner,
        Dictionary<string, IReadOnlyList<string>> ActiveBranchAssignmentPathIds,
        HashSet<string> ProjectedReadableAssignmentIds,
        HashSet<string> SourceReadableAssignmentIds,
        HashSet<string> OwnedBranchAssignmentIds)
    {
        public bool CanRead(FileDoc file, WorkDocumentScopeInfo scope)
        {
            if (string.Equals(scope.Scope, WorkDocumentConstants.ScopeWork, StringComparison.Ordinal))
                return CanReadWorkDocuments;

            if (string.Equals(scope.Scope, WorkDocumentConstants.ScopeAssignmentBranch, StringComparison.Ordinal))
            {
                var assignmentId = NullIfWhiteSpace(scope.AssignmentId);
                if (assignmentId is null || !ActiveBranchAssignmentPathIds.TryGetValue(assignmentId, out var pathIds))
                    return false;

                return pathIds
                    .Any(id => ProjectedReadableAssignmentIds.Contains(id) || SourceReadableAssignmentIds.Contains(id));
            }

            return string.Equals(file.CreatedByUserId, UserId, StringComparison.Ordinal);
        }

        public bool CanDelete(FileDoc file, WorkDocumentScopeInfo scope)
        {
            if (string.Equals(scope.Scope, WorkDocumentConstants.ScopeWork, StringComparison.Ordinal))
                return IsWorkOwner;

            if (string.Equals(scope.Scope, WorkDocumentConstants.ScopeAssignmentBranch, StringComparison.Ordinal))
            {
                var assignmentId = NullIfWhiteSpace(scope.AssignmentId);
                if (assignmentId is null || !ActiveBranchAssignmentPathIds.ContainsKey(assignmentId))
                    return false;

                return IsWorkOwner || OwnedBranchAssignmentIds.Contains(assignmentId);
            }

            return string.Equals(file.CreatedByUserId, UserId, StringComparison.Ordinal);
        }
    }

    private sealed class WorkDocumentAssignmentPathProjection
    {
        public string Id { get; set; } = default!;
        public string? Path { get; set; }
        public string? CreatedByUserId { get; set; }
    }
}
