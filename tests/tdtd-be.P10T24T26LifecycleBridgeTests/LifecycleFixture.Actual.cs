using System.Collections.Immutable;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsRun;

internal static partial class LifecycleFixture
{
    private static ActualSourceOwnerRevision ActualOwner(
        Pins pins,
        StatisticReconciliationExpectedAuthoritativeLifecycleRow expected,
        int ordinal,
        string? actualAssignmentId = null,
        string? actualContributionDecision = null,
        string? actualMappingSemanticSha256 = null,
        string? actualFlowBranchId = null,
        bool inLifecycleMetricScope = true)
    {
        var runtime = expected.RuntimePin;
        var approved = expected.LifecycleStatus ==
            StatisticReconciliationExpectedLifecycleStatuses.Approved;
        var active = expected.LifecycleStatus is not (
            StatisticReconciliationExpectedLifecycleStatuses.Terminated or
            StatisticReconciliationExpectedLifecycleStatuses.Invalidated or
            StatisticReconciliationExpectedLifecycleStatuses.Recalled or
            StatisticReconciliationExpectedLifecycleStatuses.Returned);
        var nonFlow = string.Equals(runtime.RuntimeKind,
            StatisticReconciliationExpectedRuntimeKinds.NonFlow,
            StringComparison.Ordinal);
        var ownerIncluded = approved && expected.IsEffective &&
            expected.IsLocked &&
            expected.ContributionPolicy ==
                StatisticReconciliationExpectedContributionPolicies.Include &&
            expected.RuntimeDisposition ==
                StatisticReconciliationExpectedRuntimeDispositions.Current &&
            (nonFlow ||
             (runtime.IsCanonicalEpoch == true &&
              runtime.ExecutionEpoch == runtime.CurrentExecutionEpoch &&
              runtime.ExecutionEpochId == runtime.CurrentExecutionEpochId));
        var include = expected.ContributionPolicy ==
            StatisticReconciliationExpectedContributionPolicies.Include;
        var contributionDecision = actualContributionDecision ??
            (include ? "INCLUDE" : "EXCLUDE");
        var mappingRevision = include && !nonFlow
            ? runtime.MappingResultPayloadRevision
            : null;
        var mappingSemantic = include && !nonFlow
            ? actualMappingSemanticSha256 ?? runtime.MappingResultSemanticSha256
            : null;
        var flow = nonFlow
            ? null
            : new ActualFlowRuntimeOwnerRevision(
            1,
            O($"{pins.CaseId}:flow-template"),
            runtime.FlowTemplateVersionId!,
            1,
            O($"{pins.CaseId}:flow-origin-version"),
            runtime.FlowPayloadSha256!,
            "catalog-v1",
            H($"{pins.CaseId}:flow-catalog"),
            runtime.FlowInstanceId!,
            1,
            "ACTIVE",
            runtime.CurrentExecutionEpoch!.Value,
            runtime.ExecutionEpochId!,
            runtime.ExecutionEpoch!.Value,
            runtime.ExecutionEpochRevision!.Value,
            "ACTIVE",
            runtime.IsCanonicalEpoch!.Value,
            runtime.FlowStepInstanceId!,
            runtime.FlowStepRevision!.Value,
            "ACTIVE",
            runtime.ExecutionEpoch.Value,
            true,
            expected.ReportId,
            expected.LifecycleRevision,
            expected.LifecycleStatus,
            active,
            null,
            null,
            null,
            runtime.FlowStepId!,
            actualFlowBranchId ?? runtime.FlowBranchId,
            runtime.FlowAttemptNo!.Value,
            include ? "INCLUDE" : "EXCLUDE",
            runtime.ContributionPolicySha256,
            null);
        return new ActualSourceOwnerRevision(
            pins.WorkId,
            actualAssignmentId ?? expected.WorkAssignmentId,
            pins.PeriodInstanceKey,
            pins.FormTemplateId,
            expected.ReportId,
            expected.PayloadDocumentId,
            expected.PayloadRevision,
            expected.PayloadOwnerSha256,
            expected.LifecycleRevision,
            expected.LifecycleSha256,
            expected.LifecycleStatus,
            true,
            active,
            false,
            expected.LifecycleStatus ==
                StatisticReconciliationExpectedLifecycleStatuses.Invalidated
                ? H($"{pins.CaseId}:invalidation-event")
                : null,
            true,
            true,
            "APPROVED",
            expected.ReportId,
            expected.ReportId,
            expected.LifecycleRevision,
            true,
            contributionDecision,
            ownerIncluded,
            $"FIXTURE_{expected.LifecycleStatus}",
            ordinal,
            pins.P9RunId,
            pins.P9GenerationId,
            pins.P9GenerationSha256,
            pins.DirectSourceRevision,
            mappingRevision,
            mappingSemantic,
            nonFlow ? null : "EFFECTIVE",
            flow,
            inLifecycleMetricScope);
    }

