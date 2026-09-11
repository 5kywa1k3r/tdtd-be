using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Models.StatisticsReconciliation;

namespace tdtd_be.Services.StatisticsReconciliation.TypedDelta;

public sealed class StatisticReconciliationReviewMongoBackend(
    MongoDbContext context) : IStatisticReconciliationReviewBackend
{
    public async Task<StatisticReconciliationReview?> ReadAsync(
        string reconciliationId,
        string verdictGenerationId,
        CancellationToken ct = default)
        => await context.StatisticReconciliationReviews
            .Find(item => item.ReconciliationId == reconciliationId &&
                          item.VerdictGenerationId == verdictGenerationId &&
                          item.RecordKind == StatisticReconciliationReviewKinds.FinalVerdict)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<StatisticReconciliationReview>> ReadLineageAsync(
        string reconciliationId,
        CancellationToken ct = default)
        => await context.StatisticReconciliationReviews
            .Find(item => item.ReconciliationId == reconciliationId &&
                          item.RecordKind == StatisticReconciliationReviewKinds.FinalVerdict)
            .SortBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.VerdictGenerationId)
            .ToListAsync(ct);

    public async Task<bool> TryAppendAsync(
        StatisticReconciliationReview review,
        CancellationToken ct = default)
    {
        try
        {
            await context.StatisticReconciliationReviews.InsertOneAsync(
                review,
                cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException exception)
            when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }
}

public sealed class StatisticReconciliationFinalVerdictPublisher
{
    public const string SchemaVersion = "P10_FINAL_VERDICT_V1";

    private readonly IStatisticReconciliationReviewBackend _backend;
    private readonly StatisticReconciliationFinalVerdictEvaluator _evaluator;

    public StatisticReconciliationFinalVerdictPublisher(
        IStatisticReconciliationReviewBackend backend,
        StatisticReconciliationFinalVerdictEvaluator? evaluator = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _evaluator = evaluator ?? new StatisticReconciliationFinalVerdictEvaluator();
    }

    public async Task<StatisticReconciliationReview> PublishAsync(
        StatisticReconciliationFinalVerdictRequest request,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        var decision = _evaluator.Evaluate(request);
        if (!decision.PublicationAllowed)
            throw Error(StatisticReconciliationFinalVerdictFailureCodes.PermissionDenied,
                "authorizationBeforePublication");
        var review = BuildReview(decision, null, null, createdAtUtc);
        return await AppendOrReplayAsync(review, ct);
    }

