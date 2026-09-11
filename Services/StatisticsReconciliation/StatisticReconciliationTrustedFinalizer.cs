using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation;

/// <summary>
/// A server-only finalize command. TrustedVerdict is produced by the typed
/// comparison owner and is deliberately absent from every HTTP DTO.
/// </summary>
public sealed record StatisticReconciliationTrustedFinalizeCommand(
    string ReconciliationId,
    long ExpectedStateRevision,
    string ExpectedStateHash,
    long ExpectedGenerationPublishRevision,
    StatisticReconciliationFinalVerdictRequest TrustedVerdict,
    StatisticReconciliationTrustedP8ConfigurationIdentity?
        CurrentP8Configuration = null);

public sealed record StatisticReconciliationTrustedFinalizeResult(
    bool IsReplay,
    string ReconciliationId,
    string Status,
    long StateRevision,
    string StateHash,
    long GenerationPublishRevision,
    string CurrentGenerationId,
    string CurrentGenerationHash,
    StatisticReconciliationReview Verdict);

public interface IStatisticReconciliationTrustedFinalizer
{
    Task<StatisticReconciliationTrustedFinalizeResult> FinalizePendingAsync(
        StatisticReconciliationTrustedFinalizeCommand command,
        MeResponse systemActor,
        CancellationToken ct = default);
}

