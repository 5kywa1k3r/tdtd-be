using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

var baseline = BuildBinding();
var run = BuildRun(baseline);
var cases = 0;

Accept("P10-T26-BIND-01", baseline);
Reject("P10-T26-BIND-02", baseline with { ReconciliationId = "other-run" });
Reject("P10-T26-BIND-03", baseline with { ImmutableIdentitySha256 = Sha("other-identity") });
Reject("P10-T26-BIND-04", baseline with { ImmutableHeaderSha256 = Sha("other-header") });
Reject("P10-T26-BIND-05", baseline with { WorkId = "other-work" });
Reject("P10-T26-BIND-06", baseline with { ScopeAssignmentId = "other-scope" });
Reject("P10-T26-BIND-07", baseline with { PeriodKey = "other-period" });
Reject("P10-T26-BIND-08", baseline with { PeriodInstanceKey = "other-period-instance" });
Reject("P10-T26-BIND-09", baseline with { ConceptKey = "other-concept" });
Reject("P10-T26-BIND-10", baseline with { Grain = "QUARTER" });
Reject("P10-T26-BIND-11", baseline with { TimeAxis = "PERIOD_START" });
Reject("P10-T26-BIND-12", baseline with { FilterSha256 = Sha("other-filter") });
Reject("P10-T26-BIND-13", baseline with { DynamicFormVersionId = "other-form" });
Reject("P10-T26-BIND-14", baseline with { DynamicFormSchemaSha256 = Sha("other-form-schema") });
Reject("P10-T26-BIND-15", baseline with { FlowTemplateVersionId = "other-flow-template" });
Reject("P10-T26-BIND-16", baseline with { FlowPayloadSha256 = Sha("other-flow-payload") });
Reject("P10-T26-BIND-17", baseline with { FlowInstanceId = "other-flow-instance" });
Reject("P10-T26-BIND-18", baseline with { ExecutionEpochId = "other-epoch" });
Reject("P10-T26-BIND-19", baseline with { ExecutionEpoch = baseline.ExecutionEpoch + 1 });
Reject(
    "P10-T26-BIND-20",
    baseline with { ExecutionEpochRevision = baseline.ExecutionEpochRevision + 1 });
Reject("P10-T26-BIND-21", baseline with { P8ConfigurationOwnerId = "other-p8-owner" });
Reject(
    "P10-T26-BIND-22",
    baseline with { P8ConfigurationBundleSha256 = Sha("other-p8-bundle") });
Reject(
    "P10-T26-BIND-23A",
    baseline with
    {
        LifecycleMetricScopeSha256 = null
    });
Reject(
    "P10-T26-BIND-23",
    baseline with
    {
        ActualConfigurationBundleSha256 =
            Sha("other-actual-configuration-bundle")
    });

RejectCatalog(
    "P10-T26-BIND-24",
    pins => pins with { P9CatalogVersion = "other-p9-version" });
RejectCatalog(
    "P10-T26-BIND-25",
    pins => pins with { P9CatalogRawSha256 = Sha("other-p9-catalog-raw") });
RejectCatalog(
    "P10-T26-BIND-26",
    pins => pins with { P9CatalogSemanticSha256 = Sha("other-p9-catalog-semantic") });
RejectCatalog(
    "P10-T26-BIND-27",
    pins => pins with { P9SchemaRawSha256 = Sha("other-p9-schema-raw") });
RejectCatalog(
    "P10-T26-BIND-28",
    pins => pins with { P9SchemaSemanticSha256 = Sha("other-p9-schema-semantic") });
RejectCatalog(
    "P10-T26-BIND-29",
    pins => pins with { P9StageLockSha256 = Sha("other-p9-stage-lock") });
RejectCatalog(
    "P10-T26-BIND-30",
    pins => pins with { CandidateChainId = "other-candidate-chain" });
RejectCatalog(
    "P10-T26-BIND-31",
    pins => pins with { CandidatePromptId = "other-candidate-prompt" });
RejectCatalog(
    "P10-T26-BIND-32",
    pins => pins with { CandidateCatalogVersion = "other-candidate-version" });
RejectCatalog(
    "P10-T26-BIND-33",
    pins => pins with
    {
        CandidateCatalogRawSha256 = Sha("other-candidate-catalog-raw")
    });
RejectCatalog(
    "P10-T26-BIND-34",
    pins => pins with
    {
        CandidateCatalogSemanticSha256 = Sha("other-candidate-catalog-semantic")
    });
