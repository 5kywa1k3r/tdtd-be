using tdtd_be.DTOs.Statistics;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed class StatisticReconciliationActualCrossViewV3DirectProjector(
    IStatisticReconciliationActualAggregateOwnerReader aggregateOwner,
    IStatisticReconciliationActualCrossViewLabelCatalogOwner labelCatalog)
{
    internal async Task<
        StatisticReconciliationActualCrossViewV3FamilyProjection> ProjectAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            CancellationToken cancellationToken)
    {
        var material = command.Material;
        var api = command.Api ?? throw Invalid(
            "CROSS_VIEW_OWNER_API_CAPTURE_REQUIRED");
        if (command.Aggregate is null)
            throw Invalid("CROSS_VIEW_OWNER_AGGREGATE_CAPTURE_REQUIRED");
        var filter = new StatisticReconciliationActualCrossViewV3Filter(
            api.CanonicalFilterJson);
        var generation =
            StatisticReconciliationActualCrossViewV3OwnerCommon.Generation(api);
        var authorization = api.Authorization.AuthorizationSnapshotSha256;
        var ownerId = Required(material.Api.OwnerResultId,
            "CROSS_VIEW_OWNER_DIRECT_RESULT_ID");
        var period = Required(material.Run.PeriodInstanceKey,
            "CROSS_VIEW_OWNER_PERIOD_INSTANCE_KEY");
        return api.Surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField =>
                await FieldAsync(
                    command, filter, generation, authorization, ownerId,
                    period, cancellationToken),
            StatisticReconciliationActualApiSurfaces.DirectTable =>
                await TableAsync(
                    command, filter, generation, authorization, ownerId,
                    period, cancellationToken),
            StatisticReconciliationActualApiSurfaces.DirectLabel =>
                await LabelAsync(
                    command, filter, generation, authorization, ownerId,
                    period, cancellationToken),
            _ => throw Invalid("CROSS_VIEW_OWNER_DIRECT_SURFACE_INVALID")
        };
    }

    private async Task<
        StatisticReconciliationActualCrossViewV3FamilyProjection> FieldAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            StatisticReconciliationActualCrossViewV3Filter filter,
            (string GenerationId, string GenerationSha256) generation,
            string authorization,
            string ownerId,
            string period,
            CancellationToken cancellationToken)
    {
        filter.RequireExact(
            "periodInstanceKey", "fieldId", "fieldKey", "bucketKey",
            "periodKey");
        var raw = await aggregateOwner.ReadFieldGenerationAsync(
                command.Material.Aggregate,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid(
                "CROSS_VIEW_OWNER_FIELD_ROWS_NULL");
        RequireBoundary(command, raw.Select(RowBoundary));
        var rows = raw
            .Where(value => value.ShowInTree || value.ShowInDetail)
            .Where(value => Same(value.FieldId, filter.String("fieldId")))
            .Where(value => Same(value.FieldKey, filter.String("fieldKey")))
            .Where(value => Same(value.BucketKey, filter.String("bucketKey")))
            .Where(value => Same(value.PeriodKey, filter.String("periodKey")))
            .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
            .ThenBy(value => value.FieldKey, StringComparer.Ordinal)
            .ThenBy(value => value.BucketKey, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .Select(ToField)
            .ToArray();
        var apiRows = rows.Select(value => (
            FieldIdentity(value),
            StatisticReconciliationActualCrossViewV3OwnerCommon.CanonicalRow(
                value)));
        var totals = StatisticReconciliationActualCrossViewV3OwnerCommon.Totals(
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalRows", rows.LongLength),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalValueCount", rows.Sum(value => value.ValueCount)),
            StatisticReconciliationActualCrossViewV3OwnerCommon.DecimalTotal(
                "totalSum", rows.Sum(value => value.Sum ?? 0m)),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalReportCount", rows.Sum(value => value.ReportCount)));
        return Projection(
            command, generation, authorization, ownerId, period,
            StatisticReconciliationActualApiSurfaces.DirectField,
            apiRows, totals,
            StatisticReconciliationActualCrossViewV3OwnerCommon.SerializeRows(
                rows));
    }

    private async Task<
        StatisticReconciliationActualCrossViewV3FamilyProjection> TableAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            StatisticReconciliationActualCrossViewV3Filter filter,
            (string GenerationId, string GenerationSha256) generation,
            string authorization,
            string ownerId,
            string period,
            CancellationToken cancellationToken)
    {
        filter.RequireExact(
            "periodInstanceKey", "blockId", "metricKey", "bucketKey",
            "periodKey");
        var raw = await aggregateOwner.ReadTableMetricGenerationAsync(
                command.Material.Aggregate,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid(
                "CROSS_VIEW_OWNER_TABLE_ROWS_NULL");
        RequireBoundary(command, raw.Select(RowBoundary));
        var rows = raw
            .Where(value => Same(value.BlockId, filter.String("blockId")))
            .Where(value => Same(value.MetricKey, filter.String("metricKey")))
            .Where(value => Same(value.BucketKey, filter.String("bucketKey")))
            .Where(value => Same(value.PeriodKey, filter.String("periodKey")))
            .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
            .ThenBy(value => value.BlockId, StringComparer.Ordinal)
            .ThenBy(value => value.MetricKey, StringComparer.Ordinal)
            .ThenBy(value => value.RowKey, StringComparer.Ordinal)
            .ThenBy(value => value.ColumnKey, StringComparer.Ordinal)
            .ThenBy(value => value.BucketKey, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .Select(ToTable)
            .ToArray();
        var apiRows = rows.Select(value => (
            TableIdentity(value),
            StatisticReconciliationActualCrossViewV3OwnerCommon.CanonicalRow(
                value)));
        var totals = StatisticReconciliationActualCrossViewV3OwnerCommon.Totals(
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalRows", rows.LongLength),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalValueCount", rows.Sum(value => value.ValueCount)),
            StatisticReconciliationActualCrossViewV3OwnerCommon.DecimalTotal(
                "totalSum", rows.Sum(value => value.Sum ?? 0m)),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalReportCount", rows.Sum(value => value.ReportCount)));
        return Projection(
            command, generation, authorization, ownerId, period,
            StatisticReconciliationActualApiSurfaces.DirectTable,
            apiRows, totals,
            StatisticReconciliationActualCrossViewV3OwnerCommon.SerializeRows(
                rows));
    }

    private async Task<
        StatisticReconciliationActualCrossViewV3FamilyProjection> LabelAsync(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            StatisticReconciliationActualCrossViewV3Filter filter,
            (string GenerationId, string GenerationSha256) generation,
            string authorization,
            string ownerId,
            string period,
            CancellationToken cancellationToken)
    {
        filter.RequireExact("periodInstanceKey", "labelCode", "periodKey");
        var raw = await aggregateOwner.ReadRowLabelGenerationAsync(
                command.Material.Aggregate,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid(
                "CROSS_VIEW_OWNER_LABEL_ROWS_NULL");
        RequireBoundary(command, raw.Select(RowBoundary));
        var filtered = raw
            .Where(value => Same(
                value.LabelCode,
                filter.String("labelCode")?.ToLowerInvariant()))
            .Where(value => Same(value.PeriodKey, filter.String("periodKey")))
            .OrderBy(value => value.PeriodKey, StringComparer.Ordinal)
            .ThenBy(value => value.BlockId, StringComparer.Ordinal)
            .ThenBy(value => value.LabelCode, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var catalog = await labelCatalog.ReadAsync(
                filtered.Select(value => value.LabelCode).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        var rows = filtered.Select(value => ToLabel(
                value,
                catalog.TryGetValue(value.LabelCode, out var label)
                    ? label
                    : null))
            .ToArray();
        var apiRows = rows.Select(value => (
            LabelIdentity(value),
            StatisticReconciliationActualCrossViewV3OwnerCommon.CanonicalRow(
                value)));
        var totals = StatisticReconciliationActualCrossViewV3OwnerCommon.Totals(
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalRows", rows.LongLength),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalRowCount", rows.Sum(value => value.RowCount)),
            StatisticReconciliationActualCrossViewV3OwnerCommon.IntegerTotal(
                "totalReportCount", rows.Sum(value => value.ReportCount)));
        return Projection(
            command, generation, authorization, ownerId, period,
            StatisticReconciliationActualApiSurfaces.DirectLabel,
            apiRows, totals,
            StatisticReconciliationActualCrossViewV3OwnerCommon.SerializeRows(
                rows));
    }

    private static StatisticReconciliationActualCrossViewV3FamilyProjection
        Projection(
            StatisticReconciliationActualCrossViewV2OwnerCommand command,
            (string GenerationId, string GenerationSha256) generation,
            string authorization,
            string ownerId,
            string period,
            string surface,
            IEnumerable<(string Identity, string CanonicalJson)> rows,
            System.Collections.Immutable.ImmutableArray<
                StatisticReconciliationActualApiTotalValue> totals,
            string exportJson)
    {
        var api = command.Api!;
        var apiBase =
            StatisticReconciliationActualCrossViewV3OwnerCommon.ApiBase(
                command.Material.Run.WorkId,
                command.Material.Run.ScopeAssignmentId,
                command.Material.Run.DynamicFormVersionId,
                period,
                authorization,
                surface,
                ownerId,
                generation.GenerationId,
                generation.GenerationSha256,
                api.CanonicalFilterJson,
                api.FilterSha256,
                rows,
                totals);
        return new(
            StatisticReconciliationActualCrossViewFamilies.Direct,
            apiBase,
            exportJson,
            null);
    }

    private static (string WorkId, string ScopeType, string ScopeId,
        string? TemplateId, string Period) RowBoundary(
            WorkReportFieldStatAggregate value)
        => (value.WorkId, value.ScopeType, value.ScopeId,
            value.DynamicFormTemplateId, value.PeriodInstanceKey);

    private static (string WorkId, string ScopeType, string ScopeId,
        string? TemplateId, string Period) RowBoundary(
            WorkReportTableStatAggregate value)
        => (value.WorkId, value.ScopeType, value.ScopeId,
            value.DynamicFormTemplateId, value.PeriodInstanceKey);

    private static (string WorkId, string ScopeType, string ScopeId,
        string? TemplateId, string Period) RowBoundary(
            WorkReportLabelStatAggregate value)
        => (value.WorkId, value.ScopeType, value.ScopeId,
            value.DynamicFormTemplateId, value.PeriodInstanceKey);

    private static void RequireBoundary(
        StatisticReconciliationActualCrossViewV2OwnerCommand command,
        IEnumerable<(string WorkId, string ScopeType, string ScopeId,
            string? TemplateId, string Period)> rows)
    {
        var run = command.Material.Run;
        if (rows.Any(value =>
                value.WorkId != run.WorkId ||
                value.ScopeType != "ASSIGNMENT" ||
                value.ScopeId != run.ScopeAssignmentId ||
                value.TemplateId != run.DynamicFormVersionId ||
                value.Period != run.PeriodInstanceKey))
            throw Invalid("CROSS_VIEW_OWNER_DIRECT_BOUNDARY_DRIFT");
    }

    private static bool Same(string? value, string? filter)
        => filter is null || value == filter;

    private static FieldStatisticSummaryRow ToField(
        WorkReportFieldStatAggregate value)
        => new()
        {
            WorkId = value.WorkId,
            ScopeType = value.ScopeType,
            ScopeId = value.ScopeId,
            RootAssignmentId = value.RootAssignmentId,
            DynamicFormTemplateId = value.DynamicFormTemplateId,
            DynamicFormTemplateCode = value.DynamicFormTemplateCode,
            DynamicFormTemplateName = value.DynamicFormTemplateName,
            FieldId = value.FieldId,
            FieldKey = value.FieldKey,
            FieldLabel = value.FieldLabel,
            FieldType = value.FieldType,
            StatisticLabelCodes = value.StatisticLabelCodes,
            ShowInTree = value.ShowInTree,
            ShowInDetail = value.ShowInDetail,
            BucketKey = value.BucketKey,
            BucketLabel = value.BucketLabel,
            PeriodKey = value.PeriodKey,
            PeriodInstanceKey = value.PeriodInstanceKey,
            PeriodKind = value.PeriodKind,
            ReportStatus = value.ReportStatus,
            ValueCount = value.ValueCount,
            NumericValueCount = value.NumericValueCount,
            Sum = value.Sum,
            Min = value.Min,
            Max = value.Max,
            Average = value.NumericValueCount == 0
                ? null
                : value.Sum / value.NumericValueCount,
            TrueCount = value.TrueCount,
            FalseCount = value.FalseCount,
            EarliestDateUtc = value.EarliestDateUtc,
            LatestDateUtc = value.LatestDateUtc,
            ReportCount = value.ReportCount,
            UpdatedAtUtc = value.UpdatedAtUtc
        };

    private static TableStatisticSummaryRow ToTable(
        WorkReportTableStatAggregate value)
        => new()
        {
            WorkId = value.WorkId,
            ScopeType = value.ScopeType,
            ScopeId = value.ScopeId,
            RootAssignmentId = value.RootAssignmentId,
            DynamicFormTemplateId = value.DynamicFormTemplateId,
            DynamicFormTemplateCode = value.DynamicFormTemplateCode,
            DynamicFormTemplateName = value.DynamicFormTemplateName,
            DynamicExcelTemplateId = value.DynamicExcelTemplateId,
            BlockId = value.BlockId,
            TableMode = value.TableMode,
            MetricKey = value.MetricKey,
            MetricLabelCode = value.MetricLabelCode,
            RowKey = value.RowKey,
            ColumnKey = value.ColumnKey,
            DataType = value.DataType,
            BucketKey = value.BucketKey,
            BucketLabel = value.BucketLabel,
            PeriodKey = value.PeriodKey,
            PeriodInstanceKey = value.PeriodInstanceKey,
            PeriodKind = value.PeriodKind,
            ReportStatus = value.ReportStatus,
            ValueCount = value.ValueCount,
            NumericValueCount = value.NumericValueCount,
            Sum = value.Sum,
            Min = value.Min,
            Max = value.Max,
            Average = value.NumericValueCount == 0
                ? null
                : value.Sum / value.NumericValueCount,
            TrueCount = value.TrueCount,
            FalseCount = value.FalseCount,
            EarliestDateUtc = value.EarliestDateUtc,
            LatestDateUtc = value.LatestDateUtc,
            ReportCount = value.ReportCount,
            UpdatedAtUtc = value.UpdatedAtUtc
        };

    private static LabelStatisticSummaryRow ToLabel(
        WorkReportLabelStatAggregate value,
        LabelCatalogItem? catalog)
        => new()
        {
            WorkId = value.WorkId,
            ScopeType = value.ScopeType,
            ScopeId = value.ScopeId,
            RootAssignmentId = value.RootAssignmentId,
            DynamicFormTemplateId = value.DynamicFormTemplateId,
            DynamicFormTemplateCode = value.DynamicFormTemplateCode,
            DynamicFormTemplateName = value.DynamicFormTemplateName,
            DynamicExcelTemplateId = value.DynamicExcelTemplateId,
            BlockId = value.BlockId,
            LabelCode = value.LabelCode,
            LabelName = catalog?.Name,
            LabelColor = catalog?.Color,
            LabelDataType = catalog?.DataType ?? "NUMBER",
            PeriodKey = value.PeriodKey,
            PeriodInstanceKey = value.PeriodInstanceKey,
            PeriodKind = value.PeriodKind,
            ReportStatus = value.ReportStatus,
            RowCount = value.RowCount,
            ReportCount = value.ReportCount,
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
