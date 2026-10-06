using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DashboardModel.DTOs;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;

namespace tdtd_be.DashboardModel.Services;

public sealed partial class DashboardMindMapQueryService
{
    private MeResponse? _focusActor;
    public async Task<List<DashboardTreeNodeDto>> GetAssignmentFocusPathAsync(string workId, string assignmentId, CancellationToken ct = default)
    {
        if (!MongoDB.Bson.ObjectId.TryParse(workId, out _) || !MongoDB.Bson.ObjectId.TryParse(assignmentId, out _))
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { workId, assignmentId });
        _focusActor = await DashboardAuthorityReader.ReadAsync(_ctx, _me.RequireMe().Id, ct);
        _unitScope = await DashboardUnitReadScope.ResolveAsync(_ctx, _focusActor, ct);
        var target = await LoadAccessibleAssignmentAsync(assignmentId, _focusActor.Id, ct);
        if (target.WorkId != workId) throw DashboardForbidden(AppErrorCode.DASHBOARD_ASSIGNMENT_READ_FORBIDDEN, new { workId, assignmentId });
        var access = await LoadWorkAccessContextAsync(workId, _focusActor.Id, ct);
        var path = new List<DashboardTreeNodeDto>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        WorkAssignment? node = target;
        while (node is not null)
        {
            if (!visited.Add(node.Id)) throw DashboardRequired(AppErrorCode.COMMON_VALIDATION_FAILED, "focusPath", "cycle");
            if (path.Count >= 99) throw DashboardRequired(AppErrorCode.COMMON_VALIDATION_FAILED, "focusPath", "too deep");
            if (_unitScope is not null && !_unitScope.Assignments.Any(a => a.Id == node.Id)) break;
            if (!access.FullAccess && !IsAssignmentInAccessibleBranch(node, access.EntryAssignments)) break;
            path.Add(MapNode(node));
            if (string.IsNullOrEmpty(node.ParentAssignmentId)) break;
            node = await _ctx.WorkAssignments.Find(a => a.Id == node.ParentAssignmentId && a.WorkId == workId && a.IsActive && !a.IsDeleted).FirstOrDefaultAsync(ct);
        }
        path.Reverse();
        // Do not expose an inaccessible ancestor ID at the visible entry boundary.
        if (path.Count > 0) path[0].ParentAssignmentId = null;
        return path;
    }
}
