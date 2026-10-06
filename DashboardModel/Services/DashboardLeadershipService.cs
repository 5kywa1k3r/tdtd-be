using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using tdtd_be.Common.Errors;
using tdtd_be.DashboardModel.DTOs;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.DashboardModel.Services;

public sealed partial class DashboardOverviewService
{
    private sealed record LeadershipScope(MeResponse Me, List<Work> Works, List<WorkAssignment> Assignments, string Label, string Fingerprint);

    private async Task<LeadershipScope> LoadLeadershipScopeAsync(CancellationToken ct)
    {
        // Read current account and unit before any cache lookup, including a revocation with an unchanged JWT.
        var actorId = _me.RequireMe().Id;
        var me = await DashboardAuthorityReader.ReadAsync(_ctx, actorId, ct);
        var global = DashboardAccessPolicy.HasGlobalReadAccess(me);
        var head = await DashboardUnitReadScope.ResolveAsync(_ctx, me, ct);
        var roles = await _ctx.DocRoles.Find(x => !x.IsDeleted && x.UserId == actorId && (x.DocType == DocType.WORK || x.DocType == DocType.WORK_ASSIGNMENT)).ToListAsync(ct);
        var fullIds = roles.Where(r => r.DocType == DocType.WORK && (r.Role == DocRoleType.OWNER || r.Role == DocRoleType.LEADER_DIRECTIVE || r.Role == DocRoleType.LEADER_WATCH)).Select(r => r.DocId).ToHashSet(StringComparer.Ordinal);
        var owned = await _ctx.Works.Find(x => !x.IsDeleted && x.CreatedByUserId == actorId).Project(x => x.Id).ToListAsync(ct);
        fullIds.UnionWith(owned);
        if (head is not null) fullIds.UnionWith(head.FullWorkIds);
        var af = Builders<WorkAssignment>.Filter;
        var active = af.Eq(x => x.IsDeleted, false) & af.Eq(x => x.IsActive, true);
        List<WorkAssignment> assignments;
        if (global) assignments = await ReadLeadershipAssignmentsAsync(active, ct);
        else if (head is not null) assignments = head.Assignments.ToList();
        else
        {
            var roleIds = roles.Where(r => r.DocType == DocType.WORK_ASSIGNMENT && (r.Role == DocRoleType.ASSIGNEE || r.Role == DocRoleType.ASSIGNER || r.Role == DocRoleType.ASSIGNMENT_LEADER_WATCH)).Select(r => r.DocId).ToList();
            var seeds = await ReadLeadershipAssignmentsAsync(active & af.Or(af.In(x => x.WorkId, fullIds), af.In(x => x.Id, roleIds), af.Eq(x => x.CreatedByUserId, actorId), af.ElemMatch(x => x.Assignees, x => x.UserId == actorId)), ct);
            var branches = seeds.Where(s => !string.IsNullOrEmpty(s.Path)).Select(s => af.Eq(x => x.WorkId, s.WorkId) & af.Regex(x => x.Path, new BsonRegularExpression("^" + Regex.Escape(s.Path.TrimEnd('/')) + "(?:/|$)"))).ToList();
            branches.Add(af.In(x => x.Id, seeds.Select(s => s.Id)));
            branches.Add(af.In(x => x.WorkId, fullIds));
            assignments = await ReadLeadershipAssignmentsAsync(active & af.Or(branches), ct);
        }
        var workIds = roles.Where(r => r.DocType == DocType.WORK).Select(r => r.DocId).Concat(fullIds).Concat(assignments.Select(a => a.WorkId)).Distinct().ToList();
        if (head is not null) workIds = head.WorkIds.Concat(roles.Where(r => r.DocType == DocType.WORK).Select(r => r.DocId)).Concat(owned).Distinct().ToList();
        var wf = Builders<Work>.Filter;
        var filter = wf.Eq(x => x.IsDeleted, false) & wf.In(x => x.Type, new[] { WorkType.TASK, WorkType.INDICATOR });
        if (!global) filter &= wf.In(x => x.Id, workIds);
        var works = await _ctx.Works.Find(filter).Project(w => new Work { Id = w.Id, Name = w.Name, Code = w.Code, AutoCode = w.AutoCode, Type = w.Type, Status = w.Status, Priority = w.Priority, StartDate = w.StartDate, DueDate = w.DueDate, EndDate = w.EndDate, CompletedDate = w.CompletedDate, CreatedAtUtc = w.CreatedAtUtc }).ToListAsync(ct);
        var readable = works.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        assignments = assignments.Where(a => readable.Contains(a.WorkId)).ToList();
        var identity = JsonSerializer.Serialize(new { me.Id, me.UnitId, me.UnitCode, me.PositionCode, me.UnitTypeCodes, global, head = head?.CacheScope,
            roles = roles.OrderBy(r => r.Id).Select(r => new { r.DocType, r.DocId, r.Role }),
            works = works.OrderBy(w => w.Id).Select(w => w.Id),
            assignments = assignments.OrderBy(a => a.Id).Select(a => new { a.Id, a.WorkId, a.Path, a.ParentAssignmentId, users = a.Assignees.Select(u => u.UserId).OrderBy(u => u) }) });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new(me, works, assignments, global ? "Toàn hệ thống" : head?.Label ?? "Nhiệm vụ, chỉ tiêu và nhánh được phép xem", fingerprint);
    }

