using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

// The current account's unit bounds recipient selection. The retained reporting
// unit is provenance, and must never widen a later handover to a sibling branch.
internal static class WorkAssignmentHandoverUnitScope
{
    internal static bool Allows(string? actorUnitId, string? recipientUnitId, IReadOnlyDictionary<string, Unit> units)
    {
        if (string.IsNullOrWhiteSpace(actorUnitId) || string.IsNullOrWhiteSpace(recipientUnitId)
            || !units.TryGetValue(actorUnitId, out var actor) || !units.TryGetValue(recipientUnitId, out var recipient)
            || actor.IsDeleted || recipient.IsDeleted || actor.IsVirtual || recipient.IsVirtual) return false;
        return actor.Id == recipient.Id || WorkAssignmentUnitHierarchy.IsStrictAncestor(actor, recipient, units);
    }
}
