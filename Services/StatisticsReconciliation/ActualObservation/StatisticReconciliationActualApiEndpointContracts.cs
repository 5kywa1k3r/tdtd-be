namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

public abstract class StatisticReconciliationActualApiEndpointService
{
    internal abstract Task<StatisticReconciliationActualApiAuthorizationContext?>
        AuthorizeBeforeExistenceAsync(
            string? workId,
            string? scopeAssignmentId,
            CancellationToken cancellationToken);

    internal abstract Task<StatisticReconciliationActualApiOwnerPage>
        ReadPageProjectionAsync(
            StatisticReconciliationActualApiAuthorizationContext authorization,
            StatisticReconciliationActualApiOwnerPageQuery query,
            CancellationToken cancellationToken);
}

internal sealed class StatisticReconciliationActualApiEndpointException(
    int statusCode,
    string reason) : InvalidOperationException(reason)
{
    internal int StatusCode { get; } = statusCode;
    internal string Reason { get; } = reason;
}
