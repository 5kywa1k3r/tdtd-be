using System.Collections.Immutable;
using System.Globalization;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record ActualSourceMembershipScope(
    string WorkId,
    string PeriodInstanceKey,
    string DynamicFormTemplateId,
    string OwnerMembershipSignature,
    string OwnerRunId,
    string OwnerGenerationId,
    string OwnerGenerationSha256,
    long OwnerDirectSourceRevision,
    StatRunDirectLifecycleMetricScope LifecycleMetricScope);

internal sealed record ActualFlowRuntimeOwnerRevision(
    long FlowFamilyRevision,
    string FlowTemplateId,
    string FlowTemplateVersionId,
    int FlowTemplateVersionNo,
    string FlowOriginVersionId,
    string FlowPayloadSha256,
    string FlowCatalogVersion,
    string FlowCatalogSha256,
    string FlowInstanceId,
    long FlowInstanceRevision,
    string FlowInstanceState,
    int CurrentExecutionEpoch,
    string ExecutionEpochId,
    int ExecutionEpoch,
    long ExecutionEpochRevision,
    string ExecutionEpochState,
    bool IsCanonicalEpoch,
    string StepInstanceId,
    long StepRevision,
    string StepState,
    int StepExecutionEpoch,
    bool? StepIsCanonicalEpoch,
    string? StepReportId,
    int? StepReportLifecycleRevision,
    string? StepReportLifecycleStatus,
    bool? StepReportIsActive,
    DateTime? StepInvalidatedAtUtc,
    string? StepInvalidatedByEventId,
    string? StepSupersededByStepInstanceId,
    string FlowStepId,
    string? FlowBranchId,
    int FlowAttemptNo,
    string ContributionPolicy,
    string ContributionSha256,
    string? ContributionWarning);

internal sealed record ActualSourceOwnerRevision(
    string WorkId,
    string WorkAssignmentId,
    string PeriodInstanceKey,
    string DynamicFormTemplateId,
    string ReportId,
    string PayloadDocumentId,
    int PayloadRevision,
    string PayloadSha256,
    int LifecycleRevision,
    string LifecycleSha256,
    string LifecycleStatus,
    bool IsCurrent,
    bool IsActive,
    bool IsDeleted,
    string? InvalidatedByFlowEventId,
    bool AssignmentIsActive,
    bool PeriodIsActive,
    string PeriodStatus,
    string? PeriodCurrentReportId,
    string? PeriodSourceLifecycleReportId,
    int PeriodSourceLifecycleRevision,
    bool PeriodSourceLifecycleApplied,
    string ContributionDecision,
    bool OwnerIncluded,
    string OwnerDecisionCode,
    int OwnerOrdinal,
    string OwnerRunId,
    string OwnerGenerationId,
    string OwnerGenerationSha256,
    long OwnerDirectSourceRevision,
    long? MappingRevision,
    string? MappingSemanticSha256,
    string? FlowEffectiveStatus,
    ActualFlowRuntimeOwnerRevision? FlowRuntime,
    bool InLifecycleMetricScope);

internal interface IStatisticReconciliationActualSourceOwnerReader
{
    Task<IReadOnlyList<ActualSourceOwnerRevision>> ReadAsync(
        ActualSourceMembershipScope scope,
        CancellationToken cancellationToken);
}

internal sealed record ActualSourceMembershipDecision(
    string SourceStableIdentitySha256,
    bool Included,
    string OwnerDecisionCode,
    int OwnerOrdinal,
    ActualSourceOwnerRevision ObservedOwner,
    string OwnerStateSemanticSha256,
    string DecisionSemanticSha256)
{
    internal string ReportId => ObservedOwner.ReportId;
    internal int PayloadRevision => ObservedOwner.PayloadRevision;
    internal string PayloadSha256 => ObservedOwner.PayloadSha256;
    internal int LifecycleRevision => ObservedOwner.LifecycleRevision;
}

