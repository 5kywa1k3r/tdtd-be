using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Positions;
using tdtd_be.Models;

namespace tdtd_be.Services;

public interface IPositionAdminService
{
    Task<PositionResponse> CreateAsync(CreatePositionRequest req, CancellationToken ct);
    Task<PositionResponse> UpdateAsync(string id, UpdatePositionRequest req, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<PositionResponse>> ListAsync(bool? isDeleted, string? unitTypeCode, CancellationToken ct);
    Task ValidatePositionForUnitTypeAsync(string? positionCode, string? unitTypeCode, CancellationToken ct);
}

public sealed class PositionAdminService : IPositionAdminService
{
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public PositionAdminService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
    }

    public async Task<PositionResponse> CreateAsync(CreatePositionRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var code = NormalizeCode(req.Code);
        var unitTypeCodes = NormalizeCodes(req.UnitTypeCodes);
        await EnsureUnitTypesExistAsync(unitTypeCodes, ct);

        var exists = await _ctx.Positions
            .Find(x => x.Code == code && !x.IsDeleted)
            .AnyAsync(ct);

        if (exists)
            throw new InvalidOperationException("Position code already exists.");

        var now = DateTime.UtcNow;
        var doc = new Position
        {
            Code = code,
            Name = req.Name.Trim(),
            Order = req.Order,
            Rank = req.Rank,
            UnitTypeCodes = unitTypeCodes,
            IsDeleted = false,
            Version = 1,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _ctx.Positions.InsertOneAsync(doc, cancellationToken: ct);
        return ToResp(doc);
    }

    public async Task<PositionResponse> UpdateAsync(string id, UpdatePositionRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var unitTypeCodes = NormalizeCodes(req.UnitTypeCodes);
        await EnsureUnitTypesExistAsync(unitTypeCodes, ct);

        var now = DateTime.UtcNow;
        var update = Builders<Position>.Update
            .Set(x => x.Name, req.Name.Trim())
            .Set(x => x.Order, req.Order)
            .Set(x => x.Rank, req.Rank)
            .Set(x => x.UnitTypeCodes, unitTypeCodes)
            .Set(x => x.Note, string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim())
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Inc(x => x.Version, 1);

        var after = await _ctx.Positions.FindOneAndUpdateAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            new FindOneAndUpdateOptions<Position> { ReturnDocument = ReturnDocument.After },
            ct);

        if (after is null)
            throw new InvalidOperationException("Position not found.");

        return ToResp(after);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var now = DateTime.UtcNow;
        var update = Builders<Position>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        var rs = await _ctx.Positions.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (rs.MatchedCount == 0)
            throw new InvalidOperationException("Position not found.");
    }

    public async Task<IReadOnlyList<PositionResponse>> ListAsync(bool? isDeleted, string? unitTypeCode, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var filter = Builders<Position>.Filter.Empty;
        filter &= Builders<Position>.Filter.Eq(x => x.IsDeleted, isDeleted ?? false);

        var typeCode = NormalizeOptionalCode(unitTypeCode);
        if (!string.IsNullOrWhiteSpace(typeCode))
            filter &= Builders<Position>.Filter.AnyEq(x => x.UnitTypeCodes, typeCode);

        var list = await _ctx.Positions.Find(filter)
            .SortBy(x => x.Order)
            .ThenBy(x => x.Code)
            .ToListAsync(ct);

        return list.Select(ToResp).ToList();
    }

    public async Task ValidatePositionForUnitTypeAsync(string? positionCode, string? unitTypeCode, CancellationToken ct)
    {
        var pc = NormalizeOptionalCode(positionCode);
        if (string.IsNullOrWhiteSpace(pc))
            throw new InvalidOperationException("PositionCode is required.");

        var utc = NormalizeOptionalCode(unitTypeCode);
        if (string.IsNullOrWhiteSpace(utc))
            throw new InvalidOperationException("Target unit has no primaryUnitTypeCode.");

        var exists = await _ctx.Positions
            .Find(x => x.Code == pc && !x.IsDeleted && x.UnitTypeCodes.Contains(utc))
            .AnyAsync(ct);

        if (!exists)
            throw new InvalidOperationException("PositionCode is invalid for target unit type.");
    }

    private async Task EnsureUnitTypesExistAsync(IReadOnlyList<string> codes, CancellationToken ct)
    {
        if (codes.Count == 0)
            throw new InvalidOperationException("At least one unitTypeCode is required.");

        var found = await _ctx.UnitTypes
            .Find(x => codes.Contains(x.Code) && !x.IsDeleted)
            .Project(x => x.Code)
            .ToListAsync(ct);

        var missing = codes.Except(found, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"UnitType not found: {string.Join(", ", missing)}");
    }

    private static PositionResponse ToResp(Position p) => new(
        p.Id,
        p.Code,
        p.Name,
        p.Order,
        p.Rank,
        p.UnitTypeCodes ?? new(),
        p.IsDeleted,
        p.Version,
        p.CreatedAtUtc,
        p.UpdatedAtUtc
    );

    public static string NormalizeCode(string code)
    {
        var s = (code ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(s))
            throw new InvalidOperationException("Code is required.");
        return s;
    }

    public static string? NormalizeOptionalCode(string? code)
    {
        var s = (code ?? "").Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static List<string> NormalizeCodes(IEnumerable<string>? codes)
        => (codes ?? Array.Empty<string>())
            .Select(NormalizeOptionalCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => x!)
            .ToList();
}
