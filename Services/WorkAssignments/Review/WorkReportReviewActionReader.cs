using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports;
using tdtd_be.Services.WorkAssignments.Internal;
using tdtd_be.Services.WorkAssignments.Progress;

namespace tdtd_be.Services.WorkAssignments.Review;

public sealed record ReviewLockedReport(string ReportId, string WorkId, string AssignmentId,
    string? PeriodId, string? PeriodKey, string AssigneeUserId, string Label, bool IsReviewer);
public sealed record ReviewActionContext(string ReportId, string AssignmentId, string? PeriodId,
    long PayloadRevision, long LifecycleRevision, bool ScopeOpen, bool CanReopen, string ReopenAuthorityLabel,
    string? BlockReason, IReadOnlyDictionary<string, bool> Allowed,
    IReadOnlyList<ReviewLockedReport> LockedReports, int OtherLockedReportCount);

// Hints for the confirmation dialog only. Lifecycle writes retain all transactional checks.
public static class WorkReportReviewActionReader
{
    public static async Task<ReviewActionContext> ReadAsync(MongoDbContext db, string reportId, MeResponse actor, CancellationToken ct)
    {
        var headers = Builders<WorkAssignmentReport>.Projection.Exclude(r => r.Values1DJson)
            .Exclude(r => r.FieldValuesJson).Exclude(r => r.TableValuesJson).Exclude(r => r.SummarySourceJson)
            .Exclude(r => r.Data).Exclude(r => r.SpecJson);
        var report = await db.WorkAssignmentReports.Find(r => r.Id == reportId && !r.IsDeleted)
            .Project<WorkAssignmentReport>(headers).FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND);
        var a = await db.WorkAssignments.Find(x => x.Id == report.WorkAssignmentId && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw AppExceptionFactory.NotFound(AppErrorCode.WORK_ASSIGNMENT_NOT_FOUND);
        WorkAssignmentReviewPermissionHelper.EnsureCanReviewOnNode(a, actor.Id);
        var work = await db.Works.Find(w => w.Id == a.WorkId && !w.IsDeleted).FirstOrDefaultAsync(ct);
        var ancestors = await WorkExecutionScopeGuard.ReadAncestorsAsync(db, a, ct);
        var parent = ancestors.FirstOrDefault(x => x.Id == a.ParentAssignmentId);
        var period = await db.WorkReportPeriods.Find(p => p.Id == report.WorkReportPeriodId && !p.IsDeleted).FirstOrDefaultAsync(ct);
        var scopeOpen = WorkExecutionScopeGuard.IsOpen(a, work, ancestors, period?.Id);
        var effective = WorkExecutionScopeGuard.IsEffective(a, work, ancestors);
        var flow = DynamicFlowBranchVisibility.IsFlowAssignment(a);
        var keys = new List<string> { "REPORT:" + report.Id };
        if (period != null)
        {
            var binding = await db.WorkTemplateAssignees.Find(b => b.Id == period.WorkTemplateAssigneeId && !b.IsDeleted).FirstOrDefaultAsync(ct);
            if (binding != null) keys.Add("SLOT:" + binding.Id + ":" + (binding.AssignmentType == "ONCE" ? "ONCE" : period.PeriodKey));
        }
        var locks = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Locks)
            .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(keys)))).ToListAsync(ct);
        var targetIds = locks.SelectMany(x => AggregateMongoTransaction.Read<AggregateLockState>(x).Value.Owners)
            .Select(x => x.TargetReportId).Distinct().ToArray();
        var visible = new List<ReviewLockedReport>();
        foreach (var id in targetIds.Take(100))
        {
            // Minimal metadata only; do not load payloads or disclose another actor's report.
            var r = await db.WorkAssignmentReports.Find(x => x.Id == id && !x.IsDeleted)
                .Project(x => new { x.Id, x.WorkId, x.WorkAssignmentId, x.WorkReportPeriodId, x.PeriodKey, x.AssigneeUserId }).FirstOrDefaultAsync(ct);
            if (r == null) continue;
            var node = await db.WorkAssignments.Find(x => x.Id == r.WorkAssignmentId && !x.IsDeleted).FirstOrDefaultAsync(ct);
            if (node == null || DynamicFlowBranchVisibility.IsFlowAssignment(node)) continue;
            var reviewer = WorkAssignmentCurrentAuthority.IsReviewer(node, actor.Id);
            var author = r.AssigneeUserId == actor.Id && node.Assignees.Any(u => u.UserId == actor.Id);
            if (!reviewer && !author) continue;
            visible.Add(new(r.Id, r.WorkId, r.WorkAssignmentId, r.WorkReportPeriodId, r.PeriodKey, r.AssigneeUserId,
                (node.Name ?? "Báo cáo cấp trên") + " · " + (r.PeriodKey ?? "Một lần"), reviewer));
        }
        var hasLocks = targetIds.Length > 0;
        var ageAllowed = report.Status != WorkAssignmentReportStatus.Approved ||
            WorkAssignmentHistoricalMutationPolicy.EvaluateApprovedMutation(report, period, actor, DateTime.UtcNow).IsAllowed;
        var self = report.AssigneeUserId == actor.Id;
        var canReopen = !flow && effective && !scopeOpen && !hasLocks && !self && ageAllowed && period is { IsActive: true }
            && work != null && WorkCompletionAuthority.CanDecide(a, work, parent, actor.Id)
            && !(await WorkExecutionReader.ReadAsync(db, a, work, DateTime.UtcNow, ct)).ReopenHold;
        var authorityLabel = string.IsNullOrEmpty(a.ParentAssignmentId) ? work?.Owner?.FullName :
            parent?.Assignees.FirstOrDefault(u => u.UserId == WorkAssignmentCurrentAuthority.ReviewerId(a))?.FullName;
        var reason = flow ? "Luồng này sử dụng quy trình riêng; chưa hỗ trợ thao tác tại đây."
            : self ? "Bạn không được tự duyệt hoặc trả lại báo cáo của chính mình."
            : hasLocks ? "Báo cáo đang được báo cáo cấp trên đã nộp sử dụng. Cần xử lý các báo cáo khóa trước."
            : !effective ? "Phần việc hoặc nhánh cha đang ngừng hiệu lực. Cần kích hoạt đúng nhánh trước."
            : !ageAllowed ? "Báo cáo vượt khoảng thời gian được sửa theo quyền hiện tại, tính từ lần cập nhật báo cáo."
            : !scopeOpen ? "Phạm vi đã kết thúc. Cấp giao việc cần mở lại đúng kỳ trước khi xử lý báo cáo." : null;
        var open = reason == null;
        return new(report.Id, a.Id, period?.Id, report.PayloadRevision, report.LifecycleRevision, scopeOpen, canReopen,
            authorityLabel ?? "Người có quyền kết thúc phần việc ở cấp giao", reason,
            new Dictionary<string, bool> {
                ["return"] = open && report.IsActive && report.Status is WorkAssignmentReportStatus.Submitted or WorkAssignmentReportStatus.Approved,
                ["recallApproved"] = open && report.IsActive && report.Status == WorkAssignmentReportStatus.Approved,
                ["deactivate"] = open && report.IsActive,
                ["reactivate"] = open && !report.IsActive }, visible, targetIds.Length - visible.Count);
    }
}
