using System.Collections.Immutable;
using System.Globalization;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

internal sealed class StatisticReconciliationActualLifecycleBridge(
    StatisticReconciliationLifecycleEvaluator evaluator)
    : IStatisticReconciliationActualLifecycleBridge
{
    public StatisticReconciliationActualLifecycleEvidence Build(
        StatisticReconciliationActualLifecycleBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateCaptureBinding(input);
        ValidatePrior(input.ReconciliationId, input.Prior);
        input = input with
        {
            P9ContributionAudit =
                StatRunLifecycleContributionAuditCanonical.Normalize(
                    input.P9ContributionAudit)
        };

        var compactedPrior = CompactPrior(input);
        var priorSources = compactedPrior.Sources;
        var priorActuals = compactedPrior.Actuals;
        var sourceTimeline = priorSources.ToBuilder();
        var actualTimeline = priorActuals.ToBuilder();
        var latestSources = LatestSources(priorSources);
        var latestActuals = LatestActuals(priorSources, priorActuals);
        var expectedByStableId = input.Expected.Rows.ToDictionary(
            static item => item.SourceStableIdentitySha256,
            StringComparer.Ordinal);
        var actualByStableId = input.Source.Decisions
            .Where(static item => item.ObservedOwner.InLifecycleMetricScope)
            .ToDictionary(
                static item => NeutralStableIdentity(item.ObservedOwner),
                StringComparer.Ordinal);
        var stableIds = latestSources.Keys
            .Concat(expectedByStableId.Keys)
            .Concat(actualByStableId.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var stableId in stableIds)
        {
            latestSources.TryGetValue(stableId, out var previousSource);
            latestActuals.TryGetValue(stableId, out var previousActual);
            expectedByStableId.TryGetValue(stableId, out var decision);
            var sequence = (previousSource?.Sequence ?? 0) + 1;
            if (decision is null)
            {
                actualByStableId.TryGetValue(stableId, out var currentActualSource);
                var reportId = currentActualSource?.ReportId ?? previousSource?.ReportId;
                var hasAuditForMissingDecision = reportId is not null &&
                    input.P9ContributionAudit.Sources.Any(item => string.Equals(
                        item.SourceReportId, reportId, StringComparison.Ordinal));
                if (currentActualSource is not null)
                {
                    AppendExtraSourceActual(
                        input,
                        actualByStableId,
                        stableId,
                        sequence,
                        actualTimeline);
                    continue;
                }
                if (hasAuditForMissingDecision)
                {
                    // AppendExtraAuditActuals emits exactly one unmatched Actual.
                    continue;
                }
                if (previousSource is null || !IsContributing(previousSource))
                {
                    // A validated rebased zero baseline remains sufficient evidence.
                    continue;
                }
            }
            var source = decision is not null
                ? CreateCurrentSource(input, decision, sequence, previousSource)
                : CreateAbsentSource(
                    input,
                    stableId,
                    sequence,
                    previousSource ?? throw new InvalidOperationException(
                        "Contributing prior lifecycle source is required."));
            sourceTimeline.Add(source);

            var expectedBefore = previousSource is not null &&
                IsContributing(previousSource);
            var expectedAfter = IsContributing(source);
            var hasCurrentSource = actualByStableId.ContainsKey(stableId);
            var hasCurrentAudit = input.P9ContributionAudit.Sources.Any(item =>
                string.Equals(item.SourceReportId,
                    source.ReportId, StringComparison.Ordinal));
            var exactReversalCandidate = expectedBefore && !expectedAfter;
            if (!hasCurrentSource &&
                !hasCurrentAudit &&
                !exactReversalCandidate)
            {
                continue;
            }
            var actualEvidence = ResolveActualMembershipUnit(
                input,
                source,
                previousSource,
                expectedBefore,
                expectedAfter);
            var actualBefore = previousActual is null
                ? 0m
                : Parse(previousActual.CanonicalContribution);
            var actualAfter = actualEvidence.Unit;
            var actualGenerationId = StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_MEMBERSHIP_ACTUAL_GENERATION_V3",
                input.ReconciliationId,
                input.BaseCoherentGenerationId,
                input.BaseCoherentGenerationSha256,
                input.P9ContributionAudit.SnapshotSemanticSha256,
                stableId,
                source.ObservationId);
            var actualObservationId = StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_MEMBERSHIP_ACTUAL_OBSERVATION_V3",
                input.ReconciliationId,
                input.BaseCoherentGenerationId,
                stableId,
                source.ObservationId,
                actualEvidence.EvidenceBindingSha256);
            var priorEffectiveSha = expectedBefore && !expectedAfter
                ? previousActual?.GenerationSha256
                : null;
            var actual = evaluator.CreateActual(
                actualObservationId,
                source,
                actualGenerationId,
                previousActual?.GenerationId,
                priorEffectiveSha,
                Format(actualAfter),
                Format(actualAfter - actualBefore),
                evidenceComplete: actualEvidence.EvidenceComplete);
            actualTimeline.Add(actual);
        }

        AppendExtraAuditActuals(
            input,
            actualTimeline);
        var sources = sourceTimeline
            .OrderBy(static item => item.Sequence)
            .ThenBy(static item => item.ObservationId, StringComparer.Ordinal)
            .ToImmutableArray();
        var actuals = actualTimeline
            .OrderBy(static item => item.SourceObservationId, StringComparer.Ordinal)
            .ThenBy(static item => item.ObservationId, StringComparer.Ordinal)
            .ToImmutableArray();
        var request = new StatisticReconciliationLifecycleRequest(
            input.ReconciliationId,
            input.ComparisonBindingSha256,
            sources,
            actuals);
        var result = sources.IsEmpty && actuals.IsEmpty
            ? CreateEmptyResult(request)
            : evaluator.Evaluate(request);
        return StatisticReconciliationActualLifecycleEvidenceIntegrity.Create(
            input,
            request,
            result,
            compactedPrior);
    }

    private StatisticReconciliationActualLifecyclePriorCompaction CompactPrior(
        StatisticReconciliationActualLifecycleBuildInput input)
    {
        var prior = input.Prior;
        if (prior is null)
        {
            return StatisticReconciliationActualLifecyclePriorCompaction.Empty;
        }
        if (prior.Sources.IsDefaultOrEmpty || prior.Actuals.IsDefaultOrEmpty ||
            prior.Sources.Select(static item => item.ObservationId)
                .Distinct(StringComparer.Ordinal).Count() != prior.Sources.Length ||
            prior.Actuals.Select(static item => item.ObservationId)
                .Distinct(StringComparer.Ordinal).Count() != prior.Actuals.Length)
        {
            throw new InvalidOperationException(
                "Prior lifecycle timeline cannot form a compacted baseline.");
        }

        var sourceByObservation = prior.Sources.ToDictionary(
            static item => item.ObservationId,
            StringComparer.Ordinal);
        if (prior.Actuals.Any(item =>
                !sourceByObservation.TryGetValue(item.SourceObservationId, out var source) ||
                !string.Equals(source.StableSourceId,
                    item.StableSourceId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Prior lifecycle Actual row is outside the immutable Source timeline.");
        }

        var compactedSources = ImmutableArray
            .CreateBuilder<StatisticReconciliationLifecycleSourceState>();
        var compactedActuals = ImmutableArray
            .CreateBuilder<StatisticReconciliationLifecycleActualContribution>();
        foreach (var group in prior.Sources
                     .GroupBy(static item => item.StableSourceId, StringComparer.Ordinal)
                     .OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderBy(static item => item.Sequence)
                .ThenBy(static item => item.ObservationId, StringComparer.Ordinal)
                .ToArray();
            if (ordered.Select(static item => item.Sequence).Distinct().Count() !=
                ordered.Length)
            {
                throw new InvalidOperationException(
                    "Prior lifecycle Source sequence is ambiguous.");
            }
            var originalSource = ordered[^1];
            var actualCandidates = prior.Actuals.Where(item => string.Equals(
                    item.SourceObservationId,
                    originalSource.ObservationId,
                    StringComparison.Ordinal))
                .ToArray();
            if (actualCandidates.Length != 1 ||
                !actualCandidates[0].EvidenceComplete)
            {
                throw new InvalidOperationException(
                    "Prior lifecycle latest Source/Actual pair is missing or ambiguous.");
            }
            var originalActual = actualCandidates[0];
            var contribution = Parse(originalActual.CanonicalContribution);
            if (contribution is not 0m and not 1m)
            {
                throw new InvalidOperationException(
                    "Prior lifecycle membership unit is outside {0,1}.");
            }
            var pairBinding =
                StatisticReconciliationActualLifecycleEvidenceIntegrity
                    .ComputePriorCompactionPairBinding(
                        prior,
                        group.Key,
                        originalSource.SemanticSha256,
                        originalActual.SemanticSha256);
            var compactedSource = evaluator.CreateSource(
                StatisticReconciliationLifecycleCanonical.Hash(
                    StatisticReconciliationActualLifecycleLedgerSchema
                        .CompactedPriorBaseline,
                    "SOURCE_OBSERVATION",
                    pairBinding),
                1,
                StatisticReconciliationLifecycleEventKinds.Rebuild,
                originalSource.StableSourceId,
                originalSource.ReportId,
                originalSource.BranchId,
                originalSource.StepId,
                originalSource.EpochId,
                originalSource.Epoch,
                originalSource.CurrentEpochId,
                originalSource.LifecycleStatus,
                originalSource.IsEffective,
                originalSource.ContributionPolicy,
                originalSource.MappingVersionId,
                originalSource.MappingRevision,
                originalSource.MappingVersionLocked,
                originalSource.MappingSha256,
                originalSource.ProvenanceId,
                originalSource.ProvenanceSha256,
                originalSource.SemanticSha256,
                pairBinding,
                originalSource.CanonicalValue);
            var compactedActual = evaluator.CreateActual(
                StatisticReconciliationLifecycleCanonical.Hash(
                    StatisticReconciliationActualLifecycleLedgerSchema
                        .CompactedPriorBaseline,
                    "ACTUAL_OBSERVATION",
                    pairBinding),
                compactedSource,
                originalActual.SemanticSha256,
                null,
                null,
                originalActual.CanonicalContribution,
                originalActual.CanonicalContribution,
                originalActual.EpochId,
                originalActual.ContributionPolicy,
                originalActual.MappingVersionId,
                originalActual.MappingRevision,
                originalActual.MappingSha256,
                originalActual.ProvenanceId,
                originalActual.ProvenanceSha256,
                true);
            compactedSources.Add(compactedSource);
            compactedActuals.Add(compactedActual);
        }
        if (compactedSources.Count == 0)
        {
            throw new InvalidOperationException(
                "Prior lifecycle compaction requires at least one identity.");
        }
        var sources = compactedSources.ToImmutable();
        var actuals = compactedActuals.ToImmutable();
        var proof = StatisticReconciliationActualLifecycleEvidenceIntegrity
            .ComputePriorCompactionProofSha(prior, sources, actuals);
        return new StatisticReconciliationActualLifecyclePriorCompaction(
            sources,
            actuals,
            proof,
            sources.Length);
    }

    private StatisticReconciliationLifecycleSourceState CreateCurrentSource(
        StatisticReconciliationActualLifecycleBuildInput input,
        StatisticReconciliationExpectedAuthoritativeLifecycleRow decision,
        long sequence,
        StatisticReconciliationLifecycleSourceState? previous)
    {
        var runtime = decision.RuntimePin ?? throw new InvalidOperationException(
            "Authoritative lifecycle runtime pin is required.");
        var status = NormalizeStatus(decision.LifecycleStatus);
        var includePolicy = string.Equals(
            decision.ContributionPolicy,
            StatisticReconciliationLifecyclePolicies.Include,
            StringComparison.Ordinal);
        var policy = includePolicy
            ? StatisticReconciliationLifecyclePolicies.Include
            : StatisticReconciliationLifecyclePolicies.Exclude;
        var isFlow = string.Equals(runtime.RuntimeKind, "FLOW",
            StringComparison.Ordinal);
        var branchId = runtime.FlowBranchId ??
            $"NON_FLOW:{decision.WorkAssignmentId}";
        var stepId = runtime.FlowStepId ??
            $"NON_FLOW:{decision.ReportId}";
        var epochId = runtime.ExecutionEpochId ??
            $"NON_FLOW_EPOCH:{decision.WorkAssignmentId}";
        var epoch = runtime.ExecutionEpoch ?? 1;
        var currentEpochId = runtime.CurrentExecutionEpochId ?? epochId;
        var currentEpoch = runtime.CurrentExecutionEpoch ?? epoch;
        var mappingVersionId = includePolicy
            ? decision.ContributionVersionId
            : null;
        var mappingRevision = includePolicy
            ? decision.ContributionRevision
            : (long?)null;
        var mappingSha = includePolicy
            ? decision.ContributionPolicySha256
            : null;
        var provenanceId = includePolicy
            ? decision.ContributionProvenanceId
            : null;
        var provenanceSha = includePolicy
            ? decision.ContributionProvenanceSha256
            : null;
        var lockedProvenance = !includePolicy ||
            (isFlow ? runtime.MappingLocked == true : decision.IsLocked);
        if (includePolicy &&
            (mappingRevision is null or <= 0 ||
             !lockedProvenance ||
             !IsSha(mappingSha) ||
             !IsSha(provenanceSha) ||
             string.IsNullOrWhiteSpace(mappingVersionId) ||
             string.IsNullOrWhiteSpace(provenanceId)))
        {
            throw new InvalidOperationException(
                "V_INCLUDE requires exact locked policy and provenance pins.");
        }
        var currentFlowEpoch = runtime.IsCanonicalEpoch == true &&
            epoch == currentEpoch &&
            string.Equals(epochId, currentEpochId, StringComparison.Ordinal);
        var historicalFlowEpoch = !includePolicy &&
            runtime.IsCanonicalEpoch == false &&
            epoch > 0 && epoch < currentEpoch &&
            !string.Equals(epochId, currentEpochId, StringComparison.Ordinal) &&
            !decision.IsEffective &&
            string.Equals(decision.RuntimeDisposition,
                StatisticReconciliationExpectedRuntimeDispositions.Superseded,
                StringComparison.Ordinal);
        if (isFlow &&
            (runtime.ExecutionEpoch is null or <= 0 ||
             runtime.CurrentExecutionEpoch is null or <= 0 ||
             string.IsNullOrWhiteSpace(runtime.FlowBranchId) ||
             string.IsNullOrWhiteSpace(runtime.FlowStepId) ||
             string.IsNullOrWhiteSpace(runtime.ExecutionEpochId) ||
             string.IsNullOrWhiteSpace(runtime.CurrentExecutionEpochId) ||
             (!currentFlowEpoch && !historicalFlowEpoch)))
        {
            throw new InvalidOperationException(
                "FLOW lifecycle row requires exact current or bounded historical epoch pins.");
        }

        var eventKind = DetermineEvent(status, decision, previous);
        var effective = decision.IsEffective &&
            decision.IsLocked &&
            string.Equals(decision.RuntimeDisposition,
                "CURRENT", StringComparison.Ordinal) &&
            (!isFlow ||
             (runtime.IsCanonicalEpoch == true &&
              string.Equals(epochId, currentEpochId, StringComparison.Ordinal) &&
              epoch == currentEpoch));
        var observationId = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_EXPECTED_SOURCE_OBSERVATION_V4",
            input.ReconciliationId,
            input.BaseCoherentGenerationId,
            input.Expected.LifecycleManifestSha256,
            input.Expected.DoubleCollectProofSha256,
            decision.SourceStableIdentitySha256,
            sequence.ToString(CultureInfo.InvariantCulture),
            decision.DecisionSemanticSha256,
            runtime.RuntimeSemanticSha256);
        return evaluator.CreateSource(
            observationId,
            sequence,
            eventKind,
            decision.SourceStableIdentitySha256,
            decision.ReportId,
            branchId,
            stepId,
            epochId,
            epoch,
            currentEpochId,
            status,
            effective,
            policy,
            mappingVersionId,
            mappingRevision,
            lockedProvenance,
            mappingSha,
            provenanceId,
            provenanceSha,
            input.Expected.Binding.GenerationId,
            input.Expected.Binding.GenerationSemanticSha256,
            "1");
    }    private StatisticReconciliationLifecycleSourceState CreateAbsentSource(
        StatisticReconciliationActualLifecycleBuildInput input,
        string stableId,
        long sequence,
        StatisticReconciliationLifecycleSourceState previous)
    {
        var observationId = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_MEMBERSHIP_SOURCE_ABSENT_V2",
            input.ReconciliationId,
            input.BaseCoherentGenerationId,
            stableId,
            sequence.ToString(CultureInfo.InvariantCulture),
            input.Expected.LifecycleManifestSha256,
            previous.SourceGenerationId,
            previous.SourceGenerationSha256);
        var reversal = input.P9ContributionAudit.Reversal;
        var (eventKind, status) = reversal is not null &&
            string.Equals(
                reversal.SourceReportId,
                previous.ReportId,
                StringComparison.Ordinal)
            ? reversal.Operation switch
            {
                "REVIEW_RECALL_APPROVED" or "WITHDRAW" =>
                    (StatisticReconciliationLifecycleEventKinds.Recall,
                     StatisticReconciliationLifecycleStatuses.Recalled),
                "REVIEW_RETURN" =>
                    (StatisticReconciliationLifecycleEventKinds.Return,
                     StatisticReconciliationLifecycleStatuses.Returned),
                "REVIEW_DEACTIVATE_REPORT" =>
                    (StatisticReconciliationLifecycleEventKinds.Terminate,
                     StatisticReconciliationLifecycleStatuses.Terminated),
                "AUTO_AGGREGATE_REVIEW_INVALIDATED" =>
                    (StatisticReconciliationLifecycleEventKinds.Invalidate,
                     StatisticReconciliationLifecycleStatuses.Invalidated),
                _ =>
                    (StatisticReconciliationLifecycleEventKinds.Rebuild,
                     previous.LifecycleStatus)
            }
            : (StatisticReconciliationLifecycleEventKinds.Rebuild,
               previous.LifecycleStatus);
        return evaluator.CreateSource(
            observationId,
            sequence,
            eventKind,
            stableId,
            previous.ReportId,
            previous.BranchId,
            previous.StepId,
            previous.EpochId,
            previous.Epoch,
            previous.CurrentEpochId,
            status,
            false,
            previous.ContributionPolicy,
            previous.MappingVersionId,
            previous.MappingRevision,
            previous.MappingVersionLocked,
            previous.MappingSha256,
            previous.ProvenanceId,
            previous.ProvenanceSha256,
            input.Expected.Binding.GenerationId,
            input.Expected.Binding.GenerationSemanticSha256,
            "1");
    }

    private StatisticReconciliationLifecycleSourceState CreateExtraActualSource(
        StatisticReconciliationActualLifecycleBuildInput input,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision>
            actualByStableId,
        string stableId,
        long sequence)
    {
        if (!actualByStableId.TryGetValue(stableId, out var actualDecision))
        {
            throw new InvalidOperationException(
                "P9 actual source identity is missing.");
        }
        var owner = actualDecision.ObservedOwner;
        var observationId = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_EXTRA_ACTUAL_SOURCE_V3",
            input.ReconciliationId,
            input.BaseCoherentGenerationId,
            input.Source.CaptureSemanticSha256,
            stableId,
            owner.ReportId);
        var flow = owner.FlowRuntime;
        return evaluator.CreateSource(
            observationId,
            sequence,
            StatisticReconciliationLifecycleEventKinds.Observe,
            stableId,
            owner.ReportId,
            flow?.FlowBranchId ?? $"ACTUAL_ONLY:{owner.WorkAssignmentId}",
            flow?.FlowStepId ?? $"ACTUAL_ONLY:{owner.ReportId}",
            flow?.ExecutionEpochId ?? $"ACTUAL_ONLY:{owner.OwnerRunId}",
            flow?.ExecutionEpoch ?? 1,
            flow is null || flow.ExecutionEpoch == flow.CurrentExecutionEpoch
                ? flow?.ExecutionEpochId ?? $"ACTUAL_ONLY:{owner.OwnerRunId}"
                : $"ACTUAL_ONLY_CURRENT:{flow.FlowInstanceId}:{flow.CurrentExecutionEpoch}",
            StatisticReconciliationLifecycleStatuses.Draft,
            false,
            StatisticReconciliationLifecyclePolicies.Exclude,
            null,
            null,
            false,
            null,
            null,
            null,
            input.Source.OwnerGenerationId,
            input.Source.OwnerGenerationSha256,
            "0");
    }

    private void AppendExtraSourceActual(
        StatisticReconciliationActualLifecycleBuildInput input,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> actualByStableId,
        string stableId,
        long sequence,
        ImmutableArray<StatisticReconciliationLifecycleActualContribution>.Builder actuals)
    {
        var syntheticSource = CreateExtraActualSource(
            input,
            actualByStableId,
            stableId,
            sequence);
        actuals.Add(evaluator.CreateActual(
            StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_EXTRA_SOURCE_ACTUAL_V1",
                input.BaseCoherentGenerationId,
                syntheticSource.ObservationId),
            syntheticSource,
            StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_EXTRA_SOURCE_GENERATION_V1",
                input.BaseCoherentGenerationId,
                syntheticSource.SemanticSha256),
            null,
            null,
            "1",
            "1",
            evidenceComplete: true));
    }

    private static ActualMembershipEvidence ResolveActualMembershipUnit(
        StatisticReconciliationActualLifecycleBuildInput input,
        StatisticReconciliationLifecycleSourceState source,
        StatisticReconciliationLifecycleSourceState? previous,
        bool expectedBefore,
        bool expectedAfter)
    {
        var audits = input.P9ContributionAudit.Sources
            .Where(item => string.Equals(
                item.SourceReportId,
                source.ReportId,
                StringComparison.Ordinal))
            .ToArray();
        if (audits.Length > 1)
        {
            throw new InvalidOperationException(
                "P9 lifecycle source audit is not unique by report.");
        }

        var actualSource = input.Source.Decisions.SingleOrDefault(item =>
            item.ObservedOwner.InLifecycleMetricScope &&
            string.Equals(NeutralStableIdentity(item.ObservedOwner),
                source.StableSourceId, StringComparison.Ordinal));
        var actualOnly = actualSource is not null &&
            !input.Expected.Rows.Any(item => string.Equals(
                item.SourceStableIdentitySha256,
                source.StableSourceId,
                StringComparison.Ordinal));
        var unit = audits.Length == 1 || actualOnly
            ? 1m
            : 0m;
        var expectedDecision = input.Expected.Rows.SingleOrDefault(item =>
            string.Equals(item.SourceStableIdentitySha256,
                source.StableSourceId, StringComparison.Ordinal));
        var exactExclusion = !expectedAfter &&
            audits.Length == 0 &&
            expectedDecision is not null &&
            actualSource is not null &&
            string.Equals(expectedDecision.ContributionPolicy,
                StatisticReconciliationLifecyclePolicies.Exclude,
                StringComparison.Ordinal) &&
            ExactExcludedPins(input, expectedDecision, actualSource);
        var complete = audits.Length == 1 ||
            actualSource is null ||
            actualOnly ||
            exactExclusion;
        var binding = audits.Length == 1
            ? audits[0].SourceAuditHash
            : actualSource?.DecisionSemanticSha256 ??
              input.P9ContributionAudit.SnapshotSemanticSha256;
        if (expectedAfter && audits.Length == 1)
        {
            complete = ExactIncludePins(input, source, audits[0]);
        }

        if (expectedBefore && !expectedAfter)
        {
            complete &= input.Prior is not null &&
                ExactReversal(
                    input.P9ContributionAudit,
                    input.Prior.P9ContributionAudit,
                    source.ReportId);
            binding = StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_REVERSAL_BINDING_V2",
                binding,
                input.P9ContributionAudit.Reversal?.AuditHash,
                input.Prior?.P9AuditSnapshotSha256);
        }

        return new ActualMembershipEvidence(unit, complete, binding);
    }


    private static bool ExactExcludedPins(
        StatisticReconciliationActualLifecycleBuildInput input,
        StatisticReconciliationExpectedAuthoritativeLifecycleRow expected,
        ActualSourceMembershipDecision actual)
    {
        var owner = actual.ObservedOwner;
        var flow = owner.FlowRuntime;
        var runtime = expected.RuntimePin;
        var intrinsicLifecycleZero = IntrinsicLifecycleZero(expected);
        return string.Equals(owner.WorkId,
                   expected.WorkId, StringComparison.Ordinal) &&
            string.Equals(owner.WorkId,
                input.Source.WorkId, StringComparison.Ordinal) &&
            string.Equals(owner.PeriodInstanceKey,
                input.Source.PeriodInstanceKey, StringComparison.Ordinal) &&
            string.Equals(owner.DynamicFormTemplateId,
                input.Source.DynamicFormTemplateId, StringComparison.Ordinal) &&
            string.Equals(owner.OwnerRunId,
                input.Source.OwnerRunId, StringComparison.Ordinal) &&
            string.Equals(owner.OwnerGenerationId,
                input.Source.OwnerGenerationId, StringComparison.Ordinal) &&
            string.Equals(owner.OwnerGenerationSha256,
                input.Source.OwnerGenerationSha256, StringComparison.Ordinal) &&
            owner.OwnerDirectSourceRevision ==
                input.Source.OwnerDirectSourceRevision &&
            string.Equals(runtime.MembershipSignatureSha256,
                input.Source.OwnerMembershipSignature, StringComparison.Ordinal) &&
            string.Equals(owner.WorkAssignmentId,
                expected.WorkAssignmentId, StringComparison.Ordinal) &&
            string.Equals(owner.ReportId,
                expected.ReportId, StringComparison.Ordinal) &&
            string.Equals(owner.PayloadDocumentId,
                expected.PayloadDocumentId, StringComparison.Ordinal) &&
            owner.PayloadRevision == expected.PayloadRevision &&
            string.Equals(owner.PayloadSha256,
                expected.PayloadOwnerSha256, StringComparison.Ordinal) &&
            owner.LifecycleRevision == expected.LifecycleRevision &&
            string.Equals(owner.LifecycleSha256,
                expected.LifecycleSha256, StringComparison.Ordinal) &&
            string.Equals(owner.LifecycleStatus,
                expected.LifecycleStatus, StringComparison.Ordinal) &&
            string.Equals(owner.ContributionDecision,
                "EXCLUDE", StringComparison.Ordinal) &&
            owner.InLifecycleMetricScope &&
            !actual.Included &&
            !owner.OwnerIncluded &&
            (!expected.IsEffective ||
             (owner.PeriodIsActive &&
              (owner.PeriodStatus is "APPROVED" or "OVERDUEAPPROVED") &&
              string.Equals(owner.PeriodCurrentReportId,
                  expected.ReportId, StringComparison.Ordinal) &&
              string.Equals(owner.PeriodSourceLifecycleReportId,
                  expected.ReportId, StringComparison.Ordinal) &&
              owner.PeriodSourceLifecycleRevision ==
                  expected.LifecycleRevision &&
              owner.PeriodSourceLifecycleApplied)) &&
            (intrinsicLifecycleZero ||
             ExactExcludedMappingPins(owner, runtime)) &&
            !string.IsNullOrWhiteSpace(owner.OwnerGenerationId) &&
            IsSha(owner.OwnerGenerationSha256) &&
            (flow is null
                ? intrinsicLifecycleZero ||
                  string.Equals(runtime.RuntimeKind,
                      "NON_FLOW", StringComparison.Ordinal)
                : ExactExcludedFlowPins(flow, runtime));
    }
    private static bool IntrinsicLifecycleZero(
        StatisticReconciliationExpectedAuthoritativeLifecycleRow expected)
        => expected.LifecycleStatus is
            StatisticReconciliationExpectedLifecycleStatuses.Draft or
            StatisticReconciliationExpectedLifecycleStatuses.Submitted or
            StatisticReconciliationExpectedLifecycleStatuses.Recalled or
            StatisticReconciliationExpectedLifecycleStatuses.Returned or
            StatisticReconciliationExpectedLifecycleStatuses.Terminated or
            StatisticReconciliationExpectedLifecycleStatuses.Invalidated or
            StatisticReconciliationExpectedLifecycleStatuses.Superseded;

    private static bool ExactExcludedFlowPins(
        ActualFlowRuntimeOwnerRevision flow,
        StatisticReconciliationExpectedAuthoritativeRuntimePin runtime)
        => string.Equals(runtime.RuntimeKind,
               "FLOW", StringComparison.Ordinal) &&
           string.Equals(flow.FlowTemplateVersionId,
               runtime.FlowTemplateVersionId, StringComparison.Ordinal) &&
           string.Equals(flow.FlowPayloadSha256,
               runtime.FlowPayloadSha256, StringComparison.Ordinal) &&
           string.Equals(flow.FlowInstanceId,
               runtime.FlowInstanceId, StringComparison.Ordinal) &&
           string.Equals(flow.FlowBranchId,
               runtime.FlowBranchId, StringComparison.Ordinal) &&
           string.Equals(flow.FlowStepId,
               runtime.FlowStepId, StringComparison.Ordinal) &&
           string.Equals(flow.StepInstanceId,
               runtime.FlowStepInstanceId, StringComparison.Ordinal) &&
           flow.StepRevision == runtime.FlowStepRevision &&
           flow.FlowAttemptNo == runtime.FlowAttemptNo &&
           string.Equals(flow.ExecutionEpochId,
               runtime.ExecutionEpochId, StringComparison.Ordinal) &&
           flow.ExecutionEpoch == runtime.ExecutionEpoch &&
           flow.CurrentExecutionEpoch == runtime.CurrentExecutionEpoch &&
           flow.ExecutionEpochRevision == runtime.ExecutionEpochRevision &&
           flow.IsCanonicalEpoch == runtime.IsCanonicalEpoch &&
           string.Equals(flow.ContributionPolicy,
               "EXCLUDE", StringComparison.Ordinal) &&
           string.Equals(flow.ContributionSha256,
               runtime.ContributionPolicySha256, StringComparison.Ordinal);
    private static bool ExpectedOwnerMembership(
        StatisticReconciliationExpectedAuthoritativeLifecycleRow expected)
    {
        var runtime = expected.RuntimePin;
        var active = expected.LifecycleStatus ==
                StatisticReconciliationExpectedLifecycleStatuses.Approved &&
            expected.IsEffective &&
            expected.IsLocked &&
            expected.RuntimeDisposition ==
                StatisticReconciliationExpectedRuntimeDispositions.Current;
        if (!active || runtime.RuntimeKind != "FLOW")
        {
            return active;
        }

        return runtime.IsCanonicalEpoch == true &&
            runtime.ExecutionEpoch is > 0 &&
            runtime.ExecutionEpoch == runtime.CurrentExecutionEpoch &&
            string.Equals(
                runtime.ExecutionEpochId,
                runtime.CurrentExecutionEpochId,
                StringComparison.Ordinal);
    }

    private static bool ExactExcludedMappingPins(
        ActualSourceOwnerRevision owner,
        StatisticReconciliationExpectedAuthoritativeRuntimePin runtime)
    {
        var expectedHasMapping = runtime.MappingReceiptId is not null;
        if (!expectedHasMapping)
        {
            return owner.MappingRevision is null &&
                owner.MappingSemanticSha256 is null;
        }

        return runtime.RuntimeKind == "FLOW" &&
            runtime.ContributionPolicy ==
                StatisticReconciliationLifecyclePolicies.Exclude &&
            owner.MappingRevision == runtime.MappingResultPayloadRevision &&
            string.Equals(
                owner.MappingSemanticSha256,
                runtime.MappingResultSemanticSha256,
                StringComparison.Ordinal);
    }

    private static bool ExactIncludePins(
        StatisticReconciliationActualLifecycleBuildInput input,
        StatisticReconciliationLifecycleSourceState source,
        StatRunLifecycleContributionSourceAuditSnapshot audit)
    {
        var decision = input.Expected.Rows.SingleOrDefault(item =>
            string.Equals(item.SourceStableIdentitySha256,
                source.StableSourceId, StringComparison.Ordinal));
        if (decision is null)
        {
            return false;
        }
        var runtime = decision.RuntimePin;
        var actualDecision = input.Source.Decisions.SingleOrDefault(item =>
            item.ObservedOwner.InLifecycleMetricScope &&
            string.Equals(NeutralStableIdentity(item.ObservedOwner),
                source.StableSourceId, StringComparison.Ordinal) &&
            string.Equals(item.ObservedOwner.ReportId,
                source.ReportId, StringComparison.Ordinal));
        var targets = input.P9ContributionAudit.Targets
            .Where(item => string.Equals(
                item.SourceAuditId,
                audit.SourceAuditId,
                StringComparison.Ordinal))
            .ToArray();
        return actualDecision is not null &&
            actualDecision.Included &&
            ExactIncludedActualOwner(
                input,
                decision,
                actualDecision,
                audit) &&
            string.Equals(audit.SourceReportId,
                decision.ReportId, StringComparison.Ordinal) &&
            audit.SourcePayloadRevision == decision.PayloadRevision &&
            string.Equals(audit.SourcePayloadHash,
                decision.PayloadOwnerSha256, StringComparison.Ordinal) &&
            audit.SourceLifecycleRevision == decision.LifecycleRevision &&
            string.Equals(audit.ApprovalCommandId,
                runtime.ApprovalCommandId, StringComparison.Ordinal) &&
            string.Equals(audit.ApprovalEventKey,
                runtime.ApprovalEventKey, StringComparison.Ordinal) &&
            ExactIncludeAuditRuntimePins(decision, runtime, audit) &&
            string.Equals(audit.WorkId,
                decision.WorkId, StringComparison.Ordinal) &&
            string.Equals(audit.WorkAssignmentId,
                decision.WorkAssignmentId, StringComparison.Ordinal) &&
            string.Equals(audit.PeriodInstanceKey,
                input.Source.PeriodInstanceKey, StringComparison.Ordinal) &&
            string.Equals(audit.ConfigVersionId,
                runtime.ConfigVersionId, StringComparison.Ordinal) &&
            string.Equals(audit.ConfigHash,
                runtime.ConfigSha256, StringComparison.Ordinal) &&
            string.Equals(audit.MembershipSignature,
                runtime.MembershipSignatureSha256, StringComparison.Ordinal) &&
            string.Equals(audit.MembershipSignature,
                input.Source.OwnerMembershipSignature, StringComparison.Ordinal) &&
            string.Equals(audit.ContributionPolicy,
                "INCLUDE", StringComparison.Ordinal) &&
            string.Equals(audit.ContributionPolicyHash,
                runtime.ContributionPolicySha256, StringComparison.Ordinal) &&
            string.Equals(runtime.ContributionPolicy,
                "V_INCLUDE", StringComparison.Ordinal) &&
            string.Equals(runtime.ContributionProvenanceId,
                decision.ContributionProvenanceId, StringComparison.Ordinal) &&
            string.Equals(runtime.ContributionProvenanceSha256,
                decision.ContributionProvenanceSha256,
                StringComparison.Ordinal) &&
            targets.Length > 0 &&

            targets.All(item =>
                string.Equals(item.SourceReportId,
                    source.ReportId, StringComparison.Ordinal) &&
                string.Equals(item.Operation,
                    "INCLUDE", StringComparison.Ordinal) &&
                string.Equals(item.State,
                    WorkReportFlowContributionStates.Applied,
                    StringComparison.Ordinal));
    }

    private static bool ExactIncludeAuditRuntimePins(
        StatisticReconciliationExpectedAuthoritativeLifecycleRow decision,
        StatisticReconciliationExpectedAuthoritativeRuntimePin runtime,
        StatRunLifecycleContributionSourceAuditSnapshot audit)
    {
        if (string.Equals(runtime.RuntimeKind, "FLOW", StringComparison.Ordinal))
        {
            return string.Equals(audit.RuntimeKind, "FLOW", StringComparison.Ordinal) &&
                audit.NonFlowPolicyVersion is null &&
                audit.NonFlowPolicyOwnerId is null &&
                audit.NonFlowPolicyRevision is null &&
                audit.NonFlowPolicy is null &&
                string.Equals(audit.MappingReceiptId,
                    runtime.MappingReceiptId, StringComparison.Ordinal) &&
                string.Equals(audit.MappingProvenanceId,
                    runtime.MappingProvenanceId, StringComparison.Ordinal) &&
                string.Equals(audit.MappingProvenanceHash,
                    runtime.MappingProvenanceSha256, StringComparison.Ordinal) &&
                string.Equals(audit.MappingResultSemanticHash,
                    runtime.MappingResultSemanticSha256, StringComparison.Ordinal) &&
                audit.MappingResultPayloadRevision ==
                    runtime.MappingResultPayloadRevision &&
                string.Equals(audit.MappingResultPayloadHash,
                    runtime.MappingResultPayloadSha256, StringComparison.Ordinal) &&
                string.Equals(audit.MappingFlowVersionId,
                    runtime.MappingFlowVersionId, StringComparison.Ordinal) &&
                audit.MappingFlowVersionNo == runtime.MappingFlowVersionNo &&
                string.Equals(audit.MappingFlowPayloadHash,
                    runtime.MappingFlowPayloadSha256, StringComparison.Ordinal) &&
                string.Equals(audit.FlowTemplateVersionId,
                    runtime.FlowTemplateVersionId, StringComparison.Ordinal) &&
                string.Equals(audit.FlowPayloadHash,
                    runtime.FlowPayloadSha256, StringComparison.Ordinal) &&
                string.Equals(audit.FlowInstanceId,
                    runtime.FlowInstanceId, StringComparison.Ordinal) &&
                audit.FlowExecutionEpoch == runtime.ExecutionEpoch &&
                string.Equals(audit.FlowStepInstanceId,
                    runtime.FlowStepInstanceId, StringComparison.Ordinal) &&
                string.Equals(audit.FlowBranchId,
                    runtime.FlowBranchId, StringComparison.Ordinal) &&
                string.Equals(audit.FlowStepId,
                    runtime.FlowStepId, StringComparison.Ordinal) &&
                audit.FlowAttemptNo == runtime.FlowAttemptNo;
        }
        return string.Equals(runtime.RuntimeKind,
                   "NON_FLOW", StringComparison.Ordinal) &&
            string.Equals(audit.RuntimeKind,
                "NON_FLOW", StringComparison.Ordinal) &&
            audit.MappingReceiptId is null &&
            audit.MappingProvenanceId is null &&
            audit.MappingProvenanceHash is null &&
            audit.MappingResultSemanticHash is null &&
            audit.MappingResultPayloadRevision is null &&
            audit.MappingResultPayloadHash is null &&
            audit.MappingFlowVersionId is null &&
            audit.MappingFlowVersionNo is null &&
            audit.MappingFlowPayloadHash is null &&
            audit.FlowTemplateVersionId is null &&
            audit.FlowPayloadHash is null &&
            audit.FlowInstanceId is null &&
            audit.FlowExecutionEpoch is null &&
            audit.FlowStepInstanceId is null &&
            audit.FlowBranchId is null &&
            audit.FlowStepId is null &&
            audit.FlowAttemptNo is null &&
            string.Equals(audit.NonFlowPolicyVersion,
                "P10_NON_FLOW_CONTRIBUTION_POLICY_V1",
                StringComparison.Ordinal) &&
            string.Equals(audit.NonFlowPolicyOwnerId,
                decision.ContributionVersionId, StringComparison.Ordinal) &&
            audit.NonFlowPolicyRevision == decision.ContributionRevision &&
            string.Equals(audit.NonFlowPolicy,
                runtime.ContributionPolicy, StringComparison.Ordinal) &&
            string.Equals(audit.NonFlowPolicy,
                StatisticReconciliationExpectedContributionPolicies.Include,
                StringComparison.Ordinal) &&
            string.Equals(audit.ContributionPolicyHash,
                decision.ContributionPolicySha256, StringComparison.Ordinal) &&
            string.Equals(audit.NonFlowPolicyOwnerId,
                decision.ContributionProvenanceId, StringComparison.Ordinal) &&
            string.Equals(audit.ContributionPolicyHash,
                decision.ContributionProvenanceSha256, StringComparison.Ordinal);
    }

    private static bool ExactIncludedActualOwner(
        StatisticReconciliationActualLifecycleBuildInput input,
        StatisticReconciliationExpectedAuthoritativeLifecycleRow expected,
        ActualSourceMembershipDecision actual,
        StatRunLifecycleContributionSourceAuditSnapshot audit)
    {
        var owner = actual.ObservedOwner;
        var flow = owner.FlowRuntime;
        var runtime = expected.RuntimePin;
        return owner.InLifecycleMetricScope &&
            owner.OwnerIncluded &&
            actual.Included &&
            owner.PeriodIsActive &&
            (owner.PeriodStatus is "APPROVED" or "OVERDUEAPPROVED") &&
            string.Equals(owner.PeriodCurrentReportId,
                expected.ReportId, StringComparison.Ordinal) &&
            string.Equals(owner.PeriodSourceLifecycleReportId,
                expected.ReportId, StringComparison.Ordinal) &&
            owner.PeriodSourceLifecycleRevision == expected.LifecycleRevision &&
            owner.PeriodSourceLifecycleApplied &&
            string.Equals(owner.ContributionDecision,
                "INCLUDE", StringComparison.Ordinal) &&
            string.Equals(owner.WorkId,
                expected.WorkId, StringComparison.Ordinal) &&
            string.Equals(owner.WorkId,
                input.Source.WorkId, StringComparison.Ordinal) &&
            string.Equals(owner.WorkAssignmentId,
                expected.WorkAssignmentId, StringComparison.Ordinal) &&
            string.Equals(owner.ReportId,
                expected.ReportId, StringComparison.Ordinal) &&
            string.Equals(owner.PeriodInstanceKey,
                input.Source.PeriodInstanceKey, StringComparison.Ordinal) &&
            string.Equals(owner.DynamicFormTemplateId,
                input.Source.DynamicFormTemplateId, StringComparison.Ordinal) &&
            string.Equals(owner.PayloadDocumentId,
                expected.PayloadDocumentId, StringComparison.Ordinal) &&
            owner.PayloadRevision == expected.PayloadRevision &&
            string.Equals(owner.PayloadSha256,
                expected.PayloadOwnerSha256, StringComparison.Ordinal) &&
            owner.LifecycleRevision == expected.LifecycleRevision &&
            string.Equals(owner.LifecycleSha256,
                expected.LifecycleSha256, StringComparison.Ordinal) &&
            string.Equals(owner.LifecycleStatus,
                expected.LifecycleStatus, StringComparison.Ordinal) &&
            string.Equals(owner.OwnerRunId,
                input.Source.OwnerRunId, StringComparison.Ordinal) &&
            string.Equals(owner.OwnerGenerationId,
                input.Source.OwnerGenerationId, StringComparison.Ordinal) &&
            string.Equals(owner.OwnerGenerationSha256,
                input.Source.OwnerGenerationSha256, StringComparison.Ordinal) &&
            owner.OwnerDirectSourceRevision ==
                input.Source.OwnerDirectSourceRevision &&
            string.Equals(runtime.MembershipSignatureSha256,
                input.Source.OwnerMembershipSignature, StringComparison.Ordinal) &&
            ExactIncludedOwnerRuntimePins(owner, flow, runtime, audit);
    }

    private static bool ExactIncludedOwnerRuntimePins(
        ActualSourceOwnerRevision owner,
        ActualFlowRuntimeOwnerRevision? flow,
        StatisticReconciliationExpectedAuthoritativeRuntimePin runtime,
        StatRunLifecycleContributionSourceAuditSnapshot audit)
    {
        var commonAuditPins = string.Equals(audit.SourceReportId,
                owner.ReportId, StringComparison.Ordinal) &&
            audit.SourcePayloadRevision == owner.PayloadRevision &&
            string.Equals(audit.SourcePayloadHash,
                owner.PayloadSha256, StringComparison.Ordinal) &&
            audit.SourceLifecycleRevision == owner.LifecycleRevision &&
            string.Equals(audit.WorkId,
                owner.WorkId, StringComparison.Ordinal) &&
            string.Equals(audit.WorkAssignmentId,
                owner.WorkAssignmentId, StringComparison.Ordinal) &&
            string.Equals(audit.PeriodInstanceKey,
                owner.PeriodInstanceKey, StringComparison.Ordinal) &&
            string.Equals(audit.MembershipSignature,
                runtime.MembershipSignatureSha256,
                StringComparison.Ordinal);
        if (!commonAuditPins)
            return false;
        if (string.Equals(runtime.RuntimeKind, "NON_FLOW", StringComparison.Ordinal))
        {
            return flow is null &&
                owner.MappingRevision is null &&
                owner.MappingSemanticSha256 is null &&
                string.Equals(audit.RuntimeKind,
                    "NON_FLOW", StringComparison.Ordinal) &&
                string.Equals(audit.NonFlowPolicyOwnerId,
                    runtime.ContributionProvenanceId,
                    StringComparison.Ordinal) &&
                audit.NonFlowPolicyRevision is > 0 &&
                string.Equals(audit.NonFlowPolicy,
                    runtime.ContributionPolicy, StringComparison.Ordinal) &&
                string.Equals(audit.ContributionPolicyHash,
                    runtime.ContributionPolicySha256,
                    StringComparison.Ordinal);
        }
        return string.Equals(runtime.RuntimeKind, "FLOW", StringComparison.Ordinal) &&
            owner.MappingRevision == runtime.MappingResultPayloadRevision &&
            string.Equals(owner.MappingSemanticSha256,
                runtime.MappingResultSemanticSha256,
                StringComparison.Ordinal) &&
            flow is not null &&
            string.Equals(flow.FlowTemplateVersionId,
                runtime.FlowTemplateVersionId, StringComparison.Ordinal) &&
            string.Equals(flow.FlowPayloadSha256,
                runtime.FlowPayloadSha256, StringComparison.Ordinal) &&
            string.Equals(flow.FlowInstanceId,
                runtime.FlowInstanceId, StringComparison.Ordinal) &&
            string.Equals(flow.FlowBranchId,
                runtime.FlowBranchId, StringComparison.Ordinal) &&
            string.Equals(flow.FlowStepId,
                runtime.FlowStepId, StringComparison.Ordinal) &&
            string.Equals(flow.StepInstanceId,
                runtime.FlowStepInstanceId, StringComparison.Ordinal) &&
            flow.StepRevision == runtime.FlowStepRevision &&
            flow.FlowAttemptNo == runtime.FlowAttemptNo &&
            string.Equals(flow.ExecutionEpochId,
                runtime.ExecutionEpochId, StringComparison.Ordinal) &&
            flow.ExecutionEpoch == runtime.ExecutionEpoch &&
            flow.CurrentExecutionEpoch == runtime.CurrentExecutionEpoch &&
            flow.ExecutionEpochRevision == runtime.ExecutionEpochRevision &&
            flow.IsCanonicalEpoch == runtime.IsCanonicalEpoch &&
            string.Equals(flow.ContributionPolicy,
                "INCLUDE", StringComparison.Ordinal) &&
            string.Equals(flow.ContributionSha256,
                runtime.ContributionPolicySha256,
                StringComparison.Ordinal) &&
            string.Equals(audit.SourceReportId,
                owner.ReportId, StringComparison.Ordinal) &&
            audit.SourcePayloadRevision == owner.PayloadRevision &&
            string.Equals(audit.SourcePayloadHash,
                owner.PayloadSha256, StringComparison.Ordinal) &&
            audit.SourceLifecycleRevision == owner.LifecycleRevision &&
            audit.MappingResultPayloadRevision == owner.MappingRevision &&
            string.Equals(audit.MappingResultSemanticHash,
                owner.MappingSemanticSha256, StringComparison.Ordinal) &&
            string.Equals(audit.FlowTemplateVersionId,
                flow.FlowTemplateVersionId, StringComparison.Ordinal) &&
            string.Equals(audit.FlowPayloadHash,
                flow.FlowPayloadSha256, StringComparison.Ordinal) &&
            string.Equals(audit.FlowInstanceId,
                flow.FlowInstanceId, StringComparison.Ordinal) &&
            audit.FlowExecutionEpoch == flow.ExecutionEpoch &&
            string.Equals(audit.FlowStepInstanceId,
                flow.StepInstanceId, StringComparison.Ordinal) &&
            string.Equals(audit.FlowBranchId,
                flow.FlowBranchId, StringComparison.Ordinal) &&
            string.Equals(audit.FlowStepId,
                flow.FlowStepId, StringComparison.Ordinal) &&
            audit.FlowAttemptNo == flow.FlowAttemptNo &&
            string.Equals(audit.ContributionPolicyHash,
                flow.ContributionSha256, StringComparison.Ordinal);
    }
    private static bool ExactReversal(
        StatRunLifecycleContributionAuditSnapshot current,
        StatRunLifecycleContributionAuditSnapshot prior,
        string reportId)
    {
        var reversal = current.Reversal;
        var priorSources = prior.Sources
            .Where(item => string.Equals(
                item.SourceReportId,
                reportId,
                StringComparison.Ordinal))
            .OrderBy(static item => item.SourceAuditId, StringComparer.Ordinal)
            .Select(static item => item.SourceAuditHash)
            .ToArray();
        var priorTargets = prior.Targets
            .Where(item => string.Equals(
                item.SourceReportId,
                reportId,
                StringComparison.Ordinal))
            .OrderBy(static item => item.TargetStore, StringComparer.Ordinal)
            .ThenBy(static item => item.TargetStatisticId, StringComparer.Ordinal)
            .Select(static item => item.LedgerEntryHash)
            .ToArray();
        return reversal is not null &&
            string.Equals(reversal.SourceReportId, reportId, StringComparison.Ordinal) &&
            string.Equals(current.TriggerReportId, reportId, StringComparison.Ordinal) &&
            string.Equals(reversal.EventKey, current.TriggerLifecycleEventKey, StringComparison.Ordinal) &&
            reversal.SourceLifecycleRevision == current.TriggerLifecycleRevision &&
            string.Equals(reversal.ReceiptId, current.ReceiptId, StringComparison.Ordinal) &&
            string.Equals(reversal.PriorRunId, prior.Scope.RunId, StringComparison.Ordinal) &&
            string.Equals(reversal.PriorGenerationId, prior.Scope.GenerationId, StringComparison.Ordinal) &&
            string.Equals(reversal.PriorGenerationHash, prior.Scope.GenerationSha256, StringComparison.Ordinal) &&
            string.Equals(reversal.PriorLedgerHash, prior.LedgerHash, StringComparison.Ordinal) &&
            string.Equals(reversal.PriorReversalBaselineHash, prior.ReversalBaselineHash, StringComparison.Ordinal) &&
            reversal.PriorSourceCount == priorSources.Length &&
            reversal.PriorTargetCount == priorTargets.Length &&
            reversal.PriorSourceAuditHashes.SequenceEqual(priorSources, StringComparer.Ordinal) &&
            reversal.PriorTargetLedgerHashes.SequenceEqual(priorTargets, StringComparer.Ordinal);
    }

    private void AppendExtraAuditActuals(
        StatisticReconciliationActualLifecycleBuildInput input,
        ImmutableArray<StatisticReconciliationLifecycleActualContribution>.Builder actuals)
    {
        var knownReports = input.Expected.Rows
            .Select(static item => item.ReportId)
            .Concat(input.Source.Decisions.Select(static item => item.ReportId))
            .ToHashSet(StringComparer.Ordinal);        foreach (var audit in input.P9ContributionAudit.Sources
                     .Where(item => !knownReports.Contains(item.SourceReportId))
                     .OrderBy(static item => item.SourceReportId, StringComparer.Ordinal))
        {
            var sourceObservationId = StatisticReconciliationLifecycleCanonical.Hash(
                "P10_LIFECYCLE_EXTRA_AUDIT_SOURCE_V2",
                input.BaseCoherentGenerationId,
                audit.SourceAuditId);
            var syntheticSource = evaluator.CreateSource(
                sourceObservationId,
                1,
                StatisticReconciliationLifecycleEventKinds.Observe,
                StatisticReconciliationExpectedMembershipIntegrity
                    .BuildStableIdentitySha256(
                        audit.WorkId,
                        audit.WorkAssignmentId,
                        audit.SourceReportId),
                audit.SourceReportId,
                audit.FlowBranchId ?? $"NON_FLOW:{audit.WorkAssignmentId}",
                audit.FlowStepId ?? $"NON_FLOW:{audit.SourceReportId}",
                audit.FlowInstanceId is null
                    ? $"NON_FLOW_EPOCH:{audit.WorkAssignmentId}"
                    : $"FLOW_EPOCH:{audit.FlowInstanceId}:{audit.FlowExecutionEpoch}",
                audit.FlowExecutionEpoch ?? 1,
                audit.FlowInstanceId is null
                    ? $"NON_FLOW_EPOCH:{audit.WorkAssignmentId}"
                    : $"FLOW_EPOCH:{audit.FlowInstanceId}:{audit.FlowExecutionEpoch}",
                StatisticReconciliationLifecycleStatuses.Draft,
                false,
                StatisticReconciliationLifecyclePolicies.Exclude,
                null,
                null,
                false,
                null,
                null,
                null,
                input.Source.OwnerGenerationId,
                input.Source.OwnerGenerationSha256,
                "1");
            actuals.Add(evaluator.CreateActual(
                StatisticReconciliationLifecycleCanonical.Hash(
                    "P10_LIFECYCLE_EXTRA_AUDIT_ACTUAL_V2",
                    input.BaseCoherentGenerationId,
                    audit.SourceAuditId),
                syntheticSource,
                StatisticReconciliationLifecycleCanonical.Hash(
                    "P10_LIFECYCLE_EXTRA_AUDIT_GENERATION_V2",
                    input.BaseCoherentGenerationId,
                    audit.SourceAuditId),
                null,
                null,
                "1",
                "1"));
        }
    }

    private static StatisticReconciliationLifecycleResult CreateEmptyResult(
        StatisticReconciliationLifecycleRequest request)
    {
        var emptySetSha = StatisticReconciliationLifecycleCanonical.HashSequence([]);
        var requestSha = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_REQUEST_V1",
            request.ReconciliationId,
            request.ComparisonBindingSha256,
            emptySetSha,
            emptySetSha);
        var resultSha = StatisticReconciliationLifecycleCanonical.Hash(
            "P10_LIFECYCLE_RESULT_V1",
            request.ReconciliationId,
            request.ComparisonBindingSha256,
            StatisticReconciliationLifecycleOutcomes.Matched,
            null,
            "true",
            "0",
            "0",
            "0",
            "0",
            "0",
            emptySetSha,
            requestSha);
        return new StatisticReconciliationLifecycleResult(
            request.ReconciliationId,
            request.ComparisonBindingSha256,
            StatisticReconciliationLifecycleOutcomes.Matched,
            null,
            true,
            0,
            0,
            0,
            0,
            0,
            [],
            requestSha,
            resultSha);
    }

    private static void ValidateCaptureBinding(
        StatisticReconciliationActualLifecycleBuildInput input)
    {
        _ = StatisticReconciliationLifecycleCanonical.Required(
            input.ReconciliationId,
            nameof(input.ReconciliationId));
        _ = StatisticReconciliationLifecycleCanonical.Required(
            input.BaseCoherentGenerationId,
            nameof(input.BaseCoherentGenerationId));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            input.BaseCoherentGenerationSha256,
            nameof(input.BaseCoherentGenerationSha256));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            input.ComparisonBindingSha256,
            nameof(input.ComparisonBindingSha256));
        ValidateExpectedProjection(input);
        StatisticReconciliationActualSourceMembershipAdapter.RequireIntegrity(
            input.Source);
        StatRunLifecycleContributionAuditCanonical.RequireValid(
            input.P9ContributionAudit);
        var auditScope = input.P9ContributionAudit.Scope;
        if (!string.Equals(input.Source.SourceSetSha256, input.Direct.ActualSourceSetSha256, StringComparison.Ordinal) ||
            !string.Equals(input.Source.OwnerRunId, input.Direct.Boundary.RunId, StringComparison.Ordinal) ||
            !string.Equals(input.Source.OwnerGenerationId, input.Direct.Boundary.GenerationId, StringComparison.Ordinal) ||
            !string.Equals(input.Source.OwnerGenerationSha256, input.Direct.Boundary.OwnerGenerationSha256, StringComparison.Ordinal) ||
            input.Source.OwnerDirectSourceRevision != input.Direct.Boundary.DirectSourceRevision ||
            !string.Equals(auditScope.RunId, input.Source.OwnerRunId, StringComparison.Ordinal) ||
            !string.Equals(auditScope.GenerationId, input.Source.OwnerGenerationId, StringComparison.Ordinal) ||
            !string.Equals(auditScope.GenerationSha256, input.Source.OwnerGenerationSha256, StringComparison.Ordinal) ||
            !string.Equals(auditScope.MembershipSignature, input.Source.OwnerMembershipSignature, StringComparison.Ordinal) ||
            auditScope.DirectSourceRevision != input.Source.OwnerDirectSourceRevision)
        {
            throw new InvalidOperationException(
                "Lifecycle source, Direct projection, and P9 audit are not one owner generation.");
        }
    }

    private static void ValidateExpectedProjection(
        StatisticReconciliationActualLifecycleBuildInput input)
    {
        var expected = input.Expected ?? throw new InvalidOperationException(
            "Authoritative lifecycle projection is required.");
        var binding = expected.Binding ?? throw new InvalidOperationException(
            "Exact expected generation binding is required.");
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            binding.GenerationId, nameof(binding.GenerationId));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            binding.GenerationSemanticSha256,
            nameof(binding.GenerationSemanticSha256));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            binding.ManifestSha256, nameof(binding.ManifestSha256));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            binding.MetricPlanSha256, nameof(binding.MetricPlanSha256));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            binding.MembershipSemanticSha256,
            nameof(binding.MembershipSemanticSha256));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            expected.LifecycleManifestSha256,
            nameof(expected.LifecycleManifestSha256));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            expected.DoubleCollectProofSha256,
            nameof(expected.DoubleCollectProofSha256));
        var decisions = expected.Rows.IsDefault
            ? throw new InvalidOperationException(
                "Authoritative lifecycle rows are required.")
            : expected.Rows;
        var recomputedManifest =
            StatisticReconciliationActualLifecycleEvidenceIntegrity
                .RecomputeExpectedLifecycleManifestSha(expected);
        if (binding.DocumentCount <= 0 ||
            binding.MetricPlanEntryCount <= 0 ||
            decisions.Length <= 0 ||
            !string.Equals(binding.ReconciliationId,
                input.ReconciliationId, StringComparison.Ordinal) ||
            !string.Equals(recomputedManifest,
                expected.LifecycleManifestSha256, StringComparison.Ordinal) ||
            decisions.Select(static item => item.SourceStableIdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != decisions.Length ||
            decisions.Select(static item => item.ReportId)
                .Distinct(StringComparer.Ordinal).Count() != decisions.Length ||
            decisions.Select(static item => item.DocumentSemanticSha256)
                .Distinct(StringComparer.Ordinal).Count() != decisions.Length)
        {
            throw new InvalidOperationException(
                "Authoritative lifecycle projection binding is invalid.");
        }
        foreach (var decision in decisions)
        {
            var runtime = decision.RuntimePin ??
                throw new InvalidOperationException(
                    "Authoritative runtime pin is required.");
            _ = StatisticReconciliationLifecycleCanonical.Sha(
                decision.SourceStableIdentitySha256,
                nameof(decision.SourceStableIdentitySha256));
            _ = StatisticReconciliationLifecycleCanonical.Sha(
                decision.PayloadOwnerSha256,
                nameof(decision.PayloadOwnerSha256));
            _ = StatisticReconciliationLifecycleCanonical.Sha(
                decision.PayloadCanonicalSha256,
                nameof(decision.PayloadCanonicalSha256));
            _ = StatisticReconciliationLifecycleCanonical.Sha(
                decision.LifecycleSha256,
                nameof(decision.LifecycleSha256));
            _ = StatisticReconciliationLifecycleCanonical.Sha(
                decision.ContributionPolicySha256,
                nameof(decision.ContributionPolicySha256));
            _ = StatisticReconciliationLifecycleCanonical.Sha(
                decision.ContributionProvenanceSha256,
                nameof(decision.ContributionProvenanceSha256));
            _ = StatisticReconciliationLifecycleCanonical.Sha(
                decision.DecisionSemanticSha256,
                nameof(decision.DecisionSemanticSha256));
            _ = StatisticReconciliationLifecycleCanonical.Sha(
                decision.DocumentSemanticSha256,
                nameof(decision.DocumentSemanticSha256));
            _ = StatisticReconciliationLifecycleCanonical.Sha(
                runtime.RuntimeSemanticSha256,
                nameof(runtime.RuntimeSemanticSha256));
            _ = StatisticReconciliationLifecycleCanonical.Sha(
                runtime.LifecycleOwnerSha256,
                nameof(runtime.LifecycleOwnerSha256));
            if (decision.PayloadRevision <= 0 ||
                decision.LifecycleRevision <= 0 ||
                decision.ContributionRevision <= 0 ||
                string.IsNullOrWhiteSpace(decision.StableSourceId) ||
                string.IsNullOrWhiteSpace(decision.IdentityKey) ||
                string.IsNullOrWhiteSpace(decision.WorkId) ||
                string.IsNullOrWhiteSpace(decision.WorkAssignmentId) ||
                string.IsNullOrWhiteSpace(decision.ReportId) ||
                string.IsNullOrWhiteSpace(decision.PayloadDocumentId) ||
                string.IsNullOrWhiteSpace(decision.ContributionVersionId) ||
                string.IsNullOrWhiteSpace(decision.ContributionProvenanceId) ||
                !string.Equals(decision.WorkId,
                    input.Source.WorkId, StringComparison.Ordinal) ||
                decision.ContributionPolicy is not (
                    StatisticReconciliationLifecyclePolicies.Include or
                    StatisticReconciliationLifecyclePolicies.Exclude) ||
                !string.Equals(runtime.ContributionPolicy,
                    decision.ContributionPolicy, StringComparison.Ordinal) ||
                !string.Equals(runtime.ContributionPolicySha256,
                    decision.ContributionPolicySha256,
                    StringComparison.Ordinal) ||
                !string.Equals(runtime.LifecycleOwnerSha256,
                    decision.LifecycleSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Authoritative lifecycle decision is invalid.");
            }
        }
    }
    private static void ValidatePrior(
        string reconciliationId,
        StatisticReconciliationActualLifecyclePriorGeneration? prior)
    {
        if (prior is null)
        {
            return;
        }
        StatRunLifecycleContributionAuditCanonical.RequireValid(
            prior.P9ContributionAudit);
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            prior.BaseCoherentGenerationId,
            nameof(prior.BaseCoherentGenerationId));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            prior.BaseCoherentGenerationSha256,
            nameof(prior.BaseCoherentGenerationSha256));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            prior.CommittedGenerationId,
            nameof(prior.CommittedGenerationId));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            prior.CommittedGenerationSha256,
            nameof(prior.CommittedGenerationSha256));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            prior.FinalVerdictGenerationId,
            nameof(prior.FinalVerdictGenerationId));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            prior.FinalVerdictGenerationSha256,
            nameof(prior.FinalVerdictGenerationSha256));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            prior.LifecycleManifestSha256,
            nameof(prior.LifecycleManifestSha256));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            prior.LifecycleRowSetSha256,
            nameof(prior.LifecycleRowSetSha256));
        _ = StatisticReconciliationLifecycleCanonical.Required(
            prior.P9RunId,
            nameof(prior.P9RunId));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            prior.P9GenerationId,
            nameof(prior.P9GenerationId));
        _ = StatisticReconciliationLifecycleCanonical.Sha(
            prior.P9GenerationSha256,
            nameof(prior.P9GenerationSha256));
        _ = prior.P9ContributionLedgerSha256 is null
            ? null
            : StatisticReconciliationLifecycleCanonical.Sha(
                prior.P9ContributionLedgerSha256,
                nameof(prior.P9ContributionLedgerSha256));
        _ = prior.P9ReversalBaselineSha256 is null
            ? null
            : StatisticReconciliationLifecycleCanonical.Sha(
                prior.P9ReversalBaselineSha256,
                nameof(prior.P9ReversalBaselineSha256));

        var audit = prior.P9ContributionAudit;
        var sourceHashes = audit.Sources
            .OrderBy(static item => item.SourceReportId, StringComparer.Ordinal)
            .Select(static item => item.SourceAuditHash)
            .ToImmutableArray();
        var targetHashes = audit.Targets
            .OrderBy(static item => item.TargetStore, StringComparer.Ordinal)
            .ThenBy(static item => item.TargetStatisticId, StringComparer.Ordinal)
            .Select(static item => item.LedgerEntryHash)
            .ToImmutableArray();
        var request = new StatisticReconciliationLifecycleRequest(
            prior.Result.ReconciliationId,
            prior.Result.ComparisonBindingSha256,
            prior.Sources,
            prior.Actuals);
        var rebuilt = request.Sources.IsEmpty
            ? CreateEmptyResult(request)
            : new StatisticReconciliationLifecycleEvaluator().Evaluate(request);
        if (prior.LifecycleObservationCount <= 0 ||
            !prior.Result.EvidenceComplete ||
            !string.Equals(prior.Result.Outcome,
                StatisticReconciliationLifecycleOutcomes.Matched,
                StringComparison.Ordinal) ||
            !string.Equals(prior.Result.ReconciliationId,
                reconciliationId, StringComparison.Ordinal) ||
            !string.Equals(prior.Result.RequestSemanticSha256,
                rebuilt.RequestSemanticSha256, StringComparison.Ordinal) ||
            !string.Equals(prior.Result.ResultSemanticSha256,
                rebuilt.ResultSemanticSha256, StringComparison.Ordinal) ||
            !string.Equals(prior.P9RunId,
                audit.Scope.RunId, StringComparison.Ordinal) ||
            !string.Equals(prior.P9GenerationId,
                audit.Scope.GenerationId, StringComparison.Ordinal) ||
            !string.Equals(prior.P9GenerationSha256,
                audit.Scope.GenerationSha256, StringComparison.Ordinal) ||
            !string.Equals(prior.P9ContributionLedgerSha256,
                audit.LedgerHash, StringComparison.Ordinal) ||
            !string.Equals(prior.P9ReversalBaselineSha256,
                audit.ReversalBaselineHash, StringComparison.Ordinal) ||
            !string.Equals(prior.P9AuditSnapshotSha256,
                audit.SnapshotSemanticSha256, StringComparison.Ordinal) ||
            !prior.P9SourceAuditHashes.SequenceEqual(
                sourceHashes, StringComparer.Ordinal) ||
            !prior.P9TargetLedgerHashes.SequenceEqual(
                targetHashes, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Prior lifecycle generation is not immutable complete MATCHED evidence.");
        }
    }
    private static Dictionary<string, StatisticReconciliationLifecycleSourceState>
        LatestSources(ImmutableArray<StatisticReconciliationLifecycleSourceState> values) =>
        values.GroupBy(static item => item.StableSourceId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static item => item.Sequence)
                    .ThenBy(static item => item.ObservationId, StringComparer.Ordinal)
                    .Last(),
                StringComparer.Ordinal);

    private static Dictionary<string, StatisticReconciliationLifecycleActualContribution>
        LatestActuals(
            ImmutableArray<StatisticReconciliationLifecycleSourceState> sources,
            ImmutableArray<StatisticReconciliationLifecycleActualContribution> actuals)
    {
        var sequences = sources.ToDictionary(
            static item => item.ObservationId,
            static item => item.Sequence,
            StringComparer.Ordinal);
        return actuals
            .Where(item => sequences.ContainsKey(item.SourceObservationId))
            .GroupBy(static item => item.StableSourceId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => group.OrderBy(item => sequences[item.SourceObservationId])
                    .ThenBy(static item => item.ObservationId, StringComparer.Ordinal)
                    .Last(),
                StringComparer.Ordinal);
    }


    private static string NeutralStableIdentity(
        ActualSourceOwnerRevision owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return StatisticReconciliationExpectedMembershipIntegrity
            .BuildStableIdentitySha256(
                owner.WorkId,
                owner.WorkAssignmentId,
                owner.ReportId);
    }
    private static string NormalizeStatus(string status) =>
        status.Trim().ToUpperInvariant() switch
        {
            "DRAFT" => StatisticReconciliationLifecycleStatuses.Draft,
            "SUBMITTED" => StatisticReconciliationLifecycleStatuses.Submitted,
            "APPROVED" => StatisticReconciliationLifecycleStatuses.Approved,
            "RECALLED" => StatisticReconciliationLifecycleStatuses.Recalled,
            "RETURNED" => StatisticReconciliationLifecycleStatuses.Returned,
            "TERMINATED" => StatisticReconciliationLifecycleStatuses.Terminated,
            "INVALIDATED" => StatisticReconciliationLifecycleStatuses.Invalidated,
            var value => throw new InvalidOperationException(
                $"Unsupported lifecycle status '{value}'.")
        };

    private static string DetermineEvent(
        string status,
        StatisticReconciliationExpectedAuthoritativeLifecycleRow decision,
        StatisticReconciliationLifecycleSourceState? previous) => status switch
        {
            StatisticReconciliationLifecycleStatuses.Recalled =>
                StatisticReconciliationLifecycleEventKinds.Recall,
            StatisticReconciliationLifecycleStatuses.Returned =>
                StatisticReconciliationLifecycleEventKinds.Return,
            StatisticReconciliationLifecycleStatuses.Terminated =>
                StatisticReconciliationLifecycleEventKinds.Terminate,
            StatisticReconciliationLifecycleStatuses.Invalidated =>
                StatisticReconciliationLifecycleEventKinds.Invalidate,
            StatisticReconciliationLifecycleStatuses.Approved when previous is null =>
                StatisticReconciliationLifecycleEventKinds.Approve,
            StatisticReconciliationLifecycleStatuses.Approved when
                !string.Equals(previous!.EpochId,
                    decision.RuntimePin.ExecutionEpochId ??
                        $"NON_FLOW_EPOCH:{decision.WorkAssignmentId}",
                    StringComparison.Ordinal) ||
                previous.Epoch !=
                    (decision.RuntimePin.ExecutionEpoch ?? 1) =>
                StatisticReconciliationLifecycleEventKinds.Restart,
            StatisticReconciliationLifecycleStatuses.Approved =>
                StatisticReconciliationLifecycleEventKinds.Rebuild,
            _ => StatisticReconciliationLifecycleEventKinds.Observe
        };
    private static bool IsContributing(
        StatisticReconciliationLifecycleSourceState source) =>
        source.LifecycleStatus == StatisticReconciliationLifecycleStatuses.Approved &&
        source.IsEffective &&
        string.Equals(source.EpochId, source.CurrentEpochId, StringComparison.Ordinal) &&
        source.ContributionPolicy == StatisticReconciliationLifecyclePolicies.Include;

    private static bool IsSha(string? value)
    {
        if (value is null)
        {
            return false;
        }
        try
        {
            _ = StatisticReconciliationLifecycleCanonical.Sha(value, "SHA");
            return true;
        }
        catch (StatisticReconciliationLifecycleException)
        {
            return false;
        }
    }

    private static string RequiredExpected(
        string? value,
        string path,
        bool required)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }
        if (required)
        {
            throw new InvalidOperationException($"V_INCLUDE requires {path}.");
        }
        return $"NOT_APPLICABLE:{path}";
    }

    private static decimal Parse(string value) =>
        StatisticReconciliationLifecycleCanonical.ParseDecimal(value, "VALUE");

    private static string Format(decimal value) =>
        StatisticReconciliationLifecycleCanonical.Format(value);

    private sealed record ActualMembershipEvidence(
        decimal Unit,
        bool EvidenceComplete,
        string EvidenceBindingSha256);
}
