using tdtd_be.Common.Errors;
using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentReviewPermissionHelper
{
    public static void EnsureCanReviewOnNode(WorkAssignment node, string currentUserId)
    {
        if (node == null) throw AppExceptionFactory.BadRequest(AppErrorCode.WORK_ASSIGNMENT_NODE_INVALID);
        if (string.IsNullOrWhiteSpace(currentUserId))
            throw AppExceptionFactory.Unauthorized(AppErrorCode.WORK_ASSIGNMENT_REVIEWER_MISSING);

        if (!string.Equals(node.CreatedByUserId, currentUserId, StringComparison.Ordinal))
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.WORK_ASSIGNMENT_REVIEW_FORBIDDEN,
                new { assignmentId = node.Id });
    }
}
