namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualApiProtocol
{
    internal static string RouteId(string surface)
        => surface switch
        {
            StatisticReconciliationActualApiSurfaces.DirectField =>
                "POST:/api/work-report-field-statistics/summary",
            StatisticReconciliationActualApiSurfaces.DirectTable =>
                "POST:/api/work-report-table-statistics/summary",
            StatisticReconciliationActualApiSurfaces.DirectLabel =>
                "POST:/api/work-report-label-statistics/summary",
            StatisticReconciliationActualApiSurfaces.BasicSource =>
                "POST:/api/work-assignment-basic-summary/summary#sourcesPage",
            StatisticReconciliationActualApiSurfaces.P9Diff =>
                "GET:/api/work-report-statistic-diffs/assignments/{assignmentId}/templates/{templateId}/results/{resultId}",
            _ => throw new StatisticReconciliationActualApiEndpointException(
                StatusCodes.Status400BadRequest,
                "API_SURFACE_UNSUPPORTED")
        };

    internal static string RequestSha(
        string surface,
        string routeId,
        string workId,
        string scopeAssignmentId,
        string dynamicFormTemplateId,
        string? ownerResultId,
        string filterSha256,
        string authorizationSnapshotSha256,
        int page,
        int pageSize)
        => StatisticReconciliationActualCanonical.Hash(
            "P10_ACTUAL_API_REQUEST_V1",
            surface,
            routeId,
            workId,
            scopeAssignmentId,
            dynamicFormTemplateId,
            ownerResultId,
            filterSha256,
            authorizationSnapshotSha256,
            StatisticReconciliationActualCanonical.Integer(page),
            StatisticReconciliationActualCanonical.Integer(pageSize));

    internal static string RequestSha(
        StatisticReconciliationActualApiOwnerPageQuery query)
        => RequestSha(
            query.Surface,
            query.RouteId,
            query.WorkId,
            query.ScopeAssignmentId,
            query.DynamicFormTemplateId,
            query.OwnerResultId,
            query.FilterSha256,
            query.AuthorizationSnapshotSha256,
            query.Page,
            query.PageSize);
}
