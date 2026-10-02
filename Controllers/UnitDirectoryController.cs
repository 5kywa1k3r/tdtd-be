using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Pickers;
using tdtd_be.Data;
using tdtd_be.DTOs.Pickers;
using tdtd_be.Models;

namespace tdtd_be.Controllers;

[ApiController, Authorize, Route("api/pickers/unit-directory")]
public sealed class UnitDirectoryController(MongoDbContext db, MeAccessor current) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var me = current.RequireMe();
        var actor = await db.Users.Find(x => x.Id == me.Id && !x.IsDeleted)
            .Project(x => new { x.UnitId }).FirstOrDefaultAsync(ct);
        if (actor is null) return Unauthorized();
        var unit = await db.Units.Find(x => x.Id == actor.UnitId && !x.IsDeleted)
            .Project(x => new { x.Id, x.Code }).FirstOrDefaultAsync(ct);
        var prefix = UnitCodeDirectory.ParentPrefix(unit?.Code);
        // The explicit system root has no numeric parent. Ordinary malformed codes
        // must never turn into an unbounded query.
        if (prefix is null && (RoleGuard.IsSystemAdmin(me) || RoleGuard.IsAdmin(me)
            || RoleGuard.TryGetGeneratedLevelManager(me, out var level) && level == 0)) prefix = "";
        if (prefix is null) return BadRequest("Mã đơn vị của tài khoản chưa đúng cấu trúc ba chữ số mỗi cấp.");
        var filter = Builders<Unit>.Filter.Eq(x => x.IsDeleted, false)
            & Builders<Unit>.Filter.Regex(x => x.Code, new BsonRegularExpression(UnitCodeDirectory.Pattern(prefix)));
        var rows = await db.Units.Find(filter).SortBy(x => x.Code)
            .Project(x => new UnitPickRow {
                Id = x.Id, Code = x.Code!, FullName = x.FullName, ShortName = x.ShortName,
                Symbol = x.Symbol, Level = x.Level, ParentId = x.ParentUnitId,
                IsVirtual = x.IsVirtual, PrimaryUnitTypeCode = x.PrimaryUnitTypeCode, Selectable = true
            }).ToListAsync(ct);
        // A unit directory is not a report/account grant. Command endpoints retain
        // their Work, handover and assignment checks.
        return Ok(new { rows, actorUnitId = unit?.Id, prefix });
    }
}
