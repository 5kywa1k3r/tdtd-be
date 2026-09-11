using System.Text.Json;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal static class NonFlowRuntimePinCases
{
    internal static Task LockedIncludeUsesNonFlowShape()
    {
        var report = new WorkAssignmentReport
        {
            Id = "non-flow-report-1",
            LifecycleRevision = 2
        };
        var policySha256 =
            StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
                "P10_EXPECTED_NON_FLOW_LOCKED_POLICY_V1",
                [report.Id, "non-flow-policy-owner-1", "2",
                 StatisticReconciliationExpectedContributionPolicies.Include]);
        report.CumulativeContributionPolicyJson = JsonSerializer.Serialize(new
        {
            version = "P10_NON_FLOW_CONTRIBUTION_POLICY_V1",
            locked = true,
            policy = StatisticReconciliationExpectedContributionPolicies.Include,
            ownerId = "non-flow-policy-owner-1",
            revision = 2,
            policySha256
        });

        var locked = StatisticReconciliationExpectedMongoSnapshotReader
            .ResolveLockedNonFlowContribution(report) ??
            throw new InvalidOperationException("Locked policy was not resolved.");
        Equal(StatisticReconciliationExpectedContributionPolicies.Include,
            locked.Policy, "locked policy");
        Equal("non-flow-policy-owner-1", locked.OwnerId, "locked owner");
        Equal(2L, locked.Revision, "locked revision");
        Equal(policySha256, locked.PolicySha256, "locked policy hash");

        // Mirrors the production non-flow BuildContribution binding: the locked
        // policy owner/revision/hash also form the contribution provenance pins.
        var contribution = new ExpectedContributionCandidate(
            "stable-source-1",
            locked.Policy,
            locked.OwnerId,
            locked.Revision,
            locked.PolicySha256,
            locked.OwnerId,
            locked.PolicySha256,
            isLocked: true);
        var context = Context(StatisticReconciliationExpectedRuntimeKinds.NonFlow);
        var baseline = NonFlowPin(context, contribution);
        StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity.Validate(
            baseline,
            context);

        ExpectInvalid(Rehash(baseline with
        {
            MappingReceiptId = "unexpected-mapping-receipt"
        }), context, "partial non-flow mapping");
        ExpectInvalid(Rehash(baseline with
        {
            MappingReceiptId = "unexpected-mapping-receipt",
            MappingProvenanceId = "unexpected-mapping-provenance",
            MappingProvenanceSha256 = Sha("unexpected-mapping-provenance"),
            MappingResultSemanticSha256 = Sha("unexpected-mapping-result"),
            MappingResultPayloadRevision = 1,
            MappingResultPayloadSha256 = Sha("unexpected-mapping-payload"),
            MappingFlowVersionId = "unexpected-mapping-flow-version",
            MappingFlowVersionNo = 1,
            MappingFlowPayloadSha256 = Sha("unexpected-mapping-flow-payload"),
            MappingLocked = true
        }), context, "complete non-flow mapping");
        ExpectInvalid(Rehash(baseline with
        {
            ContributionProvenanceId = string.Empty
        }), context, "missing non-flow provenance owner");
        ExpectInvalid(Rehash(baseline with
        {
            ContributionProvenanceSha256 = "0"
        }), context, "malformed non-flow provenance hash");

        var flowContext = Context(StatisticReconciliationExpectedRuntimeKinds.Flow);
        var flowWithMapping = FlowPin(flowContext, includeMapping: true);
        StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity.Validate(
            flowWithMapping,
            flowContext);
        ExpectInvalid(
            FlowPin(flowContext, includeMapping: false),
            flowContext,
            "flow include without mapping");
        return Task.CompletedTask;
    }

    private static ExpectedLedgerCompilationContextPin Context(string runtimeKind)
        => new(
            reconciliationId: "reconciliation-non-flow-pin-1",
            immutableIdentitySha256: Sha("immutable-identity"),
            immutableHeaderSha256: Sha("immutable-header"),
            tenantUnitId: "tenant-1",
            workId: "work-1",
            scopeAssignmentId: "scope-assignment-1",
            candidateChainId: "p10-chain",
            candidatePromptId: "P10-01",
            periodKey: "MONTH:2026-08",
            periodInstanceKey: "period-instance-1",
            conceptKey: "concept-1",
            grain: "MONTH",
            timeAxis: "APPROVED_AT",
            filterSha256: Sha("filter"),
            dynamicFormVersionId: "form-version-1",
            dynamicFormSchemaSha256: Sha("form-schema"),
            runtimeKind: runtimeKind,
            flowTemplateVersionId: runtimeKind ==
                StatisticReconciliationExpectedRuntimeKinds.Flow
                    ? "flow-template-version-1"
                    : null,
            flowPayloadSha256: runtimeKind ==
                StatisticReconciliationExpectedRuntimeKinds.Flow
                    ? Sha("flow-payload")
                    : null,
            flowInstanceId: runtimeKind ==
                StatisticReconciliationExpectedRuntimeKinds.Flow
                    ? "flow-instance-1"
                    : null,
            executionEpochId: runtimeKind ==
                StatisticReconciliationExpectedRuntimeKinds.Flow
                    ? "execution-epoch-1"
                    : null,
            p8ConfigurationOwnerId: "p8-owner-1",
            p8ConfigurationBundleSha256: Sha("p8-bundle"),
            sourceOwnerRunId: "p9-owner-run-1",
            sourceOwnerGenerationId: Sha("p9-owner-generation-id"),
            sourceOwnerGenerationSha256: Sha("p9-owner-generation"),
            sourceOwnerMembershipSha256: Sha("p9-owner-membership"),
            sourceOwnerRevision: 3);

    private static StatisticReconciliationExpectedAuthoritativeRuntimePin
        NonFlowPin(
            ExpectedLedgerCompilationContextPin context,
            ExpectedContributionCandidate contribution)
        => StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity.Create(
            runtimeKind: context.RuntimeKind,
            flowTemplateVersionId: null,
            flowPayloadSha256: null,
            flowInstanceId: null,
            flowBranchId: null,
            flowStepId: null,
            flowStepInstanceId: null,
            flowStepRevision: null,
            flowAttemptNo: null,
            executionEpochId: null,
            executionEpoch: null,
            currentExecutionEpochId: null,
            currentExecutionEpoch: null,
            executionEpochRevision: null,
            isCanonicalEpoch: null,
            approvalCommandId: null,
            approvalEventKey: null,
            mappingReceiptId: null,
            mappingProvenanceId: null,
            mappingProvenanceSha256: null,
            mappingResultSemanticSha256: null,
            mappingResultPayloadRevision: null,
            mappingResultPayloadSha256: null,
            mappingFlowVersionId: null,
            mappingFlowVersionNo: null,
            mappingFlowPayloadSha256: null,
            mappingLocked: null,
            configVersionId: "stat-config-version-1",
            configSha256: Sha("stat-config"),
            membershipSignatureSha256:
                context.SourceOwnerMembershipSha256!,
            contributionPolicy: contribution.Policy!,
            contributionPolicySha256: contribution.PolicySha256,
            contributionProvenanceId: contribution.ProvenanceId,
            contributionProvenanceSha256: contribution.ProvenanceSha256,
            lifecycleOwnerSha256: Sha("lifecycle-owner"));

    private static StatisticReconciliationExpectedAuthoritativeRuntimePin
        FlowPin(
            ExpectedLedgerCompilationContextPin context,
            bool includeMapping)
        => StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity.Create(
            runtimeKind: context.RuntimeKind,
            flowTemplateVersionId: context.FlowTemplateVersionId,
            flowPayloadSha256: context.FlowPayloadSha256,
            flowInstanceId: context.FlowInstanceId,
            flowBranchId: "flow-branch-1",
            flowStepId: "flow-step-1",
            flowStepInstanceId: "flow-step-instance-1",
            flowStepRevision: 1,
            flowAttemptNo: 1,
            executionEpochId: context.ExecutionEpochId,
            executionEpoch: 1,
            currentExecutionEpochId: context.ExecutionEpochId,
            currentExecutionEpoch: 1,
            executionEpochRevision: 1,
            isCanonicalEpoch: true,
            approvalCommandId: null,
            approvalEventKey: null,
            mappingReceiptId: includeMapping ? "mapping-receipt-1" : null,
            mappingProvenanceId:
                includeMapping ? "mapping-provenance-1" : null,
            mappingProvenanceSha256:
                includeMapping ? Sha("mapping-provenance") : null,
            mappingResultSemanticSha256:
                includeMapping ? Sha("mapping-result") : null,
            mappingResultPayloadRevision: includeMapping ? 1 : null,
            mappingResultPayloadSha256:
                includeMapping ? Sha("mapping-result-payload") : null,
            mappingFlowVersionId:
                includeMapping ? "mapping-flow-version-1" : null,
            mappingFlowVersionNo: includeMapping ? 1 : null,
            mappingFlowPayloadSha256:
                includeMapping ? Sha("mapping-flow-payload") : null,
            mappingLocked: includeMapping ? true : null,
            configVersionId: "stat-config-version-1",
            configSha256: Sha("stat-config"),
            membershipSignatureSha256:
                context.SourceOwnerMembershipSha256!,
            contributionPolicy:
                StatisticReconciliationExpectedContributionPolicies.Include,
            contributionPolicySha256: Sha("flow-include-policy"),
            contributionProvenanceId: "mapping-provenance-1",
            contributionProvenanceSha256: Sha("mapping-provenance"),
            lifecycleOwnerSha256: Sha("lifecycle-owner"));

    private static StatisticReconciliationExpectedAuthoritativeRuntimePin Rehash(
        StatisticReconciliationExpectedAuthoritativeRuntimePin value)
        => value with
        {
            RuntimeSemanticSha256 =
                StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
                    .BuildSemanticSha256(value)
        };

    private static void ExpectInvalid(
        StatisticReconciliationExpectedAuthoritativeRuntimePin value,
        ExpectedLedgerCompilationContextPin context,
        string name)
    {
        try
        {
            StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity.Validate(
                value,
                context);
            throw new InvalidOperationException($"Expected rejection: {name}.");
        }
        catch (StatisticReconciliationExpectedLedgerInputException error)
            when (error.Reason ==
                  StatisticReconciliationExpectedSourcePlanningFailureReasons
                      .LifecycleCandidateInvalid)
        {
        }
    }

    private static string Sha(string value)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            "P10_NON_FLOW_RUNTIME_PIN_TEST_V1",
            value);

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"Assertion failed: {name}; expected={expected}; actual={actual}.");
    }
}
