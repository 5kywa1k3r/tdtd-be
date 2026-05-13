using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
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
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly IPositionAdminService _positions;

    public UserAdminService(
        MongoDbContext ctx,
        MeAccessor me,
        UnitTreeHelper tree,
        AuthService auth,
        IPasswordHasher<AppUser> hasher,
        IPositionAdminService positions)
    {
        _ctx = ctx; _me = me; _tree = tree; _auth = auth; _hasher = hasher; _positions = positions;
    }

    private static string NormalizeUsername(string s) => (s ?? "").Trim().ToLowerInvariant();

    private static UserResponse ToResp(
        AppUser u,
        string? unitSymbol,
        string? unitName,
        string? unitCode,
        string? positionName = null) => new(
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
                throw UserAdminRoleForbidden("assignAdmin", new { roles });
    }

    private static bool ContainsRole(IEnumerable<string>? roles, string role)
        => roles?.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)) == true;

    private static List<string> NormalizeAssignedRoles(IEnumerable<string>? roles)
    {
        var normalized = new List<string>();

        foreach (var raw in roles ?? Array.Empty<string>())
        {
            var role = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(role))
                continue;

            var canonical = role;
            if (string.Equals(role, Roles.ADMIN, StringComparison.OrdinalIgnoreCase))
                canonical = Roles.ADMIN;
            else if (string.Equals(role, Roles.SYSTEM_ADMIN, StringComparison.OrdinalIgnoreCase))
                canonical = Roles.SYSTEM_ADMIN;
            else if (string.Equals(role, Roles.MANAGER_LEVEL, StringComparison.OrdinalIgnoreCase))
                canonical = Roles.MANAGER_LEVEL;
            else if (Roles.IsManagerUnit(role, out var unitId))
                canonical = Roles.ManagerUnit(unitId);

            if (!ContainsRole(normalized, canonical))
                normalized.Add(canonical);
        }

        return normalized;
    }

    private static List<string> BuildPersistedRoles(IEnumerable<string>? roles, bool isSystemAdmin)
    {
        if (isSystemAdmin)
            return new List<string> { Roles.SYSTEM_ADMIN };

        return NormalizeAssignedRoles(roles);
    }

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
    private static bool IsSystemAdminUser(AppUser u)
        => HasRole(u, Roles.SYSTEM_ADMIN) ||
           string.Equals(u.AccountKind, ManagementAccountKind.SystemAdmin, StringComparison.OrdinalIgnoreCase);
    private static bool IsSystemAdminRequest(IEnumerable<string>? roles)
        => ContainsRole(roles, Roles.SYSTEM_ADMIN);

    private static void RequireCanManageUser(MeResponse me, AppUser target)
    {
        var meIsAdmin = RoleGuard.IsAdmin(me);
        var meIsSys = RoleGuard.IsSystemAdmin(me);
        var meIsMgrLevel = RoleGuard.IsManagerLevel(me);
        var meIsMgrUnit = RoleGuard.TryGetManagerUnit(me, out _);

        // không ai được đụng ADMIN
        if (IsAdminUser(target))
            throw UserAdminManageForbidden("targetIsAdmin", me.Id, target.Id);

        // ADMIN: chỉ quản SYSTEM_ADMIN
        if (meIsAdmin)
        {
            if (!IsSystemAdminUser(target))
                throw UserAdminManageForbidden("adminCanOnlyManageSystemAdmin", me.Id, target.Id);
            return;
        }

        // SYSTEM_ADMIN: không đụng SYSTEM_ADMIN khác
        if (meIsSys)
        {
            if (IsSystemAdminUser(target) && target.Id != me.Id)
                throw UserAdminManageForbidden("systemAdminCannotManageOtherSystemAdmin", me.Id, target.Id);
            return;
        }

        // MANAGER_UNIT: không đụng SYSTEM_ADMIN
        if (meIsMgrUnit)
        {
            if (IsSystemAdminUser(target))
                throw UserAdminManageForbidden("managerUnitCannotManageSystemAdmin", me.Id, target.Id);
            return;
        }

        // MANAGER_LEVEL: không đụng SYSTEM_ADMIN
        if (meIsMgrLevel)
        {
            if (IsSystemAdminUser(target))
                throw UserAdminManageForbidden("managerLevelCannotManageSystemAdmin", me.Id, target.Id);
            return;
        }

        throw UserAdminManageForbidden("actorRoleNotAllowed", me.Id, target.Id);
    }

    // scope cho manager (unit/subtree). SYS/ADMIN coi như full scope
    private async Task EnsureScopeForTargetAsync(MeResponse me, AppUser targetUser, CancellationToken ct)
    {
        if (RoleGuard.IsAdmin(me) || RoleGuard.IsSystemAdmin(me))
            return;

        if (RoleGuard.TryGetManagerUnit(me, out var managedUnitId))
        {
            if (!string.Equals(targetUser.UnitId, managedUnitId, StringComparison.Ordinal))
                throw UserAdminScopeForbidden("managerUnitScope", new { actorUserId = me.Id, targetUserId = targetUser.Id, targetUser.UnitId, managedUnitId });
            return;
        }

        if (RoleGuard.IsManagerLevel(me))
        {
            var meUnit = await ResolveManagerLevelScopeAsync(me, ct);
            var targetUnit = await RequireUnitAsync(targetUser.UnitId, ct);
            EnsureManagerLevelScope(meUnit, (targetUnit.Code, targetUnit.Level));
            return;
        }

        throw UserAdminScopeForbidden("actorRoleNotAllowed", new { actorUserId = me.Id, targetUserId = targetUser.Id });
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
            if (!isCreate)
                throw UserAdminRoleForbidden("adminCannotUpdateRoles", new { me.Id, targetRoles });

            // ✅ ADMIN chỉ tạo đúng SYSTEM_ADMIN (không kèm role khác)
            var list = (targetRoles ?? Array.Empty<string>())
                .Select(x => (x ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (list.Count != 1 || !string.Equals(list[0], Roles.SYSTEM_ADMIN, StringComparison.OrdinalIgnoreCase))
                throw UserAdminRoleForbidden("adminCanOnlyCreateSystemAdmin", new { me.Id, roles = list });

            if (wantManagerRoles)
                throw UserAdminRoleForbidden("adminCannotAssignManagerRoles", new { me.Id, roles = list });
            return;
        }

        if (RoleGuard.IsSystemAdmin(me))
        {
            // SYSTEM_ADMIN không được tạo SYSTEM_ADMIN (vì chỉ ADMIN tạo)
            if (wantSystemAdmin)
                throw UserAdminRoleForbidden("onlyAdminCanCreateSystemAdmin", new { me.Id, targetRoles });
            // SYSTEM_ADMIN được gán MANAGER_LEVEL / MANAGER_UNIT:* OK
            return;
        }

        // Managers:
        if (wantSystemAdmin)
            throw UserAdminRoleForbidden("managerCannotAssignSystemAdmin", new { me.Id, targetRoles });
        if (wantManagerRoles)
            throw UserAdminRoleForbidden("onlySystemAdminCanAssignManagerRoles", new { me.Id, targetRoles });
    }

    private async Task<Unit> RequireUnitAsync(string? unitId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(unitId))
            throw UserAdminUnitNotFound(unitId);

        var u = await _ctx.Units.Find(x => x.Id == unitId).FirstOrDefaultAsync(ct);
        if (u is null)
            throw UserAdminUnitNotFound(unitId);
        return u;
    }

    private static void EnsureUserBearingUnit(Unit unit)
    {
        if (unit.IsVirtual)
            throw UserAdminUnitInvalid(unit.Id, "virtualUnit");
    }

    private static void EnsureUnitUsableForNewUser(Unit unit)
    {
        if (unit.IsDeleted)
            throw UserAdminUnitInvalid(unit.Id, "deletedUnit");
        EnsureUserBearingUnit(unit);
    }

    private static bool IsInSubtree(string meCode, string targetCode)
        => !string.IsNullOrWhiteSpace(meCode)
           && !string.IsNullOrWhiteSpace(targetCode)
           && targetCode.StartsWith(meCode, StringComparison.Ordinal);

    private sealed record ManagerLevelScope(string? UnitCode, int Level, bool IsLevelWide);

    private static bool TryParseGeneratedLevelManager(MeResponse me, out int level)
        => RoleGuard.TryGetGeneratedLevelManager(me, out level);

    private async Task<ManagerLevelScope> ResolveManagerLevelScopeAsync(MeResponse me, CancellationToken ct)
    {
        if (!RoleGuard.IsManagerLevel(me))
            throw UserAdminScopeForbidden("managerLevelRequired", new { me.Id });

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

        throw UserAdminUnitNotFound(me.UnitId);
    }

    private static void EnsureManagerLevelScope((string meCode, int meLevel) me, (string targetCode, int targetLevel) target)
    {
        if (!IsInSubtree(me.meCode, target.targetCode))
            throw UserAdminScopeForbidden("outsideManagerSubtree", new { me.meCode, target.targetCode });
        if (target.targetLevel < me.meLevel)
            throw UserAdminScopeForbidden("upperLevel", new { me.meLevel, target.targetLevel });
    }

    private static void EnsureManagerLevelScope(ManagerLevelScope scope, (string targetCode, int targetLevel) target)
    {
        if (scope.IsLevelWide)
        {
            if (target.targetLevel < scope.Level)
                throw UserAdminScopeForbidden("upperLevel", new { scope.Level, target.targetLevel });
            return;
        }

        EnsureManagerLevelScope((scope.UnitCode ?? string.Empty, scope.Level), target);
    }
    public async Task<UserResponse> GetByIdAsync(string userId, CancellationToken ct)
    {
        var me = _me.RequireMe();

        var u = await _ctx.Users.Find(x => x.Id == userId && !x.IsDeleted).FirstOrDefaultAsync(ct);
        if (u is null)
            throw UserAdminNotFound(userId);
        var targetUnit = string.IsNullOrWhiteSpace(u.UnitId)
            ? null
            : await _ctx.Units.Find(x => x.Id == u.UnitId)
                .Project(x => new { x.Symbol, x.ShortName, x.Code, x.Level, x.PrimaryUnitTypeCode })
                .FirstOrDefaultAsync(ct);

        var positionName = await GetPositionNameAsync(u.PositionCode, ct);

        // Allowed: ADMIN, SYSTEM_ADMIN, MANAGER_UNIT, MANAGER_LEVEL
        if (RoleGuard.IsAdmin(me) || RoleGuard.IsSystemAdmin(me))
            return ToResp(u, targetUnit?.Symbol, targetUnit?.ShortName, targetUnit?.Code, positionName);

        if (RoleGuard.TryGetManagerUnit(me, out var managedUnitId))
        {
            if (!string.Equals(u.UnitId, managedUnitId, StringComparison.Ordinal))
                throw UserAdminScopeForbidden("managerUnitScope", new { me.Id, userId, u.UnitId, managedUnitId });
            if (targetUnit is null)
                throw UserAdminScopeForbidden("targetUnitMissing", new { userId, u.UnitId });
            return ToResp(u, targetUnit.Symbol, targetUnit.ShortName, targetUnit.Code, positionName);
        }

        if (RoleGuard.IsManagerLevel(me))
        {
            if (targetUnit is null)
                throw UserAdminScopeForbidden("targetUnitMissing", new { userId, u.UnitId });

            var meUnit = await ResolveManagerLevelScopeAsync(me, ct);
            EnsureManagerLevelScope(meUnit, (targetUnit.Code, targetUnit.Level));

            return ToResp(u, targetUnit.Symbol, targetUnit.ShortName, targetUnit.Code, positionName);
        }

        throw UserAdminManageForbidden("actorRoleNotAllowed", me.Id, userId);
    }

    public async Task<UserResponse> CreateAsync(CreateUserRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();

        var username = NormalizeUsername(req.Username);
        if (await _ctx.Users.Find(x => x.Username == username && !x.IsDeleted).AnyAsync(ct))
            throw UsernameDuplicate(username);

        var normalizedRoles = NormalizeAssignedRoles(req.Roles);
        // enforce role assignment
        EnforceRoleAssignmentPolicy(me, normalizedRoles, isCreate: true);
        var createSystemAdmin = IsSystemAdminRequest(normalizedRoles);
        var persistedRoles = BuildPersistedRoles(normalizedRoles, createSystemAdmin);

        Unit? targetUnit = null;
        if (!createSystemAdmin)
        {
            targetUnit = await RequireUnitAsync(req.UnitId, ct);
            EnsureUnitUsableForNewUser(targetUnit);
        }

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
                throw UserAdminScopeForbidden("managerUnitCreateScope", new { me.Id, req.UnitId, managedUnitId });
        }
        else if (RoleGuard.IsManagerLevel(me))
        {
            var meUnit = await ResolveManagerLevelScopeAsync(me, ct);
            EnsureManagerLevelScope(meUnit, (targetUnit!.Code, targetUnit.Level));
        }
        else
        {
            throw UserAdminManageForbidden("actorRoleNotAllowed", me.Id, targetUserId: null);
        }

        var pc = createSystemAdmin ? null : PositionAdminService.NormalizeOptionalCode(req.PositionCode);
        if (!createSystemAdmin)
            await _positions.ValidatePositionForUnitTypeAsync(
                pc,
                targetUnit!.PrimaryUnitTypeCode,
                ct,
                targetUnit.Id);

        // Insert user
        var now = DateTime.UtcNow;
        var u = new AppUser
        {
            Username = username,
            FullName = req.FullName.Trim(),
            UnitId = createSystemAdmin ? null : req.UnitId,
            Roles = persistedRoles,
            PositionCode = pc,
            AccountKind = createSystemAdmin ? ManagementAccountKind.SystemAdmin : null,
            IsDeleted = false,
            CreatedByUserId = me.Id,
            UpdatedByUserId = me.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        u.PasswordHash = _hasher.HashPassword(u, req.Password);

        await _ctx.Users.InsertOneAsync(u, cancellationToken: ct);
        var positionName = await GetPositionNameAsync(pc, ct);
        return ToResp(u, targetUnit?.Symbol, targetUnit?.ShortName, targetUnit?.Code, positionName);
    }

    public async Task<UserResponse> UpdateAsync(string userId, UpdateUserRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();

        // ✅ thay vì chặn ADMIN thẳng, dùng guard target (đúng rule mới)
        var targetUser = await _ctx.Users.Find(x => x.Id == userId && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw UserAdminNotFound(userId);

        // ✅ guard target: ADMIN được manage SYSTEM_ADMIN (nhưng không đụng ADMIN, không đụng SYS ngang cấp nếu sysadmin,...)
        RequireCanManageUser(me, targetUser);
        await EnsureScopeForTargetAsync(me, targetUser, ct);

        var now = DateTime.UtcNow;
        Unit? targetUnit = null;
        var targetIsSystemAdmin = IsSystemAdminUser(targetUser)
                                  || string.Equals(targetUser.AccountKind, ManagementAccountKind.SystemAdmin, StringComparison.OrdinalIgnoreCase);
        if (!targetIsSystemAdmin)
        {
            targetUnit = await RequireUnitAsync(targetUser.UnitId, ct);
            EnsureUserBearingUnit(targetUnit);
        }

        var pc = targetIsSystemAdmin ? null : PositionAdminService.NormalizeOptionalCode(req.PositionCode);
        if (!targetIsSystemAdmin)
            await _positions.ValidatePositionForUnitTypeAsync(
                pc,
                targetUnit!.PrimaryUnitTypeCode,
                ct,
                targetUnit.Id,
                targetUser.Id);
        var fullName = req.FullName?.Trim();
        if (string.IsNullOrWhiteSpace(fullName))
            throw AppExceptionFactory.BadRequest(AppErrorCode.USER_ADMIN_FULL_NAME_REQUIRED, new { field = "fullName" });

        var update = Builders<AppUser>.Update
            .Set(x => x.FullName, fullName)
            .Set(x => x.Note, string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim())
            .Set(x => x.UpdatedByUserId, me.Id)
            .Set(x => x.PositionCode, pc)
            .Set(x => x.AccountKind, targetIsSystemAdmin ? ManagementAccountKind.SystemAdmin : targetUser.AccountKind)
            .Set(x => x.UpdatedAtUtc, now);

        // ✅ username optional
        if (!string.IsNullOrWhiteSpace(req.Username))
        {
            var username = NormalizeUsername(req.Username);
            var exists = await _ctx.Users.Find(x => x.Username == username && x.Id != userId && !x.IsDeleted).AnyAsync(ct);
            if (exists)
                throw UsernameDuplicate(username);

            update = update.Set(x => x.Username, username);
        }

        // ✅ roles only if client sends it
        if (req.Roles is not null)
        {
            var normalizedRoles = NormalizeAssignedRoles(req.Roles);
            EnforceRoleAssignmentPolicy(me, normalizedRoles, isCreate: false); // ✅ lúc này ADMIN sẽ bị chặn update roles, nhưng không ảnh hưởng update fullname/username nữa
            update = update.Set(x => x.Roles, BuildPersistedRoles(normalizedRoles, targetIsSystemAdmin));
        }
        else if (targetIsSystemAdmin && !ContainsRole(targetUser.Roles, Roles.SYSTEM_ADMIN))
        {
            update = update.Set(x => x.Roles, BuildPersistedRoles(targetUser.Roles, isSystemAdmin: true));
        }

        var after = await _ctx.Users.FindOneAndUpdateAsync(
            x => x.Id == userId && !x.IsDeleted,
            update,
            new FindOneAndUpdateOptions<AppUser> { ReturnDocument = ReturnDocument.After },
            ct);

        if (after is null)
            throw UserAdminNotFound(userId);

        await _auth.RevokeUserSessionsAsync(userId, ct);
        var positionName = await GetPositionNameAsync(pc, ct);
        return ToResp(after, targetUnit?.Symbol, targetUnit?.ShortName, targetUnit?.Code, positionName);
    }

    public async Task ResetPasswordAsync(string userId, ResetPasswordRequest req, CancellationToken ct)
    {
        var me = _me.RequireMe();

        var user = await _ctx.Users.Find(x => x.Id == userId).FirstOrDefaultAsync(ct)
            ?? throw UserAdminNotFound(userId);

        if (user.IsDeleted)
            throw AppExceptionFactory.Forbidden(AppErrorCode.USER_ADMIN_PASSWORD_RESET_FORBIDDEN, new { userId, reason = "deletedUser" });

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
                throw UserAdminScopeForbidden("managerUnitScope", new { me.Id, userId, user.UnitId, managedUnitId });
        }
        else if (RoleGuard.IsManagerLevel(me))
        {
            // Scope was checked by EnsureScopeForTargetAsync above.
        }
        else
            throw UserAdminManageForbidden("actorRoleNotAllowed", me.Id, userId);

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
            throw UserAdminNotFound(userId);

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
                throw AppExceptionFactory.BadRequest(AppErrorCode.USER_ADMIN_POSITION_INVALID, new { positionCode = pc });

            userMatch &= Builders<AppUser>.Filter.Eq(x => x.PositionCode, pc);
        }


        // ====== role scope ======
        var isAdmin = RoleGuard.IsAdmin(me);
        var isSys = RoleGuard.IsSystemAdmin(me);

        var isManagerUnit = RoleGuard.TryGetManagerUnit(me, out var managerUnitId);
        var isManagerLevel = RoleGuard.IsManagerLevel(me);

        if (!isAdmin && !isSys && !isManagerUnit && !isManagerLevel)
            throw UserAdminManageForbidden("actorRoleNotAllowed", me.Id, targetUserId: null);

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
                            new BsonDocument("$convert", new BsonDocument
                            {
                                { "input", "$$uid" },
                                { "to", "objectId" },
                                { "onError", BsonNull.Value },
                                { "onNull", BsonNull.Value }
                            })
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
            { "UnitId", new BsonDocument("$ifNull", new BsonArray { new BsonDocument("$toString", "$unitId"), "" }) },
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
            { "AccountKind", new BsonDocument("$ifNull", new BsonArray { "$accountKind", "" }) },

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
                AccountKind: d.GetValue("AccountKind", "").AsString,
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
            ?? throw UserAdminNotFound(userId);

        if (user.IsDeleted) return;

        // ✅ guard target role theo rule mới
        RequireCanManageUser(me, user);

        // ✅ scope theo manager (ADMIN/SYS full)
        await EnsureScopeForTargetAsync(me, user, ct);

        // optional: chặn tự xóa mình (tuỳ bệ hạ)
        // if (string.Equals(me.Id, userId, StringComparison.Ordinal))
        //     throw UserAdminManageForbidden("selfDelete", me.Id, userId);

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
            throw UserAdminNotFound(userId);

        await _auth.RevokeUserSessionsAsync(userId, ct);
    }

    private static AppException UserAdminRoleForbidden(string reason, object? details = null)
        => AppExceptionFactory.Forbidden(AppErrorCode.USER_ADMIN_ROLE_FORBIDDEN, new { reason, details });

    private static AppException UserAdminManageForbidden(string reason, string? actorUserId, string? targetUserId)
        => AppExceptionFactory.Forbidden(AppErrorCode.USER_ADMIN_MANAGE_FORBIDDEN, new { reason, actorUserId, targetUserId });

    private static AppException UserAdminScopeForbidden(string reason, object? details = null)
        => AppExceptionFactory.Forbidden(AppErrorCode.USER_ADMIN_SCOPE_FORBIDDEN, new { reason, details });

    private static AppException UserAdminUnitNotFound(string? unitId)
        => AppExceptionFactory.NotFound(AppErrorCode.USER_ADMIN_UNIT_NOT_FOUND, new { unitId });

    private static AppException UserAdminUnitInvalid(string? unitId, string reason)
        => AppExceptionFactory.BadRequest(AppErrorCode.USER_ADMIN_UNIT_INVALID, new { unitId, reason });

    private static AppException UserAdminNotFound(string? userId)
        => AppExceptionFactory.NotFound(AppErrorCode.USER_ADMIN_NOT_FOUND, new { userId });

    private static AppException UsernameDuplicate(string username)
        => AppExceptionFactory.Create(AppErrorCode.USER_ADMIN_USERNAME_DUPLICATE, new { username });

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
