using System.Collections.Immutable;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsRun;

internal static partial class LifecycleFixture
{
    private static StatRunLifecycleContributionAuditSnapshot IncludeAudit(
        Pins pins,
        StatisticReconciliationExpectedAuthoritativeLifecycleRow expected,
        int targetCount)
    {
        var source = SourceAudit(pins, expected);
        var receiptId = O($"{pins.CaseId}:{pins.GenerationTag}:receipt");
        var targets = Enumerable.Range(0, targetCount)
            .Select(index => Target(pins, receiptId, source, index))
            .ToImmutableArray();
        var unsigned = new StatRunLifecycleContributionAuditSnapshot(
            Scope(pins),
            receiptId,
            expected.ReportId,
            expected.LifecycleRevision,
            H($"{pins.CaseId}:{pins.GenerationTag}:event"),
            "P9_CONTRIBUTION_V2",
            H($"{pins.CaseId}:{pins.GenerationTag}:ledger"),
            H($"{pins.CaseId}:{pins.GenerationTag}:reversal-baseline"),
            [source],
            targets,
            0,
            0,
            string.Empty,
            string.Empty,
            null,
            null,
            string.Empty);
        return StatRunLifecycleContributionAuditCanonical.Normalize(unsigned);
    }

    private static StatRunLifecycleContributionSourceAuditSnapshot SourceAudit(
        Pins pins,
        StatisticReconciliationExpectedAuthoritativeLifecycleRow expected)
    {
        var runtime = expected.RuntimePin;
        if (string.Equals(runtime.RuntimeKind,
                StatisticReconciliationExpectedRuntimeKinds.NonFlow,
                StringComparison.Ordinal))
        {
            return NonFlowSourceAudit(pins, expected);
        }
        var mappingCommandId = O($"{pins.CaseId}:mapping-command");
        var mappingRequestHash = H($"{pins.CaseId}:mapping-request");
        var previewTokenId = O($"{pins.CaseId}:preview-token");
        var previewTokenHash = H($"{pins.CaseId}:preview-token-hash");
        var sourceSignatureVersion = "P9_MAPPING_SOURCE_SIGNATURE_V1";
        var sourceSignature = H($"{pins.CaseId}:source-signature");
        var sourcePinsHash = H($"{pins.CaseId}:source-pins");
        var ruleSetHash = H($"{pins.CaseId}:mapping-rule-set");
        var evaluatorVersion = "fixture-evaluator-v1";
        var functionRegistryVersion = "fixture-registry-v1";
        var functionRegistryHash = H($"{pins.CaseId}:function-registry");
        var material = new
        {
            version = "P9_FLOW_CONTRIBUTION_SOURCE_V1",
            Id = expected.ReportId,
            expected.PayloadRevision,
            PayloadHash = expected.PayloadOwnerSha256,
            expected.LifecycleRevision,
            approvalCommandId = runtime.ApprovalCommandId,
            approvalEventKey = runtime.ApprovalEventKey,
            mappingReceiptId = runtime.MappingReceiptId,
            mappingProvenanceId = runtime.MappingProvenanceId,
            mappingProvenanceHash = runtime.MappingProvenanceSha256,
            mappingCommandId,
            mappingRequestHash,
            PreviewTokenId = previewTokenId,
            PreviewTokenHash = previewTokenHash,
            SourceSignatureVersion = sourceSignatureVersion,
            SourceSignature = sourceSignature,
            sourcePinsHash,
            ResultSemanticHash = runtime.MappingResultSemanticSha256,
            ResultPayloadRevision = runtime.MappingResultPayloadRevision,
            ResultPayloadHash = runtime.MappingResultPayloadSha256,
            MappingRuleSetHash = ruleSetHash,
            EvaluatorVersion = evaluatorVersion,
            FunctionRegistryVersion = functionRegistryVersion,
            FunctionRegistryHash = functionRegistryHash,
            mappingFlowVersionId = runtime.MappingFlowVersionId,
            mappingFlowVersionNo = runtime.MappingFlowVersionNo,
            mappingFlowPayloadHash = runtime.MappingFlowPayloadSha256,
            flowTemplateVersionId = runtime.FlowTemplateVersionId,
            flowPayloadHash = runtime.FlowPayloadSha256,
            flowInstanceId = runtime.FlowInstanceId,
            flowExecutionEpoch = runtime.ExecutionEpoch,
            flowStepInstanceId = runtime.FlowStepInstanceId,
            flowBranchId = runtime.FlowBranchId,
            flowStepId = runtime.FlowStepId,
            flowAttemptNo = runtime.FlowAttemptNo,
            expected.WorkId,
            expected.WorkAssignmentId,
            pins.PeriodInstanceKey,
            pins.ConfigVersionId,
            ConfigHash = pins.ConfigSha256,
            membershipSignature = pins.MembershipSignatureSha256,
            contributionPolicy = "INCLUDE",
            contributionPolicyHash = runtime.ContributionPolicySha256
        };
        var sourceAuditHash = StatRunCanonicalJson.HashObject(material);
        var sourceAuditId = StatRunCanonicalJson.HashText(
            $"P9_FLOW_CONTRIBUTION_SOURCE_AUDIT_V1\n{sourceAuditHash}");
        var reversalIdentity = StatRunCanonicalJson.HashText(
            "P9_FLOW_CONTRIBUTION_SOURCE_INVERSE_V1\n" +
            $"{sourceAuditId}\n{pins.P9GenerationId}");
        return new StatRunLifecycleContributionSourceAuditSnapshot(
            sourceAuditId,
            sourceAuditHash,
            "FLOW",
            expected.ReportId,
            expected.PayloadRevision,
            expected.PayloadOwnerSha256,
            expected.LifecycleRevision,
            runtime.ApprovalCommandId!,
            runtime.ApprovalEventKey!,
            runtime.MappingReceiptId!,
            runtime.MappingProvenanceId!,
            runtime.MappingProvenanceSha256!,
            runtime.MappingResultSemanticSha256!,
            runtime.MappingResultPayloadRevision!.Value,
            runtime.MappingResultPayloadSha256!,
            runtime.MappingFlowVersionId!,
            runtime.MappingFlowVersionNo!.Value,
            runtime.MappingFlowPayloadSha256!,
            runtime.FlowTemplateVersionId!,
            runtime.FlowPayloadSha256!,
            runtime.FlowInstanceId!,
            runtime.ExecutionEpoch!.Value,
            runtime.FlowStepInstanceId!,
            runtime.FlowBranchId!,
            runtime.FlowStepId!,
            runtime.FlowAttemptNo!.Value,
            null,
            null,
            null,
            null,
            expected.WorkId,
            expected.WorkAssignmentId,
            pins.PeriodInstanceKey,
            pins.ConfigVersionId,
            pins.ConfigSha256,
            pins.MembershipSignatureSha256,
            "INCLUDE",
            runtime.ContributionPolicySha256,
            reversalIdentity);
    }

