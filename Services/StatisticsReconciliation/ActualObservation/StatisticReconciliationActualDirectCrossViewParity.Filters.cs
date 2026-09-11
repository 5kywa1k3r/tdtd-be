using System.Collections.Immutable;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualDirectCrossViewParity
{
    private static readonly ImmutableHashSet<string> FieldApiFilterNames =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "periodInstanceKey", "fieldId", "fieldKey",
            "statisticLabelCode", "fieldType", "bucketKey", "showInTree",
            "showInDetail", "periodKey", "periodKeyFrom", "periodKeyTo",
            "reportStatus");
    private static readonly ImmutableHashSet<string> TableApiFilterNames =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "periodInstanceKey", "dynamicExcelTemplateId", "blockId",
            "tableMode", "metricKey", "metricLabelCode", "dataType",
            "bucketKey", "periodKey", "reportStatus");
    private static readonly ImmutableHashSet<string> LabelApiFilterNames =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "periodInstanceKey", "dynamicExcelTemplateId", "labelCode",
            "periodKey", "reportStatus");
    private static readonly ImmutableArray<string> ExportFilterNames =
    [
        "blockId", "bucketKey", "dynamicFormTemplateId", "fieldId",
        "fieldKey", "labelCode", "metricKey", "periodKey"
    ];

    private static ValidatedFilters ValidateFilters(
        NormalizedPlan plan,
        StatisticReconciliationActualDirectCrossViewParityActual? actual)
    {
        if (actual is null)
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .InputRequired);
        if (!Eq(actual.SchemaVersion, Schema))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .SchemaUnsupported);
        var exportJson = CanonicalObject(
            actual.CanonicalExportFilterJson,
            StatisticReconciliationActualDirectCrossViewParityFailures
                .PreimageMissing);
        var api = ReadApiFilter(plan.Surface, plan.CanonicalApiFilterJson);
        var export = ReadExportFilter(exportJson);
        if (!Eq(api.PeriodInstanceKey, plan.PeriodInstanceKey) ||
            !Eq(export.DynamicFormTemplateId, plan.DynamicFormTemplateId) ||
            !Equivalent(plan.Surface, api, export))
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .FilterMismatch);
        }
        var exportSha = StatisticReconciliationActualJson.RawSha256(exportJson);
        var relation = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_FILTER_RELATION_V1",
            plan.Surface,
            plan.PeriodInstanceKey,
            plan.CanonicalApiFilterJson,
            plan.ApiFilterSha256,
            exportJson,
            exportSha,
            api.SemanticSha256,
            export.SemanticSha256);
        return new(api, export, exportJson, exportSha, relation);
    }

    private static ApiFilter ReadApiFilter(string surface, string json)
    {
        var allowed = surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField =>
                FieldApiFilterNames,
            StatisticReconciliationActualApiSurfaces.DirectTable =>
                TableApiFilterNames,
            StatisticReconciliationActualApiSurfaces.DirectLabel =>
                LabelApiFilterNames,
            _ => throw F(
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .SurfaceUnsupported)
        };
        var values = Properties(json);
        if (values.Keys.Any(name => !allowed.Contains(name)))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .FilterInvalid);
        var periodInstance = StringValue(values, "periodInstanceKey", true);
        var fieldId = StringValue(values, "fieldId");
        var fieldKey = StringValue(values, "fieldKey");
        var blockId = StringValue(values, "blockId");
        var metricKey = StringValue(values, "metricKey");
        var labelCode = StringValue(values, "labelCode");
        var periodKey = StringValue(values, "periodKey");
        var bucketKey = StringValue(values, "bucketKey");
        var unsupported = surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField => new[]
            {
                "statisticLabelCode", "fieldType", "showInTree",
                "showInDetail", "periodKeyFrom", "periodKeyTo",
                "reportStatus"
            },
            StatisticReconciliationActualApiSurfaces.DirectTable => new[]
            {
                "dynamicExcelTemplateId", "tableMode", "metricLabelCode",
                "dataType", "reportStatus"
            },
            _ => new[] { "dynamicExcelTemplateId", "reportStatus" }
        };
        if (unsupported.Any(name => Present(values, name)))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .FilterMismatch);
        if (surface == StatisticReconciliationActualApiSurfaces.DirectLabel &&
            labelCode is not null)
            labelCode = labelCode.ToLowerInvariant();
        var semantic = H(
            "P10_ACTUAL_DIRECT_API_FILTER_SEMANTICS_V1",
            surface,
            periodInstance,
            fieldId,
            fieldKey,
            blockId,
            metricKey,
            labelCode,
            periodKey,
            bucketKey);
        return new(
            periodInstance!, fieldId, fieldKey, blockId, metricKey,
            labelCode, periodKey, bucketKey, semantic);
    }

    private static ExportFilter ReadExportFilter(string json)
    {
        var values = Properties(json);
        if (!values.Keys.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(ExportFilterNames, StringComparer.Ordinal))
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .FilterInvalid);
        }
        var dynamicTemplate = StringValue(
            values, "dynamicFormTemplateId", true);
        var fieldId = StringValue(values, "fieldId");
        var fieldKey = StringValue(values, "fieldKey");
        var blockId = StringValue(values, "blockId");
        var metricKey = StringValue(values, "metricKey");
        var labelCode = StringValue(values, "labelCode");
        var periodKey = StringValue(values, "periodKey");
        var bucketKey = StringValue(values, "bucketKey");
        if (labelCode is not null && !Eq(labelCode, labelCode.ToLowerInvariant()))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .FilterInvalid);
        var semantic = H(
            "P10_ACTUAL_DIRECT_EXPORT_FILTER_SEMANTICS_V1",
            dynamicTemplate,
            fieldId,
            fieldKey,
            blockId,
            metricKey,
            labelCode,
            periodKey,
            bucketKey);
        return new(
            dynamicTemplate!, fieldId, fieldKey, blockId, metricKey,
            labelCode, periodKey, bucketKey, semantic);
    }

    private static bool Equivalent(
        string surface,
        ApiFilter api,
        ExportFilter export)
        => surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField =>
                Eq(api.FieldId, export.FieldId) &&
                Eq(api.FieldKey, export.FieldKey) &&
                Eq(api.BucketKey, export.BucketKey) &&
                Eq(api.PeriodKey, export.PeriodKey) &&
                export.BlockId is null && export.MetricKey is null &&
                export.LabelCode is null,
            StatisticReconciliationActualApiSurfaces.DirectTable =>
                Eq(api.BlockId, export.BlockId) &&
                Eq(api.MetricKey, export.MetricKey) &&
                Eq(api.BucketKey, export.BucketKey) &&
                Eq(api.PeriodKey, export.PeriodKey) &&
                export.FieldId is null && export.FieldKey is null &&
                export.LabelCode is null,
            StatisticReconciliationActualApiSurfaces.DirectLabel =>
                Eq(api.BlockId, export.BlockId) &&
                Eq(api.LabelCode, export.LabelCode) &&
                Eq(api.PeriodKey, export.PeriodKey) &&
                export.FieldId is null && export.FieldKey is null &&
                export.MetricKey is null && export.BucketKey is null,
            _ => false
        };

    private static Dictionary<string, JsonElement> Properties(string json)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            json, "PARITY_FILTER_JSON");
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .FilterInvalid);
        return document.RootElement.EnumerateObject().ToDictionary(
            item => item.Name,
            item => item.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static string? StringValue(
        IReadOnlyDictionary<string, JsonElement> values,
        string name,
        bool required = false)
    {
        if (!values.TryGetValue(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            if (required)
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .FilterInvalid);
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .FilterInvalid);
        return Required(value.GetString());
    }

    private static bool Present(
        IReadOnlyDictionary<string, JsonElement> values,
        string name)
        => values.TryGetValue(name, out var value) &&
           value.ValueKind != JsonValueKind.Null;

    private sealed record ApiFilter(
        string PeriodInstanceKey,
        string? FieldId,
        string? FieldKey,
        string? BlockId,
        string? MetricKey,
        string? LabelCode,
        string? PeriodKey,
        string? BucketKey,
        string SemanticSha256);

    private sealed record ExportFilter(
        string DynamicFormTemplateId,
        string? FieldId,
        string? FieldKey,
        string? BlockId,
        string? MetricKey,
        string? LabelCode,
        string? PeriodKey,
        string? BucketKey,
        string SemanticSha256);

    private sealed record ValidatedFilters(
        ApiFilter ApiFilter,
        ExportFilter ExportFilter,
        string CanonicalExportFilterJson,
        string ExportFilterSha256,
        string RelationSha256);
}
