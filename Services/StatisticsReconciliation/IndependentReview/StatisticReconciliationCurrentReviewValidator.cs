using System.Collections.Immutable;
using System.Globalization;
using tdtd_be.Data;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation.IndependentReview;

public sealed record StatisticReconciliationCurrentReviewValidation(
    string ValidationSha256);

public interface IStatisticReconciliationCurrentReviewValidator
{
    Task<StatisticReconciliationCurrentReviewValidation> ValidateAsync(
        StatisticReconciliationRun persistedRun,
        StatisticReconciliationReview verdict,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Re-derives the exact stored verdict from independently re-read current
/// owners. This owner is invoked only after reviewer scope authorization and
/// before any review decision read or write.
/// </summary>
internal sealed class StatisticReconciliationCurrentReviewValidator(
    MongoDbContext context,
    StatisticReconciliationActualGenerationPublisher actualPublisher,
    IStatisticReconciliationTrustedCurrentOwnerReader currentOwnerReader,
    IStatisticReconciliationTrustedLifecycleVerifier lifecycleVerifier,
    IStatisticReconciliationExpectedAuthoritativeCurrentValidator
        expectedCurrentValidator,
    StatisticReconciliationFinalVerdictEvaluator evaluator)
    : IStatisticReconciliationCurrentReviewValidator
{
    public async Task<StatisticReconciliationCurrentReviewValidation>
        ValidateAsync(
            StatisticReconciliationRun persistedRun,
            StatisticReconciliationReview verdict,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistedRun);
        ArgumentNullException.ThrowIfNull(verdict);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            persistedRun);
        StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);
        RequireStableCurrent(persistedRun, verdict);

        var actual = await actualPublisher.ReadCompleteAsync(
                persistedRun.Id,
                persistedRun.CurrentGenerationId!,
                cancellationToken)
            .ConfigureAwait(false) ?? throw Invalid("actualGenerationMissing");
        if (actual.GenerationSemanticSha256 !=
                persistedRun.CurrentGenerationHash ||
            !StatisticReconciliationActualRunBindingGuard.Matches(
                persistedRun, actual.CommittedRunBinding))
            throw Invalid("actualGenerationBinding");

        var relational = actual.RelationalProofBinding is null
            ? throw Invalid("actualRelationalProofMissing")
            : StatisticReconciliationActualRelationalProofBinding.Normalize(
                actual.RelationalProofBinding);
        if (actual.CommittedRunBinding.RelationalProofBinding is null ||
            relational != StatisticReconciliationActualRelationalProofBinding
                .Normalize(actual.CommittedRunBinding.RelationalProofBinding))
            throw Invalid("actualRelationalProofBinding");

        var exact = StatisticReconciliationTrustedVerdictPipeline
            .ExactExpectedBinding(actual);
        if (exact.ReconciliationId != persistedRun.Id)
            throw Invalid("expectedReconciliationBinding");
        var expected = await LoadExpectedAsync(
                persistedRun, exact, cancellationToken)
            .ConfigureAwait(false);

        var lifecycleFirst = await lifecycleVerifier.ReadAsync(
                actual, cancellationToken)
            .ConfigureAwait(false);
        var currentFirst = await currentOwnerReader.ReadCurrentAsync(
                persistedRun, actual, cancellationToken)
            .ConfigureAwait(false);
        var expectedFirst = await expectedCurrentValidator.ValidateAsync(
                persistedRun, exact, cancellationToken)
            .ConfigureAwait(false);

        var lifecycleSecond = await lifecycleVerifier.ReadAsync(
                actual, cancellationToken)
            .ConfigureAwait(false);
        var currentSecond = await currentOwnerReader.ReadCurrentAsync(
                persistedRun, actual, cancellationToken)
            .ConfigureAwait(false);
        var expectedSecond = await expectedCurrentValidator.ValidateAsync(
                persistedRun, exact, cancellationToken)
            .ConfigureAwait(false);

        var lifecycle = lifecycleVerifier.RequireStable(
            lifecycleFirst, lifecycleSecond);
        var current = StatisticReconciliationTrustedVerdictPipeline
            .RequireStableCurrent(currentFirst, currentSecond);
        if (expectedFirst != expectedSecond)
            throw Invalid("expectedAuthoritativeCrossReplayChanged");
        var expectedCurrent = expectedFirst;

