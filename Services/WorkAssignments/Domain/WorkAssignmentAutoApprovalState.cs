using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Domain;

public static class WorkAssignmentAutoApprovalState
{
    public const string AutoApproveReviewerComment = "Tự duyệt theo điều kiện cấu hình.";

    public static bool IsAutoApproved(WorkAssignmentReport? report)
        => report is not null &&
           (report.AutoApprovedAtUtc.HasValue ||
            string.Equals(report.ReviewerComment, AutoApproveReviewerComment, StringComparison.Ordinal));

    public static bool IsLocked(WorkAssignmentReport? report)
        => IsAutoApproved(report) && report!.AutoApprovalConfirmedAtUtc.HasValue;

    public static bool CanReporterWithdraw(WorkAssignmentReport? report)
        => IsAutoApproved(report) && !IsLocked(report);
}
