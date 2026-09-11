namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

// V1 remains an internal Direct-only construction aid for the focused tests.
// Production consumers must use the family-aware V2 contract, which carries
// distinct API/export bases and explicit API applicability.
internal static class StatisticReconciliationActualDirectCrossViewParityV1Status
{
    internal const bool ProductionSignable = false;
    internal const string SuccessorSchema =
        StatisticReconciliationActualCrossViewParityV2Schemas.Proof;
}
