using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.StatisticsConfiguration;
using tdtd_be.DTOs.WorkAssignments.SummaryTokens;

namespace tdtd_be.Services.WorkAssignments.SummaryTokens;

public interface IWorkSummaryTokenService
{
    Task<WorkSummaryTokenGrantResponse> GrantAsync(
        WorkSummaryTokenGrantRequest request,
        MeResponse issuer,
        CancellationToken ct);

    Task<WorkSummaryTokenGrantResponse> GrantP8Async(
        string ownerUnitId,
        StatConfigMutationEnvelope<WorkSummaryTokenGrantP8Payload> request,
        MeResponse issuer,
        CancellationToken ct);

    Task<WorkSummaryTokenCompensationResponse> CompensateP8Async(
        string ledgerId,
        StatConfigMutationEnvelope<WorkSummaryTokenCompensationP8Payload> request,
        MeResponse actor,
        CancellationToken ct);

    Task<WorkSummaryTokenQuotaResponse> GetQuotaAsync(
        string ownerUnitId,
        string? tokenKind,
        string? periodMonthKey,
        MeResponse actor,
        CancellationToken ct);

    Task<PagedResult<WorkSummaryTokenLedgerRow>> SearchLedgerAsync(
        WorkSummaryTokenLedgerSearchRequest request,
        MeResponse actor,
        CancellationToken ct);

    Task<WorkSummaryTokenConsumeResult> ConsumeAdvancedConfigLockAsync(
        WorkAssignmentAdvancedSummaryConfig config,
        long existingLockedConfigCount,
        string actorUserId,
        string? requestTokenId,
        CancellationToken ct);

    Task<WorkSummaryTokenConsumeResult> ConsumeAdvancedConfigLockP8Async(
        IClientSessionHandle session,
        WorkAssignmentAdvancedSummaryConfig config,
        string ownerUnitId,
        bool isFree,
        string requestTokenId,
        string actorUserId,
        CancellationToken ct);

    Task<WorkSummaryTokenConsumeResult> ConsumeAdvancedBroadHistoricalBuildP9Async(
        WorkAssignmentAdvancedSummaryConfig config,
        string ownerUnitId,
        string commandId,
        string grain,
        string grainKey,
        string actorUserId,
        CancellationToken ct);

    Task MarkFailedAsync(
        string ledgerId,
        string actorUserId,
        string error,
        CancellationToken ct);
}

public sealed record WorkSummaryTokenConsumeResult(
    string LedgerId,
    string CommandReceiptId,
    string PoolId,
    string OwnerUnitId,
    string TokenKind,
    string PeriodMonthKey,
    int Units,
    int BaseMonthlyQuota,
    int GrantedUnits,
    int MonthlyQuota,
    int UsedBefore,
    int UsedAfter,
    long PoolRevision,
    string PoolHash,
    bool IsFree);
