namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualCrossViewParityV2
{
    private static ValidatedTotal ExpectedExportTotal(
        string name,
        string type,
        string canonical,
        int scale = 0)
        => new(
            name,
            type,
            StatisticReconciliationActualExportValueStates.Value,
            canonical,
            scale,
            H("P10_ACTUAL_EXPORT_TOTAL_V1",
                name,
                type,
                StatisticReconciliationActualExportValueStates.Value,
                canonical,
                I(scale)));
}
