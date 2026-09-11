using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    public async Task<StatisticReconciliationRemediationEvidenceResponse>
        AuthorizeRecheckRemediationAsync(
            string reconciliationId,
            MeResponse actor,
            CancellationToken ct = default)
    {
        // This internal command has no request body. Role and activation gates
        // precede every hidden reconciliation/P9/evidence existence read.
        RoleGuard.RequireSystemAdmin(actor);
        var activation = _activation.RequireFoundation(
            StatisticReconciliationCapabilities.SourceToResultReconciliation,
            StatisticReconciliationRouteRegistry
                .RecheckRemediationAuthorize);
        reconciliationId = NormalizeHiddenObjectId(reconciliationId,
            "reconciliationId", actor);
        var run = await _ctx.StatisticReconciliationRuns.Find(value =>
                value.Id == reconciliationId &&
                value.CandidateChainId == activation.ChainId &&
                !value.IsDeleted)
            .FirstOrDefaultAsync(ct) ?? throw NotFound();
        RequireReadIntegrity(run);
        if (run.Status != StatisticReconciliationRunStatuses.Mismatched ||
            run.Recheck is not null ||
            run.CurrentGenerationId is null ||
            run.CurrentGenerationHash is null ||
            run.PendingGenerationId is not null ||
            run.PendingGenerationHash is not null ||
            run.PendingGenerationPublishedAtUtc.HasValue ||
            run.LeaseOwnerId is not null || run.ClaimToken is not null ||
            run.LeaseUntilUtc.HasValue || run.LastHeartbeatAtUtc.HasValue)
            throw JobConflict("REMEDIATION_BASE_STATE_INVALID");

        var baseVerdict = await LoadSingleCurrentVerdictAsync(run, ct);
        if (!IsIdentityRemediationBase(run, baseVerdict))
            throw JobConflict("REMEDIATION_BASE_VERDICT_INVALID");

        var scope = await _ctx.WorkAssignments.Find(value =>
                value.Id == run.ScopeAssignmentId &&
                value.WorkId == run.WorkId &&
                !value.IsDeleted && value.IsActive)
            .FirstOrDefaultAsync(ct) ?? throw NotFound();
        var successor = await ResolveRecheckCaptureBindingAsync(
            run, scope, actor, ct);
        var successorP9 = await _ctx.WorkReportStatisticRebuildJobs
            .Find(value => value.Id == successor.P9RunId &&
                           value.GenerationId ==
                               successor.P9GenerationId &&
                           value.GenerationHash ==
                               successor.P9GenerationHash &&
                           !value.IsDeleted)
            .FirstOrDefaultAsync(ct) ??
            throw JobConflict("REMEDIATION_P9_OPERATION_MISSING");

        var store = new StatisticReconciliationTrustedRemediationEvidenceStore(
            _ctx);
        var existing = await store.ResolveAsync(
            run, baseVerdict, successor, successorP9, ct);
        if (existing is not null)
            return Response(existing, true);

        StatisticReconciliationTrustedRemediationEvidence evidence;
        try
        {
            evidence =
                StatisticReconciliationTrustedRemediationEvidenceCanonical
                    .Build(run, baseVerdict, successor, successorP9,
                        actor.Id, activation.EvidenceSha256, MongoUtcNow());
        }
        catch (InvalidOperationException)
        {
            throw JobConflict("REMEDIATION_P9_OPERATION_INVALID");
        }
        var (stored, replayed) = await store.AppendOrReplayAsync(evidence, ct);
        return Response(stored, replayed);
    }

    private static StatisticReconciliationRemediationEvidenceResponse
        Response(
            StatisticReconciliationTrustedRemediationEvidence evidence,
            bool replayed)
        => new()
        {
            ReconciliationId = evidence.ReconciliationId,
            EvidenceId = evidence.Id,
            EvidenceSha256 = evidence.EvidenceSha256,
            SuccessorP9GenerationId = evidence.SuccessorP9GenerationId,
            IsReplay = replayed
        };

    private async Task<StatisticReconciliationReview>
        LoadSingleCurrentVerdictAsync(
            StatisticReconciliationRun run,
            CancellationToken ct)
    {
        var verdicts = (await new StatisticReconciliationReviewMongoBackend(
                _ctx).ReadLineageAsync(run.Id, ct))
            .Where(value =>
                value.ActualGenerationId == run.CurrentGenerationId &&
                value.ActualGenerationSha256 == run.CurrentGenerationHash)
            .ToArray();
        if (verdicts.Length != 1)
            throw JobConflict("RECHECK_BASE_VERDICT_CARDINALITY_INVALID");
        StatisticReconciliationFinalVerdictPublisher.ValidateStored(
            verdicts[0]);
        if (FinalRunStatus(verdicts[0]) != run.Status)
            throw JobConflict("RECHECK_BASE_STATUS_VERDICT_MISMATCH");
        return verdicts[0];
    }

    internal static bool IsIdentityRemediationBase(
        StatisticReconciliationRun run,
        StatisticReconciliationReview verdict)
        => run.Status == StatisticReconciliationRunStatuses.Mismatched &&
           verdict.Verdict ==
               StatisticReconciliationFinalVerdicts.Mismatched &&
           verdict.MissingOrExtraIdentity &&
           verdict.RootCauseClass is
               StatisticReconciliationRootCauseClasses.MissingIdentity or
               StatisticReconciliationRootCauseClasses.ExtraIdentity &&
           !verdict.Signable && !verdict.CloseoutAllowed;
}
