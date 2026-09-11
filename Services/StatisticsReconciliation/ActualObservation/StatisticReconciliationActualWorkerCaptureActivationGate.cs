namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualWorkerCaptureActivationGate
{
    internal static async Task<T> ExecuteAsync<T>(
        IStatisticReconciliationCandidateActivation activation,
        Func<CancellationToken, Task<T>> enabledCaptureAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(enabledCaptureAsync);

        _ = activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry.WorkerCapture);
        cancellationToken.ThrowIfCancellationRequested();

        return await enabledCaptureAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
