using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

public sealed class StatisticReconciliationLifecycleEvaluator
{
    public StatisticReconciliationLifecycleSourceState CreateSource(
        string observationId,
        long sequence,
        string eventKind,
        string stableSourceId,
        string reportId,
        string branchId,
        string stepId,
        string epochId,
        long epoch,
        string currentEpochId,
        string lifecycleStatus,
        bool isEffective,
        string? contributionPolicy,
        string? mappingVersionId,
        long? mappingRevision,
        bool mappingVersionLocked,
        string? mappingSha256,
        string? provenanceId,
        string? provenanceSha256,
        string sourceGenerationId,
        string sourceGenerationSha256,
        string canonicalValue)
    {
        observationId = Required(observationId, "SOURCE_OBSERVATION_ID");
        eventKind = Required(eventKind, "SOURCE_EVENT_KIND").ToUpperInvariant();
        if (sequence < 1 || epoch < 1 ||
            !StatisticReconciliationLifecycleEventKinds.All.Contains(eventKind))
            Fail("SOURCE_SEQUENCE_OR_EVENT_INVALID");

        stableSourceId = Required(stableSourceId, "STABLE_SOURCE_ID");
        reportId = Required(reportId, "REPORT_ID");
        branchId = Required(branchId, "BRANCH_ID");
        stepId = Required(stepId, "STEP_ID");
        epochId = Required(epochId, "EPOCH_ID");
        currentEpochId = Required(currentEpochId, "CURRENT_EPOCH_ID");
        lifecycleStatus = Required(lifecycleStatus, "LIFECYCLE_STATUS")
            .ToUpperInvariant();
        if (!StatisticReconciliationLifecycleStatuses.All.Contains(lifecycleStatus))
            Fail("LIFECYCLE_STATUS_INVALID");
        RequireEventStatus(eventKind, lifecycleStatus);

        var policy = string.IsNullOrWhiteSpace(contributionPolicy)
            ? StatisticReconciliationLifecyclePolicies.Exclude
            : contributionPolicy.Trim().ToUpperInvariant();
        if (policy is not StatisticReconciliationLifecyclePolicies.Exclude and
            not StatisticReconciliationLifecyclePolicies.Include)
            Fail("CONTRIBUTION_POLICY_INVALID");

        mappingVersionId = Optional(mappingVersionId, "MAPPING_VERSION_ID");
        mappingSha256 = OptionalSha(mappingSha256, "MAPPING_SHA256");
        provenanceId = Optional(provenanceId, "PROVENANCE_ID");
        provenanceSha256 = OptionalSha(provenanceSha256, "PROVENANCE_SHA256");
        var hasMapping = mappingVersionId is not null || mappingRevision.HasValue ||
                         mappingSha256 is not null || provenanceId is not null ||
                         provenanceSha256 is not null;
        if (hasMapping && (mappingVersionId is null || mappingRevision is not > 0 ||
                           mappingSha256 is null || provenanceId is null ||
                           provenanceSha256 is null))
            Fail("MAPPING_PROVENANCE_INCOMPLETE");
        if (policy == StatisticReconciliationLifecyclePolicies.Include &&
            (!hasMapping || !mappingVersionLocked))
            Fail("INCLUDE_REQUIRES_LOCKED_PROVENANCE");

        sourceGenerationId = Required(sourceGenerationId, "SOURCE_GENERATION_ID");
        sourceGenerationSha256 = Sha(sourceGenerationSha256,
            "SOURCE_GENERATION_SHA256");
        canonicalValue = Decimal(canonicalValue, "SOURCE_VALUE");
        var semantic = Hash(
            "P10_LIFECYCLE_SOURCE_V1", observationId,
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            eventKind, stableSourceId, reportId, branchId, stepId, epochId,
            epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            currentEpochId, lifecycleStatus, isEffective ? "true" : "false",
            policy, mappingVersionId, mappingRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            mappingVersionLocked ? "true" : "false", mappingSha256,
            provenanceId, provenanceSha256, sourceGenerationId,
            sourceGenerationSha256, canonicalValue);

        return new StatisticReconciliationLifecycleSourceState(
            observationId, sequence, eventKind, stableSourceId, reportId,
            branchId, stepId, epochId, epoch, currentEpochId, lifecycleStatus,
            isEffective, policy, mappingVersionId, mappingRevision,
            mappingVersionLocked, mappingSha256, provenanceId,
            provenanceSha256, sourceGenerationId, sourceGenerationSha256,
            canonicalValue, semantic);
    }

