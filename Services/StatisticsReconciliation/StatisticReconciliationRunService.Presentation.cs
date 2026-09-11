using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.Services.StatisticsReconciliation;

public sealed partial class StatisticReconciliationRunService
{
    private const string PresentationSchemaVersion =
        "P10_RECONCILIATION_PRESENTATION_V1";

    private async Task<IReadOnlyDictionary<string,
        StatisticReconciliationPresentationResponse>> BuildPresentationsAsync(
        IReadOnlyList<StatisticReconciliationRun> sourceRuns,
        MeResponse actor,
        CancellationToken ct)
    {
        if (sourceRuns.Count == 0)
            return new Dictionary<string,
                StatisticReconciliationPresentationResponse>(
                StringComparer.Ordinal);

        var runs = sourceRuns
            .Select(StatisticReconciliationRecheckCaptureBindingCanonical
                .EffectiveCurrentRun)
            .ToArray();
        if (runs.Select(value => value.Id).Distinct(StringComparer.Ordinal)
                .Count() != runs.Length)
            throw JobConflict("PRESENTATION_RUN_IDENTITY_INVALID");

        var permissionCodes =
            StatisticReconciliationIndependentReviewOwner.PermissionCodes(actor);
        const int before = 1;
        const int after = 1;
        var canViewOperatorDetail = RoleGuard.IsAdmin(actor) ||
                                    RoleGuard.IsSystemAdmin(actor);
        var currentRuns = runs.Where(value =>
                value.CurrentGenerationId is not null &&
                value.CurrentGenerationHash is not null)
            .ToArray();
        IReadOnlyList<StatisticReconciliationReview> verdicts = currentRuns.Length == 0
            ? []
            : await _ctx.StatisticReconciliationReviews.Find(
                    Builders<StatisticReconciliationReview>.Filter.Or(
                        currentRuns.Select(value =>
                            Builders<StatisticReconciliationReview>.Filter.Eq(
                                item => item.ReconciliationId, value.Id) &
                            Builders<StatisticReconciliationReview>.Filter.Eq(
                                item => item.ActualGenerationId,
                                value.CurrentGenerationId) &
                            Builders<StatisticReconciliationReview>.Filter.Eq(
                                item => item.ActualGenerationSha256,
                                value.CurrentGenerationHash) &
                            Builders<StatisticReconciliationReview>.Filter.Eq(
                                item => item.RecordKind,
                                StatisticReconciliationReviewKinds.FinalVerdict))))
                .ToListAsync(ct);
        foreach (var verdict in verdicts)
            StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);
        var verdictGroups = verdicts.GroupBy(value => value.ReconciliationId,
                StringComparer.Ordinal)
            .ToDictionary(value => value.Key, value => value.ToArray(),
                StringComparer.Ordinal);
        if (verdictGroups.Values.Any(value => value.Length != 1))
            throw JobConflict("PRESENTATION_VERDICT_INTEGRITY_INVALID");