/// <summary>
/// Publishes one validated FINAL_VERDICT and promotes that verdict's already
/// committed pending ACTUAL generation by an exact one-winner Mongo CAS.
/// This owner has no controller and never accepts client semantic material.
/// </summary>
public sealed class StatisticReconciliationTrustedFinalizer(
    MongoDbContext context,
    IStatisticReconciliationCandidateActivation activation,
    IAppTimeService time,
    StatisticReconciliationFinalVerdictPublisher verdictPublisher,
    IStatisticReconciliationReviewBackend verdictBackend)
    : IStatisticReconciliationTrustedFinalizer
{
    public async Task<StatisticReconciliationTrustedFinalizeResult>
        FinalizePendingAsync(
            StatisticReconciliationTrustedFinalizeCommand command,
            MeResponse systemActor,
            CancellationToken ct = default)
    {
        global::tdtd_be.Services.StatisticsReconciliation.Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(systemActor);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.TrustedVerdict);

        var binding = activation.RequireFoundation(
            StatisticReconciliationCapabilities.ExpectedActualDelta,
            StatisticReconciliationRouteRegistry.WorkerFinalize);
        if (!ObjectId.TryParse(command.ReconciliationId, out _) ||
            command.ExpectedStateRevision < 1 ||
            command.ExpectedGenerationPublishRevision < 1 ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                command.ExpectedStateHash))
        {
            throw NotFound();
        }

        var run = await context.StatisticReconciliationRuns
            .Find(value => value.Id == command.ReconciliationId &&
                           value.CandidateChainId == binding.ChainId &&
                           !value.IsDeleted)
            .FirstOrDefaultAsync(ct) ?? throw NotFound();
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);

        var request = command.TrustedVerdict;
        if (request.ReconciliationId != run.Id)
            throw Conflict("FINAL_VERDICT_RECONCILIATION_MISMATCH");

        if (run.CurrentGenerationId is not null ||
            run.CurrentGenerationHash is not null)
        {
            return await ResolveReplayAsync(run, request, ct);
        }

        var now = MongoUtc(time.UtcNow);
        if (run.DeadlineAtUtc <= now)
            throw Timeout();
        if (run.Status != StatisticReconciliationRunStatuses.Queued ||
            run.LeaseOwnerId is not null ||
            run.ClaimToken is not null ||
            run.LeaseUntilUtc.HasValue ||
            run.LastHeartbeatAtUtc.HasValue ||
            run.PendingGenerationId is null ||
            run.PendingGenerationHash is null ||
            !run.PendingGenerationPublishedAtUtc.HasValue ||
            run.StateRevision != command.ExpectedStateRevision ||
            run.StateHash != command.ExpectedStateHash ||
            run.GenerationPublishRevision !=
                command.ExpectedGenerationPublishRevision ||
            request.ActualGenerationId != run.PendingGenerationId ||
            request.ActualGenerationSha256 != run.PendingGenerationHash)
        {
            throw Conflict("PENDING_FINALIZE_CAS_MISMATCH");
        }

        await RequireCommittedPendingAsync(run, ct);

        StatisticReconciliationReview verdict;
        try
        {
            verdict = await verdictPublisher.PublishAsync(request, now, ct);
            StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);
        }
        catch (StatisticReconciliationFinalVerdictException exception)
        {
            throw Conflict(exception.Code);
        }

        if (verdict.ReconciliationId != run.Id ||
            verdict.ActualGenerationId != run.PendingGenerationId ||
            verdict.ActualGenerationSha256 != run.PendingGenerationHash)
        {
            throw Conflict("FINAL_VERDICT_PENDING_BINDING_MISMATCH");
        }

        await RequireSingleVerdictForActualAsync(run.Id,
            run.PendingGenerationId, run.PendingGenerationHash,
            verdict.VerdictGenerationId, ct);

        var status = FinalStatus(verdict);
        var failedAtUtc = status == StatisticReconciliationRunStatuses.Failed
            ? now
            : (DateTime?)null;
        var diagnosticCode =
            status == StatisticReconciliationRunStatuses.Failed
                ? "P10_RECONCILIATION_FINAL_VERDICT_FAILED"
                : null;
        var nextStateRevision = checked(run.StateRevision + 1);
        var stateHash =
            StatisticReconciliationRunService.BuildTrustedFinalizedStateHash(
                run,
                status,
                nextStateRevision,
                run.PendingGenerationId,
                run.PendingGenerationHash,
                diagnosticCode,
                failedAtUtc);

        var fb = Builders<StatisticReconciliationRun>.Filter;
        var updated =
            await context.StatisticReconciliationRuns.FindOneAndUpdateAsync(
                fb.Eq(value => value.Id, run.Id) &
                fb.Eq(value => value.CandidateChainId, binding.ChainId) &
                fb.Eq(value => value.Status,
                    StatisticReconciliationRunStatuses.Queued) &
                fb.Eq(value => value.StateRevision, run.StateRevision) &
                fb.Eq(value => value.StateHash, run.StateHash) &
                fb.Eq(value => value.GenerationPublishRevision,
                    run.GenerationPublishRevision) &
                fb.Eq(value => value.PendingGenerationId,
                    run.PendingGenerationId) &
                fb.Eq(value => value.PendingGenerationHash,
                    run.PendingGenerationHash) &
                fb.Eq(value => value.PendingGenerationPublishedAtUtc,
                    run.PendingGenerationPublishedAtUtc) &
                fb.Eq(value => value.CurrentGenerationId, null) &
                fb.Eq(value => value.CurrentGenerationHash, null) &
                fb.Eq(value => value.LeaseOwnerId, null) &
                fb.Eq(value => value.ClaimToken, null) &
                fb.Eq(value => value.LeaseUntilUtc, null) &
                fb.Eq(value => value.LastHeartbeatAtUtc, null) &
                fb.Gt(value => value.DeadlineAtUtc, now) &
                fb.Eq(value => value.IsDeleted, false),
                Builders<StatisticReconciliationRun>.Update
                    .Set(value => value.Status, status)
                    .Set(value => value.StateRevision, nextStateRevision)
                    .Set(value => value.StateHash, stateHash)
                    .Set(value => value.NextRetryAtUtc, null)
                    .Set(value => value.DiagnosticCode, diagnosticCode)
                    .Set(value => value.PendingGenerationId, null)
                    .Set(value => value.PendingGenerationHash, null)
                    .Set(value => value.PendingGenerationPublishedAtUtc, null)
                    .Set(value => value.CurrentGenerationId,
                        run.PendingGenerationId)
                    .Set(value => value.CurrentGenerationHash,
                        run.PendingGenerationHash)
                    .Set(value => value.FailedAtUtc, failedAtUtc)
                    .Set(value => value.LatestWriterUserId, systemActor.Id)
                    .Set(value => value.UpdatedAtUtc, now)
                    .Set(value => value.UpdatedByUserId, systemActor.Id),
                new FindOneAndUpdateOptions<StatisticReconciliationRun>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);

        if (updated is null)
        {
            var observed = await context.StatisticReconciliationRuns
                .Find(value => value.Id == run.Id &&
                               value.CandidateChainId == binding.ChainId &&
                               !value.IsDeleted)
                .FirstOrDefaultAsync(ct);
            if (observed is null)
                throw Conflict("FINALIZE_TARGET_DISAPPEARED");
            StatisticReconciliationRunService
                .RequireActualCaptureReadIntegrity(observed);
            if (observed.CurrentGenerationId == request.ActualGenerationId &&
                observed.CurrentGenerationHash ==
                    request.ActualGenerationSha256 &&
                observed.PendingGenerationId is null &&
                observed.PendingGenerationHash is null &&
                !observed.PendingGenerationPublishedAtUtc.HasValue)
            {
                return await ResolveReplayAsync(observed, request, ct);
            }

            throw Conflict("PENDING_FINALIZE_CAS_LOST");
        }

        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            updated);
        return Result(false, updated, verdict);
    }

    private async Task RequireCommittedPendingAsync(
        StatisticReconciliationRun run,
        CancellationToken ct)
    {
        StatisticReconciliationActualAppendResult? committed;
        try
        {
            committed = await StatisticReconciliationActualGenerationPublisher
                .ReadCompleteFromBackendAsync(
                    new StatisticReconciliationActualObservationMongoBackend(
                        context),
                    run.Id,
                    run.PendingGenerationId!,
                    ct);
        }
        catch (StatisticReconciliationActualObservationException)
        {
            throw Conflict("ACTUAL_GENERATION_COMMIT_INVALID");
        }

        if (committed is null ||
            committed.GenerationSemanticSha256 != run.PendingGenerationHash)
        {
            throw Conflict("ACTUAL_GENERATION_NOT_COMMITTED");
        }

        if (!StatisticReconciliationActualRunBindingGuard.Matches(
                run, committed.CommittedRunBinding))
        {
            throw Conflict("ACTUAL_GENERATION_RUN_BINDING_MISMATCH");
        }
    }

    private async Task<StatisticReconciliationTrustedFinalizeResult>
        ResolveReplayAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationFinalVerdictRequest request,
            CancellationToken ct)
    {
        if (run.PendingGenerationId is not null ||
            run.PendingGenerationHash is not null ||
            run.PendingGenerationPublishedAtUtc.HasValue ||
            run.CurrentGenerationId != request.ActualGenerationId ||
            run.CurrentGenerationHash != request.ActualGenerationSha256)
        {
            throw Conflict("CURRENT_GENERATION_ALREADY_FINALIZED");
        }

        StatisticReconciliationFinalVerdictDecision decision;
        try
        {
            decision = new StatisticReconciliationFinalVerdictEvaluator()
                .Evaluate(request);
        }
        catch (StatisticReconciliationFinalVerdictException exception)
        {
            throw Conflict(exception.Code);
        }

        var candidates = (await verdictBackend.ReadLineageAsync(run.Id, ct))
            .Where(value =>
                value.ActualGenerationId == run.CurrentGenerationId &&
                value.ActualGenerationSha256 == run.CurrentGenerationHash)
            .ToArray();
        if (candidates.Length != 1 ||
            !Matches(candidates[0], decision) ||
            run.Status != FinalStatus(candidates[0]))
        {
            throw Conflict("FINALIZE_REPLAY_MISMATCH");
        }

        StatisticReconciliationFinalVerdictPublisher.ValidateStored(
            candidates[0]);
        return Result(true, run, candidates[0]);
    }

    private async Task RequireSingleVerdictForActualAsync(
        string reconciliationId,
        string actualGenerationId,
        string actualGenerationHash,
        string expectedVerdictGenerationId,
        CancellationToken ct)
    {
        var candidates =
            (await verdictBackend.ReadLineageAsync(reconciliationId, ct))
            .Where(value =>
                value.ActualGenerationId == actualGenerationId &&
                value.ActualGenerationSha256 == actualGenerationHash)
            .ToArray();
        if (candidates.Length != 1 ||
            candidates[0].VerdictGenerationId !=
                expectedVerdictGenerationId)
        {
            throw Conflict("FINAL_VERDICT_ACTUAL_CARDINALITY_INVALID");
        }
    }

    private static bool Matches(
        StatisticReconciliationReview value,
        StatisticReconciliationFinalVerdictDecision decision)
        => value.ReconciliationId == decision.ReconciliationId &&
           value.ComparisonBindingSha256 ==
               decision.ComparisonBindingSha256 &&
           value.ExpectedGenerationId == decision.ExpectedGenerationId &&
           value.ExpectedGenerationSha256 ==
               decision.ExpectedGenerationSha256 &&
           value.ActualGenerationId == decision.ActualGenerationId &&
           value.ActualGenerationSha256 ==
               decision.ActualGenerationSha256 &&
           value.DeltaManifestSha256 == decision.DeltaManifestSha256 &&
           value.AuthorizationEvidenceSha256 ==
               decision.AuthorizationEvidenceSha256 &&
           value.FreshnessAssessmentSha256 ==
               decision.FreshnessAssessmentSha256 &&
           value.RootCauseClassificationSha256 ==
               decision.RootCauseClassificationSha256 &&
           value.RootCauseClass == decision.RootCauseClass &&
           value.FailureKind == decision.FailureKind &&
           value.FailureEvidenceSha256 ==
               decision.FailureEvidenceSha256 &&
           value.Verdict == decision.Verdict &&
           value.CompleteEvidence == decision.CompleteEvidence &&
           value.AllRequiredLayersZero ==
               decision.AllRequiredLayersZero &&
           value.MissingOrExtraIdentity ==
               decision.MissingOrExtraIdentity &&
           value.UnknownBlocksCloseout ==
               decision.UnknownBlocksCloseout &&
           value.CloseoutAllowed == decision.CloseoutAllowed &&
           value.Signable == decision.Signable &&
           value.SupersedesVerdictGenerationId is null;

    private static string FinalStatus(
        StatisticReconciliationReview verdict)
        => verdict.RootCauseClass ==
           StatisticReconciliationRootCauseClasses.Freshness
            ? StatisticReconciliationRunStatuses.Stale
            : verdict.Verdict switch
            {
                StatisticReconciliationFinalVerdicts.Matched =>
                    StatisticReconciliationRunStatuses.Matched,
                StatisticReconciliationFinalVerdicts.Mismatched =>
                    StatisticReconciliationRunStatuses.Mismatched,
                _ => StatisticReconciliationRunStatuses.Failed
            };

    private static StatisticReconciliationTrustedFinalizeResult Result(
        bool isReplay,
        StatisticReconciliationRun run,
        StatisticReconciliationReview verdict)
        => new(
            isReplay,
            run.Id,
            run.Status,
            run.StateRevision,
            run.StateHash,
            run.GenerationPublishRevision,
            run.CurrentGenerationId!,
            run.CurrentGenerationHash!,
            verdict);

    private static DateTime MongoUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
    }

    private static AppException Conflict(string reason)
        => new(
            AppErrorCode.STAT_RECONCILIATION_JOB_CONFLICT,
            new { reason, writes = 0 });

    private static AppException Timeout()
        => new(
            AppErrorCode.STAT_RECONCILIATION_JOB_TIMEOUT,
            new { reason = "P10_RECONCILIATION_JOB_TIMEOUT", writes = 0 });

    private static AppException NotFound()
        => new(
            AppErrorCode.STAT_RECONCILIATION_NOT_FOUND,
            new { writes = 0 });
}