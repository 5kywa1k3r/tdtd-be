using tdtd_be.DTOs.Statistics;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public interface IWorkReportLabelStatisticsService
{
    Task RebuildForReportAsync(string reportId, string? actorUserId, CancellationToken ct = default);
    Task<ReportStatisticAggregateKey?> RebuildValuesForReportAsync(
        string reportId,
        string? actorUserId,
        CancellationToken ct = default);
    Task RebuildAggregatesForWorkPeriodAsync(
        string workId,
        string? periodInstanceKey,
        string? dynamicFormTemplateId,
        string? actorUserId,
        CancellationToken ct = default);
    Task<LabelStatisticSummaryResponse> SearchSummaryAsync(
        LabelStatisticSummaryRequest req,
        CancellationToken ct = default);
}
