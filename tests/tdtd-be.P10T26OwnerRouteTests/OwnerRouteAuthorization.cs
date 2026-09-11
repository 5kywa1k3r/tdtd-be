using System.Collections.Immutable;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class OwnerRouteAuthorization
{
    internal static StatisticReconciliationActualApiAuthorizationContext Create(
        string actor)
    {
        var permissions = ImmutableArray.Create("STATISTICS_READ");
        var sha = StatisticReconciliationActualApiObservationAdapter.AuthorizationSha(
            actor,
            OwnerRouteFixture.WorkId,
            OwnerRouteFixture.ScopeId,
            permissions,
            1,
            1);
        return new StatisticReconciliationActualApiAuthorizationContext(
            actor,
            OwnerRouteFixture.WorkId,
            OwnerRouteFixture.ScopeId,
            permissions,
            1,
            1,
            sha,
            true);
    }
}
