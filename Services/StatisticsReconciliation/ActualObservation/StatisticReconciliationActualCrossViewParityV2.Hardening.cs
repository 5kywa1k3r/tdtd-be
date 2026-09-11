using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualCrossViewParityV2
{
    private static readonly ImmutableArray<string> ExportFilterProperties =
    [
        "blockId", "bucketKey", "dynamicFormTemplateId", "fieldId",
        "fieldKey", "labelCode", "metricKey", "periodKey"
    ];
    private static readonly ImmutableArray<string> DirectFieldProperties =
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
    private static readonly ImmutableArray<string> DirectTableProperties =
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
    private static readonly ImmutableArray<string> DirectLabelProperties =
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
    private static readonly ImmutableArray<string> BasicSourceProperties =
    [
        "workAssignmentId", "workAssignmentReportId", "workReportPeriodId",
        "assigneeUserId", "assigneeUsername", "assigneeFullName", "unitId",
        "unitSymbol", "unitShortName", "unitName", "periodKey",
        "periodInstanceKey", "periodKind", "reportStatus", "submittedAtUtc",
        "approvedAtUtc", "payloadUpdatedAtUtc", "payloadRevision",
        "payloadHash"
    ];
    private static readonly ImmutableArray<string> DiffProperties =
    [
        "rowId", "ordinal", "key", "conceptKind", "conceptKey", "left",
        "right", "equal", "differenceKind", "numericDelta"
    ];
    private static readonly ImmutableArray<string> DiffValueProperties =
    [
        "state", "dataType", "canonicalValue", "numericValue",
        "booleanValue", "dateValueUtc", "choiceIds"
    ];

    private static void RequirePlanFilterSemantics(NormalizedPlan plan)
    {
        using var exportDocument = StatisticReconciliationActualJson.ParseStrict(
            plan.CanonicalExportFilterJson, "CROSS_VIEW_EXPORT_FILTER");
        RequireExactProperties(exportDocument.RootElement,
            ExportFilterProperties,
            StatisticReconciliationActualCrossViewParityV2Failures.PlanInvalid);
        var export = exportDocument.RootElement;
        var template = OptionalString(export, "dynamicFormTemplateId",
            StatisticReconciliationActualCrossViewParityV2Failures.PlanInvalid);
        if ((plan.Family == StatisticReconciliationActualCrossViewFamilies.Direct && !Eq(template, plan.DynamicFormTemplateId)) ||
            (plan.Family != StatisticReconciliationActualCrossViewFamilies.Direct && template is not null && !Eq(template, plan.DynamicFormTemplateId)))
            throw Fail(StatisticReconciliationActualCrossViewParityV2Failures
                .FilterMismatch);

        if (!plan.ApiRequired)
        {
            if (ExportFilterProperties
                .Where(name => name != "dynamicFormTemplateId")
                .Any(name => export.GetProperty(name).ValueKind !=
                             JsonValueKind.Null))
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .FilterMismatch);
            return;
        }
        using var apiDocument = StatisticReconciliationActualJson.ParseStrict(
            plan.CanonicalApiFilterJson!, "CROSS_VIEW_API_FILTER");
        var api = apiDocument.RootElement;
        if (plan.Family == StatisticReconciliationActualCrossViewFamilies.Diff)
        {
            if (api.EnumerateObject().Any() ||
                ExportFilterProperties
                    .Where(name => name != "dynamicFormTemplateId")
                    .Any(name => export.GetProperty(name).ValueKind !=
                                 JsonValueKind.Null))
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .FilterMismatch);
            return;
        }
        if (plan.Family is StatisticReconciliationActualCrossViewFamilies.Basic
            or StatisticReconciliationActualCrossViewFamilies.Flow)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal)
                { "q", "periodKey", "unitId", "assigneeUserId" };
            if (api.EnumerateObject().Any(property =>
                    !allowed.Contains(property.Name)) ||
                ExportFilterProperties
                    .Where(name => name != "dynamicFormTemplateId")
                    .Any(name => export.GetProperty(name).ValueKind !=
                                 JsonValueKind.Null))
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .FilterMismatch);
            return;
        }

        var allowedDirect = plan.ApiSurface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField =>
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "periodInstanceKey", "fieldId", "fieldKey",
                    "statisticLabelCode", "fieldType", "bucketKey",
                    "showInTree", "showInDetail", "periodKey",
                    "periodKeyFrom", "periodKeyTo", "reportStatus"
                },
            StatisticReconciliationActualApiSurfaces.DirectTable =>
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "periodInstanceKey", "dynamicExcelTemplateId", "blockId",
                    "tableMode", "metricKey", "metricLabelCode", "dataType",
                    "bucketKey", "periodKey", "reportStatus"
                },
            _ => new HashSet<string>(StringComparer.Ordinal)
                {
                    "periodInstanceKey", "dynamicExcelTemplateId", "labelCode",
                    "periodKey", "reportStatus"
                }
        };
        if (api.EnumerateObject().Any(property =>
                !allowedDirect.Contains(property.Name)) ||
            !Eq(OptionalString(api, "periodInstanceKey",
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .FilterMismatch), plan.PeriodInstanceKey))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .FilterMismatch);
        static string? A(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) &&
            value.ValueKind != JsonValueKind.Null
                ? value.GetString()
                : null;
        if (!Eq(A(api, "fieldId"), A(export, "fieldId")) ||
            !Eq(A(api, "fieldKey"), A(export, "fieldKey")) ||
            !Eq(A(api, "blockId"), A(export, "blockId")) ||
            !Eq(A(api, "metricKey"), A(export, "metricKey")) ||
            !Eq(A(api, "labelCode")?.ToLowerInvariant(),
                A(export, "labelCode")) ||
            !Eq(A(api, "periodKey"), A(export, "periodKey")) ||
            !Eq(A(api, "bucketKey"), A(export, "bucketKey")))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .FilterMismatch);
    }

    private static void RequireApiSemantics(
        NormalizedPlan plan,
        ImmutableArray<ValidatedApiRow> rows,
        ImmutableArray<ValidatedTotal> totals,
        string failure)
    {
        foreach (var row in rows)
        {
            var properties = plan.ApiSurface switch
            {
                StatisticReconciliationActualApiSurfaces.DirectField =>
                    DirectFieldProperties,
                StatisticReconciliationActualApiSurfaces.DirectTable =>
                    DirectTableProperties,
                StatisticReconciliationActualApiSurfaces.DirectLabel =>
                    DirectLabelProperties,
                StatisticReconciliationActualApiSurfaces.BasicSource =>
                    BasicSourceProperties,
                StatisticReconciliationActualApiSurfaces.P9Diff =>
                    DiffProperties,
                _ => throw Fail(failure)
            };
            RequireExactProperties(row.Root, properties, failure);
            var identity = plan.ApiSurface switch
            {
                StatisticReconciliationActualApiSurfaces.DirectField => H(
                    "P10_ACTUAL_API_DIRECT_FIELD_ROW_ID_V1",
                    S(row.Root, "workId", failure),
                    S(row.Root, "scopeType", failure),
                    S(row.Root, "scopeId", failure),
                    O(row.Root, "rootAssignmentId", failure),
                    O(row.Root, "dynamicFormTemplateId", failure),
                    S(row.Root, "fieldId", failure),
                    S(row.Root, "fieldKey", failure),
                    S(row.Root, "fieldType", failure),
                    O(row.Root, "bucketKey", failure),
                    S(row.Root, "periodKey", failure),
                    S(row.Root, "periodInstanceKey", failure),
                    I(N(row.Root, "reportStatus", failure))),
                StatisticReconciliationActualApiSurfaces.DirectTable => H(
                    "P10_ACTUAL_API_DIRECT_TABLE_ROW_ID_V1",
                    S(row.Root, "workId", failure),
                    S(row.Root, "scopeType", failure),
                    S(row.Root, "scopeId", failure),
                    O(row.Root, "rootAssignmentId", failure),
                    O(row.Root, "dynamicFormTemplateId", failure),
                    O(row.Root, "dynamicExcelTemplateId", failure),
                    S(row.Root, "blockId", failure),
                    S(row.Root, "tableMode", failure),
                    S(row.Root, "metricKey", failure),
                    S(row.Root, "rowKey", failure),
                    S(row.Root, "columnKey", failure),
                    S(row.Root, "dataType", failure),
                    O(row.Root, "bucketKey", failure),
                    S(row.Root, "periodKey", failure),
                    S(row.Root, "periodInstanceKey", failure),
                    I(N(row.Root, "reportStatus", failure))),
                StatisticReconciliationActualApiSurfaces.DirectLabel => H(
                    "P10_ACTUAL_API_DIRECT_LABEL_ROW_ID_V1",
                    S(row.Root, "workId", failure),
                    S(row.Root, "scopeType", failure),
                    S(row.Root, "scopeId", failure),
                    O(row.Root, "rootAssignmentId", failure),
                    O(row.Root, "dynamicFormTemplateId", failure),
                    O(row.Root, "dynamicExcelTemplateId", failure),
                    S(row.Root, "blockId", failure),
                    S(row.Root, "labelCode", failure),
                    S(row.Root, "periodKey", failure),
                    S(row.Root, "periodInstanceKey", failure),
                    I(N(row.Root, "reportStatus", failure))),
                StatisticReconciliationActualApiSurfaces.BasicSource =>
                    S(row.Root, "workAssignmentReportId", failure),
                _ => S(row.Root, "rowId", failure)
            };
            if (!Eq(row.Identity, identity))
                throw Fail(failure);
            if (plan.ApiSurface?.StartsWith("DIRECT_",
                    StringComparison.Ordinal) == true &&
                (!Eq(S(row.Root, "workId", failure), plan.WorkId) ||
                 !Eq(S(row.Root, "scopeType", failure), "ASSIGNMENT") ||
                 !Eq(S(row.Root, "scopeId", failure), plan.ScopeId) ||
                 !Eq(O(row.Root, "dynamicFormTemplateId", failure),
                     plan.DynamicFormTemplateId) ||
                 !Eq(S(row.Root, "periodInstanceKey", failure),
                     plan.PeriodInstanceKey)))
                throw Fail(failure);
            if (plan.ApiSurface ==
                    StatisticReconciliationActualApiSurfaces.BasicSource &&
                !Eq(S(row.Root, "periodInstanceKey", failure),
                    plan.PeriodInstanceKey))
                throw Fail(failure);
            if (plan.ApiSurface ==
                StatisticReconciliationActualApiSurfaces.P9Diff)
            {
                if (N(row.Root, "ordinal", failure) != row.Ordinal)
                    throw Fail(failure);
                RequireExactProperties(row.Root.GetProperty("left"),
                    DiffValueProperties, failure);
                RequireExactProperties(row.Root.GetProperty("right"),
                    DiffValueProperties, failure);
            }
        }
        var expectedTotals = plan.ApiSurface switch
        {
            StatisticReconciliationActualApiSurfaces.BasicSource =>
                new[] { Total("totalRows", "INTEGER", I(rows.Length)) },
            StatisticReconciliationActualApiSurfaces.P9Diff => new[]
            {
                Total("totalChangedRowCount", "INTEGER", I(rows.Count(row =>
                    !row.Root.GetProperty("equal").GetBoolean()))),
                Total("totalEqualRowCount", "INTEGER", I(rows.Count(row =>
                    row.Root.GetProperty("equal").GetBoolean()))),
                Total("totalRowCount", "INTEGER", I(rows.Length)),
                Total("totalRows", "INTEGER", I(rows.Length))
            },
            StatisticReconciliationActualApiSurfaces.DirectLabel => new[]
            {
                Total("totalReportCount", "INTEGER", I(Sum(rows, "reportCount"))),
                Total("totalRowCount", "INTEGER", I(Sum(rows, "rowCount"))),
                Total("totalRows", "INTEGER", I(rows.Length))
            },
            _ => new[]
            {
                Total("totalReportCount", "INTEGER", I(Sum(rows, "reportCount"))),
                Total("totalRows", "INTEGER", I(rows.Length)),
                Total("totalSum", "DECIMAL",
                    StatisticReconciliationActualCanonical.Number(
                        SumDecimal(rows, "sum"))),
                Total("totalValueCount", "INTEGER", I(Sum(rows, "valueCount")))
            }
        };
        var ordered = expectedTotals.OrderBy(value => value.Name,
            StringComparer.Ordinal).ToImmutableArray();
        if (!totals.SequenceEqual(ordered))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .TotalsMismatch);
    }

    private static void RequireExportBaseSemantics(
        NormalizedPlan plan,
        ImmutableArray<StatisticReconciliationActualExportColumnContract> columns,
        ImmutableArray<ValidatedExportRow> rows,
        ImmutableArray<ValidatedTotal> totals,
        string failure)
    {
        var sourceRows = rows.Select(row =>
        {
            if (row.CanonicalSourceRowJson is null)
                throw Fail(failure);
            using var document = StatisticReconciliationActualJson.ParseStrict(
                row.CanonicalSourceRowJson, "CROSS_VIEW_EXPORT_BASE_ROW");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Fail(failure);
            return document.RootElement.Clone();
        }).ToImmutableArray();
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in sourceRows.Where(row =>
                     row.ValueKind == JsonValueKind.Object))
            foreach (var property in row.EnumerateObject()) union.Add(property.Name);
        if (!columns.Skip(1).Select(column => column.Name).ToHashSet(
                StringComparer.Ordinal).SetEquals(union))
            throw Fail(failure);

        for (var columnIndex = 1; columnIndex < columns.Length; columnIndex++)
        {
            var column = columns[columnIndex];
            string? inferredType = null;
            var declaredType =
                StatRunExportColumnManifestContract.DeclaredValueType(
                    plan.ExportResultKind,
                    column.Name);
            var declaredSchema = Eq(
                plan.ExportResultKind,
                StatRunExportResultKinds.DirectField);
            if (declaredSchema && declaredType is null)
                throw Fail(failure);
            var sawNull = false;
            var sawEmpty = false;
            foreach (var row in sourceRows)
            {
                var value = row.ValueKind == JsonValueKind.Object &&
                            row.TryGetProperty(column.Name, out var property)
                    ? property
                    : default;
                if (value.ValueKind is JsonValueKind.Undefined or
                    JsonValueKind.Null)
                {
                    sawNull = true;
                    continue;
                }
                if (value.ValueKind == JsonValueKind.String &&
                    value.GetString()?.Length == 0)
                {
                    sawEmpty = true;
                    continue;
                }
                var type = ExportType(column.Name, value, failure);
                if (declaredType is not null && !Eq(declaredType, type))
                    throw Fail(failure);
                if (inferredType is not null && !Eq(inferredType, type))
                    throw Fail(failure);
                inferredType = type;
            }
            inferredType ??= declaredType;
            if (inferredType is null || sawNull && sawEmpty ||
                !Eq(column.ValueType, inferredType) ||
                !Eq(column.BlankPolicy, sawNull
                    ? StatisticReconciliationActualExportBlankPolicies.Null
                    : sawEmpty
                        ? StatisticReconciliationActualExportBlankPolicies.Empty
                        : StatisticReconciliationActualExportBlankPolicies.Forbidden) ||
                column.IsFullFilterTotal != IsTotalColumn(column.Name))
                throw Fail(failure);
        }
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < columns.Length;
                 columnIndex++)
            {
                var expected = ExpectedCell(
                    columns[columnIndex], sourceRows[rowIndex], rowIndex,
                    failure);
                if (!expected.Equals(rows[rowIndex].Cells[columnIndex]))
                    throw Fail(
                        StatisticReconciliationActualCrossViewParityV2Failures
                            .RowCellMismatch);
            }
        }
        RequireExportTotals(columns, rows, totals, failure);

        var sidecar = StatRunExportColumnManifestContract.Create(
            columns.Select(column => new StatRunExportColumnManifestEntry(
                column.Ordinal, column.Name, column.ValueType,
                column.BlankPolicy, column.IsFullFilterTotal)).ToArray());
        if (!Eq(sidecar.Sha256, plan.ExportColumnManifestSha256))
            throw Fail(failure);
    }

    private static void RequireExportCaptureTotals(
        ImmutableArray<StatisticReconciliationActualExportColumnContract> columns,
        ImmutableArray<ValidatedExportRow> rows,
        ImmutableArray<ValidatedTotal> totals,
        string failure)
        => RequireExportTotals(columns, rows, totals, failure);

    private static void RequireExportTotals(
        ImmutableArray<StatisticReconciliationActualExportColumnContract> columns,
        ImmutableArray<ValidatedExportRow> rows,
        ImmutableArray<ValidatedTotal> totals,
        string failure)
    {
        var expected = new List<ValidatedTotal>
        {
            ExpectedExportTotal("columnCount", "INTEGER", I(columns.Length)),
            ExpectedExportTotal("rowCount", "INTEGER", I(rows.Length))
        };
        foreach (var column in columns.Where(column => column.IsFullFilterTotal))
        {
            if (rows.Length == 0)
                throw Fail(failure);
            var value = rows[0].Cells[column.Ordinal];
            if (value.ValueState != "VALUE" || rows.Any(row =>
                    !SameValue(value, row.Cells[column.Ordinal])))
                throw Fail(failure);
            expected.Add(new ValidatedTotal(
                column.Name, value.ValueType, value.ValueState,
                value.CanonicalValue, value.DecimalScale,
                H("P10_ACTUAL_EXPORT_TOTAL_V1", column.Name,
                    value.ValueType, value.ValueState, value.CanonicalValue,
                    I(value.DecimalScale))));
        }
        if (!totals.SequenceEqual(expected.OrderBy(value => value.Name,
                StringComparer.Ordinal)))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .TotalsMismatch);
    }

    private static void RequireOwnerSemantic(
        NormalizedPlan plan,
        StatisticReconciliationActualExportManifest manifest,
        string failure)
    {
        var sidecar = StatRunExportColumnManifestContract.Create(
            manifest.Columns.Select(column => new StatRunExportColumnManifestEntry(
                column.Ordinal, column.Name, column.ValueType,
                column.BlankPolicy, column.IsFullFilterTotal)).ToArray());
        if (!Eq(sidecar.Sha256, plan.ExportColumnManifestSha256))
            throw Fail(failure);
        var owner = StatRunExportColumnManifestContract
            .ComputeOwnerSemanticSha256(
                manifest.ResultKind, manifest.WorkId, manifest.ScopeType,
                manifest.ScopeId, manifest.ResultId, manifest.ResultSha256,
                manifest.ConfigSha256, manifest.SourceSha256,
                manifest.FilterSha256, manifest.LifecycleRevision,
                manifest.RowCount, manifest.ColumnCount, sidecar.Sha256);
        if (!Eq(owner, manifest.OwnerSemanticSha256) ||
            !Eq(owner, plan.ExportOwnerSemanticSha256))
            throw Fail(failure);
    }

    private static void RequireExactProperties(
        JsonElement value,
        ImmutableArray<string> expected,
        string failure)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(expected.OrderBy(name => name,
                    StringComparer.Ordinal), StringComparer.Ordinal))
            throw Fail(failure);
    }

    private static string? OptionalString(
        JsonElement value, string name, string failure)
    {
        if (!value.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw Fail(failure);
        return Required(property.GetString());
    }
    private static string S(JsonElement value, string name, string failure)
        => O(value, name, failure) ?? throw Fail(failure);
    private static string? O(JsonElement value, string name, string failure)
        => OptionalString(value, name, failure);
    private static long N(JsonElement value, string name, string failure)
    {
        if (!value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var result) || result < 0)
            throw Fail(failure);
        return result;
    }
    private static long Sum(
        ImmutableArray<ValidatedApiRow> rows, string property)
    {
        long total = 0;
        foreach (var row in rows)
            total = checked(total + N(row.Root, property,
                StatisticReconciliationActualCrossViewParityV2Failures
                    .TotalsMismatch));
        return total;
    }
    private static decimal SumDecimal(
        ImmutableArray<ValidatedApiRow> rows, string property)
    {
        decimal total = 0;
        foreach (var row in rows)
        {
            var value = row.Root.GetProperty(property);
            if (value.ValueKind == JsonValueKind.Null)
                continue;
            if (value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDecimal(out var number))
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .TotalsMismatch);
            total = checked(total + number);
        }
        return total;
    }
    private static ValidatedTotal Total(
        string name, string type, string canonical)
        => new(name, type, "VALUE", canonical, 0,
            H("P10_ACTUAL_API_TOTAL_V1", name, type, canonical));

    private static string ExportType(
        string header, JsonElement value, string failure)
        => value.ValueKind switch
        {
            JsonValueKind.Number => "DECIMAL",
            JsonValueKind.True or JsonValueKind.False => "BOOLEAN",
            JsonValueKind.String when IsInstant(header, value.GetString()) =>
                "UTC_INSTANT",
            JsonValueKind.String => "TEXT",
            JsonValueKind.Array or JsonValueKind.Object => "JSON",
            _ => throw Fail(failure)
        };
    private static bool IsInstant(string header, string? value)
        => value is not null &&
           (header.EndsWith("Utc", StringComparison.Ordinal) ||
            header.EndsWith("Date", StringComparison.Ordinal)) &&
           DateTime.TryParse(value, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
               out _);
    private static bool IsTotalColumn(string name)
        => name.Length > 5 && name.StartsWith("total",
               StringComparison.Ordinal) && name[5] is >= 'A' and <= 'Z';

    private static ValidatedCell ExpectedCell(
        StatisticReconciliationActualExportColumnContract column,
        JsonElement source,
        int rowIndex,
        string failure)
    {
        if (column.Ordinal == 0)
            return Cell(column, "VALUE", I(rowIndex + 1), 0, false);
        if (source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty(column.Name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return Cell(column, "NULL", string.Empty, 0, false);
        if (value.ValueKind == JsonValueKind.String &&
            value.GetString()?.Length == 0)
            return Cell(column, "EMPTY", string.Empty, 0, false);
        return column.ValueType switch
        {
            "DECIMAL" when value.TryGetDecimal(out var number) =>
                DecimalCell(column, number),
            "BOOLEAN" when value.ValueKind is JsonValueKind.True or
                JsonValueKind.False => Cell(column, "VALUE",
                    value.GetBoolean() ? "true" : "false", 0, false),
            "UTC_INSTANT" when DateTime.TryParse(value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var date) => Cell(column, "VALUE",
                    DateTime.SpecifyKind(date, DateTimeKind.Utc).ToString(
                        "O", CultureInfo.InvariantCulture), 0, false),
            "TEXT" when value.ValueKind == JsonValueKind.String =>
                TextCell(column, value.GetString()!),
            "JSON" when value.ValueKind is JsonValueKind.Array or
                JsonValueKind.Object => Cell(column, "VALUE",
                    StatisticReconciliationActualJson.Canonicalize(value),
                    0, false),
            _ => throw Fail(failure)
        };
    }
    private static ValidatedCell DecimalCell(
        StatisticReconciliationActualExportColumnContract column,
        decimal value)
    {
        var canonical = value.ToString(CultureInfo.InvariantCulture);
        var dot = canonical.IndexOf('.');
        return Cell(column, "VALUE", canonical,
            dot < 0 ? 0 : canonical.Length - dot - 1, false);
    }
    private static ValidatedCell TextCell(
        StatisticReconciliationActualExportColumnContract column,
        string value)
    {
        var neutral = value.Length > 0 && value[0] is '=' or '+' or '-' or '@';
        return Cell(column, "VALUE", neutral ? $"'{value}" : value, 0, neutral);
    }
    private static ValidatedCell Cell(
        StatisticReconciliationActualExportColumnContract column,
        string state, string canonical, int scale, bool neutral)
        => new(column.Ordinal, column.Name, column.ValueType, state, canonical,
            scale, neutral, H("P10_ACTUAL_EXPORT_CELL_V1", I(column.Ordinal),
                column.Name, column.ValueType, state, canonical, I(scale),
                neutral ? "true" : "false"));
    private static bool SameValue(ValidatedCell left, ValidatedCell right)
        => Eq(left.ValueType, right.ValueType) &&
           Eq(left.ValueState, right.ValueState) &&
           Eq(left.CanonicalValue, right.CanonicalValue) &&
           left.DecimalScale == right.DecimalScale;
}
