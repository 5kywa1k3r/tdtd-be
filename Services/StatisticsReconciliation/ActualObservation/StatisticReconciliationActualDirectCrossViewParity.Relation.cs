using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualDirectCrossViewParity
{
    private static StatisticReconciliationActualDirectCrossViewParityProof Relate(
        NormalizedPlan plan,
        StatisticReconciliationActualDirectCrossViewParityActual actual,
        ValidatedFilters filters,
        ValidatedBase baseline,
        ValidatedApi api,
        ValidatedExport export)
    {
        if (baseline.Rows.Length != api.Rows.Length ||
            baseline.Rows.Length != export.Rows.Length)
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .RowCountMismatch);
        }
        if (!baseline.Totals.SequenceEqual(api.Totals))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .TotalsMismatch);

        var expectedHeaders = ImmutableArray.Create("ordinal")
            .AddRange(PropertiesFor(plan.Surface));
        if (!export.Columns.Select(column => column.Name).SequenceEqual(
                expectedHeaders, StringComparer.Ordinal) ||
            export.Columns.Any(column => column.IsFullFilterTotal))
        {
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ExportCaptureInvalid);
        }
        var expectedColumns = DeriveColumns(
            plan.ResultKind,
            baseline.Rows,
            expectedHeaders);
        if (!expectedColumns.SequenceEqual(export.Columns))
            throw F(StatisticReconciliationActualDirectCrossViewParityFailures
                .ExportCaptureInvalid);

        var relations = ImmutableArray.CreateBuilder<string>(baseline.Rows.Length);
        long relationCount = 0;
        for (var index = 0; index < baseline.Rows.Length; index++)
        {
            var expected = baseline.Rows[index];
            var observedApi = api.Rows[index];
            var observedExport = export.Rows[index];
            if (expected.Ordinal != index || observedApi.Ordinal != index ||
                observedExport.Ordinal != index + 1 ||
                !Eq(expected.Identity, observedApi.Identity))
            {
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .RowOrderMismatch);
            }
            if (!Eq(expected.CanonicalJson, observedApi.CanonicalJson))
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .RowCellMismatch);

            var cellRelations = ImmutableArray.CreateBuilder<string>(
                export.Columns.Length);
            for (var columnIndex = 0; columnIndex < export.Columns.Length;
                 columnIndex++)
            {
                var expectedCell = ExpectedCell(
                    export.Columns[columnIndex], expected.Root, index);
                var observedCell = observedExport.Cells[columnIndex];
                if (!SameExpectedCell(expectedCell, observedCell))
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .RowCellMismatch);
                cellRelations.Add(H(
                    "P10_ACTUAL_DIRECT_CROSS_VIEW_CELL_RELATION_V1",
                    I(index),
                    I(columnIndex),
                    expectedCell.SemanticSha256,
                    observedCell.SemanticSha256));
                relationCount = checked(relationCount + 1);
            }
            relations.Add(H(
                "P10_ACTUAL_DIRECT_CROSS_VIEW_ROW_RELATION_V1",
                I(index),
                expected.Identity,
                expected.SemanticSha256,
                observedApi.SemanticSha256,
                observedExport.SemanticSha256,
                HS("P10_ACTUAL_DIRECT_CROSS_VIEW_ROW_CELL_RELATIONS_V1",
                    cellRelations)));
        }

        var rowRelationSha = HS(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_ROW_RELATIONS_V1", relations);
        var totalsRelationSha = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_TOTALS_RELATION_V1",
            baseline.TotalsSha256,
            TotalsSha("P10_ACTUAL_DIRECT_CROSS_VIEW_API_TOTALS_V1", api.Totals),
            export.TotalsSha256,
            I(baseline.Totals.Length),
            I(export.Totals.Length));
        var resultRelationSha = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_RESULT_RELATION_V1",
            plan.P9RunId,
            plan.P9GenerationId,
            plan.P9GenerationSha256,
            I(plan.P9DirectSourceRevision),
            I(plan.P9DirectPublicationRevision),
            plan.ExportId,
            plan.ExportResultId,
            actual.Api.OwnerResultId,
            actual.ExportManifest.ResultId,
            actual.ExportManifest.ResultSha256,
            api.CaptureSha256,
            export.ManifestSha256,
            export.CaptureSha256);
        var actualSha = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_ACTUAL_V1",
            Schema,
            baseline.ProjectionSha256,
            api.PartitionSha256,
            export.PartitionSha256,
            filters.RelationSha256,
            resultRelationSha);
        var proofSha = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_PROOF_V1",
            Schema,
            "true",
            StatisticReconciliationActualDirectCrossViewParityFailures.None,
            plan.SemanticSha256,
            actualSha,
            baseline.RowsSha256,
            I(baseline.Rows.Length),
            api.PartitionSha256,
            I(api.Rows.Length),
            export.RowsSha256,
            I(export.Rows.Length),
            rowRelationSha,
            I(relationCount),
            totalsRelationSha,
            I(baseline.Totals.Length + export.Totals.Length),
            filters.RelationSha256,
            resultRelationSha);
        return new(
            Schema,
            true,
            StatisticReconciliationActualDirectCrossViewParityFailures.None,
            plan.SemanticSha256,
            actualSha,
            baseline.RowsSha256,
            baseline.Rows.Length,
            api.PartitionSha256,
            api.Rows.Length,
            export.RowsSha256,
            export.Rows.Length,
            rowRelationSha,
            relationCount,
            totalsRelationSha,
            baseline.Totals.Length + export.Totals.Length,
            filters.RelationSha256,
            resultRelationSha,
            proofSha);
    }

    private static ImmutableArray<
        StatisticReconciliationActualExportColumnContract> DeriveColumns(
            string resultKind,
            ImmutableArray<SemanticRow> rows,
            ImmutableArray<string> headers)
    {
        var result = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualExportColumnContract>(headers.Length);
        result.Add(new(
            0,
            "ordinal",
            StatisticReconciliationActualExportValueTypes.Integer,
            StatisticReconciliationActualExportBlankPolicies.Forbidden,
            false));
        for (var columnIndex = 1; columnIndex < headers.Length; columnIndex++)
        {
            var name = headers[columnIndex];
            string? valueType = null;
            var declaredValueType =
                StatRunExportColumnManifestContract.DeclaredValueType(
                    resultKind,
                    name);
            var declaredSchema = Eq(
                resultKind,
                StatRunExportResultKinds.DirectField);
            if (declaredSchema && declaredValueType is null)
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ExportCaptureInvalid);
            var sawNull = false;
            var sawEmpty = false;
            foreach (var row in rows)
            {
                var value = row.Root.GetProperty(name);
                if (value.ValueKind == JsonValueKind.Null)
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
                var current = ExportType(name, value);
                if (declaredValueType is not null &&
                    !Eq(declaredValueType, current))
                {
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .ExportCaptureInvalid);
                }
                if (valueType is not null && !Eq(valueType, current))
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .ExportCaptureInvalid);
                valueType = current;
            }
            valueType ??= declaredValueType;
            if (valueType is null || sawNull && sawEmpty)
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .ExportCaptureInvalid);
            result.Add(new(
                columnIndex,
                name,
                valueType,
                sawNull
                    ? StatisticReconciliationActualExportBlankPolicies.Null
                    : sawEmpty
                        ? StatisticReconciliationActualExportBlankPolicies.Empty
                        : StatisticReconciliationActualExportBlankPolicies.Forbidden,
                false));
        }
        return result.MoveToImmutable();
    }

    private static string ExportType(string header, JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Number =>
                StatisticReconciliationActualExportValueTypes.Decimal,
            JsonValueKind.True or JsonValueKind.False =>
                StatisticReconciliationActualExportValueTypes.Boolean,
            JsonValueKind.String when IsExportInstant(header, value.GetString()) =>
                StatisticReconciliationActualExportValueTypes.UtcInstant,
            JsonValueKind.String =>
                StatisticReconciliationActualExportValueTypes.Text,
            JsonValueKind.Array or JsonValueKind.Object =>
                StatisticReconciliationActualExportValueTypes.Json,
            _ => throw F(
                StatisticReconciliationActualDirectCrossViewParityFailures
                    .ExportCaptureInvalid)
        };

    private static bool IsExportInstant(string header, string? value)
        => value is not null &&
           (header.EndsWith("Utc", StringComparison.Ordinal) ||
            header.EndsWith("Date", StringComparison.Ordinal)) &&
           DateTime.TryParse(
               value,
               CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal |
               DateTimeStyles.AdjustToUniversal,
               out _);

    private static ExportSemanticCell ExpectedCell(
        StatisticReconciliationActualExportColumnContract column,
        JsonElement row,
        int zeroBasedRow)
    {
        if (column.Ordinal == 0)
            return ExpectedValueCell(column, I(zeroBasedRow + 1), 0, false);
        var value = row.GetProperty(column.Name);
        if (value.ValueKind == JsonValueKind.Null)
            return ExpectedBlankCell(
                column,
                StatisticReconciliationActualExportValueStates.Null);
        if (value.ValueKind == JsonValueKind.String &&
            value.GetString()?.Length == 0)
            return ExpectedBlankCell(
                column,
                StatisticReconciliationActualExportValueStates.Empty);
        switch (column.ValueType)
        {
            case StatisticReconciliationActualExportValueTypes.Decimal:
                if (!value.TryGetDecimal(out var number))
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .RowCellMismatch);
                var decimalValue = number.ToString(CultureInfo.InvariantCulture);
                if (!CanonicalDecimal(decimalValue, out var scale))
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .RowCellMismatch);
                return ExpectedValueCell(column, decimalValue, scale, false);
            case StatisticReconciliationActualExportValueTypes.Boolean:
                return ExpectedValueCell(
                    column,
                    value.GetBoolean() ? "true" : "false",
                    0,
                    false);
            case StatisticReconciliationActualExportValueTypes.UtcInstant:
                if (!DateTime.TryParse(
                        value.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal |
                        DateTimeStyles.AdjustToUniversal,
                        out var date))
                    throw F(
                        StatisticReconciliationActualDirectCrossViewParityFailures
                            .RowCellMismatch);
                return ExpectedValueCell(
                    column,
                    DateTime.SpecifyKind(date, DateTimeKind.Utc).ToString(
                        "O", CultureInfo.InvariantCulture),
                    0,
                    false);
            case StatisticReconciliationActualExportValueTypes.Text:
                var text = value.GetString() ?? string.Empty;
                var neutralized = text.Length > 0 &&
                    text[0] is '=' or '+' or '-' or '@';
                return ExpectedValueCell(
                    column,
                    neutralized ? $"'{text}" : text,
                    0,
                    neutralized);
            case StatisticReconciliationActualExportValueTypes.Json:
                return ExpectedValueCell(
                    column,
                    StatisticReconciliationActualJson.Canonicalize(value),
                    0,
                    false);
            default:
                throw F(
                    StatisticReconciliationActualDirectCrossViewParityFailures
                        .RowCellMismatch);
        }
    }

    private static ExportSemanticCell ExpectedBlankCell(
        StatisticReconciliationActualExportColumnContract column,
        string state)
        => ExpectedCellCore(column, state, string.Empty, 0, false);

    private static ExportSemanticCell ExpectedValueCell(
        StatisticReconciliationActualExportColumnContract column,
        string canonical,
        int scale,
        bool neutralized)
        => ExpectedCellCore(
            column,
            StatisticReconciliationActualExportValueStates.Value,
            canonical,
            scale,
            neutralized);

    private static ExportSemanticCell ExpectedCellCore(
        StatisticReconciliationActualExportColumnContract column,
        string state,
        string canonical,
        int scale,
        bool neutralized)
        => new(
            column.Ordinal,
            column.Name,
            column.ValueType,
            state,
            canonical,
            scale,
            neutralized,
            H("P10_ACTUAL_EXPORT_CELL_V1",
                I(column.Ordinal),
                column.Name,
                column.ValueType,
                state,
                canonical,
                I(scale),
                neutralized ? "true" : "false"));

    private static bool SameExpectedCell(
        ExportSemanticCell expected,
        ExportSemanticCell actual)
        => expected.Ordinal == actual.Ordinal &&
           Eq(expected.Name, actual.Name) &&
           Eq(expected.ValueType, actual.ValueType) &&
           Eq(expected.ValueState, actual.ValueState) &&
           Eq(expected.CanonicalValue, actual.CanonicalValue) &&
           expected.DecimalScale == actual.DecimalScale &&
           expected.FormulaNeutralized == actual.FormulaNeutralized &&
           Eq(expected.SemanticSha256, actual.SemanticSha256);

    private static StatisticReconciliationActualDirectCrossViewParityProof
        Incomplete(string failureCode)
    {
        var empty = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_INCOMPLETE_MATERIAL_V1",
            Schema,
            failureCode);
        var proof = H(
            "P10_ACTUAL_DIRECT_CROSS_VIEW_PROOF_V1",
            Schema,
            "false",
            failureCode,
            empty, empty, empty, "0", empty, "0", empty, "0", empty, "0",
            empty, "0", empty, empty);
        return new(
            Schema,
            false,
            failureCode,
            empty,
            empty,
            empty,
            0,
            empty,
            0,
            empty,
            0,
            empty,
            0,
            empty,
            0,
            empty,
            empty,
            proof);
    }
}
