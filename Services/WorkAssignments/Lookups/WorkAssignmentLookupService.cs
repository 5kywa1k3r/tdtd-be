using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Lookups;

public sealed class WorkAssignmentLookupService : IWorkAssignmentLookupService
{
    private readonly MongoDbContext _ctx;

    public WorkAssignmentLookupService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<Work> LoadWorkAsync(string workId, CancellationToken ct = default)
    {
        var work = await _ctx.Works
            .Find(x => x.Id == workId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (work is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_NOT_FOUND, new { workId });

        return work;
    }

    public async Task<WorkAssignment> LoadAssignmentAsync(string assignmentId, CancellationToken ct = default)
    {
        var entity = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND, new { assignmentId });

        return entity;
    }

    public async Task<WorkAssignment?> LoadParentAsync(
        string? parentAssignmentId,
        string workId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(parentAssignmentId))
            return null;

        var parent = await _ctx.WorkAssignments
            .Find(x => x.Id == parentAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (parent is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_PARENT_NOT_FOUND, new { parentAssignmentId });

        if (!string.Equals(parent.WorkId, workId, StringComparison.Ordinal))
            throw AppExceptionFactory.BadRequest(
                AppErrorCode.WORK_ASSIGNMENT_PARENT_WORK_MISMATCH,
                new { parentAssignmentId, parent.WorkId, workId });

        return parent;
    }

    public async Task EnsureParentExistsAsync(string parentAssignmentId, CancellationToken ct = default)
    {
        var parent = await _ctx.WorkAssignments
            .Find(x => x.Id == parentAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (parent is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_PARENT_NOT_FOUND, new { parentAssignmentId });
    }
}
