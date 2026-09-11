using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

var first = Seal(Facts('a'));
var normalized = StatisticReconciliationActualRelationalProofOwner
    .NormalizeFacts(first);
Equal(first, normalized);
Console.WriteLine("PASS P10-RPO-01 facts-normalize");

Throws("P10-RPO-02 facts-one-bit-tamper", () =>
    StatisticReconciliationActualRelationalProofOwner.NormalizeFacts(
        first with { RawAtomCount = first.RawAtomCount + 1 }));

var binding = StatisticReconciliationActualRelationalProofOwner
    .BindDoubleResolved(first, first.SemanticSha256,
        first, first.SemanticSha256);
Equal(binding, StatisticReconciliationActualRelationalProofBinding.Normalize(
    binding));
Equal(first.SemanticSha256, binding.FirstRelationalResolutionSha256);
Equal(binding.FirstRelationalResolutionSha256,
    binding.SecondRelationalResolutionSha256);
Console.WriteLine("PASS P10-RPO-03 double-resolve-positive");

var second = Seal(Facts('b'));
Throws("P10-RPO-04 facts-drift", () =>
    StatisticReconciliationActualRelationalProofOwner.BindDoubleResolved(
        first, first.SemanticSha256, second, second.SemanticSha256));

Throws("P10-RPO-05 first-resolution-drift", () =>
    StatisticReconciliationActualRelationalProofOwner.BindDoubleResolved(
        first, Hash('f'), first, first.SemanticSha256));

Throws("P10-RPO-06 durable-binding-tamper", () =>
    StatisticReconciliationActualRelationalProofBinding.Normalize(
        binding with { DoubleResolveProofSha256 = Hash('f') }));

var invalidOwner = new StatisticReconciliationActualRelationalProofOwner(
    null!, null!, null!, null!, null!);
var invalid = await invalidOwner.ResolveAsync(null!);
if (invalid.State !=
        StatisticReconciliationActualRelationalProofStates.Incomplete ||
    invalid.FailureCode != "RELATIONAL_PROOF_INPUT_REQUIRED" ||
    invalid.Binding is not null || invalid.ResolutionSha256.Length != 64)
    throw new InvalidOperationException(
        $"P10-RPO-07 expected input failure, got {invalid.FailureCode}");
Console.WriteLine("PASS P10-RPO-07 invalid-input-fail-closed");

var contradictoryCount = first with
{
    SummaryParityRawAtomCount = first.RawAtomCount + 1
};
contradictoryCount = contradictoryCount with
{
    SemanticSha256 = StatisticReconciliationActualRelationalProofOwner
        .ComputeFactsSemantic(contradictoryCount)
};
Throws("P10-RPO-08 contradictory-raw-count", () =>
    StatisticReconciliationActualRelationalProofOwner.NormalizeFacts(
        contradictoryCount));

var impossibleCrossView = first with
{
    CrossViewCompatibilityProofSha256 = Hash('f')
};
impossibleCrossView = impossibleCrossView with
{
    SemanticSha256 = StatisticReconciliationActualRelationalProofOwner
        .ComputeFactsSemantic(impossibleCrossView)
};
Throws("P10-RPO-09 nested-cross-view-root", () =>
    StatisticReconciliationActualRelationalProofOwner.NormalizeFacts(
        impossibleCrossView));

var nonApplicablePlan = DirectPlan();
var nonApplicableFacts = NonApplicableFacts(
    nonApplicablePlan,
    Facts('c'));
StatisticReconciliationActualRelationalProofOwner.RequirePlanProjections(
    nonApplicableFacts,
    nonApplicablePlan);
Console.WriteLine("PASS P10-RPO-NA-01 direct-plan-fixed-slots");
var nonApplicableTamper = Seal(nonApplicableFacts with
{
    ExtendedAdvancedProofSha256 = Hash('f')
});
Throws("P10-RPO-NA-02 applicability-proof-tamper", () =>
    StatisticReconciliationActualRelationalProofOwner.RequirePlanProjections(
        nonApplicableTamper,
        nonApplicablePlan));