    public async Task<StatisticReconciliationReview> PublishSupersedingRecheckAsync(
        string reconciliationId,
        string previousVerdictGenerationId,
        StatisticReconciliationFinalVerdictRequest recheck,
        StatisticReconciliationVerdictRemediationRequest? remediation,
        DateTime createdAtUtc,
        CancellationToken ct = default)
    {
        StatisticReconciliationFinalVerdictEvaluator.RequireSha(
            previousVerdictGenerationId,
            "previousVerdictGenerationId");
        var previous = await _backend.ReadAsync(
            reconciliationId,
            previousVerdictGenerationId,
            ct) ?? throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid,
                "previousVerdictMissing");
        ValidateStored(previous);
        if (previous.ReconciliationId != reconciliationId)
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid,
                "previousVerdictNotSupersedable");

        var decision = _evaluator.Evaluate(recheck);
        if (!decision.PublicationAllowed)
            throw Error(StatisticReconciliationFinalVerdictFailureCodes.PermissionDenied,
                "authorizationBeforeRecheckPublication");
        if (decision.ReconciliationId != reconciliationId ||
            decision.ActualGenerationId == previous.ActualGenerationId ||
            decision.ActualGenerationSha256 == previous.ActualGenerationSha256)
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid,
                "recheckMustBeNewGeneration");

        var identityMismatch = previous.RootCauseClass is
            StatisticReconciliationRootCauseClasses.MissingIdentity or
            StatisticReconciliationRootCauseClasses.ExtraIdentity;
        if (identityMismatch && remediation is null)
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid,
                "identityRemediationRequired");
        var storedRemediation = remediation is null
            ? null
            : NormalizeRemediation(remediation, decision);

        var competing = (await _backend.ReadLineageAsync(reconciliationId, ct))
            .FirstOrDefault(item =>
                item.SupersedesVerdictGenerationId == previous.VerdictGenerationId);
        var review = BuildReview(decision, previous, storedRemediation, createdAtUtc);
        if (competing is not null)
        {
            ValidateStored(competing);
            if (competing.DocumentSemanticSha256 == review.DocumentSemanticSha256)
                return competing;
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid,
                "previousVerdictAlreadySuperseded");
        }
        return await AppendOrReplayAsync(review, ct);
    }

    public static void ValidateStored(StatisticReconciliationReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (review.SchemaVersion != SchemaVersion ||
            review.RecordKind != StatisticReconciliationReviewKinds.FinalVerdict ||
            review.Verdict is not (
                StatisticReconciliationFinalVerdicts.Matched or
                StatisticReconciliationFinalVerdicts.Mismatched or
                StatisticReconciliationFinalVerdicts.Failed))
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.PersistenceInvalid,
                "header");
        StatisticReconciliationFinalVerdictEvaluator.RequireSha(
            review.VerdictGenerationId,
            "stored.verdictGenerationId");
        StatisticReconciliationFinalVerdictEvaluator.RequireSha(
            review.VerdictGenerationSha256,
            "stored.verdictGenerationSha256");
        if ((review.SupersedesVerdictGenerationId is null) !=
            (review.SupersedesVerdictGenerationSha256 is null))
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.PersistenceInvalid,
                "supersessionPair");
        if (review.SupersedesVerdictGenerationId is not null)
        {
            StatisticReconciliationFinalVerdictEvaluator.RequireSha(
                review.SupersedesVerdictGenerationId,
                "stored.supersedesId");
            StatisticReconciliationFinalVerdictEvaluator.RequireSha(
                review.SupersedesVerdictGenerationSha256,
                "stored.supersedesSha");
        }
        var remediationSha = review.Remediation is null
            ? null
            : ValidateStoredRemediation(review.Remediation, review);
        var generationSha = GenerationSha(
            review.ComparisonBindingSha256,
            review.ExpectedGenerationId,
            review.ExpectedGenerationSha256,
            review.ActualGenerationId,
            review.ActualGenerationSha256,
            review.DeltaManifestSha256,
            review.AuthorizationEvidenceSha256,
            review.FreshnessAssessmentSha256,
            review.RootCauseClassificationSha256,
            review.RootCauseClass,
            review.FailureKind,
            review.FailureEvidenceSha256,
            review.Verdict,
            review.CompleteEvidence,
            review.AllRequiredLayersZero,
            review.MissingOrExtraIdentity,
            review.UnknownBlocksCloseout,
            review.CloseoutAllowed,
            review.Signable,
            review.SupersedesVerdictGenerationId,
            review.SupersedesVerdictGenerationSha256,
            remediationSha);
        var generationId = GenerationId(review.ReconciliationId, generationSha);
        var documentSha = DocumentSha(review, generationId, generationSha, remediationSha);
        if (review.VerdictGenerationSha256 != generationSha ||
            review.VerdictGenerationId != generationId ||
            review.Id != generationId ||
            review.DocumentSemanticSha256 != documentSha ||
            review.CreatedAtUtc == default || review.CreatedAtUtc.Kind != DateTimeKind.Utc)
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.PersistenceInvalid,
                "semanticHash");
    }

    private async Task<StatisticReconciliationReview> AppendOrReplayAsync(
        StatisticReconciliationReview review,
        CancellationToken ct)
    {
        ValidateStored(review);
        if (await _backend.TryAppendAsync(review, ct))
            return review;
        var existing = await _backend.ReadAsync(
            review.ReconciliationId,
            review.VerdictGenerationId,
            ct);
        if (existing is null)
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.ReplayMismatch,
                "uniqueConflictWithoutExactGeneration");
        ValidateStored(existing);
        if (existing.DocumentSemanticSha256 != review.DocumentSemanticSha256)
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.ReplayMismatch,
                "sameGenerationDifferentDocument");
        return existing;
    }

    private static StatisticReconciliationReview BuildReview(
        StatisticReconciliationFinalVerdictDecision decision,
        StatisticReconciliationReview? previous,
        StatisticReconciliationVerdictRemediation? remediation,
        DateTime createdAtUtc)
    {
        if (createdAtUtc == default || createdAtUtc.Kind != DateTimeKind.Utc)
            throw StatisticReconciliationFinalVerdictEvaluator.Invalid("createdAtUtc");
        var generationSha = GenerationSha(
            decision.ComparisonBindingSha256,
            decision.ExpectedGenerationId,
            decision.ExpectedGenerationSha256,
            decision.ActualGenerationId,
            decision.ActualGenerationSha256,
            decision.DeltaManifestSha256,
            decision.AuthorizationEvidenceSha256,
            decision.FreshnessAssessmentSha256,
            decision.RootCauseClassificationSha256,
            decision.RootCauseClass,
            decision.FailureKind,
            decision.FailureEvidenceSha256,
            decision.Verdict,
            decision.CompleteEvidence,
            decision.AllRequiredLayersZero,
            decision.MissingOrExtraIdentity,
            decision.UnknownBlocksCloseout,
            decision.CloseoutAllowed,
            decision.Signable,
            previous?.VerdictGenerationId,
            previous?.VerdictGenerationSha256,
            remediation?.RemediationSemanticSha256);
        var generationId = GenerationId(decision.ReconciliationId, generationSha);
        var review = new StatisticReconciliationReview
        {
            Id = generationId,
            SchemaVersion = SchemaVersion,
            RecordKind = StatisticReconciliationReviewKinds.FinalVerdict,
            ReconciliationId = decision.ReconciliationId,
            VerdictGenerationId = generationId,
            VerdictGenerationSha256 = generationSha,
            ComparisonBindingSha256 = decision.ComparisonBindingSha256,
            ExpectedGenerationId = decision.ExpectedGenerationId,
            ExpectedGenerationSha256 = decision.ExpectedGenerationSha256,
            ActualGenerationId = decision.ActualGenerationId,
            ActualGenerationSha256 = decision.ActualGenerationSha256,
            DeltaManifestSha256 = decision.DeltaManifestSha256,
            AuthorizationEvidenceSha256 = decision.AuthorizationEvidenceSha256,
            FreshnessAssessmentSha256 = decision.FreshnessAssessmentSha256,
            RootCauseClassificationSha256 = decision.RootCauseClassificationSha256,
            RootCauseClass = decision.RootCauseClass,
            FailureKind = decision.FailureKind,
            FailureEvidenceSha256 = decision.FailureEvidenceSha256,
            Verdict = decision.Verdict,
            CompleteEvidence = decision.CompleteEvidence,
            AllRequiredLayersZero = decision.AllRequiredLayersZero,
            MissingOrExtraIdentity = decision.MissingOrExtraIdentity,
            UnknownBlocksCloseout = decision.UnknownBlocksCloseout,
            CloseoutAllowed = decision.CloseoutAllowed,
            Signable = decision.Signable,
            SupersedesVerdictGenerationId = previous?.VerdictGenerationId,
            SupersedesVerdictGenerationSha256 = previous?.VerdictGenerationSha256,
            Remediation = remediation,
            CreatedAtUtc = createdAtUtc
        };
        review.DocumentSemanticSha256 = DocumentSha(
            review,
            generationId,
            generationSha,
            remediation?.RemediationSemanticSha256);
        return review;
    }

    private static StatisticReconciliationVerdictRemediation NormalizeRemediation(
        StatisticReconciliationVerdictRemediationRequest value,
        StatisticReconciliationFinalVerdictDecision recheck)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.ReferenceType is not (
                StatisticReconciliationRemediationReferenceTypes.ImplementationPatch or
                StatisticReconciliationRemediationReferenceTypes.AuthorizedP9Operation) ||
            string.IsNullOrWhiteSpace(value.ReferenceId) || value.ReferenceId.Length > 256 ||
            value.ReferenceId.Any(character => character < 0x21 || character > 0x7e))
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid,
                "remediationReference");
        foreach (var (sha, field) in new[]
                 {
                     (value.ReferenceSha256, "referenceSha256"),
                     (value.BeforeSourceSha256, "beforeSourceSha256"),
                     (value.AfterSourceSha256, "afterSourceSha256"),
                     (value.BeforeResultSha256, "beforeResultSha256"),
                     (value.AfterResultSha256, "afterResultSha256"),
                     (value.RecheckGenerationId, "recheckGenerationId"),
                     (value.RecheckGenerationSha256, "recheckGenerationSha256")
                 })
            StatisticReconciliationFinalVerdictEvaluator.RequireSha(sha, field);
        if (value.BeforeSourceSha256 == value.AfterSourceSha256 &&
            value.BeforeResultSha256 == value.AfterResultSha256)
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid,
                "beforeAfterUnchanged");
        if (value.RecheckGenerationId != recheck.ActualGenerationId ||
            value.RecheckGenerationSha256 != recheck.ActualGenerationSha256)
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.SupersessionInvalid,
                "recheckGenerationBinding");
        var remediationSha = RemediationSha(
            value.ReferenceType,
            value.ReferenceId,
            value.ReferenceSha256,
            value.BeforeSourceSha256,
            value.AfterSourceSha256,
            value.BeforeResultSha256,
            value.AfterResultSha256,
            value.RecheckGenerationId,
            value.RecheckGenerationSha256,
            recheck.Verdict);
        return new StatisticReconciliationVerdictRemediation
        {
            ReferenceType = value.ReferenceType,
            ReferenceId = value.ReferenceId,
            ReferenceSha256 = value.ReferenceSha256,
            BeforeSourceSha256 = value.BeforeSourceSha256,
            AfterSourceSha256 = value.AfterSourceSha256,
            BeforeResultSha256 = value.BeforeResultSha256,
            AfterResultSha256 = value.AfterResultSha256,
            RecheckGenerationId = value.RecheckGenerationId,
            RecheckGenerationSha256 = value.RecheckGenerationSha256,
            ResultingVerdict = recheck.Verdict,
            RemediationSemanticSha256 = remediationSha
        };
    }

    private static string ValidateStoredRemediation(
        StatisticReconciliationVerdictRemediation value,
        StatisticReconciliationReview review)
    {
        var request = new StatisticReconciliationVerdictRemediationRequest(
            value.ReferenceType,
            value.ReferenceId,
            value.ReferenceSha256,
            value.BeforeSourceSha256,
            value.AfterSourceSha256,
            value.BeforeResultSha256,
            value.AfterResultSha256,
            value.RecheckGenerationId,
            value.RecheckGenerationSha256);
        var decision = new StatisticReconciliationFinalVerdictDecision(
            true,
            review.ReconciliationId,
            review.ComparisonBindingSha256,
            review.ExpectedGenerationId,
            review.ExpectedGenerationSha256,
            review.ActualGenerationId,
            review.ActualGenerationSha256,
            review.DeltaManifestSha256,
            review.AuthorizationEvidenceSha256,
            review.FreshnessAssessmentSha256,
            review.RootCauseClassificationSha256,
            review.RootCauseClass,
            review.FailureKind,
            review.FailureEvidenceSha256,
            review.Verdict,
            review.CompleteEvidence,
            review.AllRequiredLayersZero,
            review.MissingOrExtraIdentity,
            review.UnknownBlocksCloseout,
            review.CloseoutAllowed,
            review.Signable,
            "~");
        var normalized = NormalizeRemediation(request, decision);
        if (value.ResultingVerdict != review.Verdict ||
            value.RemediationSemanticSha256 != normalized.RemediationSemanticSha256)
            throw Error(
                StatisticReconciliationFinalVerdictFailureCodes.PersistenceInvalid,
                "remediationSemanticHash");
        return normalized.RemediationSemanticSha256;
    }

    private static string GenerationSha(
        string comparisonBindingSha256,
        string expectedGenerationId,
        string expectedGenerationSha256,
        string actualGenerationId,
        string actualGenerationSha256,
        string deltaManifestSha256,
        string authorizationEvidenceSha256,
        string? freshnessAssessmentSha256,
        string? rootCauseClassificationSha256,
        string? rootCauseClass,
        string failureKind,
        string? failureEvidenceSha256,
        string verdict,
        bool completeEvidence,
        bool allRequiredLayersZero,
        bool missingOrExtraIdentity,
        bool unknownBlocksCloseout,
        bool closeoutAllowed,
        bool signable,
        string? supersedesId,
        string? supersedesSha,
        string? remediationSha)
        => StatisticReconciliationFinalVerdictEvaluator.HashFields(
            "P10_FINAL_VERDICT_GENERATION_V1",
            comparisonBindingSha256,
            expectedGenerationId,
            expectedGenerationSha256,
            actualGenerationId,
            actualGenerationSha256,
            deltaManifestSha256,
            authorizationEvidenceSha256,
            freshnessAssessmentSha256 ?? "~",
            rootCauseClassificationSha256 ?? "~",
            rootCauseClass ?? "~",
            failureKind,
            failureEvidenceSha256 ?? "~",
            verdict,
            completeEvidence ? "1" : "0",
            allRequiredLayersZero ? "1" : "0",
            missingOrExtraIdentity ? "1" : "0",
            unknownBlocksCloseout ? "1" : "0",
            closeoutAllowed ? "1" : "0",
            signable ? "1" : "0",
            supersedesId ?? "~",
            supersedesSha ?? "~",
            remediationSha ?? "~");

    private static string GenerationId(string reconciliationId, string generationSha)
        => StatisticReconciliationFinalVerdictEvaluator.HashFields(
            "P10_FINAL_VERDICT_GENERATION_ID_V1",
            reconciliationId,
            generationSha);

    private static string DocumentSha(
        StatisticReconciliationReview review,
        string generationId,
        string generationSha,
        string? remediationSha)
        => StatisticReconciliationFinalVerdictEvaluator.HashFields(
            "P10_FINAL_VERDICT_DOCUMENT_V1",
            SchemaVersion,
            StatisticReconciliationReviewKinds.FinalVerdict,
            review.ReconciliationId,
            generationId,
            generationSha,
            review.ComparisonBindingSha256,
            review.ExpectedGenerationId,
            review.ExpectedGenerationSha256,
            review.ActualGenerationId,
            review.ActualGenerationSha256,
            review.DeltaManifestSha256,
            review.AuthorizationEvidenceSha256,
            review.FreshnessAssessmentSha256 ?? "~",
            review.RootCauseClassificationSha256 ?? "~",
            review.RootCauseClass ?? "~",
            review.FailureKind,
            review.FailureEvidenceSha256 ?? "~",
            review.Verdict,
            review.CompleteEvidence ? "1" : "0",
            review.AllRequiredLayersZero ? "1" : "0",
            review.MissingOrExtraIdentity ? "1" : "0",
            review.UnknownBlocksCloseout ? "1" : "0",
            review.CloseoutAllowed ? "1" : "0",
            review.Signable ? "1" : "0",
            review.SupersedesVerdictGenerationId ?? "~",
            review.SupersedesVerdictGenerationSha256 ?? "~",
            remediationSha ?? "~");

    private static string RemediationSha(
        string type,
        string id,
        string referenceSha,
        string beforeSource,
        string afterSource,
        string beforeResult,
        string afterResult,
        string generationId,
        string generationSha,
        string verdict)
        => StatisticReconciliationFinalVerdictEvaluator.HashFields(
            "P10_FINAL_VERDICT_REMEDIATION_V1",
            type,
            id,
            referenceSha,
            beforeSource,
            afterSource,
            beforeResult,
            afterResult,
            generationId,
            generationSha,
            verdict);

    private static StatisticReconciliationFinalVerdictException Error(
        string code,
        string detail) => new(code, detail);
}
