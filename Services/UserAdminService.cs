using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Users.Admin;
using tdtd_be.Models;

namespace tdtd_be.Services;

public interface IUserAdminService
{
    Task<UserResponse> GetByIdAsync(string userId, CancellationToken ct);
    Task<UserResponse> CreateAsync(CreateUserRequest req, CancellationToken ct);
    Task<UserResponse> UpdateAsync(string userId, UpdateUserRequest req, CancellationToken ct);
    Task ResetPasswordAsync(string userId, ResetPasswordRequest req, CancellationToken ct);
    Task<PagedResult<UserSearchRow>> SearchUsersAsync(
        string? q,
        bool? isDeleted,
        string? unitCodePrefix,
        string? positionCode,
        int page,
        int pageSize,
        string? sortField,
        string? sortDirection,
        CancellationToken ct);
    Task SoftDeleteAsync(string userId, CancellationToken ct);
}

public sealed class UserAdminService : IUserAdminService
{
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly UnitTreeHelper _tree;
    private readonly AuthService _auth;
    private readonly PasswordHasher<AppUser> _hasher;
    private readonly IPositionAdminService _positions;

    public UserAdminService(
        MongoDbContext ctx,
        MeAccessor me,
        UnitTreeHelper tree,
        AuthService auth,
        PasswordHasher<AppUser> hasher,
        IPositionAdminService positions)
    {
        _ctx = ctx; _me = me; _tree = tree; _auth = auth; _hasher = hasher; _positions = positions;
    }

    private static string NormalizeUsername(string s) => (s ?? "").Trim().ToLowerInvariant();

    private static UserResponse ToResp(AppUser u, string unitSymbol, string unitName, string unitCode, string? positionName = null) => new(
        u.Id,
        u.Username,
        u.FullName,
        u.UnitId,
        unitSymbol,
        unitName,
        unitCode,
        u.Roles ?? new(),
        u.IsDeleted,
        u.CreatedAtUtc,
        u.UpdatedAtUtc,
        u.PositionCode,
        positionName,
        u.AccountKind
    );

