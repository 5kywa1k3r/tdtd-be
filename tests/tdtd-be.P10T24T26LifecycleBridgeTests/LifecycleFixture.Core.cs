using System.Collections.Immutable;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsRun;

internal sealed record LifecycleFixtureResult(
    StatisticReconciliationActualLifecycleBuildInput Input,
    StatisticReconciliationExpectedAuthoritativeLifecycleRow ExpectedRow,
    ActualSourceOwnerRevision? ActualOwner);

internal static partial class LifecycleFixture
{
    internal static async Task<LifecycleFixtureResult> CurrentAsync(
        string caseId,
        string status,
        string policy,
        int targetCount = 0,
        bool omitActual = false,
        string? actualAssignmentId = null,
        string? actualContributionDecision = null,
        string? actualMappingSemanticSha256 = null,
        string? actualFlowBranchId = null,
        int executionEpoch = 1,
        int currentExecutionEpoch = 1,
        int? currentScopeEpoch = null,
        string runtimeKind =
            StatisticReconciliationExpectedRuntimeKinds.Flow)
    {
        var pins = Pins.Create(caseId, "CURRENT");
        var expected = Expected(pins, status, policy, lifecycleRevision: 1,
            executionEpoch, currentExecutionEpoch, runtimeKind);
        var owners = omitActual
            ? ImmutableArray<ActualSourceOwnerRevision>.Empty
            : [ActualOwner(
                pins,
                expected,
                ordinal: 0,
                actualAssignmentId: actualAssignmentId,
                actualContributionDecision: actualContributionDecision,
                actualMappingSemanticSha256: actualMappingSemanticSha256,
                actualFlowBranchId: actualFlowBranchId)];
        var source = await CaptureSourceAsync(
            pins, owners, currentScopeEpoch ?? currentExecutionEpoch);
        var direct = await CaptureDirectAsync(pins, source);
        var audit = policy ==
                        StatisticReconciliationExpectedContributionPolicies.Include &&
                    !omitActual
            ? IncludeAudit(pins, expected, targetCount)
            : EmptyAudit(pins, expected.ReportId, expected.LifecycleRevision);
        var input = Input(pins, expected, source, direct, audit, prior: null);
        return new LifecycleFixtureResult(
            input,
            expected,
            owners.FirstOrDefault());
    }

    internal static Task<LifecycleFixtureResult> NonFlowCurrentAsync(
        string caseId,
        int targetCount,
        string? actualContributionDecision = null) =>
        CurrentAsync(
            caseId,
            StatisticReconciliationExpectedLifecycleStatuses.Approved,
            StatisticReconciliationExpectedContributionPolicies.Include,
            targetCount,
            actualContributionDecision: actualContributionDecision,
            runtimeKind:
                StatisticReconciliationExpectedRuntimeKinds.NonFlow);

    internal static async Task<LifecycleFixtureResult> ReversalAsync(
        string caseId,
        string status,
        string operation)
    {
        var priorPins = Pins.Create(caseId, "PRIOR");
        var priorExpected = Expected(
            priorPins,
            StatisticReconciliationExpectedLifecycleStatuses.Approved,
            StatisticReconciliationExpectedContributionPolicies.Include,
            lifecycleRevision: 1);
        var priorOwner = ActualOwner(priorPins, priorExpected, ordinal: 0);
        var priorSource = await CaptureSourceAsync(priorPins, [priorOwner]);
        var priorDirect = await CaptureDirectAsync(priorPins, priorSource);
        var priorAudit = IncludeAudit(
            priorPins,
            priorExpected,
            targetCount: 2);
        var priorInput = Input(
            priorPins,
            priorExpected,
            priorSource,
            priorDirect,
            priorAudit,
            prior: null);
        var priorEvidence = Build(priorInput);
        RequireMatched(priorEvidence, $"{caseId}:PRIOR");
        var prior = Prior(caseId, priorEvidence, priorAudit);

        var currentPins = priorPins.Next("CURRENT");
        var currentExpected = Expected(
            currentPins,
            status,
            StatisticReconciliationExpectedContributionPolicies.Exclude,
            lifecycleRevision: 2);
        var currentOwner = ActualOwner(
            currentPins,
            currentExpected,
            ordinal: 0);
        var currentSource = await CaptureSourceAsync(
            currentPins,
            [currentOwner]);
        var currentDirect = await CaptureDirectAsync(currentPins, currentSource);
        var currentAudit = ReversalAudit(
            currentPins,
            currentExpected,
            operation,
            priorAudit);
        var currentInput = Input(
            currentPins,
            currentExpected,
            currentSource,
            currentDirect,
            currentAudit,
            prior);
        return new LifecycleFixtureResult(
            currentInput,
            currentExpected,
            currentOwner);
    }

