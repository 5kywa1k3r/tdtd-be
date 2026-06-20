using tdtd_be.Models;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.Common;
using tdtd_be.DTOs.WorkAssignments.SummaryTokens;

namespace tdtd_be.Services.WorkAssignments.SummaryTokens;

public interface IWorkSummaryTokenService
{
    Task<WorkSummaryTokenGrantResponse> GrantAsync(
        WorkSummaryTokenGrantRequest request,
        MeResponse issuer,
        CancellationToken ct);

    Task<WorkSummaryTokenQuotaResponse> GetQuotaAsync(
        string ownerUserId,
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

    Task MarkFailedAsync(
        string ledgerId,
        string actorUserId,
        string error,
        CancellationToken ct);
}

public sealed record WorkSummaryTokenConsumeResult(
    string LedgerId,
    int Units,
    int MonthlyQuota,
    int UsedBefore,
    int UsedAfter,
    bool IsFree);
