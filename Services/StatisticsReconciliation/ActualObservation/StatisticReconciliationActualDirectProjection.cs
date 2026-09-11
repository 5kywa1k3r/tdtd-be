using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record ActualDirectProjectionBoundary(
    string WorkId,
    string PeriodInstanceKey,
    string DynamicFormFamilyId,
    string DynamicFormTemplateId,
    int DynamicFormVersionNo,
    string DynamicFormSchemaSha256,
    string RunId,
    string GenerationId,
    string OwnerGenerationSha256,
    string OwnerLifecycleEventKey,
    DateTime OwnerComputedAtUtc,
    long DirectSourceRevision,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    long ConfigRevision,
    string ConfigSha256,
    string CandidateChainId,
    string CatalogVersion,
    string CatalogRawSha256,
    string CatalogSemanticSha256,
    string SchemaRawSha256,
    string SchemaSemanticSha256,
    string StageLockSha256,
    string OwnerMembershipSignature);

internal interface IStatisticReconciliationActualDirectProjectionOwnerReader
{
    Task<IReadOnlyList<WorkReportFieldStatValue>> ReadFieldRowsAsync(
        ActualDirectProjectionBoundary boundary,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkReportTableStatValue>> ReadTableMetricRowsAsync(
        ActualDirectProjectionBoundary boundary,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkReportLabelStatValue>> ReadRowLabelRowsAsync(
        ActualDirectProjectionBoundary boundary,
        CancellationToken cancellationToken);
}

internal sealed record ActualDirectProjectionProvenance(
    string RunId,
    string GenerationId,
    string OwnerGenerationSha256,
    string LifecycleEventKey,
    string SourceReportId,
    int SourcePayloadRevision,
    string SourcePayloadSha256,
    int SourceLifecycleRevision,
    long DirectSourceRevision,
    string DynamicFormFamilyId,
    string DynamicFormTemplateId,
    int DynamicFormVersionNo,
    string DynamicFormSchemaSha256,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    long ConfigRevision,
    string ConfigSha256,
    string CandidateChainId,
    string CatalogVersion,
    string CatalogRawSha256,
    string CatalogSemanticSha256,
    string SchemaRawSha256,
    string SchemaSemanticSha256,
    string StageLockSha256,
    string OwnerMembershipSignature,
    DateTime ComputedAtUtc,
    string SemanticSha256);

internal sealed record ActualDirectTypedValue(
    string ValueKind,
    string CanonicalValue,
    string? BucketKey,
    decimal? NumericValue,
    bool? BooleanValue,
    DateTime? DateValueUtc,
    string? TextValue,
    string ValueIdentitySha256);

internal sealed record ActualDirectRowState(
    bool AssignmentIsActive,
    bool ReportIsActive,
    string? InvalidatedByFlowEventId,
    bool SourceMembershipMatched,
    string? SourceDecisionSemanticSha256,
    string OwnerRowSemanticSha256,
    string SemanticSha256);

internal sealed record ActualFieldProjectionObservation(
    string OwnerRowId,
    string WorkAssignmentId,
    string ReportId,
    string PeriodKey,
    string PeriodInstanceKey,
    string DynamicFormTemplateId,
    string FieldId,
    string FieldKey,
    string FieldType,
    string? ConceptCode,
    ImmutableArray<string> StatisticLabelCodes,
    string? BucketKey,
    string SourceKey,
    ActualDirectTypedValue TypedValue,
    ActualDirectRowState RowState,
    ActualDirectProjectionProvenance Provenance,
    string StableTypedIdentitySha256,
    string OccurrenceIdentitySha256,
    string SemanticSha256);

internal sealed record ActualTableMetricProjectionObservation(
    string OwnerRowId,
    string WorkAssignmentId,
    string ReportId,
    string PeriodKey,
    string PeriodInstanceKey,
    string DynamicFormTemplateId,
    string? DynamicExcelTemplateId,
    string BlockId,
    string TableMode,
    string MetricKey,
    string RowKey,
    string ColumnKey,
    string SourceKey,
    string DataType,
    string? ConceptCode,
    string? BucketKey,
    ActualDirectTypedValue TypedValue,
    ActualDirectRowState RowState,
    ActualDirectProjectionProvenance Provenance,
    string StableTypedIdentitySha256,
    string OccurrenceIdentitySha256,
    string SemanticSha256);

internal sealed record ActualRowLabelProjectionObservation(
    string OwnerRowId,
    string WorkAssignmentId,
    string ReportId,
    string WorkReportPeriodId,
    string PeriodKey,
    string PeriodInstanceKey,
    string DynamicFormTemplateId,
    string? DynamicExcelTemplateId,
    string BlockId,
    string? SheetId,
    string RowKey,
    int RowIndex,
    string LabelCode,
    string Source,
    ActualDirectTypedValue TypedValue,
    ActualDirectRowState RowState,
    ActualDirectProjectionProvenance Provenance,
    string StableTypedIdentitySha256,
    string OccurrenceIdentitySha256,
    string SemanticSha256);

internal sealed record ActualDirectProjectionCapture(
    ActualDirectProjectionBoundary Boundary,
    string ActualSourceSetSha256,
    string BoundarySemanticSha256,
    ImmutableArray<ActualFieldProjectionObservation> FieldRows,
    ImmutableArray<ActualTableMetricProjectionObservation> TableMetricRows,
    ImmutableArray<ActualRowLabelProjectionObservation> RowLabelRows,
    int TotalRowCount,
    string CaptureSemanticSha256);

internal sealed class StatisticReconciliationActualDirectProjectionAdapter
{
    internal const int MaxProjectionRows = 100_000;

    internal async Task<ActualDirectProjectionCapture> CaptureAsync(
        ActualDirectProjectionBoundary boundary,
        ActualSourceMembershipCapture membership,
        IStatisticReconciliationActualDirectProjectionOwnerReader reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(reader);
        var normalized = NormalizeBoundary(boundary);
        RequireMembershipBinding(normalized, membership);

        var fieldRows = await reader.ReadFieldRowsAsync(normalized, cancellationToken).ConfigureAwait(false)
            ?? throw new StatisticReconciliationActualObservationException("DIRECT_FIELD_ROWS_NULL");
        var tableRows = await reader.ReadTableMetricRowsAsync(normalized, cancellationToken).ConfigureAwait(false)
            ?? throw new StatisticReconciliationActualObservationException("DIRECT_TABLE_ROWS_NULL");
        var labelRows = await reader.ReadRowLabelRowsAsync(normalized, cancellationToken).ConfigureAwait(false)
            ?? throw new StatisticReconciliationActualObservationException("DIRECT_LABEL_ROWS_NULL");
        var total = checked(fieldRows.Count + tableRows.Count + labelRows.Count);
        if (total > MaxProjectionRows)
            throw new StatisticReconciliationActualObservationException("DIRECT_ROWS_LIMIT");

        var sources = membership.IncludedSources.ToDictionary(x => x.ReportId, StringComparer.Ordinal);
        var fieldOwnerIds = new HashSet<string>(StringComparer.Ordinal);
        var tableOwnerIds = new HashSet<string>(StringComparer.Ordinal);
        var labelOwnerIds = new HashSet<string>(StringComparer.Ordinal);
        var fields = fieldRows
            .Select(row => AdaptField(normalized, sources, fieldOwnerIds, row))
            .OrderBy(x => x.PeriodKey, StringComparer.Ordinal)
            .ThenBy(x => x.FieldKey, StringComparer.Ordinal)
            .ThenBy(x => x.BucketKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.ReportId, StringComparer.Ordinal)
            .ThenBy(x => x.SourceKey, StringComparer.Ordinal)
            .ThenBy(x => x.OwnerRowId, StringComparer.Ordinal)
            .ToImmutableArray();
        var tables = tableRows
            .Select(row => AdaptTable(normalized, sources, tableOwnerIds, row))
            .OrderBy(x => x.PeriodKey, StringComparer.Ordinal)
            .ThenBy(x => x.BlockId, StringComparer.Ordinal)
            .ThenBy(x => x.MetricKey, StringComparer.Ordinal)
            .ThenBy(x => x.RowKey, StringComparer.Ordinal)
            .ThenBy(x => x.ColumnKey, StringComparer.Ordinal)
            .ThenBy(x => x.ReportId, StringComparer.Ordinal)
            .ThenBy(x => x.SourceKey, StringComparer.Ordinal)
            .ThenBy(x => x.OwnerRowId, StringComparer.Ordinal)
            .ToImmutableArray();
        var labels = labelRows
            .Select(row => AdaptLabel(normalized, sources, labelOwnerIds, row))
            .OrderBy(x => x.PeriodKey, StringComparer.Ordinal)
            .ThenBy(x => x.BlockId, StringComparer.Ordinal)
            .ThenBy(x => x.LabelCode, StringComparer.Ordinal)
            .ThenBy(x => x.RowKey, StringComparer.Ordinal)
            .ThenBy(x => x.RowIndex)
            .ThenBy(x => x.ReportId, StringComparer.Ordinal)
            .ThenBy(x => x.OwnerRowId, StringComparer.Ordinal)
            .ToImmutableArray();

        var boundarySha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_DIRECT_PROJECTION_BOUNDARY_V1",
            normalized.WorkId,
            normalized.PeriodInstanceKey,
            normalized.DynamicFormFamilyId,
            normalized.DynamicFormTemplateId,
            normalized.DynamicFormVersionNo.ToString(CultureInfo.InvariantCulture),
            normalized.DynamicFormSchemaSha256,
            normalized.RunId,
            normalized.GenerationId,
            normalized.OwnerGenerationSha256,
            normalized.OwnerLifecycleEventKey,
            StatisticReconciliationActualCanonical.Instant(normalized.OwnerComputedAtUtc),
            StatisticReconciliationActualCanonical.Integer(normalized.DirectSourceRevision),
            normalized.ConfigId,
            normalized.ConfigVersionId,
            normalized.ConfigVersionNo.ToString(CultureInfo.InvariantCulture),
            StatisticReconciliationActualCanonical.Integer(normalized.ConfigRevision),
            normalized.ConfigSha256,
            normalized.CandidateChainId,
            normalized.CatalogVersion,
            normalized.CatalogRawSha256,
            normalized.CatalogSemanticSha256,
            normalized.SchemaRawSha256,
            normalized.SchemaSemanticSha256,
            normalized.StageLockSha256,
            normalized.OwnerMembershipSignature,
            membership.CaptureSemanticSha256);
        var captureSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_DIRECT_PROJECTION_CAPTURE_V2",
            boundarySha,
            membership.SourceSetSha256,
            StatisticReconciliationActualCanonical.HashSequence("P10_ACTUAL_DIRECT_FIELD_ROWS_V1", fields.Select(x => x.SemanticSha256)),
            StatisticReconciliationActualCanonical.HashSequence("P10_ACTUAL_DIRECT_TABLE_ROWS_V1", tables.Select(x => x.SemanticSha256)),
            StatisticReconciliationActualCanonical.HashSequence("P10_ACTUAL_DIRECT_LABEL_ROWS_V1", labels.Select(x => x.SemanticSha256)));

        return new ActualDirectProjectionCapture(
            normalized,
            membership.SourceSetSha256,
            boundarySha,
            fields,
            tables,
            labels,
            total,
            captureSha);
    }