        if (!current.EvidenceComplete || !current.CaptureCoherent ||
            current.RelationalProofBindingSha256 !=
                relational.SemanticSha256 ||
            !lifecycle.EvidenceComplete || !lifecycle.CaptureCoherent ||
            !StatisticReconciliationTrustedVerdictPipeline
                .ValidExpectedCurrentProof(expectedCurrent, exact) ||
            !expectedCurrent.Complete || !expectedCurrent.Current)
            throw Invalid("authoritativeCurrentIncomplete");

        var expectedProofSha = ExpectedProofSha(expectedCurrent);
        var fenceSha = StatisticReconciliationTrustedVerdictPipeline
            .ExpectedCurrentFenceSha256(
                current,
                expectedProofSha,
                "EXPECTED_AUTHORITATIVE_CURRENT_PROVEN",
                exact);
        current = current with
        {
            FenceSha256 = fenceSha,
            State = "EXPECTED_AUTHORITATIVE_CURRENT_PROVEN"
        };

        var request = StatisticReconciliationTrustedVerdictDeriver.Derive(
            persistedRun,
            expected.Commit,
            expected.Atoms,
            actual,
            current.CurrentOwners,
            current.FenceSha256,
            current.EvidenceComplete,
            current.CaptureCoherent,
            lifecycle);
        var decision = evaluator.Evaluate(request);
        RequireExactVerdict(verdict, decision);