RejectCatalog(
    "P10-T26-BIND-35",
    pins => pins with
    {
        CandidateSchemaRawSha256 = Sha("other-candidate-schema-raw")
    });
RejectCatalog(
    "P10-T26-BIND-36",
    pins => pins with
    {
        CandidateSchemaSemanticSha256 = Sha("other-candidate-schema-semantic")
    });
RejectCatalog(
    "P10-T26-BIND-37",
    pins => pins with
    {
        CandidateStageLockSha256 = Sha("other-candidate-stage-lock")
    });
RejectCatalog(
    "P10-T26-BIND-38",
    pins => pins with { CatalogPinSetSha256 = Sha("other-pin-set") });

var repoRoot = FindRepoRoot();
var publicationSource = File.ReadAllText(Path.Combine(
    repoRoot,
    "tdtd-be",
    "Services",
    "StatisticsReconciliation",
    "ActualObservation",
    "StatisticReconciliationActualPublication.cs"));
Require(
    "P10-T26-BIND-39",
    CountOccurrences(
        publicationSource,
        "RunBindingFromStoredDocument(") == 3 &&
    publicationSource.Contains(
        "StatisticReconciliationActualPublicationContext CommittedRunBinding",
        StringComparison.Ordinal) &&
    publicationSource.Contains(
        "document.ActualConfigurationBundleSha256",
        StringComparison.Ordinal));

var workerSource = File.ReadAllText(Path.Combine(
    repoRoot,
    "tdtd-be",
    "Services",
    "StatisticsReconciliation",
    "StatisticReconciliationRunService.Worker.cs"));
var guardIndex = workerSource.IndexOf(
    "StatisticReconciliationActualRunBindingGuard.Matches",
    StringComparison.Ordinal);
var replayIndex = workerSource.IndexOf(
    "var exactPublishedReplay",
    guardIndex,
    StringComparison.Ordinal);
var casIndex = workerSource.IndexOf(
    "FindOneAndUpdateAsync",
    guardIndex,
    StringComparison.Ordinal);
var guardSource = File.ReadAllText(Path.Combine(
    repoRoot,
    "tdtd-be",
    "Services",
    "StatisticsReconciliation",
    "ActualObservation",
    "StatisticReconciliationActualRunBindingGuard.cs"));
var legacyRun = BuildRun(baseline);
legacyRun.ActualCapturePlan = null;
legacyRun.ActualConfigurationBundleSha256 = null;
var legacyBinding = baseline with
{
    ActualConfigurationBundleSha256 =
        baseline.P8ConfigurationBundleSha256
};
Require(
    "P10-T26-BIND-40",
    guardIndex >= 0 &&
    replayIndex > guardIndex &&
    casIndex > replayIndex &&
    workerSource.Contains(
        "ACTUAL_GENERATION_RUN_BINDING_MISMATCH",
        StringComparison.Ordinal) &&
    guardSource.Contains("run.ActualCapturePlan is null",
        StringComparison.Ordinal) &&
    guardSource.Contains("run.ActualConfigurationBundleSha256",
        StringComparison.Ordinal) &&
    StatisticReconciliationActualRunBindingGuard.Matches(
        legacyRun,
        legacyBinding) &&
    !StatisticReconciliationActualRunBindingGuard.Matches(
        legacyRun,
        baseline));

var currentFenceSource = File.ReadAllText(Path.Combine(
    repoRoot,
    "tdtd-be",
    "Services",
    "StatisticsReconciliation",
    "ActualObservation",
    "StatisticReconciliationTrustedCurrentFence.cs"));
Require(
    "P10-T26-BIND-41",
    currentFenceSource.Contains(
        "outer.SemanticSha256 != inner.SemanticSha256",
        StringComparison.Ordinal) &&
    currentFenceSource.Contains(
        "plan.SemanticSha256 != committedPlan.SemanticSha256",
        StringComparison.Ordinal) &&
    !currentFenceSource.Contains("outer != inner", StringComparison.Ordinal) &&
    !currentFenceSource.Contains(
        "plan != committedPlan", StringComparison.Ordinal));
var expectedCatalog = ObservationCatalogPins(baseline.CatalogPins);
var expectedCatalogComparison =
    StatisticReconciliationTrustedCatalogComparison.FromExpected(
        expectedCatalog);
var actualCatalogComparison =
    StatisticReconciliationTrustedCatalogComparison.FromActual(
        baseline.CatalogPins);
