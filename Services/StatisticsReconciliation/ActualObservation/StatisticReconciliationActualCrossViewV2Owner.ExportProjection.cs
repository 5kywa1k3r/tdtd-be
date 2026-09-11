using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationActualCrossViewV2ExportProjection(
    StatisticReconciliationActualCrossViewExportBaseProjection Base,
    string ColumnManifestSha256);

/// <summary>
/// Replays the export table projection from an authoritative result JSON. It
/// does not inspect rendered CSV/XLSX bytes or copy their cells/column order.
/// </summary>
internal static class
    StatisticReconciliationActualCrossViewV2ExportBaseProjector
{
    internal static StatisticReconciliationActualCrossViewV2ExportProjection
        ProjectJson(
            string resultJson,
            StatisticReconciliationActualExportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var document = StatisticReconciliationActualJson.ParseStrict(
            resultJson,
            "CROSS_VIEW_OWNER_RESULT_JSON");
        var sourceRows = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
                .Select(value => value.Clone()).ToImmutableArray()
            : ImmutableArray.Create(document.RootElement.Clone());
        if (sourceRows.Length > StatisticReconciliationActualExportParser.MaxRows ||
            sourceRows.Any(value => value.ValueKind != JsonValueKind.Object))
        {
            throw Invalid("CROSS_VIEW_OWNER_RESULT_ROWS_INVALID");
        }

        var headers = new List<string> { "ordinal" };
        var seen = new HashSet<string>(headers, StringComparer.Ordinal);
        foreach (var row in sourceRows)
        {
            foreach (var property in row.EnumerateObject())
            {
                if (seen.Add(property.Name))
                    headers.Add(property.Name);
            }
        }
        if (headers.Count > StatisticReconciliationActualExportParser.MaxColumns)
            throw Invalid("CROSS_VIEW_OWNER_RESULT_COLUMNS_LIMIT");

        var columns = BuildColumns(manifest.ResultKind, headers, sourceRows);
        var rows = sourceRows.Select((row, index) =>
        {
            var cells = columns.Select(column => BuildCell(
                    column, row, index))
                .ToImmutableArray();
            var rowSha = StatisticReconciliationActualCanonical.HashSequence(
                "P10_ACTUAL_EXPORT_ROW_V1",
                cells.Select(cell => cell.CellSemanticSha256));
            return new StatisticReconciliationActualCrossViewExportBaseRow(
                index + 1,
                StatisticReconciliationActualJson.Canonicalize(row),
                cells,
                rowSha);
        }).ToImmutableArray();
        var totals = BuildTotals(columns, rows);
        var sidecar = StatRunExportColumnManifestContract.Create(
            columns.Select(column => new StatRunExportColumnManifestEntry(
                column.Ordinal,
                column.Name,
                column.ValueType,
                column.BlankPolicy,
                column.IsFullFilterTotal)).ToArray());
        var canonicalFilter = Required(
            manifest.CanonicalFilterJson,
            "CROSS_VIEW_OWNER_EXPORT_FILTER_JSON");
        var period = Required(
            manifest.PeriodInstanceKey,
            "CROSS_VIEW_OWNER_EXPORT_PERIOD_INSTANCE_KEY");
        var projection = new
            StatisticReconciliationActualCrossViewExportBaseProjection(
                StatisticReconciliationActualCrossViewParityV2Schemas.Base,
                manifest.WorkId,
                manifest.ScopeType,
                manifest.ScopeId,
                // Export manifests intentionally do not duplicate the form
                // template target. The trusted material supplies this value
                // after the caller checks its exact result binding.
                string.Empty,
                manifest.AuthorizationSnapshotSha256,
                manifest.ResultKind,
                manifest.ResultId,
                manifest.ResultSha256,
                manifest.ConfigSha256,
                manifest.SourceSha256,
                manifest.LifecycleRevision,
                period,
                canonicalFilter,
                manifest.FilterSha256,
                columns,
                rows,
                totals);
        return new(projection, sidecar.Sha256);
    }

    internal static StatisticReconciliationActualCrossViewExportBaseProjection
        BindTemplate(
            StatisticReconciliationActualCrossViewExportBaseProjection value,
            string dynamicFormTemplateId)
        => value with
        {
            DynamicFormTemplateId = Required(
                dynamicFormTemplateId,
                "CROSS_VIEW_OWNER_DYNAMIC_FORM_TEMPLATE_ID")
        };

    private static ImmutableArray<
        StatisticReconciliationActualExportColumnContract> BuildColumns(
            string resultKind,
            IReadOnlyList<string> headers,
            ImmutableArray<JsonElement> rows)
    {
        var result = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualExportColumnContract>(headers.Count);
        result.Add(new StatisticReconciliationActualExportColumnContract(
            0,
            "ordinal",
            StatisticReconciliationActualExportValueTypes.Integer,
            StatisticReconciliationActualExportBlankPolicies.Forbidden,
            false));
        for (var ordinal = 1; ordinal < headers.Count; ordinal++)
        {
            var name = headers[ordinal];
            string? valueType = null;
            var declaredValueType =
                StatRunExportColumnManifestContract.DeclaredValueType(
                    resultKind,
                    name);
            var declaredSchema = string.Equals(
                resultKind,
                StatRunExportResultKinds.DirectField,
                StringComparison.Ordinal);
            if (declaredSchema && declaredValueType is null)
                throw Invalid("CROSS_VIEW_OWNER_COLUMN_SCHEMA_INVALID");
            var sawNull = false;
            var sawEmpty = false;
            foreach (var row in rows)
            {
                if (!row.TryGetProperty(name, out var value) ||
                    value.ValueKind == JsonValueKind.Null)
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
                var current = ValueType(name, value);
                if (declaredValueType is not null && !string.Equals(
                        declaredValueType,
                        current,
                        StringComparison.Ordinal))
                {
                    throw Invalid("CROSS_VIEW_OWNER_COLUMN_TYPE_MIXED");
                }
                if (valueType is not null &&
                    !string.Equals(valueType, current, StringComparison.Ordinal))
                {
                    throw Invalid("CROSS_VIEW_OWNER_COLUMN_TYPE_MIXED");
                }
                valueType = current;
            }
            valueType ??= declaredValueType;
            if (valueType is null || sawNull && sawEmpty)
                throw Invalid("CROSS_VIEW_OWNER_COLUMN_SHAPE_INVALID");
            result.Add(new StatisticReconciliationActualExportColumnContract(
                ordinal,
                name,
                valueType,
                sawNull
                    ? StatisticReconciliationActualExportBlankPolicies.Null
                    : sawEmpty
                        ? StatisticReconciliationActualExportBlankPolicies.Empty
                        : StatisticReconciliationActualExportBlankPolicies.Forbidden,
                IsFrozenTotal(name)));
        }
        return result.MoveToImmutable();
    }

    private static string ValueType(string header, JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out _) =>
                StatisticReconciliationActualExportValueTypes.Decimal,
            JsonValueKind.True or JsonValueKind.False =>
                StatisticReconciliationActualExportValueTypes.Boolean,
            JsonValueKind.String when IsInstant(header, value.GetString()) =>
                StatisticReconciliationActualExportValueTypes.UtcInstant,
            JsonValueKind.String =>
                StatisticReconciliationActualExportValueTypes.Text,
            JsonValueKind.Array or JsonValueKind.Object =>
                StatisticReconciliationActualExportValueTypes.Json,
            _ => throw Invalid("CROSS_VIEW_OWNER_COLUMN_VALUE_INVALID")
        };

    private static StatisticReconciliationActualExportCellObservation BuildCell(
        StatisticReconciliationActualExportColumnContract column,
        JsonElement row,
        int rowIndex)
    {
        if (column.Ordinal == 0)
            return Cell(column, "VALUE", Number(rowIndex + 1), 0, false);
        if (!row.TryGetProperty(column.Name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return Cell(column, "NULL", string.Empty, 0, false);
        if (value.ValueKind == JsonValueKind.String &&
            value.GetString()?.Length == 0)
            return Cell(column, "EMPTY", string.Empty, 0, false);
        return column.ValueType switch
        {
            StatisticReconciliationActualExportValueTypes.Decimal
                when value.TryGetDecimal(out var number) =>
                DecimalCell(column, number),
            StatisticReconciliationActualExportValueTypes.Boolean
                when value.ValueKind is JsonValueKind.True or
                    JsonValueKind.False =>
                Cell(column, "VALUE", value.GetBoolean() ? "true" : "false",
                    0, false),
            StatisticReconciliationActualExportValueTypes.UtcInstant
                when DateTime.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal |
                    DateTimeStyles.AdjustToUniversal,
                    out var date) =>
                Cell(column, "VALUE",
                    DateTime.SpecifyKind(date, DateTimeKind.Utc).ToString(
                        "O", CultureInfo.InvariantCulture), 0, false),
            StatisticReconciliationActualExportValueTypes.Text
                when value.ValueKind == JsonValueKind.String =>
                TextCell(column, value.GetString()!),
            StatisticReconciliationActualExportValueTypes.Json
                when value.ValueKind is JsonValueKind.Array or
                    JsonValueKind.Object =>
                Cell(column, "VALUE",
                    StatisticReconciliationActualJson.Canonicalize(value),
                    0, false),
            _ => throw Invalid("CROSS_VIEW_OWNER_CELL_SHAPE_INVALID")
        };
    }

    private static StatisticReconciliationActualExportCellObservation
        DecimalCell(
            StatisticReconciliationActualExportColumnContract column,
            decimal value)
    {
        var canonical = value.ToString(CultureInfo.InvariantCulture);
        var dot = canonical.IndexOf('.');
        return Cell(
            column,
            "VALUE",
            canonical,
            dot < 0 ? 0 : canonical.Length - dot - 1,
            false);
    }

    private static StatisticReconciliationActualExportCellObservation TextCell(
        StatisticReconciliationActualExportColumnContract column,
        string value)
    {
        var neutralized = value.Length > 0 && value[0] is '=' or '+' or '-' or '@';
        return Cell(
            column,
            "VALUE",
            neutralized ? $"'{value}" : value,
            0,
            neutralized);
    }

    private static StatisticReconciliationActualExportCellObservation Cell(
        StatisticReconciliationActualExportColumnContract column,
        string state,
        string canonical,
        int scale,
        bool neutralized)
    {
        var sha = StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_EXPORT_CELL_V1",
            Number(column.Ordinal),
            column.Name,
            column.ValueType,
            state,
            canonical,
            Number(scale),
            neutralized ? "true" : "false");
        return new(
            column.Ordinal,
            column.Name,
            column.ValueType,
            state,
            canonical,
            scale,
            neutralized,
            sha);
    }

    private static ImmutableArray<
        StatisticReconciliationActualExportTotalObservation> BuildTotals(
            ImmutableArray<StatisticReconciliationActualExportColumnContract>
                columns,
            ImmutableArray<
                StatisticReconciliationActualCrossViewExportBaseRow> rows)
    {
        var totals = new List<
            StatisticReconciliationActualExportTotalObservation>
        {
            Total("columnCount", "INTEGER", "VALUE", Number(columns.Length), 0),
            Total("rowCount", "INTEGER", "VALUE", Number(rows.Length), 0)
        };
        foreach (var column in columns.Where(value => value.IsFullFilterTotal))
        {
            if (rows.Length == 0)
                throw Invalid("CROSS_VIEW_OWNER_TOTAL_COLUMN_EMPTY");
            var first = rows[0].Cells[column.Ordinal];
            if (first.ValueState != "VALUE" || rows.Any(row =>
                    !SameValue(first, row.Cells[column.Ordinal])))
                throw Invalid("CROSS_VIEW_OWNER_TOTAL_COLUMN_UNSTABLE");
            totals.Add(Total(
                column.Name,
                first.ValueType,
                first.ValueState,
                first.CanonicalValue,
                first.DecimalScale));
        }
        return totals.OrderBy(value => value.Name, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static StatisticReconciliationActualExportTotalObservation Total(
        string name,
        string type,
        string state,
        string canonical,
        int scale)
        => new(
            name,
            type,
            state,
            canonical,
            scale,
            StatisticReconciliationActualCanonical.Hash(
                "P10_ACTUAL_EXPORT_TOTAL_V1",
                name,
                type,
                state,
                canonical,
                Number(scale)));

    private static bool SameValue(
        StatisticReconciliationActualExportCellObservation left,
        StatisticReconciliationActualExportCellObservation right)
        => left.ValueType == right.ValueType &&
           left.ValueState == right.ValueState &&
           left.CanonicalValue == right.CanonicalValue &&
           left.DecimalScale == right.DecimalScale;

    private static bool IsInstant(string header, string? value)
        => value is not null &&
           (header.EndsWith("Utc", StringComparison.Ordinal) ||
            header.EndsWith("Date", StringComparison.Ordinal)) &&
           DateTime.TryParse(
               value,
               CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal |
               DateTimeStyles.AdjustToUniversal,
               out _);

    private static bool IsFrozenTotal(string name)
        => name.Length > 5 &&
           name.StartsWith("total", StringComparison.Ordinal) &&
           name[5] is >= 'A' and <= 'Z';

    private static string Required(string? value, string name)
        => StatisticReconciliationActualCanonical.Required(value, name);

    private static string Number(long value)
        => StatisticReconciliationActualCanonical.Integer(value);

    private static StatisticReconciliationActualObservationException Invalid(
        string reason)
        => new(reason);
}
