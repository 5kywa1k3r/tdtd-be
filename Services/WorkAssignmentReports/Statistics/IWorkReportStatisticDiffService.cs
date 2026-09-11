using System.Text.Json;
using tdtd_be.DTOs.Statistics;

namespace tdtd_be.Services.WorkAssignmentReports.Statistics;

public interface IWorkReportStatisticDiffService
{
    Task<WorkReportStatisticDiffConfigReadback> GetP8ConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        CancellationToken ct);

    Task<WorkReportStatisticDiffConfigVersionsResult>
        ListP8ConfigVersionsAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            CancellationToken ct);

    Task<WorkReportStatisticDiffConfigReadback>
        GetP8ConfigVersionAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            int versionNo,
            CancellationToken ct);

    Task<WorkReportStatisticDiffConfigReadback> PutP8ConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        JsonElement body,
        CancellationToken ct);

    Task<WorkReportStatisticDiffConfigReadback> LockP8ConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        JsonElement body,
        CancellationToken ct);

    Task<WorkReportStatisticDiffConfigReadback>
        CreateNextP8DraftAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            JsonElement body,
            CancellationToken ct);

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

    Task<P9StatisticDiffResultResponse> RunP9Async(
        string assignmentId,
        string dynamicFormTemplateId,
        P9StatisticDiffRunRequest request,
        CancellationToken ct = default);

    Task<P9StatisticDiffResultResponse> GetP9ResultAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string resultId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}
