using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports;

internal static class DynamicFlowMappingRuntimeSignatureContractTests
{
    private sealed record ContractCase(string Id, string Semantic, Action Run);

    private static readonly IReadOnlyList<ContractCase> Cases =
    [
        new(
            "P7-RUNTIME-01",
            "Rule-set hash is invariant to rule order and embedded object property order",
            RuleSetHashIsCanonical),
        new(
            "P7-RUNTIME-02",
            "Source signature is invariant to canonical source ordering",
            SourceSignatureIsSourceOrderInvariant),
        new(
            "P7-RUNTIME-03",
            "Source payload, lifecycle, current, active, and period drift changes the signature",
            SourceReportPinDriftChangesSignature),
        new(
            "P7-RUNTIME-04",
            "Flow definition, target, and Form snapshot pin drift changes the signature",
            RuntimeAndFormPinDriftChangesSignature),
        new(
            "P7-RUNTIME-05",
            "Missing or invalid runtime pins fail closed with an exact field",
            RequiredPinsFailClosed),
        new(
            "P7-RUNTIME-06",
            "Result semantic hash canonicalizes every embedded JSON object",
            ResultSemanticHashCanonicalizesEmbeddedJson),
        new(
            "P7-RUNTIME-07",
            "Result semantic hash detects value and array-order drift",
            ResultSemanticHashDetectsSemanticDrift),
        new(
            "P7-RUNTIME-08",
            "The complete matching caller assertion matrix is accepted",
            MatchingCallerAssertionMatrixPasses),
        new(
            "P7-RUNTIME-09",
            "Every caller assertion mismatch fails with its exact field",
            CallerAssertionMismatchMatrixFailsClosed),
        new(
            "P7-RUNTIME-10",
            "Malformed policy JSON and shape fail closed as validation errors",
            MalformedPolicyFailsClosed),
        new(
            "P7-RUNTIME-11",
            "Missing and failed policy loads map to the unavailable contract",
            PolicyLoadErrorsMapToUnavailable),
        new(
            "P7-RUNTIME-12",
            "Response identity and apply request hashes preserve runtime parity",
            ResponseIdentityAndApplyHashPreserveParity)
    ];

    public static IReadOnlyDictionary<string, string> SemanticRegistry { get; } =
        Cases.ToDictionary(item => item.Id, item => item.Semantic, StringComparer.Ordinal);

