using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

internal static class PublicationV7Fixture
{
    internal static Task<StatisticReconciliationActualPublicationContext>
        PrepareAsync(
            StatisticReconciliationActualCoherentGeneration generation,
            StatisticReconciliationActualPublicationContext context,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = Plan(context.ReconciliationId);
        var layers = generation.OrderedLayerObservations;
        if (layers.Length !=
                StatisticReconciliationActualCoherentLayers.RequiredOrder.Length)
            throw new InvalidOperationException("V7_FIXTURE_LAYER_SET_INVALID");

        string Capture(string layer) => layers.Single(value =>
            string.Equals(value.Layer, layer, StringComparison.Ordinal))
            .CaptureSemanticSha256;

        var binding = Relational(plan, Capture);
        var decisions = ImmutableArray<
            StatisticReconciliationActualPublishedSourceDecision>.Empty;
        var membership = StatisticReconciliationActualCanonical.HashSequence(
            "P10_INCLUDED_SOURCE_MEMBERSHIP_V1", []);
        var manifest = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_PUBLICATION_SOURCE_DECISION_MANIFEST_V1", []);
        return Task.FromResult(context with
        {
            RuntimeKind = StatisticReconciliationExpectedRuntimeKinds.Flow,
            SummaryPlanBinding = plan,
            ActualMembershipSemanticSha256 = membership,
            ActualSourceDecisionManifestSha256 = manifest,
            ActualSourceDecisions = decisions,
            LifecycleMetricScopeSha256 = Sha("v7-lifecycle-metric-scope"),
            RelationalProofBinding = binding
        });
    }

    private static StatisticReconciliationActualSummaryPlanBinding Plan(
        string reconciliationId)
    {
        var identity = new StatisticReconciliationExpectedMetricIdentityCompiler()
            .Compile(new ExpectedMetricIdentityRequest(
                "ADVANCED", "FIELD", "value:SUM", "MONTH:2026-08",
                "field-v7", null, null, null, null, null, "MONTH", null));
        var operations = ImmutableArray.Create("SUM");
        const string pointer = "/projected/field-v7";
        var entrySha = StatisticReconciliationExpectedMetricPlanIntegrity
            .BuildEntrySha256(identity, pointer, "NUMBER", false, false,
                operations, StatisticReconciliationExpectedDiffTransitionModes.None,
                null, null, null, null);
        var descriptor = StatisticReconciliationActualSummaryIdentityDescriptor
            .Create(new StatisticReconciliationExpectedValueFreeMetricPlanDescriptor(
                StatisticReconciliationExpectedMetricPlanEntrySchemaVersions.V2,
                identity, pointer, "NUMBER", false, null, false, operations,
                StatisticReconciliationExpectedDiffTransitionModes.None,
                null, null, null, null, ["NONE"], entrySha));
        var expected = new StatisticReconciliationExpectedGenerationBinding(
            reconciliationId,
            Sha("v7-expected-generation-id"),
            Sha("v7-expected-generation-semantic"),
            Sha("v7-expected-metric-plan"),
            1,
            Sha("v7-expected-manifest"),
            1,
            Sha("v7-expected-membership"),
            StatisticReconciliationExpectedRuntimeKinds.Flow);
        return StatisticReconciliationActualSummaryPlanBinding.Create(
            expected, [descriptor]);
    }

