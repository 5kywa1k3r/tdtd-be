using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationActualDirectRawOwnerParityProof(
    string SchemaVersion,
    bool Applicable,
    bool Complete,
    string ProofCode,
    string SummaryPlanBindingSha256,
    string RawProofSha256,
    string DirectCaptureSha256,
    string DescriptorManifestSha256,
    int DescriptorCount,
    string RawManifestSha256,
    int RawAtomCount,
    string OwnerManifestSha256,
    int OwnerItemCount,
    string RelationManifestSha256,
    int RelationCount,
    string ProofSha256);

/// <summary>
/// Pure server-side relation between independently compiled DIRECT raw atoms
/// and the frozen P9 direct projection rows. It admits only owner shapes that
/// retain the exact raw value; lossy DATE/PERIOD/STRING_LIST projections fail
/// closed.
/// </summary>
internal static class StatisticReconciliationActualDirectRawOwnerParity
{
    internal const string SchemaVersion =
        "P10_ACTUAL_DIRECT_RAW_OWNER_PARITY_PROOF_V1";

    internal static StatisticReconciliationActualDirectRawOwnerParityProof
        Prove(
            StatisticReconciliationActualSummaryPlanBinding exactPlan,
            StatisticReconciliationActualRawSummaryProof raw,
            ActualSourceMembershipCapture source,
            ActualDirectProjectionCapture direct)
    {
        var plan = StatisticReconciliationActualSummaryPlanBinding.Normalize(
            exactPlan ?? throw new ArgumentNullException(nameof(exactPlan)));
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(direct);

        RequireRaw(plan, raw, source);
        var owner = RequireCapture(source, direct);
        var descriptors = plan.IdentityDescriptors
            .Where(value => value.Family == "DIRECT")
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var directAtoms = raw.Atoms
            .Where(value => value.Family == "DIRECT")
            .ToImmutableArray();
        var descriptorManifest = HSeq(
            "P10_ACTUAL_SUMMARY_OWNER_DIRECT_DESCRIPTORS_V1",
            descriptors.Select(value => value.SemanticSha256));
        var rawManifest = HSeq(
            "P10_ACTUAL_SUMMARY_OWNER_DIRECT_RAW_ATOMS_V1",
            directAtoms.Select(value => value.AtomSemanticSha256));

        var relations = ImmutableArray.CreateBuilder<string>(
            descriptors.Length);
        foreach (var descriptor in descriptors)
        {
            RequireLosslessDescriptor(descriptor);
            var occurrences = Select(descriptor, direct);
            var samples = Samples(descriptor, occurrences, raw.Sources);
            var expected = Compile(descriptor, samples.Values,
                samples.ReportCount, samples.RowCount);
            var actual = directAtoms.Where(value =>
                    value.IdentitySha256 == descriptor.IdentitySha256)
                .ToImmutableArray();
            if (!actual.SequenceEqual(expected))
                throw Fail("DIRECT_OWNER_RESULT_MISMATCH");
            relations.Add(Hash(
                "P10_ACTUAL_SUMMARY_OWNER_DIRECT_RELATION_V1",
                descriptor.SemanticSha256,
                HSeq("P10_ACTUAL_SUMMARY_OWNER_DIRECT_RELATION_RAW_V1",
                    actual.Select(value => value.AtomSemanticSha256)),
                HSeq("P10_ACTUAL_SUMMARY_OWNER_DIRECT_OCCURRENCES_V1",
                    samples.OccurrencePins),
                HSeq("P10_ACTUAL_SUMMARY_OWNER_DIRECT_RECOMPUTED_V1",
                    expected.Select(value => value.AtomSemanticSha256))));
        }

        if (descriptors.Length == 0 && directAtoms.Length != 0)
            throw Fail("DIRECT_RAW_ATOMS_WITHOUT_DESCRIPTOR");
        var relationManifest = HSeq(
            "P10_ACTUAL_SUMMARY_OWNER_DIRECT_RELATIONS_V1", relations);
        var applicable = descriptors.Length > 0;
        var code = applicable ? "COMPLETE" : "DIRECT_NOT_APPLICABLE";
        var proof = Hash(
            SchemaVersion,
            B(applicable),
            "COMPLETE",
            code,
            plan.SemanticSha256,
            raw.ProofSha256,
            direct.CaptureSemanticSha256,
            descriptorManifest,
            I(descriptors.Length),
            rawManifest,
            I(directAtoms.Length),
            owner.Manifest,
            I(owner.Count),
            relationManifest,
            I(relations.Count));
        return new(
            SchemaVersion, applicable, true, code, plan.SemanticSha256,
            raw.ProofSha256, direct.CaptureSemanticSha256,
            descriptorManifest, descriptors.Length, rawManifest,
            directAtoms.Length, owner.Manifest, owner.Count,
            relationManifest, relations.Count, proof);
    }

    private static void RequireRaw(
        StatisticReconciliationActualSummaryPlanBinding plan,
        StatisticReconciliationActualRawSummaryProof raw,
        ActualSourceMembershipCapture source)
    {
        StatisticReconciliationActualSourceMembershipAdapter.RequireIntegrity(
            source);
        if (raw.SchemaVersion !=
                StatisticReconciliationActualMongoRawSummaryOwner
                    .ProofSchemaVersion ||
            raw.SummaryPlanBindingSha256 != plan.SemanticSha256 ||
            raw.ActualSourceCaptureSha256 != source.CaptureSemanticSha256 ||
            raw.ActualMembershipSemanticSha256 !=
                source.MembershipSemanticSha256 ||
            raw.Sources.IsDefault || raw.Atoms.IsDefault ||
            raw.Sources.Length != source.IncludedSources.Length)
            throw Fail("DIRECT_RAW_BINDING_INVALID");

        var included = source.IncludedSources.ToDictionary(
            value => value.ReportId, StringComparer.Ordinal);
        string? priorSource = null;
        var payloadIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var envelope in raw.Sources)
        {
            if (!included.TryGetValue(envelope.ReportId, out var decision))
                throw Fail("DIRECT_RAW_SOURCE_NOT_INCLUDED");
            var owner = decision.ObservedOwner;
            var stable =
                StatisticReconciliationActualSourceMembershipAdapter
                    .ActualStableIdentitySha256(owner.WorkId,
                        owner.WorkAssignmentId, owner.ReportId);
            if (envelope.SourceStableIdentitySha256 != stable ||
                decision.SourceStableIdentitySha256 != stable ||
                envelope.WorkId != owner.WorkId ||
                envelope.WorkAssignmentId != owner.WorkAssignmentId ||
                envelope.PayloadDocumentId != owner.PayloadDocumentId ||
                envelope.PayloadRevision != owner.PayloadRevision ||
                envelope.PayloadOwnerSha256 != owner.PayloadSha256 ||
                !payloadIds.Add(envelope.PayloadDocumentId) ||
                priorSource is not null &&
                StringComparer.Ordinal.Compare(priorSource, stable) >= 0)
                throw Fail("DIRECT_RAW_SOURCE_OWNER_MISMATCH");
            using var document = StatisticReconciliationActualJson.ParseStrict(
                envelope.CanonicalPayloadJson,
                "DIRECT_RAW_CANONICAL_PAYLOAD");
            var canonical = StatisticReconciliationActualJson.Canonicalize(
                document.RootElement);
            var canonicalSha =
                StatisticReconciliationActualJson.RawSha256(canonical);
            var semantic = Hash(
                "P10_ACTUAL_RAW_PAYLOAD_ENVELOPE_V1",
                stable,
                owner.WorkId,
                owner.WorkAssignmentId,
                owner.ReportId,
                owner.PayloadDocumentId,
                I(owner.PayloadRevision),
                owner.PayloadSha256,
                I(owner.LifecycleRevision),
                owner.LifecycleSha256,
                decision.OwnerStateSemanticSha256,
                decision.DecisionSemanticSha256,
                canonicalSha);
            if (canonical != envelope.CanonicalPayloadJson ||
                canonicalSha != envelope.CanonicalPayloadSha256 ||
                semantic != envelope.EnvelopeSemanticSha256)
                throw Fail("DIRECT_RAW_ENVELOPE_SEMANTIC_MISMATCH");
            priorSource = stable;
        }