    private static void PreventAssignAdmin(IEnumerable<string>? roles)
    {
        if (roles is null) return;
        foreach (var r in roles)
            if (string.Equals(r, Roles.ADMIN, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cannot assign ADMIN.");
    }

    private static bool ContainsRole(IEnumerable<string>? roles, string role)
        => roles?.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool ContainsManagerRoles(IEnumerable<string>? roles)
    {
        if (roles is null) return false;
        foreach (var r in roles)
            if (string.Equals(r, Roles.MANAGER_LEVEL, StringComparison.OrdinalIgnoreCase) || Roles.IsManagerUnit(r, out _))
                return true;
        return false;
    }

    private static bool HasRole(AppUser u, string role)
    => u.Roles?.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool IsAdminUser(AppUser u) => HasRole(u, Roles.ADMIN);
    private static bool IsSystemAdminUser(AppUser u) => HasRole(u, Roles.SYSTEM_ADMIN);

    private static void RequireCanManageUser(MeResponse me, AppUser target)
    {
        var meIsAdmin = RoleGuard.IsAdmin(me);
        var meIsSys = RoleGuard.IsSystemAdmin(me);
        var meIsMgrLevel = RoleGuard.IsManagerLevel(me);
        var meIsMgrUnit = RoleGuard.TryGetManagerUnit(me, out _);

        // không ai được đụng ADMIN
        if (IsAdminUser(target))
            throw new BadHttpRequestException("Cannot manage ADMIN user.");

        // ADMIN: chỉ quản SYSTEM_ADMIN
        if (meIsAdmin)
        {
            if (!IsSystemAdminUser(target))
                throw new BadHttpRequestException("ADMIN can only manage SYSTEM_ADMIN.");
            return;
        }

        // SYSTEM_ADMIN: không đụng SYSTEM_ADMIN khác
        if (meIsSys)
        {
            if (IsSystemAdminUser(target) && target.Id != me.Id)
                throw new BadHttpRequestException("SYSTEM_ADMIN cannot manage other SYSTEM_ADMIN.");
            return;
        }

        // MANAGER_UNIT: không đụng SYSTEM_ADMIN
        if (meIsMgrUnit)
        {
            if (IsSystemAdminUser(target))
                throw new BadHttpRequestException("Cannot manage SYSTEM_ADMIN.");
            return;
        }

        // MANAGER_LEVEL: không đụng SYSTEM_ADMIN
        if (meIsMgrLevel)
        {
            if (IsSystemAdminUser(target))
                throw new BadHttpRequestException("Cannot manage SYSTEM_ADMIN.");
            return;
        }

        throw new BadHttpRequestException("Not allowed.");
    }

    // scope cho manager (unit/subtree). SYS/ADMIN coi như full scope
    private async Task EnsureScopeForTargetAsync(MeResponse me, AppUser targetUser, CancellationToken ct)
    {
        if (RoleGuard.IsAdmin(me) || RoleGuard.IsSystemAdmin(me))
            return;

        if (RoleGuard.TryGetManagerUnit(me, out var managedUnitId))
        {
            if (!string.Equals(targetUser.UnitId, managedUnitId, StringComparison.Ordinal))
                throw new BadHttpRequestException("MANAGER_UNIT scope.");
            return;
        }

        if (RoleGuard.IsManagerLevel(me))
        {
            var meUnit = await ResolveManagerLevelScopeAsync(me, ct);
            var targetUnit = await RequireUnitAsync(targetUser.UnitId, ct);
            EnsureManagerLevelScope(meUnit, (targetUnit.Code, targetUnit.Level));
            return;
        }

        throw new BadHttpRequestException("Not allowed.");
    }

    /// <summary>
    /// Rule gán role khi CREATE/UPDATE:
    /// - ADMIN: chỉ được gán SYSTEM_ADMIN (ngoài ra chỉ user thường)
    /// - SYSTEM_ADMIN: được gán MANAGER_LEVEL / MANAGER_UNIT:* (và user thường)
    /// - Managers: không được gán SYSTEM_ADMIN / manager roles
    /// </summary>
    private static void EnforceRoleAssignmentPolicy(MeResponse me, IEnumerable<string>? targetRoles, bool isCreate)
    {
        PreventAssignAdmin(targetRoles);

        var wantSystemAdmin = ContainsRole(targetRoles, Roles.SYSTEM_ADMIN);
        var wantManagerRoles = ContainsManagerRoles(targetRoles);

        if (RoleGuard.IsAdmin(me))
        {
            if (!isCreate) throw new BadHttpRequestException("ADMIN cannot update roles.");

            // ✅ ADMIN chỉ tạo đúng SYSTEM_ADMIN (không kèm role khác)
            var list = (targetRoles ?? Array.Empty<string>())
                .Select(x => (x ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (list.Count != 1 || !string.Equals(list[0], Roles.SYSTEM_ADMIN, StringComparison.OrdinalIgnoreCase))
                throw new BadHttpRequestException("ADMIN can only create SYSTEM_ADMIN (only).");

            if (wantManagerRoles) throw new BadHttpRequestException("ADMIN cannot assign manager roles.");
            return;
        }

        if (RoleGuard.IsSystemAdmin(me))
        {
            // SYSTEM_ADMIN không được tạo SYSTEM_ADMIN (vì chỉ ADMIN tạo)
            if (wantSystemAdmin) throw new BadHttpRequestException("Only ADMIN can create SYSTEM_ADMIN.");
            // SYSTEM_ADMIN được gán MANAGER_LEVEL / MANAGER_UNIT:* OK
            return;
        }

        // Managers:
        if (wantSystemAdmin) throw new BadHttpRequestException("Cannot assign SYSTEM_ADMIN.");
        if (wantManagerRoles) throw new BadHttpRequestException("Only SYSTEM_ADMIN can assign manager roles.");
    }

    private async Task<Unit> RequireUnitAsync(string unitId, CancellationToken ct)
    {
        var u = await _ctx.Units.Find(x => x.Id == unitId).FirstOrDefaultAsync(ct);
        if (u is null) throw new InvalidOperationException("Unit not found.");
        return u;
    }

    private static void EnsureUserBearingUnit(Unit unit)
    {
        if (unit.IsVirtual)
            throw new InvalidOperationException("Cannot create or manage a user inside a virtual unit.");
    }

    private static void EnsureUnitUsableForNewUser(Unit unit)
    {
        if (unit.IsDeleted)
            throw new InvalidOperationException("Cannot create a user inside a deleted unit.");
        EnsureUserBearingUnit(unit);
    }

    private static bool IsInSubtree(string meCode, string targetCode)
        => !string.IsNullOrWhiteSpace(meCode)
           && !string.IsNullOrWhiteSpace(targetCode)
           && targetCode.StartsWith(meCode, StringComparison.Ordinal);

    private sealed record ManagerLevelScope(string? UnitCode, int Level, bool IsLevelWide);

    private static bool TryParseGeneratedLevelManager(MeResponse me, out int level)
    {
        level = 0;
        var username = NormalizeUsername(me.Username ?? string.Empty);
        var prefix = ManagementAccountConvention.LevelManagerPrefix;
        if (!username.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var raw = username[prefix.Length..].Trim();
        return int.TryParse(raw, out level) && level >= 0;
    }

    private async Task<ManagerLevelScope> ResolveManagerLevelScopeAsync(MeResponse me, CancellationToken ct)
    {
        if (!RoleGuard.IsManagerLevel(me))
            throw new BadHttpRequestException("MANAGER_LEVEL required.");

        if (!string.IsNullOrWhiteSpace(me.UnitId))
        {
            var meUnit = await _ctx.Units.Find(x => x.Id == me.UnitId)
                .Project(x => new { x.Code, x.Level })
                .FirstOrDefaultAsync(ct);

            if (meUnit is not null)
                return new ManagerLevelScope(meUnit.Code, meUnit.Level, IsLevelWide: false);
        }

        if (TryParseGeneratedLevelManager(me, out var generatedLevel))
            return new ManagerLevelScope(UnitCode: null, Level: generatedLevel, IsLevelWide: true);

        throw new InvalidOperationException("Me unit not found.");
    }

    private static void EnsureManagerLevelScope((string meCode, int meLevel) me, (string targetCode, int targetLevel) target)
    {
        if (!IsInSubtree(me.meCode, target.targetCode))
            throw new BadHttpRequestException("Outside manager subtree.");
        if (target.targetLevel < me.meLevel)
            throw new BadHttpRequestException("Cannot manage upper level.");
    }

    private static void EnsureManagerLevelScope(ManagerLevelScope scope, (string targetCode, int targetLevel) target)
    {
        if (scope.IsLevelWide)
        {
            if (target.targetLevel < scope.Level)
                throw new BadHttpRequestException("Cannot manage upper level.");
            return;
        }

        EnsureManagerLevelScope((scope.UnitCode ?? string.Empty, scope.Level), target);
    }
    public async Task<UserResponse> GetByIdAsync(string userId, CancellationToken ct)
    {
        var me = _me.RequireMe();

        var u = await _ctx.Users.Find(x => x.Id == userId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        if (u is null) throw new InvalidOperationException("User not found.");
        var targetUnit = await _ctx.Units.Find(x => x.Id == u.UnitId)
            .Project(x => new { x.Symbol, x.ShortName, x.Code, x.Level, x.PrimaryUnitTypeCode })
            .FirstOrDefaultAsync(ct) ?? throw new InvalidOperationException("Target unit not found.");

        var positionName = await GetPositionNameAsync(u.PositionCode, ct);

        // Allowed: ADMIN, SYSTEM_ADMIN, MANAGER_UNIT, MANAGER_LEVEL
        if (RoleGuard.IsAdmin(me) || RoleGuard.IsSystemAdmin(me))
            return ToResp(u, targetUnit.Symbol, targetUnit.ShortName, targetUnit.Code, positionName);

        if (RoleGuard.TryGetManagerUnit(me, out var managedUnitId))
        {
            if (!string.Equals(u.UnitId, managedUnitId, StringComparison.Ordinal))
                throw new BadHttpRequestException("MANAGER_UNIT scope.");
            return ToResp(u, targetUnit.Symbol, targetUnit.ShortName, targetUnit.Code, positionName);
        }

        if (RoleGuard.IsManagerLevel(me))
        {
            var meUnit = await ResolveManagerLevelScopeAsync(me, ct);
            EnsureManagerLevelScope(meUnit, (targetUnit.Code, targetUnit.Level));

            return ToResp(u, targetUnit.Symbol, targetUnit.ShortName, targetUnit.Code, positionName);
        }

        throw new BadHttpRequestException("Not allowed.");
    }

    public async Task<UserResponse> CreateAsync(CreateUserRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();

        var username = NormalizeUsername(req.Username);
        if (await _ctx.Users.Find(x => x.Username == username && !x.IsDeleted).AnyAsync(ct))
            throw new InvalidOperationException("Username exists.");

        var targetUnit = await RequireUnitAsync(req.UnitId, ct);
        EnsureUnitUsableForNewUser(targetUnit);

        // enforce role assignment
        EnforceRoleAssignmentPolicy(me, req.Roles, isCreate: true);

        // Scope theo role của người thao tác
        if (RoleGuard.IsAdmin(me))
        {
            // Admin được search/view, và CHỈ tạo SystemAdmin
            // (unit ROOT có còn bắt buộc không? bệ hạ không nhắc lại -> bỏ ràng buộc ROOT)
        }
        else if (RoleGuard.IsSystemAdmin(me))
        {
            // full scope, ok
        }
        else if (RoleGuard.TryGetManagerUnit(me, out var managedUnitId))
        {
            if (!string.Equals(req.UnitId, managedUnitId, StringComparison.Ordinal))
                throw new BadHttpRequestException("MANAGER_UNIT can only create users in its own unit.");
        }
        else if (RoleGuard.IsManagerLevel(me))
        {
            var meUnit = await ResolveManagerLevelScopeAsync(me, ct);
            EnsureManagerLevelScope(meUnit, (targetUnit.Code, targetUnit.Level));
        }
        else
        {
            throw new BadHttpRequestException("Not allowed.");
        }

        var pc = PositionAdminService.NormalizeOptionalCode(req.PositionCode);
        await _positions.ValidatePositionForUnitTypeAsync(pc, targetUnit.PrimaryUnitTypeCode, ct);

        // Insert user
        var now = DateTime.UtcNow;
        var u = new AppUser
        {
            Username = username,
            FullName = req.FullName.Trim(),
            UnitId = req.UnitId,
            Roles = req.Roles ?? new(),
            PositionCode = pc,
            IsDeleted = false,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        u.PasswordHash = _hasher.HashPassword(u, req.Password);

        await _ctx.Users.InsertOneAsync(u, cancellationToken: ct);
        var positionName = await GetPositionNameAsync(pc, ct);
        return ToResp(u, targetUnit.Symbol, targetUnit.ShortName, targetUnit.Code, positionName);
    }

    public async Task<UserResponse> UpdateAsync(string userId, UpdateUserRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();

        // ✅ thay vì chặn ADMIN thẳng, dùng guard target (đúng rule mới)
        var targetUser = await _ctx.Users.Find(x => x.Id == userId && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("User not found.");

        // ✅ guard target: ADMIN được manage SYSTEM_ADMIN (nhưng không đụng ADMIN, không đụng SYS ngang cấp nếu sysadmin,...)
        RequireCanManageUser(me, targetUser);
        await EnsureScopeForTargetAsync(me, targetUser, ct);

        var targetUnit = await RequireUnitAsync(targetUser.UnitId, ct);
        EnsureUserBearingUnit(targetUnit);

        var now = DateTime.UtcNow;
        var pc = PositionAdminService.NormalizeOptionalCode(req.PositionCode);
        await _positions.ValidatePositionForUnitTypeAsync(pc, targetUnit.PrimaryUnitTypeCode, ct);
        var update = Builders<AppUser>.Update
            .Set(x => x.FullName, req.FullName.Trim())
            .Set(x => x.Note, string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim())
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.PositionCode, pc)
            .Set(x => x.UpdatedAtUtc, now);

        // ✅ username optional
        if (!string.IsNullOrWhiteSpace(req.Username))
        {
            var username = NormalizeUsername(req.Username);
            var exists = await _ctx.Users.Find(x => x.Username == username && x.Id != userId && !x.IsDeleted).AnyAsync(ct);
            if (exists) throw new InvalidOperationException("Username exists.");

            update = update.Set(x => x.Username, username);
        }

        // ✅ roles only if client sends it
        if (req.Roles is not null)
        {
            EnforceRoleAssignmentPolicy(me, req.Roles, isCreate: false); // ✅ lúc này ADMIN sẽ bị chặn update roles, nhưng không ảnh hưởng update fullname/username nữa
            update = update.Set(x => x.Roles, req.Roles);
        }

        var after = await _ctx.Users.FindOneAndUpdateAsync(
            x => x.Id == userId && !x.IsDeleted,
            update,
            new FindOneAndUpdateOptions<AppUser> { ReturnDocument = ReturnDocument.After },
            ct);

        if (after is null) throw new InvalidOperationException("User not found.");

        await _auth.RevokeUserSessionsAsync(userId, ct);
        var positionName = await GetPositionNameAsync(pc, ct);
        return ToResp(after, targetUnit.Symbol, targetUnit.ShortName, targetUnit.Code, positionName);
    }

    public async Task ResetPasswordAsync(string userId, ResetPasswordRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();

        var user = await _ctx.Users.Find(x => x.Id == userId).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("User not found.");

        if (user.IsDeleted)
            throw new InvalidOperationException("Cannot reset password for a deleted user.");

        // ✅ guard target role theo rule mới
        RequireCanManageUser(me, user);

        // ✅ scope theo manager (ADMIN/SYS full)
        await EnsureScopeForTargetAsync(me, user, ct);

        if (RoleGuard.IsSystemAdmin(me) || RoleGuard.IsAdmin(me))
        {
            // ok
        }
        else if (RoleGuard.TryGetManagerUnit(me, out var managedUnitId))
        {
            if (!string.Equals(user.UnitId, managedUnitId, StringComparison.Ordinal))
                throw new BadHttpRequestException("MANAGER_UNIT scope.");
        }
        else if (RoleGuard.IsManagerLevel(me))
        {
            // Scope was checked by EnsureScopeForTargetAsync above.
        }
        else throw new BadHttpRequestException("Not allowed.");

        user.PasswordHash = _hasher.HashPassword(user, req.NewPassword);

        var now = DateTime.UtcNow;
        var update = Builders<AppUser>.Update
            .Set(x => x.PasswordHash, user.PasswordHash)
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now);

        var rs = await _ctx.Users.UpdateOneAsync(
            x => x.Id == userId && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (rs.MatchedCount == 0)
            throw new InvalidOperationException("User not found.");

        await _auth.RevokeUserSessionsAsync(userId, ct);
    }
    public async Task<PagedResult<UserSearchRow>> SearchUsersAsync(
       string? q,
       bool? isDeleted,
       string? unitCodePrefix, 
       string? positionCode,
       int page,
       int pageSize,
       string? sortField,
       string? sortDirection,
       CancellationToken ct)
    {
        var me = _me.RequireMe();

        page = Math.Max(0, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var skip = page * pageSize;

        // ====== base filter on Users (trước lookup) ======
        var userMatch = Builders<AppUser>.Filter.Empty;

        if (!string.IsNullOrWhiteSpace(q))
        {
            var qq = q.Trim();
            userMatch &= Builders<AppUser>.Filter.Or(
                Builders<AppUser>.Filter.Regex(x => x.Username, new BsonRegularExpression(Regex.Escape(qq), "i")),
                Builders<AppUser>.Filter.Regex(x => x.FullName, new BsonRegularExpression(Regex.Escape(qq), "i"))
            );
        }

        userMatch &= Builders<AppUser>.Filter.Eq(x => x.IsDeleted, isDeleted ?? false);
        if (!string.IsNullOrWhiteSpace(positionCode))
        {
            var pc = PositionAdminService.NormalizeOptionalCode(positionCode);
            var exists = await _ctx.Positions.Find(x => x.Code == pc && !x.IsDeleted).AnyAsync(ct);
            if (!exists)
                throw new InvalidOperationException("Invalid positionCode.");

            userMatch &= Builders<AppUser>.Filter.Eq(x => x.PositionCode, pc);
        }


        // ====== role scope ======
        var isAdmin = RoleGuard.IsAdmin(me);
        var isSys = RoleGuard.IsSystemAdmin(me);

        var isManagerUnit = RoleGuard.TryGetManagerUnit(me, out var managerUnitId);
        var isManagerLevel = RoleGuard.IsManagerLevel(me);

        if (!isAdmin && !isSys && !isManagerUnit && !isManagerLevel)
            throw new BadHttpRequestException("Not allowed.");

        if (isManagerUnit)
            userMatch &= Builders<AppUser>.Filter.Eq(x => x.UnitId, managerUnitId);

        // ====== MANAGER_LEVEL scope ======
        ManagerLevelScope? managerLevelScope = null;

        if (isManagerLevel)
        {
            managerLevelScope = await ResolveManagerLevelScopeAsync(me, ct);
        }

        // ====== build sort (whitelist) ======
        var sort = BuildUserSearchSort(sortField, sortDirection);

        // ====== aggregate with lookup + facet ======
        var unitsCol = _ctx.Units.CollectionNamespace.CollectionName;
        var positionsCol = _ctx.Positions.CollectionNamespace.CollectionName;

        var lookupStage = new BsonDocument("$lookup", new BsonDocument
        {
            { "from", unitsCol },
            { "let", new BsonDocument("uid", "$unitId") }, // ✅ field thực trong Users collection
            { "pipeline", new BsonArray
                {
                    new BsonDocument("$match", new BsonDocument("$expr",
                        // Keep historical unit labels for old/deactivated data. New-data flows still reject deleted units.
                        new BsonDocument("$eq", new BsonArray
                        {
                            "$_id",
                            new BsonDocument("$toObjectId", "$$uid")
                        })
                    )),
                    new BsonDocument("$project", new BsonDocument
                    {
                        { "code", 1 },
                        { "shortName", 1 },
                        { "symbol", 1 },
                        { "level", 1 }
                    })
                }
            },
            { "as", "unit" }
        });

        var unwindStage = new BsonDocument("$unwind", new BsonDocument
        {
            { "path", "$unit" },
            { "preserveNullAndEmptyArrays", true }
        });

        var positionLookupStage = new BsonDocument("$lookup", new BsonDocument
        {
            { "from", positionsCol },
            { "let", new BsonDocument("pc", "$positionCode") },
            { "pipeline", new BsonArray
                {
                    new BsonDocument("$match", new BsonDocument("$expr",
                        new BsonDocument("$and", new BsonArray
                        {
                            new BsonDocument("$eq", new BsonArray { "$code", "$$pc" }),
                            new BsonDocument("$eq", new BsonArray { "$isDeleted", false })
                        })
                    )),
                    new BsonDocument("$project", new BsonDocument
                    {
                        { "name", 1 },
                        { "order", 1 },
                        { "rank", 1 }
                    })
                }
            },
            { "as", "position" }
        });

        var unwindPositionStage = new BsonDocument("$unwind", new BsonDocument
        {
            { "path", "$position" },
            { "preserveNullAndEmptyArrays", true }
        });

        var pipeline = _ctx.Users.Aggregate()
            .Match(userMatch)
            .AppendStage<BsonDocument>(lookupStage)
            .AppendStage<BsonDocument>(unwindStage)
            .AppendStage<BsonDocument>(positionLookupStage)
            .AppendStage<BsonDocument>(unwindPositionStage);

        // ✅ Filter theo unitCodePrefix (UI chọn 1 mã đơn vị cha)
        if (!string.IsNullOrWhiteSpace(unitCodePrefix))
        {
            var escaped = Regex.Escape(unitCodePrefix.Trim());
            pipeline = pipeline.AppendStage<BsonDocument>(
                new BsonDocument("$match", new BsonDocument
                {
                // ⚠️ camelCase đúng với lookup field bạn đang dùng
                { "unit.code", new BsonDocument("$regex", "^" + escaped) }
                })
            );
        }

        // ✅ MANAGER_LEVEL scope: filter by unit.code prefix & unit.level >= meLevel
        if (managerLevelScope is not null)
        {
            var scopeDoc = new BsonDocument
            {
                { "unit.level", new BsonDocument("$gte", managerLevelScope.Level) }
            };

            if (!managerLevelScope.IsLevelWide)
            {
                var escaped = Regex.Escape(managerLevelScope.UnitCode ?? "");
                scopeDoc.Add("unit.code", new BsonDocument("$regex", "^" + escaped));
            }

            var unitScopeMatch = new BsonDocument("$match", scopeDoc);

            pipeline = pipeline.AppendStage<BsonDocument>(unitScopeMatch);
        }

        // Project: include unitCode for sort + roles for FE
        var project = new BsonDocument("$project", new BsonDocument
        {
            { "_id", 0 },
            { "Id", new BsonDocument("$toString", "$_id") },
            { "Username", "$username" },
            { "FullName", "$fullName" },
            { "UnitId", new BsonDocument("$toString", "$unitId") },
            { "IsDeleted", "$isDeleted" },

            { "UnitShortName", new BsonDocument("$ifNull", new BsonArray { "$unit.shortName", "" }) },
            { "UnitSymbol", new BsonDocument("$ifNull", new BsonArray { "$unit.symbol", "" }) },

            // hidden sort field
            { "_unitCode", new BsonDocument("$ifNull", new BsonArray { "$unit.code", "" }) },

            // allow sort by createdAt/updatedAt
            { "CreatedAtUtc", "$createdAtUtc" },
            { "UpdatedAtUtc", "$updatedAtUtc" },

            // roles for FE disable buttons
            { "Roles", new BsonDocument("$ifNull", new BsonArray { "$roles", new BsonArray() }) },

            // position
            { "PositionCode", new BsonDocument("$ifNull", new BsonArray { "$positionCode", "" }) },
            { "PositionName", new BsonDocument("$ifNull", new BsonArray { "$position.name", "" }) },

            // computed order for sorting by position
            { "_posOrder", new BsonDocument("$ifNull", new BsonArray { "$position.order", 9999 }) },
        });

        pipeline = pipeline.AppendStage<BsonDocument>(project);

        var facet = new BsonDocument("$facet", new BsonDocument
    {
        {
            "rows",
            new BsonArray
            {
                new BsonDocument("$sort", sort),
                new BsonDocument("$skip", skip),
                new BsonDocument("$limit", pageSize)
            }
        },
        {
            "total",
            new BsonArray
            {
                new BsonDocument("$count", "value")
            }
        }
    });

        var faceted = await pipeline.AppendStage<BsonDocument>(facet).FirstOrDefaultAsync(ct);

        var rowsDoc = faceted?["rows"].AsBsonArray ?? new BsonArray();
        var totalDoc = faceted?["total"].AsBsonArray ?? new BsonArray();
        var total = totalDoc.Count > 0 ? totalDoc[0]["value"].ToInt64() : 0L;

        var rows = rowsDoc
            .Select(x => x.AsBsonDocument)
            .Select(d => new UserSearchRow(
                Id: d.GetValue("Id", "").AsString,
                Username: d.GetValue("Username", "").AsString,
                FullName: d.GetValue("FullName", "").AsString,
                UnitId: d.GetValue("UnitId", "").AsString,
                UnitShortName: d.GetValue("UnitShortName", "").AsString,
                UnitSymbol: d.GetValue("UnitSymbol", "").AsString,
                UnitCode: d.GetValue("_unitCode", "").AsString,
                IsDeleted: d.GetValue("IsDeleted", false).ToBoolean(),
                PositionCode: d.GetValue("PositionCode", "").AsString,
                PositionName: d.GetValue("PositionName", "").AsString,
                Roles: d.TryGetValue("Roles", out var rv) && rv.IsBsonArray
                    ? rv.AsBsonArray
                        .Select(x => x.IsString ? x.AsString : x.ToString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x!)
                        .ToList()
                    : new List<string>()
            ))
            .ToList();

        return new PagedResult<UserSearchRow>(rows, total, page, pageSize);
    }

    /// <summary>
    /// Sort whitelist + default: _unitCode asc, Username asc.
    /// FE có thể request sortField.
    /// </summary>
    private static BsonDocument BuildUserSearchSort(string? sortField, string? sortDirection)
    {
        var desc = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);
        sortField = (sortField ?? "").Trim();

        // default: unitCode asc, username asc
        if (string.IsNullOrWhiteSpace(sortField))
            return new BsonDocument { { "_unitCode", 1 }, { "Username", 1 } };

        return sortField switch
        {
            // sort by unit-related (cho phép)
            "unit" or "unitShortName" => new BsonDocument { { "UnitShortName", desc ? -1 : 1 }, { "Username", 1 } },
            "unitSymbol" => new BsonDocument { { "UnitSymbol", desc ? -1 : 1 }, { "Username", 1 } },

            // sort by user fields
            "username" => new BsonDocument { { "Username", desc ? -1 : 1 }, { "_unitCode", 1 } },
            "fullName" => new BsonDocument { { "FullName", desc ? -1 : 1 }, { "_unitCode", 1 } },
            "createdAtUtc" => new BsonDocument { { "CreatedAtUtc", desc ? -1 : 1 }, { "_unitCode", 1 } },
            "updatedAtUtc" => new BsonDocument { { "UpdatedAtUtc", desc ? -1 : 1 }, { "_unitCode", 1 } },
            "isDeleted" => new BsonDocument { { "IsDeleted", desc ? -1 : 1 }, { "_unitCode", 1 } },
            "positionCode" => new BsonDocument { { "_posOrder", desc ? -1 : 1 }, { "_unitCode", 1 }, { "Username", 1 } },

            // fallback default
            _ => new BsonDocument { { "_unitCode", 1 }, { "Username", 1 } }
        };
    }
    public async Task SoftDeleteAsync(string userId, CancellationToken ct)
    {
        var me = _me.RequireMe();

        var user = await _ctx.Users.Find(x => x.Id == userId).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("User not found.");

        if (user.IsDeleted) return;

        // ✅ guard target role theo rule mới
        RequireCanManageUser(me, user);

        // ✅ scope theo manager (ADMIN/SYS full)
        await EnsureScopeForTargetAsync(me, user, ct);

        // optional: chặn tự xóa mình (tuỳ bệ hạ)
        // if (string.Equals(me.Id, userId, StringComparison.Ordinal))
        //     throw new InvalidOperationException("Cannot delete yourself.");

        var now = DateTime.UtcNow;

        var update = Builders<AppUser>.Update
            .Set(x => x.IsDeleted, true)
            .Set(x => x.DeletedAtUtc, now)
            .Set(x => x.DeletedByUserId, me.Id)
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.UpdatedAtUtc, now);

        var rs = await _ctx.Users.UpdateOneAsync(
            x => x.Id == userId && !x.IsDeleted,
            update,
            cancellationToken: ct);

        if (rs.MatchedCount == 0)
            throw new InvalidOperationException("User not found.");

        await _auth.RevokeUserSessionsAsync(userId, ct);
    }

    private async Task<string?> GetPositionNameAsync(string? positionCode, CancellationToken ct)
    {
        var pc = PositionAdminService.NormalizeOptionalCode(positionCode);
        if (string.IsNullOrWhiteSpace(pc)) return null;

        return await _ctx.Positions
            .Find(x => x.Code == pc && !x.IsDeleted)
            .Project(x => x.Name)
            .FirstOrDefaultAsync(ct);
    }
}
