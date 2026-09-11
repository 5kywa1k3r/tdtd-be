namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed partial class StatisticReconciliationActualCrossViewParityV2
{
    // Transitional call seam for the new production owners. The optional
    // discriminator keeps the original instance method/source compatibility.
    internal static StatisticReconciliationActualCrossViewParityV2Proof Prove(
        StatisticReconciliationActualCrossViewParityV2Plan? plan,
        StatisticReconciliationActualCrossViewParityV2Base? baseline,
        StatisticReconciliationActualCrossViewParityV2Actual? actual,
        bool ownerCompatibility = true)
    {
        _ = ownerCompatibility;
        return new StatisticReconciliationActualCrossViewParityV2().Prove(
            plan,
            baseline,
            actual);
    }
}
