using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models;
using tdtd_be.Services.Common;

namespace tdtd_be.Services.WorkAssignments.Domain;

public sealed class WorkAssignmentTreeService : IWorkAssignmentTreeService
{
    private readonly MongoDbContext _ctx;
    private readonly IDocRoleService _docRole;

    public WorkAssignmentTreeService(MongoDbContext ctx, IDocRoleService docRole)
    {
        _ctx = ctx;
        _docRole = docRole;
    }

    public async Task<string> GenerateAssignmentCodeAsync(string workId, CancellationToken ct = default)
    {
        var prefix = "WA";

        var count = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && !x.IsDeleted)
            .CountDocumentsAsync(ct);

        return $"{prefix}{(count + 1):D6}";
    }

    public async Task EnsureNoDuplicateAssignmentAsync(
        string workId,
        string? parentAssignmentId,
        string dynamicExcelId,
        List<string> assigneeUserIds,
        string? excludeAssignmentId,
        CancellationToken ct = default)
    {
        var filter = Builders<WorkAssignment>.Filter.And(
            Builders<WorkAssignment>.Filter.Eq(x => x.WorkId, workId),
            Builders<WorkAssignment>.Filter.Eq(x => x.ParentAssignmentId, parentAssignmentId),
            Builders<WorkAssignment>.Filter.Eq(x => x.DynamicExcelId, dynamicExcelId),
            Builders<WorkAssignment>.Filter.Eq(x => x.IsDeleted, false)
        );

        if (!string.IsNullOrWhiteSpace(excludeAssignmentId))
        {
            filter = Builders<WorkAssignment>.Filter.And(
                filter,
                Builders<WorkAssignment>.Filter.Ne(x => x.Id, excludeAssignmentId)
            );
        }

        var exists = await _ctx.WorkAssignments
            .Find(filter)
            .Project(x => new
            {
                x.Id,
                AssigneeUserIds = x.Assignees.Select(a => a.UserId).ToList()
            })
            .ToListAsync(ct);

        var duplicated = exists.Any(x => x.AssigneeUserIds.Intersect(assigneeUserIds).Any());

        if (duplicated)
            throw new InvalidOperationException("Đã tồn tại cấu hình giao trùng người nhận cho cùng biểu mẫu trong cùng nhánh.");
    }

    public async Task RebuildDescendantPathAsync(
        WorkAssignment parent,
        string oldParentPath,
        string oldRootAssignmentId,
        CancellationToken ct = default)
    {
        var descendants = await _ctx.WorkAssignments
            .Find(x =>
                x.WorkId == parent.WorkId &&
                !x.IsDeleted &&
                x.Id != parent.Id &&
                x.Path.StartsWith(oldParentPath + "/"))
            .ToListAsync(ct);

        foreach (var child in descendants)
        {
            child.Path = parent.Path + child.Path.Substring(oldParentPath.Length);
            child.RootAssignmentId = parent.RootAssignmentId;
            child.Level = child.Path.Count(c => c == '/') - 1;

            await _ctx.WorkAssignments.ReplaceOneAsync(
                x => x.Id == child.Id && !x.IsDeleted,
                child,
                cancellationToken: ct);

            await _docRole.UpsertWorkAssignmentRolesAsync(child, ct);
        }
    }
}