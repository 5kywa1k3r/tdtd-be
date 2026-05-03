using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Services.WorkAssignments.Domain;

public sealed class WorkAssignmentTreeService : IWorkAssignmentTreeService
{
    private readonly MongoDbContext _ctx;

    public WorkAssignmentTreeService(MongoDbContext ctx)
    {
        _ctx = ctx;
    }

    public async Task<string> GenerateAssignmentCodeAsync(string workId, CancellationToken ct = default)
    {
        const string prefix = "WA";

        var count = await _ctx.WorkAssignments
            .Find(x => x.WorkId == workId && !x.IsDeleted)
            .CountDocumentsAsync(ct);

        return $"{prefix}{(count + 1):D6}";
    }
}