namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationActualCrossViewV3FamilyProjection(
    string Family,
    StatisticReconciliationActualCrossViewApiBaseProjection? ApiBase,
    string ExportSourceJson,
    StatisticReconciliationActualCrossViewBasicGenerationPreimage?
        BasicGeneration);
