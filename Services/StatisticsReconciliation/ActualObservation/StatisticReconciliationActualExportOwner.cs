namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationActualExportOwnerTarget(
    string ExportId,
    string ResultKind,
    string WorkId,
    string ScopeType,
    string ScopeId,
    string ResultId,
    string ExpectedSourceOwnerSha256,
    string ExpectedConfigOwnerSha256,
    string ExpectedRequestSha256,
    string ExpectedAuthorizationSnapshotSha256,
    string ExpectedContentSha256,
    string ExpectedColumnManifestSha256,
    string ExpectedOwnerSemanticSha256,
    string? ExpectedPeriodInstanceKey = null,
    string? ExpectedFilterSha256 = null);

internal static class StatisticReconciliationActualExportOwnerReadStates
{
    internal const string Ready = "READY";
    internal const string Stale = "STALE";
}

internal sealed record StatisticReconciliationActualExportOwnerRead(
    string State,
    string? Reason,
    StatisticReconciliationActualExportArtifact? Artifact)
{
    internal static StatisticReconciliationActualExportOwnerRead Ready(
        StatisticReconciliationActualExportArtifact artifact)
        => new(
            StatisticReconciliationActualExportOwnerReadStates.Ready,
            null,
            artifact);

    internal static StatisticReconciliationActualExportOwnerRead Stale(
        string reason)
        => new(
            StatisticReconciliationActualExportOwnerReadStates.Stale,
            reason,
            null);
}

internal interface IStatisticReconciliationActualExportOwnerReader
{
    Task<StatisticReconciliationActualExportOwnerRead> ReadArtifactAsync(
        StatisticReconciliationActualExportOwnerTarget target,
        CancellationToken cancellationToken = default);
}
