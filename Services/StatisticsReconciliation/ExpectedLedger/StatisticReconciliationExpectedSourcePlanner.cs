using System.Collections.Immutable;
using System.Text;

namespace tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal sealed class StatisticReconciliationExpectedSourcePlanner
    : IStatisticReconciliationExpectedSourcePlanner
{
    private const int MaxPayloadBytes = 16 * 1024 * 1024;

    private readonly IStatisticReconciliationExpectedLedgerCompiler _inputCompiler;

    public StatisticReconciliationExpectedSourcePlanner(
        IStatisticReconciliationExpectedLedgerCompiler inputCompiler)
    {
        _inputCompiler = inputCompiler;
    }

    public StatisticReconciliationExpectedSourcePlan Plan(
        ExpectedAuthoritativeSourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.LifecycleCandidates.IsDefault ||
            snapshot.ContributionCandidates.IsDefault ||
            snapshot.CurrentEpochMembership is null ||
            snapshot.LockedP8Configuration is null ||
            snapshot.RuntimeMappingContributionLineage is null)
            throw Fail(
                StatisticReconciliationExpectedSourcePlanningFailureReasons.SnapshotInvalid,
                "$.snapshot",
                "Complete authoritative source snapshot required.");

        var membership = NormalizeMembership(snapshot.CurrentEpochMembership);
        var candidates = snapshot.LifecycleCandidates
            .Select((candidate, index) => NormalizeLifecycleCandidate(
                candidate,
                snapshot.ContextPin,
                $"$.lifecycleCandidates[{index}]"))
            .GroupBy(item => item.StableSourceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(LifecycleFingerprint, StringComparer.Ordinal)
                    .Select(replay => replay.First())
                    .OrderBy(item => item.Identity.LifecycleRevision)
                    .ThenBy(item => item.Identity.PayloadRevision)
                    .ToImmutableArray(),
                StringComparer.Ordinal);

        foreach (var sourceId in membership)
        {
            if (!candidates.ContainsKey(sourceId))
                throw Fail(
                    StatisticReconciliationExpectedSourcePlanningFailureReasons
                        .CurrentMemberMissing,
                    "$.currentEpochMembership.sourceIdentityKeys",
                    $"Current-epoch member {sourceId} has no immutable lifecycle candidate.");
        }

        var contributionBySource = NormalizeContributions(
            snapshot.ContributionCandidates,
            candidates.Keys.ToImmutableHashSet(StringComparer.Ordinal));
        var approved = new List<ExpectedLifecycleRevisionCandidate>();
        var included = new List<ExpectedIncludedSource>();
        var decisions = new List<ExpectedSourceDecision>();

        foreach (var group in candidates.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var groupContribution = SelectContribution(
                group.Key,
                contributionBySource);
            if (!membership.Contains(group.Key))
            {
                decisions.AddRange(group.Value.Select(candidate => Decision(
                    candidate,
                    StatisticReconciliationExpectedSourceDecisionReasons
                        .OutsideCurrentEpoch,
                    groupContribution)));
                continue;
            }

            var eligible = group.Value.Where(IsApprovedEffectiveCurrentLocked).ToArray();
            if (eligible.Length > 1)
                throw Fail(
                    StatisticReconciliationExpectedSourcePlanningFailureReasons
                        .LifecycleAmbiguous,
                    "$.lifecycleCandidates",
                    $"Multiple approved/effective/current locked revisions for {group.Key}.");

            var selected = eligible.SingleOrDefault();
            foreach (var candidate in group.Value)
            {
                if (!ReferenceEquals(candidate, selected))
                    decisions.Add(Decision(
                        candidate,
                        LifecycleRejectionReason(candidate, selected is not null),
                        groupContribution));
            }

            if (selected is null)
                continue;

            approved.Add(selected);
            var contribution = groupContribution;
            if (string.Equals(
                    contribution.Policy,
                    StatisticReconciliationExpectedContributionPolicies.Include,
                    StringComparison.Ordinal))
            {
                var sourceSemanticSha256 = H(
                    "P10_EXPECTED_INCLUDED_SOURCE_V1",
                    LifecycleFingerprint(selected),
                    ContributionFingerprint(contribution));
                included.Add(new ExpectedIncludedSource(
                    selected,
                    contribution,
                    sourceSemanticSha256));
                decisions.Add(Decision(
                    selected,
                    StatisticReconciliationExpectedSourceDecisionReasons.Included,
                    contribution));
            }
            else
            {
                var reason = contribution.VersionId == "DEFAULT"
                    ? StatisticReconciliationExpectedSourceDecisionReasons
                        .ContributionDefaultExclude
                    : StatisticReconciliationExpectedSourceDecisionReasons
                        .ContributionExcluded;
                decisions.Add(Decision(selected, reason, contribution));
            }
        }

        var approvedOrdered = approved
            .OrderBy(item => item.StableSourceId, StringComparer.Ordinal)
            .ToImmutableArray();
        var membershipForApproved = new CurrentEpochFlowMembership(
            snapshot.ContextPin,
            snapshot.CurrentEpochMembership.RuntimeKind,
            snapshot.CurrentEpochMembership.FlowTemplateVersionId,
            snapshot.CurrentEpochMembership.FlowPayloadSha256,
            snapshot.CurrentEpochMembership.FlowInstanceId,
            snapshot.CurrentEpochMembership.ExecutionEpochId,
            snapshot.CurrentEpochMembership.ExecutionEpoch,
            snapshot.CurrentEpochMembership.ExecutionEpochRevision,
            approvedOrdered.Select(item => item.StableSourceId));
        var payloads = approvedOrdered.Select(candidate =>
            new ApprovedEffectiveReportPayloadRevision(
                candidate.Identity,
                candidate.PayloadDocumentId,
                candidate.Identity.DynamicFormVersionId,
                snapshot.ContextPin.DynamicFormSchemaSha256,
                candidate.Identity.PayloadCanonicalSha256,
                candidate.PayloadJson));
        var compileInput = new StatisticReconciliationExpectedLedgerCompileInput(
            new ApprovedEffectiveReportPayloadRevisionSet(snapshot.ContextPin, payloads),
            membershipForApproved,
            snapshot.LockedP8Configuration,
            snapshot.RuntimeMappingContributionLineage,
            new ImmutableSourceIdentitySet(
                snapshot.ContextPin,
                approvedOrdered.Select(item => item.Identity)));
        var boundInputs = _inputCompiler.BindInputs(compileInput);

        var lifecycleSemanticSha256 = H(
            "P10_EXPECTED_LIFECYCLE_SELECTION_V1",
            candidates
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .SelectMany(item => item.Value)
                .Select(LifecycleFingerprint));
        var contributionSemanticSha256 = H(
            "P10_EXPECTED_CONTRIBUTION_SELECTION_V1",
            contributionBySource
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .SelectMany(item => item.Value)
                .Select(ContributionFingerprint));
        var orderedDecisions = decisions
            .OrderBy(item => item.StableSourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.PayloadRevision)
            .ThenBy(item => item.ReasonCode, StringComparer.Ordinal)
            .ToImmutableArray();
        var sourceSetSha256 = H(
            "P10_EXPECTED_SOURCE_SET_V1",
            approvedOrdered.Select(LifecycleFingerprint)
                .Concat(orderedDecisions.Select(item => item.DecisionSemanticSha256)));

        return new StatisticReconciliationExpectedSourcePlan(
            compileInput,
            boundInputs,
            approvedOrdered,
            included.OrderBy(
                item => item.LifecycleRevision.StableSourceId,
                StringComparer.Ordinal),
            orderedDecisions,
            lifecycleSemanticSha256,
            contributionSemanticSha256,
            sourceSetSha256);
    }

    private static ImmutableHashSet<string> NormalizeMembership(
        CurrentEpochFlowMembership membership)
    {
        if (membership.SourceIdentityKeys.IsDefault)
            throw Fail(
                StatisticReconciliationExpectedSourcePlanningFailureReasons.SnapshotInvalid,
                "$.currentEpochMembership.sourceIdentityKeys",
                "Server-derived membership required.");

        var result = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        for (var index = 0; index < membership.SourceIdentityKeys.Length; index++)
        {
            var sourceId = Id(
                membership.SourceIdentityKeys[index],
                $"$.currentEpochMembership.sourceIdentityKeys[{index}]");
            if (!result.Add(sourceId))
                throw Fail(
                    StatisticReconciliationExpectedSourcePlanningFailureReasons.SnapshotInvalid,
                    $"$.currentEpochMembership.sourceIdentityKeys[{index}]",
                    "Duplicate current-epoch member.");
        }

        return result.ToImmutable();
    }

    private static ExpectedLifecycleRevisionCandidate NormalizeLifecycleCandidate(
        ExpectedLifecycleRevisionCandidate candidate,
        ExpectedLedgerCompilationContextPin context,
        string path)
    {
        if (candidate is null || candidate.Identity is null)
            throw Fail(
                StatisticReconciliationExpectedSourcePlanningFailureReasons
                    .LifecycleCandidateInvalid,
                path,
                "Lifecycle candidate and identity required.");
        var sourceId = Id(candidate.StableSourceId, $"{path}.stableSourceId");
        if (!string.Equals(sourceId, candidate.Identity.IdentityKey, StringComparison.Ordinal) ||
            !string.Equals(candidate.Identity.WorkId, context.WorkId, StringComparison.Ordinal) ||
            !string.Equals(
                candidate.Identity.ScopeAssignmentId,
                context.ScopeAssignmentId,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.Identity.DynamicFormVersionId,
                context.DynamicFormVersionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.Identity.FlowInstanceId,
                context.FlowInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.Identity.ExecutionEpochId,
                context.ExecutionEpochId,
                StringComparison.Ordinal))
            throw Fail(
                StatisticReconciliationExpectedSourcePlanningFailureReasons
                    .LifecycleCandidateInvalid,
                path,
                "Lifecycle identity does not bind the requested context.");
        if (candidate.Identity.PayloadRevision <= 0 ||
            candidate.Identity.LifecycleRevision <= 0 ||
            !StatisticReconciliationExpectedLifecycleStatuses.All.Contains(
                candidate.LifecycleStatus) ||
            !StatisticReconciliationExpectedRuntimeDispositions.All.Contains(
                candidate.RuntimeDisposition))
            throw Fail(
                StatisticReconciliationExpectedSourcePlanningFailureReasons
                    .LifecycleCandidateInvalid,
                path,
                "Known positive lifecycle revision and runtime disposition required.");

        Id(candidate.PayloadDocumentId, $"{path}.payloadDocumentId");
        Sha(candidate.Identity.PayloadOwnerSha256, $"{path}.payloadOwnerSha256");
        Sha(candidate.Identity.PayloadCanonicalSha256, $"{path}.payloadCanonicalSha256");
        Sha(candidate.Identity.LifecycleSha256, $"{path}.lifecycleSha256");
        var canonical = StatisticReconciliationExpectedLedgerCanonicalizer.NormalizeObject(
            candidate.PayloadJson,
            MaxPayloadBytes,
            $"{path}.payloadJson",
            StatisticReconciliationExpectedSourcePlanningFailureReasons
                .LifecycleCandidateInvalid);
        if (!string.Equals(
                canonical.Sha256,
                candidate.Identity.PayloadCanonicalSha256,
                StringComparison.Ordinal))
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons
                    .PayloadContentHashMismatch,
                $"{path}.payloadJson",
                "Lifecycle candidate payload hash mismatch.");
        if (candidate.RuntimePin is null)
        {
            if (context.SourceOwnerRunId is not null)
                throw Fail(
                    StatisticReconciliationExpectedSourcePlanningFailureReasons
                        .LifecycleCandidateInvalid,
                    $"{path}.runtimePin",
                    "Production lifecycle candidate requires authoritative runtime pin.");
        }
        else
        {
            StatisticReconciliationExpectedAuthoritativeRuntimePinIntegrity
                .Validate(candidate.RuntimePin, context);
            if (candidate.RuntimeDisposition ==
                    StatisticReconciliationExpectedRuntimeDispositions.Current &&
                context.RuntimeKind ==
                    StatisticReconciliationExpectedRuntimeKinds.Flow &&
                (candidate.RuntimePin.IsCanonicalEpoch != true ||
                 candidate.RuntimePin.ExecutionEpochId !=
                    candidate.RuntimePin.CurrentExecutionEpochId ||
                 candidate.RuntimePin.ExecutionEpoch !=
                    candidate.RuntimePin.CurrentExecutionEpoch))
                throw Fail(
                    StatisticReconciliationExpectedSourcePlanningFailureReasons
                        .LifecycleCandidateInvalid,
                    $"{path}.runtimePin",
                    "Current flow lifecycle candidate requires the canonical current epoch.");
        }
        return new ExpectedLifecycleRevisionCandidate(
            sourceId,
            candidate.Identity,
            candidate.PayloadDocumentId,
            canonical.Value,
            candidate.LifecycleStatus,
            candidate.IsEffective,
            candidate.IsLocked,
            candidate.RuntimeDisposition,
            candidate.RuntimePin);
    }

    private static Dictionary<string, ImmutableArray<ExpectedContributionCandidate>>
        NormalizeContributions(
            ImmutableArray<ExpectedContributionCandidate> candidates,
            ImmutableHashSet<string> lifecycleSourceIds)
    {
        var normalized = new List<ExpectedContributionCandidate>();
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            var path = $"$.contributionCandidates[{index}]";
            if (candidate is null)
                throw Fail(
                    StatisticReconciliationExpectedSourcePlanningFailureReasons
                        .ContributionInvalid,
                    path,
                    "Contribution candidate required.");
            var sourceId = Id(candidate.StableSourceId, $"{path}.stableSourceId");
            if (!lifecycleSourceIds.Contains(sourceId) ||
                candidate.Revision <= 0 ||
                !candidate.IsLocked)
                throw Fail(
                    StatisticReconciliationExpectedSourcePlanningFailureReasons
                        .ContributionInvalid,
                    path,
                    "Locked contribution must bind an immutable lifecycle source.");
            var policy = candidate.Policy ??
                StatisticReconciliationExpectedContributionPolicies.Exclude;
            if (policy is not StatisticReconciliationExpectedContributionPolicies.Exclude and
                not StatisticReconciliationExpectedContributionPolicies.Include)
                throw Fail(
                    StatisticReconciliationExpectedSourcePlanningFailureReasons
                        .ContributionInvalid,
                    $"{path}.policy",
                    "Contribution policy must be V_EXCLUDE or V_INCLUDE.");
            Id(candidate.VersionId, $"{path}.versionId");
            Id(candidate.ProvenanceId, $"{path}.provenanceId");
            Sha(candidate.PolicySha256, $"{path}.policySha256");
            Sha(candidate.ProvenanceSha256, $"{path}.provenanceSha256");
            normalized.Add(new ExpectedContributionCandidate(
                sourceId,
                policy,
                candidate.VersionId,
                candidate.Revision,
                candidate.PolicySha256,
                candidate.ProvenanceId,
                candidate.ProvenanceSha256,
                candidate.IsLocked));
        }

        return normalized
            .GroupBy(item => item.StableSourceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(ContributionFingerprint, StringComparer.Ordinal)
                    .Select(replay => replay.First())
                    .OrderBy(item => item.VersionId, StringComparer.Ordinal)
                    .ThenBy(item => item.Revision)
                    .ToImmutableArray(),
                StringComparer.Ordinal);
    }

    private static ExpectedContributionCandidate SelectContribution(
        string sourceId,
        IReadOnlyDictionary<string, ImmutableArray<ExpectedContributionCandidate>> bySource)
    {
        if (!bySource.TryGetValue(sourceId, out var candidates) || candidates.Length == 0)
            return DefaultContribution(sourceId);
        if (candidates.Length > 1)
            throw Fail(
                StatisticReconciliationExpectedSourcePlanningFailureReasons
                    .ContributionAmbiguous,
                "$.contributionCandidates",
                $"Multiple locked contribution versions for {sourceId}.");
        return candidates[0];
    }

    private static ExpectedContributionCandidate DefaultContribution(string sourceId)
        => new(
            sourceId,
            StatisticReconciliationExpectedContributionPolicies.Exclude,
            "DEFAULT",
            1,
            H("P10_EXPECTED_DEFAULT_EXCLUDE_POLICY_V1", sourceId),
            "NONE",
            H("P10_EXPECTED_DEFAULT_EXCLUDE_PROVENANCE_V1", sourceId),
            true);

    private static bool IsApprovedEffectiveCurrentLocked(
        ExpectedLifecycleRevisionCandidate candidate)
        => string.Equals(
               candidate.LifecycleStatus,
               StatisticReconciliationExpectedLifecycleStatuses.Approved,
               StringComparison.Ordinal) &&
           candidate.IsEffective &&
           candidate.IsLocked &&
           string.Equals(
               candidate.RuntimeDisposition,
               StatisticReconciliationExpectedRuntimeDispositions.Current,
               StringComparison.Ordinal);

    private static string LifecycleRejectionReason(
        ExpectedLifecycleRevisionCandidate candidate,
        bool hasSelectedRevision)
    {
        if (candidate.RuntimeDisposition ==
                StatisticReconciliationExpectedRuntimeDispositions.Invalidated ||
            candidate.LifecycleStatus ==
                StatisticReconciliationExpectedLifecycleStatuses.Invalidated)
            return StatisticReconciliationExpectedSourceDecisionReasons.Invalidated;
        if (candidate.RuntimeDisposition ==
                StatisticReconciliationExpectedRuntimeDispositions.Superseded ||
            candidate.LifecycleStatus ==
                StatisticReconciliationExpectedLifecycleStatuses.Superseded)
            return StatisticReconciliationExpectedSourceDecisionReasons.Superseded;
        if (candidate.RuntimeDisposition ==
                StatisticReconciliationExpectedRuntimeDispositions.Terminated ||
            candidate.LifecycleStatus ==
                StatisticReconciliationExpectedLifecycleStatuses.Terminated)
            return StatisticReconciliationExpectedSourceDecisionReasons.Terminated;
        if (candidate.LifecycleStatus is
            StatisticReconciliationExpectedLifecycleStatuses.Recalled or
            StatisticReconciliationExpectedLifecycleStatuses.Returned)
            return StatisticReconciliationExpectedSourceDecisionReasons.RecalledOrReturned;
        if (candidate.LifecycleStatus is
            StatisticReconciliationExpectedLifecycleStatuses.Draft or
            StatisticReconciliationExpectedLifecycleStatuses.Submitted)
            return StatisticReconciliationExpectedSourceDecisionReasons.DraftOrUnapproved;
        if (!candidate.IsEffective)
            return StatisticReconciliationExpectedSourceDecisionReasons.NotEffective;
        if (!candidate.IsLocked)
            return StatisticReconciliationExpectedSourceDecisionReasons.NotLocked;
        if (hasSelectedRevision)
            return StatisticReconciliationExpectedSourceDecisionReasons.OlderApprovedRevision;
        return StatisticReconciliationExpectedSourceDecisionReasons.NotEffective;
    }

    private static ExpectedSourceDecision Decision(
        ExpectedLifecycleRevisionCandidate candidate,
        string reason,
        ExpectedContributionCandidate contribution)
    {
        var disposition = reason ==
            StatisticReconciliationExpectedSourceDecisionReasons.Included
            ? "INCLUDED"
            : "REJECTED";
        var semanticSha256 = H(
            "P10_EXPECTED_SOURCE_DECISION_V2",
            LifecycleFingerprint(candidate),
            candidate.StableSourceId,
            candidate.Identity.PayloadRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            candidate.PayloadDocumentId,
            candidate.LifecycleStatus,
            candidate.IsEffective ? "true" : "false",
            candidate.IsLocked ? "true" : "false",
            candidate.RuntimeDisposition,
            disposition,
            reason,
            contribution.Policy ??
                StatisticReconciliationExpectedContributionPolicies.Exclude,
            contribution.VersionId,
            contribution.Revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            contribution.PolicySha256,
            contribution.ProvenanceId,
            contribution.ProvenanceSha256,
            candidate.RuntimePin?.RuntimeSemanticSha256 ?? "~");
        return new ExpectedSourceDecision(
            candidate.StableSourceId,
            candidate.Identity,
            candidate.PayloadDocumentId,
            candidate.LifecycleStatus,
            candidate.IsEffective,
            candidate.IsLocked,
            candidate.RuntimeDisposition,
            disposition,
            reason,
            contribution.Policy ??
                StatisticReconciliationExpectedContributionPolicies.Exclude,
            contribution.VersionId,
            contribution.Revision,
            contribution.PolicySha256,
            contribution.ProvenanceId,
            contribution.ProvenanceSha256,
            semanticSha256,
            candidate.RuntimePin);
    }

    private static string LifecycleFingerprint(ExpectedLifecycleRevisionCandidate candidate)
        => H(
            "P10_EXPECTED_LIFECYCLE_CANDIDATE_V2",
            candidate.StableSourceId,
            candidate.Identity.ReportId,
            candidate.Identity.PayloadRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            candidate.Identity.PayloadOwnerSha256,
            candidate.Identity.PayloadCanonicalSha256,
            candidate.Identity.LifecycleRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            candidate.Identity.LifecycleSha256,
            candidate.LifecycleStatus,
            candidate.IsEffective ? "1" : "0",
            candidate.IsLocked ? "1" : "0",
            candidate.RuntimeDisposition,
            candidate.RuntimePin?.RuntimeSemanticSha256 ?? "~");

    private static string ContributionFingerprint(ExpectedContributionCandidate candidate)
        => H(
            "P10_EXPECTED_CONTRIBUTION_CANDIDATE_V1",
            candidate.StableSourceId,
            candidate.Policy ??
                StatisticReconciliationExpectedContributionPolicies.Exclude,
            candidate.VersionId,
            candidate.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            candidate.PolicySha256,
            candidate.ProvenanceId,
            candidate.ProvenanceSha256,
            candidate.IsLocked ? "1" : "0");

    private static string H(string domain, params string[] fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, fields);

    private static string H(string domain, IEnumerable<string> fields)
        => StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(domain, fields);

    private static string Id(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 512 ||
            value != value.Trim() ||
            !value.IsNormalized(NormalizationForm.FormC) ||
            value.Any(character => char.IsControl(character) || character == '\u001f'))
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.IdentifierInvalid,
                path,
                "Canonical bounded identifier required.");
        return value;
    }

    private static string Sha(string? value, string path)
    {
        if (value is null ||
            value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and
                                             not (>= 'a' and <= 'f')))
            throw Fail(
                StatisticReconciliationExpectedLedgerInputFailureReasons.Sha256Invalid,
                path,
                "Lowercase SHA-256 required.");
        return value;
    }

    private static StatisticReconciliationExpectedLedgerInputException Fail(
        string reason,
        string path,
        string message)
        => new(reason, path, message);
}

internal sealed class StatisticReconciliationExpectedLedgerAuthoritativeInputProvider
    : IStatisticReconciliationExpectedLedgerAuthoritativeInputProvider
{
    private readonly IStatisticReconciliationExpectedAuthoritativeSnapshotReader _reader;
    private readonly IStatisticReconciliationExpectedSourcePlanner _planner;

    public StatisticReconciliationExpectedLedgerAuthoritativeInputProvider(
        IStatisticReconciliationExpectedAuthoritativeSnapshotReader reader,
        IStatisticReconciliationExpectedSourcePlanner planner)
    {
        _reader = reader;
        _planner = planner;
    }

    public async ValueTask<StatisticReconciliationExpectedLedgerCompileInput> ReadAsync(
        ExpectedLedgerCompilationContextPin requestedContext,
        CancellationToken cancellationToken)
    {
        var snapshot = await _reader.ReadAsync(requestedContext, cancellationToken);
        return _planner.Plan(snapshot).CompileInput;
    }
}