    private static StatRunLifecycleContributionSourceAuditSnapshot
        NonFlowSourceAudit(
            Pins pins,
            StatisticReconciliationExpectedAuthoritativeLifecycleRow expected)
    {
        var runtime = expected.RuntimePin;
        var policyVersion = "P10_NON_FLOW_CONTRIBUTION_POLICY_V1";
        var material = new
        {
            version = "P9_NON_FLOW_CONTRIBUTION_SOURCE_V1",
            Id = expected.ReportId,
            expected.PayloadRevision,
            PayloadHash = expected.PayloadOwnerSha256,
            expected.LifecycleRevision,
            approvalCommandId = runtime.ApprovalCommandId,
            approvalEventKey = runtime.ApprovalEventKey,
            expected.WorkId,
            expected.WorkAssignmentId,
            pins.PeriodInstanceKey,
            pins.ConfigVersionId,
            ConfigHash = pins.ConfigSha256,
            membershipSignature = pins.MembershipSignatureSha256,
            policyVersion,
            policyOwnerId = expected.ContributionVersionId,
            policyRevision = expected.ContributionRevision,
            policy = expected.ContributionPolicy,
            policySha256 = expected.ContributionPolicySha256,
            contributionOperation = "INCLUDE"
        };
        var sourceAuditHash = StatRunCanonicalJson.HashObject(material);
        var sourceAuditId = StatRunCanonicalJson.HashText(
            $"P9_NON_FLOW_CONTRIBUTION_SOURCE_AUDIT_V1\n{sourceAuditHash}");
        var reversalIdentity = StatRunCanonicalJson.HashText(
            "P9_NON_FLOW_CONTRIBUTION_SOURCE_INVERSE_V1\n" +
            $"{sourceAuditId}\n{pins.P9GenerationId}");
        return new StatRunLifecycleContributionSourceAuditSnapshot(
            SourceAuditId: sourceAuditId,
            SourceAuditHash: sourceAuditHash,
            RuntimeKind: "NON_FLOW",
            SourceReportId: expected.ReportId,
            SourcePayloadRevision: expected.PayloadRevision,
            SourcePayloadHash: expected.PayloadOwnerSha256,
            SourceLifecycleRevision: expected.LifecycleRevision,
            ApprovalCommandId: runtime.ApprovalCommandId!,
            ApprovalEventKey: runtime.ApprovalEventKey!,
            MappingReceiptId: null,
            MappingProvenanceId: null,
            MappingProvenanceHash: null,
            MappingResultSemanticHash: null,
            MappingResultPayloadRevision: null,
            MappingResultPayloadHash: null,
            MappingFlowVersionId: null,
            MappingFlowVersionNo: null,
            MappingFlowPayloadHash: null,
            FlowTemplateVersionId: null,
            FlowPayloadHash: null,
            FlowInstanceId: null,
            FlowExecutionEpoch: null,
            FlowStepInstanceId: null,
            FlowBranchId: null,
            FlowStepId: null,
            FlowAttemptNo: null,
            NonFlowPolicyVersion: policyVersion,
            NonFlowPolicyOwnerId: expected.ContributionVersionId,
            NonFlowPolicyRevision: expected.ContributionRevision,
            NonFlowPolicy: expected.ContributionPolicy,
            WorkId: expected.WorkId,
            WorkAssignmentId: expected.WorkAssignmentId,
            PeriodInstanceKey: pins.PeriodInstanceKey,
            ConfigVersionId: pins.ConfigVersionId,
            ConfigHash: pins.ConfigSha256,
            MembershipSignature: pins.MembershipSignatureSha256,
            ContributionPolicy: "INCLUDE",
            ContributionPolicyHash: expected.ContributionPolicySha256,
            ReversalIdentity: reversalIdentity);
    }

