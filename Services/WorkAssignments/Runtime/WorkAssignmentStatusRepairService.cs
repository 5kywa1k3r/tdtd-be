using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using tdtd_be.Data;
using tdtd_be.Services.Common;
using tdtd_be.Services.WorkAssignments.Progress;

namespace tdtd_be.Services.WorkAssignments.Runtime;

public sealed class WorkAssignmentStatusRepairService : IWorkAssignmentStatusRepairService
{
    private readonly MongoDbContext _ctx;
    private readonly IWorkAssignmentProgressService _progress;
    private readonly IWorkAssignmentStatusSyncService _sync;
    private readonly IDocRoleReadModelProjectionService _docRoleReadModelProjection;
    private readonly ILogger<WorkAssignmentStatusRepairService> _log;

    public WorkAssignmentStatusRepairService(
        MongoDbContext ctx,
        IWorkAssignmentProgressService progress,
        IWorkAssignmentStatusSyncService sync,
        IDocRoleReadModelProjectionService docRoleReadModelProjection,
        ILogger<WorkAssignmentStatusRepairService> log)
    {
        _ctx = ctx;
        _progress = progress;
        _sync = sync;
        _docRoleReadModelProjection = docRoleReadModelProjection;
        _log = log;
    }

    public async Task RebuildWorkTreeAsync(string workId, CancellationToken ct = default)
    {
        var all = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && !x.IsDeleted)
            .SortByDescending(x => x.Level)
            .ThenByDescending(x => x.Path)
            .ToListAsync(ct);

        _log.LogInformation(
            "WorkAssignment status repair started. workId={workId} assignmentCount={assignmentCount}",
            workId,
            all.Count);

        foreach (var leafOrNode in all.OrderByDescending(x => x.Level))
        {
            await _progress.RecomputeSingleAsync(leafOrNode.Id, ct);
            await _docRoleReadModelProjection.RebuildAssignmentAsync(leafOrNode.Id, "system", ct);
        }

        var roots = all
            .Where(x => x.ParentAssignmentId == null && x.IsActive)
            .Select(x => x.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var rootId in roots)
        {
            await _sync.SyncFromAssignmentAsync(rootId, ct);
        }

        _log.LogInformation(
            "WorkAssignment status repair completed. workId={workId} assignmentCount={assignmentCount} rootCount={rootCount}",
            workId,
            all.Count,
            roots.Count);
    }
}
