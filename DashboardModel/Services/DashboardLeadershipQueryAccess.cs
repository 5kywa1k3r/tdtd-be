using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using System.Text.RegularExpressions;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;

namespace tdtd_be.DashboardModel.Services;

public sealed partial class DashboardOverviewService
{
    // Query authority contains keys/branch paths only, never the full list payload.
    private sealed record LeadershipQueryAccess(MeResponse Me, bool Global, BsonDocument Assignments,
        BsonDocument FullAssignments, List<string> WorkIds, List<string> UnitIds, string Label);

    private static BsonDocument RenderLeadershipFilter<T>(IMongoCollection<T> collection, FilterDefinition<T> filter)
        => filter.Render(new RenderArgs<T>(collection.DocumentSerializer, BsonSerializer.SerializerRegistry));

    private async Task<LeadershipQueryAccess> LoadLeadershipQueryAccessAsync(CancellationToken ct)
    {
        var me = await DashboardAuthorityReader.ReadAsync(_ctx, _me.RequireMe().Id, ct);
        if (DashboardAccessPolicy.HasGlobalReadAccess(me))
            return new(me, true, new(), new(), new(), new(), "Toàn hệ thống");

        var roles = await _ctx.DocRoles.Find(x => !x.IsDeleted && x.UserId == me.Id
            && (x.DocType == DocType.WORK || x.DocType == DocType.WORK_ASSIGNMENT))
            .Project(x => new DocRole { DocType = x.DocType, DocId = x.DocId, Role = x.Role }).ToListAsync(ct);
        var fullRoleIds = roles.Where(x => x.DocType == DocType.WORK && x.Role is DocRoleType.OWNER or DocRoleType.LEADER_DIRECTIVE or DocRoleType.LEADER_WATCH).Select(x => x.DocId).ToList();
        var roleAssignmentIds = roles.Where(x => x.DocType == DocType.WORK_ASSIGNMENT && x.Role is DocRoleType.ASSIGNEE or DocRoleType.ASSIGNER or DocRoleType.ASSIGNMENT_LEADER_WATCH).Select(x => x.DocId).ToList();
        var unitIds = new List<string>(); var userIds = new List<string>(); var label = "Nhiệm vụ, chỉ tiêu và nhánh được phép xem";
        if (DashboardUnitReadScope.IsUnitHead(me.PositionCode, me.UnitTypeCodes))
        {
            var unit = await _ctx.Units.Find(x => x.Id == me.UnitId && !x.IsDeleted && !x.IsVirtual).FirstOrDefaultAsync(ct);
            if (unit is not null && DashboardUnitReadScope.IsUnitHead(me.PositionCode, unit.UnitTypeCodes) && UnitManagementScope.Contains(unit.Code, unit.Code))
            {
                var units = await _ctx.Units.Find(Builders<Unit>.Filter.Eq(x => x.IsDeleted, false)
                    & Builders<Unit>.Filter.Eq(x => x.IsVirtual, false)
                    & Builders<Unit>.Filter.Regex(x => x.Code, new BsonRegularExpression("^" + Regex.Escape(unit.Code!))))
                    .Project(x => new Unit { Id = x.Id, Code = x.Code }).ToListAsync(ct);
                unitIds = units.Where(x => UnitManagementScope.Contains(unit.Code, x.Code)).Select(x => x.Id).ToList();
                userIds = await _ctx.Users.Find(x => !x.IsDeleted && unitIds.Contains(x.UnitId!)).Project(x => x.Id).ToListAsync(ct);
                label = unit.FullName + (unitIds.Count > 1 ? " và đơn vị trực thuộc" : "");
            }
        }
        var wf = Builders<Work>.Filter;
        var fullWorkFilter = wf.Eq(x => x.CreatedByUserId, me.Id) | wf.In(x => x.Id, fullRoleIds);
        if (unitIds.Count > 0) fullWorkFilter |= wf.In(x => x.Owner!.UnitId, unitIds)
            | ((wf.Eq(x => x.Owner, null) | wf.Eq(x => x.Owner!.UnitId, null)) & wf.In(x => x.CreatedByUserId, userIds));
        var fullWorkIds = await _ctx.Works.Find(wf.Eq(x => x.IsDeleted, false) & fullWorkFilter).Project(x => x.Id).ToListAsync(ct);
        var af = Builders<WorkAssignment>.Filter;
        var active = af.Eq(x => x.IsDeleted, false) & af.Eq(x => x.IsActive, true);
        var seedFilter = af.In(x => x.Id, roleAssignmentIds) | af.Eq(x => x.CreatedByUserId, me.Id)
            | af.ElemMatch(x => x.Assignees, x => x.UserId == me.Id);
        if (unitIds.Count > 0) seedFilter |= af.In(x => x.IssuedByUnitId, unitIds)
            | (af.Eq(x => x.IssuedByUnitId, null) & af.In(x => x.CreatedByUserId, userIds));
        // Full Work access already covers its branches; only read topology keys for other seeds.
        var seeds = await _ctx.WorkAssignments.Find(active & seedFilter & af.Nin(x => x.WorkId, fullWorkIds))
            .Project(x => new WorkAssignment { Id = x.Id, WorkId = x.WorkId, Path = x.Path }).ToListAsync(ct);
        var minimal = new List<WorkAssignment>();
        foreach (var seed in seeds.Where(x => !string.IsNullOrWhiteSpace(x.Path)).OrderBy(x => x.Path.Length))
            if (!minimal.Any(parent => DashboardUnitReadScope.InBranch(seed, parent))) minimal.Add(seed);
        var branches = minimal.Select(x => af.Eq(a => a.WorkId, x.WorkId)
            & af.Regex(a => a.Path, new BsonRegularExpression("^" + Regex.Escape(unitIds.Count > 0 ? x.Path : x.Path.TrimEnd('/')) + "(?:/|$)"))).ToList();
        branches.Add(af.In(x => x.Id, seeds.Select(x => x.Id)));
        branches.Add(af.In(x => x.WorkId, fullWorkIds));
        var full = active & af.Or(branches);
        var visible = full;
        if (unitIds.Count > 0) visible |= active & af.ElemMatch(x => x.Assignees, x => unitIds.Contains(x.UnitId!));
        return new(me, false, RenderLeadershipFilter(_ctx.WorkAssignments, visible), RenderLeadershipFilter(_ctx.WorkAssignments, full),
            roles.Where(x => x.DocType == DocType.WORK).Select(x => x.DocId).Concat(fullWorkIds).Distinct().ToList(), unitIds, label);
    }
}
