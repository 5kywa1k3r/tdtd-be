using System.Text.Json;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;

namespace tdtd_be.Services.DynamicFlows;

internal sealed record DynamicFlowMappingRuntimeContext(
    DynamicFlowTemplateVersion FlowVersion,
    DynamicFlowInstance FlowInstance,
    DynamicFlowStepInstance TargetStep,
    IReadOnlyList<DynamicFlowMappingRuleDto> Rules,
    string RuleSetHash);

internal sealed class DynamicFlowMappingContractException : Exception
{
    public DynamicFlowMappingContractException(
        string reason,
        string? field = null)
        : base(field is null ? reason : $"{reason}: {field}")
    {
        Reason = reason;
        Field = field;
    }

    public string Reason { get; }
    public string? Field { get; }
}

internal static class DynamicFlowMappingRuntimeContract
{
    public const string MappingRuleContractVersion = "P7-MAP-RULES-1";
    public const string CapabilityAllowed = "ALLOWED";
    public const string CapabilityBlocked = "BLOCKED";
    public const string CapabilityReadonly = "READONLY";
    public const string Fresh = "FRESH";
    public const string Stale = "STALE";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string ComputeRuleSetHash(
        IReadOnlyCollection<DynamicFlowMappingRuleDto> rules)
    {
        var ordered = rules
            .OrderBy(rule => rule.MappingId, StringComparer.Ordinal)
            .ThenBy(rule => rule.MappingVersion)
            .ToList();
        return DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
            JsonSerializer.Serialize(ordered, Json));
    }

    public static string ComputeSourceSignature(
        DynamicFlowMappingRuntimeContext runtime,
        WorkAssignmentReport target,
        IReadOnlyCollection<DynamicFlowMappingSourceReport> sources)
    {
        var sourceFacts = sources
            .OrderBy(source => source.Report.Id, StringComparer.Ordinal)
            .Select(source =>
            {
                var assignment = source.Assignment ??
                    throw new DynamicFlowMappingContractException(
                        "DYNAMIC_FLOW_MAPPING_RUNTIME_PIN_MISSING",
                        "source.assignment");
                return new
                {
                    assignmentId = RequireText(
                        assignment.Id,
                        "source.assignmentId"),
                    assignmentIsActive = assignment.IsActive,
                    assignmentEffectiveStatus = RequireText(
                        assignment.FlowEffectiveStatus,
                        "source.assignmentEffectiveStatus"),
                    assignmentFlowInstanceId = RequireText(
                        assignment.FlowInstanceId,
                        "source.assignmentFlowInstanceId"),
                    assignmentExecutionEpoch = RequirePositive(
                        assignment.FlowExecutionEpoch,
                        "source.assignmentExecutionEpoch"),
                    assignmentStepId = RequireText(
                        assignment.FlowStepId,
                        "source.assignmentStepId"),
                    assignmentBranchId = RequireText(
                        assignment.FlowBranchId,
                        "source.assignmentBranchId"),
                    assignmentAttemptNo = RequirePositive(
                        assignment.FlowAttemptNo,
                        "source.assignmentAttemptNo"),
                    assignmentReportLifecycleSeriesRevision =
                        RequireNonNegative(
                            assignment.ReportLifecycleSeriesRevision,
                            "source.assignmentReportLifecycleSeriesRevision"),
                    assignmentMaterializationRevision =
                        RequireNonNegative(
                            assignment.DynamicFlowMaterializationRevision,
                            "source.assignmentMaterializationRevision"),
                    reportId = source.Report.Id,
                    payloadRevision = source.Report.PayloadRevision,
                    payloadHash = RequireHash(
                        source.Report.PayloadHash,
                        "source.payloadHash"),
                    lifecycleRevision = source.Report.LifecycleRevision,
                    lifecycleStatus =
                        source.Report.Status.ToString().ToUpperInvariant(),
                    lifecycleIsActive = source.Report.IsActive,
                    lifecycleIsCurrent = source.Report.IsCurrent,
                    periodInstanceKey = RequireText(
                        source.Report.PeriodInstanceKey,
                        "source.periodInstanceKey"),
                    flowInstanceId = RequireText(
                        source.RuntimeInstance?.Id,
                        "source.flowInstanceId"),
                    executionEpoch = RequirePositive(
                        source.RuntimeStep?.ExecutionEpoch,
                        "source.executionEpoch"),
                    stepInstanceId = RequireText(
                        source.RuntimeStep?.Id,
                        "source.stepInstanceId"),
                    stepId = RequireText(
                        source.RuntimeStep?.FlowStepId,
                        "source.stepId"),
                    branchId = RequireText(
                        source.RuntimeStep?.BranchId,
                        "source.branchId"),
                    attemptNo = RequirePositive(
                        source.RuntimeStep?.AttemptNo,
                        "source.attemptNo"),
                    formFamilyId = RequireText(
                        source.RuntimeStep?.FormFamilyId,
                        "source.formFamilyId"),
                    formVersionId = RequireText(
                        source.RuntimeStep?.FormVersionId,
                        "source.formVersionId"),
                    formVersionNo = RequirePositive(
                        source.RuntimeStep?.FormVersionNo,
                        "source.formVersionNo"),
                    formSchemaHash = RequireHash(
                        source.RuntimeStep?.FormSchemaHash,
                        "source.formSchemaHash"),
                    formSnapshotHash = RequireHash(
                        source.RuntimeStep?.FormSnapshotHash,
                        "source.formSnapshotHash")
                };
            })
            .ToList();

        var facts = new
        {
            signatureVersion = DynamicFlowMappingSecurityContract.SourceSignatureVersion,
            flowFamilyId = RequireText(
                runtime.FlowInstance.FlowTemplateId,
                "flow.flowFamilyId"),
            flowVersionId = RequireText(
                runtime.FlowInstance.FlowTemplateVersionId,
                "flow.flowVersionId"),
            flowVersionNo = RequirePositive(
                runtime.FlowInstance.FlowTemplateVersionNo,
                "flow.flowVersionNo"),
            flowPayloadHash = RequireHash(
                runtime.FlowInstance.FlowPayloadHash,
                "flow.flowPayloadHash"),
            definitionRevision = RequireText(
                runtime.FlowInstance.DefinitionRevision,
                "flow.definitionRevision"),
            catalogVersion = RequireText(
                runtime.FlowInstance.CatalogVersion,
                "flow.catalogVersion"),
            catalogSemanticHash = RequireHash(
                runtime.FlowInstance.CatalogSemanticHash,
                "flow.catalogSemanticHash"),
            mappingRuleContractVersion = MappingRuleContractVersion,
            mappingRuleSetHash = RequireHash(
                runtime.RuleSetHash,
                "mapping.ruleSetHash"),
            evaluatorVersion = DynamicFlowMappingExpressionEvaluator.EvaluatorVersion,
            functionRegistryVersion = DynamicFlowRegisteredFunctionRegistry.RegistryVersion,
            functionRegistryHash = DynamicFlowRegisteredFunctionRegistry.RegistryHash,
            flowInstanceId = RequireText(
                runtime.FlowInstance.Id,
                "runtime.flowInstanceId"),
            executionEpoch = RequirePositive(
                runtime.TargetStep.ExecutionEpoch,
                "runtime.executionEpoch"),
            targetStepInstanceId = RequireText(
                runtime.TargetStep.Id,
                "target.stepInstanceId"),
            targetStepId = RequireText(
                runtime.TargetStep.FlowStepId,
                "target.stepId"),
            targetBranchId = RequireText(
                runtime.TargetStep.BranchId,
                "target.branchId"),
            targetAttemptNo = RequirePositive(
                runtime.TargetStep.AttemptNo,
                "target.attemptNo"),
            targetAssignmentId = RequireText(
                target.WorkAssignmentId,
                "target.assignmentId"),
            targetReportId = RequireText(
                target.Id,
                "target.reportId"),
            targetFormFamilyId = RequireText(
                runtime.TargetStep.FormFamilyId,
                "target.formFamilyId"),
            targetFormVersionId = RequireText(
                runtime.TargetStep.FormVersionId,
                "target.formVersionId"),
            targetFormVersionNo = RequirePositive(
                runtime.TargetStep.FormVersionNo,
                "target.formVersionNo"),
            targetFormSchemaHash = RequireHash(
                runtime.TargetStep.FormSchemaHash,
                "target.formSchemaHash"),
            targetFormSnapshotHash = RequireHash(
                runtime.TargetStep.FormSnapshotHash,
                "target.formSnapshotHash"),
            expectedTargetPayloadRevision = target.PayloadRevision,
            expectedTargetPayloadHash = RequireHash(target.PayloadHash, "target.payloadHash"),
            expectedTargetLifecycleRevision = target.LifecycleRevision,
            sources = sourceFacts
        };
        return DynamicFlowMappingSecurityContract.ComputeSourceSignature(
            JsonSerializer.Serialize(facts, Json));
    }

    public static string ComputeResultSemanticHash(
        DynamicFlowMappingPreviewResponse response)
    {
        var facts = new
        {
            response.TargetReportId,
            response.TargetAssignmentId,
            response.DataOrigin,
            response.CumulativeContributionMode,
            CumulativeContributionPolicyJson =
                CanonicalizeEmbeddedJson(response.CumulativeContributionPolicyJson),
            SummarySourceJson =
                CanonicalizeEmbeddedJson(response.SummarySourceJson),
            FieldValuesJson = CanonicalizeEmbeddedJson(response.FieldValuesJson),
            TableValuesJson = CanonicalizeEmbeddedJson(response.TableValuesJson),
            changes = response.Changes
                .OrderBy(change => change.MappingId, StringComparer.Ordinal)
                .ThenBy(change => change.MappingVersion)
                .ThenBy(change => change.TargetKey, StringComparer.Ordinal)
                .Select(change => new
                {
                    change.MappingId,
                    change.MappingVersion,
                    change.TargetKind,
                    change.TargetKey,
                    change.SourceReportId,
                    change.SourceKey,
                    PreviousValueJson =
                        CanonicalizeEmbeddedJson(change.PreviousValueJson),
                    NextValueJson =
                        CanonicalizeEmbeddedJson(change.NextValueJson),
                    change.Status,
                    change.Reason,
                    change.ConceptCode,
                    change.ContributionPolicy,
                    sources = change.Sources
                        .OrderBy(source => source.SourceReportId, StringComparer.Ordinal)
                        .ThenBy(
                            source => source.SourceAssignmentId,
                            StringComparer.Ordinal)
                        .ThenBy(
                            source => source.SourceStepId,
                            StringComparer.Ordinal)
                        .ThenBy(source => source.InputKey, StringComparer.Ordinal)
                        .ThenBy(source => source.SourceKey, StringComparer.Ordinal)
                        .ThenBy(source => source.RowKey, StringComparer.Ordinal)
                        .Select(source => new
                        {
                            source.InputKey,
                            source.SourceDynamicFormTemplateId,
                            source.SourceStepId,
                            source.SourceStepCode,
                            source.SourceAssignmentId,
                            source.SourceReportId,
                            source.SourceKey,
                            source.RowKey,
                            source.SourcePayloadRevision,
                            source.SourcePayloadHash,
                            source.SourceLifecycleRevision,
                            ValueJson =
                                CanonicalizeEmbeddedJson(source.ValueJson)
                        })
                }),
            response.HasBlockingConflicts
        };
        return DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
            JsonSerializer.Serialize(facts, Json));
    }

    public static string ComputeApplyRequestHash(
        DynamicFlowMappingRequest request,
        DynamicFlowMappingRuntimeContext runtime,
        WorkAssignmentReport target,
        string sourceSignature,
        string resultSemanticHash)
    {
        var facts = new
        {
            operation = "APPLY_DYNAMIC_FLOW_MAPPING",
            commandId = request.CommandId,
            targetReportId = target.Id,
            targetAssignmentId = target.WorkAssignmentId,
            expectedPayloadRevision = request.ExpectedPayloadRevision,
            expectedLifecycleRevision = request.ExpectedLifecycleRevision,
            expectedPayloadHash = request.ExpectedPayloadHash,
            previewToken = request.PreviewToken,
            sourceSignature,
            resultSemanticHash,
            runtime.RuleSetHash,
            flowInstanceId = runtime.FlowInstance.Id,
            executionEpoch = runtime.TargetStep.ExecutionEpoch
        };
        return DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
            JsonSerializer.Serialize(facts, Json));
    }

    public static void ValidateCallerAssertions(
        DynamicFlowMappingRequest request,
        DynamicFlowMappingRuntimeContext runtime,
        WorkAssignmentReport target)
    {
        AssertOptional(request.FlowFamilyId, runtime.FlowInstance.FlowTemplateId, "flowFamilyId");
        AssertOptional(request.FlowVersionId, runtime.FlowInstance.FlowTemplateVersionId, "flowVersionId");
        AssertOptional(request.FlowPayloadHash, runtime.FlowInstance.FlowPayloadHash, "flowPayloadHash");
        AssertOptional(request.CatalogVersion, runtime.FlowInstance.CatalogVersion, "catalogVersion");
        AssertOptional(
            request.CatalogSemanticHash,
            runtime.FlowInstance.CatalogSemanticHash,
            "catalogSemanticHash");
        AssertOptional(request.MappingRuleSetHash, runtime.RuleSetHash, "mappingRuleSetHash");
        AssertOptional(
            request.EvaluatorVersion,
            DynamicFlowMappingExpressionEvaluator.EvaluatorVersion,
            "evaluatorVersion");
        AssertOptional(
            request.FunctionRegistryVersion,
            DynamicFlowRegisteredFunctionRegistry.RegistryVersion,
            "functionRegistryVersion");
        AssertOptional(
            request.FunctionRegistryHash,
            DynamicFlowRegisteredFunctionRegistry.RegistryHash,
            "functionRegistryHash");
        AssertOptional(request.FlowInstanceId, runtime.FlowInstance.Id, "flowInstanceId");
        AssertOptional(request.ExecutionEpoch, runtime.TargetStep.ExecutionEpoch, "executionEpoch");
        AssertOptional(request.StepInstanceId, runtime.TargetStep.Id, "stepInstanceId");
        AssertOptional(request.StepId, runtime.TargetStep.FlowStepId, "stepId");
        AssertOptional(request.BranchId, runtime.TargetStep.BranchId, "branchId");
        AssertOptional(request.AttemptNo, runtime.TargetStep.AttemptNo, "attemptNo");
        AssertOptional(request.TargetAssignmentId, target.WorkAssignmentId, "targetAssignmentId");
        AssertOptional(request.TargetReportId, target.Id, "targetReportId");
        AssertOptional(request.FormFamilyId, runtime.TargetStep.FormFamilyId, "formFamilyId");
        AssertOptional(request.FormVersionId, runtime.TargetStep.FormVersionId, "formVersionId");
        AssertOptional(request.FormVersionNo, runtime.TargetStep.FormVersionNo, "formVersionNo");
        AssertOptional(request.FormSchemaHash, runtime.TargetStep.FormSchemaHash, "formSchemaHash");
        AssertOptional(request.ExpectedPayloadHash, target.PayloadHash, "expectedPayloadHash");
    }

    public static void PopulateResponseIdentity(
        DynamicFlowMappingPreviewResponse response,
        DynamicFlowMappingRuntimeContext runtime,
        WorkAssignmentReport target)
    {
        response.FlowFamilyId = runtime.FlowInstance.FlowTemplateId;
        response.FlowVersionId = runtime.FlowInstance.FlowTemplateVersionId;
        response.FlowPayloadHash = runtime.FlowInstance.FlowPayloadHash;
        response.CatalogVersion = runtime.FlowInstance.CatalogVersion;
        response.CatalogSemanticHash = runtime.FlowInstance.CatalogSemanticHash;
        response.MappingRuleSetHash = runtime.RuleSetHash;
        response.EvaluatorVersion = DynamicFlowMappingExpressionEvaluator.EvaluatorVersion;
        response.FunctionRegistryVersion = DynamicFlowRegisteredFunctionRegistry.RegistryVersion;
        response.FunctionRegistryHash = DynamicFlowRegisteredFunctionRegistry.RegistryHash;
        response.FlowInstanceId = runtime.FlowInstance.Id;
        response.ExecutionEpoch = runtime.TargetStep.ExecutionEpoch;
        response.StepInstanceId = runtime.TargetStep.Id;
        response.StepId = runtime.TargetStep.FlowStepId;
        response.BranchId = runtime.TargetStep.BranchId;
        response.AttemptNo = runtime.TargetStep.AttemptNo;
        response.FormFamilyId = runtime.TargetStep.FormFamilyId;
        response.FormVersionId = runtime.TargetStep.FormVersionId;
        response.FormVersionNo = runtime.TargetStep.FormVersionNo;
        response.FormSchemaHash = runtime.TargetStep.FormSchemaHash;
        response.TargetPayloadRevision = target.PayloadRevision;
        response.TargetPayloadHash = target.PayloadHash ?? string.Empty;
        response.TargetLifecycleRevision = target.LifecycleRevision;
    }

    private static void AssertOptional(string? supplied, string? expected, string field)
    {
        if (!string.IsNullOrWhiteSpace(supplied) &&
            !string.Equals(supplied.Trim(), expected, StringComparison.Ordinal))
        {
            throw new DynamicFlowMappingContractException(
                "DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT",
                field);
        }
    }

    private static void AssertOptional(int? supplied, int expected, string field)
    {
        if (supplied.HasValue && supplied.Value != expected)
        {
            throw new DynamicFlowMappingContractException(
                "DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT",
                field);
        }
    }

    private static string RequireText(string? value, string field)
        => !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new DynamicFlowMappingContractException(
                "DYNAMIC_FLOW_MAPPING_RUNTIME_PIN_MISSING",
                field);

    private static string RequireHash(string? value, string field)
        => value is { Length: 64 } &&
           value.All(character =>
               character is >= '0' and <= '9' ||
               character is >= 'a' and <= 'f')
            ? value
            : throw new DynamicFlowMappingContractException(
                "DYNAMIC_FLOW_MAPPING_RUNTIME_PIN_INVALID",
                field);

    private static int RequirePositive(int? value, string field)
        => value is > 0
            ? value.Value
            : throw new DynamicFlowMappingContractException(
                "DYNAMIC_FLOW_MAPPING_RUNTIME_PIN_INVALID",
                field);

    private static long RequireNonNegative(long value, string field)
        => value >= 0
            ? value
            : throw new DynamicFlowMappingContractException(
                "DYNAMIC_FLOW_MAPPING_RUNTIME_PIN_INVALID",
                field);

    private static string? CanonicalizeEmbeddedJson(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : DynamicFlowMappingSecurityContract.CanonicalizeJson(value);
}
