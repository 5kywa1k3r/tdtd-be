using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;

namespace tdtd_be.Services.StatisticsReconciliation.Production;

public interface IStatisticReconciliationProductionWorker
{
    Task<int> ProcessPendingAsync(
        int maxJobs,
        CancellationToken cancellationToken = default);
}

internal enum StatisticReconciliationProductionRecoveryState
{
    None,
    Completed
}

internal sealed record StatisticReconciliationProductionLease(
    string ReconciliationId,
    string WorkerId,
    string ClaimToken,
    DateTime LeaseUntilUtc,
    bool IsRecheck);

internal interface IStatisticReconciliationProductionRuntime
{
    Task<StatisticReconciliationProductionRecoveryState>
        RecoverOneAsync(CancellationToken cancellationToken);

    Task<StatisticReconciliationProductionLease?> ClaimInitialAsync(
        CancellationToken cancellationToken);

    Task<StatisticReconciliationProductionLease?> ClaimRecheckAsync(
        CancellationToken cancellationToken);

    Task CaptureAsync(
        StatisticReconciliationProductionLease lease,
        CancellationToken cancellationToken);

    Task HeartbeatAsync(
        StatisticReconciliationProductionLease lease,
        CancellationToken cancellationToken);

    Task FailTransientAsync(
        StatisticReconciliationProductionLease lease,
        string failureCode,
        CancellationToken cancellationToken);
}

