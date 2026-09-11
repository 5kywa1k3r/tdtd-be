using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationActualExtendedRawSourceOwnerParityCommand(
    StatisticReconciliationActualSummaryPlanBinding SummaryPlan,
    StatisticReconciliationActualExtendedRawSourceResolution Resolution,
    ActualAdvancedCapture? Advanced,
    ActualP9DiffCapture? Diff);

internal sealed record StatisticReconciliationActualExtendedRawSourceOwnerFamilyParityProof(
    string Family,
    bool Applicable,
    bool Complete,
    string ProofCode,
    string SummaryPlanBindingSha256,
    string DescriptorManifestSha256,
    int DescriptorCount,
    string TypedAtomManifestSha256,
    int TypedAtomCount,
    string OwnerManifestSha256,
    int OwnerItemCount,
    string RelationManifestSha256,
    int RelationCount,
    string ProofSha256);

internal sealed record StatisticReconciliationActualExtendedRawSourceOwnerParityProof(
    string SchemaVersion,
    string State,
    string FailureCode,
    ImmutableArray<string> RequiredPersistenceFields,
    string SummaryPlanBindingSha256,
    string ExtendedRawSourceProofSha256,
    ImmutableArray<StatisticReconciliationActualExtendedRawSourceOwnerFamilyParityProof> Families,
    string TypedAtomManifestSha256,
    int TypedAtomCount,
    string OwnerManifestSha256,
    int OwnerItemCount,
    string RelationManifestSha256,
    int RelationCount,
    string ProofSha256);

internal interface IStatisticReconciliationActualExtendedRawSourceOwnerParity
{
    StatisticReconciliationActualExtendedRawSourceOwnerParityProof Prove(
        StatisticReconciliationActualExtendedRawSourceOwnerParityCommand command);
}