    public StatisticReconciliationLifecycleActualContribution CreateActual(
        string observationId,
        StatisticReconciliationLifecycleSourceState source,
        string generationId,
        string? supersedesGenerationId,
        string? priorEffectiveGenerationSha256,
        string canonicalContribution,
        string canonicalDelta,
        string? epochId = null,
        string? contributionPolicy = null,
        string? mappingVersionId = null,
        long? mappingRevision = null,
        string? mappingSha256 = null,
        string? provenanceId = null,
        string? provenanceSha256 = null,
        bool evidenceComplete = true)
    {
        source = ValidateSource(source);
        observationId = Required(observationId, "ACTUAL_OBSERVATION_ID");
        generationId = Required(generationId, "ACTUAL_GENERATION_ID");
        supersedesGenerationId = Optional(supersedesGenerationId,
            "SUPERSEDES_GENERATION_ID");
        priorEffectiveGenerationSha256 = OptionalSha(
            priorEffectiveGenerationSha256, "PRIOR_EFFECTIVE_GENERATION_SHA256");
        canonicalContribution = Decimal(canonicalContribution,
            "ACTUAL_CONTRIBUTION");
        canonicalDelta = Decimal(canonicalDelta, "ACTUAL_DELTA");
        epochId = Optional(epochId, "ACTUAL_EPOCH_ID") ?? source.EpochId;
        contributionPolicy = (Optional(contributionPolicy,
                "ACTUAL_CONTRIBUTION_POLICY") ?? source.ContributionPolicy)
            .ToUpperInvariant();
        mappingVersionId = Optional(mappingVersionId, "ACTUAL_MAPPING_VERSION_ID")
                           ?? source.MappingVersionId;
        mappingRevision ??= source.MappingRevision;
        mappingSha256 = OptionalSha(mappingSha256, "ACTUAL_MAPPING_SHA256")
                        ?? source.MappingSha256;
        provenanceId = Optional(provenanceId, "ACTUAL_PROVENANCE_ID")
                       ?? source.ProvenanceId;
        provenanceSha256 = OptionalSha(provenanceSha256,
                               "ACTUAL_PROVENANCE_SHA256")
                           ?? source.ProvenanceSha256;

        var generationSha = Hash(
            "P10_LIFECYCLE_ACTUAL_GENERATION_V1", generationId,
            supersedesGenerationId, priorEffectiveGenerationSha256,
            source.ObservationId, source.StableSourceId, canonicalContribution,
            canonicalDelta, epochId, contributionPolicy, mappingVersionId,
            mappingRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            mappingSha256, provenanceId, provenanceSha256,
            evidenceComplete ? "true" : "false");
        var semantic = Hash(
            "P10_LIFECYCLE_ACTUAL_V1", observationId, source.ObservationId,
            source.StableSourceId, generationId, generationSha,
            supersedesGenerationId, priorEffectiveGenerationSha256,
            canonicalContribution, canonicalDelta, epochId,
            contributionPolicy, mappingVersionId,
            mappingRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            mappingSha256, provenanceId, provenanceSha256,
            evidenceComplete ? "true" : "false");

        return new StatisticReconciliationLifecycleActualContribution(
            observationId, source.ObservationId, source.StableSourceId,
            generationId, generationSha, supersedesGenerationId,
            priorEffectiveGenerationSha256, canonicalContribution,
            canonicalDelta, epochId, contributionPolicy, mappingVersionId,
            mappingRevision, mappingSha256, provenanceId, provenanceSha256,
            evidenceComplete, semantic);
    }

    public string ComputeRequestSemanticSha256(
        StatisticReconciliationLifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reconciliationId = Required(request.ReconciliationId,
            "RECONCILIATION_ID");
        var binding = Sha(request.ComparisonBindingSha256,
            "COMPARISON_BINDING_SHA256");
        var sources = NormalizeSources(request.Sources);
        var actuals = NormalizeActuals(request.Actuals);
        return Hash("P10_LIFECYCLE_REQUEST_V1", reconciliationId, binding,
            HashSequence(sources.Select(value => value.SemanticSha256)),
            HashSequence(actuals.Select(value => value.SemanticSha256)));
    }

