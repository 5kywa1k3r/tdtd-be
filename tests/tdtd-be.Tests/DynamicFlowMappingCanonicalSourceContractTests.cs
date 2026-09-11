using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowMappingCanonicalSourceContractTests
{
    public static void Run()
    {
        CanonicalSourcePasses();
        Reject(
            fixture => fixture.SourceStep.FlowInstanceId = "foreign-instance",
            "DYNAMIC_FLOW_MAPPING_SOURCE_INSTANCE_CONFLICT");
        Reject(
            fixture => fixture.SourceStep.ExecutionEpoch++,
            "DYNAMIC_FLOW_MAPPING_SOURCE_EPOCH_CONFLICT");
        Reject(
            fixture => fixture.IsTopologyAncestor = false,
            "DYNAMIC_FLOW_MAPPING_SOURCE_TOPOLOGY_MISMATCH");
        Reject(
            fixture => fixture.SourceStep.InvalidatedAtUtc =
                new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            "DYNAMIC_FLOW_MAPPING_SOURCE_INVALIDATED");
        Reject(
            fixture => fixture.SourceStep.SupersededByStepInstanceId = "successor",
            "DYNAMIC_FLOW_MAPPING_SOURCE_SUPERSEDED");
        Reject(
            fixture => fixture.SourceStep.State = DynamicFlowStepStates.InProgress,
            "DYNAMIC_FLOW_MAPPING_SOURCE_LIFECYCLE_CONFLICT");
        Reject(
            fixture => fixture.SourceReport.Status =
                WorkAssignmentReportStatus.Submitted,
            "DYNAMIC_FLOW_MAPPING_SOURCE_REPORT_INEFFECTIVE");
        Reject(
            fixture => fixture.SourceReport.DynamicFormSchemaHash =
                new string('b', 64),
            "DYNAMIC_FLOW_MAPPING_SOURCE_FORM_PIN_CONFLICT");
        Reject(
            fixture => fixture.SourceReport.LifecycleRevision++,
            "DYNAMIC_FLOW_MAPPING_SOURCE_LIFECYCLE_PIN_CONFLICT");
        Reject(
            fixture => fixture.SourceReport.PayloadHash = "UPPERCASE",
            "DYNAMIC_FLOW_MAPPING_SOURCE_PAYLOAD_PIN_INVALID");
        Reject(
            fixture => fixture.SourceAssignment.FlowAttemptNo++,
            "DYNAMIC_FLOW_MAPPING_SOURCE_ASSIGNMENT_PIN_CONFLICT");
        Reject(
            fixture => fixture.SourceStep.FlowStepId =
                fixture.TargetStep.FlowStepId,
            "DYNAMIC_FLOW_MAPPING_SOURCE_TARGET_CYCLE_FORBIDDEN");
    }

    private static void CanonicalSourcePasses()
    {
        var fixture = Fixture.Create();
        Validate(fixture);
        Console.WriteLine(
            "PASS P7-SOURCE-EXACT canonical instance/epoch/topology/Form/payload/lifecycle source");
    }

    private static void Reject(Action<Fixture> mutate, string reason)
    {
        var fixture = Fixture.Create();
        mutate(fixture);
        try
        {
            Validate(fixture);
            throw new InvalidOperationException($"Expected {reason}.");
        }
        catch (DynamicFlowMappingSourceContractException error)
            when (error.Reason == reason)
        {
            Console.WriteLine($"PASS P7-SOURCE-EXACT {reason}");
        }
    }

    private static void Validate(Fixture fixture)
        => DynamicFlowMappingCanonicalSourceContract.Validate(
            fixture.Instance,
            fixture.TargetStep,
            fixture.TargetAssignment,
            fixture.SourceStep,
            fixture.SourceAssignment,
            fixture.SourceReport,
            fixture.IsTopologyAncestor);

    private sealed class Fixture
    {
        public required DynamicFlowInstance Instance { get; init; }
        public required DynamicFlowStepInstance TargetStep { get; init; }
        public required WorkAssignment TargetAssignment { get; init; }
        public required DynamicFlowStepInstance SourceStep { get; init; }
        public required WorkAssignment SourceAssignment { get; init; }
        public required WorkAssignmentReport SourceReport { get; init; }
        public bool IsTopologyAncestor { get; set; } = true;

        public static Fixture Create()
        {
            const string hash =
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var instance = new DynamicFlowInstance
            {
                Id = "instance",
                WorkId = "work",
                ExecutionEpoch = 4,
                State = DynamicFlowInstanceStates.Active
            };
            var targetStep = new DynamicFlowStepInstance
            {
                Id = "target-step-instance",
                FlowInstanceId = instance.Id,
                ExecutionEpoch = instance.ExecutionEpoch,
                FlowStepId = "target",
                BranchId = "target-branch",
                AttemptNo = 1,
                State = DynamicFlowStepStates.InProgress,
                IsCanonicalEpoch = true
            };
            var targetAssignment = new WorkAssignment
            {
                Id = "target-assignment",
                WorkId = instance.WorkId,
                FlowInstanceId = instance.Id,
                FlowExecutionEpoch = instance.ExecutionEpoch,
                FlowStepId = targetStep.FlowStepId,
                FlowBranchId = targetStep.BranchId,
                FlowAttemptNo = targetStep.AttemptNo,
                FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective,
                IsActive = true
            };
            var sourceStep = new DynamicFlowStepInstance
            {
                Id = "source-step-instance",
                FlowInstanceId = instance.Id,
                ExecutionEpoch = instance.ExecutionEpoch,
                FlowStepId = "source",
                BranchId = "source-branch",
                AttemptNo = 2,
                State = DynamicFlowStepStates.Approved,
                IsCanonicalEpoch = true,
                AssignmentId = "source-assignment",
                ReportId = "source-report",
                FormFamilyId = "form-family",
                FormVersionId = "form-version",
                FormVersionNo = 3,
                FormSchemaHash = hash,
                ReportLifecycleRevision = 7,
                ReportLifecycleStatus = "APPROVED",
                ReportLifecycleIsActive = true
            };
            var sourceAssignment = new WorkAssignment
            {
                Id = sourceStep.AssignmentId,
                WorkId = instance.WorkId,
                FlowInstanceId = instance.Id,
                FlowExecutionEpoch = instance.ExecutionEpoch,
                FlowStepId = sourceStep.FlowStepId,
                FlowBranchId = sourceStep.BranchId,
                FlowAttemptNo = sourceStep.AttemptNo,
                FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective,
                IsActive = true
            };
            var sourceReport = new WorkAssignmentReport
            {
                Id = sourceStep.ReportId,
                WorkId = instance.WorkId,
                WorkAssignmentId = sourceAssignment.Id,
                DynamicFormFamilyId = sourceStep.FormFamilyId,
                DynamicFormTemplateId = sourceStep.FormVersionId,
                DynamicFormVersionNo = sourceStep.FormVersionNo,
                DynamicFormSchemaHash = sourceStep.FormSchemaHash,
                PayloadRevision = 11,
                PayloadHash = hash,
                LifecycleRevision = sourceStep.ReportLifecycleRevision.Value,
                Status = WorkAssignmentReportStatus.Approved,
                IsActive = true,
                IsCurrent = true
            };
            return new Fixture
            {
                Instance = instance,
                TargetStep = targetStep,
                TargetAssignment = targetAssignment,
                SourceStep = sourceStep,
                SourceAssignment = sourceAssignment,
                SourceReport = sourceReport
            };
        }
    }
}