    private Task<List<WorkAssignment>> ReadLeadershipAssignmentsAsync(FilterDefinition<WorkAssignment> filter, CancellationToken ct) =>
        _ctx.WorkAssignments.Find(filter).Project(a => new WorkAssignment { Id = a.Id, WorkId = a.WorkId, ParentAssignmentId = a.ParentAssignmentId, Path = a.Path, Code = a.Code, Name = a.Name, DynamicExcelName = a.DynamicExcelName, DynamicFormTemplateName = a.DynamicFormTemplateName,
            IsActive = a.IsActive, IsDeleted = a.IsDeleted, CreatedByUserId = a.CreatedByUserId, CreatedAtUtc = a.CreatedAtUtc, StartDate = a.StartDate, DueDate = a.DueDate, DueAtUtc = a.DueAtUtc, CompletedDate = a.CompletedDate, CompletedAtUtc = a.CompletedAtUtc, ProgressStatus = a.ProgressStatus, Assignees = a.Assignees }).ToListAsync(ct);

    private async Task<DashboardOverviewResponse> GetLeadershipOverviewAsync(DashboardOverviewRequest req, CancellationToken ct)
    {
        var section = DashboardLeadershipProjection.Section(req);
        var now = DateTime.UtcNow;
        var range = DashboardLeadershipProjection.Range(req, now);
        var scope = await LoadLeadershipScopeAsync(ct);
        var currentDate = DashboardLeadershipProjection.Day(DashboardLeadershipProjection.BusinessToday(now));
        var contextKey = section == "CURRENT" ? currentDate : $"{DashboardLeadershipProjection.Day(range.From)}:{DashboardLeadershipProjection.Day(range.To)}:{currentDate}";
        return await _cache.GetOrCreateAsync($"dashboard:leadership:v2:{scope.Me.Id}:{scope.Fingerprint}:{section}:{contextKey}", async innerCt =>
        {
            DashboardLeadershipDto data;
            if (section == "CURRENT") data = DashboardLeadershipProjection.Current(scope.Works, scope.Assignments, now);
            else
            {
                var reports = await LoadLeadershipReportItemsAsync(scope, range.From, range.To, now, innerCt);
                var actualReports = reports.Where(r => r.ReportId is not null).ToList();
                data = new()
                {
                    Section = section, CohortKind = "PERIOD_EVENTS_AND_REPORT_DUE_RANGE", GeneratedAtUtc = now, AsOfDate = currentDate,
                    FromDate = DashboardLeadershipProjection.Day(range.From), ToDate = DashboardLeadershipProjection.Day(range.To), EffectiveBucket = DashboardLeadershipProjection.Bucket(range.From, range.To),
                    WorkActivity = DashboardLeadershipProjection.Activity(range.From, range.To, scope.Works.Select(w => w.CreatedAtUtc), scope.Works.Select(DashboardLeadershipProjection.WorkCompletion)),
                    AssignmentActivity = DashboardLeadershipProjection.Activity(range.From, range.To, scope.Assignments.Select(a => a.CreatedAtUtc), scope.Assignments.Select(DashboardLeadershipProjection.AssignmentCompletion)),
                    MissingWorkCompletionDateCount = scope.Works.Count(w => w.Status == WorkStatus.S3 && w.CompletedDate is null),
                    MissingAssignmentCompletionDateCount = scope.Assignments.Count(a => (a.ProgressStatus == 2 || a.CompletedAtUtc.HasValue) && a.CompletedDate is null),
                    DuePeriodTotal = reports.Count, MissingReportCount = reports.Count(r => r.ReadState == "MISSING_REPORT"), UnavailableReportPeriodCount = reports.Count(r => r.ReadState == "UNAVAILABLE_CURRENT_REPORT"),
                    ReportTotal = actualReports.Count, ApprovedReportCount = actualReports.Count(r => r.ReportStatus == "APPROVED"), ReturnedReportCount = actualReports.Count(r => r.ReportStatus == "RETURNED"),
                    LateReportCount = actualReports.Count(r => r.Timeliness == "LATE"), OnTimeReportCount = actualReports.Count(r => r.Timeliness == "ON_TIME"), UnassessedReportCount = actualReports.Count(r => r.Timeliness == "UNASSESSED"),
                    ReportStatus = new[] {"DRAFT", "SUBMITTED", "APPROVED", "RETURNED"}.Select(s => new DashboardLeadershipCountDto { Key = s, Count = actualReports.Count(r => r.ReportStatus == s) }).ToList(),
                    Timeliness = new[] {"ON_TIME", "LATE", "AT_RISK", "UNASSESSED"}.Select(key => new DashboardLeadershipTimingDto { ObjectKind = "REPORT", Key = key, Count = actualReports.Count(r => r.Timeliness == key) }).ToList(),
                };
            }
            data.ScopeLabel = scope.Label;
            return new DashboardOverviewResponse { Leadership = data, Mode = "LEADERSHIP", ScopeLabel = scope.Label, GeneratedAtUtc = data.GeneratedAtUtc };
        }, ct, req.ForceRefresh, TimeSpan.FromMinutes(15));
    }