    internal static string OwnerStableObjectId(string generationId, string owner, params string?[] identity)
    {
        var material = string.Join(
            "\n",
            new[]
                {
                    "P9_DIRECT_ROW_V1",
                    StatisticReconciliationActualCanonical.Sha256(generationId, "OWNER_GENERATION_ID"),
                    StatisticReconciliationActualCanonical.Required(owner, "OWNER_KIND")
                }
                .Concat(identity.Select(value => value ?? "<null>")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant()[..24];
    }

    private static ActualDirectProjectionBoundary NormalizeBoundary(ActualDirectProjectionBoundary value)
    {
        if (value.DynamicFormVersionNo < 1 || value.DirectSourceRevision < 1
            || value.ConfigVersionNo < 1 || value.ConfigRevision < 1)
        {
            throw new StatisticReconciliationActualObservationException("DIRECT_BOUNDARY_REVISION_INVALID");
        }
        return value with
        {
            WorkId = StatisticReconciliationActualCanonical.Required(value.WorkId, "DIRECT_WORK_ID"),
            PeriodInstanceKey = StatisticReconciliationActualCanonical.Required(value.PeriodInstanceKey, "DIRECT_PERIOD_INSTANCE_KEY"),
            DynamicFormFamilyId = StatisticReconciliationActualCanonical.Required(value.DynamicFormFamilyId, "DIRECT_FORM_FAMILY_ID"),
            DynamicFormTemplateId = StatisticReconciliationActualCanonical.Required(value.DynamicFormTemplateId, "DIRECT_FORM_TEMPLATE_ID"),
            DynamicFormSchemaSha256 = StatisticReconciliationActualCanonical.Sha256(value.DynamicFormSchemaSha256, "DIRECT_FORM_SCHEMA_SHA256"),
            RunId = StatisticReconciliationActualCanonical.Required(value.RunId, "DIRECT_RUN_ID"),
            GenerationId = StatisticReconciliationActualCanonical.Sha256(value.GenerationId, "DIRECT_GENERATION_ID"),
            OwnerGenerationSha256 = StatisticReconciliationActualCanonical.Sha256(value.OwnerGenerationSha256, "DIRECT_OWNER_GENERATION_SHA256"),
            OwnerLifecycleEventKey = StatisticReconciliationActualCanonical.Required(value.OwnerLifecycleEventKey, "DIRECT_OWNER_LIFECYCLE_EVENT_KEY"),
            OwnerComputedAtUtc = StatisticReconciliationActualCanonical.Utc(value.OwnerComputedAtUtc, "DIRECT_OWNER_COMPUTED_AT")
                ?? throw new StatisticReconciliationActualObservationException("DIRECT_OWNER_COMPUTED_AT_REQUIRED"),
            ConfigId = StatisticReconciliationActualCanonical.Required(value.ConfigId, "DIRECT_CONFIG_ID"),
            ConfigVersionId = StatisticReconciliationActualCanonical.Required(value.ConfigVersionId, "DIRECT_CONFIG_VERSION_ID"),
            ConfigSha256 = StatisticReconciliationActualCanonical.Sha256(value.ConfigSha256, "DIRECT_CONFIG_SHA256"),
            CandidateChainId = StatisticReconciliationActualCanonical.Required(value.CandidateChainId, "DIRECT_CANDIDATE_CHAIN_ID"),
            CatalogVersion = StatisticReconciliationActualCanonical.Required(value.CatalogVersion, "DIRECT_CATALOG_VERSION"),
            CatalogRawSha256 = StatisticReconciliationActualCanonical.Sha256(value.CatalogRawSha256, "DIRECT_CATALOG_RAW_SHA256"),
            CatalogSemanticSha256 = StatisticReconciliationActualCanonical.Sha256(value.CatalogSemanticSha256, "DIRECT_CATALOG_SEMANTIC_SHA256"),
            SchemaRawSha256 = StatisticReconciliationActualCanonical.Sha256(value.SchemaRawSha256, "DIRECT_SCHEMA_RAW_SHA256"),
            SchemaSemanticSha256 = StatisticReconciliationActualCanonical.Sha256(value.SchemaSemanticSha256, "DIRECT_SCHEMA_SEMANTIC_SHA256"),
            StageLockSha256 = StatisticReconciliationActualCanonical.Sha256(value.StageLockSha256, "DIRECT_STAGE_LOCK_SHA256"),
            OwnerMembershipSignature = StatisticReconciliationActualCanonical.Sha256(value.OwnerMembershipSignature, "DIRECT_MEMBERSHIP_SIGNATURE")
        };
    }

    private static void RequireMembershipBinding(
        ActualDirectProjectionBoundary boundary,
        ActualSourceMembershipCapture membership)
    {
        if (!StringComparer.Ordinal.Equals(boundary.WorkId, membership.WorkId)
            || !StringComparer.Ordinal.Equals(boundary.PeriodInstanceKey, membership.PeriodInstanceKey)
            || !StringComparer.Ordinal.Equals(boundary.DynamicFormTemplateId, membership.DynamicFormTemplateId)
            || !StringComparer.Ordinal.Equals(boundary.OwnerMembershipSignature, membership.OwnerMembershipSignature)
            || !StringComparer.Ordinal.Equals(boundary.RunId, membership.OwnerRunId)
            || !StringComparer.Ordinal.Equals(boundary.GenerationId, membership.OwnerGenerationId)
            || !StringComparer.Ordinal.Equals(boundary.OwnerGenerationSha256, membership.OwnerGenerationSha256)
            || boundary.DirectSourceRevision != membership.OwnerDirectSourceRevision)
        {
            throw new StatisticReconciliationActualObservationException("DIRECT_SOURCE_MEMBERSHIP_MISMATCH");
        }
    }

    private static ActualFieldProjectionObservation AdaptField(
        ActualDirectProjectionBoundary boundary,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> sources,
        HashSet<string> ownerIds,
        WorkReportFieldStatValue row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var common = ValidateCommon(
            boundary, sources, ownerIds, row.Id, row.WorkId, row.WorkAssignmentId,
            row.WorkAssignmentReportId, row.PeriodInstanceKey, row.DynamicFormTemplateId,
            row.AssignmentIsActive, row.ReportIsActive, row.InvalidatedByFlowEventId,
            row.SourcePayloadRevision, row.SourcePayloadHash, row.DirectProjection);
        var periodKey = StatisticReconciliationActualCanonical.Required(row.PeriodKey, "FIELD_PERIOD_KEY");
        var fieldId = StatisticReconciliationActualCanonical.Required(row.FieldId, "FIELD_ID");
        var fieldKey = StatisticReconciliationActualCanonical.Required(row.FieldKey, "FIELD_KEY");
        var fieldType = StatisticReconciliationActualCanonical.Upper(row.FieldType, "FIELD_TYPE");
        var sourceKey = StatisticReconciliationActualCanonical.Required(row.SourceKey, "FIELD_SOURCE_KEY");
        var bucketKey = StatisticReconciliationActualCanonical.Optional(row.BucketKey, "FIELD_BUCKET_KEY");
        var value = Typed(row.ValueKind, row.NumericValue, row.BooleanValue, row.DateValueUtc, row.TextValue, null, bucketKey);
        sources.TryGetValue(common.ReportId, out var sourceDecision);
        var rowState = BindOwnerRowState(common.RowState, sourceDecision, FieldOwnerRowSemantic(row));
        var expectedId = OwnerStableObjectId(
            boundary.GenerationId, "FIELD_VALUE", common.ReportId, row.PeriodInstanceKey,
            boundary.DynamicFormTemplateId, fieldId, sourceKey, bucketKey, value.ValueKind);
        if (!StringComparer.Ordinal.Equals(common.OwnerRowId, expectedId))
            throw new StatisticReconciliationActualObservationException("FIELD_OWNER_ROW_ID_MISMATCH");
        var labels = (row.StatisticLabelCodes ?? new List<string>())
            .Select(x => StatisticReconciliationActualCanonical.Required(x, "FIELD_LABEL_CODE"))
            .ToImmutableArray();
        var stable = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_FIELD_TYPED_IDENTITY_V1",
            boundary.DynamicFormTemplateId, fieldId, fieldKey, sourceKey, bucketKey);
        var occurrence = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_FIELD_OCCURRENCE_V1", stable, common.ReportId, common.OwnerRowId,
            common.Provenance.SourcePayloadRevision.ToString(CultureInfo.InvariantCulture),
            common.Provenance.SourceLifecycleRevision.ToString(CultureInfo.InvariantCulture));
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_FIELD_OBSERVATION_V2", occurrence, value.ValueIdentitySha256,
            rowState.SemanticSha256, common.Provenance.SemanticSha256, periodKey, fieldType, row.ConceptCode,
            StatisticReconciliationActualCanonical.HashSequence("P10_ACTUAL_FIELD_LABELS_V1", labels));
        return new ActualFieldProjectionObservation(
            common.OwnerRowId, common.WorkAssignmentId, common.ReportId, periodKey,
            row.PeriodInstanceKey, boundary.DynamicFormTemplateId, fieldId, fieldKey,
            fieldType, StatisticReconciliationActualCanonical.Optional(row.ConceptCode, "FIELD_CONCEPT_CODE"),
            labels, bucketKey, sourceKey, value, rowState, common.Provenance, stable, occurrence, semantic);
    }

    private static ActualTableMetricProjectionObservation AdaptTable(
        ActualDirectProjectionBoundary boundary,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> sources,
        HashSet<string> ownerIds,
        WorkReportTableStatValue row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var common = ValidateCommon(
            boundary, sources, ownerIds, row.Id, row.WorkId, row.WorkAssignmentId,
            row.WorkAssignmentReportId, row.PeriodInstanceKey, row.DynamicFormTemplateId,
            row.AssignmentIsActive, row.ReportIsActive, row.InvalidatedByFlowEventId,
            row.SourcePayloadRevision, row.SourcePayloadHash, row.DirectProjection);
        var periodKey = StatisticReconciliationActualCanonical.Required(row.PeriodKey, "TABLE_PERIOD_KEY");
        var excelTemplateId = StatisticReconciliationActualCanonical.Optional(row.DynamicExcelTemplateId, "TABLE_EXCEL_TEMPLATE_ID");
        var blockId = StatisticReconciliationActualCanonical.Required(row.BlockId, "TABLE_BLOCK_ID");
        var tableMode = StatisticReconciliationActualCanonical.Upper(row.TableMode, "TABLE_MODE");
        var metricKey = StatisticReconciliationActualCanonical.Required(row.MetricKey, "TABLE_METRIC_KEY");
        var rowKey = StatisticReconciliationActualCanonical.Required(row.RowKey, "TABLE_ROW_KEY");
        var columnKey = StatisticReconciliationActualCanonical.Required(row.ColumnKey, "TABLE_COLUMN_KEY");
        var sourceKey = StatisticReconciliationActualCanonical.Required(row.SourceKey, "TABLE_SOURCE_KEY");
        var dataType = StatisticReconciliationActualCanonical.Upper(row.DataType, "TABLE_DATA_TYPE");
        var bucketKey = StatisticReconciliationActualCanonical.Optional(row.BucketKey, "TABLE_BUCKET_KEY");
        var value = Typed(row.ValueKind, row.NumericValue, row.BooleanValue, row.DateValue, row.TextValue, row.Value, bucketKey);
        sources.TryGetValue(common.ReportId, out var sourceDecision);
        var rowState = BindOwnerRowState(common.RowState, sourceDecision, TableOwnerRowSemantic(row));
        var expectedId = OwnerStableObjectId(
            boundary.GenerationId, "TABLE_VALUE", common.ReportId, blockId, metricKey,
            sourceKey, bucketKey, value.ValueKind);
        if (!StringComparer.Ordinal.Equals(common.OwnerRowId, expectedId))
            throw new StatisticReconciliationActualObservationException("TABLE_OWNER_ROW_ID_MISMATCH");
        var stable = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_TABLE_TYPED_IDENTITY_V1",
            boundary.DynamicFormTemplateId, excelTemplateId, blockId, tableMode,
            metricKey, rowKey, columnKey, sourceKey, bucketKey, dataType);
        var occurrence = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_TABLE_OCCURRENCE_V1", stable, common.ReportId, common.OwnerRowId,
            common.Provenance.SourcePayloadRevision.ToString(CultureInfo.InvariantCulture),
            common.Provenance.SourceLifecycleRevision.ToString(CultureInfo.InvariantCulture));
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_TABLE_OBSERVATION_V2", occurrence, value.ValueIdentitySha256,
            rowState.SemanticSha256, common.Provenance.SemanticSha256, periodKey, dataType, row.ConceptCode);
        return new ActualTableMetricProjectionObservation(
            common.OwnerRowId, common.WorkAssignmentId, common.ReportId, periodKey,
            row.PeriodInstanceKey, boundary.DynamicFormTemplateId, excelTemplateId,
            blockId, tableMode, metricKey, rowKey, columnKey, sourceKey, dataType,
            StatisticReconciliationActualCanonical.Optional(row.ConceptCode, "TABLE_CONCEPT_CODE"),
            bucketKey, value, rowState, common.Provenance, stable, occurrence, semantic);
    }

    private static ActualRowLabelProjectionObservation AdaptLabel(
        ActualDirectProjectionBoundary boundary,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> sources,
        HashSet<string> ownerIds,
        WorkReportLabelStatValue row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var common = ValidateCommon(
            boundary, sources, ownerIds, row.Id, row.WorkId, row.WorkAssignmentId,
            row.WorkAssignmentReportId, row.PeriodInstanceKey, row.DynamicFormTemplateId,
            row.AssignmentIsActive, row.ReportIsActive, row.InvalidatedByFlowEventId,
            row.SourcePayloadRevision, row.SourcePayloadHash, row.DirectProjection);
        var periodId = StatisticReconciliationActualCanonical.Required(row.WorkReportPeriodId, "LABEL_PERIOD_ID");
        var periodKey = StatisticReconciliationActualCanonical.Required(row.PeriodKey, "LABEL_PERIOD_KEY");
        var excelTemplateId = StatisticReconciliationActualCanonical.Optional(row.DynamicExcelTemplateId, "LABEL_EXCEL_TEMPLATE_ID");
        var blockId = StatisticReconciliationActualCanonical.Required(row.BlockId, "LABEL_BLOCK_ID");
        var sheetId = StatisticReconciliationActualCanonical.Optional(row.SheetId, "LABEL_SHEET_ID");
        var rowKey = StatisticReconciliationActualCanonical.Required(row.RowKey, "LABEL_ROW_KEY");
        if (row.RowIndex < 0)
            throw new StatisticReconciliationActualObservationException("LABEL_ROW_INDEX_INVALID");
        var labelCode = StatisticReconciliationActualCanonical.Required(row.LabelCode, "LABEL_CODE");
        var source = StatisticReconciliationActualCanonical.Upper(row.Source, "LABEL_SOURCE");
        if (source != "ROW_LABEL")
            throw new StatisticReconciliationActualObservationException("LABEL_SOURCE_INVALID");
        var value = Typed("ROW_LABEL", null, null, null, labelCode, null, null);
        sources.TryGetValue(common.ReportId, out var sourceDecision);
        var rowState = BindOwnerRowState(common.RowState, sourceDecision, LabelOwnerRowSemantic(row));
        var expectedId = OwnerStableObjectId(
            boundary.GenerationId, "LABEL_VALUE", common.ReportId,
            row.SourcePayloadRevision.ToString(CultureInfo.InvariantCulture),
            common.Provenance.SourceLifecycleRevision.ToString(CultureInfo.InvariantCulture),
            periodId, row.PeriodInstanceKey, boundary.DynamicFormTemplateId,
            excelTemplateId, blockId, sheetId, rowKey,
            row.RowIndex.ToString(CultureInfo.InvariantCulture), labelCode, source);
        if (!StringComparer.Ordinal.Equals(common.OwnerRowId, expectedId))
            throw new StatisticReconciliationActualObservationException("LABEL_OWNER_ROW_ID_MISMATCH");
        var stable = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_LABEL_TYPED_IDENTITY_V1",
            boundary.DynamicFormTemplateId, excelTemplateId, blockId, sheetId,
            rowKey, row.RowIndex.ToString(CultureInfo.InvariantCulture), labelCode, source);
        var occurrence = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_LABEL_OCCURRENCE_V1", stable, common.ReportId, common.OwnerRowId,
            common.Provenance.SourcePayloadRevision.ToString(CultureInfo.InvariantCulture),
            common.Provenance.SourceLifecycleRevision.ToString(CultureInfo.InvariantCulture));
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_LABEL_OBSERVATION_V2", occurrence, value.ValueIdentitySha256,
            rowState.SemanticSha256, common.Provenance.SemanticSha256, periodKey);
        return new ActualRowLabelProjectionObservation(
            common.OwnerRowId, common.WorkAssignmentId, common.ReportId, periodId,
            periodKey, row.PeriodInstanceKey, boundary.DynamicFormTemplateId,
            excelTemplateId, blockId, sheetId, rowKey, row.RowIndex, labelCode,
            source, value, rowState, common.Provenance, stable, occurrence, semantic);
    }

    private static (string OwnerRowId, string WorkAssignmentId, string ReportId, ActualDirectRowState RowState, ActualDirectProjectionProvenance Provenance) ValidateCommon(
        ActualDirectProjectionBoundary boundary,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> sources,
        HashSet<string> ownerIds,
        string? ownerRowId,
        string? workId,
        string? workAssignmentId,
        string? reportId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId,
        bool assignmentIsActive,
        bool reportIsActive,
        string? invalidatedByFlowEventId,
        int sourcePayloadRevision,
        string? sourcePayloadSha256,
        WorkReportDirectProjectionPin? pin)
    {
        var id = StatisticReconciliationActualCanonical.Required(ownerRowId, "DIRECT_OWNER_ROW_ID", 64).ToLowerInvariant();
        if (!ownerIds.Add(id))
            throw new StatisticReconciliationActualObservationException("DIRECT_OWNER_ROW_ID_AMBIGUOUS");
        var rowWorkId = StatisticReconciliationActualCanonical.Required(workId, "DIRECT_ROW_WORK_ID");
        var assignmentId = StatisticReconciliationActualCanonical.Required(workAssignmentId, "DIRECT_ROW_ASSIGNMENT_ID");
        var rowReportId = StatisticReconciliationActualCanonical.Required(reportId, "DIRECT_ROW_REPORT_ID");
        var period = StatisticReconciliationActualCanonical.Required(periodInstanceKey, "DIRECT_ROW_PERIOD_INSTANCE_KEY");
        var template = StatisticReconciliationActualCanonical.Required(dynamicFormTemplateId, "DIRECT_ROW_TEMPLATE_ID");
        if (!StringComparer.Ordinal.Equals(rowWorkId, boundary.WorkId)
            || !StringComparer.Ordinal.Equals(period, boundary.PeriodInstanceKey)
            || !StringComparer.Ordinal.Equals(template, boundary.DynamicFormTemplateId))
        {
            throw new StatisticReconciliationActualObservationException("DIRECT_ROW_SCOPE_MISMATCH");
        }

        var invalidatedEvent = StatisticReconciliationActualCanonical.Optional(
            invalidatedByFlowEventId,
            "DIRECT_ROW_INVALIDATED_EVENT");
        var payloadSha = StatisticReconciliationActualCanonical.Sha256(sourcePayloadSha256, "DIRECT_ROW_PAYLOAD_SHA256");
        sources.TryGetValue(rowReportId, out var source);
        if (source is not null
            && !StringComparer.Ordinal.Equals(assignmentId, source.ObservedOwner.WorkAssignmentId))
            throw new StatisticReconciliationActualObservationException("DIRECT_ROW_SOURCE_ASSIGNMENT_MISMATCH");
        if (source is not null
            && (sourcePayloadRevision != source.PayloadRevision
                || !StringComparer.Ordinal.Equals(payloadSha, source.PayloadSha256)))
        {
            throw new StatisticReconciliationActualObservationException("DIRECT_ROW_SOURCE_PAYLOAD_MISMATCH");
        }
        var provenance = NormalizeProvenance(boundary, pin, source);
        if (!StringComparer.Ordinal.Equals(rowReportId, provenance.SourceReportId)
            || sourcePayloadRevision != provenance.SourcePayloadRevision
            || !StringComparer.Ordinal.Equals(payloadSha, provenance.SourcePayloadSha256))
        {
            throw new StatisticReconciliationActualObservationException("DIRECT_ROW_PROVENANCE_MISMATCH");
        }
        var stateSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_DIRECT_ROW_STATE_V2",
            rowWorkId,
            assignmentId,
            rowReportId,
            sourcePayloadRevision.ToString(CultureInfo.InvariantCulture),
            payloadSha,
            StatisticReconciliationActualCanonical.Boolean(assignmentIsActive),
            StatisticReconciliationActualCanonical.Boolean(reportIsActive),
            invalidatedEvent,
            StatisticReconciliationActualCanonical.Boolean(source is not null));
        var state = new ActualDirectRowState(
            assignmentIsActive,
            reportIsActive,
            invalidatedEvent,
            source is not null,
            source?.DecisionSemanticSha256,
            string.Empty,
            stateSha);
        return (id, assignmentId, rowReportId, state, provenance);
    }
    private static ActualDirectProjectionProvenance NormalizeProvenance(
        ActualDirectProjectionBoundary boundary,
        WorkReportDirectProjectionPin? value,
        ActualSourceMembershipDecision? source)
    {
        var pin = value ?? throw new StatisticReconciliationActualObservationException("DIRECT_PROJECTION_PIN_REQUIRED");
        if (pin.SourcePayloadRevision < 1 || pin.SourceLifecycleRevision < 1 || pin.DirectSourceRevision < 1
            || pin.DynamicFormVersionNo < 1 || pin.ConfigVersionNo < 1 || pin.ConfigRevision < 1)
        {
            throw new StatisticReconciliationActualObservationException("DIRECT_PROJECTION_PIN_REVISION_INVALID");
        }
        var computedAt = StatisticReconciliationActualCanonical.Utc(pin.ComputedAtUtc, "DIRECT_COMPUTED_AT")
            ?? throw new StatisticReconciliationActualObservationException("DIRECT_COMPUTED_AT_REQUIRED");
        var normalized = new ActualDirectProjectionProvenance(
            StatisticReconciliationActualCanonical.Required(pin.RunId, "PIN_RUN_ID"),
            StatisticReconciliationActualCanonical.Sha256(pin.GenerationId, "PIN_GENERATION_ID"),
            boundary.OwnerGenerationSha256,
            StatisticReconciliationActualCanonical.Required(pin.LifecycleEventKey, "PIN_LIFECYCLE_EVENT_KEY"),
            StatisticReconciliationActualCanonical.Required(pin.SourceReportId, "PIN_SOURCE_REPORT_ID"),
            pin.SourcePayloadRevision,
            StatisticReconciliationActualCanonical.Sha256(pin.SourcePayloadHash, "PIN_SOURCE_PAYLOAD_SHA256"),
            pin.SourceLifecycleRevision,
            pin.DirectSourceRevision,
            StatisticReconciliationActualCanonical.Required(pin.DynamicFormFamilyId, "PIN_FORM_FAMILY_ID"),
            StatisticReconciliationActualCanonical.Required(pin.DynamicFormTemplateId, "PIN_FORM_TEMPLATE_ID"),
            pin.DynamicFormVersionNo,
            StatisticReconciliationActualCanonical.Sha256(pin.DynamicFormSchemaHash, "PIN_FORM_SCHEMA_SHA256"),
            StatisticReconciliationActualCanonical.Required(pin.ConfigId, "PIN_CONFIG_ID"),
            StatisticReconciliationActualCanonical.Required(pin.ConfigVersionId, "PIN_CONFIG_VERSION_ID"),
            pin.ConfigVersionNo,
            pin.ConfigRevision,
            StatisticReconciliationActualCanonical.Sha256(pin.ConfigHash, "PIN_CONFIG_SHA256"),
            StatisticReconciliationActualCanonical.Required(pin.CandidateChainId, "PIN_CANDIDATE_CHAIN_ID"),
            StatisticReconciliationActualCanonical.Required(pin.CatalogVersion, "PIN_CATALOG_VERSION"),
            StatisticReconciliationActualCanonical.Sha256(pin.CatalogRawSha256, "PIN_CATALOG_RAW_SHA256"),
            StatisticReconciliationActualCanonical.Sha256(pin.CatalogSemanticSha256, "PIN_CATALOG_SEMANTIC_SHA256"),
            StatisticReconciliationActualCanonical.Sha256(pin.SchemaRawSha256, "PIN_SCHEMA_RAW_SHA256"),
            StatisticReconciliationActualCanonical.Sha256(pin.SchemaSemanticSha256, "PIN_SCHEMA_SEMANTIC_SHA256"),
            StatisticReconciliationActualCanonical.Sha256(pin.StageLockSha256, "PIN_STAGE_LOCK_SHA256"),
            StatisticReconciliationActualCanonical.Sha256(pin.SourceMembershipSignature, "PIN_MEMBERSHIP_SIGNATURE"),
            computedAt,
            string.Empty);
        if (!Matches(boundary, normalized)
            || (source is not null
                && (!StringComparer.Ordinal.Equals(source.ReportId, normalized.SourceReportId)
                    || source.PayloadRevision != normalized.SourcePayloadRevision
                    || !StringComparer.Ordinal.Equals(source.PayloadSha256, normalized.SourcePayloadSha256)
                    || source.LifecycleRevision != normalized.SourceLifecycleRevision)))
        {
            throw new StatisticReconciliationActualObservationException("DIRECT_PROJECTION_PIN_MISMATCH");
        }
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_DIRECT_PROVENANCE_V1",
            normalized.RunId, normalized.GenerationId, normalized.OwnerGenerationSha256, normalized.LifecycleEventKey,
            normalized.SourceReportId, normalized.SourcePayloadRevision.ToString(CultureInfo.InvariantCulture),
            normalized.SourcePayloadSha256, normalized.SourceLifecycleRevision.ToString(CultureInfo.InvariantCulture),
            StatisticReconciliationActualCanonical.Integer(normalized.DirectSourceRevision), normalized.DynamicFormFamilyId,
            normalized.DynamicFormTemplateId, normalized.DynamicFormVersionNo.ToString(CultureInfo.InvariantCulture),
            normalized.DynamicFormSchemaSha256, normalized.ConfigId, normalized.ConfigVersionId,
            normalized.ConfigVersionNo.ToString(CultureInfo.InvariantCulture), StatisticReconciliationActualCanonical.Integer(normalized.ConfigRevision),
            normalized.ConfigSha256, normalized.CandidateChainId, normalized.CatalogVersion,
            normalized.CatalogRawSha256, normalized.CatalogSemanticSha256,
            normalized.SchemaRawSha256, normalized.SchemaSemanticSha256,
            normalized.StageLockSha256, normalized.OwnerMembershipSignature,
            StatisticReconciliationActualCanonical.Instant(normalized.ComputedAtUtc));
        return normalized with { SemanticSha256 = semantic };
    }

    private static bool Matches(
        ActualDirectProjectionBoundary boundary,
        ActualDirectProjectionProvenance pin)
        => StringComparer.Ordinal.Equals(boundary.RunId, pin.RunId)
           && StringComparer.Ordinal.Equals(boundary.GenerationId, pin.GenerationId)
           && StringComparer.Ordinal.Equals(boundary.OwnerGenerationSha256, pin.OwnerGenerationSha256)
           && StringComparer.Ordinal.Equals(boundary.OwnerLifecycleEventKey, pin.LifecycleEventKey)
           && new DateTimeOffset(boundary.OwnerComputedAtUtc).ToUnixTimeMilliseconds()
              == new DateTimeOffset(pin.ComputedAtUtc).ToUnixTimeMilliseconds()
           && boundary.DirectSourceRevision == pin.DirectSourceRevision
           && StringComparer.Ordinal.Equals(boundary.DynamicFormFamilyId, pin.DynamicFormFamilyId)
           && StringComparer.Ordinal.Equals(boundary.DynamicFormTemplateId, pin.DynamicFormTemplateId)
           && boundary.DynamicFormVersionNo == pin.DynamicFormVersionNo
           && StringComparer.Ordinal.Equals(boundary.DynamicFormSchemaSha256, pin.DynamicFormSchemaSha256)
           && StringComparer.Ordinal.Equals(boundary.ConfigId, pin.ConfigId)
           && StringComparer.Ordinal.Equals(boundary.ConfigVersionId, pin.ConfigVersionId)
           && boundary.ConfigVersionNo == pin.ConfigVersionNo
           && boundary.ConfigRevision == pin.ConfigRevision
           && StringComparer.Ordinal.Equals(boundary.ConfigSha256, pin.ConfigSha256)
           && StringComparer.Ordinal.Equals(boundary.CandidateChainId, pin.CandidateChainId)
           && StringComparer.Ordinal.Equals(boundary.CatalogVersion, pin.CatalogVersion)
           && StringComparer.Ordinal.Equals(boundary.CatalogRawSha256, pin.CatalogRawSha256)
           && StringComparer.Ordinal.Equals(boundary.CatalogSemanticSha256, pin.CatalogSemanticSha256)
           && StringComparer.Ordinal.Equals(boundary.SchemaRawSha256, pin.SchemaRawSha256)
           && StringComparer.Ordinal.Equals(boundary.SchemaSemanticSha256, pin.SchemaSemanticSha256)
           && StringComparer.Ordinal.Equals(boundary.StageLockSha256, pin.StageLockSha256)
           && StringComparer.Ordinal.Equals(boundary.OwnerMembershipSignature, pin.OwnerMembershipSignature);

    private static ActualDirectRowState BindOwnerRowState(
        ActualDirectRowState state,
        ActualSourceMembershipDecision? source,
        string ownerRowSemanticSha256)
    {
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_DIRECT_BOUND_ROW_STATE_V1",
            state.SemanticSha256,
            source?.DecisionSemanticSha256,
            ownerRowSemanticSha256);
        return state with
        {
            SourceDecisionSemanticSha256 = source?.DecisionSemanticSha256,
            OwnerRowSemanticSha256 = ownerRowSemanticSha256,
            SemanticSha256 = semantic
        };
    }

    private static string FieldOwnerRowSemantic(WorkReportFieldStatValue row)
        => OwnerRowSemantic("P10_ACTUAL_DIRECT_FIELD_OWNER_ROW_V1", new
        {
            row.AssigneeUserId,
            row.AssigneeUnitId,
            row.RootAssignmentId,
            row.AncestorAssignmentIds,
            row.FlowTemplateId,
            row.FlowTemplateVersionNo,
            row.FlowInstanceId,
            row.FlowStepId,
            row.FlowStepCode,
            row.FlowStepOrder,
            row.FlowBranchId,
            row.ParentFlowBranchId,
            row.FlowAttemptNo,
            row.FlowRole,
            row.IsFlowFinalNode,
            row.FlowEffectiveStatus,
            row.WorkReportPeriodId,
            row.DynamicFormTemplateCode,
            row.DynamicFormTemplateName,
            row.PeriodKind,
            row.PeriodAnchorDate,
            row.PeriodStartDate,
            row.PeriodEndDate,
            row.CompletedDate,
            row.IsHistoricalData,
            row.ReportStatus,
            row.FieldLabel,
            row.StatisticLabelCodes,
            row.ShowInTree,
            row.ShowInDetail,
            row.BucketLabel
        });

    private static string TableOwnerRowSemantic(WorkReportTableStatValue row)
        => OwnerRowSemantic("P10_ACTUAL_DIRECT_TABLE_OWNER_ROW_V1", new
        {
            row.AssigneeUserId,
            row.AssigneeUnitId,
            row.RootAssignmentId,
            row.AncestorAssignmentIds,
            row.FlowTemplateId,
            row.FlowTemplateVersionNo,
            row.FlowInstanceId,
            row.FlowStepId,
            row.FlowStepCode,
            row.FlowStepOrder,
            row.FlowBranchId,
            row.ParentFlowBranchId,
            row.FlowAttemptNo,
            row.FlowRole,
            row.IsFlowFinalNode,
            row.FlowEffectiveStatus,
            row.WorkReportPeriodId,
            row.DynamicFormTemplateCode,
            row.DynamicFormTemplateName,
            row.PeriodKind,
            row.PeriodAnchorDate,
            row.PeriodStartDate,
            row.PeriodEndDate,
            row.CompletedDate,
            row.IsHistoricalData,
            row.ReportStatus,
            row.MetricLabelCode,
            row.BucketLabel
        });

    private static string LabelOwnerRowSemantic(WorkReportLabelStatValue row)
        => OwnerRowSemantic("P10_ACTUAL_DIRECT_LABEL_OWNER_ROW_V1", new
        {
            row.AssigneeUserId,
            row.AssigneeUnitId,
            row.RootAssignmentId,
            row.AncestorAssignmentIds,
            row.FlowTemplateId,
            row.FlowTemplateVersionNo,
            row.FlowInstanceId,
            row.FlowStepId,
            row.FlowStepCode,
            row.FlowStepOrder,
            row.FlowBranchId,
            row.ParentFlowBranchId,
            row.FlowAttemptNo,
            row.FlowRole,
            row.IsFlowFinalNode,
            row.FlowEffectiveStatus,
            row.DynamicFormTemplateCode,
            row.DynamicFormTemplateName,
            row.PeriodKind,
            row.PeriodAnchorDate,
            row.PeriodStartDate,
            row.PeriodEndDate,
            row.CompletedDate,
            row.IsHistoricalData,
            row.ReportStatus
        });

    private static string OwnerRowSemantic(string domain, object value)
        => StatisticReconciliationActualCanonical.Hash(
            domain,
            JsonSerializer.Serialize(value));
    private static ActualDirectTypedValue Typed(
        string? valueKind,
        decimal? numeric,
        bool? boolean,
        DateTime? date,
        string? text,
        decimal? legacyNumeric,
        string? bucketKey)
    {
        var kind = StatisticReconciliationActualCanonical.Upper(valueKind, "DIRECT_VALUE_KIND");
        var bucket = StatisticReconciliationActualCanonical.Optional(bucketKey, "DIRECT_VALUE_BUCKET_KEY");
        string canonical;
        switch (kind)
        {
            case "MISSING":
                RequireNoValue(numeric, boolean, date, text, legacyNumeric, false);
                if (bucket is not null)
                    throw new StatisticReconciliationActualObservationException("DIRECT_MISSING_BUCKET_INVALID");
                canonical = "<missing>";
                break;
            case "NULL":
                RequireNoValue(numeric, boolean, date, text, legacyNumeric, false);
                if (bucket is not null)
                    throw new StatisticReconciliationActualObservationException("DIRECT_NULL_BUCKET_INVALID");
                canonical = "<null>";
                break;
            case "EMPTY":
                RequireNoValue(numeric, boolean, date, text, legacyNumeric, false);
                if (bucket is not null)
                    throw new StatisticReconciliationActualObservationException("DIRECT_EMPTY_BUCKET_INVALID");
                canonical = string.Empty;
                break;
            case "NUMBER":
                if (!numeric.HasValue || boolean.HasValue || date.HasValue || text is not null || bucket is not null)
                    throw new StatisticReconciliationActualObservationException("DIRECT_NUMBER_CHANNEL_INVALID");
                if (legacyNumeric.HasValue && legacyNumeric.Value != numeric.Value)
                    throw new StatisticReconciliationActualObservationException("DIRECT_NUMBER_LEGACY_MISMATCH");
                canonical = StatisticReconciliationActualCanonical.Number(numeric.Value);
                break;
            case "BOOLEAN":
                if (!boolean.HasValue || numeric.HasValue || date.HasValue || text is not null || bucket is not null)
                    throw new StatisticReconciliationActualObservationException("DIRECT_BOOLEAN_CHANNEL_INVALID");
                if (legacyNumeric.HasValue && legacyNumeric.Value != 0m)
                    throw new StatisticReconciliationActualObservationException("DIRECT_BOOLEAN_LEGACY_INVALID");
                canonical = StatisticReconciliationActualCanonical.Boolean(boolean.Value);
                break;
            case "DATE":
                if (!date.HasValue || numeric.HasValue || boolean.HasValue || text is not null || bucket is not null)
                    throw new StatisticReconciliationActualObservationException("DIRECT_DATE_CHANNEL_INVALID");
                if (legacyNumeric.HasValue && legacyNumeric.Value != 0m)
                    throw new StatisticReconciliationActualObservationException("DIRECT_DATE_LEGACY_INVALID");
                canonical = StatisticReconciliationActualCanonical.Instant(date.Value);
                date = date.Value;
                break;
            case "OPTION":
                RequireNoValue(numeric, boolean, date, text, legacyNumeric, false);
                canonical = bucket
                    ?? throw new StatisticReconciliationActualObservationException("DIRECT_OPTION_BUCKET_REQUIRED");
                break;
            case "TEXT_BUCKET":
                if (text is null || bucket is null || numeric.HasValue || boolean.HasValue || date.HasValue)
                    throw new StatisticReconciliationActualObservationException("DIRECT_TEXT_BUCKET_CHANNEL_INVALID");
                if (legacyNumeric.HasValue && legacyNumeric.Value != 0m)
                    throw new StatisticReconciliationActualObservationException("DIRECT_TEXT_BUCKET_LEGACY_INVALID");
                canonical = bucket;
                break;
            case "TEXT":
                if (text is null || numeric.HasValue || boolean.HasValue || date.HasValue)
                    throw new StatisticReconciliationActualObservationException("DIRECT_TEXT_CHANNEL_INVALID");
                if (legacyNumeric.HasValue && legacyNumeric.Value != 0m)
                    throw new StatisticReconciliationActualObservationException("DIRECT_TEXT_LEGACY_INVALID");
                canonical = bucket ?? text;
                break;
            case "PRESENT":
            case "ROW_LABEL":
                if (text is null || numeric.HasValue || boolean.HasValue || date.HasValue || bucket is not null)
                    throw new StatisticReconciliationActualObservationException("DIRECT_TEXT_CHANNEL_INVALID");
                if (legacyNumeric.HasValue && legacyNumeric.Value != 0m)
                    throw new StatisticReconciliationActualObservationException("DIRECT_TEXT_LEGACY_INVALID");
                canonical = text;
                break;
            default:
                throw new StatisticReconciliationActualObservationException("DIRECT_VALUE_KIND_UNSUPPORTED");
        }
        var identity = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_DIRECT_TYPED_VALUE_V2", kind, canonical, bucket, text);
        return new ActualDirectTypedValue(kind, canonical, bucket, numeric, boolean, date, text, identity);
    }
    private static void RequireNoValue(
        decimal? numeric,
        bool? boolean,
        DateTime? date,
        string? text,
        decimal? legacyNumeric,
        bool allowLegacy)
    {
        if (numeric.HasValue || boolean.HasValue || date.HasValue || text is not null
            || (!allowLegacy && legacyNumeric.HasValue && legacyNumeric.Value != 0m))
        {
            throw new StatisticReconciliationActualObservationException("DIRECT_EMPTY_CHANNEL_INVALID");
        }
    }
}
