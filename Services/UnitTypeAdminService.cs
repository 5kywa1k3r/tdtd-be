using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.UnitTypes;
using tdtd_be.Models;

namespace tdtd_be.Services;

public interface IUnitTypeAdminService
{
    Task<UnitTypeResponse> CreateAsync(CreateUnitTypeRequest req, CancellationToken ct);
    Task<UnitTypeResponse> UpdateAsync(string id, UpdateUnitTypeRequest req, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<UnitTypeResponse>> ListAsync(bool? isDeleted, CancellationToken ct);
}

public sealed class UnitTypeAdminService : IUnitTypeAdminService
{
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public UnitTypeAdminService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx; _me = me;
    }

    private static UnitTypeResponse ToResp(UnitType t) => new(
        t.Id, t.Code, t.Name, t.IsDeleted, t.Version, t.CreatedAtUtc, t.UpdatedAtUtc
    );

    public async Task<UnitTypeResponse> CreateAsync(CreateUnitTypeRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdmin(me);

        var code = req.Code.Trim();
        var exists = await _ctx.UnitTypes
            .Find(x => x.Code == code && !x.IsDeleted)
            .AnyAsync(ct);
        if (exists) throw new InvalidOperationException("UnitType code already exists.");

        var now = DateTime.UtcNow;
        var doc = new UnitType
        {
            Code = code,
            Name = req.Name.Trim(),
            IsDeleted = false,
            Version = 1,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _ctx.UnitTypes.InsertOneAsync(doc, cancellationToken: ct);
        return ToResp(doc);
    }
    public async Task<UnitTypeResponse> UpdateAsync(string id, UpdateUnitTypeRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdmin(me);

        var now = DateTime.UtcNow;

        var update = Builders<UnitType>.Update
            .Set(x => x.Name, req.Name.Trim())
            .Set(x => x.Note, string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim())
            .Inc(x => x.Version, 1)
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now);

        var after = await _ctx.UnitTypes.FindOneAndUpdateAsync(
            x => x.Id == id && !x.IsDeleted, // Policy A: record đã xóa thì không cho update
            update,
            new FindOneAndUpdateOptions<UnitType> { ReturnDocument = ReturnDocument.After },
            ct);

        if (after is null) throw new InvalidOperationException("UnitType not found.");
        return ToResp(after);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdmin(me);

        var update = Builders<UnitType>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, DateTime.UtcNow)
            .Set(x => x.DeletedByUserId, me.Id);

        var rs = await _ctx.UnitTypes.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (rs.MatchedCount == 0)
            throw new InvalidOperationException("UnitType not found.");
    }

    public async Task<IReadOnlyList<UnitTypeResponse>> ListAsync(bool? isDeleted, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdmin(me);

        var filter = Builders<UnitType>.Filter.Empty;
        if (isDeleted.HasValue)
            filter &= Builders<UnitType>.Filter.Eq(x => x.IsDeleted, isDeleted.Value);

        var list = await _ctx.UnitTypes.Find(filter).SortBy(x => x.Code).ToListAsync(ct);
        return list.Select(ToResp).ToList();
    }
}