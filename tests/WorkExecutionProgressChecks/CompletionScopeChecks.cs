using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.DTOs.Auth;
using tdtd_be.Services.WorkAssignments.Progress;
using tdtd_be.Services.WorkAssignmentReports;

internal static class CompletionScopeChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var w = new Work { Id = "w", Status = WorkStatus.S3 };
        var root = new WorkAssignment { Id = "root", WorkId = w.Id, IsActive = true, CompletedAtUtc = DateTime.UtcNow };
        var middle = new WorkAssignment { Id = "middle", WorkId = w.Id, ParentAssignmentId = root.Id, IsActive = true };
        var leaf = new WorkAssignment { Id = "leaf", WorkId = w.Id, ParentAssignmentId = middle.Id, IsActive = true,
            CompletionReopenedAtUtc = DateTime.UtcNow, CompletionReviewPeriodId = "selected" };
        WorkAssignment[] ancestors = [root, middle];
        check(WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, "selected"), "Selected period can reopen below completed Work and ancestor");
        check(!WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, "other"), "Sibling period stays closed");
        check(!WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, null), "Report exception never grants whole assignment/config scope");
        w.CompletedAtUtc = leaf.CompletionReopenedAtUtc!.Value.AddSeconds(1);
        check(!WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, "selected"), "New Work completion invalidates an older period exception");
        w.CompletedAtUtc = leaf.CompletionReopenedAtUtc.Value.AddSeconds(-1);
        check(WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, "selected"), "Fresh period exception works after Work completion");
        root.CompletedAtUtc = leaf.CompletionReopenedAtUtc.Value.AddSeconds(1);
        check(!WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, "selected"), "New ancestor completion invalidates an older period exception");
        root.CompletedAtUtc = leaf.CompletionReopenedAtUtc.Value.AddSeconds(-1);
        var approvalTime = leaf.CompletionReopenedAtUtc.Value.AddSeconds(2);
        check(WorkExecutionScopeGuard.SettlesReopenedPeriod(leaf, "selected", WorkAssignmentReportStatus.Submitted, approvalTime), "Fresh approval settles exact reopened period");
        check(WorkExecutionScopeGuard.SettlesReopenedPeriod(leaf, "selected", WorkAssignmentReportStatus.Draft, approvalTime), "Fresh auto-approval settles exact reopened period");
        check(!WorkExecutionScopeGuard.SettlesReopenedPeriod(leaf, "selected", WorkAssignmentReportStatus.Approved, approvalTime), "Reactivating approved report cannot settle reopen hold");
        check(!WorkExecutionScopeGuard.SettlesReopenedPeriod(leaf, "other", WorkAssignmentReportStatus.Submitted, approvalTime), "Approval of another period cannot settle correction");
        check(!WorkExecutionScopeGuard.SettlesReopenedPeriod(leaf, "selected", WorkAssignmentReportStatus.Submitted, leaf.CompletionReopenedAtUtc.Value), "Old approval cannot settle a new reopen");
        middle.IsActive = false;
        check(!WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, "selected"), "Reopened period cannot bypass deactivated ancestor");
        middle.IsActive = true; root.IsDeleted = true;
        check(!WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, "selected"), "Deleted ancestor blocks reopened period");
        root.IsDeleted = false; root.WorkId = "other";
        check(!WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, "selected"), "Foreign Work ancestor fails closed");
        root.WorkId = w.Id;
        check(!WorkExecutionScopeGuard.IsOpen(leaf, w, [middle], "selected"), "Missing ancestor fails closed even with stale Path");
        root.ParentAssignmentId = middle.Id;
        check(!WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, "selected"), "Ancestor-only cycle fails closed");
        root.ParentAssignmentId = null; leaf.CompletedAtUtc = DateTime.UtcNow;
        check(!WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, "selected"), "New completion cancels old reopen bypass");
        leaf.CompletedAtUtc = null; leaf.IsActive = false;
        check(!WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, "selected"), "Reactivated parent does not activate individually disabled child");
        leaf.IsActive = true; w.Status = WorkStatus.S1; w.CompletedAtUtc = null; root.CompletedAtUtc = null;
        check(WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, null), "Normal open scope remains editable");
        leaf.CompletedDate = new(2020, 1, 1); leaf.ProgressStatus = 1;
        check(WorkExecutionScopeGuard.IsOpen(leaf, w, ancestors, null), "Historical completion date alone is not finalized execution");
        var now = new DateTime(2026, 10, 6);
        var report = new WorkAssignmentReport { Status = WorkAssignmentReportStatus.Approved,
            CompletedDate = new(2020, 1, 1), PeriodKey = "20200101", UpdatedAtUtc = now.AddDays(-1) };
        var actor = new MeResponse("ordinary", "ordinary", "Ordinary", [], "", null, null, null, [], null, false);
        check(WorkAssignmentHistoricalMutationPolicy.EvaluateApprovedMutation(report, null, actor, now).IsAllowed,
            "Historical report updated yesterday uses recent correction window");
        report.UpdatedAtUtc = now.AddMonths(-2); report.PayloadUpdatedAtUtc = now;
        check(!WorkAssignmentHistoricalMutationPolicy.EvaluateApprovedMutation(report, null, actor, now).IsAllowed,
            "Background payload timestamp cannot renew report correction window");
        report.UpdatedAtUtc = now.AddMonths(-1);
        check(WorkAssignmentHistoricalMutationPolicy.EvaluateApprovedMutation(report, null, actor, now).IsAllowed,
            "Exact one-month update boundary remains allowed");
    }
}
