using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentReviewPermissionHelper
{
    public static void EnsureCanReviewOnNode(WorkAssignment node, string currentUserId)
    {
        if (node == null) throw new InvalidOperationException("Node không hợp lệ.");
        if (string.IsNullOrWhiteSpace(currentUserId))
            throw new UnauthorizedAccessException("Không xác định được người dùng review.");

        if (!string.Equals(node.CreatedByUserId, currentUserId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Chỉ owner của node review mới được xem và duyệt báo cáo.");
    }
}