    private static StatisticReconciliationActualRelationalProofBinding Relational(
        StatisticReconciliationActualSummaryPlanBinding plan,
        Func<string, string> capture)
    {
        var emptyCentral = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_CENTRAL_PLAN_DESCRIPTOR_MANIFEST_V1", []);
        var centralPlan = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CENTRAL_PLAN_NOT_APPLICABLE_V1",
            plan.SemanticSha256, plan.ExpectedIdentitySetSha256, emptyCentral, I(0));
        var centralProof = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CENTRAL_PLAN_PROJECTION_V1",
            plan.SemanticSha256, plan.ExpectedIdentitySetSha256,
            I(plan.MetricIdentityCount), "DIRECT", "BASIC", centralPlan,
            emptyCentral, I(0));
        var familyManifests = new[] { "ADVANCED", "DIFF" }.Select(family =>
            StatisticReconciliationActualExtendedRawSourceTypedCompiler
                .DescriptorManifest(
                    $"P10_ACTUAL_EXTENDED_OWNER_{family}_DESCRIPTORS_V1",
                    plan.IdentityDescriptors.Where(value =>
                        value.Family == family).ToImmutableArray()));
        var extendedDescriptors = StatisticReconciliationActualCanonical
            .HashSequence(
                "P10_ACTUAL_RELATIONAL_EXTENDED_OWNER_DESCRIPTORS_V1",
                familyManifests);
        var diffNotApplicable =
            StatisticReconciliationActualExtendedRawSourceIntegrity
                .DiffNotApplicable(plan.SemanticSha256);
        var api = capture(StatisticReconciliationActualCoherentLayers.Api);
        var export = capture(StatisticReconciliationActualCoherentLayers.Export);
        var authorization = Sha("v7-cross-view-authorization");
        var compatibility = Sha("v7-cross-view-compatibility");
        var crossProof = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CROSS_VIEW_PARITY_PROOF_V3",
            StatisticReconciliationActualCrossViewParityV3Schemas.Proof,
            authorization, compatibility, api, export);
        var crossResolution = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_CROSS_VIEW_V3_OWNER_RESOLUTION_V1",
            StatisticReconciliationActualCrossViewV3OwnerSchemas.Resolution,
            StatisticReconciliationActualCrossViewV2OwnerStates.Complete,
            authorization, crossProof);
        var draft = new StatisticReconciliationActualRelationalProofFacts(
            SchemaVersion: StatisticReconciliationActualRelationalProofSchemas.Facts,
            SummaryPlanBindingSha256: plan.SemanticSha256,
            CentralProjectedPlanSha256: centralPlan,
            CentralPlanProjectionProofSha256: centralProof,
            SourceCaptureSha256: capture(StatisticReconciliationActualCoherentLayers.SourceMembership),
            DirectCaptureSha256: capture(StatisticReconciliationActualCoherentLayers.DirectProjection),
            AggregateCaptureSha256: capture(StatisticReconciliationActualCoherentLayers.Aggregate),
            BasicCaptureSha256: capture(StatisticReconciliationActualCoherentLayers.Basic),
            AdvancedCaptureSha256: capture(StatisticReconciliationActualCoherentLayers.Advanced),
            DiffCaptureSha256: capture(StatisticReconciliationActualCoherentLayers.Diff),
            ApiCaptureSha256: api,
            ExportCaptureSha256: export,
            RawProofSha256: Sha("v7-raw-proof"),
            RawSourceManifestSha256: Sha("v7-raw-source-manifest"),
            RawSourceCount: 0,
            RawAtomManifestSha256: Sha("v7-raw-atom-manifest"),
            RawAtomCount: 0,
            RawDoubleCollectProofSha256: Sha("v7-raw-double-collect"),
            SummaryParityProofSha256: Sha("v7-summary-proof"),
            SummaryParityRawManifestSha256: Sha("v7-summary-raw"),
            SummaryParityRawAtomCount: 0,
            SummaryParityOwnerManifestSha256: Sha("v7-summary-owner"),
            SummaryParityOwnerItemCount: 0,
            SummaryParityRelationManifestSha256: Sha("v7-summary-relation"),
            SummaryParityRelationCount: 0,
            DirectParityProofSha256: Sha("v7-direct-proof"),
            DirectParityOwnerManifestSha256: Sha("v7-direct-owner"),
            DirectParityOwnerItemCount: 0,
            DirectParityRelationManifestSha256: Sha("v7-direct-relation"),
            DirectParityRelationCount: 0,
            ExtendedRawResolutionProofSha256: Sha("v7-extended-resolution"),
            ExtendedAdvancedProofSha256: Sha("v7-extended-advanced"),
            ExtendedAdvancedSchemaOptionBindingSha256: Sha("v7-extended-schema-options"),
            ExtendedAdvancedDescriptorProjectionManifestSha256: Sha("v7-extended-projections"),
            ExtendedAdvancedDescriptorProjectionCount: 1,
            ExtendedAdvancedTypedAtomManifestSha256: Sha("v7-extended-advanced-atoms"),
            ExtendedAdvancedTypedAtomCount: 0,
            ExtendedAdvancedDescriptorContributionManifestSha256: Sha("v7-extended-contributions"),
            ExtendedAdvancedDescriptorContributionCount: 0,
            ExtendedDiffProofSha256: diffNotApplicable.ProofSha256,
            ExtendedDiffPeriodBindingSha256: diffNotApplicable.PeriodBindingSha256,
            ExtendedDiffTypedAtomManifestSha256: diffNotApplicable.TypedAtomManifestSha256,
            ExtendedDiffTypedAtomCount: 0,
            ExtendedDiffSourcePairManifestSha256: diffNotApplicable.SourcePairManifestSha256,
            ExtendedDiffSourcePairCount: 0,
            ExtendedPartitionDoubleCollectManifestSha256: Sha("v7-extended-partition"),
            ExtendedPartitionDoubleCollectCount: 1,
            ExtendedOwnerParityProofSha256: Sha("v7-extended-owner-proof"),
            ExtendedOwnerDescriptorManifestSha256: extendedDescriptors,
            ExtendedOwnerDescriptorCount: 1,
            ExtendedOwnerTypedAtomManifestSha256: Sha("v7-extended-owner-atoms"),
            ExtendedOwnerTypedAtomCount: 0,
            ExtendedOwnerItemManifestSha256: Sha("v7-extended-owner-items"),
            ExtendedOwnerItemCount: 0,
            ExtendedOwnerRelationManifestSha256: Sha("v7-extended-owner-relations"),
            ExtendedOwnerRelationCount: 0,
            CrossViewAuthorizationRelationSha256: authorization,
            CrossViewResolutionSha256: crossResolution,
            CrossViewProofSha256: crossProof,
            CrossViewCompatibilityProofSha256: compatibility,
            CrossViewOriginalApiSemanticSha256: api,
            CrossViewOriginalExportSemanticSha256: export,
            SemanticSha256: Sha("v7-draft"));
        var facts = draft with
        {
            SemanticSha256 = StatisticReconciliationActualRelationalProofOwner
                .ComputeFactsSemantic(draft)
        };
        facts = StatisticReconciliationActualRelationalProofOwner.NormalizeFacts(facts);
        return StatisticReconciliationActualRelationalProofOwner.BindDoubleResolved(
            facts, facts.SemanticSha256, facts, facts.SemanticSha256);
    }

    private static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);

    private static string Sha(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
