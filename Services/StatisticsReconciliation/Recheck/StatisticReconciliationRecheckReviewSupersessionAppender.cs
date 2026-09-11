using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.Recheck;

internal sealed class StatisticReconciliationRecheckReviewSupersessionAppender(
    MongoDbContext context,
    IStatisticReconciliationReviewBackend verdictBackend,
    StatisticReconciliationIndependentReviewService service,
    IAppTimeService time)
    : IStatisticReconciliationRecheckReviewSupersessionAppender
{
    public async Task AppendOrReplayAsync(
        string reconciliationId,
        string previousVerdictGenerationId,
        string successorVerdictGenerationId,
        string commandId,
        MeResponse actor,
        CancellationToken ct = default)
    {
        global::tdtd_be.Services.StatisticsReconciliation.Production.StatisticReconciliationInternalWorkerAccess.RequireWorkerOrSystemAdmin(actor);
        var run = await context.StatisticReconciliationRuns.Find(value =>
                value.Id == reconciliationId && !value.IsDeleted)
            .FirstOrDefaultAsync(ct) ?? throw Invalid("RUN_NOT_FOUND");
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        if (run.CurrentGenerationId is null ||
            run.CurrentGenerationHash is null)
            throw Invalid("CURRENT_REQUIRED");
        var verdicts = (await verdictBackend.ReadLineageAsync(run.Id, ct))
            .Where(value =>
                value.VerdictGenerationId == successorVerdictGenerationId &&
                value.SupersedesVerdictGenerationId ==
                    previousVerdictGenerationId &&
                value.ActualGenerationId == run.CurrentGenerationId &&
                value.ActualGenerationSha256 == run.CurrentGenerationHash)
            .ToArray();
        if (verdicts.Length != 1)
            throw Invalid("SUCCESSOR_VERDICT_NOT_CURRENT");
        var verdict = verdicts[0];
        StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);

        var reviewStateRevision = run.CurrentRecheckFinalizeReceipt?
            .ReviewSupersessionExpectedStateRevision ?? run.StateRevision;
        if (reviewStateRevision < 1)
            throw Invalid("REVIEW_SUPERSESSION_REVISION_INVALID");

        var permissionSnapshot =
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_RECHECK_SYSTEM_SUPERSESSION_PERMISSION_V1",
                actor.Id,
                run.WorkId,
                run.ScopeAssignmentId,
                commandId);
        var permission = new StatisticReconciliationReviewPermission(
            true, true, true, true, true, actor.Id,
            permissionSnapshot, 1, 1);
        var effective = RecheckCurrentView(run);
        var permissionColumn =
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_PERMISSION_COLUMN_V1",
                verdict.AuthorizationEvidenceSha256,
                permission.ActorId,
                permission.PermissionSnapshotSha256,
                permission.RowCountBeforeRedaction,
                permission.RowCountAfterRedaction);
        var record = new StatisticReconciliationEightColumnRecord(
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_IDENTITY_COLUMN_V1",
                run.ImmutableIdentityHash, run.ImmutableHeaderHash),
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_CONFIG_COLUMN_V1",
                effective.P8ConfigBundleHash,
                effective.ActualConfigurationBundleSha256,
                run.CandidateCatalogSemanticSha256,
                run.CandidateSchemaSemanticSha256),
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_EXPECTED_COLUMN_V1",
                verdict.ExpectedGenerationId,
                verdict.ExpectedGenerationSha256),
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_ACTUAL_COLUMN_V1",
                verdict.ActualGenerationId,
                verdict.ActualGenerationSha256),
            verdict.DeltaManifestSha256,
            verdict.FreshnessAssessmentSha256 ??
                StatisticReconciliationIndependentReviewCanonical.Hash(
                    "P10_REVIEW_FRESHNESS_NONE_V1"),
            permissionColumn,
            verdict.Verdict);
        var recordSha = StatisticReconciliationIndependentReviewCanonical
            .ReviewRecordHash(record);
        var generation = new StatisticReconciliationReviewGeneration(
            run.Id,
            verdict.VerdictGenerationId,
            verdict.VerdictGenerationSha256,
            verdict.DocumentSemanticSha256,
            record,
            recordSha,
            verdict.Verdict,
            verdict.CompleteEvidence,
            verdict.Signable && verdict.CloseoutAllowed &&
                verdict.AllRequiredLayersZero,
            verdict.UnknownBlocksCloseout ||
                verdict.RootCauseClass ==
                    StatisticReconciliationRootCauseClasses.Unknown,
            run.ActorUserId,
            run.LatestWriterUserId,
            reviewStateRevision);
        var command = new StatisticReconciliationReviewSupersessionCommand(
            commandId,
            new string('0', 64),
            run.Id,
            previousVerdictGenerationId,
            generation.GenerationId,
            generation.GenerationSha256,
            reviewStateRevision);
        command = command with
        {
            RequestSha256 =
                StatisticReconciliationIndependentReviewCanonical
                    .SupersessionCommandHash(command)
        };
        _ = await service.SupersedeApprovalsAsync(
            permission, generation, command, MongoUtc(time.UtcNow), ct);
    }

    private static StatisticReconciliationRun RecheckCurrentView(
        StatisticReconciliationRun run)
    {
        var active = run.Recheck;
        if (active?.Phase ==
            StatisticReconciliationRecheckPhases.ReviewSupersessionPending)
        {
            return StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(run);
        }
        return run.CurrentGenerationRecheckCaptureBinding is null
            ? run
            : StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCaptureRun(run);
    }

    private static DateTime MongoUtc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
        return new DateTime(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
    }

    private static InvalidOperationException Invalid(string reason) =>
        new($"P10_RECHECK_REVIEW_SUPERSESSION:{reason}");
}
