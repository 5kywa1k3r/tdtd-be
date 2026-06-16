using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Pickers;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Labels;
using tdtd_be.DTOs.Pickers;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Services;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/pickers")]
[Authorize]
public sealed class PickersController : ControllerBase
{
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly WorkAssignmentTargetScopePolicy _targetScopePolicy;
    private readonly ILabelEnumCatalogService _labelEnumCatalogs;

    public PickersController(
        MongoDbContext ctx,
        MeAccessor me,
        WorkAssignmentTargetScopePolicy targetScopePolicy,
        ILabelEnumCatalogService labelEnumCatalogs)
    {
        _ctx = ctx;
        _me = me;
        _targetScopePolicy = targetScopePolicy;
        _labelEnumCatalogs = labelEnumCatalogs;
    }

    private static FilterDefinition<Unit> ExcludeHiddenRoot(FilterDefinitionBuilder<Unit> fb)
        => fb.And(
            fb.Ne(x => x.Code, null),
            fb.Ne(x => x.Code, ""),
            fb.Ne(x => x.Code, "ROOT"),
            fb.Ne(x => x.FullName, "ROOT"),
            fb.Ne(x => x.FullName, "ROOT UNIT"),
            fb.Ne(x => x.ShortName, "ROOT"),
            fb.Ne(x => x.ShortName, "ROOT UNIT"),
            fb.Ne(x => x.Symbol, "ROOT"),
            fb.Ne(x => x.Symbol, "ROOT UNIT")
        );

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

    private static bool IsHiddenRootUnit(UnitPickRow row)
        => IsRootCodeToken(row.Code) ||
           (string.IsNullOrWhiteSpace(row.ParentId) &&
            (IsRootNameToken(row.FullName) ||
             IsRootNameToken(row.ShortName) ||
             IsRootNameToken(row.Symbol)));