        return new StatisticReconciliationCurrentReviewValidation(H(
            "P10_REVIEW_CURRENT_VALIDATION_V4",
            persistedRun.ImmutableIdentityHash,
            persistedRun.ImmutableHeaderHash,
            verdict.VerdictGenerationId,
            verdict.VerdictGenerationSha256,
            verdict.DocumentSemanticSha256,
            actual.GenerationId,
            actual.GenerationSemanticSha256,
            actual.ManifestSha256,
            relational.SemanticSha256,
            RequireSha(actual.CapturedLifecycleMetricScopeSha256,
                "actualLifecycleMetricScopeMissing"),
            current.FenceSha256,
            current.RelationalProofBindingSha256,
            expectedProofSha,
            lifecycle.ProofSha256,
            decision.DecisionSemanticSha256));
    }

    private async Task<ExpectedGeneration> LoadExpectedAsync(
        StatisticReconciliationRun persistedRun,
        StatisticReconciliationExpectedGenerationBinding exact,
        CancellationToken cancellationToken)
    {
        var documents = await
            StatisticReconciliationExpectedMongoGenerationBindingReader
                .ReadGenerationAsync(
                    context,
                    persistedRun.Id,
                    exact.GenerationId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (documents.Count is < 1 ||
            documents.Count >
                StatisticReconciliationExpectedObservationIntegrity
                    .MaxGenerationDocuments)
            throw Invalid("expectedGenerationCardinality");
        var commit = StatisticReconciliationExpectedObservationIntegrity
            .ValidateGeneration(documents);
        StatisticReconciliationExpectedMongoProjectionInputReader
            .RequireExactBinding(
                commit,
                commit.Commit ?? throw Invalid("expectedCommitMissing"),
                exact);
        var atoms = documents
            .Where(value => value.RecordKind ==
                StatisticReconciliationObservationRecordKinds.ExpectedAtom)
            .Select(value => value.Atom ??
                throw Invalid("expectedAtomMissing"))
            .OrderBy(value => value.IdentitySha256, StringComparer.Ordinal)
            .ThenBy(value => value.TransitionLeg, StringComparer.Ordinal)
            .ThenBy(value => value.TransitionKind, StringComparer.Ordinal)
            .ThenBy(value => value.AtomKind, StringComparer.Ordinal)
            .ThenBy(value => value.ValueIdentitySha256,
                StringComparer.Ordinal)
            .ToImmutableArray();
        return new ExpectedGeneration(commit, atoms);
    }

    private static void RequireStableCurrent(
        StatisticReconciliationRun run,
        StatisticReconciliationReview verdict)
    {
        var terminal = run.Status is
            StatisticReconciliationRunStatuses.Matched or
            StatisticReconciliationRunStatuses.Mismatched or
            StatisticReconciliationRunStatuses.Stale or
            StatisticReconciliationRunStatuses.Failed;
        if (!terminal || run.Recheck is not null ||
            run.PendingGenerationId is not null ||
            run.PendingGenerationHash is not null ||
            run.PendingGenerationPublishedAtUtc.HasValue ||
            run.CurrentGenerationId != verdict.ActualGenerationId ||
            run.CurrentGenerationHash != verdict.ActualGenerationSha256 ||
            run.Status != StatisticReconciliationRunService
                .FinalRunStatus(verdict) ||
            !verdict.Signable || !verdict.CloseoutAllowed ||
            !verdict.CompleteEvidence || !verdict.AllRequiredLayersZero ||
            verdict.UnknownBlocksCloseout || verdict.MissingOrExtraIdentity)
            throw Invalid("storedVerdictNotSignable");
    }

    private static void RequireExactVerdict(
        StatisticReconciliationReview stored,
        StatisticReconciliationFinalVerdictDecision decision)
    {
        if (!decision.PublicationAllowed || !decision.Signable ||
            stored.ReconciliationId != decision.ReconciliationId ||
            stored.ComparisonBindingSha256 !=
                decision.ComparisonBindingSha256 ||
            stored.ExpectedGenerationId != decision.ExpectedGenerationId ||
            stored.ExpectedGenerationSha256 !=
                decision.ExpectedGenerationSha256 ||
            stored.ActualGenerationId != decision.ActualGenerationId ||
            stored.ActualGenerationSha256 !=
                decision.ActualGenerationSha256 ||
            stored.DeltaManifestSha256 != decision.DeltaManifestSha256 ||
            stored.AuthorizationEvidenceSha256 !=
                decision.AuthorizationEvidenceSha256 ||
            stored.FreshnessAssessmentSha256 !=
                decision.FreshnessAssessmentSha256 ||
            stored.RootCauseClassificationSha256 !=
                decision.RootCauseClassificationSha256 ||
            stored.RootCauseClass != decision.RootCauseClass ||
            stored.FailureKind != decision.FailureKind ||
            stored.FailureEvidenceSha256 != decision.FailureEvidenceSha256 ||
            stored.Verdict != decision.Verdict ||
            stored.CompleteEvidence != decision.CompleteEvidence ||
            stored.AllRequiredLayersZero != decision.AllRequiredLayersZero ||
            stored.MissingOrExtraIdentity != decision.MissingOrExtraIdentity ||
            stored.UnknownBlocksCloseout != decision.UnknownBlocksCloseout ||
            stored.CloseoutAllowed != decision.CloseoutAllowed ||
            stored.Signable != decision.Signable)
            throw Invalid("storedVerdictCurrentReplayMismatch");
    }

    private static string ExpectedProofSha(
        StatisticReconciliationExpectedAuthoritativeCurrentProof proof)
        => H(
            "P10_TRUSTED_EXPECTED_CURRENT_EVIDENCE_V1",
            proof.DoubleCollectProofSha256,
            proof.FirstCollectProofSha256,
            proof.SecondCollectProofSha256,
            proof.CurrentGenerationId,
            proof.CurrentGenerationSemanticSha256,
            proof.CurrentMetricPlanSha256,
            proof.CurrentMetricPlanEntryCount.ToString(
                CultureInfo.InvariantCulture),
            proof.CurrentMembershipSemanticSha256,
            proof.CurrentInputBindingSha256,
            proof.CurrentLifecycleSemanticSha256,
            proof.CurrentContributionSemanticSha256,
            proof.Complete ? "1" : "0",
            proof.Current ? "1" : "0");

    private static string RequireSha(string? value, string reason)
        => value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw Invalid(reason);

    private static string H(string domain, params string[] fields)
        => StatisticReconciliationFinalVerdictEvaluator.HashFields(
            domain, fields);

    private static InvalidOperationException Invalid(string reason)
        => new($"P10_REVIEW_CURRENT_VALIDATION:{reason}");

    private sealed record ExpectedGeneration(
        StatisticReconciliationObservation Commit,
        ImmutableArray<StatisticReconciliationObservationAtom> Atoms);
}
