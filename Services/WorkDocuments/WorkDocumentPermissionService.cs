using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.Common;
using tdtd_be.Services.Works;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkDocuments;

public sealed class WorkDocumentPermissionService : IWorkDocumentPermissionService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkPermissionService _workPermission;
    private readonly IDocRoleService _docRole;

    public WorkDocumentPermissionService(
        MongoDbContext ctx,
        IWorkPermissionService workPermission,
        IDocRoleService docRole)
    {
        _ctx = ctx;
        _workPermission = workPermission;
        _docRole = docRole;
    }

    public async Task EnsureCanCreateWorkDocumentAsync(string workId, string userId, CancellationToken ct)
    {
        await EnsureWorkExistsAsync(workId, ct);
        await _workPermission.EnsureCanUpdateRootAsync(workId, userId, ct);
    }

    public async Task<WorkAssignment> EnsureCanCreateAssignmentDocumentAsync(string workId, string assignmentId, string userId, CancellationToken ct)
    {
        await EnsureWorkExistsAsync(workId, ct);

        var assignment = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && x.WorkId == workId && x.IsActive && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (assignment is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND, new { workId, assignmentId });

        if (!(await ReadAccessAsync(workId, userId, ct)).UploadAssignments.Any(a => a.Id == assignmentId))
            throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_FORBIDDEN, new { workId, assignmentId });

        return assignment;
    }

    public async Task<WorkDocumentAccessSnapshot> ReadAccessAsync(string workId, string userId, CancellationToken ct)
    {
        await EnsureWorkExistsAsync(workId, ct);
        var roles = await _ctx.DocRoles.Find(r => r.UserId == userId && !r.IsDeleted &&
            (r.DocType == DocType.WORK && r.DocId == workId || r.DocType == DocType.WORK_ASSIGNMENT)).ToListAsync(ct);
        var assignments = await _ctx.WorkAssignments.Find(a => a.WorkId == workId && a.IsActive && !a.IsDeleted).ToListAsync(ct);
        return new(workId, userId, roles.Any(r => r.DocType == DocType.WORK && r.DocId == workId),
            roles.Any(r => r.DocType == DocType.WORK && r.DocId == workId && r.Role == DocRoleType.OWNER), assignments,
            roles.Where(r => r.DocType == DocType.WORK_ASSIGNMENT).Select(r => r.DocId));
    }

    public async Task<bool> CanReadFileAsync(FileDoc file, string userId, CancellationToken ct)
    {
        if (file.SourceType == "REPORT_EVIDENCE" || file.IsDeleted) return false;
        var scope = WorkDocumentScopeResolver.Resolve(file);
        if (scope.Scope is not (WorkDocumentConstants.ScopeWork or WorkDocumentConstants.ScopeAssignmentBranch)) return file.CreatedByUserId == userId;
        if (string.IsNullOrWhiteSpace(scope.WorkId)) return false;
        var access = await ReadAccessAsync(scope.WorkId, userId, ct);
        return access.CanRead(file, scope);
    }

    public async Task EnsureCanReadFileAsync(FileDoc file, string userId, CancellationToken ct)
    {
        if (!await CanReadFileAsync(file, userId, ct))
            throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_FORBIDDEN, new { fileId = file.Id });
    }

    public Task<bool> CanUpdateFileAsync(FileDoc file, string userId, CancellationToken ct)
        => CanDeleteFileAsync(file, userId, ct);

    public async Task EnsureCanUpdateFileAsync(FileDoc file, string userId, CancellationToken ct)
    {
        if (!await CanUpdateFileAsync(file, userId, ct))
            throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_FORBIDDEN, new { fileId = file.Id });
    }

    public async Task<bool> CanDeleteFileAsync(FileDoc file, string userId, CancellationToken ct)
    {
        if (file.SourceType == "REPORT_EVIDENCE" || file.IsDeleted) return false;
        var scope = WorkDocumentScopeResolver.Resolve(file);
        if (scope.Scope is not (WorkDocumentConstants.ScopeWork or WorkDocumentConstants.ScopeAssignmentBranch)) return file.CreatedByUserId == userId;
        if (string.IsNullOrWhiteSpace(scope.WorkId)) return false;
        return (await ReadAccessAsync(scope.WorkId, userId, ct)).CanDelete(file, scope);
    }

    public async Task EnsureCanDeleteFileAsync(FileDoc file, string userId, CancellationToken ct)
    {
        if (!await CanDeleteFileAsync(file, userId, ct))
            throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_FORBIDDEN, new { fileId = file.Id });
    }

    public async Task<List<WorkAssignment>> GetAssignmentUploadTargetsAsync(string workId, string userId, CancellationToken ct)
        => (await ReadAccessAsync(workId, userId, ct)).UploadAssignments.ToList();

    private async Task<Work?> EnsureWorkExistsAsync(string workId, CancellationToken ct)
    {
        var work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (work is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_NOT_FOUND, new { workId });

        return work;
    }

}
