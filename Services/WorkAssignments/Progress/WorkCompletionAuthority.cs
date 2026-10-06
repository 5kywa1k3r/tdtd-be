using tdtd_be.Models;
using tdtd_be.Services.WorkAssignments.Internal;

namespace tdtd_be.Services.WorkAssignments.Progress;

public static class WorkCompletionAuthority
{
    public static bool CanRequest(WorkAssignment a, string userId) =>
        a.IsActive && !a.IsDeleted && (a.Assignees ?? new()).Any(u => u.UserId == userId);

    public static bool CanDecide(WorkAssignment a, Work w, WorkAssignment? parent, string userId)
    {
        if (a.IsDeleted || !a.IsActive || w.IsDeleted || string.IsNullOrWhiteSpace(userId)) return false;
        if (string.IsNullOrWhiteSpace(a.ParentAssignmentId)) return w.Owner?.UserId == userId;
        // The current issuer/reviewer must still belong to the current parent scope after handover.
        return parent != null && !parent.IsDeleted && parent.IsActive && parent.WorkId == a.WorkId &&
            WorkAssignmentCurrentAuthority.IsReviewer(a, userId) &&
            (parent.Assignees ?? new()).Any(u => u.UserId == userId);
    }
}
