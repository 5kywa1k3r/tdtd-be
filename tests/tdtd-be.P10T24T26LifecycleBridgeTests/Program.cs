using System.Collections.Immutable;
using MongoDB.Bson;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsRun;

var cases = new List<(string Id, Func<Task<LifecycleFixtureResult>> Create,
    Action<StatisticReconciliationActualLifecycleEvidence> Assert)>
{
    Current("P10-APPROVE-01", "DRAFT", "V_EXCLUDE", 0, AssertMatchedZero),
    Current("P10-APPROVE-02", "SUBMITTED", "V_EXCLUDE", 0, AssertMatchedZero),
    Current("P10-APPROVE-03", "APPROVED", "V_INCLUDE", 2, AssertMatchedOne),
    Current("P10-APPROVE-04", "APPROVED", "V_EXCLUDE", 0, AssertMatchedZero),
    Reversal("P10-RECALL-01", "RECALLED", "REVIEW_RECALL_APPROVED"),
    Reversal("P10-RECALL-02", "RECALLED", "WITHDRAW"),
    Reversal("P10-RECALL-03", "RECALLED", "REVIEW_RECALL_APPROVED"),
    Current("P10-RECALL-04", "APPROVED", "V_INCLUDE", 1, AssertMatchedOne),
    Reversal("P10-RETURN-01", "RETURNED", "REVIEW_RETURN"),
    Reversal("P10-RETURN-02", "RETURNED", "REVIEW_RETURN"),
    Reversal("P10-RETURN-03", "RETURNED", "REVIEW_RETURN"),
    Current("P10-RETURN-04", "APPROVED", "V_INCLUDE", 1, AssertMatchedOne),
    Current("P10-CONTRIBUTION-01", "APPROVED", "V_EXCLUDE", 0, AssertMatchedZero),
    Current("P10-CONTRIBUTION-02", "APPROVED", "V_INCLUDE", 1, AssertMatchedOne),
    Current("P10-CONTRIBUTION-03", "APPROVED", "V_INCLUDE", 2, AssertMatchedOne),
    Current("P10-CONTRIBUTION-04", "APPROVED", "V_INCLUDE", 3, AssertMatchedOne),
    Reversal("P10-TERMINATE-01", "TERMINATED", "REVIEW_DEACTIVATE_REPORT"),
    Reversal("P10-TERMINATE-02", "INVALIDATED", "AUTO_AGGREGATE_REVIEW_INVALIDATED"),
    Current("P10-TERMINATE-03", "APPROVED", "V_EXCLUDE", 0, AssertMatchedZero),
    Current("P10-TERMINATE-04", "APPROVED", "V_INCLUDE", 1, AssertMatchedOne),
    Reversal("P10-LFCROLLBACK-01", "INVALIDATED", "AUTO_AGGREGATE_REVIEW_INVALIDATED"),
    Reversal("P10-LFCROLLBACK-02", "RECALLED", "REVIEW_RECALL_APPROVED"),
    Reversal("P10-LFCROLLBACK-03", "RETURNED", "REVIEW_RETURN"),
    Reversal("P10-LFCROLLBACK-04", "TERMINATED", "REVIEW_DEACTIVATE_REPORT")
};

var passed = 0;
foreach (var test in cases)
{
    var fixture = await test.Create();
    AssertProductionCandidateShape(test.Id, fixture);
    var evidence = LifecycleFixture.Build(fixture.Input);
    AssertEnvelope(test.Id, evidence);
    test.Assert(evidence);
    var replay = LifecycleFixture.Build(fixture.Input);
    Require(replay.Manifest.ManifestSha256 == evidence.Manifest.ManifestSha256,
        $"{test.Id}:REPLAY_MANIFEST");
    Require(replay.Result.ResultSemanticSha256 == evidence.Result.ResultSemanticSha256,
        $"{test.Id}:REPLAY_RESULT");
    Console.WriteLine($"PASS {test.Id}");
    passed++;
}
Require(passed == 24, "EXACT24_COUNT");

var missing = await LifecycleFixture.CurrentAsync(
    "AUX-MISSING", "APPROVED", "V_INCLUDE", targetCount: 0, omitActual: true);