var currentCatalogComparison =
    StatisticReconciliationTrustedCatalogComparison.FromCurrent(
        baseline.CatalogPins,
        baseline.CoherentCatalogPinSetSha256,
        baseline.CoherentCatalogPinSetSha256);
Require(
    "P10-T26-BIND-42",
    expectedCatalog.CatalogPinSetSha256 !=
        baseline.CatalogPins.CatalogPinSetSha256 &&
    expectedCatalogComparison == actualCatalogComparison &&
    actualCatalogComparison == currentCatalogComparison);

var neutralBinding = new StatisticReconciliationComparisonBindingPins(
    Sha("neutral-source"),
    Sha("neutral-p8-configuration"),
    Sha("neutral-actual-configuration"),
    actualCatalogComparison,
    Sha("neutral-runtime"),
    Sha("neutral-membership"));
var neutralPins = new StatisticReconciliationActualFreshnessPins(
    neutralBinding,
    Sha("neutral-result"),
    Sha("neutral-generation"),
    Sha("neutral-export"));
var evaluator = new StatisticReconciliationFreshnessEvaluator();
var fresh = evaluator.Evaluate(new StatisticReconciliationFreshnessRequest(
    neutralBinding with { CatalogPinSetSha256 = expectedCatalogComparison },
    neutralPins,
    neutralPins with
    {
        Binding = neutralBinding with
        {
            CatalogPinSetSha256 = currentCatalogComparison
        }
    },
    ActualGenerationComplete: true,
    RequiredLayersComplete: true,
    CaptureCoherent: true));
Require(
    "P10-T26-BIND-43",
    fresh.State == StatisticReconciliationFreshnessStates.Fresh &&
    fresh.DriftDomain == StatisticReconciliationFreshnessDomains.None &&
    fresh.ReasonCode == StatisticReconciliationFreshnessReasons.None &&
    fresh.MatchAllowed &&
    fresh.Signable);

var coherentCatalogDrift =
    StatisticReconciliationTrustedCatalogComparison.FromCurrent(
        baseline.CatalogPins,
        baseline.CoherentCatalogPinSetSha256,
        Sha("other-coherent-catalog-pin-set"));
var staleCurrent = evaluator.Evaluate(
    new StatisticReconciliationFreshnessRequest(
        neutralBinding with
        {
            CatalogPinSetSha256 = expectedCatalogComparison
        },
        neutralPins,
        neutralPins with
        {
            Binding = neutralBinding with
            {
                CatalogPinSetSha256 = coherentCatalogDrift
            }
        },
        ActualGenerationComplete: true,
        RequiredLayersComplete: true,
        CaptureCoherent: true));
Require(
    "P10-T26-BIND-44",
    staleCurrent.State == StatisticReconciliationFreshnessStates.Stale &&
    staleCurrent.DriftDomain ==
        StatisticReconciliationFreshnessDomains.Catalog &&
    staleCurrent.ReasonCode ==
        StatisticReconciliationFreshnessReasons.CatalogDrift &&
    !staleCurrent.MatchAllowed &&
    !staleCurrent.Signable);

var changedExpectedCatalog = ObservationCatalogPins(baseline.CatalogPins);
changedExpectedCatalog.CandidateCatalogVersion = "other-candidate-v1";
var staleExpected = evaluator.Evaluate(
    new StatisticReconciliationFreshnessRequest(
        neutralBinding with
        {
            CatalogPinSetSha256 =
                StatisticReconciliationTrustedCatalogComparison.FromExpected(
                    changedExpectedCatalog)
        },
        neutralPins,
        neutralPins,
        ActualGenerationComplete: true,
        RequiredLayersComplete: true,
        CaptureCoherent: true));
Require(
    "P10-T26-BIND-45",
    staleExpected.State == StatisticReconciliationFreshnessStates.Stale &&
    staleExpected.DriftDomain ==
        StatisticReconciliationFreshnessDomains.Catalog &&
    staleExpected.ReasonCode ==
        StatisticReconciliationFreshnessReasons.CatalogDrift);
var expectedCurrentBinding = new
    StatisticReconciliationExpectedGenerationBinding(
        "run-id",
        Sha("expected-generation"),
        Sha("expected-generation-semantic"),
        Sha("expected-metric-plan"),
        1,
        Sha("expected-manifest"),
        2,
        Sha("expected-membership"),
        "DIRECT");
