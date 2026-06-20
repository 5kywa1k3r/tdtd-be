using tdtd_be.DTOs.WorkAssignments.BasicSummary;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

public interface IWorkAssignmentBasicSummaryService
{
    Task<WorkAssignmentBasicSummaryConfigDto?> GetConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string actorUserId,
        CancellationToken ct);

    Task<WorkAssignmentBasicSummaryConfigDto> SaveConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        SaveWorkAssignmentBasicSummaryConfigRequest req,
        string actorUserId,
        CancellationToken ct);

    Task<WorkAssignmentBasicSummaryResponse> GetSummaryAsync(
        WorkAssignmentBasicSummaryRequest req,
        string actorUserId,
        CancellationToken ct);

    Task RefreshSnapshotJobAsync(
        string snapshotId,
        WorkAssignmentBasicSummaryRequest req,
        string actorUserId,
        CancellationToken ct);
}
