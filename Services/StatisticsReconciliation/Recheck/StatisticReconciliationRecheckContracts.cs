using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.Recheck;

public static class StatisticReconciliationRecheckTerminalStatuses
{
    public const string Matched = "MATCHED";
    public const string Mismatched = "MISMATCHED";
    public const string Stale = "STALE";
    public const string Failed = "FAILED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(
        [Matched, Mismatched, Stale, Failed], StringComparer.Ordinal);
}

public static class StatisticReconciliationRecheckFailureCodes
{
    public const string Invalid = "P10_RECHECK_INVALID";
    public const string CasStale = "P10_RECHECK_CAS_STALE";
    public const string ReplayMismatch = "P10_RECHECK_REPLAY_MISMATCH";
    public const string AlreadyActive = "P10_RECHECK_ALREADY_ACTIVE";
    public const string FenceStale = "P10_RECHECK_FENCE_STALE";
    public const string BaseCurrentChanged = "P10_RECHECK_BASE_CURRENT_CHANGED";
    public const string SuccessorNotDistinct =
        "P10_RECHECK_SUCCESSOR_NOT_DISTINCT";
    public const string PhaseInvalid = "P10_RECHECK_PHASE_INVALID";
}

public sealed class StatisticReconciliationRecheckException : Exception
{
    public StatisticReconciliationRecheckException(string code, string detail)
        : base($"{code}:{detail}") => Code = code;

    public string Code { get; }
}

/// <summary>
/// The public begin command intentionally contains only idempotency and CAS
/// fields. Expected/actual evidence, delta hashes and verdicts are resolved by
/// trusted owners and never accepted from the caller.
/// </summary>
public sealed record StatisticReconciliationBeginRecheckCommand(
    string CommandId,
    long ExpectedStateRevision,
    string ExpectedStateHash);

public sealed record StatisticReconciliationBeginRecheckReceipt(
    bool Replayed,
    string ReconciliationId,
    string MarkerId,
    long AcceptedStateRevision,
    string AcceptedStatus);

public sealed record StatisticReconciliationRecheckWorkerLease(
    string ReconciliationId,
    string MarkerId,
    string WorkerId,
    string ClaimToken,
    DateTime LeaseUntilUtc,
    long StateRevision);

internal sealed record StatisticReconciliationTrustedRecheckPublishCommand(
    string ReconciliationId,
    string MarkerId,
    string WorkerId,
    string ClaimToken,
    string SuccessorGenerationId,
    string SuccessorGenerationHash);

internal sealed record StatisticReconciliationTrustedRecheckPromotionCommand(
    string ReconciliationId,
    string MarkerId,
    string BaseCurrentGenerationId,
    string BaseCurrentGenerationHash,
    string BaseVerdictGenerationId,
    string BaseVerdictGenerationHash,
    string SuccessorGenerationId,
    string SuccessorGenerationHash,
    string TerminalStatus,
    string SuccessorVerdictGenerationId,
    string SuccessorVerdictGenerationHash,
    long ExpectedStateRevision,
    string ExpectedStateHash,
    long ExpectedGenerationPublishRevision,
    string ReviewSupersessionCommandId);

internal sealed record StatisticReconciliationTrustedRecheckPromotionResult(
    string ReconciliationId,
    string MarkerId,
    string PreviousCurrentGenerationId,
    string CurrentGenerationId,
    string CurrentGenerationHash,
    string TerminalStatus,
    long StateRevision,
    string StateHash,
    bool Replayed);

internal interface IStatisticReconciliationRecheckCurrentPromoter
{
    Task<StatisticReconciliationTrustedRecheckPromotionResult>
        PromoteRecheckAsync(
            StatisticReconciliationTrustedRecheckPromotionCommand command,
            MeResponse actor,
            CancellationToken ct = default);

    Task CompleteReviewSupersessionAsync(
        string reconciliationId,
        string markerId,
        long expectedStateRevision,
        string expectedStateHash,
        MeResponse actor,
        CancellationToken ct = default);
}

internal interface IStatisticReconciliationRecheckReviewSupersessionAppender
{
    /// <summary>
    /// Appends or exactly replays all review supersession markers for the old
    /// generation. Implementations must reject a same-command/different-body
    /// collision.
    /// </summary>
    Task AppendOrReplayAsync(
        string reconciliationId,
        string previousVerdictGenerationId,
        string successorVerdictGenerationId,
        string commandId,
        MeResponse actor,
        CancellationToken ct = default);
}
