using tdtd_be.DTOs.Statistics;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public interface IWorkReportFieldStatisticsService
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

    Task<RebuildFieldStatisticResponse> RebuildForWorkPeriodAsync(
        RebuildFieldStatisticRequest req,
        string? actorUserId,
        CancellationToken ct = default);

    Task<FieldStatisticSummaryResponse> SearchSummaryAsync(
        FieldStatisticSummaryRequest req,
        CancellationToken ct = default);

    Task<FieldTextConcatResponse> SearchTextConcatAsync(
        FieldTextConcatRequest req,
        CancellationToken ct = default);

    Task<FieldTextConcatExportFile> ExportTextConcatCsvAsync(
        FieldTextConcatRequest req,
        CancellationToken ct = default);
}