var missingEvidence = LifecycleFixture.Build(missing.Input);
Require(missingEvidence.Result.Outcome ==
    StatisticReconciliationLifecycleOutcomes.Mismatched,
    "MISSING_OUTCOME");
Require(missingEvidence.Result.MissingCount == 1 &&
    missingEvidence.Result.ExtraCount == 0,
    "MISSING_COUNT");
Require(missingEvidence.Result.RootCause ==
    StatisticReconciliationLifecycleRootCauses.MissingIdentity,
    "MISSING_ROOT");

var excludedOmission = await LifecycleFixture.CurrentAsync(
    "AUX-EXCLUDED-OMISSION", "APPROVED", "V_EXCLUDE",
    targetCount: 0, omitActual: true);
var excludedOmissionEvidence = LifecycleFixture.Build(
    excludedOmission.Input);
Require(excludedOmissionEvidence.Result.Outcome ==
    StatisticReconciliationLifecycleOutcomes.Mismatched,
    "EXCLUDED_OMISSION_OUTCOME");
Require(excludedOmissionEvidence.Result.MissingCount == 1 &&
    excludedOmissionEvidence.Result.ExtraCount == 0,
    "EXCLUDED_OMISSION_COUNT");
Require(excludedOmissionEvidence.Result.RootCause ==
    StatisticReconciliationLifecycleRootCauses.MissingIdentity,
    "EXCLUDED_OMISSION_ROOT");

var extra = await LifecycleFixture.OrphanActualAsync("AUX-EXTRA");
var extraEvidence = LifecycleFixture.Build(extra.Input);
Require(extraEvidence.Result.Outcome ==
    StatisticReconciliationLifecycleOutcomes.Mismatched,
    "EXTRA_OUTCOME");
Require(extraEvidence.Result.ExtraCount == 1 &&
    extraEvidence.Result.MissingCount == 0,
    "EXTRA_COUNT");
Require(extraEvidence.Result.Steps.Any(step =>
        step.RootCause ==
            StatisticReconciliationLifecycleRootCauses.ExtraIdentity),
    "EXTRA_ROOT");

var outOfScope = await LifecycleFixture.OrphanActualAsync(
    "AUX-OUT-OF-SCOPE", orphanInLifecycleMetricScope: false);
var outOfScopeEvidence = LifecycleFixture.Build(outOfScope.Input);
Require(outOfScopeEvidence.Result.Outcome ==
        StatisticReconciliationLifecycleOutcomes.Matched &&
    outOfScopeEvidence.Result.MissingCount == 0 &&
    outOfScopeEvidence.Result.ExtraCount == 0,
    "OUT_OF_SCOPE_CANDIDATE_IGNORED");

var staleEpoch = await LifecycleFixture.CurrentAsync(
    "AUX-STALE-EPOCH", "APPROVED", "V_EXCLUDE", targetCount: 0,
    executionEpoch: 1, currentExecutionEpoch: 2, currentScopeEpoch: 2);
var staleEpochEvidence = LifecycleFixture.Build(staleEpoch.Input);
AssertMatchedZero(staleEpochEvidence);
var staleScope = LifecycleFixture.MetricScopeFor("AUX-STALE-EPOCH", 2);
var staleAssignment = new WorkAssignment
{
    Id = staleScope.ScopeAssignmentId,
    FlowInstanceId = staleScope.FlowInstanceId,
    FlowBranchId = staleScope.RuntimeFlowBranchId,
    FlowStepId = staleScope.RuntimeFlowStepId,
    FlowEffectiveStatus = staleScope.FlowEffectiveStatus,
    IsFlowFinalNode = false,
    FlowExecutionEpoch = 1
};
Require(StatRunDirectProjectionService.InLifecycleMetricScope(
        staleScope, staleAssignment),
    "STALE_EPOCH_CANDIDATE_INCLUDED");
staleAssignment.FlowExecutionEpoch = 3;
ExpectThrows(
    () => StatRunDirectProjectionService.InLifecycleMetricScope(
        staleScope, staleAssignment),
    "FUTURE_EPOCH_CANDIDATE_FAILS_CLOSED");
ExpectThrows(
    () => StatRunDirectLifecycleMetricScopeCanonical.RequireValid(
        staleScope with { ExecutionEpoch = 3 }),
    "SCOPE_EPOCH_ONE_BIT_DRIFT");
