using MongoDB.Bson;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

internal sealed record WorkReportStatisticProjectionContext(
    string? AssigneeUserId,
    string? AssigneeUnitId,
    bool AssignmentIsActive,
    bool ReportIsActive,
    string? FlowTemplateId,
    int? FlowTemplateVersionNo,
    string? FlowInstanceId,
    string? FlowStepId,
    string? FlowStepCode,
    int? FlowStepOrder,
    string? FlowBranchId,
    string? ParentFlowBranchId,
    int? FlowAttemptNo,
    string? FlowRole,
    string? FlowEffectiveStatus,
    string? InvalidatedByFlowEventId,
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
            NormalizeObjectIdOrNull(assignment?.FlowTemplateId),
            assignment?.FlowTemplateVersionNo,
            NormalizeObjectIdOrNull(assignment?.FlowInstanceId),
            NormalizeTextOrNull(assignment?.FlowStepId),
            NormalizeTextOrNull(assignment?.FlowStepCode),
            assignment?.FlowStepOrder,
            NormalizeObjectIdOrNull(assignment?.FlowBranchId),
            NormalizeObjectIdOrNull(assignment?.ParentFlowBranchId),
            assignment?.FlowAttemptNo,
            NormalizeTextOrNull(assignment?.FlowRole),
            NormalizeTextOrNull(assignment?.FlowEffectiveStatus),
            NormalizeObjectIdOrNull(assignment?.InvalidatedByFlowEventId),
            payload.PayloadRevision,
            payload.PayloadHash);
    }

    private static string? NormalizeObjectIdOrNull(string? value)
        => ObjectId.TryParse(value, out _) ? value : null;

    private static string? NormalizeTextOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