    private static StatRunDirectSourceMemberOwnerSnapshot ToP9Snapshot(
        ActualSourceOwnerRevision owner)
        => new(
            owner.WorkId,
            owner.WorkAssignmentId,
            owner.PeriodInstanceKey,
            owner.DynamicFormTemplateId,
            owner.ReportId,
            owner.PayloadDocumentId,
            owner.PayloadRevision,
            owner.PayloadSha256,
            owner.LifecycleRevision,
            owner.LifecycleSha256,
            owner.LifecycleStatus,
            owner.IsCurrent,
            owner.IsActive,
            owner.IsDeleted,
            owner.InvalidatedByFlowEventId,
            owner.AssignmentIsActive,
            owner.PeriodIsActive,
            owner.PeriodStatus,
            owner.PeriodCurrentReportId,
            owner.PeriodSourceLifecycleReportId,
            owner.PeriodSourceLifecycleRevision,
            owner.PeriodSourceLifecycleApplied,
            owner.OwnerOrdinal,
            owner.MappingRevision,
            owner.MappingSemanticSha256,
            owner.FlowEffectiveStatus,
            owner.FlowRuntime is null ? null : ToP9Flow(owner.FlowRuntime),
            owner.OwnerIncluded,
            owner.ContributionDecision,
            owner.OwnerDecisionCode,
            owner.InLifecycleMetricScope);

    private static StatRunDirectSourceFlowOwnerSnapshot ToP9Flow(
        ActualFlowRuntimeOwnerRevision value)
        => new(
            value.FlowFamilyRevision,
            value.FlowTemplateId,
            value.FlowTemplateVersionId,
            value.FlowTemplateVersionNo,
            value.FlowOriginVersionId,
            value.FlowPayloadSha256,
            value.FlowCatalogVersion,
            value.FlowCatalogSha256,
            value.FlowInstanceId,
            value.FlowInstanceRevision,
            value.FlowInstanceState,
            value.CurrentExecutionEpoch,
            value.ExecutionEpochId,
            value.ExecutionEpoch,
            value.ExecutionEpochRevision,
            value.ExecutionEpochState,
            value.IsCanonicalEpoch,
            value.StepInstanceId,
            value.StepRevision,
            value.StepState,
            value.StepExecutionEpoch,
            value.StepIsCanonicalEpoch,
            value.StepReportId,
            value.StepReportLifecycleRevision,
            value.StepReportLifecycleStatus,
            value.StepReportIsActive,
            value.StepInvalidatedAtUtc,
            value.StepInvalidatedByEventId,
            value.StepSupersededByStepInstanceId,
            value.FlowStepId,
            value.FlowBranchId,
            value.FlowAttemptNo,
            value.ContributionPolicy,
            value.ContributionSha256,
            value.ContributionWarning);
    private static async Task<ActualSourceMembershipCapture> CaptureSourceAsync(
        Pins pins,
        ImmutableArray<ActualSourceOwnerRevision> owners,
        int currentScopeEpoch = 1)
    {
        var lifecycleMetricScope = MetricScope(pins, currentScopeEpoch);
        var scope = new ActualSourceMembershipScope(
            pins.WorkId,
            pins.PeriodInstanceKey,
            pins.FormTemplateId,
            pins.MembershipSignatureSha256,
            pins.P9RunId,
            pins.P9GenerationId,
            pins.P9GenerationSha256,
            pins.DirectSourceRevision,
            lifecycleMetricScope);
        var p9Scope = new StatRunDirectSourceOwnerScope(
            pins.P9RunId,
            pins.P9GenerationId,
            pins.P9GenerationSha256,
            pins.WorkId,
            pins.PeriodInstanceKey,
            pins.FormTemplateId,
            pins.MembershipSignatureSha256,
            pins.DirectSourceRevision,
            lifecycleMetricScope);
        var snapshot = new StatRunDirectSourceOwnerSnapshot(
            p9Scope,
            owners.Select(ToP9Snapshot).ToArray());
        var reader = new StatisticReconciliationActualSourceMongoOwnerReader(
            new P9OwnerReader(snapshot));
        return await new StatisticReconciliationActualSourceMembershipAdapter()
            .CaptureAsync(scope, reader);
    }

