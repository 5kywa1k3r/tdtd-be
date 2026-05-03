using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Services.WorkAssignments.Lookups;

public sealed class WorkAssignmentDataGuardService : IWorkAssignmentDataGuardService
{
    private readonly MongoDbContext _ctx;

    public WorkAssignmentDataGuardService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<bool> HasAssignmentDataAsync(
        string workAssignmentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workAssignmentId))
            return false;

        return await _ctx.WorkAssignmentReports
            .Find(x =>
                x.WorkAssignmentId == workAssignmentId &&
                !x.IsDeleted)
            .Limit(1)
            .AnyAsync(ct);
    }

    public async Task<HashSet<string>> GetAssignmentIdsHavingDataAsync(
        IEnumerable<string> workAssignmentIds,
        CancellationToken ct = default)
    {
        var ids = (workAssignmentIds ?? Enumerable.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var result = await _ctx.WorkAssignmentReports
            .Find(x =>
                ids.Contains(x.WorkAssignmentId) &&
                !x.IsDeleted)
            .Project(x => x.WorkAssignmentId)
            .ToListAsync(ct);

        return result
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
    }
}