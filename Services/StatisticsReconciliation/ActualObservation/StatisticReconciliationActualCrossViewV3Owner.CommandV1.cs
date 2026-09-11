namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualCrossViewV3OwnerSchemas
{
    internal const string Command =
        "P10_ACTUAL_CROSS_VIEW_V3_OWNER_COMMAND_V1";
    internal const string Resolution =
        "P10_ACTUAL_CROSS_VIEW_V3_OWNER_RESOLUTION_V1";
}

/// <summary>
/// Server-only carrier for final trusted captures and their exact owner
/// selectors. It contains no caller-provided row, cell, total, or semantic
/// projection.
/// </summary>
internal sealed record StatisticReconciliationActualCrossViewV3OwnerCommand(
    string SchemaVersion,
    StatisticReconciliationActualTrustedCaptureMaterial Material,
    ActualSourceMembershipCapture? Source,
    ActualDirectProjectionCapture? Direct,
    ActualAggregateCapture? Aggregate,
    ActualBasicResultObservation? Basic,
    ActualAdvancedCapture? Advanced,
    ActualP9DiffCapture? Diff,
    StatisticReconciliationActualApiCapture? Api,
    StatisticReconciliationActualExportCapture? Export)
{
    internal StatisticReconciliationActualCrossViewV2OwnerCommand
        ToProjectionCarrier()
        => new(
            StatisticReconciliationActualCrossViewV2OwnerSchemas.Command,
            Material,
            Source,
            Direct,
            Aggregate,
            Basic,
            Advanced,
            Diff,
            Api,
            Export);
}
