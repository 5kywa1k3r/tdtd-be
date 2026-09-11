using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualAggregateAdapter
{
    internal const int MaxAggregateRows = 100_000;

    internal async Task<ActualAggregateCapture> CaptureAsync(
        ActualAggregatePublicationBoundary boundary,
        ActualSourceMembershipCapture membership,
        ActualDirectProjectionCapture directProjection,
        IStatisticReconciliationActualAggregateOwnerReader reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(directProjection);
        ArgumentNullException.ThrowIfNull(reader);

        var normalized = NormalizeBoundary(boundary);
        RequirePriorLayerBinding(normalized, membership, directProjection);
        var expectedTotal = normalized.AggregateStoreDigests.Sum(x => x.RowCount);
        if (expectedTotal > MaxAggregateRows)
            throw Fail("AGGREGATE_PINNED_ROWS_LIMIT");

        var fields = await reader.ReadFieldGenerationAsync(normalized, cancellationToken)
            .ConfigureAwait(false) ?? throw Fail("AGGREGATE_FIELD_ROWS_NULL");
        var tables = await reader.ReadTableMetricGenerationAsync(normalized, cancellationToken)
            .ConfigureAwait(false) ?? throw Fail("AGGREGATE_TABLE_ROWS_NULL");
        var labels = await reader.ReadRowLabelGenerationAsync(normalized, cancellationToken)
            .ConfigureAwait(false) ?? throw Fail("AGGREGATE_LABEL_ROWS_NULL");
        var total = checked(fields.Count + tables.Count + labels.Count);
        if (total > MaxAggregateRows)
            throw Fail("AGGREGATE_ROWS_LIMIT");

        VerifyStoreDigest(normalized, StatisticReconciliationActualAggregateStores.Field, fields);
        VerifyStoreDigest(normalized, StatisticReconciliationActualAggregateStores.TableMetric, tables);
        VerifyStoreDigest(normalized, StatisticReconciliationActualAggregateStores.RowLabel, labels);

        var ownerFilterSha = OwnerFilterSha(normalized.Direct.GenerationId);
        var configSha = ConfigIdentitySha(normalized.Direct);
        var resultSha = ResultGenerationIdentitySha(normalized);
        var sources = membership.IncludedSources.ToDictionary(x => x.ReportId, StringComparer.Ordinal);
        var fieldIds = new HashSet<string>(StringComparer.Ordinal);
        var tableIds = new HashSet<string>(StringComparer.Ordinal);
        var labelIds = new HashSet<string>(StringComparer.Ordinal);

        var fieldObservations = fields
            .Select(row => AdaptField(
                normalized, sources, fieldIds, ownerFilterSha, configSha, resultSha, row))
            .OrderBy(x => x.Time.PeriodKey, StringComparer.Ordinal)
            .ThenBy(x => x.FieldKey, StringComparer.Ordinal)
            .ThenBy(x => x.BucketKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.OwnerRowId, StringComparer.Ordinal)
            .ToImmutableArray();
        var tableObservations = tables
            .Select(row => AdaptTable(
                normalized, sources, tableIds, ownerFilterSha, configSha, resultSha, row))
            .OrderBy(x => x.Time.PeriodKey, StringComparer.Ordinal)
            .ThenBy(x => x.BlockId, StringComparer.Ordinal)
            .ThenBy(x => x.MetricKey, StringComparer.Ordinal)
            .ThenBy(x => x.RowKey, StringComparer.Ordinal)
            .ThenBy(x => x.ColumnKey, StringComparer.Ordinal)
            .ThenBy(x => x.BucketKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.OwnerRowId, StringComparer.Ordinal)
            .ToImmutableArray();
        var labelObservations = labels
            .Select(row => AdaptLabel(
                normalized, sources, labelIds, ownerFilterSha, configSha, resultSha, row))
            .OrderBy(x => x.Time.PeriodKey, StringComparer.Ordinal)
            .ThenBy(x => x.BlockId, StringComparer.Ordinal)
            .ThenBy(x => x.LabelCode, StringComparer.Ordinal)
            .ThenBy(x => x.OwnerRowId, StringComparer.Ordinal)
            .ToImmutableArray();

        var captureSha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_AGGREGATE_CAPTURE_V1",
            directProjection.CaptureSemanticSha256,
            membership.CaptureSemanticSha256,
            ownerFilterSha,
            configSha,
            resultSha,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_FIELD_AGGREGATES_V1",
                fieldObservations.Select(x => x.SemanticSha256)),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_TABLE_AGGREGATES_V1",
                tableObservations.Select(x => x.SemanticSha256)),
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_LABEL_AGGREGATES_V1",
                labelObservations.Select(x => x.SemanticSha256)));

        return new ActualAggregateCapture(
            normalized,
            membership.SourceSetSha256,
            directProjection.CaptureSemanticSha256,
            ownerFilterSha,
            configSha,
            resultSha,
            fieldObservations,
            tableObservations,
            labelObservations,
            total,
            captureSha);
    }

    private static ActualAggregatePublicationBoundary NormalizeBoundary(
        ActualAggregatePublicationBoundary value)
    {
        ArgumentNullException.ThrowIfNull(value.Direct);
        if (value.DirectPublicationRevision < 1)
            throw Fail("AGGREGATE_PUBLICATION_REVISION_INVALID");
        var direct = NormalizeDirectBoundary(value.Direct);
        var freshness = StatisticReconciliationActualCanonical.Upper(
            value.FreshnessState,
            "AGGREGATE_FRESHNESS_STATE");
        if (freshness != "FRESH")
            throw Fail("AGGREGATE_PUBLICATION_NOT_FRESH");
        var publishedAt = StatisticReconciliationActualCanonical.Utc(
            value.PublishedAtUtc,
            "AGGREGATE_PUBLISHED_AT") ?? throw Fail("AGGREGATE_PUBLISHED_AT_REQUIRED");
        var pins = value.AggregateStoreDigests;
        if (pins.IsDefault)
            throw Fail("AGGREGATE_STORE_DIGESTS_REQUIRED");
        var normalizedPins = pins.Select(NormalizeStorePin)
            .OrderBy(x => x.Store, StringComparer.Ordinal)
            .ToImmutableArray();
        if (normalizedPins.Length != StatisticReconciliationActualAggregateStores.ExactSet.Length
            || !normalizedPins.Select(x => x.Store).SequenceEqual(
                StatisticReconciliationActualAggregateStores.ExactSet,
                StringComparer.Ordinal))
        {
            throw Fail("AGGREGATE_STORE_DIGEST_SET_INVALID");
        }
        return value with
        {
            Direct = direct,
            PublicationScopeKey = StatisticReconciliationActualCanonical.Required(
                value.PublicationScopeKey,
                "AGGREGATE_PUBLICATION_SCOPE_KEY"),
            FreshnessState = freshness,
            PublishedAtUtc = publishedAt,
            AggregateStoreDigests = normalizedPins
        };
    }

    private static ActualDirectProjectionBoundary NormalizeDirectBoundary(
        ActualDirectProjectionBoundary value)
    {
        if (value.DynamicFormVersionNo < 1 || value.DirectSourceRevision < 1
            || value.ConfigVersionNo < 1 || value.ConfigRevision < 1)
        {
            throw Fail("AGGREGATE_BOUNDARY_REVISION_INVALID");
        }
        return value with
        {
            WorkId = StatisticReconciliationActualCanonical.Required(value.WorkId, "AGGREGATE_WORK_ID"),
            PeriodInstanceKey = StatisticReconciliationActualCanonical.Required(value.PeriodInstanceKey, "AGGREGATE_PERIOD_INSTANCE_KEY"),
            DynamicFormFamilyId = StatisticReconciliationActualCanonical.Required(value.DynamicFormFamilyId, "AGGREGATE_FORM_FAMILY_ID"),
            DynamicFormTemplateId = StatisticReconciliationActualCanonical.Required(value.DynamicFormTemplateId, "AGGREGATE_FORM_TEMPLATE_ID"),
            DynamicFormSchemaSha256 = StatisticReconciliationActualCanonical.Sha256(value.DynamicFormSchemaSha256, "AGGREGATE_FORM_SCHEMA_SHA256"),
            RunId = StatisticReconciliationActualCanonical.Required(value.RunId, "AGGREGATE_RUN_ID"),
            GenerationId = StatisticReconciliationActualCanonical.Sha256(value.GenerationId, "AGGREGATE_GENERATION_ID"),
            OwnerGenerationSha256 = StatisticReconciliationActualCanonical.Sha256(value.OwnerGenerationSha256, "AGGREGATE_OWNER_GENERATION_SHA256"),
            OwnerLifecycleEventKey = StatisticReconciliationActualCanonical.Required(value.OwnerLifecycleEventKey, "AGGREGATE_LIFECYCLE_EVENT_KEY"),
            OwnerComputedAtUtc = StatisticReconciliationActualCanonical.Utc(value.OwnerComputedAtUtc, "AGGREGATE_COMPUTED_AT")
                ?? throw Fail("AGGREGATE_COMPUTED_AT_REQUIRED"),
            ConfigId = StatisticReconciliationActualCanonical.Required(value.ConfigId, "AGGREGATE_CONFIG_ID"),
            ConfigVersionId = StatisticReconciliationActualCanonical.Required(value.ConfigVersionId, "AGGREGATE_CONFIG_VERSION_ID"),
            ConfigSha256 = StatisticReconciliationActualCanonical.Sha256(value.ConfigSha256, "AGGREGATE_CONFIG_SHA256"),
            CandidateChainId = StatisticReconciliationActualCanonical.Required(value.CandidateChainId, "AGGREGATE_CANDIDATE_CHAIN_ID"),
            CatalogVersion = StatisticReconciliationActualCanonical.Required(value.CatalogVersion, "AGGREGATE_CATALOG_VERSION"),
            CatalogRawSha256 = StatisticReconciliationActualCanonical.Sha256(value.CatalogRawSha256, "AGGREGATE_CATALOG_RAW_SHA256"),
            CatalogSemanticSha256 = StatisticReconciliationActualCanonical.Sha256(value.CatalogSemanticSha256, "AGGREGATE_CATALOG_SEMANTIC_SHA256"),
            SchemaRawSha256 = StatisticReconciliationActualCanonical.Sha256(value.SchemaRawSha256, "AGGREGATE_SCHEMA_RAW_SHA256"),
            SchemaSemanticSha256 = StatisticReconciliationActualCanonical.Sha256(value.SchemaSemanticSha256, "AGGREGATE_SCHEMA_SEMANTIC_SHA256"),
            StageLockSha256 = StatisticReconciliationActualCanonical.Sha256(value.StageLockSha256, "AGGREGATE_STAGE_LOCK_SHA256"),
            OwnerMembershipSignature = StatisticReconciliationActualCanonical.Sha256(value.OwnerMembershipSignature, "AGGREGATE_MEMBERSHIP_SIGNATURE")
        };
    }

    private static ActualAggregateStoreDigestPin NormalizeStorePin(
        ActualAggregateStoreDigestPin value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.RowCount < 0)
            throw Fail("AGGREGATE_STORE_ROW_COUNT_INVALID");
        return value with
        {
            Store = StatisticReconciliationActualCanonical.Required(value.Store, "AGGREGATE_STORE"),
            Sha256 = StatisticReconciliationActualCanonical.Sha256(value.Sha256, "AGGREGATE_STORE_SHA256")
        };
    }

    private static void RequirePriorLayerBinding(
        ActualAggregatePublicationBoundary boundary,
        ActualSourceMembershipCapture membership,
        ActualDirectProjectionCapture projection)
    {
        if (projection.Boundary != boundary.Direct
            || !StringComparer.Ordinal.Equals(projection.ActualSourceSetSha256, membership.SourceSetSha256)
            || !StringComparer.Ordinal.Equals(boundary.Direct.WorkId, membership.WorkId)
            || !StringComparer.Ordinal.Equals(boundary.Direct.PeriodInstanceKey, membership.PeriodInstanceKey)
            || !StringComparer.Ordinal.Equals(boundary.Direct.DynamicFormTemplateId, membership.DynamicFormTemplateId)
            || !StringComparer.Ordinal.Equals(boundary.Direct.RunId, membership.OwnerRunId)
            || !StringComparer.Ordinal.Equals(boundary.Direct.GenerationId, membership.OwnerGenerationId)
            || !StringComparer.Ordinal.Equals(boundary.Direct.OwnerGenerationSha256, membership.OwnerGenerationSha256)
            || !StringComparer.Ordinal.Equals(boundary.Direct.OwnerMembershipSignature, membership.OwnerMembershipSignature)
            || boundary.Direct.DirectSourceRevision != membership.OwnerDirectSourceRevision)
        {
            throw Fail("AGGREGATE_PRIOR_LAYER_MISMATCH");
        }
    }

    private static void VerifyStoreDigest<T>(
        ActualAggregatePublicationBoundary boundary,
        string store,
        IReadOnlyList<T> rows)
    {
        var expected = boundary.AggregateStoreDigests.Single(x => x.Store == store);
        var observed = ComputeStoreDigest(store, rows);
        if (expected.RowCount != observed.RowCount
            || !StringComparer.Ordinal.Equals(expected.Sha256, observed.Sha256))
        {
            throw Fail("AGGREGATE_STORE_DIGEST_MISMATCH");
        }
    }

    private static ActualAggregateStoreDigestPin ComputeStoreDigest<T>(
        string store,
        IReadOnlyList<T> rows)
    {
        var material = rows
            .Select(CanonicalPersistedRow)
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.CanonicalJson)
            .ToArray();
        var digestMaterial = $"P9_DIRECT_STORE_ROWS_V2\n{string.Join("\n", material)}";
        return new ActualAggregateStoreDigestPin(
            store,
            material.LongLength,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestMaterial)))
                .ToLowerInvariant());
    }

    private static (string Id, string CanonicalJson) CanonicalPersistedRow<T>(T row)
    {
        if (row is null)
            throw Fail("AGGREGATE_OWNER_ROW_NULL");
        var document = row.ToBsonDocument();
        foreach (var operational in new[]
                 {
                     "createdAtUtc", "updatedAtUtc", "createdByUserId", "updatedByUserId"
                 })
        {
            document.Remove(operational);
        }
        var id = document.TryGetValue("_id", out var idValue)
            ? idValue.ToString() ?? throw Fail("AGGREGATE_OWNER_ID_INVALID")
            : throw Fail("AGGREGATE_OWNER_ID_MISSING");
        var canonical = CanonicalizeBson(document).AsBsonDocument.ToJson(
            new JsonWriterSettings
            {
                OutputMode = JsonOutputMode.CanonicalExtendedJson,
                Indent = false
            });
        return (id, canonical);
    }

    private static BsonValue CanonicalizeBson(BsonValue value)
    {
        if (value.IsBsonDocument)
        {
            return new BsonDocument(value.AsBsonDocument.Elements
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => new BsonElement(x.Name, CanonicalizeBson(x.Value))));
        }
        if (value.IsBsonArray)
            return new BsonArray(value.AsBsonArray.Select(CanonicalizeBson));
        return value;
    }

    private static ActualFieldAggregateObservation AdaptField(
        ActualAggregatePublicationBoundary boundary,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> sources,
        HashSet<string> ownerIds,
        string filterSha,
        string configSha,
        string resultSha,
        WorkReportFieldStatAggregate row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var ownerId = OwnerId(row.Id, ownerIds, "FIELD");
        var workId = Required(row.WorkId, "FIELD_WORK_ID");
        var scopeType = Required(row.ScopeType, "FIELD_SCOPE_TYPE");
        var scopeId = Required(row.ScopeId, "FIELD_SCOPE_ID");
        var formId = Required(row.DynamicFormTemplateId, "FIELD_FORM_TEMPLATE_ID");
        var fieldId = Required(row.FieldId, "FIELD_ID");
        var fieldKey = Required(row.FieldKey, "FIELD_KEY");
        var bucketKey = OptionalExact(row.BucketKey, "FIELD_BUCKET_KEY");
        RequireRowBoundary(boundary.Direct, workId, formId, row.PeriodInstanceKey, row.IsDeleted);
        var expectedId = StatisticReconciliationActualDirectProjectionAdapter.OwnerStableObjectId(
            boundary.Direct.GenerationId,
            "FIELD_AGGREGATE",
            workId,
            scopeType,
            scopeId,
            formId,
            fieldId,
            bucketKey,
            row.PeriodInstanceKey,
            StatisticReconciliationActualCanonical.Integer(row.ReportStatus));
        if (!StringComparer.Ordinal.Equals(ownerId, expectedId))
            throw Fail("FIELD_AGGREGATE_OWNER_ID_MISMATCH");
        var time = Time(row.PeriodKey, row.PeriodInstanceKey, row.PeriodKind,
            row.PeriodAnchorDate, row.PeriodStartDate, row.PeriodEndDate,
            row.CompletedDate, row.IsHistoricalData, row.ReportStatus);
        var counts = new ActualAggregateCounts(row.ReportCount, row.ValueCount, row.NumericValueCount);
        var measures = Measures(row.Sum, row.Min, row.Max, row.NumericValueCount,
            row.TrueCount, row.FalseCount, row.EarliestDateUtc, row.LatestDateUtc);
        var provenance = Provenance(boundary.Direct, row.DirectProjection);
        var ownerSemantic = OwnerSemantic("P10_ACTUAL_FIELD_AGGREGATE_OWNER_ROW_V1", row);
        var state = RowState(sources, provenance, counts, measures, ownerSemantic);
        var identities = IdentityPins(
            StatisticReconciliationActualCanonical.Hash("P10_ACTUAL_FIELD_CONCEPT_V1", formId, fieldId),
            GrainSha(workId, scopeType, scopeId, row.RootAssignmentId),
            TimeSha(time), filterSha, configSha, resultSha);
        var semantic = ObservationSemantic(
            "P10_ACTUAL_FIELD_AGGREGATE_OBSERVATION_V1",
            ownerId, identities, counts, measures, state, provenance);
        return new ActualFieldAggregateObservation(
            ownerId, workId, scopeType, scopeId,
            OptionalExact(row.RootAssignmentId, "FIELD_ROOT_ASSIGNMENT_ID"), formId,
            OptionalExact(row.DynamicFormTemplateCode, "FIELD_FORM_CODE"),
            OptionalExact(row.DynamicFormTemplateName, "FIELD_FORM_NAME"),
            fieldId, fieldKey, Required(row.FieldLabel, "FIELD_LABEL"),
            Required(row.FieldType, "FIELD_TYPE"),
            SnapshotStrings(row.StatisticLabelCodes, "FIELD_STATISTIC_LABEL_CODE"),
            row.ShowInTree, row.ShowInDetail, bucketKey,
            OptionalExact(row.BucketLabel, "FIELD_BUCKET_LABEL"),
            time, counts, measures, identities, state, provenance, semantic);
    }

    private static ActualTableMetricAggregateObservation AdaptTable(
        ActualAggregatePublicationBoundary boundary,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> sources,
        HashSet<string> ownerIds,
        string filterSha,
        string configSha,
        string resultSha,
        WorkReportTableStatAggregate row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var ownerId = OwnerId(row.Id, ownerIds, "TABLE");
        var workId = Required(row.WorkId, "TABLE_WORK_ID");
        var scopeType = Required(row.ScopeType, "TABLE_SCOPE_TYPE");
        var scopeId = Required(row.ScopeId, "TABLE_SCOPE_ID");
        var formId = Required(row.DynamicFormTemplateId, "TABLE_FORM_TEMPLATE_ID");
        var excelId = OptionalExact(row.DynamicExcelTemplateId, "TABLE_EXCEL_TEMPLATE_ID");
        var blockId = Required(row.BlockId, "TABLE_BLOCK_ID");
        var tableMode = Required(row.TableMode, "TABLE_MODE");
        var metricKey = Required(row.MetricKey, "TABLE_METRIC_KEY");
        var metricLabelCode = OptionalExact(row.MetricLabelCode, "TABLE_METRIC_LABEL_CODE");
        var dataType = Required(row.DataType, "TABLE_DATA_TYPE");
        var bucketKey = OptionalExact(row.BucketKey, "TABLE_BUCKET_KEY");
        RequireRowBoundary(boundary.Direct, workId, formId, row.PeriodInstanceKey, row.IsDeleted);
        var expectedId = StatisticReconciliationActualDirectProjectionAdapter.OwnerStableObjectId(
            boundary.Direct.GenerationId,
            "TABLE_AGGREGATE",
            workId,
            scopeType,
            scopeId,
            formId,
            excelId,
            blockId,
            tableMode,
            metricKey,
            metricLabelCode,
            dataType,
            bucketKey,
            row.PeriodInstanceKey,
            StatisticReconciliationActualCanonical.Integer(row.ReportStatus));
        if (!StringComparer.Ordinal.Equals(ownerId, expectedId))
            throw Fail("TABLE_AGGREGATE_OWNER_ID_MISMATCH");
        var time = Time(row.PeriodKey, row.PeriodInstanceKey, row.PeriodKind,
            row.PeriodAnchorDate, row.PeriodStartDate, row.PeriodEndDate,
            row.CompletedDate, row.IsHistoricalData, row.ReportStatus);
        var counts = new ActualAggregateCounts(row.ReportCount, row.ValueCount, row.NumericValueCount);
        var measures = Measures(row.Sum, row.Min, row.Max, row.NumericValueCount,
            row.TrueCount, row.FalseCount, row.EarliestDateUtc, row.LatestDateUtc);
        var provenance = Provenance(boundary.Direct, row.DirectProjection);
        var ownerSemantic = OwnerSemantic("P10_ACTUAL_TABLE_AGGREGATE_OWNER_ROW_V1", row);
        var state = RowState(sources, provenance, counts, measures, ownerSemantic);
        var identities = IdentityPins(
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_TABLE_METRIC_CONCEPT_V1",
                formId, excelId, blockId, metricKey, metricLabelCode),
            GrainSha(workId, scopeType, scopeId, row.RootAssignmentId),
            TimeSha(time), filterSha, configSha, resultSha);
        var semantic = ObservationSemantic(
            "P10_ACTUAL_TABLE_AGGREGATE_OBSERVATION_V1",
            ownerId, identities, counts, measures, state, provenance);
        return new ActualTableMetricAggregateObservation(
            ownerId, workId, scopeType, scopeId,
            OptionalExact(row.RootAssignmentId, "TABLE_ROOT_ASSIGNMENT_ID"), formId,
            OptionalExact(row.DynamicFormTemplateCode, "TABLE_FORM_CODE"),
            OptionalExact(row.DynamicFormTemplateName, "TABLE_FORM_NAME"),
            excelId, blockId, tableMode, metricKey, metricLabelCode,
            Required(row.RowKey, "TABLE_ROW_KEY"), Required(row.ColumnKey, "TABLE_COLUMN_KEY"),
            dataType, bucketKey, OptionalExact(row.BucketLabel, "TABLE_BUCKET_LABEL"),
            time, counts, measures, identities, state, provenance, semantic);
    }

    private static ActualRowLabelAggregateObservation AdaptLabel(
        ActualAggregatePublicationBoundary boundary,
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> sources,
        HashSet<string> ownerIds,
        string filterSha,
        string configSha,
        string resultSha,
        WorkReportLabelStatAggregate row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var ownerId = OwnerId(row.Id, ownerIds, "LABEL");
        var workId = Required(row.WorkId, "LABEL_WORK_ID");
        var scopeType = Required(row.ScopeType, "LABEL_SCOPE_TYPE");
        var scopeId = Required(row.ScopeId, "LABEL_SCOPE_ID");
        var formId = Required(row.DynamicFormTemplateId, "LABEL_FORM_TEMPLATE_ID");
        var excelId = OptionalExact(row.DynamicExcelTemplateId, "LABEL_EXCEL_TEMPLATE_ID");
        var blockId = Required(row.BlockId, "LABEL_BLOCK_ID");
        var labelCode = Required(row.LabelCode, "LABEL_CODE");
        RequireRowBoundary(boundary.Direct, workId, formId, row.PeriodInstanceKey, row.IsDeleted);
        var expectedId = StatisticReconciliationActualDirectProjectionAdapter.OwnerStableObjectId(
            boundary.Direct.GenerationId,
            "LABEL_AGGREGATE",
            workId,
            scopeType,
            scopeId,
            formId,
            excelId,
            blockId,
            labelCode,
            row.PeriodInstanceKey,
            StatisticReconciliationActualCanonical.Integer(row.ReportStatus));
        if (!StringComparer.Ordinal.Equals(ownerId, expectedId))
            throw Fail("LABEL_AGGREGATE_OWNER_ID_MISMATCH");
        var time = Time(row.PeriodKey, row.PeriodInstanceKey, row.PeriodKind,
            row.PeriodAnchorDate, row.PeriodStartDate, row.PeriodEndDate,
            row.CompletedDate, row.IsHistoricalData, row.ReportStatus);
        var counts = new ActualAggregateCounts(row.ReportCount, row.RowCount, 0);
        var measures = Measures(0, null, null, 0, 0, 0, null, null);
        var provenance = Provenance(boundary.Direct, row.DirectProjection);
        var ownerSemantic = OwnerSemantic("P10_ACTUAL_LABEL_AGGREGATE_OWNER_ROW_V1", row);
        var state = RowState(sources, provenance, counts, measures, ownerSemantic);
        var identities = IdentityPins(
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_ROW_LABEL_CONCEPT_V1", formId, excelId, blockId, labelCode),
            GrainSha(workId, scopeType, scopeId, row.RootAssignmentId),
            TimeSha(time), filterSha, configSha, resultSha);
        var semantic = ObservationSemantic(
            "P10_ACTUAL_LABEL_AGGREGATE_OBSERVATION_V1",
            ownerId, identities, counts, measures, state, provenance);
        return new ActualRowLabelAggregateObservation(
            ownerId, workId, scopeType, scopeId,
            OptionalExact(row.RootAssignmentId, "LABEL_ROOT_ASSIGNMENT_ID"), formId,
            OptionalExact(row.DynamicFormTemplateCode, "LABEL_FORM_CODE"),
            OptionalExact(row.DynamicFormTemplateName, "LABEL_FORM_NAME"),
            excelId, blockId, labelCode, time, counts, identities, state, provenance, semantic);
    }

    private static void RequireRowBoundary(
        ActualDirectProjectionBoundary boundary,
        string workId,
        string formId,
        string? periodInstanceKey,
        bool isDeleted)
    {
        if (isDeleted)
            throw Fail("AGGREGATE_ROW_DELETED");
        if (!StringComparer.Ordinal.Equals(workId, boundary.WorkId)
            || !StringComparer.Ordinal.Equals(formId, boundary.DynamicFormTemplateId)
            || !StringComparer.Ordinal.Equals(
                Required(periodInstanceKey, "AGGREGATE_ROW_PERIOD_INSTANCE_KEY"),
                boundary.PeriodInstanceKey))
        {
            throw Fail("AGGREGATE_ROW_SCOPE_MISMATCH");
        }
    }

    private static ActualDirectProjectionProvenance Provenance(
        ActualDirectProjectionBoundary boundary,
        WorkReportDirectProjectionPin? pin)
    {
        if (pin is null)
            throw Fail("AGGREGATE_ROW_PROVENANCE_REQUIRED");
        var normalized = new ActualDirectProjectionProvenance(
            Required(pin.RunId, "AGGREGATE_PIN_RUN_ID"),
            StatisticReconciliationActualCanonical.Sha256(pin.GenerationId, "AGGREGATE_PIN_GENERATION_ID"),
            boundary.OwnerGenerationSha256,
            Required(pin.LifecycleEventKey, "AGGREGATE_PIN_LIFECYCLE_EVENT_KEY"),
            Required(pin.SourceReportId, "AGGREGATE_PIN_SOURCE_REPORT_ID"),
            pin.SourcePayloadRevision,
            StatisticReconciliationActualCanonical.Sha256(pin.SourcePayloadHash, "AGGREGATE_PIN_PAYLOAD_SHA256"),
            pin.SourceLifecycleRevision,
            pin.DirectSourceRevision,
            Required(pin.DynamicFormFamilyId, "AGGREGATE_PIN_FORM_FAMILY_ID"),
            Required(pin.DynamicFormTemplateId, "AGGREGATE_PIN_FORM_TEMPLATE_ID"),
            pin.DynamicFormVersionNo,
            StatisticReconciliationActualCanonical.Sha256(pin.DynamicFormSchemaHash, "AGGREGATE_PIN_FORM_SCHEMA_SHA256"),
            Required(pin.ConfigId, "AGGREGATE_PIN_CONFIG_ID"),
            Required(pin.ConfigVersionId, "AGGREGATE_PIN_CONFIG_VERSION_ID"),
            pin.ConfigVersionNo,
            pin.ConfigRevision,
            StatisticReconciliationActualCanonical.Sha256(pin.ConfigHash, "AGGREGATE_PIN_CONFIG_SHA256"),
            Required(pin.CandidateChainId, "AGGREGATE_PIN_CANDIDATE_CHAIN_ID"),
            Required(pin.CatalogVersion, "AGGREGATE_PIN_CATALOG_VERSION"),
            StatisticReconciliationActualCanonical.Sha256(pin.CatalogRawSha256, "AGGREGATE_PIN_CATALOG_RAW_SHA256"),
            StatisticReconciliationActualCanonical.Sha256(pin.CatalogSemanticSha256, "AGGREGATE_PIN_CATALOG_SEMANTIC_SHA256"),
            StatisticReconciliationActualCanonical.Sha256(pin.SchemaRawSha256, "AGGREGATE_PIN_SCHEMA_RAW_SHA256"),
            StatisticReconciliationActualCanonical.Sha256(pin.SchemaSemanticSha256, "AGGREGATE_PIN_SCHEMA_SEMANTIC_SHA256"),
            StatisticReconciliationActualCanonical.Sha256(pin.StageLockSha256, "AGGREGATE_PIN_STAGE_LOCK_SHA256"),
            StatisticReconciliationActualCanonical.Sha256(pin.SourceMembershipSignature, "AGGREGATE_PIN_MEMBERSHIP_SIGNATURE"),
            StatisticReconciliationActualCanonical.Utc(pin.ComputedAtUtc, "AGGREGATE_PIN_COMPUTED_AT")
                ?? throw Fail("AGGREGATE_PIN_COMPUTED_AT_REQUIRED"),
            string.Empty);
        if (normalized.SourcePayloadRevision < 1 || normalized.SourceLifecycleRevision < 1
            || normalized.DirectSourceRevision < 1 || normalized.DynamicFormVersionNo < 1
            || normalized.ConfigVersionNo < 1 || normalized.ConfigRevision < 1)
        {
            throw Fail("AGGREGATE_PIN_REVISION_INVALID");
        }
        if (!StringComparer.Ordinal.Equals(normalized.RunId, boundary.RunId)
            || !StringComparer.Ordinal.Equals(normalized.GenerationId, boundary.GenerationId)
            || !StringComparer.Ordinal.Equals(normalized.LifecycleEventKey, boundary.OwnerLifecycleEventKey)
            || !SameUtcMillisecond(normalized.ComputedAtUtc, boundary.OwnerComputedAtUtc)
            || normalized.DirectSourceRevision != boundary.DirectSourceRevision
            || !StringComparer.Ordinal.Equals(normalized.DynamicFormFamilyId, boundary.DynamicFormFamilyId)
            || !StringComparer.Ordinal.Equals(normalized.DynamicFormTemplateId, boundary.DynamicFormTemplateId)
            || normalized.DynamicFormVersionNo != boundary.DynamicFormVersionNo
            || !StringComparer.Ordinal.Equals(normalized.DynamicFormSchemaSha256, boundary.DynamicFormSchemaSha256)
            || !StringComparer.Ordinal.Equals(normalized.ConfigId, boundary.ConfigId)
            || !StringComparer.Ordinal.Equals(normalized.ConfigVersionId, boundary.ConfigVersionId)
            || normalized.ConfigVersionNo != boundary.ConfigVersionNo
            || normalized.ConfigRevision != boundary.ConfigRevision
            || !StringComparer.Ordinal.Equals(normalized.ConfigSha256, boundary.ConfigSha256)
            || !StringComparer.Ordinal.Equals(normalized.CandidateChainId, boundary.CandidateChainId)
            || !StringComparer.Ordinal.Equals(normalized.CatalogVersion, boundary.CatalogVersion)
            || !StringComparer.Ordinal.Equals(normalized.CatalogRawSha256, boundary.CatalogRawSha256)
            || !StringComparer.Ordinal.Equals(normalized.CatalogSemanticSha256, boundary.CatalogSemanticSha256)
            || !StringComparer.Ordinal.Equals(normalized.SchemaRawSha256, boundary.SchemaRawSha256)
            || !StringComparer.Ordinal.Equals(normalized.SchemaSemanticSha256, boundary.SchemaSemanticSha256)
            || !StringComparer.Ordinal.Equals(normalized.StageLockSha256, boundary.StageLockSha256)
            || !StringComparer.Ordinal.Equals(normalized.OwnerMembershipSignature, boundary.OwnerMembershipSignature))
        {
            throw Fail("AGGREGATE_ROW_GENERATION_MISMATCH");
        }
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_AGGREGATE_PROVENANCE_V1",
            normalized.RunId, normalized.GenerationId, normalized.OwnerGenerationSha256,
            normalized.LifecycleEventKey, normalized.SourceReportId,
            StatisticReconciliationActualCanonical.Integer(normalized.SourcePayloadRevision),
            normalized.SourcePayloadSha256,
            StatisticReconciliationActualCanonical.Integer(normalized.SourceLifecycleRevision),
            StatisticReconciliationActualCanonical.Integer(normalized.DirectSourceRevision),
            normalized.DynamicFormFamilyId, normalized.DynamicFormTemplateId,
            StatisticReconciliationActualCanonical.Integer(normalized.DynamicFormVersionNo),
            normalized.DynamicFormSchemaSha256,
            normalized.ConfigId, normalized.ConfigVersionId,
            StatisticReconciliationActualCanonical.Integer(normalized.ConfigVersionNo),
            StatisticReconciliationActualCanonical.Integer(normalized.ConfigRevision),
            normalized.ConfigSha256, normalized.CandidateChainId,
            normalized.CatalogVersion, normalized.CatalogRawSha256,
            normalized.CatalogSemanticSha256, normalized.SchemaRawSha256,
            normalized.SchemaSemanticSha256, normalized.StageLockSha256,
            normalized.OwnerMembershipSignature,
            StatisticReconciliationActualCanonical.Instant(normalized.ComputedAtUtc));
        return normalized with { SemanticSha256 = semantic };
    }

    private static ActualAggregateRowState RowState(
        IReadOnlyDictionary<string, ActualSourceMembershipDecision> sources,
        ActualDirectProjectionProvenance provenance,
        ActualAggregateCounts counts,
        ActualAggregateMeasures measures,
        string ownerSemantic)
    {
        var sourceMatched = sources.TryGetValue(provenance.SourceReportId, out var source);
        var payloadMatched = sourceMatched
            && source!.PayloadRevision == provenance.SourcePayloadRevision
            && StringComparer.Ordinal.Equals(source.PayloadSha256, provenance.SourcePayloadSha256);
        var lifecycleMatched = sourceMatched
            && source!.LifecycleRevision == provenance.SourceLifecycleRevision;
        var countsNonNegative = counts.ReportCount >= 0 && counts.RowCount >= 0
                                && counts.NumericValueCount >= 0
                                && measures.TrueCount >= 0 && measures.FalseCount >= 0;
        var reportWithin = counts.ReportCount >= 0 && counts.RowCount >= 0
                           && counts.ReportCount <= counts.RowCount;
        var numericWithin = counts.NumericValueCount >= 0 && counts.RowCount >= 0
                            && counts.NumericValueCount <= counts.RowCount;
        var booleanWithin = measures.TrueCount >= 0 && measures.FalseCount >= 0
                            && counts.RowCount >= 0 && measures.TrueCount <= counts.RowCount
                            && measures.FalseCount <= counts.RowCount - measures.TrueCount;
        var numericShape = counts.NumericValueCount == 0
            ? measures.Min is null && measures.Max is null && measures.Sum == 0
            : measures.Min.HasValue && measures.Max.HasValue && measures.Min <= measures.Max;
        var dateOrdered = (measures.EarliestDateUtc is null && measures.LatestDateUtc is null)
                          || (measures.EarliestDateUtc.HasValue && measures.LatestDateUtc.HasValue
                              && measures.EarliestDateUtc <= measures.LatestDateUtc);
        var decisionSha = source?.DecisionSemanticSha256;
        var semantic = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_AGGREGATE_ROW_STATE_V1",
            StatisticReconciliationActualCanonical.Boolean(sourceMatched),
            StatisticReconciliationActualCanonical.Boolean(payloadMatched),
            StatisticReconciliationActualCanonical.Boolean(lifecycleMatched),
            decisionSha,
            StatisticReconciliationActualCanonical.Boolean(countsNonNegative),
            StatisticReconciliationActualCanonical.Boolean(reportWithin),
            StatisticReconciliationActualCanonical.Boolean(numericWithin),
            StatisticReconciliationActualCanonical.Boolean(booleanWithin),
            StatisticReconciliationActualCanonical.Boolean(numericShape),
            StatisticReconciliationActualCanonical.Boolean(dateOrdered),
            ownerSemantic,
            provenance.SemanticSha256);
        return new ActualAggregateRowState(
            sourceMatched, payloadMatched, lifecycleMatched, decisionSha,
            countsNonNegative, reportWithin, numericWithin, booleanWithin,
            numericShape, dateOrdered, ownerSemantic, semantic);
    }

    private static ActualAggregateTimePin Time(
        string? periodKey,
        string? periodInstanceKey,
        string? periodKind,
        DateTime? anchor,
        DateTime? start,
        DateTime? end,
        DateTime? completed,
        bool historical,
        int reportStatus)
        => new(
            Required(periodKey, "AGGREGATE_PERIOD_KEY"),
            Required(periodInstanceKey, "AGGREGATE_PERIOD_INSTANCE_KEY"),
            Required(periodKind, "AGGREGATE_PERIOD_KIND"),
            StatisticReconciliationActualCanonical.Utc(anchor, "AGGREGATE_PERIOD_ANCHOR"),
            StatisticReconciliationActualCanonical.Utc(start, "AGGREGATE_PERIOD_START"),
            StatisticReconciliationActualCanonical.Utc(end, "AGGREGATE_PERIOD_END"),
            StatisticReconciliationActualCanonical.Utc(completed, "AGGREGATE_COMPLETED_DATE"),
            historical,
            reportStatus);

    private static ActualAggregateMeasures Measures(
        decimal sum,
        decimal? min,
        decimal? max,
        long numericCount,
        long trueCount,
        long falseCount,
        DateTime? earliest,
        DateTime? latest)
        => new(
            sum,
            min,
            max,
            numericCount > 0 ? sum / numericCount : null,
            trueCount,
            falseCount,
            StatisticReconciliationActualCanonical.Utc(earliest, "AGGREGATE_EARLIEST_DATE"),
            StatisticReconciliationActualCanonical.Utc(latest, "AGGREGATE_LATEST_DATE"));

    private static ActualAggregateIdentityPins IdentityPins(
        string conceptSha,
        string grainSha,
        string timeSha,
        string filterSha,
        string configSha,
        string resultSha)
        => new(conceptSha, grainSha, timeSha, filterSha, configSha, resultSha);

    private static string GrainSha(
        string workId,
        string scopeType,
        string scopeId,
        string? rootAssignmentId)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_AGGREGATE_GRAIN_V1",
            workId,
            scopeType,
            scopeId,
            OptionalExact(rootAssignmentId, "AGGREGATE_ROOT_ASSIGNMENT_ID"));

    private static string TimeSha(ActualAggregateTimePin value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_AGGREGATE_TIME_V1",
            value.PeriodKey,
            value.PeriodInstanceKey,
            value.PeriodKind,
            value.PeriodAnchorDateUtc.HasValue ? StatisticReconciliationActualCanonical.Instant(value.PeriodAnchorDateUtc.Value) : null,
            value.PeriodStartDateUtc.HasValue ? StatisticReconciliationActualCanonical.Instant(value.PeriodStartDateUtc.Value) : null,
            value.PeriodEndDateUtc.HasValue ? StatisticReconciliationActualCanonical.Instant(value.PeriodEndDateUtc.Value) : null,
            value.CompletedDateUtc.HasValue ? StatisticReconciliationActualCanonical.Instant(value.CompletedDateUtc.Value) : null,
            StatisticReconciliationActualCanonical.Boolean(value.IsHistoricalData),
            StatisticReconciliationActualCanonical.Integer(value.ReportStatus));

    private static string OwnerFilterSha(string generationId)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_AGGREGATE_OWNER_FILTER_V1",
            "directProjection.generationId",
            generationId,
            "isDeleted",
            "false");

    private static string ConfigIdentitySha(ActualDirectProjectionBoundary value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_AGGREGATE_CONFIG_IDENTITY_V1",
            value.DynamicFormFamilyId,
            value.DynamicFormTemplateId,
            StatisticReconciliationActualCanonical.Integer(value.DynamicFormVersionNo),
            value.DynamicFormSchemaSha256,
            value.ConfigId,
            value.ConfigVersionId,
            StatisticReconciliationActualCanonical.Integer(value.ConfigVersionNo),
            StatisticReconciliationActualCanonical.Integer(value.ConfigRevision),
            value.ConfigSha256,
            value.CandidateChainId,
            value.CatalogVersion,
            value.CatalogRawSha256,
            value.CatalogSemanticSha256,
            value.SchemaRawSha256,
            value.SchemaSemanticSha256,
            value.StageLockSha256);

    private static string ResultGenerationIdentitySha(ActualAggregatePublicationBoundary value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_AGGREGATE_RESULT_GENERATION_V1",
            value.PublicationScopeKey,
            StatisticReconciliationActualCanonical.Integer(value.DirectPublicationRevision),
            value.FreshnessState,
            StatisticReconciliationActualCanonical.Instant(value.PublishedAtUtc),
            value.Direct.RunId,
            value.Direct.GenerationId,
            value.Direct.OwnerGenerationSha256,
            value.Direct.OwnerLifecycleEventKey,
            StatisticReconciliationActualCanonical.Instant(value.Direct.OwnerComputedAtUtc),
            StatisticReconciliationActualCanonical.Integer(value.Direct.DirectSourceRevision),
            value.Direct.OwnerMembershipSignature,
            StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_AGGREGATE_STORE_PINS_V1",
                value.AggregateStoreDigests.Select(x => StatisticReconciliationActualCanonical.Hash(
                    "P10_ACTUAL_AGGREGATE_STORE_PIN_V1",
                    x.Store,
                    StatisticReconciliationActualCanonical.Integer(x.RowCount),
                    x.Sha256))));

    private static string ObservationSemantic(
        string domain,
        string ownerId,
        ActualAggregateIdentityPins identities,
        ActualAggregateCounts counts,
        ActualAggregateMeasures measures,
        ActualAggregateRowState state,
        ActualDirectProjectionProvenance provenance)
        => StatisticReconciliationActualCanonical.Hash(
            domain,
            ownerId,
            identities.ConceptIdentitySha256,
            identities.GrainIdentitySha256,
            identities.TimeIdentitySha256,
            identities.OwnerFilterSha256,
            identities.ConfigIdentitySha256,
            identities.ResultGenerationIdentitySha256,
            StatisticReconciliationActualCanonical.Integer(counts.ReportCount),
            StatisticReconciliationActualCanonical.Integer(counts.RowCount),
            StatisticReconciliationActualCanonical.Integer(counts.NumericValueCount),
            StatisticReconciliationActualCanonical.Number(measures.Sum),
            measures.Min.HasValue ? StatisticReconciliationActualCanonical.Number(measures.Min.Value) : null,
            measures.Max.HasValue ? StatisticReconciliationActualCanonical.Number(measures.Max.Value) : null,
            measures.Mean.HasValue ? StatisticReconciliationActualCanonical.Number(measures.Mean.Value) : null,
            StatisticReconciliationActualCanonical.Integer(measures.TrueCount),
            StatisticReconciliationActualCanonical.Integer(measures.FalseCount),
            measures.EarliestDateUtc.HasValue ? StatisticReconciliationActualCanonical.Instant(measures.EarliestDateUtc.Value) : null,
            measures.LatestDateUtc.HasValue ? StatisticReconciliationActualCanonical.Instant(measures.LatestDateUtc.Value) : null,
            state.SemanticSha256,
            provenance.SemanticSha256);

    private static string OwnerSemantic<T>(string domain, T row)
        => StatisticReconciliationActualCanonical.Hash(
            domain,
            CanonicalPersistedRow(row).CanonicalJson);

    private static string OwnerId(string? value, HashSet<string> ownerIds, string owner)
    {
        var id = Required(value, $"{owner}_AGGREGATE_OWNER_ID");
        if (!ownerIds.Add(id))
            throw Fail($"{owner}_AGGREGATE_OWNER_ID_AMBIGUOUS");
        return id;
    }

    private static ImmutableArray<string> SnapshotStrings(
        IReadOnlyList<string>? values,
        string name)
    {
        if (values is null)
            throw Fail($"{name}_LIST_REQUIRED");
        if (values.Count > 10_000)
            throw Fail($"{name}_LIST_LIMIT");
        return values.Select((value, index) => Required(value, $"{name}_{index}"))
            .ToImmutableArray();
    }

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);

    private static string? OptionalExact(string? value, string name)
        => value is null ? null : StatisticReconciliationActualCanonical.Required(value, name);

    private static bool SameUtcMillisecond(DateTime left, DateTime right)
        => left.Kind == DateTimeKind.Utc && right.Kind == DateTimeKind.Utc
           && left.Ticks / TimeSpan.TicksPerMillisecond == right.Ticks / TimeSpan.TicksPerMillisecond;

    private static StatisticReconciliationActualObservationException Fail(string reason)
        => new(reason);
}
