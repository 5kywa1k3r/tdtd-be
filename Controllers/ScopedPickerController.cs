using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Pickers;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments;
using tdtd_be.Services.Works;

namespace tdtd_be.Controllers;

[ApiController, Authorize, Route("api/pickers/scoped")]
public sealed class ScopedPickerController : ControllerBase
{
    private readonly MongoDbContext _ctx;
    private readonly MeAccessor _me;
    private readonly IWorkPermissionService _workPermission;
    private readonly IWorkAssignmentService _assignments;

    public ScopedPickerController(MongoDbContext ctx, MeAccessor me,
        IWorkPermissionService workPermission, IWorkAssignmentService assignments)
        => (_ctx, _me, _workPermission, _assignments) = (ctx, me, workPermission, assignments);

    // Read-only POST: avoids ambiguous array query encoding and URL length limits.
    [HttpPost("query")]
    public async Task<IActionResult> Query([FromBody] ScopedPickerRequest request, CancellationToken ct)
    {
        if (request.Context is null || request.Context.Purpose is null
            || !System.Enum.IsDefined(typeof(PickerPurpose), request.Context.Purpose.Value)
            || request.Operation is not ("children" or "units" or "directory" or "users" or "lookup" or "restoreUsers" or "restoreUnits" or "expandUnits")
            || (request.Ids?.Count ?? 0) > 2000)
            return BadRequest("Ngữ cảnh bộ chọn không hợp lệ.");
        var context = request.Context;
        var ids = (request.Ids ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
        if (ids.Any(x => !ObjectId.TryParse(x, out _))
            || !ValidOptionalId(request.ParentId) || !ValidOptionalId(request.UnitId))
            return BadRequest("ID bộ chọn không hợp lệ.");

        List<Unit> units;
        List<AppUser> users;
        var selectableUnits = new HashSet<string>(StringComparer.Ordinal);
        AssignmentPickerScope? assignmentScope = null;
        if (context.Purpose == PickerPurpose.WorkLeaders)
        {
            if (!ValidOptionalId(context.WorkId) || !string.IsNullOrWhiteSpace(context.ParentAssignmentId)
                || (context.AssigneeUnitIds?.Count ?? 0) != 0 || (context.AssigneeUserIds?.Count ?? 0) != 0)
                return BadRequest("Ngữ cảnh lãnh đạo công việc không hợp lệ.");
            var me = _me.RequireMe();
            var actor = await _ctx.Users.Find(x => x.Id == me.Id && !x.IsDeleted).FirstOrDefaultAsync(ct);
            if (actor is null) return Forbid();
            units = await _ctx.Units.Find(x => !x.IsDeleted).ToListAsync(ct);
            var actorUnit = units.FirstOrDefault(x => x.Id == actor.UnitId);
            if (string.IsNullOrWhiteSpace(context.WorkId))
                _workPermission.EnsureCanCreateRoot(new MeResponse(actor.Id, actor.Username, actor.FullName,
                    (actorUnit?.UnitTypeCodes ?? new()).Append(actorUnit?.PrimaryUnitTypeCode ?? "").Distinct().ToList(), actor.UnitId ?? "", actorUnit?.Symbol, actorUnit?.FullName,
                    actorUnit?.Code, actor.Roles, actor.PositionCode, actor.IsDeleted, actor.AccountKind));
            else
            {
                if (!await _ctx.Works.Find(x => x.Id == context.WorkId && !x.IsDeleted).AnyAsync(ct)) return NotFound();
                await _workPermission.EnsureCanUpdateRootAsync(context.WorkId, actor.Id, ct);
            }
            users = await _ctx.Users.Find(x => !x.IsDeleted && Positions.KnownCodes.Contains(x.PositionCode!))
                .Project(x => new AppUser { Id = x.Id, Username = x.Username, FullName = x.FullName,
                    UnitId = x.UnitId, PositionCode = x.PositionCode }).ToListAsync(ct);
            var liveUnits = units.Select(x => x.Id).ToHashSet();
            users = users.Where(x => x.UnitId != null && liveUnits.Contains(x.UnitId)).ToList();
        }
        else
        {
            var scope = await _assignments.GetPickerScopeAsync(context, ct, request);
            assignmentScope = scope;
            (units, users, selectableUnits) = (scope.Units, scope.Users, scope.SelectableUnitIds);
        }

        var page = Math.Max(0, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 50);
        var q = (request.Q ?? "").Trim();
        if (request.Operation is "users" or "lookup" or "restoreUsers")
        {
            IEnumerable<AppUser> matches = users;
            if (!string.IsNullOrWhiteSpace(request.UnitId)) matches = matches.Where(x => x.UnitId == request.UnitId.Trim());
            if (request.Operation == "restoreUsers") matches = matches.Where(x => ids.Contains(x.Id));
            else if (request.Operation == "lookup") matches = matches.Where(x => string.Equals(x.Username, q, StringComparison.OrdinalIgnoreCase));
            else if (q.Length > 0) matches = matches.Where(x => Contains(x.Username, q) || Contains(x.FullName, q));
            var found = matches.OrderBy(x => x.Username, StringComparer.Ordinal).ToList();
            var slice = request.Operation == "restoreUsers" || assignmentScope?.UserTotalRows is not null
                ? found : found.Skip((int)Math.Min((long)page * size, int.MaxValue)).Take(size);
            var unitMap = units.ToDictionary(x => x.Id);
            return Ok(new { rows = slice.Select(x => new UserPickRow {
                Id = x.Id, Username = x.Username, FullName = x.FullName, UnitId = x.UnitId, PositionCode = x.PositionCode,
                UnitSymbol = x.UnitId != null && unitMap.TryGetValue(x.UnitId, out var u) ? u.Symbol : null,
                UnitCode = x.UnitId != null && unitMap.TryGetValue(x.UnitId, out var codeUnit) ? codeUnit.Code : null,
                UnitShortName = x.UnitId != null && unitMap.TryGetValue(x.UnitId, out var nameUnit) ? nameUnit.ShortName ?? nameUnit.FullName : null,
                PositionName = Positions.GetName(x.PositionCode),
                Selectable = assignmentScope?.UserConflicts.ContainsKey(x.Id) != true,
                ConflictUnitId = assignmentScope?.UserConflicts.GetValueOrDefault(x.Id)?.Id,
                ConflictUnitName = assignmentScope?.UserConflicts.GetValueOrDefault(x.Id) is { } covering ? covering.ShortName ?? covering.FullName : null
            }), totalRows = assignmentScope?.UserTotalRows ?? found.Count, page, pageSize = size });
        }

        // Navigation ancestors are visible but only selectableUnits can become unit recipients.
        var visible = users.Where(x => x.UnitId != null).Select(x => x.UnitId!).Concat(selectableUnits).ToHashSet();
        var byId = units.ToDictionary(x => x.Id);
        foreach (var id in visible.ToList())
        {
            var seen = new HashSet<string>();
            var current = id;
            while (byId.TryGetValue(current, out var u) && !string.IsNullOrWhiteSpace(u.ParentUnitId) && seen.Add(current))
            {
                current = u.ParentUnitId;
                visible.Add(current);
            }
        }
        var navigable = units.Where(x => visible.Contains(x.Id) && !Hidden(x)).ToList();
        IEnumerable<Unit> unitMatches = navigable;
        if (request.Operation == "expandUnits")
        {
            // Read-only expansion: the whole group must be selectable; never silently
            // trim forbidden children from a request that means "all units in this group".
            if (ids.Any(id => !selectableUnits.Contains(id)))
                return BadRequest("Không xác minh được toàn bộ đơn vị trong nhóm đã chọn.");
            var selected = units.Where(x => ids.Contains(x.Id)).ToList();
            unitMatches = navigable.Where(x => !x.IsVirtual && selectableUnits.Contains(x.Id)
                && selected.Any(s => s.IsVirtual
                      ? UnitManagementScope.Contains(s.Code, x.Code, false)
                    : s.Id == x.Id));
        }
        else if (request.Operation == "children")
            unitMatches = string.IsNullOrWhiteSpace(request.ParentId)
                ? navigable.Where(x => string.IsNullOrWhiteSpace(x.ParentUnitId) || !byId.TryGetValue(x.ParentUnitId, out var p) || Hidden(p))
                : navigable.Where(x => x.ParentUnitId == request.ParentId.Trim());
        else if (request.Operation == "restoreUnits")
            unitMatches = navigable.Where(x => ids.Contains(x.Id) && selectableUnits.Contains(x.Id));
        else if (q.Length > 0)
            unitMatches = navigable.Where(x => Contains(x.FullName, q) || Contains(x.ShortName, q) || Contains(x.Symbol, q) || Contains(x.Code, q));
        var foundUnits = unitMatches.OrderBy(x => x.ShortName).ThenBy(x => x.Code).ToList();
        var unitSlice = request.Operation == "units" ? foundUnits.Skip((int)Math.Min((long)page * size, int.MaxValue)).Take(size) : foundUnits;
        return Ok(new { rows = unitSlice.Select(x => new UnitPickRow {
            Id = x.Id, Code = x.Code ?? "", FullName = x.FullName, ShortName = x.ShortName, Symbol = x.Symbol,
            Level = x.Level, ParentId = x.ParentUnitId, IsVirtual = x.IsVirtual, PrimaryUnitTypeCode = x.PrimaryUnitTypeCode,
            Selectable = selectableUnits.Contains(x.Id)
        }), totalRows = foundUnits.Count, page, pageSize = size });
    }

    private static bool ValidOptionalId(string? id) => string.IsNullOrWhiteSpace(id) || ObjectId.TryParse(id, out _);
    private static bool Contains(string? value, string q) => value?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
    private static bool Hidden(Unit unit) => string.IsNullOrWhiteSpace(unit.Code) || unit.Code == "ROOT"
        || new[] { unit.FullName, unit.ShortName, unit.Symbol }.Any(x => x is "ROOT" or "ROOT UNIT");
}
