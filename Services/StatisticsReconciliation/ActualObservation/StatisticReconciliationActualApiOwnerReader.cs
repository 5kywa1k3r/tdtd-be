namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Server-owned authorization projection. Implementations must resolve the
/// actor and scope before consulting a result identifier or result collection.
/// </summary>
internal interface IStatisticReconciliationActualApiAuthorizationSource
{
    Task<StatisticReconciliationActualApiAuthorizationContext>
        AuthorizeBeforeExistenceAsync(
            StatisticReconciliationActualApiAuthorizationProbe probe,
            CancellationToken cancellationToken);
}

/// <summary>
/// Read-only API-result projection. Implementations may issue only query/count
/// operations; refresh, rebuild, export creation and download are forbidden.
/// </summary>
internal interface IStatisticReconciliationActualApiPageProjectionSource
{
    Task<StatisticReconciliationActualApiOwnerPage> ReadPageProjectionAsync(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Concrete fail-closed owner used by the API observation adapter. The two
/// dependencies intentionally separate authorization-before-existence from the
/// result query so a production binding cannot accidentally resolve a result
/// while deciding access.
/// </summary>
internal sealed class StatisticReconciliationActualReadOnlyApiOwnerReader(
    IStatisticReconciliationActualApiAuthorizationSource authorizationSource,
    IStatisticReconciliationActualApiPageProjectionSource pageSource)
    : IStatisticReconciliationActualApiOwnerReader
{
    public async Task<StatisticReconciliationActualApiAuthorizationContext>
        AuthorizeAsync(
            StatisticReconciliationActualApiAuthorizationProbe probe,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(authorizationSource);
        cancellationToken.ThrowIfCancellationRequested();
        var authorization = await authorizationSource
            .AuthorizeBeforeExistenceAsync(probe, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Fail("AUTHORIZATION_NULL");
        if (!authorization.IsAuthorized)
            return authorization;

        if (!Eq(probe.WorkId, authorization.WorkId) ||
            !Eq(probe.ScopeAssignmentId, authorization.ScopeAssignmentId) ||
            authorization.RowCountBeforeRedaction < 0 ||
            authorization.RowCountAfterRedaction < 0 ||
            authorization.RowCountAfterRedaction >
            authorization.RowCountBeforeRedaction)
            throw Fail("AUTHORIZATION_BINDING_INVALID");
        var computed = StatisticReconciliationActualApiObservationAdapter
            .AuthorizationSha(
                authorization.ActorUserId,
                authorization.WorkId,
                authorization.ScopeAssignmentId,
                authorization.PermissionCodes,
                authorization.RowCountBeforeRedaction,
                authorization.RowCountAfterRedaction);
        if (!Eq(computed, authorization.AuthorizationSnapshotSha256))
            throw Fail("AUTHORIZATION_DIGEST_MISMATCH");
        return authorization;
    }

    public async Task<StatisticReconciliationActualApiOwnerPage> ReadPageAsync(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(pageSource);
        if (!authorization.IsAuthorized ||
            !Eq(authorization.WorkId, query.WorkId) ||
            !Eq(authorization.ScopeAssignmentId, query.ScopeAssignmentId) ||
            !Eq(authorization.AuthorizationSnapshotSha256,
                query.AuthorizationSnapshotSha256) ||
            !StatisticReconciliationActualApiSurfaces.IsSupported(query.Surface))
            throw Fail("PAGE_AUTHORIZATION_BINDING_INVALID");

        cancellationToken.ThrowIfCancellationRequested();
        var page = await pageSource.ReadPageProjectionAsync(
                authorization,
                query,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Fail("PAGE_NULL");
        if (!Eq(page.Surface, query.Surface) ||
            !Eq(page.RouteId, query.RouteId) ||
            !Eq(page.WorkId, query.WorkId) ||
            !Eq(page.ScopeAssignmentId, query.ScopeAssignmentId) ||
            !Eq(page.DynamicFormTemplateId, query.DynamicFormTemplateId) ||
            !Eq(page.OwnerResultId, query.OwnerResultId) ||
            !Eq(page.FilterSha256, query.FilterSha256) ||
            !Eq(page.AuthorizationSnapshotSha256,
                query.AuthorizationSnapshotSha256) ||
            !Eq(page.RequestSha256, query.RequestSha256) ||
            page.Page != query.Page || page.PageSize != query.PageSize)
            throw Fail("PAGE_TARGET_BINDING_MISMATCH");
        return page;
    }

    private static bool Eq(string? left, string? right)
        => StringComparer.Ordinal.Equals(left, right);

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_API_OWNER_{reason}");
}
