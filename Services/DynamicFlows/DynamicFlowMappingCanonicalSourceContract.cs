using tdtd_be.Models;
using tdtd_be.Models.Enums;

namespace tdtd_be.Services.DynamicFlows;

internal sealed class DynamicFlowMappingSourceContractException : Exception
{
    public DynamicFlowMappingSourceContractException(string reason, string field)
        : base($"{reason}: {field}")
    {
        Reason = reason;
        Field = field;
    }

    public string Reason { get; }
    public string Field { get; }
}

internal static class DynamicFlowMappingCanonicalSourceContract
{
    public static void Validate(
        DynamicFlowInstance instance,
        DynamicFlowStepInstance targetStep,
        WorkAssignment targetAssignment,
        DynamicFlowStepInstance sourceStep,
        WorkAssignment sourceAssignment,
        WorkAssignmentReport sourceReport,
        bool isTopologyAncestor)
    {
        Require(
            sourceStep.FlowStepId != targetStep.FlowStepId,
            "DYNAMIC_FLOW_MAPPING_SOURCE_TARGET_CYCLE_FORBIDDEN",
            "source.stepId");
        Require(
            isTopologyAncestor,
            "DYNAMIC_FLOW_MAPPING_SOURCE_TOPOLOGY_MISMATCH",
            "source.stepId");
        Require(
            sourceStep.FlowInstanceId == instance.Id &&
            targetStep.FlowInstanceId == instance.Id,
            "DYNAMIC_FLOW_MAPPING_SOURCE_INSTANCE_CONFLICT",
            "source.flowInstanceId");
        Require(
            sourceStep.ExecutionEpoch == instance.ExecutionEpoch &&
            targetStep.ExecutionEpoch == instance.ExecutionEpoch,
            "DYNAMIC_FLOW_MAPPING_SOURCE_EPOCH_CONFLICT",
            "source.executionEpoch");
        Require(
            sourceStep.IsCanonicalEpoch != false &&
            sourceStep.InvalidatedAtUtc is null &&
            string.IsNullOrWhiteSpace(sourceStep.InvalidatedByFlowEventId),
            "DYNAMIC_FLOW_MAPPING_SOURCE_INVALIDATED",
            "source.stepInstanceId");
        Require(
            string.IsNullOrWhiteSpace(sourceStep.SupersededByStepInstanceId),
            "DYNAMIC_FLOW_MAPPING_SOURCE_SUPERSEDED",
            "source.stepInstanceId");
        Require(
            sourceStep.State is DynamicFlowStepStates.Approved or
                DynamicFlowStepStates.Completed,
            "DYNAMIC_FLOW_MAPPING_SOURCE_LIFECYCLE_CONFLICT",
            "source.stepState");
        Require(
            !string.IsNullOrWhiteSpace(sourceStep.AssignmentId) &&
            sourceStep.AssignmentId == sourceAssignment.Id &&
            !string.IsNullOrWhiteSpace(sourceStep.ReportId) &&
            sourceStep.ReportId == sourceReport.Id,
            "DYNAMIC_FLOW_MAPPING_SOURCE_RUNTIME_IDENTITY_CONFLICT",
            "source.assignmentReport");

        Require(
            targetAssignment.WorkId == instance.WorkId &&
            sourceAssignment.WorkId == instance.WorkId,
            "DYNAMIC_FLOW_MAPPING_SOURCE_WORK_CONFLICT",
            "source.workId");
        Require(
            sourceAssignment.IsActive &&
            !sourceAssignment.IsDeleted &&
            sourceAssignment.FlowEffectiveStatus == DynamicFlowEffectiveStatuses.Effective,
            "DYNAMIC_FLOW_MAPPING_SOURCE_ASSIGNMENT_INEFFECTIVE",
            "source.assignmentId");
        Require(
            sourceAssignment.FlowInstanceId == instance.Id &&
            sourceAssignment.FlowExecutionEpoch == instance.ExecutionEpoch &&
            sourceAssignment.FlowStepId == sourceStep.FlowStepId &&
            sourceAssignment.FlowBranchId == sourceStep.BranchId &&
            sourceAssignment.FlowAttemptNo == sourceStep.AttemptNo,
            "DYNAMIC_FLOW_MAPPING_SOURCE_ASSIGNMENT_PIN_CONFLICT",
            "source.assignmentRuntimePin");

        Require(
            !sourceReport.IsDeleted &&
            sourceReport.IsActive &&
            sourceReport.IsCurrent &&
            sourceReport.Status == WorkAssignmentReportStatus.Approved,
            "DYNAMIC_FLOW_MAPPING_SOURCE_REPORT_INEFFECTIVE",
            "source.reportId");
        Require(
            sourceReport.WorkAssignmentId == sourceAssignment.Id &&
            sourceReport.WorkId == instance.WorkId,
            "DYNAMIC_FLOW_MAPPING_SOURCE_REPORT_ASSIGNMENT_CONFLICT",
            "source.reportAssignment");
        Require(
            sourceReport.DynamicFormFamilyId == sourceStep.FormFamilyId &&
            sourceReport.DynamicFormTemplateId == sourceStep.FormVersionId &&
            sourceReport.DynamicFormVersionNo == sourceStep.FormVersionNo &&
            sourceReport.DynamicFormSchemaHash == sourceStep.FormSchemaHash,
            "DYNAMIC_FLOW_MAPPING_SOURCE_FORM_PIN_CONFLICT",
            "source.formPin");
        Require(
            sourceStep.ReportLifecycleRevision == sourceReport.LifecycleRevision &&
            sourceStep.ReportLifecycleStatus ==
                sourceReport.Status.ToString().ToUpperInvariant() &&
            sourceStep.ReportLifecycleIsActive == sourceReport.IsActive,
            "DYNAMIC_FLOW_MAPPING_SOURCE_LIFECYCLE_PIN_CONFLICT",
            "source.lifecyclePin");
        Require(
            sourceReport.PayloadRevision >= 0 &&
            IsLowerSha256(sourceReport.PayloadHash),
            "DYNAMIC_FLOW_MAPPING_SOURCE_PAYLOAD_PIN_INVALID",
            "source.payloadPin");
    }

    private static bool IsLowerSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character =>
               character is >= '0' and <= '9' ||
               character is >= 'a' and <= 'f');

    private static void Require(bool condition, string reason, string field)
    {
        if (!condition)
            throw new DynamicFlowMappingSourceContractException(reason, field);
    }
}
