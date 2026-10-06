using tdtd_be.Models;
using tdtd_be.Common.Time;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal static class WorkAssignmentBackfillPeriodPolicy
{
    public const string CompletedDatePolicyReason = "ASSIGNMENT_BACKFILL_PERIOD";

    public static bool IsBackfillHistoricalPeriod(
        WorkAssignment? assignment,
        DateTime? periodStart,
        DateTime? periodEnd,
        DateTime? periodAnchor,
        DateTime nowUtc)
        => TryResolveCompletedDateBounds(
            assignment,
            periodStart,
            periodEnd,
            periodAnchor,
            nowUtc,
            out _,
            out _);

    public static bool TryResolveCompletedDateBounds(
        WorkAssignment? assignment,
        DateTime? periodStart,
        DateTime? periodEnd,
        DateTime? periodAnchor,
        DateTime nowUtc,
        out DateTime minDate,
        out DateTime maxDate)
    {
        minDate = default;
        maxDate = default;

        if (assignment is null)
            return false;

        var sourceStart = ReportCivilDate.ReadPeriodDay(periodStart ?? periodAnchor);
        var sourceEnd = ReportCivilDate.ReadPeriodDay(periodEnd ?? periodStart ?? periodAnchor);
        var anchorDate = (periodAnchor ?? periodEnd ?? periodStart)?.Date;
        if (!sourceStart.HasValue || !sourceEnd.HasValue)
            return false;

        if (sourceEnd.Value < sourceStart.Value)
            (sourceStart, sourceEnd) = (sourceEnd, sourceStart);

        var assignmentCreatedDate = assignment.CreatedAtUtc == default
            ? ReportCivilDate.FromInstant(nowUtc)
            : ReportCivilDate.FromInstant(assignment.CreatedAtUtc);
        var assignmentStartDate = assignment.StartDate?.Date ?? assignmentCreatedDate;

        if (assignmentStartDate >= assignmentCreatedDate)
            return false;

        if (!anchorDate.HasValue ||
            sourceEnd.Value < assignmentStartDate ||
            anchorDate.Value < assignmentStartDate ||
            anchorDate.Value >= assignmentCreatedDate)
            return false;

        minDate = sourceStart.Value > assignmentStartDate
            ? sourceStart.Value
            : assignmentStartDate;
        maxDate = ReportCivilDate.FromInstant(nowUtc);

        return maxDate >= minDate;
    }
}
