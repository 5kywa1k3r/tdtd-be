using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
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
    Task<IReadOnlyList<UnitResponse>> SearchByCodePrefixAsync(string? codePrefix, CancellationToken ct);

    Task<IReadOnlyList<UnitHistoryResponse>> GetHistoryAsync(string unitId, int take, CancellationToken ct);
    Task<IReadOnlyList<UnitPickNodeDTO>> GetChildrenAsync(string? parentId, CancellationToken ct);
}

public sealed class UnitService : IUnitService
{
    private const int SegLen = 3;

    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IManagementAccountProvisioner _accounts;

    public UnitService(MongoDbContext ctx, MeAccessor me, IManagementAccountProvisioner accounts)
    {
        _ctx = ctx;
        _me = me;
        _accounts = accounts;
    }

    private static int LevelFromCode(string code) => code.Length / SegLen;

    private static bool IsRootCodeToken(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized is "" or "ROOT";
    }

    private static bool IsRootNameToken(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized is "ROOT" or "ROOT UNIT";
    }

    private static bool IsHiddenRootUnit(Unit u)
        => IsRootCodeToken(u.Code) ||
           (string.IsNullOrWhiteSpace(u.ParentUnitId) &&
            (IsRootNameToken(u.FullName) || IsRootNameToken(u.ShortName) || IsRootNameToken(u.Symbol)));

    private static bool IsHiddenRootUnit(UnitBrowseNode u)
        => IsRootCodeToken(u.Code) ||
           (string.IsNullOrWhiteSpace(u.ParentUnitId) &&
            (IsRootNameToken(u.FullName) || IsRootNameToken(u.ShortName) || IsRootNameToken(u.Symbol)));

    private static UnitResponse ToResp(Unit u) => new(
        u.Id, u.FullName, u.Code, u.ShortName, u.Symbol, u.Level, u.Version,
        u.ParentUnitId, u.PrimaryUnitTypeCode, u.UnitTypeCodes ?? new(), u.IsVirtual, u.Note,
        u.CreatedAtUtc, u.UpdatedAtUtc
    );

    private static UnitHistoryResponse ToHist(UnitVersionHistory h) => new(
        h.Id, h.UnitId, h.Version, h.FullName, h.Code,
        h.ShortName, h.Symbol, h.Level, h.ParentUnitId,
        h.PrimaryUnitTypeCode, h.UnitTypeCodes ?? new(), h.IsVirtual, h.CreatedAtUtc
    );

    // =============================
    // CREATE
    // =============================
    public async Task<UnitResponse> CreateAsync(CreateUnitRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var now = DateTime.UtcNow;
        var primaryUnitTypeCode = await RequireUnitTypeCodeAsync(req.PrimaryUnitTypeCode, ct);
        var unitTypeCodes = MergeUnitTypeCodes(primaryUnitTypeCode, req.UnitTypeCodes);

        string? parentId = string.IsNullOrWhiteSpace(req.ParentUnitId)
            ? null
            : req.ParentUnitId.Trim();
        parentId ??= (await FindHiddenRootAsync(ct))?.Id;

        var symbol = string.IsNullOrWhiteSpace(req.Symbol) ? null : req.Symbol.Trim();
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var symbolExists = await _ctx.Units
                .Find(x => x.Symbol == symbol && !x.IsDeleted)
                .AnyAsync(ct);
            if (symbolExists)
                throw AppExceptionFactory.Create(AppErrorCode.UNIT_SYMBOL_DUPLICATE, new { symbol });
        }

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
                ?? throw AppExceptionFactory.NotFound(AppErrorCode.UNIT_PARENT_NOT_FOUND, new { parentId });

