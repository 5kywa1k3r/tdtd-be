using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualDirectCrossViewParity
{
    private static readonly ImmutableArray<string> FieldProperties =
    [
        "workId", "scopeType", "scopeId", "rootAssignmentId",
        "dynamicFormTemplateId", "dynamicFormTemplateCode",
        "dynamicFormTemplateName", "fieldId", "fieldKey", "fieldLabel",
        "fieldType", "statisticLabelCodes", "showInTree", "showInDetail",
        "bucketKey", "bucketLabel", "periodKey", "periodInstanceKey",
        "periodKind", "reportStatus", "valueCount", "numericValueCount",
        "sum", "min", "max", "average", "trueCount", "falseCount",
        "earliestDateUtc", "latestDateUtc", "reportCount", "updatedAtUtc"
    ];
    private static readonly ImmutableArray<string> TableProperties =
    [
        "workId", "scopeType", "scopeId", "rootAssignmentId",
        "dynamicFormTemplateId", "dynamicFormTemplateCode",
        "dynamicFormTemplateName", "dynamicExcelTemplateId", "blockId",
        "tableMode", "metricKey", "metricLabelCode", "rowKey",
        "columnKey", "dataType", "bucketKey", "bucketLabel", "periodKey",
        "periodInstanceKey", "periodKind", "reportStatus", "valueCount",
        "numericValueCount", "sum", "min", "max", "average",
        "trueCount", "falseCount", "earliestDateUtc", "latestDateUtc",
        "reportCount", "updatedAtUtc"
    ];
    private static readonly ImmutableArray<string> LabelProperties =
    [
        "workId", "scopeType", "scopeId", "rootAssignmentId",
        "dynamicFormTemplateId", "dynamicFormTemplateCode",
        "dynamicFormTemplateName", "dynamicExcelTemplateId", "blockId",
        "labelCode", "labelName", "labelColor", "labelDataType",
        "statisticLabelLayer", "runtimeLabelLayer", "catalogLabelLayer",
        "configurationLayer", "periodKey", "periodInstanceKey",
        "periodKind", "reportStatus", "rowCount", "reportCount",
        "updatedAtUtc"
    ];

    private static ValidatedBase ValidateBase(
        NormalizedPlan plan,
        StatisticReconciliationActualDirectCrossViewBaseProjection? value)
    {
        if (value is null || !Eq(value.SchemaVersion, Schema))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .BaseProjectionInvalid);
        if (!Eq(value.Surface, plan.Surface) ||
            !Eq(value.WorkId, plan.WorkId) ||
            !Eq(value.ScopeAssignmentId, plan.ScopeAssignmentId) ||
            !Eq(value.DynamicFormTemplateId, plan.DynamicFormTemplateId) ||
            !Eq(value.PeriodInstanceKey, plan.PeriodInstanceKey) ||
            !Eq(value.P9RunId, plan.P9RunId) ||
            !Eq(value.P9GenerationId, plan.P9GenerationId) ||
            !Eq(value.P9GenerationSha256, plan.P9GenerationSha256) ||
            value.P9DirectSourceRevision != plan.P9DirectSourceRevision ||
            value.P9DirectPublicationRevision !=
                plan.P9DirectPublicationRevision ||
            !Eq(value.CanonicalFilterJson, plan.CanonicalApiFilterJson) ||
            !Eq(value.FilterSha256, plan.ApiFilterSha256) ||
            value.Rows.IsDefault || value.FullFilterTotals.IsDefault ||
            value.Rows.Length != plan.ExpectedTotalRows ||
            value.Rows.Length > MaximumRows)
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .BaseProjectionInvalid);
        }
        var filter = ReadApiFilter(plan.Surface, plan.CanonicalApiFilterJson);
        var rows = value.Rows.Select((row, index) =>
        {
            if (row is null || row.Ordinal != index)
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .BaseProjectionInvalid);
            return ValidateSemanticRow(
                plan,
                filter,
                index,
                row.Identity,
                row.CanonicalRowJson,
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .BaseProjectionInvalid);
        }).ToImmutableArray();
        RequireUniqueRows(rows,
            StatisticReconciliationActualDirectCrossViewParityFailures
                .BaseProjectionInvalid);
        var totals = NormalizeTotals(
            value.FullFilterTotals,
            StatisticReconciliationActualDirectCrossViewParityFailures
                .BaseProjectionInvalid);
        var recomputedTotals = TotalsForRows(plan.Surface, rows);
        if (!totals.SequenceEqual(recomputedTotals))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .TotalsMismatch);
        var rowsSha = HS(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_BASE_ROWS_V1",
            rows.Select(row => row.SemanticSha256));
        var totalsSha = TotalsSha(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_BASE_TOTALS_V1", totals);
        var projectionSha = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_BASE_PROJECTION_V1",
            Schema,
            plan.Surface,
            plan.WorkId,
            plan.ScopeAssignmentId,
            plan.DynamicFormTemplateId,
            plan.PeriodInstanceKey,
            plan.P9RunId,
            plan.P9GenerationId,
            plan.P9GenerationSha256,
            I(plan.P9DirectSourceRevision),
            I(plan.P9DirectPublicationRevision),
            plan.CanonicalApiFilterJson,
            plan.ApiFilterSha256,
            rowsSha,
            I(rows.Length),
            totalsSha);
        return new(rows, totals, rowsSha, totalsSha, projectionSha);
    }

    private static SemanticRow ValidateSemanticRow(
        NormalizedPlan plan,
        ApiFilter filter,
        int ordinal,
        string? identity,
        string? json,
        string failureCode)
    {
        if (ordinal < 0 || string.IsNullOrWhiteSpace(json))
            throw F(failureCode);
        var canonical = CanonicalRow(json!, failureCode, out var root);
        var expectedProperties = PropertiesFor(plan.Surface);
        var names = root.EnumerateObject().Select(item => item.Name).ToArray();
        if (!names.OrderBy(item => item, StringComparer.Ordinal).SequenceEqual(
                expectedProperties.OrderBy(item => item, StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw F(failureCode);
        RequireRowTarget(plan, root, failureCode);
        RequireRowFilter(plan.Surface, filter, root, failureCode);
        var computedIdentity = RowIdentity(plan.Surface, root, failureCode);
        if (!Eq(identity, computedIdentity))
            throw F(failureCode);
        var semantic = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_SEMANTIC_ROW_V1",
            plan.Surface,
            I(ordinal),
            computedIdentity,
            canonical);
        return new(ordinal, computedIdentity, canonical, root, semantic);
    }

    private static string CanonicalRow(
        string json,
        string failureCode,
        out JsonElement root)
    {
        using var document = StatisticReconciliationActualJson.ParseStrict(
            json, "PARITY_ROW_JSON");
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw F(failureCode);
        var canonical = StatisticReconciliationActualJson.Canonicalize(
            document.RootElement);
        if (!Eq(canonical, json))
            throw F(failureCode);
        root = document.RootElement.Clone();
        return canonical;
    }

    private static ImmutableArray<string> PropertiesFor(string surface)
        => surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField =>
                FieldProperties,
            StatisticReconciliationActualApiSurfaces.DirectTable =>
                TableProperties,
            StatisticReconciliationActualApiSurfaces.DirectLabel =>
                LabelProperties,
            _ => throw F(
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .SurfaceUnsupported)
        };

    private static void RequireRowTarget(
        NormalizedPlan plan,
        JsonElement row,
        string failureCode)
    {
        if (!Eq(JsonString(row, "workId", failureCode), plan.WorkId) ||
            !Eq(JsonString(row, "scopeType", failureCode), AssignmentScope) ||
            !Eq(JsonString(row, "scopeId", failureCode),
                plan.ScopeAssignmentId) ||
            !Eq(JsonString(row, "dynamicFormTemplateId", failureCode),
                plan.DynamicFormTemplateId) ||
            !Eq(JsonString(row, "periodInstanceKey", failureCode),
                plan.PeriodInstanceKey))
            throw F(failureCode);
    }

    private static void RequireRowFilter(
        string surface,
        ApiFilter filter,
        JsonElement row,
        string failureCode)
    {
        static bool SameOptional(
            JsonElement item,
            string property,
            string? expected,
            string code)
            => expected is null || Eq(JsonString(item, property, code), expected);
        var valid = surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField =>
                SameOptional(row, "fieldId", filter.FieldId, failureCode) &&
                SameOptional(row, "fieldKey", filter.FieldKey, failureCode) &&
                SameOptional(row, "bucketKey", filter.BucketKey, failureCode) &&
                SameOptional(row, "periodKey", filter.PeriodKey, failureCode) &&
                (JsonBoolean(row, "showInTree", failureCode) ||
                 JsonBoolean(row, "showInDetail", failureCode)),
            StatisticReconciliationActualApiSurfaces.DirectTable =>
                SameOptional(row, "blockId", filter.BlockId, failureCode) &&
                SameOptional(row, "metricKey", filter.MetricKey, failureCode) &&
                SameOptional(row, "bucketKey", filter.BucketKey, failureCode) &&
                SameOptional(row, "periodKey", filter.PeriodKey, failureCode),
            StatisticReconciliationActualApiSurfaces.DirectLabel =>
                SameOptional(row, "blockId", filter.BlockId, failureCode) &&
                SameOptional(row, "labelCode", filter.LabelCode, failureCode) &&
                SameOptional(row, "periodKey", filter.PeriodKey, failureCode),
            _ => false
        };
        if (!valid)
            throw F(failureCode);
    }

    private static string RowIdentity(
        string surface,
        JsonElement row,
        string failureCode)
        => surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField => H(
                "P10_ACTUAL_API_DIRECT_FIELD_ROW_ID_V1",
                JsonString(row, "workId", failureCode),
                JsonString(row, "scopeType", failureCode),
                JsonString(row, "scopeId", failureCode),
                JsonOptionalString(row, "rootAssignmentId", failureCode),
                JsonOptionalString(row, "dynamicFormTemplateId", failureCode),
                JsonString(row, "fieldId", failureCode),
                JsonString(row, "fieldKey", failureCode),
                JsonString(row, "fieldType", failureCode),
                JsonOptionalString(row, "bucketKey", failureCode),
                JsonString(row, "periodKey", failureCode),
                JsonString(row, "periodInstanceKey", failureCode),
                I(JsonInt64(row, "reportStatus", failureCode))),
            StatisticReconciliationActualApiSurfaces.DirectTable => H(
                "P10_ACTUAL_API_DIRECT_TABLE_ROW_ID_V1",
                JsonString(row, "workId", failureCode),
                JsonString(row, "scopeType", failureCode),
                JsonString(row, "scopeId", failureCode),
                JsonOptionalString(row, "rootAssignmentId", failureCode),
                JsonOptionalString(row, "dynamicFormTemplateId", failureCode),
                JsonOptionalString(row, "dynamicExcelTemplateId", failureCode),
                JsonString(row, "blockId", failureCode),
                JsonString(row, "tableMode", failureCode),
                JsonString(row, "metricKey", failureCode),
                JsonString(row, "rowKey", failureCode),
                JsonString(row, "columnKey", failureCode),
                JsonString(row, "dataType", failureCode),
                JsonOptionalString(row, "bucketKey", failureCode),
                JsonString(row, "periodKey", failureCode),
                JsonString(row, "periodInstanceKey", failureCode),
                I(JsonInt64(row, "reportStatus", failureCode))),
            StatisticReconciliationActualApiSurfaces.DirectLabel => H(
                "P10_ACTUAL_API_DIRECT_LABEL_ROW_ID_V1",
                JsonString(row, "workId", failureCode),
                JsonString(row, "scopeType", failureCode),
                JsonString(row, "scopeId", failureCode),
                JsonOptionalString(row, "rootAssignmentId", failureCode),
                JsonOptionalString(row, "dynamicFormTemplateId", failureCode),
                JsonOptionalString(row, "dynamicExcelTemplateId", failureCode),
                JsonString(row, "blockId", failureCode),
                JsonString(row, "labelCode", failureCode),
                JsonString(row, "periodKey", failureCode),
                JsonString(row, "periodInstanceKey", failureCode),
                I(JsonInt64(row, "reportStatus", failureCode))),
            _ => throw F(failureCode)
        };

    private static ImmutableArray<SemanticTotal> TotalsForRows(
        string surface,
        ImmutableArray<SemanticRow> rows)
    {
        long SumLong(string property)
        {
            long value = 0;
            foreach (var row in rows)
                value = checked(value + JsonInt64(row.Root, property,
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .TotalsMismatch));
            return value;
        }
        decimal SumDecimal(string property)
        {
            decimal value = 0;
            foreach (var row in rows)
                value = checked(value + JsonDecimal(row.Root, property,
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .TotalsMismatch));
            return value;
        }
        var values = surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField or
            StatisticReconciliationActualApiSurfaces.DirectTable => new[]
            {
                Total("totalReportCount", "INTEGER", I(SumLong("reportCount"))),
                Total("totalRows", "INTEGER", I(rows.Length)),
                Total("totalSum", "DECIMAL",
                    StatisticReconciliationActualCanonical.Number(
                        SumDecimal("sum"))),
                Total("totalValueCount", "INTEGER", I(SumLong("valueCount")))
            },
            StatisticReconciliationActualApiSurfaces.DirectLabel => new[]
            {
                Total("totalReportCount", "INTEGER", I(SumLong("reportCount"))),
                Total("totalRowCount", "INTEGER", I(SumLong("rowCount"))),
                Total("totalRows", "INTEGER", I(rows.Length))
            },
            _ => throw F(
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .SurfaceUnsupported)
        };
        return values.OrderBy(value => value.Name, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<SemanticTotal> NormalizeTotals(
        ImmutableArray<StatisticReconciliationActualApiTotalValue> input,
        string failureCode)
    {
        if (input.IsDefault || input.Length is < 1 or > 32)
            throw F(failureCode);
        var result = input.Select(value =>
        {
            if (value is null)
                throw F(failureCode);
            var name = Required(value.Name);
            var type = ExactUpper(value.ValueType);
            string canonical;
            if (type == "INTEGER")
            {
                if (!long.TryParse(value.CanonicalValue, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var number) ||
                    number < 0)
                    throw F(failureCode);
                canonical = I(number);
            }
            else if (type == "DECIMAL")
            {
                if (!decimal.TryParse(value.CanonicalValue,
                        NumberStyles.AllowLeadingSign |
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var number))
                    throw F(failureCode);
                canonical = StatisticReconciliationActualCanonical.Number(number);
            }
            else
                throw F(failureCode);
            if (!Eq(canonical, value.CanonicalValue))
                throw F(failureCode);
            return Total(name, type, canonical);
        }).OrderBy(value => value.Name, StringComparer.Ordinal)
          .ToImmutableArray();
        if (result.Select(value => value.Name).Distinct(StringComparer.Ordinal)
                .Count() != result.Length)
            throw F(failureCode);
        return result;
    }

    private static SemanticTotal Total(
        string name,
        string valueType,
        string canonicalValue)
        => new(
            name,
            valueType,
            canonicalValue,
            H("P10_ACTUAL_DIRECT_CROSS_VIEW_TOTAL_V1",
                name, valueType, canonicalValue));

    private static string TotalsSha(
        string domain,
        IEnumerable<SemanticTotal> values)
        => HS(domain, values.Select(value => value.SemanticSha256));

    private static void RequireUniqueRows(
        ImmutableArray<SemanticRow> rows,
        string failureCode)
    {
        if (rows.Select(value => value.Identity).Distinct(StringComparer.Ordinal)
                .Count() != rows.Length)
            throw F(failureCode);
    }

    private static string JsonString(
        JsonElement root,
        string name,
        string failureCode)
        => JsonOptionalString(root, name, failureCode) ?? throw F(failureCode);

    private static string? JsonOptionalString(
        JsonElement root,
        string name,
        string failureCode)
    {
        if (!root.TryGetProperty(name, out var value))
            throw F(failureCode);
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw F(failureCode);
        var text = value.GetString();
        return text is null ? null : Required(text);
    }

    private static long JsonInt64(
        JsonElement root,
        string name,
        string failureCode)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var result) || result < 0)
            throw F(failureCode);
        return result;
    }

    private static decimal JsonDecimal(
        JsonElement root,
        string name,
        string failureCode)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out var result))
            throw F(failureCode);
        return result;
    }

    private static bool JsonBoolean(
        JsonElement root,
        string name,
        string failureCode)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw F(failureCode);
        return value.GetBoolean();
    }

    private sealed record SemanticRow(
        int Ordinal,
        string Identity,
        string CanonicalJson,
        JsonElement Root,
        string SemanticSha256);

    private sealed record SemanticTotal(
        string Name,
        string ValueType,
        string CanonicalValue,
        string SemanticSha256);

    private sealed record ValidatedBase(
        ImmutableArray<SemanticRow> Rows,
        ImmutableArray<SemanticTotal> Totals,
        string RowsSha256,
        string TotalsSha256,
        string ProjectionSha256);
}