        var totals = await ReadPresentationTotalsAsync(
            verdictGroups.Values.Select(value => value[0]).ToArray(), ct);
        var result = new Dictionary<string,
            StatisticReconciliationPresentationResponse>(
            runs.Length, StringComparer.Ordinal);
        foreach (var run in runs)
        {
            var permissionSnapshot =
                StatisticReconciliationIndependentReviewOwner.PermissionSnapshot(
                    actor.Id, actor.UnitId, run.WorkId,
                    run.ScopeAssignmentId, permissionCodes, before, after);
            var verdict = verdictGroups.GetValueOrDefault(run.Id)?[0];
            var columns = BuildPresentationColumns(run, verdict,
                permissionSnapshot);
            var root = $"/api/works/{Uri.EscapeDataString(run.WorkId)}" +
                       $"/statistics/{Uri.EscapeDataString(run.ScopeAssignmentId)}" +
                       $"/reconciliations/{Uri.EscapeDataString(run.Id)}";
            var evidenceLinks = new[]
            {
                new StatisticReconciliationPresentationLinkResponse
                {
                    Rel = "REVIEW",
                    Href = root + "/review-decisions",
                    Method = "GET"
                },
                new StatisticReconciliationPresentationLinkResponse
                {
                    Rel = "EVIDENCE_EXPORT",
                    Href = root + "/evidence-exports",
                    Method = "POST"
                }
            };
            IReadOnlyList<StatisticReconciliationPresentationLinkResponse>
                sourceLinks = canViewOperatorDetail
                    ?
                    [
                        new StatisticReconciliationPresentationLinkResponse
                        {
                            Rel = "SOURCE_DETAIL",
                            Href = root + "/detail#source-provenance",
                            Method = "GET"
                        }
                    ]
                    : [];
            result.Add(run.Id, new StatisticReconciliationPresentationResponse
            {
                SchemaVersion = PresentationSchemaVersion,
                DetailLevel = canViewOperatorDetail ? "OPERATOR" : "REDACTED",
                Recheck = BuildPresentationRecheck(run, verdict),
                Rows =
                [
                    new StatisticReconciliationPresentationRowResponse
                    {
                        Id = run.Id,
                        Columns = columns,
                        Metadata = new
                            StatisticReconciliationPresentationMetadataResponse
                        {
                            Total = verdict is null
                                ? 0
                                : totals.GetValueOrDefault(run.Id),
                            RootCause = verdict?.RootCauseClass ??
                                        RedactDiagnostic(run.DiagnosticCode) ??
                                        "NONE",
                            RowCountBeforeRedaction = before,
                            RowCountAfterRedaction = after,
                            PermissionCodes = permissionCodes,
                            EvidenceLinks = evidenceLinks,
                            SourceLinks = sourceLinks
                        }
                    }
                ]
            });
        }
        return result;
    }

    internal static StatisticReconciliationRecheckPresentationResponse
        BuildPresentationRecheck(
            StatisticReconciliationRun run,
            StatisticReconciliationReview? verdict)
        => new()
        {
            InProgress = run.Recheck is not null,
            BaseEligible = run.Recheck is null &&
                run.StateRevision > 0 &&
                StatisticReconciliationCanonicalJson.IsCanonicalSha256(run.StateHash) &&
                run.CurrentGenerationId is not null &&
                run.CurrentGenerationHash is not null &&
                run.PendingGenerationId is null &&
                run.PendingGenerationHash is null &&
                !run.PendingGenerationPublishedAtUtc.HasValue &&
                run.LeaseOwnerId is null && run.ClaimToken is null &&
                !run.LeaseUntilUtc.HasValue && !run.LastHeartbeatAtUtc.HasValue &&
                verdict is not null &&
                (IsApprovedMatchedRecheckBase(run, verdict) ||
                 IsIdentityRemediationBase(run, verdict) ||
                 IsFreshnessRecoveryRecheckBase(run, verdict))
        };

    internal static StatisticReconciliationPresentationColumnsResponse
        BuildPresentationColumns(
            StatisticReconciliationRun run,
            StatisticReconciliationReview? verdict,
            string permissionSnapshotSha256)
    {
        ArgumentNullException.ThrowIfNull(run);
        StatisticReconciliationIndependentReviewCanonical.RequireSha(
            permissionSnapshotSha256, "permissionSnapshotSha256");
        var identity =
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_IDENTITY_COLUMN_V1", run.ImmutableIdentityHash,
                run.ImmutableHeaderHash);
        var config =
            StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_CONFIG_COLUMN_V1", run.P8ConfigBundleHash,
                run.ActualConfigurationBundleSha256,
                run.CandidateCatalogSemanticSha256,
                run.CandidateSchemaSemanticSha256);
        if (verdict is null)
        {
            return new StatisticReconciliationPresentationColumnsResponse
            {
                Identity = identity,
                Config = config,
                Expected = "NONE",
                Actual = run.CurrentGenerationHash ?? "NONE",
                Delta = "NONE",
                Freshness = NormalizeUtc(run.UpdatedAtUtc)!.Value
                    .ToString("O", CultureInfo.InvariantCulture),
                Permission = permissionSnapshotSha256,
                Verdict = run.Status
            };
        }

        StatisticReconciliationFinalVerdictPublisher.ValidateStored(verdict);
        if (verdict.ReconciliationId != run.Id ||
            verdict.ActualGenerationId != run.CurrentGenerationId ||
            verdict.ActualGenerationSha256 != run.CurrentGenerationHash)
            throw JobConflict("PRESENTATION_VERDICT_BINDING_INVALID");
        return new StatisticReconciliationPresentationColumnsResponse
        {
            Identity = identity,
            Config = config,
            Expected =
                StatisticReconciliationIndependentReviewCanonical.Hash(
                    "P10_REVIEW_EXPECTED_COLUMN_V1",
                    verdict.ExpectedGenerationId,
                    verdict.ExpectedGenerationSha256),
            Actual = StatisticReconciliationIndependentReviewCanonical.Hash(
                "P10_REVIEW_ACTUAL_COLUMN_V1", verdict.ActualGenerationId,
                verdict.ActualGenerationSha256),
            Delta = verdict.DeltaManifestSha256,
            Freshness = verdict.FreshnessAssessmentSha256 ?? "NONE",
            Permission = permissionSnapshotSha256,
            Verdict = verdict.Verdict
        };
    }

    private async Task<IReadOnlyDictionary<string, long>>
        ReadPresentationTotalsAsync(
            IReadOnlyList<StatisticReconciliationReview> verdicts,
            CancellationToken ct)
    {
        if (verdicts.Count == 0)
            return new Dictionary<string, long>(StringComparer.Ordinal);
        var clauses = verdicts.Select(value => new BsonDocument
        {
            { "reconciliationId", value.ReconciliationId },
            { "generationId", new BsonDocument("$in", new BsonArray
                { value.ExpectedGenerationId, value.ActualGenerationId }) },
            { "recordKind", new BsonDocument("$in", new BsonArray
                {
                    StatisticReconciliationObservationRecordKinds.ExpectedAtom,
                    StatisticReconciliationObservationRecordKinds.ActualAtom
                }) },
            { "atom", new BsonDocument("$ne", BsonNull.Value) }
        }).ToArray();
        var pipeline = new[]
        {
            new BsonDocument("$match",
                new BsonDocument("$or", new BsonArray(clauses))),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", new BsonDocument
                    {
                        { "reconciliationId", "$reconciliationId" },
                        { "identity", "$atom.identitySha256" },
                        { "atomKind", "$atom.atomKind" },
                        { "transitionLeg", new BsonDocument("$ifNull",
                            new BsonArray { "$atom.transitionLeg", "NONE" }) },
                        { "transitionKind", new BsonDocument("$ifNull",
                            new BsonArray { "$atom.transitionKind", "NONE" }) }
                    }
                }
            }),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", "$_id.reconciliationId" },
                { "total", new BsonDocument("$sum", 1) }
            }),
            new BsonDocument("$limit", verdicts.Count + 1)
        };
        var values = await _ctx.StatisticReconciliationObservations
            .Aggregate<BsonDocument>(pipeline).ToListAsync(ct);
        if (values.Count > verdicts.Count)
            throw JobConflict("PRESENTATION_TOTAL_INTEGRITY_INVALID");
        var result = values.ToDictionary(value => value["_id"].AsString,
            value => value["total"].ToInt64(), StringComparer.Ordinal);
        if (result.Values.Any(value => value < 0 ||
                                       value >
                                       StatisticReconciliationEvidenceCanonical
                                           .MaximumRows))
            throw JobConflict("PRESENTATION_TOTAL_OUT_OF_RANGE");
        return result;
    }
}
