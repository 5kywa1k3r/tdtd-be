using System.Collections.Immutable;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class ExtendedOwnerParityCases
{
    private const string LeftPeriod =
        "{\"mode\":\"EXACT\",\"periodKey\":\"2026-07\"}";
    private const string RightPeriod =
        "{\"mode\":\"EXACT\",\"periodKey\":\"2026-08\"}";

    internal static async Task<int> RunAsync()
    {
        var advancedDescriptor = TestSupport.AdvancedDescriptor(
            "DAY",
            "2026-08-10",
            "choice",
            "choice",
            "BUCKET_COUNT",
            "ENUM",
            ["VALUES"]);
        var diffDescriptor = TestSupport.DiffDescriptor(
            "FIELD", "CHOICE", "CHANGED");
        var plan = TestSupport.Plan(advancedDescriptor, diffDescriptor);
        var projection = TestSupport.Projection(
            advancedDescriptor,
            "choice",
            "singleSelect",
            ("A", "Alpha"));
        var advancedEnvelope = TestSupport.AdvancedEnvelope(
            0,
            "DAY",
            "2026-08-10",
            "advanced-report",
            "{\"values\":{\"choice\":\"A\"}}" );
        var typedAdvanced = TestSupport.Compiler().CompileAdvanced(
            plan,
            "DAY",
            "2026-08-10",
            [advancedEnvelope],
            [projection],
            TestSupport.SchemaBinding);
        var advancedManifest = TestSupport.Hs(
            "P10_ACTUAL_EXTENDED_ADVANCED_GRAIN_ENVELOPE_MANIFEST_V2",
            [advancedEnvelope.EnvelopeSemanticSha256]);
        var start = new DateTime(
            2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        var grain =
            StatisticReconciliationActualExtendedRawSourceIntegrity
                .AdvancedGrain(
                    "advanced-node",
                    "DAY",
                    "2026-08-10",
                    start,
                    start.AddDays(1),
                    TestSupport.Sha("advanced-assignment-manifest"),
                    1,
                    advancedManifest,
                    1,
                    typedAdvanced);
        var advancedSource =
            StatisticReconciliationActualExtendedRawSourceIntegrity.Advanced(
                "advanced-config",
                "advanced-version",
                1,
                1,
                TestSupport.Sha("advanced-config"),
                plan.SemanticSha256,
                TestSupport.Sha("advanced-scope"),
                TestSupport.SchemaBinding,
                [grain],
                [advancedEnvelope]);
        var advancedOwner = AdvancedOwner(advancedDescriptor, start);

        var left = TestSupport.DiffSide("LEFT", "[\"A\",\"B\"]");
        var right = TestSupport.DiffSideAt(
            left.Envelopes.Length, "RIGHT", "[\"A\",\"C\"]");
        var typedDiff = TestSupport.Compiler().CompileDiff(
            plan,
            "LEFT_TO_RIGHT",
            "FIELD",
            "CHOICE",
            LeftPeriod,
            "FIELD",
            "CHOICE",
            RightPeriod,
            left.Pins,
            left.Envelopes,
            right.Pins,
            right.Envelopes);
        var sides = ImmutableArray.Create(
            StatisticReconciliationActualExtendedRawSourceIntegrity.DiffSide(
                "LEFT",
                typedDiff.LeftTransitionLeg,
                TestSupport.Sha("left-selector"),
                TestSupport.Sha("left-scope"),
                TestSupport.Sha("left-period"),
                TestSupport.Sha("left-assignments"),
                1,
                left.Pins,
                left.Pins,
                left.Envelopes,
                typedDiff.DescriptorManifestSha256,
                typedDiff.DescriptorCount,
                typedDiff.LeftAtoms),
            StatisticReconciliationActualExtendedRawSourceIntegrity.DiffSide(
                "RIGHT",
                typedDiff.RightTransitionLeg,
                TestSupport.Sha("right-selector"),
                TestSupport.Sha("right-scope"),
                TestSupport.Sha("right-period"),
                TestSupport.Sha("right-assignments"),
                1,
                right.Pins,
                right.Pins,
                right.Envelopes,
                typedDiff.DescriptorManifestSha256,
                typedDiff.DescriptorCount,
                typedDiff.RightAtoms));
        var allDiffEnvelopes = left.Envelopes.Concat(right.Envelopes)
            .ToImmutableArray();
        var diffSource =
            StatisticReconciliationActualExtendedRawSourceIntegrity.Diff(
                "diff-config",
                "diff-version",
                1,
                1,
                TestSupport.Sha("diff-config"),
                plan.SemanticSha256,
                "LEFT_TO_RIGHT",
                typedDiff.PeriodBindingSha256,
                sides,
                allDiffEnvelopes,
                typedDiff.SourcePairs,
                typedDiff.TransitionAtoms);
        var diffOwner = DiffOwner(diffDescriptor, left.Pins, right.Pins);
        var collect =
            StatisticReconciliationActualExtendedRawSourceIntegrity.Collect(
                plan.SemanticSha256,
                advancedSource,
                diffSource);
        var reader = new StaticCollectReader(collect);
        var owner = new StatisticReconciliationActualExtendedRawSourceOwner(
            reader);
        var material = new StatisticReconciliationActualTrustedCaptureMaterial(
            null!,
            "TEST_BOUNDARY",
            null!,
            null!,
            null!,
            null!,
            advancedOwner.Boundary,
            diffOwner.Boundary,
            null!,
            null!,
            null!);
        var resolution = await owner.ResolveAsync(
            new StatisticReconciliationActualExtendedRawSourceCommand(
                StatisticReconciliationActualExtendedRawSourceSchemas.Command,
                material,
                plan,
                advancedOwner,
                diffOwner));
        TestSupport.Equal(
            StatisticReconciliationActualExtendedRawSourceSchemas.Complete,
            resolution.State,
            "P10-EXT-OWNER-01 double-collect-resolution");
        TestSupport.Equal(2, reader.ReadCount,
            "P10-EXT-OWNER-02 independent-double-collect");
        var parity =
            new StatisticReconciliationActualExtendedRawSourceOwnerParity()
                .Prove(new(
                    plan,
                    resolution,
                    advancedOwner,
                    diffOwner));
        TestSupport.Equal(
            StatisticReconciliationActualExtendedRawSourceSchemas.Complete,
            parity.State,
            "P10-EXT-OWNER-03 real-owner-parity-prove");
        TestSupport.Equal(2, parity.Families.Length,
            "P10-EXT-OWNER-04 both-families-bound");

        var forged = resolution with
        {
            Advanced = resolution.Advanced! with
            {
                SchemaOptionBindingSha256 = OneBit(
                    resolution.Advanced.SchemaOptionBindingSha256)
            }
        };
        var rejected =
            new StatisticReconciliationActualExtendedRawSourceOwnerParity()
                .Prove(new(
                    plan,
                    forged,
                    advancedOwner,
                    diffOwner));
        TestSupport.Equal(
            StatisticReconciliationActualExtendedRawSourceSchemas.Incomplete,
            rejected.State,
            "P10-EXT-OWNER-05 schema-binding-one-bit-rejected");
        // A locked plan with no ADVANCED or DIFF descriptors still carries the
        // concrete capture targets/material above. Only the downstream
        // extended proof slots are marked not applicable.
        var nonExtendedPlan = TestSupport.Plan(
            TestSupport.DirectDescriptor());
        var advancedNotApplicable =
            StatisticReconciliationActualExtendedRawSourceIntegrity
                .AdvancedNotApplicable(nonExtendedPlan.SemanticSha256);
        var diffNotApplicable =
            StatisticReconciliationActualExtendedRawSourceIntegrity
                .DiffNotApplicable(nonExtendedPlan.SemanticSha256);
        var notApplicableCollect =
            StatisticReconciliationActualExtendedRawSourceIntegrity.Collect(
                nonExtendedPlan.SemanticSha256,
                advancedNotApplicable,
                diffNotApplicable);
        var notApplicableReader = new StaticCollectReader(notApplicableCollect);
        var notApplicableOwner =
            new StatisticReconciliationActualExtendedRawSourceOwner(
                notApplicableReader);
        var notApplicableResolution = await notApplicableOwner.ResolveAsync(
            new StatisticReconciliationActualExtendedRawSourceCommand(
                StatisticReconciliationActualExtendedRawSourceSchemas.Command,
                material,
                nonExtendedPlan,
                advancedOwner,
                diffOwner));
        TestSupport.Equal(
            StatisticReconciliationActualExtendedRawSourceSchemas.Complete,
            notApplicableResolution.State,
            "P10-EXT-OWNER-06 fixed-slot-resolution-complete");
        TestSupport.Equal(0,
            notApplicableResolution.PartitionDoubleCollectCount,
            "P10-EXT-OWNER-07 nonapplicable-partitions-empty");
        var notApplicableParity =
            new StatisticReconciliationActualExtendedRawSourceOwnerParity()
                .Prove(new(
                    nonExtendedPlan,
                    notApplicableResolution,
                    advancedOwner,
                    diffOwner));
        TestSupport.Equal(
            StatisticReconciliationActualExtendedRawSourceSchemas.Complete,
            notApplicableParity.State,
            "P10-EXT-OWNER-08 nonapplicable-owner-parity-complete");
        TestSupport.Equal(true,
            notApplicableParity.Families.All(value =>
                !value.Applicable && value.Complete &&
                value.ProofCode ==
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .LockedMetricFamilyNotApplicable &&
                value.DescriptorCount == 0 && value.TypedAtomCount == 0 &&
                value.OwnerItemCount == 0 && value.RelationCount == 0),
            "P10-EXT-OWNER-09 nonapplicable-fixed-slot-shape");
        var applicabilityTamper = notApplicableResolution with
        {
            Advanced = notApplicableResolution.Advanced! with
            {
                ProofCode =
                    StatisticReconciliationActualExtendedRawSourceFailures.None
            }
        };
        var applicabilityRejected =
            new StatisticReconciliationActualExtendedRawSourceOwnerParity()
                .Prove(new(
                    nonExtendedPlan,
                    applicabilityTamper,
                    advancedOwner,
                    diffOwner));
        TestSupport.Equal(
            StatisticReconciliationActualExtendedRawSourceSchemas.Incomplete,
            applicabilityRejected.State,
            "P10-EXT-OWNER-10 applicability-code-tamper-rejected");
        var equivalentAdvanced = advancedOwner with
        {
            Boundary = advancedOwner.Boundary with
            {
                DependencyPins = advancedOwner.Boundary.DependencyPins
                    .Select(value => string.Concat(value)).ToImmutableArray(),
                DayNodeIds = advancedOwner.Boundary.DayNodeIds
                    .Select(value => string.Concat(value)).ToImmutableArray(),
                MonthNodeIds = advancedOwner.Boundary.MonthNodeIds
                    .Select(value => string.Concat(value)).ToImmutableArray(),
                YearNodeIds = advancedOwner.Boundary.YearNodeIds
                    .Select(value => string.Concat(value)).ToImmutableArray()
            }
        };
        var equivalentDiff = diffOwner with
        {
            Boundary = diffOwner.Boundary with
            {
                DependencyPins = diffOwner.Boundary.DependencyPins
                    .Select(value => string.Concat(value)).ToImmutableArray()
            }
        };
        var equivalentResolution = await notApplicableOwner.ResolveAsync(
            new StatisticReconciliationActualExtendedRawSourceCommand(
                StatisticReconciliationActualExtendedRawSourceSchemas.Command,
                material,
                nonExtendedPlan,
                equivalentAdvanced,
                equivalentDiff));
        TestSupport.Equal(
            StatisticReconciliationActualExtendedRawSourceSchemas.Complete,
            equivalentResolution.State,
            "P10-EXT-OWNER-11 equivalent-distinct-boundary-arrays");
        var driftedAdvanced = equivalentAdvanced with
        {
            Boundary = equivalentAdvanced.Boundary with
            {
                CandidateChainId = equivalentAdvanced.Boundary.CandidateChainId +
                    "-drift"
            }
        };
        var driftedResolution = await notApplicableOwner.ResolveAsync(
            new StatisticReconciliationActualExtendedRawSourceCommand(
                StatisticReconciliationActualExtendedRawSourceSchemas.Command,
                material,
                nonExtendedPlan,
                driftedAdvanced,
                equivalentDiff));
        TestSupport.Equal(
            StatisticReconciliationActualExtendedRawSourceSchemas.Incomplete,
            driftedResolution.State,
            "P10-EXT-OWNER-12 boundary-one-field-drift-rejected");
        TestSupport.Equal(
            StatisticReconciliationActualExtendedRawSourceFailures.CommandInvalid,
            driftedResolution.FailureCode,
            "P10-EXT-OWNER-12 boundary-one-field-drift-code");
        return 12;
    }

    private static ActualAdvancedCapture AdvancedOwner(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        DateTime start)
    {
        var samples = ImmutableArray.Create("Alpha");
        const string result = "{\"Alpha\":1}";
        var fieldSemantic = TestSupport.H(
            "P10_ACTUAL_ADVANCED_FIELD_V1",
            "0",
            "choice",
            "choice",
            "Choice",
            "SINGLE_SELECT",
            "BUCKET_COUNT",
            "1",
            "1",
            TestSupport.RawSha(result),
            TestSupport.Hs(
                "P10_ACTUAL_ADVANCED_FIELD_SAMPLES_V1", samples));
        var field = new ActualAdvancedFieldObservation(
            0,
            "choice",
            "choice",
            "Choice",
            "SINGLE_SELECT",
            "BUCKET_COUNT",
            1,
            1,
            result,
            samples,
            fieldSemantic);
        const string valueJson = "{}";
        var valueSemantic = TestSupport.H(
            "P10_ACTUAL_ADVANCED_TYPED_VALUE_V1",
            TestSupport.RawSha(valueJson),
            TestSupport.Hs(
                "P10_ACTUAL_ADVANCED_TYPED_FIELDS_V1",
                [field.SemanticSha256]));
        var value = new ActualAdvancedValueObservation(
            1,
            "ADVANCED_SUMMARY_DAY_NODE_V1",
            start,
            "advanced-config",
            TestSupport.Sha("advanced-config"),
            "DAY",
            "2026-08-10",
            "2026-08-10",
            "2026-08",
            "2026",
            start,
            start.AddDays(1),
            "DIRECT_CHILDREN_OR_SELF",
            null,
            null,
            null,
            null,
            1,
            1,
            1,
            1,
            1,
            0,
            [],
            [field],
            valueSemantic);
        var state = new ActualAdvancedNodeState(
            "CLEAN", false, null, false,
            true, true, true, true, true, true, true, true, true, true,
            TestSupport.Sha("advanced-lifecycle"));
        var observed = TestSupport.RawSha(valueJson);
        var node = new ActualAdvancedNodeObservation(
            "work_assignment_advanced_summary_day_nodes",
            "advanced-node",
            "DAY",
            "2026-08-10",
            "2026-08-10",
            "2026-08",
            "2026",
            start,
            start.AddDays(1),
            TestSupport.Sha("advanced-source-signature"),
            1,
            ["advanced-report"],
            [],
            valueJson,
            observed,
            observed,
            observed,
            start,
            value,
            state,
            TestSupport.Sha("advanced-node-semantic"));
        var boundary = new ActualAdvancedOwnerBoundary(
            "work",
            "assignment",
            "form",
            "section",
            "advanced-config",
            "advanced-version",
            1,
            1,
            TestSupport.Sha("advanced-config"),
            [],
            "UTC_GREGORIAN",
            "chain",
            "P9-05",
            3,
            TestSupport.Sha("advanced-catalog-raw"),
            TestSupport.Sha("advanced-catalog-semantic"),
            TestSupport.Sha("advanced-stage-lock"),
            ["advanced-node"],
            [],
            []);
        var draft = new ActualAdvancedCapture(
            boundary, [node], 1, "~");
        return draft with
        {
            CaptureSemanticSha256 =
                StatisticReconciliationActualSummaryOwnerParity
                    .AdvancedCaptureSemantic(draft)
        };
    }

    private static ActualP9DiffCapture DiffOwner(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
            leftPins,
        ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
            rightPins)
    {
        var before = Choice("A", "B");
        var after = Choice("A", "C");
        var row = new ActualP9DiffRowObservation(
            TestSupport.Sha("diff-row"),
            0,
            descriptor.MetricId,
            "FIELD",
            descriptor.FieldId!,
            before,
            after,
            false,
            "VALUE_CHANGED",
            null,
            true,
            true,
            StatisticReconciliationActualP9DiffAdapter.P10DeltaNotComputed,
            null,
            TestSupport.Sha("diff-row-semantic"));
        var boundary = new ActualP9DiffOwnerBoundary(
            TestSupport.Sha("diff-result"),
            "diff-run",
            "work",
            "assignment",
            "form",
            "diff-config",
            "diff-version",
            1,
            1,
            TestSupport.Sha("diff-config"),
            [],
            "chain",
            "P9-06",
            4,
            TestSupport.Sha("diff-catalog-raw"),
            TestSupport.Sha("diff-catalog-semantic"),
            TestSupport.Sha("diff-stage-lock"));
        var sourcePins = leftPins.Concat(rightPins)
            .Select((pin, index) =>
            {
                var semantic = TestSupport.H(
                    "P10_ACTUAL_P9_DIFF_SOURCE_PIN_V1",
                    TestSupport.I(index),
                    pin.Side,
                    pin.SourceReportId,
                    TestSupport.I(pin.SourcePayloadRevision),
                    pin.SourcePayloadSha256,
                    TestSupport.I(pin.SourceLifecycleRevision),
                    pin.DirectRunId,
                    pin.DirectGenerationId);
                return new ActualP9DiffSourcePinObservation(
                    index,
                    pin.Side,
                    pin.SourceReportId,
                    pin.SourcePayloadRevision,
                    pin.SourcePayloadSha256,
                    pin.SourceLifecycleRevision,
                    pin.DirectRunId,
                    pin.DirectGenerationId,
                    semantic);
            })
            .ToImmutableArray();
        var state = new ActualP9DiffOwnerState(
            "COMPLETED", true, true, false, false,
            true, true, true, true, true, true, true, true, true, true, true,
            TestSupport.Sha("diff-lifecycle"));
        var draft = new ActualP9DiffCapture(
            boundary,
            "FIELD",
            descriptor.FieldId!,
            "field",
            "CHOICE",
            LeftPeriod,
            "FIELD",
            descriptor.FieldId!,
            "field",
            "CHOICE",
            RightPeriod,
            "LEFT_TO_RIGHT",
            "INCLUDE",
            "INCLUDE",
            "UTC_GREGORIAN",
            sourcePins,
            [row],
            TestSupport.Sha("diff-result-sha"),
            TestSupport.Sha("diff-result-sha"),
            state,
            StatisticReconciliationActualP9DiffAdapter.P10DeltaNotComputed,
            null,
            "~");
        return draft with
        {
            CaptureSemanticSha256 =
                StatisticReconciliationActualSummaryOwnerParity
                    .DiffCaptureSemantic(draft)
        };
    }

    private static ActualP9DiffTypedObservation Choice(params string[] values)
    {
        var exact = values.OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        var canonical = string.Join('\u001f', exact);
        var semantic = TestSupport.H(
            "P10_ACTUAL_P9_DIFF_TYPED_VALUE_V1",
            "VALUE",
            "CHOICE",
            canonical,
            null,
            null,
            null,
            TestSupport.Hs("P10_ACTUAL_P9_DIFF_CHOICES_V1", exact),
            "true");
        return new(
            "VALUE",
            "CHOICE",
            canonical,
            null,
            null,
            null,
            exact,
            true,
            semantic);
    }

    private static string OneBit(string value)
        => value[..^1] + (value[^1] == '0' ? '1' : '0');

    private sealed class StaticCollectReader(
        StatisticReconciliationActualExtendedRawSourceCollect collect)
        : IStatisticReconciliationActualExtendedRawSourceCollectReader
    {
        internal int ReadCount { get; private set; }

        public Task<StatisticReconciliationActualExtendedRawSourceCollect>
            CollectAsync(
                StatisticReconciliationActualExtendedRawSourceCommand command,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(collect);
        }
    }
}
