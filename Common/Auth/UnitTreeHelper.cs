using MongoDB.Driver;
using tdtd_be.Data;

namespace tdtd_be.Common.Auth;

public sealed class UnitTreeHelper
{
    private readonly MongoDbContext _ctx;
    public UnitTreeHelper(MongoDbContext ctx) => _ctx = ctx;

    public async Task<string?> GetParentUnitIdAsync(string unitId, CancellationToken ct = default)
        => await _ctx.Units.Find(x => x.Id == unitId).Project(x => x.ParentUnitId).FirstOrDefaultAsync(ct);
}