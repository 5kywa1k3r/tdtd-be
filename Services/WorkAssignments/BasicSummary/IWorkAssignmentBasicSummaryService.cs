using System.Text.Json;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;

namespace tdtd_be.Services.WorkAssignments.BasicSummary;

public interface IWorkAssignmentBasicSummaryService
{
    Task<WorkAssignmentBasicSummaryConfigReadback> GetP8ConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        CancellationToken ct);

    Task<WorkAssignmentBasicSummaryConfigVersionsResult>
        ListP8ConfigVersionsAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            CancellationToken ct);

    Task<WorkAssignmentBasicSummaryConfigReadback>
        GetP8ConfigVersionAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            int versionNo,
            CancellationToken ct);

    Task<WorkAssignmentBasicSummaryConfigReadback> PutP8ConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        JsonElement body,
        CancellationToken ct);

    Task<WorkAssignmentBasicSummaryConfigReadback> LockP8ConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        JsonElement body,
        CancellationToken ct);

    Task<WorkAssignmentBasicSummaryConfigReadback>
        CreateNextP8DraftAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            JsonElement body,
            CancellationToken ct);

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

    Task<WorkAssignmentBasicSummaryJobResetResultDto> ResetSnapshotJobAsync(
        string snapshotId,
        string actorUserId,
        CancellationToken ct);
}