internal sealed record ActualSourceMembershipCapture(
    string WorkId,
    string PeriodInstanceKey,
    string DynamicFormTemplateId,
    string OwnerMembershipSignature,
    string OwnerRunId,
    string OwnerGenerationId,
    string OwnerGenerationSha256,
    long OwnerDirectSourceRevision,
    ImmutableArray<ActualSourceMembershipDecision> Decisions,
    ImmutableArray<ActualSourceMembershipDecision> IncludedSources,
    string SourceSetSha256,
    string CaptureSemanticSha256,
    StatRunDirectLifecycleMetricScope LifecycleMetricScope,
    string? MembershipSemanticSha256 = null,
    string? LifecycleMetricScopeSha256 = null);

internal sealed class StatisticReconciliationActualSourceMembershipAdapter
{
    internal const int MaxSourceCandidates = 49_999;

    internal async Task<ActualSourceMembershipCapture> CaptureAsync(
        ActualSourceMembershipScope scope,
        IStatisticReconciliationActualSourceOwnerReader reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reader);
        var boundary = NormalizeScope(scope);
        var ownerRows = await reader.ReadAsync(boundary, cancellationToken).ConfigureAwait(false)
            ?? throw new StatisticReconciliationActualObservationException("SOURCE_OWNER_ROWS_NULL");
        if (ownerRows.Count > MaxSourceCandidates)
            throw new StatisticReconciliationActualObservationException("SOURCE_OWNER_ROWS_LIMIT");

        var reports = new HashSet<string>(StringComparer.Ordinal);
        var ordinals = new HashSet<int>();
        var decisions = ImmutableArray.CreateBuilder<ActualSourceMembershipDecision>(ownerRows.Count);
        foreach (var ownerRow in ownerRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observed = NormalizeOwner(boundary, ownerRow);
            if (!reports.Add(observed.ReportId))
                throw new StatisticReconciliationActualObservationException("SOURCE_REPORT_AMBIGUOUS");
            if (!ordinals.Add(observed.OwnerOrdinal))
                throw new StatisticReconciliationActualObservationException("SOURCE_OWNER_ORDINAL_AMBIGUOUS");
            decisions.Add(Observe(observed));
        }

