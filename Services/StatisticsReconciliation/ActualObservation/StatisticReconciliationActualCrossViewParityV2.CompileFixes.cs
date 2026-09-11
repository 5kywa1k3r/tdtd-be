namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

// Kept as a separate new file so the family-aware prover remains isolated
// from all capture, persistence, verdict and review production call sites.
internal static class StatisticReconciliationActualCrossViewParityV2Contracts
{
    internal static bool ValidManifestDimensions(
        StatisticReconciliationActualExportManifest value)
        => value.RowCount >= 0 && value.ColumnCount > 0 &&
           !value.Columns.IsDefault &&
           value.Columns.Length == value.ColumnCount;
}
