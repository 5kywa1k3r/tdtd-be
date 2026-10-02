using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentDatePolicy
{
    public static DateTime ResolveEffectiveStartDate(DateTime? requestedStartDate, DateTime nowUtc)
        => requestedStartDate?.Date ?? nowUtc.Date;

    public static DateTime ResolveEffectiveStartDate(WorkAssignment assignment, DateTime nowUtc)
        => ResolveEffectiveStartDate(assignment.StartDate, nowUtc);

    public static DateTime? ResolveEffectiveCompletedDate(
        DateTime? requestedCompletedDate,
        Work work,
        WorkAssignment? parent)
        => ResolveEffectiveDueDate(requestedCompletedDate, work, parent);

    public static DateTime? ResolveEffectiveDueDate(
        DateTime? requestedDueDate,
        Work work,
        WorkAssignment? parent)
    {
        if (requestedDueDate.HasValue)
            return requestedDueDate.Value.Date;

        return ResolveInheritedDueDate(work, parent);
    }

    public static DateTime? ResolveEffectiveCompletedDate(
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent)
        => ResolveEffectiveDueDate(assignment, work, parent);

    public static DateTime? ResolveEffectiveDueDate(
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent)
    {
        if (assignment.DueDate.HasValue)
            return assignment.DueDate.Value.Date;

        if (!assignment.CompletedAtUtc.HasValue && assignment.CompletedDate.HasValue)
            return assignment.CompletedDate.Value.Date;

        return ResolveInheritedDueDate(work, parent);
    }

    public static DateTime? ResolveWorkRootDueDate(Work work)
        => tdtd_be.Services.Works.WorkDatePolicy.EffectiveDueDate(work)?.Date;

    public static DateTime? ResolveWorkBoundaryEndDate(Work work)
        => ResolveWorkRootDueDate(work);

    private static DateTime? ResolveInheritedDueDate(Work work, WorkAssignment? parent)
    {
        if (parent is not null)
        {
            return parent.DueDate?.Date
                   ?? (!parent.CompletedAtUtc.HasValue ? parent.CompletedDate?.Date : null)
                   ?? parent.DueAtUtc?.Date
                   ?? parent.LatestDueAtUtc?.Date
                   ?? ResolveWorkRootDueDate(work);
        }

        return ResolveWorkRootDueDate(work);
    }
}
