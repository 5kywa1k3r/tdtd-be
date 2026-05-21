using tdtd_be.Common.Time;
using tdtd_be.Enum;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.Common;
using tdtd_be.Services.Common.Time;

namespace tdtd_be.Services.WorkAssignments.Internal;

internal sealed record WorkAssignmentCompletionPendingPeriodSample(
    string WorkAssignmentId,
    string AssigneeUserId,
    string PeriodKey,
    DateTime? DueAtUtc,
    string Reason);

internal sealed record WorkAssignmentCompletionReadinessResult(
    int OpenPeriodCount,
    int FutureExpectedPendingCount,
    IReadOnlyList<WorkAssignmentCompletionPendingPeriodSample> Samples)
{
    public bool CanComplete => OpenPeriodCount == 0 && FutureExpectedPendingCount == 0;
}

internal static class WorkAssignmentCompletionReadiness
{
    private const int MaxSamples = 10;
    private const string KeySeparator = "\u001f";

    public static WorkAssignmentCompletionReadinessResult Evaluate(
        Work work,
        IReadOnlyCollection<WorkAssignment> allAssignments,
        IReadOnlyCollection<string> scopeAssignmentIds,
        IReadOnlyCollection<WorkTemplateAssignee> bindings,
        IReadOnlyCollection<WorkReportPeriod> activeScheduledPeriods,
        DateTime completedDate,
        DateTime nowUtc)
    {
        var scopeIds = scopeAssignmentIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

        if (scopeIds.Count == 0)
            return new WorkAssignmentCompletionReadinessResult(0, 0, Array.Empty<WorkAssignmentCompletionPendingPeriodSample>());

        var assignmentById = allAssignments
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToDictionary(x => x.Id!, StringComparer.Ordinal);

        var scopedAssignments = allAssignments
            .Where(x => !x.IsDeleted && x.IsActive && !string.IsNullOrWhiteSpace(x.Id) && scopeIds.Contains(x.Id!))
            .ToList();

        var scopedBindings = bindings
            .Where(x =>
                !x.IsDeleted &&
                x.IsActive &&
                !string.IsNullOrWhiteSpace(x.WorkAssignmentId) &&
                !string.IsNullOrWhiteSpace(x.AssigneeUserId) &&
                scopeIds.Contains(x.WorkAssignmentId))
            .GroupBy(x => x.WorkAssignmentId, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => x
                    .GroupBy(y => y.AssigneeUserId, StringComparer.Ordinal)
                    .Select(y => y.OrderByDescending(z => z.UpdatedAtUtc).First())
                    .ToList(),
                StringComparer.Ordinal);

        var activePeriods = activeScheduledPeriods
            .Where(x =>
                !x.IsDeleted &&
                x.IsActive &&
                !string.IsNullOrWhiteSpace(x.WorkAssignmentId) &&
                !string.IsNullOrWhiteSpace(x.AssigneeUserId) &&
                !string.IsNullOrWhiteSpace(x.PeriodKey) &&
                scopeIds.Contains(x.WorkAssignmentId) &&
                IsScheduledPeriod(x))
            .ToList();

        var samples = new List<WorkAssignmentCompletionPendingPeriodSample>();
        var openPeriodCount = 0;

        foreach (var period in activePeriods)
        {
            if (WorkReportPeriodStatusHelper.IsTerminal(period.Status))
                continue;

            openPeriodCount++;
            AddSample(samples, new WorkAssignmentCompletionPendingPeriodSample(
                period.WorkAssignmentId,
                period.AssigneeUserId,
                period.PeriodKey,
                period.DueAtUtc,
                "OPEN_MATERIALIZED_PERIOD"));
        }

        var terminalPeriodKeys = activePeriods
            .Where(x => WorkReportPeriodStatusHelper.IsTerminal(x.Status))
            .Select(x => BuildPeriodKey(x.WorkAssignmentId, x.AssigneeUserId, x.PeriodKey))
            .ToHashSet(StringComparer.Ordinal);

        var futureExpectedPendingCount = 0;
        var futureStartDate = nowUtc.Date;

        foreach (var assignment in scopedAssignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.Id))
                continue;

