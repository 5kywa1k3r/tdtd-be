using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualExtendedRawSourceTypedCompiler(
    IStatisticReconciliationActualRawSummaryCompiler rawCompiler)
{
    internal StatisticReconciliationActualExtendedAdvancedTypedPartition
        CompileAdvanced(
            StatisticReconciliationActualSummaryPlanBinding exactPlan,
            string grain,
            string grainKey,
            ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
                envelopes,
            ImmutableArray<StatisticReconciliationActualExtendedAdvancedDescriptorProjection>
                projections,
            string schemaOptionBindingSha256)
    {
        var plan = NormalizePlan(exactPlan);
        grain = StatisticReconciliationActualCanonical.Upper(
            grain, "EXTENDED_TYPED_ADVANCED_GRAIN");
        grainKey = StatisticReconciliationActualCanonical.Required(
            grainKey, "EXTENDED_TYPED_ADVANCED_GRAIN_KEY");
        var periodKey = $"{grain}:{grainKey}";
        var descriptors = plan.IdentityDescriptors
            .Where(value =>
                value.Family == "ADVANCED" &&
                value.AdvancedGrain == grain &&
                value.PeriodKey == periodKey)
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var schemaBinding = StatisticReconciliationActualCanonical.Sha256(
            schemaOptionBindingSha256,
            "EXTENDED_ADVANCED_SCHEMA_OPTION_BINDING");
        if (schemaBinding != schemaOptionBindingSha256)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedDescriptorMappingInvalid);
        var projectionMap = RequireAdvancedProjectionSet(plan, projections);
        if (descriptors.Length == 0 || envelopes.IsDefault ||
            envelopes.Any(value =>
                value.Family != "ADVANCED" ||
                value.Side is not null ||
                value.Grain != grain ||
                value.GrainKey != grainKey))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedDescriptorMappingInvalid);

        ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms;
        try
        {
            var compiled = ImmutableArray.CreateBuilder<
                StatisticReconciliationActualRawSummaryAtom>();
            foreach (var descriptor in descriptors)
                compiled.AddRange(rawCompiler.Compile(
                    Slice(plan, [descriptor]),
                    envelopes.Select(value =>
                            ToAdvancedProjectedRawSource(
                                descriptor,
                                projectionMap[descriptor.IdentitySha256],
                                schemaBinding,
                                value))
                        .ToImmutableArray()));
            atoms = OrderAtoms(compiled);
        }
        catch (StatisticReconciliationActualObservationException)
        {
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .TypedAtomInvalid);
        }
        RequireDescriptorOwnership(atoms, descriptors, "ADVANCED");
        var descriptorManifest = DescriptorManifest(
            "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_MANIFEST_V2",
            descriptors);
        var atomManifest = AtomManifest(
            "P10_ACTUAL_EXTENDED_ADVANCED_TYPED_ATOM_MANIFEST_V2", atoms);
        var contributions = descriptors.Select(descriptor =>
                CompileContribution(
                    plan,
                    descriptor,
                    projectionMap[descriptor.IdentitySha256],
                    schemaBinding,
                    envelopes))
            .ToImmutableArray();
        var contributionManifest = StatisticReconciliationActualCanonical
            .HashSequence(
                "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_CONTRIBUTION_MANIFEST_V2",
                contributions.Select(value =>
                    value.ContributionSemanticSha256));
        var descriptorProjections = descriptors.Select(value =>
                projectionMap[value.IdentitySha256])
            .ToImmutableArray();
        var projectionManifest = StatisticReconciliationActualCanonical
            .HashSequence(
                "P10_ACTUAL_EXTENDED_ADVANCED_GRAIN_DESCRIPTOR_PROJECTION_MANIFEST_V1",
                descriptorProjections.Select(value =>
                    value.ProjectionSemanticSha256));
        return new(
            descriptorManifest,
            descriptors.Length,
            schemaBinding,
            descriptorProjections,
            projectionManifest,
            descriptorProjections.Length,
            contributions,
            contributionManifest,
            atoms,
            atomManifest);
    }

    private StatisticReconciliationActualExtendedAdvancedDescriptorContributionProof
        CompileContribution(
            StatisticReconciliationActualSummaryPlanBinding plan,
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            StatisticReconciliationActualExtendedAdvancedDescriptorProjection
                projection,
            string schemaOptionBindingSha256,
            ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
                envelopes)
    {
        var values = new SortedDictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var envelope in envelopes)
        {
            ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms;
            try
            {
                atoms = rawCompiler.Compile(
                    Slice(plan, [descriptor]),
                    [ToAdvancedProjectedRawSource(
                        descriptor,
                        projection,
                        schemaOptionBindingSha256,
                        envelope)]);
            }
            catch (StatisticReconciliationActualObservationException)
            {
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .TypedAtomInvalid);
            }
            var counts = atoms.Where(value =>
                    value.IdentitySha256 == descriptor.IdentitySha256 &&
                    value.TransitionLeg == "NONE" &&
                    value.AtomKind == "COUNT")
                .ToArray();
            if (counts.Length != 1 ||
                !long.TryParse(
                    counts[0].CanonicalValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var count) || count < 0)
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .TypedAtomInvalid);
            if (count > 0)
            {
                var source = StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_VALUE_SOURCE_V2",
                    descriptor.SemanticSha256,
                    envelope.ReportId,
                    envelope.EnvelopeSemanticSha256);
                if (!values.TryAdd(envelope.ReportId, source))
                    throw Incomplete(
                        StatisticReconciliationActualExtendedRawSourceFailures
                            .AdvancedSourceInvalid);
            }
        }
        var manifest = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_VALUE_SOURCE_MANIFEST_V2",
            values.Values);
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_ADVANCED_DESCRIPTOR_CONTRIBUTION_V2",
            descriptor.SemanticSha256,
            descriptor.IdentitySha256,
            manifest,
            StatisticReconciliationActualCanonical.Integer(values.Count));
        return new(
            descriptor.SemanticSha256,
            descriptor.IdentitySha256,
            manifest,
            values.Count,
            semantic);
    }
    internal void RequireAdvancedCoverage(
        StatisticReconciliationActualSummaryPlanBinding exactPlan,
        IEnumerable<(string Grain, string GrainKey)> grainKeys)
    {
        var plan = NormalizePlan(exactPlan);
        var keys = grainKeys
            .Select(value =>
                $"{StatisticReconciliationActualCanonical.Upper(value.Grain, "EXTENDED_TYPED_ADVANCED_GRAIN")}:" +
                StatisticReconciliationActualCanonical.Required(
                    value.GrainKey, "EXTENDED_TYPED_ADVANCED_GRAIN_KEY"))
            .ToImmutableArray();
        if (keys.Length == 0 ||
            keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedDescriptorMappingInvalid);
        var descriptors = plan.IdentityDescriptors
            .Where(value => value.Family == "ADVANCED")
            .ToImmutableArray();
        if (descriptors.Length == 0 ||
            descriptors.Any(value =>
                value.AdvancedGrain is null ||
                !keys.Contains(value.PeriodKey, StringComparer.Ordinal)) ||
            keys.Any(key => descriptors.All(value =>
                value.PeriodKey != key)))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .AdvancedDescriptorMappingInvalid);
    }

    internal StatisticReconciliationActualExtendedDiffTypedCompilation
        CompileDiff(
            StatisticReconciliationActualSummaryPlanBinding exactPlan,
            string direction,
            string leftConceptKind,
            string leftDataType,
            string leftPeriodCanonicalJson,
            string rightConceptKind,
            string rightDataType,
            string rightPeriodCanonicalJson,
            ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
                leftPins,
            ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
                leftEnvelopes,
            ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
                rightPins,
            ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
                rightEnvelopes)
    {
        var plan = NormalizePlan(exactPlan);
        direction = StatisticReconciliationActualCanonical.Upper(
            direction, "EXTENDED_DIFF_DIRECTION");
        leftConceptKind = StatisticReconciliationActualCanonical.Upper(
            leftConceptKind, "EXTENDED_DIFF_LEFT_CONCEPT_KIND");
        rightConceptKind = StatisticReconciliationActualCanonical.Upper(
            rightConceptKind, "EXTENDED_DIFF_RIGHT_CONCEPT_KIND");
        leftDataType = StatisticReconciliationActualCanonical.Upper(
            leftDataType, "EXTENDED_DIFF_LEFT_DATA_TYPE");
        rightDataType = StatisticReconciliationActualCanonical.Upper(
            rightDataType, "EXTENDED_DIFF_RIGHT_DATA_TYPE");
        var descriptors = plan.IdentityDescriptors
            .Where(value => value.Family == "DIFF")
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        if (direction is not ("LEFT_TO_RIGHT" or "RIGHT_TO_LEFT") ||
            descriptors.Length == 0 || leftConceptKind != rightConceptKind ||
            leftDataType != rightDataType ||
            descriptors.Any(value => value.DiffKind != leftConceptKind))
            throw DescriptorInvalid();
        foreach (var descriptor in descriptors)
            RequireDiffType(descriptor, leftConceptKind, leftDataType);
        RequireSide("LEFT", leftPins, leftEnvelopes);
        RequireSide("RIGHT", rightPins, rightEnvelopes);
        var periodBinding = RequirePeriods(
            descriptors, direction, leftPeriodCanonicalJson,
            rightPeriodCanonicalJson);
        var leftLeg = direction == "LEFT_TO_RIGHT" ? "BEFORE" : "AFTER";
        var rightLeg = direction == "LEFT_TO_RIGHT" ? "AFTER" : "BEFORE";
        var leftAtoms = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualRawSummaryAtom>();
        var rightAtoms = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualRawSummaryAtom>();
        var transitions = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualRawSummaryAtom>();
        var relations = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualExtendedDiffSourcePairProof>();
        foreach (var descriptor in descriptors)
        {
            if (!IndependentPointers(
                    descriptor.BeforeJsonPointer!, descriptor.AfterJsonPointer!))
                throw PairRequired();
            var physicalLeft = CompileProjectedLeg(
                plan, descriptor, leftLeg, leftEnvelopes);
            var physicalRight = CompileProjectedLeg(
                plan, descriptor, rightLeg, rightEnvelopes);
            leftAtoms.AddRange(physicalLeft);
            rightAtoms.AddRange(physicalRight);
            var beforeAtoms = direction == "LEFT_TO_RIGHT"
                ? physicalLeft : physicalRight;
            var afterAtoms = direction == "LEFT_TO_RIGHT"
                ? physicalRight : physicalLeft;
            var before = AggregateSide(
                descriptor, beforeAtoms, leftConceptKind, leftDataType);
            var after = AggregateSide(
                descriptor, afterAtoms, leftConceptKind, leftDataType);
            var relation = Relation(
                descriptor, leftPins, leftEnvelopes, rightPins,
                rightEnvelopes,
                direction == "LEFT_TO_RIGHT" ? before : after,
                direction == "LEFT_TO_RIGHT" ? after : before);
            relations.Add(relation);
            var transitionSource = AggregatedPairSource(
                descriptor, relation, before, after,
                leftEnvelopes, rightEnvelopes);
            ImmutableArray<StatisticReconciliationActualRawSummaryAtom>
                compiled;
            try
            {
                compiled = rawCompiler.Compile(
                    Slice(plan, [descriptor]), [transitionSource]);
            }
            catch (StatisticReconciliationActualObservationException)
            {
                throw Incomplete(
                    StatisticReconciliationActualExtendedRawSourceFailures
                        .TypedAtomInvalid);
            }
            RequireDescriptorOwnership(compiled, [descriptor], "DIFF");
            transitions.AddRange(compiled.Where(value =>
                value.TransitionLeg is "CHANGE_STATE" or "DELTA"));
        }
        var orderedLeft = OrderAtoms(leftAtoms);
        var orderedRight = OrderAtoms(rightAtoms);
        var orderedTransitions = OrderAtoms(transitions);
        if (orderedLeft.Length == 0 || orderedRight.Length == 0 ||
            orderedTransitions.Length == 0)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .TypedAtomInvalid);
        return new(
            DescriptorManifest(
                "P10_ACTUAL_EXTENDED_DIFF_DESCRIPTOR_MANIFEST_V2",
                descriptors),
            descriptors.Length,
            periodBinding,
            leftLeg,
            rightLeg,
            relations.ToImmutable(),
            orderedLeft,
            orderedRight,
            orderedTransitions);
    }
    private ImmutableArray<StatisticReconciliationActualRawSummaryAtom>
        CompileProjectedLeg(
            StatisticReconciliationActualSummaryPlanBinding plan,
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            string leg,
            ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
                envelopes)
    {
        var exactPlan = NormalizePlan(plan);
        if (exactPlan.IdentityDescriptors.Count(value =>
                value.SemanticSha256 == descriptor.SemanticSha256) != 1)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .TypedAtomInvalid);
        var compiled = OrderAtoms(
            CompileDiffLegAtoms(descriptor, leg, envelopes));
        RequireDescriptorOwnership(compiled, [descriptor], "DIFF");
        if (compiled.Length == 0 || compiled.Any(value =>
                value.TransitionLeg != leg))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .TypedAtomInvalid);
        return compiled;
    }

    private static StatisticReconciliationActualRawPayloadEnvelope
        ProjectedSource(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            string leg,
            StatisticReconciliationActualExtendedRawSourceEnvelope envelope)
    {
        var source = JsonNode.Parse(envelope.CanonicalPayloadJson)
            ?? throw PairRequired();
        var root = new JsonObject();
        if (TryNode(source, descriptor.JsonPointer, out var value))
            SetPointer(
                root,
                leg == "BEFORE"
                    ? descriptor.BeforeJsonPointer!
                    : descriptor.AfterJsonPointer!,
                value?.DeepClone());
        using var document = StatisticReconciliationActualJson.ParseStrict(
            root.ToJsonString(), "EXTENDED_DIFF_PROJECTED_SOURCE");
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
        var payloadSha = StatisticReconciliationActualJson.RawSha256(canonical);
        var stable = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_PROJECTED_SOURCE_ID_V2",
            descriptor.IdentitySha256,
            leg,
            envelope.EnvelopeSemanticSha256);
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_PROJECTED_SOURCE_V2",
            stable,
            descriptor.SemanticSha256,
            leg,
            envelope.EnvelopeSemanticSha256,
            payloadSha);
        return new(
            stable,
            envelope.WorkId,
            envelope.WorkAssignmentId,
            envelope.ReportId,
            envelope.PayloadDocumentId,
            envelope.PayloadRevision,
            envelope.PayloadOwnerSha256,
            payloadSha,
            canonical,
            semantic);
    }

    private static DiffAggregate AggregateSide(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms,
        string conceptKind,
        string ownerType)
    {
        var leg = atoms.Length == 0 ? throw PairRequired() :
            atoms[0].TransitionLeg;
        var reports = Count(atoms, "REPORT_COUNT", leg);
        var values = Count(atoms, "COUNT", leg);
        var nulls = Count(atoms, "NULL", leg);
        var missing = Count(atoms, "MISSING", leg);
        var empty = Count(atoms, "EMPTY", leg);
        if (nulls > 0)
            return new("NULL", true, null);
        if (values == 0)
        {
            if (reports == 0 || missing == reports)
                return new("MISSING", false, null);
            if (empty > 0)
                return new(
                    "EMPTY",
                    true,
                    descriptor.ValueType == "STRING_LIST"
                        ? new JsonArray()
                        : JsonValue.Create(string.Empty));
            throw PairRequired();
        }
        if (ownerType == "NUMBER")
        {
            var sum = Aggregate(atoms, "SUM", leg)
                ?? throw PairRequired();
            return new(
                "VALUE",
                true,
                JsonValue.Create(decimal.Parse(
                    sum,
                    NumberStyles.AllowLeadingSign |
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture)));
        }
        if (ownerType == "BOOLEAN")
        {
            var groups = ValueGroups(atoms, "BOOLEAN", leg);
            if (groups.Values.Sum() != values || groups.Count != 1)
                throw PairRequired();
            return new(
                "VALUE",
                true,
                JsonValue.Create(groups.Keys.Single() == "true"));
        }
        if (ownerType == "DATE")
        {
            var minimum = Aggregate(atoms, "MIN", leg)
                ?? throw PairRequired();
            if (!minimum.StartsWith("DAY:", StringComparison.Ordinal) ||
                !DateTime.TryParseExact(
                    minimum[4..],
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var day))
                throw PairRequired();
            return new(
                "VALUE",
                true,
                JsonValue.Create(day.ToString(
                    "dd/MM/yyyy", CultureInfo.InvariantCulture)));
        }
        var choiceLike = ownerType == "CHOICE" ||
            conceptKind == "ROW_LABEL";
        if (choiceLike)
        {
            if (descriptor.ValueType == "ENUM")
            {
                var enumGroups = ValueGroups(atoms, "ENUM", leg);
                if (enumGroups.Values.Sum() != values ||
                    enumGroups.Count != 1)
                    throw PairRequired();
                return new("VALUE", true,
                    JsonValue.Create(enumGroups.Keys.Single()));
            }
            if (descriptor.ValueType != "STRING_LIST")
                throw PairRequired();
            var groups = ValueGroups(atoms, "STRING_LIST", leg);
            if (groups.Values.Sum() != values)
                throw PairRequired();
            var choices = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var raw in groups.Keys)
            {
                using var array = StatisticReconciliationActualJson.ParseStrict(
                    raw, "EXTENDED_DIFF_CHOICE_ARRAY");
                if (array.RootElement.ValueKind != JsonValueKind.Array ||
                    array.RootElement.EnumerateArray().Any(value =>
                        value.ValueKind != JsonValueKind.String))
                    throw PairRequired();
                foreach (var value in array.RootElement.EnumerateArray())
                    choices.Add(value.GetString()!);
            }
            return new(
                "VALUE",
                true,
                new JsonArray(choices.Select(value =>
                    (JsonNode?)JsonValue.Create(value)).ToArray()));
        }
        if (ownerType == "TEXT")
        {
            var groups = ValueGroups(atoms, "TEXT", leg);
            if (groups.Values.Sum() != values)
                throw PairRequired();
            var ordered = groups.SelectMany(value => Enumerable.Repeat(
                    value.Key, checked((int)value.Value)))
                .OrderBy(value => value, StringComparer.Ordinal);
            return new(
                "VALUE",
                true,
                JsonValue.Create(string.Join('\u001f', ordered)));
        }
        throw DescriptorInvalid();
    }

    private static StatisticReconciliationActualExtendedDiffSourcePairProof
        Relation(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
                leftPins,
            ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
                leftEnvelopes,
            ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin>
                rightPins,
            ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
                rightEnvelopes,
            DiffAggregate physicalLeft,
            DiffAggregate physicalRight)
    {
        var kind = physicalLeft.State == "MISSING"
            ? physicalRight.State == "MISSING" ? "NEITHER" : "RIGHT_ONLY"
            : physicalRight.State == "MISSING" ? "LEFT_ONLY" : "BOTH";
        if (kind == "NEITHER")
            throw PairRequired();
        var leftPinManifest = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXTENDED_DIFF_PAIR_LEFT_PINS_V2",
            leftPins.Select(value => value.PinSemanticSha256));
        var leftEnvelopeManifest = StatisticReconciliationActualCanonical
            .HashSequence(
                "P10_ACTUAL_EXTENDED_DIFF_PAIR_LEFT_ENVELOPES_V2",
                leftEnvelopes.Select(value => value.EnvelopeSemanticSha256));
        var rightPinManifest = StatisticReconciliationActualCanonical.HashSequence(
            "P10_ACTUAL_EXTENDED_DIFF_PAIR_RIGHT_PINS_V2",
            rightPins.Select(value => value.PinSemanticSha256));
        var rightEnvelopeManifest = StatisticReconciliationActualCanonical
            .HashSequence(
                "P10_ACTUAL_EXTENDED_DIFF_PAIR_RIGHT_ENVELOPES_V2",
                rightEnvelopes.Select(value => value.EnvelopeSemanticSha256));
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_SOURCE_PAIR_V2",
            descriptor.SemanticSha256,
            descriptor.IdentitySha256,
            kind,
            leftPinManifest,
            StatisticReconciliationActualCanonical.Integer(leftPins.Length),
            leftEnvelopeManifest,
            StatisticReconciliationActualCanonical.Integer(
                leftEnvelopes.Length),
            rightPinManifest,
            StatisticReconciliationActualCanonical.Integer(rightPins.Length),
            rightEnvelopeManifest,
            StatisticReconciliationActualCanonical.Integer(
                rightEnvelopes.Length));
        return new(
            descriptor.SemanticSha256,
            descriptor.IdentitySha256,
            kind,
            leftPinManifest,
            leftPins.Length,
            leftEnvelopeManifest,
            leftEnvelopes.Length,
            rightPinManifest,
            rightPins.Length,
            rightEnvelopeManifest,
            rightEnvelopes.Length,
            semantic);
    }

    private static StatisticReconciliationActualRawPayloadEnvelope
        AggregatedPairSource(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            StatisticReconciliationActualExtendedDiffSourcePairProof relation,
            DiffAggregate before,
            DiffAggregate after,
            ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
                left,
            ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
                right)
    {
        var root = new JsonObject();
        if (before.Present)
            SetPointer(
                root,
                descriptor.BeforeJsonPointer!,
                before.Value?.DeepClone());
        if (after.Present)
            SetPointer(
                root,
                descriptor.AfterJsonPointer!,
                after.Value?.DeepClone());
        using var document = StatisticReconciliationActualJson.ParseStrict(
            root.ToJsonString(), "EXTENDED_DIFF_AGGREGATED_PAIR");
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
        var payloadSha = StatisticReconciliationActualJson.RawSha256(canonical);
        var stable = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_AGGREGATED_PAIR_ID_V2",
            descriptor.IdentitySha256,
            relation.PairSemanticSha256);
        var owner = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_AGGREGATED_PAIR_OWNER_V2",
            relation.LeftEnvelopeManifestSha256,
            relation.RightEnvelopeManifestSha256);
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_AGGREGATED_PAIR_SOURCE_V2",
            stable,
            descriptor.SemanticSha256,
            relation.PairSemanticSha256,
            payloadSha);
        var first = left.Concat(right).FirstOrDefault();
        return new(
            stable,
            first?.WorkId ?? stable,
            stable,
            stable,
            stable,
            1,
            owner,
            payloadSha,
            canonical,
            semantic);
    }
    private static void RequireSide(
        string side,
        ImmutableArray<StatisticReconciliationActualExtendedDiffSourcePin> pins,
        ImmutableArray<StatisticReconciliationActualExtendedRawSourceEnvelope>
            envelopes)
    {
        if (pins.IsDefault || envelopes.IsDefault ||
            pins.Length != envelopes.Length)
            throw PairRequired();
        var byKey = new Dictionary<
            string, StatisticReconciliationActualExtendedDiffSourcePin>(
            StringComparer.Ordinal);
        foreach (var pin in pins)
        {
            var key = $"{pin.SourceReportId}\0{pin.DirectRunId}\0{pin.DirectGenerationId}";
            if (pin.Side != side || !byKey.TryAdd(key, pin))
                throw PairRequired();
        }
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var envelope in envelopes)
        {
            var key = $"{envelope.ReportId}\0{envelope.DirectRunId}\0{envelope.DirectGenerationId}";
            if (envelope.Side != side || !byKey.TryGetValue(key, out var pin) ||
                !found.Add(key) ||
                envelope.PayloadRevision != pin.SourcePayloadRevision ||
                envelope.PayloadOwnerSha256 != pin.SourcePayloadSha256 ||
                envelope.LifecycleRevision != pin.SourceLifecycleRevision ||
                envelope.DirectGenerationSha256 !=
                    pin.DirectGenerationSha256)
                throw PairRequired();
        }
        if (found.Count != pins.Length)
            throw PairRequired();
    }

    private static string RequirePeriods(
        ImmutableArray<StatisticReconciliationActualSummaryIdentityDescriptor>
            descriptors,
        string direction,
        string leftCanonical,
        string rightCanonical)
    {
        var left = DiffPeriod(leftCanonical, "LEFT");
        var right = DiffPeriod(rightCanonical, "RIGHT");
        var binding = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_DIFF_PERIOD_BINDING_V2",
            direction,
            StatisticReconciliationActualJson.RawSha256(leftCanonical),
            StatisticReconciliationActualJson.RawSha256(rightCanonical));
        var pair = "EXPLICIT:P9_DIFF_PERIOD_PAIR:" +
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_MAPPER_DIFF_PERIOD_PAIR_V1",
                leftCanonical,
                rightCanonical);
        var explicitBinding = "P10_DIFF_PERIOD_PAIR:" + binding;
        var common = left.Mode == "EXACT" && right.Mode == "EXACT" &&
            left.Key == right.Key ? left.Key : null;
        if (descriptors.Any(value =>
                value.PeriodKey != pair &&
                value.PeriodKey != explicitBinding &&
                (common is null || value.PeriodKey != common)))
            throw DescriptorInvalid();
        return binding;
    }

    private static DiffPeriodShape DiffPeriod(string canonical, string side)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            canonical, $"EXTENDED_DIFF_{side}_PERIOD");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            StatisticReconciliationActualJson.Canonicalize(root) != canonical ||
            !root.TryGetProperty("mode", out var modeValue) ||
            modeValue.ValueKind != JsonValueKind.String)
            throw DescriptorInvalid();
        var mode = modeValue.GetString();
        string? Read(string name)
            => !root.TryGetProperty(name, out var value) ||
               value.ValueKind == JsonValueKind.Null
                ? null
                : value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : throw DescriptorInvalid();
        var key = Read("periodKey");
        var from = Read("periodKeyFrom");
        var to = Read("periodKeyTo");
        if (mode == "EXACT")
        {
            if (string.IsNullOrWhiteSpace(key) || from is not null ||
                to is not null)
                throw DescriptorInvalid();
        }
        else if (mode == "RANGE")
        {
            if (key is not null || string.IsNullOrWhiteSpace(from) ||
                string.IsNullOrWhiteSpace(to) ||
                StringComparer.Ordinal.Compare(from, to) > 0)
                throw DescriptorInvalid();
        }
        else
            throw DescriptorInvalid();
        return new(mode, key, from, to);
    }

    private static void RequireDiffType(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string conceptKind,
        string ownerType)
    {
        var choiceLike = ownerType == "CHOICE" ||
            conceptKind == "ROW_LABEL" && ownerType != "NUMBER";
        var valid = ownerType switch
        {
            "NUMBER" => descriptor.ValueType == "NUMBER" &&
                descriptor.Operations.Contains("SUM", StringComparer.Ordinal),
            "BOOLEAN" => descriptor.ValueType == "BOOLEAN" &&
                descriptor.Operations.Contains("VALUES", StringComparer.Ordinal),
            "DATE" => (descriptor.ValueType is "DATE" or "FULL_DATE") &&
                descriptor.Operations.Contains("MIN", StringComparer.Ordinal),
            "TEXT" when !choiceLike => descriptor.ValueType == "TEXT" &&
                descriptor.Operations.Contains("VALUES", StringComparer.Ordinal),
            _ when choiceLike =>
                (descriptor.ValueType is "ENUM" or "STRING_LIST") &&
                descriptor.Operations.Contains("VALUES", StringComparer.Ordinal),
            _ => false
        };
        if (!valid || descriptor.ExpandArray &&
            descriptor.ValueType == "STRING_LIST")
            throw DescriptorInvalid();
    }

    private static long Count(
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind,
        string leg)
    {
        var values = atoms.Where(value => value.AtomKind == kind &&
                value.TransitionLeg == leg)
            .ToArray();
        if (values.Length != 1 ||
            !long.TryParse(
                values[0].CanonicalValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var result) || result < 0)
            throw PairRequired();
        return result;
    }

    private static string? Aggregate(
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind,
        string leg)
    {
        var values = atoms.Where(value => value.AtomKind == kind &&
                value.TransitionLeg == leg)
            .ToArray();
        if (values.Length != 1)
            throw PairRequired();
        return values[0].ValueState == "MISSING"
            ? null
            : values[0].ValueState == "VALUE"
                ? values[0].CanonicalValue
                : throw PairRequired();
    }

    private static SortedDictionary<string, long> ValueGroups(
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind,
        string leg)
    {
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var atom in atoms.Where(value =>
                     value.AtomKind == kind && value.TransitionLeg == leg &&
                     value.ValueState == "VALUE"))
        {
            if (atom.OccurrenceCount <= 0 ||
                !result.TryAdd(atom.CanonicalValue, atom.OccurrenceCount))
                throw PairRequired();
        }
        return result;
    }

    private static StatisticReconciliationActualExtendedRawSourceIncompleteException
        DescriptorInvalid()
        => Incomplete(
            StatisticReconciliationActualExtendedRawSourceFailures
                .DiffDescriptorMappingInvalid);
    private static string Fingerprint(JsonElement root, string pointer)
    {
        if (!TryElement(root, pointer, out var value))
            return "MISSING";
        return "VALUE:" +
               StatisticReconciliationActualJson.Canonicalize(value);
    }

    private static bool TryElement(
        JsonElement root,
        string pointer,
        out JsonElement value)
    {
        value = root;
        if (pointer.Length == 0)
            return true;
        foreach (var segment in Segments(pointer))
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out value))
                    return false;
                continue;
            }
            if (value.ValueKind == JsonValueKind.Array &&
                int.TryParse(
                    segment,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var index) &&
                index >= 0 && index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }
            return false;
        }
        return true;
    }

    private static bool TryNode(
        JsonNode root,
        string pointer,
        out JsonNode? value)
    {
        value = root;
        if (pointer.Length == 0)
            return true;
        foreach (var segment in Segments(pointer))
        {
            if (value is JsonObject obj)
            {
                if (!obj.ContainsKey(segment))
                    return false;
                value = obj[segment];
                continue;
            }
            if (value is JsonArray array &&
                int.TryParse(
                    segment,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var index) &&
                index >= 0 && index < array.Count)
            {
                value = array[index];
                continue;
            }
            return false;
        }
        return true;
    }

    private static void RemovePointer(JsonNode root, string pointer)
    {
        var segments = Segments(pointer);
        if (segments.Length == 0)
            throw PairRequired();
        if (!TryParent(root, segments, create: false, out var parent))
            return;
        var tail = segments[^1];
        if (parent is JsonObject obj)
            obj.Remove(tail);
        else if (parent is JsonArray array &&
                 int.TryParse(
                     tail,
                     NumberStyles.None,
                     CultureInfo.InvariantCulture,
                     out var index) &&
                 index >= 0 && index < array.Count)
            array[index] = null;
        else
            throw PairRequired();
    }

    private static void SetPointer(
        JsonNode root,
        string pointer,
        JsonNode? value)
    {
        var segments = Segments(pointer);
        if (segments.Length == 0 ||
            !TryParent(root, segments, create: true, out var parent))
            throw PairRequired();
        var tail = segments[^1];
        if (parent is JsonObject obj)
        {
            obj[tail] = value;
            return;
        }
        if (parent is JsonArray array &&
            int.TryParse(
                tail,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index) &&
            index >= 0)
        {
            while (array.Count <= index)
                array.Add(null);
            array[index] = value;
            return;
        }
        throw PairRequired();
    }

    private static bool TryParent(
        JsonNode root,
        ImmutableArray<string> segments,
        bool create,
        out JsonNode? parent)
    {
        parent = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            var nextIsArray = int.TryParse(
                segments[index + 1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _);
            if (parent is JsonObject obj)
            {
                if (!obj.ContainsKey(segment) || obj[segment] is null)
                {
                    if (!create)
                        return false;
                    obj[segment] = nextIsArray
                        ? new JsonArray()
                        : new JsonObject();
                }
                parent = obj[segment];
                continue;
            }
            if (parent is JsonArray array &&
                int.TryParse(
                    segment,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var arrayIndex) &&
                arrayIndex >= 0)
            {
                if (!create && arrayIndex >= array.Count)
                    return false;
                while (array.Count <= arrayIndex)
                    array.Add(null);
                if (array[arrayIndex] is null)
                {
                    if (!create)
                        return false;
                    array[arrayIndex] = nextIsArray
                        ? new JsonArray()
                        : new JsonObject();
                }
                parent = array[arrayIndex];
                continue;
            }
            return false;
        }
        return parent is not null;
    }

    private static bool IndependentPointers(string before, string after)
    {
        var left = Segments(before);
        var right = Segments(after);
        if (left.Length == 0 || right.Length == 0)
            return false;
        var common = Math.Min(left.Length, right.Length);
        var samePrefix = true;
        for (var index = 0; index < common; index++)
            samePrefix &= left[index] == right[index];
        return !samePrefix;
    }

    private static ImmutableArray<string> Segments(string pointer)
        => pointer.Length == 0
            ? ImmutableArray<string>.Empty
            : pointer[1..].Split('/')
                .Select(value => value
                    .Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal))
                .ToImmutableArray();

    private static StatisticReconciliationActualRawPayloadEnvelope
        ToAdvancedRawSource(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            StatisticReconciliationActualExtendedRawSourceEnvelope value)
    {
        if (descriptor.Family != "ADVANCED" ||
            descriptor.ValueType != "TEXT")
            return ToRawSource(value);
        var root = JsonNode.Parse(value.CanonicalPayloadJson)
            ?? throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .TypedAtomInvalid);
        if (!TryNode(root, descriptor.JsonPointer, out var node) ||
            node is not JsonValue text ||
            !text.TryGetValue<string>(out var raw) ||
            !string.IsNullOrWhiteSpace(raw))
            return ToRawSource(value);
        const string canonical = "{}";
        var payloadSha =
            StatisticReconciliationActualJson.RawSha256(canonical);
        var stable =
            StatisticReconciliationActualSourceMembershipAdapter
                .ActualStableIdentitySha256(
                    value.WorkId,
                    value.WorkAssignmentId,
                    value.ReportId);
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_ADVANCED_TYPED_SOURCE_V3",
            stable,
            descriptor.SemanticSha256,
            value.EnvelopeSemanticSha256,
            payloadSha);
        return new(
            stable,
            value.WorkId,
            value.WorkAssignmentId,
            value.ReportId,
            value.PayloadDocumentId,
            value.PayloadRevision,
            value.PayloadOwnerSha256,
            payloadSha,
            canonical,
            semantic);
    }

    private static StatisticReconciliationActualRawPayloadEnvelope ToRawSource(
        StatisticReconciliationActualExtendedRawSourceEnvelope value)
    {
        var stable =
            StatisticReconciliationActualSourceMembershipAdapter
                .ActualStableIdentitySha256(
                    value.WorkId,
                    value.WorkAssignmentId,
                    value.ReportId);
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXTENDED_RAW_TYPED_SOURCE_V2",
            stable,
            value.EnvelopeSemanticSha256,
            value.CanonicalPayloadSha256);
        return new(
            stable,
            value.WorkId,
            value.WorkAssignmentId,
            value.ReportId,
            value.PayloadDocumentId,
            value.PayloadRevision,
            value.PayloadOwnerSha256,
            value.CanonicalPayloadSha256,
            value.CanonicalPayloadJson,
            semantic);
    }

    private static StatisticReconciliationActualSummaryPlanBinding Slice(
        StatisticReconciliationActualSummaryPlanBinding plan,
        IEnumerable<StatisticReconciliationActualSummaryIdentityDescriptor>
            descriptors)
    {
        var selected = descriptors
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        if (selected.Length == 0)
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .TypedAtomInvalid);
        var binding = new StatisticReconciliationExpectedGenerationBinding(
            plan.ExpectedReconciliationId,
            plan.ExpectedGenerationId,
            plan.ExpectedGenerationSha256,
            plan.ExpectedMetricPlanSha256,
            selected.Length,
            plan.ExpectedManifestSha256,
            Math.Max(1, plan.ExpectedDocumentCount),
            plan.ExpectedMembershipSemanticSha256,
            plan.ExpectedRuntimeKind);
        return StatisticReconciliationActualSummaryPlanBinding.Create(
            binding, selected);
    }

    private static void RequireDescriptorOwnership(
        ImmutableArray<StatisticReconciliationActualRawSummaryAtom> atoms,
        IEnumerable<StatisticReconciliationActualSummaryIdentityDescriptor>
            descriptors,
        string family)
    {
        var expected = descriptors
            .Select(value => value.SemanticSha256)
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (atoms.IsDefault || atoms.Length == 0 ||
            atoms.Any(value =>
                value.Family != family ||
                !expected.Contains(value.DescriptorSemanticSha256)) ||
            expected.Any(descriptor => !atoms.Any(value =>
                value.DescriptorSemanticSha256 == descriptor)))
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .TypedAtomInvalid);
    }

    private static StatisticReconciliationActualSummaryPlanBinding
        NormalizePlan(
            StatisticReconciliationActualSummaryPlanBinding plan)
    {
        try
        {
            return StatisticReconciliationActualSummaryPlanBinding.Normalize(
                plan);
        }
        catch (Exception exception) when (exception is
                   ArgumentNullException or
                   StatisticReconciliationActualObservationException)
        {
            throw Incomplete(
                StatisticReconciliationActualExtendedRawSourceFailures
                    .SummaryPlanInvalid,
                StatisticReconciliationActualExtendedRawSourceFields
                    .SummaryPlanBinding);
        }
    }

    private static ImmutableArray<StatisticReconciliationActualRawSummaryAtom>
        OrderAtoms(
            IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms)
        => atoms
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(value => value.TransitionLeg, StringComparer.Ordinal)
            .ThenBy(value => value.AtomKind, StringComparer.Ordinal)
            .ThenBy(value => value.CanonicalValue, StringComparer.Ordinal)
            .ToImmutableArray();

    internal static string DescriptorManifest(
        string domain,
        IEnumerable<StatisticReconciliationActualSummaryIdentityDescriptor>
            descriptors)
        => StatisticReconciliationActualCanonical.HashSequence(
            domain,
            descriptors
                .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
                .Select(value => value.SemanticSha256));

    internal static string AtomManifest(
        string domain,
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms)
        => StatisticReconciliationActualCanonical.HashSequence(
            domain,
            atoms.Select(value => value.AtomSemanticSha256));

    private static StatisticReconciliationActualExtendedRawSourceIncompleteException
        PairRequired()
        => Incomplete(
            StatisticReconciliationActualExtendedRawSourceFailures
                .DiffPairRelationRequired,
            StatisticReconciliationActualExtendedRawSourceFields
                .DiffPairRelation);

    private static StatisticReconciliationActualExtendedRawSourceIncompleteException
        Incomplete(string failure, params string[] fields)
        => new(failure, fields);

    private sealed record DiffAggregate(
        string State,
        bool Present,
        JsonNode? Value);

    private sealed record DiffPeriodShape(
        string Mode,
        string? Key,
        string? From,
        string? To);

    private sealed record SideInput(
        StatisticReconciliationActualExtendedDiffSourcePin Pin,
        StatisticReconciliationActualExtendedRawSourceEnvelope Envelope);

    private sealed record DiffPairInput(
        StatisticReconciliationActualExtendedDiffSourcePairProof Proof,
        StatisticReconciliationActualExtendedRawSourceEnvelope Left,
        StatisticReconciliationActualExtendedRawSourceEnvelope Right);
}
