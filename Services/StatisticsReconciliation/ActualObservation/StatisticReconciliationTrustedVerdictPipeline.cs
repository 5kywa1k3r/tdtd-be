using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.LifecycleReconciliation;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.ActualObservation;

internal sealed record StatisticReconciliationTrustedVerdictCommand(
    string CommandId,
    string ReconciliationId,
    string ActualGenerationId,
    string ActualGenerationSha256,
    long ExpectedStateRevision,
    string ExpectedStateHash,
    long ExpectedGenerationPublishRevision);

internal interface IStatisticReconciliationTrustedVerdictPipeline
{
    Task<StatisticReconciliationTrustedFinalizeResult> FinalizeAsync(
        StatisticReconciliationTrustedVerdictCommand command,
        MeResponse systemActor,
        CancellationToken cancellationToken);
}

internal sealed class StatisticReconciliationTrustedVerdictPipeline(
    MongoDbContext context,
    IStatisticReconciliationTrustedFinalizer finalizer,
    IStatisticReconciliationTrustedRecheckFinalizer recheckFinalizer,
    IStatisticReconciliationTrustedCurrentOwnerReader currentOwnerReader,
    IStatisticReconciliationTrustedLifecycleVerifier lifecycleVerifier,
    IStatisticReconciliationExpectedGenerationBindingReader expectedBindingReader,
    IStatisticReconciliationExpectedAuthoritativeCurrentValidator
        expectedCurrentValidator,
    ILogger<StatisticReconciliationTrustedVerdictPipeline> logger)
    : IStatisticReconciliationTrustedVerdictPipeline
{
    internal const string SchemaVersion = "P10_TRUSTED_VERDICT_PIPELINE_V2";

    public async Task<StatisticReconciliationTrustedFinalizeResult> FinalizeAsync(
        StatisticReconciliationTrustedVerdictCommand command,
        MeResponse systemActor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(systemActor);
        RequireCommand(command);

        var run = await context.StatisticReconciliationRuns
            .Find(value => value.Id == command.ReconciliationId && !value.IsDeleted)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw Fail("RUN_NOT_FOUND");
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(run);
        var pendingExact =
            run.PendingGenerationId == command.ActualGenerationId &&
            run.PendingGenerationHash == command.ActualGenerationSha256 &&
            run.PendingGenerationPublishedAtUtc.HasValue;
        var noPending = run.PendingGenerationId is null &&
            run.PendingGenerationHash is null &&
            !run.PendingGenerationPublishedAtUtc.HasValue;
        var currentExact =
            run.CurrentGenerationId == command.ActualGenerationId &&
            run.CurrentGenerationHash == command.ActualGenerationSha256;
        var commandPinsExact =
            run.StateRevision == command.ExpectedStateRevision &&
            run.StateHash == command.ExpectedStateHash &&
            run.GenerationPublishRevision ==
                command.ExpectedGenerationPublishRevision;
        var leaseClear = run.LeaseOwnerId is null &&
            run.ClaimToken is null &&
            !run.LeaseUntilUtc.HasValue &&
            !run.LastHeartbeatAtUtc.HasValue;
        var terminal = run.Status is
            StatisticReconciliationRunStatuses.Matched or
            StatisticReconciliationRunStatuses.Mismatched or
            StatisticReconciliationRunStatuses.Stale or
            StatisticReconciliationRunStatuses.Failed;
        var receiptValid = run.CurrentRecheckFinalizeReceipt is not null &&
            StatisticReconciliationRunService
                .HasValidRecheckFinalizeReceipt(run);

        var isInitialPending =
            run.Recheck is null &&
            run.CurrentGenerationId is null &&
            run.CurrentGenerationHash is null &&
            run.CurrentGenerationRecheckCaptureBinding is null &&
            run.Status == StatisticReconciliationRunStatuses.Queued &&
            pendingExact && commandPinsExact;
        var isInitialReplay =
            run.Recheck is null &&
            run.CurrentRecheckFinalizeReceipt is null &&
            run.CurrentGenerationRecheckCaptureBinding is null &&
            terminal && noPending && currentExact;
        var isRecheckPending =
            run.Recheck?.Phase ==
                StatisticReconciliationRecheckPhases.PendingPublished &&
            run.CurrentGenerationId is not null &&
            run.CurrentGenerationHash is not null &&
            run.Status == StatisticReconciliationRunStatuses.Queued &&
            pendingExact && commandPinsExact;
        var isRecheckPromoted =
            run.Recheck?.Phase ==
                StatisticReconciliationRecheckPhases
                    .ReviewSupersessionPending &&
            receiptValid && terminal && noPending && currentExact &&
            run.Status == run.CurrentRecheckFinalizeReceipt!.TerminalStatus;
        var isRecheckClearedReplay =
            run.Recheck is null && receiptValid && terminal && noPending &&
            currentExact &&
            run.Status == run.CurrentRecheckFinalizeReceipt!.TerminalStatus;
        var isRecheck = isRecheckPending || isRecheckPromoted ||
            isRecheckClearedReplay;
        if ((!isInitialPending && !isInitialReplay && !isRecheck) ||
            !leaseClear)
            throw Fail("PENDING_STATE_PIN_MISMATCH");

        if (isInitialReplay || isRecheckClearedReplay)
            return await StableReplayAsync(
                    run, command, isRecheckClearedReplay, cancellationToken)
                .ConfigureAwait(false);

        if (isRecheckPromoted)
            return await recheckFinalizer.ReplayPromotedRecheckAsync(
                    run.Id, systemActor, cancellationToken)
                .ConfigureAwait(false);

        StatisticReconciliationActualAppendResult actual;
        try
        {
            actual = await StatisticReconciliationActualGenerationPublisher
                .ReadCompleteFromBackendAsync(
                    new StatisticReconciliationActualObservationMongoBackend(context),
                    run.Id,
                    command.ActualGenerationId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw Fail("ACTUAL_GENERATION_NOT_COMMITTED");
        }
        catch (StatisticReconciliationActualObservationException error)
        {
            throw Fail("ACTUAL_GENERATION_INVALID", error);
        }
        if (actual.GenerationSemanticSha256 != command.ActualGenerationSha256 ||
            !StatisticReconciliationActualRunBindingGuard.Matches(
                run, actual.CommittedRunBinding))
            throw Fail("ACTUAL_GENERATION_RUN_BINDING_MISMATCH");

        var expected = await LoadExactExpectedGenerationAsync(
                run, actual, cancellationToken)
            .ConfigureAwait(false);
        var lifecycleFirst = await lifecycleVerifier.ReadAsync(
                actual, cancellationToken)
            .ConfigureAwait(false);
        var currentFirst = await currentOwnerReader.ReadAsync(
                run, actual, systemActor, cancellationToken)
            .ConfigureAwait(false);
        var expectedFirst = await ReadExpectedCurrentAsync(
                run, expected.Binding, cancellationToken)
            .ConfigureAwait(false);

        // Repeat the complete cross-owner round. Each owner already performs
        // its own internal double collect; equality across these two rounds
        // closes mutations between the independent owner families.
        var lifecycleSecond = await lifecycleVerifier.ReadAsync(
                actual, cancellationToken)
            .ConfigureAwait(false);
        var currentSecond = await currentOwnerReader.ReadAsync(
                run, actual, systemActor, cancellationToken)
            .ConfigureAwait(false);
        var expectedSecond = await ReadExpectedCurrentAsync(
                run, expected.Binding, cancellationToken)
            .ConfigureAwait(false);

        var lifecycle = lifecycleVerifier.RequireStable(
            lifecycleFirst, lifecycleSecond);
        var currentRead = RequireStableCurrent(
            currentFirst, currentSecond);
        var expectedCurrent = RequireStableExpectedCurrent(
            expectedFirst, expectedSecond, expected.Binding);
        currentRead = BindExpectedCurrent(
            currentRead, expectedCurrent, expected.Binding);
        var request = StatisticReconciliationTrustedVerdictDeriver.Derive(
            run, expected.Commit, expected.Atoms, actual,
            currentRead.CurrentOwners, currentRead.FenceSha256,
            currentRead.EvidenceComplete, currentRead.CaptureCoherent,
            lifecycle);
        var freshness = request.RootCauseRequest?.Freshness;
        if (freshness is not null &&
            freshness.State != StatisticReconciliationFreshnessStates.Fresh)
        {
            logger.LogWarning(
                "P10 trusted verdict freshness is not current. State={State}; Domain={Domain}; Reason={Reason}; CurrentState={CurrentState}; CurrentComplete={CurrentComplete}; CurrentCoherent={CurrentCoherent}; LifecycleOutcome={LifecycleOutcome}; LifecycleRootCause={LifecycleRootCause}; LifecycleComplete={LifecycleComplete}; LifecycleCoherent={LifecycleCoherent}",
                freshness.State,
                freshness.DriftDomain,
                freshness.ReasonCode,
                currentRead.State,
                currentRead.EvidenceComplete,
                currentRead.CaptureCoherent,
                lifecycle.Outcome,
                lifecycle.RootCause,
                lifecycle.EvidenceComplete,
                lifecycle.CaptureCoherent);
        }
        var diagnosticDecision =
            new StatisticReconciliationFinalVerdictEvaluator().Evaluate(request);
        if (diagnosticDecision.Verdict !=
            StatisticReconciliationFinalVerdicts.Matched)
        {
            var layerSummary = string.Join(
                ";",
                request.RootCauseRequest?.Layers.Select(layer =>
                    $"{layer.Ordinal}:{layer.Layer}:" +
                    $"complete={layer.EvidenceComplete}:" +
                    $"delta={layer.DeltaState}:" +
                    $"compared={layer.ComparisonCount}:" +
                    $"nonzero={layer.NonzeroCount}:" +
                    $"missing={layer.MissingCount}:" +
                    $"extra={layer.ExtraCount}:" +
                    $"attribution={layer.AttributionState}") ?? []);
            logger.LogWarning(
                "P10 trusted verdict is not MATCHED. Verdict={Verdict}; FailureKind={FailureKind}; CompleteEvidence={CompleteEvidence}; AllRequiredLayersZero={AllRequiredLayersZero}; MissingOrExtraIdentity={MissingOrExtraIdentity}; UnknownBlocksCloseout={UnknownBlocksCloseout}; RootCauseClass={RootCauseClass}; Layers={Layers}",
                diagnosticDecision.Verdict,
                diagnosticDecision.FailureKind,
                diagnosticDecision.CompleteEvidence,
                diagnosticDecision.AllRequiredLayersZero,
                diagnosticDecision.MissingOrExtraIdentity,
                diagnosticDecision.UnknownBlocksCloseout,
                diagnosticDecision.RootCauseClass,
                layerSummary);
        }
        var finalizeCommand = new StatisticReconciliationTrustedFinalizeCommand(
            run.Id,
            command.ExpectedStateRevision,
            command.ExpectedStateHash,
            command.ExpectedGenerationPublishRevision,
            request,
            currentRead.CurrentP8Configuration);
        return isRecheck
            ? await recheckFinalizer.FinalizePendingRecheckAsync(
                    finalizeCommand, systemActor, cancellationToken)
                .ConfigureAwait(false)
            : await finalizer.FinalizePendingAsync(
                    finalizeCommand, systemActor, cancellationToken)
                .ConfigureAwait(false);
    }

    internal static StatisticReconciliationTrustedVerdictCommand CreateCommand(
        string reconciliationId,
        string actualGenerationId,
        string actualGenerationSha256,
        long expectedStateRevision,
        string expectedStateHash,
        long expectedGenerationPublishRevision)
    {
        var commandId = H(
            "P10_TRUSTED_VERDICT_COMMAND_V1",
            reconciliationId,
            actualGenerationId,
            actualGenerationSha256,
            I(expectedStateRevision),
            expectedStateHash,
            I(expectedGenerationPublishRevision));
        return new StatisticReconciliationTrustedVerdictCommand(
            commandId,
            reconciliationId,
            actualGenerationId,
            actualGenerationSha256,
            expectedStateRevision,
            expectedStateHash,
            expectedGenerationPublishRevision);
    }

    private async Task<StatisticReconciliationTrustedFinalizeResult>
        StableReplayAsync(
            StatisticReconciliationRun run,
            StatisticReconciliationTrustedVerdictCommand command,
            bool requireRecheckReceipt,
            CancellationToken cancellationToken)
    {
        var verdicts = await context.StatisticReconciliationReviews
            .Find(value =>
                value.ReconciliationId == run.Id &&
                value.RecordKind ==
                    StatisticReconciliationReviewKinds.FinalVerdict &&
                value.ActualGenerationId == command.ActualGenerationId &&
                value.ActualGenerationSha256 ==
                    command.ActualGenerationSha256)
            .SortBy(value => value.VerdictGenerationId)
            .Limit(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (verdicts.Count != 1)
            throw Fail("STABLE_REPLAY_VERDICT_CARDINALITY_INVALID");
        var verdict = verdicts[0];
        StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);
        if (requireRecheckReceipt)
        {
            var receipt = run.CurrentRecheckFinalizeReceipt ??
                throw Fail("STABLE_REPLAY_RECEIPT_MISSING");
            if (!StatisticReconciliationRunService
                    .HasValidRecheckFinalizeReceipt(run) ||
                receipt.SuccessorActualGenerationId !=
                    command.ActualGenerationId ||
                receipt.SuccessorActualGenerationSha256 !=
                    command.ActualGenerationSha256 ||
                receipt.SuccessorVerdictGenerationId !=
                    verdict.VerdictGenerationId ||
                receipt.SuccessorVerdictGenerationSha256 !=
                    verdict.VerdictGenerationSha256)
                throw Fail("STABLE_REPLAY_RECEIPT_MISMATCH");
        }
        if (run.Status !=
            StatisticReconciliationRunService.FinalRunStatus(verdict))
            throw Fail("STABLE_REPLAY_STATUS_MISMATCH");
        return new StatisticReconciliationTrustedFinalizeResult(
            true, run.Id, run.Status, run.StateRevision, run.StateHash,
            run.GenerationPublishRevision, command.ActualGenerationId,
            command.ActualGenerationSha256, verdict);
    }

    private async Task<ExpectedGeneration> LoadExactExpectedGenerationAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationActualAppendResult actual,
        CancellationToken cancellationToken)
    {
        var exact = ExactExpectedBinding(actual);
        if (exact.ReconciliationId != run.Id)
            throw Fail("EXPECTED_RECONCILIATION_BINDING_MISMATCH");

        StatisticReconciliationExpectedGenerationBinding resolved;
        try
        {
            resolved = await expectedBindingReader.ResolveAsync(
                    run,
                    exact.GenerationId,
                    exact.GenerationSemanticSha256,
                    exact.MetricPlanSha256,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw Fail("EXPECTED_GENERATION_BINDING_INVALID", error);
        }
        if (resolved != exact)
            throw Fail("EXPECTED_GENERATION_BINDING_MISMATCH");

        var documents = await
            StatisticReconciliationExpectedMongoGenerationBindingReader
                .ReadGenerationAsync(
                    context,
                    run.Id,
                    exact.GenerationId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (documents.Count is < 1 ||
            documents.Count >
                StatisticReconciliationExpectedObservationIntegrity
                    .MaxGenerationDocuments)
            throw Fail("EXPECTED_GENERATION_DOCUMENT_CARDINALITY_INVALID");

        StatisticReconciliationObservation commit;
        try
        {
            commit = StatisticReconciliationExpectedObservationIntegrity
                .ValidateGeneration(documents);
            StatisticReconciliationExpectedMongoProjectionInputReader
                .RequireExactBinding(
                    commit,
                    commit.Commit ?? throw Fail("EXPECTED_COMMIT_MISSING"),
                    exact);
        }
        catch (Exception error) when (error is
            StatisticReconciliationExpectedObservationIntegrityException or
            StatisticReconciliationExpectedLedgerInputException or
            StatisticReconciliationExpectedProjectionInputException or
            StatisticReconciliationTrustedVerdictPipelineException or
            OverflowException)
        {
            throw Fail("EXPECTED_GENERATION_INVALID", error);
        }

        return new ExpectedGeneration(
            exact,
            commit,
            documents
                .Where(value => value.RecordKind ==
                    StatisticReconciliationObservationRecordKinds.ExpectedAtom)
                .Select(value => value.Atom ??
                    throw Fail("EXPECTED_ATOM_MISSING"))
                .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
                .ThenBy(value => value.TransitionLeg, StringComparer.Ordinal)
                .ThenBy(value => value.TransitionKind, StringComparer.Ordinal)
                .ThenBy(value => value.AtomKind, StringComparer.Ordinal)
                .ThenBy(value => value.ValueIdentitySha256, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private async Task<ExpectedCurrentRead> ReadExpectedCurrentAsync(
        StatisticReconciliationRun run,
        StatisticReconciliationExpectedGenerationBinding exact,
        CancellationToken cancellationToken)
    {
        try
        {
            var proof = await expectedCurrentValidator.ValidateAsync(
                    run, exact, cancellationToken)
                .ConfigureAwait(false);
            if (!ValidExpectedCurrentProof(proof, exact))
                return ExpectedCurrentRead.Incomplete(
                    H("P10_TRUSTED_EXPECTED_CURRENT_INVALID_V1",
                        exact.GenerationId,
                        exact.GenerationSemanticSha256,
                        run.StateHash),
                    "EXPECTED_AUTHORITATIVE_CURRENT_INVALID");
            var proofSha = H(
                "P10_TRUSTED_EXPECTED_CURRENT_EVIDENCE_V1",
                proof.DoubleCollectProofSha256,
                proof.FirstCollectProofSha256,
                proof.SecondCollectProofSha256,
                proof.CurrentGenerationId,
                proof.CurrentGenerationSemanticSha256,
                proof.CurrentMetricPlanSha256,
                I(proof.CurrentMetricPlanEntryCount),
                proof.CurrentMembershipSemanticSha256,
                proof.CurrentInputBindingSha256,
                proof.CurrentLifecycleSemanticSha256,
                proof.CurrentContributionSemanticSha256,
                proof.Complete ? "1" : "0",
                proof.Current ? "1" : "0");
            return !proof.Complete
                ? ExpectedCurrentRead.Incomplete(
                    proofSha, "EXPECTED_AUTHORITATIVE_CURRENT_INCOMPLETE")
                : new ExpectedCurrentRead(
                    true,
                    proof.Current,
                    proofSha,
                    proof.Current
                        ? "EXPECTED_AUTHORITATIVE_CURRENT_PROVEN"
                        : "EXPECTED_AUTHORITATIVE_CURRENT_DRIFT");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ExpectedCurrentRead.Incomplete(
                H("P10_TRUSTED_EXPECTED_CURRENT_UNAVAILABLE_V1",
                    exact.GenerationId,
                    exact.GenerationSemanticSha256,
                    exact.MetricPlanSha256,
                    exact.MembershipSemanticSha256,
                    run.StateHash),
                "EXPECTED_AUTHORITATIVE_CURRENT_UNAVAILABLE");
        }
    }

    internal static StatisticReconciliationTrustedCurrentOwnerRead
        RequireStableCurrent(
            StatisticReconciliationTrustedCurrentOwnerRead first,
            StatisticReconciliationTrustedCurrentOwnerRead second)
    {
        if (first == second)
            return first;
        return second with
        {
            FenceSha256 = H(
                "P10_TRUSTED_CURRENT_OWNER_CROSS_REPLAY_CHANGED_V1",
                first.FenceSha256,
                second.FenceSha256),
            EvidenceComplete = false,
            CaptureCoherent = false,
            State = "AUTHORITATIVE_CURRENT_CROSS_REPLAY_CHANGED"
        };
    }

    private static ExpectedCurrentRead RequireStableExpectedCurrent(
        ExpectedCurrentRead first,
        ExpectedCurrentRead second,
        StatisticReconciliationExpectedGenerationBinding exact)
    {
        if (first == second)
            return first;
        return ExpectedCurrentRead.Incomplete(
            H(
                "P10_TRUSTED_EXPECTED_CURRENT_CROSS_REPLAY_CHANGED_V1",
                first.ProofSha256,
                first.State,
                second.ProofSha256,
                second.State,
                exact.GenerationId,
                exact.GenerationSemanticSha256),
            "EXPECTED_AUTHORITATIVE_CURRENT_CROSS_REPLAY_CHANGED");
    }

    private static StatisticReconciliationTrustedCurrentOwnerRead
        BindExpectedCurrent(
            StatisticReconciliationTrustedCurrentOwnerRead current,
            ExpectedCurrentRead expected,
            StatisticReconciliationExpectedGenerationBinding exact)
    {
        var fence = ExpectedCurrentFenceSha256(
            current,
            expected.ProofSha256,
            expected.State,
            exact);
        if (!expected.Complete)
            return current with
            {
                FenceSha256 = fence,
                EvidenceComplete = false,
                CaptureCoherent = false,
                State = expected.State
            };
        if (expected.Current)
            return current with
            {
                FenceSha256 = fence,
                State = expected.State
            };

        var drift = H(
            "P10_TRUSTED_EXPECTED_AUTHORITATIVE_DRIFT_V1",
            expected.ProofSha256,
            exact.GenerationId,
            exact.MembershipSemanticSha256);
        return current with
        {
            CurrentOwners = current.CurrentOwners with
            {
                Binding = current.CurrentOwners.Binding with
                {
                    MembershipSemanticSha256 = drift
                }
            },
            FenceSha256 = fence,
            State = expected.State
        };
    }

    internal static string ExpectedCurrentFenceSha256(
        StatisticReconciliationTrustedCurrentOwnerRead current,
        string expectedProofSha256,
        string expectedState,
        StatisticReconciliationExpectedGenerationBinding exact)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(exact);
        if (!Sha(current.FenceSha256) ||
            !Sha(current.RelationalProofBindingSha256) ||
            !Sha(expectedProofSha256) ||
            string.IsNullOrWhiteSpace(expectedState) ||
            expectedState != expectedState.Trim() ||
            !Sha(exact.GenerationId) ||
            !Sha(exact.GenerationSemanticSha256) ||
            !Sha(exact.MetricPlanSha256) ||
            !Sha(exact.MembershipSemanticSha256))
            throw Fail("EXPECTED_CURRENT_FENCE_INVALID");
        return H(
            "P10_TRUSTED_CURRENT_WITH_EXPECTED_V2",
            current.FenceSha256,
            current.RelationalProofBindingSha256,
            expectedProofSha256,
            expectedState,
            exact.GenerationId,
            exact.GenerationSemanticSha256,
            exact.MetricPlanSha256,
            exact.MembershipSemanticSha256);
    }

    internal static StatisticReconciliationExpectedGenerationBinding
        ExactExpectedBinding(StatisticReconciliationActualAppendResult actual)
    {
        var committed = actual.CommittedRunBinding.SummaryPlanBinding ??
            throw Fail("COMMITTED_EXPECTED_BINDING_MISSING");
        var normalized = StatisticReconciliationActualSummaryPlanBinding
            .Normalize(committed);
        if (actual.SummaryPlanBinding is null ||
            StatisticReconciliationActualSummaryPlanBinding
                .Normalize(actual.SummaryPlanBinding).SemanticSha256 !=
            normalized.SemanticSha256)
            throw Fail("ACTUAL_EXPECTED_BINDING_MISMATCH");
        return new StatisticReconciliationExpectedGenerationBinding(
            normalized.ExpectedReconciliationId,
            normalized.ExpectedGenerationId,
            normalized.ExpectedGenerationSha256,
            normalized.ExpectedMetricPlanSha256,
            normalized.ExpectedMetricPlanEntryCount,
            normalized.ExpectedManifestSha256,
            normalized.ExpectedDocumentCount,
            normalized.ExpectedMembershipSemanticSha256,
            normalized.ExpectedRuntimeKind);
    }

    internal static bool ValidExpectedCurrentProof(
        StatisticReconciliationExpectedAuthoritativeCurrentProof? proof,
        StatisticReconciliationExpectedGenerationBinding exact)
        => proof is not null && proof.Binding == exact &&
           proof.CurrentMetricPlanEntryCount > 0 &&
           Sha(proof.CurrentGenerationId) &&
           Sha(proof.CurrentGenerationSemanticSha256) &&
           Sha(proof.CurrentMetricPlanSha256) &&
           Sha(proof.CurrentMembershipSemanticSha256) &&
           Sha(proof.CurrentInputBindingSha256) &&
           Sha(proof.CurrentLifecycleSemanticSha256) &&
           Sha(proof.CurrentContributionSemanticSha256) &&
           Sha(proof.FirstCollectProofSha256) &&
           Sha(proof.SecondCollectProofSha256) &&
           Sha(proof.DoubleCollectProofSha256);
    internal static void RequireExpectedRunBinding(
        StatisticReconciliationRun run,
        StatisticReconciliationObservation commit)
    {
        var pins = commit.CatalogPins;
        var coherent = commit.ReconciliationId == run.Id &&
            commit.ImmutableIdentitySha256 == run.ImmutableIdentityHash &&
            commit.ImmutableHeaderSha256 == run.ImmutableHeaderHash &&
            commit.TenantUnitId == run.TenantUnitId &&
            commit.WorkId == run.WorkId &&
            commit.ScopeAssignmentId == run.ScopeAssignmentId &&
            commit.PeriodKey == run.PeriodKey &&
            commit.PeriodInstanceKey == run.PeriodInstanceKey &&
            commit.ConceptKey == run.ConceptKey &&
            commit.Grain == run.Grain && commit.TimeAxis == run.TimeAxis &&
            commit.FilterSha256 == run.FilterHash &&
            commit.DynamicFormVersionId == run.DynamicFormVersionId &&
            commit.DynamicFormSchemaSha256 == run.DynamicFormSchemaHash &&
            commit.FlowTemplateVersionId == run.FlowTemplateVersionId &&
            commit.FlowPayloadSha256 == run.FlowPayloadHash &&
            commit.FlowInstanceId == run.FlowInstanceId &&
            commit.ExecutionEpochId == run.FlowExecutionEpochId &&
            commit.ExecutionEpoch == run.FlowExecutionEpoch &&
            commit.ExecutionEpochRevision == run.FlowExecutionEpochRevision &&
            commit.P8ConfigurationOwnerId == run.P8ConfigOwnerId &&
            commit.P8ConfigurationBundleSha256 == run.P8ConfigBundleHash &&
            pins.P9CatalogVersion == run.P9CatalogVersion &&
            pins.P9CatalogRawSha256 == run.P9CatalogRawSha256 &&
            pins.P9CatalogSemanticSha256 == run.P9CatalogSemanticSha256 &&
            pins.P9SchemaRawSha256 == run.P9SchemaRawSha256 &&
            pins.P9SchemaSemanticSha256 == run.P9SchemaSemanticSha256 &&
            pins.P9StageLockSha256 == run.P9StageLockSha256 &&
            pins.CandidateChainId == run.CandidateChainId &&
            pins.CandidatePromptId == run.CandidatePromptId &&
            pins.CandidateCatalogVersion == run.CandidateCatalogVersion &&
            pins.CandidateCatalogRawSha256 == run.CandidateCatalogRawSha256 &&
            pins.CandidateCatalogSemanticSha256 ==
                run.CandidateCatalogSemanticSha256 &&
            pins.CandidateSchemaRawSha256 == run.CandidateSchemaRawSha256 &&
            pins.CandidateSchemaSemanticSha256 ==
                run.CandidateSchemaSemanticSha256 &&
            pins.CandidateStageLockSha256 == run.CandidateStageLockSha256;
        if (!coherent)
            throw Fail("EXPECTED_GENERATION_RUN_BINDING_MISMATCH");
    }

    private static void RequireCommand(
        StatisticReconciliationTrustedVerdictCommand command)
    {
        if (command.ExpectedStateRevision < 1 ||
            command.ExpectedGenerationPublishRevision < 1 ||
            !Sha(command.CommandId) || !Sha(command.ActualGenerationId) ||
            !Sha(command.ActualGenerationSha256) ||
            !Sha(command.ExpectedStateHash))
            throw Fail("COMMAND_INVALID");
        var expected = CreateCommand(
            command.ReconciliationId,
            command.ActualGenerationId,
            command.ActualGenerationSha256,
            command.ExpectedStateRevision,
            command.ExpectedStateHash,
            command.ExpectedGenerationPublishRevision);
        if (expected != command)
            throw Fail("COMMAND_SEMANTIC_MISMATCH");
    }

    private static bool Sha(string? value)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string H(string domain, params string[] fields)
        => StatisticReconciliationFinalVerdictEvaluator.HashFields(domain, fields);

    private static string I(long value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static StatisticReconciliationTrustedVerdictPipelineException Fail(
        string reason,
        Exception? inner = null)
        => new(reason, inner);

    private sealed record ExpectedGeneration(
        StatisticReconciliationExpectedGenerationBinding Binding,
        StatisticReconciliationObservation Commit,
        ImmutableArray<StatisticReconciliationObservationAtom> Atoms);

    private sealed record ExpectedCurrentRead(
        bool Complete,
        bool Current,
        string ProofSha256,
        string State)
    {
        internal static ExpectedCurrentRead Incomplete(
            string proofSha256,
            string state)
            => new(false, false, proofSha256, state);
    }
}

internal static partial class StatisticReconciliationTrustedVerdictDeriver
{
    private const string OpaqueMetricPrefix = "OWNER_OPAQUE:";
    private static readonly StatisticReconciliationStatefulComparator Comparator = new();
    private static readonly StatisticReconciliationRootCauseClassifier RootCause = new();

    private static StatisticReconciliationFinalVerdictRequest DeriveLegacy(
        StatisticReconciliationRun run,
        StatisticReconciliationObservation expectedCommit,
        ImmutableArray<StatisticReconciliationObservationAtom> expectedAtoms,
        StatisticReconciliationActualAppendResult actual)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(expectedCommit);
        ArgumentNullException.ThrowIfNull(actual);
        if (expectedAtoms.IsDefault || actual.TypedObservations.IsDefault)
            throw Fail("TYPED_ATOMS_UNINITIALIZED");

        var expectedBinding = new StatisticReconciliationComparisonBindingPins(
            expectedCommit.SourceSetSha256,
            expectedCommit.P8ConfigurationBundleSha256,
            RequiredSha(run.ActualConfigurationBundleSha256,
                "RUN_ACTUAL_CONFIGURATION_PIN_MISSING"),
            StatisticReconciliationTrustedCatalogComparison.FromExpected(
                expectedCommit.CatalogPins),
            actual.CapturedRuntimePinSetSha256);
        var capturedBinding = new StatisticReconciliationComparisonBindingPins(
            actual.CapturedSourceSetSha256,
            actual.CommittedRunBinding.P8ConfigurationBundleSha256,
            actual.CommittedRunBinding.ActualConfigurationBundleSha256,
            StatisticReconciliationTrustedCatalogComparison.FromActual(
                actual.CommittedRunBinding.CatalogPins),
            actual.CapturedRuntimePinSetSha256);
        var capturedPins = new StatisticReconciliationActualFreshnessPins(
            capturedBinding,
            actual.CapturedResultPinSetSha256,
            actual.GenerationSemanticSha256,
            actual.CapturedExportPinSetSha256);
        var freshness = new StatisticReconciliationFreshnessEvaluator().Evaluate(
            new StatisticReconciliationFreshnessRequest(
                expectedBinding,
                capturedPins,
                capturedPins,
                ActualGenerationComplete: true,
                RequiredLayersComplete: true,
                CaptureCoherent: true));
        var comparisonBindingSha256 = freshness.ExpectedBindingSha256;
        var permission = RootCause.CreatePermissionEvidence(
            StatisticReconciliationRootCausePermissionStates.Authorized,
            run.AuthorizationSnapshotHash);

        ImmutableArray<StatisticReconciliationRootCauseLayerEvidence> layers;
        if (freshness.State != StatisticReconciliationFreshnessStates.Fresh)
        {
            layers = [];
        }
        else
        {
            var output = ImmutableArray.CreateBuilder<
                StatisticReconciliationRootCauseLayerEvidence>(8);
            output.Add(SourceLayer(
                comparisonBindingSha256,
                expectedCommit.SourceSetSha256,
                actual.CapturedSourceSetSha256));
            output.Add(TypedLayer(1, comparisonBindingSha256,
                expectedAtoms.Where(value => value.Family == "DIRECT"),
                actual.TypedObservations.Where(value =>
                    value.Layer == "DIRECT_PROJECTION")));
            output.Add(TypedLayer(2, comparisonBindingSha256,
                expectedAtoms.Where(value => value.Family == "DIRECT"),
                actual.TypedObservations.Where(value => value.Layer == "AGGREGATE")));
            output.Add(TypedLayer(3, comparisonBindingSha256,
                expectedAtoms.Where(value => value.Family == "BASIC"),
                actual.TypedObservations.Where(value => value.Layer == "BASIC")));
            output.Add(TypedLayer(4, comparisonBindingSha256,
                expectedAtoms.Where(value => value.Family == "ADVANCED"),
                actual.TypedObservations.Where(value => value.Layer == "ADVANCED")));
            output.Add(TypedLayer(5, comparisonBindingSha256,
                expectedAtoms.Where(value => value.Family == "DIFF"),
                actual.TypedObservations.Where(value => value.Layer == "DIFF")));
            output.Add(TypedLayer(6, comparisonBindingSha256, [],
                actual.TypedObservations.Where(value => value.Layer == "API")));
            output.Add(TypedLayer(7, comparisonBindingSha256, [],
                actual.TypedObservations.Where(value => value.Layer == "EXPORT")));
            layers = output.MoveToImmutable();
        }

        var rootRequest = new StatisticReconciliationRootCauseRequest(
            comparisonBindingSha256,
            permission,
            freshness,
            layers);
        var deltaManifest = H(
            "P10_TRUSTED_DELTA_MANIFEST_V2",
            layers.Select(value => value.DeltaManifestSha256).ToArray());
        return new StatisticReconciliationFinalVerdictRequest(
            run.Id,
            comparisonBindingSha256,
            expectedCommit.GenerationId,
            expectedCommit.GenerationSemanticSha256,
            actual.GenerationId,
            actual.GenerationSemanticSha256,
            deltaManifest,
            permission,
            rootRequest,
            StatisticReconciliationFinalVerdictFailureKinds.None,
            null);
    }

    private static StatisticReconciliationRootCauseLayerEvidence SourceLayer(
        string binding,
        string expectedSourceSet,
        string actualSourceSet,
        StatisticReconciliationTrustedLifecycleProof? lifecycleProof = null)
    {
        var identity = new StatisticReconciliationStableIdentity(
            StatisticReconciliationStableIdentityKinds.Row,
            ["SOURCE_MEMBERSHIP", "SOURCE_SET"]);
        var expected = new StatisticReconciliationStatefulObservation(
            identity, "TEXT", "VALUE", expectedSourceSet);
        var actual = new StatisticReconciliationStatefulObservation(
            identity, "TEXT", "VALUE", actualSourceSet);
        if (lifecycleProof is null)
            return BuildLayer(0, binding,
                H("P10_TRUSTED_EXPECTED_SOURCE_LAYER_V1", expectedSourceSet),
                H("P10_TRUSTED_ACTUAL_SOURCE_LAYER_V1", actualSourceSet),
                [("source", expected, actual,
                    0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L)]);

        var lifecycleIdentity = new StatisticReconciliationStableIdentity(
            StatisticReconciliationStableIdentityKinds.Row,
            ["SOURCE_MEMBERSHIP", "LIFECYCLE"]);
        var expectedLifecycle = new StatisticReconciliationStatefulObservation(
            lifecycleIdentity, "TEXT", "VALUE",
            StatisticReconciliationLifecycleOutcomes.Matched);
        var actualLifecycle = new StatisticReconciliationStatefulObservation(
            lifecycleIdentity, "TEXT", "VALUE", lifecycleProof.Outcome);
        return BuildLayer(0, binding,
            H("P10_TRUSTED_EXPECTED_SOURCE_LAYER_V2", expectedSourceSet,
                lifecycleProof.ManifestSha256 ?? "~",
                StatisticReconciliationLifecycleOutcomes.Matched),
            H("P10_TRUSTED_ACTUAL_SOURCE_LAYER_V2", actualSourceSet,
                lifecycleProof.ProofSha256),
            [
                ("source", expected, actual,
                    0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L),
                ("lifecycle", expectedLifecycle, actualLifecycle,
                    0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L)
            ]);
    }

    private static StatisticReconciliationRootCauseLayerEvidence TypedLayer(
        int ordinal,
        string binding,
        IEnumerable<StatisticReconciliationObservationAtom> expected,
        IEnumerable<StatisticReconciliationActualTypedObservation> actual)
    {
        var left = expected.Select(Flatten).ToArray();
        var right = actual.Select(Flatten).ToArray();
        var expectedSha = H("P10_TRUSTED_EXPECTED_LAYER_V2",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            H("P10_TRUSTED_EXPECTED_ATOMS_V2",
                left.Select(value => value.SemanticSha256).ToArray()));
        var actualSha = H("P10_TRUSTED_ACTUAL_LAYER_V2",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            H("P10_TRUSTED_ACTUAL_ATOMS_V2",
                right.Select(value => value.SemanticSha256).ToArray()));
        var leftMap = Unique(left, "EXPECTED_DUPLICATE_COMPARISON_IDENTITY");
        var rightMap = Unique(right, "ACTUAL_DUPLICATE_COMPARISON_IDENTITY");
        var pairs = leftMap.Keys.Union(rightMap.Keys, StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(key =>
            {
                leftMap.TryGetValue(key, out var l);
                rightMap.TryGetValue(key, out var r);
                return (key, l?.Observation, r?.Observation,
                    l?.OccurrenceCount ?? 0, r?.OccurrenceCount ?? 0,
                    l?.ReportCount ?? 0, r?.ReportCount ?? 0,
                    l?.RowCount ?? 0, r?.RowCount ?? 0,
                    l?.NumericValueCount ?? 0, r?.NumericValueCount ?? 0);
            }).ToArray();
        return BuildLayer(ordinal, binding, expectedSha, actualSha, pairs);
    }

    private static StatisticReconciliationRootCauseLayerEvidence BuildLayer(
        int ordinal,
        string binding,
        string expectedSha,
        string actualSha,
        IEnumerable<(string Key,
            StatisticReconciliationStatefulObservation? Expected,
            StatisticReconciliationStatefulObservation? Actual,
            long ExpectedOccurrence, long ActualOccurrence,
            long ExpectedReport, long ActualReport,
            long ExpectedRow, long ActualRow,
            long ExpectedNumeric, long ActualNumeric)> source)
    {
        var comparisons = source.Select(value =>
        {
            var typed = Comparator.Compare(value.Expected, value.Actual);
            var countsEqual = value.ExpectedOccurrence == value.ActualOccurrence &&
                value.ExpectedReport == value.ActualReport &&
                value.ExpectedRow == value.ActualRow &&
                value.ExpectedNumeric == value.ActualNumeric;
            var comparisonSha = H("P10_TRUSTED_ATOM_COMPARISON_V2",
                value.Key, typed.ComparisonSha256,
                I(value.ExpectedOccurrence), I(value.ActualOccurrence),
                I(value.ExpectedReport), I(value.ActualReport),
                I(value.ExpectedRow), I(value.ActualRow),
                I(value.ExpectedNumeric), I(value.ActualNumeric));
            return new AtomComparison(
                typed,
                comparisonSha,
                typed.Equal && countsEqual);
        }).OrderBy(value => value.Typed.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(value => value.ComparisonSha256, StringComparer.Ordinal)
            .ToArray();
        var nonzero = comparisons.Where(value => !value.Equal).ToArray();
        var identities = nonzero
            .Where(value => value.Typed.DeltaCode is
                StatisticReconciliationIdentityDeltaCodes.MissingIdentity or
                StatisticReconciliationIdentityDeltaCodes.ExtraIdentity)
            .Select(value => RootCause.CreateIdentityEvidence(
                value.Typed.DeltaCode,
                value.Typed.IdentitySha256,
                value.ComparisonSha256))
            .OrderBy(value => value.DeltaCode ==
                StatisticReconciliationIdentityDeltaCodes.MissingIdentity ? 0 : 1)
            .ThenBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(value => value.ComparisonSha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var missing = identities.Count(value => value.DeltaCode ==
            StatisticReconciliationIdentityDeltaCodes.MissingIdentity);
        var extra = identities.Length - missing;
        var manifest = H("P10_TRUSTED_LAYER_DELTA_MANIFEST_V2",
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            H("P10_TRUSTED_LAYER_COMPARISONS_V2",
                comparisons.Select(value => value.ComparisonSha256).ToArray()));
        return RootCause.CreateLayerEvidence(
            ordinal,
            StatisticReconciliationRootCauseLayers.Ordered[ordinal],
            binding,
            evidenceComplete: true,
            expectedSha,
            actualSha,
            manifest,
            comparisons.Length,
            nonzero.Length,
            missing,
            extra,
            redactedCount: 0,
            nonzero.Length == 0
                ? StatisticReconciliationRootCauseLayerDeltaStates.Zero
                : StatisticReconciliationRootCauseLayerDeltaStates.Nonzero,
            nonzero.Length == 0
                ? StatisticReconciliationRootCauseAttributionStates.NotRequired
                : StatisticReconciliationRootCauseAttributionStates.Proven,
            nonzero.Length > 0 && identities.Length == 0 ? manifest : null,
            null,
            identities);
    }

    private static Dictionary<string, FlatAtom> Unique(
        IEnumerable<FlatAtom> atoms,
        string reason)
    {
        var result = new Dictionary<string, FlatAtom>(StringComparer.Ordinal);
        foreach (var atom in atoms)
            if (!result.TryAdd(atom.Key, atom))
                throw Fail(reason);
        return result;
    }

    private static FlatAtom Flatten(StatisticReconciliationObservationAtom atom)
        => Flat(atom.Family, atom.Kind, atom.MetricId, atom.PeriodKey!,
            atom.FieldId, atom.TableId, atom.RowId, atom.LabelId,
            atom.BasicScope, atom.BasicScopeId, atom.AdvancedGrain,
            atom.DiffKind, atom.TransitionLeg ?? "NONE",
            atom.TransitionKind, atom.CollectionSemantics,
            atom.AtomKind, IsCountAtom(atom.AtomKind)
                ? "NUMBER"
                : atom.ValueType, atom.ValueState,
            atom.CanonicalValue, atom.DecimalScale, atom.OccurrenceCount,
            atom.ReportCount, atom.RowCount, atom.NumericValueCount,
            atom.AtomSemanticSha256);

    private static bool IsCountAtom(string atomKind)
        => atomKind is "REPORT_COUNT" or "ROW_COUNT" or "COUNT" or
            "NUMERIC_VALUE_COUNT" or "ADDED_COUNT" or "REMOVED_COUNT" or
            "CHANGED_COUNT" or "UNCHANGED_COUNT";

    private static FlatAtom Flatten(
        StatisticReconciliationActualTypedObservation atom)
        => Flat(atom.Family, atom.Kind, atom.MetricId, atom.PeriodKey,
            atom.FieldId, atom.TableId, atom.RowId, atom.LabelId,
            atom.BasicScope, atom.BasicScopeId, atom.AdvancedGrain,
            atom.DiffKind, atom.TransitionLeg, atom.TransitionKind,
            atom.CollectionSemantics, atom.AtomKind, atom.ValueType,
            atom.ValueState,
            atom.CanonicalValue, atom.DecimalScale, atom.OccurrenceCount,
            atom.ReportCount, atom.RowCount, atom.NumericValueCount,
            atom.AtomSemanticSha256);

    private static FlatAtom Flat(
        string family, string kind, string metricId, string periodKey,
        string? fieldId, string? tableId, string? rowId, string? labelId,
        string? scope, string? scopeId, string? grain, string? diffKind,
        string transitionLeg, string? transitionKind,
        string? collectionSemantics,
        string atomKind, string valueType, string valueState,
        string canonicalValue, int scale, long occurrence, long reports,
        long rows, long numeric, string semanticSha)
    {
        var discriminator = IsDistinctValueAtom(atomKind) &&
            valueState == StatisticReconciliationObservationValueStates.Value
                ? canonicalValue
                : "~";
        var components = new[]
        {
            family, kind, metricId, periodKey, fieldId ?? "~", tableId ?? "~",
            rowId ?? "~", labelId ?? "~", scope ?? "~", scopeId ?? "~",
            grain ?? "~", diffKind ?? "~", transitionLeg,
            transitionKind ?? "~", collectionSemantics ?? "~", atomKind,
            discriminator
        };
        var key = H("P10_TRUSTED_TYPED_IDENTITY_KEY_V2", components);
        var identityKind = kind switch
        {
            "TABLE" => StatisticReconciliationStableIdentityKinds.Table,
            "ROW_LABEL" => StatisticReconciliationStableIdentityKinds.Label,
            _ => StatisticReconciliationStableIdentityKinds.Row
        };
        var collection = valueType == "STRING_LIST"
            ? collectionSemantics switch
            {
                "UNORDERED" =>
                    StatisticReconciliationCollectionSemantics.Unordered,
                "ORDERED" =>
                    StatisticReconciliationCollectionSemantics.Ordered,
                _ => throw Fail("STRING_LIST_COLLECTION_SEMANTICS_REQUIRED")
            }
            : collectionSemantics is null
                ? null
                : throw Fail("NON_LIST_COLLECTION_SEMANTICS_INVALID");
        var observation = new StatisticReconciliationStatefulObservation(
            new StatisticReconciliationStableIdentity(identityKind,
                components.Append(key).ToArray()),
            valueType,
            valueState,
            valueState == StatisticReconciliationObservationValueStates.Value
                ? canonicalValue
                : null,
            valueState == StatisticReconciliationObservationValueStates.Value
                ? scale
                : 0,
            valueState == StatisticReconciliationObservationValueStates.Value
                ? collection
                : null);
        _ = Comparator.Compare(observation, observation);
        return new FlatAtom(key, observation, occurrence, reports, rows,
            numeric, semanticSha);
    }

    private static bool IsDistinctValueAtom(string atomKind)
        => atomKind is "BUCKET" or "DATE" or "FULL_DATE" or "PERIOD" or
            "BOOLEAN" or "ENUM" or "STRING_LIST" or
            "STRING_LIST_ORDERED" or "STRING_LIST_UNORDERED" or "TEXT";

    private static string RequiredSha(string? value, string reason)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw Fail(reason);

    private static string H(string domain, params string[] fields)
        => StatisticReconciliationFinalVerdictEvaluator.HashFields(domain, fields);

    private static string I(long value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static StatisticReconciliationTrustedVerdictPipelineException Fail(
        string reason)
        => new(reason);

    private sealed record FlatAtom(
        string Key,
        StatisticReconciliationStatefulObservation Observation,
        long OccurrenceCount,
        long ReportCount,
        long RowCount,
        long NumericValueCount,
        string SemanticSha256);

    private sealed record AtomComparison(
        StatisticReconciliationStatefulComparison Typed,
        string ComparisonSha256,
        bool Equal);
}

internal sealed class StatisticReconciliationTrustedVerdictPipelineException
    : Exception
{
    internal StatisticReconciliationTrustedVerdictPipelineException(
        string reason,
        Exception? inner = null)
        : base($"P10_TRUSTED_VERDICT_PIPELINE:{reason}", inner)
        => Reason = reason;

    internal string Reason { get; }
}