var metricScope = LifecycleFixture.MetricScopeFor("AUX-SCOPE-DRIFT");
ExpectThrows(
    () => StatRunDirectLifecycleMetricScopeCanonical.RequireValid(
        metricScope with { ScopeMode = "FLOW_BRANCH" }),
    "SCOPE_MODE_ONE_BIT_DRIFT");
ExpectThrows(
    () => StatRunDirectLifecycleMetricScopeCanonical.RequireValid(
        metricScope with { ScopeId = LifecycleFixture.O("wrong-scope") }),
    "SCOPE_ID_ONE_BIT_DRIFT");
ExpectThrows(
    () => StatRunDirectLifecycleMetricScopeCanonical.RequireValid(
        metricScope with
        {
            RuntimeFlowBranchId = LifecycleFixture.O("wrong-branch")
        }),
    "SCOPE_BRANCH_ONE_BIT_DRIFT");
ExpectThrows(
    () => StatRunDirectLifecycleMetricScopeCanonical.RequireValid(
        metricScope with
        {
            RuntimeFlowStepId = LifecycleFixture.O("wrong-step")
        }),
    "SCOPE_STEP_ONE_BIT_DRIFT");
ExpectThrows(
    () => StatRunDirectLifecycleMetricScopeCanonical.RequireValid(
        metricScope with
        {
            FlowInstanceId = LifecycleFixture.O("wrong-flow-instance")
        }),
    "SCOPE_FLOW_INSTANCE_ONE_BIT_DRIFT");

var include = await LifecycleFixture.CurrentAsync(
    "AUX-INCLUDE", "APPROVED", "V_INCLUDE", targetCount: 2);
var includeEvidence = LifecycleFixture.Build(include.Input);
var reordered = include.Input with
{
    P9ContributionAudit =
        LifecycleFixture.ReorderTargets(include.Input.P9ContributionAudit)
};
var reorderedEvidence = LifecycleFixture.Build(reordered);
Require(includeEvidence.ManifestSha256 == reorderedEvidence.ManifestSha256,
    "TARGET_REORDER_STABLE");

var zeroTargets = await LifecycleFixture.CurrentAsync(
    "AUX-ZERO-TARGET", "APPROVED", "V_INCLUDE", targetCount: 0);
var zeroTargetEvidence = LifecycleFixture.Build(zeroTargets.Input);
Require(zeroTargetEvidence.Result.Outcome ==
    StatisticReconciliationLifecycleOutcomes.Stale,
    "INCLUDE_ZERO_TARGET_FAIL_CLOSED");

var ownerMutation = await LifecycleFixture.CurrentAsync(
    "AUX-OWNER-MUTATION", "APPROVED", "V_INCLUDE", targetCount: 1,
    actualContributionDecision: "EXCLUDE");
var ownerMutationEvidence = LifecycleFixture.Build(ownerMutation.Input);
Require(ownerMutationEvidence.Result.Outcome ==
    StatisticReconciliationLifecycleOutcomes.Stale,
    "OWNER_CONTRIBUTION_MUTATION_FAIL_CLOSED");

var excludedOwnerMutation = await LifecycleFixture.CurrentAsync(
    "AUX-EXCLUDED-OWNER-MUTATION", "APPROVED", "V_EXCLUDE",
    targetCount: 0, actualContributionDecision: "INCLUDE");
var excludedOwnerMutationEvidence = LifecycleFixture.Build(
    excludedOwnerMutation.Input);
Require(excludedOwnerMutationEvidence.Result.Outcome ==
    StatisticReconciliationLifecycleOutcomes.Stale,
    "EXCLUDED_OWNER_MUTATION_FAIL_CLOSED");

var nonFlowInclude = await LifecycleFixture.NonFlowCurrentAsync(
    "AUX-NONFLOW-INCLUDE", targetCount: 2);
var nonFlowEvidence = LifecycleFixture.Build(nonFlowInclude.Input);
AssertMatchedOne(nonFlowEvidence);
Require(nonFlowEvidence.Manifest.P9ContributionAudit.Sources.Single().RuntimeKind ==
        StatisticReconciliationExpectedRuntimeKinds.NonFlow &&
    nonFlowEvidence.Manifest.P9ContributionAudit.Sources.Single()
        .NonFlowPolicyVersion == "P10_NON_FLOW_CONTRIBUTION_POLICY_V1" &&
    nonFlowEvidence.Manifest.P9ContributionAudit.Sources.Single()
        .FlowInstanceId is null,
    "NONFLOW_DISCRIMINATED_SOURCE_AUDIT");
