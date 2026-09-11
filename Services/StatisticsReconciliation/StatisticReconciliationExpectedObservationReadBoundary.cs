namespace tdtd_be.Services.StatisticsReconciliation;

internal static class StatisticReconciliationExpectedObservationReadBoundary
{
    internal static async Task<TResult> ExecuteAsync<TScope, TRun, TResult>(
        bool requireDetailedPermission,
        Action requireDetail,
        Func<Task<TScope>> authorizeScope,
        Func<TScope, Task<TRun>> loadAuthorizedRun,
        Action<TRun> requireRunIntegrity,
        Func<TScope, TRun, Task<TResult>> readAuthorizedTarget)
    {
        ArgumentNullException.ThrowIfNull(requireDetail);
        ArgumentNullException.ThrowIfNull(authorizeScope);
        ArgumentNullException.ThrowIfNull(loadAuthorizedRun);
        ArgumentNullException.ThrowIfNull(requireRunIntegrity);
        ArgumentNullException.ThrowIfNull(readAuthorizedTarget);

        if (requireDetailedPermission)
            requireDetail();
        var scope = await authorizeScope();
        var run = await loadAuthorizedRun(scope);
        requireRunIntegrity(run);
        return await readAuthorizedTarget(scope, run);
    }
}
