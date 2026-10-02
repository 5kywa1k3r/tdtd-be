using System.Text.Json;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;

namespace tdtd_be.Services.WorkAssignments.AdvancedSummary;

public interface IWorkAssignmentAdvancedSummaryConfigService
{
    Task<AdvancedNativeComparisonResponse> CompareNativeSummaryAsync(AdvancedNativeComparisonRequest request, CancellationToken ct);
    Task<AdvancedNativeComparisonRecordResponse> RecordNativeComparisonAsync(AdvancedNativeComparisonRequest request, CancellationToken ct);
    Task<AdvancedNativeComparisonRecordResponse> ReadNativeComparisonRecordAsync(string id, string hash, CancellationToken ct);
    Task<AdvancedNativeComparisonRecordDiagnostic> DiagnoseNativeComparisonRecordAsync(string id, string hash, CancellationToken ct);
    Task<AdvancedNativeComparisonRecordPage> ListNativeComparisonRecordsAsync(string scopeId, string formId, string? after, int limit, CancellationToken ct);
    Task<AdvancedNativeSummaryResponse> GetNativeSummaryAsync(AdvancedNativeSummaryRequest request, CancellationToken ct);
    Task<AdvancedNativeRefreshResponse> QueueNativeRefreshAsync(AdvancedNativeSummaryRequest request, CancellationToken ct);
    Task<AdvancedNativeRefreshResponse> ReadNativeRefreshByCommandAsync(string commandId, CancellationToken ct);
    Task<AdvancedNativeRefreshResponse> ReadNativeRefreshAsync(string refreshId, CancellationToken ct);
    Task<AdvancedNativeRefreshResponse> RetryNativeRefreshAsync(string refreshId, CancellationToken ct);
    Task RefreshNativeSnapshotJobAsync(string refreshId, CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryConfigReadback> GetP8ConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryConfigVersionsResult>
        ListP8ConfigVersionsAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            string sectionId,
            CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryConfigVersionDto>
        GetP8ConfigVersionAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            string sectionId,
            int versionNo,
            CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryConfigReadback> PutP8ConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        JsonElement body,
        CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryConfigReadback> LockP8ConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        JsonElement body,
        CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryConfigReadback>
        CreateNextP8DraftAsync(
            string assignmentId,
            string dynamicFormTemplateId,
            string sectionId,
            JsonElement body,
            CancellationToken ct);

    Task<WorkAssignmentAdvancedSummaryConfigReadback> ArchiveP8ConfigAsync(
        string assignmentId,
        string dynamicFormTemplateId,
        string sectionId,
        JsonElement body,
        CancellationToken ct);

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