    internal static async Task<ActualSourceMembershipCapture>
        RecaptureExcludedForFenceAsync(
            string caseId,
            string ownerDecisionCode,
            bool omit)
    {
        var pins = Pins.Create(caseId, "CURRENT");
        var expected = Expected(
            pins,
            StatisticReconciliationExpectedLifecycleStatuses.Draft,
            StatisticReconciliationExpectedContributionPolicies.Exclude,
            lifecycleRevision: 1);
        if (omit)
            return await CaptureSourceAsync(
                pins, ImmutableArray<ActualSourceOwnerRevision>.Empty);
        var owner = ActualOwner(pins, expected, ordinal: 0) with
        {
            OwnerDecisionCode = ownerDecisionCode
        };
        return await CaptureSourceAsync(pins, [owner]);
    }

    private static async Task<ActualDirectProjectionCapture> CaptureDirectAsync(
        Pins pins,
        ActualSourceMembershipCapture source)
    {
        var boundary = new ActualDirectProjectionBoundary(
            pins.WorkId,
            pins.PeriodInstanceKey,
            pins.FormFamilyId,
            pins.FormTemplateId,
            1,
            H($"{pins.CaseId}:form-schema"),
            pins.P9RunId,
            pins.P9GenerationId,
            pins.P9GenerationSha256,
            H($"{pins.CaseId}:{pins.GenerationTag}:lifecycle-event"),
            DateTime.UnixEpoch.AddDays(1),
            pins.DirectSourceRevision,
            O($"{pins.CaseId}:config"),
            pins.ConfigVersionId,
            1,
            1,
            pins.ConfigSha256,
            "candidate-chain-v1",
            "catalog-v1",
            H($"{pins.CaseId}:catalog-raw"),
            H($"{pins.CaseId}:catalog-semantic"),
            H($"{pins.CaseId}:schema-raw"),
            H($"{pins.CaseId}:schema-semantic"),
            H($"{pins.CaseId}:stage-lock"),
            pins.MembershipSignatureSha256);
        return await new StatisticReconciliationActualDirectProjectionAdapter()
            .CaptureAsync(boundary, source, new EmptyDirectOwnerReader());
    }

    private static StatRunDirectLifecycleMetricScope MetricScope(
        Pins pins,
        int currentScopeEpoch = 1)
        => StatRunDirectLifecycleMetricScopeCanonical.Create(
            StatRunDirectLifecycleMetricFamilies.Direct,
            StatRunDirectLifecycleMetricFamilies.AllCandidates,
            scopeId: null,
            scopeAssignmentId: pins.WorkAssignmentId,
            flowInstanceId: pins.FlowInstanceId,
            runtimeFlowBranchId: pins.FlowBranchId,
            runtimeFlowStepId: pins.FlowStepId,
            flowEffectiveStatus: "EFFECTIVE",
            executionEpochId: currentScopeEpoch == 1
                ? pins.ExecutionEpochId
                : O($"{pins.CaseId}:current-execution-epoch"),
            executionEpoch: currentScopeEpoch,
            executionEpochRevision: 1,
            configurationKind: "BASIC",
            configurationOwnerId: O($"{pins.CaseId}:p8-owner"),
            configurationVersionId: pins.ConfigVersionId,
            configurationVersionNo: 1,
            configurationRevision: 1,
            configurationOwnerSha256: pins.ConfigSha256,
            configurationCanonicalJsonSha256:
                H($"{pins.CaseId}:p8-config-canonical"),
            capturePlanSha256: H($"{pins.CaseId}:capture-plan"),
            immutableSelectorSha256:
                H($"{pins.CaseId}:basic-selector"));
    private sealed class P9OwnerReader(
        StatRunDirectSourceOwnerSnapshot snapshot)
        : IStatRunDirectProjectionReadOwner
    {
        public Task<StatRunDirectSourceOwnerSnapshot> ReadSourceOwnerAsync(
            StatRunDirectSourceOwnerScope scope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scope != snapshot.Scope)
                throw new InvalidOperationException("P9 owner scope mismatch.");
            return Task.FromResult(snapshot);
        }
    }
    private sealed class EmptyDirectOwnerReader
        : IStatisticReconciliationActualDirectProjectionOwnerReader
    {
        public Task<IReadOnlyList<WorkReportFieldStatValue>> ReadFieldRowsAsync(
            ActualDirectProjectionBoundary boundary,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkReportFieldStatValue>>([]);

        public Task<IReadOnlyList<WorkReportTableStatValue>>
            ReadTableMetricRowsAsync(
                ActualDirectProjectionBoundary boundary,
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkReportTableStatValue>>([]);

        public Task<IReadOnlyList<WorkReportLabelStatValue>> ReadRowLabelRowsAsync(
            ActualDirectProjectionBoundary boundary,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkReportLabelStatValue>>([]);
    }
}
