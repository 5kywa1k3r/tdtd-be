using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentUnitHierarchy
{
    public static bool IsStrictAncestor(Unit? ancestor, Unit? child, IReadOnlyDictionary<string, Unit> units)
    {
        if (ancestor is null || child is null || ancestor.IsDeleted || child.IsDeleted ||
            ancestor.IsVirtual || child.IsVirtual || ancestor.Id == child.Id) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cursor = child;
        while (!string.IsNullOrWhiteSpace(cursor.ParentUnitId) && seen.Add(cursor.Id))
        {
            if (!units.TryGetValue(cursor.ParentUnitId, out var parent) || parent.IsDeleted || parent.IsVirtual)
                return false;
            if (parent.Id == ancestor.Id) return true;
            cursor = parent;
        }
        return false;
    }
}
