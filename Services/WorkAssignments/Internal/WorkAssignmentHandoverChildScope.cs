using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentHandoverChildScope
{
    public static bool Allows(string fromUserId, Unit? targetUnit,
        IReadOnlyCollection<WorkAssignment> children, IReadOnlyDictionary<string, Unit> units)
        => children.All(child => WorkAssignmentCurrentAuthority.IsReviewer(child, fromUserId) &&
            child.Assignees is { Count: > 0 } && child.Assignees.All(assignee =>
                assignee.UnitId is not null && units.TryGetValue(assignee.UnitId, out var childUnit) &&
                WorkAssignmentUnitHierarchy.IsStrictAncestor(targetUnit, childUnit, units)));
}