            var parentCode = parent.Code
                ?? throw AppExceptionFactory.Create(AppErrorCode.UNIT_PARENT_CODE_MISSING, new { parentId });
            code = await GenerateNextChildCodeAsync(parentCode, ct);
        }

        var unit = new Unit
        {
            FullName = req.FullName.Trim(),
            ShortName = string.IsNullOrWhiteSpace(req.ShortName) ? null : req.ShortName.Trim(),
            ParentUnitId = parentId,
            Code = code,
            Level = LevelFromCode(code),
            Symbol = symbol,
            PrimaryUnitTypeCode = primaryUnitTypeCode,
            UnitTypeCodes = unitTypeCodes,
            IsVirtual = req.IsVirtual,
            Version = 1,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsDeleted = false
        };

        await _ctx.Units.InsertOneAsync(unit, cancellationToken: ct);
        await InsertHistoryAsync(unit, me.Id, now, ct);
        if (!unit.IsVirtual)
            await _accounts.EnsureForUnitAsync(unit, me.Id, now, ct);

        return ToResp(unit);
    }

    // =============================
    // UPDATE (includes MOVE)
    // =============================
    public async Task<UnitResponse> UpdateAsync(string unitId, UpdateUnitRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var existing = await _ctx.Units
            .Find(x => x.Id == unitId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw UnitNotFound(unitId);

        var now = DateTime.UtcNow;
        var primaryUnitTypeCode = await RequireUnitTypeCodeAsync(req.PrimaryUnitTypeCode, ct);
        var unitTypeCodes = MergeUnitTypeCodes(primaryUnitTypeCode, req.UnitTypeCodes);
        var symbol = string.IsNullOrWhiteSpace(req.Symbol) ? null : req.Symbol.Trim();

        if (req.IsVirtual)
            await EnsureNoDirectNormalUsersAsync(unitId, ct);

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var symbolExists = await _ctx.Units
                .Find(x => x.Id != unitId && x.Symbol == symbol && !x.IsDeleted)
                .AnyAsync(ct);
            if (symbolExists)
                throw AppExceptionFactory.Create(AppErrorCode.UNIT_SYMBOL_DUPLICATE, new { symbol, unitId });
        }

        var update = Builders<Unit>.Update
            .Set(x => x.FullName, req.FullName.Trim())
            .Set(x => x.ShortName, string.IsNullOrWhiteSpace(req.ShortName) ? null : req.ShortName.Trim())
            .Set(x => x.Symbol, symbol)
            .Set(x => x.PrimaryUnitTypeCode, primaryUnitTypeCode)
            .Set(x => x.UnitTypeCodes, unitTypeCodes)
            .Set(x => x.IsVirtual, req.IsVirtual)
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
        if (!after!.IsVirtual)
            await _accounts.EnsureForUnitAsync(after!, me.Id, now, ct);

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
            PrimaryUnitTypeCode: u.PrimaryUnitTypeCode,
            UnitTypeCodes: u.UnitTypeCodes ?? new(),
            IsVirtual: u.IsVirtual,
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
        RoleGuard.RequireAdminOrSystemAdmin(me);

        var unit = await _ctx.Units
            .Find(x => x.Id == unitId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct)
            ?? throw UnitNotFound(unitId);

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
            .Find(u =>
                subtreeIds.Contains(u.UnitId) &&
                !u.IsDeleted &&
                u.AccountKind != ManagementAccountKind.UnitManager &&
                u.AccountKind != ManagementAccountKind.LevelManager)
            .Limit(1)
            .AnyAsync(ct);

        if (hasUsers)
            throw AppExceptionFactory.Create(AppErrorCode.UNIT_DELETE_HAS_USERS, new { unitId, subtreeIds });

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

        var userUpdate = Builders<AppUser>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now)
            .Set(x => x.UpdatedByUserId, me.Id);

        await _ctx.Users.UpdateManyAsync(
            x => subtreeIds.Contains(x.UnitId) && x.AccountKind == ManagementAccountKind.UnitManager && !x.IsDeleted,
            userUpdate,
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

        var hiddenRootIds = list.Where(IsHiddenRootUnit).Select(x => x.Id).ToList();
        var visible = list.Where(x => !IsHiddenRootUnit(x)).ToList();

        if (hiddenRootIds.Count > 0)
        {
            var children = await _ctx.Units
                .Find(x => x.ParentUnitId != null && hiddenRootIds.Contains(x.ParentUnitId) && !x.IsDeleted)
                .SortBy(x => x.Code)
                .ToListAsync(ct);

            visible.AddRange(children.Where(x => !IsHiddenRootUnit(x)));
        }

        return visible
            .OrderBy(x => x.Code, StringComparer.Ordinal)
            .Select(ToResp)
            .ToList();
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

        return list
            .Where(x => !IsHiddenRootUnit(x))
            .Select(ToResp)
            .ToList();
    }

    // =============================
    // SEARCH PREFIX
    // =============================
    public async Task<IReadOnlyList<UnitResponse>> SearchByCodePrefixAsync(string? prefix, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var canBrowseAllUnits = RoleGuard.IsAdmin(me) || RoleGuard.IsSystemAdmin(me);
        int? levelWideManagerLevel = null;
        if (string.IsNullOrWhiteSpace(me.UnitId) &&
            RoleGuard.TryGetGeneratedLevelManager(me, out var generatedLevel))
        {
            levelWideManagerLevel = generatedLevel;
        }

        prefix = (prefix ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(prefix))
        {
            if (!canBrowseAllUnits && levelWideManagerLevel is null)
                return new List<UnitResponse>();

            var filter = Builders<Unit>.Filter.Eq(x => x.IsDeleted, false);
            if (levelWideManagerLevel is not null)
                filter &= Builders<Unit>.Filter.Gte(x => x.Level, levelWideManagerLevel.Value);

            var allUnits = await _ctx.Units
                .Find(filter)
                .SortBy(x => x.Code)
                .ToListAsync(ct);

            return allUnits
                .Where(x => !IsHiddenRootUnit(x))
                .Select(ToResp)
                .ToList();
        }

        var units = _ctx.Units;

        string? scopeUnitId = me.UnitId;
        if (RoleGuard.TryGetManagerUnit(me, out var mu) && !string.IsNullOrWhiteSpace(mu))
            scopeUnitId = mu!;

        var browseRoots = await GetBrowseRootUnitsAsync(canBrowseAllUnits, scopeUnitId, levelWideManagerLevel, ct);

        if (!canBrowseAllUnits)
        {
            var allowed = browseRoots.Any(root =>
                string.Equals(prefix, root.Code, StringComparison.Ordinal) ||
                prefix.StartsWith(root.Code, StringComparison.Ordinal));

            if (!allowed)
                throw AppExceptionFactory.Forbidden(AppErrorCode.UNIT_SCOPE_FORBIDDEN, new { prefix, scopeUnitId });
        }

        var list = await units
            .Find(x => x.Code.StartsWith(prefix) && !x.IsDeleted)
            .SortBy(x => x.Code)
            .ToListAsync(ct);

        return list
            .Where(x => !IsHiddenRootUnit(x))
            .Select(ToResp)
            .ToList();
    }

    public async Task<IReadOnlyList<UnitPickNodeDTO>> GetChildrenAsync(string? parentId, CancellationToken ct)
    {
        var me = _me.RequireMe();
        var units = _ctx.Units;
        var canBrowseAllUnits = RoleGuard.IsAdmin(me) || RoleGuard.IsSystemAdmin(me);
        int? levelWideManagerLevel = null;
        if (string.IsNullOrWhiteSpace(me.UnitId) &&
            RoleGuard.TryGetGeneratedLevelManager(me, out var generatedLevel))
        {
            levelWideManagerLevel = generatedLevel;
        }

        string? scopeUnitId = me.UnitId;
        if (RoleGuard.TryGetManagerUnit(me, out var mu) && !string.IsNullOrWhiteSpace(mu))
            scopeUnitId = mu!;

        var browseRoots = await GetBrowseRootUnitsAsync(canBrowseAllUnits, scopeUnitId, levelWideManagerLevel, ct);

        if (string.IsNullOrWhiteSpace(parentId))
        {
            return browseRoots
                .OrderBy(x => x.Code, StringComparer.Ordinal)
                .Select(ToPickNode)
                .ToList();
        }

        var parent = await units.Find(x => x.Id == parentId && !x.IsDeleted)
            .Project(x => new UnitBrowseNode(
                x.Id,
                x.FullName,
                x.Code,
                x.Level,
                x.ShortName,
                x.Symbol,
                x.ParentUnitId,
                x.PrimaryUnitTypeCode,
                x.IsVirtual))
            .FirstOrDefaultAsync(ct);

        if (parent is null)
            throw UnitNotFound(parentId);

        if (!canBrowseAllUnits)
        {
            var allowed = browseRoots.Any(root =>
                string.Equals(parent.Code, root.Code, StringComparison.Ordinal) ||
                parent.Code.StartsWith(root.Code, StringComparison.Ordinal));

            if (!allowed)
                throw AppExceptionFactory.Forbidden(AppErrorCode.UNIT_SCOPE_FORBIDDEN, new { parentId, parent.Code, scopeUnitId });
        }

        return await units.Find(x => x.ParentUnitId == parentId && !x.IsDeleted)
            .SortBy(x => x.Code)
            .Project(x => new UnitPickNodeDTO(
                x.Id,
                x.FullName,
                x.Code,
                x.Level,
                x.ShortName ?? "",
                x.Symbol ?? "",
                x.PrimaryUnitTypeCode,
                x.IsVirtual
            ))
            .ToListAsync(ct);
    }

    private async Task<List<UnitBrowseNode>> GetBrowseRootUnitsAsync(
        bool canBrowseAllUnits,
        string? scopeUnitId,
        int? levelWideManagerLevel,
        CancellationToken ct)
    {
        var units = _ctx.Units;

        if (canBrowseAllUnits)
        {
            var roots = await units.Find(x => x.ParentUnitId == null && !x.IsDeleted)
                .SortBy(x => x.Code)
                .Project(x => new UnitBrowseNode(
                    x.Id,
                    x.FullName,
                    x.Code,
                    x.Level,
                    x.ShortName,
                    x.Symbol,
                    x.ParentUnitId,
                    x.PrimaryUnitTypeCode,
                    x.IsVirtual))
                .ToListAsync(ct);

            var hiddenRootIds = roots.Where(IsHiddenRootUnit).Select(x => x.Id).ToList();
            var visible = roots.Where(x => !IsHiddenRootUnit(x)).ToList();

            if (hiddenRootIds.Count > 0)
            {
                var children = await units.Find(x => x.ParentUnitId != null && hiddenRootIds.Contains(x.ParentUnitId) && !x.IsDeleted)
                    .SortBy(x => x.Code)
                    .Project(x => new UnitBrowseNode(
                        x.Id,
                        x.FullName,
                        x.Code,
                        x.Level,
                        x.ShortName,
                        x.Symbol,
                        x.ParentUnitId,
                        x.PrimaryUnitTypeCode,
                        x.IsVirtual))
                    .ToListAsync(ct);

                visible.AddRange(children.Where(x => !IsHiddenRootUnit(x)));
            }

            return visible
                .OrderBy(x => x.Code, StringComparer.Ordinal)
                .ToList();
        }

        if (levelWideManagerLevel is not null)
        {
            var levelRoots = await units.Find(x => !x.IsDeleted && x.Level == levelWideManagerLevel.Value)
                .SortBy(x => x.Code)
                .Project(x => new UnitBrowseNode(
                    x.Id,
                    x.FullName,
                    x.Code,
                    x.Level,
                    x.ShortName,
                    x.Symbol,
                    x.ParentUnitId,
                    x.PrimaryUnitTypeCode,
                    x.IsVirtual))
                .ToListAsync(ct);

            return levelRoots
                .Where(x => !IsHiddenRootUnit(x))
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(scopeUnitId))
            throw AppExceptionFactory.NotFound(AppErrorCode.UNIT_SCOPE_NOT_FOUND, new { scopeUnitId });

        var scope = await units.Find(x => x.Id == scopeUnitId && !x.IsDeleted)
            .Project(x => new UnitBrowseNode(
                x.Id,
                x.FullName,
                x.Code,
                x.Level,
                x.ShortName,
                x.Symbol,
                x.ParentUnitId,
                x.PrimaryUnitTypeCode,
                x.IsVirtual))
            .FirstOrDefaultAsync(ct);

        if (scope is null)
            throw AppExceptionFactory.NotFound(AppErrorCode.UNIT_SCOPE_NOT_FOUND, new { scopeUnitId });

        // Dashboard/assignment picker cần thấy các đơn vị ngang cấp.
        // Tạm thời dùng cùng level (code length bằng nhau) để trả entry nodes.
        var scopedRoots = await units.Find(x => !x.IsDeleted && x.Code.Length == scope.Code.Length)
            .SortBy(x => x.Code)
            .Project(x => new UnitBrowseNode(
                x.Id,
                x.FullName,
                x.Code,
                x.Level,
                x.ShortName,
                x.Symbol,
                x.ParentUnitId,
                x.PrimaryUnitTypeCode,
                x.IsVirtual))
            .ToListAsync(ct);

        return scopedRoots
            .Where(x => !IsHiddenRootUnit(x))
            .ToList();
    }

    private static UnitPickNodeDTO ToPickNode(UnitBrowseNode x)
        => new(
            x.Id,
            x.FullName,
            x.Code,
            x.Level,
            x.ShortName ?? "",
            x.Symbol ?? "",
            x.PrimaryUnitTypeCode,
            x.IsVirtual
        );

    private sealed record UnitBrowseNode(
        string Id,
        string FullName,
        string Code,
        int Level,
        string? ShortName,
        string? Symbol,
        string? ParentUnitId,
        string? PrimaryUnitTypeCode,
        bool IsVirtual
    );

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

    private async Task<Unit?> FindHiddenRootAsync(CancellationToken ct)
    {
        var roots = await _ctx.Units
            .Find(x => x.ParentUnitId == null && !x.IsDeleted)
            .SortBy(x => x.Code)
            .ToListAsync(ct);

        return roots.FirstOrDefault(IsHiddenRootUnit);
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

    private async Task<string> RequireUnitTypeCodeAsync(string? code, CancellationToken ct)
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            throw AppExceptionFactory.BadRequest(AppErrorCode.UNIT_PRIMARY_TYPE_REQUIRED, new { field = "primaryUnitTypeCode" });

        var exists = await _ctx.UnitTypes
            .Find(x => x.Code == normalized && !x.IsDeleted)
            .AnyAsync(ct);

        if (!exists)
            throw AppExceptionFactory.NotFound(AppErrorCode.UNIT_TYPE_NOT_FOUND, new { code = normalized });

        return normalized;
    }

    private static List<string> MergeUnitTypeCodes(string primary, IEnumerable<string>? secondaryCodes)
    {
        var result = new List<string> { primary };
        result.AddRange((secondaryCodes ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task EnsureNoDirectNormalUsersAsync(string unitId, CancellationToken ct)
    {
        var hasDirectUsers = await _ctx.Users
            .Find(x =>
                x.UnitId == unitId &&
                !x.IsDeleted &&
                x.AccountKind != ManagementAccountKind.UnitManager &&
                x.AccountKind != ManagementAccountKind.LevelManager)
            .Limit(1)
            .AnyAsync(ct);

        if (hasDirectUsers)
            throw AppExceptionFactory.Create(AppErrorCode.UNIT_VIRTUAL_HAS_USERS, new { unitId });
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
            PrimaryUnitTypeCode = u.PrimaryUnitTypeCode,
            UnitTypeCodes = u.UnitTypeCodes ?? new(),
            IsVirtual = u.IsVirtual,
            CreatedByUserId = byUserId,
            UpdatedByUserId = byUserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _ctx.UnitHistories.InsertOneAsync(h, cancellationToken: ct);
    }

    private static AppException UnitNotFound(string? unitId)
        => AppExceptionFactory.NotFound(AppErrorCode.UNIT_NOT_FOUND, new { unitId });
}