var currentOwnerRead = new StatisticReconciliationTrustedCurrentOwnerRead(
    neutralPins,
    Sha("trusted-current-fence"),
    true,
    true,
    "EXPECTED_AUTHORITATIVE_CURRENT_PROVEN",
    Sha("relational-proof-binding"));
var expectedCurrentProof = Sha("expected-current-proof");
var sharedFence = StatisticReconciliationTrustedVerdictPipeline
    .ExpectedCurrentFenceSha256(
        currentOwnerRead,
        expectedCurrentProof,
        "EXPECTED_AUTHORITATIVE_CURRENT_PROVEN",
        expectedCurrentBinding);
var legacyFence = StatisticReconciliationFinalVerdictEvaluator.HashFields(
    "P10_TRUSTED_CURRENT_WITH_EXPECTED_V1",
    currentOwnerRead.FenceSha256,
    expectedCurrentProof,
    "EXPECTED_AUTHORITATIVE_CURRENT_PROVEN",
    expectedCurrentBinding.GenerationId,
    expectedCurrentBinding.GenerationSemanticSha256,
    expectedCurrentBinding.MetricPlanSha256,
    expectedCurrentBinding.MembershipSemanticSha256);
Require(
    "P10-T26-BIND-46",
    sharedFence == StatisticReconciliationFinalVerdictEvaluator.HashFields(
        "P10_TRUSTED_CURRENT_WITH_EXPECTED_V2",
        currentOwnerRead.FenceSha256,
        currentOwnerRead.RelationalProofBindingSha256,
        expectedCurrentProof,
        "EXPECTED_AUTHORITATIVE_CURRENT_PROVEN",
        expectedCurrentBinding.GenerationId,
        expectedCurrentBinding.GenerationSemanticSha256,
        expectedCurrentBinding.MetricPlanSha256,
        expectedCurrentBinding.MembershipSemanticSha256) &&
    sharedFence != legacyFence &&
    sharedFence != StatisticReconciliationTrustedVerdictPipeline
        .ExpectedCurrentFenceSha256(
            currentOwnerRead with
            {
                RelationalProofBindingSha256 =
                    Sha("other-relational-proof-binding")
            },
            expectedCurrentProof,
            "EXPECTED_AUTHORITATIVE_CURRENT_PROVEN",
            expectedCurrentBinding));
var immutableExpectedProof =
    StatisticReconciliationExpectedAuthoritativeCurrentValidator
        .BuildDoubleCollectProofSha256(
            expectedCurrentBinding,
            Sha("expected-run-immutable-header"),
            Sha("expected-first-collect"),
            Sha("expected-second-collect"),
            current: true);
Require(
    "P10-T26-BIND-47",
    immutableExpectedProof ==
        StatisticReconciliationExpectedLedgerCanonicalizer.HashSequence(
            "P10_EXPECTED_AUTHORITATIVE_CURRENT_PROOF_V2",
            [expectedCurrentBinding.GenerationId,
             expectedCurrentBinding.GenerationSemanticSha256,
             expectedCurrentBinding.MetricPlanSha256,
             expectedCurrentBinding.ManifestSha256,
             expectedCurrentBinding.DocumentCount.ToString(
                 System.Globalization.CultureInfo.InvariantCulture),
             Sha("expected-run-immutable-header"),
             Sha("expected-first-collect"),
             Sha("expected-second-collect"),
             "CURRENT"]) &&
    immutableExpectedProof !=
        StatisticReconciliationExpectedAuthoritativeCurrentValidator
            .BuildDoubleCollectProofSha256(
                expectedCurrentBinding,
                Sha("other-expected-run-immutable-header"),
                Sha("expected-first-collect"),
                Sha("expected-second-collect"),
                current: true) &&
    immutableExpectedProof !=
        StatisticReconciliationExpectedAuthoritativeCurrentValidator
            .BuildDoubleCollectProofSha256(
                expectedCurrentBinding,
                Sha("expected-run-immutable-header"),
                Sha("other-expected-first-collect"),
                Sha("expected-second-collect"),
                current: true));
Console.WriteLine(
    $"P10_T26_PUBLICATION_BINDING_OK cases={cases} boundFields=38 " +
    "committedRead=true beforeReplay=true beforeCas=true " +
    "catalogComparison=true");
return;

void Accept(
    string id,
    StatisticReconciliationActualPublicationContext candidate)
    => Require(
        id,
        StatisticReconciliationActualRunBindingGuard.Matches(run, candidate));

