using tdtd_be.DTOs.Statistics;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public interface IWorkReportStatisticDiffService
{
    Task<List<WorkReportStatisticDiffConfigDto>> ListConfigsAsync(
        string? workId,
        string? assignmentId,
        string? dynamicFormTemplateId,
        CancellationToken ct = default);

    Task<WorkReportStatisticDiffConfigDto> SaveConfigAsync(
        WorkReportStatisticDiffSaveRequest req,
        string? actorUserId,
        CancellationToken ct = default);

    Task DeleteConfigAsync(
        string configId,
        string? actorUserId,
        CancellationToken ct = default);

    Task<WorkReportStatisticDiffRunResponse> RunAsync(
        WorkReportStatisticDiffRunRequest req,
        CancellationToken ct = default);
}
