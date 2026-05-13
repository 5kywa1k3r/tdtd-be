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
    {
        if (requestedCompletedDate.HasValue)
            return requestedCompletedDate.Value.Date;

        return ResolveInheritedCompletedDate(work, parent);
    }

    public static DateTime? ResolveEffectiveCompletedDate(
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent)
    {
        if (assignment.CompletedDate.HasValue)
            return assignment.CompletedDate.Value.Date;

        return ResolveInheritedCompletedDate(work, parent);
    }

    public static DateTime? ResolveWorkRootDueDate(Work work)
        => work.DueDate?.Date ?? work.EndDate?.Date;

    public static DateTime? ResolveWorkBoundaryEndDate(Work work)
    {
        var endDate = work.EndDate?.Date;
        var dueDate = work.DueDate?.Date;

        if (!endDate.HasValue)
            return dueDate;
        if (!dueDate.HasValue)
            return endDate;

        return dueDate.Value > endDate.Value
            ? dueDate.Value
            : endDate.Value;
    }

    private static DateTime? ResolveInheritedCompletedDate(Work work, WorkAssignment? parent)
    {
        if (parent is not null)
        {
            return parent.CompletedDate?.Date
                   ?? parent.DueAtUtc?.Date
                   ?? parent.LatestDueAtUtc?.Date
                   ?? ResolveWorkRootDueDate(work);
        }

        return ResolveWorkRootDueDate(work);
    }
}
