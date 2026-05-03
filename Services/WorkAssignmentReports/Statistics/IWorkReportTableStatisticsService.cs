namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

using tdtd_be.DTOs.Statistics;

public interface IWorkReportTableStatisticsService
{
    Task RebuildForReportAsync(string reportId, string? actorUserId, CancellationToken ct = default);

    Task RebuildAggregatesForWorkPeriodAsync(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId,
        string? actorUserId,
        CancellationToken ct = default);

    Task<RebuildTableStatisticResponse> RebuildForWorkPeriodAsync(
        RebuildTableStatisticRequest req,
        string? actorUserId,
        CancellationToken ct = default);

    Task<TableStatisticSummaryResponse> SearchSummaryAsync(
        TableStatisticSummaryRequest req,
        CancellationToken ct = default);
}
