using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Units;
using tdtd_be.Models;

namespace tdtd_be.Services;

public interface IUnitService
{
    Task<UnitResponse> CreateAsync(CreateUnitRequest req, CancellationToken ct);
    Task<UnitResponse> UpdateAsync(string unitId, UpdateUnitRequest req, CancellationToken ct);
    Task DeleteAsync(string unitId, CancellationToken ct);

    Task<IReadOnlyList<UnitResponse>> ListRootsAsync(CancellationToken ct);
    Task<IReadOnlyList<UnitResponse>> ListChildrenAsync(string parentUnitId, CancellationToken ct);
    Task<IReadOnlyList<UnitResponse>> SearchByCodePrefixAsync(string codePrefix, CancellationToken ct);

    Task<IReadOnlyList<UnitHistoryResponse>> GetHistoryAsync(string unitId, int take, CancellationToken ct);
    Task<IReadOnlyList<UnitPickNodeDTO>> GetChildrenAsync(string? parentId, CancellationToken ct);
}

public sealed class UnitService : IUnitService
{
    private const int SegLen = 3;

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public UnitService(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
    }

    private static int LevelFromCode(string code) => code.Length / SegLen;

    private static UnitResponse ToResp(Unit u) => new(
        u.Id, u.FullName, u.Code, u.ShortName, u.Symbol, u.Level, u.Version,
        u.ParentUnitId, u.UnitTypeCodes ?? new(), u.Note,
        u.CreatedAtUtc, u.UpdatedAtUtc
    );

    private static UnitHistoryResponse ToHist(UnitVersionHistory h) => new(
        h.Id, h.UnitId, h.Version, h.FullName, h.Code,
        h.ShortName, h.Symbol, h.Level, h.ParentUnitId,
        h.UnitTypeCodes ?? new(), h.CreatedAtUtc
    );

    // =============================
    // CREATE
    // =============================
    public async Task<UnitResponse> CreateAsync(CreateUnitRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(me);

        var now = DateTime.UtcNow;

        string? parentId = string.IsNullOrWhiteSpace(req.ParentUnitId)
            ? null
            : req.ParentUnitId.Trim();

        string code;

        if (parentId is null)
        {
            code = await GenerateNextRootCodeAsync(ct);
        }
        else
        {
            var parent = await _ctx.Units
                .Find(x => x.Id == parentId && !x.IsDeleted)
                .FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Parent unit not found or deleted.");

            code = await GenerateNextChildCodeAsync(parent.Code, ct);
        }

        var unit = new Unit
        {
            FullName = req.FullName.Trim(),
            ShortName = string.IsNullOrWhiteSpace(req.ShortName) ? null : req.ShortName.Trim(),
            ParentUnitId = parentId,
            Code = code,
            Level = LevelFromCode(code),
            Symbol = string.IsNullOrWhiteSpace(req.Symbol) ? null : req.Symbol.Trim(),
            UnitTypeCodes = req.UnitTypeCodes ?? new(),
            Version = 1,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsDeleted = false
        };

        await _ctx.Units.InsertOneAsync(unit, cancellationToken: ct);
        await InsertHistoryAsync(unit, me.Id, now, ct);

        return ToResp(unit);
    }

