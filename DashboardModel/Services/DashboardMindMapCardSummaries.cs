using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.DashboardModel.DTOs.MindMap;
using tdtd_be.Models;

namespace tdtd_be.DashboardModel.Services;

public sealed partial class DashboardMindMapQueryService
{
    public async Task<DashboardMindMapCardSummariesResponse> GetCardSummariesAsync(string workId,
        DashboardMindMapCardSummariesRequest? req, CancellationToken ct = default)
    {
        req ??= new();
        var ids = ValidateCardSummaryRequest(workId, req);
        workId = ObjectId.Parse(workId).ToString();
        if (req.FromUtc.HasValue) { req.FromUtc = req.FromUtc.Value.ToUniversalTime(); req.ToUtc = req.ToUtc!.Value.ToUniversalTime(); }
        var actorId = _me.RequireMe().Id;
        // Current authority is re-read once for this batch. No summary is read before every ID passes access checks.
        _focusActor = await DashboardAuthorityReader.ReadAsync(_ctx, actorId, ct);
        _unitScope = null;
        var access = await LoadWorkAccessContextAsync(workId, actorId, ct);
        var af = Builders<WorkAssignment>.Filter;
        var nodes = await _ctx.WorkAssignments.Find(af.In(a => a.Id, ids) & af.Eq(a => a.WorkId, workId)
            & af.Eq(a => a.IsActive, true) & af.Eq(a => a.IsDeleted, false))
            .Project(a => new WorkAssignment { Id = a.Id, WorkId = a.WorkId, Path = a.Path, RootAssignmentId = a.RootAssignmentId,
                AssignmentType = a.AssignmentType, Schedule = a.Schedule }).ToListAsync(ct);
        var nodeMap = nodes.ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var id in ids)
            if (!nodeMap.TryGetValue(id, out var node) || (_unitScope is not null && !_unitScope.Assignments.Any(a => a.Id == id))
                || (!access.FullAccess && !IsAssignmentInAccessibleBranch(node, access.EntryAssignments)))
                throw DashboardForbidden(AppErrorCode.DASHBOARD_ASSIGNMENT_READ_FORBIDDEN, new { assignmentId = id, workId });

        var recipientAccess = new Dictionary<(string AssignmentId, string UserId), bool>();
        bool AllowsRecipient(string assignmentId, string? userId)
        {
            if (_unitScope is null) return true;
            var key = (assignmentId, userId ?? "");
            if (!recipientAccess.TryGetValue(key, out var allowed))
                recipientAccess[key] = allowed = _unitScope.AllowsRecipient(assignmentId, userId);
            return allowed;
        }

        // Bindings store unit IDs as strings; periods store them as ObjectIds. Canonicalize both scopes consistently.
        var unitIds = (req.UnitIds ?? new()).Select(id => ObjectId.Parse(id).ToString()).Distinct(StringComparer.Ordinal).ToList();
        var bf = Builders<WorkTemplateAssignee>.Filter;
        var bindingFilter = bf.Eq(b => b.WorkId, workId) & bf.In(b => b.WorkAssignmentId, ids)
            & bf.Eq(b => b.IsActive, true) & bf.Eq(b => b.IsDeleted, false);
        if (unitIds.Count > 0) bindingFilter &= bf.In(b => b.AssigneeUnitId, unitIds);
        var bindings = await _ctx.WorkTemplateAssignees.Find(bindingFilter)
            .Project(b => new WorkTemplateAssignee { Id = b.Id, WorkId = b.WorkId, WorkAssignmentId = b.WorkAssignmentId,
                IsActive = b.IsActive, DynamicFormTemplateId = b.DynamicFormTemplateId, DynamicFormTemplateCode = b.DynamicFormTemplateCode,
                DynamicFormTemplateName = b.DynamicFormTemplateName, AssigneeUserId = b.AssigneeUserId, AssigneeUnitId = b.AssigneeUnitId })
            .ToListAsync(ct);
        bindings = bindings.Where(b => AllowsRecipient(b.WorkAssignmentId, b.AssigneeUserId)).ToList();

