using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static partial class StatisticReconciliationActualExtendedRawSourceIntegrity
{
    internal static StatisticReconciliationActualExtendedRawSourceEnvelope
        Envelope(
            string family,
            string? side,
            string? grain,
            string? grainKey,
            int sourceOrdinal,
            string workId,
            string workAssignmentId,
            string reportId,
            string payloadDocumentId,
            int payloadRevision,
            string payloadOwnerSha256,
            string canonicalPayloadJson,
            int lifecycleRevision,
            string lifecycleSha256,
            string? directRunId,
            string? directGenerationId,
            string? directGenerationSha256)
    {
        family = Required(family, "EXTENDED_ENVELOPE_FAMILY");
        side = Optional(side, "EXTENDED_ENVELOPE_SIDE");
        grain = Optional(grain, "EXTENDED_ENVELOPE_GRAIN");
        grainKey = Optional(grainKey, "EXTENDED_ENVELOPE_GRAIN_KEY");
        directRunId = Optional(directRunId, "EXTENDED_ENVELOPE_DIRECT_RUN_ID");
        directGenerationId = Optional(
            directGenerationId, "EXTENDED_ENVELOPE_DIRECT_GENERATION_ID");
        if (sourceOrdinal < 0 || payloadRevision <= 0 || lifecycleRevision < 0)
            throw Fail("EXTENDED_ENVELOPE_REVISION_INVALID");
        if (family == "ADVANCED")
        {
            if (side is not null || grain is not ("DAY" or "MONTH" or "YEAR") ||
                grainKey is null || directRunId is not null ||
                directGenerationId is not null ||
                directGenerationSha256 is not null)
                throw Fail("EXTENDED_ADVANCED_ENVELOPE_SHAPE_INVALID");
        }
        else if (family == "DIFF")
        {
            if (side is not ("LEFT" or "RIGHT") || grain is not null ||
                grainKey is not null || directRunId is null ||
                directGenerationId is null ||
                directGenerationSha256 is null)
                throw Fail("EXTENDED_DIFF_ENVELOPE_SHAPE_INVALID");
        }
        else
            throw Fail("EXTENDED_ENVELOPE_FAMILY_UNSUPPORTED");

        workId = Required(workId, "EXTENDED_ENVELOPE_WORK_ID");
        workAssignmentId = Required(
            workAssignmentId, "EXTENDED_ENVELOPE_ASSIGNMENT_ID");
        reportId = Required(reportId, "EXTENDED_ENVELOPE_REPORT_ID");
        payloadDocumentId = Required(
            payloadDocumentId, "EXTENDED_ENVELOPE_PAYLOAD_DOCUMENT_ID");
        payloadOwnerSha256 = Sha(
            payloadOwnerSha256, "EXTENDED_ENVELOPE_PAYLOAD_OWNER_SHA");
        lifecycleSha256 = Sha(
            lifecycleSha256, "EXTENDED_ENVELOPE_LIFECYCLE_SHA");
        directGenerationSha256 = directGenerationSha256 is null
            ? null
            : Sha(directGenerationSha256,
                "EXTENDED_ENVELOPE_DIRECT_GENERATION_SHA");
        using var payload = StatisticReconciliationActualJson.ParseStrict(
            canonicalPayloadJson, "EXTENDED_ENVELOPE_CANONICAL_PAYLOAD");
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            payload.RootElement);
        if (!StringComparer.Ordinal.Equals(canonical, canonicalPayloadJson))
            throw Fail("EXTENDED_ENVELOPE_PAYLOAD_NOT_CANONICAL");
        var canonicalPayloadSha256 =
            StatisticReconciliationActualJson.RawSha256(canonical);
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_RAW_SOURCE_ENVELOPE_V2",
            family,
            side,
            grain,
            grainKey,
            I(sourceOrdinal),
            workId,
            workAssignmentId,
            reportId,
            payloadDocumentId,
            I(payloadRevision),
            payloadOwnerSha256,
            canonicalPayloadSha256,
            canonical,
            I(lifecycleRevision),
            lifecycleSha256,
            directRunId,
            directGenerationId,
            directGenerationSha256);
        return new(
            family,
            side,
            grain,
            grainKey,
            sourceOrdinal,
            workId,
            workAssignmentId,
            reportId,
            payloadDocumentId,
            payloadRevision,
            payloadOwnerSha256,
            canonicalPayloadSha256,
            canonical,
            lifecycleRevision,
            lifecycleSha256,
            directRunId,
            directGenerationId,
            directGenerationSha256,
            semantic);
    }

    internal static StatisticReconciliationActualExtendedDiffSourcePin Pin(
        string side,
        string sourceReportId,
        int sourcePayloadRevision,
        string sourcePayloadSha256,
        int sourceLifecycleRevision,
        string directRunId,
        string directGenerationId,
        string directGenerationSha256)
    {
        side = Required(side, "EXTENDED_DIFF_PIN_SIDE");
        if (side is not ("LEFT" or "RIGHT") || sourcePayloadRevision <= 0 ||
            sourceLifecycleRevision < 0)
            throw Fail("EXTENDED_DIFF_PIN_SHAPE_INVALID");
        sourceReportId = Required(
            sourceReportId, "EXTENDED_DIFF_PIN_REPORT_ID");
        sourcePayloadSha256 = Sha(
            sourcePayloadSha256, "EXTENDED_DIFF_PIN_PAYLOAD_SHA");
        directRunId = Required(directRunId, "EXTENDED_DIFF_PIN_RUN_ID");
        directGenerationId = Required(
            directGenerationId, "EXTENDED_DIFF_PIN_GENERATION_ID");
        directGenerationSha256 = Sha(
            directGenerationSha256, "EXTENDED_DIFF_PIN_GENERATION_SHA");
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_SOURCE_PIN_V2",
            side,
            sourceReportId,
            I(sourcePayloadRevision),
            sourcePayloadSha256,
            I(sourceLifecycleRevision),
            directRunId,
            directGenerationId,
            directGenerationSha256);
        return new(
            side,
            sourceReportId,
            sourcePayloadRevision,
            sourcePayloadSha256,
            sourceLifecycleRevision,
            directRunId,
            directGenerationId,
            directGenerationSha256,
            semantic);
    }

    internal static StatisticReconciliationActualExtendedAdvancedGrainProof
        AdvancedGrain(
            string ownerNodeId,
            string grain,
            string grainKey,
            DateTime windowStartUtc,
            DateTime windowEndExclusiveUtc,
            string sourceAssignmentManifestSha256,
            int sourceAssignmentCount,
            string sourceEnvelopeManifestSha256,
            int sourceEnvelopeCount,
            StatisticReconciliationActualExtendedAdvancedTypedPartition typed)
    {
        ownerNodeId = Required(ownerNodeId, "EXTENDED_ADVANCED_NODE_ID");
        grain = Required(grain, "EXTENDED_ADVANCED_GRAIN");
        grainKey = Required(grainKey, "EXTENDED_ADVANCED_GRAIN_KEY");
        if (grain is not ("DAY" or "MONTH" or "YEAR") ||
            windowStartUtc.Kind != DateTimeKind.Utc ||
            windowEndExclusiveUtc.Kind != DateTimeKind.Utc ||
            windowStartUtc >= windowEndExclusiveUtc ||
            sourceAssignmentCount < 0 || sourceEnvelopeCount < 0 ||
            typed is null || typed.DescriptorCount <= 0 ||
            typed.Atoms.IsDefault || typed.Atoms.Length == 0)
            throw Fail("EXTENDED_ADVANCED_GRAIN_SHAPE_INVALID");
        sourceAssignmentManifestSha256 = Sha(
            sourceAssignmentManifestSha256,
            "EXTENDED_ADVANCED_ASSIGNMENT_MANIFEST");
        sourceEnvelopeManifestSha256 = Sha(
            sourceEnvelopeManifestSha256,
            "EXTENDED_ADVANCED_ENVELOPE_MANIFEST");
        var schemaOptionBindingSha256 = Sha(
            typed.SchemaOptionBindingSha256,
            "EXTENDED_ADVANCED_SCHEMA_OPTION_BINDING");
        var atoms = RequireAtoms(
            typed.Atoms,
            "ADVANCED",
            ["NONE"],
            grain,
            $"{grain}:{grainKey}");
        var descriptors = DescriptorSemantics(atoms);
        var projections = RequireAdvancedProjections(
            typed.DescriptorProjections, atoms);
        var projectionManifest = Hs(
            "P10_ACTUAL_EXTENDED_ADVANCED_GRAIN_DESCRIPTOR_PROJECTION_MANIFEST_V1",
            projections.Select(value => value.ProjectionSemanticSha256));
        var contributions = RequireAdvancedContributions(
            typed.DescriptorContributions, atoms);
        var contributionManifest = Hs(
            "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_CONTRIBUTION_MANIFEST_V2",
            contributions.Select(value => value.ContributionSemanticSha256));
        if (descriptors.Length != typed.DescriptorCount ||
            projections.Length != typed.DescriptorCount ||
            projections.Length != typed.DescriptorProjectionCount ||
            contributions.Length != typed.DescriptorCount ||
            contributions.Any(value =>
                value.ValueSourceReportCount > sourceEnvelopeCount) ||
            Hs(
                "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_MANIFEST_V2",
                descriptors) != Sha(
                typed.DescriptorManifestSha256,
                "EXTENDED_ADVANCED_DESCRIPTOR_MANIFEST") ||
            projectionManifest != Sha(
                typed.DescriptorProjectionManifestSha256,
                "EXTENDED_ADVANCED_PROJECTION_MANIFEST") ||
            contributionManifest != Sha(
                typed.DescriptorContributionManifestSha256,
                "EXTENDED_ADVANCED_CONTRIBUTION_MANIFEST") ||
            AtomManifest(
                "P10_ACTUAL_EXTENDED_ADVANCED_TYPED_ATOM_MANIFEST_V2",
                atoms) != Sha(
                typed.AtomManifestSha256,
                "EXTENDED_ADVANCED_TYPED_ATOM_MANIFEST"))
            throw Fail("EXTENDED_ADVANCED_TYPED_PARTITION_INVALID");
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_ADVANCED_GRAIN_V2",
            ownerNodeId,
            grain,
            grainKey,
            StatisticReconciliationActualCanonical.Instant(windowStartUtc),
            StatisticReconciliationActualCanonical.Instant(
                windowEndExclusiveUtc),
            sourceAssignmentManifestSha256,
            I(sourceAssignmentCount),
            sourceEnvelopeManifestSha256,
            I(sourceEnvelopeCount),
            typed.DescriptorManifestSha256,
            I(typed.DescriptorCount),
            schemaOptionBindingSha256,
            projectionManifest,
            I(projections.Length),
            contributionManifest,
            I(contributions.Length),
            typed.AtomManifestSha256,
            I(atoms.Length));
        return new(
            ownerNodeId,
            grain,
            grainKey,
            windowStartUtc,
            windowEndExclusiveUtc,
            sourceAssignmentManifestSha256,
            sourceAssignmentCount,
            sourceEnvelopeManifestSha256,
            sourceEnvelopeCount,
            typed.DescriptorManifestSha256,
            typed.DescriptorCount,
            schemaOptionBindingSha256,
            projections,
            projectionManifest,
            projections.Length,
            contributions,
            contributionManifest,
            contributions.Length,
            atoms,
            typed.AtomManifestSha256,
            atoms.Length,
            semantic);
    }

    internal static StatisticReconciliationActualExtendedAdvancedSourceProof
        Advanced(
            string configId,
            string configVersionId,
            int configVersionNo,
            long configRevision,
            string configSha256,
            string summaryPlanBindingSha256,
            string sourceScopeSemanticSha256,
            string schemaOptionBindingSha256,
            ImmutableArray<
                StatisticReconciliationActualExtendedAdvancedGrainProof> grains,
            ImmutableArray<
                StatisticReconciliationActualExtendedRawSourceEnvelope> envelopes)
    {
        configId = Required(configId, "EXTENDED_ADVANCED_CONFIG_ID");
        configVersionId = Required(
            configVersionId, "EXTENDED_ADVANCED_CONFIG_VERSION_ID");
        if (configVersionNo <= 0 || configRevision <= 0 || grains.IsDefault ||
            grains.Length == 0 || envelopes.IsDefault)
            throw Fail("EXTENDED_ADVANCED_PROOF_SHAPE_INVALID");
        configSha256 = Sha(configSha256, "EXTENDED_ADVANCED_CONFIG_SHA");
        summaryPlanBindingSha256 = Sha(
            summaryPlanBindingSha256, "EXTENDED_ADVANCED_PLAN_SHA");
        sourceScopeSemanticSha256 = Sha(
            sourceScopeSemanticSha256, "EXTENDED_ADVANCED_SCOPE_SHA");
        schemaOptionBindingSha256 = Sha(
            schemaOptionBindingSha256,
            "EXTENDED_ADVANCED_SCHEMA_OPTION_BINDING");
        if (grains.Any(value =>
                value.SchemaOptionBindingSha256 != schemaOptionBindingSha256))
            throw Fail("EXTENDED_ADVANCED_SCHEMA_OPTION_GRAIN_MISMATCH");
        RequireAdvancedOrder(grains, envelopes);
        foreach (var envelope in envelopes)
            RequireEnvelope(envelope);
        foreach (var grain in grains)
        {
            RequireAdvancedGrain(grain);
            var selected = envelopes.Where(value =>
                    value.Grain == grain.Grain &&
                    value.GrainKey == grain.GrainKey)
                .ToImmutableArray();
            var manifest = Hs(
                "P10_ACTUAL_EXTENDED_ADVANCED_GRAIN_ENVELOPE_MANIFEST_V2",
                selected.Select(value => value.EnvelopeSemanticSha256));
            if (selected.Length != grain.SourceEnvelopeCount ||
                manifest != grain.SourceEnvelopeManifestSha256)
                throw Fail(
                    "EXTENDED_ADVANCED_GRAIN_ENVELOPE_BINDING_INVALID");
        }
        var grainManifest = Hs(
            "P10_ACTUAL_EXTENDED_ADVANCED_GRAIN_MANIFEST_V2",
            grains.Select(value => value.GrainSemanticSha256));
        var envelopeManifest = Hs(
            "P10_ACTUAL_EXTENDED_ADVANCED_ENVELOPE_MANIFEST_V2",
            envelopes.Select(value => value.EnvelopeSemanticSha256));
        var atoms = grains.SelectMany(value => value.TypedAtoms)
            .ToImmutableArray();
        var atomManifest = AtomManifest(
            "P10_ACTUAL_EXTENDED_ADVANCED_ALL_TYPED_ATOM_MANIFEST_V2",
            atoms);
        var proof = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_ADVANCED_SOURCE_PROOF_V3",
            "APPLICABLE",
            "COMPLETE",
            StatisticReconciliationActualExtendedRawSourceFailures.None,
            configId,
            configVersionId,
            I(configVersionNo),
            I(configRevision),
            configSha256,
            summaryPlanBindingSha256,
            sourceScopeSemanticSha256,
            schemaOptionBindingSha256,
            grainManifest,
            I(grains.Length),
            envelopeManifest,
            I(envelopes.Length),
            atomManifest,
            I(atoms.Length));
        return new(
            true,
            true,
            StatisticReconciliationActualExtendedRawSourceFailures.None,
            configId,
            configVersionId,
            configVersionNo,
            configRevision,
            configSha256,
            summaryPlanBindingSha256,
            sourceScopeSemanticSha256,
            schemaOptionBindingSha256,
            grains,
            grainManifest,
            envelopes,
            envelopeManifest,
            atomManifest,
            atoms.Length,
            proof);
    }

    internal static StatisticReconciliationActualExtendedAdvancedSourceProof
        AdvancedNotApplicable(string summaryPlanBindingSha256)
    {
        summaryPlanBindingSha256 = Sha(
            summaryPlanBindingSha256, "EXTENDED_ADVANCED_PLAN_SHA");
        var code = StatisticReconciliationActualExtendedRawSourceFailures
            .LockedMetricFamilyNotApplicable;
        var configSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_ADVANCED_NOT_APPLICABLE_CONFIG_V1",
            summaryPlanBindingSha256);
        var scopeSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_ADVANCED_NOT_APPLICABLE_SCOPE_V1",
            summaryPlanBindingSha256);
        var schemaSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_ADVANCED_NOT_APPLICABLE_SCHEMA_V1",
            summaryPlanBindingSha256);
        var grains = ImmutableArray<
            StatisticReconciliationActualExtendedAdvancedGrainProof>.Empty;
        var envelopes = ImmutableArray<
            StatisticReconciliationActualExtendedRawSourceEnvelope>.Empty;
        var grainManifest = Hs(
            "P10_ACTUAL_EXTENDED_ADVANCED_GRAIN_MANIFEST_V2", []);
        var envelopeManifest = Hs(
            "P10_ACTUAL_EXTENDED_ADVANCED_ENVELOPE_MANIFEST_V2", []);
        var atomManifest = Hs(
            "P10_ACTUAL_EXTENDED_ADVANCED_ALL_TYPED_ATOM_MANIFEST_V2", []);
        var proof = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_ADVANCED_SOURCE_PROOF_V3",
            "NOT_APPLICABLE", "COMPLETE", code,
            "NOT_APPLICABLE", "NOT_APPLICABLE", I(0), I(0), configSha,
            summaryPlanBindingSha256, scopeSha, schemaSha,
            grainManifest, I(0), envelopeManifest, I(0), atomManifest, I(0));
        return new(
            false, true, code,
            "NOT_APPLICABLE", "NOT_APPLICABLE", 0, 0, configSha,
            summaryPlanBindingSha256, scopeSha, schemaSha,
            grains, grainManifest, envelopes, envelopeManifest,
            atomManifest, 0, proof);
    }

    internal static StatisticReconciliationActualExtendedDiffSideProof DiffSide(
        string side,
        string transitionLeg,
        string selectorSemanticSha256,
        string sourceScopeSemanticSha256,
        string periodSemanticSha256,
        string sourceAssignmentManifestSha256,
        int sourceAssignmentCount,
        ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
            projectionPins,
        ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
            capturedPins,
        ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
            envelopes,
        string descriptorManifestSha256,
        int descriptorCount,
        ImmutableArray<StatisticReconciliationActualRawSummaryAtom> typedAtoms)
    {
        side = Required(side, "EXTENDED_DIFF_SIDE");
        transitionLeg = Required(
            transitionLeg, "EXTENDED_DIFF_TRANSITION_LEG");
        if (side is not ("LEFT" or "RIGHT") ||
            transitionLeg is not ("BEFORE" or "AFTER") ||
            sourceAssignmentCount < 0 ||
            projectionPins.IsDefault || capturedPins.IsDefault ||
            envelopes.IsDefault || descriptorCount <= 0 ||
            typedAtoms.IsDefault || typedAtoms.Length == 0)
            throw Fail("EXTENDED_DIFF_SIDE_SHAPE_INVALID");
        selectorSemanticSha256 = Sha(
            selectorSemanticSha256, "EXTENDED_DIFF_SELECTOR_SHA");
        sourceScopeSemanticSha256 = Sha(
            sourceScopeSemanticSha256, "EXTENDED_DIFF_SCOPE_SHA");
        periodSemanticSha256 = Sha(
            periodSemanticSha256, "EXTENDED_DIFF_PERIOD_SHA");
        sourceAssignmentManifestSha256 = Sha(
            sourceAssignmentManifestSha256,
            "EXTENDED_DIFF_ASSIGNMENT_MANIFEST");
        descriptorManifestSha256 = Sha(
            descriptorManifestSha256,
            "EXTENDED_DIFF_DESCRIPTOR_MANIFEST");
        RequirePinOrder(side, projectionPins);
        RequirePinOrder(side, capturedPins);
        foreach (var pin in projectionPins)
            RequirePin(pin);
        foreach (var pin in capturedPins)
            RequirePin(pin);
        if (!projectionPins.SequenceEqual(capturedPins))
            throw Fail("EXTENDED_DIFF_PIN_BIJECTION_INVALID");
        RequireDiffEnvelopeOrder(side, envelopes);
        foreach (var envelope in envelopes)
            RequireEnvelope(envelope);
        if (envelopes.Length != projectionPins.Length)
            throw Fail("EXTENDED_DIFF_ENVELOPE_PIN_COUNT_MISMATCH");
        for (var index = 0; index < projectionPins.Length; index++)
        {
            var pin = projectionPins[index];
            var envelope = envelopes[index];
            if (envelope.Side != pin.Side ||
                envelope.ReportId != pin.SourceReportId ||
                envelope.PayloadRevision != pin.SourcePayloadRevision ||
                envelope.PayloadOwnerSha256 != pin.SourcePayloadSha256 ||
                envelope.LifecycleRevision != pin.SourceLifecycleRevision ||
                envelope.DirectRunId != pin.DirectRunId ||
                envelope.DirectGenerationId != pin.DirectGenerationId ||
                envelope.DirectGenerationSha256 !=
                pin.DirectGenerationSha256)
                throw Fail("EXTENDED_DIFF_ENVELOPE_PIN_MISMATCH");
        }
        var atoms = RequireAtoms(
            typedAtoms,
            "DIFF",
            [transitionLeg],
            null,
            null);
        var descriptors = DescriptorSemantics(atoms);
        if (descriptors.Length != descriptorCount ||
            Hs(
                "P10_ACTUAL_EXTENDED_DIFF_DESCRIPTOR_MANIFEST_V2",
                descriptors) != descriptorManifestSha256)
            throw Fail("EXTENDED_DIFF_SIDE_DESCRIPTOR_INVALID");
        var projectionManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_PROJECTION_PIN_MANIFEST_V2",
            projectionPins.Select(value => value.PinSemanticSha256));
        var capturedManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_CAPTURED_PIN_MANIFEST_V2",
            capturedPins.Select(value => value.PinSemanticSha256));
        var envelopeManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_SIDE_ENVELOPE_MANIFEST_V2",
            envelopes.Select(value => value.EnvelopeSemanticSha256));
        var atomManifest = AtomManifest(
            "P10_ACTUAL_EXTENDED_DIFF_SIDE_TYPED_ATOM_MANIFEST_V2", atoms);
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_SIDE_V2",
            side,
            transitionLeg,
            selectorSemanticSha256,
            sourceScopeSemanticSha256,
            periodSemanticSha256,
            sourceAssignmentManifestSha256,
            I(sourceAssignmentCount),
            projectionManifest,
            I(projectionPins.Length),
            capturedManifest,
            I(capturedPins.Length),
            envelopeManifest,
            I(envelopes.Length),
            descriptorManifestSha256,
            I(descriptorCount),
            atomManifest,
            I(atoms.Length));
        return new(
            side,
            transitionLeg,
            selectorSemanticSha256,
            sourceScopeSemanticSha256,
            periodSemanticSha256,
            sourceAssignmentManifestSha256,
            sourceAssignmentCount,
            projectionPins,
            projectionManifest,
            projectionPins.Length,
            capturedPins,
            capturedManifest,
            capturedPins.Length,
            envelopeManifest,
            envelopes.Length,
            descriptorManifestSha256,
            descriptorCount,
            atoms,
            atomManifest,
            atoms.Length,
            semantic);
    }

    internal static StatisticReconciliationActualExtendedDiffSourceProof Diff(
        string configId,
        string configVersionId,
        int configVersionNo,
        long configRevision,
        string configSha256,
        string summaryPlanBindingSha256,
        string direction,
        string periodBindingSha256,
        ImmutableArray<StatisticReconciliationActualExtendedDiffSideProof> sides,
        ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
            envelopes,
        ImmutableArray<
            StatisticReconciliationActualExtendedDiffSourcePairProof> sourcePairs,
        ImmutableArray<StatisticReconciliationActualRawSummaryAtom>
            transitionAtoms)
    {
        configId = Required(configId, "EXTENDED_DIFF_CONFIG_ID");
        configVersionId = Required(
            configVersionId, "EXTENDED_DIFF_CONFIG_VERSION_ID");
        if (configVersionNo <= 0 || configRevision <= 0 || sides.IsDefault ||
            sides.Length != 2 || envelopes.IsDefault ||
            sourcePairs.IsDefault || transitionAtoms.IsDefault ||
            sides[0].Side != "LEFT" || sides[1].Side != "RIGHT" ||
            sourcePairs.Length == 0 || transitionAtoms.Length == 0)
            throw Fail("EXTENDED_DIFF_PROOF_SHAPE_INVALID");
        configSha256 = Sha(configSha256, "EXTENDED_DIFF_CONFIG_SHA");
        summaryPlanBindingSha256 = Sha(
            summaryPlanBindingSha256, "EXTENDED_DIFF_PLAN_SHA");
        direction = Required(direction, "EXTENDED_DIFF_DIRECTION");
        periodBindingSha256 = Sha(
            periodBindingSha256, "EXTENDED_DIFF_PERIOD_BINDING_SHA");
        if (direction is not ("LEFT_TO_RIGHT" or "RIGHT_TO_LEFT") ||
            sides[0].TransitionLeg !=
                (direction == "LEFT_TO_RIGHT" ? "BEFORE" : "AFTER") ||
            sides[1].TransitionLeg !=
                (direction == "LEFT_TO_RIGHT" ? "AFTER" : "BEFORE"))
            throw Fail("EXTENDED_DIFF_DIRECTION_LEG_BINDING_INVALID");
        RequireGlobalDiffEnvelopeOrder(envelopes);
        foreach (var envelope in envelopes)
            RequireEnvelope(envelope);
        foreach (var side in sides)
            RequireDiffSide(
                side,
                envelopes.Where(value => value.Side == side.Side)
                    .ToImmutableArray());
        var sideEnvelopes = sides.SelectMany(side => envelopes
                .Where(value => value.Side == side.Side))
            .ToImmutableArray();
        if (!sideEnvelopes.SequenceEqual(envelopes) ||
            sides.Any(side =>
                side.EnvelopeManifestSha256 != Hs(
                    "P10_ACTUAL_EXTENDED_DIFF_SIDE_ENVELOPE_MANIFEST_V2",
                    envelopes.Where(value => value.Side == side.Side)
                        .Select(value => value.EnvelopeSemanticSha256))) ||
            sides[0].DescriptorManifestSha256 !=
                sides[1].DescriptorManifestSha256 ||
            sides[0].DescriptorCount != sides[1].DescriptorCount)
            throw Fail("EXTENDED_DIFF_SIDE_BINDING_INVALID");

        RequireSourcePairs(sourcePairs, sides, envelopes);
        var transitions = RequireAtoms(
            transitionAtoms,
            "DIFF",
            ["CHANGE_STATE", "DELTA"],
            null,
            null);
        var transitionDescriptors = DescriptorSemantics(transitions);
        if (transitionDescriptors.Length != sides[0].DescriptorCount ||
            Hs(
                "P10_ACTUAL_EXTENDED_DIFF_DESCRIPTOR_MANIFEST_V2",
                transitionDescriptors) != sides[0].DescriptorManifestSha256)
            throw Fail("EXTENDED_DIFF_TRANSITION_DESCRIPTOR_INVALID");
        var sideManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_SIDE_MANIFEST_V2",
            sides.Select(value => value.SideSemanticSha256));
        var envelopeManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_ENVELOPE_MANIFEST_V2",
            envelopes.Select(value => value.EnvelopeSemanticSha256));
        var pairManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_SOURCE_PAIR_MANIFEST_V2",
            sourcePairs.Select(value => value.PairSemanticSha256));
        var transitionManifest = AtomManifest(
            "P10_ACTUAL_EXTENDED_DIFF_TRANSITION_ATOM_MANIFEST_V2",
            transitions);
        var allAtoms = sides.SelectMany(value => value.TypedAtoms)
            .Concat(transitions)
            .ToImmutableArray();
        if (allAtoms.Select(AtomKey).Distinct(StringComparer.Ordinal).Count() !=
            allAtoms.Length)
            throw Fail("EXTENDED_DIFF_TYPED_ATOM_DUPLICATE");
        var typedManifest = AtomManifest(
            "P10_ACTUAL_EXTENDED_DIFF_ALL_TYPED_ATOM_MANIFEST_V2",
            allAtoms);
        var proof = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_SOURCE_PROOF_V3",
            "APPLICABLE",
            "COMPLETE",
            StatisticReconciliationActualExtendedRawSourceFailures.None,
            configId,
            configVersionId,
            I(configVersionNo),
            I(configRevision),
            configSha256,
            summaryPlanBindingSha256,
            direction,
            periodBindingSha256,
            sideManifest,
            I(sides.Length),
            envelopeManifest,
            I(envelopes.Length),
            pairManifest,
            I(sourcePairs.Length),
            transitionManifest,
            I(transitions.Length),
            typedManifest,
            I(allAtoms.Length));
        return new(
            true,
            true,
            StatisticReconciliationActualExtendedRawSourceFailures.None,
            configId,
            configVersionId,
            configVersionNo,
            configRevision,
            configSha256,
            summaryPlanBindingSha256,
            direction,
            periodBindingSha256,
            sides,
            sideManifest,
            envelopes,
            envelopeManifest,
            sourcePairs,
            pairManifest,
            sourcePairs.Length,
            transitions,
            transitionManifest,
            transitions.Length,
            typedManifest,
            allAtoms.Length,
            proof);
    }

    internal static StatisticReconciliationActualExtendedDiffSourceProof
        DiffNotApplicable(string summaryPlanBindingSha256)
    {
        summaryPlanBindingSha256 = Sha(
            summaryPlanBindingSha256, "EXTENDED_DIFF_PLAN_SHA");
        var code = StatisticReconciliationActualExtendedRawSourceFailures
            .LockedMetricFamilyNotApplicable;
        var configSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_NOT_APPLICABLE_CONFIG_V1",
            summaryPlanBindingSha256);
        var periodSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_NOT_APPLICABLE_PERIOD_V1",
            summaryPlanBindingSha256);
        var sides = ImmutableArray<
            StatisticReconciliationActualExtendedDiffSideProof>.Empty;
        var envelopes = ImmutableArray<
            StatisticReconciliationActualExtendedRawSourceEnvelope>.Empty;
        var pairs = ImmutableArray<
            StatisticReconciliationActualExtendedDiffSourcePairProof>.Empty;
        var transitions = ImmutableArray<
            StatisticReconciliationActualRawSummaryAtom>.Empty;
        var sideManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_SIDE_MANIFEST_V2", []);
        var envelopeManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_ENVELOPE_MANIFEST_V2", []);
        var pairManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_SOURCE_PAIR_MANIFEST_V2", []);
        var transitionManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_TRANSITION_ATOM_MANIFEST_V2", []);
        var typedManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_ALL_TYPED_ATOM_MANIFEST_V2", []);
        var proof = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_SOURCE_PROOF_V3",
            "NOT_APPLICABLE", "COMPLETE", code,
            "NOT_APPLICABLE", "NOT_APPLICABLE", I(0), I(0), configSha,
            summaryPlanBindingSha256, "NOT_APPLICABLE", periodSha,
            sideManifest, I(0), envelopeManifest, I(0), pairManifest, I(0),
            transitionManifest, I(0), typedManifest, I(0));
        return new(
            false, true, code,
            "NOT_APPLICABLE", "NOT_APPLICABLE", 0, 0, configSha,
            summaryPlanBindingSha256, "NOT_APPLICABLE", periodSha,
            sides, sideManifest, envelopes, envelopeManifest,
            pairs, pairManifest, 0, transitions, transitionManifest, 0,
            typedManifest, 0, proof);
    }

    internal static StatisticReconciliationActualExtendedRawSourceCollect Collect(
        string summaryPlanBindingSha256,
        StatisticReconciliationActualExtendedAdvancedSourceProof advanced,
        StatisticReconciliationActualExtendedDiffSourceProof diff)
    {
        summaryPlanBindingSha256 = Sha(
            summaryPlanBindingSha256, "EXTENDED_COLLECT_PLAN_SHA");
        RequireAdvanced(advanced);
        RequireDiff(diff);
        if (advanced.SummaryPlanBindingSha256 != summaryPlanBindingSha256 ||
            diff.SummaryPlanBindingSha256 != summaryPlanBindingSha256)
            throw Fail("EXTENDED_COLLECT_PLAN_BINDING_INVALID");
        var sha = StatisticReconciliationActualCanonical.Hash(
            StatisticReconciliationActualExtendedRawSourceSchemas.Collect,
            summaryPlanBindingSha256,
            advanced.Applicable ? "APPLICABLE" : "NOT_APPLICABLE",
            advanced.Complete ? "COMPLETE" : "INCOMPLETE",
            advanced.ProofCode,
            advanced.ProofSha256,
            advanced.GrainManifestSha256,
            I(advanced.Grains.Length),
            advanced.EnvelopeManifestSha256,
            I(advanced.Envelopes.Length),
            advanced.TypedAtomManifestSha256,
            I(advanced.TypedAtomCount),
            diff.Applicable ? "APPLICABLE" : "NOT_APPLICABLE",
            diff.Complete ? "COMPLETE" : "INCOMPLETE",
            diff.ProofCode,
            diff.ProofSha256,
            diff.SideManifestSha256,
            I(diff.Sides.Length),
            diff.EnvelopeManifestSha256,
            I(diff.Envelopes.Length),
            diff.SourcePairManifestSha256,
            I(diff.SourcePairCount),
            diff.TypedAtomManifestSha256,
            I(diff.TypedAtomCount));
        return new(
            StatisticReconciliationActualExtendedRawSourceSchemas.Collect,
            summaryPlanBindingSha256,
            advanced,
            diff,
            sha);
    }

    internal static void RequireCollect(
        StatisticReconciliationActualExtendedRawSourceCollect value)
    {
        if (value is null || value.SchemaVersion !=
            StatisticReconciliationActualExtendedRawSourceSchemas.Collect)
            throw Fail("EXTENDED_COLLECT_SCHEMA_INVALID");
        var expected = Collect(
            value.SummaryPlanBindingSha256,
            value.Advanced,
            value.Diff);
        if (expected.CollectSha256 != value.CollectSha256)
            throw Fail("EXTENDED_COLLECT_SHA_INVALID");
    }

    internal static void RequireResolution(
        StatisticReconciliationActualExtendedRawSourceResolution value)
    {
        if (value is null ||
            value.SchemaVersion !=
                StatisticReconciliationActualExtendedRawSourceSchemas.Resolution ||
            value.State !=
                StatisticReconciliationActualExtendedRawSourceSchemas.Complete ||
            value.FailureCode !=
                StatisticReconciliationActualExtendedRawSourceFailures.None ||
            value.RequiredPersistenceFields.IsDefault ||
            value.RequiredPersistenceFields.Length != 0 ||
            value.Advanced is null || value.Diff is null ||
            value.PartitionDoubleCollectCount < 0)
            throw Fail("EXTENDED_RESOLUTION_SHAPE_INVALID");
        var collect = Collect(
            value.SummaryPlanBindingSha256, value.Advanced, value.Diff);
        RequireCollect(collect);
        if (value.FirstCollectSha256 != collect.CollectSha256 ||
            value.SecondCollectSha256 != collect.CollectSha256)
            throw Fail("EXTENDED_RESOLUTION_COLLECT_BINDING_INVALID");
        var partitionProofs = ResolutionPartitionProofs(collect);
        var partitionManifest = Hs(
            "P10_ACTUAL_EXTENDED_RAW_SOURCE_PARTITION_DOUBLE_COLLECT_MANIFEST_V2",
            partitionProofs);
        if (value.PartitionDoubleCollectCount != partitionProofs.Length ||
            value.PartitionDoubleCollectManifestSha256 != partitionManifest)
            throw Fail("EXTENDED_RESOLUTION_PARTITION_BINDING_INVALID");
        var doubleCollect = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_RAW_SOURCE_DOUBLE_COLLECT_V2",
            value.SummaryPlanBindingSha256,
            collect.CollectSha256,
            collect.CollectSha256,
            partitionManifest,
            I(partitionProofs.Length));
        var proof = StatisticReconciliationActualCanonical.Hash(
            StatisticReconciliationActualExtendedRawSourceSchemas.Resolution,
            StatisticReconciliationActualExtendedRawSourceSchemas.Complete,
            StatisticReconciliationActualExtendedRawSourceFailures.None,
            value.SummaryPlanBindingSha256,
            value.Advanced.Applicable ? "APPLICABLE" : "NOT_APPLICABLE",
            value.Advanced.Complete ? "COMPLETE" : "INCOMPLETE",
            value.Advanced.ProofCode,
            value.Advanced.ProofSha256,
            value.Advanced.GrainManifestSha256,
            I(value.Advanced.Grains.Length),
            value.Advanced.EnvelopeManifestSha256,
            I(value.Advanced.Envelopes.Length),
            value.Advanced.TypedAtomManifestSha256,
            I(value.Advanced.TypedAtomCount),
            value.Diff.Applicable ? "APPLICABLE" : "NOT_APPLICABLE",
            value.Diff.Complete ? "COMPLETE" : "INCOMPLETE",
            value.Diff.ProofCode,
            value.Diff.ProofSha256,
            value.Diff.SideManifestSha256,
            I(value.Diff.Sides.Length),
            value.Diff.EnvelopeManifestSha256,
            I(value.Diff.Envelopes.Length),
            value.Diff.SourcePairManifestSha256,
            I(value.Diff.SourcePairCount),
            value.Diff.TypedAtomManifestSha256,
            I(value.Diff.TypedAtomCount),
            collect.CollectSha256,
            collect.CollectSha256,
            partitionManifest,
            I(partitionProofs.Length),
            doubleCollect);
        if (value.DoubleCollectProofSha256 != doubleCollect ||
            value.ProofSha256 != proof)
            throw Fail("EXTENDED_RESOLUTION_PROOF_INVALID");
    }

    private static ImmutableArray<string> ResolutionPartitionProofs(
        StatisticReconciliationActualExtendedRawSourceCollect collect)
    {
        var partitions = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        if (collect.Advanced.Applicable)
        {
            foreach (var grain in collect.Advanced.Grains)
            {
                var key =
                    $"ADVANCED/{grain.Grain}/{grain.GrainKey}/{grain.OwnerNodeId}";
                var semantic = StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_EXTENDED_ADVANCED_PARTITION_V2",
                    grain.GrainSemanticSha256,
                    grain.SourceEnvelopeManifestSha256,
                    I(grain.SourceEnvelopeCount),
                    grain.TypedAtomManifestSha256,
                    I(grain.TypedAtomCount));
                if (!partitions.TryAdd(key, semantic))
                    throw Fail("EXTENDED_RESOLUTION_PARTITION_DUPLICATE");
            }
        }
        if (collect.Diff.Applicable)
        {
            foreach (var side in collect.Diff.Sides)
            {
                var key = $"DIFF/{side.Side}";
                var semantic = StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_EXTENDED_DIFF_SIDE_PARTITION_V2",
                    side.SideSemanticSha256,
                    side.ProjectionPinManifestSha256,
                    side.EnvelopeManifestSha256,
                    side.TypedAtomManifestSha256,
                    I(side.TypedAtomCount));
                if (!partitions.TryAdd(key, semantic))
                    throw Fail("EXTENDED_RESOLUTION_PARTITION_DUPLICATE");
            }
            if (!partitions.TryAdd(
                    "DIFF/TRANSITION",
                    StatisticReconciliationActualCanonical.Hash(
                        "P10_ACTUAL_EXTENDED_DIFF_TRANSITION_PARTITION_V2",
                        collect.Diff.SourcePairManifestSha256,
                        I(collect.Diff.SourcePairCount),
                        collect.Diff.TransitionAtomManifestSha256,
                        I(collect.Diff.TransitionAtomCount))))
                throw Fail("EXTENDED_RESOLUTION_PARTITION_DUPLICATE");
        }
        return partitions.Select(value =>
                StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_EXTENDED_RAW_SOURCE_PARTITION_DOUBLE_COLLECT_V2",
                    value.Key,
                    value.Value,
                    value.Value))
            .ToImmutableArray();
    }
    private static void RequireAdvanced(
        StatisticReconciliationActualExtendedAdvancedSourceProof value)
    {
        if (value is null)
            throw Fail("EXTENDED_ADVANCED_PROOF_NULL");
        var expected = value.Applicable
            ? Advanced(
                value.ConfigId,
                value.ConfigVersionId,
                value.ConfigVersionNo,
                value.ConfigRevision,
                value.ConfigSha256,
                value.SummaryPlanBindingSha256,
                value.SourceScopeSemanticSha256,
                value.SchemaOptionBindingSha256,
                value.Grains,
                value.Envelopes)
            : AdvancedNotApplicable(value.SummaryPlanBindingSha256);
        if (!value.Complete ||
            value.ProofCode != (value.Applicable
                ? StatisticReconciliationActualExtendedRawSourceFailures.None
                : StatisticReconciliationActualExtendedRawSourceFailures
                    .LockedMetricFamilyNotApplicable) ||
            expected.Applicable != value.Applicable ||
            expected.Complete != value.Complete ||
            expected.ProofCode != value.ProofCode ||
            expected.ConfigId != value.ConfigId ||
            expected.ConfigVersionId != value.ConfigVersionId ||
            expected.ConfigVersionNo != value.ConfigVersionNo ||
            expected.ConfigRevision != value.ConfigRevision ||
            expected.ConfigSha256 != value.ConfigSha256 ||
            expected.SourceScopeSemanticSha256 !=
                value.SourceScopeSemanticSha256 ||
            expected.SchemaOptionBindingSha256 !=
                value.SchemaOptionBindingSha256 ||
            expected.GrainManifestSha256 != value.GrainManifestSha256 ||
            expected.Grains.Length != value.Grains.Length ||
            expected.EnvelopeManifestSha256 != value.EnvelopeManifestSha256 ||
            expected.Envelopes.Length != value.Envelopes.Length ||
            expected.TypedAtomManifestSha256 !=
                value.TypedAtomManifestSha256 ||
            expected.TypedAtomCount != value.TypedAtomCount ||
            expected.ProofSha256 != value.ProofSha256)
            throw Fail("EXTENDED_ADVANCED_PROOF_INTEGRITY_INVALID");
    }

    private static void RequireDiff(
        StatisticReconciliationActualExtendedDiffSourceProof value)
    {
        if (value is null)
            throw Fail("EXTENDED_DIFF_PROOF_NULL");
        var expected = value.Applicable
            ? Diff(
                value.ConfigId,
                value.ConfigVersionId,
                value.ConfigVersionNo,
                value.ConfigRevision,
                value.ConfigSha256,
                value.SummaryPlanBindingSha256,
                value.Direction,
                value.PeriodBindingSha256,
                value.Sides,
                value.Envelopes,
                value.SourcePairs,
                value.TransitionAtoms)
            : DiffNotApplicable(value.SummaryPlanBindingSha256);
        if (!value.Complete ||
            value.ProofCode != (value.Applicable
                ? StatisticReconciliationActualExtendedRawSourceFailures.None
                : StatisticReconciliationActualExtendedRawSourceFailures
                    .LockedMetricFamilyNotApplicable) ||
            expected.Applicable != value.Applicable ||
            expected.Complete != value.Complete ||
            expected.ProofCode != value.ProofCode ||
            expected.ConfigId != value.ConfigId ||
            expected.ConfigVersionId != value.ConfigVersionId ||
            expected.ConfigVersionNo != value.ConfigVersionNo ||
            expected.ConfigRevision != value.ConfigRevision ||
            expected.ConfigSha256 != value.ConfigSha256 ||
            expected.Direction != value.Direction ||
            expected.PeriodBindingSha256 != value.PeriodBindingSha256 ||
            expected.SideManifestSha256 != value.SideManifestSha256 ||
            expected.Sides.Length != value.Sides.Length ||
            expected.EnvelopeManifestSha256 != value.EnvelopeManifestSha256 ||
            expected.Envelopes.Length != value.Envelopes.Length ||
            expected.SourcePairManifestSha256 !=
                value.SourcePairManifestSha256 ||
            expected.SourcePairCount != value.SourcePairCount ||
            expected.TransitionAtomManifestSha256 !=
                value.TransitionAtomManifestSha256 ||
            expected.TransitionAtomCount != value.TransitionAtomCount ||
            expected.TypedAtomManifestSha256 !=
                value.TypedAtomManifestSha256 ||
            expected.TypedAtomCount != value.TypedAtomCount ||
            expected.ProofSha256 != value.ProofSha256)
            throw Fail("EXTENDED_DIFF_PROOF_INTEGRITY_INVALID");
    }
    private static void RequireEnvelope(
        StatisticReconciliationActualExtendedRawSourceEnvelope value)
    {
        if (value is null)
            throw Fail("EXTENDED_ENVELOPE_NULL");
        var expected = Envelope(
            value.Family,
            value.Side,
            value.Grain,
            value.GrainKey,
            value.SourceOrdinal,
            value.WorkId,
            value.WorkAssignmentId,
            value.ReportId,
            value.PayloadDocumentId,
            value.PayloadRevision,
            value.PayloadOwnerSha256,
            value.CanonicalPayloadJson,
            value.LifecycleRevision,
            value.LifecycleSha256,
            value.DirectRunId,
            value.DirectGenerationId,
            value.DirectGenerationSha256);
        if (expected != value)
            throw Fail("EXTENDED_ENVELOPE_INTEGRITY_INVALID");
    }

    private static void RequirePin(
        StatisticReconciliationActualExtendedDiffSourcePin value)
    {
        if (value is null)
            throw Fail("EXTENDED_DIFF_PIN_NULL");
        var expected = Pin(
            value.Side,
            value.SourceReportId,
            value.SourcePayloadRevision,
            value.SourcePayloadSha256,
            value.SourceLifecycleRevision,
            value.DirectRunId,
            value.DirectGenerationId,
            value.DirectGenerationSha256);
        if (expected != value)
            throw Fail("EXTENDED_DIFF_PIN_INTEGRITY_INVALID");
    }

    private static void RequireAdvancedGrain(
        StatisticReconciliationActualExtendedAdvancedGrainProof value)
    {
        if (value is null)
            throw Fail("EXTENDED_ADVANCED_GRAIN_NULL");
        var expected = AdvancedGrain(
            value.OwnerNodeId,
            value.Grain,
            value.GrainKey,
            value.WindowStartUtc,
            value.WindowEndExclusiveUtc,
            value.SourceAssignmentManifestSha256,
            value.SourceAssignmentCount,
            value.SourceEnvelopeManifestSha256,
            value.SourceEnvelopeCount,
            new(
                value.DescriptorManifestSha256,
                value.DescriptorCount,
                value.SchemaOptionBindingSha256,
                value.DescriptorProjections,
                value.DescriptorProjectionManifestSha256,
                value.DescriptorProjectionCount,
                value.DescriptorContributions,
                value.DescriptorContributionManifestSha256,
                value.TypedAtoms,
                value.TypedAtomManifestSha256));
        if (expected != value)
            throw Fail("EXTENDED_ADVANCED_GRAIN_INTEGRITY_INVALID");
    }

    private static void RequireDiffSide(
        StatisticReconciliationActualExtendedDiffSideProof value,
        ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
            envelopes)
    {
        if (value is null)
            throw Fail("EXTENDED_DIFF_SIDE_NULL");
        var expected = DiffSide(
            value.Side,
            value.TransitionLeg,
            value.SelectorSemanticSha256,
            value.SourceScopeSemanticSha256,
            value.PeriodSemanticSha256,
            value.SourceAssignmentManifestSha256,
            value.SourceAssignmentCount,
            value.ProjectionPins,
            value.CapturedPins,
            envelopes,
            value.DescriptorManifestSha256,
            value.DescriptorCount,
            value.TypedAtoms);
        if (expected.ProjectionPinManifestSha256 !=
                value.ProjectionPinManifestSha256 ||
            expected.ProjectionPinCount != value.ProjectionPinCount ||
            expected.CapturedPinManifestSha256 !=
                value.CapturedPinManifestSha256 ||
            expected.CapturedPinCount != value.CapturedPinCount ||
            expected.EnvelopeManifestSha256 != value.EnvelopeManifestSha256 ||
            expected.EnvelopeCount != value.EnvelopeCount ||
            expected.TypedAtomManifestSha256 !=
                value.TypedAtomManifestSha256 ||
            expected.TypedAtomCount != value.TypedAtomCount ||
            expected.SideSemanticSha256 != value.SideSemanticSha256)
            throw Fail("EXTENDED_DIFF_SIDE_INTEGRITY_INVALID");
    }

    private static ImmutableArray<
        StatisticReconciliationActualExtendedAdvancedDescriptorContributionProof>
        RequireAdvancedContributions(
            ImmutableArray<
                StatisticReconciliationActualExtendedAdvancedDescriptorContributionProof>
                values,
            ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms)
    {
        if (values.IsDefault || !values.SequenceEqual(values.OrderBy(
                value => value.IdentitySha256, StringComparer.Ordinal)) ||
            values.Select(value => value.IdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw Fail("EXTENDED_ADVANCED_CONTRIBUTION_ORDER_INVALID");
        var descriptors = atoms
            .GroupBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var semantics = group.Select(value =>
                            value.DescriptorSemanticSha256)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    return semantics.Length == 1
                        ? semantics[0]
                        : throw Fail(
                            "EXTENDED_ADVANCED_CONTRIBUTION_DESCRIPTOR_INVALID");
                },
                StringComparer.Ordinal);
        if (values.Length != descriptors.Count)
            throw Fail("EXTENDED_ADVANCED_CONTRIBUTION_CARDINALITY_INVALID");
        foreach (var value in values)
        {
            if (value is null || value.ValueSourceReportCount < 0 ||
                !descriptors.TryGetValue(
                    Sha(value.IdentitySha256,
                        "EXTENDED_ADVANCED_CONTRIBUTION_IDENTITY_SHA"),
                    out var descriptor) ||
                descriptor != Sha(
                    value.DescriptorSemanticSha256,
                    "EXTENDED_ADVANCED_CONTRIBUTION_DESCRIPTOR_SHA"))
                throw Fail(
                    "EXTENDED_ADVANCED_CONTRIBUTION_DESCRIPTOR_INVALID");
            var manifest = Sha(
                value.ValueSourceManifestSha256,
                "EXTENDED_ADVANCED_VALUE_SOURCE_MANIFEST_SHA");
            var semantic = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_CONTRIBUTION_V2",
                descriptor,
                value.IdentitySha256,
                manifest,
                I(value.ValueSourceReportCount));
            if (semantic != Sha(
                    value.ContributionSemanticSha256,
                    "EXTENDED_ADVANCED_CONTRIBUTION_SEMANTIC_SHA"))
                throw Fail("EXTENDED_ADVANCED_CONTRIBUTION_SEMANTIC_INVALID");
        }
        return values;
    }

    private static void RequireSourcePairs(
        ImmutableArray<
            StatisticReconciliationActualExtendedDiffSourcePairProof> pairs,
        ImmutableArray<StatisticReconciliationActualExtendedDiffSideProof> sides,
        ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
            envelopes)
    {
        if (!pairs.SequenceEqual(pairs.OrderBy(
                value => value.IdentitySha256, StringComparer.Ordinal)) ||
            pairs.Select(value => value.IdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != pairs.Length)
            throw Fail("EXTENDED_DIFF_SOURCE_PAIR_ORDER_INVALID");
        var leftPins = sides[0].ProjectionPins;
        var rightPins = sides[1].ProjectionPins;
        var leftEnvelopes = envelopes.Where(value => value.Side == "LEFT")
            .ToImmutableArray();
        var rightEnvelopes = envelopes.Where(value => value.Side == "RIGHT")
            .ToImmutableArray();
        var leftPinManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_PAIR_LEFT_PINS_V2",
            leftPins.Select(value => value.PinSemanticSha256));
        var rightPinManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_PAIR_RIGHT_PINS_V2",
            rightPins.Select(value => value.PinSemanticSha256));
        var leftEnvelopeManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_PAIR_LEFT_ENVELOPES_V2",
            leftEnvelopes.Select(value => value.EnvelopeSemanticSha256));
        var rightEnvelopeManifest = Hs(
            "P10_ACTUAL_EXTENDED_DIFF_PAIR_RIGHT_ENVELOPES_V2",
            rightEnvelopes.Select(value => value.EnvelopeSemanticSha256));
        var descriptors = sides[0].TypedAtoms
            .GroupBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var semantics = group.Select(value =>
                            value.DescriptorSemanticSha256)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    return semantics.Length == 1
                        ? semantics[0]
                        : throw Fail(
                            "EXTENDED_DIFF_SOURCE_PAIR_DESCRIPTOR_INVALID");
                },
                StringComparer.Ordinal);
        if (pairs.Length != sides[0].DescriptorCount ||
            pairs.Length != descriptors.Count)
            throw Fail("EXTENDED_DIFF_SOURCE_PAIR_CARDINALITY_INVALID");

        bool Missing(
            StatisticReconciliationActualExtendedDiffSideProof side,
            string identity)
        {
            var atoms = side.TypedAtoms.Where(value =>
                    value.IdentitySha256 == identity)
                .ToArray();
            long CountOf(string kind)
            {
                var values = atoms.Where(value =>
                        value.AtomKind == kind &&
                        value.TransitionLeg == side.TransitionLeg)
                    .ToArray();
                if (values.Length != 1 ||
                    !long.TryParse(
                        values[0].CanonicalValue,
                        out var result) ||
                    result < 0)
                    throw Fail(
                        "EXTENDED_DIFF_SOURCE_PAIR_STATE_ATOM_INVALID");
                return result;
            }
            var reports = CountOf("REPORT_COUNT");
            var values = CountOf("COUNT");
            var missing = CountOf("MISSING");
            return reports == 0 || values == 0 && missing == reports;
        }

        foreach (var pair in pairs)
        {
            var identity = Sha(
                pair.IdentitySha256,
                "EXTENDED_DIFF_PAIR_IDENTITY_SHA");
            if (!descriptors.TryGetValue(identity, out var descriptor) ||
                descriptor != Sha(
                    pair.DescriptorSemanticSha256,
                    "EXTENDED_DIFF_PAIR_DESCRIPTOR_SHA") ||
                pair.LeftPinCount != leftPins.Length ||
                pair.LeftEnvelopeCount != leftEnvelopes.Length ||
                pair.RightPinCount != rightPins.Length ||
                pair.RightEnvelopeCount != rightEnvelopes.Length ||
                pair.LeftPinManifestSha256 != leftPinManifest ||
                pair.LeftEnvelopeManifestSha256 != leftEnvelopeManifest ||
                pair.RightPinManifestSha256 != rightPinManifest ||
                pair.RightEnvelopeManifestSha256 != rightEnvelopeManifest)
                throw Fail("EXTENDED_DIFF_SOURCE_PAIR_BINDING_INVALID");
            var leftMissing = Missing(sides[0], identity);
            var rightMissing = Missing(sides[1], identity);
            var relationKind = leftMissing
                ? rightMissing ? "NEITHER" : "RIGHT_ONLY"
                : rightMissing ? "LEFT_ONLY" : "BOTH";
            if (relationKind == "NEITHER" ||
                pair.RelationKind != relationKind)
                throw Fail("EXTENDED_DIFF_SOURCE_PAIR_RELATION_INVALID");
            var semantic = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_EXTENDED_DIFF_SOURCE_PAIR_V2",
                descriptor,
                identity,
                relationKind,
                leftPinManifest,
                I(leftPins.Length),
                leftEnvelopeManifest,
                I(leftEnvelopes.Length),
                rightPinManifest,
                I(rightPins.Length),
                rightEnvelopeManifest,
                I(rightEnvelopes.Length));
            if (semantic != Sha(
                    pair.PairSemanticSha256,
                    "EXTENDED_DIFF_PAIR_SEMANTIC_SHA"))
                throw Fail("EXTENDED_DIFF_SOURCE_PAIR_SEMANTIC_INVALID");
        }
    }

    private static ImmutableArray<StatisticReconciliationActualRawSummaryAtom>
        RequireAtoms(
            ImmutableArray<StatisticReconciliationActualRawSummaryAtom> values,
            string family,
            ImmutableArray<string> allowedLegs,
            string? advancedGrain,
            string? periodKey)
    {
        if (values.IsDefault || values.Length == 0 ||
            !values.SequenceEqual(values
                .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
                .ThenBy(value => value.TransitionLeg, StringComparer.Ordinal)
                .ThenBy(value => value.AtomKind, StringComparer.Ordinal)
                .ThenBy(value => value.CanonicalValue,
                    StringComparer.Ordinal)) ||
            values.Select(AtomKey).Distinct(StringComparer.Ordinal).Count() !=
                values.Length)
            throw Fail("EXTENDED_TYPED_ATOM_ORDER_INVALID");
        foreach (var value in values)
        {
            if (value is null || value.Family != family ||
                !allowedLegs.Contains(
                    value.TransitionLeg, StringComparer.Ordinal) ||
                advancedGrain is not null &&
                value.AdvancedGrain != advancedGrain ||
                periodKey is not null && value.PeriodKey != periodKey ||
                value.DecimalScale < 0 || value.OccurrenceCount < 0 ||
                value.ReportCount < 0 || value.RowCount < 0 ||
                value.NumericValueCount < 0)
                throw Fail("EXTENDED_TYPED_ATOM_SHAPE_INVALID");
            var expected = StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_RAW_SUMMARY_ATOM_V1",
                Sha(
                    value.DescriptorSemanticSha256,
                    "EXTENDED_ATOM_DESCRIPTOR_SHA"),
                Sha(value.IdentitySha256, "EXTENDED_ATOM_IDENTITY_SHA"),
                Required(
                    value.TransitionLeg, "EXTENDED_ATOM_TRANSITION_LEG"),
                value.TransitionKind ?? "~",
                value.CollectionSemantics ?? "~",
                Required(value.AtomKind, "EXTENDED_ATOM_KIND"),
                Required(value.ValueType, "EXTENDED_ATOM_VALUE_TYPE"),
                Required(value.ValueState, "EXTENDED_ATOM_VALUE_STATE"),
                value.CanonicalValue ??
                    throw Fail("EXTENDED_ATOM_CANONICAL_VALUE_NULL"),
                I(value.DecimalScale),
                I(value.OccurrenceCount),
                I(value.ReportCount),
                I(value.RowCount),
                I(value.NumericValueCount));
            if (Sha(
                    value.AtomSemanticSha256,
                    "EXTENDED_ATOM_SEMANTIC_SHA") != expected)
                throw Fail("EXTENDED_TYPED_ATOM_SEMANTIC_INVALID");
        }
        return values;
    }

    private static ImmutableArray<string> DescriptorSemantics(
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms)
        => atoms
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .Select(value => value.DescriptorSemanticSha256)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

    private static string AtomManifest(
        string domain,
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms)
        => Hs(domain, atoms.Select(value => value.AtomSemanticSha256));

    private static string AtomKey(
        StatisticReconciliationActualRawSummaryAtom value)
        => string.Join(
            '',
            value.IdentitySha256,
            value.TransitionLeg,
            value.AtomKind,
            value.ValueType,
            value.ValueState,
            value.CanonicalValue);

    private static void RequireAdvancedOrder(
        ImmutableArray<
            StatisticReconciliationActualExtendedAdvancedGrainProof> grains,
        ImmutableArray<
            StatisticReconciliationActualExtendedRawSourceEnvelope> envelopes)
    {
        if (!grains.SequenceEqual(grains
                .OrderBy(value => GrainOrdinal(value.Grain))
                .ThenBy(value => value.GrainKey, StringComparer.Ordinal)
                .ThenBy(value => value.OwnerNodeId, StringComparer.Ordinal)) ||
            grains.Select(value => $"{value.Grain}\0{value.GrainKey}")
                .Distinct(StringComparer.Ordinal).Count() != grains.Length ||
            !envelopes.SequenceEqual(envelopes
                .OrderBy(value => GrainOrdinal(value.Grain!))
                .ThenBy(value => value.GrainKey, StringComparer.Ordinal)
                .ThenBy(value => value.WorkAssignmentId,
                    StringComparer.Ordinal)
                .ThenBy(value => value.ReportId, StringComparer.Ordinal)) ||
            !envelopes.Select((value, index) =>
                    value.SourceOrdinal == index)
                .All(value => value))
            throw Fail("EXTENDED_ADVANCED_ORDER_INVALID");
    }

    private static void RequirePinOrder(
        string side,
        ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin> pins)
    {
        if (pins.Any(value => value.Side != side) ||
            !pins.SequenceEqual(pins
                .OrderBy(value => value.SourceReportId, StringComparer.Ordinal)
                .ThenBy(value => value.DirectRunId, StringComparer.Ordinal)
                .ThenBy(value => value.DirectGenerationId,
                    StringComparer.Ordinal)) ||
            pins.Select(value =>
                    $"{value.Side}\0{value.SourceReportId}\0{value.DirectRunId}\0{value.DirectGenerationId}")
                .Distinct(StringComparer.Ordinal).Count() != pins.Length)
            throw Fail("EXTENDED_DIFF_PIN_ORDER_INVALID");
    }

    private static void RequireDiffEnvelopeOrder(
        string side,
        ImmutableArray<
            StatisticReconciliationActualExtendedRawSourceEnvelope> envelopes)
    {
        if (envelopes.Any(value => value.Side != side) ||
            !envelopes.SequenceEqual(envelopes
                .OrderBy(value => value.ReportId, StringComparer.Ordinal)
                .ThenBy(value => value.DirectRunId, StringComparer.Ordinal)
                .ThenBy(value => value.DirectGenerationId,
                    StringComparer.Ordinal)))
            throw Fail("EXTENDED_DIFF_ENVELOPE_ORDER_INVALID");
    }

    private static void RequireGlobalDiffEnvelopeOrder(
        ImmutableArray<
            StatisticReconciliationActualExtendedRawSourceEnvelope> envelopes)
    {
        if (!envelopes.SequenceEqual(envelopes
                .OrderBy(value => value.Side == "LEFT" ? 0 : 1)
                .ThenBy(value => value.ReportId, StringComparer.Ordinal)
                .ThenBy(value => value.DirectRunId, StringComparer.Ordinal)
                .ThenBy(value => value.DirectGenerationId,
                    StringComparer.Ordinal)) ||
            !envelopes.Select((value, index) =>
                    value.SourceOrdinal == index)
                .All(value => value))
            throw Fail("EXTENDED_DIFF_GLOBAL_ENVELOPE_ORDER_INVALID");
    }

    private static int GrainOrdinal(string grain) => grain switch
    {
        "DAY" => 0,
        "MONTH" => 1,
        "YEAR" => 2,
        _ => 3
    };

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);

    private static string? Optional(string? value, string name)
        => StatisticReconciliationActualCanonical.Optional(value, name);

    private static string Sha(string? value, string name)
    {
        var normalized = StatisticReconciliationActualCanonical.Sha256(
            value, name);
        if (normalized != value)
            throw Fail($"{name}_NOT_LOWER_SHA256");
        return normalized;
    }

    private static string Hs(string domain, IEnumerable<string> values)
        => StatisticReconciliationActualCanonical.HashSequence(domain, values);

    private static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);

    private static StatisticReconciliationActualObservationException Fail(
        string reason) => new($"ACTUAL_EXTENDED_RAW_SOURCE:{reason}");
}