    // =============================
    // UPDATE (includes MOVE)
    // =============================
    public async Task<UnitResponse> UpdateAsync(string unitId, UpdateUnitRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(me);

        var existing = await _ctx.Units
            .Find(x => x.Id == unitId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Unit not found or deleted.");

        var now = DateTime.UtcNow;

        var update = Builders<Unit>.Update
            .Set(x => x.FullName, req.FullName.Trim())
            .Set(x => x.ShortName, string.IsNullOrWhiteSpace(req.ShortName) ? null : req.ShortName.Trim())
            .Set(x => x.Symbol, string.IsNullOrWhiteSpace(req.Symbol) ? null : req.Symbol.Trim()) // ✅ NEW
            .Set(x => x.Note, string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim())
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Inc(x => x.Version, 1);

        var after = await _ctx.Units.FindOneAndUpdateAsync(
            x => x.Id == unitId && !x.IsDeleted,
            update,
            new FindOneAndUpdateOptions<Unit> { ReturnDocument = ReturnDocument.After },
            ct);

        await InsertHistoryAsync(after!, me.Id, now, ct);

        return ToResp(after!);
    }

    private static UnitResponse ToResp(Unit u, string? oldParentUnitId = null, string? oldCode = null)
    {
        return new UnitResponse(
            Id: u.Id,
            FullName: u.FullName,
            Code: u.Code,
            ShortName: u.ShortName,
            Level: u.Level,
            Version: u.Version,
            ParentUnitId: u.ParentUnitId,
            UnitTypeCodes: u.UnitTypeCodes ?? new(),
            Symbol: u.Symbol,
            Note: u.Note,
            CreatedAtUtc: u.CreatedAtUtc,
            UpdatedAtUtc: u.UpdatedAtUtc,
            OldParentUnitId: oldParentUnitId,
            OldCode: oldCode
        );
    }

    // =============================
    // SOFT DELETE (cascade)
    // =============================

    public async Task DeleteAsync(string unitId, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireSystemAdmin(me);

        var unit = await _ctx.Units
            .Find(x => x.Id == unitId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Unit not found.");

        var prefix = unit.Code;

        // 🔹 1. Lấy subtree UnitId (Units << Users nên bước này rẻ)
        var subtreeIds = await _ctx.Units
            .Find(x => x.Code.StartsWith(prefix) && !x.IsDeleted)
            .Project(x => x.Id)
            .ToListAsync(ct);

        if (subtreeIds.Count == 0)
            return;

        // 🔹 2. Check user theo UnitId (cần index UnitId)
        var hasUsers = await _ctx.Users
            .Find(u => subtreeIds.Contains(u.UnitId))
            .Limit(1)
            .AnyAsync(ct);

        if (hasUsers)
            throw new InvalidOperationException("Cannot delete subtree because it contains users.");

        // 🔹 3. Soft delete subtree
        var now = DateTime.UtcNow;

        var update = Builders<Unit>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        await _ctx.Units.UpdateManyAsync(
            x => x.Code.StartsWith(prefix) && !x.IsDeleted,
            update,
            cancellationToken: ct);
    }

    // =============================
    // LIST ROOTS
    // =============================
    public async Task<IReadOnlyList<UnitResponse>> ListRootsAsync(CancellationToken ct)
    {
        var list = await _ctx.Units
            .Find(x => x.ParentUnitId == null && !x.IsDeleted)
            .SortBy(x => x.Code)
            .ToListAsync(ct);

        return list.Select(ToResp).ToList();
    }

    // =============================
    // LIST CHILDREN
    // =============================
    public async Task<IReadOnlyList<UnitResponse>> ListChildrenAsync(string parentUnitId, CancellationToken ct)
    {
        var list = await _ctx.Units
            .Find(x => x.ParentUnitId == parentUnitId && !x.IsDeleted)
            .SortBy(x => x.Code)
            .ToListAsync(ct);

        return list.Select(ToResp).ToList();
    }

    // =============================
    // SEARCH PREFIX
    // =============================
    public async Task<IReadOnlyList<UnitResponse>> SearchByCodePrefixAsync(string prefix, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return new List<UnitResponse>();

        var list = await _ctx.Units
            .Find(x => x.Code.StartsWith(prefix) && !x.IsDeleted)
            .SortBy(x => x.Code)
            .ToListAsync(ct);

        return list.Select(ToResp).ToList();
    }

    public async Task<IReadOnlyList<UnitPickNodeDTO>> GetChildrenAsync(string? parentId, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var units = _ctx.Units;

        // scope root: SYSTEM_ADMIN => global; others => me.UnitId (hoặc MANAGER_UNIT override)
        var scopeUnitId = me.UnitId;
        if (RoleGuard.TryGetManagerUnit(me, out var mu) && !string.IsNullOrWhiteSpace(mu))
            scopeUnitId = mu!;

        // load scope code (needed for out-of-scope checks)
        string? scopeCode = null;
        if (!RoleGuard.IsSystemAdmin(me))
        {
            var scope = await units.Find(x => x.Id == scopeUnitId)
                .Project(x => new { x.Code })
                .FirstOrDefaultAsync(ct);

            if (scope is null) throw new InvalidOperationException("Scope unit not found.");
            scopeCode = scope.Code;
        }

        // ===== level 0 (no parentId) =====
        if (string.IsNullOrWhiteSpace(parentId))
        {
            // SYSTEM_ADMIN can browse global roots
            if (RoleGuard.IsSystemAdmin(me))
            {
                return await units.Find(x => x.ParentUnitId == null && !x.IsDeleted)
                    .SortBy(x => x.Code)
                    .Project(x => new UnitPickNodeDTO(
                        x.Id,
                        x.FullName,
                        x.Code,
                        x.Level,
                        x.ShortName ?? "",
                        x.Symbol ?? ""
                    ))
                    .ToListAsync(ct);
            }

            // others: entry node = scope unit only
            return await units.Find(x => x.Id == scopeUnitId && !x.IsDeleted)
                .Project(x => new UnitPickNodeDTO(
                    x.Id,
                    x.FullName,
                    x.Code,
                    x.Level,
                    x.ShortName ?? "",
                    x.Symbol ?? ""
                ))
                .ToListAsync(ct);
        }

        // ===== load parent & scope check =====
        var parent = await units.Find(x => x.Id == parentId && !x.IsDeleted)
            .Project(x => new { x.Code })
            .FirstOrDefaultAsync(ct);

        if (parent is null) throw new InvalidOperationException("Unit not found.");

        // out-of-scope guard for non-system admins
        if (!RoleGuard.IsSystemAdmin(me))
        {
            if (string.IsNullOrEmpty(scopeCode) ||
                !parent.Code.StartsWith(scopeCode, StringComparison.Ordinal))
                throw new BadHttpRequestException("Out of scope.");
        }

        var parentCode = parent.Code;
        var childLen = parentCode.Length + 3;

        var filter =
            Builders<Unit>.Filter.Regex(x => x.Code,
                new MongoDB.Bson.BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(parentCode)))
            & Builders<Unit>.Filter.Where(x => x.Code.Length == childLen);

        return await units.Find(filter)
            .SortBy(x => x.Code)
            .Project(x => new UnitPickNodeDTO(
                x.Id,
                x.FullName,
                x.Code,
                x.Level,
                x.ShortName ?? "",
                x.Symbol ?? ""
            ))
            .ToListAsync(ct);
    }

    // =============================
    // HISTORY
    // =============================
    public async Task<IReadOnlyList<UnitHistoryResponse>> GetHistoryAsync(string unitId, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);

        var list = await _ctx.UnitHistories
            .Find(x => x.UnitId == unitId)
            .SortByDescending(x => x.Version)
            .Limit(take)
            .ToListAsync(ct);

        return list.Select(ToHist).ToList();
    }

