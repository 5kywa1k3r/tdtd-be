using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public interface IWorkAssignmentAdvancedSummaryConfigService
{
    Task<List<WorkAssignmentAdvancedSummaryConfigDto>> ListConfigsAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        string actorUserId,
        CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryConfigDto> SaveDraftAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        SaveWorkAssignmentAdvancedSummaryDraftRequest req,
        string actorUserId,
        CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryConfigDto> LockConfigAsync(
        string configId,
        LockWorkAssignmentAdvancedSummaryConfigRequest req,
        string actorUserId,
        CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryConfigDto> RequestPreviewAsync(
        string configId,
        PreviewWorkAssignmentAdvancedSummaryConfigRequest req,
        string actorUserId,
        CancellationToken ct);

    Task RunPreviewJobAsync(
        string configId,
        string expectedConfigHash,
        string actorUserId,
        string correlationId,
        CancellationToken ct);
}