        var collect = HSeq("P10_ACTUAL_RAW_SUMMARY_COLLECT_V1",
            raw.Sources.Select(value => value.EnvelopeSemanticSha256));
        var sourceManifest = HSeq(
            "P10_ACTUAL_RAW_SUMMARY_SOURCE_MANIFEST_V1",
            raw.Sources.Select(value => value.EnvelopeSemanticSha256));
        var doubleCollect = Hash(
            "P10_ACTUAL_RAW_SUMMARY_DOUBLE_COLLECT_V1",
            source.CaptureSemanticSha256, collect, collect);
        if (collect != raw.FirstCollectSha256 ||
            collect != raw.SecondCollectSha256 ||
            sourceManifest != raw.SourceManifestSha256 ||
            doubleCollect != raw.DoubleCollectProofSha256)
            throw Fail("DIRECT_RAW_COLLECT_INVALID");

        var descriptorMap = plan.IdentityDescriptors.ToDictionary(
            value => value.IdentitySha256, StringComparer.Ordinal);
        string? priorAtom = null;
        var atomKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var atom in raw.Atoms)
        {
            if (!descriptorMap.TryGetValue(atom.IdentitySha256,
                    out var descriptor) ||
                atom.DescriptorSemanticSha256 != descriptor.SemanticSha256 ||
                atom.Family != descriptor.Family ||
                atom.Kind != descriptor.Kind ||
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
                    StringComparer.Ordinal) ||
                atom.DecimalScale < 0 || atom.OccurrenceCount < 0 ||
                atom.ReportCount < 0 || atom.RowCount < 0 ||
                atom.NumericValueCount < 0)
                throw Fail("DIRECT_RAW_ATOM_DESCRIPTOR_INVALID");
            var semantic = AtomSemantic(descriptor, atom.AtomKind,
                atom.ValueType, atom.ValueState, atom.CanonicalValue,
                atom.DecimalScale, atom.OccurrenceCount, atom.ReportCount,
                atom.RowCount, atom.NumericValueCount, atom.TransitionLeg,
                atom.TransitionKind, atom.CollectionSemantics);
            var ordering = string.Join('\u001f', atom.IdentitySha256,
                atom.TransitionLeg, atom.AtomKind, atom.CanonicalValue);
            var unique = string.Join('\u001f', ordering, atom.ValueState,
                atom.TransitionKind, atom.CollectionSemantics);
            if (semantic != atom.AtomSemanticSha256 ||
                !atomKeys.Add(unique) ||
                priorAtom is not null &&
                StringComparer.Ordinal.Compare(priorAtom, ordering) > 0)
                throw Fail("DIRECT_RAW_ATOM_SEMANTIC_INVALID");
            priorAtom = ordering;
        }
        var atomManifest = HSeq(
            "P10_ACTUAL_RAW_SUMMARY_ATOM_MANIFEST_V1",
            raw.Atoms.Select(value => value.AtomSemanticSha256));
        var proof = Hash(raw.SchemaVersion, plan.SemanticSha256,
            source.CaptureSemanticSha256,
            source.MembershipSemanticSha256!, sourceManifest,
            I(raw.Sources.Length), atomManifest, I(raw.Atoms.Length),
            doubleCollect);
        if (atomManifest != raw.AtomManifestSha256 ||
            proof != raw.ProofSha256)
            throw Fail("DIRECT_RAW_PROOF_INVALID");
    }

    private static (string Manifest, int Count) RequireCapture(
        ActualSourceMembershipCapture source,
        ActualDirectProjectionCapture direct)
    {
        var boundary = direct.Boundary ?? throw Fail(
            "DIRECT_BOUNDARY_REQUIRED");
        if (boundary.WorkId != source.WorkId ||
            boundary.PeriodInstanceKey != source.PeriodInstanceKey ||
            boundary.DynamicFormTemplateId !=
                source.DynamicFormTemplateId ||
            boundary.OwnerMembershipSignature !=
                source.OwnerMembershipSignature ||
            boundary.RunId != source.OwnerRunId ||
            boundary.GenerationId != source.OwnerGenerationId ||
            boundary.OwnerGenerationSha256 !=
                source.OwnerGenerationSha256 ||
            boundary.DirectSourceRevision !=
                source.OwnerDirectSourceRevision ||
            direct.ActualSourceSetSha256 != source.SourceSetSha256)
            throw Fail("DIRECT_SOURCE_MEMBERSHIP_MISMATCH");
        if (direct.FieldRows.IsDefault || direct.TableMetricRows.IsDefault ||
            direct.RowLabelRows.IsDefault || direct.TotalRowCount < 0 ||
            direct.TotalRowCount >
                StatisticReconciliationActualDirectProjectionAdapter
                    .MaxProjectionRows ||
            direct.TotalRowCount != checked(direct.FieldRows.Length +
                direct.TableMetricRows.Length + direct.RowLabelRows.Length))
            throw Fail("DIRECT_CAPTURE_CARDINALITY_INVALID");

        var boundarySha = Hash(
            "P10_ACTUAL_DIRECT_PROJECTION_BOUNDARY_V1",
            boundary.WorkId,
            boundary.PeriodInstanceKey,
            boundary.DynamicFormFamilyId,
            boundary.DynamicFormTemplateId,
            boundary.DynamicFormVersionNo.ToString(
                CultureInfo.InvariantCulture),
            boundary.DynamicFormSchemaSha256,
            boundary.RunId,
            boundary.GenerationId,
            boundary.OwnerGenerationSha256,
            boundary.OwnerLifecycleEventKey,
            StatisticReconciliationActualCanonical.Instant(
                boundary.OwnerComputedAtUtc),
            I(boundary.DirectSourceRevision),
            boundary.ConfigId,
            boundary.ConfigVersionId,
            boundary.ConfigVersionNo.ToString(CultureInfo.InvariantCulture),
            I(boundary.ConfigRevision),
            boundary.ConfigSha256,
            boundary.CandidateChainId,
            boundary.CatalogVersion,
            boundary.CatalogRawSha256,
            boundary.CatalogSemanticSha256,
            boundary.SchemaRawSha256,
            boundary.SchemaSemanticSha256,
            boundary.StageLockSha256,
            boundary.OwnerMembershipSignature,
            source.CaptureSemanticSha256);
        if (boundarySha != direct.BoundarySemanticSha256)
            throw Fail("DIRECT_BOUNDARY_SEMANTIC_MISMATCH");

        var decisions = source.IncludedSources.ToDictionary(
            value => value.ReportId, StringComparer.Ordinal);
        foreach (var row in direct.FieldRows)
            RequireField(boundary, decisions, row);
        foreach (var row in direct.TableMetricRows)
            RequireTable(boundary, decisions, row);
        foreach (var row in direct.RowLabelRows)
            RequireLabel(boundary, decisions, row);
        if (!direct.FieldRows.SequenceEqual(direct.FieldRows
                .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
                .ThenBy(value => value.FieldKey, StringComparer.Ordinal)
                .ThenBy(value => value.BucketKey ?? string.Empty,
                    StringComparer.Ordinal)
                .ThenBy(value => value.ReportId, StringComparer.Ordinal)
                .ThenBy(value => value.SourceKey, StringComparer.Ordinal)
                .ThenBy(value => value.OwnerRowId, StringComparer.Ordinal)) ||
            !direct.TableMetricRows.SequenceEqual(direct.TableMetricRows
                .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
                .ThenBy(value => value.BlockId, StringComparer.Ordinal)
                .ThenBy(value => value.MetricKey, StringComparer.Ordinal)
                .ThenBy(value => value.RowKey, StringComparer.Ordinal)
                .ThenBy(value => value.ColumnKey, StringComparer.Ordinal)
                .ThenBy(value => value.ReportId, StringComparer.Ordinal)
                .ThenBy(value => value.SourceKey, StringComparer.Ordinal)
                .ThenBy(value => value.OwnerRowId, StringComparer.Ordinal)) ||
            !direct.RowLabelRows.SequenceEqual(direct.RowLabelRows
                .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
                .ThenBy(value => value.BlockId, StringComparer.Ordinal)
                .ThenBy(value => value.LabelCode, StringComparer.Ordinal)
                .ThenBy(value => value.RowKey, StringComparer.Ordinal)
                .ThenBy(value => value.RowIndex)
                .ThenBy(value => value.ReportId, StringComparer.Ordinal)
                .ThenBy(value => value.OwnerRowId, StringComparer.Ordinal)))
            throw Fail("DIRECT_CAPTURE_ORDER_INVALID");
        var ownerIds = direct.FieldRows.Select(value => value.OwnerRowId)
            .Concat(direct.TableMetricRows.Select(value => value.OwnerRowId))
            .Concat(direct.RowLabelRows.Select(value => value.OwnerRowId))
            .ToArray();
        if (ownerIds.Distinct(StringComparer.Ordinal).Count() !=
            ownerIds.Length)
            throw Fail("DIRECT_OWNER_ROW_ID_AMBIGUOUS");

        var fieldManifest = HSeq("P10_ACTUAL_DIRECT_FIELD_ROWS_V1",
            direct.FieldRows.Select(value => value.SemanticSha256));
        var tableManifest = HSeq("P10_ACTUAL_DIRECT_TABLE_ROWS_V1",
            direct.TableMetricRows.Select(value => value.SemanticSha256));
        var labelManifest = HSeq("P10_ACTUAL_DIRECT_LABEL_ROWS_V1",
            direct.RowLabelRows.Select(value => value.SemanticSha256));
        var capture = Hash("P10_ACTUAL_DIRECT_PROJECTION_CAPTURE_V2",
            boundarySha, source.SourceSetSha256, fieldManifest,
            tableManifest, labelManifest);
        if (capture != direct.CaptureSemanticSha256)
            throw Fail("DIRECT_CAPTURE_SEMANTIC_MISMATCH");
        var ownerManifest = Hash(
            "P10_ACTUAL_SUMMARY_OWNER_DIRECT_MANIFEST_V1",
            capture, boundarySha, source.SourceSetSha256,
            fieldManifest, tableManifest, labelManifest);
        return (ownerManifest, direct.TotalRowCount);
    }

    private static void RequireField(
        ActualDirectProjectionBoundary boundary,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> decisions,
        ActualFieldProjectionObservation row)
    {
        var source = RequireCommon(boundary, decisions, row.OwnerRowId,
            row.WorkAssignmentId, row.ReportId, row.PeriodInstanceKey,
            row.DynamicFormTemplateId, row.TypedValue, row.RowState,
            row.Provenance);
        if (row.StatisticLabelCodes.IsDefault ||
            row.StatisticLabelCodes.Any(string.IsNullOrWhiteSpace) ||
            row.StatisticLabelCodes.Distinct(StringComparer.Ordinal).Count() !=
                row.StatisticLabelCodes.Length)
            throw Fail("DIRECT_FIELD_LABEL_SET_INVALID");
        var stable = Hash("P10_ACTUAL_FIELD_TYPED_IDENTITY_V1",
            boundary.DynamicFormTemplateId, row.FieldId, row.FieldKey,
            row.SourceKey, row.BucketKey);
        var occurrence = Hash("P10_ACTUAL_FIELD_OCCURRENCE_V1", stable,
            row.ReportId, row.OwnerRowId,
            row.Provenance.SourcePayloadRevision.ToString(
                CultureInfo.InvariantCulture),
            row.Provenance.SourceLifecycleRevision.ToString(
                CultureInfo.InvariantCulture));
        var semantic = Hash("P10_ACTUAL_FIELD_OBSERVATION_V2", occurrence,
            row.TypedValue.ValueIdentitySha256, row.RowState.SemanticSha256,
            row.Provenance.SemanticSha256, row.PeriodKey, row.FieldType,
            row.ConceptCode,
            HSeq("P10_ACTUAL_FIELD_LABELS_V1",
                row.StatisticLabelCodes));
        if (source.ReportId != row.ReportId ||
            stable != row.StableTypedIdentitySha256 ||
            occurrence != row.OccurrenceIdentitySha256 ||
            semantic != row.SemanticSha256)
            throw Fail("DIRECT_FIELD_SEMANTIC_MISMATCH");
    }

    private static void RequireTable(
        ActualDirectProjectionBoundary boundary,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> decisions,
        ActualTableMetricProjectionObservation row)
    {
        RequireCommon(boundary, decisions, row.OwnerRowId,
            row.WorkAssignmentId, row.ReportId, row.PeriodInstanceKey,
            row.DynamicFormTemplateId, row.TypedValue, row.RowState,
            row.Provenance);
        var stable = Hash("P10_ACTUAL_TABLE_TYPED_IDENTITY_V1",
            boundary.DynamicFormTemplateId, row.DynamicExcelTemplateId,
            row.BlockId, row.TableMode, row.MetricKey, row.RowKey,
            row.ColumnKey, row.SourceKey, row.BucketKey, row.DataType);
        var occurrence = Hash("P10_ACTUAL_TABLE_OCCURRENCE_V1", stable,
            row.ReportId, row.OwnerRowId,
            row.Provenance.SourcePayloadRevision.ToString(
                CultureInfo.InvariantCulture),
            row.Provenance.SourceLifecycleRevision.ToString(
                CultureInfo.InvariantCulture));
        var semantic = Hash("P10_ACTUAL_TABLE_OBSERVATION_V2", occurrence,
            row.TypedValue.ValueIdentitySha256, row.RowState.SemanticSha256,
            row.Provenance.SemanticSha256, row.PeriodKey, row.DataType,
            row.ConceptCode);
        if (stable != row.StableTypedIdentitySha256 ||
            occurrence != row.OccurrenceIdentitySha256 ||
            semantic != row.SemanticSha256)
            throw Fail("DIRECT_TABLE_SEMANTIC_MISMATCH");
    }

    private static void RequireLabel(
        ActualDirectProjectionBoundary boundary,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> decisions,
        ActualRowLabelProjectionObservation row)
    {
        RequireCommon(boundary, decisions, row.OwnerRowId,
            row.WorkAssignmentId, row.ReportId, row.PeriodInstanceKey,
            row.DynamicFormTemplateId, row.TypedValue, row.RowState,
            row.Provenance);
        if (row.RowIndex < 0 || row.Source != "ROW_LABEL")
            throw Fail("DIRECT_LABEL_SHAPE_INVALID");
        var stable = Hash("P10_ACTUAL_LABEL_TYPED_IDENTITY_V1",
            boundary.DynamicFormTemplateId, row.DynamicExcelTemplateId,
            row.BlockId, row.SheetId, row.RowKey,
            row.RowIndex.ToString(CultureInfo.InvariantCulture),
            row.LabelCode, row.Source);
        var occurrence = Hash("P10_ACTUAL_LABEL_OCCURRENCE_V1", stable,
            row.ReportId, row.OwnerRowId,
            row.Provenance.SourcePayloadRevision.ToString(
                CultureInfo.InvariantCulture),
            row.Provenance.SourceLifecycleRevision.ToString(
                CultureInfo.InvariantCulture));
        var semantic = Hash("P10_ACTUAL_LABEL_OBSERVATION_V2", occurrence,
            row.TypedValue.ValueIdentitySha256, row.RowState.SemanticSha256,
            row.Provenance.SemanticSha256, row.PeriodKey);
        if (stable != row.StableTypedIdentitySha256 ||
            occurrence != row.OccurrenceIdentitySha256 ||
            semantic != row.SemanticSha256)
            throw Fail("DIRECT_LABEL_SEMANTIC_MISMATCH");
    }

    private static ActualSourceMembershipDecision RequireCommon(
        ActualDirectProjectionBoundary boundary,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> decisions,
        string ownerRowId,
        string assignmentId,
        string reportId,
        string periodInstanceKey,
        string templateId,
        ActualDirectTypedValue typed,
        ActualDirectRowState state,
        ActualDirectProjectionProvenance provenance)
    {
        if (!decisions.TryGetValue(reportId, out var source) ||
            source.ObservedOwner.WorkAssignmentId != assignmentId ||
            periodInstanceKey != boundary.PeriodInstanceKey ||
            templateId != boundary.DynamicFormTemplateId ||
            provenance.SourceReportId != reportId ||
            provenance.SourcePayloadRevision != source.PayloadRevision ||
            provenance.SourcePayloadSha256 != source.PayloadSha256 ||
            provenance.SourceLifecycleRevision != source.LifecycleRevision)
            throw Fail("DIRECT_ROW_SOURCE_JOIN_INVALID");
        RequireTyped(typed);
        var ownerSemantic = Sha(state.OwnerRowSemanticSha256,
            "DIRECT_OWNER_ROW_SEMANTIC_SHA256");
        var baseState = Hash("P10_ACTUAL_DIRECT_ROW_STATE_V2",
            boundary.WorkId, assignmentId, reportId,
            provenance.SourcePayloadRevision.ToString(
                CultureInfo.InvariantCulture),
            provenance.SourcePayloadSha256, B(state.AssignmentIsActive),
            B(state.ReportIsActive), state.InvalidatedByFlowEventId,
            B(state.SourceMembershipMatched));
        var boundState = Hash("P10_ACTUAL_DIRECT_BOUND_ROW_STATE_V1",
            baseState, source.DecisionSemanticSha256, ownerSemantic);
        if (state.SourceDecisionSemanticSha256 !=
                source.DecisionSemanticSha256 ||
            state.SemanticSha256 != boundState)
            throw Fail("DIRECT_ROW_STATE_SEMANTIC_MISMATCH");
        RequireProvenance(boundary, source, provenance);
        _ = StatisticReconciliationActualCanonical.Required(ownerRowId,
            "DIRECT_OWNER_ROW_ID", 64);
        return source;
    }

    private static void RequireProvenance(
        ActualDirectProjectionBoundary boundary,
        ActualSourceMembershipDecision source,
        ActualDirectProjectionProvenance value)
    {
        if (value.RunId != boundary.RunId ||
            value.GenerationId != boundary.GenerationId ||
            value.OwnerGenerationSha256 !=
                boundary.OwnerGenerationSha256 ||
            value.LifecycleEventKey != boundary.OwnerLifecycleEventKey ||
            value.DirectSourceRevision != boundary.DirectSourceRevision ||
            value.DynamicFormFamilyId != boundary.DynamicFormFamilyId ||
            value.DynamicFormTemplateId !=
                boundary.DynamicFormTemplateId ||
            value.DynamicFormVersionNo != boundary.DynamicFormVersionNo ||
            value.DynamicFormSchemaSha256 !=
                boundary.DynamicFormSchemaSha256 ||
            value.ConfigId != boundary.ConfigId ||
            value.ConfigVersionId != boundary.ConfigVersionId ||
            value.ConfigVersionNo != boundary.ConfigVersionNo ||
            value.ConfigRevision != boundary.ConfigRevision ||
            value.ConfigSha256 != boundary.ConfigSha256 ||
            value.CandidateChainId != boundary.CandidateChainId ||
            value.CatalogVersion != boundary.CatalogVersion ||
            value.CatalogRawSha256 != boundary.CatalogRawSha256 ||
            value.CatalogSemanticSha256 !=
                boundary.CatalogSemanticSha256 ||
            value.SchemaRawSha256 != boundary.SchemaRawSha256 ||
            value.SchemaSemanticSha256 !=
                boundary.SchemaSemanticSha256 ||
            value.StageLockSha256 != boundary.StageLockSha256 ||
            value.OwnerMembershipSignature !=
                boundary.OwnerMembershipSignature ||
            new DateTimeOffset(value.ComputedAtUtc).ToUnixTimeMilliseconds() !=
            new DateTimeOffset(boundary.OwnerComputedAtUtc)
                .ToUnixTimeMilliseconds())
            throw Fail("DIRECT_PROVENANCE_BOUNDARY_MISMATCH");
        var semantic = Hash("P10_ACTUAL_DIRECT_PROVENANCE_V1",
            value.RunId, value.GenerationId, value.OwnerGenerationSha256,
            value.LifecycleEventKey, value.SourceReportId,
            value.SourcePayloadRevision.ToString(CultureInfo.InvariantCulture),
            value.SourcePayloadSha256,
            value.SourceLifecycleRevision.ToString(
                CultureInfo.InvariantCulture),
            I(value.DirectSourceRevision), value.DynamicFormFamilyId,
            value.DynamicFormTemplateId,
            value.DynamicFormVersionNo.ToString(CultureInfo.InvariantCulture),
            value.DynamicFormSchemaSha256, value.ConfigId,
            value.ConfigVersionId,
            value.ConfigVersionNo.ToString(CultureInfo.InvariantCulture),
            I(value.ConfigRevision), value.ConfigSha256,
            value.CandidateChainId, value.CatalogVersion,
            value.CatalogRawSha256, value.CatalogSemanticSha256,
            value.SchemaRawSha256, value.SchemaSemanticSha256,
            value.StageLockSha256, value.OwnerMembershipSignature,
            StatisticReconciliationActualCanonical.Instant(
                value.ComputedAtUtc));
        if (semantic != value.SemanticSha256 ||
            value.SourceReportId != source.ReportId)
            throw Fail("DIRECT_PROVENANCE_SEMANTIC_MISMATCH");
    }

    private static void RequireTyped(ActualDirectTypedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string canonical;
        switch (value.ValueKind)
        {
            case "MISSING":
                RequireNoChannels(value);
                canonical = "<missing>";
                break;
            case "NULL":
                RequireNoChannels(value);
                canonical = "<null>";
                break;
            case "EMPTY":
                RequireNoChannels(value);
                canonical = string.Empty;
                break;
            case "NUMBER" when value.NumericValue.HasValue &&
                               !value.BooleanValue.HasValue &&
                               !value.DateValueUtc.HasValue &&
                               value.TextValue is null &&
                               value.BucketKey is null:
                canonical = StatisticReconciliationActualCanonical.Number(
                    value.NumericValue.Value);
                break;
            case "BOOLEAN" when value.BooleanValue.HasValue &&
                                !value.NumericValue.HasValue &&
                                !value.DateValueUtc.HasValue &&
                                value.TextValue is null &&
                                value.BucketKey is null:
                canonical = B(value.BooleanValue.Value);
                break;
            case "DATE" when value.DateValueUtc.HasValue &&
                             !value.NumericValue.HasValue &&
                             !value.BooleanValue.HasValue &&
                             value.TextValue is null &&
                             value.BucketKey is null:
                canonical = StatisticReconciliationActualCanonical.Instant(
                    value.DateValueUtc.Value);
                break;
            case "OPTION" when value.BucketKey is not null &&
                               !value.NumericValue.HasValue &&
                               !value.BooleanValue.HasValue &&
                               !value.DateValueUtc.HasValue &&
                               value.TextValue is null:
                canonical = value.BucketKey;
                break;
            case "TEXT_BUCKET" when value.BucketKey is not null &&
                                    value.TextValue is not null &&
                                    !value.NumericValue.HasValue &&
                                    !value.BooleanValue.HasValue &&
                                    !value.DateValueUtc.HasValue:
                canonical = value.BucketKey;
                break;
            case "TEXT" when value.TextValue is not null &&
                              !value.NumericValue.HasValue &&
                              !value.BooleanValue.HasValue &&
                              !value.DateValueUtc.HasValue:
                canonical = value.BucketKey ?? value.TextValue;
                break;
            case "PRESENT" or "ROW_LABEL" when
                value.TextValue is not null && value.BucketKey is null &&
                !value.NumericValue.HasValue &&
                !value.BooleanValue.HasValue &&
                !value.DateValueUtc.HasValue:
                canonical = value.TextValue;
                break;
            default:
                throw Fail("DIRECT_TYPED_VALUE_CHANNEL_INVALID");
        }
        var identity = Hash("P10_ACTUAL_DIRECT_TYPED_VALUE_V2",
            value.ValueKind, canonical, value.BucketKey, value.TextValue);
        if (canonical != value.CanonicalValue ||
            identity != value.ValueIdentitySha256)
            throw Fail("DIRECT_TYPED_VALUE_SEMANTIC_MISMATCH");
    }

    private static void RequireNoChannels(ActualDirectTypedValue value)
    {
        if (value.NumericValue.HasValue || value.BooleanValue.HasValue ||
            value.DateValueUtc.HasValue || value.TextValue is not null ||
            value.BucketKey is not null)
            throw Fail("DIRECT_EMPTY_VALUE_CHANNEL_INVALID");
    }

    private static ImmutableArray<OwnerOccurrence> Select(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        ActualDirectProjectionCapture direct)
        => descriptor.Kind switch
        {
            "FIELD" => direct.FieldRows.Where(row =>
                    row.PeriodKey == descriptor.PeriodKey &&
                    row.FieldId == descriptor.FieldId &&
                    row.StatisticLabelCodes.Contains(descriptor.MetricId,
                        StringComparer.Ordinal))
                .Select(row => new OwnerOccurrence(row.ReportId,
                    row.SemanticSha256, row.OccurrenceIdentitySha256,
                    row.FieldType, row.TypedValue, row.RowState))
                .OrderBy(value => value.OccurrenceSha256,
                    StringComparer.Ordinal).ToImmutableArray(),
            "TABLE" => direct.TableMetricRows.Where(row =>
                    row.PeriodKey == descriptor.PeriodKey &&
                    row.BlockId == descriptor.TableId &&
                    row.ConceptCode is not null &&
                    row.ConceptCode == descriptor.MetricId)
                .Select(row => new OwnerOccurrence(row.ReportId,
                    row.SemanticSha256, row.OccurrenceIdentitySha256,
                    row.DataType, row.TypedValue, row.RowState))
                .OrderBy(value => value.OccurrenceSha256,
                    StringComparer.Ordinal).ToImmutableArray(),
            "ROW_LABEL" => direct.RowLabelRows.Where(row =>
                    row.PeriodKey == descriptor.PeriodKey &&
                    row.BlockId == descriptor.TableId &&
                    row.RowKey == descriptor.RowId &&
                    row.LabelCode == descriptor.LabelId &&
                    descriptor.MetricId == row.LabelCode)
                .Select(row => new OwnerOccurrence(row.ReportId,
                    row.SemanticSha256, row.OccurrenceIdentitySha256,
                    "ROW_LABEL", row.TypedValue, row.RowState))
                .OrderBy(value => value.OccurrenceSha256,
                    StringComparer.Ordinal).ToImmutableArray(),
            _ => throw Fail("DIRECT_DESCRIPTOR_KIND_UNSUPPORTED")
        };

    private static SamplePartition Samples(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        ImmutableArray<OwnerOccurrence> occurrences,
        ImmutableArray<StatisticReconciliationActualRawPayloadEnvelope>
            sources)
    {
        if (occurrences.Any(value => sources.All(source =>
                source.ReportId != value.ReportId)))
            throw Fail("DIRECT_OWNER_REPORT_NOT_IN_RAW_SET");
        var samples = ImmutableArray.CreateBuilder<OwnerSample>();
        var pins = ImmutableArray.CreateBuilder<string>();
        long rowCount = 0;
        foreach (var source in sources)
        {
            var rows = occurrences.Where(value =>
                    value.ReportId == source.ReportId)
                .OrderBy(value => value.OccurrenceSha256,
                    StringComparer.Ordinal).ToImmutableArray();
            if (rows.Length == 0 && descriptor.Kind == "FIELD")
                throw Fail("DIRECT_FIELD_REPORT_ROW_REQUIRED");
            if (!descriptor.ExpandArray && rows.Length > 1)
                throw Fail("DIRECT_OWNER_SCALAR_CARDINALITY_INVALID");
            if (descriptor.ExpandArray && rows.Length > 1 &&
                (descriptor.ValueType is not ("BUCKET" or "ENUM") ||
                 rows.Any(value => value.TypedValue.ValueKind is
                     "MISSING" or "NULL" or "EMPTY")))
                throw Fail("DIRECT_OWNER_ARRAY_CARDINALITY_INVALID");
            if (rows.Any(value => !value.State.AssignmentIsActive ||
                    !value.State.ReportIsActive ||
                    value.State.InvalidatedByFlowEventId is not null ||
                    !value.State.SourceMembershipMatched))
                throw Fail("DIRECT_OWNER_ROW_INACTIVE");

            var rawReportSamples = RawReportSamples(descriptor, source);
            var ownerReportSamples = rows.Length == 0
                ? ImmutableArray.Create(OwnerSample.Missing)
                : rows.Select(value => ToSample(descriptor, value))
                    .ToImmutableArray();
            var orderedRaw = OrderSamples(rawReportSamples);
            var orderedOwner = OrderSamples(ownerReportSamples);
            if (!orderedRaw.SequenceEqual(orderedOwner))
                throw Fail("DIRECT_OWNER_REPORT_VALUE_MISMATCH");

            samples.AddRange(ownerReportSamples);
            if (descriptor.ExpandArray && ownerReportSamples.All(value =>
                    value.State == "VALUE"))
                rowCount = checked(rowCount + ownerReportSamples.Length);
            var rawSampleManifest = HSeq(
                "P10_ACTUAL_SUMMARY_OWNER_DIRECT_REPORT_RAW_SAMPLES_V1",
                orderedRaw.Select(SampleSemantic));
            var ownerRowsManifest = HSeq(
                "P10_ACTUAL_SUMMARY_OWNER_DIRECT_REPORT_ROWS_V1",
                rows.Select(value => Hash(
                    "P10_ACTUAL_SUMMARY_OWNER_DIRECT_ROW_PIN_V1",
                    value.SemanticSha256, value.OccurrenceSha256)));
            pins.Add(Hash(
                "P10_ACTUAL_SUMMARY_OWNER_DIRECT_REPORT_RELATION_V1",
                source.SourceStableIdentitySha256,
                source.EnvelopeSemanticSha256,
                rawSampleManifest,
                ownerRowsManifest));
        }
        return new(samples.ToImmutable(), sources.Length, rowCount,
            pins.ToImmutable());
    }

    private static ImmutableArray<OwnerSample> RawReportSamples(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        StatisticReconciliationActualRawPayloadEnvelope source)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            source.CanonicalPayloadJson, "DIRECT_RAW_REPORT_PAYLOAD");
        if (!TryResolvePointer(document.RootElement, descriptor.JsonPointer,
                out var value))
            return [OwnerSample.Missing];
        if (descriptor.ExpandArray && value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() == 0)
                return [OwnerSample.Empty];
            return value.EnumerateArray()
                .Select(item => NormalizeRawSample(item, descriptor.ValueType))
                .ToImmutableArray();
        }
        return [NormalizeRawSample(value, descriptor.ValueType)];
    }

    private static OwnerSample NormalizeRawSample(
        JsonElement value,
        string valueType)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return OwnerSample.Null;
        if (value.ValueKind == JsonValueKind.String &&
            value.GetString()!.Length == 0)
            return OwnerSample.Empty;
        if (value.ValueKind == JsonValueKind.Array &&
            value.GetArrayLength() == 0)
            return OwnerSample.Empty;
        return valueType switch
        {
            "NUMBER" when value.ValueKind == JsonValueKind.Number &&
                          value.TryGetDecimal(out var number) =>
                NumberSample(number),
            "BOOLEAN" when value.ValueKind == JsonValueKind.True =>
                new("VALUE", "true", 0),
            "BOOLEAN" when value.ValueKind == JsonValueKind.False =>
                new("VALUE", "false", 0),
            "BUCKET" when value.ValueKind == JsonValueKind.String =>
                new("VALUE", "S:" + value.GetString(), 0),
            "ENUM" when value.ValueKind == JsonValueKind.String =>
                new("VALUE", value.GetString()!, 0),
            "FULL_DATE" when value.ValueKind == JsonValueKind.String =>
                FullDateSample(value.GetString()!),
            "TEXT" when value.ValueKind == JsonValueKind.String =>
                new("VALUE", value.GetString()!, 0),
            _ => throw Fail("DIRECT_RAW_REPORT_VALUE_UNSUPPORTED")
        };
    }

    private static OwnerSample NumberSample(decimal value)
    {
        var canonical = StatisticReconciliationActualCanonical.Number(value);
        return new("VALUE", canonical, DecimalScale(canonical));
    }

    private static OwnerSample FullDateSample(string value)
    {
        if (!DateTime.TryParseExact(value, "dd/MM/yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Fail("DIRECT_RAW_REPORT_FULL_DATE_INVALID");
        return new("VALUE", "DAY:" +
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 0);
    }

    private static ImmutableArray<OwnerSample> OrderSamples(
        IEnumerable<OwnerSample> values)
        => values.OrderBy(value => value.State, StringComparer.Ordinal)
            .ThenBy(value => value.CanonicalValue, StringComparer.Ordinal)
            .ThenBy(value => value.DecimalScale)
            .ToImmutableArray();

    private static string SampleSemantic(OwnerSample value)
        => Hash("P10_ACTUAL_SUMMARY_OWNER_DIRECT_REPORT_SAMPLE_V1",
            value.State, value.CanonicalValue, I(value.DecimalScale));

    private static bool TryResolvePointer(
        JsonElement root,
        string pointer,
        out JsonElement value)
    {
        value = root;
        if (pointer.Length == 0)
            return true;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out value))
                    return false;
                continue;
            }
            if (value.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var index) &&
                index >= 0 && (segment == "0" || !segment.StartsWith('0')) &&
                index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }
            return false;
        }
        return true;
    }
    private static OwnerSample ToSample(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        OwnerOccurrence occurrence)
    {
        var value = occurrence.TypedValue;
        var declared = occurrence.DeclaredType.Replace("_", string.Empty,
            StringComparison.Ordinal).ToUpperInvariant();
        if (!DeclaredTypeMatches(descriptor.ValueType, declared,
                value.ValueKind))
            throw Fail("DIRECT_OWNER_DECLARED_TYPE_MISMATCH");
        return value.ValueKind switch
        {
            "MISSING" => OwnerSample.Missing,
            "NULL" => OwnerSample.Null,
            "EMPTY" => OwnerSample.Empty,
            "NUMBER" when descriptor.ValueType == "NUMBER" &&
                          value.NumericValue.HasValue =>
                new("VALUE",
                    StatisticReconciliationActualCanonical.Number(
                        value.NumericValue.Value),
                    DecimalScale(StatisticReconciliationActualCanonical.Number(
                        value.NumericValue.Value))),
            "BOOLEAN" when descriptor.ValueType == "BOOLEAN" &&
                           value.BooleanValue.HasValue =>
                new("VALUE", B(value.BooleanValue.Value), 0),
            "OPTION" or "TEXT_BUCKET" when
                descriptor.ValueType == "BUCKET" &&
                value.BucketKey is not null =>
                new("VALUE", "S:" + value.BucketKey, 0),
            "OPTION" when descriptor.ValueType == "ENUM" &&
                          value.BucketKey is not null =>
                new("VALUE", value.BucketKey, 0),
            "DATE" when descriptor.ValueType == "FULL_DATE" &&
                        value.DateValueUtc.HasValue &&
                        value.DateValueUtc.Value.TimeOfDay == TimeSpan.Zero =>
                new("VALUE", "DAY:" + value.DateValueUtc.Value.ToUniversalTime()
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 0),
            "TEXT" or "PRESENT" when descriptor.ValueType == "TEXT" &&
                                        value.TextValue is not null =>
                new("VALUE", value.TextValue, 0),
            "ROW_LABEL" when descriptor.ValueType == "TEXT" &&
                              value.TextValue is not null =>
                new("VALUE", value.TextValue, 0),
            _ => throw Fail("DIRECT_VALUE_TYPE_OWNER_UNSUPPORTED")
        };
    }

    private static bool DeclaredTypeMatches(
        string valueType,
        string declared,
        string valueKind)
    {
        if (valueKind is "MISSING" or "NULL" or "EMPTY")
            return valueType switch
            {
                "NUMBER" => declared == "NUMBER",
                "BOOLEAN" => declared == "BOOLEAN",
                "BUCKET" or "ENUM" => declared is
                    "SINGLESELECT" or "MULTISELECT" or "SHORTTEXT" or
                    "OPTION" or "TEXTBUCKET",
                "FULL_DATE" => declared is "DATE" or "FULLDATE",
                "TEXT" => declared is
                    "TEXT" or "SHORTTEXT" or "ROWLABEL",
                _ => false
            };
        return valueType switch
        {
            "NUMBER" => declared == "NUMBER" && valueKind == "NUMBER",
            "BOOLEAN" => declared == "BOOLEAN" && valueKind == "BOOLEAN",
            "BUCKET" => declared is
                    "SINGLESELECT" or "MULTISELECT" or "SHORTTEXT" or
                    "OPTION" or "TEXTBUCKET" &&
                valueKind is "OPTION" or "TEXT_BUCKET",
            "ENUM" => declared is "SINGLESELECT" or "MULTISELECT" or
                    "OPTION" && valueKind == "OPTION",
            "FULL_DATE" => declared is "DATE" or "FULLDATE" &&
                valueKind == "DATE",
            "TEXT" => declared is "TEXT" or "SHORTTEXT" or "ROWLABEL" &&
                valueKind is "TEXT" or "PRESENT" or "ROW_LABEL",
            _ => false
        };
    }

    private static void RequireLosslessDescriptor(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor)
    {
        if (descriptor.Family != "DIRECT" ||
            descriptor.ValueType is "DATE" or "PERIOD" or "STRING_LIST" ||
            descriptor.ValueType is not
                ("NUMBER" or "BOOLEAN" or "BUCKET" or "ENUM" or
                 "FULL_DATE" or "TEXT") ||
            descriptor.TransitionMode != "NONE" ||
            descriptor.TransitionLegs.IsDefault ||
            !descriptor.TransitionLegs.SequenceEqual(["NONE"]) ||
            descriptor.TransitionKind is not null ||
            descriptor.BeforeJsonPointer is not null ||
            descriptor.AfterJsonPointer is not null ||
            descriptor.DifferenceOperation is not null)
            throw Fail("DIRECT_VALUE_TYPE_OWNER_UNSUPPORTED");
    }

    private static ImmutableArray<StatisticReconciliationActualRawSummaryAtom>
        Compile(
            StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
            ImmutableArray<OwnerSample> samples,
            long reportCount,
            long rowCount)
    {
        var values = samples.Where(value => value.State == "VALUE").ToArray();
        var numeric = descriptor.ValueType == "NUMBER"
            ? values.Select(value => decimal.Parse(value.CanonicalValue,
                NumberStyles.AllowLeadingSign |
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture)).ToArray()
            : [];
        var numericCount = (long)numeric.Length;
        var atoms = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualRawSummaryAtom>();
        atoms.Add(CountAtom(descriptor, "REPORT_COUNT", reportCount,
            reportCount, rowCount, numericCount));
        atoms.Add(CountAtom(descriptor, "ROW_COUNT", rowCount,
            reportCount, rowCount, numericCount));
        atoms.Add(CountAtom(descriptor, "COUNT", values.LongLength,
            reportCount, rowCount, numericCount));
        atoms.Add(CountAtom(descriptor, "NUMERIC_VALUE_COUNT", numericCount,
            reportCount, rowCount, numericCount));
        foreach (var state in new[] { "MISSING", "NULL", "EMPTY" })
        {
            var count = samples.LongCount(value => value.State == state);
            atoms.Add(Atom(descriptor, state, descriptor.ValueType, state,
                I(count), 0, count, reportCount, rowCount, numericCount,
                descriptor.CollectionSemantics));
        }
        decimal? sum = null;
        if (numeric.Length > 0 &&
            (descriptor.Operations.Contains("SUM") ||
             descriptor.Operations.Contains("MEAN")))
        {
            decimal current = 0;
            foreach (var value in numeric)
                current = checked(current + value);
            sum = current;
        }
        if (descriptor.Operations.Contains("SUM"))
            atoms.Add(Aggregate(descriptor, "SUM", sum, reportCount,
                rowCount, numericCount));
        if (descriptor.Operations.Contains("MIN"))
            atoms.Add(MinMax(descriptor, true, values, numeric,
                reportCount, rowCount, numericCount));
        if (descriptor.Operations.Contains("MAX"))
            atoms.Add(MinMax(descriptor, false, values, numeric,
                reportCount, rowCount, numericCount));
        if (descriptor.Operations.Contains("MEAN"))
            atoms.Add(Aggregate(descriptor, "MEAN",
                numericCount == 0 ? null : sum!.Value / numericCount,
                reportCount, rowCount, numericCount));
        if (descriptor.Operations.Contains("VALUES") &&
            descriptor.ValueType != "NUMBER")
        {
            foreach (var group in values.GroupBy(value =>
                         value.CanonicalValue, StringComparer.Ordinal)
                     .OrderBy(value => value.Key, StringComparer.Ordinal))
                atoms.Add(Atom(descriptor, ValueAtomKind(descriptor.ValueType),
                    descriptor.ValueType, "VALUE", group.Key,
                    group.First().DecimalScale, group.LongCount(), reportCount,
                    rowCount, numericCount, descriptor.CollectionSemantics));
        }
        var ordered = atoms.OrderBy(value => value.IdentitySha256,
                StringComparer.Ordinal)
            .ThenBy(value => value.TransitionLeg, StringComparer.Ordinal)
            .ThenBy(value => value.AtomKind, StringComparer.Ordinal)
            .ThenBy(value => value.CanonicalValue, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!descriptor.AtomKinds.ToImmutableHashSet(StringComparer.Ordinal)
                .SetEquals(ordered.Select(value => value.AtomKind)))
            throw Fail("DIRECT_RECOMPUTED_ATOM_PARTITION_INVALID");
        return ordered;
    }

    private static StatisticReconciliationActualRawSummaryAtom CountAtom(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string kind,
        long value,
        long reportCount,
        long rowCount,
        long numericCount)
        => Atom(descriptor, kind, "NUMBER", "VALUE", I(value), 0, 1,
            reportCount, rowCount, numericCount, null);

    private static StatisticReconciliationActualRawSummaryAtom Aggregate(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string kind,
        decimal? value,
        long reportCount,
        long rowCount,
        long numericCount)
    {
        var canonical = value.HasValue
            ? StatisticReconciliationActualCanonical.Number(value.Value)
            : string.Empty;
        return Atom(descriptor, kind, "NUMBER",
            value.HasValue ? "VALUE" : "MISSING", canonical,
            value.HasValue ? DecimalScale(canonical) : 0,
            value.HasValue ? 1 : 0, reportCount, rowCount, numericCount,
            null);
    }

    private static StatisticReconciliationActualRawSummaryAtom MinMax(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        bool minimum,
        IReadOnlyList<OwnerSample> values,
        IReadOnlyList<decimal> numeric,
        long reportCount,
        long rowCount,
        long numericCount)
    {
        if (descriptor.ValueType == "NUMBER")
        {
            decimal? value = numeric.Count == 0
                ? null
                : minimum ? numeric.Min() : numeric.Max();
            return Aggregate(descriptor, minimum ? "MIN" : "MAX", value,
                reportCount, rowCount, numericCount);
        }
        var canonical = values.Count == 0
            ? null
            : minimum
                ? values.MinBy(value => value.CanonicalValue,
                    StringComparer.Ordinal)!.CanonicalValue
                : values.MaxBy(value => value.CanonicalValue,
                    StringComparer.Ordinal)!.CanonicalValue;
        return Atom(descriptor, minimum ? "MIN" : "MAX",
            descriptor.ValueType, canonical is null ? "MISSING" : "VALUE",
            canonical ?? string.Empty, 0, canonical is null ? 0 : 1,
            reportCount, rowCount, numericCount,
            descriptor.CollectionSemantics);
    }

    private static StatisticReconciliationActualRawSummaryAtom Atom(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string kind,
        string valueType,
        string state,
        string canonical,
        int scale,
        long occurrenceCount,
        long reportCount,
        long rowCount,
        long numericCount,
        string? collection)
    {
        var semantic = AtomSemantic(descriptor, kind, valueType, state,
            canonical, scale, occurrenceCount, reportCount, rowCount,
            numericCount, "NONE", descriptor.TransitionKind, collection);
        return new(descriptor.SemanticSha256, descriptor.IdentitySha256,
            descriptor.Family, descriptor.Kind, descriptor.MetricId,
            descriptor.FieldId, descriptor.TableId, descriptor.RowId,
            descriptor.LabelId, descriptor.BasicScope,
            descriptor.BasicScopeId, descriptor.AdvancedGrain,
            descriptor.DiffKind, descriptor.PeriodKey, kind, valueType, state,
            canonical, scale, occurrenceCount, reportCount, rowCount,
            numericCount, "NONE", descriptor.TransitionKind, collection,
            semantic);
    }

    private static string AtomSemantic(
        StatisticReconciliationActualSummaryIdentityDescriptor descriptor,
        string kind,
        string valueType,
        string state,
        string canonical,
        int scale,
        long occurrenceCount,
        long reportCount,
        long rowCount,
        long numericCount,
        string leg,
        string? transitionKind,
        string? collection)
        => Hash("P10_ACTUAL_RAW_SUMMARY_ATOM_V1",
            descriptor.SemanticSha256, descriptor.IdentitySha256, leg,
            transitionKind ?? "~", collection ?? "~", kind, valueType,
            state, canonical, I(scale), I(occurrenceCount), I(reportCount),
            I(rowCount), I(numericCount));

    private static string ValueAtomKind(string valueType)
        => valueType switch
        {
            "BUCKET" => "BUCKET",
            "FULL_DATE" => "FULL_DATE",
            "BOOLEAN" => "BOOLEAN",
            "ENUM" => "ENUM",
            "TEXT" => "TEXT",
            _ => throw Fail("DIRECT_VALUES_OPERATION_TYPE_INVALID")
        };

    private static int DecimalScale(string canonical)
    {
        var point = canonical.IndexOf('.');
        return point < 0 ? 0 : canonical.Length - point - 1;
    }

    private static string Hash(string domain, params string?[] values)
        => StatisticReconciliationActualCanonical.Hash(domain, values);
    private static string HSeq(string domain, IEnumerable<string> values)
        => StatisticReconciliationActualCanonical.HashSequence(domain, values);
    private static string Sha(string? value, string name)
        => StatisticReconciliationActualCanonical.Sha256(value, name);
    private static string I(long value)
        => StatisticReconciliationActualCanonical.Integer(value);
    private static string B(bool value)
        => StatisticReconciliationActualCanonical.Boolean(value);
    private static StatisticReconciliationActualObservationException Fail(
        string reason) => new($"DIRECT_RAW_OWNER_PARITY:{reason}");

    private sealed record OwnerOccurrence(
        string ReportId,
        string SemanticSha256,
        string OccurrenceSha256,
        string DeclaredType,
        ActualDirectTypedValue TypedValue,
        ActualDirectRowState State);

    private sealed record OwnerSample(
        string State,
        string CanonicalValue,
        int DecimalScale)
    {
        internal static readonly OwnerSample Missing =
            new("MISSING", string.Empty, 0);
        internal static readonly OwnerSample Null =
            new("NULL", string.Empty, 0);
        internal static readonly OwnerSample Empty =
            new("EMPTY", string.Empty, 0);
    }

    private sealed record SamplePartition(
        ImmutableArray<OwnerSample> Values,
        long ReportCount,
        long RowCount,
        ImmutableArray<string> OccurrencePins);
}