var basicRelationTamper = Seal(nonApplicableFacts with
{
    SummaryParityRelationCount = 1
});
Throws("P10-RPO-NA-03 basic-applicability-count-tamper", () =>
    StatisticReconciliationActualRelationalProofOwner.RequirePlanProjections(
        basicRelationTamper,
        nonApplicablePlan));
var stored = new StatisticReconciliationObservation
{
    RecordKind = StatisticReconciliationActualPublicationRecordKinds.Commit,
    ActualRelationalProofBindingSha256 = binding.SemanticSha256,
    ActualRelationalProof = StatisticReconciliationActualGenerationPublisher
        .ToStoredRelationalProof(binding)
};
var roundTrip = BsonSerializer.Deserialize<StatisticReconciliationObservation>(
    stored.ToBson());
Equal(binding, StatisticReconciliationActualGenerationPublisher
    .RelationalProofBindingFromStoredDocument(roundTrip));
Console.WriteLine("PASS P10-RPO-V7-01 full-bson-roundtrip");

var descriptorTamper = BsonSerializer.Deserialize<
    StatisticReconciliationObservation>(stored.ToBson());
descriptorTamper.ActualRelationalProof!
    .ExtendedAdvancedDescriptorContributionManifestSha256 = Hash('f');
Throws("P10-RPO-V7-04 contribution-manifest-tamper", () =>
    StatisticReconciliationActualGenerationPublisher
        .RelationalProofBindingFromStoredDocument(descriptorTamper));

var periodTamper = BsonSerializer.Deserialize<
    StatisticReconciliationObservation>(stored.ToBson());
periodTamper.ActualRelationalProof!.ExtendedDiffPeriodBindingSha256 = Hash('f');
Throws("P10-RPO-V7-05 period-binding-tamper", () =>
    StatisticReconciliationActualGenerationPublisher
        .RelationalProofBindingFromStoredDocument(periodTamper));

var ownerManifestTamper = BsonSerializer.Deserialize<
    StatisticReconciliationObservation>(stored.ToBson());
ownerManifestTamper.ActualRelationalProof!
    .ExtendedOwnerItemManifestSha256 = Hash('0');
Throws("P10-RPO-V7-06 owner-manifest-tamper", () =>
    StatisticReconciliationActualGenerationPublisher
        .RelationalProofBindingFromStoredDocument(ownerManifestTamper));
var ownerCountTamper = BsonSerializer.Deserialize<
    StatisticReconciliationObservation>(stored.ToBson());
ownerCountTamper.ActualRelationalProof!.ExtendedOwnerDescriptorCount++;
Throws("P10-RPO-V7-07 owner-count-tamper", () =>
    StatisticReconciliationActualGenerationPublisher
        .RelationalProofBindingFromStoredDocument(ownerCountTamper));
var directTamper = BsonSerializer.Deserialize<
    StatisticReconciliationObservation>(stored.ToBson());
directTamper.ActualRelationalProof!.DirectParityRelationCount++;
Throws("P10-RPO-V7-08 direct-relation-count-tamper", () =>
    StatisticReconciliationActualGenerationPublisher
        .RelationalProofBindingFromStoredDocument(directTamper));
var schemaOptionTamper = BsonSerializer.Deserialize<
    StatisticReconciliationObservation>(stored.ToBson());
schemaOptionTamper.ActualRelationalProof!
    .ExtendedAdvancedSchemaOptionBindingSha256 = Hash('0');
Throws("P10-RPO-V7-09 schema-option-binding-tamper", () =>
    StatisticReconciliationActualGenerationPublisher
        .RelationalProofBindingFromStoredDocument(schemaOptionTamper));
roundTrip.ActualRelationalProof!.RawAtomCount++;
Throws("P10-RPO-V7-02 persisted-one-bit-tamper", () =>
    StatisticReconciliationActualGenerationPublisher
        .RelationalProofBindingFromStoredDocument(roundTrip));

Throws("P10-RPO-V7-03 missing-proof-fail-closed", () =>
    StatisticReconciliationActualGenerationPublisher
        .RelationalProofBindingFromStoredDocument(new()
        {
            RecordKind =
                StatisticReconciliationActualPublicationRecordKinds.Commit,
            ActualRelationalProofBindingSha256 = binding.SemanticSha256
        }));
var registrations = new ServiceCollection();
registrations.AddStatisticReconciliationActualCapture(
    new ConfigurationBuilder().Build());
