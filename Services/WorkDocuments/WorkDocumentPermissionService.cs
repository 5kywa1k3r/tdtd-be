using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.Common;
using tdtd_be.Services.Works;

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

        if (!string.Equals(assignment.CreatedByUserId, userId, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_FORBIDDEN, new { workId, assignmentId });

        return assignment;
    }

    public async Task<bool> CanReadFileAsync(FileDoc file, string userId, CancellationToken ct)
    {
        var scope = WorkDocumentScopeResolver.Resolve(file);

        if (string.Equals(scope.Scope, WorkDocumentConstants.ScopeWork, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(scope.WorkId))
                return false;

            return await _docRole.HasAnyRoleAsync(DocType.WORK, scope.WorkId, userId, ct);
        }

        if (string.Equals(scope.Scope, WorkDocumentConstants.ScopeAssignmentBranch, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(scope.AssignmentId))
                return false;

            var assignment = await LoadActiveAssignmentAsync(scope.AssignmentId, scope.WorkId, ct);
            if (assignment is null)
                return false;

            var ancestorIds = WorkDocumentScopeResolver.ParseAssignmentPath(assignment.Path, assignment.Id);
            var roleFb = Builders<AssignmentListDocRole>.Filter;
            var roleFilter =
                roleFb.Eq(x => x.WorkId, assignment.WorkId) &
                roleFb.Eq(x => x.UserId, userId) &
                roleFb.In(x => x.AssignmentId, ancestorIds) &
                roleFb.Eq(x => x.IsDeleted, false) &
                roleFb.SizeGt(x => x.Roles, 0);

            var hasProjectedRole = await _ctx.AssignmentListDocRoles
                .Find(roleFilter)
                .AnyAsync(ct);

            if (hasProjectedRole)
                return true;

            var assignmentFb = Builders<WorkAssignment>.Filter;
            var assignmentFilter =
                assignmentFb.Eq(x => x.WorkId, assignment.WorkId) &
                assignmentFb.In(x => x.Id, ancestorIds) &
                assignmentFb.Eq(x => x.IsActive, true) &
                assignmentFb.Eq(x => x.IsDeleted, false);

            var ancestorNodes = await _ctx.WorkAssignments
                .Find(assignmentFilter)
                .ToListAsync(ct);

            return ancestorNodes.Any(x => IsBranchMember(x, userId));
        }

        return string.Equals(file.CreatedByUserId, userId, StringComparison.Ordinal);
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
        var scope = WorkDocumentScopeResolver.Resolve(file);

        if (string.Equals(scope.Scope, WorkDocumentConstants.ScopeWork, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(scope.WorkId))
                return false;

            return await IsWorkOwnerAsync(scope.WorkId, userId, ct);
        }

        if (string.Equals(scope.Scope, WorkDocumentConstants.ScopeAssignmentBranch, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(scope.AssignmentId))
                return false;

            var assignment = await LoadActiveAssignmentAsync(scope.AssignmentId, scope.WorkId, ct);
            if (assignment is null)
                return false;

            if (string.Equals(assignment.CreatedByUserId, userId, StringComparison.Ordinal))
                return true;

            return await IsWorkOwnerAsync(assignment.WorkId, userId, ct);
        }

        return string.Equals(file.CreatedByUserId, userId, StringComparison.Ordinal);
    }

    public async Task EnsureCanDeleteFileAsync(FileDoc file, string userId, CancellationToken ct)
    {
        if (!await CanDeleteFileAsync(file, userId, ct))
            throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_FORBIDDEN, new { fileId = file.Id });
    }

    public async Task<List<WorkAssignment>> GetAssignmentUploadTargetsAsync(string workId, string userId, CancellationToken ct)
    {
        await EnsureWorkExistsAsync(workId, ct);

        return await _ctx.WorkAssignments
            .Find(x =>
                x.WorkId == workId &&
                x.CreatedByUserId == userId &&
                x.IsActive &&
                !x.IsDeleted)
            .SortBy(x => x.Path)
            .ToListAsync(ct);
    }

    private async Task<Work?> EnsureWorkExistsAsync(string workId, CancellationToken ct)
    {
        var work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (work is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_NOT_FOUND, new { workId });

        return work;
    }

    private async Task<bool> IsWorkOwnerAsync(string workId, string userId, CancellationToken ct)
    {
        return await _ctx.Works
            .Find(x => x.Id == workId && x.CreatedByUserId == userId && !x.IsDeleted)
            .AnyAsync(ct);
    }

    private async Task<WorkAssignment?> LoadActiveAssignmentAsync(string assignmentId, string? workId, CancellationToken ct)
    {
        var fb = Builders<WorkAssignment>.Filter;
        var filter = fb.Eq(x => x.Id, assignmentId) & fb.Eq(x => x.IsActive, true) & fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(workId))
            filter &= fb.Eq(x => x.WorkId, workId);

        return await _ctx.WorkAssignments.Find(filter).FirstOrDefaultAsync(ct);
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
}