var nonFlowReordered = LifecycleFixture.Build(nonFlowInclude.Input with
{
    P9ContributionAudit = LifecycleFixture.ReorderTargets(
        nonFlowInclude.Input.P9ContributionAudit)
});
Require(nonFlowReordered.ManifestSha256 == nonFlowEvidence.ManifestSha256,
    "NONFLOW_TARGET_REORDER_STABLE");

var nonFlowZeroTarget = await LifecycleFixture.NonFlowCurrentAsync(
    "AUX-NONFLOW-ZERO-TARGET", targetCount: 0);
Require(LifecycleFixture.Build(nonFlowZeroTarget.Input).Result.Outcome ==
        StatisticReconciliationLifecycleOutcomes.Stale,
    "NONFLOW_ZERO_TARGET_FAIL_CLOSED");

var nonFlowOwnerMutation = await LifecycleFixture.NonFlowCurrentAsync(
    "AUX-NONFLOW-OWNER-MUTATION", targetCount: 1,
    actualContributionDecision: "EXCLUDE");
Require(LifecycleFixture.Build(nonFlowOwnerMutation.Input).Result.Outcome ==
        StatisticReconciliationLifecycleOutcomes.Stale,
    "NONFLOW_OWNER_DECISION_FAIL_CLOSED");

var nonFlowAudit = nonFlowInclude.Input.P9ContributionAudit;
var nonFlowSource = nonFlowAudit.Sources.Single();
ExpectThrows(
    () => StatRunLifecycleContributionAuditCanonical.RequireValid(
        StatRunLifecycleContributionAuditCanonical.Normalize(
            nonFlowAudit with
            {
                Sources =
                [nonFlowSource with
                {
                    NonFlowPolicyRevision =
                        nonFlowSource.NonFlowPolicyRevision + 1
                }]
            })),
    "NONFLOW_POLICY_REVISION_TAMPER_FAILS");
ExpectThrows(
    () => StatRunLifecycleContributionAuditCanonical.RequireValid(
        StatRunLifecycleContributionAuditCanonical.Normalize(
            nonFlowAudit with
            {
                Sources =
                [nonFlowSource with
                {
                    ConfigHash = LifecycleFixture.H("wrong-config")
                }]
            })),
    "NONFLOW_CONFIG_TAMPER_FAILS");
ExpectThrows(
    () => StatRunLifecycleContributionAuditCanonical.RequireValid(
        StatRunLifecycleContributionAuditCanonical.Normalize(
            nonFlowAudit with
            {
                Sources =
                [nonFlowSource with
                {
                    MembershipSignature =
                        LifecycleFixture.H("wrong-membership")
                }]
            })),
    "NONFLOW_MEMBERSHIP_TAMPER_FAILS");

var fenceBaseline = await LifecycleFixture.RecaptureExcludedForFenceAsync(
    "AUX-EXCLUDED-DECISION-FENCE", "FIXTURE_DRAFT", omit: false);
var fenceMutation = await LifecycleFixture.RecaptureExcludedForFenceAsync(
    "AUX-EXCLUDED-DECISION-FENCE", "FIXTURE_DRAFT_MUTATED", omit: false);
var fenceDelete = await LifecycleFixture.RecaptureExcludedForFenceAsync(
    "AUX-EXCLUDED-DECISION-FENCE", "FIXTURE_DRAFT", omit: true);
var fenceBaseProof = StatisticReconciliationTrustedSourceDecisionFence.Current(
    fenceBaseline);
var fenceMutationProof =
    StatisticReconciliationTrustedSourceDecisionFence.Current(fenceMutation);
var fenceDeleteProof =
    StatisticReconciliationTrustedSourceDecisionFence.Current(fenceDelete);
