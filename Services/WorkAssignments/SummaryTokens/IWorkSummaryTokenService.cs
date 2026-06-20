using tdtd_be.Models;

namespace tdtd_be.Services.WorkAssignments.SummaryTokens;

public interface IWorkSummaryTokenService
{
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
