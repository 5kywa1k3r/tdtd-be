using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Pickers;
using tdtd_be.Data;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.Pickers;
using tdtd_be.Enum;
using tdtd_be.Models;

namespace tdtd_be.Controllers;

[ApiController]
[Route("api/pickers")]
[Authorize]
public sealed class PickersController : ControllerBase
{
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;

    public PickersController(MongoDbContext ctx, MeAccessor me)
    {
        _ctx = ctx;
        _me = me;
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

        var fb = Builders<Unit>.Filter;
        var filter = fb.And(fb.Eq(x => x.IsDeleted, false), ExcludeHiddenRoot(fb));

        if (string.IsNullOrWhiteSpace(pid))
        {
            // ✅ START NODE: trả "cấp gần nhất dưới ROOT"
            // Rule theo bệ hạ: ROOT code dài 3 => con trực tiếp code dài 6
            filter = fb.And(
                filter,
                fb.Ne(x => x.Code, null),
                fb.Where(x => x.Code.Length == 6)
            );
        }
        else
        {
            // ✅ CHILDREN of specific parent
            filter = fb.And(filter, fb.Eq(x => x.ParentUnitId, pid));
        }

        var rows = await _ctx.Units.Find(filter)
            .SortBy(x => x.ShortName).ThenBy(x => x.Code)
            .Project(x => new UnitPickRow
            {
                Id = x.Id,
                Code = x.Code,
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
                Code = x.Code,
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
    // ASSIGNEES: by unit (rule: unit.level >= meLevel)
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
        var allowedUnit = await _ctx.Units
            .Find(x => x.Id == selectedUnitId && !x.IsDeleted && x.Level >= meLevel)
            .Project(x => x.Id)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(allowedUnit))
        {
            return Ok(new PagedResult<UserPickRow>(
                rows: new(),
                total: 0,
                page: page,
                pageSize: pageSize
            ));
        }

        var fb = Builders<AppUser>.Filter;
        var filter = fb.And(
            fb.Eq(x => x.IsDeleted, false),
            fb.Eq(x => x.UnitId, selectedUnitId)
        );

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

        // enforce unit.level >= meLevel
        var unit = await _ctx.Units.Find(x => x.Id == u.UnitId && !x.IsDeleted)
            .Project(x => new { x.Level })
            .FirstOrDefaultAsync(ct);

        if (unit is null || unit.Level < meLevel) return Ok(null);

        return Ok(new UserPickRow
        {
            Id = u.Id,
            Username = u.Username,
            FullName = u.FullName,
            UnitId = u.UnitId,
            PositionCode = u.PositionCode
        });
    }
}