Require(fenceBaseline.SourceSetSha256 == fenceMutation.SourceSetSha256 &&
    fenceBaseline.MembershipSemanticSha256 ==
        fenceMutation.MembershipSemanticSha256 &&
    fenceBaseProof.Count == fenceMutationProof.Count &&
    fenceBaseProof.ManifestSha256 != fenceMutationProof.ManifestSha256,
    "EXCLUDED_ONE_BIT_FULL_DECISION_DRIFT");
Require(fenceBaseline.SourceSetSha256 == fenceDelete.SourceSetSha256 &&
    fenceBaseline.MembershipSemanticSha256 ==
        fenceDelete.MembershipSemanticSha256 &&
    fenceBaseProof.Count == 1 && fenceDeleteProof.Count == 0 &&
    fenceBaseProof.ManifestSha256 != fenceDeleteProof.ManifestSha256,
    "EXCLUDED_DELETE_FULL_DECISION_DRIFT");

var assignmentMutation = await LifecycleFixture.CurrentAsync(
    "AUX-ASSIGNMENT-MUTATION", "APPROVED", "V_EXCLUDE", targetCount: 0,
    actualAssignmentId: LifecycleFixture.O("different-assignment"));
var assignmentMutationEvidence = LifecycleFixture.Build(assignmentMutation.Input);
Require(assignmentMutationEvidence.Result.MissingCount == 1 &&
    assignmentMutationEvidence.Result.ExtraCount >= 1,
    $"NEUTRAL_TUPLE_ONE_BIT_MISSING_EXTRA:" +
    $"{assignmentMutationEvidence.Result.MissingCount}:" +
    $"{assignmentMutationEvidence.Result.ExtraCount}:" +
    assignmentMutationEvidence.Result.RootCause);

var reversal = await LifecycleFixture.ReversalAsync(
    "AUX-REVERSAL-TAMPER", "RECALLED", "REVIEW_RECALL_APPROVED");
var wrongPriorGeneration = reversal.Input with
{
    P9ContributionAudit = LifecycleFixture.MutateReversal(
        reversal.Input.P9ContributionAudit,
        value => value with
        {
            PriorGenerationHash = LifecycleFixture.H("wrong-prior-generation")
        })
};
var wrongPriorEvidence = LifecycleFixture.Build(wrongPriorGeneration);
Require(wrongPriorEvidence.Result.Outcome ==
    StatisticReconciliationLifecycleOutcomes.Stale,
    "WRONG_PRIOR_GENERATION_FAIL_CLOSED");

var repeated = await LifecycleFixture.RepeatedRecheckAsync();
AssertMatched(repeated.Second);
AssertMatched(repeated.Third);
Require(repeated.Second.Result.Steps.Last().ActualDelta == "0" &&
    repeated.Third.Result.Steps.Last().ActualDelta == "0",
    "REPEATED_RECHECK_DELTA_ZERO");
Require(repeated.Second.Manifest.PriorCompactionSchema ==
        StatisticReconciliationActualLifecycleLedgerSchema
            .CompactedPriorBaseline &&
    repeated.Second.Manifest.PriorCompactionIdentityCount == 1 &&
    repeated.Third.Manifest.PriorCompactionIdentityCount == 1 &&
    repeated.Second.Manifest.ObservationCount == 6 &&
    repeated.Third.Manifest.ObservationCount == 6,
    "REPEATED_RECHECK_COMPACTED_CONSTANT_CARDINALITY");

var priorZero = await LifecycleFixture.PriorZeroCompactionAsync();
AssertMatched(priorZero.PriorZeroUnchanged);
Require(priorZero.PriorZeroUnchanged.Manifest.PriorCompactionIdentityCount == 2,
    "PRIOR_ZERO_COMPACTED_PAIR_COUNT");
Require(priorZero.SourceReappeared.Result.ExtraCount == 1 &&
    priorZero.SourceReappeared.Result.Steps.Count(step =>
        step.RootCause ==
            StatisticReconciliationLifecycleRootCauses.ExtraIdentity) == 1,
    "P9_SOURCE_REAPPEARS_EXACTLY_ONE_EXTRA");
Require(priorZero.AuditReappeared.Result.ExtraCount == 1 &&
    priorZero.AuditReappeared.Result.Steps.Count(step =>
        step.RootCause ==
            StatisticReconciliationLifecycleRootCauses.ExtraIdentity) == 1,
    "P9_AUDIT_REAPPEARS_EXACTLY_ONE_EXTRA");

