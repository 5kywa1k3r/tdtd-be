using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationActualSummaryOwnerParityPlan(
    StatisticReconciliationActualSummaryPlanBinding SummaryPlan,
    StatisticReconciliationActualRawSummaryProof RawSummary);

internal sealed record StatisticReconciliationActualSummaryOwnerParityActual(
    ActualBasicResultObservation? Basic,
    ActualAdvancedCapture? Advanced,
    ActualP9DiffCapture? Diff);

internal sealed record StatisticReconciliationActualSummaryOwnerFamilyParityProof(
    string Family,
    bool Applicable,
    bool Complete,
    string ProofCode,
    string DescriptorManifestSha256,
    int DescriptorCount,
    string RawManifestSha256,
    int RawAtomCount,
    string OwnerManifestSha256,
    int OwnerItemCount,
    string RelationManifestSha256,
    int RelationCount,
    string ProofSha256);

internal sealed record StatisticReconciliationActualSummaryOwnerParityProof(
    bool Complete,
    string? FailureCode,
    string SummaryPlanBindingSha256,
    string RawProofSha256,
    ImmutableArray<StatisticReconciliationActualSummaryOwnerFamilyParityProof>
        FamilyProofs,
    string RawManifestSha256,
    int RawAtomCount,
    string OwnerManifestSha256,
    int OwnerItemCount,
    string RelationManifestSha256,
    int RelationCount,
    string ProofSha256);

/// <summary>
/// Pure, value-bearing parity proof between the independently recomputed raw
/// actual projection and the frozen P9 Basic/Advanced/Diff result captures.
/// It deliberately has no publication or verdict side effects.
/// </summary>
internal static partial class StatisticReconciliationActualSummaryOwnerParity
{
    internal const string SchemaVersion =
        "P10_ACTUAL_SUMMARY_OWNER_PARITY_PROOF_V1";

    private static readonly ImmutableArray<string> FamilyOrder =
        ["DIRECT", "BASIC", "ADVANCED", "DIFF"];