    // =========================
    // UNITS: children (node cha -> node con)
    // =========================
    // ✅ OPEN: không chặn theo level nữa
    // Sort: shortName (tạm), sau này thay bằng order
    [HttpGet("units/children")]
    public async Task<ActionResult<List<UnitPickRow>>> GetUnitChildren(
        [FromQuery] string? parentId,
        CancellationToken ct = default)
    {
        _ = _me.RequireMe(); // đảm bảo có auth, hiện tại không dùng scope

        // normalize parentId (tránh trường hợp FE gửi "null" hoặc "")
        var pid = (parentId ?? "").Trim();
        if (string.Equals(pid, "null", StringComparison.OrdinalIgnoreCase)) pid = "";

        if (string.IsNullOrWhiteSpace(pid))
        {
            var roots = await _ctx.Units.Find(x => !x.IsDeleted && x.ParentUnitId == null)
                .SortBy(x => x.ShortName).ThenBy(x => x.Code)
                .Project(x => new UnitPickRow
                {
                    Id = x.Id,
                    Code = x.Code ?? "",
                    FullName = x.FullName,
                    ShortName = x.ShortName,
                    Symbol = x.Symbol,
                    Level = x.Level,
                    ParentId = x.ParentUnitId,
                    IsVirtual = x.IsVirtual
                })
                .ToListAsync(ct);

            var hiddenRootIds = roots
                .Where(IsHiddenRootUnit)
                .Select(x => x.Id)
                .ToList();

            var rootRows = roots
                .Where(x => !IsHiddenRootUnit(x))
                .ToList();

            if (hiddenRootIds.Count > 0)
            {
                var hiddenRootChildren = await _ctx.Units
                    .Find(x => !x.IsDeleted && x.ParentUnitId != null && hiddenRootIds.Contains(x.ParentUnitId))
                    .SortBy(x => x.ShortName).ThenBy(x => x.Code)
                    .Project(x => new UnitPickRow
                    {
                        Id = x.Id,
                        Code = x.Code ?? "",
                        FullName = x.FullName,
                        ShortName = x.ShortName,
                        Symbol = x.Symbol,
                        Level = x.Level,
                        ParentId = x.ParentUnitId,
                        IsVirtual = x.IsVirtual
                    })
                    .ToListAsync(ct);

                rootRows.AddRange(hiddenRootChildren.Where(x => !IsHiddenRootUnit(x)));
            }

            return Ok(rootRows
                .OrderBy(x => x.ShortName)
                .ThenBy(x => x.Code)
                .ToList());
        }

        var fb = Builders<Unit>.Filter;
        var filter = fb.And(fb.Eq(x => x.IsDeleted, false), ExcludeHiddenRoot(fb), fb.Eq(x => x.ParentUnitId, pid));

        var rows = await _ctx.Units.Find(filter)
            .SortBy(x => x.ShortName).ThenBy(x => x.Code)
            .Project(x => new UnitPickRow
            {
                Id = x.Id,
                Code = x.Code ?? "",
                FullName = x.FullName,
                ShortName = x.ShortName,
                Symbol = x.Symbol,
                Level = x.Level,
                ParentId = x.ParentUnitId,
                IsVirtual = x.IsVirtual
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    // =========================
    // UNITS: search nhanh theo CODE
    // =========================
    // ✅ OPEN: không chặn theo level nữa
    // ✅ chỉ search code (không search name)
    [HttpGet("units/search")]
    public async Task<ActionResult<PagedResult<UnitPickRow>>> SearchUnitsByCode(
        [FromQuery] string? code,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        _ = _me.RequireMe();

        page = Math.Max(0, page);
        pageSize = PickQueryHelper.ClampPageSize(pageSize, 20, 50);

        var fb = Builders<Unit>.Filter;
        var filter = fb.And(fb.Eq(x => x.IsDeleted, false), ExcludeHiddenRoot(fb));

        if (!string.IsNullOrWhiteSpace(code))
        {
            var s = code.Trim();
            // chỉ code
            filter = fb.And(filter, fb.Regex(x => x.Code, new BsonRegularExpression(s, "i")));
        }

        var total = await _ctx.Units.CountDocumentsAsync(filter, cancellationToken: ct);

        var rows = await _ctx.Units.Find(filter)
            .SortBy(x => x.ShortName).ThenBy(x => x.Code)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new UnitPickRow
            {
                Id = x.Id,
                Code = x.Code ?? "",
                FullName = x.FullName,
                ShortName = x.ShortName,
                Symbol = x.Symbol,
                Level = x.Level,
                ParentId = x.ParentUnitId,
                IsVirtual = x.IsVirtual
            })
            .ToListAsync(ct);

        return Ok(new PagedResult<UnitPickRow>(
            rows: rows,
            total: total,
            page: page,
            pageSize: pageSize
        ));
    }

    [HttpGet("users/search")]
    public async Task<ActionResult<PagedResult<UserPickRow>>> SearchUsers(
        [FromQuery] string? q,
        [FromQuery] string? unitId,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        _ = _me.RequireMe();

        page = Math.Max(0, page);
        pageSize = PickQueryHelper.ClampPageSize(pageSize, 20, 50);

        var fb = Builders<AppUser>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);

        if (!string.IsNullOrWhiteSpace(unitId))
            filter = fb.And(filter, fb.Eq(x => x.UnitId, unitId.Trim()));

        if (!string.IsNullOrWhiteSpace(q))
        {
            var s = Regex.Escape(q.Trim());
            var rx = new BsonRegularExpression(s, "i");
            filter = fb.And(filter, fb.Or(fb.Regex(x => x.Username, rx), fb.Regex(x => x.FullName, rx)));
        }

        var total = await _ctx.Users.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.Users.Find(filter)
            .SortBy(x => x.Username)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new UserPickRow
            {
                Id = x.Id,
                Username = x.Username,
                FullName = x.FullName,
                UnitId = x.UnitId,
                PositionCode = x.PositionCode
            })
            .ToListAsync(ct);

        return Ok(new PagedResult<UserPickRow>(
            rows: rows,
            total: total,
            page: page,
            pageSize: pageSize
        ));
    }

    [HttpGet("positions/search")]
    public async Task<ActionResult<PagedResult<CatalogPickRow>>> SearchPositions(
        [FromQuery] string? q,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        _ = _me.RequireMe();

        page = Math.Max(0, page);
        pageSize = PickQueryHelper.ClampPageSize(pageSize, 20, 50);

        var fb = Builders<Position>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var s = Regex.Escape(q.Trim());
            var rx = new BsonRegularExpression(s, "i");
            filter = fb.And(filter, fb.Or(fb.Regex(x => x.Code, rx), fb.Regex(x => x.Name, rx)));
        }