    public StatisticReconciliationLifecycleResult Evaluate(
        StatisticReconciliationLifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reconciliationId = Required(request.ReconciliationId,
            "RECONCILIATION_ID");
        var binding = Sha(request.ComparisonBindingSha256,
            "COMPARISON_BINDING_SHA256");
        var sources = NormalizeSources(request.Sources);
        var actuals = NormalizeActuals(request.Actuals);
        var requestSha = Hash("P10_LIFECYCLE_REQUEST_V1", reconciliationId,
            binding,
            HashSequence(sources.Select(value => value.SemanticSha256)),
            HashSequence(actuals.Select(value => value.SemanticSha256)));

        var actualBySource = actuals
            .GroupBy(value => value.SourceObservationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group =>
            {
                if (group.Count() != 1)
                    Fail("ACTUAL_SOURCE_IDENTITY_DUPLICATE");
                return group.Single();
            }, StringComparer.Ordinal);
        var expectedBeforeBySource = new Dictionary<string, decimal>(
            StringComparer.Ordinal);
        var actualBeforeBySource = new Dictionary<string, decimal>(
            StringComparer.Ordinal);
        var priorGenerationBySource = new Dictionary<string, (string Id, string Sha)>(
            StringComparer.Ordinal);
        var steps = new List<StatisticReconciliationLifecycleStepResult>();
        var seenActualIds = new HashSet<string>(StringComparer.Ordinal);
        var evidenceComplete = true;
        var matched = 0;
        var missing = 0;
        var extra = 0;

        foreach (var source in sources)
        {
            expectedBeforeBySource.TryGetValue(source.StableSourceId,
                out var expectedBefore);
            actualBeforeBySource.TryGetValue(source.StableSourceId,
                out var actualBefore);
            var expectedAfter = IsContributing(source)
                ? ParseDecimal(source.CanonicalValue, "SOURCE_VALUE")
                : 0m;
            var expectedDelta = expectedAfter - expectedBefore;
            actualBySource.TryGetValue(source.ObservationId, out var actual);
            string? root = null;
            decimal? actualAfter = null;
            decimal? actualDelta = null;

            if (actual is null)
            {
                root = StatisticReconciliationLifecycleRootCauses.MissingIdentity;
                missing++;
            }
            else
            {
                seenActualIds.Add(actual.ObservationId);
                actualAfter = ParseDecimal(actual.CanonicalContribution,
                    "ACTUAL_CONTRIBUTION");
                actualDelta = ParseDecimal(actual.CanonicalDelta, "ACTUAL_DELTA");
                if (!actual.EvidenceComplete)
                {
                    root = StatisticReconciliationLifecycleRootCauses.Freshness;
                    evidenceComplete = false;
                }
                else if (!Same(actual.StableSourceId, source.StableSourceId) ||
                         !Same(actual.EpochId, source.EpochId) ||
                         !Same(actual.ContributionPolicy,
                             source.ContributionPolicy) ||
                         !Same(actual.MappingVersionId,
                             source.MappingVersionId) ||
                         actual.MappingRevision != source.MappingRevision ||
                         !Same(actual.MappingSha256, source.MappingSha256) ||
                         !Same(actual.ProvenanceId, source.ProvenanceId) ||
                         !Same(actual.ProvenanceSha256,
                             source.ProvenanceSha256))
                {
                    root = StatisticReconciliationLifecycleRootCauses.SourceMembership;
                }
                else if (!ValidGenerationLineage(source, actual,
                             expectedBefore, expectedAfter,
                             priorGenerationBySource.TryGetValue(
                                 source.StableSourceId, out var prior)
                                 ? prior : null))
                {
                    root = StatisticReconciliationLifecycleRootCauses.Snapshot;
                    evidenceComplete = false;
                }
                else if (actualAfter != expectedAfter ||
                         actualDelta != expectedDelta ||
                         actualDelta != actualAfter - actualBefore)
                {
                    root = StatisticReconciliationLifecycleRootCauses.Projection;
                }

                actualBeforeBySource[source.StableSourceId] = actualAfter.Value;
                priorGenerationBySource[source.StableSourceId] =
                    (actual.GenerationId, actual.GenerationSha256);
            }

            expectedBeforeBySource[source.StableSourceId] = expectedAfter;
            var exact = root is null;
            if (exact)
                matched++;
            var stepSha = Hash("P10_LIFECYCLE_STEP_RESULT_V1",
                source.ObservationId, source.StableSourceId,
                source.Sequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                source.EventKind, source.LifecycleStatus,
                source.ContributionPolicy, source.EpochId,
                Format(expectedBefore), Format(expectedAfter),
                Format(expectedDelta),
                actualAfter.HasValue ? Format(actualAfter.Value) : null,
                actualDelta.HasValue ? Format(actualDelta.Value) : null,
                exact ? "true" : "false", root);
            steps.Add(new StatisticReconciliationLifecycleStepResult(
                source.ObservationId, source.StableSourceId, source.Sequence,
                source.EventKind, source.LifecycleStatus,
                source.ContributionPolicy, source.EpochId,
                Format(expectedBefore), Format(expectedAfter),
                Format(expectedDelta),
                actualAfter.HasValue ? Format(actualAfter.Value) : null,
                actualDelta.HasValue ? Format(actualDelta.Value) : null,
                exact, root, stepSha));
        }

        foreach (var actual in actuals.Where(value =>
                     !seenActualIds.Contains(value.ObservationId)))
        {
            extra++;
            var stepSha = Hash("P10_LIFECYCLE_STEP_RESULT_V1",
                actual.SourceObservationId, actual.StableSourceId, "0",
                "EXTRA", string.Empty, actual.ContributionPolicy,
                actual.EpochId, "0", "0", "0",
                actual.CanonicalContribution, actual.CanonicalDelta, "false",
                StatisticReconciliationLifecycleRootCauses.ExtraIdentity);
            steps.Add(new StatisticReconciliationLifecycleStepResult(
                actual.SourceObservationId, actual.StableSourceId, 0, "EXTRA",
                string.Empty, actual.ContributionPolicy, actual.EpochId,
                "0", "0", "0", actual.CanonicalContribution,
                actual.CanonicalDelta, false,
                StatisticReconciliationLifecycleRootCauses.ExtraIdentity,
                stepSha));
        }

        var firstRoot = steps.Where(value => !value.Exact)
            .OrderBy(value => value.Sequence == 0 ? long.MaxValue : value.Sequence)
            .ThenBy(value => value.SourceObservationId, StringComparer.Ordinal)
            .Select(value => value.RootCause)
            .FirstOrDefault();
        var outcome = firstRoot is null
            ? StatisticReconciliationLifecycleOutcomes.Matched
            : evidenceComplete
                ? StatisticReconciliationLifecycleOutcomes.Mismatched
                : StatisticReconciliationLifecycleOutcomes.Stale;
        var resultSha = Hash("P10_LIFECYCLE_RESULT_V1", reconciliationId,
            binding, outcome, firstRoot,
            evidenceComplete ? "true" : "false",
            sources.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            actuals.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            matched.ToString(System.Globalization.CultureInfo.InvariantCulture),
            missing.ToString(System.Globalization.CultureInfo.InvariantCulture),
            extra.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HashSequence(steps.Select(value => value.StepSemanticSha256)),
            requestSha);
        return new StatisticReconciliationLifecycleResult(
            reconciliationId, binding, outcome, firstRoot, evidenceComplete,
            sources.Length, actuals.Length, matched, missing, extra,
            steps.ToImmutableArray(), requestSha, resultSha);
    }