Require(StatisticReconciliationActualLifecycleEvidenceIntegrity
        .RequireObservationBudget(49_999, 49_999) == 100_000,
    "INITIAL_49999_OBSERVATION_BUDGET");
Require(StatisticReconciliationActualLifecycleEvidenceIntegrity
        .RequireObservationBudget(99_998, 99_998) == 199_998,
    "UNCHANGED_RECHECK_49999_OBSERVATION_BUDGET");
ExpectThrows(
    () => StatisticReconciliationActualLifecycleEvidenceIntegrity
        .RequireObservationBudget(100_000, 100_000),
    "UNCHANGED_RECHECK_50000_REJECTED_BEFORE_APPEND");
ExpectThrows(
    () => StatisticReconciliationActualLifecycleEvidenceIntegrity
        .RequireObservationBudget(99_998, 100_001),
    "ORPHAN_PUSHES_OBSERVATION_BUDGET_OVER_LIMIT");

var rows = new[]
{
    new BsonDocument
    {
        ["schemaVersion"] = "P10_EXPECTED_OBSERVATION_V1",
        ["recordKind"] = "EXPECTED_ATOM"
    },
    new BsonDocument
    {
        ["schemaVersion"] = "P10_ACTUAL_OBSERVATION_V5",
        ["recordKind"] = "GENERATION_COMMIT"
    },
    new BsonDocument
    {
        ["schemaVersion"] =
            StatisticReconciliationActualLifecycleLedgerSchema.Version,
        ["recordKind"] =
            StatisticReconciliationActualLifecycleLedgerSchema.SourceKind
    }
};
Require(rows.Count(
        StatisticReconciliationActualLifecycleMongoLedger.IsOwnedEnvelope) == 1,
    "SHARED_LEDGER_EXACT_SCHEMA_KIND_FILTER");

var tamperedRows = includeEvidence.Rows.ToArray();
tamperedRows[0] = tamperedRows[0] with
{
    PayloadCanonicalJson = tamperedRows[0].PayloadCanonicalJson + " "
};
ExpectThrows(
    () => StatisticReconciliationActualLifecycleEvidenceIntegrity.Rehydrate(
        tamperedRows.ToImmutableArray()),
    "SIDE_LEDGER_PAYLOAD_TAMPER_FAILS");

Require(includeEvidence.Manifest.ObservationCount > 0,
    "MANIFEST_COUNT_POSITIVE");
Require(includeEvidence.Manifest.ResultMismatchSetSha256 ==
    StatisticReconciliationActualLifecycleEvidenceIntegrity
        .ComputeResultMismatchSetSha(includeEvidence.Result),
    "MISMATCH_SET_BOUND");

Console.WriteLine(
    "P10_LIFECYCLE_PRODUCTION_BRIDGE_OK cases=24 auxiliary=39 " +
    "nonVacuousReversal=true historicalEpoch=true compactionV3=true " +
    "nonFlowInclude=true fullDecisionFence=true " +
    "missingExtraAuxiliary=true p9Writes=0");
return 0;

static (string Id, Func<Task<LifecycleFixtureResult>> Create,
    Action<StatisticReconciliationActualLifecycleEvidence> Assert) Current(
        string id,
        string status,
        string policy,
        int targetCount,
        Action<StatisticReconciliationActualLifecycleEvidence> assert) =>
    (id, () => LifecycleFixture.CurrentAsync(
        id, status, policy, targetCount), assert);

static (string Id, Func<Task<LifecycleFixtureResult>> Create,
    Action<StatisticReconciliationActualLifecycleEvidence> Assert) Reversal(
        string id,
        string status,
        string operation) =>
    (id, () => LifecycleFixture.ReversalAsync(id, status, operation),
        AssertMatchedReversal);