RequireRegistration<IStatisticReconciliationActualExtendedRawSourceOwnerParity>(
    registrations, ServiceLifetime.Scoped, "P10-RPO-10 extended-parity-di");
RequireRegistration<IStatisticReconciliationActualRelationalProofOwner>(
    registrations, ServiceLifetime.Scoped, "P10-RPO-11 relational-di");
RequireRegistration<IStatisticReconciliationTrustedCurrentOwnerReader>(
    registrations, ServiceLifetime.Scoped, "P10-RPO-12 current-reader-di");
RequireRegistration<StatisticReconciliationActualCaptureService>(
    registrations, ServiceLifetime.Scoped, "P10-RPO-13 capture-service-di");
Console.WriteLine("PASS P10-RPO-10/11/12/13 production-di-wiring");

var directExact = await DirectRelationFixture(1m, 2m);
var directProof = StatisticReconciliationActualDirectRawOwnerParity.Prove(
    directExact.Plan, directExact.Raw, directExact.Source, directExact.Direct);
if (!directProof.Complete || !directProof.Applicable ||
    directProof.RelationCount != 1)
    throw new InvalidOperationException("P10-RPO-DIRECT-01 expected complete");
Console.WriteLine("PASS P10-RPO-DIRECT-01 per-report-positive");
var directSwap = await DirectRelationFixture(2m, 1m);
Throws("P10-RPO-DIRECT-02 cross-report-swap-rejected", () =>
    StatisticReconciliationActualDirectRawOwnerParity.Prove(
        directExact.Plan, directExact.Raw, directExact.Source,
        directSwap.Direct));

Console.WriteLine(
    "P10_ACTUAL_PUBLICATION_V7_PROOF_OK cases=9 fullRoundtrip=true " +
    "tamperRejected=true extendedRootsTamperRejected=4 missingRejected=true");

Console.WriteLine(
    "P10_RELATIONAL_PROOF_OWNER_OK checks=15 doubleResolve=true " +
    "failClosed=true nestedRoots=true wired=true");

static StatisticReconciliationActualRelationalProofFacts Seal(
    StatisticReconciliationActualRelationalProofFacts draft)
{
    var semantic = StatisticReconciliationActualRelationalProofOwner
        .ComputeFactsSemantic(draft);
    return StatisticReconciliationActualRelationalProofOwner.NormalizeFacts(
        draft with { SemanticSha256 = semantic });
}

static StatisticReconciliationActualRelationalProofFacts Facts(char seed)
{
    var api = Hash(Next(seed, 1));
    var export = Hash(Next(seed, 2));
    var authorization = Hash(Next(seed, 17));
    var compatibility = Hash(Next(seed, 20));
    var crossViewProof = StatisticReconciliationActualCanonical.Hash(
        "P10_ACTUAL_CROSS_VIEW_PARITY_PROOF_V3",
        StatisticReconciliationActualCrossViewParityV3Schemas.Proof,
        authorization, compatibility, api, export);
    var crossViewResolution = StatisticReconciliationActualCanonical.Hash(
        "P10_ACTUAL_CROSS_VIEW_V3_OWNER_RESOLUTION_V1",
        StatisticReconciliationActualCrossViewV3OwnerSchemas.Resolution,
        StatisticReconciliationActualCrossViewV2OwnerStates.Complete,
        authorization, crossViewProof);
    return new StatisticReconciliationActualRelationalProofFacts(
        StatisticReconciliationActualRelationalProofSchemas.Facts,
        Hash(seed), Hash(Next(seed, 31)), Hash(Next(seed, 32)),
        Hash(Next(seed, 3)), Hash(Next(seed, 4)),
        Hash(Next(seed, 5)), Hash(Next(seed, 6)), Hash(Next(seed, 7)),
        Hash(Next(seed, 8)), api, export, Hash(Next(seed, 9)),
        Hash(Next(seed, 10)), 1, Hash(Next(seed, 11)), 11,
        Hash(Next(seed, 12)), Hash(Next(seed, 13)),
        Hash(Next(seed, 14)), 11, Hash(Next(seed, 15)), 1,
        Hash(Next(seed, 16)), 1,
        Hash(Next(seed, 38)), Hash(Next(seed, 39)), 1,
        Hash(Next(seed, 40)), 1,
        Hash(Next(seed, 22)), Hash(Next(seed, 23)),
        Hash(Next(seed, 41)), Hash(Next(seed, 42)), 1,
        Hash(Next(seed, 24)), 1,
        Hash(Next(seed, 33)), 1,
        Hash(Next(seed, 25)), Hash(Next(seed, 34)),
        Hash(Next(seed, 26)), 1, Hash(Next(seed, 27)), 1,
        Hash(Next(seed, 28)), 2, Hash(Next(seed, 29)),
        Hash(Next(seed, 35)), 2,
        Hash(Next(seed, 36)), 2,
        Hash(Next(seed, 37)), 2,
        Hash(Next(seed, 30)), 2, authorization,
        crossViewResolution, crossViewProof,
        compatibility, api, export, Hash(Next(seed, 21)));
}

