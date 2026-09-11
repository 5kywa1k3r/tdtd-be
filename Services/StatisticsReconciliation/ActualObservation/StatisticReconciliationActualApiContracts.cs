using System.Collections.Immutable;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal static class StatisticReconciliationActualApiSurfaces
{
    internal const string DirectField = "DIRECT_FIELD";
    internal const string DirectTable = "DIRECT_TABLE";
    internal const string DirectLabel = "DIRECT_LABEL";
    internal const string BasicSource = "BASIC_SOURCE";
    internal const string P9Diff = "P9_DIFF";

    internal static bool IsSupported(string value)
        => value is DirectField or DirectTable or DirectLabel or BasicSource or P9Diff;
}

internal static class StatisticReconciliationActualApiCaptureStates
{
    internal const string Ready = "READY";
    internal const string Stale = "STALE";
    internal const string Invalid = "INVALID";
}

internal sealed record StatisticReconciliationActualApiPageSelector(
    int Page,
    int PageSize);

internal sealed record StatisticReconciliationActualApiCaptureRequest(
    string? Surface,
    string? WorkId,
    string? ScopeAssignmentId,
    string? DynamicFormTemplateId,
    string? OwnerResultId,
    string? FilterJson,
    long ExpectedTotalRows,
    ImmutableArray<StatisticReconciliationActualApiPageSelector> Pages);

internal sealed record StatisticReconciliationActualApiAuthorizationProbe(
    string? WorkId,
    string? ScopeAssignmentId);

internal sealed record StatisticReconciliationActualApiAuthorizationContext(
    string ActorUserId,
    string WorkId,
    string ScopeAssignmentId,
    ImmutableArray<string> PermissionCodes,
    long RowCountBeforeRedaction,
    long RowCountAfterRedaction,
    string AuthorizationSnapshotSha256,
    bool IsAuthorized);

internal sealed record StatisticReconciliationActualApiOwnerPageQuery(
    string Surface,
    string RouteId,
    string WorkId,
    string ScopeAssignmentId,
    string DynamicFormTemplateId,
    string? OwnerResultId,
    string CanonicalFilterJson,
    string FilterSha256,
    string AuthorizationSnapshotSha256,
    int Page,
    int PageSize,
    string RequestSha256);

internal sealed record StatisticReconciliationActualApiTotalValue(
    string Name,
    string ValueType,
    string CanonicalValue);

internal sealed record StatisticReconciliationActualApiOwnerRow(
    string Identity,
    string CanonicalRowJson,
    string RowSemanticSha256);

internal sealed record StatisticReconciliationActualApiOwnerPage(
    string Surface,
    string RouteId,
    string WorkId,
    string ScopeAssignmentId,
    string DynamicFormTemplateId,
    string? OwnerResultId,
    string FilterSha256,
    string AuthorizationSnapshotSha256,
    string RequestSha256,
    string? ETag,
    string? GenerationId,
    string? GenerationSha256,
    int Page,
    int PageSize,
    int? TotalPages,
    int ReturnedRows,
    ImmutableArray<StatisticReconciliationActualApiTotalValue> FullFilterTotals,
    ImmutableArray<StatisticReconciliationActualApiOwnerRow> Rows);

internal interface IStatisticReconciliationActualApiOwnerReader
{
    Task<StatisticReconciliationActualApiAuthorizationContext> AuthorizeAsync(
        StatisticReconciliationActualApiAuthorizationProbe probe,
        CancellationToken cancellationToken);

    Task<StatisticReconciliationActualApiOwnerPage> ReadPageAsync(
        StatisticReconciliationActualApiAuthorizationContext authorization,
        StatisticReconciliationActualApiOwnerPageQuery query,
        CancellationToken cancellationToken);
}

internal sealed record StatisticReconciliationActualApiRowObservation(
    int AbsoluteOrdinal,
    string Identity,
    string CanonicalRowJson,
    string RowSemanticSha256,
    bool StoredSemanticMatches);

internal sealed record StatisticReconciliationActualApiPageObservation(
    int Page,
    int PageSize,
    int? TotalPages,
    int ReturnedRows,
    long TotalRows,
    ImmutableArray<StatisticReconciliationActualApiTotalValue> FullFilterTotals,
    ImmutableArray<StatisticReconciliationActualApiRowObservation> Rows,
    string? ETag,
    string? GenerationId,
    string? GenerationSha256,
    bool TargetBindingMatches,
    bool RequestBindingMatches,
    bool PagingContractValid,
    bool RowIdentitiesUnique,
    bool RowSemanticsValid,
    string PageSemanticSha256);

internal sealed record StatisticReconciliationActualApiCapture(
    string Surface,
    string RouteId,
    string WorkId,
    string ScopeAssignmentId,
    string DynamicFormTemplateId,
    string? OwnerResultId,
    string CanonicalFilterJson,
    string FilterSha256,
    StatisticReconciliationActualApiAuthorizationContext Authorization,
    ImmutableArray<StatisticReconciliationActualApiPageObservation> Pages,
    bool FullFilterTotalsStable,
    bool GenerationStable,
    bool OverlapStable,
    bool PagingContractsValid,
    string CaptureState,
    string? CaptureReason,
    string CaptureSemanticSha256);