            if (!scopedBindings.TryGetValue(assignment.Id, out var assignmentBindings) ||
                assignmentBindings.Count == 0)
                continue;

            var parent = LoadParent(assignment, assignmentById);
            var dueItems = BuildExpectedDueItems(assignment, work, parent, futureStartDate);
            if (dueItems.Count == 0)
                continue;

            foreach (var binding in assignmentBindings)
            {
                foreach (var due in dueItems)
                {
                    if (terminalPeriodKeys.Contains(BuildPeriodKey(assignment.Id, binding.AssigneeUserId, due.PeriodKey)))
                        continue;

                    futureExpectedPendingCount++;
                    AddSample(samples, new WorkAssignmentCompletionPendingPeriodSample(
                        assignment.Id,
                        binding.AssigneeUserId,
                        due.PeriodKey,
                        due.DueAtUtc,
                        "FUTURE_EXPECTED_PERIOD"));
                }
            }
        }

        return new WorkAssignmentCompletionReadinessResult(
            openPeriodCount,
            futureExpectedPendingCount,
            samples);
    }

    private static List<AssignmentScheduleDueItem> BuildExpectedDueItems(
        WorkAssignment assignment,
        Work work,
        WorkAssignment? parent,
        DateTime futureStartDate)
    {
        if (IsOnceAssignment(assignment))
        {
            if (!assignment.DueAtUtc.HasValue || assignment.DueAtUtc.Value.Date < futureStartDate.Date)
                return new List<AssignmentScheduleDueItem>();

            return new List<AssignmentScheduleDueItem>
            {
                new()
                {
                    DueAtUtc = assignment.DueAtUtc.Value,
                    PeriodKey = AssignmentScheduleTimeHelper.GetPeriodKey(null, assignment.DueAtUtc.Value)
                }
            };
        }

        if (assignment.Schedule is null || !ScheduleValidator.IsValid(assignment.Schedule))
            return new List<AssignmentScheduleDueItem>();

        var start = WorkAssignmentDatePolicy.ResolveEffectiveStartDate(assignment, futureStartDate);
        if (start < futureStartDate.Date)
            start = futureStartDate.Date;

        if (assignment.Schedule.StartDate.HasValue && assignment.Schedule.StartDate.Value.Date > start)
            start = assignment.Schedule.StartDate.Value.Date;

        var end = WorkAssignmentDatePolicy.ResolveEffectiveCompletedDate(assignment, work, parent);
        if (!end.HasValue || end.Value.Date < start.Date)
            return new List<AssignmentScheduleDueItem>();

        return AssignmentScheduleDueHelper.GetDueItemsInRange(assignment.Schedule, start, end.Value.Date);
    }

    private static WorkAssignment? LoadParent(
        WorkAssignment assignment,
        IReadOnlyDictionary<string, WorkAssignment> assignmentById)
    {
        if (string.IsNullOrWhiteSpace(assignment.ParentAssignmentId))
            return null;

        return assignmentById.TryGetValue(assignment.ParentAssignmentId, out var parent)
            ? parent
            : null;
    }

    private static bool IsOnceAssignment(WorkAssignment assignment)
        => string.Equals(assignment.AssignmentType, WorkAssignmentTypes.Once, StringComparison.OrdinalIgnoreCase);

    private static bool IsScheduledPeriod(WorkReportPeriod period)
        => string.IsNullOrWhiteSpace(period.PeriodKind) ||
           string.Equals(period.PeriodKind, WorkReportPeriodKind.Scheduled, StringComparison.OrdinalIgnoreCase);

    private static string BuildPeriodKey(string assignmentId, string assigneeUserId, string periodKey)
        => string.Concat(assignmentId, KeySeparator, assigneeUserId, KeySeparator, periodKey);

    private static void AddSample(
        List<WorkAssignmentCompletionPendingPeriodSample> samples,
        WorkAssignmentCompletionPendingPeriodSample sample)
    {
        if (samples.Count < MaxSamples)
            samples.Add(sample);
    }
}
