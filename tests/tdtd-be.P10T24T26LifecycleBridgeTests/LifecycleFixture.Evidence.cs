using System.Collections.Immutable;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsRun;

internal static partial class LifecycleFixture
{
    private static StatisticReconciliationExpectedAuthoritativeLifecycleRow
        Expected(
            Pins pins,
            string status,
            string policy,
            int lifecycleRevision,
            int executionEpoch = 1,
            int currentExecutionEpoch = 1,
            string runtimeKind =
                StatisticReconciliationExpectedRuntimeKinds.Flow)
    {
        var include = policy ==
            StatisticReconciliationExpectedContributionPolicies.Include;
        var payloadRevision = 1;
        var payloadOwner = H($"{pins.CaseId}:payload-owner:{payloadRevision}");
        var payloadCanonical = H(
            $"{pins.CaseId}:payload-canonical:{payloadRevision}");
        var lifecycle = H(
            $"{pins.CaseId}:lifecycle:{status}:{lifecycleRevision}");
        var nonFlow = string.Equals(runtimeKind,
            StatisticReconciliationExpectedRuntimeKinds.NonFlow,
            StringComparison.Ordinal);
        var contributionOwner = nonFlow
            ? O($"{pins.CaseId}:nonflow-policy-owner")
            : pins.FlowTemplateVersionId;
        var contributionRevision = nonFlow ? 7L : 1L;
        var policySha = nonFlow && include
            ? StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
                "P10_EXPECTED_NON_FLOW_LOCKED_POLICY_V1",
                pins.ReportId,
                contributionOwner,
                contributionRevision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                policy)
            : H($"{pins.CaseId}:policy:{policy}");
        var provenanceId = include
            ? nonFlow
                ? contributionOwner
                : O($"{pins.CaseId}:mapping-provenance")
            : "NONE";
        var provenanceSha = include
            ? nonFlow
                ? policySha
                : H($"{pins.CaseId}:mapping-provenance-sha")
            : H($"{pins.CaseId}:no-mapping-provenance");
        var approved = status ==
            StatisticReconciliationExpectedLifecycleStatuses.Approved;
        var approvalCommand = approved
            ? O($"{pins.CaseId}:approval-command")
            : null;
        var approvalEvent = approved
            ? H($"{pins.CaseId}:approval-event")
            : null;
        var currentExecutionEpochId = executionEpoch == currentExecutionEpoch
            ? pins.ExecutionEpochId
            : O($"{pins.CaseId}:current-execution-epoch");
        var runtime =
            StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
                .Create(
                    runtimeKind:
                        runtimeKind,
                    flowTemplateVersionId: nonFlow ? null : pins.FlowTemplateVersionId,
                    flowPayloadSha256: nonFlow ? null : pins.FlowPayloadSha256,
                    flowInstanceId: nonFlow ? null : pins.FlowInstanceId,
                    flowBranchId: nonFlow ? null : pins.FlowBranchId,
                    flowStepId: nonFlow ? null : pins.FlowStepId,
                    flowStepInstanceId: nonFlow ? null : pins.FlowStepInstanceId,
                    flowStepRevision: nonFlow ? null : 1,
                    flowAttemptNo: nonFlow ? null : 1,
                    executionEpochId: nonFlow ? null : pins.ExecutionEpochId,
                    executionEpoch: nonFlow ? null : executionEpoch,
                    currentExecutionEpochId: nonFlow ? null : currentExecutionEpochId,
                    currentExecutionEpoch: nonFlow ? null : currentExecutionEpoch,
                    executionEpochRevision: nonFlow ? null : 1,
                    isCanonicalEpoch: nonFlow
                        ? null
                        : executionEpoch == currentExecutionEpoch,
                    approvalCommandId: approvalCommand,
                    approvalEventKey: approvalEvent,
                    mappingReceiptId: include && !nonFlow
                        ? O($"{pins.CaseId}:mapping-receipt")
                        : null,
                    mappingProvenanceId: include && !nonFlow ? provenanceId : null,
                    mappingProvenanceSha256: include && !nonFlow ? provenanceSha : null,
                    mappingResultSemanticSha256: include && !nonFlow
                        ? H($"{pins.CaseId}:mapping-result-semantic")
                        : null,
                    mappingResultPayloadRevision: include && !nonFlow ? 7 : null,
                    mappingResultPayloadSha256: include && !nonFlow
                        ? H($"{pins.CaseId}:mapping-result-payload")
                        : null,
                    mappingFlowVersionId: include && !nonFlow
                        ? O($"{pins.CaseId}:mapping-flow-version")
                        : null,
                    mappingFlowVersionNo: include && !nonFlow ? 1 : null,
                    mappingFlowPayloadSha256: include && !nonFlow
                        ? H($"{pins.CaseId}:mapping-flow-payload")
                        : null,
                    mappingLocked: include && !nonFlow ? true : null,
                    configVersionId: pins.ConfigVersionId,
                    configSha256: pins.ConfigSha256,
                    membershipSignatureSha256:
                        pins.MembershipSignatureSha256,
                    contributionPolicy: policy,
                    contributionPolicySha256: policySha,
                    contributionProvenanceId: provenanceId,
                    contributionProvenanceSha256: provenanceSha,
                    lifecycleOwnerSha256: lifecycle);
        var neutral =
            StatisticReconciliationExpectedMembershipIntegrity
                .BuildStableIdentitySha256(
                    pins.WorkId,
                    pins.WorkAssignmentId,
                    pins.ReportId);
        var effective = approved &&
            (nonFlow || executionEpoch == currentExecutionEpoch);
        var disposition = !nonFlow && executionEpoch != currentExecutionEpoch
            ? StatisticReconciliationExpectedRuntimeDispositions.Superseded
            : status switch
        {
            StatisticReconciliationExpectedLifecycleStatuses.Terminated =>
                StatisticReconciliationExpectedRuntimeDispositions.Terminated,
            StatisticReconciliationExpectedLifecycleStatuses.Invalidated =>
                StatisticReconciliationExpectedRuntimeDispositions.Invalidated,
            _ => StatisticReconciliationExpectedRuntimeDispositions.Current
        };
        var reason = status switch
        {
            StatisticReconciliationExpectedLifecycleStatuses.Draft or
            StatisticReconciliationExpectedLifecycleStatuses.Submitted =>
                StatisticReconciliationExpectedSourceDecisionReasons
                    .DraftOrUnapproved,
            StatisticReconciliationExpectedLifecycleStatuses.Recalled or
            StatisticReconciliationExpectedLifecycleStatuses.Returned =>
                StatisticReconciliationExpectedSourceDecisionReasons
                    .RecalledOrReturned,
            StatisticReconciliationExpectedLifecycleStatuses.Terminated =>
                StatisticReconciliationExpectedSourceDecisionReasons.Terminated,
            StatisticReconciliationExpectedLifecycleStatuses.Invalidated =>
                StatisticReconciliationExpectedSourceDecisionReasons.Invalidated,
            _ when include =>
                StatisticReconciliationExpectedSourceDecisionReasons.Included,
            _ => StatisticReconciliationExpectedSourceDecisionReasons
                .ContributionDefaultExclude
        };
        var decisionSha = H(
            $"{pins.CaseId}:{pins.GenerationTag}:decision:{neutral}:" +
            $"{status}:{policy}:{lifecycleRevision}:" +
            runtime.RuntimeSemanticSha256);
        var documentSha = H(
            $"{pins.CaseId}:{pins.GenerationTag}:document:{decisionSha}");
        return new StatisticReconciliationExpectedAuthoritativeLifecycleRow(
            neutral,
            neutral,
            neutral,
            pins.WorkId,
            pins.WorkAssignmentId,
            pins.ReportId,
            pins.PayloadDocumentId,
            payloadRevision,
            payloadOwner,
            payloadCanonical,
            lifecycleRevision,
            lifecycle,
            status,
            effective,
            true,
            disposition,
            include ? "INCLUDED" : "EXCLUDED",
            reason,
            policy,
            contributionOwner,
            contributionRevision,
            policySha,
            provenanceId,
            provenanceSha,
            runtime,
            decisionSha,
            documentSha);
    }

    private static StatisticReconciliationExpectedAuthoritativeLifecycleProjection
        Projection(
            Pins pins,
            StatisticReconciliationExpectedAuthoritativeLifecycleRow row) =>
        Projection(pins, [row]);

    private static StatisticReconciliationExpectedAuthoritativeLifecycleProjection
        Projection(
            Pins pins,
            ImmutableArray<StatisticReconciliationExpectedAuthoritativeLifecycleRow>
                rows)
    {
        if (rows.IsDefaultOrEmpty)
            throw new InvalidOperationException("Lifecycle projection rows are required.");
        var binding = new StatisticReconciliationExpectedGenerationBinding(
            pins.ReconciliationId,
            pins.ExpectedGenerationId,
            pins.ExpectedGenerationSha256,
            pins.ExpectedMetricPlanSha256,
            1,
            pins.ExpectedManifestSha256,
            checked(rows.Length * 6),
            pins.ExpectedMembershipSha256,
            rows.Select(static item => item.RuntimePin.RuntimeKind)
                .Distinct(StringComparer.Ordinal).Single());
        var draft =
            new StatisticReconciliationExpectedAuthoritativeLifecycleProjection(
                binding,
                rows,
                H($"{pins.CaseId}:{pins.GenerationTag}:pending-lifecycle"),
                H($"{pins.CaseId}:{pins.GenerationTag}:double-collect"));
        return draft with
        {
            LifecycleManifestSha256 =
                StatisticReconciliationActualLifecycleEvidenceIntegrity
                    .RecomputeExpectedLifecycleManifestSha(draft)
        };
    }

    private static StatisticReconciliationActualLifecycleBuildInput Input(
        Pins pins,
        StatisticReconciliationExpectedAuthoritativeLifecycleRow expected,
        ActualSourceMembershipCapture source,
        ActualDirectProjectionCapture direct,
        StatRunLifecycleContributionAuditSnapshot audit,
        StatisticReconciliationActualLifecyclePriorGeneration? prior) =>
        Input(pins, [expected], source, direct, audit, prior);

    private static StatisticReconciliationActualLifecycleBuildInput Input(
        Pins pins,
        ImmutableArray<StatisticReconciliationExpectedAuthoritativeLifecycleRow>
            expected,
        ActualSourceMembershipCapture source,
        ActualDirectProjectionCapture direct,
        StatRunLifecycleContributionAuditSnapshot audit,
        StatisticReconciliationActualLifecyclePriorGeneration? prior) =>
        new(
            pins.ReconciliationId,
            pins.BaseCoherentGenerationId,
            pins.BaseCoherentGenerationSha256,
            pins.ComparisonBindingSha256,
            Projection(pins, expected),
            source,
            direct,
            audit,
            prior);

    private static StatisticReconciliationActualLifecyclePriorGeneration Prior(
        string caseId,
        StatisticReconciliationActualLifecycleEvidence evidence,
        StatRunLifecycleContributionAuditSnapshot audit)
    {
        var sourceHashes = audit.Sources
            .OrderBy(static item => item.SourceReportId, StringComparer.Ordinal)
            .Select(static item => item.SourceAuditHash)
            .ToImmutableArray();
        var targetHashes = audit.Targets
            .OrderBy(static item => item.TargetStore, StringComparer.Ordinal)
            .ThenBy(static item => item.TargetStatisticId, StringComparer.Ordinal)
            .Select(static item => item.LedgerEntryHash)
            .ToImmutableArray();
        return new StatisticReconciliationActualLifecyclePriorGeneration(
            evidence.Manifest.BaseCoherentGenerationId,
            evidence.Manifest.BaseCoherentGenerationSha256,
            H($"{caseId}:prior-committed-generation"),
            H($"{caseId}:prior-committed-generation-sha"),
            H($"{caseId}:prior-final-verdict-generation"),
            H($"{caseId}:prior-final-verdict-generation-sha"),
            evidence.ManifestSha256,
            evidence.ObservationCount,
            evidence.Manifest.LifecycleRowSetSha256,
            evidence.Result,
            evidence.Request.Sources,
            evidence.Request.Actuals,
            audit.Scope.RunId,
            audit.Scope.GenerationId,
            audit.Scope.GenerationSha256,
            audit.LedgerHash,
            audit.ReversalBaselineHash,
            audit.SnapshotSemanticSha256,
            audit,
            sourceHashes,
            targetHashes);
    }

    private static StatRunLifecycleContributionAuditSnapshot EmptyAudit(
        Pins pins,
        string reportId,
        int lifecycleRevision)
    {
        var unsigned = new StatRunLifecycleContributionAuditSnapshot(
            Scope(pins),
            O($"{pins.CaseId}:{pins.GenerationTag}:receipt"),
            reportId,
            lifecycleRevision,
            H($"{pins.CaseId}:{pins.GenerationTag}:event"),
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
            null,
            string.Empty);
        return StatRunLifecycleContributionAuditCanonical.Normalize(unsigned);
    }

    private static StatRunDirectSourceOwnerScope Scope(Pins pins) => new(
        pins.P9RunId,
        pins.P9GenerationId,
        pins.P9GenerationSha256,
        pins.WorkId,
        pins.PeriodInstanceKey,
        pins.FormTemplateId,
        pins.MembershipSignatureSha256,
        pins.DirectSourceRevision,
        MetricScope(pins));
}
