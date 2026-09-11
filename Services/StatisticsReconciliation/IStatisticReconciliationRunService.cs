using System.Text.Json;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed record StatisticReconciliationCreateResult(
    bool IsReplay,
    StatisticReconciliationSummaryResponse Run);

public interface IStatisticReconciliationRunService
{
    Task<StatisticReconciliationCapturePlanPreflightResponse>
        PreflightCapturePlanAsync(
            string workId,
            string scopeAssignmentId,
            JsonElement body,
            MeResponse actor,
            CancellationToken ct = default);


    Task<StatisticReconciliationCreateResult> CreateAsync(
        string workId,
        string scopeAssignmentId,
        JsonElement body,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationPageResponse> ListAsync(
        string workId,
        string scopeAssignmentId,
        int page,
        int pageSize,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationSummaryResponse> GetSummaryAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationDetailResponse> GetDetailAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationExpectedBusinessSummaryResponse>
        GetExpectedBusinessSummaryAsync(
            string workId,
            string scopeAssignmentId,
            string reconciliationId,
            string? generationId,
            MeResponse actor,
            CancellationToken ct = default);

    Task<StatisticReconciliationExpectedProvenancePageResponse>
        GetExpectedProvenanceAsync(
            string workId,
            string scopeAssignmentId,
            string reconciliationId,
            string? generationId,
            string? cursor,
            string? pageSize,
            MeResponse actor,
            CancellationToken ct = default);


    Task<StatisticReconciliationSummaryResponse> CancelAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        JsonElement body,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationWorkerLeaseResponse?> ClaimAsync(
        StatisticReconciliationWorkerClaimRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationDetailResponse> HeartbeatAsync(
        string reconciliationId,
        StatisticReconciliationWorkerFenceRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationDetailResponse> FailAsync(
        string reconciliationId,
        StatisticReconciliationWorkerFailRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationDetailResponse> PublishPendingAsync(
        string reconciliationId,
        StatisticReconciliationPendingPublishRequest request,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationBeginRecheckResponse> BeginRecheckAsync(
        string workId,
        string scopeAssignmentId,
        string reconciliationId,
        Stream body,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationRemediationEvidenceResponse>
        AuthorizeRecheckRemediationAsync(
            string reconciliationId,
            MeResponse actor,
            CancellationToken ct = default);

    Task<StatisticReconciliationWorkerLeaseResponse> ClaimRecheckAsync(
        string reconciliationId,
        Stream body,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationDetailResponse> HeartbeatRecheckAsync(
        string reconciliationId,
        Stream body,
        MeResponse actor,
        CancellationToken ct = default);

    Task<StatisticReconciliationDetailResponse> FailRecheckAsync(
        string reconciliationId,
        Stream body,
        MeResponse actor,
        CancellationToken ct = default);
}
