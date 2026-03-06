using MongoDB.Driver;
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
            throw new InvalidOperationException("Công việc không tồn tại.");

        return work;
    }

    public async Task<WorkAssignment> LoadAssignmentAsync(string assignmentId, CancellationToken ct = default)
    {
        var entity = await _ctx.WorkAssignments
            .Find(x => x.Id == assignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
            throw new InvalidOperationException("Bản ghi giao nhiệm vụ không tồn tại.");

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
            throw new InvalidOperationException("Assignment cha không tồn tại.");

        if (!string.Equals(parent.WorkId, workId, StringComparison.Ordinal))
            throw new InvalidOperationException("Assignment cha không thuộc cùng công việc.");

        return parent;
    }

    public async Task EnsureParentExistsAsync(string parentAssignmentId, CancellationToken ct = default)
    {
        var parent = await _ctx.WorkAssignments
            .Find(x => x.Id == parentAssignmentId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (parent is null)
            throw new InvalidOperationException("Assignment cha không tồn tại.");
    }
}