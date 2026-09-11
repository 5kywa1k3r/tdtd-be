using tdtd_be.DTOs.Auth;

namespace tdtd_be.Services.StatisticsReconciliation.Recheck;

/// <summary>
/// Crash-safe order: append/replay successor verdict happens upstream, current
/// is atomically swapped by the trusted promoter, review supersessions are
/// appended/replayed, and only then is the durable retry marker cleared.
/// </summary>
internal sealed class StatisticReconciliationRecheckCompletionCoordinator(
    IStatisticReconciliationRecheckCurrentPromoter promoter,
    IStatisticReconciliationRecheckReviewSupersessionAppender reviews)
{
    internal async Task<StatisticReconciliationTrustedRecheckPromotionResult>
        CompleteAsync(
            StatisticReconciliationTrustedRecheckPromotionCommand command,
            MeResponse actor,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        var promoted = await promoter.PromoteRecheckAsync(command, actor, ct);
        await reviews.AppendOrReplayAsync(
            command.ReconciliationId,
            command.BaseVerdictGenerationId,
            command.SuccessorVerdictGenerationId,
            command.ReviewSupersessionCommandId,
            actor,
            ct);
        await promoter.CompleteReviewSupersessionAsync(
            promoted.ReconciliationId,
            promoted.MarkerId,
            promoted.StateRevision,
            promoted.StateHash,
            actor,
            ct);
        return promoted;
    }
}