        var ordered = decisions
            .OrderBy(x => x.ReportId, StringComparer.Ordinal)
            .ThenBy(x => x.OwnerOrdinal)
            .ToImmutableArray();
        var included = ordered.Where(x => x.Included).ToImmutableArray();
        var sourceSetSha = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_OWNER_SOURCE_SET_V1",
            included.Select(x => x.SourceStableIdentitySha256)
                .OrderBy(x => x, StringComparer.Ordinal));
        var membershipSemanticSha = IncludedMembershipSemanticSha256(included);
        var captureSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_SOURCE_MEMBERSHIP_CAPTURE_V3",
            boundary.WorkId,
            boundary.PeriodInstanceKey,
            boundary.DynamicFormTemplateId,
            boundary.OwnerMembershipSignature,
            boundary.OwnerRunId,
            boundary.OwnerGenerationId,
            boundary.OwnerGenerationSha256,
            StatisticReconciliationActualCanonical.Integer(boundary.OwnerDirectSourceRevision),
            boundary.LifecycleMetricScope.SemanticSha256,
            sourceSetSha,
            membershipSemanticSha,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_SOURCE_OWNER_DECISIONS_V2",
                ordered.Select(x => x.DecisionSemanticSha256)));

        return new ActualSourceMembershipCapture(
            boundary.WorkId,
            boundary.PeriodInstanceKey,
            boundary.DynamicFormTemplateId,
            boundary.OwnerMembershipSignature,
            boundary.OwnerRunId,
            boundary.OwnerGenerationId,
            boundary.OwnerGenerationSha256,
            boundary.OwnerDirectSourceRevision,
            ordered,
            included,
            sourceSetSha,
            captureSha,
            boundary.LifecycleMetricScope,
            membershipSemanticSha,
            boundary.LifecycleMetricScope.SemanticSha256);
    }

    internal static string IncludedMembershipSemanticSha256(
        IEnumerable<ActualSourceMembershipDecision> included)
    {
        ArgumentNullException.ThrowIfNull(included);
        return StatisticReconciliationActualCanonical.HashSequence(
            "P10_INCLUDED_SOURCE_MEMBERSHIP_V1",
            included.Select(value => NeutralStableIdentitySha256(
                    value.ObservedOwner.WorkId,
                    value.ObservedOwner.WorkAssignmentId,
                    value.ObservedOwner.ReportId))
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    internal static string NeutralStableIdentitySha256(
        string workId,
        string workAssignmentId,
        string reportId)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_SOURCE_STABLE_IDENTITY_V1",
            StatisticReconciliationActualCanonical.Required(
                workId, "NEUTRAL_SOURCE_WORK_ID"),
            StatisticReconciliationActualCanonical.Required(
                workAssignmentId, "NEUTRAL_SOURCE_ASSIGNMENT_ID"),
            StatisticReconciliationActualCanonical.Required(
                reportId, "NEUTRAL_SOURCE_REPORT_ID"));

    internal static string ActualStableIdentitySha256(
        string workId,
        string workAssignmentId,
        string reportId)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_SOURCE_STABLE_IDENTITY_V1",
            StatisticReconciliationActualCanonical.Required(
                workId, "ACTUAL_SOURCE_WORK_ID"),
            StatisticReconciliationActualCanonical.Required(
                workAssignmentId, "ACTUAL_SOURCE_ASSIGNMENT_ID"),
            StatisticReconciliationActualCanonical.Required(
                reportId, "ACTUAL_SOURCE_REPORT_ID"));

    internal static void RequireIntegrity(
        ActualSourceMembershipCapture value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var lifecycleMetricScope =
            StatRunDirectLifecycleMetricScopeCanonical.RequireValid(
                value.LifecycleMetricScope);
        var lifecycleScopeSha = StatisticReconciliationActualCanonical.Sha256(
            value.LifecycleMetricScopeSha256,
            "LIFECYCLE_METRIC_SCOPE_SHA256");
        if (lifecycleMetricScope.SemanticSha256 != lifecycleScopeSha)
            throw new StatisticReconciliationActualObservationException(
                "SOURCE_CAPTURE_LIFECYCLE_SCOPE_MISMATCH");
        if (value.Decisions.IsDefault || value.IncludedSources.IsDefault)
            throw new StatisticReconciliationActualObservationException(
                "SOURCE_CAPTURE_ARRAY_REQUIRED");
        var ordered = value.Decisions
            .OrderBy(item => item.ReportId, StringComparer.Ordinal)
            .ThenBy(item => item.OwnerOrdinal)
            .ToImmutableArray();
        var included = ordered.Where(item => item.Included).ToImmutableArray();
        if (!value.Decisions.SequenceEqual(ordered) ||
            !value.IncludedSources.SequenceEqual(included) ||
            ordered.Select(item => item.ReportId)
                .Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new StatisticReconciliationActualObservationException(
                "SOURCE_CAPTURE_ORDER_OR_SET_INVALID");
        var sourceSet = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_OWNER_SOURCE_SET_V1",
            included.Select(item => item.SourceStableIdentitySha256)
                .OrderBy(item => item, StringComparer.Ordinal));
        var membership = IncludedMembershipSemanticSha256(included);
        var capture = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_SOURCE_MEMBERSHIP_CAPTURE_V3",
            value.WorkId,
            value.PeriodInstanceKey,
            value.DynamicFormTemplateId,
            value.OwnerMembershipSignature,
            value.OwnerRunId,
            value.OwnerGenerationId,
            value.OwnerGenerationSha256,
            StatisticReconciliationActualCanonical.Integer(
                value.OwnerDirectSourceRevision),
            lifecycleScopeSha,
            sourceSet,
            membership,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_SOURCE_OWNER_DECISIONS_V2",
                ordered.Select(item => item.DecisionSemanticSha256)));
        if (!string.Equals(value.SourceSetSha256, sourceSet,
                StringComparison.Ordinal) ||
            !string.Equals(value.MembershipSemanticSha256, membership,
                StringComparison.Ordinal) ||
            !string.Equals(value.CaptureSemanticSha256, capture,
                StringComparison.Ordinal))
            throw new StatisticReconciliationActualObservationException(
                "SOURCE_CAPTURE_SEMANTIC_MISMATCH");
    }
    private static ActualSourceMembershipScope NormalizeScope(ActualSourceMembershipScope value)
    {
        if (value.OwnerDirectSourceRevision < 1)
            throw new StatisticReconciliationActualObservationException("OWNER_DIRECT_SOURCE_REVISION_INVALID");
        var lifecycleMetricScope = StatRunDirectLifecycleMetricScopeCanonical
            .RequireValid(value.LifecycleMetricScope);
        return value with
        {
            WorkId = StatisticReconciliationActualCanonical.Required(value.WorkId, "OWNER_WORK_ID"),
            PeriodInstanceKey = StatisticReconciliationActualCanonical.Required(value.PeriodInstanceKey, "OWNER_PERIOD_INSTANCE_KEY"),
            DynamicFormTemplateId = StatisticReconciliationActualCanonical.Required(value.DynamicFormTemplateId, "OWNER_FORM_TEMPLATE_ID"),
            OwnerMembershipSignature = StatisticReconciliationActualCanonical.Sha256(value.OwnerMembershipSignature, "OWNER_MEMBERSHIP_SIGNATURE"),
            OwnerRunId = StatisticReconciliationActualCanonical.Required(value.OwnerRunId, "OWNER_RUN_ID"),
            OwnerGenerationId = StatisticReconciliationActualCanonical.Sha256(value.OwnerGenerationId, "OWNER_GENERATION_ID"),
            OwnerGenerationSha256 = StatisticReconciliationActualCanonical.Sha256(value.OwnerGenerationSha256, "OWNER_GENERATION_SHA256"),
            LifecycleMetricScope = lifecycleMetricScope
        };
    }

    private static ActualSourceOwnerRevision NormalizeOwner(
        ActualSourceMembershipScope boundary,
        ActualSourceOwnerRevision value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.PayloadRevision < 1 || value.LifecycleRevision < 1
            || value.OwnerOrdinal < 0 || value.OwnerDirectSourceRevision < 1)
        {
            throw new StatisticReconciliationActualObservationException("SOURCE_OWNER_REVISION_INVALID");
        }

        var normalized = value with
        {
            WorkId = StatisticReconciliationActualCanonical.Required(value.WorkId, "SOURCE_WORK_ID"),
            WorkAssignmentId = StatisticReconciliationActualCanonical.Required(value.WorkAssignmentId, "SOURCE_ASSIGNMENT_ID"),
            PeriodInstanceKey = StatisticReconciliationActualCanonical.Required(value.PeriodInstanceKey, "SOURCE_PERIOD_INSTANCE_KEY"),
            DynamicFormTemplateId = StatisticReconciliationActualCanonical.Required(value.DynamicFormTemplateId, "SOURCE_FORM_TEMPLATE_ID"),
            ReportId = StatisticReconciliationActualCanonical.Required(value.ReportId, "SOURCE_REPORT_ID"),
            PayloadDocumentId = StatisticReconciliationActualCanonical.Required(value.PayloadDocumentId, "SOURCE_PAYLOAD_DOCUMENT_ID"),
            PayloadSha256 = StatisticReconciliationActualCanonical.Sha256(value.PayloadSha256, "SOURCE_PAYLOAD_SHA256"),
            LifecycleSha256 = StatisticReconciliationActualCanonical.Sha256(value.LifecycleSha256, "SOURCE_LIFECYCLE_SHA256"),
            LifecycleStatus = StatisticReconciliationActualCanonical.Upper(value.LifecycleStatus, "SOURCE_LIFECYCLE_STATUS"),
            InvalidatedByFlowEventId = StatisticReconciliationActualCanonical.Optional(value.InvalidatedByFlowEventId, "SOURCE_INVALIDATED_EVENT"),
            PeriodStatus = StatisticReconciliationActualCanonical.Upper(
                value.PeriodStatus, "SOURCE_PERIOD_STATUS"),
            PeriodCurrentReportId = StatisticReconciliationActualCanonical.Optional(
                value.PeriodCurrentReportId, "SOURCE_PERIOD_CURRENT_REPORT"),
            PeriodSourceLifecycleReportId =
                StatisticReconciliationActualCanonical.Optional(
                    value.PeriodSourceLifecycleReportId,
                    "SOURCE_PERIOD_LIFECYCLE_REPORT"),
            ContributionDecision = StatisticReconciliationActualCanonical.Upper(value.ContributionDecision, "SOURCE_CONTRIBUTION_DECISION"),
            OwnerDecisionCode = StatisticReconciliationActualCanonical.Upper(value.OwnerDecisionCode, "SOURCE_OWNER_DECISION_CODE"),
            OwnerRunId = StatisticReconciliationActualCanonical.Required(value.OwnerRunId, "SOURCE_OWNER_RUN_ID"),
            OwnerGenerationId = StatisticReconciliationActualCanonical.Sha256(value.OwnerGenerationId, "SOURCE_OWNER_GENERATION_ID"),
            OwnerGenerationSha256 = StatisticReconciliationActualCanonical.Sha256(value.OwnerGenerationSha256, "SOURCE_OWNER_GENERATION_SHA256"),
            MappingSemanticSha256 = value.MappingSemanticSha256 is null
                ? null
                : StatisticReconciliationActualCanonical.Sha256(value.MappingSemanticSha256, "SOURCE_MAPPING_SHA256"),
            FlowEffectiveStatus = StatisticReconciliationActualCanonical.Optional(value.FlowEffectiveStatus, "SOURCE_FLOW_EFFECTIVE_STATUS")?.ToUpperInvariant(),
            FlowRuntime = null
        };
        normalized = normalized with
        {
            FlowRuntime = value.FlowRuntime is null ? null : NormalizeFlow(normalized, value.FlowRuntime)
        };
        if (!StringComparer.Ordinal.Equals(normalized.WorkId, boundary.WorkId)
            || !StringComparer.Ordinal.Equals(normalized.PeriodInstanceKey, boundary.PeriodInstanceKey)
            || !StringComparer.Ordinal.Equals(normalized.DynamicFormTemplateId, boundary.DynamicFormTemplateId))
        {
            throw new StatisticReconciliationActualObservationException("SOURCE_SCOPE_MISMATCH");
        }
        if (!StringComparer.Ordinal.Equals(normalized.OwnerRunId, boundary.OwnerRunId)
            || !StringComparer.Ordinal.Equals(normalized.OwnerGenerationId, boundary.OwnerGenerationId)
            || !StringComparer.Ordinal.Equals(normalized.OwnerGenerationSha256, boundary.OwnerGenerationSha256)
            || normalized.OwnerDirectSourceRevision != boundary.OwnerDirectSourceRevision)
        {
            throw new StatisticReconciliationActualObservationException("SOURCE_OWNER_GENERATION_MISMATCH");
        }
        if (normalized.PeriodSourceLifecycleRevision < 0 ||
            normalized.ContributionDecision is not ("INCLUDE" or "EXCLUDE"))
            throw new StatisticReconciliationActualObservationException("SOURCE_CONTRIBUTION_DECISION_INVALID");
        if (normalized.FlowRuntime is null)
        {
            if (normalized.MappingRevision.HasValue || normalized.MappingSemanticSha256 is not null
                || normalized.FlowEffectiveStatus is not null)
            {
                throw new StatisticReconciliationActualObservationException("SOURCE_NONFLOW_PROVENANCE_MIXED");
            }
        }
        else
        {
            if (normalized.FlowEffectiveStatus is null)
                throw new StatisticReconciliationActualObservationException("SOURCE_FLOW_EFFECTIVENESS_REQUIRED");
            var hasMappingRevision = normalized.MappingRevision.HasValue;
            var hasMappingHash = normalized.MappingSemanticSha256 is not null;
            if (hasMappingRevision != hasMappingHash || (hasMappingRevision && normalized.MappingRevision!.Value < 1))
                throw new StatisticReconciliationActualObservationException("SOURCE_FLOW_MAPPING_PIN_PARTIAL");
            if (normalized.OwnerIncluded && normalized.ContributionDecision == "INCLUDE" && !hasMappingRevision)
                throw new StatisticReconciliationActualObservationException("SOURCE_INCLUDED_FLOW_MAPPING_REQUIRED");
        }
        return normalized;
    }

    private static ActualFlowRuntimeOwnerRevision NormalizeFlow(
        ActualSourceOwnerRevision source,
        ActualFlowRuntimeOwnerRevision value)
    {
        if (value.FlowFamilyRevision < 1 || value.FlowTemplateVersionNo < 1
            || value.FlowInstanceRevision < 1 || value.CurrentExecutionEpoch < 1
            || value.ExecutionEpoch < 1 || value.ExecutionEpochRevision < 1
            || value.StepRevision < 1 || value.StepExecutionEpoch < 1 || value.FlowAttemptNo < 1)
        {
            throw new StatisticReconciliationActualObservationException("SOURCE_FLOW_REVISION_INVALID");
        }
        var normalized = value with
        {
            FlowTemplateId = StatisticReconciliationActualCanonical.Required(value.FlowTemplateId, "FLOW_TEMPLATE_ID"),
            FlowTemplateVersionId = StatisticReconciliationActualCanonical.Required(value.FlowTemplateVersionId, "FLOW_TEMPLATE_VERSION_ID"),
            FlowOriginVersionId = StatisticReconciliationActualCanonical.Required(value.FlowOriginVersionId, "FLOW_ORIGIN_VERSION_ID"),
            FlowPayloadSha256 = StatisticReconciliationActualCanonical.Sha256(value.FlowPayloadSha256, "FLOW_PAYLOAD_SHA256"),
            FlowCatalogVersion = StatisticReconciliationActualCanonical.Required(value.FlowCatalogVersion, "FLOW_CATALOG_VERSION"),
            FlowCatalogSha256 = StatisticReconciliationActualCanonical.Sha256(value.FlowCatalogSha256, "FLOW_CATALOG_SHA256"),
            FlowInstanceId = StatisticReconciliationActualCanonical.Required(value.FlowInstanceId, "FLOW_INSTANCE_ID"),
            FlowInstanceState = StatisticReconciliationActualCanonical.Upper(value.FlowInstanceState, "FLOW_INSTANCE_STATE"),
            ExecutionEpochId = StatisticReconciliationActualCanonical.Required(value.ExecutionEpochId, "FLOW_EPOCH_ID"),
            ExecutionEpochState = StatisticReconciliationActualCanonical.Upper(value.ExecutionEpochState, "FLOW_EPOCH_STATE"),
            StepInstanceId = StatisticReconciliationActualCanonical.Required(value.StepInstanceId, "FLOW_STEP_INSTANCE_ID"),
            StepState = StatisticReconciliationActualCanonical.Upper(value.StepState, "FLOW_STEP_STATE"),
            StepReportId = StatisticReconciliationActualCanonical.Optional(value.StepReportId, "FLOW_STEP_REPORT_ID"),
            StepReportLifecycleStatus = StatisticReconciliationActualCanonical.Optional(value.StepReportLifecycleStatus, "FLOW_STEP_REPORT_STATUS")?.ToUpperInvariant(),
            StepInvalidatedAtUtc = StatisticReconciliationActualCanonical.Utc(value.StepInvalidatedAtUtc, "FLOW_STEP_INVALIDATED_AT"),
            StepInvalidatedByEventId = StatisticReconciliationActualCanonical.Optional(value.StepInvalidatedByEventId, "FLOW_STEP_INVALIDATED_EVENT"),
            StepSupersededByStepInstanceId = StatisticReconciliationActualCanonical.Optional(value.StepSupersededByStepInstanceId, "FLOW_STEP_SUPERSEDED_BY"),
            FlowStepId = StatisticReconciliationActualCanonical.Required(value.FlowStepId, "FLOW_STEP_ID"),
            FlowBranchId = StatisticReconciliationActualCanonical.Optional(value.FlowBranchId, "FLOW_BRANCH_ID"),
            ContributionPolicy = StatisticReconciliationActualCanonical.Upper(value.ContributionPolicy, "FLOW_CONTRIBUTION_POLICY"),
            ContributionSha256 = StatisticReconciliationActualCanonical.Sha256(value.ContributionSha256, "FLOW_CONTRIBUTION_SHA256"),
            ContributionWarning = StatisticReconciliationActualCanonical.Optional(value.ContributionWarning, "FLOW_CONTRIBUTION_WARNING", 4096)
        };
        if ((normalized.StepReportId is not null
                && !StringComparer.Ordinal.Equals(normalized.StepReportId, source.ReportId))
            || normalized.StepReportLifecycleRevision != source.LifecycleRevision
            || !StringComparer.Ordinal.Equals(normalized.StepReportLifecycleStatus, source.LifecycleStatus)
            || normalized.StepReportIsActive != source.IsActive)
        {
            throw new StatisticReconciliationActualObservationException("SOURCE_FLOW_REPORT_PIN_DRIFT");
        }
        return normalized;
    }

    private static ActualSourceMembershipDecision Observe(ActualSourceOwnerRevision owner)
    {
        var flow = owner.FlowRuntime;
        var sourceIdentity = ActualStableIdentitySha256(
            owner.WorkId,
            owner.WorkAssignmentId,
            owner.ReportId);
        var stateSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_SOURCE_OWNER_STATE_V2",
            owner.WorkId,
            owner.WorkAssignmentId,
            owner.PeriodInstanceKey,
            owner.DynamicFormTemplateId,
            owner.ReportId,
            owner.PayloadDocumentId,
            owner.PayloadRevision.ToString(CultureInfo.InvariantCulture),
            owner.PayloadSha256,
            owner.LifecycleRevision.ToString(CultureInfo.InvariantCulture),
            owner.LifecycleSha256,
            owner.LifecycleStatus,
            StatisticReconciliationActualCanonical.Boolean(owner.IsCurrent),
            StatisticReconciliationActualCanonical.Boolean(owner.IsActive),
            StatisticReconciliationActualCanonical.Boolean(owner.IsDeleted),
            owner.InvalidatedByFlowEventId,
            StatisticReconciliationActualCanonical.Boolean(owner.AssignmentIsActive),
            StatisticReconciliationActualCanonical.Boolean(owner.PeriodIsActive),
            owner.PeriodStatus,
            owner.PeriodCurrentReportId,
            owner.PeriodSourceLifecycleReportId,
            owner.PeriodSourceLifecycleRevision.ToString(
                CultureInfo.InvariantCulture),
            StatisticReconciliationActualCanonical.Boolean(
                owner.PeriodSourceLifecycleApplied),
            owner.ContributionDecision,
            StatisticReconciliationActualCanonical.Boolean(owner.OwnerIncluded),
            owner.OwnerDecisionCode,
            owner.OwnerOrdinal.ToString(CultureInfo.InvariantCulture),
            owner.OwnerRunId,
            owner.OwnerGenerationId,
            owner.OwnerGenerationSha256,
            StatisticReconciliationActualCanonical.Integer(owner.OwnerDirectSourceRevision),
            owner.MappingRevision.HasValue ? StatisticReconciliationActualCanonical.Integer(owner.MappingRevision.Value) : null,
            owner.MappingSemanticSha256,
            owner.FlowEffectiveStatus,
            flow is null ? null : FlowSemanticSha256(flow),
            StatisticReconciliationActualCanonical.Boolean(
                owner.InLifecycleMetricScope));
        var decisionSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_SOURCE_OWNER_DECISION_V2",
            sourceIdentity,
            StatisticReconciliationActualCanonical.Boolean(owner.OwnerIncluded),
            owner.OwnerDecisionCode,
            owner.OwnerOrdinal.ToString(CultureInfo.InvariantCulture),
            stateSha);
        return new ActualSourceMembershipDecision(
            sourceIdentity,
            owner.OwnerIncluded,
            owner.OwnerDecisionCode,
            owner.OwnerOrdinal,
            owner,
            stateSha,
            decisionSha);
    }

    private static string FlowSemanticSha256(ActualFlowRuntimeOwnerRevision flow)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_SOURCE_FLOW_OWNER_STATE_V2",
            StatisticReconciliationActualCanonical.Integer(flow.FlowFamilyRevision),
            flow.FlowTemplateId,
            flow.FlowTemplateVersionId,
            flow.FlowTemplateVersionNo.ToString(CultureInfo.InvariantCulture),
            flow.FlowOriginVersionId,
            flow.FlowPayloadSha256,
            flow.FlowCatalogVersion,
            flow.FlowCatalogSha256,
            flow.FlowInstanceId,
            StatisticReconciliationActualCanonical.Integer(flow.FlowInstanceRevision),
            flow.FlowInstanceState,
            flow.CurrentExecutionEpoch.ToString(CultureInfo.InvariantCulture),
            flow.ExecutionEpochId,
            flow.ExecutionEpoch.ToString(CultureInfo.InvariantCulture),
            StatisticReconciliationActualCanonical.Integer(flow.ExecutionEpochRevision),
            flow.ExecutionEpochState,
            StatisticReconciliationActualCanonical.Boolean(flow.IsCanonicalEpoch),
            flow.StepInstanceId,
            StatisticReconciliationActualCanonical.Integer(flow.StepRevision),
            flow.StepState,
            flow.StepExecutionEpoch.ToString(CultureInfo.InvariantCulture),
            flow.StepIsCanonicalEpoch.HasValue
                ? StatisticReconciliationActualCanonical.Boolean(flow.StepIsCanonicalEpoch.Value)
                : null,
            flow.StepReportId,
            flow.StepReportLifecycleRevision?.ToString(CultureInfo.InvariantCulture),
            flow.StepReportLifecycleStatus,
            flow.StepReportIsActive.HasValue
                ? StatisticReconciliationActualCanonical.Boolean(flow.StepReportIsActive.Value)
                : null,
            flow.StepInvalidatedAtUtc.HasValue
                ? StatisticReconciliationActualCanonical.Instant(flow.StepInvalidatedAtUtc.Value)
                : null,
            flow.StepInvalidatedByEventId,
            flow.StepSupersededByStepInstanceId,
            flow.FlowStepId,
            flow.FlowBranchId,
            flow.FlowAttemptNo.ToString(CultureInfo.InvariantCulture),
            flow.ContributionPolicy,
            flow.ContributionSha256,
            flow.ContributionWarning);
}