    private static ImmutableArray<StatisticReconciliationLifecycleSourceState>
        NormalizeSources(
            ImmutableArray<StatisticReconciliationLifecycleSourceState> sources)
    {
        if (sources.IsDefault)
            Fail("SOURCE_TIMELINE_INVALID");
        var validated = sources.Select(ValidateSource).ToArray();
        var deduped = new List<StatisticReconciliationLifecycleSourceState>();
        foreach (var group in validated.GroupBy(value => value.ObservationId,
                     StringComparer.Ordinal))
        {
            if (group.Select(value => value.SemanticSha256)
                    .Distinct(StringComparer.Ordinal).Count() != 1)
                Fail("SOURCE_REPLAY_CONFLICT");
            deduped.Add(group.First());
        }
        if (deduped.GroupBy(value => (value.StableSourceId, value.Sequence))
            .Any(group => group.Count() != 1))
            Fail("SOURCE_SEQUENCE_CONFLICT");
        return deduped.OrderBy(value => value.Sequence)
            .ThenBy(value => value.ObservationId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<StatisticReconciliationLifecycleActualContribution>
        NormalizeActuals(
            ImmutableArray<StatisticReconciliationLifecycleActualContribution> actuals)
    {
        if (actuals.IsDefault)
            Fail("ACTUAL_TIMELINE_INVALID");
        var validated = actuals.Select(ValidateActual).ToArray();
        var deduped = new List<StatisticReconciliationLifecycleActualContribution>();
        foreach (var group in validated.GroupBy(value => value.ObservationId,
                     StringComparer.Ordinal))
        {
            if (group.Select(value => value.SemanticSha256)
                    .Distinct(StringComparer.Ordinal).Count() != 1)
                Fail("ACTUAL_REPLAY_CONFLICT");
            deduped.Add(group.First());
        }
        return deduped.OrderBy(value => value.SourceObservationId,
                StringComparer.Ordinal)
            .ThenBy(value => value.ObservationId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static StatisticReconciliationLifecycleSourceState ValidateSource(
        StatisticReconciliationLifecycleSourceState value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var evaluator = new StatisticReconciliationLifecycleEvaluator();
        var rebuilt = evaluator.CreateSource(
            value.ObservationId, value.Sequence, value.EventKind,
            value.StableSourceId, value.ReportId, value.BranchId, value.StepId,
            value.EpochId, value.Epoch, value.CurrentEpochId,
            value.LifecycleStatus, value.IsEffective, value.ContributionPolicy,
            value.MappingVersionId, value.MappingRevision,
            value.MappingVersionLocked, value.MappingSha256,
            value.ProvenanceId, value.ProvenanceSha256,
            value.SourceGenerationId, value.SourceGenerationSha256,
            value.CanonicalValue);
        if (!Same(rebuilt.SemanticSha256, value.SemanticSha256))
            Fail("SOURCE_SEMANTIC_HASH_INVALID");
        return rebuilt;
    }

    private static StatisticReconciliationLifecycleActualContribution ValidateActual(
        StatisticReconciliationLifecycleActualContribution value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Required(value.ObservationId, "ACTUAL_OBSERVATION_ID");
        Required(value.SourceObservationId, "SOURCE_OBSERVATION_ID");
        Required(value.StableSourceId, "ACTUAL_STABLE_SOURCE_ID");
        Required(value.GenerationId, "ACTUAL_GENERATION_ID");
        Sha(value.GenerationSha256, "ACTUAL_GENERATION_SHA256");
        Optional(value.SupersedesGenerationId, "SUPERSEDES_GENERATION_ID");
        OptionalSha(value.PriorEffectiveGenerationSha256,
            "PRIOR_EFFECTIVE_GENERATION_SHA256");
        var contribution = Decimal(value.CanonicalContribution,
            "ACTUAL_CONTRIBUTION");
        var delta = Decimal(value.CanonicalDelta, "ACTUAL_DELTA");
        Required(value.EpochId, "ACTUAL_EPOCH_ID");
        Required(value.ContributionPolicy, "ACTUAL_CONTRIBUTION_POLICY");
        OptionalSha(value.MappingSha256, "ACTUAL_MAPPING_SHA256");
        OptionalSha(value.ProvenanceSha256, "ACTUAL_PROVENANCE_SHA256");
        var expectedGenerationSha = Hash(
            "P10_LIFECYCLE_ACTUAL_GENERATION_V1", value.GenerationId,
            value.SupersedesGenerationId,
            value.PriorEffectiveGenerationSha256,
            value.SourceObservationId, value.StableSourceId, contribution, delta,
            value.EpochId, value.ContributionPolicy, value.MappingVersionId,
            value.MappingRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.MappingSha256, value.ProvenanceId, value.ProvenanceSha256,
            value.EvidenceComplete ? "true" : "false");
        var expectedSemantic = Hash(
            "P10_LIFECYCLE_ACTUAL_V1", value.ObservationId,
            value.SourceObservationId, value.StableSourceId,
            value.GenerationId, expectedGenerationSha,
            value.SupersedesGenerationId,
            value.PriorEffectiveGenerationSha256, contribution, delta,
            value.EpochId, value.ContributionPolicy, value.MappingVersionId,
            value.MappingRevision?.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value.MappingSha256, value.ProvenanceId, value.ProvenanceSha256,
            value.EvidenceComplete ? "true" : "false");
        if (!Same(value.GenerationSha256, expectedGenerationSha) ||
            !Same(value.SemanticSha256, expectedSemantic))
            Fail("ACTUAL_SEMANTIC_HASH_INVALID");
        return value with
        {
            CanonicalContribution = contribution,
            CanonicalDelta = delta,
            GenerationSha256 = expectedGenerationSha,
            SemanticSha256 = expectedSemantic
        };
    }

    private static bool IsContributing(
        StatisticReconciliationLifecycleSourceState value)
        => value.LifecycleStatus == StatisticReconciliationLifecycleStatuses.Approved &&
           value.IsEffective && Same(value.EpochId, value.CurrentEpochId) &&
           value.ContributionPolicy ==
           StatisticReconciliationLifecyclePolicies.Include;

    private static bool ValidGenerationLineage(
        StatisticReconciliationLifecycleSourceState source,
        StatisticReconciliationLifecycleActualContribution actual,
        decimal expectedBefore,
        decimal expectedAfter,
        (string Id, string Sha)? prior)
    {
        if (prior is null)
            return actual.SupersedesGenerationId is null;
        if (!Same(actual.SupersedesGenerationId, prior.Value.Id))
            return false;
        var isReversal = expectedBefore != 0m && expectedAfter == 0m;
        return !isReversal || Same(actual.PriorEffectiveGenerationSha256,
            prior.Value.Sha);
    }

    private static void RequireEventStatus(string eventKind, string status)
    {
        var expected = eventKind switch
        {
            StatisticReconciliationLifecycleEventKinds.Approve or
            StatisticReconciliationLifecycleEventKinds.Restart =>
                StatisticReconciliationLifecycleStatuses.Approved,
            StatisticReconciliationLifecycleEventKinds.Recall =>
                StatisticReconciliationLifecycleStatuses.Recalled,
            StatisticReconciliationLifecycleEventKinds.Return =>
                StatisticReconciliationLifecycleStatuses.Returned,
            StatisticReconciliationLifecycleEventKinds.Terminate =>
                StatisticReconciliationLifecycleStatuses.Terminated,
            StatisticReconciliationLifecycleEventKinds.Invalidate or
            StatisticReconciliationLifecycleEventKinds.Rollback =>
                StatisticReconciliationLifecycleStatuses.Invalidated,
            _ => null
        };
        if (expected is not null && !Same(expected, status))
            Fail("EVENT_STATUS_CONFLICT");
    }

    private static string? OptionalSha(string? value, string path)
        => value is null ? null : Sha(value, path);

    private static string Hash(params string?[] values)
        => StatisticReconciliationLifecycleCanonical.Hash(values);
    private static string HashSequence(IEnumerable<string> values)
        => StatisticReconciliationLifecycleCanonical.HashSequence(values);
    private static string Required(string? value, string path)
        => StatisticReconciliationLifecycleCanonical.Required(value, path);
    private static string? Optional(string? value, string path)
        => StatisticReconciliationLifecycleCanonical.Optional(value, path);
    private static string Sha(string? value, string path)
        => StatisticReconciliationLifecycleCanonical.Sha(value, path);
    private static string Decimal(string? value, string path)
        => StatisticReconciliationLifecycleCanonical.Decimal(value, path);
    private static decimal ParseDecimal(string value, string path)
        => StatisticReconciliationLifecycleCanonical.ParseDecimal(value, path);
    private static string Format(decimal value)
        => StatisticReconciliationLifecycleCanonical.Format(value);
    private static bool Same(string? left, string? right)
        => StatisticReconciliationLifecycleCanonical.Same(left, right);
    private static void Fail(string detail)
        => StatisticReconciliationLifecycleCanonical.Fail(
            StatisticReconciliationLifecycleFailureCodes.EvidenceInvalid,
            detail);
}