        var pf = Builders<WorkReportPeriod>.Filter;
        var periodFilter = pf.Eq(p => p.WorkId, workId) & pf.In(p => p.WorkAssignmentId, ids)
            & pf.Eq(p => p.IsActive, true) & pf.Eq(p => p.IsDeleted, false);
        if (unitIds.Count > 0) periodFilter &= pf.In(p => p.AssigneeUnitId, unitIds);
        if (req.FromUtc.HasValue) periodFilter &= pf.Gte(p => p.DueAtUtc, req.FromUtc.Value) & pf.Lte(p => p.DueAtUtc, req.ToUtc!.Value);
        var periods = await _ctx.WorkReportPeriods.Find(periodFilter)
            .Project(p => new WorkReportPeriod { Id = p.Id, WorkId = p.WorkId, WorkAssignmentId = p.WorkAssignmentId,
                IsActive = p.IsActive, DynamicFormTemplateId = p.DynamicFormTemplateId, DynamicFormTemplateCode = p.DynamicFormTemplateCode,
                DynamicFormTemplateName = p.DynamicFormTemplateName, AssigneeUserId = p.AssigneeUserId, AssigneeUnitId = p.AssigneeUnitId,
                DueAtUtc = p.DueAtUtc, Status = p.Status, CurrentReportId = p.CurrentReportId }).ToListAsync(ct);
        periods = periods.Where(p => AllowsRecipient(p.WorkAssignmentId, p.AssigneeUserId)).ToList();
        var reportIds = periods.Select(p => p.CurrentReportId).Where(id => ObjectId.TryParse(id, out _)).Cast<string>().Distinct(StringComparer.Ordinal).ToList();
        var reports = new List<WorkAssignmentReport>();
        if (reportIds.Count > 0)
        {
            var rf = Builders<WorkAssignmentReport>.Filter;
            reports = await _ctx.WorkAssignmentReports.Find(rf.In(r => r.Id, reportIds) & rf.Eq(r => r.WorkId, workId)
                & rf.In(r => r.WorkAssignmentId, ids) & rf.Eq(r => r.IsActive, true) & rf.Eq(r => r.IsDeleted, false) & rf.Eq(r => r.IsCurrent, true))
                .Project(r => new WorkAssignmentReport { Id = r.Id, WorkId = r.WorkId, WorkAssignmentId = r.WorkAssignmentId,
                    WorkReportPeriodId = r.WorkReportPeriodId, DynamicFormTemplateId = r.DynamicFormTemplateId, AssigneeUserId = r.AssigneeUserId,
                    IsActive = r.IsActive, IsCurrent = r.IsCurrent, Status = r.Status, ReturnedAtUtc = r.ReturnedAtUtc }).ToListAsync(ct);
        }
        var reportMap = reports.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var bindingsByAssignment = bindings.ToLookup(b => b.WorkAssignmentId, StringComparer.Ordinal);
        var periodsByAssignment = periods.ToLookup(p => p.WorkAssignmentId, StringComparer.Ordinal);
        return new DashboardMindMapCardSummariesResponse { FromUtc = req.FromUtc, ToUtc = req.ToUtc, GeneratedAtUtc = DateTime.UtcNow,
            Rows = ids.Select(id => DashboardMindMapCardProjection.Build(nodeMap[id], bindingsByAssignment[id].ToList(),
                periodsByAssignment[id].ToList(), reportMap)).ToList() };
    }

    internal static List<string> ValidateCardSummaryRequest(string workId, DashboardMindMapCardSummariesRequest req)
    {
        if (!ObjectId.TryParse(workId, out _) || req.AssignmentIds is null || req.AssignmentIds.Count is < 1 or > 50
            || req.AssignmentIds.Any(id => !ObjectId.TryParse(id, out _))
            || req.UnitIds?.Any(id => !ObjectId.TryParse(id, out _)) == true
            || req.FromUtc.HasValue != req.ToUtc.HasValue || req.FromUtc?.ToUniversalTime() > req.ToUtc?.ToUniversalTime()
            || req.FromUtc?.Kind == DateTimeKind.Unspecified || req.ToUtc?.Kind == DateTimeKind.Unspecified)
            throw AppExceptionFactory.BadRequest(AppErrorCode.COMMON_VALIDATION_FAILED, new { field = "cardSummaries", maxAssignments = 50, requiredDatePair = true });
        return req.AssignmentIds.Select(id => ObjectId.Parse(id).ToString()).Distinct(StringComparer.Ordinal).ToList();
    }
}