    private async Task<List<DashboardLeadershipItemDto>> LoadLeadershipReportItemsAsync(LeadershipScope scope, DateTime from, DateTime to, DateTime now, CancellationToken ct)
    {
        if (scope.Assignments.Count == 0) return new();
        var assignmentMap = scope.Assignments.ToDictionary(a => a.Id);
        var workMap = scope.Works.ToDictionary(w => w.Id);
        var workItems = DashboardLeadershipProjection.WorkItems(scope.Works, scope.Assignments, DashboardLeadershipProjection.BusinessToday(now));
        var startUtc = DateTime.SpecifyKind(from.AddHours(-7), DateTimeKind.Utc);
        var endUtc = DateTime.SpecifyKind(to.AddDays(1).AddHours(-7), DateTimeKind.Utc);
        var pf = Builders<WorkReportPeriod>.Filter;
        var periods = await _ctx.WorkReportPeriods.Find(pf.Eq(p => p.IsDeleted, false) & pf.Eq(p => p.IsActive, true) & pf.In(p => p.WorkAssignmentId, assignmentMap.Keys) & pf.Gte(p => p.DueAtUtc, startUtc) & pf.Lt(p => p.DueAtUtc, endUtc))
            .Project(p => new WorkReportPeriod { Id = p.Id, WorkId = p.WorkId, WorkAssignmentId = p.WorkAssignmentId, AssigneeUserId = p.AssigneeUserId, PeriodKey = p.PeriodKey, DueAtUtc = p.DueAtUtc, CurrentReportId = p.CurrentReportId }).ToListAsync(ct);
        // The head-scope recipient mask is part of the authority, not a unit UI filter.
        periods = periods.Where(p => assignmentMap.TryGetValue(p.WorkAssignmentId, out var a) && a.WorkId == p.WorkId && a.Assignees.Any(u => u.UserId == p.AssigneeUserId)).ToList();
        if (periods.Count == 0) return new();
        var ids = periods.Select(p => p.Id).ToList();
        var rf = Builders<WorkAssignmentReport>.Filter;
        var reports = await _ctx.WorkAssignmentReports.Find(rf.Eq(r => r.IsDeleted, false) & rf.Eq(r => r.IsActive, true) & rf.Eq(r => r.IsCurrent, true) & rf.In(r => r.WorkReportPeriodId, ids))
            .Project(r => new WorkAssignmentReport { Id = r.Id, WorkId = r.WorkId, WorkAssignmentId = r.WorkAssignmentId, WorkReportPeriodId = r.WorkReportPeriodId, AssigneeUserId = r.AssigneeUserId, Status = r.Status, DueAtUtc = r.DueAtUtc, SubmittedAtUtc = r.SubmittedAtUtc, ReturnedAtUtc = r.ReturnedAtUtc, IsLateSubmission = r.IsLateSubmission, UpdatedAtUtc = r.UpdatedAtUtc }).ToListAsync(ct);
        var lookup = reports.GroupBy(r => r.WorkReportPeriodId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UpdatedAtUtc).ToList());
        return periods.Select(p =>
        {
            var a = assignmentMap[p.WorkAssignmentId]; var w = workMap[p.WorkId];
            var candidates = lookup.GetValueOrDefault(p.Id)?.Where(r => r.WorkId == p.WorkId && r.WorkAssignmentId == p.WorkAssignmentId && r.AssigneeUserId == p.AssigneeUserId).ToList();
            var report = candidates?.Count == 1 && !string.IsNullOrEmpty(p.CurrentReportId) && candidates[0].Id == p.CurrentReportId ? candidates[0] : null;
            var state = DashboardLeadershipProjection.ReportState(report, p, now);
            var item = workItems[w.Id] with { };
            item.Id = report?.Id ?? p.Id; item.ObjectKind = report is null ? "REPORT_PERIOD" : "REPORT"; item.ReportPeriodId = p.Id;
            item.ReadState = report is not null ? "READY" : candidates?.Count > 0 || !string.IsNullOrEmpty(p.CurrentReportId) ? "UNAVAILABLE_CURRENT_REPORT" : "MISSING_REPORT";
            item.AssignmentId = a.Id; item.AssignmentName = AssignmentTitle(a); item.ReportId = report?.Id;
            item.StartDate = null; item.CompletedDate = null;
            item.AssigneeName = a.Assignees.First(u => u.UserId == p.AssigneeUserId).FullName; item.PeriodKey = p.PeriodKey;
            item.DueDate = p.DueAtUtc is { } due ? DashboardLeadershipProjection.Day(DashboardLeadershipProjection.BusinessToday(due)) : null;
            item.SourceReportStatus = report is null ? null : (int)report.Status;
            item.Status = item.SourceReportStatus;
            item.ReportStatus = state.Status; item.Timeliness = state.Timeliness;
            return item;
        }).ToList();
    }
    private static string AssignmentTitle(WorkAssignment a) => !string.IsNullOrWhiteSpace(a.Name) ? a.Name : a.DynamicFormTemplateName ?? a.DynamicExcelName ?? a.Code;

    public async Task<DashboardLeadershipItemsDto> SearchLeadershipItemsAsync(DashboardLeadershipItemsRequest req, CancellationToken ct = default)
    {
        var section = DashboardLeadershipProjection.Section(req.Context);
        if (!string.Equals(req.Context.View, "LEADERSHIP", StringComparison.OrdinalIgnoreCase)) throw DashboardLeadershipProjection.Invalid("view", req.Context.View);
        if (req.Page < 0 || req.Page > 100000 || req.PageSize < 1 || req.PageSize > 50) throw DashboardLeadershipProjection.Invalid("page", req.Page);
        if (req.Status.HasValue && (req.Status < 0 || req.Status > 5) || req.Priority.HasValue && req.Priority is not (1 or 2 or 3)) throw DashboardLeadershipProjection.Invalid("status/priority", req.Status);
        var now = DateTime.UtcNow; var today = DashboardLeadershipProjection.BusinessToday(now);
        var range = DashboardLeadershipProjection.Range(req.Context, now);
        var from = req.BucketFromDate is null ? range.From : DashboardLeadershipProjection.ParseDay(req.BucketFromDate);
        var to = req.BucketToDate is null ? range.To : DashboardLeadershipProjection.ParseDay(req.BucketToDate);
        if (from < range.From || to > range.To || from > to) throw DashboardLeadershipProjection.Invalid("bucketRange", "outside context");
        var selection = req.Selection.ToUpperInvariant();
        DashboardLeadershipProjection.ValidateSelectionFilters(req, selection);
        var currentSelections = new[] { "WORK_ALL", "WORK_STATUS", "ASSIGNMENT_ALL", "ASSIGNMENT_STATUS", "WORK_ON_TIME", "WORK_LATE", "WORK_AT_RISK", "WORK_UNASSESSED", "ASSIGNMENT_ON_TIME", "ASSIGNMENT_LATE", "ASSIGNMENT_AT_RISK", "ASSIGNMENT_UNASSESSED", "OPEN", "OVERDUE", "IN_PROGRESS", "NO_DEADLINE", "OVERDUE_BEFORE_WINDOW", "TIMELINE", "DIRECT_ASSIGNMENTS" }
            .Concat(DashboardLeadershipProjection.DeadlineBucketKeys.Select(key => "WORK_DEADLINE_" + key)).ToArray();
        var periodSelections = new[] { "WORK_CREATED", "WORK_COMPLETED", "ASSIGNMENT_CREATED", "ASSIGNMENT_COMPLETED", "REPORT_ALL", "REPORT_APPROVED", "REPORT_RETURNED", "REPORT_LATE", "REPORT_ON_TIME", "REPORT_UNASSESSED", "REPORT_PENDING", "REPORT_UNAVAILABLE", "REPORT_DRAFT", "REPORT_SUBMITTED" };
        if (!(section == "CURRENT" ? currentSelections : periodSelections).Contains(selection)) throw DashboardLeadershipProjection.Invalid("selection", selection);
        if (selection == "DIRECT_ASSIGNMENTS" && string.IsNullOrWhiteSpace(req.WorkId)) throw DashboardLeadershipProjection.Invalid("workId", req.WorkId);
        return await QueryLeadershipItemsAsync(req, selection, from, to, now, ct);
    }
}