        var total = await _ctx.Positions.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.Positions.Find(filter)
            .SortBy(x => x.Order)
            .ThenBy(x => x.Code)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new CatalogPickRow
            {
                Id = x.Id,
                Code = x.Code,
                Name = x.Name
            })
            .ToListAsync(ct);

        return Ok(new PagedResult<CatalogPickRow>(rows, total, page, pageSize));
    }

    [HttpGet("unit-types/search")]
    public async Task<ActionResult<PagedResult<CatalogPickRow>>> SearchUnitTypes(
        [FromQuery] string? q,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        _ = _me.RequireMe();

        page = Math.Max(0, page);
        pageSize = PickQueryHelper.ClampPageSize(pageSize, 20, 50);

        var fb = Builders<UnitType>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var s = Regex.Escape(q.Trim());
            var rx = new BsonRegularExpression(s, "i");
            filter = fb.And(filter, fb.Or(fb.Regex(x => x.Code, rx), fb.Regex(x => x.Name, rx)));
        }

        var total = await _ctx.UnitTypes.CountDocumentsAsync(filter, cancellationToken: ct);
        var rows = await _ctx.UnitTypes.Find(filter)
            .SortBy(x => x.Code)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new CatalogPickRow
            {
                Id = x.Id,
                Code = x.Code,
                Name = x.Name
            })
            .ToListAsync(ct);

        return Ok(new PagedResult<CatalogPickRow>(rows, total, page, pageSize));
    }

    [HttpGet("label-enums/{catalogId}/options/search")]
    public async Task<ActionResult<PagedResult<LabelEnumOptionPickRow>>> SearchLabelEnumOptions(
        [FromRoute] string catalogId,
        [FromQuery] string? q,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _labelEnumCatalogs.SearchOptionsAsync(catalogId, q, page, pageSize, ct);
        return Ok(result);
    }

    // =========================
    // LEADERS: by unit (PositionCode ∈ Positions.KnownCodes)
    // =========================
    // ✅ không chặn cấp, không chặn scope, chỉ cần thuộc unit + có enum chức vụ
    // ✅ search username only
    [HttpGet("leaders/by-unit")]
    public async Task<ActionResult<PagedResult<UserPickRow>>> SearchLeadersByUnit(
        [FromQuery] string unitId,
        [FromQuery] string? username,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        _ = _me.RequireMe();

        if (string.IsNullOrWhiteSpace(unitId))
            return BadRequest("unitId is required.");

        page = Math.Max(0, page);
        pageSize = PickQueryHelper.ClampPageSize(pageSize, 20, 50);

        var selectedUnitId = unitId.Trim();

        var fb = Builders<AppUser>.Filter;
        var filter = fb.And(
            fb.Eq(x => x.IsDeleted, false),
            fb.Eq(x => x.UnitId, selectedUnitId),
            fb.In(x => x.PositionCode, Positions.KnownCodes)
        );

        if (!string.IsNullOrWhiteSpace(username))
        {
            var s = username.Trim().ToLowerInvariant();
            // username đã lowercase, dùng regex i cho tiện (có thể đổi contains nếu muốn)
            filter = fb.And(filter, fb.Regex(x => x.Username, new BsonRegularExpression(s, "i")));
        }

        var total = await _ctx.Users.CountDocumentsAsync(filter, cancellationToken: ct);

        var rows = await _ctx.Users.Find(filter)
            .SortBy(x => x.Username)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new UserPickRow
            {
                Id = x.Id,
                Username = x.Username,
                FullName = x.FullName,
                UnitId = x.UnitId,
                PositionCode = x.PositionCode
            })
            .ToListAsync(ct);

        return Ok(new PagedResult<UserPickRow>(
            rows: rows,
            total: total,
            page: page,
            pageSize: pageSize
        ));
    }

    // lookup nhanh theo username (leaders)
    [HttpGet("leaders/lookup")]
    public async Task<ActionResult<UserPickRow?>> LookupLeaderByUsername(
        [FromQuery] string username,
        [FromQuery] string? unitId,
        CancellationToken ct = default)
    {
        _ = _me.RequireMe();

        username = (username ?? "").Trim().ToLowerInvariant();
        if (username.Length == 0) return BadRequest("username is required.");

        var fb = Builders<AppUser>.Filter;
        var filter = fb.And(
            fb.Eq(x => x.IsDeleted, false),
            fb.Eq(x => x.Username, username),
            fb.In(x => x.PositionCode, Positions.KnownCodes)
        );

        if (!string.IsNullOrWhiteSpace(unitId))
        {
            filter = fb.And(filter, fb.Eq(x => x.UnitId, unitId.Trim()));
        }

        var u = await _ctx.Users.Find(filter)
            .Project(x => new UserPickRow
            {
                Id = x.Id,
                Username = x.Username,
                FullName = x.FullName,
                UnitId = x.UnitId,
                PositionCode = x.PositionCode
            })
            .FirstOrDefaultAsync(ct);

        return Ok(u);
    }

    // =========================
    // ASSIGNEES: by unit.
    // Assignment target picker mirrors assignment-create target rules for mu_* accounts:
    // - normal flow assigns to units, which later resolve to mu_* accounts;
    // - direct user picking is only for final units with no assignable child units;
    // - peer/subordinate unit coordination exposes the target unit's mu_* account only.
    // =========================
    // ✅ search username only
    [HttpGet("assignees/by-unit")]
    public async Task<ActionResult<PagedResult<UserPickRow>>> SearchAssigneesByUnit(
        [FromQuery] string unitId,
        [FromQuery] string? username,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var meLevel = PickQueryHelper.GetUnitLevel(me.UnitCode);

        if (string.IsNullOrWhiteSpace(unitId))
            return BadRequest("unitId is required.");

        page = Math.Max(0, page);
        pageSize = PickQueryHelper.ClampPageSize(pageSize, 20, 50);

        var selectedUnitId = unitId.Trim();
        var selectedUnit = await _ctx.Units
            .Find(x => x.Id == selectedUnitId && !x.IsDeleted && x.Level >= meLevel)
            .FirstOrDefaultAsync(ct);

        if (selectedUnit is null)
            return EmptyUserPickPage(page, pageSize);

        var fb = Builders<AppUser>.Filter;
        var filter = fb.And(
            fb.Eq(x => x.IsDeleted, false),
            fb.Eq(x => x.UnitId, selectedUnitId)
        );

        if (IsUnitManager(me))
        {
            var actorUnit = await LoadActorUnitAsync(me, ct);
            if (actorUnit is null)
                return EmptyUserPickPage(page, pageSize);

            var actorUnitHasAssignableDescendants = await HasAssignableDescendantUnitAsync(actorUnit, ct);
            var assignmentTargetFilter = BuildUnitManagerAssignmentAssigneeFilter(
                fb,
                me,
                actorUnit,
                selectedUnit,
                actorUnitHasAssignableDescendants,
                _targetScopePolicy);

            if (assignmentTargetFilter is null)
                return EmptyUserPickPage(page, pageSize);

            filter = fb.And(filter, assignmentTargetFilter);
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            var s = username.Trim().ToLowerInvariant();
            filter = fb.And(filter, fb.Regex(x => x.Username, new BsonRegularExpression(s, "i")));
        }

        var total = await _ctx.Users.CountDocumentsAsync(filter, cancellationToken: ct);

        var rows = await _ctx.Users.Find(filter)
            .SortBy(x => x.Username)
            .Skip(page * pageSize)
            .Limit(pageSize)
            .Project(x => new UserPickRow
            {
                Id = x.Id,
                Username = x.Username,
                FullName = x.FullName,
                UnitId = x.UnitId,
                UnitCode = selectedUnit.Code,
                UnitShortName = selectedUnit.ShortName,
                UnitSymbol = selectedUnit.Symbol,
                PositionCode = x.PositionCode
            })
            .ToListAsync(ct);

        return Ok(new PagedResult<UserPickRow>(
            rows: rows,
            total: total,
            page: page,
            pageSize: pageSize
        ));
    }

    [HttpGet("assignees/lookup")]
    public async Task<ActionResult<UserPickRow?>> LookupAssigneeByUsername(
        [FromQuery] string username,
        [FromQuery] string? unitId,
        CancellationToken ct = default)
    {
        var me = _me.RequireMe();
        var meLevel = PickQueryHelper.GetUnitLevel(me.UnitCode);

        username = (username ?? "").Trim().ToLowerInvariant();
        if (username.Length == 0) return BadRequest("username is required.");

        var fb = Builders<AppUser>.Filter;
        var filter = fb.And(
            fb.Eq(x => x.IsDeleted, false),
            fb.Eq(x => x.Username, username)
        );

        if (!string.IsNullOrWhiteSpace(unitId))
        {
            filter = fb.And(filter, fb.Eq(x => x.UnitId, unitId.Trim()));
        }

        var u = await _ctx.Users.Find(filter).FirstOrDefaultAsync(ct);
        if (u is null || string.IsNullOrWhiteSpace(u.UnitId)) return Ok(null);

        var unit = await _ctx.Units.Find(x => x.Id == u.UnitId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);

        if (unit is null || unit.Level < meLevel) return Ok(null);

        if (IsUnitManager(me))
        {
            var actorUnit = await LoadActorUnitAsync(me, ct);
            if (actorUnit is null) return Ok(null);

            var actorUnitHasAssignableDescendants = await HasAssignableDescendantUnitAsync(actorUnit, ct);
            if (!IsAllowedUnitManagerAssignmentTarget(
                    me,
                    actorUnit,
                    u,
                    unit,
                    actorUnitHasAssignableDescendants,
                    _targetScopePolicy))
            {
                return Ok(null);
            }
        }

        return Ok(new UserPickRow
        {
            Id = u.Id,
            Username = u.Username,
            FullName = u.FullName,
            UnitId = u.UnitId,
            UnitCode = unit.Code,
            UnitShortName = unit.ShortName,
            UnitSymbol = unit.Symbol,
            PositionCode = u.PositionCode
        });
    }

    private ActionResult<PagedResult<UserPickRow>> EmptyUserPickPage(int page, int pageSize)
        => Ok(new PagedResult<UserPickRow>(
            rows: new(),
            total: 0,
            page: page,
            pageSize: pageSize));

    private async Task<Unit?> LoadActorUnitAsync(MeResponse me, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(me.UnitId))
            return null;

        return await _ctx.Units
            .Find(x => x.Id == me.UnitId && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<bool> HasAssignableDescendantUnitAsync(Unit actorUnit, CancellationToken ct)
    {
        var fb = Builders<Unit>.Filter;
        var filter = fb.Eq(x => x.IsDeleted, false)
                     & fb.Eq(x => x.IsVirtual, false)
                     & fb.Ne(x => x.Id, actorUnit.Id);

        var code = actorUnit.Code?.Trim();
        if (!string.IsNullOrWhiteSpace(code))
        {
            filter &= fb.Regex(x => x.Code, new BsonRegularExpression("^" + Regex.Escape(code)));
        }
        else
        {
            filter &= fb.Eq(x => x.ParentUnitId, actorUnit.Id);
        }

        return await _ctx.Units
            .Find(filter)
            .Limit(1)
            .AnyAsync(ct);
    }

    private static FilterDefinition<AppUser>? BuildUnitManagerAssignmentAssigneeFilter(
        FilterDefinitionBuilder<AppUser> fb,
        MeResponse me,
        Unit actorUnit,
        Unit selectedUnit,
        bool actorUnitHasAssignableDescendants,
        WorkAssignmentTargetScopePolicy targetScopePolicy)
    {
        if (string.Equals(actorUnit.Id, selectedUnit.Id, StringComparison.Ordinal))
        {
            if (actorUnitHasAssignableDescendants)
                return null;

            return fb.And(
                NormalUserAccountFilter(fb),
                fb.Ne(x => x.Id, me.Id));
        }

        if (IsPeerUnit(actorUnit, selectedUnit) || IsDescendantUnit(actorUnit, selectedUnit))
        {
            return fb.And(
                UnitManagerAccountFilter(fb),
                fb.Ne(x => x.Id, me.Id));
        }

        var configuredFilters = new List<FilterDefinition<AppUser>>();
        if (targetScopePolicy.AllowsConfiguredTargetAccountKind(
                actorUnit,
                selectedUnit,
                ManagementAccountKind.UnitManager))
        {
            configuredFilters.Add(UnitManagerAccountFilter(fb));
        }

        if (targetScopePolicy.AllowsConfiguredTargetAccountKind(
                actorUnit,
                selectedUnit,
                ManagementAccountKind.NormalUser))
        {
            configuredFilters.Add(NormalUserAccountFilter(fb));
        }

        if (configuredFilters.Count > 0)
        {
            return fb.And(
                configuredFilters.Count == 1 ? configuredFilters[0] : fb.Or(configuredFilters),
                fb.Ne(x => x.Id, me.Id));
        }

        return null;
    }

    private static bool IsAllowedUnitManagerAssignmentTarget(
        MeResponse me,
        Unit actorUnit,
        AppUser targetUser,
        Unit targetUnit,
        bool actorUnitHasAssignableDescendants,
        WorkAssignmentTargetScopePolicy targetScopePolicy)
    {
        if (string.Equals(me.Id, targetUser.Id, StringComparison.Ordinal))
            return false;

        if (IsUnitManager(targetUser))
            return IsPeerUnit(actorUnit, targetUnit) ||
                   IsDescendantUnit(actorUnit, targetUnit) ||
                   targetScopePolicy.AllowsConfiguredTarget(actorUnit, targetUnit, targetUser);

        if (IsNormalUser(targetUser))
        {
            return (!actorUnitHasAssignableDescendants &&
                    string.Equals(actorUnit.Id, targetUnit.Id, StringComparison.Ordinal)) ||
                   targetScopePolicy.AllowsConfiguredTarget(actorUnit, targetUnit, targetUser);
        }

        return false;
    }

    private static FilterDefinition<AppUser> UnitManagerAccountFilter(FilterDefinitionBuilder<AppUser> fb)
        => fb.Or(
            fb.Eq(x => x.AccountKind, ManagementAccountKind.UnitManager),
            fb.Regex(x => x.Username, new BsonRegularExpression("^" + Regex.Escape(ManagementAccountConvention.UnitManagerPrefix), "i")));

    private static FilterDefinition<AppUser> NormalUserAccountFilter(FilterDefinitionBuilder<AppUser> fb)
        => fb.And(
            fb.Or(
                fb.Eq(x => x.AccountKind, null),
                fb.Eq(x => x.AccountKind, string.Empty),
                fb.Eq(x => x.AccountKind, ManagementAccountKind.NormalUser)),
            fb.Not(fb.Regex(x => x.Username, new BsonRegularExpression("^(mu_|ml_)", "i"))));

    private static bool IsUnitManager(MeResponse me)
        => string.Equals(me.AccountKind, ManagementAccountKind.UnitManager, StringComparison.OrdinalIgnoreCase) ||
           (me.Username ?? string.Empty).StartsWith(ManagementAccountConvention.UnitManagerPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnitManager(AppUser user)
        => string.Equals(user.AccountKind, ManagementAccountKind.UnitManager, StringComparison.OrdinalIgnoreCase) ||
           (user.Username ?? string.Empty).StartsWith(ManagementAccountConvention.UnitManagerPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsLevelManager(AppUser user)
        => string.Equals(user.AccountKind, ManagementAccountKind.LevelManager, StringComparison.OrdinalIgnoreCase) ||
           (user.Username ?? string.Empty).StartsWith(ManagementAccountConvention.LevelManagerPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsNormalUser(AppUser user)
        => !IsUnitManager(user) &&
           !IsLevelManager(user) &&
           (string.IsNullOrWhiteSpace(user.AccountKind) ||
            string.Equals(user.AccountKind, ManagementAccountKind.NormalUser, StringComparison.OrdinalIgnoreCase));

    private static bool IsPeerUnit(Unit actorUnit, Unit targetUnit)
    {
        if (string.Equals(actorUnit.Id, targetUnit.Id, StringComparison.Ordinal))
            return false;

        return actorUnit.Level == targetUnit.Level &&
               string.Equals(actorUnit.ParentUnitId ?? string.Empty, targetUnit.ParentUnitId ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool IsDescendantUnit(Unit actorUnit, Unit targetUnit)
    {
        if (string.Equals(actorUnit.Id, targetUnit.Id, StringComparison.Ordinal))
            return false;

        var actorCode = actorUnit.Code?.Trim();
        var targetCode = targetUnit.Code?.Trim();
        if (!string.IsNullOrWhiteSpace(actorCode) &&
            !string.IsNullOrWhiteSpace(targetCode) &&
            targetUnit.Level > actorUnit.Level &&
            targetCode.StartsWith(actorCode, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(targetUnit.ParentUnitId, actorUnit.Id, StringComparison.Ordinal);
    }
}