static StatisticReconciliationActualRelationalProofFacts NonApplicableFacts(
    StatisticReconciliationActualSummaryPlanBinding plan,
    StatisticReconciliationActualRelationalProofFacts draft)
{
    var advanced = StatisticReconciliationActualExtendedRawSourceIntegrity
        .AdvancedNotApplicable(plan.SemanticSha256);
    var diff = StatisticReconciliationActualExtendedRawSourceIntegrity
        .DiffNotApplicable(plan.SemanticSha256);
    var centralDescriptors = plan.IdentityDescriptors
        .Where(value => value.Family is "DIRECT" or "BASIC")
        .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
        .ToImmutableArray();
    var centralManifest = Hs(
        "P10_ACTUAL_CENTRAL_PLAN_DESCRIPTOR_MANIFEST_V1",
        centralDescriptors.Select(value => value.SemanticSha256));
    var centralProof = H(
        "P10_ACTUAL_CENTRAL_PLAN_PROJECTION_V1",
        plan.SemanticSha256,
        plan.ExpectedIdentitySetSha256,
        I(plan.MetricIdentityCount),
        "DIRECT",
        "BASIC",
        plan.SemanticSha256,
        centralManifest,
        I(centralDescriptors.Length));
    var ownerDescriptorManifest = Hs(
        "P10_ACTUAL_RELATIONAL_EXTENDED_OWNER_DESCRIPTORS_V1",
        new[] { "ADVANCED", "DIFF" }.Select(family =>
            StatisticReconciliationActualExtendedRawSourceTypedCompiler
                .DescriptorManifest(
                    $"P10_ACTUAL_EXTENDED_OWNER_{family}_DESCRIPTORS_V1",
                    [])));
    var result = draft with
    {
        SummaryPlanBindingSha256 = plan.SemanticSha256,
        CentralProjectedPlanSha256 = plan.SemanticSha256,
        CentralPlanProjectionProofSha256 = centralProof,
        SummaryParityRelationCount = 0,
        DirectParityRelationCount = 1,
        ExtendedAdvancedProofSha256 = advanced.ProofSha256,
        ExtendedAdvancedSchemaOptionBindingSha256 =
            advanced.SchemaOptionBindingSha256,
        ExtendedAdvancedDescriptorProjectionManifestSha256 = Hs(
            "P10_ACTUAL_RELATIONAL_EXTENDED_ADVANCED_DESCRIPTOR_PROJECTIONS_V1",
            []),
        ExtendedAdvancedDescriptorProjectionCount = 0,
        ExtendedAdvancedTypedAtomManifestSha256 =
            advanced.TypedAtomManifestSha256,
        ExtendedAdvancedTypedAtomCount = 0,
        ExtendedAdvancedDescriptorContributionManifestSha256 = Hs(
            "P10_ACTUAL_RELATIONAL_EXTENDED_ADVANCED_CONTRIBUTIONS_V1",
            []),
        ExtendedAdvancedDescriptorContributionCount = 0,
        ExtendedDiffProofSha256 = diff.ProofSha256,
        ExtendedDiffPeriodBindingSha256 = diff.PeriodBindingSha256,
        ExtendedDiffTypedAtomManifestSha256 = diff.TypedAtomManifestSha256,
        ExtendedDiffTypedAtomCount = 0,
        ExtendedDiffSourcePairManifestSha256 = diff.SourcePairManifestSha256,
        ExtendedDiffSourcePairCount = 0,
        ExtendedPartitionDoubleCollectManifestSha256 = Hs(
            "P10_ACTUAL_EXTENDED_RAW_SOURCE_PARTITION_DOUBLE_COLLECT_MANIFEST_V2",
            []),
        ExtendedPartitionDoubleCollectCount = 0,
        ExtendedOwnerDescriptorManifestSha256 = ownerDescriptorManifest,
        ExtendedOwnerDescriptorCount = 0,
        ExtendedOwnerTypedAtomCount = 0,
        ExtendedOwnerItemCount = 0,
        ExtendedOwnerRelationCount = 0
    };
    return Seal(result);
}
static async Task<DirectRelationValue> DirectRelationFixture(
    decimal firstOwner,
    decimal secondOwner)
{
    var firstSource = DirectActualFixture.Source(DirectActualFixture.Report1, 0);
    var secondSource = DirectActualFixture.Source(DirectActualFixture.Report2, 1);
    var membership = await DirectActualFixture.CaptureSources(
        [firstSource, secondSource]);
    var boundary = DirectActualFixture.Boundary();
    var firstRow = DirectActualFixture.Field(boundary, firstSource,
        "field-main", "amount", "NUMBER", numeric: firstOwner);
    firstRow.FieldType = "number";
    firstRow.StatisticLabelCodes = ["amount"];
    var secondRow = DirectActualFixture.Field(boundary, secondSource,
        "field-main", "amount", "NUMBER", numeric: secondOwner);
    secondRow.FieldType = "number";
    secondRow.StatisticLabelCodes = ["amount"];
    var direct = await DirectActualFixture.CaptureDirect(boundary, membership,
        fields: [firstRow, secondRow]);
    var plan = DirectPlan();
    var rawSources = membership.IncludedSources
        .Select(decision => DirectEnvelope(decision,
            decision.ReportId == DirectActualFixture.Report1 ? 1m : 2m))
        .OrderBy(value => value.SourceStableIdentitySha256,
            StringComparer.Ordinal).ToImmutableArray();
    return new(plan, DirectRaw(plan, membership, rawSources), membership,
        direct);
}

