using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
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
        t.Id,
        t.Code,
        t.Name,
        (t.PositionRules ?? new())
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.PositionCode)
            .Select(ToRuleDto)
            .ToList(),
        t.IsDeleted,
        t.Version,
        t.CreatedAtUtc,
        t.UpdatedAtUtc
    );

    public async Task<UnitTypeResponse> CreateAsync(CreateUnitTypeRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var code = req.Code.Trim().ToUpperInvariant();
        var exists = await _ctx.UnitTypes
            .Find(x => x.Code == code && !x.IsDeleted)
            .AnyAsync(ct);
        if (exists)
            throw AppExceptionFactory.Create(AppErrorCode.UNIT_TYPE_CODE_DUPLICATE, new { code });

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
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var now = DateTime.UtcNow;

        var update = Builders<UnitType>.Update
            .Set(x => x.Name, req.Name.Trim())
            .Set(x => x.Note, string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim())
            .Inc(x => x.Version, 1)
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now);

        if (req.PositionRules is not null)
        {
            var rules = NormalizePositionRules(req.PositionRules);
            await EnsurePositionsExistAsync(rules.Select(x => x.PositionCode), ct);
            update = update.Set(x => x.PositionRules, rules);
        }

        var after = await _ctx.UnitTypes.FindOneAndUpdateAsync(
            x => x.Id == id && !x.IsDeleted, // Policy A: record đã xóa thì không cho update
            update,
            new FindOneAndUpdateOptions<UnitType> { ReturnDocument = ReturnDocument.After },
            ct);

        if (after is null)
            throw UnitTypeNotFound(id);
        return ToResp(after);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var unitType = await _ctx.UnitTypes
            .Find(x => x.Id == id && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (unitType is null)
            throw UnitTypeNotFound(id);

        var isUsedByUnits = await _ctx.Units
            .Find(x =>
                !x.IsDeleted &&
                (x.PrimaryUnitTypeCode == unitType.Code || x.UnitTypeCodes.Contains(unitType.Code)))
            .Limit(1)
            .AnyAsync(ct);

        if (isUsedByUnits)
            throw AppExceptionFactory.Create(AppErrorCode.UNIT_TYPE_IN_USE_BY_UNIT, new { id, unitType.Code });

        var isUsedByPositions = await _ctx.Positions
            .Find(x => !x.IsDeleted && x.UnitTypeCodes.Contains(unitType.Code))
            .Limit(1)
            .AnyAsync(ct);

        if (isUsedByPositions)
            throw AppExceptionFactory.Create(AppErrorCode.UNIT_TYPE_IN_USE_BY_POSITION, new { id, unitType.Code });

        var now = DateTime.UtcNow;
        var update = Builders<UnitType>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id)
            .Inc(x => x.Version, 1);

        var rs = await _ctx.UnitTypes.UpdateOneAsync(
            x => x.Id == id && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (rs.MatchedCount == 0)
            throw UnitTypeNotFound(id);
    }

    public async Task<IReadOnlyList<UnitTypeResponse>> ListAsync(bool? isDeleted, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var filter = Builders<UnitType>.Filter.Empty;
        if (isDeleted.HasValue)
            filter &= Builders<UnitType>.Filter.Eq(x => x.IsDeleted, isDeleted.Value);

        var list = await _ctx.UnitTypes.Find(filter).SortBy(x => x.Code).ToListAsync(ct);
        return list.Select(ToResp).ToList();
    }

    private async Task EnsurePositionsExistAsync(IEnumerable<string> codes, CancellationToken ct)
    {
        var list = codes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
            return;

        var found = await _ctx.Positions
            .Find(x => !x.IsDeleted && list.Contains(x.Code))
            .Project(x => x.Code)
            .ToListAsync(ct);

        var missing = list.Except(found, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
            throw AppExceptionFactory.NotFound(AppErrorCode.UNIT_TYPE_POSITION_NOT_FOUND, new { codes = missing });
    }

    private static List<UnitTypePositionRule> NormalizePositionRules(
        IEnumerable<UnitTypePositionRuleDto>? input)
    {
        var rows = new List<UnitTypePositionRule>();
        var index = 0;
        foreach (var item in input ?? Array.Empty<UnitTypePositionRuleDto>())
        {
            var code = PositionAdminService.NormalizeOptionalCode(item.PositionCode);
            if (string.IsNullOrWhiteSpace(code))
                continue;

            if (rows.Any(x => string.Equals(x.PositionCode, code, StringComparison.OrdinalIgnoreCase)))
                throw AppExceptionFactory.Create(AppErrorCode.UNIT_TYPE_POSITION_RULE_DUPLICATE, new { positionCode = code });

            if (item.MaxUsersPerUnit.HasValue && item.MaxUsersPerUnit.Value < 0)
                throw AppExceptionFactory.BadRequest(AppErrorCode.UNIT_TYPE_MAX_USERS_INVALID, new { positionCode = code, item.MaxUsersPerUnit });

            rows.Add(new UnitTypePositionRule
            {
                PositionCode = code,
                IsEnabled = item.IsEnabled,
                MaxUsersPerUnit = item.MaxUsersPerUnit,
                SortOrder = item.SortOrder != 0 ? item.SortOrder : index
            });
            index++;
        }

        return rows
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.PositionCode)
            .ToList();
    }

    private static UnitTypePositionRuleDto ToRuleDto(UnitTypePositionRule rule) => new()
    {
        PositionCode = rule.PositionCode,
        IsEnabled = rule.IsEnabled,
        MaxUsersPerUnit = rule.MaxUsersPerUnit,
        SortOrder = rule.SortOrder
    };

    private static AppException UnitTypeNotFound(string? id)
        => AppExceptionFactory.NotFound(AppErrorCode.UNIT_TYPE_NOT_FOUND, new { id });
}
