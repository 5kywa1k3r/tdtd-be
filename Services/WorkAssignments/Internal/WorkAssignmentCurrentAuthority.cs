using tdtd_be.Models;
using tdtd_be.Services;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentCurrentAuthority
{
    public static string? ReviewerId(WorkAssignment? assignment)
        => string.IsNullOrWhiteSpace(assignment?.CurrentReviewerUserId)
            ? assignment?.CreatedByUserId
            : assignment.CurrentReviewerUserId;

    public static bool IsReviewer(WorkAssignment? assignment, string? userId)
        => !string.IsNullOrWhiteSpace(userId) &&
           string.Equals(ReviewerId(assignment), userId, StringComparison.Ordinal);

    public static bool CanLeadChild(AppUser actor)
    {
        if (WorkAssignmentTargetScopeValidator.IsUnitManager(actor)) return true;
        if (WorkAssignmentTargetScopeValidator.IsLevelManager(actor)) return true;
        if (!string.Equals(actor.AccountKind, ManagementAccountKind.NormalUser, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(actor.AccountKind)) return false;
        return actor.PositionCode?.Trim().ToUpperInvariant() is
            "TRUONG_PHONG" or "PHO_TRUONG_PHONG" or "PHO_TRUONG_PHONG_PHU_TRACH" or
            "DOI_TRUONG" or "PHO_DOI_TRUONG" or "PHO_DOI_TRUONG_PHU_TRACH" or
            "TRUONG_CONG_AN_XA" or "TRUONG_CONG_AN_PHUONG" or
            "PHO_TRUONG_CONG_AN_XA" or "PHO_TRUONG_CONG_AN_PHUONG" or
            "PHO_TRUONG_CONG_AN_XA_PHU_TRACH" or "PHO_TRUONG_CONG_AN_PHUONG_PHU_TRACH";
    }
}