static StatisticReconciliationActualSummaryPlanBinding DirectPlan()
{
    var identity = new StatisticReconciliationExpectedMetricIdentityCompiler()
        .Compile(new ExpectedMetricIdentityRequest(
            "DIRECT", "FIELD", "amount", "2026-08", "field-main",
            null, null, null, null, null, null, null));
    var operations = ImmutableArray.Create("MAX", "MEAN", "MIN", "SUM");
    const string pointer = "/fieldValues/values/amount";
    var entrySha = StatisticReconciliationExpectedMetricPlanIntegrity
        .BuildEntrySha256(identity, pointer, "NUMBER", false, false,
            operations, StatisticReconciliationExpectedDiffTransitionModes.None,
            null, null, null, null);
    var valueFree = new
        StatisticReconciliationExpectedValueFreeMetricPlanDescriptor(
            StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2,
            identity, pointer, "NUMBER", false, null, false, operations,
            StatisticReconciliationExpectedDiffTransitionModes.None,
            null, null, null, null, ["NONE"], entrySha);
    var descriptor = StatisticReconciliationActualSummaryIdentityDescriptor
        .Create(valueFree);
    var generation = new StatisticReconciliationExpectedGenerationBinding(
        "relational-direct", Sha("generation"),
        Sha("generation-semantic"), Sha("metric-plan"), 1,
        Sha("manifest"), 8, Sha("membership"), "NON_FLOW");
    return StatisticReconciliationActualSummaryPlanBinding.Create(generation,
        [descriptor]);
}