    internal static async Task<LifecycleFixtureResult> OrphanActualAsync(
        string caseId,
        bool orphanInLifecycleMetricScope = true)
    {
        var pins = Pins.Create(caseId, "CURRENT");
        var expected = Expected(
            pins,
            StatisticReconciliationExpectedLifecycleStatuses.Draft,
            StatisticReconciliationExpectedContributionPolicies.Exclude,
            lifecycleRevision: 1);
        var expectedOwner = ActualOwner(pins, expected, ordinal: 0);
        var orphanPins = pins.ForOrphan();
        var orphanExpected = Expected(
            orphanPins,
            StatisticReconciliationExpectedLifecycleStatuses.Draft,
            StatisticReconciliationExpectedContributionPolicies.Exclude,
            lifecycleRevision: 1);
        var orphanOwner = ActualOwner(
            orphanPins,
            orphanExpected,
            ordinal: 1,
            inLifecycleMetricScope: orphanInLifecycleMetricScope);
        var source = await CaptureSourceAsync(
            pins,
            [expectedOwner, orphanOwner]);
        var direct = await CaptureDirectAsync(pins, source);
        var audit = EmptyAudit(
            pins,
            expected.ReportId,
            expected.LifecycleRevision);
        return new LifecycleFixtureResult(
            Input(pins, expected, source, direct, audit, prior: null),
            expected,
            expectedOwner);
    }

    internal static async Task<(
        StatisticReconciliationActualLifecycleEvidence Second,
        StatisticReconciliationActualLifecycleEvidence Third)>
        RepeatedRecheckAsync()
    {
        const string caseId = "AUX-COMPACTION-RECHECK";
        var firstPins = Pins.Create(caseId, "PRIOR");
        var firstExpected = Expected(firstPins, "APPROVED", "V_INCLUDE", 1);
        var firstOwner = ActualOwner(firstPins, firstExpected, 0);
        var firstSource = await CaptureSourceAsync(firstPins, [firstOwner]);
        var firstDirect = await CaptureDirectAsync(firstPins, firstSource);
        var firstAudit = IncludeAudit(firstPins, firstExpected, 1);
        var firstEvidence = Build(Input(firstPins, firstExpected, firstSource,
            firstDirect, firstAudit, null));
        RequireMatched(firstEvidence, $"{caseId}:FIRST");
        var firstPrior = Prior($"{caseId}:FIRST", firstEvidence, firstAudit);

        var secondPins = firstPins.Next("RECHECK1");
        var secondExpected = Expected(secondPins, "APPROVED", "V_INCLUDE", 1);
        var secondOwner = ActualOwner(secondPins, secondExpected, 0);
        var secondSource = await CaptureSourceAsync(secondPins, [secondOwner]);
        var secondDirect = await CaptureDirectAsync(secondPins, secondSource);
        var secondAudit = IncludeAudit(secondPins, secondExpected, 1);
        var secondEvidence = Build(Input(secondPins, secondExpected, secondSource,
            secondDirect, secondAudit, firstPrior));
        RequireMatched(secondEvidence, $"{caseId}:SECOND");
        var secondPrior = Prior($"{caseId}:SECOND", secondEvidence, secondAudit);

        var thirdPins = firstPins.Next("RECHECK2") with
        {
            DirectSourceRevision = 3
        };
        var thirdExpected = Expected(thirdPins, "APPROVED", "V_INCLUDE", 1);
        var thirdOwner = ActualOwner(thirdPins, thirdExpected, 0);
        var thirdSource = await CaptureSourceAsync(thirdPins, [thirdOwner]);
        var thirdDirect = await CaptureDirectAsync(thirdPins, thirdSource);
        var thirdAudit = IncludeAudit(thirdPins, thirdExpected, 1);
        var thirdEvidence = Build(Input(thirdPins, thirdExpected, thirdSource,
            thirdDirect, thirdAudit, secondPrior));
        RequireMatched(thirdEvidence, $"{caseId}:THIRD");
        return (secondEvidence, thirdEvidence);
    }

