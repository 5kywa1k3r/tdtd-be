using MongoDB.Driver;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    // Recovery is limited to a completed recheck with receipt-bound P8 drift.
    // A STALE label alone does not prove this boundary; initial STALE, missing
    // provenance and arbitrary FAILED verdicts remain ineligible.
    internal static bool IsFreshnessRecoveryRecheckBase(
        StatisticReconciliationRun run,
        StatisticReconciliationReview verdict)
    {
        if (run.Status != StatisticReconciliationRunStatuses.Stale ||
            run.Recheck is not null || run.PendingGenerationId is not null ||
            run.PendingGenerationHash is not null ||
            run.PendingGenerationPublishedAtUtc.HasValue ||
            run.LeaseOwnerId is not null || run.ClaimToken is not null ||
            run.LeaseUntilUtc.HasValue || run.LastHeartbeatAtUtc.HasValue ||
            verdict.Verdict != StatisticReconciliationFinalVerdicts.Failed ||
            verdict.RootCauseClass != StatisticReconciliationRootCauseClasses.Freshness ||
            verdict.FailureKind != StatisticReconciliationFinalVerdictFailureKinds.None ||
            verdict.FailureEvidenceSha256 is not null ||
            verdict.CompleteEvidence || verdict.AllRequiredLayersZero ||
            verdict.MissingOrExtraIdentity || verdict.UnknownBlocksCloseout ||
            verdict.Signable || verdict.CloseoutAllowed ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(verdict.FreshnessAssessmentSha256) ||
            !StatisticReconciliationCanonicalJson.IsCanonicalSha256(verdict.RootCauseClassificationSha256) ||
            run.CurrentRecheckFinalizeReceipt is not { } receipt ||
            receipt.SchemaVersion != StatisticReconciliationRecheckFinalizeReceipt.CurrentP8SchemaVersion ||
            receipt.CurrentP8Configuration is not { } currentP8 ||
            run.CurrentGenerationRecheckCaptureBinding is not { } captured ||
            verdict.ReconciliationId != run.Id ||
            verdict.ActualGenerationId != run.CurrentGenerationId ||
            verdict.ActualGenerationSha256 != run.CurrentGenerationHash ||
            receipt.SuccessorVerdictGenerationId != verdict.VerdictGenerationId ||
            receipt.SuccessorVerdictGenerationSha256 != verdict.VerdictGenerationSha256 ||
            verdict.SupersedesVerdictGenerationId != receipt.BaseVerdictGenerationId ||
            verdict.SupersedesVerdictGenerationSha256 != receipt.BaseVerdictGenerationSha256)
            return false;

        try
        {
            StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);
            StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(captured);
            if (!HasValidRecheckFinalizeReceipt(run)) return false;
            var capturedP8 = CaptureP8Identity(captured);
            return StatisticReconciliationActualCapturePlanIntegrity.IsV4(captured.ActualCapturePlan) &&
                capturedP8.OwnerId == captured.DynamicFormVersionId &&
                capturedP8.OwnerId == currentP8.OwnerId &&
                capturedP8.ConfigId == currentP8.ConfigId &&
                capturedP8.VersionId != currentP8.VersionId &&
                (long)currentP8.VersionNo == (long)capturedP8.VersionNo + 1 &&
                currentP8.Revision > capturedP8.Revision &&
                currentP8.Revision - capturedP8.Revision == 1 &&
                currentP8.BundleSha256 != capturedP8.BundleSha256;
        }
        catch (Exception error) when (error is InvalidOperationException or
            StatisticReconciliationFinalVerdictException or StatisticReconciliationRecheckException)
        {
            return false;
        }
    }

    internal static bool IsFreshnessRecoverySuccessor(
        StatisticReconciliationRun run,
        StatisticReconciliationRecheckCaptureBinding successor)
    {
        if (run.CurrentRecheckFinalizeReceipt?.CurrentP8Configuration is not { } required ||
            run.CurrentGenerationRecheckCaptureBinding is not { } captured)
            return false;
        try
        {
            StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(successor);
            StatisticReconciliationRecheckCaptureBindingCanonical.RequireValid(captured);
            StatisticReconciliationTrustedP8ConfigurationIdentityCanonical.RequireValid(required);
            var successorP8 = CaptureP8Identity(successor);
            return HasValidRecheckFinalizeReceipt(run) &&
                successor.P9RunId != captured.P9RunId &&
                successor.P9GenerationId != captured.P9GenerationId &&
                successor.P9GenerationHash != captured.P9GenerationHash &&
                successor.RemediationEvidenceSha256 is null &&
                successorP8.OwnerId == successor.DynamicFormVersionId &&
                successorP8.SemanticSha256 == required.SemanticSha256;
        }
        catch (Exception error) when (error is InvalidOperationException or
            StatisticReconciliationRecheckException)
        {
            return false;
        }
    }

    private static StatisticReconciliationTrustedP8ConfigurationIdentity CaptureP8Identity(
        StatisticReconciliationRecheckCaptureBinding capture)
    {
        var identity = StatisticReconciliationTrustedP8ConfigurationIdentityCanonical.Create(
            capture.P8ConfigOwnerId ?? throw new InvalidOperationException("FRESHNESS_RECOVERY_P8_OWNER_MISSING"),
            capture.P8ConfigId!, capture.P8ConfigVersionId!,
            capture.P8ConfigVersionNo!.Value, capture.P8ConfigRevision!.Value,
            capture.P8ConfigHash!);
        if (identity.BundleSha256 != capture.P8ConfigBundleHash ||
            capture.ActualCapturePlan.P8ConfigurationOwnerId != identity.OwnerId ||
            capture.ActualCapturePlan.P8ConfigurationBundleSha256 != identity.BundleSha256)
            throw new InvalidOperationException("FRESHNESS_RECOVERY_P8_CAPTURE_MISMATCH");
        return identity;
    }

    private async Task RequireFreshnessRecoverySuccessorAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationRecheckCaptureBinding successor,
        CancellationToken ct)
    {
        if (!IsFreshnessRecoverySuccessor(run, successor))
            throw JobConflict("RECHECK_FRESHNESS_SUCCESSOR_BINDING_INVALID");

        // The receipt proves the old configuration boundary; this separate
        // owner read rejects a successor that became obsolete again. Worker
        // capture/finalization retains its existing current-owner fences.
        var owners = await _ctx.DynamicFormTemplates.Find(value =>
                value.Id == successor.P8ConfigOwnerId && value.IsActive &&
                value.IsPublished && !value.IsDeleted)
            .Limit(2).ToListAsync(ct);
        if (owners.Count != 1)
            throw JobConflict("RECHECK_FRESHNESS_CURRENT_P8_NOT_EXACT");
        var trusted = DynamicFormStatisticConfigCommandService.GetP804TrustedPersistedView(owners[0]);
        if (trusted is null || trusted.Status != "LOCKED")
            throw JobConflict("RECHECK_FRESHNESS_CURRENT_P8_NOT_LOCKED");
        var captured = run.CurrentGenerationRecheckCaptureBinding!;
        var previous = DynamicFormStatisticConfigCommandService.GetP804TrustedPersistedView(
            owners[0], captured.P8ConfigId, captured.P8ConfigVersionId,
            captured.P8ConfigVersionNo, captured.P8ConfigRevision, captured.P8ConfigHash);
        if (previous is null || previous.Status != "LOCKED" ||
            trusted.PreviousVersionId != previous.VersionId)
            throw JobConflict("RECHECK_FRESHNESS_P8_LINEAGE_INVALID");
        var currentP8 = StatisticReconciliationTrustedP8ConfigurationIdentityCanonical.Create(
            owners[0].Id, trusted.ConfigId, trusted.VersionId,
            trusted.VersionNo, trusted.Revision, trusted.ConfigHash);
        if (currentP8.SemanticSha256 != run.CurrentRecheckFinalizeReceipt!
                .CurrentP8Configuration!.SemanticSha256)
            throw JobConflict("RECHECK_FRESHNESS_CURRENT_P8_CHANGED");
        
        // Reject unsupported lifecycle ancestry before accepting a recheck.
        // This invokes the same actual read/ancestor validation as the worker,
        // without constructing a publication CAS or introducing a DI cycle.
        var lifecyclePriorOwner = StatisticReconciliationActualLifecyclePriorOwner.ForReadOnlyAdmission(
            new StatisticReconciliationActualObservationMongoBackend(_ctx),
            new StatisticReconciliationReviewMongoBackend(_ctx),
            StatisticReconciliationActualLifecycleMongoLedger.ForReadOnlyAdmission(_ctx));
        try
        {
            await lifecyclePriorOwner.RequireFreshnessRecoveryAdmissionAsync(run, successor, ct);
        }
        catch (Exception error) when (error is InvalidOperationException or
            StatisticReconciliationLifecycleException or StatisticReconciliationFinalVerdictException or
            StatisticReconciliationActualObservationException or StatisticReconciliationRecheckException)
        {
            throw JobConflict("RECHECK_FRESHNESS_LIFECYCLE_PRIOR_INVALID");
        }
    }
}
