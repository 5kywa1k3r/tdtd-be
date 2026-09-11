namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualCrossViewParityV2
{
    private static string RelateApi(
        ValidatedApiBase baseline,
        ValidatedApi actual)
    {
        if (baseline.Rows.Length != actual.Rows.Length)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .RowCountMismatch);
        if (!baseline.Totals.SequenceEqual(actual.Totals))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .TotalsMismatch);
        var relations = new string[baseline.Rows.Length];
        for (var index = 0; index < baseline.Rows.Length; index++)
        {
            var expected = baseline.Rows[index];
            var observed = actual.Rows[index];
            if (expected.Ordinal != observed.Ordinal ||
                !Eq(expected.Identity, observed.Identity))
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .RowOrderMismatch);
            if (!Eq(expected.CanonicalJson, observed.CanonicalJson))
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .RowCellMismatch);
            relations[index] = H(
                "P10_ACTUAL_CROSS_VIEW_API_ROW_RELATION_V2",
                I(index), expected.Identity, expected.SemanticSha256,
                observed.SemanticSha256);
        }
        return H(
            "P10_ACTUAL_CROSS_VIEW_API_RELATION_V2",
            baseline.RowsSha256,
            actual.RowsSha256,
            baseline.TotalsSha256,
            actual.TotalsSha256,
            HS("P10_ACTUAL_CROSS_VIEW_API_ROW_RELATIONS_V2", relations));
    }

    private static ExportRelation RelateExport(
        ValidatedExportBase baseline,
        ValidatedExport actual)
    {
        if (baseline.Rows.Length != actual.Rows.Length)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .RowCountMismatch);
        if (!baseline.Columns.SequenceEqual(actual.Columns))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .ExportInvalid);
        if (!baseline.Totals.SequenceEqual(actual.Totals))
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .TotalsMismatch);
        var rowRelations = new string[baseline.Rows.Length];
        long cellCount = 0;
        for (var rowIndex = 0; rowIndex < baseline.Rows.Length; rowIndex++)
        {
            var expected = baseline.Rows[rowIndex];
            var observed = actual.Rows[rowIndex];
            if (expected.Ordinal != observed.Ordinal)
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .RowOrderMismatch);
            if (expected.Cells.Length != observed.Cells.Length)
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .RowCellMismatch);
            var cellRelations = new string[expected.Cells.Length];
            for (var cellIndex = 0; cellIndex < expected.Cells.Length;
                 cellIndex++)
            {
                var left = expected.Cells[cellIndex];
                var right = observed.Cells[cellIndex];
                if (!left.Equals(right))
                    throw Fail(
                        StatisticReconciliationActualCrossViewParityV2Failures
                            .RowCellMismatch);
                cellRelations[cellIndex] = H(
                    "P10_ACTUAL_CROSS_VIEW_EXPORT_CELL_RELATION_V2",
                    I(rowIndex), I(cellIndex), left.SemanticSha256,
                    right.SemanticSha256);
                cellCount = checked(cellCount + 1);
            }
            rowRelations[rowIndex] = H(
                "P10_ACTUAL_CROSS_VIEW_EXPORT_ROW_RELATION_V2",
                I(rowIndex), expected.SemanticSha256,
                observed.SemanticSha256,
                HS("P10_ACTUAL_CROSS_VIEW_EXPORT_CELL_RELATIONS_V2",
                    cellRelations));
        }
        var cellRelationSha = HS(
            "P10_ACTUAL_CROSS_VIEW_EXPORT_ROW_RELATIONS_V2", rowRelations);
        var totalsRelationSha = H(
            "P10_ACTUAL_CROSS_VIEW_EXPORT_TOTAL_RELATION_V2",
            baseline.TotalsSha256,
            actual.TotalsSha256,
            I(baseline.Totals.Length));
        var filterRelationSha = H(
            "P10_ACTUAL_CROSS_VIEW_EXPORT_FILTER_RELATION_V2",
            actual.FilterRelationSha256);
        return new(
            cellRelationSha,
            cellCount,
            totalsRelationSha,
            baseline.Totals.Length,
            filterRelationSha);
    }

    private static (string Sha256, long Count) RelateSharedRows(
        NormalizedPlan plan,
        ValidatedApiBase api,
        ValidatedExportBase export)
    {
        if (!plan.ViewsShareOrderedRows)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .FamilySurfaceInvalid);
        if (api.Rows.Length != export.Rows.Length)
            throw Fail(
                StatisticReconciliationActualCrossViewParityV2Failures
                    .RowCountMismatch);
        var relations = new string[api.Rows.Length];
        for (var index = 0; index < api.Rows.Length; index++)
        {
            var apiRow = api.Rows[index];
            var exportRow = export.Rows[index];
            if (apiRow.Ordinal != index || exportRow.Ordinal != index + 1 ||
                exportRow.CanonicalSourceRowJson is null)
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .RowOrderMismatch);
            if (!Eq(apiRow.CanonicalJson,
                    exportRow.CanonicalSourceRowJson))
                throw Fail(
                    StatisticReconciliationActualCrossViewParityV2Failures
                        .SharedRelationMismatch);
            relations[index] = H(
                "P10_ACTUAL_CROSS_VIEW_SHARED_ROW_RELATION_V2",
                plan.Family,
                I(index),
                apiRow.Identity,
                apiRow.SemanticSha256,
                exportRow.CanonicalSourceRowJson,
                exportRow.SemanticSha256);
        }
        return (
            HS("P10_ACTUAL_CROSS_VIEW_SHARED_ROW_RELATIONS_V2", relations),
            relations.LongLength);
    }

    private static StatisticReconciliationActualCrossViewParityV2Proof
        Incomplete(string failureCode)
    {
        var empty = H(
            "P10_ACTUAL_CROSS_VIEW_INCOMPLETE_V2",
            ProofSchema,
            failureCode);
        var proof = H(
            "P10_ACTUAL_CROSS_VIEW_PROOF_V2",
            ProofSchema,
            "false",
            failureCode,
            empty, empty, empty, "0", empty, "0", empty, "0", empty, "0",
            empty, "0", empty, empty);
        return new(
            ProofSchema,
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

    private sealed record ExportRelation(
        string CellRelationSha256,
        long CellRelationCount,
        string TotalsRelationSha256,
        int TotalsRelationCount,
        string FilterRelationSha256);
}
