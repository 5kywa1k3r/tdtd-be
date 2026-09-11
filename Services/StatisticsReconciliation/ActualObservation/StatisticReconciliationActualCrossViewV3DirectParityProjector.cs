using System.Collections.Immutable;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Independently projects the API and export partitions from the exact raw
/// aggregate generation. The two canonical filters are parsed and applied
/// separately; neither view is reconstructed from the other view's bytes.
/// </summary>
internal sealed class
    StatisticReconciliationActualCrossViewV3DirectParityProjector(
        IStatisticReconciliationActualAggregateOwnerReader aggregateOwner,
        IStatisticReconciliationActualCrossViewLabelCatalogOwner labelCatalog)
{
    internal async Task<
        StatisticReconciliationActualCrossViewV3FamilyProjection> ProjectAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            StatisticReconciliationActualExportManifest manifest,
            CancellationToken cancellationToken)
    {
        var material = command.Material;
        var api = command.Api ?? throw Invalid(
            "CROSS_VIEW_OWNER_API_CAPTURE_REQUIRED");
        if (command.Direct is null ||
            !SameBoundary(command.Direct.Boundary, material.Direct) ||
            command.Aggregate is null ||
            !SameBoundary(command.Aggregate.Boundary, material.Aggregate))
            throw Invalid("CROSS_VIEW_OWNER_DIRECT_CAPTURE_BINDING_INVALID");

        var apiFilter =
            new StatisticReconciliationActualCrossViewV3CanonicalFilter(
                api.CanonicalFilterJson);
        var exportFilter =
            new StatisticReconciliationActualCrossViewV3CanonicalFilter(
                Required(manifest.CanonicalFilterJson,
                    "CROSS_VIEW_OWNER_EXPORT_FILTER_JSON"));
        exportFilter.RequireExact(
            "blockId", "bucketKey", "dynamicFormTemplateId", "fieldId",
            "fieldKey", "labelCode", "metricKey", "periodKey");

        var generation =
            StatisticReconciliationActualCrossViewV3OwnerCommon.Generation(api);
        var ownerId = Required(material.Api.OwnerResultId,
            "CROSS_VIEW_OWNER_DIRECT_RESULT_ID");
        var period = Required(material.Run.PeriodInstanceKey,
            "CROSS_VIEW_OWNER_PERIOD_INSTANCE_KEY");
        if (apiFilter.String("periodInstanceKey") != period)
            throw Invalid("CROSS_VIEW_OWNER_API_PERIOD_MISMATCH");

        return api.Surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField
                when manifest.ResultKind ==
                     StatRunExportResultKinds.DirectField =>
                await FieldAsync(command, apiFilter, exportFilter, generation,
                    ownerId, period, cancellationToken),
            StatisticReconciliationActualApiSurfaces.DirectTable
                when manifest.ResultKind ==
                     StatRunExportResultKinds.DirectTable =>
                await TableAsync(command, apiFilter, exportFilter, generation,
                    ownerId, period, cancellationToken),
            StatisticReconciliationActualApiSurfaces.DirectLabel
                when manifest.ResultKind ==
                     StatRunExportResultKinds.DirectLabel =>
                await LabelAsync(command, apiFilter, exportFilter, generation,
                    ownerId, period, cancellationToken),
            _ => throw Invalid("CROSS_VIEW_OWNER_DIRECT_SURFACE_INVALID")
        };
    }

    private async Task<StatisticReconciliationActualCrossViewV3FamilyProjection>
        FieldAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            StatisticReconciliationActualCrossViewV3CanonicalFilter apiFilter,
            StatisticReconciliationActualCrossViewV3CanonicalFilter exportFilter,
            (string GenerationId, string GenerationSha256) generation,
            string ownerId,
            string period,
            CancellationToken cancellationToken)
    {
        apiFilter.RequireAllowed(
            "periodInstanceKey", "fieldId", "fieldKey",
            "statisticLabelCode", "fieldType", "bucketKey", "showInTree",
            "showInDetail", "periodKey", "periodKeyFrom", "periodKeyTo",
            "reportStatus");
        var generationRows = await aggregateOwner.ReadFieldGenerationAsync(
                command.Material.Aggregate,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid(
                "CROSS_VIEW_OWNER_FIELD_ROWS_NULL");
        var raw = SelectTargetRows(command, generationRows, Boundary);

        var apiRows = raw.Where(value => value.ShowInTree || value.ShowInDetail)
            .Where(value => Match(value.FieldId, apiFilter.String("fieldId")))
            .Where(value => Match(value.FieldKey, apiFilter.String("fieldKey")))
            .Where(value => MatchLabel(value.StatisticLabelCodes,
                apiFilter.String("statisticLabelCode")?.ToLowerInvariant()))
            .Where(value => Match(value.FieldType, apiFilter.String("fieldType")))
            .Where(value => Match(value.BucketKey, apiFilter.String("bucketKey")))
            .Where(value => Match(value.ShowInTree,
                apiFilter.Boolean("showInTree")))
            .Where(value => Match(value.ShowInDetail,
                apiFilter.Boolean("showInDetail")))
            .Where(value => Match(value.PeriodKey, apiFilter.String("periodKey")))
            .Where(value => AtOrAfter(value.PeriodKey,
                apiFilter.String("periodKeyFrom")))
            .Where(value => AtOrBefore(value.PeriodKey,
                apiFilter.String("periodKeyTo")))
            .Where(value => Match(value.ReportStatus,
                apiFilter.Int32("reportStatus")))
            .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
            .ThenBy(value => value.FieldKey, StringComparer.Ordinal)
            .ThenBy(value => value.BucketKey, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .Select(ToField).ToArray();

        var exportRows = raw
            .Where(value => value.ShowInTree || value.ShowInDetail)
            .Where(value => Match(value.DynamicFormTemplateId,
                exportFilter.String("dynamicFormTemplateId")))
            .Where(value => Match(value.FieldId, exportFilter.String("fieldId")))
            .Where(value => Match(value.FieldKey, exportFilter.String("fieldKey")))
            .Where(value => Match(value.BucketKey,
                exportFilter.String("bucketKey")))
            .Where(value => Match(value.PeriodKey,
                exportFilter.String("periodKey")))
            .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
            .ThenBy(value => value.FieldKey, StringComparer.Ordinal)
            .ThenBy(value => value.BucketKey, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .Select(ToField).ToArray();

        var totals = StatisticReconciliationActualCrossViewV3OwnerCommon.Totals(
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalRows", apiRows.LongLength),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalValueCount", apiRows.Sum(value => value.ValueCount)),
            StatisticReconciliationActualCrossViewV3OwnerCommon.DecimalTotal(
                "totalSum", apiRows.Sum(value => value.Sum ?? 0m)),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalReportCount", apiRows.Sum(value => value.ReportCount)));
        return Projection(command, generation, ownerId, period,
            StatisticReconciliationActualApiSurfaces.DirectField,
            apiRows.Select(value => (FieldIdentity(value),
                StatisticReconciliationActualCrossViewV3OwnerCommon
                    .CanonicalRow(value))),
            totals,
            StatisticReconciliationActualCrossViewV3OwnerCommon.SerializeRows(
                exportRows));
    }

    private async Task<StatisticReconciliationActualCrossViewV3FamilyProjection>
        TableAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            StatisticReconciliationActualCrossViewV3CanonicalFilter apiFilter,
            StatisticReconciliationActualCrossViewV3CanonicalFilter exportFilter,
            (string GenerationId, string GenerationSha256) generation,
            string ownerId,
            string period,
            CancellationToken cancellationToken)
    {
        apiFilter.RequireAllowed(
            "periodInstanceKey", "dynamicExcelTemplateId", "blockId",
            "tableMode", "metricKey", "metricLabelCode", "dataType",
            "bucketKey", "periodKey", "reportStatus");
        var generationRows = await aggregateOwner.ReadTableMetricGenerationAsync(
                command.Material.Aggregate,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid(
                "CROSS_VIEW_OWNER_TABLE_ROWS_NULL");
        var raw = SelectTargetRows(command, generationRows, Boundary);

        var apiRows = raw
            .Where(value => Match(value.DynamicExcelTemplateId,
                apiFilter.String("dynamicExcelTemplateId")))
            .Where(value => Match(value.BlockId, apiFilter.String("blockId")))
            .Where(value => Match(value.TableMode,
                apiFilter.String("tableMode")?.ToUpperInvariant()))
            .Where(value => Match(value.MetricKey, apiFilter.String("metricKey")))
            .Where(value => Match(value.MetricLabelCode,
                apiFilter.String("metricLabelCode")?.ToLowerInvariant()))
            .Where(value => Match(value.DataType,
                apiFilter.String("dataType")?.ToUpperInvariant()))
            .Where(value => Match(value.BucketKey, apiFilter.String("bucketKey")))
            .Where(value => Match(value.PeriodKey, apiFilter.String("periodKey")))
            .Where(value => Match(value.ReportStatus,
                apiFilter.Int32("reportStatus")))
            .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
            .ThenBy(value => value.BlockId, StringComparer.Ordinal)
            .ThenBy(value => value.MetricKey, StringComparer.Ordinal)
            .ThenBy(value => value.RowKey, StringComparer.Ordinal)
            .ThenBy(value => value.ColumnKey, StringComparer.Ordinal)
            .ThenBy(value => value.BucketKey, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .Select(ToTable).ToArray();

        var exportRows = raw
            .Where(value => Match(value.DynamicFormTemplateId,
                exportFilter.String("dynamicFormTemplateId")))
            .Where(value => Match(value.BlockId, exportFilter.String("blockId")))
            .Where(value => Match(value.MetricKey,
                exportFilter.String("metricKey")))
            .Where(value => Match(value.BucketKey,
                exportFilter.String("bucketKey")))
            .Where(value => Match(value.PeriodKey,
                exportFilter.String("periodKey")))
            .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
            .ThenBy(value => value.BlockId, StringComparer.Ordinal)
            .ThenBy(value => value.MetricKey, StringComparer.Ordinal)
            .ThenBy(value => value.RowKey, StringComparer.Ordinal)
            .ThenBy(value => value.ColumnKey, StringComparer.Ordinal)
            .ThenBy(value => value.BucketKey, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .Select(ToTable).ToArray();

        var totals = StatisticReconciliationActualCrossViewV3OwnerCommon.Totals(
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalRows", apiRows.LongLength),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalValueCount", apiRows.Sum(value => value.ValueCount)),
            StatisticReconciliationActualCrossViewV3OwnerCommon.DecimalTotal(
                "totalSum", apiRows.Sum(value => value.Sum ?? 0m)),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalReportCount", apiRows.Sum(value => value.ReportCount)));
        return Projection(command, generation, ownerId, period,
            StatisticReconciliationActualApiSurfaces.DirectTable,
            apiRows.Select(value => (TableIdentity(value),
                StatisticReconciliationActualCrossViewV3OwnerCommon
                    .CanonicalRow(value))),
            totals,
            StatisticReconciliationActualCrossViewV3OwnerCommon.SerializeRows(
                exportRows));
    }

    private async Task<StatisticReconciliationActualCrossViewV3FamilyProjection>
        LabelAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            StatisticReconciliationActualCrossViewV3CanonicalFilter apiFilter,
            StatisticReconciliationActualCrossViewV3CanonicalFilter exportFilter,
            (string GenerationId, string GenerationSha256) generation,
            string ownerId,
            string period,
            CancellationToken cancellationToken)
    {
        apiFilter.RequireAllowed(
            "periodInstanceKey", "dynamicExcelTemplateId", "labelCode",
            "periodKey", "reportStatus");
        var generationRows = await aggregateOwner.ReadRowLabelGenerationAsync(
                command.Material.Aggregate,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid(
                "CROSS_VIEW_OWNER_LABEL_ROWS_NULL");
        var raw = SelectTargetRows(command, generationRows, Boundary);

        var apiRaw = raw
            .Where(value => Match(value.DynamicExcelTemplateId,
                apiFilter.String("dynamicExcelTemplateId")))
            .Where(value => Match(value.LabelCode,
                apiFilter.String("labelCode")?.ToLowerInvariant()))
            .Where(value => Match(value.PeriodKey, apiFilter.String("periodKey")))
            .Where(value => Match(value.ReportStatus,
                apiFilter.Int32("reportStatus")))
            .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
            .ThenBy(value => value.BlockId, StringComparer.Ordinal)
            .ThenBy(value => value.LabelCode, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var exportRaw = raw
            .Where(value => Match(value.DynamicFormTemplateId,
                exportFilter.String("dynamicFormTemplateId")))
            .Where(value => Match(value.LabelCode,
                exportFilter.String("labelCode")?.ToLowerInvariant()))
            .Where(value => Match(value.PeriodKey,
                exportFilter.String("periodKey")))
            .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
            .ThenBy(value => value.BlockId, StringComparer.Ordinal)
            .ThenBy(value => value.LabelCode, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var catalog = await labelCatalog.ReadAsync(
                apiRaw.Concat(exportRaw).Select(value => value.LabelCode)
                    .Distinct(StringComparer.Ordinal).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var apiRows = apiRaw.Select(value => ToLabel(value,
                catalog.TryGetValue(value.LabelCode, out var label)
                    ? label
                    : null))
            .ToArray();
        var exportRows = exportRaw.Select(value => ToLabel(value,
                catalog.TryGetValue(value.LabelCode, out var label)
                    ? label
                    : null))
            .ToArray();
        var totals = StatisticReconciliationActualCrossViewV3OwnerCommon.Totals(
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalRows", apiRows.LongLength),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalRowCount", apiRows.Sum(value => value.RowCount)),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalReportCount", apiRows.Sum(value => value.ReportCount)));
        return Projection(command, generation, ownerId, period,
            StatisticReconciliationActualApiSurfaces.DirectLabel,
            apiRows.Select(value => (LabelIdentity(value),
                StatisticReconciliationActualCrossViewV3OwnerCommon
                    .CanonicalRow(value))),
            totals,
            StatisticReconciliationActualCrossViewV3OwnerCommon.SerializeRows(
                exportRows));
    }

    private static StatisticReconciliationActualCrossViewV3FamilyProjection
        Projection(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            (string GenerationId, string GenerationSha256) generation,
            string ownerId,
            string period,
            string surface,
            IEnumerable<(string Identity, string CanonicalJson)> rows,
            ImmutableArray<StatisticReconciliationActualApiTotalValue> totals,
            string exportJson)
    {
        var api = command.Api!;
        return new(
            StatisticReconciliationActualCrossViewFamilies.Direct,
            StatisticReconciliationActualCrossViewV3OwnerCommon.ApiBase(
                command.Material.Run.WorkId,
                command.Material.Run.ScopeAssignmentId,
                command.Material.Run.DynamicFormVersionId,
                period,
                api.Authorization.AuthorizationSnapshotSha256,
                surface,
                ownerId,
                generation.GenerationId,
                generation.GenerationSha256,
                api.CanonicalFilterJson,
                api.FilterSha256,
                rows,
                totals),
            exportJson,
            null);
    }

    internal static T[] SelectTargetRows<T>(
        StatisticReconciliationActualCrossViewV2OwnerCommand command,
        IEnumerable<T> rows,
        Func<T, (string WorkId, string ScopeType, string ScopeId,
            string? TemplateId, string Period)> boundary)
    {
        var run = command.Material.Run;
        var observed = rows
            .Select(value => (Value: value, Boundary: boundary(value)))
            .ToArray();
        if (observed.Any(value =>
                value.Boundary.WorkId != run.WorkId ||
                value.Boundary.TemplateId != run.DynamicFormVersionId ||
                value.Boundary.Period != run.PeriodInstanceKey ||
                string.IsNullOrWhiteSpace(value.Boundary.ScopeId) ||
                value.Boundary.ScopeType is not ("WORK" or "ROOT" or
                    "ASSIGNMENT") ||
                value.Boundary.ScopeType == "WORK" &&
                value.Boundary.ScopeId != run.WorkId))
            throw Invalid("CROSS_VIEW_OWNER_DIRECT_BOUNDARY_DRIFT");

        return observed
            .Where(value => value.Boundary.ScopeType == "ASSIGNMENT" &&
                value.Boundary.ScopeId == run.ScopeAssignmentId)
            .Select(value => value.Value)
            .ToArray();
    }

    private static (string, string, string, string?, string) Boundary(
        WorkReportFieldStatAggregate value)
        => (value.WorkId, value.ScopeType, value.ScopeId,
            value.DynamicFormTemplateId, value.PeriodInstanceKey);
    private static (string, string, string, string?, string) Boundary(
        WorkReportTableStatAggregate value)
        => (value.WorkId, value.ScopeType, value.ScopeId,
            value.DynamicFormTemplateId, value.PeriodInstanceKey);
    private static (string, string, string, string?, string) Boundary(
        WorkReportLabelStatAggregate value)
        => (value.WorkId, value.ScopeType, value.ScopeId,
            value.DynamicFormTemplateId, value.PeriodInstanceKey);

    internal static bool SameBoundary(
        ActualAggregatePublicationBoundary left,
        ActualAggregatePublicationBoundary right)
        => left is not null && right is not null &&
           SameBoundary(left.Direct, right.Direct) &&
           StringComparer.Ordinal.Equals(
               left.PublicationScopeKey, right.PublicationScopeKey) &&
           left.DirectPublicationRevision == right.DirectPublicationRevision &&
           StringComparer.Ordinal.Equals(
               left.FreshnessState, right.FreshnessState) &&
           left.PublishedAtUtc == right.PublishedAtUtc &&
           !left.AggregateStoreDigests.IsDefault &&
           !right.AggregateStoreDigests.IsDefault &&
           left.AggregateStoreDigests.Length ==
               right.AggregateStoreDigests.Length &&
           left.AggregateStoreDigests.Zip(right.AggregateStoreDigests)
               .All(pair =>
                   StringComparer.Ordinal.Equals(
                       pair.First.Store, pair.Second.Store) &&
                   pair.First.RowCount == pair.Second.RowCount &&
                   StringComparer.Ordinal.Equals(
                       pair.First.Sha256, pair.Second.Sha256));

    internal static bool SameBoundary(
        ActualDirectProjectionBoundary left,
        ActualDirectProjectionBoundary right)
        => left is not null && right is not null &&
           StringComparer.Ordinal.Equals(left.WorkId, right.WorkId) &&
           StringComparer.Ordinal.Equals(
               left.PeriodInstanceKey, right.PeriodInstanceKey) &&
           StringComparer.Ordinal.Equals(
               left.DynamicFormFamilyId, right.DynamicFormFamilyId) &&
           StringComparer.Ordinal.Equals(
               left.DynamicFormTemplateId, right.DynamicFormTemplateId) &&
           left.DynamicFormVersionNo == right.DynamicFormVersionNo &&
           StringComparer.Ordinal.Equals(
               left.DynamicFormSchemaSha256,
               right.DynamicFormSchemaSha256) &&
           StringComparer.Ordinal.Equals(left.RunId, right.RunId) &&
           StringComparer.Ordinal.Equals(
               left.GenerationId, right.GenerationId) &&
           StringComparer.Ordinal.Equals(
               left.OwnerGenerationSha256, right.OwnerGenerationSha256) &&
           StringComparer.Ordinal.Equals(
               left.OwnerLifecycleEventKey,
               right.OwnerLifecycleEventKey) &&
           left.OwnerComputedAtUtc == right.OwnerComputedAtUtc &&
           left.DirectSourceRevision == right.DirectSourceRevision &&
           StringComparer.Ordinal.Equals(left.ConfigId, right.ConfigId) &&
           StringComparer.Ordinal.Equals(
               left.ConfigVersionId, right.ConfigVersionId) &&
           left.ConfigVersionNo == right.ConfigVersionNo &&
           left.ConfigRevision == right.ConfigRevision &&
           StringComparer.Ordinal.Equals(
               left.ConfigSha256, right.ConfigSha256) &&
           StringComparer.Ordinal.Equals(
               left.CandidateChainId, right.CandidateChainId) &&
           StringComparer.Ordinal.Equals(
               left.CatalogVersion, right.CatalogVersion) &&
           StringComparer.Ordinal.Equals(
               left.CatalogRawSha256, right.CatalogRawSha256) &&
           StringComparer.Ordinal.Equals(
               left.CatalogSemanticSha256,
               right.CatalogSemanticSha256) &&
           StringComparer.Ordinal.Equals(
               left.SchemaRawSha256, right.SchemaRawSha256) &&
           StringComparer.Ordinal.Equals(
               left.SchemaSemanticSha256,
               right.SchemaSemanticSha256) &&
           StringComparer.Ordinal.Equals(
               left.StageLockSha256, right.StageLockSha256) &&
           StringComparer.Ordinal.Equals(
               left.OwnerMembershipSignature,
               right.OwnerMembershipSignature);
    private static bool Match(string? value, string? filter)
        => filter is null || value == filter;
    private static bool Match(bool value, bool? filter)
        => !filter.HasValue || value == filter.Value;
    private static bool Match(int value, int? filter)
        => !filter.HasValue || value == filter.Value;
    private static bool MatchLabel(IReadOnlyCollection<string>? values,
        string? filter)
        => filter is null || values?.Contains(filter,
            StringComparer.Ordinal) == true;
    private static bool AtOrAfter(string value, string? from)
        => from is null || StringComparer.Ordinal.Compare(value, from) >= 0;
    private static bool AtOrBefore(string value, string? to)
        => to is null || StringComparer.Ordinal.Compare(value, to) <= 0;

    private static FieldStatisticSummaryRow ToField(
        WorkReportFieldStatAggregate value) => new()
    {
        WorkId = value.WorkId, ScopeType = value.ScopeType,
        ScopeId = value.ScopeId, RootAssignmentId = value.RootAssignmentId,
        DynamicFormTemplateId = value.DynamicFormTemplateId,
        DynamicFormTemplateCode = value.DynamicFormTemplateCode,
        DynamicFormTemplateName = value.DynamicFormTemplateName,
        FieldId = value.FieldId, FieldKey = value.FieldKey,
        FieldLabel = value.FieldLabel, FieldType = value.FieldType,
        StatisticLabelCodes = value.StatisticLabelCodes,
        ShowInTree = value.ShowInTree, ShowInDetail = value.ShowInDetail,
        BucketKey = value.BucketKey, BucketLabel = value.BucketLabel,
        PeriodKey = value.PeriodKey,
        PeriodInstanceKey = value.PeriodInstanceKey,
        PeriodKind = value.PeriodKind, ReportStatus = value.ReportStatus,
        ValueCount = value.ValueCount,
        NumericValueCount = value.NumericValueCount, Sum = value.Sum,
        Min = value.Min, Max = value.Max,
        Average = value.NumericValueCount == 0
            ? null
            : value.Sum / value.NumericValueCount,
        TrueCount = value.TrueCount, FalseCount = value.FalseCount,
        EarliestDateUtc = value.EarliestDateUtc,
        LatestDateUtc = value.LatestDateUtc,
        ReportCount = value.ReportCount, UpdatedAtUtc = value.UpdatedAtUtc
    };

    private static TableStatisticSummaryRow ToTable(
        WorkReportTableStatAggregate value) => new()
    {
        WorkId = value.WorkId, ScopeType = value.ScopeType,
        ScopeId = value.ScopeId, RootAssignmentId = value.RootAssignmentId,
        DynamicFormTemplateId = value.DynamicFormTemplateId,
        DynamicFormTemplateCode = value.DynamicFormTemplateCode,
        DynamicFormTemplateName = value.DynamicFormTemplateName,
        DynamicExcelTemplateId = value.DynamicExcelTemplateId,
        BlockId = value.BlockId, TableMode = value.TableMode,
        MetricKey = value.MetricKey, MetricLabelCode = value.MetricLabelCode,
        RowKey = value.RowKey, ColumnKey = value.ColumnKey,
        DataType = value.DataType, BucketKey = value.BucketKey,
        BucketLabel = value.BucketLabel, PeriodKey = value.PeriodKey,
        PeriodInstanceKey = value.PeriodInstanceKey,
        PeriodKind = value.PeriodKind, ReportStatus = value.ReportStatus,
        ValueCount = value.ValueCount,
        NumericValueCount = value.NumericValueCount, Sum = value.Sum,
        Min = value.Min, Max = value.Max,
        Average = value.NumericValueCount == 0
            ? null
            : value.Sum / value.NumericValueCount,
        TrueCount = value.TrueCount, FalseCount = value.FalseCount,
        EarliestDateUtc = value.EarliestDateUtc,
        LatestDateUtc = value.LatestDateUtc,
        ReportCount = value.ReportCount, UpdatedAtUtc = value.UpdatedAtUtc
    };

    private static LabelStatisticSummaryRow ToLabel(
        WorkReportLabelStatAggregate value,
        LabelCatalogItem? catalog) => new()
    {
        WorkId = value.WorkId, ScopeType = value.ScopeType,
        ScopeId = value.ScopeId, RootAssignmentId = value.RootAssignmentId,
        DynamicFormTemplateId = value.DynamicFormTemplateId,
        DynamicFormTemplateCode = value.DynamicFormTemplateCode,
        DynamicFormTemplateName = value.DynamicFormTemplateName,
        DynamicExcelTemplateId = value.DynamicExcelTemplateId,
        BlockId = value.BlockId, LabelCode = value.LabelCode,
        LabelName = catalog?.Name, LabelColor = catalog?.Color,
        LabelDataType = catalog?.DataType ?? "NUMBER",
        PeriodKey = value.PeriodKey,
        PeriodInstanceKey = value.PeriodInstanceKey,
        PeriodKind = value.PeriodKind, ReportStatus = value.ReportStatus,
        RowCount = value.RowCount, ReportCount = value.ReportCount,
        UpdatedAtUtc = value.UpdatedAtUtc
    };

    private static string FieldIdentity(FieldStatisticSummaryRow value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_DIRECT_FIELD_ROW_ID_V1",
            value.WorkId, value.ScopeType, value.ScopeId,
            value.RootAssignmentId, value.DynamicFormTemplateId,
            value.FieldId, value.FieldKey, value.FieldType, value.BucketKey,
            value.PeriodKey, value.PeriodInstanceKey,
            StatisticReconciliationActualCanonical.Integer(value.ReportStatus));
    private static string TableIdentity(TableStatisticSummaryRow value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_DIRECT_TABLE_ROW_ID_V1",
            value.WorkId, value.ScopeType, value.ScopeId,
            value.RootAssignmentId, value.DynamicFormTemplateId,
            value.DynamicExcelTemplateId, value.BlockId, value.TableMode,
            value.MetricKey, value.RowKey, value.ColumnKey, value.DataType,
            value.BucketKey, value.PeriodKey, value.PeriodInstanceKey,
            StatisticReconciliationActualCanonical.Integer(value.ReportStatus));
    private static string LabelIdentity(LabelStatisticSummaryRow value)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_DIRECT_LABEL_ROW_ID_V1",
            value.WorkId, value.ScopeType, value.ScopeId,
            value.RootAssignmentId, value.DynamicFormTemplateId,
            value.DynamicExcelTemplateId, value.BlockId, value.LabelCode,
            value.PeriodKey, value.PeriodInstanceKey,
            StatisticReconciliationActualCanonical.Integer(value.ReportStatus));

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);
    private static StatisticReconciliationActualObservationException Invalid(
        string reason) => new(reason);
}