internal sealed partial class StatisticReconciliationActualExtendedRawSourceOwnerParity
    : IStatisticReconciliationActualExtendedRawSourceOwnerParity
{
    internal const string SchemaVersion =
        "P10_ACTUAL_EXTENDED_RAW_SOURCE_OWNER_PARITY_V2";

    public StatisticReconciliationActualExtendedRawSourceOwnerParityProof Prove(
        StatisticReconciliationActualExtendedRawSourceOwnerParityCommand command)
    {
        StatisticReconciliationActualSummaryPlanBinding? plan = null;
        var rawProof = "~";
        try
        {
            ArgumentNullException.ThrowIfNull(command);
            plan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
                command.SummaryPlan);
            StatisticReconciliationActualExtendedRawSourceIntegrity.RequireResolution(
                command.Resolution);
            rawProof = command.Resolution.ProofSha256;
            if (command.Resolution.SummaryPlanBindingSha256 != plan.SemanticSha256 ||
                command.Resolution.Advanced is null || command.Resolution.Diff is null)
                throw Fail("SUMMARY_PLAN_BINDING_MISMATCH");
            var families = ImmutableArray.Create(
                Advanced(plan, command.Resolution.Advanced, command.Advanced),
                Diff(plan, command.Resolution.Diff, command.Diff));
            return Complete(plan.SemanticSha256, rawProof, families);
        }
        catch (Exception exception) when (exception is ParityFailure or
                   StatisticReconciliationActualObservationException or
                   ArgumentException or JsonException or FormatException or
                   OverflowException or InvalidOperationException)
        {
            return Incomplete(exception is ParityFailure known
                    ? known.Code : "EXTENDED_OWNER_PARITY_INPUT_INTEGRITY",
                plan?.SemanticSha256 ?? "~", rawProof);
        }
    }

    private static StatisticReconciliationActualExtendedRawSourceOwnerFamilyParityProof Advanced(
        StatisticReconciliationActualSummaryPlanBinding plan,
        StatisticReconciliationActualExtendedAdvancedSourceProof source,
        ActualAdvancedCapture? owner)
    {
        var descriptors = plan.IdentityDescriptors
            .Where(value => value.Family == "ADVANCED")
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!source.Complete || source.ProofCode != (source.Applicable
                ? StatisticReconciliationActualExtendedRawSourceFailures.None
                : StatisticReconciliationActualExtendedRawSourceFailures
                    .LockedMetricFamilyNotApplicable))
            throw Fail("ADVANCED_SOURCE_APPLICABILITY_INVALID");
        if (!source.Applicable)
        {
            if (descriptors.Length != 0)
                throw Fail("ADVANCED_SOURCE_APPLICABILITY_DRIFT");
            return NotApplicable(plan.SemanticSha256, "ADVANCED");
        }
        if (owner is null)
            throw Fail("ADVANCED_OWNER_CAPTURE_REQUIRED");
        if (source.ConfigId != owner.Boundary.ConfigId ||
            source.ConfigVersionId != owner.Boundary.ConfigVersionId ||
            source.ConfigSha256 != owner.Boundary.ConfigSha256 ||
            source.Grains.Length != owner.Nodes.Length ||
            owner.TotalNodeCount != owner.Nodes.Length || owner.Nodes.IsDefault ||
            StatisticReconciliationActualSummaryOwnerParity.AdvancedCaptureSemantic(
                owner) != owner.CaptureSemanticSha256 ||
            owner.Nodes.Any(value =>
                !value.OwnerState.IsCleanResult || value.Value is null))
            throw Fail("ADVANCED_OWNER_CAPTURE_INVALID");
        var fields = owner.Nodes.SelectMany(node => node.Value!.Fields
                .Select(field => new AdvancedField(node, field)))
            .ToArray();
        if (descriptors.Length == 0 || fields.Length != descriptors.Length)
            throw Fail("ADVANCED_OWNER_CARDINALITY");
        var relations = ImmutableArray.CreateBuilder<string>();
        foreach (var descriptor in descriptors)
        {
            var matches = fields.Where(value =>
                    value.Node.Grain == descriptor.AdvancedGrain &&
                    descriptor.PeriodKey ==
                        $"{value.Node.Grain}:{value.Node.GrainKey}" &&
                    value.Field.FieldId == descriptor.FieldId &&
                    descriptor.MetricId ==
                        $"{value.Field.FieldKey}:{value.Field.Method}")
                .ToArray();
            if (matches.Length != 1)
                throw Fail("ADVANCED_DESCRIPTOR_OWNER_BIJECTION");
            var match = matches[0];
            var grainMatches = source.Grains.Where(value =>
                    value.OwnerNodeId == match.Node.OwnerNodeId &&
                    value.Grain == match.Node.Grain &&
                    value.GrainKey == match.Node.GrainKey &&
                    value.WindowStartUtc == match.Node.WindowStartUtc &&
                    value.WindowEndExclusiveUtc ==
                        match.Node.WindowEndExclusiveUtc)
                .ToArray();
            if (grainMatches.Length != 1)
                throw Fail("ADVANCED_GRAIN_OWNER_BIJECTION");
            var grain = grainMatches[0];
            if (grain.SourceAssignmentCount !=
                    match.Node.Value!.SourceAssignmentCount ||
                grain.SourceEnvelopeCount !=
                    match.Node.Value.SourceReportCount)
                throw Fail("ADVANCED_GRAIN_OWNER_COUNT_MISMATCH");
            var atoms = grain.TypedAtoms.Where(value =>
                    value.IdentitySha256 == descriptor.IdentitySha256)
                .ToArray();
            var projections = grain.DescriptorProjections.Where(value =>
                    value.IdentitySha256 == descriptor.IdentitySha256 &&
                    value.DescriptorSemanticSha256 ==
                        descriptor.SemanticSha256)
                .ToArray();
            if (projections.Length != 1)
                throw Fail("ADVANCED_PROJECTION_DESCRIPTOR_BIJECTION");
            var projection = projections[0];
            var contributions = grain.DescriptorContributions.Where(value =>
                    value.IdentitySha256 == descriptor.IdentitySha256 &&
                    value.DescriptorSemanticSha256 ==
                        descriptor.SemanticSha256)
                .ToArray();
            if (contributions.Length != 1 ||
                match.Field.SourceReportCount !=
                    contributions[0].ValueSourceReportCount ||
                grain.SourceEnvelopeCount !=
                    Count(atoms, "REPORT_COUNT", "NONE") ||
                match.Field.ValueCount !=
                    AdvancedOwnerValueCount(descriptor, atoms))
                throw Fail("ADVANCED_OWNER_COUNT_MISMATCH");
            var grainEnvelopes = source.Envelopes.Where(value =>
                    value.Grain == grain.Grain &&
                    value.GrainKey == grain.GrainKey)
                .ToImmutableArray();
            var methodProof = RequireAdvancedResult(
                descriptor, projection, atoms, match.Field, grainEnvelopes);
            relations.Add(Hash(
                "P10_ACTUAL_EXTENDED_OWNER_ADVANCED_RELATION_V2",
                descriptor.SemanticSha256,
                source.SchemaOptionBindingSha256,
                grain.SchemaOptionBindingSha256,
                projection.ProjectionSemanticSha256,
                grain.DescriptorProjectionManifestSha256,
                Hs(
                    "P10_ACTUAL_EXTENDED_OWNER_ADVANCED_ATOMS_V2",
                    atoms.Select(value => value.AtomSemanticSha256)),
                contributions[0].ContributionSemanticSha256,
                grain.DescriptorContributionManifestSha256,
                methodProof,
                grain.GrainSemanticSha256,
                match.Node.SemanticSha256,
                match.Field.SemanticSha256));
        }
        return Family(
            "ADVANCED",
            plan.SemanticSha256,
            descriptors,
            source.TypedAtomManifestSha256,
            source.TypedAtomCount,
            Hs(
                "P10_ACTUAL_EXTENDED_OWNER_ADVANCED_ITEMS_V1",
                owner.Nodes.Select(value => value.SemanticSha256)),
            fields.Length,
            relations.ToImmutable());
    }

    private static string RequireAdvancedResult(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        StatisticReconciliationActualExtendedAdvancedDescriptorProjection
            projection,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        ActualAdvancedFieldObservation owner,
        ImmutableArray<
            StatisticReconciliationActualExtendedRawSourceEnvelope> envelopes)
    {
        var requiredOperation = owner.Method switch
        {
            "COUNT" => "COUNT",
            "SUM" => "SUM",
            "MIN" => "MIN",
            "MAX" => "MAX",
            "MEAN" => "MEAN",
            "MIN_DATE" => "MIN",
            "MAX_DATE" => "MAX",
            "TRUE_COUNT" or "FALSE_COUNT" or "BUCKET_COUNT" or "JOIN" =>
                "VALUES",
            _ => throw Fail("ADVANCED_METHOD_UNPROVABLE")
        };
        var typeValid = owner.DataType switch
        {
            "NUMBER" => descriptor.ValueType == "NUMBER" &&
                (owner.Method is "SUM" or "COUNT" or "MEAN" or "MIN" or
                    "MAX"),
            "DATE" => (descriptor.ValueType is "DATE" or "FULL_DATE") &&
                (owner.Method is "MIN_DATE" or "MAX_DATE" or "COUNT"),
            "BOOLEAN" => descriptor.ValueType == "BOOLEAN" &&
                (owner.Method is "TRUE_COUNT" or "FALSE_COUNT" or "COUNT"),
            "SINGLE_SELECT" =>
                (descriptor.ValueType is "ENUM" or "TEXT") &&
                (owner.Method is "BUCKET_COUNT" or "COUNT"),
            "MULTI_SELECT" =>
                (descriptor.ValueType is "ENUM" or "STRING_LIST" or "TEXT") &&
                (owner.Method is "BUCKET_COUNT" or "COUNT"),
            "STRING_LIST" => descriptor.ValueType == "STRING_LIST" &&
                (owner.Method is "BUCKET_COUNT" or "COUNT" or "JOIN"),
            "TEXT" => descriptor.ValueType == "TEXT" &&
                (owner.Method is "COUNT" or "JOIN"),
            _ => false
        };
        if (!typeValid ||
            !descriptor.Operations.Contains(
                requiredOperation, StringComparer.Ordinal))
            throw Fail("ADVANCED_METHOD_TYPE_UNPROVABLE");

        string? raw;
        string methodProof;
        if (owner.Method == "JOIN")
        {
            var join = AdvancedJoin(
                descriptor, projection, envelopes, owner);
            raw = join.Json;
            methodProof = join.Proof;
        }
        else
        {
            raw = owner.Method switch
            {
                "COUNT" => I(AdvancedOwnerValueCount(
                    descriptor, atoms)),
                "SUM" => Aggregate(atoms, "SUM", "NONE") ?? "0",
                "MIN" or "MAX" or "MEAN" =>
                    Aggregate(atoms, owner.Method, "NONE"),
                "MIN_DATE" => AdvancedDate(
                    Aggregate(atoms, "MIN", "NONE")),
                "MAX_DATE" => AdvancedDate(
                    Aggregate(atoms, "MAX", "NONE")),
                "TRUE_COUNT" => I(ExactValueCount(
                    atoms, descriptor.ValueType, "true", "NONE")),
                "FALSE_COUNT" => I(ExactValueCount(
                    atoms, descriptor.ValueType, "false", "NONE")),
                "BUCKET_COUNT" => AdvancedBuckets(
                    descriptor, atoms, owner.ValueCount),
                _ => throw Fail("ADVANCED_METHOD_UNPROVABLE")
            };
            methodProof = Hash(
                "P10_ACTUAL_EXTENDED_OWNER_ADVANCED_METHOD_V2",
                descriptor.SemanticSha256,
                owner.Method,
                raw ?? "~");
        }
        using var actual = StatisticReconciliationActualJson.ParseStrict(
            owner.ResultCanonicalJson, "EXTENDED_ADVANCED_OWNER_RESULT");
        var actualCanonical = StatisticReconciliationActualJson.Canonicalize(
            actual.RootElement);
        using var wanted = StatisticReconciliationActualJson.ParseStrict(
            raw ?? "null", "EXTENDED_ADVANCED_RAW_RESULT");
        if (actualCanonical != owner.ResultCanonicalJson ||
            actualCanonical != StatisticReconciliationActualJson.Canonicalize(
                wanted.RootElement))
            throw Fail("ADVANCED_OWNER_RESULT_MISMATCH");
        return methodProof;
    }

    private static long AdvancedOwnerValueCount(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms)
    {
        var count = Count(atoms, "COUNT", "NONE");
        if (descriptor.ValueType != "STRING_LIST")
            return count;
        var samples = AtomValues(atoms, "STRING_LIST", "NONE");
        if (samples.Values.Sum() != count)
            throw Fail("ADVANCED_STRING_LIST_SAMPLE_COUNT_MISMATCH");
        return ExpandedValues(atoms, descriptor, "NONE").Values.Sum();
    }

    private static string? AdvancedDate(string? canonical)
    {
        if (canonical is null)
            return null;
        var date = CanonicalDate(canonical);
        return JsonSerializer.Serialize(
            date.ToString("O", CultureInfo.InvariantCulture));
    }

    private static string AdvancedBuckets(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        long expectedCount)
    {
        var buckets = ExpandedValues(atoms, descriptor, "NONE");
        if (buckets.Values.Sum() != expectedCount)
            throw Fail("ADVANCED_BUCKET_OCCURRENCE_MISMATCH");
        return JsonSerializer.Serialize(buckets);
    }

    private static (string Json, string Proof) AdvancedJoin(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        StatisticReconciliationActualExtendedAdvancedDescriptorProjection
            projection,
        ImmutableArray<
            StatisticReconciliationActualExtendedRawSourceEnvelope> envelopes,
        ActualAdvancedFieldObservation owner)
    {
        var textShape = descriptor.ValueType == "TEXT" &&
            projection.RawFieldType is "shortText" or "longText";
        var stringListShape = descriptor.ValueType == "STRING_LIST" &&
            projection.RawFieldType is "stringList" or "richText";
        if ((!textShape && !stringListShape) ||
            descriptor.ExpandArray ||
            projection.DescriptorSemanticSha256 !=
                descriptor.SemanticSha256 ||
            projection.IdentitySha256 != descriptor.IdentitySha256 ||
            projection.FieldId != descriptor.FieldId ||
            envelopes.IsDefault ||
            !envelopes.SequenceEqual(envelopes.OrderBy(
                value => value.SourceOrdinal)))
            throw Fail("ADVANCED_JOIN_SOURCE_ORDER_UNPROVABLE");
        var samples = ImmutableArray.CreateBuilder<string>(5);
        long valueCount = 0;
        var valueSources = ImmutableArray.CreateBuilder<string>();
        foreach (var envelope in envelopes)
        {
            using var payload = StatisticReconciliationActualJson.ParseStrict(
                envelope.CanonicalPayloadJson,
                "EXTENDED_ADVANCED_JOIN_PAYLOAD");
            if (!TryAdvancedProjectionValue(
                    payload.RootElement, projection, out var value) ||
                value.ValueKind == JsonValueKind.Null)
                continue;
            var itemOrdinal = 0;
            foreach (var displayValue in AdvancedJoinDisplayValues(
                         value, projection))
            {
                var text = displayValue.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                valueCount = checked(valueCount + 1);
                valueSources.Add(Hash(
                    "P10_ACTUAL_EXTENDED_OWNER_ADVANCED_JOIN_VALUE_V2",
                    descriptor.SemanticSha256,
                    envelope.ReportId,
                    envelope.EnvelopeSemanticSha256,
                    I(itemOrdinal++),
                    text));
                var sample = text.Length > 160 ? text[..160] : text;
                if (samples.Count < 5 &&
                    !samples.Contains(sample, StringComparer.Ordinal))
                    samples.Add(sample);
            }
        }
        var exact = samples.ToImmutable();
        if (valueCount != owner.ValueCount ||
            owner.SampleValues.IsDefault ||
            !owner.SampleValues.SequenceEqual(exact, StringComparer.Ordinal))
            throw Fail("ADVANCED_JOIN_SAMPLE_ORDER_MISMATCH");
        var sourceManifest = Hs(
            "P10_ACTUAL_EXTENDED_OWNER_ADVANCED_JOIN_VALUES_V2",
            valueSources);
        var sampleManifest = Hs(
            "P10_ACTUAL_EXTENDED_OWNER_ADVANCED_JOIN_SAMPLES_V1",
            exact.Select(value => Hash(
                "P10_ACTUAL_EXTENDED_OWNER_ADVANCED_JOIN_SAMPLE_V1",
                value)));
        var proof = Hash(
            "P10_ACTUAL_EXTENDED_OWNER_ADVANCED_JOIN_ORDER_V2",
            descriptor.SemanticSha256,
            projection.ProjectionSemanticSha256,
            sourceManifest,
            I(valueCount),
            sampleManifest,
            I(exact.Length));
        return (JsonSerializer.Serialize(string.Join(", ", exact)), proof);
    }

    private static bool TryResolvePointer(
        JsonElement root,
        string pointer,
        out JsonElement value)
    {
        value = root;
        if (pointer.Length == 0)
            return true;
        if (!pointer.StartsWith("/", StringComparison.Ordinal))
            return false;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var segment = raw
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
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

    private static StatisticReconciliationActualExtendedRawSourceOwnerFamilyParityProof Diff(
        StatisticReconciliationActualSummaryPlanBinding plan,
        StatisticReconciliationActualExtendedDiffSourceProof source,
        ActualP9DiffCapture? owner)
    {
        var descriptors = plan.IdentityDescriptors
            .Where(value => value.Family == "DIFF")
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!source.Complete || source.ProofCode != (source.Applicable
                ? StatisticReconciliationActualExtendedRawSourceFailures.None
                : StatisticReconciliationActualExtendedRawSourceFailures
                    .LockedMetricFamilyNotApplicable))
            throw Fail("DIFF_SOURCE_APPLICABILITY_INVALID");
        if (!source.Applicable)
        {
            if (descriptors.Length != 0)
                throw Fail("DIFF_SOURCE_APPLICABILITY_DRIFT");
            return NotApplicable(plan.SemanticSha256, "DIFF");
        }
        if (owner is null)
            throw Fail("DIFF_OWNER_CAPTURE_REQUIRED");
        if (source.ConfigId != owner.Boundary.ConfigId ||
            source.ConfigVersionId != owner.Boundary.ConfigVersionId ||
            source.ConfigSha256 != owner.Boundary.ConfigSha256 ||
            source.Direction != owner.Direction ||
            !owner.OwnerState.IsUsableResult ||
            StatisticReconciliationActualSummaryOwnerParity.DiffCaptureSemantic(
                owner) != owner.CaptureSemanticSha256)
            throw Fail("DIFF_OWNER_CAPTURE_INVALID");
        if (descriptors.Length == 0 ||
            descriptors.Length != owner.Rows.Length ||
            source.SourcePairCount != descriptors.Length)
            throw Fail("DIFF_OWNER_CARDINALITY");
        RequireDiffPeriods(descriptors, source, owner);
        var before = source.Sides.Single(value =>
            value.TransitionLeg == "BEFORE").TypedAtoms;
        var after = source.Sides.Single(value =>
            value.TransitionLeg == "AFTER").TypedAtoms;
        var effectiveKind = owner.Direction == "RIGHT_TO_LEFT"
            ? owner.RightConceptKind : owner.LeftConceptKind;
        var effectiveKey = owner.Direction == "RIGHT_TO_LEFT"
            ? owner.RightConceptKey : owner.LeftConceptKey;
        var relations = ImmutableArray.CreateBuilder<string>();
        foreach (var descriptor in descriptors)
        {
            var matches = owner.Rows.Where(row =>
                    row.Key == descriptor.MetricId &&
                    row.ConceptKind == effectiveKind &&
                    row.ConceptKey == effectiveKey &&
                    Concept(descriptor, row))
                .ToArray();
            if (matches.Length != 1)
                throw Fail("DIFF_DESCRIPTOR_OWNER_BIJECTION");
            var row = matches[0];
            RequireDiffDescriptorType(
                descriptor, effectiveKind, row.Left.DataType);
            if (row.Left.DataType != row.Right.DataType)
                throw Fail("DIFF_DECLARED_TYPE_MISMATCH");
            var beforeAtoms = before.Where(value =>
                    value.IdentitySha256 == descriptor.IdentitySha256)
                .ToArray();
            var afterAtoms = after.Where(value =>
                    value.IdentitySha256 == descriptor.IdentitySha256)
                .ToArray();
            var beforeValue = DiffValueFromAtoms(
                descriptor, beforeAtoms, row.Left.DataType, effectiveKind,
                owner.MissingPolicy, owner.EmptyPolicy);
            var afterValue = DiffValueFromAtoms(
                descriptor, afterAtoms, row.Right.DataType, effectiveKind,
                owner.MissingPolicy, owner.EmptyPolicy);
            if (!Typed(row.Left, beforeValue) ||
                !Typed(row.Right, afterValue))
                throw Fail("DIFF_OWNER_LEG_MISMATCH");
            RequireComparison(row, beforeValue, afterValue);
            var pairMatches = source.SourcePairs.Where(value =>
                    value.IdentitySha256 == descriptor.IdentitySha256 &&
                    value.DescriptorSemanticSha256 ==
                        descriptor.SemanticSha256)
                .ToArray();
            if (pairMatches.Length != 1)
                throw Fail("DIFF_PAIR_DESCRIPTOR_BIJECTION");
            var pair = pairMatches[0];
            var transitions = source.TransitionAtoms.Where(value =>
                    value.IdentitySha256 == descriptor.IdentitySha256)
                .ToArray();
            var transitionKind = One(
                transitions, "TRANSITION_KIND", "CHANGE_STATE");
            var actualTransition = beforeValue.State == "MISSING" &&
                                   afterValue.State != "MISSING"
                ? "ADDED"
                : beforeValue.State != "MISSING" &&
                  afterValue.State == "MISSING"
                    ? "REMOVED"
                    : beforeValue.State != afterValue.State ||
                      beforeValue.State == "VALUE" &&
                      beforeValue.Canonical != afterValue.Canonical
                        ? "CHANGED"
                        : "UNCHANGED";
            if (transitionKind.CanonicalValue != descriptor.TransitionKind ||
                Count(transitions, "ADDED_COUNT", "CHANGE_STATE") !=
                    (actualTransition == "ADDED" ? 1 : 0) ||
                Count(transitions, "REMOVED_COUNT", "CHANGE_STATE") !=
                    (actualTransition == "REMOVED" ? 1 : 0) ||
                Count(transitions, "CHANGED_COUNT", "CHANGE_STATE") !=
                    (actualTransition == "CHANGED" ? 1 : 0) ||
                Count(transitions, "UNCHANGED_COUNT", "CHANGE_STATE") !=
                    (actualTransition == "UNCHANGED" ? 1 : 0))
                throw Fail("DIFF_TRANSITION_MISMATCH");
            if (descriptor.DifferenceOperation == "SUBTRACT")
            {
                var raw = Aggregate(
                    transitions, "DIFFERENCE", "DELTA");
                var expected = beforeValue.Number.HasValue &&
                               afterValue.Number.HasValue
                    ? afterValue.Number.Value - beforeValue.Number.Value
                    : (decimal?)null;
                if (raw is not null &&
                        (!expected.HasValue ||
                         decimal.Parse(raw, CultureInfo.InvariantCulture) !=
                         expected.Value) ||
                    raw is null && expected.HasValue ||
                    row.P9NumericDelta !=
                        (expected.HasValue ? -expected.Value : null))
                    throw Fail("DIFF_DELTA_MISMATCH");
            }
            relations.Add(Hash(
                "P10_ACTUAL_EXTENDED_OWNER_DIFF_RELATION_V2",
                descriptor.SemanticSha256,
                source.PeriodBindingSha256,
                Hs(
                    "P10_ACTUAL_EXTENDED_OWNER_DIFF_ATOMS_V2",
                    beforeAtoms.Concat(afterAtoms).Concat(transitions)
                        .Select(value => value.AtomSemanticSha256)),
                pair.PairSemanticSha256,
                row.SemanticSha256));
        }
        return Family(
            "DIFF",
            plan.SemanticSha256,
            descriptors,
            source.TypedAtomManifestSha256,
            source.TypedAtomCount,
            Hs(
                "P10_ACTUAL_EXTENDED_OWNER_DIFF_ITEMS_V1",
                owner.Rows.Select(value => value.SemanticSha256)),
            owner.Rows.Length,
            relations.ToImmutable());
    }

    private static void RequireDiffPeriods(
        ImmutableArray<StatisticReconciliationActualSummaryIdentityDescriptor>
            descriptors,
        StatisticReconciliationActualExtendedDiffSourceProof source,
        ActualP9DiffCapture? owner)
    {
        var left = DiffPeriod(owner.LeftPeriodCanonicalJson, "LEFT");
        var right = DiffPeriod(owner.RightPeriodCanonicalJson, "RIGHT");
        var expected = Hash(
            "P10_ACTUAL_EXTENDED_DIFF_PERIOD_BINDING_V2",
            owner.Direction,
            StatisticReconciliationActualJson.RawSha256(
                owner.LeftPeriodCanonicalJson),
            StatisticReconciliationActualJson.RawSha256(
                owner.RightPeriodCanonicalJson));
        var pair = "EXPLICIT:P9_DIFF_PERIOD_PAIR:" + Hash(
            "P10_ACTUAL_MAPPER_DIFF_PERIOD_PAIR_V1",
            owner.LeftPeriodCanonicalJson,
            owner.RightPeriodCanonicalJson);
        var explicitBinding = "P10_DIFF_PERIOD_PAIR:" + expected;
        var common = left.Mode == "EXACT" &&
                     right.Mode == "EXACT" &&
                     left.Key == right.Key
            ? left.Key
            : null;
        if (source.PeriodBindingSha256 != expected ||
            descriptors.Any(value =>
                value.PeriodKey != pair &&
                value.PeriodKey != explicitBinding &&
                (common is null || value.PeriodKey != common)))
            throw Fail("DIFF_PERIOD_BINDING_MISMATCH");
    }

    private static (string Mode, string? Key) DiffPeriod(
        string canonical, string side)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            canonical, $"EXTENDED_OWNER_DIFF_{side}_PERIOD");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            StatisticReconciliationActualJson.Canonicalize(root) != canonical ||
            !root.TryGetProperty("mode", out var modeValue) ||
            modeValue.ValueKind != JsonValueKind.String)
            throw Fail("DIFF_PERIOD_SHAPE_INVALID");
        var mode = modeValue.GetString();
        string? Read(string name)
            => !root.TryGetProperty(name, out var value) ||
               value.ValueKind == JsonValueKind.Null
                ? null
                : value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : throw Fail("DIFF_PERIOD_SHAPE_INVALID");
        var key = Read("periodKey");
        var from = Read("periodKeyFrom");
        var to = Read("periodKeyTo");
        if (mode == "EXACT")
        {
            if (string.IsNullOrWhiteSpace(key) ||
                from is not null || to is not null)
                throw Fail("DIFF_PERIOD_SHAPE_INVALID");
        }
        else if (mode == "RANGE")
        {
            if (key is not null ||
                string.IsNullOrWhiteSpace(from) ||
                string.IsNullOrWhiteSpace(to) ||
                StringComparer.Ordinal.Compare(from, to) > 0)
                throw Fail("DIFF_PERIOD_SHAPE_INVALID");
        }
        else
            throw Fail("DIFF_PERIOD_SHAPE_INVALID");
        return (mode, key);
    }

    private static void RequireDiffDescriptorType(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string conceptKind,
        string ownerType)
    {
        var choiceLike = ownerType == "CHOICE" ||
            conceptKind == "ROW_LABEL" && ownerType != "NUMBER";
        var valid = ownerType switch
        {
            "NUMBER" => descriptor.ValueType == "NUMBER" &&
                descriptor.Operations.Contains(
                    "SUM", StringComparer.Ordinal),
            "BOOLEAN" => descriptor.ValueType == "BOOLEAN" &&
                descriptor.Operations.Contains(
                    "VALUES", StringComparer.Ordinal),
            "DATE" =>
                (descriptor.ValueType is "DATE" or "FULL_DATE") &&
                descriptor.Operations.Contains(
                    "MIN", StringComparer.Ordinal),
            "TEXT" when !choiceLike =>
                descriptor.ValueType == "TEXT" &&
                descriptor.Operations.Contains(
                    "VALUES", StringComparer.Ordinal),
            _ when choiceLike =>
                (descriptor.ValueType is "ENUM" or "STRING_LIST") &&
                descriptor.Operations.Contains(
                    "VALUES", StringComparer.Ordinal),
            _ => false
        };
        if (!valid || descriptor.DiffKind != conceptKind)
            throw Fail("DIFF_DESCRIPTOR_TYPE_UNPROVABLE");
    }

    private static RawDiffValue DiffValueFromAtoms(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string type,
        string conceptKind,
        string missingPolicy,
        string emptyPolicy)
    {
        if (atoms.Count == 0)
            throw Fail("DIFF_LEG_ATOMS_REQUIRED");
        var leg = atoms[0].TransitionLeg;
        var reports = Count(atoms, "REPORT_COUNT", leg);
        var values = Count(atoms, "COUNT", leg);
        RawDiffValue result;
        if (Count(atoms, "NULL", leg) > 0)
            result = RawDiffValue.NonValue("NULL", type);
        else if (values == 0 &&
                 (reports == 0 ||
                  Count(atoms, "MISSING", leg) == reports))
            result = RawDiffValue.NonValue("MISSING", type);
        else if (values == 0 &&
                 Count(atoms, "EMPTY", leg) > 0)
            result = new(
                "EMPTY", type, string.Empty, null, null, null,
                ImmutableArray<string>.Empty);
        else if (values <= 0)
            throw Fail("DIFF_VALUE_COUNT_INVALID");
        else if (type == "NUMBER")
            result = NumberValue(atoms, leg);
        else if (type == "BOOLEAN")
            result = BooleanValue(atoms, leg, values);
        else if (type == "DATE")
            result = DateValue(atoms, leg);
        else if (type == "CHOICE" ||
                 conceptKind == "ROW_LABEL")
            result = ChoiceValue(
                descriptor, atoms, leg, values, type);
        else if (type == "TEXT")
            result = TextValue(atoms, leg, values);
        else
            throw Fail("DIFF_TYPED_VALUE_UNPROVABLE");

        if (result.State == "EMPTY" &&
            emptyPolicy == "AS_MISSING")
            result = RawDiffValue.NonValue("MISSING", type);
        if (result.State == "EMPTY" && emptyPolicy == "REJECT" ||
            result.State == "MISSING" && missingPolicy == "REJECT")
            throw Fail("DIFF_POLICY_OWNER_IMPOSSIBLE");
        if (result.State == "MISSING" &&
            missingPolicy == "AS_ZERO")
        {
            if (type != "NUMBER")
                throw Fail("DIFF_AS_ZERO_NON_NUMBER");
            result = new(
                "VALUE", "NUMBER", "0", 0m, null, null,
                ImmutableArray<string>.Empty);
        }
        return result;
    }

    private static RawDiffValue NumberValue(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string leg)
    {
        var raw = Aggregate(atoms, "SUM", leg) ??
            throw Fail("DIFF_SUM_REQUIRED");
        var value = decimal.Parse(raw, CultureInfo.InvariantCulture);
        return new(
            "VALUE", "NUMBER", Number(value), value, null, null,
            ImmutableArray<string>.Empty);
    }

    private static RawDiffValue BooleanValue(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string leg,
        long count)
    {
        var values = AtomValues(atoms, "BOOLEAN", leg);
        if (values.Values.Sum() != count ||
            values.Count != 1 ||
            values.Keys.Single() is not ("true" or "false"))
            throw Fail("DIFF_BOOLEAN_AMBIGUOUS");
        var value = values.Keys.Single() == "true";
        return new(
            "VALUE", "BOOLEAN", value ? "true" : "false",
            null, value, null, ImmutableArray<string>.Empty);
    }

    private static RawDiffValue DateValue(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string leg)
    {
        var raw = Aggregate(atoms, "MIN", leg) ??
            throw Fail("DIFF_DATE_MIN_REQUIRED");
        var date = CanonicalDate(raw);
        return new(
            "VALUE", "DATE",
            date.ToString("O", CultureInfo.InvariantCulture),
            null, null, date, ImmutableArray<string>.Empty);
    }

    private static RawDiffValue ChoiceValue(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string leg,
        long count,
        string ownerType)
    {
        var choices = ExpandedValues(atoms, descriptor, leg);
        if (descriptor.ValueType == "STRING_LIST")
        {
var samples = AtomValues(atoms, "STRING_LIST", leg);
            if (samples.Values.Sum() != count)
                throw Fail("DIFF_CHOICE_SAMPLE_COUNT_MISMATCH");
        }
        else if (choices.Values.Sum() != count)
            throw Fail("DIFF_CHOICE_OCCURRENCE_MISMATCH");
var keys = choices.Keys
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        return new(
            keys.Length == 0 ? "EMPTY" : "VALUE",
            ownerType,
            keys.Length == 0
                ? string.Empty
                : string.Join('', keys),
            null,
            null,
            null,
            keys);
    }

    private static RawDiffValue TextValue(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string leg,
        long count)
    {
        var values = AtomValues(atoms, "TEXT", leg);
        if (values.Values.Sum() != count)
            throw Fail("DIFF_TEXT_OCCURRENCE_MISMATCH");
        var text = string.Join(
            '',
            values.SelectMany(value => Enumerable.Repeat(
                    value.Key, checked((int)value.Value)))
                .OrderBy(value => value, StringComparer.Ordinal));
        return new(
            text.Length == 0 ? "EMPTY" : "VALUE",
            "TEXT",
            text,
            null,
            null,
            null,
            ImmutableArray<string>.Empty);
    }

    private static DateTime CanonicalDate(string raw)
    {
        DateTime date;
        if (raw.StartsWith("DAY:", StringComparison.Ordinal) &&
            DateTime.TryParseExact(
                raw[4..],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date))
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);
        if (raw.StartsWith("MONTH:", StringComparison.Ordinal) &&
            DateTime.TryParseExact(
                raw[6..],
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date))
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);
        if (raw.StartsWith("YEAR:", StringComparison.Ordinal) &&
            DateTime.TryParseExact(
                raw[5..],
                "yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date))
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);
        throw Fail("RAW_DATE_CANONICAL_INVALID");
    }

    private static SortedDictionary<string, long> ExpandedValues(
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms,
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string leg)
    {
        if (descriptor.ValueType != "STRING_LIST")
            return AtomValues(atoms, descriptor.ValueType, leg);
        var result = new SortedDictionary<string, long>(
            StringComparer.Ordinal);
        foreach (var group in AtomValues(
                     atoms, "STRING_LIST", leg))
        {
            using var document =
                StatisticReconciliationActualJson.ParseStrict(
                    group.Key, "EXTENDED_OWNER_STRING_LIST");
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.EnumerateArray().Any(value =>
                    value.ValueKind != JsonValueKind.String))
                throw Fail("RAW_STRING_LIST_INVALID");
            foreach (var value in document.RootElement.EnumerateArray())
            {
                var item = value.GetString()!;
                result[item] = checked(
                    result.GetValueOrDefault(item) + group.Value);
            }
        }
        return result;
    }

    private static long ExactValueCount(
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind,
        string canonical,
        string leg)
        => AtomValues(atoms, kind, leg)
            .Where(value => value.Key == canonical)
            .Sum(value => value.Value);

    private static bool Typed(
        ActualP9DiffTypedObservation owner,
        RawDiffValue raw)
        => owner.TypedShapeValid &&
           owner.State == raw.State &&
           owner.DataType == raw.Type &&
           owner.CanonicalValue == raw.Canonical &&
           owner.NumericValue == raw.Number &&
           owner.BooleanValue == raw.Boolean &&
           owner.DateValueUtc == raw.Date &&
           owner.ChoiceIds.SequenceEqual(
               raw.Choices, StringComparer.Ordinal);

    private static void RequireComparison(
        ActualP9DiffRowObservation row,
        RawDiffValue before,
        RawDiffValue after)
    {
        var equal = before.State == after.State &&
            (before.State != "VALUE" ||
             before.Canonical == after.Canonical);
        var kind = before.State != after.State
            ? "STATE_CHANGED"
            : before.State != "VALUE" || equal
                ? "UNCHANGED"
                : "VALUE_CHANGED";
        var delta = before.Number.HasValue && after.Number.HasValue
            ? before.Number.Value - after.Number.Value
            : (decimal?)null;
        if (!row.P9ComparisonConsistent ||
            !row.DeclaredTypesMatch ||
            row.P9Equal != equal ||
            row.P9DifferenceKind != kind ||
            row.P9NumericDelta != delta)
            throw Fail("DIFF_OWNER_COMPARISON_MISMATCH");
    }

    private static bool Concept(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        ActualP9DiffRowObservation row)
        => descriptor.DiffKind switch
        {
            "FIELD" =>
                descriptor.FieldId == row.ConceptKey &&
                row.Key == $"FIELD:{row.ConceptKey}",
            "TABLE_METRIC" =>
                descriptor.TableId == row.ConceptKey &&
                row.Key == $"ROW:{descriptor.RowId}",
            "ROW_LABEL" =>
                $"{descriptor.TableId}:{descriptor.LabelId}" ==
                    row.ConceptKey &&
                row.Key == $"ROW:{descriptor.RowId}",
            _ => false
        };

    private static SortedDictionary<string, long> AtomValues(
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms,
        string? kind,
        string leg)
    {
        var result = new SortedDictionary<string, long>(
            StringComparer.Ordinal);
        foreach (var atom in atoms.Where(value =>
                     value.TransitionLeg == leg &&
                     value.ValueState == "VALUE" &&
                     (kind is null
                         ? value.AtomKind is "ENUM" or "STRING_LIST" or
                             "TEXT"
                         : value.AtomKind == kind)))
        {
            if (!result.TryAdd(
                    atom.CanonicalValue, atom.OccurrenceCount))
                throw Fail("RAW_VALUE_ATOM_DUPLICATE");
        }
        return result;
    }

    private static long ValueCount(
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind, string canonical, string leg)
        => atoms.Where(value => value.AtomKind == kind &&
                value.CanonicalValue == canonical && value.TransitionLeg == leg)
            .Sum(value => value.OccurrenceCount);

    private static long Count(IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind, string leg)
    {
        var atom = One(atoms, kind, leg);
        if (!long.TryParse(atom.CanonicalValue, NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) || value < 0)
            throw Fail("RAW_COUNT_INVALID");
        return value;
    }

    private static string? Aggregate(
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind, string leg)
    {
        var atom = One(atoms, kind, leg);
        return atom.ValueState == "MISSING" ? null : atom.ValueState == "VALUE"
            ? atom.CanonicalValue : throw Fail("RAW_AGGREGATE_INVALID");
    }

    private static StatisticReconciliationActualRawSummaryAtom One(
        IEnumerable<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind, string leg)
    {
        var values = atoms.Where(value => value.AtomKind == kind &&
            value.TransitionLeg == leg).ToArray();
        return values.Length == 1 ? values[0] : throw Fail("RAW_ATOM_CARDINALITY");
    }

    private static StatisticReconciliationActualExtendedRawSourceOwnerFamilyParityProof Family(
        string family,
        string planSha,
        ImmutableArray<StatisticReconciliationActualSummaryIdentityDescriptor> descriptors,
        string atomManifest, int atomCount, string ownerManifest, int ownerCount,
        ImmutableArray<string> relations)
    {
        var descriptorManifest = StatisticReconciliationActualExtendedRawSourceTypedCompiler
            .DescriptorManifest($"P10_ACTUAL_EXTENDED_OWNER_{family}_DESCRIPTORS_V1",
                descriptors);
        var relationManifest = Hs(
            "P10_ACTUAL_EXTENDED_OWNER_RELATION_MANIFEST_V1", relations);
        var proof = Hash("P10_ACTUAL_EXTENDED_OWNER_FAMILY_PARITY_V2", family,
            "APPLICABLE", "COMPLETE",
            StatisticReconciliationActualExtendedRawSourceFailures.None,
            planSha, descriptorManifest, I(descriptors.Length), atomManifest,
            I(atomCount), ownerManifest, I(ownerCount), relationManifest,
            I(relations.Length));
        return new(
            family, true, true,
            StatisticReconciliationActualExtendedRawSourceFailures.None,
            planSha, descriptorManifest, descriptors.Length, atomManifest,
            atomCount, ownerManifest, ownerCount, relationManifest,
            relations.Length, proof);
    }

    private static StatisticReconciliationActualExtendedRawSourceOwnerFamilyParityProof
        NotApplicable(string planSha, string family)
    {
        var descriptors = ImmutableArray<
            StatisticReconciliationActualSummaryIdentityDescriptor>.Empty;
        var descriptorManifest =
            StatisticReconciliationActualExtendedRawSourceTypedCompiler
                .DescriptorManifest(
                    $"P10_ACTUAL_EXTENDED_OWNER_{family}_DESCRIPTORS_V1",
                    descriptors);
        var typedManifest = Hs(
            "P10_ACTUAL_EXTENDED_OWNER_NOT_APPLICABLE_TYPED_V1", []);
        var ownerManifest = Hs(
            "P10_ACTUAL_EXTENDED_OWNER_NOT_APPLICABLE_ITEMS_V1", []);
        var relationManifest = Hs(
            "P10_ACTUAL_EXTENDED_OWNER_RELATION_MANIFEST_V1", []);
        var code = StatisticReconciliationActualExtendedRawSourceFailures
            .LockedMetricFamilyNotApplicable;
        var proof = Hash(
            "P10_ACTUAL_EXTENDED_OWNER_FAMILY_PARITY_V2",
            family, "NOT_APPLICABLE", "COMPLETE", code, planSha,
            descriptorManifest, I(0), typedManifest, I(0), ownerManifest,
            I(0), relationManifest, I(0));
        return new(
            family, false, true, code, planSha,
            descriptorManifest, 0, typedManifest, 0, ownerManifest, 0,
            relationManifest, 0, proof);
    }
    private static StatisticReconciliationActualExtendedRawSourceOwnerParityProof Complete(
        string plan, string rawProof,
        ImmutableArray<StatisticReconciliationActualExtendedRawSourceOwnerFamilyParityProof> families)
    {
        if (families.Length != 2 ||
            !families.Select(value => value.Family).SequenceEqual(
                new[] { "ADVANCED", "DIFF" }, StringComparer.Ordinal) ||
            families.Any(value => !value.Complete ||
                value.SummaryPlanBindingSha256 != plan ||
                value.ProofCode != (value.Applicable
                    ? StatisticReconciliationActualExtendedRawSourceFailures.None
                    : StatisticReconciliationActualExtendedRawSourceFailures
                        .LockedMetricFamilyNotApplicable)))
            throw Fail("EXTENDED_OWNER_FAMILY_SLOT_INVALID");
        var typedManifest = Hs("P10_ACTUAL_EXTENDED_OWNER_TYPED_V1",
            families.Select(value => value.TypedAtomManifestSha256));
        var ownerManifest = Hs("P10_ACTUAL_EXTENDED_OWNER_ITEMS_V1",
            families.Select(value => value.OwnerManifestSha256));
        var relationManifest = Hs("P10_ACTUAL_EXTENDED_OWNER_RELATIONS_V1",
            families.Select(value => value.RelationManifestSha256));
        var typedCount = families.Sum(value => value.TypedAtomCount);
        var ownerCount = families.Sum(value => value.OwnerItemCount);
        var relationCount = families.Sum(value => value.RelationCount);
        var proof = Hash(SchemaVersion, "COMPLETE", "NONE", plan, rawProof,
            Hs("P10_ACTUAL_EXTENDED_OWNER_FAMILIES_V1",
                families.Select(value => value.ProofSha256)),
            typedManifest, I(typedCount), ownerManifest, I(ownerCount),
            relationManifest, I(relationCount));
        return new(SchemaVersion, "COMPLETE", "NONE", ImmutableArray<string>.Empty,
            plan, rawProof, families, typedManifest, typedCount, ownerManifest,
            ownerCount, relationManifest, relationCount, proof);
    }

    private static StatisticReconciliationActualExtendedRawSourceOwnerParityProof Incomplete(
        string failure, string plan, string rawProof)
    {
        var fields = ImmutableArray.Create("extendedRawSource.completeResolution",
            "extendedRawSource.ownerParity");
        var proof = Hash(SchemaVersion, "INCOMPLETE", failure, plan, rawProof,
            Hs("P10_ACTUAL_EXTENDED_OWNER_REQUIRED_V1", fields));
        return new(SchemaVersion, "INCOMPLETE", failure, fields, plan, rawProof,
            ImmutableArray<StatisticReconciliationActualExtendedRawSourceOwnerFamilyParityProof>.Empty,
            proof, 0, proof, 0, proof, 0, proof);
    }

    private static string Hs(string domain, IEnumerable<string> values)
        => StatisticReconciliationActualCanonical.HashSequence(domain, values);
    private static string Hash(string domain, params string?[] values)
        => StatisticReconciliationActualCanonical.Hash(domain, values);
    private static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);
    private static string Number(decimal value)
        => StatisticReconciliationActualCanonical.Number(value);
    private static ParityFailure Fail(string code) => new(code);

    private sealed record AdvancedField(
        ActualAdvancedNodeObservation Node, ActualAdvancedFieldObservation Field);
    private sealed record RawDiffValue(
        string State,
        string Type,
        string? Canonical,
        decimal? Number,
        bool? Boolean,
        DateTime? Date,
        ImmutableArray<string> Choices)
    {
        internal static RawDiffValue NonValue(string state, string type)
            => new(state, type, null, null, null, null,
                ImmutableArray<string>.Empty);
    }
    private sealed class ParityFailure(string code) : Exception(code)
    {
        internal string Code { get; } = code;
    }
}