    private static StatRunLifecycleContributionTargetAuditSnapshot Target(
        Pins pins,
        string receiptId,
        StatRunLifecycleContributionSourceAuditSnapshot source,
        int index)
    {
        var targetId = O($"{pins.CaseId}:{pins.GenerationTag}:target:{index}");
        var targetIdentity = H(
            $"{pins.CaseId}:{pins.GenerationTag}:target-identity:{index}");
        var contributionId = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_CONTRIBUTION_TARGET_V2",
            source.RuntimeKind,
            source.SourceAuditId,
            source.SourceAuditHash,
            store = "work_report_field_stat_values",
            targetId,
            targetIdentityHash = targetIdentity,
            operation = "INCLUDE",
            operationVersion = "P9_CONTRIBUTION_V2",
            RunId = pins.P9RunId,
            receiptId,
            GenerationId = pins.P9GenerationId,
            SourceMembershipSignature = pins.MembershipSignatureSha256
        });
        var reversalIdentity = StatRunCanonicalJson.HashText(
            $"P9_CONTRIBUTION_TARGET_INVERSE_V2\n{contributionId}");
        var ledgerEntryHash = StatRunCanonicalJson.HashObject(new
        {
            version = "P9_CONTRIBUTION_LEDGER_ENTRY_V2",
            contributionId,
            source.RuntimeKind,
            source.SourceAuditId,
            source.SourceReportId,
            store = "work_report_field_stat_values",
            targetId,
            targetIdentityHash = targetIdentity,
            operation = "INCLUDE",
            operationVersion = "P9_CONTRIBUTION_V2",
            RunId = pins.P9RunId,
            receiptId,
            GenerationId = pins.P9GenerationId,
            reversalIdentity,
            state = "APPLIED"
        });
        return new StatRunLifecycleContributionTargetAuditSnapshot(
            contributionId,
            source.SourceAuditId,
            source.SourceReportId,
            "work_report_field_stat_values",
            targetId,
            targetIdentity,
            "INCLUDE",
            "P9_CONTRIBUTION_V2",
            pins.P9RunId,
            receiptId,
            pins.P9GenerationId,
            reversalIdentity,
            "APPLIED",
            ledgerEntryHash);
    }

    private static StatRunLifecycleContributionAuditSnapshot ReversalAudit(
        Pins pins,
        StatisticReconciliationExpectedAuthoritativeLifecycleRow expected,
        string operation,
        StatRunLifecycleContributionAuditSnapshot prior)
    {
        var sourceHashes = prior.Sources
            .Where(item => item.SourceReportId == expected.ReportId)
            .OrderBy(static item => item.SourceAuditId, StringComparer.Ordinal)
            .Select(static item => item.SourceAuditHash)
            .ToImmutableArray();
        var targetHashes = prior.Targets
            .Where(item => item.SourceReportId == expected.ReportId)
            .OrderBy(static item => item.TargetStore, StringComparer.Ordinal)
            .ThenBy(static item => item.TargetStatisticId, StringComparer.Ordinal)
            .Select(static item => item.LedgerEntryHash)
            .ToImmutableArray();
        var receipt = O($"{pins.CaseId}:{pins.GenerationTag}:receipt");
        var eventKey = H($"{pins.CaseId}:{pins.GenerationTag}:event");
        var reversal = new StatRunLifecycleReversalAuditSnapshot(
            operation,
            eventKey,
            expected.ReportId,
            expected.LifecycleRevision,
            receipt,
            prior.Scope.RunId,
            prior.Scope.GenerationId,
            prior.Scope.GenerationSha256,
            prior.LedgerHash,
            prior.ReversalBaselineHash,
            sourceHashes.Length,
            targetHashes.Length,
            sourceHashes,
            targetHashes,
            string.Empty);
        var unsigned = new StatRunLifecycleContributionAuditSnapshot(
            Scope(pins),
            receipt,
            expected.ReportId,
            expected.LifecycleRevision,
            eventKey,
            "P9_CONTRIBUTION_V2",
            H($"{pins.CaseId}:{pins.GenerationTag}:ledger"),
            H($"{pins.CaseId}:{pins.GenerationTag}:reversal-baseline"),
            [],
            [],
            0,
            0,
            string.Empty,
            string.Empty,
            null,
            reversal,
            string.Empty);
        reversal = reversal with
        {
            AuditHash = ReversalAuditSha(unsigned, reversal)
        };
        return StatRunLifecycleContributionAuditCanonical.Normalize(unsigned with
        {
            Reversal = reversal
        });
    }

    private static string ReversalAuditSha(
        StatRunLifecycleContributionAuditSnapshot value,
        StatRunLifecycleReversalAuditSnapshot reversal) =>
        StatRunCanonicalJson.HashObject(new
        {
            version = "P9_DIRECT_REVERSAL_AUDIT_V1",
            RunId = value.Scope.RunId,
            GenerationId = value.Scope.GenerationId,
            reversal.Operation,
            reversal.EventKey,
            reversal.SourceReportId,
            reversal.SourceLifecycleRevision,
            reversal.ReceiptId,
            reversal.PriorRunId,
            reversal.PriorGenerationId,
            reversal.PriorGenerationHash,
            reversal.PriorLedgerHash,
            reversal.PriorReversalBaselineHash,
            reversal.PriorSourceCount,
            reversal.PriorTargetCount,
            reversal.PriorSourceAuditHashes,
            reversal.PriorTargetLedgerHashes
        });
}
