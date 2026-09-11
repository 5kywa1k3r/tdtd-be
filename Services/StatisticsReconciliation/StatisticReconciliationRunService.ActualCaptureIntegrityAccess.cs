using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    internal static void RequireActualCaptureReadIntegrity(
        StatisticReconciliationRun run)
        => RequireReadIntegrity(run ?? throw new ArgumentNullException(nameof(run)));

    internal static string BuildTrustedFinalizedStateHash(
        StatisticReconciliationRun run,
        string status,
        long nextStateRevision,
        string currentGenerationId,
        string currentGenerationHash,
        string? diagnosticCode,
        DateTime? failedAtUtc)
        => BuildStateHash(run.Id, StateOf(run) with
        {
            Status = status,
            StateRevision = nextStateRevision,
            NextRetryAtUtc = null,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = diagnosticCode,
            PendingGenerationId = null,
            PendingGenerationHash = null,
            PendingGenerationPublishedAtUtc = null,
            CurrentGenerationId = currentGenerationId,
            CurrentGenerationHash = currentGenerationHash,
            FailedAtUtc = failedAtUtc
        });

    internal static string BuildTrustedRecheckPromotedStateHash(
        StatisticReconciliationRun run,
        StatisticReconciliationRecheckMarker marker,
        string status,
        long nextStateRevision,
        string currentGenerationId,
        string currentGenerationHash,
        string? diagnosticCode,
        DateTime? failedAtUtc)
        => BuildStateHash(run.Id, StateOf(run) with
        {
            Status = status,
            StateRevision = nextStateRevision,
            NextRetryAtUtc = null,
            LeaseOwnerId = null,
            ClaimToken = null,
            LeaseUntilUtc = null,
            LastHeartbeatAtUtc = null,
            DiagnosticCode = diagnosticCode,
            PendingGenerationId = null,
            PendingGenerationHash = null,
            PendingGenerationPublishedAtUtc = null,
            CurrentGenerationId = currentGenerationId,
            CurrentGenerationHash = currentGenerationHash,
            Recheck = marker,
            CurrentGenerationRecheckCaptureBindingSha256 =
                marker.CaptureBinding.BindingSha256,
            FailedAtUtc = failedAtUtc
        });

    internal static string BuildTrustedReviewDecisionStateHash(
        StatisticReconciliationRun run,
        long nextStateRevision,
        long nextReviewDecisionRevision)
        => BuildStateHash(run.Id, StateOf(run) with
        {
            StateRevision = nextStateRevision,
            ReviewDecisionRevision = nextReviewDecisionRevision
        });
}