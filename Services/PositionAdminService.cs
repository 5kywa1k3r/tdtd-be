using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
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
    Task ValidatePositionForUnitTypeAsync(
        string? positionCode,
        string? unitTypeCode,
        CancellationToken ct,
        string? unitId = null,
        string? excludeUserId = null);
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
            throw AppExceptionFactory.Create(AppErrorCode.POSITION_CODE_DUPLICATE, new { code });

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
            throw PositionNotFound(id);

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
            throw PositionNotFound(id);
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

    public async Task ValidatePositionForUnitTypeAsync(
        string? positionCode,
        string? unitTypeCode,
        CancellationToken ct,
        string? unitId = null,
        string? excludeUserId = null)
    {
        var pc = NormalizeOptionalCode(positionCode);
        if (string.IsNullOrWhiteSpace(pc))
            throw AppExceptionFactory.BadRequest(AppErrorCode.POSITION_CODE_REQUIRED, new { field = "positionCode" });

        var utc = NormalizeOptionalCode(unitTypeCode);
        if (string.IsNullOrWhiteSpace(utc))
            throw AppExceptionFactory.BadRequest(AppErrorCode.POSITION_UNIT_TYPE_REQUIRED, new { field = "unitTypeCode", unitId });

        var unitType = await _ctx.UnitTypes
            .Find(x => x.Code == utc && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        var rules = unitType?.PositionRules ?? new();
        if (rules.Count > 0)
        {
            var rule = rules.FirstOrDefault(x =>
                x.IsEnabled &&
                string.Equals(x.PositionCode, pc, StringComparison.OrdinalIgnoreCase));

            if (rule is null)
                throw InvalidPositionForUnitType(pc, utc, unitId, "ruleMissing");

            var positionExists = await _ctx.Positions
                .Find(x => x.Code == pc && !x.IsDeleted)
                .AnyAsync(ct);
            if (!positionExists)
                throw InvalidPositionForUnitType(pc, utc, unitId, "positionMissing");

            await EnsurePositionQuotaAsync(pc, unitId, excludeUserId, rule.MaxUsersPerUnit, ct);
            return;
        }

        var exists = await _ctx.Positions
            .Find(x => x.Code == pc && !x.IsDeleted && x.UnitTypeCodes.Contains(utc))
            .AnyAsync(ct);

        if (!exists)
            throw InvalidPositionForUnitType(pc, utc, unitId, "notAllowed");

        await EnsurePositionQuotaAsync(pc, unitId, excludeUserId, null, ct);
    }

    private async Task EnsurePositionQuotaAsync(
        string positionCode,
        string? unitId,
        string? excludeUserId,
        int? maxUsersPerUnit,
        CancellationToken ct)
    {
        if (!maxUsersPerUnit.HasValue || string.IsNullOrWhiteSpace(unitId))
            return;

        var fb = Builders<AppUser>.Filter;
        var filter = fb.Eq(x => x.UnitId, unitId)
                     & fb.Eq(x => x.PositionCode, positionCode)
                     & fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(excludeUserId))
            filter &= fb.Ne(x => x.Id, excludeUserId);

        var used = await _ctx.Users.CountDocumentsAsync(filter, cancellationToken: ct);
        if (used >= maxUsersPerUnit.Value)
            throw AppExceptionFactory.Create(AppErrorCode.POSITION_QUOTA_EXCEEDED, new { positionCode, unitId, maxUsersPerUnit, used });
    }

    private async Task EnsureUnitTypesExistAsync(IReadOnlyList<string> codes, CancellationToken ct)
    {
        if (codes.Count == 0)
            throw AppExceptionFactory.BadRequest(AppErrorCode.POSITION_UNIT_TYPE_CODES_REQUIRED, new { field = "unitTypeCodes" });

        var found = await _ctx.UnitTypes
            .Find(x => codes.Contains(x.Code) && !x.IsDeleted)
            .Project(x => x.Code)
            .ToListAsync(ct);

        var missing = codes.Except(found, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            throw AppExceptionFactory.NotFound(AppErrorCode.UNIT_TYPE_NOT_FOUND, new { codes = missing });
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
            throw AppExceptionFactory.BadRequest(AppErrorCode.POSITION_CODE_REQUIRED, new { field = "code" });
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

    private static AppException PositionNotFound(string? id)
        => AppExceptionFactory.NotFound(AppErrorCode.POSITION_NOT_FOUND, new { id });

    private static AppException InvalidPositionForUnitType(string positionCode, string unitTypeCode, string? unitId, string reason)
        => AppExceptionFactory.BadRequest(AppErrorCode.POSITION_INVALID_FOR_UNIT_TYPE, new { positionCode, unitTypeCode, unitId, reason });
}
