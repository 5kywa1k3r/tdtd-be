using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsRun;

namespace tdtd_be.Services.StatisticsRun;

public sealed record StatRunCreateResult(bool IsReplay, StatRunJobResponse Job);

public interface IStatRunService
{
    Task<StatRunCreateResult> CreateAsync(
        string capabilityId,
        StatRunCreateRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunJobResponse> GetAsync(
        string jobId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunWorkerLeaseResponse?> ClaimAsync(
        string workerId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunJobResponse> HeartbeatAsync(
        string jobId,
        StatRunWorkerFenceRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunJobResponse> CompleteFoundationAsync(
        string jobId,
        StatRunWorkerFenceRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunJobResponse> RetryAsync(
        string jobId,
        StatRunWorkerRetryRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunJobResponse> ExpireLeaseAsync(
        string jobId,
        StatRunWorkerFenceRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunJobResponse> ResetAsync(
        string jobId,
        StatRunResetRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunOperationsJobPageResponse> ListOperationsAsync(
        int limit,
        string? status,
        string? runKind,
        string? workId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunOperationsJobResponse> GetOperationsAsync(
        string jobId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunJobDiagnosticResponse> GetOperationDiagnosticsAsync(
        string jobId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunOperationMutationResponse> RetryOperationAsync(
        string jobId,
        StatRunOperationCommandRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunOperationMutationResponse> CancelOperationAsync(
        string jobId,
        StatRunOperationCommandRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunOperationMutationResponse> ResetOperationAsync(
        string jobId,
        StatRunOperationCommandRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunOperationMutationResponse> RequeueOperationAsync(
        string jobId,
        StatRunOperationCommandRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunCleanupPreviewResponse> PreviewCleanupAsync(
        StatRunCleanupPreviewRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatRunCleanupApplyResponse> ApplyCleanupAsync(
        StatRunCleanupApplyRequest request,
        MeResponse actor,
        CancellationToken ct = default);
}