    internal static async Task<(
        StatisticReconciliationActualLifecycleEvidence PriorZeroUnchanged,
        StatisticReconciliationActualLifecycleEvidence SourceReappeared,
        StatisticReconciliationActualLifecycleEvidence AuditReappeared)>
        PriorZeroCompactionAsync()
    {
        const string caseId = "AUX-COMPACTION-PRIOR-ZERO";
        var firstPins = Pins.Create(caseId, "PRIOR");
        var absentPins = firstPins.ForOrphan();
        var keptExpected = Expected(firstPins, "DRAFT", "V_EXCLUDE", 1);
        var absentExpected = Expected(absentPins, "DRAFT", "V_EXCLUDE", 1);
        var keptOwner = ActualOwner(firstPins, keptExpected, 0);
        var absentOwner = ActualOwner(absentPins, absentExpected, 1);
        var firstSource = await CaptureSourceAsync(
            firstPins, [keptOwner, absentOwner]);
        var firstDirect = await CaptureDirectAsync(firstPins, firstSource);
        var firstAudit = EmptyAudit(firstPins, keptExpected.ReportId, 1);
        var firstEvidence = Build(Input(firstPins,
            [keptExpected, absentExpected], firstSource, firstDirect,
            firstAudit, null));
        RequireMatched(firstEvidence, $"{caseId}:FIRST");
        var firstPrior = Prior($"{caseId}:FIRST", firstEvidence, firstAudit);

        var secondPins = firstPins.Next("RECHECK1");
        var secondExpected = Expected(secondPins, "DRAFT", "V_EXCLUDE", 1);
        var secondOwner = ActualOwner(secondPins, secondExpected, 0);
        var secondSource = await CaptureSourceAsync(secondPins, [secondOwner]);
        var secondDirect = await CaptureDirectAsync(secondPins, secondSource);
        var secondAudit = EmptyAudit(secondPins, secondExpected.ReportId, 1);
        var secondEvidence = Build(Input(secondPins, secondExpected, secondSource,
            secondDirect, secondAudit, firstPrior));
        RequireMatched(secondEvidence, $"{caseId}:SECOND");
        var secondPrior = Prior($"{caseId}:SECOND", secondEvidence, secondAudit);

        var thirdPins = firstPins.Next("RECHECK2") with
        {
            DirectSourceRevision = 3
        };
        var thirdExpected = Expected(thirdPins, "DRAFT", "V_EXCLUDE", 1);
        var thirdOwner = ActualOwner(thirdPins, thirdExpected, 0);
        var thirdSource = await CaptureSourceAsync(thirdPins, [thirdOwner]);
        var thirdDirect = await CaptureDirectAsync(thirdPins, thirdSource);
        var thirdAudit = EmptyAudit(thirdPins, thirdExpected.ReportId, 1);
        var unchanged = Build(Input(thirdPins, thirdExpected, thirdSource,
            thirdDirect, thirdAudit, secondPrior));

        var reappearedPins = thirdPins.ForOrphan();
        var reappearedExpected = Expected(
            reappearedPins, "DRAFT", "V_EXCLUDE", 1);
        var reappearedOwner = ActualOwner(
            reappearedPins, reappearedExpected, 1);
        var sourceWithReappeared = await CaptureSourceAsync(
            thirdPins, [thirdOwner, reappearedOwner]);
        var directWithReappeared = await CaptureDirectAsync(
            thirdPins, sourceWithReappeared);
        var sourceReappeared = Build(Input(
            thirdPins,
            thirdExpected,
            sourceWithReappeared,
            directWithReappeared,
            thirdAudit,
            secondPrior));

        var auditExpected = Expected(
            reappearedPins, "APPROVED", "V_INCLUDE", 1);
        var auditOnly = IncludeAudit(thirdPins, auditExpected, 1);
        var auditReappeared = Build(Input(thirdPins, thirdExpected, thirdSource,
            thirdDirect, auditOnly, secondPrior));
        return (unchanged, sourceReappeared, auditReappeared);
    }

    internal static StatRunDirectLifecycleMetricScope MetricScopeFor(
        string caseId,
        int currentScopeEpoch = 1)
        => MetricScope(Pins.Create(caseId, "CURRENT"), currentScopeEpoch);
    internal static StatisticReconciliationActualLifecycleEvidence Build(
        StatisticReconciliationActualLifecycleBuildInput input) =>
        new StatisticReconciliationActualLifecycleBridge(
                new StatisticReconciliationLifecycleEvaluator())
            .Build(input);

    internal static StatRunLifecycleContributionAuditSnapshot ReorderTargets(
        StatRunLifecycleContributionAuditSnapshot value) =>
        StatRunLifecycleContributionAuditCanonical.Normalize(value with
        {
            Targets = value.Targets.Reverse().ToImmutableArray()
        });

