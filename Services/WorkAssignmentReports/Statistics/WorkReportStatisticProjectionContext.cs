using MongoDB.Bson;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

internal sealed record WorkReportStatisticProjectionContext(
    string? AssigneeUserId,
    string? AssigneeUnitId,
    bool AssignmentIsActive,
    bool ReportIsActive,
    int SourcePayloadRevision,
    string? SourcePayloadHash);

internal static class WorkReportStatisticProjectionContextBuilder
{
    public static WorkReportStatisticProjectionContext From(
        WorkAssignmentReport report,
        WorkAssignment? assignment,
        WorkReportPeriod? period,
        WorkReportPayloadSnapshot payload)
    {
        var assignee = assignment?.Assignees?
            .FirstOrDefault(x => string.Equals(x.UserId, report.AssigneeUserId, StringComparison.Ordinal));

        return new WorkReportStatisticProjectionContext(
            NormalizeObjectIdOrNull(period?.AssigneeUserId ?? report.AssigneeUserId),
            NormalizeObjectIdOrNull(period?.AssigneeUnitId ?? assignee?.UnitId),
            assignment?.IsActive == true,
            report.IsActive != false,
            payload.PayloadRevision,
            payload.PayloadHash);
    }

    private static string? NormalizeObjectIdOrNull(string? value)
        => ObjectId.TryParse(value, out _) ? value : null;
}