static void AssertEnvelope(
    string id,
    StatisticReconciliationActualLifecycleEvidence evidence)
{
    Require(evidence.Manifest.ObservationCount == evidence.Rows.Length,
        $"{id}:COUNT");
    Require(evidence.Manifest.MembershipUnit ==
        StatisticReconciliationActualLifecycleLedgerSchema.MembershipUnit,
        $"{id}:UNIT");
    Require(evidence.Manifest.BaseCoherentGenerationId !=
        evidence.Manifest.ManifestSha256,
        $"{id}:NO_SELF_CHOSEN_HASH");
    var hasPrior = evidence.Manifest.PriorLifecycleManifestSha256 is not null;
    Require(hasPrior
            ? evidence.Manifest.PriorCompactionSchema ==
                  StatisticReconciliationActualLifecycleLedgerSchema
                      .CompactedPriorBaseline &&
              evidence.Manifest.PriorCompactionProofSha256 is not null &&
              evidence.Manifest.PriorCompactionIdentityCount > 0
            : evidence.Manifest.PriorCompactionSchema is null &&
              evidence.Manifest.PriorCompactionProofSha256 is null &&
              evidence.Manifest.PriorCompactionIdentityCount == 0,
        $"{id}:COMPACTION_ALL_OR_NONE");
}

static void AssertProductionCandidateShape(
    string id,
    LifecycleFixtureResult fixture)
{
    if (fixture.ActualOwner is null)
        return;

    var decision = fixture.Input.Source.Decisions.Single();
    Require(decision.ReportId == fixture.ExpectedRow.ReportId,
        $"{id}:P9_CANDIDATE_REPORT");
    var expectedIncluded = fixture.ExpectedRow.LifecycleStatus ==
            StatisticReconciliationExpectedLifecycleStatuses.Approved &&
        fixture.ExpectedRow.ContributionPolicy ==
            StatisticReconciliationExpectedContributionPolicies.Include;
    Require(decision.Included == expectedIncluded &&
        decision.ObservedOwner.OwnerIncluded == expectedIncluded,
        $"{id}:P9_CANDIDATE_INCLUDED");
    Require(decision.ObservedOwner.ContributionDecision ==
            (expectedIncluded ? "INCLUDE" : "EXCLUDE"),
        $"{id}:P9_CANDIDATE_DECISION");
    Require(fixture.Input.Source.IncludedSources.Length ==
            (expectedIncluded ? 1 : 0),
        $"{id}:P9_INCLUDED_SET");
}

static void AssertMatchedZero(
    StatisticReconciliationActualLifecycleEvidence evidence)
{
    AssertMatched(evidence);
    var step = evidence.Result.Steps.Single();
    Require(step.ExpectedAfter == "0" && step.ActualAfter == "0",
        "MATCHED_ZERO_UNIT");
}

static void AssertMatchedOne(
    StatisticReconciliationActualLifecycleEvidence evidence)
{
    AssertMatched(evidence);
    var step = evidence.Result.Steps.Single();
    Require(step.ExpectedAfter == "1" && step.ActualAfter == "1",
        "MATCHED_ONE_UNIT");
    Require(evidence.Manifest.P9ContributionAudit.Targets.Length > 0,
        "MATCHED_INCLUDE_TARGET_PROVENANCE");
}

static void AssertMatchedReversal(
    StatisticReconciliationActualLifecycleEvidence evidence)
{
    AssertMatched(evidence);
    var step = evidence.Result.Steps.Single(step => step.Sequence == 2);
    Require(step.ExpectedBefore == "1" && step.ExpectedAfter == "0" &&
        step.ExpectedDelta == "-1" && step.ActualAfter == "0" &&
        step.ActualDelta == "-1",
        "REVERSAL_EXACT_INVERSE");
    Require(evidence.Manifest.PriorFinalVerdictGenerationId is not null &&
        evidence.Manifest.PriorCommittedGenerationId is not null &&
        evidence.Manifest.PriorLifecycleManifestSha256 is not null &&
        evidence.Manifest.PriorP9AuditSnapshotSha256 is not null &&
        evidence.Manifest.P9ReversalAuditSha256 is not null,
        "REVERSAL_PRIOR_PINS");
}

static void AssertMatched(
    StatisticReconciliationActualLifecycleEvidence evidence)
{
    Require(evidence.Result.EvidenceComplete, "MATCHED_COMPLETE");
    Require(evidence.Result.Outcome ==
        StatisticReconciliationLifecycleOutcomes.Matched,
        $"MATCHED_OUTCOME:{evidence.Result.Outcome}:" +
        evidence.Result.RootCause);
}

static void Require(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

static void ExpectThrows(Action action, string reason)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }
    throw new InvalidOperationException(reason);
}