    // =============================
    // GENERATORS (giữ nguyên)
    // =============================
    private async Task<string> GenerateNextRootCodeAsync(CancellationToken ct)
    {
        var last = await _ctx.Units
            .Find(x => x.ParentUnitId == null && !x.IsDeleted)
            .SortByDescending(x => x.Code)
            .Project(x => x.Code)
            .FirstOrDefaultAsync(ct);

        int next = string.IsNullOrWhiteSpace(last) ? 1 : int.Parse(last) + 1;
        return next.ToString().PadLeft(SegLen, '0');
    }

    private async Task<string> GenerateNextChildCodeAsync(string parentCode, CancellationToken ct)
    {
        var childLevel = LevelFromCode(parentCode) + 1;

        var filter = Builders<Unit>.Filter.And(
            Builders<Unit>.Filter.Regex(x => x.Code, new BsonRegularExpression("^" + parentCode)),
            Builders<Unit>.Filter.Eq(x => x.Level, childLevel),
            Builders<Unit>.Filter.Eq(x => x.IsDeleted, false)
        );

        var last = await _ctx.Units.Find(filter)
            .SortByDescending(x => x.Code)
            .Project(x => x.Code)
            .FirstOrDefaultAsync(ct);

        int next = 1;
        if (!string.IsNullOrWhiteSpace(last))
        {
            var seg = last.Substring(last.Length - SegLen, SegLen);
            next = int.Parse(seg) + 1;
        }

        return parentCode + next.ToString().PadLeft(SegLen, '0');
    }

    private async Task InsertHistoryAsync(Unit u, string byUserId, DateTime now, CancellationToken ct)
    {
        var h = new UnitVersionHistory
        {
            UnitId = u.Id,
            Version = u.Version,
            FullName = u.FullName,
            Code = u.Code,
            ShortName = u.ShortName,
            Symbol = u.Symbol,
            Level = u.Level,
            ParentUnitId = u.ParentUnitId,
            UnitTypeCodes = u.UnitTypeCodes ?? new(),
            CreatedByUserId = byUserId,
            UpdatedByUserId = byUserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _ctx.UnitHistories.InsertOneAsync(h, cancellationToken: ct);
    }
}