void Reject(
    string id,
    StatisticReconciliationActualPublicationContext candidate)
    => Require(
        id,
        !StatisticReconciliationActualRunBindingGuard.Matches(run, candidate));

void RejectCatalog(
    string id,
    Func<
        StatisticReconciliationActualPublicationCatalogPins,
        StatisticReconciliationActualPublicationCatalogPins> mutate)
    => Reject(id, baseline with { CatalogPins = mutate(baseline.CatalogPins) });

void Require(string id, bool condition)
{
    if (!condition)
        throw new InvalidOperationException($"{id}: assertion failed");
    cases++;
    Console.WriteLine($"{id}: PASS");
}

static StatisticReconciliationActualPublicationContext BuildBinding()
{
    var pins = new StatisticReconciliationActualPublicationCatalogPins(
        "p9-v1",
        Sha("p9-catalog-raw"),
        Sha("p9-catalog-semantic"),
        Sha("p9-schema-raw"),
        Sha("p9-schema-semantic"),
        Sha("p9-stage-lock"),
        "candidate-chain",
        "candidate-prompt",
        "candidate-v1",
        Sha("candidate-catalog-raw"),
        Sha("candidate-catalog-semantic"),
        Sha("candidate-schema-raw"),
        Sha("candidate-schema-semantic"),
        Sha("candidate-stage-lock"),
        Sha("placeholder-pin-set"));
    pins = pins with { CatalogPinSetSha256 = CatalogPinSet(pins) };
    return new StatisticReconciliationActualPublicationContext(
        "run-id",
        Sha("immutable-identity"),
        Sha("immutable-header"),
        "work-id",
        "scope-id",
        "2026-08",
        "period-instance",
        "concept",
        "MONTH",
        "PERIOD_END",
        Sha("filter"),
        "form-version",
        Sha("form-schema"),
        "flow-template-version",
        Sha("flow-payload"),
        "flow-instance",
        "execution-epoch-id",
        7,
        11,
        "p8-owner",
        Sha("p8-bundle"),
        Sha("actual-configuration-bundle"),
        Sha("coherent-catalog-pin-set"),
        pins,
        LifecycleMetricScopeSha256: Sha("lifecycle-metric-scope"));
}

static StatisticReconciliationRun BuildRun(
    StatisticReconciliationActualPublicationContext value)
{
    var pins = value.CatalogPins;
    return new StatisticReconciliationRun
    {
        Id = value.ReconciliationId,
        ImmutableIdentityHash = value.ImmutableIdentitySha256,
        ImmutableHeaderHash = value.ImmutableHeaderSha256,
        WorkId = value.WorkId,
        ScopeAssignmentId = value.ScopeAssignmentId,
        PeriodKey = value.PeriodKey,
        PeriodInstanceKey = value.PeriodInstanceKey,
        ConceptKey = value.ConceptKey,
        Grain = value.Grain,
        TimeAxis = value.TimeAxis,
        FilterHash = value.FilterSha256,
        DynamicFormVersionId = value.DynamicFormVersionId,
        DynamicFormSchemaHash = value.DynamicFormSchemaSha256,
        FlowTemplateVersionId = value.FlowTemplateVersionId,
        FlowPayloadHash = value.FlowPayloadSha256,
        FlowInstanceId = value.FlowInstanceId,
        FlowExecutionEpochId = value.ExecutionEpochId,
        FlowExecutionEpoch = value.ExecutionEpoch,
        FlowExecutionEpochRevision = value.ExecutionEpochRevision,
        P8ConfigOwnerId = value.P8ConfigurationOwnerId,
        P8ConfigBundleHash = value.P8ConfigurationBundleSha256,
        ActualCapturePlan = new StatisticReconciliationActualCapturePlan
        {
            ActualConfigurationBundleSha256 =
                value.ActualConfigurationBundleSha256
        },
        ActualConfigurationBundleSha256 =
            value.ActualConfigurationBundleSha256,
        P9CatalogVersion = pins.P9CatalogVersion,
        P9CatalogRawSha256 = pins.P9CatalogRawSha256,
        P9CatalogSemanticSha256 = pins.P9CatalogSemanticSha256,
        P9SchemaRawSha256 = pins.P9SchemaRawSha256,
        P9SchemaSemanticSha256 = pins.P9SchemaSemanticSha256,
        P9StageLockSha256 = pins.P9StageLockSha256,
        CandidateChainId = pins.CandidateChainId,
        CandidatePromptId = pins.CandidatePromptId,
        CandidateCatalogVersion = pins.CandidateCatalogVersion,
        CandidateCatalogRawSha256 = pins.CandidateCatalogRawSha256,
        CandidateCatalogSemanticSha256 = pins.CandidateCatalogSemanticSha256,
        CandidateSchemaRawSha256 = pins.CandidateSchemaRawSha256,
        CandidateSchemaSemanticSha256 = pins.CandidateSchemaSemanticSha256,
        CandidateStageLockSha256 = pins.CandidateStageLockSha256
    };
}

