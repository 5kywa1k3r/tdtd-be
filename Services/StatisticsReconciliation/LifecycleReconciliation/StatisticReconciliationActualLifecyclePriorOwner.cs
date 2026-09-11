using System.Collections.Immutable;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;

internal interface IStatisticReconciliationActualLifecyclePriorOwner
{
    Task<StatisticReconciliationActualLifecyclePriorGeneration?> ResolveAsync(
        StatisticReconciliationRun run,
        CancellationToken cancellationToken = default);
}

internal sealed class StatisticReconciliationActualLifecyclePriorOwner
    : IStatisticReconciliationActualLifecyclePriorOwner
{
    private readonly Func<string, string, CancellationToken, Task<StatisticReconciliationActualAppendResult?>> readComplete;
    private readonly IStatisticReconciliationReviewBackend reviewBackend;
    private readonly IStatisticReconciliationActualLifecycleLedger ledger;

    public StatisticReconciliationActualLifecyclePriorOwner(
        StatisticReconciliationActualGenerationPublisher publisher,
        IStatisticReconciliationReviewBackend reviewBackend,
        IStatisticReconciliationActualLifecycleLedger ledger)
        : this(publisher.ReadCompleteAsync, reviewBackend, ledger) { }

    private StatisticReconciliationActualLifecyclePriorOwner(
        Func<string, string, CancellationToken, Task<StatisticReconciliationActualAppendResult?>> readComplete,
        IStatisticReconciliationReviewBackend reviewBackend,
        IStatisticReconciliationActualLifecycleLedger ledger)
    {
        this.readComplete = readComplete;
        this.reviewBackend = reviewBackend;
        this.ledger = ledger;
    }

    internal static StatisticReconciliationActualLifecyclePriorOwner ForReadOnlyAdmission(
        IStatisticReconciliationActualObservationBackend backend,
        IStatisticReconciliationReviewBackend reviewBackend,
        IStatisticReconciliationActualLifecycleLedger ledger) => new(
            (reconciliationId, generationId, ct) =>
                StatisticReconciliationActualGenerationPublisher.ReadCompleteFromBackendAsync(
                    backend, reconciliationId, generationId, ct), reviewBackend, ledger);

    public async Task<StatisticReconciliationActualLifecyclePriorGeneration?>
        ResolveAsync(
            StatisticReconciliationRun run,
            CancellationToken cancellationToken = default)
        => await ResolveCoreAsync(run, null, cancellationToken).ConfigureAwait(false);

    internal async Task RequireFreshnessRecoveryAdmissionAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationRecheckCaptureBinding successor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(successor);
        if (run.Status != StatisticReconciliationRunStatuses.Stale || run.Recheck is not null)
            throw RecoveryInvalid("ADMISSION_TERMINAL_BASE_REQUIRED");
        _ = await ResolveCoreAsync(run, successor, cancellationToken).ConfigureAwait(false)
            ?? throw RecoveryInvalid("ADMISSION_CURRENT_REQUIRED");
    }

    private async Task<StatisticReconciliationActualLifecyclePriorGeneration?> ResolveCoreAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationRecheckCaptureBinding? admissionSuccessor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        var hasGenerationId = !string.IsNullOrWhiteSpace(run.CurrentGenerationId);
        var hasGenerationHash = !string.IsNullOrWhiteSpace(run.CurrentGenerationHash);
        if (!hasGenerationId && !hasGenerationHash)
        {
            return null;
        }
        if (hasGenerationId != hasGenerationHash)
        {
            throw new InvalidOperationException(
                "P10 lifecycle prior committed-generation tuple is partial.");
        }

        var generationId = StatisticReconciliationLifecycleCanonical.Sha(
            run.CurrentGenerationId,
            nameof(run.CurrentGenerationId));
        var generationSha = StatisticReconciliationLifecycleCanonical.Sha(
            run.CurrentGenerationHash,
            nameof(run.CurrentGenerationHash));
        var actual = await readComplete(
                run.Id,
                generationId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "P10 lifecycle prior committed generation is missing.");
        if (!string.Equals(
                actual.GenerationSemanticSha256,
                generationSha,
                StringComparison.Ordinal) ||
            actual.LifecycleManifestSha256 is null ||
            actual.LifecycleObservationCount is not > 0)
        {
            throw new InvalidOperationException(
                "P10 lifecycle prior committed generation is not V4 lifecycle-bound.");
        }

        var evidence = await ledger.ReadAndValidateByManifestAsync(
                run.Id,
                actual.LifecycleManifestSha256,
                actual.LifecycleObservationCount.Value,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "P10 lifecycle prior evidence is missing.");
        var lineage = (await reviewBackend.ReadLineageAsync(
                run.Id,
                cancellationToken)
            .ConfigureAwait(false)).ToArray();
        return await ResolveValidatedCurrentAsync(run, actual, evidence, lineage,
            admissionSuccessor, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<StatisticReconciliationActualLifecyclePriorGeneration> ResolveValidatedCurrentAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationActualAppendResult actual,
        StatisticReconciliationActualLifecycleEvidence evidence,
        IReadOnlyList<StatisticReconciliationReview> lineage,
        StatisticReconciliationRecheckCaptureBinding? admissionSuccessor,
        CancellationToken cancellationToken = default)
    {
        var generationId = run.CurrentGenerationId!;
        var generationSha = run.CurrentGenerationHash!;
        foreach (var item in lineage)
        {
            StatisticReconciliationFinalVerdictPublisher.ValidateStored(item);
        }
        var active = lineage.Where(item =>
                string.Equals(
                    item.ActualGenerationId,
                    generationId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    item.ActualGenerationSha256,
                    generationSha,
                    StringComparison.Ordinal) &&
                !lineage.Any(successor => string.Equals(
                    successor.SupersedesVerdictGenerationId,
                    item.VerdictGenerationId,
                    StringComparison.Ordinal)))
            .ToArray();
        if (active.Length != 1)
        {
            throw new InvalidOperationException(
                "P10 lifecycle prior FINAL_VERDICT is missing or ambiguous.");
        }
        var verdict = active[0];
        if (admissionSuccessor is not null &&
            (!StatisticReconciliationRunService.IsFreshnessRecoveryRecheckBase(run, verdict) ||
             !StatisticReconciliationRunService.IsFreshnessRecoverySuccessor(run, admissionSuccessor) ||
             !SameCreationSource(run, run.CurrentGenerationRecheckCaptureBinding!) ||
             !SameCreationSource(run, admissionSuccessor)))
            throw RecoveryInvalid("ADMISSION_BASE_OR_SOURCE_CHANGED");
        if (admissionSuccessor is not null ||
            run.Recheck?.BaseTerminalStatus == StatisticReconciliationRunStatuses.Stale ||
            evidence.Result.Outcome != StatisticReconciliationLifecycleOutcomes.Matched ||
            !evidence.Result.EvidenceComplete)
        {
            if (!StatisticReconciliationActualRunBindingGuard.Matches(run, actual.CommittedRunBinding))
                throw RecoveryInvalid("CURRENT_ACTUAL_CONTEXT");
            var ancestor = SelectFreshnessRecoveryAncestor(run, verdict, evidence, lineage, admissionSuccessor);
            var ancestorActual = await readComplete(run.Id,
                ancestor.ActualGenerationId, cancellationToken).ConfigureAwait(false);
            if (ancestorActual is null ||
                ancestorActual.GenerationSemanticSha256 != ancestor.ActualGenerationSha256 ||
                !StatisticReconciliationActualRunBindingGuard.MatchesInitialCreation(run, ancestorActual.CommittedRunBinding) ||
                ancestorActual.LifecycleManifestSha256 != evidence.Manifest.PriorLifecycleManifestSha256 ||
                ancestorActual.LifecycleObservationCount != evidence.Manifest.PriorLifecycleObservationCount)
                throw RecoveryInvalid("ANCESTOR_ACTUAL_BINDING");
            var ancestorEvidence = await ledger.ReadAndValidateByManifestAsync(run.Id,
                ancestorActual.LifecycleManifestSha256!,
                ancestorActual.LifecycleObservationCount!.Value, cancellationToken).ConfigureAwait(false)
                ?? throw RecoveryInvalid("ANCESTOR_LIFECYCLE_MISSING");
            return CompleteFreshnessRecoveryPrior(run, verdict, evidence, lineage, ancestorEvidence, admissionSuccessor);
        }
        return CreatePrior(generationId, generationSha, verdict, evidence);
    }

    // A recovery lifecycle baseline is historical evidence, not the immediate
    // verdict being superseded. The active marker and persisted STALE current
    // remain untouched; the finalizer still supersedes that exact STALE verdict.
    internal static StatisticReconciliationReview SelectFreshnessRecoveryAncestor(
        StatisticReconciliationRun run,
        StatisticReconciliationReview currentVerdict,
        StatisticReconciliationActualLifecycleEvidence currentEvidence,
        IReadOnlyList<StatisticReconciliationReview> lineage,
        StatisticReconciliationRecheckCaptureBinding? admissionSuccessor = null)
    {
        StatisticReconciliationActualLifecycleEvidenceIntegrity.Validate(currentEvidence);
        foreach (var item in lineage) StatisticReconciliationFinalVerdictPublisher.ValidateStored(item);
        var receipt = run.CurrentRecheckFinalizeReceipt ?? throw RecoveryInvalid("RECEIPT_REQUIRED");
        var marker = run.Recheck;
        StatisticReconciliationRun terminalBase;
        StatisticReconciliationRecheckCaptureBinding successor;
        if (admissionSuccessor is null)
        {
            if (marker is null) throw RecoveryInvalid("ACTIVE_MARKER_REQUIRED");
            StatisticReconciliationRecheckCanonical.RequireValidMarker(marker);
            if (run.Status != StatisticReconciliationRunStatuses.Running ||
            marker.Phase != StatisticReconciliationRecheckPhases.CaptureRunning ||
            marker.BaseTerminalStatus != StatisticReconciliationRunStatuses.Stale ||
            marker.BaseCurrentGenerationId != run.CurrentGenerationId ||
            marker.BaseCurrentGenerationHash != run.CurrentGenerationHash ||
            marker.BaseVerdictGenerationId != currentVerdict.VerdictGenerationId ||
            marker.BaseVerdictGenerationHash != currentVerdict.VerdictGenerationSha256 ||
            run.PendingGenerationId is not null || run.PendingGenerationHash is not null ||
            run.PendingGenerationPublishedAtUtc.HasValue ||
            string.IsNullOrWhiteSpace(run.LeaseOwnerId) || string.IsNullOrWhiteSpace(run.ClaimToken) ||
            !run.LeaseUntilUtc.HasValue)
                throw RecoveryInvalid("ACTIVE_BASE_BINDING");
            // Projection of the structurally proven immutable marker base;
            // never persisted or treated as a valid mutable run state.
            terminalBase = new StatisticReconciliationRun
            {
                Id = run.Id, Status = marker.BaseTerminalStatus,
                CurrentGenerationId = marker.BaseCurrentGenerationId,
                CurrentGenerationHash = marker.BaseCurrentGenerationHash,
                CurrentGenerationRecheckCaptureBinding = run.CurrentGenerationRecheckCaptureBinding,
                CurrentRecheckFinalizeReceipt = receipt
            };
            successor = marker.CaptureBinding;
        }
        else
        {
            if (marker is not null || run.Status != StatisticReconciliationRunStatuses.Stale)
                throw RecoveryInvalid("ADMISSION_TERMINAL_BASE_REQUIRED");
            terminalBase = run;
            successor = admissionSuccessor;
        }
        if (currentEvidence.Result.Outcome != StatisticReconciliationLifecycleOutcomes.Stale ||
            currentEvidence.Result.RootCause != StatisticReconciliationLifecycleRootCauses.Freshness ||
            currentEvidence.Result.EvidenceComplete || currentEvidence.Manifest.ReconciliationId != run.Id ||
            !StatisticReconciliationRunService.HasValidRecheckFinalizeReceipt(run) ||
            !StatisticReconciliationRunService.IsFreshnessRecoverySuccessor(run, successor))
            throw RecoveryInvalid("ACTIVE_BASE_BINDING");
        if (!StatisticReconciliationRunService.IsFreshnessRecoveryRecheckBase(terminalBase, currentVerdict) ||
            !SameCreationSource(run, run.CurrentGenerationRecheckCaptureBinding!) ||
            !SameCreationSource(run, successor))
            throw RecoveryInvalid("FRESHNESS_BASE_OR_SOURCE_CHANGED");

        var manifest = currentEvidence.Manifest;
        var currentRows = lineage.Where(value => value.ActualGenerationId == run.CurrentGenerationId &&
            value.ActualGenerationSha256 == run.CurrentGenerationHash).ToArray();
        var candidates = lineage.Where(value => value.VerdictGenerationId == receipt.BaseVerdictGenerationId).ToArray();
        if (lineage.Count != 2 || currentRows.Length != 1 || currentRows[0].VerdictGenerationSha256 != currentVerdict.VerdictGenerationSha256 ||
            lineage.Any(value => value.SupersedesVerdictGenerationId == currentVerdict.VerdictGenerationId) ||
            candidates.Length != 1)
            throw RecoveryInvalid("LINEAGE_CARDINALITY");
        var ancestor = candidates[0];
        if (ancestor.ReconciliationId != run.Id ||
            ancestor.VerdictGenerationSha256 != receipt.BaseVerdictGenerationSha256 ||
            ancestor.ActualGenerationId != receipt.BaseActualGenerationId ||
            ancestor.ActualGenerationSha256 != receipt.BaseActualGenerationSha256 ||
            ancestor.Verdict != StatisticReconciliationFinalVerdicts.Matched || !ancestor.CompleteEvidence ||
            ancestor.FailureKind != StatisticReconciliationFinalVerdictFailureKinds.None ||
            ancestor.SupersedesVerdictGenerationId is not null || ancestor.SupersedesVerdictGenerationSha256 is not null ||
            lineage.Count(value => value.SupersedesVerdictGenerationId == ancestor.VerdictGenerationId) != 1 ||
            manifest.PriorCommittedGenerationId != ancestor.ActualGenerationId ||
            manifest.PriorCommittedGenerationSha256 != ancestor.ActualGenerationSha256 ||
            manifest.PriorFinalVerdictGenerationId != ancestor.VerdictGenerationId ||
            manifest.PriorFinalVerdictGenerationSha256 != ancestor.VerdictGenerationSha256)
            throw RecoveryInvalid("ONE_HOP_ANCESTOR_BINDING");
        return ancestor;
    }

    internal static StatisticReconciliationActualLifecyclePriorGeneration CompleteFreshnessRecoveryPrior(
        StatisticReconciliationRun run,
        StatisticReconciliationReview currentVerdict,
        StatisticReconciliationActualLifecycleEvidence currentEvidence,
        IReadOnlyList<StatisticReconciliationReview> lineage,
        StatisticReconciliationActualLifecycleEvidence ancestorEvidence,
        StatisticReconciliationRecheckCaptureBinding? admissionSuccessor = null)
    {
        var ancestor = SelectFreshnessRecoveryAncestor(run, currentVerdict, currentEvidence, lineage, admissionSuccessor);
        StatisticReconciliationActualLifecycleEvidenceIntegrity.Validate(ancestorEvidence);
        var prior = currentEvidence.Manifest;
        var manifest = ancestorEvidence.Manifest;
        var audit = manifest.P9ContributionAudit;
        if (manifest.ReconciliationId != run.Id || manifest.PriorLifecycleManifestSha256 is not null ||
            manifest.PriorLifecycleObservationCount != 0 || !ancestorEvidence.Result.EvidenceComplete ||
            ancestorEvidence.Result.Outcome != StatisticReconciliationLifecycleOutcomes.Matched ||
            manifest.ManifestSha256 != prior.PriorLifecycleManifestSha256 ||
            manifest.ObservationCount != prior.PriorLifecycleObservationCount ||
            manifest.LifecycleRowSetSha256 != prior.PriorLifecycleRowSetSha256 ||
            manifest.BaseCoherentGenerationId != prior.PriorBaseCoherentGenerationId ||
            manifest.BaseCoherentGenerationSha256 != prior.PriorBaseCoherentGenerationSha256 ||
            audit.Scope.RunId != prior.PriorP9RunId || audit.Scope.GenerationId != prior.PriorP9GenerationId ||
            audit.Scope.GenerationSha256 != prior.PriorP9GenerationSha256 ||
            audit.LedgerHash != prior.PriorP9ContributionLedgerSha256 ||
            audit.ReversalBaselineHash != prior.PriorP9ReversalBaselineSha256 ||
            audit.SnapshotSemanticSha256 != prior.PriorP9AuditSnapshotSha256 ||
            audit.Scope.RunId != run.P9RunId || audit.Scope.GenerationId != run.P9GenerationId ||
            audit.Scope.GenerationSha256 != run.P9GenerationHash ||
            audit.Scope.WorkId != run.WorkId || audit.Scope.DynamicFormTemplateId != run.DynamicFormVersionId ||
            audit.Scope.PeriodInstanceKey != run.PeriodInstanceKey ||
            audit.Reversal is not null || audit.Sources.IsDefaultOrEmpty ||
            audit.Sources.Any(value => value.SourceReportId != run.SourceReportId ||
                value.SourcePayloadRevision != run.SourcePayloadRevision || value.SourcePayloadHash != run.SourcePayloadHash ||
                value.SourceLifecycleRevision != run.SourceLifecycleRevision ||
                value.WorkId != run.WorkId || value.WorkAssignmentId != run.ScopeAssignmentId ||
                value.PeriodInstanceKey != run.PeriodInstanceKey) ||
            !SameCreationSource(run, admissionSuccessor ?? run.Recheck!.CaptureBinding))
            throw RecoveryInvalid("COHERENT_ANCESTOR_CONTEXT");
        return CreatePrior(ancestor.ActualGenerationId, ancestor.ActualGenerationSha256, ancestor, ancestorEvidence);
    }

    private static bool SameCreationSource(StatisticReconciliationRun run,
        StatisticReconciliationRecheckCaptureBinding capture) =>
        capture.SourceReportId == run.SourceReportId &&
        capture.SourcePayloadRevision == run.SourcePayloadRevision && capture.SourcePayloadHash == run.SourcePayloadHash &&
        capture.SourceLifecycleRevision == run.SourceLifecycleRevision && capture.SourceLifecycleHash == run.SourceLifecycleHash &&
        capture.SourceLifecycleEventKey == run.SourceLifecycleEventKey && capture.SourceLifecycleStatus == run.SourceLifecycleStatus &&
        capture.DynamicFormVersionId == run.DynamicFormVersionId && capture.DynamicFormSchemaHash == run.DynamicFormSchemaHash &&
        capture.PeriodKey == run.PeriodKey && capture.PeriodInstanceKey == run.PeriodInstanceKey &&
        capture.PeriodKind == run.PeriodKind && capture.PeriodStartUtc == run.PeriodStartUtc &&
        capture.PeriodEndUtc == run.PeriodEndUtc && capture.TimeAxis == run.TimeAxis &&
        capture.ActualCapturePlan.Export.WorkId == run.WorkId &&
        capture.ActualCapturePlan.Export.ScopeType == "ASSIGNMENT" &&
        capture.ActualCapturePlan.Export.ScopeId == run.ScopeAssignmentId;

    private static InvalidOperationException RecoveryInvalid(string code) =>
        new("P10_LIFECYCLE_FRESHNESS_RECOVERY_" + code);

    internal static StatisticReconciliationActualLifecyclePriorGeneration CreatePrior(
        string generationId, string generationSha, StatisticReconciliationReview verdict,
        StatisticReconciliationActualLifecycleEvidence evidence)
    {
        var audit = evidence.Manifest.P9ContributionAudit;
        return new StatisticReconciliationActualLifecyclePriorGeneration(
            evidence.Manifest.BaseCoherentGenerationId,
            evidence.Manifest.BaseCoherentGenerationSha256,
            generationId,
            generationSha,
            verdict.VerdictGenerationId,
            verdict.VerdictGenerationSha256,
            evidence.Manifest.ManifestSha256,
            evidence.Manifest.ObservationCount,
            evidence.Manifest.LifecycleRowSetSha256,
            evidence.Result,
            evidence.Request.Sources,
            evidence.Request.Actuals,
            audit.Scope.RunId,
            audit.Scope.GenerationId,
            audit.Scope.GenerationSha256,
            audit.LedgerHash,
            audit.ReversalBaselineHash,
            audit.SnapshotSemanticSha256,
            audit,
            audit.Sources
                .OrderBy(static item => item.SourceReportId, StringComparer.Ordinal)
                .Select(static item => item.SourceAuditHash)
                .ToImmutableArray(),
            audit.Targets
                .OrderBy(static item => item.TargetStore, StringComparer.Ordinal)
                .ThenBy(static item => item.TargetStatisticId, StringComparer.Ordinal)
                .Select(static item => item.LedgerEntryHash)
                .ToImmutableArray());
    }
}