static StatisticReconciliationActualRawPayloadEnvelope DirectEnvelope(
    ActualSourceMembershipDecision decision,
    decimal value)
{
    var owner = decision.ObservedOwner;
    var canonical = Canonical(JsonSerializer.Serialize(new
    {
        fieldValues = new { values = new { amount = value } }
    }));
    var canonicalSha = StatisticReconciliationActualJson.RawSha256(canonical);
    var semantic = H("P10_ACTUAL_RAW_PAYLOAD_ENVELOPE_V1",
        decision.SourceStableIdentitySha256, owner.WorkId,
        owner.WorkAssignmentId, owner.ReportId, owner.PayloadDocumentId,
        I(owner.PayloadRevision), owner.PayloadSha256,
        I(owner.LifecycleRevision), owner.LifecycleSha256,
        decision.OwnerStateSemanticSha256, decision.DecisionSemanticSha256,
        canonicalSha);
    return new(decision.SourceStableIdentitySha256, owner.WorkId,
        owner.WorkAssignmentId, owner.ReportId, owner.PayloadDocumentId,
        owner.PayloadRevision, owner.PayloadSha256, canonicalSha, canonical,
        semantic);
}

static StatisticReconciliationActualRawSummaryProof DirectRaw(
    StatisticReconciliationActualSummaryPlanBinding plan,
    ActualSourceMembershipCapture membership,
    ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope> sources)
{
    var atoms = new StatisticReconciliationActualRawSummaryCompiler()
        .Compile(plan, sources);
    var sourceManifest = Hs("P10_ACTUAL_RAW_SUMMARY_SOURCE_MANIFEST_V1",
        sources.Select(value => value.EnvelopeSemanticSha256));
    var atomManifest = Hs("P10_ACTUAL_RAW_SUMMARY_ATOM_MANIFEST_V1",
        atoms.Select(value => value.AtomSemanticSha256));
    var collect = Hs("P10_ACTUAL_RAW_SUMMARY_COLLECT_V1",
        sources.Select(value => value.EnvelopeSemanticSha256));
    var doubleCollect = H("P10_ACTUAL_RAW_SUMMARY_DOUBLE_COLLECT_V1",
        membership.CaptureSemanticSha256, collect, collect);
    var proof = H(
        StatisticReconciliationActualMongoRawSummaryOwner.ProofSchemaVersion,
        plan.SemanticSha256, membership.CaptureSemanticSha256,
        membership.MembershipSemanticSha256!, sourceManifest,
        I(sources.Length), atomManifest, I(atoms.Length), doubleCollect);
    return new(
        StatisticReconciliationActualMongoRawSummaryOwner.ProofSchemaVersion,
        plan.SemanticSha256, membership.CaptureSemanticSha256,
        membership.MembershipSemanticSha256!, sources, sourceManifest, atoms,
        atomManifest, collect, collect, doubleCollect, proof);
}

static string H(string domain, params string?[] values)
    => StatisticReconciliationActualCanonical.Hash(domain, values);
static string Hs(string domain, IEnumerable<string> values)
    => StatisticReconciliationActualCanonical.HashSequence(domain, values);
static string I(long value)
    => StatisticReconciliationActualCanonical.Integer(value);
static string Sha(string value)
    => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();
static string Canonical(string value)
{
    using var document = StatisticReconciliationActualJson.ParseStrict(
        value, "DIRECT_FIXTURE_JSON");
    return StatisticReconciliationActualJson.Canonicalize(
        document.RootElement);
}

static char Next(char seed, int offset)
{
    const string hex = "0123456789abcdef";
    return hex[(hex.IndexOf(seed) + offset) % hex.Length];
}

static string Hash(char value) => new(value, 64);

static void RequireRegistration<T>(
    IServiceCollection services,
    ServiceLifetime lifetime,
    string id)
{
    var matches = services.Where(value => value.ServiceType == typeof(T))
        .ToArray();
    if (matches.Length != 1 || matches[0].Lifetime != lifetime)
        throw new InvalidOperationException(
            $"{id}: expected one {lifetime} registration");
}
static void Throws(string id, Action action)
{
    try
    {
        action();
    }
    catch (StatisticReconciliationActualObservationException)
    {
        Console.WriteLine($"PASS {id}");
        return;
    }
    throw new InvalidOperationException($"{id}: expected fail-closed exception");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"Expected {expected}, actual {actual}");
}

record DirectRelationValue(
    StatisticReconciliationActualSummaryPlanBinding Plan,
    StatisticReconciliationActualRawSummaryProof Raw,
    ActualSourceMembershipCapture Source,
    ActualDirectProjectionCapture Direct);
