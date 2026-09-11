using System.Collections.Immutable;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class OwnerRoutePermissionDrift
{
    internal static StatisticReconciliationActualApiAuthorizationContext Create()
    {
        var permissions = ImmutableArray.Create(
            "RECONCILIATION_DETAIL",
            "STATISTICS_READ");
        var sha = StatisticReconciliationActualApiObservationAdapter.AuthorizationSha(
            "service-actor",
            OwnerRouteFixture.WorkId,
            OwnerRouteFixture.ScopeId,
            permissions,
            1,
            1);
        return new StatisticReconciliationActualApiAuthorizationContext(
            "service-actor",
            OwnerRouteFixture.WorkId,
            OwnerRouteFixture.ScopeId,
            permissions,
            1,
            1,
            sha,
            true);
    }
}