    public static void Run()
    {
        Require(Cases.Count == 12, "P7-RUNTIME registry must contain 12 cases");
        Require(SemanticRegistry.Count == 12, "P7-RUNTIME ids must be unique");
        for (var number = 1; number <= 12; number++)
        {
            Require(
                SemanticRegistry.ContainsKey($"P7-RUNTIME-{number:00}"),
                $"missing P7-RUNTIME-{number:00}");
        }

        foreach (var contractCase in Cases)
        {
            try
            {
                contractCase.Run();
                Console.WriteLine($"PASS {contractCase.Id} {contractCase.Semantic}");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"{contractCase.Id} ({contractCase.Semantic}) failed: {error.Message}",
                    error);
            }
        }
    }

    private static void RuleSetHashIsCanonical()
    {
        var left = new List<DynamicFlowMappingRuleDto>
        {
            Rule("map-b", 2, alternateExpressionOrder: false),
            Rule("map-a", 1, alternateExpressionOrder: false)
        };
        var right = new List<DynamicFlowMappingRuleDto>
        {
            Rule("map-a", 1, alternateExpressionOrder: true),
            Rule("map-b", 2, alternateExpressionOrder: true)
        };

        var leftHash = DynamicFlowMappingRuntimeContract.ComputeRuleSetHash(left);
        var rightHash = DynamicFlowMappingRuntimeContract.ComputeRuleSetHash(right);
        Require(leftHash == rightHash, "rule and embedded property reorder must preserve hash");

        right[0].MappingVersion++;
        var driftedHash = DynamicFlowMappingRuntimeContract.ComputeRuleSetHash(right);
        Require(leftHash != driftedHash, "mapping version drift must change rule-set hash");
    }

    private static void SourceSignatureIsSourceOrderInvariant()
    {
        var fixture = Fixture.Create();
        var forward = ComputeSourceSignature(fixture);
        var reversed = DynamicFlowMappingRuntimeContract.ComputeSourceSignature(
            fixture.Runtime,
            fixture.Target,
            fixture.Sources.AsEnumerable().Reverse().ToList());
        Require(forward == reversed, "source report order must not affect signature");
    }

    private static void SourceReportPinDriftChangesSignature()
    {
        var mutations = new (string Field, Action<Fixture> Mutate)[]
        {
            ("source.payloadRevision", fixture => fixture.Sources[0].Report.PayloadRevision++),
            ("source.payloadHash", fixture => fixture.Sources[0].Report.PayloadHash = Sha('0')),
            ("source.lifecycleRevision", fixture => fixture.Sources[0].Report.LifecycleRevision++),
            (
                "source.lifecycleStatus",
                fixture => fixture.Sources[0].Report.Status = WorkAssignmentReportStatus.Submitted),
            ("source.lifecycleIsActive", fixture => fixture.Sources[0].Report.IsActive = false),
            ("source.lifecycleIsCurrent", fixture => fixture.Sources[0].Report.IsCurrent = false),
            (
                "source.periodInstanceKey",
                fixture => fixture.Sources[0].Report.PeriodInstanceKey = "period-a-drift"),
            (
                "source.assignmentEffectiveStatus",
                fixture => fixture.Sources[0].Assignment!.FlowEffectiveStatus =
                    DynamicFlowEffectiveStatuses.Invalidated),
            (
                "source.assignmentLifecycleSeriesRevision",
                fixture => fixture.Sources[0].Assignment!
                    .ReportLifecycleSeriesRevision++),
            (
                "source.assignmentMaterializationRevision",
                fixture => fixture.Sources[0].Assignment!
                    .DynamicFlowMaterializationRevision++)
        };

        foreach (var mutation in mutations)
            AssertSignatureDrift(mutation.Field, mutation.Mutate);
    }

    private static void RuntimeAndFormPinDriftChangesSignature()
    {
        var mutations = new (string Field, Action<Fixture> Mutate)[]
        {
            (
                "flow.flowFamilyId",
                fixture => fixture.Runtime.FlowInstance.FlowTemplateId = "flow-family-drift"),
            (
                "flow.flowVersionId",
                fixture => fixture.Runtime.FlowInstance.FlowTemplateVersionId = "flow-version-drift"),
            (
                "flow.flowVersionNo",
                fixture => fixture.Runtime.FlowInstance.FlowTemplateVersionNo++),
            (
                "flow.flowPayloadHash",
                fixture => fixture.Runtime.FlowInstance.FlowPayloadHash = Sha('0')),
            (
                "flow.definitionRevision",
                fixture => fixture.Runtime.FlowInstance.DefinitionRevision = "definition-r2"),
            (
                "flow.catalogVersion",
                fixture => fixture.Runtime.FlowInstance.CatalogVersion = "catalog-v2"),
            (
                "flow.catalogSemanticHash",
                fixture => fixture.Runtime.FlowInstance.CatalogSemanticHash = Sha('0')),
            (
                "mapping.ruleSetHash",
                fixture => fixture.Runtime = fixture.Runtime with { RuleSetHash = Sha('0') }),
            (
                "source.formFamilyId",
                fixture => fixture.Sources[0].RuntimeStep!.FormFamilyId = "source-form-family-drift"),
            (
                "source.formVersionId",
                fixture => fixture.Sources[0].RuntimeStep!.FormVersionId = "source-form-version-drift"),
            (
                "source.formVersionNo",
                fixture => fixture.Sources[0].RuntimeStep!.FormVersionNo++),
            (
                "source.formSchemaHash",
                fixture => fixture.Sources[0].RuntimeStep!.FormSchemaHash = Sha('0')),
            (
                "source.formSnapshotHash",
                fixture => fixture.Sources[0].RuntimeStep!.FormSnapshotHash = Sha('0')),
            (
                "target.formFamilyId",
                fixture => fixture.Runtime.TargetStep.FormFamilyId = "target-form-family-drift"),
            (
                "target.formVersionId",
                fixture => fixture.Runtime.TargetStep.FormVersionId = "target-form-version-drift"),
            (
                "target.formVersionNo",
                fixture => fixture.Runtime.TargetStep.FormVersionNo++),
            (
                "target.formSchemaHash",
                fixture => fixture.Runtime.TargetStep.FormSchemaHash = Sha('0')),
            (
                "target.formSnapshotHash",
                fixture => fixture.Runtime.TargetStep.FormSnapshotHash = Sha('0')),
            (
                "target.payloadRevision",
                fixture => fixture.Target.PayloadRevision++),
            (
                "target.payloadHash",
                fixture => fixture.Target.PayloadHash = Sha('0')),
            (
                "target.lifecycleRevision",
                fixture => fixture.Target.LifecycleRevision++)
        };

        foreach (var mutation in mutations)
            AssertSignatureDrift(mutation.Field, mutation.Mutate);
    }

    private static void RequiredPinsFailClosed()
    {
        AssertPinFailure(
            fixture => fixture.Sources[0].Report.PeriodInstanceKey = " ",
            "DYNAMIC_FLOW_MAPPING_RUNTIME_PIN_MISSING",
            "source.periodInstanceKey");
        AssertPinFailure(
            fixture => fixture.Sources[0].RuntimeStep!.FormSnapshotHash = Sha('A'),
            "DYNAMIC_FLOW_MAPPING_RUNTIME_PIN_INVALID",
            "source.formSnapshotHash");
        AssertPinFailure(
            fixture => fixture.Runtime.FlowInstance.DefinitionRevision = string.Empty,
            "DYNAMIC_FLOW_MAPPING_RUNTIME_PIN_MISSING",
            "flow.definitionRevision");
        AssertPinFailure(
            fixture => fixture.Runtime.TargetStep.FormVersionNo = 0,
            "DYNAMIC_FLOW_MAPPING_RUNTIME_PIN_INVALID",
            "target.formVersionNo");
        AssertPinFailure(
            fixture => fixture.Target.PayloadHash = null,
            "DYNAMIC_FLOW_MAPPING_RUNTIME_PIN_INVALID",
            "target.payloadHash");
    }

    private static void ResultSemanticHashCanonicalizesEmbeddedJson()
    {
        var canonical = Preview(alternateObjectOrder: false);
        var reordered = Preview(alternateObjectOrder: true);
        var canonicalHash =
            DynamicFlowMappingRuntimeContract.ComputeResultSemanticHash(canonical);
        var reorderedHash =
            DynamicFlowMappingRuntimeContract.ComputeResultSemanticHash(reordered);
        Require(
            canonicalHash == reorderedHash,
            "embedded object, change, and provenance reorder must preserve result hash");
    }

    private static void ResultSemanticHashDetectsSemanticDrift()
    {
        var baseline = DynamicFlowMappingRuntimeContract.ComputeResultSemanticHash(
            Preview(alternateObjectOrder: false));
        var valueDrift = DynamicFlowMappingRuntimeContract.ComputeResultSemanticHash(
            Preview(alternateObjectOrder: true, semanticDrift: true));
        var arrayDrift = DynamicFlowMappingRuntimeContract.ComputeResultSemanticHash(
            Preview(alternateObjectOrder: true, arrayOrderDrift: true));

        Require(baseline != valueDrift, "embedded value drift must change result hash");
        Require(baseline != arrayDrift, "embedded array order drift must change result hash");

        var provenanceMutations =
            new (string Field, Action<DynamicFlowMappingPreviewResponse> Mutate)[]
            {
                (
                    "change.sourceReportId",
                    response => response.Changes[0].SourceReportId = "other-report"),
                (
                    "source.dynamicFormTemplateId",
                    response => response.Changes[0].Sources[0]
                        .SourceDynamicFormTemplateId = "other-form"),
                (
                    "source.stepId",
                    response => response.Changes[0].Sources[0]
                        .SourceStepId = "other-step"),
                (
                    "source.stepCode",
                    response => response.Changes[0].Sources[0]
                        .SourceStepCode = "OTHER"),
                (
                    "source.assignmentId",
                    response => response.Changes[0].Sources[0]
                        .SourceAssignmentId = "other-assignment"),
                (
                    "source.reportId",
                    response => response.Changes[0].Sources[0]
                        .SourceReportId = "other-report"),
                (
                    "source.valueJson",
                    response => response.Changes[0].Sources[0]
                        .ValueJson = """{"amount":999,"currency":"VND"}""")
            };
        foreach (var mutation in provenanceMutations)
        {
            var drifted = Preview(alternateObjectOrder: false);
            mutation.Mutate(drifted);
            Require(
                baseline != DynamicFlowMappingRuntimeContract
                    .ComputeResultSemanticHash(drifted),
                $"{mutation.Field} drift must change result hash");
        }
    }

    private static void MatchingCallerAssertionMatrixPasses()
    {
        var fixture = Fixture.Create();
        DynamicFlowMappingRuntimeContract.ValidateCallerAssertions(
            MatchingRequest(fixture),
            fixture.Runtime,
            fixture.Target);
    }

    private static void CallerAssertionMismatchMatrixFailsClosed()
    {
        var fixture = Fixture.Create();
        var mismatches = new (string Field, Action<DynamicFlowMappingRequest> Supply)[]
        {
            ("flowFamilyId", request => request.FlowFamilyId = "mismatch"),
            ("flowVersionId", request => request.FlowVersionId = "mismatch"),
            ("flowPayloadHash", request => request.FlowPayloadHash = Sha('0')),
            ("catalogVersion", request => request.CatalogVersion = "mismatch"),
            ("catalogSemanticHash", request => request.CatalogSemanticHash = Sha('0')),
            ("mappingRuleSetHash", request => request.MappingRuleSetHash = Sha('0')),
            ("evaluatorVersion", request => request.EvaluatorVersion = "mismatch"),
            (
                "functionRegistryVersion",
                request => request.FunctionRegistryVersion = "mismatch"),
            ("functionRegistryHash", request => request.FunctionRegistryHash = Sha('0')),
            ("flowInstanceId", request => request.FlowInstanceId = "mismatch"),
            ("executionEpoch", request => request.ExecutionEpoch = 999),
            ("stepInstanceId", request => request.StepInstanceId = "mismatch"),
            ("stepId", request => request.StepId = "mismatch"),
            ("branchId", request => request.BranchId = "mismatch"),
            ("attemptNo", request => request.AttemptNo = 999),
            (
                "targetAssignmentId",
                request => request.TargetAssignmentId = "mismatch"),
            ("targetReportId", request => request.TargetReportId = "mismatch"),
            ("formFamilyId", request => request.FormFamilyId = "mismatch"),
            ("formVersionId", request => request.FormVersionId = "mismatch"),
            ("formVersionNo", request => request.FormVersionNo = 999),
            ("formSchemaHash", request => request.FormSchemaHash = Sha('0')),
            ("expectedPayloadHash", request => request.ExpectedPayloadHash = Sha('0'))
        };

        Require(mismatches.Length == 22, "caller assertion matrix must cover 22 fields");
        foreach (var mismatch in mismatches)
        {
            var request = new DynamicFlowMappingRequest();
            mismatch.Supply(request);
            try
            {
                DynamicFlowMappingRuntimeContract.ValidateCallerAssertions(
                    request,
                    fixture.Runtime,
                    fixture.Target);
                throw new InvalidOperationException(
                    $"Expected identity conflict for {mismatch.Field}.");
            }
            catch (DynamicFlowMappingContractException error)
                when (error.Reason == "DYNAMIC_FLOW_MAPPING_IDENTITY_CONFLICT" &&
                      error.Field == mismatch.Field)
            {
                // Exact field and stable reason are the contract.
            }
        }
    }

    private static void MalformedPolicyFailsClosed()
    {
        AssertMalformedPolicy(
            "{",
            "DYNAMIC_FLOW_TEMPLATE_PAYLOAD_JSON_INVALID");
        AssertMalformedPolicy(
            "[]",
            "DYNAMIC_FLOW_TEMPLATE_PAYLOAD_OBJECT_REQUIRED");
    }

    private static void PolicyLoadErrorsMapToUnavailable()
    {
        var method = typeof(WorkAssignmentReportService).GetMethod(
            "DynamicFlowMappingPolicyDenied",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                nameof(WorkAssignmentReportService),
                "DynamicFlowMappingPolicyDenied");
        var report = new WorkAssignmentReport
        {
            Id = "target-report",
            WorkAssignmentId = "target-assignment"
        };
        var assignment = new WorkAssignment
        {
            Id = report.WorkAssignmentId
        };

        foreach (var reason in new[]
                 {
                     "DYNAMIC_FLOW_MAPPING_POLICY_NOT_LOADED",
                     "DYNAMIC_FLOW_MAPPING_TARGET_POLICY_LOAD_FAILED",
                     "DYNAMIC_FLOW_MAPPING_SOURCE_POLICY_LOAD_FAILED"
                 })
        {
            var error = method.Invoke(
                null,
                new object?[] { report, assignment, reason, null }) as AppException
                ?? throw new InvalidOperationException(
                    $"Policy helper did not return AppException for {reason}.");
            Require(
                error.Code == AppErrorCode.DYNAMIC_FLOW_MAPPING_POLICY_UNAVAILABLE,
                $"{reason} must map to policy unavailable");
            Require(
                JsonSerializer.Serialize(error.Details).Contains(
                    reason,
                    StringComparison.Ordinal),
                $"{reason} must remain in error details");
        }

        var policySnapshotClassifier =
            typeof(WorkAssignmentReportService).GetMethod(
                "IsDynamicFlowMappingPolicySnapshotFailure",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                nameof(WorkAssignmentReportService),
                "IsDynamicFlowMappingPolicySnapshotFailure");
        var policySnapshotFailure = new AppException(
            AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
            new { reason = "DYNAMIC_FLOW_LOCKED_SNAPSHOT_INTEGRITY_FAILED" },
            innerException: new AppException(
                AppErrorCode.DYNAMIC_FLOW_FIELD_POLICY_COVERAGE_INCOMPLETE));
        var nonPolicySnapshotFailure = new AppException(
            AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
            new { reason = "DYNAMIC_FLOW_LOCKED_SNAPSHOT_INTEGRITY_FAILED" },
            innerException: new InvalidOperationException("form pin drift"));
        var legacyPolicyValidatorFailure = new AppException(
            AppErrorCode.DYNAMIC_FLOW_REVISION_CONFLICT,
            new { reason = "DYNAMIC_FLOW_LOCKED_SNAPSHOT_INTEGRITY_FAILED" },
            innerException: new AppException(
                AppErrorCode.COMMON_VALIDATION_FAILED,
                new
                {
                    path = "fieldPolicies",
                    reason = "DYNAMIC_FLOW_POLICY_SCOPE_DUPLICATE"
                }));
        Require(
            (bool)policySnapshotClassifier.Invoke(
                null,
                new object[] { policySnapshotFailure })! &&
            (bool)policySnapshotClassifier.Invoke(
                null,
                new object[] { legacyPolicyValidatorFailure })! &&
            !(bool)policySnapshotClassifier.Invoke(
                null,
                new object[] { nonPolicySnapshotFailure })!,
            "only typed or legacy-shaped locked policy coverage/conflict failures may normalize to mapping policy unavailable");

        var service = ReadSource(
            "Services/WorkAssignmentReports/WorkAssignmentReportService.cs");
        var targetResolverStart = service.IndexOf(
            "private DynamicFlowPolicyEvaluationResult",
            StringComparison.Ordinal);
        var genericResolverStart = service.IndexOf(
            "private async Task RedactDynamicFlowMappingSourceIdentityWithoutRawAccessAsync(",
            StringComparison.Ordinal);
        Require(
            targetResolverStart >= 0 &&
            genericResolverStart > targetResolverStart,
            "mapping target policy resolver source slice");
        var targetResolver = service[targetResolverStart..genericResolverStart];
        Require(
            targetResolver.Contains(
                "runtime.FlowVersion.PayloadJson",
                StringComparison.Ordinal) &&
            targetResolver.Contains(
                "runtime.TargetStep.FlowStepId",
                StringComparison.Ordinal) &&
            targetResolver.Contains(
                "runtime.TargetStep.FlowStepCode",
                StringComparison.Ordinal) &&
            targetResolver.Contains(
                "IsDynamicFlowReportAfterSubmit(report)",
                StringComparison.Ordinal),
            "mapping target policy must use the exact runtime version, step, and report lifecycle");
        Require(
            !targetResolver.Contains(
                "_ctx.DynamicFlowTemplateVersions",
                StringComparison.Ordinal),
            "mapping target policy must not re-fetch by template id and version number");
    }

    private static void ResponseIdentityAndApplyHashPreserveParity()
    {
        var fixture = Fixture.Create();
        var response = new DynamicFlowMappingPreviewResponse();
        DynamicFlowMappingRuntimeContract.PopulateResponseIdentity(
            response,
            fixture.Runtime,
            fixture.Target);

        Require(
            response.FlowFamilyId == fixture.Runtime.FlowInstance.FlowTemplateId &&
            response.FlowVersionId == fixture.Runtime.FlowInstance.FlowTemplateVersionId &&
            response.FlowPayloadHash == fixture.Runtime.FlowInstance.FlowPayloadHash &&
            response.CatalogVersion == fixture.Runtime.FlowInstance.CatalogVersion &&
            response.CatalogSemanticHash == fixture.Runtime.FlowInstance.CatalogSemanticHash &&
            response.MappingRuleSetHash == fixture.Runtime.RuleSetHash,
            "flow response identity");
        Require(
            response.FlowInstanceId == fixture.Runtime.FlowInstance.Id &&
            response.ExecutionEpoch == fixture.Runtime.TargetStep.ExecutionEpoch &&
            response.StepInstanceId == fixture.Runtime.TargetStep.Id &&
            response.StepId == fixture.Runtime.TargetStep.FlowStepId &&
            response.BranchId == fixture.Runtime.TargetStep.BranchId &&
            response.AttemptNo == fixture.Runtime.TargetStep.AttemptNo,
            "runtime response identity");
        Require(
            response.FormFamilyId == fixture.Runtime.TargetStep.FormFamilyId &&
            response.FormVersionId == fixture.Runtime.TargetStep.FormVersionId &&
            response.FormVersionNo == fixture.Runtime.TargetStep.FormVersionNo &&
            response.FormSchemaHash == fixture.Runtime.TargetStep.FormSchemaHash,
            "Form response identity");
        Require(
            response.TargetPayloadRevision == fixture.Target.PayloadRevision &&
            response.TargetPayloadHash == fixture.Target.PayloadHash &&
            response.TargetLifecycleRevision == fixture.Target.LifecycleRevision,
            "target response revisions");

        var request = MatchingRequest(fixture);
        request.CommandId = "mapping-command";
        request.PreviewToken = "preview-token-a";
        var sourceSignature = ComputeSourceSignature(fixture);
        var resultHash = DynamicFlowMappingRuntimeContract.ComputeResultSemanticHash(
            Preview(alternateObjectOrder: false));
        var first = DynamicFlowMappingRuntimeContract.ComputeApplyRequestHash(
            request,
            fixture.Runtime,
            fixture.Target,
            sourceSignature,
            resultHash);
        var repeated = DynamicFlowMappingRuntimeContract.ComputeApplyRequestHash(
            request,
            fixture.Runtime,
            fixture.Target,
            sourceSignature,
            resultHash);
        Require(first == repeated, "identical apply request must preserve hash");

        request.PreviewToken = "preview-token-b";
        var drifted = DynamicFlowMappingRuntimeContract.ComputeApplyRequestHash(
            request,
            fixture.Runtime,
            fixture.Target,
            sourceSignature,
            resultHash);
        Require(first != drifted, "apply preview-token drift must change hash");
    }

    private static DynamicFlowMappingRuleDto Rule(
        string mappingId,
        int mappingVersion,
        bool alternateExpressionOrder)
        => new()
        {
            MappingId = mappingId,
            MappingVersion = mappingVersion,
            MappingKind = "FIELD_TO_FIELD",
            Target = new DynamicFlowMappingEndpointDto
            {
                Kind = "FIELD",
                StepId = "target-step",
                FieldKey = $"{mappingId}-target"
            },
            Calculation = new DynamicFlowMappingCalculationDto
            {
                Kind = "EXPRESSION",
                ResultDataType = "STRING",
                Expression = JsonNode.Parse(
                    alternateExpressionOrder
                        ? """{"args":[{"value":{"b":2,"a":1}}],"op":"copy"}"""
                        : """{"op":"copy","args":[{"value":{"a":1,"b":2}}]}""")
            }
        };

    private static DynamicFlowMappingPreviewResponse Preview(
        bool alternateObjectOrder,
        bool semanticDrift = false,
        bool arrayOrderDrift = false)
    {
        var amount = semanticDrift ? 11 : 10;
        var contributionJson = alternateObjectOrder
            ? """{"nested":{"a":1,"b":2},"mode":"INCLUDE"}"""
            : """{"mode":"INCLUDE","nested":{"b":2,"a":1}}""";
        var amountJson = JsonSerializer.Serialize(amount);
        var fieldJson = (alternateObjectOrder
            ? """{"values":{"sequence":[1,2],"flag":true,"amount":{"value":__AMOUNT__,"currency":"VND"}}}"""
            : """{"values":{"amount":{"currency":"VND","value":__AMOUNT__},"flag":true,"sequence":[1,2]}}""")
            .Replace("__AMOUNT__", amountJson, StringComparison.Ordinal);
        var tableJson = arrayOrderDrift
            ? """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r2","value":2},{"rowKey":"r1","value":1}]}]}"""
            : alternateObjectOrder
                ? """{"blocks":[{"rows":[{"value":1,"rowKey":"r1"},{"value":2,"rowKey":"r2"}],"blockId":"b1"}]}"""
                : """{"blocks":[{"blockId":"b1","rows":[{"rowKey":"r1","value":1},{"rowKey":"r2","value":2}]}]}""";

        var changeA = new DynamicFlowMappingChangeDto
        {
            MappingId = "map-a",
            MappingVersion = 1,
            TargetKind = "FIELD",
            TargetKey = "amount",
            SourceReportId = "source-report-a",
            SourceKey = "source-amount",
            PreviousValueJson = alternateObjectOrder
                ? """{"currency":"VND","value":9}"""
                : """{"value":9,"currency":"VND"}""",
            NextValueJson = (alternateObjectOrder
                ? """{"value":__AMOUNT__,"currency":"VND"}"""
                : """{"currency":"VND","value":__AMOUNT__}""")
                .Replace("__AMOUNT__", amountJson, StringComparison.Ordinal),
            Status = "APPLIED",
            ConceptCode = "AMOUNT",
            ContributionPolicy = "INCLUDE",
            Sources = Provenance(alternateObjectOrder)
        };
        var changeB = new DynamicFlowMappingChangeDto
        {
            MappingId = "map-b",
            MappingVersion = 1,
            TargetKind = "TABLE_COLUMN",
            TargetKey = "b1:value",
            SourceReportId = "source-report-a",
            SourceKey = "source-table",
            PreviousValueJson = alternateObjectOrder
                ? """{"b":2,"a":1}"""
                : """{"a":1,"b":2}""",
            NextValueJson = alternateObjectOrder
                ? """{"nested":{"x":1,"y":2},"ok":true}"""
                : """{"ok":true,"nested":{"y":2,"x":1}}""",
            Status = "APPLIED",
            Sources = Provenance(alternateObjectOrder)
        };
        List<DynamicFlowMappingChangeDto> changes = alternateObjectOrder
            ? [changeB, changeA]
            : [changeA, changeB];

        return new DynamicFlowMappingPreviewResponse
        {
            TargetReportId = "target-report",
            TargetAssignmentId = "target-assignment",
            DataOrigin = "DYNAMIC_FLOW_MAPPING",
            CumulativeContributionMode = "INCLUDE",
            CumulativeContributionPolicyJson = contributionJson,
            FieldValuesJson = fieldJson,
            TableValuesJson = tableJson,
            Changes = changes,
            HasBlockingConflicts = false
        };
    }

    private static List<DynamicFlowMappingInputProvenanceDto> Provenance(
        bool reversed)
    {
        var first = new DynamicFlowMappingInputProvenanceDto
        {
            InputKey = "left",
            SourceDynamicFormTemplateId = "source-form-a",
            SourceStepId = "source-step-a",
            SourceStepCode = "SOURCE_A",
            SourceAssignmentId = "source-assignment-a",
            SourceReportId = "source-report-a",
            SourceKey = "amount",
            RowKey = "r1",
            SourcePayloadRevision = 3,
            SourcePayloadHash = Sha('1'),
            SourceLifecycleRevision = 4,
            ValueJson = reversed
                ? """{"amount":10,"currency":"VND"}"""
                : """{"currency":"VND","amount":10}"""
        };
        var second = new DynamicFlowMappingInputProvenanceDto
        {
            InputKey = "right",
            SourceDynamicFormTemplateId = "source-form-b",
            SourceStepId = "source-step-b",
            SourceStepCode = "SOURCE_B",
            SourceAssignmentId = "source-assignment-b",
            SourceReportId = "source-report-b",
            SourceKey = "amount",
            RowKey = "r2",
            SourcePayloadRevision = 5,
            SourcePayloadHash = Sha('2'),
            SourceLifecycleRevision = 6,
            ValueJson = reversed
                ? """{"amount":20,"currency":"VND"}"""
                : """{"currency":"VND","amount":20}"""
        };
        return reversed ? [second, first] : [first, second];
    }

    private static DynamicFlowMappingRequest MatchingRequest(Fixture fixture)
        => new()
        {
            FlowFamilyId = fixture.Runtime.FlowInstance.FlowTemplateId,
            FlowVersionId = fixture.Runtime.FlowInstance.FlowTemplateVersionId,
            FlowPayloadHash = fixture.Runtime.FlowInstance.FlowPayloadHash,
            CatalogVersion = fixture.Runtime.FlowInstance.CatalogVersion,
            CatalogSemanticHash = fixture.Runtime.FlowInstance.CatalogSemanticHash,
            MappingRuleSetHash = fixture.Runtime.RuleSetHash,
            EvaluatorVersion = DynamicFlowMappingExpressionEvaluator.EvaluatorVersion,
            FunctionRegistryVersion =
                DynamicFlowRegisteredFunctionRegistry.RegistryVersion,
            FunctionRegistryHash = DynamicFlowRegisteredFunctionRegistry.RegistryHash,
            FlowInstanceId = fixture.Runtime.FlowInstance.Id,
            ExecutionEpoch = fixture.Runtime.TargetStep.ExecutionEpoch,
            StepInstanceId = fixture.Runtime.TargetStep.Id,
            StepId = fixture.Runtime.TargetStep.FlowStepId,
            BranchId = fixture.Runtime.TargetStep.BranchId,
            AttemptNo = fixture.Runtime.TargetStep.AttemptNo,
            TargetAssignmentId = fixture.Target.WorkAssignmentId,
            TargetReportId = fixture.Target.Id,
            FormFamilyId = fixture.Runtime.TargetStep.FormFamilyId,
            FormVersionId = fixture.Runtime.TargetStep.FormVersionId,
            FormVersionNo = fixture.Runtime.TargetStep.FormVersionNo,
            FormSchemaHash = fixture.Runtime.TargetStep.FormSchemaHash,
            ExpectedPayloadRevision = fixture.Target.PayloadRevision,
            ExpectedLifecycleRevision = fixture.Target.LifecycleRevision,
            ExpectedPayloadHash = fixture.Target.PayloadHash
        };

    private static void AssertSignatureDrift(
        string field,
        Action<Fixture> mutate)
    {
        var fixture = Fixture.Create();
        var baseline = ComputeSourceSignature(fixture);
        mutate(fixture);
        var drifted = ComputeSourceSignature(fixture);
        Require(baseline != drifted, $"{field} drift must change source signature");
    }

    private static void AssertPinFailure(
        Action<Fixture> mutate,
        string reason,
        string field)
    {
        var fixture = Fixture.Create();
        mutate(fixture);
        try
        {
            _ = ComputeSourceSignature(fixture);
            throw new InvalidOperationException($"Expected {reason} for {field}.");
        }
        catch (DynamicFlowMappingContractException error)
            when (error.Reason == reason && error.Field == field)
        {
            // Exact field and stable reason are the contract.
        }
    }

    private static void AssertMalformedPolicy(string payloadJson, string reason)
    {
        try
        {
            _ = new DynamicFlowPolicyEvaluator().Evaluate(
                payloadJson,
                new DynamicFlowPolicyEvaluationContext
                {
                    StepId = "target-step",
                    StepCode = "TARGET",
                    ActorRole = "ASSIGNEE"
                });
            throw new InvalidOperationException($"Expected malformed policy reason {reason}.");
        }
        catch (AppException error)
            when (error.Code == AppErrorCode.COMMON_VALIDATION_FAILED)
        {
            Require(
                JsonSerializer.Serialize(error.Details).Contains(
                    reason,
                    StringComparison.Ordinal),
                $"malformed policy must expose {reason}");
        }
    }

    private static string ComputeSourceSignature(Fixture fixture)
        => DynamicFlowMappingRuntimeContract.ComputeSourceSignature(
            fixture.Runtime,
            fixture.Target,
            fixture.Sources);

    private static string Sha(char character) => new(character, 64);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string ReadSource(string relativePath)
    {
        var relative = relativePath.Replace(
            '/',
            Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, relative);
            if (File.Exists(direct))
                return File.ReadAllText(direct);

            var nested = Path.Combine(
                directory.FullName,
                "tdtd-be",
                relative);
            if (File.Exists(nested))
                return File.ReadAllText(nested);
        }

        throw new FileNotFoundException(
            $"Unable to locate source file {relativePath}.");
    }

    private sealed class Fixture
    {
        public required DynamicFlowMappingRuntimeContext Runtime { get; set; }
        public required WorkAssignmentReport Target { get; init; }
        public required List<DynamicFlowMappingSourceReport> Sources { get; init; }

        public static Fixture Create()
        {
            var rules = new List<DynamicFlowMappingRuleDto>
            {
                Rule("map-a", 1, alternateExpressionOrder: false),
                Rule("map-b", 2, alternateExpressionOrder: false)
            };
            var flowVersion = new DynamicFlowTemplateVersion
            {
                Id = "flow-version",
                TemplateId = "flow-family",
                VersionNo = 3,
                CatalogVersion = "catalog-v1",
                CatalogSemanticHash = Sha('b'),
                PayloadJson = "{}",
                PayloadHash = Sha('a')
            };
            var flowInstance = new DynamicFlowInstance
            {
                Id = "flow-instance",
                WorkId = "work",
                FlowTemplateId = flowVersion.TemplateId,
                FlowTemplateVersionId = flowVersion.Id,
                FlowTemplateVersionNo = flowVersion.VersionNo,
                FlowPayloadHash = flowVersion.PayloadHash,
                CatalogVersion = flowVersion.CatalogVersion,
                CatalogSemanticHash = flowVersion.CatalogSemanticHash,
                DefinitionRevision = "definition-r1",
                ExecutionEpoch = 7
            };
            var targetStep = new DynamicFlowStepInstance
            {
                Id = "target-step-instance",
                FlowInstanceId = flowInstance.Id,
                ExecutionEpoch = flowInstance.ExecutionEpoch,
                DefinitionRevision = flowInstance.DefinitionRevision,
                FlowStepId = "target-step",
                FlowStepCode = "TARGET",
                BranchId = "target-branch",
                AttemptNo = 2,
                FormFamilyId = "target-form-family",
                FormVersionId = "target-form-version",
                FormVersionNo = 5,
                FormSchemaHash = Sha('c'),
                FormSnapshotHash = Sha('d')
            };
            var runtime = new DynamicFlowMappingRuntimeContext(
                flowVersion,
                flowInstance,
                targetStep,
                rules,
                DynamicFlowMappingRuntimeContract.ComputeRuleSetHash(rules));
            var target = new WorkAssignmentReport
            {
                Id = "target-report",
                WorkId = flowInstance.WorkId,
                WorkAssignmentId = "target-assignment",
                PayloadRevision = 9,
                PayloadHash = Sha('e'),
                LifecycleRevision = 8,
                Status = WorkAssignmentReportStatus.Draft,
                IsActive = true,
                IsCurrent = true,
                PeriodInstanceKey = "target-period"
            };

            return new Fixture
            {
                Runtime = runtime,
                Target = target,
                Sources =
                [
                    Source(
                        flowInstance,
                        "source-report-a",
                        "source-assignment-a",
                        "source-step-a",
                        "source-branch-a",
                        "period-a",
                        payloadRevision: 3,
                        lifecycleRevision: 4,
                        payloadHash: Sha('1'),
                        formSchemaHash: Sha('3'),
                        formSnapshotHash: Sha('4')),
                    Source(
                        flowInstance,
                        "source-report-b",
                        "source-assignment-b",
                        "source-step-b",
                        "source-branch-b",
                        "period-b",
                        payloadRevision: 5,
                        lifecycleRevision: 6,
                        payloadHash: Sha('2'),
                        formSchemaHash: Sha('5'),
                        formSnapshotHash: Sha('6'))
                ]
            };
        }

        private static DynamicFlowMappingSourceReport Source(
            DynamicFlowInstance flowInstance,
            string reportId,
            string assignmentId,
            string stepId,
            string branchId,
            string periodInstanceKey,
            int payloadRevision,
            int lifecycleRevision,
            string payloadHash,
            string formSchemaHash,
            string formSnapshotHash)
        {
            var report = new WorkAssignmentReport
            {
                Id = reportId,
                WorkId = flowInstance.WorkId,
                WorkAssignmentId = assignmentId,
                PayloadRevision = payloadRevision,
                PayloadHash = payloadHash,
                LifecycleRevision = lifecycleRevision,
                Status = WorkAssignmentReportStatus.Approved,
                IsActive = true,
                IsCurrent = true,
                PeriodInstanceKey = periodInstanceKey
            };
            var step = new DynamicFlowStepInstance
            {
                Id = $"{stepId}-instance",
                FlowInstanceId = flowInstance.Id,
                ExecutionEpoch = flowInstance.ExecutionEpoch,
                DefinitionRevision = flowInstance.DefinitionRevision,
                FlowStepId = stepId,
                FlowStepCode = stepId.ToUpperInvariant(),
                BranchId = branchId,
                AttemptNo = 1,
                FormFamilyId = $"{stepId}-form-family",
                FormVersionId = $"{stepId}-form-version",
                FormVersionNo = 2,
                FormSchemaHash = formSchemaHash,
                FormSnapshotHash = formSnapshotHash
            };
            var assignment = new WorkAssignment
            {
                Id = assignmentId,
                WorkId = flowInstance.WorkId,
                IsActive = true,
                FlowEffectiveStatus =
                    DynamicFlowEffectiveStatuses.Effective,
                FlowInstanceId = flowInstance.Id,
                FlowExecutionEpoch = flowInstance.ExecutionEpoch,
                FlowStepId = step.FlowStepId,
                FlowStepCode = step.FlowStepCode,
                FlowBranchId = step.BranchId,
                FlowAttemptNo = step.AttemptNo,
                ReportLifecycleSeriesRevision = 7,
                DynamicFlowMaterializationRevision = 8
            };
            return new DynamicFlowMappingSourceReport(
                report,
                step.FlowStepId,
                step.FlowStepCode,
                """{"values":{"amount":1}}""",
                """{"blocks":[]}""",
                flowInstance,
                step,
                assignment);
        }
    }
}