/// <summary>
/// Registered production owner for initial and recheck reconciliation work.
/// All business transitions are delegated to internal services; this owner
/// does not issue HTTP requests or write P5-P9 stores.
/// </summary>
internal sealed class StatisticReconciliationProductionWorker(
    IStatisticReconciliationProductionRuntime runtime,
    IOptions<StatisticReconciliationProductionWorkerOptions> configured,
    ILogger<StatisticReconciliationProductionWorker> logger)
    : IStatisticReconciliationProductionWorker
{
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(
        Math.Clamp(configured.Value.HeartbeatSeconds, 1, 120));

    internal StatisticReconciliationProductionWorker(
        IStatisticReconciliationProductionRuntime runtime,
        TimeSpan heartbeatInterval,
        ILogger<StatisticReconciliationProductionWorker> logger)
        : this(
            runtime,
            Microsoft.Extensions.Options.Options.Create(new StatisticReconciliationProductionWorkerOptions
            {
                HeartbeatSeconds = Math.Max(
                    1,
                    (int)Math.Ceiling(heartbeatInterval.TotalSeconds))
            }),
            logger)
    {
        _heartbeatInterval = heartbeatInterval > TimeSpan.Zero
            ? heartbeatInterval
            : throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
    }

    public async Task<int> ProcessPendingAsync(
        int maxJobs,
        CancellationToken cancellationToken = default)
    {
        if (maxJobs is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(maxJobs));

        var processed = 0;
        while (processed < maxJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var recovery = await runtime.RecoverOneAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (recovery ==
                    StatisticReconciliationProductionRecoveryState.Completed)
                {
                    processed++;
                    continue;
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A committed pending generation is the highest-priority
                // crash window. Leave it immutable for the next recurring
                // attempt instead of claiming unrelated work.
                logger.LogError(
                    exception,
                    "P10 production worker could not recover a pending generation.");
                break;
            }

            var lease = await runtime.ClaimRecheckAsync(cancellationToken)
                .ConfigureAwait(false) ??
                await runtime.ClaimInitialAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (lease is null)
                break;

            await ProcessLeaseAsync(lease, cancellationToken)
                .ConfigureAwait(false);
            processed++;
        }
        return processed;
    }

    private async Task ProcessLeaseAsync(
        StatisticReconciliationProductionLease lease,
        CancellationToken cancellationToken)
    {
        using var heartbeatCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        var heartbeat = KeepLeaseAliveAsync(
            lease,
            heartbeatCancellation.Token);
        Exception? captureFailure = null;
        try
        {
            await runtime.CaptureAsync(lease, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            captureFailure = new OperationCanceledException(
                "P10 production capture was cancelled.",
                cancellationToken);
        }
        catch (Exception exception)
        {
            captureFailure = exception;
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (heartbeatCancellation.IsCancellationRequested)
            {
                // Expected after capture reaches a boundary.
            }
            catch (Exception heartbeatFailure)
            {
                captureFailure ??= heartbeatFailure;
            }
        }

        if (captureFailure is null)
            return;

        try
        {
            await runtime.FailTransientAsync(
                    lease,
                    "P10_PRODUCTION_CAPTURE_FAILED",
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception fenceFailure)
        {
            // PublishPending intentionally clears the lease before trusted
            // finalize. A failure in that crash window is recovered from the
            // committed pending generation on the next recurring pass.
            logger.LogWarning(
                fenceFailure,
                "P10 production capture failure could not requeue {ReconciliationId}; pending recovery may own the next transition.",
                lease.ReconciliationId);
        }

        logger.LogError(
            captureFailure,
            "P10 production capture failed for {ReconciliationId}; recheck={IsRecheck}.",
            lease.ReconciliationId,
            lease.IsRecheck);
        if (captureFailure is OperationCanceledException &&
            cancellationToken.IsCancellationRequested)
            throw captureFailure;
    }

    private async Task KeepLeaseAliveAsync(
        StatisticReconciliationProductionLease lease,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_heartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            await runtime.HeartbeatAsync(lease, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

internal sealed class StatisticReconciliationProductionRuntime(
    MongoDbContext context,
    IAppTimeService time,
    IStatisticReconciliationRunService runService,
    IStatisticReconciliationActualClaimedCaptureWorker captureWorker,
    IStatisticReconciliationTrustedVerdictPipeline verdictPipeline,
    IStatisticReconciliationInternalWorkerIdentity identity,
    ILogger<StatisticReconciliationProductionRuntime> logger)
    : IStatisticReconciliationProductionRuntime
{
    // Internal stream requests obey the same strict camelCase contract as HTTP.
    private static readonly JsonSerializerOptions RecheckBodyJson = new(JsonSerializerDefaults.Web);

    public async Task<StatisticReconciliationProductionRecoveryState>
        RecoverOneAsync(CancellationToken cancellationToken)
    {
        var fb = Builders<StatisticReconciliationRun>.Filter;
        var pending =
            fb.Eq(value => value.Status,
                StatisticReconciliationRunStatuses.Queued) &
            fb.Ne(value => value.PendingGenerationId, null) &
            fb.Ne(value => value.PendingGenerationHash, null) &
            fb.Ne(value => value.PendingGenerationPublishedAtUtc, null) &
            fb.Eq(value => value.LeaseOwnerId, null) &
            fb.Eq(value => value.ClaimToken, null) &
            fb.Eq(value => value.LeaseUntilUtc, null) &
            fb.Eq(value => value.LastHeartbeatAtUtc, null);
        var promotedRecheck =
            fb.Eq("recheck.phase",
                StatisticReconciliationRecheckPhases
                    .ReviewSupersessionPending) &
            fb.Ne(value => value.CurrentGenerationId, null) &
            fb.Ne(value => value.CurrentGenerationHash, null) &
            fb.Ne(value => value.CurrentRecheckFinalizeReceipt, null) &
            fb.Eq(value => value.PendingGenerationId, null) &
            fb.Eq(value => value.PendingGenerationHash, null) &
            fb.Eq(value => value.PendingGenerationPublishedAtUtc, null);
        var run = await context.StatisticReconciliationRuns
            .Find((pending | promotedRecheck) &
                  fb.Eq(value => value.IsDeleted, false))
            .SortBy(value => value.UpdatedAtUtc)
            .ThenBy(value => value.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
            return StatisticReconciliationProductionRecoveryState.None;

        StatisticReconciliationRunService
            .RequireActualCaptureReadIntegrity(run);
        var generationId = run.PendingGenerationId ??
            run.CurrentGenerationId!;
        var generationHash = run.PendingGenerationHash ??
            run.CurrentGenerationHash!;
        var command = StatisticReconciliationTrustedVerdictPipeline
            .CreateCommand(
                run.Id,
                generationId,
                generationHash,
                run.StateRevision,
                run.StateHash,
                run.GenerationPublishRevision);
        var result = await verdictPipeline.FinalizeAsync(
                command,
                identity.Actor,
                cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation(
            "P10 production worker recovered {ReconciliationId} to {Status}; replay={IsReplay}.",
            result.ReconciliationId,
            result.Status,
            result.IsReplay);
        return StatisticReconciliationProductionRecoveryState.Completed;
    }

    public async Task<StatisticReconciliationProductionLease?>
        ClaimInitialAsync(CancellationToken cancellationToken)
    {
        var claim = await runService.ClaimAsync(
                new StatisticReconciliationWorkerClaimRequest
                {
                    WorkerId = identity.WorkerId
                },
                identity.Actor,
                cancellationToken)
            .ConfigureAwait(false);
        return claim is null ? null : Lease(claim, false);
    }

    public async Task<StatisticReconciliationProductionLease?>
        ClaimRecheckAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var now = time.UtcNow;
            var fb = Builders<StatisticReconciliationRun>.Filter;
            var ready =
                fb.Eq(value => value.Status,
                    StatisticReconciliationRunStatuses.Queued) &
                fb.Eq("recheck.phase",
                    StatisticReconciliationRecheckPhases.ReadyToClaim) &
                (fb.Eq(value => value.NextRetryAtUtc, null) |
                 fb.Lte(value => value.NextRetryAtUtc, now));
            var expired =
                fb.Eq(value => value.Status,
                    StatisticReconciliationRunStatuses.Running) &
                fb.Eq("recheck.phase",
                    StatisticReconciliationRecheckPhases.CaptureRunning) &
                fb.Lte(value => value.LeaseUntilUtc, now);
            var candidateId = await context.StatisticReconciliationRuns
                .Find((ready | expired) &
                      fb.Ne(value => value.Recheck, null) &
                      fb.Eq(value => value.PendingGenerationId, null) &
                      fb.Eq(value => value.PendingGenerationHash, null) &
                      fb.Eq(value => value.IsDeleted, false))
                .SortBy(value => value.NextRetryAtUtc)
                .ThenBy(value => value.UpdatedAtUtc)
                .ThenBy(value => value.Id)
                .Project(value => value.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (candidateId is null)
                return null;

            try
            {
                await using var body = new MemoryStream(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new StatisticReconciliationRecheckClaimRequest
                        {
                            WorkerId = identity.WorkerId
                        }, RecheckBodyJson),
                    writable: false);
                var claim = await runService.ClaimRecheckAsync(
                        candidateId,
                        body,
                        identity.Actor,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Lease(claim, true);
            }
            catch (AppException exception)
                when (exception.Code is
                    AppErrorCode.STAT_RECONCILIATION_JOB_CONFLICT or
                    AppErrorCode.STAT_RECONCILIATION_NOT_FOUND)
            {
                logger.LogDebug(
                    "P10 production worker skipped raced recheck candidate {ReconciliationId}.",
                    candidateId);
            }
        }
        return null;
    }

    public async Task CaptureAsync(
        StatisticReconciliationProductionLease lease,
        CancellationToken cancellationToken)
    {
        var outcome = await captureWorker.CaptureAsync(
                lease.ReconciliationId,
                lease.WorkerId,
                lease.ClaimToken,
                identity.Actor,
                cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.Published)
            throw new InvalidOperationException(
                $"P10_CAPTURE_NOT_PUBLISHED:{outcome.Generation.StaleReason ?? outcome.Generation.CaptureState}");
    }

    public Task HeartbeatAsync(
        StatisticReconciliationProductionLease lease,
        CancellationToken cancellationToken)
        => lease.IsRecheck
            ? HeartbeatRecheckAsync(lease, cancellationToken)
            : runService.HeartbeatAsync(
            lease.ReconciliationId,
            new StatisticReconciliationWorkerFenceRequest
            {
                WorkerId = lease.WorkerId,
                ClaimToken = lease.ClaimToken
            },
            identity.Actor,
            cancellationToken);

    public Task FailTransientAsync(
        StatisticReconciliationProductionLease lease,
        string failureCode,
        CancellationToken cancellationToken)
        => lease.IsRecheck
            ? FailRecheckAsync(lease, failureCode, cancellationToken)
            : runService.FailAsync(
            lease.ReconciliationId,
            new StatisticReconciliationWorkerFailRequest
            {
                WorkerId = lease.WorkerId,
                ClaimToken = lease.ClaimToken,
                FailureCode = failureCode,
                Transient = true
            },
            identity.Actor,
            cancellationToken);

    private async Task HeartbeatRecheckAsync(
        StatisticReconciliationProductionLease lease,
        CancellationToken cancellationToken)
    {
        await using var body = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(
                new StatisticReconciliationWorkerFenceRequest
                {
                    WorkerId = lease.WorkerId,
                    ClaimToken = lease.ClaimToken
                }, RecheckBodyJson),
            writable: false);
        await runService.HeartbeatRecheckAsync(
                lease.ReconciliationId,
                body,
                identity.Actor,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task FailRecheckAsync(
        StatisticReconciliationProductionLease lease,
        string failureCode,
        CancellationToken cancellationToken)
    {
        await using var body = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(
                new StatisticReconciliationWorkerFailRequest
                {
                    WorkerId = lease.WorkerId,
                    ClaimToken = lease.ClaimToken,
                    FailureCode = failureCode,
                    Transient = true
                }, RecheckBodyJson),
            writable: false);
        await runService.FailRecheckAsync(
                lease.ReconciliationId,
                body,
                identity.Actor,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static StatisticReconciliationProductionLease Lease(
        StatisticReconciliationWorkerLeaseResponse claim,
        bool isRecheck)
        => new(
            claim.Run.ReconciliationId,
            claim.WorkerId,
            claim.ClaimToken,
            claim.LeaseUntilUtc,
            isRecheck);
}
