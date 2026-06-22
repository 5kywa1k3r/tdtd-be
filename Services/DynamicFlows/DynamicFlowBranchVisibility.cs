using MongoDB.Driver;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowBranchVisibility
{
    public static bool IsFlowAssignment(WorkAssignment? assignment)
        => !string.IsNullOrWhiteSpace(assignment?.FlowInstanceId);

    public static List<string> BuildVisibleUnitIds(
        WorkAssignment assignment,
        IEnumerable<string>? inheritedVisibleUnitIds = null)
    {
        if (!IsFlowAssignment(assignment))
            return new List<string>();

        var result = new List<string>();
        AddMany(result, inheritedVisibleUnitIds);
        Add(result, assignment.IssuedByUnitId);
        AddMany(result, assignment.TargetUnitIds);
        AddMany(result, (assignment.Assignees ?? new List<UserRef>()).Select(x => x.UnitId));
        return result;
    }

    public static bool CanUnitRead(
        WorkAssignment assignment,
        string? actorUnitId,
        IEnumerable<string>? visibleUnitIds = null)
    {
        if (!IsFlowAssignment(assignment))
            return true;

        if (string.IsNullOrWhiteSpace(actorUnitId))
            return false;

        var units = visibleUnitIds?.ToList() ?? BuildVisibleUnitIds(assignment);
        return units.Contains(actorUnitId.Trim(), StringComparer.Ordinal);
    }

    public static bool CanUnitRead(AssignmentListDocRole row, string? actorUnitId)
    {
        if (string.IsNullOrWhiteSpace(row.FlowInstanceId))
            return true;

        return !string.IsNullOrWhiteSpace(actorUnitId) &&
               (row.VisibleUnitIds ?? new List<string>()).Contains(actorUnitId.Trim(), StringComparer.Ordinal);
    }

    public static FilterDefinition<AssignmentListDocRole> BuildAssignmentListFilter(
        FilterDefinitionBuilder<AssignmentListDocRole> fb,
        string? actorUnitId)
    {
        var nonFlow = fb.Eq(x => x.FlowInstanceId, null);
        if (string.IsNullOrWhiteSpace(actorUnitId))
            return nonFlow;

        return fb.Or(nonFlow, fb.AnyEq(x => x.VisibleUnitIds, actorUnitId.Trim()));
    }

    private static void AddMany(List<string> target, IEnumerable<string?>? values)
    {
        if (values is null)
            return;

        foreach (var value in values)
            Add(target, value);
    }

    private static void Add(List<string> target, string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!target.Contains(value, StringComparer.Ordinal))
            target.Add(value);
    }
}