    internal static StatRunLifecycleContributionAuditSnapshot MutateReversal(
        StatRunLifecycleContributionAuditSnapshot value,
        Func<StatRunLifecycleReversalAuditSnapshot,
            StatRunLifecycleReversalAuditSnapshot> mutate)
    {
        var reversal = mutate(value.Reversal ??
            throw new InvalidOperationException(
                "Fixture reversal is required.")) with
        {
            AuditHash = string.Empty
        };
        reversal = reversal with
        {
            AuditHash = ReversalAuditSha(value, reversal)
        };
        return StatRunLifecycleContributionAuditCanonical.Normalize(value with
        {
            Reversal = reversal
        });
    }

    internal static string H(string value) =>
        StatisticReconciliationActualCanonical.Hash(
            "P10_T24_T26_FIXTURE_V2",
            value);

    internal static string O(string value) => H(value)[..24];

    private static void RequireMatched(
        StatisticReconciliationActualLifecycleEvidence evidence,
        string reason)
    {
        if (!evidence.Result.EvidenceComplete ||
            evidence.Result.Outcome !=
                StatisticReconciliationLifecycleOutcomes.Matched)
        {
            throw new InvalidOperationException(
                $"{reason}:{evidence.Result.Outcome}:" +
                evidence.Result.RootCause);
        }
    }

    private sealed record Pins(
        string CaseId,
        string GenerationTag,
        string ReconciliationId,
        string WorkId,
        string WorkAssignmentId,
        string ReportId,
        string PayloadDocumentId,
        string PeriodInstanceKey,
        string FormFamilyId,
        string FormTemplateId,
        string P9RunId,
        string P9GenerationId,
        string P9GenerationSha256,
        string MembershipSignatureSha256,
        long DirectSourceRevision,
        string ExpectedGenerationId,
        string ExpectedGenerationSha256,
        string ExpectedManifestSha256,
        string ExpectedMetricPlanSha256,
        string ExpectedMembershipSha256,
        string BaseCoherentGenerationId,
        string BaseCoherentGenerationSha256,
        string ComparisonBindingSha256,
        string FlowTemplateVersionId,
        string FlowPayloadSha256,
        string FlowInstanceId,
        string FlowBranchId,
        string FlowStepId,
        string FlowStepInstanceId,
        string ExecutionEpochId,
        string ConfigVersionId,
        string ConfigSha256)
    {
        internal static Pins Create(string caseId, string generationTag) =>
            new(
                caseId,
                generationTag,
                O($"{caseId}:reconciliation"),
                O($"{caseId}:work"),
                O($"{caseId}:assignment"),
                O($"{caseId}:report"),
                O($"{caseId}:payload"),
                $"period:{caseId}",
                O($"{caseId}:form-family"),
                O($"{caseId}:form-template"),
                O($"{caseId}:{generationTag}:p9-run"),
                H($"{caseId}:{generationTag}:p9-generation"),
                H($"{caseId}:{generationTag}:p9-generation-sha"),
                H($"{caseId}:{generationTag}:membership"),
                generationTag == "PRIOR" ? 1 : 2,
                H($"{caseId}:{generationTag}:expected-generation"),
                H($"{caseId}:{generationTag}:expected-generation-sha"),
                H($"{caseId}:{generationTag}:expected-manifest"),
                H($"{caseId}:{generationTag}:expected-plan"),
                H($"{caseId}:{generationTag}:expected-membership"),
                H($"{caseId}:{generationTag}:base-generation"),
                H($"{caseId}:{generationTag}:base-generation-sha"),
                H($"{caseId}:{generationTag}:comparison"),
                O($"{caseId}:flow-template-version"),
                H($"{caseId}:flow-payload"),
                O($"{caseId}:flow-instance"),
                O($"{caseId}:flow-branch"),
                O($"{caseId}:flow-step"),
                O($"{caseId}:flow-step-instance"),
                O($"{caseId}:execution-epoch"),
                O($"{caseId}:config-version"),
                H($"{caseId}:config-sha"));

        internal Pins Next(string generationTag) =>
            Create(CaseId, generationTag);

        internal Pins ForOrphan() => this with
        {
            WorkAssignmentId = O($"{CaseId}:orphan-assignment"),
            ReportId = O($"{CaseId}:orphan-report"),
            PayloadDocumentId = O($"{CaseId}:orphan-payload"),
            FlowStepInstanceId = O($"{CaseId}:orphan-step-instance")
        };
    }
}