static string CatalogPinSet(
    StatisticReconciliationActualPublicationCatalogPins pins)
    => StatisticReconciliationActualCanonical.Hash(
        "P10_ACTUAL_CATALOG_PIN_SET_V1",
        pins.P9CatalogVersion,
        pins.P9CatalogRawSha256,
        pins.P9CatalogSemanticSha256,
        pins.P9SchemaRawSha256,
        pins.P9SchemaSemanticSha256,
        pins.P9StageLockSha256,
        pins.CandidateChainId,
        pins.CandidatePromptId,
        pins.CandidateCatalogVersion,
        pins.CandidateCatalogRawSha256,
        pins.CandidateCatalogSemanticSha256,
        pins.CandidateSchemaRawSha256,
        pins.CandidateSchemaSemanticSha256,
        pins.CandidateStageLockSha256);

static StatisticReconciliationObservationCatalogPins ObservationCatalogPins(
    StatisticReconciliationActualPublicationCatalogPins pins)
{
    var expected = new StatisticReconciliationObservationCatalogPins
    {
        P9CatalogVersion = pins.P9CatalogVersion,
        P9CatalogRawSha256 = pins.P9CatalogRawSha256,
        P9CatalogSemanticSha256 = pins.P9CatalogSemanticSha256,
        P9SchemaRawSha256 = pins.P9SchemaRawSha256,
        P9SchemaSemanticSha256 = pins.P9SchemaSemanticSha256,
        P9StageLockSha256 = pins.P9StageLockSha256,
        CandidateChainId = pins.CandidateChainId,
        CandidatePromptId = pins.CandidatePromptId,
        CandidateCatalogVersion = pins.CandidateCatalogVersion,
        CandidateCatalogRawSha256 = pins.CandidateCatalogRawSha256,
        CandidateCatalogSemanticSha256 =
            pins.CandidateCatalogSemanticSha256,
        CandidateSchemaRawSha256 = pins.CandidateSchemaRawSha256,
        CandidateSchemaSemanticSha256 =
            pins.CandidateSchemaSemanticSha256,
        CandidateStageLockSha256 = pins.CandidateStageLockSha256
    };
    expected.CatalogPinSetSha256 = StatisticReconciliationActualCanonical.Hash(
        "P10_EXPECTED_CATALOG_PINS_V1",
        expected.P9CatalogVersion,
        expected.P9CatalogRawSha256,
        expected.P9CatalogSemanticSha256,
        expected.P9SchemaRawSha256,
        expected.P9SchemaSemanticSha256,
        expected.P9StageLockSha256,
        expected.CandidateChainId,
        expected.CandidatePromptId,
        expected.CandidateCatalogVersion,
        expected.CandidateCatalogRawSha256,
        expected.CandidateCatalogSemanticSha256,
        expected.CandidateSchemaRawSha256,
        expected.CandidateSchemaSemanticSha256,
        expected.CandidateStageLockSha256);
    return expected;
}
static string Sha(string value)
    => StatisticReconciliationActualCanonical.Hash(
        "P10_T26_PUBLICATION_BINDING_TEST_V1",
        value);

static int CountOccurrences(string source, string token)
{
    var count = 0;
    for (var index = 0;
         (index = source.IndexOf(token, index, StringComparison.Ordinal)) >= 0;
         index += token.Length)
    {
        count++;
    }
    return count;
}

static string FindRepoRoot()
{
    for (var current = new DirectoryInfo(Directory.GetCurrentDirectory());
         current is not null;
         current = current.Parent)
    {
        if (File.Exists(Path.Combine(current.FullName, "tdtd-be", "tdtd-be.csproj")))
            return current.FullName;
    }
    throw new InvalidOperationException("Repository root not found.");
}