    internal static StatisticReconciliationActualSummaryOwnerParityProof Prove(
        StatisticReconciliationActualSummaryOwnerParityPlan plan,
        StatisticReconciliationActualSummaryOwnerParityActual actual)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(actual);
        StatisticReconciliationActualSummaryPlanBinding? normalized = null;
        try
        {
            normalized = StatisticReconciliationActualSummaryPlanBinding
                .Normalize(plan.SummaryPlan);
            ValidateRaw(normalized, plan.RawSummary);
            var families = FamilyOrder.Select(family =>
                    ProveFamily(family, normalized, plan.RawSummary, actual))
                .ToImmutableArray();
            var complete = families.All(value => value.Complete);
            var failure = complete
                ? null
                : families.First(value => !value.Complete).ProofCode;
            return Top(complete, failure, normalized.SemanticSha256,
                plan.RawSummary.ProofSha256, families);
        }
        catch (Exception exception) when (exception is
                   ParityFailure or StatisticReconciliationActualObservationException
                   or JsonException or OverflowException or FormatException)
        {
            var code = exception is ParityFailure parity
                ? parity.Code
                : $"INPUT_INTEGRITY:{exception.Message}";
            return Top(false, code,
                normalized?.SemanticSha256 ?? plan.SummaryPlan?.SemanticSha256 ?? "~",
                plan.RawSummary?.ProofSha256 ?? "~",
                ImmutableArray<StatisticReconciliationActualSummaryOwnerFamilyParityProof>
                    .Empty);
        }
    }

    private static StatisticReconciliationActualSummaryOwnerFamilyParityProof
        ProveFamily(
            string family,
            StatisticReconciliationActualSummaryPlanBinding plan,
            StatisticReconciliationActualRawSummaryProof raw,
            StatisticReconciliationActualSummaryOwnerParityActual actual)
    {
        var descriptors = plan.IdentityDescriptors
            .Where(value => value.Family == family)
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var atoms = raw.Atoms.Where(value => value.Family == family)
            .ToImmutableArray();
        var descriptorManifest = HashSequence(
            "P10_ACTUAL_SUMMARY_OWNER_DESCRIPTOR_MANIFEST_V1",
            descriptors.Select(value => value.SemanticSha256));
        var rawManifest = HashSequence(
            "P10_ACTUAL_SUMMARY_OWNER_RAW_FAMILY_MANIFEST_V1",
            atoms.Select(value => value.AtomSemanticSha256));
        var owner = OwnerManifest(family, actual);

        if (family == "DIRECT")
            return Family(family, descriptors.Length > 0, true,
                "DIRECT_OWNER_PARITY_OUT_OF_SCOPE", descriptorManifest,
                descriptors.Length, rawManifest, atoms.Length,
                owner.Manifest, owner.Count, EmptyRelation(), 0);

        if (descriptors.Length == 0)
            return Family(family, false, true,
                "LOCKED_METRIC_FAMILY_NOT_APPLICABLE", descriptorManifest,
                0, rawManifest, atoms.Length, owner.Manifest, owner.Count,
                EmptyRelation(), 0);

        try
        {
            var relations = family switch
            {
                "BASIC" => ProveBasic(descriptors, atoms, raw.Sources,
                    actual.Basic ?? throw Fail("BASIC_OWNER_CAPTURE_REQUIRED")),
                "ADVANCED" => ProveAdvanced(descriptors, atoms,
                    actual.Advanced ?? throw Fail(
                        "ADVANCED_OWNER_CAPTURE_REQUIRED"), raw.Sources),
                "DIFF" => ProveDiff(descriptors, atoms,
                    actual.Diff ?? throw Fail("DIFF_OWNER_CAPTURE_REQUIRED"),
                    raw.Sources),
                _ => throw Fail("FAMILY_UNSUPPORTED")
            };
            var relationManifest = HashSequence(
                "P10_ACTUAL_SUMMARY_OWNER_RELATION_MANIFEST_V1", relations);
            return Family(family, true, true, "COMPLETE",
                descriptorManifest, descriptors.Length, rawManifest,
                atoms.Length, owner.Manifest, owner.Count,
                relationManifest, relations.Length);
        }
        catch (Exception exception) when (exception is
                   ParityFailure or StatisticReconciliationActualObservationException
                   or JsonException or OverflowException or FormatException)
        {
            var code = exception is ParityFailure parity
                ? parity.Code
                : $"{family}_OWNER_INTEGRITY:{exception.Message}";
            return Family(family, true, false, code, descriptorManifest,
                descriptors.Length, rawManifest, atoms.Length,
                owner.Manifest, owner.Count, EmptyRelation(), 0);
        }
    }

    private static void ValidateRaw(
        StatisticReconciliationActualSummaryPlanBinding plan,
        StatisticReconciliationActualRawSummaryProof raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.SchemaVersion !=
                StatisticReconciliationActualMongoRawSummaryOwner
                    .ProofSchemaVersion ||
            raw.SummaryPlanBindingSha256 != plan.SemanticSha256 ||
            raw.Sources.IsDefault || raw.Atoms.IsDefault)
            throw Fail("RAW_PROOF_BINDING_INVALID");

        Sha(raw.ActualSourceCaptureSha256, "RAW_SOURCE_CAPTURE");
        Sha(raw.ActualMembershipSemanticSha256, "RAW_MEMBERSHIP");
        Sha(raw.ProofSha256, "RAW_PROOF");
        if (raw.Sources.Length > 100_000 || raw.Atoms.Length > 80_000)
            throw Fail("RAW_PROOF_CARDINALITY_INVALID");
        string? priorSource = null;
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var payloadIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in raw.Sources)
        {
            var stable = Sha(source.SourceStableIdentitySha256,
                "RAW_STABLE_IDENTITY");
            var work = Required(source.WorkId, "RAW_WORK_ID");
            var assignment = Required(source.WorkAssignmentId,
                "RAW_ASSIGNMENT_ID");
            var report = Required(source.ReportId, "RAW_REPORT_ID");
            var expectedStable = Hash(
                "P10_ACTUAL_SOURCE_STABLE_IDENTITY_V1",
                work,
                assignment,
                report);
            if (stable != expectedStable)
                throw Fail("RAW_STABLE_IDENTITY_MISMATCH");
            Required(source.PayloadDocumentId, "RAW_PAYLOAD_ID");
            if (source.PayloadRevision < 1 || !sourceIds.Add(stable) ||
                !payloadIds.Add(source.PayloadDocumentId) ||
                priorSource is not null &&
                StringComparer.Ordinal.Compare(priorSource, stable) >= 0)
                throw Fail("RAW_SOURCE_PARTITION_INVALID");
            priorSource = stable;
            Sha(source.PayloadOwnerSha256, "RAW_PAYLOAD_OWNER");
            Sha(source.EnvelopeSemanticSha256, "RAW_ENVELOPE_SEMANTIC");
            using var document = StatisticReconciliationActualJson.ParseStrict(
                source.CanonicalPayloadJson, "RAW_CANONICAL_PAYLOAD");
            var canonical = StatisticReconciliationActualJson.Canonicalize(
                document.RootElement);
            if (canonical != source.CanonicalPayloadJson ||
                StatisticReconciliationActualJson.RawSha256(canonical) !=
                Sha(source.CanonicalPayloadSha256, "RAW_CANONICAL_PAYLOAD_SHA"))
                throw Fail("RAW_SOURCE_CANONICAL_MISMATCH");
        }
        var collect = HashSequence("P10_ACTUAL_RAW_SUMMARY_COLLECT_V1",
            raw.Sources.Select(value => value.EnvelopeSemanticSha256));
        var sourceManifest = HashSequence(
            "P10_ACTUAL_RAW_SUMMARY_SOURCE_MANIFEST_V1",
            raw.Sources.Select(value => value.EnvelopeSemanticSha256));
        if (collect != raw.FirstCollectSha256 ||
            collect != raw.SecondCollectSha256 ||
            sourceManifest != raw.SourceManifestSha256)
            throw Fail("RAW_SOURCE_MANIFEST_MISMATCH");
        var doubleCollect = Hash(
            "P10_ACTUAL_RAW_SUMMARY_DOUBLE_COLLECT_V1",
            raw.ActualSourceCaptureSha256, collect, collect);
        if (doubleCollect != raw.DoubleCollectProofSha256)
            throw Fail("RAW_DOUBLE_COLLECT_MISMATCH");

        var descriptors = plan.IdentityDescriptors.ToDictionary(
            value => value.IdentitySha256, StringComparer.Ordinal);
        string? priorAtom = null;
        var atomKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var atom in raw.Atoms)
        {
            if (!descriptors.TryGetValue(atom.IdentitySha256,
                    out var descriptor))
                throw Fail("RAW_ATOM_DESCRIPTOR_MISSING");
            ValidateAtomDescriptor(descriptor, atom);
            var semantic = Hash("P10_ACTUAL_RAW_SUMMARY_ATOM_V1",
                descriptor.SemanticSha256, descriptor.IdentitySha256,
                atom.TransitionLeg, atom.TransitionKind ?? "~",
                atom.CollectionSemantics ?? "~", atom.AtomKind,
                atom.ValueType, atom.ValueState, atom.CanonicalValue,
                I(atom.DecimalScale), I(atom.OccurrenceCount),
                I(atom.ReportCount), I(atom.RowCount),
                I(atom.NumericValueCount));
            if (semantic != atom.AtomSemanticSha256)
                throw Fail("RAW_ATOM_SEMANTIC_MISMATCH");
            var ordering = string.Join('\u001f', atom.IdentitySha256,
                atom.TransitionLeg, atom.AtomKind, atom.CanonicalValue);
            var unique = string.Join('\u001f', ordering,
                atom.ValueState, atom.TransitionKind,
                atom.CollectionSemantics);
            if (!atomKeys.Add(unique) || priorAtom is not null &&
                StringComparer.Ordinal.Compare(priorAtom, ordering) > 0)
                throw Fail("RAW_ATOM_PARTITION_INVALID");
            priorAtom = ordering;
        }
        foreach (var descriptor in plan.IdentityDescriptors)
            ValidateDescriptorPartition(descriptor, raw.Atoms.Where(value =>
                value.IdentitySha256 == descriptor.IdentitySha256).ToArray());
        var atomManifest = HashSequence(
            "P10_ACTUAL_RAW_SUMMARY_ATOM_MANIFEST_V1",
            raw.Atoms.Select(value => value.AtomSemanticSha256));
        if (atomManifest != raw.AtomManifestSha256)
            throw Fail("RAW_ATOM_MANIFEST_MISMATCH");
        var proof = Hash(raw.SchemaVersion, plan.SemanticSha256,
            raw.ActualSourceCaptureSha256,
            raw.ActualMembershipSemanticSha256, sourceManifest,
            I(raw.Sources.Length), atomManifest, I(raw.Atoms.Length),
            doubleCollect);
        if (proof != raw.ProofSha256)
            throw Fail("RAW_PROOF_SEMANTIC_MISMATCH");
    }

    private static void ValidateAtomDescriptor(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        StatisticReconciliationActualRawSummaryAtom atom)
    {
        if (atom.DescriptorSemanticSha256 != descriptor.SemanticSha256 ||
            atom.IdentitySha256 != descriptor.IdentitySha256 ||
            atom.Family != descriptor.Family || atom.Kind != descriptor.Kind ||
            atom.MetricId != descriptor.MetricId ||
            atom.FieldId != descriptor.FieldId ||
            atom.TableId != descriptor.TableId ||
            atom.RowId != descriptor.RowId ||
            atom.LabelId != descriptor.LabelId ||
            atom.BasicScope != descriptor.BasicScope ||
            atom.BasicScopeId != descriptor.BasicScopeId ||
            atom.AdvancedGrain != descriptor.AdvancedGrain ||
            atom.DiffKind != descriptor.DiffKind ||
            atom.PeriodKey != descriptor.PeriodKey ||
            !descriptor.AtomKinds.Contains(atom.AtomKind,
                StringComparer.Ordinal) || atom.DecimalScale < 0 ||
            atom.OccurrenceCount < 0 || atom.ReportCount < 0 ||
            atom.RowCount < 0 || atom.NumericValueCount < 0)
            throw Fail("RAW_ATOM_DESCRIPTOR_MISMATCH");
    }

    private static void ValidateDescriptorPartition(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms)
    {
        if (atoms.Count == 0)
            throw Fail("RAW_DESCRIPTOR_PARTITION_MISSING");
        var baseKinds = descriptor.AtomKinds.Where(kind => kind is not
            ("TRANSITION_KIND" or "ADDED_COUNT" or "REMOVED_COUNT" or
             "CHANGED_COUNT" or "UNCHANGED_COUNT" or "DIFFERENCE"))
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (descriptor.Family != "DIFF")
        {
            if (atoms.Any(value => value.TransitionLeg != "NONE") ||
                !baseKinds.SetEquals(atoms.Select(value => value.AtomKind)))
                throw Fail("RAW_NON_DIFF_PARTITION_INVALID");
            return;
        }
        foreach (var leg in new[] { "BEFORE", "AFTER" })
            if (!baseKinds.SetEquals(atoms.Where(value =>
                    value.TransitionLeg == leg).Select(value => value.AtomKind)))
                throw Fail("RAW_DIFF_LEG_PARTITION_INVALID");
        var changeKinds = atoms.Where(value =>
                value.TransitionLeg == "CHANGE_STATE")
            .Select(value => value.AtomKind).ToImmutableHashSet(
                StringComparer.Ordinal);
        if (!changeKinds.SetEquals(new[] { "TRANSITION_KIND", "ADDED_COUNT",
                "REMOVED_COUNT", "CHANGED_COUNT", "UNCHANGED_COUNT" }) ||
            (descriptor.DifferenceOperation == "SUBTRACT") !=
            atoms.Any(value => value.TransitionLeg == "DELTA" &&
                value.AtomKind == "DIFFERENCE") ||
            atoms.Any(value => value.TransitionLeg is not
                ("BEFORE" or "AFTER" or "CHANGE_STATE" or "DELTA")))
            throw Fail("RAW_DIFF_PARTITION_INVALID");
    }

    private static StatisticReconciliationActualSummaryOwnerParityProof Top(
        bool complete,
        string? failure,
        string planSha,
        string rawProofSha,
        ImmutableArray<StatisticReconciliationActualSummaryOwnerFamilyParityProof>
            families)
    {
        var rawManifest = HashSequence(
            "P10_ACTUAL_SUMMARY_OWNER_TOP_RAW_MANIFEST_V1",
            families.Select(value => value.RawManifestSha256));
        var ownerManifest = HashSequence(
            "P10_ACTUAL_SUMMARY_OWNER_TOP_OWNER_MANIFEST_V1",
            families.Select(value => value.OwnerManifestSha256));
        var relationManifest = HashSequence(
            "P10_ACTUAL_SUMMARY_OWNER_TOP_RELATION_MANIFEST_V1",
            families.Select(value => value.RelationManifestSha256));
        var rawCount = families.Sum(value => value.RawAtomCount);
        var ownerCount = families.Sum(value => value.OwnerItemCount);
        var relationCount = families.Sum(value => value.RelationCount);
        var familyManifest = HashSequence(
            "P10_ACTUAL_SUMMARY_OWNER_FAMILY_PROOFS_V1",
            families.Select(value => value.ProofSha256));
        var proof = Hash(SchemaVersion, complete ? "COMPLETE" : "INCOMPLETE",
            failure ?? "~", planSha, rawProofSha, familyManifest,
            rawManifest, I(rawCount), ownerManifest, I(ownerCount),
            relationManifest, I(relationCount));
        return new StatisticReconciliationActualSummaryOwnerParityProof(
            complete, failure, planSha, rawProofSha, families,
            rawManifest, rawCount, ownerManifest, ownerCount,
            relationManifest, relationCount, proof);
    }

    private static StatisticReconciliationActualSummaryOwnerFamilyParityProof
        Family(
            string family,
            bool applicable,
            bool complete,
            string code,
            string descriptorManifest,
            int descriptorCount,
            string rawManifest,
            int rawCount,
            string ownerManifest,
            int ownerCount,
            string relationManifest,
            int relationCount)
    {
        var proof = Hash("P10_ACTUAL_SUMMARY_OWNER_FAMILY_PROOF_V1",
            family, applicable ? "APPLICABLE" : "NOT_APPLICABLE",
            complete ? "COMPLETE" : "INCOMPLETE", code,
            descriptorManifest, I(descriptorCount), rawManifest, I(rawCount),
            ownerManifest, I(ownerCount), relationManifest, I(relationCount));
        return new StatisticReconciliationActualSummaryOwnerFamilyParityProof(
            family, applicable, complete, code, descriptorManifest,
            descriptorCount, rawManifest, rawCount, ownerManifest,
            ownerCount, relationManifest, relationCount, proof);
    }

    private static string EmptyRelation() => HashSequence(
        "P10_ACTUAL_SUMMARY_OWNER_RELATION_MANIFEST_V1", []);

    private static string Hash(string domain, params string?[] values)
        => StatisticReconciliationActualCanonical.Hash(domain, values);
    private static string HashSequence(string domain, IEnumerable<string> values)
        => StatisticReconciliationActualCanonical.HashSequence(domain, values);
    private static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);
    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);
    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static string Number(decimal value)
        => StatisticReconciliationActualCanonical.Number(value);

    private static long Integer(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind,
        string leg = "NONE")
    {
        var atom = One(atoms, kind, leg);
        if (atom.ValueType != "NUMBER" || atom.ValueState != "VALUE" ||
            atom.DecimalScale != 0 || atom.OccurrenceCount != 1 ||
            !long.TryParse(atom.CanonicalValue, NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) || value < 0)
            throw Fail("RAW_INTEGER_ATOM_INVALID");
        return value;
    }

    private static decimal? Decimal(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind,
        string leg = "NONE")
    {
        var atom = One(atoms, kind, leg);
        if (atom.ValueState == "MISSING" && atom.CanonicalValue.Length == 0 &&
            atom.OccurrenceCount == 0)
            return null;
        if (atom.ValueType != "NUMBER" || atom.ValueState != "VALUE" ||
            atom.OccurrenceCount != 1 ||
            !decimal.TryParse(atom.CanonicalValue,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value) ||
            Number(value) != atom.CanonicalValue)
            throw Fail("RAW_DECIMAL_ATOM_INVALID");
        return value;
    }

    private static StatisticReconciliationActualRawSummaryAtom One(
        IReadOnlyList<StatisticReconciliationActualRawSummaryAtom> atoms,
        string kind,
        string leg = "NONE")
    {
        var found = atoms.Where(value => value.AtomKind == kind &&
            value.TransitionLeg == leg).ToArray();
        return found.Length == 1
            ? found[0]
            : throw Fail("RAW_REQUIRED_ATOM_CARDINALITY");
    }

    private static ParityFailure Fail(string code) => new(code);

    private sealed class ParityFailure(string code) : InvalidOperationException(code)
    {
        internal string Code { get; } = code;
    }

    private sealed record OwnerDigest(string Manifest, int Count);
}
