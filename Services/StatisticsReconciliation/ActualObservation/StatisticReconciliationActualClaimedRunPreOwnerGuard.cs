using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

/// <summary>
/// Single production pre-owner gate. The Mongo material owner invokes this
/// after the exact run read and before resolving any plan-referenced owner.
/// Delegates keep the run/plan integrity owners authoritative while allowing
/// focused tests to count the boundary without a Mongo server.
/// </summary>
internal static class StatisticReconciliationActualClaimedRunPreOwnerGuard
{
    internal static StatisticReconciliationActualCapturePlan Require(
        StatisticReconciliationRun run,
        string workerId,
        string claimToken,
        DateTime nowUtc,
        Action<StatisticReconciliationRun> requireReadIntegrity,
        Action<StatisticReconciliationActualCapturePlan?, string?>
            requirePlanIntegrity)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(requireReadIntegrity);
        ArgumentNullException.ThrowIfNull(requirePlanIntegrity);
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw Fail("NOW_NOT_UTC");

        requireReadIntegrity(run);
        if (!StringComparer.Ordinal.Equals(
                run.Status,
                StatisticReconciliationRunStatuses.Running) ||
            !StringComparer.Ordinal.Equals(run.LeaseOwnerId, workerId) ||
            !StringComparer.Ordinal.Equals(run.ClaimToken, claimToken) ||
            run.LeaseUntilUtc is not { Kind: DateTimeKind.Utc } leaseUntil ||
            leaseUntil <= nowUtc ||
            run.DeadlineAtUtc.Kind != DateTimeKind.Utc ||
            run.DeadlineAtUtc <= nowUtc ||
            !HasCaptureGenerationShape(run))
        {
            throw Fail("STALE_WORKER_FENCE");
        }

        var plan = run.ActualCapturePlan ?? throw Fail("CAPTURE_PLAN_REQUIRED");
        requirePlanIntegrity(plan, run.ActualCapturePlanSha256);
        return plan;
    }

    private static bool HasCaptureGenerationShape(
        StatisticReconciliationRun run)
    {
        var noPending = run.PendingGenerationId is null &&
                        run.PendingGenerationHash is null &&
                        run.PendingGenerationPublishedAtUtc is null;
        if (!noPending)
            return false;
        if (run.Recheck is null)
            return run.CurrentGenerationId is null &&
                   run.CurrentGenerationHash is null;
        return run.Recheck.Phase ==
                   StatisticReconciliationRecheckPhases.CaptureRunning &&
               StringComparer.Ordinal.Equals(run.CurrentGenerationId,
                   run.Recheck.BaseCurrentGenerationId) &&
               StringComparer.Ordinal.Equals(run.CurrentGenerationHash,
                   run.Recheck.BaseCurrentGenerationHash);
    }

    private static StatisticReconciliationActualObservationException Fail(
        string reason)
        => new($"ACTUAL_TRUSTED_MATERIAL_{reason}");
}
