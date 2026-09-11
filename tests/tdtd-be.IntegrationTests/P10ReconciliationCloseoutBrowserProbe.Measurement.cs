using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    private P10CloseoutMeasuredParity? _closeoutMeasuredParity;

    private async Task<bool> ValidateCloseoutMeasuredBrowserArtifactsAsync(
        CancellationToken ct)
    {
        var masterPath = Path.Combine(
            _paths.RunRoot,
            "browser",
            "browser-result.json");
        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(masterPath, ct));
        var root = document.RootElement;
        HarnessAssert.Equal(
            "P10_CLOSE_BROWSER_GATE_V1",
            root.GetProperty("schemaVersion").GetString(),
            "P10-CLOSE browser gate schema");
        HarnessAssert.Equal(
            "PASS",
            root.GetProperty("status").GetString(),
            "P10-CLOSE browser gate status");
        HarnessAssert.Equal(
            3,
            root.GetProperty("expected").GetInt32(),
            "P10-CLOSE expected browser cases");
        HarnessAssert.Equal(
            3,
            root.GetProperty("passed").GetInt32(),
            "P10-CLOSE passed browser cases");
        HarnessAssert.Equal(
            0,
            root.GetProperty("networkMockCount").GetInt32(),
            "P10-CLOSE browser network mocks");
        var caseIds = root.GetProperty("caseIds")
            .EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();
        HarnessAssert.True(
            caseIds.SequenceEqual(
                CloseoutBrowserCaseIds,
                StringComparer.Ordinal),
            "P10-CLOSE browser case ID order");

        var rawCases = new List<P10CloseoutRawBrowserCase>();
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            HarnessAssert.True(
                !item.TryGetProperty("domApiMongoParity", out _) &&
                !item.TryGetProperty("exportMongoParity", out _),
                "P10-CLOSE browser JS must not self-label Mongo parity");
            var evidence = new P10CloseoutRawBrowserCase(
                item.GetProperty("caseId").GetString() ?? string.Empty,
                item.GetProperty("status").GetString() ?? string.Empty,
                item.GetProperty("accessibilityPassed").GetBoolean(),
                item.GetProperty("securityPassed").GetBoolean(),
                item.GetProperty("networkMockCount").GetInt32(),
                item.GetProperty("resultPath").GetString() ?? string.Empty,
                item.GetProperty("tracePath").GetString() ?? string.Empty,
                item.GetProperty("networkPath").GetString() ?? string.Empty,
                item.GetProperty("screenshotPath").GetString() ?? string.Empty,
                item.GetProperty("downloadPath").GetString() ?? string.Empty);
            foreach (var path in new[]
                     {
                         evidence.ResultPath,
                         evidence.TracePath,
                         evidence.NetworkPath,
                         evidence.ScreenshotPath,
                         evidence.DownloadPath
                     })
            {
                RequireCloseoutOwnedArtifact(path);
                HarnessAssert.True(
                    File.Exists(path) && new FileInfo(path).Length > 0,
                    $"P10-CLOSE case artifact is missing: {path}");
            }
            rawCases.Add(evidence);
        }
        HarnessAssert.True(
            rawCases.Select(value => value.CaseId).SequenceEqual(
                CloseoutBrowserCaseIds,
                StringComparer.Ordinal) &&
            rawCases.All(value =>
                value.Status == "PASS" &&
                value.AccessibilityPassed &&
                value.SecurityPassed &&
                value.NetworkMockCount == 0),
            "P10-CLOSE browser case evidence failed");

        var lifecycle = ReadCloseoutCaseAssertions(rawCases[0]);
        var review = ReadCloseoutCaseAssertions(rawCases[1]);
        var export = ReadCloseoutCaseAssertions(rawCases[2]);
        var stateParity = await ValidateCloseoutStateObservationAsync(
            lifecycle.GetProperty("stateObservation"),
            ct);
        var sourceParity = await ValidateCloseoutSourceObservationAsync(
            review.GetProperty("sourceObservation"),
            ct);
        var reviewParity = await ValidateCloseoutReviewObservationAsync(
            review.GetProperty("reviewObservation"),
            review,
            rawCases[1],
            ct);
        var exportParity = await ValidateCloseoutExportObservationAsync(
            export.GetProperty("exportObservation"),
            ct);
        _closeoutMeasuredParity = new P10CloseoutMeasuredParity(
            stateParity.Passed &&
            sourceParity.Passed &&
            reviewParity.Passed &&
            exportParity.Passed,
            stateParity,
            sourceParity,
            reviewParity,
            exportParity);

        _closeoutBrowserCases.Clear();
        foreach (var item in rawCases)
        {
            var measuredDomApiMongo = item.CaseId switch
            {
                "P10-BROWSER-01" => stateParity.Passed,
                "P10-BROWSER-02" =>
                    sourceParity.Passed && reviewParity.Passed,
                "P10-BROWSER-03" =>
                    stateParity.Passed && sourceParity.Passed,
                _ => false
            };
            _closeoutBrowserCases.Add(new P10CloseoutBrowserCase(
                item.CaseId,
                item.Status,
                measuredDomApiMongo,
                exportParity.Passed,
                item.AccessibilityPassed,
                item.SecurityPassed,
                item.NetworkMockCount,
                item.ResultPath,
                item.TracePath,
                item.NetworkPath,
                item.ScreenshotPath,
                item.DownloadPath));
        }
        HarnessAssert.True(
            _closeoutMeasuredParity.Passed &&
            _closeoutBrowserCases.All(value =>
                value.DomApiMongoParity && value.ExportMongoParity),
            "P10-CLOSE measured DOM/API/export/Mongo parity failed");
        return true;
    }

    private JsonElement ReadCloseoutCaseAssertions(
        P10CloseoutRawBrowserCase value)
    {
        RequireCloseoutOwnedArtifact(value.ResultPath);
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(value.ResultPath));
        var root = document.RootElement;
        HarnessAssert.Equal(
            "P10_CLOSE_BROWSER_CASE_V1",
            root.GetProperty("schemaVersion").GetString(),
            $"P10-CLOSE {value.CaseId} result schema");
        HarnessAssert.Equal(
            value.CaseId,
            root.GetProperty("caseId").GetString(),
            "P10-CLOSE result case ID");
        HarnessAssert.Equal(
            "PASS",
            root.GetProperty("status").GetString(),
            $"P10-CLOSE {value.CaseId} result status");
        HarnessAssert.True(
            !root.TryGetProperty("domApiMongoParity", out _) &&
            !root.TryGetProperty("exportMongoParity", out _),
            "P10-CLOSE result cannot self-assert Mongo parity");
        return root.GetProperty("assertions").Clone();
    }

    private static readonly string[] CloseoutPresentationColumnNames =
    [
        "Identity", "Config", "Expected", "Actual", "Delta",
        "Freshness", "Permission", "Verdict"
    ];

    private async Task<P10CloseoutStateParity>
        ValidateCloseoutStateObservationAsync(
            JsonElement observation,
            CancellationToken ct)
    {
        var registry = _closeoutStateRegistry ??
            throw new InvalidOperationException(
                "P10-CLOSE production state registry is unavailable.");
        var prepared = _closeoutPrepared ??
            throw new InvalidOperationException(
                "P10-CLOSE prepared fixture is unavailable.");
        var expectedStatuses = observation.GetProperty("expectedStatuses")
            .EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();
        HarnessAssert.True(
            expectedStatuses.SequenceEqual(
                CloseoutProductionStatuses,
                StringComparer.Ordinal) &&
            registry.ExpectedStatuses.SequenceEqual(
                CloseoutProductionStatuses,
                StringComparer.Ordinal),
            "P10-CLOSE exact production status registry");

        var domRows = ReadCloseoutDomPresentationRows(
            observation.GetProperty("domRows"));
        var apiRows = ReadCloseoutApiPresentationRows(
            observation.GetProperty("apiRows"));
        var apiSummaryStatuses = observation
            .GetProperty("apiSummaryStatuses")
            .EnumerateArray()
            .Select(value => new P10CloseoutObservedStateRow(
                value.GetProperty("reconciliationId").GetString() ??
                    string.Empty,
                value.GetProperty("status").GetString() ?? string.Empty,
                []))
            .ToArray();
        var domRowsSha256 = CloseoutDisplayPresentationRowsSha256(domRows);
        var apiRowsSha256 = CloseoutApiPresentationRowsSha256(apiRows);
        var apiDisplayedRowsSha256 =
            CloseoutDisplayPresentationRowsSha256(apiRows.Select(
                CloseoutDisplayProjection));
        var domIdentitySha256 = CloseoutMatrixSha256(domRows.Select(value =>
            (IReadOnlyList<string>)[value.Id]));
        var apiIdentityStatusSha256 =
            CloseoutIdentityStatusSha256(apiSummaryStatuses);
        HarnessAssert.Equal(
            observation.GetProperty("domRowsSha256").GetString(),
            domRowsSha256,
            "P10-CLOSE DOM presentation hash recompute");
        HarnessAssert.Equal(
            observation.GetProperty("apiRowsSha256").GetString(),
            apiRowsSha256,
            "P10-CLOSE API presentation hash recompute");
        HarnessAssert.Equal(
            observation.GetProperty("apiDisplayedRowsSha256").GetString(),
            apiDisplayedRowsSha256,
            "P10-CLOSE API displayed projection hash recompute");
        HarnessAssert.Equal(
            observation.GetProperty("domIdentitySha256").GetString(),
            domIdentitySha256,
            "P10-CLOSE DOM identity hash recompute");
        HarnessAssert.Equal(
            observation.GetProperty("apiIdentityStatusSha256").GetString(),
            apiIdentityStatusSha256,
            "P10-CLOSE API identity/status hash recompute");
        HarnessAssert.True(
            domRows.Length == 6 && apiRows.Length == 6 &&
            CloseoutJsonEqual(
                domRows,
                apiRows.Select(CloseoutDisplayProjection).ToArray()) &&
            domRowsSha256 == apiDisplayedRowsSha256,
            "P10-CLOSE raw server presentation DOM/API parity");

        var redactedDetail = ReadCloseoutDetailPresentation(
            observation.GetProperty("redactedDetail"));
        var targetApiRow = apiRows.Single(value => value.Id ==
            prepared.TargetReconciliationId);
        HarnessAssert.True(
            redactedDetail.ApiRow.DetailLevel == "REDACTED" &&
            redactedDetail.DetailRequestCount == 0 &&
            redactedDetail.DetailSuccessCount == 0 &&
            redactedDetail.SourceLinks.Count == 0 &&
            redactedDetail.ApiRow.Metadata.SourceLinks.Count == 0 &&
            CloseoutJsonEqual(redactedDetail.ApiRow, targetApiRow) &&
            CloseoutJsonEqual(
                redactedDetail.DomRow,
                CloseoutDisplayProjection(redactedDetail.ApiRow)) &&
            CloseoutLinksEqual(
                redactedDetail.EvidenceLinks,
                redactedDetail.ApiRow.Metadata.EvidenceLinks),
            "P10-CLOSE redacted server presentation/detail-link parity");

        var expectedRows = registry.Rows
            .Where(value => value.ReconciliationId is not null)
            .ToArray();
        var typedRows = await RequireDatabase()
            .GetCollection<StatisticReconciliationRun>(RunCollection)
            .Find(value => value.WorkId == Fixture().WorkId &&
                value.ScopeAssignmentId == Fixture().ScopeAssignmentId &&
                !value.IsDeleted)
            .ToListAsync(ct);
        foreach (var row in typedRows)
        {
            StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
                row);
        }
        var mongoRows = typedRows.Select(value =>
                new P10CloseoutMongoStateRow(
                    value.Id,
                    value.Status,
                    value.StateRevision,
                    value.ReviewDecisionRevision,
                    value.StateHash,
                    value.SourceReportId,
                    value.SourcePayloadRevision,
                    value.SourcePayloadHash,
                    value.SourceLifecycleRevision,
                    value.SourceLifecycleHash))
            .OrderBy(value => value.ReconciliationId, StringComparer.Ordinal)
            .ToArray();
        var mongoPresentation =
            await BuildCloseoutMongoPresentationAsync(
                typedRows,
                "executor",
                operatorDetail: false,
                ct);
        var mongoIdentityStatusSha256 = CloseoutIdentityStatusSha256(
            mongoRows.Select(value => new P10CloseoutObservedStateRow(
                value.ReconciliationId,
                value.Status,
                [])).ToArray());
        var mongoPresentationRowsSha256 =
            CloseoutApiPresentationRowsSha256(mongoPresentation.Rows);
        HarnessAssert.Equal(
            string.Join(", ", mongoPresentation.PermissionOracle.PermissionCodes) +
                " - " +
                mongoPresentation.PermissionOracle.RowCountBeforeRedaction +
                "/" +
                mongoPresentation.PermissionOracle.RowCountAfterRedaction,
            redactedDetail.PermissionText,
            "P10-CLOSE redacted DOM permission metadata");
        var emptyMongoCount = await RequireDatabase()
            .GetCollection<BsonDocument>(RunCollection)
            .CountDocumentsAsync(new BsonDocument
            {
                ["workId"] = ObjectId.Parse(Fixture().WorkId),
                ["scopeAssignmentId"] =
                    ObjectId.Parse(Fixture().SiblingAssignmentId),
                ["isDeleted"] = false
            }, cancellationToken: ct);
        var emptyDom = observation.GetProperty("emptyDom").GetBoolean();
        var emptyApiRowCount = observation.GetProperty("emptyApiRowCount")
            .GetInt32();

        var targetExpected = expectedRows.Single(value =>
            value.ReconciliationId == prepared.TargetReconciliationId);
        var targetActual = mongoRows.Single(value =>
            value.ReconciliationId == prepared.TargetReconciliationId);
        var nonTargetRowsSealed = expectedRows
            .Where(value => value.ReconciliationId !=
                prepared.TargetReconciliationId)
            .All(expected => mongoRows.Any(actual =>
                actual.ReconciliationId == expected.ReconciliationId &&
                actual.Status == expected.Status &&
                actual.StateRevision == expected.StateRevision &&
                actual.ReviewDecisionRevision ==
                    expected.ReviewDecisionRevision &&
                actual.StateHash == expected.StateHash &&
                CloseoutSourceEquals(expected, actual)));
        var targetSourceStable = CloseoutSourceEquals(
            targetExpected,
            targetActual);
        var reviewRows = await RequireDatabase()
            .GetCollection<StatisticReconciliationIndependentReviewAuditRecord>(
                "work_report_statistic_reconciliation_reviews")
            .Find(value => value.ReconciliationId ==
                    prepared.TargetReconciliationId &&
                value.RecordKind ==
                    StatisticReconciliationReviewRecordKinds.Decision)
            .ToListAsync(ct);
        foreach (var row in reviewRows)
        {
            StatisticReconciliationIndependentReviewService.ValidateStored(row);
        }
        var orderedReviews = reviewRows.OrderBy(value => Array.IndexOf(
                CloseoutReviewGates,
                value.Gate))
            .ToArray();
        var expectedInitialStateRevision = targetExpected.StateRevision ?? -1;
        var expectedInitialReviewRevision =
            targetExpected.ReviewDecisionRevision ?? -1;
        var reviewSequenceExact = orderedReviews.Length == 5 &&
            orderedReviews.Select(value => value.Gate).SequenceEqual(
                CloseoutReviewGates,
                StringComparer.Ordinal) &&
            orderedReviews.Select(value => value.StateRevision).SequenceEqual(
                Enumerable.Range(0, 5).Select(index =>
                    expectedInitialStateRevision + index));
        var targetTransition = new P10CloseoutTargetStateTransition(
            prepared.TargetReconciliationId,
            targetExpected.Status,
            targetActual.Status,
            expectedInitialStateRevision,
            targetActual.StateRevision,
            expectedInitialReviewRevision,
            targetActual.ReviewDecisionRevision,
            targetExpected.StateHash ?? string.Empty,
            targetActual.StateHash,
            targetSourceStable,
            true,
            orderedReviews.Length,
            reviewSequenceExact,
            CloseoutMatrixSha256(orderedReviews.Select(value =>
                (IReadOnlyList<string>)
                [
                    value.Gate,
                    value.OperationCommandId,
                    value.StateRevision.ToString(
                        CultureInfo.InvariantCulture),
                    value.DocumentSha256
                ])));
        HarnessAssert.True(
            typedRows.Count == 6 && expectedRows.Length == 6 &&
            nonTargetRowsSealed &&
            targetActual.Status == targetExpected.Status &&
            targetActual.StateRevision ==
                expectedInitialStateRevision + 5 &&
            targetActual.ReviewDecisionRevision ==
                expectedInitialReviewRevision + 5 &&
            targetActual.StateHash != targetExpected.StateHash &&
            targetSourceStable && reviewSequenceExact &&
            CloseoutJsonEqual(apiRows, mongoPresentation.Rows) &&
            apiRowsSha256 == mongoPresentationRowsSha256 &&
            mongoIdentityStatusSha256 == apiIdentityStatusSha256 &&
            emptyDom && emptyApiRowCount == 0 && emptyMongoCount == 0,
            "P10-CLOSE direct Mongo presentation/sealed-state parity");
        return new P10CloseoutStateParity(
            true,
            expectedStatuses,
            emptyDom,
            emptyApiRowCount,
            emptyMongoCount,
            domRows,
            apiRows,
            mongoPresentation.Rows,
            mongoRows,
            redactedDetail,
            mongoPresentation.PermissionOracle,
            mongoPresentation.Inputs,
            nonTargetRowsSealed,
            targetTransition,
            domRowsSha256,
            apiRowsSha256,
            apiDisplayedRowsSha256,
            mongoPresentationRowsSha256,
            domIdentitySha256,
            apiIdentityStatusSha256,
            mongoIdentityStatusSha256);
    }

    private async Task<P10CloseoutMongoPresentationResult>
        BuildCloseoutMongoPresentationAsync(
            IReadOnlyList<StatisticReconciliationRun> sourceRuns,
            string actorKey,
            bool operatorDetail,
            CancellationToken ct)
    {
        var fixtureActor = Actor(actorKey);
        var mongoActor = await RequireDatabase()
            .GetCollection<AppUser>("users")
            .Find(value => value.Id == fixtureActor.Id)
            .SingleAsync(ct);
        HarnessAssert.True(
            mongoActor.UnitId == fixtureActor.UnitId &&
            mongoActor.Roles.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(
                    fixtureActor.Roles.OrderBy(
                        value => value,
                        StringComparer.Ordinal),
                    StringComparer.Ordinal),
            "P10-CLOSE direct Mongo presentation actor binding");
        var actorRoles = mongoActor.Roles.ToArray();
        var permissionRoles = actorRoles
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim());
        var permissionCodes = new[] { "STAT_RECONCILIATION_REVIEW" }
            .Concat(permissionRoles.Select(value =>
                "ROLE:" + value.ToUpperInvariant()))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var derivedOperatorDetail = actorRoles.Any(role =>
                string.Equals(role, "ADMIN",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "SYSTEM_ADMIN",
                    StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(
                mongoActor.AccountKind,
                "SYSTEM_ADMIN",
                StringComparison.OrdinalIgnoreCase);
        HarnessAssert.True(
            !mongoActor.IsDeleted && operatorDetail == derivedOperatorDetail,
            "P10-CLOSE direct Mongo actor detail-level derivation");
        const int before = 1;
        const int after = 1;
        var permissionSnapshot = CloseoutPresentationHash(
            "P10_REVIEW_PERMISSION_SNAPSHOT_V2",
            mongoActor.Id,
            mongoActor.UnitId ?? string.Empty,
            Fixture().WorkId,
            Fixture().ScopeAssignmentId,
            string.Join("\n", permissionCodes),
            before,
            after);
        var runIds = sourceRuns.Select(value => value.Id).ToArray();
        var verdicts = await RequireDatabase()
            .GetCollection<StatisticReconciliationReview>(
                "work_report_statistic_reconciliation_reviews")
            .Find(value => runIds.Contains(value.ReconciliationId) &&
                value.RecordKind ==
                    StatisticReconciliationReviewKinds.FinalVerdict)
            .ToListAsync(ct);
        foreach (var verdict in verdicts)
        {
            StatisticReconciliationFinalVerdictPublisher.ValidateStored(
                verdict);
        }
        var verdictGroups = sourceRuns.ToDictionary(
            run => run.Id,
            run => verdicts.Where(value =>
                    value.ReconciliationId == run.Id &&
                    value.ActualGenerationId == run.CurrentGenerationId &&
                    value.ActualGenerationSha256 == run.CurrentGenerationHash)
                .ToArray(),
            StringComparer.Ordinal);
        HarnessAssert.True(
            verdictGroups.Values.All(value => value.Length <= 1),
            "P10-CLOSE exact current-bound Mongo presentation verdict cardinality");
        var observations = await RequireDatabase()
            .GetCollection<StatisticReconciliationObservation>(
                "work_report_statistic_reconciliation_observations")
            .Find(value => runIds.Contains(value.ReconciliationId))
            .ToListAsync(ct);
        var rows = new List<P10CloseoutApiPresentationRow>();
        var inputs = new List<P10CloseoutPresentationMongoInput>();
        foreach (var run in sourceRuns.OrderBy(value => value.Id,
                     StringComparer.Ordinal))
        {
            var currentVerdicts = verdictGroups.GetValueOrDefault(run.Id);
            var verdict = currentVerdicts is { Length: 1 }
                ? currentVerdicts[0]
                : null;
            var columns = BuildCloseoutPresentationColumns(
                run,
                verdict,
                permissionSnapshot);
            var observationInputs = observations
                .Where(value => value.ReconciliationId == run.Id)
                .Select(value => new P10CloseoutPresentationObservationInput(
                    value.Id,
                    value.ReconciliationId,
                    value.GenerationId,
                    value.RecordKind,
                    value.Atom?.IdentitySha256,
                    value.Atom?.Family,
                    value.Atom?.Kind,
                    value.Atom?.MetricId,
                    value.Atom?.AtomKind,
                    value.Atom?.ValueType,
                    value.Atom?.ValueState,
                    value.Atom?.CanonicalValue,
                    value.Atom?.DecimalScale,
                    value.Atom?.OccurrenceCount,
                    value.Atom?.TransitionLeg,
                    value.Atom?.TransitionKind,
                    value.ActualSourceDecision?.Included,
                    value.ActualSourceDecision?.SourceStableIdentitySha256))
                .OrderBy(value => value.GenerationId, StringComparer.Ordinal)
                .ThenBy(value => value.RecordKind, StringComparer.Ordinal)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .ToArray();
            var total = verdict is null
                ? 0
                : observationInputs.Where(value =>
                        (value.GenerationId == verdict.ExpectedGenerationId ||
                         value.GenerationId == verdict.ActualGenerationId) &&
                        (value.RecordKind ==
                             StatisticReconciliationObservationRecordKinds
                                 .ExpectedAtom ||
                         value.RecordKind ==
                             StatisticReconciliationObservationRecordKinds
                                 .ActualAtom) &&
                        value.IdentitySha256 is not null &&
                        value.AtomKind is not null)
                    .Select(value => string.Join("\u001f",
                        value.IdentitySha256,
                        value.AtomKind,
                        value.TransitionLeg ?? "NONE",
                        value.TransitionKind ?? "NONE"))
                    .Distinct(StringComparer.Ordinal)
                    .LongCount();
            var root = $"/api/works/{Uri.EscapeDataString(run.WorkId)}" +
                       $"/statistics/{Uri.EscapeDataString(
                           run.ScopeAssignmentId)}" +
                       $"/reconciliations/{Uri.EscapeDataString(run.Id)}";
            var evidenceLinks = new[]
            {
                new P10CloseoutPresentationLink(
                    "REVIEW",
                    root + "/review-decisions",
                    "GET"),
                new P10CloseoutPresentationLink(
                    "EVIDENCE_EXPORT",
                    root + "/evidence-exports",
                    "POST")
            };
            IReadOnlyList<P10CloseoutPresentationLink> sourceLinks =
                derivedOperatorDetail
                    ?
                    [
                        new P10CloseoutPresentationLink(
                            "SOURCE_DETAIL",
                            root + "/detail#source-provenance",
                            "GET")
                    ]
                    : [];
            rows.Add(new P10CloseoutApiPresentationRow(
                run.Id,
                run.Status,
                "P10_RECONCILIATION_PRESENTATION_V1",
                derivedOperatorDetail ? "OPERATOR" : "REDACTED",
                CloseoutPresentationColumns(columns),
                new P10CloseoutPresentationMetadata(
                    total,
                    verdict?.RootCauseClass ??
                    CloseoutRedactedDiagnostic(run.DiagnosticCode) ?? "NONE",
                    before,
                    after,
                    permissionCodes,
                    evidenceLinks,
                    sourceLinks)));
            inputs.Add(new P10CloseoutPresentationMongoInput(
                run.Id,
                run.WorkId,
                run.ScopeAssignmentId,
                run.DiagnosticCode,
                run.ImmutableIdentityHash,
                run.ImmutableHeaderHash,
                run.P8ConfigBundleHash,
                run.ActualConfigurationBundleSha256,
                run.CandidateCatalogSemanticSha256,
                run.CandidateSchemaSemanticSha256,
                run.CurrentGenerationId,
                run.CurrentGenerationHash,
                run.Status,
                run.UpdatedAtUtc,
                verdict is null
                    ? null
                    : new P10CloseoutPresentationVerdictInput(
                        verdict.VerdictGenerationId,
                        verdict.VerdictGenerationSha256,
                        verdict.DocumentSemanticSha256,
                        verdict.ExpectedGenerationId,
                        verdict.ExpectedGenerationSha256,
                        verdict.ActualGenerationId,
                        verdict.ActualGenerationSha256,
                        verdict.DeltaManifestSha256,
                        verdict.FreshnessAssessmentSha256,
                        verdict.Verdict,
                        verdict.RootCauseClass,
                        verdict.CreatedAtUtc,
                        verdict.CompleteEvidence,
                        verdict.Signable,
                        verdict.CloseoutAllowed,
                        verdict.AllRequiredLayersZero,
                        verdict.UnknownBlocksCloseout),
                observationInputs,
                total));
        }
        return new P10CloseoutMongoPresentationResult(
            rows,
            new P10CloseoutPresentationPermissionOracle(
                actorKey,
                mongoActor.Id,
                mongoActor.UnitId ?? string.Empty,
                actorRoles,
                mongoActor.AccountKind,
                mongoActor.IsDeleted,
                Fixture().WorkId,
                Fixture().ScopeAssignmentId,
                permissionCodes,
                permissionSnapshot,
                before,
                after,
                derivedOperatorDetail ? "OPERATOR" : "REDACTED"),
            inputs);
    }

    private static StatisticReconciliationPresentationColumnsResponse
        BuildCloseoutPresentationColumns(
            StatisticReconciliationRun run,
            StatisticReconciliationReview? verdict,
            string permissionSnapshotSha256)
    {
        var identity = CloseoutPresentationHash(
            "P10_REVIEW_IDENTITY_COLUMN_V1",
            run.ImmutableIdentityHash,
            run.ImmutableHeaderHash);
        var config = CloseoutPresentationHash(
            "P10_REVIEW_CONFIG_COLUMN_V1",
            run.P8ConfigBundleHash,
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
                Freshness = run.UpdatedAtUtc.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                Permission = permissionSnapshotSha256,
                Verdict = run.Status
            };
        }
        return new StatisticReconciliationPresentationColumnsResponse
        {
            Identity = identity,
            Config = config,
            Expected = CloseoutPresentationHash(
                "P10_REVIEW_EXPECTED_COLUMN_V1",
                verdict.ExpectedGenerationId,
                verdict.ExpectedGenerationSha256),
            Actual = CloseoutPresentationHash(
                "P10_REVIEW_ACTUAL_COLUMN_V1",
                verdict.ActualGenerationId,
                verdict.ActualGenerationSha256),
            Delta = verdict.DeltaManifestSha256,
            Freshness = verdict.FreshnessAssessmentSha256 ?? "NONE",
            Permission = permissionSnapshotSha256,
            Verdict = verdict.Verdict
        };
    }

    private static string CloseoutPresentationHash(params object?[] fields)
    {
        using var stream = new MemoryStream();
        foreach (var field in fields)
        {
            var text = field switch
            {
                null => "~",
                bool value => value ? "1" : "0",
                DateTime value => value.ToUniversalTime().ToString("O"),
                _ => Convert.ToString(
                    field,
                    CultureInfo.InvariantCulture) ?? "~"
            };
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(BitConverter.GetBytes(
                System.Net.IPAddress.HostToNetworkOrder(bytes.Length)));
            stream.Write(bytes);
        }
        return Convert.ToHexString(
                SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, string>
        CloseoutPresentationColumns(
            StatisticReconciliationPresentationColumnsResponse value)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Identity"] = value.Identity,
            ["Config"] = value.Config,
            ["Expected"] = value.Expected,
            ["Actual"] = value.Actual,
            ["Delta"] = value.Delta,
            ["Freshness"] = value.Freshness,
            ["Permission"] = value.Permission,
            ["Verdict"] = value.Verdict
        };

    private static string? CloseoutRedactedDiagnostic(string? value)
        => value is "P10_TEST_TRANSIENT" or "P10_TEST_TERMINAL" or
               "P10_RECONCILIATION_JOB_TIMEOUT"
            ? value
            : string.IsNullOrWhiteSpace(value)
                ? null
                : "P10_RECONCILIATION_JOB_FAILED";

    private static P10CloseoutApiPresentationRow[]
        ReadCloseoutApiPresentationRows(JsonElement value)
        => value.EnumerateArray().Select(ReadCloseoutApiPresentationRow)
            .OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();

    private static P10CloseoutApiPresentationRow
        ReadCloseoutApiPresentationRow(JsonElement value)
        => new(
            value.GetProperty("id").GetString() ?? string.Empty,
            value.GetProperty("status").GetString() ?? string.Empty,
            value.GetProperty("schemaVersion").GetString() ?? string.Empty,
            value.GetProperty("detailLevel").GetString() ?? string.Empty,
            ReadCloseoutPresentationColumns(value.GetProperty("columns")),
            ReadCloseoutPresentationMetadata(value.GetProperty("metadata")));

    private static P10CloseoutDomPresentationRow[]
        ReadCloseoutDomPresentationRows(JsonElement value)
        => value.EnumerateArray().Select(ReadCloseoutDomPresentationRow)
            .OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();

    private static P10CloseoutDomPresentationRow
        ReadCloseoutDomPresentationRow(JsonElement value)
        => new(
            value.GetProperty("id").GetString() ?? string.Empty,
            value.GetProperty("schemaVersion").GetString() ?? string.Empty,
            value.GetProperty("detailLevel").GetString() ?? string.Empty,
            ReadCloseoutPresentationColumns(value.GetProperty("columns")),
            new P10CloseoutDisplayedPresentationMetadata(
                value.GetProperty("displayedMetadata")
                    .GetProperty("total").GetInt64(),
                value.GetProperty("displayedMetadata")
                    .GetProperty("rootCause").GetString() ?? string.Empty,
                value.GetProperty("displayedMetadata")
                    .GetProperty("rowCountBeforeRedaction").GetInt32(),
                value.GetProperty("displayedMetadata")
                    .GetProperty("rowCountAfterRedaction").GetInt32()));

    private static IReadOnlyDictionary<string, string>
        ReadCloseoutPresentationColumns(JsonElement value)
    {
        var names = value.EnumerateObject().Select(property => property.Name)
            .ToArray();
        HarnessAssert.True(
            names.SequenceEqual(
                CloseoutPresentationColumnNames,
                StringComparer.Ordinal),
            "P10-CLOSE exact server presentation column order");
        return CloseoutPresentationColumnNames.ToDictionary(
            name => name,
            name => value.GetProperty(name).GetString() ?? string.Empty,
            StringComparer.Ordinal);
    }

    private static P10CloseoutPresentationMetadata
        ReadCloseoutPresentationMetadata(JsonElement value)
        => new(
            value.GetProperty("total").GetInt64(),
            value.GetProperty("rootCause").GetString() ?? string.Empty,
            value.GetProperty("rowCountBeforeRedaction").GetInt32(),
            value.GetProperty("rowCountAfterRedaction").GetInt32(),
            value.GetProperty("permissionCodes").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty).ToArray(),
            ReadCloseoutPresentationLinks(
                value.GetProperty("evidenceLinks")),
            ReadCloseoutPresentationLinks(
                value.GetProperty("sourceLinks")));

    private static P10CloseoutPresentationLink[]
        ReadCloseoutPresentationLinks(JsonElement value)
        => value.EnumerateArray().Select(item =>
                new P10CloseoutPresentationLink(
                    item.GetProperty("rel").GetString() ?? string.Empty,
                    item.GetProperty("href").GetString() ?? string.Empty,
                    item.GetProperty("method").GetString() ?? string.Empty))
            .ToArray();

    private static P10CloseoutDetailPresentation
        ReadCloseoutDetailPresentation(JsonElement value)
        => new(
            ReadCloseoutApiPresentationRow(value.GetProperty("apiRow")),
            ReadCloseoutDomPresentationRow(value.GetProperty("domRow")),
            value.GetProperty("totalText").GetString() ?? string.Empty,
            value.GetProperty("rootCauseText").GetString() ?? string.Empty,
            value.GetProperty("permissionText").GetString() ?? string.Empty,
            ReadCloseoutPresentationLinks(
                value.GetProperty("evidenceLinks")),
            ReadCloseoutPresentationLinks(
                value.GetProperty("sourceLinks")),
            value.GetProperty("detailRequestCount").GetInt32(),
            value.GetProperty("detailSuccessCount").GetInt32());

    private static P10CloseoutDomPresentationRow CloseoutDisplayProjection(
        P10CloseoutApiPresentationRow value)
        => new(
            value.Id,
            value.SchemaVersion,
            value.DetailLevel,
            value.Columns,
            new P10CloseoutDisplayedPresentationMetadata(
                value.Metadata.Total,
                value.Metadata.RootCause,
                value.Metadata.RowCountBeforeRedaction,
                value.Metadata.RowCountAfterRedaction));

    private static bool CloseoutJsonEqual<T>(T left, T right)
        => JsonSerializer.Serialize(left, EvidenceJson.Options) ==
           JsonSerializer.Serialize(right, EvidenceJson.Options);

    private static bool CloseoutLinksEqual(
        IReadOnlyList<P10CloseoutPresentationLink> left,
        IReadOnlyList<P10CloseoutPresentationLink> right)
        => left.Count == right.Count && left.Select((value, index) =>
            value == right[index]).All(value => value);

    private static string CloseoutPresentationLinkSetValue(
        IReadOnlyList<P10CloseoutPresentationLink> links)
        => string.Join("\u001d", links.Select(link => string.Join(
            "\u001c", link.Rel, link.Href, link.Method)));

    private static IReadOnlyList<string>
        CloseoutDisplayPresentationHashCells(
            P10CloseoutDomPresentationRow value)
        =>
        [
            value.Id,
            value.SchemaVersion,
            value.DetailLevel,
            .. CloseoutPresentationColumnNames.Select(name =>
                value.Columns[name]),
            value.DisplayedMetadata.Total.ToString(
                CultureInfo.InvariantCulture),
            value.DisplayedMetadata.RootCause,
            value.DisplayedMetadata.RowCountBeforeRedaction.ToString(
                CultureInfo.InvariantCulture),
            value.DisplayedMetadata.RowCountAfterRedaction.ToString(
                CultureInfo.InvariantCulture)
        ];

    private static IReadOnlyList<string> CloseoutApiPresentationHashCells(
        P10CloseoutApiPresentationRow value)
        =>
        [
            value.Id,
            value.Status,
            value.SchemaVersion,
            value.DetailLevel,
            .. CloseoutPresentationColumnNames.Select(name =>
                value.Columns[name]),
            value.Metadata.Total.ToString(CultureInfo.InvariantCulture),
            value.Metadata.RootCause,
            value.Metadata.RowCountBeforeRedaction.ToString(
                CultureInfo.InvariantCulture),
            value.Metadata.RowCountAfterRedaction.ToString(
                CultureInfo.InvariantCulture),
            string.Join("\u001d", value.Metadata.PermissionCodes),
            CloseoutPresentationLinkSetValue(value.Metadata.EvidenceLinks),
            CloseoutPresentationLinkSetValue(value.Metadata.SourceLinks)
        ];

    private static string CloseoutDisplayPresentationRowsSha256(
        IEnumerable<P10CloseoutDomPresentationRow> rows)
        => CloseoutMatrixSha256(rows.Select(
            CloseoutDisplayPresentationHashCells));

    private static string CloseoutApiPresentationRowsSha256(
        IEnumerable<P10CloseoutApiPresentationRow> rows)
        => CloseoutMatrixSha256(rows.Select(
            CloseoutApiPresentationHashCells));

    private async Task<P10CloseoutSourceParity>
        ValidateCloseoutSourceObservationAsync(
            JsonElement observation,
            CancellationToken ct)
    {
        HarnessAssert.True(
            observation.GetProperty("operatorDetailsOpened").GetBoolean(),
            "P10-CLOSE operator provenance drill-down was not opened");
        var dom = ReadScalarObject(observation.GetProperty("domValues"));
        var api = ReadScalarObject(observation.GetProperty("apiValues"));
        var domSha256 = CloseoutKeyValueSha256(dom);
        var apiSha256 = CloseoutKeyValueSha256(api);
        HarnessAssert.Equal(
            observation.GetProperty("domValuesSha256").GetString(),
            domSha256,
            "P10-CLOSE DOM provenance hash recompute");
        HarnessAssert.Equal(
            observation.GetProperty("apiValuesSha256").GetString(),
            apiSha256,
            "P10-CLOSE API provenance hash recompute");

        var prepared = _closeoutPrepared ?? throw new InvalidOperationException(
            "P10-CLOSE prepared fixture is unavailable.");
        var target = await LoadRunAsync(
            prepared.TargetReconciliationId,
            ct);
        var mongo = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["sourceLifecycleHash"] = BsonText(
                target,
                "sourceLifecycleHash"),
            ["sourceLifecycleRevision"] = BsonLong(
                target,
                "sourceLifecycleRevision").ToString(
                    CultureInfo.InvariantCulture),
            ["sourcePayloadHash"] = BsonText(target, "sourcePayloadHash"),
            ["sourcePayloadRevision"] = BsonLong(
                target,
                "sourcePayloadRevision").ToString(CultureInfo.InvariantCulture),
            ["sourceReportId"] = BsonText(target, "sourceReportId")
        };
        var mongoSha256 = CloseoutKeyValueSha256(mongo);
        var domKeys = new[]
        {
            "sourceReportId",
            "sourcePayloadRevision",
            "sourceLifecycleRevision"
        };
        HarnessAssert.True(
            domKeys.All(key =>
                dom.TryGetValue(key, out var domValue) &&
                api.TryGetValue(key, out var apiValue) &&
                domValue == apiValue) &&
            api.Count == 5 &&
            api.All(pair => mongo.TryGetValue(pair.Key, out var value) &&
                value == pair.Value) &&
            apiSha256 == mongoSha256,
            "P10-CLOSE operator DOM/API/direct-Mongo provenance parity");
        return new P10CloseoutSourceParity(
            true,
            prepared.TargetReconciliationId,
            dom,
            api,
            mongo,
            domSha256,
            apiSha256,
            mongoSha256);
    }

    private async Task<P10CloseoutReviewParity>
        ValidateCloseoutReviewObservationAsync(
            JsonElement observation,
            JsonElement assertions,
            P10CloseoutRawBrowserCase rawCase,
            CancellationToken ct)
    {
        var prepared = _closeoutPrepared ?? throw new InvalidOperationException(
            "P10-CLOSE prepared fixture is unavailable.");
        var registry = _closeoutStateRegistry ??
            throw new InvalidOperationException(
                "P10-CLOSE production state registry is unavailable.");
        var targetInitial = registry.Rows.Single(value =>
            value.ReconciliationId == prepared.TargetReconciliationId);
        var initialStateRevision = targetInitial.StateRevision ?? -1;
        var initialReviewRevision =
            targetInitial.ReviewDecisionRevision ?? -1;
        var reviewerDetailRequestCount = observation.GetProperty(
            "reviewerDetailRequestCount").GetInt32();
        var executorDetailRequestCount = observation.GetProperty(
            "executorDetailRequestCount").GetInt32();
        var browserGates = observation.GetProperty("gates")
            .EnumerateArray()
            .Select(value => new P10CloseoutObservedReview(
                value.GetProperty("gate").GetString() ?? string.Empty,
                value.GetProperty("actorId").GetString() ?? string.Empty,
                value.GetProperty("commandId").GetString() ?? string.Empty,
                value.GetProperty("status").GetInt32(),
                value.GetProperty("stateRevisionBefore").GetInt64(),
                value.GetProperty("expectedStateRevision").GetInt64(),
                value.GetProperty("stateRevisionAfter").GetInt64(),
                value.GetProperty("submissionMode").GetString() ??
                    string.Empty,
                value.GetProperty("uiControlName").GetString() ??
                    string.Empty,
                value.GetProperty("uiStatusText").GetString() ??
                    string.Empty,
                value.GetProperty("uiStatusTextSha256").GetString() ??
                    string.Empty,
                value.GetProperty("apiDecisionId").GetString() ??
                    string.Empty,
                value.GetProperty("apiPermissionSnapshotSha256").GetString() ??
                    string.Empty,
                value.GetProperty("apiDocumentSha256").GetString() ??
                    string.Empty))
            .ToArray();
        var reviewRows = await RequireDatabase()
            .GetCollection<StatisticReconciliationIndependentReviewAuditRecord>(
                "work_report_statistic_reconciliation_reviews")
            .Find(value => value.ReconciliationId ==
                    prepared.TargetReconciliationId &&
                (value.RecordKind ==
                     StatisticReconciliationReviewRecordKinds.Decision ||
                 value.RecordKind ==
                     StatisticReconciliationReviewRecordKinds.Supersession))
            .ToListAsync(ct);
        foreach (var row in reviewRows)
        {
            StatisticReconciliationIndependentReviewService.ValidateStored(row);
        }
        var lineageRows = reviewRows
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .Select(CloseoutMongoReviewPin)
            .ToArray();
        var mongoRows = reviewRows
            .Where(value => value.RecordKind ==
                StatisticReconciliationReviewRecordKinds.Decision)
            .OrderBy(value => Array.IndexOf(
                CloseoutReviewGates,
                value.Gate))
            .Select(CloseoutMongoReviewPin)
            .ToArray();
        var gateSequenceExact = browserGates.Length == 5 &&
            mongoRows.Length == 5 &&
            browserGates.Select(value => value.Gate).SequenceEqual(
                CloseoutReviewGates,
                StringComparer.Ordinal) &&
            browserGates.Select(value => value.ActorId)
                .Distinct(StringComparer.Ordinal).Count() == 5 &&
            browserGates.Select((value, index) =>
                value.HttpStatus == 200 &&
                value.SubmissionMode == "UI_CLICK" &&
                value.StateRevisionBefore == initialStateRevision + index &&
                value.ExpectedStateRevision == value.StateRevisionBefore &&
                value.StateRevisionAfter == value.StateRevisionBefore + 1 &&
                value.UiControlName == $"Phê duyệt {value.Gate}" &&
                value.UiStatusText ==
                    $"Đã ghi APPROVE cho cổng {value.Gate}." &&
                value.UiStatusTextSha256 == HashText(value.UiStatusText) &&
                mongoRows[index].Gate == value.Gate &&
                mongoRows[index].ActorId == value.ActorId &&
                mongoRows[index].CommandId == value.CommandId &&
                mongoRows[index].DecisionId == value.ApiDecisionId &&
                mongoRows[index].PermissionSnapshotSha256 ==
                    value.ApiPermissionSnapshotSha256 &&
                mongoRows[index].DocumentSha256 ==
                    value.ApiDocumentSha256 &&
                mongoRows[index].Decision == "APPROVE" &&
                mongoRows[index].Status == "ACTIVE" &&
                mongoRows[index].StateRevision ==
                    value.StateRevisionBefore)
            .All(value => value);

        var denials = observation.GetProperty("denials");
        var stale = ReadCloseoutDenial(
            "STALE_CAS",
            denials.GetProperty("staleCas"));
        var sod = ReadCloseoutDenial(
            "SEPARATION_OF_DUTIES",
            denials.GetProperty("separationOfDuties"));
        var outsiderDetail = ReadCloseoutDenial(
            "OUTSIDER_DETAIL",
            denials.GetProperty("outsiderDetail"));
        var outsiderExport = ReadCloseoutDenial(
            "OUTSIDER_EXPORT",
            denials.GetProperty("outsiderExport"));
        var network = ReadCloseoutNetworkEvidence(rawCase);
        var reviewPath =
            $"/api/works/{Fixture().WorkId}/statistics/{Fixture().ScopeAssignmentId}/reconciliations/{prepared.TargetReconciliationId}/review-decisions";
        var detailPath =
            $"/api/works/{Fixture().WorkId}/statistics/{Fixture().ScopeAssignmentId}/reconciliations/{prepared.TargetReconciliationId}/detail";
        var exportPath =
            $"/api/works/{Fixture().WorkId}/statistics/{Fixture().ScopeAssignmentId}/reconciliations/{prepared.TargetReconciliationId}/evidence-exports";
        var traceRows = ReadCloseoutReviewUiTrace(rawCase);
        var traceSequenceExact = traceRows.Length == 5 &&
            browserGates.Length == 5 &&
            traceRows.Select((value, index) =>
                value.Sequence == index + 1 &&
                value.Action == "review-ui-click" &&
                value.Gate == browserGates[index].Gate &&
                value.ActorId == browserGates[index].ActorId &&
                value.CommandId == browserGates[index].CommandId &&
                value.Method == "POST" &&
                value.Path == reviewPath &&
                value.Selector ==
                    $"li[data-review-gate='{value.Gate}'] button[data-review-decision='APPROVE']" &&
                value.AccessibleName == browserGates[index].UiControlName &&
                value.StateRevisionBefore ==
                    browserGates[index].StateRevisionBefore &&
                value.ExpectedStateRevision ==
                    browserGates[index].ExpectedStateRevision &&
                value.ResponseStatus == browserGates[index].HttpStatus &&
                value.StateRevisionAfter ==
                    browserGates[index].StateRevisionAfter &&
                value.UiStatusTextSha256 ==
                    browserGates[index].UiStatusTextSha256)
            .All(value => value);
        var traceHash = CloseoutMatrixSha256(traceRows.Select(value =>
            (IReadOnlyList<string>)
            [
                value.Sequence.ToString(CultureInfo.InvariantCulture),
                value.Action,
                value.Gate,
                value.ActorId,
                value.CommandId,
                value.Method,
                value.Path,
                value.Selector,
                value.AccessibleName,
                value.StateRevisionBefore.ToString(
                    CultureInfo.InvariantCulture),
                value.ExpectedStateRevision.ToString(
                    CultureInfo.InvariantCulture),
                value.ResponseStatus.ToString(CultureInfo.InvariantCulture),
                value.StateRevisionAfter.ToString(
                    CultureInfo.InvariantCulture),
                value.UiStatusTextSha256
            ]));
        var expectedDenials = new[]
        {
            (Pin: stale, Method: "POST", Path: reviewPath, Status: 409),
            (Pin: sod, Method: "POST", Path: reviewPath, Status: 403),
            (Pin: outsiderDetail, Method: "GET", Path: detailPath,
                Status: 403),
            (Pin: outsiderExport, Method: "POST", Path: exportPath,
                Status: 404)
        };
        var networkBound = expectedDenials.All(value =>
            value.Pin.Method == value.Method &&
            value.Pin.Path == value.Path &&
            value.Pin.HttpStatus == value.Status &&
            network.BrowserRows.Any(row =>
                row.Method == value.Method &&
                row.Path == value.Path &&
                row.HttpStatus == value.Status) &&
            network.ProxyRows.Any(row =>
                row.Method == value.Method &&
                row.Path == value.Path &&
                row.HttpStatus == value.Status));
        var successNetworkBound =
            network.BrowserRows.Count(value =>
                value.Method == "POST" &&
                value.Path == reviewPath &&
                value.HttpStatus == 200) == 5 &&
            network.ProxyRows.Count(value =>
                value.Method == "POST" &&
                value.Path == reviewPath &&
                value.HttpStatus == 200) == 5;
        var denialCommandIds = new[]
        {
            stale.CommandId,
            sod.CommandId,
            outsiderExport.CommandId
        };
        var denialReviewRowCount = reviewRows.Count(value =>
            denialCommandIds.Contains(
                value.OperationCommandId,
                StringComparer.Ordinal));
        var denialExportRowCount = await RequireDatabase()
            .GetCollection<StatisticReconciliationEvidenceExport>(
                "work_report_statistic_reconciliation_exports")
            .CountDocumentsAsync(value => denialCommandIds.Contains(
                value.CommandId), cancellationToken: ct);
        var run = await RequireDatabase()
            .GetCollection<StatisticReconciliationRun>(RunCollection)
            .Find(value => value.Id == prepared.TargetReconciliationId &&
                !value.IsDeleted)
            .SingleAsync(ct);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            run);
        var sodActorGuardField = run.ActorUserId == sod.ActorId
            ? "ActorUserId"
            : run.LatestWriterUserId == sod.ActorId
                ? "LatestWriterUserId"
                : string.Empty;
        var mappingGate = browserGates.Single(value =>
            value.Gate == "MAPPING");
        var staleExact = stale.CommandId.EndsWith("-stale",
                StringComparison.Ordinal) &&
            stale.HttpStatus == 409 &&
            stale.ErrorCode is null &&
            stale.ProblemTitle == "Independent review state conflict." &&
            stale.BodyBytes > 0 &&
            stale.AuthorizedSummaryStatus == 200 &&
            stale.ExpectedStateRevision ==
                stale.StateRevisionBefore + 1 &&
            stale.StateRevisionAfter == stale.StateRevisionBefore &&
            mappingGate.StateRevisionBefore == stale.StateRevisionBefore;
        var sodExact = sod.HttpStatus == 403 &&
            sod.ErrorCode is null &&
            sod.ProblemTitle is null &&
            sod.BodyBytes == 0 &&
            sod.AuthorizedSummaryStatus == 200 &&
            sod.ExpectedStateRevision == sod.StateRevisionBefore &&
            sod.StateRevisionAfter == sod.StateRevisionBefore &&
            sod.StateRevisionBefore == initialStateRevision + 5 &&
            sodActorGuardField.Length > 0;
        var outsiderDetailExact = outsiderDetail.HttpStatus == 403 &&
            outsiderDetail.ErrorCode == "STAT_RECONCILIATION_FORBIDDEN" &&
            outsiderDetail.BodyBytes > 0 &&
            outsiderDetail.UiRole == "status" &&
            outsiderDetail.UiAriaLive == "polite" &&
            outsiderDetail.UiText.Length > 0 &&
            outsiderDetail.UiTextSha256 ==
                HashText(outsiderDetail.UiText) &&
            outsiderDetail.UiRetryButtonCount == 1 &&
            outsiderDetail.UiArticleCount == 0;
        var outsiderExportExact = outsiderExport.HttpStatus == 404 &&
            outsiderExport.ErrorCode is null &&
            outsiderExport.ProblemTitle is null &&
            outsiderExport.BodyBytes == 0;
        var denialParity = new P10CloseoutDenialParity(
            stale,
            sod,
            outsiderDetail,
            outsiderExport,
            networkBound,
            successNetworkBound,
            denialReviewRowCount,
            denialExportRowCount,
            sodActorGuardField,
            run.StateRevision,
            run.ReviewDecisionRevision,
            run.StateHash,
            true);
        var browserHash = CloseoutMatrixSha256(browserGates.Select(value =>
            (IReadOnlyList<string>)
            [
                value.Gate,
                value.ActorId,
                value.CommandId,
                value.StateRevisionBefore.ToString(
                    CultureInfo.InvariantCulture),
                value.StateRevisionAfter.ToString(
                    CultureInfo.InvariantCulture),
                value.UiStatusTextSha256,
                value.ApiDecisionId,
                value.ApiPermissionSnapshotSha256,
                value.ApiDocumentSha256
            ]));
        var mongoHash = CloseoutMatrixSha256(mongoRows.Select(value =>
            (IReadOnlyList<string>)
            [
                value.Gate,
                value.ActorId,
                value.CommandId,
                value.StateRevision.ToString(CultureInfo.InvariantCulture),
                value.DocumentSha256
            ]));
        HarnessAssert.True(
            gateSequenceExact &&
            traceSequenceExact &&
            reviewerDetailRequestCount == 0 &&
            executorDetailRequestCount == 0 &&
            observation.GetProperty("apiApproved").GetBoolean() &&
            observation.GetProperty("apiApprovedGateCount").GetInt32() == 5 &&
            staleExact && sodExact && outsiderDetailExact &&
            outsiderExportExact && networkBound && successNetworkBound &&
            denialReviewRowCount == 0 && denialExportRowCount == 0 &&
            run.StateRevision == initialStateRevision + 5 &&
            run.ReviewDecisionRevision == initialReviewRevision + 5 &&
            assertions.GetProperty("operatorProvenanceVisible")
                .GetBoolean() &&
            assertions.GetProperty("redactedOperatorDetailAbsent")
                .GetBoolean(),
            "P10-CLOSE five UI gates/opaque denials/direct Mongo parity");
        var lineageHash = CloseoutMatrixSha256(lineageRows.Select(value =>
            (IReadOnlyList<string>)
            [
                value.Id,
                value.RecordKind,
                value.Status,
                value.OperationCommandId,
                value.RequestSha256,
                value.ReconciliationId,
                value.GenerationId,
                value.GenerationSha256,
                value.SemanticVerdictSha256,
                value.ReviewRecordSha256,
                value.Gate,
                value.Decision ?? string.Empty,
                value.ReviewerActorId,
                value.PermissionSnapshotSha256,
                value.RowCountBeforeRedaction.ToString(
                    CultureInfo.InvariantCulture),
                value.RowCountAfterRedaction.ToString(
                    CultureInfo.InvariantCulture),
                value.StateRevision.ToString(CultureInfo.InvariantCulture),
                value.SupersedesDecisionId ?? string.Empty,
                value.SupersededByGenerationId ?? string.Empty,
                value.DocumentSha256,
                value.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)
            ]));
        return new P10CloseoutReviewParity(
            true,
            browserGates,
            traceRows,
            mongoRows,
            lineageRows,
            browserHash,
            traceHash,
            mongoHash,
            lineageHash,
            reviewerDetailRequestCount,
            executorDetailRequestCount,
            denialParity);
    }


    private P10CloseoutReviewUiTrace[] ReadCloseoutReviewUiTrace(
        P10CloseoutRawBrowserCase rawCase)
    {
        RequireCloseoutOwnedArtifact(rawCase.TracePath);
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(rawCase.TracePath));
        var root = document.RootElement;
        HarnessAssert.True(
            root.EnumerateObject().Select(value => value.Name).SequenceEqual(
                new[]
                {
                    "schemaVersion", "caseId", "actions",
                    "consoleErrors", "pageErrors"
                },
                StringComparer.Ordinal) &&
            root.GetProperty("schemaVersion").GetString() ==
                "P10_CLOSE_BROWSER_TRACE_V1" &&
            root.GetProperty("caseId").GetString() == rawCase.CaseId &&
            root.GetProperty("consoleErrors").GetArrayLength() == 0 &&
            root.GetProperty("pageErrors").GetArrayLength() == 0,
            "P10-CLOSE exact review trace envelope");
        var expectedKeys = new[]
        {
            "sequence", "action", "gate", "actorId", "commandId",
            "method", "path", "selector", "accessibleName",
            "stateRevisionBefore", "expectedStateRevision",
            "responseStatus", "stateRevisionAfter", "uiStatusTextSha256"
        };
        return root.GetProperty("actions").EnumerateArray()
            .Select(value =>
            {
                HarnessAssert.True(
                    value.EnumerateObject().Select(item => item.Name)
                        .SequenceEqual(expectedKeys, StringComparer.Ordinal),
                    "P10-CLOSE exact review UI trace row keys/order");
                return new P10CloseoutReviewUiTrace(
                    value.GetProperty("sequence").GetInt32(),
                    value.GetProperty("action").GetString() ?? string.Empty,
                    value.GetProperty("gate").GetString() ?? string.Empty,
                    value.GetProperty("actorId").GetString() ?? string.Empty,
                    value.GetProperty("commandId").GetString() ?? string.Empty,
                    value.GetProperty("method").GetString() ?? string.Empty,
                    value.GetProperty("path").GetString() ?? string.Empty,
                    value.GetProperty("selector").GetString() ?? string.Empty,
                    value.GetProperty("accessibleName").GetString() ??
                        string.Empty,
                    value.GetProperty("stateRevisionBefore").GetInt64(),
                    value.GetProperty("expectedStateRevision").GetInt64(),
                    value.GetProperty("responseStatus").GetInt32(),
                    value.GetProperty("stateRevisionAfter").GetInt64(),
                    value.GetProperty("uiStatusTextSha256").GetString() ??
                        string.Empty);
            })
            .ToArray();
    }

    private static P10CloseoutMongoReviewPin CloseoutMongoReviewPin(
        StatisticReconciliationIndependentReviewAuditRecord value)
        => new(
            value.Id,
            value.RecordKind,
            value.Status,
            value.OperationCommandId,
            value.RequestSha256,
            value.ReconciliationId,
            value.GenerationId,
            value.GenerationSha256,
            value.SemanticVerdictSha256,
            value.ReviewRecordSha256,
            value.Gate,
            value.Decision,
            value.ReviewerActorId,
            value.PermissionSnapshotSha256,
            value.RowCountBeforeRedaction,
            value.RowCountAfterRedaction,
            value.StateRevision,
            value.SupersedesDecisionId,
            value.SupersededByGenerationId,
            value.DocumentSha256,
            value.CreatedAtUtc);

    private async Task<P10CloseoutExportParity>
        ValidateCloseoutExportObservationAsync(
            JsonElement observation,
            CancellationToken ct)
    {
        var prepared = _closeoutPrepared ?? throw new InvalidOperationException(
            "P10-CLOSE prepared fixture is unavailable.");
        var operatorPresentation = ReadCloseoutDetailPresentation(
            observation.GetProperty("operatorPresentation"));
        var redactedPresentation = ReadCloseoutDetailPresentation(
            observation.GetProperty("redactedPresentation"));
        var targetRun = await RequireDatabase()
            .GetCollection<StatisticReconciliationRun>(RunCollection)
            .Find(value => value.Id == prepared.TargetReconciliationId &&
                !value.IsDeleted)
            .SingleAsync(ct);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
            targetRun);
        var operatorMongo = await BuildCloseoutMongoPresentationAsync(
            [targetRun], "admin", operatorDetail: true, ct);
        var redactedMongo = await BuildCloseoutMongoPresentationAsync(
            [targetRun], "executor", operatorDetail: false, ct);
        var operatorMongoRow = operatorMongo.Rows.Single();
        var redactedMongoRow = redactedMongo.Rows.Single();
        HarnessAssert.True(
            operatorPresentation.DetailRequestCount == 1 &&
            operatorPresentation.DetailSuccessCount == 1 &&
            redactedPresentation.DetailRequestCount == 0 &&
            redactedPresentation.DetailSuccessCount == 0 &&
            operatorPresentation.SourceLinks.Count == 1 &&
            redactedPresentation.SourceLinks.Count == 0 &&
            CloseoutJsonEqual(operatorPresentation.ApiRow, operatorMongoRow) &&
            CloseoutJsonEqual(redactedPresentation.ApiRow, redactedMongoRow) &&
            CloseoutJsonEqual(operatorPresentation.DomRow,
                CloseoutDisplayProjection(operatorMongoRow)) &&
            CloseoutJsonEqual(redactedPresentation.DomRow,
                CloseoutDisplayProjection(redactedMongoRow)) &&
            CloseoutLinksEqual(operatorPresentation.EvidenceLinks,
                operatorMongoRow.Metadata.EvidenceLinks) &&
            CloseoutLinksEqual(operatorPresentation.SourceLinks,
                operatorMongoRow.Metadata.SourceLinks) &&
            CloseoutLinksEqual(redactedPresentation.EvidenceLinks,
                redactedMongoRow.Metadata.EvidenceLinks) &&
            CloseoutLinksEqual(redactedPresentation.SourceLinks,
                redactedMongoRow.Metadata.SourceLinks),
            "P10-CLOSE operator/redacted raw presentation DOM/API/Mongo parity");

        var reviewLineageRaw = await RequireDatabase()
            .GetCollection<StatisticReconciliationIndependentReviewAuditRecord>(
                "work_report_statistic_reconciliation_reviews")
            .Find(value => value.ReconciliationId ==
                    prepared.TargetReconciliationId &&
                (value.RecordKind ==
                     StatisticReconciliationReviewRecordKinds.Decision ||
                 value.RecordKind ==
                     StatisticReconciliationReviewRecordKinds.Supersession))
            .ToListAsync(ct);
        foreach (var row in reviewLineageRaw)
        {
            StatisticReconciliationIndependentReviewService.ValidateStored(row);
        }
        var reviewLineage = reviewLineageRaw
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .Select(CloseoutMongoReviewPin)
            .ToArray();
        var exportSemantic = BuildCloseoutExportSemanticOracle(
            operatorMongo.Inputs.Single(),
            operatorMongo.PermissionOracle.PermissionSnapshotSha256,
            redactedMongo.PermissionOracle.PermissionSnapshotSha256,
            reviewLineage);

        var operatorJson = ReadExportSurface(
            observation.GetProperty("operatorJson"));
        var operatorCsv = ReadExportSurface(
            observation.GetProperty("operatorCsv"));
        var redactedJson = ReadExportSurface(
            observation.GetProperty("redactedJson"));
        foreach (var surface in new[]
                 {
                     operatorJson,
                     operatorCsv,
                     redactedJson
                 })
        {
            HarnessAssert.Equal(
                surface.ClaimedRowsSha256,
                CloseoutMatrixSha256(surface.Rows),
                $"P10-CLOSE {surface.Label} normalized row hash recompute");
            RequireCloseoutOwnedArtifact(surface.DownloadPath);
            var bytes = await File.ReadAllBytesAsync(surface.DownloadPath, ct);
            HarnessAssert.Equal(
                surface.DownloadSha256,
                HashBytes(bytes),
                $"P10-CLOSE {surface.Label} download hash recompute");
            HarnessAssert.True(
                surface.Rows.Count > 0 &&
                surface.Rows.All(row => row.Count == 8 &&
                    row[6] == surface.PermissionSnapshotSha256) &&
                surface.PermissionSnapshotSha256.Length == 64,
                $"P10-CLOSE {surface.Label} actor permission rows");
        }
        var operatorJsonCsvAllColumnsEqual =
            CloseoutRowsEqual(operatorJson.Rows, operatorCsv.Rows) &&
            operatorJson.ClaimedRowsSha256 ==
                operatorCsv.ClaimedRowsSha256 &&
            operatorJson.PermissionSnapshotSha256 ==
                operatorCsv.PermissionSnapshotSha256;
        var operatorRedactedNonPermissionEqual =
            CloseoutRowsEqualExceptPermission(
                operatorJson.Rows,
                redactedJson.Rows);
        var actorPermissionPinsDiffer =
            operatorJson.PermissionSnapshotSha256 !=
                redactedJson.PermissionSnapshotSha256 &&
            operatorJson.ClaimedRowsSha256 !=
                redactedJson.ClaimedRowsSha256;
        HarnessAssert.True(
            operatorJsonCsvAllColumnsEqual &&
            operatorRedactedNonPermissionEqual &&
            actorPermissionPinsDiffer &&
            CloseoutRowsSemanticallyEqual(operatorJson.Rows,
                exportSemantic.OperatorRows) &&
            CloseoutRowsSemanticallyEqual(operatorCsv.Rows,
                exportSemantic.OperatorRows) &&
            CloseoutRowsSemanticallyEqual(redactedJson.Rows,
                exportSemantic.RedactedRows),
            "P10-CLOSE direct-Mongo observation/export semantic row parity");

        HarnessAssert.True(
            operatorJson.PermissionSnapshotSha256 ==
                operatorMongo.PermissionOracle.PermissionSnapshotSha256 &&
            operatorCsv.PermissionSnapshotSha256 ==
                operatorMongo.PermissionOracle.PermissionSnapshotSha256 &&
            redactedJson.PermissionSnapshotSha256 ==
                redactedMongo.PermissionOracle.PermissionSnapshotSha256 &&
            operatorPresentation.ApiRow.Columns["Permission"] ==
                operatorJson.PermissionSnapshotSha256 &&
            redactedPresentation.ApiRow.Columns["Permission"] ==
                redactedJson.PermissionSnapshotSha256 &&
            CloseoutPresentationColumnNames.Where(name => name != "Permission")
                .All(name => operatorPresentation.ApiRow.Columns[name] ==
                    redactedPresentation.ApiRow.Columns[name]),
            "P10-CLOSE presentation/export/direct-Mongo actor permission cross-bind");

        var exports = await RequireDatabase()
            .GetCollection<StatisticReconciliationEvidenceExport>(
                "work_report_statistic_reconciliation_exports")
            .Find(value => value.ReconciliationId ==
                prepared.TargetReconciliationId)
            .ToListAsync(ct);
        HarnessAssert.Equal(
            3,
            exports.Count,
            "P10-CLOSE exact three successful export records");
        var operatorJsonMongo = RequireExportMongo(
            exports,
            operatorJson,
            "JSON",
            "OPERATOR");
        var operatorCsvMongo = RequireExportMongo(
            exports,
            operatorCsv,
            "CSV",
            "OPERATOR");
        var redactedJsonMongo = RequireExportMongo(
            exports,
            redactedJson,
            "JSON",
            "REDACTED");
        foreach (var pair in new[]
                 {
                     (Surface: operatorJson, Mongo: operatorJsonMongo),
                     (Surface: operatorCsv, Mongo: operatorCsvMongo),
                     (Surface: redactedJson, Mongo: redactedJsonMongo)
                 })
        {
            var bytes = File.ReadAllBytes(pair.Surface.DownloadPath);
            HarnessAssert.True(
                bytes.AsSpan().SequenceEqual(pair.Mongo.Content) &&
                pair.Mongo.ContentSha256 == HashBytes(pair.Mongo.Content) &&
                pair.Mongo.ContentLength == pair.Mongo.Content.LongLength &&
                pair.Mongo.FileName == pair.Surface.FileName &&
                pair.Mongo.PermissionSnapshotSha256 ==
                    pair.Surface.PermissionSnapshotSha256,
                $"P10-CLOSE {pair.Surface.Label} download/direct-Mongo bytes/permission");
        }
        var operatorJsonManifest = RequireCloseoutManifestBinding(
            operatorJson.Manifest,
            operatorJsonMongo,
            exportSemantic.OperatorRows.Count,
            exportSemantic.OperatorRowManifestSha256,
            exportSemantic);
        var operatorCsvManifestDocument = JsonDocument.Parse(
            operatorCsvMongo.ManifestJson);
        using (operatorCsvManifestDocument)
        {
            _ = RequireCloseoutManifestBinding(
                operatorCsvManifestDocument.RootElement,
                operatorCsvMongo,
                exportSemantic.OperatorRows.Count,
                exportSemantic.OperatorRowManifestSha256,
                exportSemantic);
        }
        var redactedJsonManifest = RequireCloseoutManifestBinding(
            redactedJson.Manifest,
            redactedJsonMongo,
            exportSemantic.RedactedRows.Count,
            exportSemantic.RedactedRowManifestSha256,
            exportSemantic);
        HarnessAssert.True(
            operatorJsonMongo.GenerationId ==
                exportSemantic.VerdictGenerationId &&
            operatorJsonMongo.GenerationSha256 ==
                exportSemantic.VerdictGenerationSha256 &&
            operatorJsonMongo.SemanticVerdictSha256 ==
                exportSemantic.SemanticVerdictSha256 &&
            redactedJsonMongo.GenerationId ==
                exportSemantic.VerdictGenerationId &&
            redactedJsonMongo.GenerationSha256 ==
                exportSemantic.VerdictGenerationSha256 &&
            redactedJsonMongo.SemanticVerdictSha256 ==
                exportSemantic.SemanticVerdictSha256 &&
            exports.All(value =>
                value.ReviewSignatureSha256 ==
                    exportSemantic.ReviewSignatureSha256 &&
                value.FinalApprovalSha256 ==
                    exportSemantic.FinalApprovalSha256),
            "P10-CLOSE export/current verdict generation binding");

        return new P10CloseoutExportParity(
            true,
            operatorPresentation,
            redactedPresentation,
            exportSemantic,
            operatorMongoRow,
            redactedMongoRow,
            operatorMongo.PermissionOracle,
            redactedMongo.PermissionOracle,
            operatorMongo.Inputs,
            operatorJson.Rows,
            operatorCsv.Rows,
            redactedJson.Rows,
            operatorJson.ClaimedRowsSha256,
            operatorCsv.ClaimedRowsSha256,
            redactedJson.ClaimedRowsSha256,
            operatorJsonCsvAllColumnsEqual,
            operatorRedactedNonPermissionEqual,
            actorPermissionPinsDiffer,
            [
                CloseoutMongoExportPin(
                    operatorJsonMongo,
                    operatorJsonManifest),
                CloseoutMongoExportPin(
                    operatorCsvMongo,
                    ReadCloseoutStoredManifest(operatorCsvMongo)),
                CloseoutMongoExportPin(
                    redactedJsonMongo,
                    redactedJsonManifest)
            ]);
    }

    private P10CloseoutObservedExportSurface ReadExportSurface(
        JsonElement value)
    {
        var relative = value.GetProperty("download")
            .GetProperty("path").GetString() ?? string.Empty;
        var fullPath = Path.GetFullPath(Path.Combine(
            _paths.RunRoot,
            "browser",
            relative.Replace('/', Path.DirectorySeparatorChar)));
        return new P10CloseoutObservedExportSurface(
            value.GetProperty("label").GetString() ?? string.Empty,
            ReadStringMatrix(value.GetProperty("normalizedRows")),
            value.GetProperty("normalizedRowsSha256").GetString() ??
                string.Empty,
            value.GetProperty("permissionSnapshotSha256").GetString() ??
                string.Empty,
            value.GetProperty("download").GetProperty("suggestedFileName")
                .GetString() ?? string.Empty,
            fullPath,
            value.GetProperty("download").GetProperty("sha256")
                .GetString() ?? string.Empty,
            value.TryGetProperty("manifest", out var manifest)
                ? manifest.Clone()
                : default);
    }

    private static StatisticReconciliationEvidenceExport RequireExportMongo(
        IReadOnlyList<StatisticReconciliationEvidenceExport> values,
        P10CloseoutObservedExportSurface surface,
        string format,
        string detailLevel)
    {
        var matches = values.Where(value =>
                value.ContentSha256 == surface.DownloadSha256 &&
                value.Format == format &&
                value.DetailLevel == detailLevel)
            .ToArray();
        HarnessAssert.Equal(
            1,
            matches.Length,
            $"P10-CLOSE {surface.Label} exact Mongo export");
        return matches[0];
    }

    private static P10CloseoutMongoExportPin CloseoutMongoExportPin(
        StatisticReconciliationEvidenceExport value,
        P10CloseoutEvidenceManifestPin manifest)
        => new(
            value.Id,
            value.SchemaVersion,
            value.CommandId,
            value.WorkId,
            value.ScopeAssignmentId,
            value.ReconciliationId,
            value.Format,
            value.DetailLevel,
            value.GenerationId,
            value.GenerationSha256,
            value.SemanticVerdictSha256,
            value.ReviewSignatureSha256,
            value.FinalApprovalSha256,
            value.PermissionSnapshotSha256,
            value.CreatedByActorId,
            value.FileName,
            value.ContentType,
            value.ManifestSha256,
            value.ContentSha256,
            value.ContentLength,
            value.CreatedAtUtc,
            value.ExpiresAtUtc,
            value.DocumentSha256,
            manifest);

    private static P10CloseoutEvidenceManifestPin
        RequireCloseoutManifestBinding(
            JsonElement manifest,
            StatisticReconciliationEvidenceExport export,
            int expectedRowCount,
            string expectedRowManifestSha256,
            P10CloseoutExportSemanticOracle semantic)
    {
        var pin = ReadCloseoutEvidenceManifest(manifest);
        var createdAtUtc = export.CreatedAtUtc.ToString(
            "O", CultureInfo.InvariantCulture);
        var expiresAtUtc = export.ExpiresAtUtc.ToString(
            "O", CultureInfo.InvariantCulture);
        var expectedArtifactId = CloseoutEvidenceHash(
            "P10_EVIDENCE_ARTIFACT_ID_V1",
            export.WorkId,
            export.ScopeAssignmentId,
            export.ReconciliationId,
            export.CommandId,
            export.Format,
            export.DetailLevel,
            export.ManifestSha256,
            export.ContentSha256);
        var expectedDocumentSha256 = CloseoutEvidenceHash(
            "P10_EVIDENCE_DOCUMENT_V1",
            export.Id,
            export.CommandId,
            export.WorkId,
            export.ScopeAssignmentId,
            export.ReconciliationId,
            export.GenerationId,
            export.GenerationSha256,
            export.SemanticVerdictSha256,
            export.ReviewSignatureSha256,
            export.FinalApprovalSha256,
            export.PermissionSnapshotSha256,
            export.CreatedByActorId,
            export.Format,
            export.DetailLevel,
            export.FileName,
            export.ManifestSha256,
            export.ContentSha256,
            export.ContentLength,
            createdAtUtc,
            expiresAtUtc);
        HarnessAssert.True(
            export.SchemaVersion ==
                "P10_RECONCILIATION_EVIDENCE_EXPORT_V1" &&
            export.CreatedAtUtc.Kind == DateTimeKind.Utc &&
            export.ExpiresAtUtc.Kind == DateTimeKind.Utc &&
            export.ExpiresAtUtc > export.CreatedAtUtc &&
            export.Id == expectedArtifactId &&
            export.DocumentSha256 == expectedDocumentSha256 &&
            pin.SchemaVersion == "P10_EVIDENCE_MANIFEST_V1" &&
            pin.CommandId == export.CommandId &&
            pin.WorkId == export.WorkId &&
            pin.ScopeAssignmentId == export.ScopeAssignmentId &&
            pin.ReconciliationId == export.ReconciliationId &&
            pin.GenerationId == export.GenerationId &&
            pin.GenerationSha256 == export.GenerationSha256 &&
            pin.SemanticVerdictSha256 == export.SemanticVerdictSha256 &&
            pin.ReviewSignatureSha256 == export.ReviewSignatureSha256 &&
            pin.FinalApprovalSha256 == export.FinalApprovalSha256 &&
            pin.PermissionSnapshotSha256 == export.PermissionSnapshotSha256 &&
            pin.Format == export.Format &&
            pin.DetailLevel == export.DetailLevel &&
            pin.RowCount == expectedRowCount &&
            pin.RowManifestSha256 == expectedRowManifestSha256 &&
            CloseoutManifestUtcEquals(
                pin.SnapshotAtUtc,
                semantic.VerdictCreatedAtUtc) &&
            CloseoutManifestUtcEquals(pin.CreatedAtUtc, export.CreatedAtUtc) &&
            CloseoutManifestUtcEquals(pin.ExpiresAtUtc, export.ExpiresAtUtc) &&
            pin.GenerationId == semantic.VerdictGenerationId &&
            pin.GenerationSha256 == semantic.VerdictGenerationSha256 &&
            pin.SemanticVerdictSha256 == semantic.SemanticVerdictSha256 &&
            pin.ReviewSignatureSha256 == semantic.ReviewSignatureSha256 &&
            pin.FinalApprovalSha256 == semantic.FinalApprovalSha256 &&
            HashBytes(Encoding.UTF8.GetBytes(export.ManifestJson)) ==
                export.ManifestSha256 &&
            HashBytes(export.Content) == export.ContentSha256 &&
            export.Content.LongLength == export.ContentLength,
            "P10-CLOSE artifact/document/manifest/export/Mongo semantic binding");
        return pin;
    }

    private static bool CloseoutManifestUtcEquals(
        string raw,
        DateTime expected)
        => DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) &&
            parsed.Kind == DateTimeKind.Utc &&
            expected.Kind == DateTimeKind.Utc &&
            parsed == expected;

    private static P10CloseoutEvidenceManifestPin
        ReadCloseoutStoredManifest(
            StatisticReconciliationEvidenceExport export)
    {
        using var document = JsonDocument.Parse(export.ManifestJson);
        return ReadCloseoutEvidenceManifest(document.RootElement);
    }

    private static P10CloseoutEvidenceManifestPin
        ReadCloseoutEvidenceManifest(JsonElement value)
        => new(
            value.GetProperty("schemaVersion").GetString() ?? string.Empty,
            value.GetProperty("commandId").GetString() ?? string.Empty,
            value.GetProperty("workId").GetString() ?? string.Empty,
            value.GetProperty("scopeAssignmentId").GetString() ?? string.Empty,
            value.GetProperty("reconciliationId").GetString() ?? string.Empty,
            value.GetProperty("generationId").GetString() ?? string.Empty,
            value.GetProperty("generationSha256").GetString() ?? string.Empty,
            value.GetProperty("semanticVerdictSha256").GetString() ??
                string.Empty,
            value.GetProperty("reviewSignatureSha256").GetString() ??
                string.Empty,
            value.GetProperty("finalApprovalSha256").GetString() ??
                string.Empty,
            value.GetProperty("permissionSnapshotSha256").GetString() ??
                string.Empty,
            value.GetProperty("format").GetString() ?? string.Empty,
            value.GetProperty("detailLevel").GetString() ?? string.Empty,
            value.GetProperty("rowCount").GetInt32(),
            value.GetProperty("rowManifestSha256").GetString() ??
                string.Empty,
            value.GetProperty("snapshotAtUtc").GetString() ?? string.Empty,
            value.GetProperty("createdAtUtc").GetString() ?? string.Empty,
            value.GetProperty("expiresAtUtc").GetString() ?? string.Empty);

    private static P10CloseoutExportSemanticOracle
        BuildCloseoutExportSemanticOracle(
            P10CloseoutPresentationMongoInput input,
            string operatorPermission,
            string redactedPermission,
            IReadOnlyList<P10CloseoutMongoReviewPin> reviewLineage)
    {
        var verdict = input.Verdict ?? throw new InvalidOperationException(
            "P10-CLOSE export current verdict is unavailable.");
        var expected = input.ObservationInputs.Where(value =>
                value.GenerationId == verdict.ExpectedGenerationId &&
                value.RecordKind ==
                    StatisticReconciliationObservationRecordKinds.ExpectedAtom &&
                value.IdentitySha256 is not null)
            .ToArray();
        var actual = input.ObservationInputs.Where(value =>
                value.GenerationId == verdict.ActualGenerationId &&
                value.RecordKind ==
                    StatisticReconciliationObservationRecordKinds.ActualAtom &&
                value.IdentitySha256 is not null)
            .ToArray();
        var sourceIds = input.ObservationInputs.Where(value =>
                value.GenerationId == verdict.ActualGenerationId &&
                value.RecordKind ==
                    StatisticReconciliationObservationRecordKinds
                        .ActualSourceDecision &&
                value.SourceIncluded == true &&
                value.SourceStableIdentitySha256 is not null)
            .Select(value => value.SourceStableIdentitySha256!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var reviewSignature = CloseoutEvidenceHashSequence(
            "P10_EVIDENCE_REVIEW_SIGNATURE_V1",
            reviewLineage.OrderBy(value => value.DecisionId,
                    StringComparer.Ordinal)
                .Select(value => value.DocumentSha256));
        var superseded = reviewLineage.Where(value => value.RecordKind ==
                StatisticReconciliationReviewRecordKinds.Supersession &&
                value.SupersedesDecisionId is not null)
            .Select(value => value.SupersedesDecisionId!)
            .ToHashSet(StringComparer.Ordinal);
        var active = reviewLineage.Where(value =>
                value.RecordKind ==
                    StatisticReconciliationReviewRecordKinds.Decision &&
                value.GenerationId == verdict.VerdictGenerationId &&
                !superseded.Contains(value.DecisionId))
            .ToArray();
        var approvals = active.Where(value =>
                value.Decision ==
                    StatisticReconciliationReviewDecisions.Approve &&
                value.SemanticVerdictSha256 ==
                    verdict.SemanticVerdictSha256 &&
                value.GenerationSha256 == verdict.VerdictGenerationSha256)
            .ToArray();
        var coherentSnapshot = verdict.Signable &&
            verdict.CloseoutAllowed && verdict.AllRequiredLayersZero;
        var unknownRootCause = verdict.UnknownBlocksCloseout ||
            verdict.RootCauseClass ==
                StatisticReconciliationRootCauseClasses.Unknown;
        var approved = verdict.Verdict == "MATCHED" &&
            verdict.CompleteEvidence && coherentSnapshot &&
            !unknownRootCause &&
            approvals.Select(value => value.Gate)
                .Distinct(StringComparer.Ordinal).Count() == 5 &&
            CloseoutReviewGates.All(gate => approvals.Count(value =>
                value.Gate == gate) == 1) &&
            approvals.Select(value => value.ActorId)
                .Distinct(StringComparer.Ordinal).Count() == 5;
        var finalApproval = CloseoutPresentationHash(
            "P10_REVIEW_FINAL_APPROVAL_V1",
            input.ReconciliationId,
            verdict.VerdictGenerationId,
            verdict.VerdictGenerationSha256,
            verdict.SemanticVerdictSha256,
            approved,
            string.Concat(approvals.OrderBy(value => value.Gate,
                    StringComparer.Ordinal)
                .Select(value => value.DocumentSha256)));
        var operatorRows = BuildCloseoutExportRows(
            expected,
            actual,
            sourceIds,
            verdict.FreshnessAssessmentSha256 ?? "NONE",
            operatorPermission,
            verdict.Verdict);
        var redactedRows = BuildCloseoutExportRows(
            expected,
            actual,
            [],
            verdict.FreshnessAssessmentSha256 ?? "NONE",
            redactedPermission,
            verdict.Verdict);
        return new P10CloseoutExportSemanticOracle(
            input.ReconciliationId,
            verdict.VerdictGenerationId,
            verdict.VerdictGenerationSha256,
            verdict.SemanticVerdictSha256,
            verdict.FreshnessAssessmentSha256 ?? "NONE",
            verdict.Verdict,
            verdict.CreatedAtUtc,
            verdict.CompleteEvidence,
            coherentSnapshot,
            unknownRootCause,
            sourceIds,
            reviewLineage,
            reviewSignature,
            approved,
            approvals.Length,
            finalApproval,
            operatorRows.Select(CloseoutExportMatrixRow).ToArray(),
            redactedRows.Select(CloseoutExportMatrixRow).ToArray(),
            CloseoutExportRowManifestSha256(operatorRows),
            CloseoutExportRowManifestSha256(redactedRows));
    }

    private static P10CloseoutExportCanonicalRow[] BuildCloseoutExportRows(
        IReadOnlyList<P10CloseoutPresentationObservationInput> expected,
        IReadOnlyList<P10CloseoutPresentationObservationInput> actual,
        IReadOnlyList<string> sourceIds,
        string freshness,
        string permission,
        string verdict)
    {
        var expectedGroups = expected.GroupBy(
                CloseoutExportKey,
                StringComparer.Ordinal)
            .ToDictionary(value => value.Key, CloseoutExportGroupCell,
                StringComparer.Ordinal);
        var actualGroups = actual.GroupBy(
                CloseoutExportKey,
                StringComparer.Ordinal)
            .ToDictionary(value => value.Key, CloseoutExportGroupCell,
                StringComparer.Ordinal);
        return expectedGroups.Keys.Concat(actualGroups.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(key =>
            {
                var expectedCell = expectedGroups.GetValueOrDefault(key) ??
                    new P10CloseoutExportCell("NONE", "MISSING", "null");
                var actualCell = actualGroups.GetValueOrDefault(key) ??
                    new P10CloseoutExportCell("NONE", "MISSING", "null");
                var match = expectedCell == actualCell;
                var delta = new P10CloseoutExportCell(
                    "DELTA",
                    "VALUE",
                    JsonSerializer.Serialize(match
                        ? "MATCH"
                        : expectedCell.ValueState == "MISSING"
                            ? "EXTRA"
                            : actualCell.ValueState == "MISSING"
                                ? "MISSING"
                                : "DIFFERENT"));
                var atom = expected.Concat(actual).First(value =>
                    CloseoutExportKey(value) == key);
                return new P10CloseoutExportCanonicalRow(
                    atom.IdentitySha256!,
                    $"{atom.Family}/{atom.Kind}/{atom.MetricId}/" +
                    atom.AtomKind,
                    expectedCell,
                    actualCell,
                    delta,
                    freshness,
                    permission,
                    verdict,
                    sourceIds);
            })
            .OrderBy(value => value.Identity, StringComparer.Ordinal)
            .ThenBy(value => value.Config, StringComparer.Ordinal)
            .ThenBy(value => value.Expected.CanonicalJson,
                StringComparer.Ordinal)
            .ThenBy(value => value.Actual.CanonicalJson,
                StringComparer.Ordinal)
            .ToArray();
    }

    private static string CloseoutExportKey(
        P10CloseoutPresentationObservationInput value)
        => string.Join('|',
            value.IdentitySha256,
            value.AtomKind,
            value.TransitionLeg ?? "NONE",
            value.TransitionKind ?? "NONE");

    private static P10CloseoutExportCell CloseoutExportGroupCell(
        IGrouping<string, P10CloseoutPresentationObservationInput> values)
    {
        var ordered = values.OrderBy(value => value.ValueState,
                StringComparer.Ordinal)
            .ThenBy(value => value.CanonicalValue, StringComparer.Ordinal)
            .ThenBy(value => value.DecimalScale)
            .ThenBy(value => value.OccurrenceCount)
            .Select(value => new P10CloseoutExportGroupValue(
                value.ValueState!,
                value.CanonicalValue!,
                value.DecimalScale!.Value,
                value.OccurrenceCount!.Value))
            .ToArray();
        var first = values.First();
        return new P10CloseoutExportCell(
            first.ValueType!,
            ordered.Length == 1 ? ordered[0].ValueState : "MULTI",
            JsonSerializer.Serialize(ordered));
    }

    private static IReadOnlyList<string> CloseoutExportMatrixRow(
        P10CloseoutExportCanonicalRow value)
        =>
        [
            value.Identity,
            value.Config,
            value.Expected.CanonicalJson,
            value.Actual.CanonicalJson,
            value.Delta.CanonicalJson,
            value.Freshness,
            value.Permission,
            value.Verdict
        ];

    private static string CloseoutExportRowManifestSha256(
        IReadOnlyList<P10CloseoutExportCanonicalRow> rows)
        => CloseoutEvidenceHashSequence(
            "P10_EVIDENCE_ROW_MANIFEST_V1",
            rows.Select(CloseoutExportRowSemantic));

    private static string CloseoutExportRowSemantic(
        P10CloseoutExportCanonicalRow row)
        => CloseoutEvidenceHash(
            "P10_EVIDENCE_ROW_V1",
            row.Identity,
            row.Config,
            CloseoutExportCellSemantic(row.Expected),
            CloseoutExportCellSemantic(row.Actual),
            CloseoutExportCellSemantic(row.Delta),
            row.Freshness,
            row.Permission,
            row.Verdict,
            CloseoutEvidenceHashSequence(
                "P10_EVIDENCE_ROW_SOURCE_SET_V1",
                row.SourceStableIds));

    private static string CloseoutExportCellSemantic(
        P10CloseoutExportCell value)
        => CloseoutEvidenceHash(
            "P10_EVIDENCE_CELL_V1",
            value.ValueType,
            value.ValueState,
            value.CanonicalJson);

    private static string CloseoutEvidenceHash(
        string domain,
        params object?[] values)
        => HashBytes(Encoding.UTF8.GetBytes(string.Join("\n",
            new[] { domain }.Concat(values.Select(value =>
                Convert.ToString(value, CultureInfo.InvariantCulture) ??
                "<NULL>")))));

    private static string CloseoutEvidenceHashSequence(
        string domain,
        IEnumerable<string> values)
    {
        var material = values.ToArray();
        return CloseoutEvidenceHash(
            domain,
            material.Length,
            string.Join("\n", material.Select((value, index) =>
                $"{index}:{value}")));
    }
    private P10CloseoutNetworkEvidence ReadCloseoutNetworkEvidence(
        P10CloseoutRawBrowserCase rawCase)
    {
        RequireCloseoutOwnedArtifact(rawCase.NetworkPath);
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(rawCase.NetworkPath));
        var root = document.RootElement;
        HarnessAssert.True(
            root.GetProperty("schemaVersion").GetString() ==
                "P10_CLOSE_BROWSER_NETWORK_V1" &&
            root.GetProperty("caseId").GetString() == rawCase.CaseId &&
            root.GetProperty("networkMockCount").GetInt32() == 0 &&
            root.GetProperty("routeInterceptionRegistrationCount")
                .GetInt32() == 0 &&
            root.GetProperty("proxyReconciled").GetBoolean(),
            "P10-CLOSE review network evidence identity");
        P10CloseoutNetworkRow[] Rows(JsonElement value)
            => value.EnumerateArray()
                .Select(row => new P10CloseoutNetworkRow(
                    row.GetProperty("method").GetString() ?? string.Empty,
                    row.GetProperty("path").GetString() ?? string.Empty,
                    row.GetProperty("status").ValueKind ==
                        JsonValueKind.Number
                        ? row.GetProperty("status").GetInt32()
                        : -1))
                .ToArray();
        return new P10CloseoutNetworkEvidence(
            Rows(root.GetProperty("browser")),
            Rows(root.GetProperty("proxy")),
            true);
    }

    private static P10CloseoutObservedDenial ReadCloseoutDenial(
        string kind,
        JsonElement value)
    {
        var ui = value.TryGetProperty("uiLiveRegion", out var uiValue) &&
            uiValue.ValueKind == JsonValueKind.Object
                ? uiValue
                : default;
        string? NullableString(string name)
            => value.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String
                    ? property.GetString()
                    : null;
        long? NullableInt64(string name)
            => value.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.Number
                    ? property.GetInt64()
                    : null;
        int? NullableInt32(string name)
            => value.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.Number
                    ? property.GetInt32()
                    : null;
        return new P10CloseoutObservedDenial(
            kind,
            value.GetProperty("commandId").GetString() ?? string.Empty,
            value.GetProperty("actorId").GetString() ?? string.Empty,
            value.GetProperty("method").GetString() ?? string.Empty,
            value.GetProperty("path").GetString() ?? string.Empty,
            value.GetProperty("status").GetInt32(),
            NullableString("errorCode"),
            NullableString("problemTitle"),
            value.GetProperty("bodyBytes").GetInt32(),
            NullableInt64("expectedStateRevision"),
            NullableInt64("stateRevisionBefore"),
            NullableInt64("stateRevisionAfter"),
            NullableInt32("authorizedSummaryStatus"),
            ui.ValueKind == JsonValueKind.Object
                ? ui.GetProperty("role").GetString() ?? string.Empty
                : string.Empty,
            ui.ValueKind == JsonValueKind.Object
                ? ui.GetProperty("ariaLive").GetString() ?? string.Empty
                : string.Empty,
            ui.ValueKind == JsonValueKind.Object
                ? ui.GetProperty("text").GetString() ?? string.Empty
                : string.Empty,
            ui.ValueKind == JsonValueKind.Object
                ? ui.GetProperty("textSha256").GetString() ?? string.Empty
                : string.Empty,
            ui.ValueKind == JsonValueKind.Object
                ? ui.GetProperty("retryButtonCount").GetInt32()
                : 0,
            ui.ValueKind == JsonValueKind.Object
                ? ui.GetProperty("articleCount").GetInt32()
                : 0);
    }

    private static bool CloseoutSourceEquals(
        P10CloseoutProductionStateRow expected,
        P10CloseoutMongoStateRow actual)
        => expected.SourceReportId == actual.SourceReportId &&
            expected.SourcePayloadRevision == actual.SourcePayloadRevision &&
            expected.SourcePayloadHash == actual.SourcePayloadHash &&
            expected.SourceLifecycleRevision ==
                actual.SourceLifecycleRevision &&
            expected.SourceLifecycleHash == actual.SourceLifecycleHash;

    private static P10CloseoutObservedStateRow[] ReadObservedStateRows(
        JsonElement value)
        => value.EnumerateArray()
            .Select(row => new P10CloseoutObservedStateRow(
                row.GetProperty("reconciliationId").GetString() ??
                    string.Empty,
                row.GetProperty("status").GetString() ?? string.Empty,
                row.GetProperty("cells").EnumerateArray()
                    .Select(cell => cell.GetString() ?? string.Empty)
                    .ToArray()))
            .OrderBy(row => row.ReconciliationId, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<IReadOnlyList<string>> ReadStringMatrix(
        JsonElement value)
        => value.EnumerateArray()
            .Select(row => (IReadOnlyList<string>)row.EnumerateArray()
                .Select(cell => cell.GetString() ?? string.Empty)
                .ToArray())
            .ToArray();

    private static SortedDictionary<string, string> ReadScalarObject(
        JsonElement value)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                _ => property.Value.GetRawText()
            };
        }
        return result;
    }

    private static string CloseoutIdentityStatusSha256(
        IEnumerable<P10CloseoutObservedStateRow> rows)
        => CloseoutMatrixSha256(rows
            .OrderBy(value => value.ReconciliationId, StringComparer.Ordinal)
            .Select(value => (IReadOnlyList<string>)
            [
                value.ReconciliationId,
                value.Status
            ]));

    private static string CloseoutKeyValueSha256(
        IReadOnlyDictionary<string, string> values)
        => CloseoutMatrixSha256(values
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => (IReadOnlyList<string>)
            [
                value.Key,
                value.Value
            ]));

    private static string CloseoutMatrixSha256(
        IEnumerable<IReadOnlyList<string>> rows)
        => HashText(string.Join(
            "\n",
            rows.Select(row => string.Join("\u001f", row))));

    private static bool CloseoutRowsEqual(
        IReadOnlyList<IReadOnlyList<string>> left,
        IReadOnlyList<IReadOnlyList<string>> right)
        => left.Count == right.Count && left.Select((row, index) =>
                row.SequenceEqual(right[index], StringComparer.Ordinal))
            .All(value => value);

    private static bool CloseoutRowsSemanticallyEqual(
        IReadOnlyList<IReadOnlyList<string>> left,
        IReadOnlyList<IReadOnlyList<string>> right)
        => left.Count == right.Count && left.Select((row, rowIndex) =>
                row.Count == 8 && right[rowIndex].Count == 8 &&
                row.Select((cell, columnIndex) =>
                        columnIndex is 2 or 3 or 4
                            ? CloseoutJsonCellEqual(
                                cell,
                                right[rowIndex][columnIndex])
                            : string.Equals(
                                cell,
                                right[rowIndex][columnIndex],
                                StringComparison.Ordinal))
                    .All(value => value))
            .All(value => value);

    private static bool CloseoutJsonCellEqual(string left, string right)
    {
        try
        {
            return JsonNode.DeepEquals(
                JsonNode.Parse(left),
                JsonNode.Parse(right));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool CloseoutRowsEqualExceptPermission(
        IReadOnlyList<IReadOnlyList<string>> left,
        IReadOnlyList<IReadOnlyList<string>> right)
        => left.Count == right.Count && left.Select((row, index) =>
                row.Count == 8 && right[index].Count == 8 &&
                row.Where((_, cell) => cell != 6).SequenceEqual(
                    right[index].Where((_, cell) => cell != 6),
                    StringComparer.Ordinal))
            .All(value => value);
}

internal sealed record P10CloseoutRawBrowserCase(
    string CaseId,
    string Status,
    bool AccessibilityPassed,
    bool SecurityPassed,
    int NetworkMockCount,
    string ResultPath,
    string TracePath,
    string NetworkPath,
    string ScreenshotPath,
    string DownloadPath);

internal sealed record P10CloseoutObservedStateRow(
    string ReconciliationId,
    string Status,
    IReadOnlyList<string> Cells);

internal sealed record P10CloseoutPresentationLink(
    string Rel,
    string Href,
    string Method);

internal sealed record P10CloseoutPresentationMetadata(
    long Total,
    string RootCause,
    int RowCountBeforeRedaction,
    int RowCountAfterRedaction,
    IReadOnlyList<string> PermissionCodes,
    IReadOnlyList<P10CloseoutPresentationLink> EvidenceLinks,
    IReadOnlyList<P10CloseoutPresentationLink> SourceLinks);

internal sealed record P10CloseoutDisplayedPresentationMetadata(
    long Total,
    string RootCause,
    int RowCountBeforeRedaction,
    int RowCountAfterRedaction);

internal sealed record P10CloseoutApiPresentationRow(
    string Id,
    string Status,
    string SchemaVersion,
    string DetailLevel,
    IReadOnlyDictionary<string, string> Columns,
    P10CloseoutPresentationMetadata Metadata);

internal sealed record P10CloseoutDomPresentationRow(
    string Id,
    string SchemaVersion,
    string DetailLevel,
    IReadOnlyDictionary<string, string> Columns,
    P10CloseoutDisplayedPresentationMetadata DisplayedMetadata);

internal sealed record P10CloseoutDetailPresentation(
    P10CloseoutApiPresentationRow ApiRow,
    P10CloseoutDomPresentationRow DomRow,
    string TotalText,
    string RootCauseText,
    string PermissionText,
    IReadOnlyList<P10CloseoutPresentationLink> EvidenceLinks,
    IReadOnlyList<P10CloseoutPresentationLink> SourceLinks,
    int DetailRequestCount,
    int DetailSuccessCount);

internal sealed record P10CloseoutPresentationPermissionOracle(
    string ActorKey,
    string ActorId,
    string UnitId,
    IReadOnlyList<string> ActorRoles,
    string? AccountKind,
    bool IsDeleted,
    string WorkId,
    string ScopeAssignmentId,
    IReadOnlyList<string> PermissionCodes,
    string PermissionSnapshotSha256,
    int RowCountBeforeRedaction,
    int RowCountAfterRedaction,
    string DetailLevel);

internal sealed record P10CloseoutPresentationVerdictInput(
    string VerdictGenerationId,
    string VerdictGenerationSha256,
    string SemanticVerdictSha256,
    string ExpectedGenerationId,
    string ExpectedGenerationSha256,
    string ActualGenerationId,
    string ActualGenerationSha256,
    string DeltaManifestSha256,
    string? FreshnessAssessmentSha256,
    string Verdict,
    string? RootCauseClass,
    DateTime CreatedAtUtc,
    bool CompleteEvidence,
    bool Signable,
    bool CloseoutAllowed,
    bool AllRequiredLayersZero,
    bool UnknownBlocksCloseout);

internal sealed record P10CloseoutPresentationObservationInput(
    string Id,
    string ReconciliationId,
    string GenerationId,
    string RecordKind,
    string? IdentitySha256,
    string? Family,
    string? Kind,
    string? MetricId,
    string? AtomKind,
    string? ValueType,
    string? ValueState,
    string? CanonicalValue,
    int? DecimalScale,
    long? OccurrenceCount,
    string? TransitionLeg,
    string? TransitionKind,
    bool? SourceIncluded,
    string? SourceStableIdentitySha256);

internal sealed record P10CloseoutPresentationMongoInput(
    string ReconciliationId,
    string WorkId,
    string ScopeAssignmentId,
    string? DiagnosticCode,
    string ImmutableIdentityHash,
    string ImmutableHeaderHash,
    string P8ConfigBundleHash,
    string? ActualConfigurationBundleSha256,
    string CandidateCatalogSemanticSha256,
    string CandidateSchemaSemanticSha256,
    string? CurrentGenerationId,
    string? CurrentGenerationHash,
    string Status,
    DateTime UpdatedAtUtc,
    P10CloseoutPresentationVerdictInput? Verdict,
    IReadOnlyList<P10CloseoutPresentationObservationInput> ObservationInputs,
    long Total);

internal sealed record P10CloseoutMongoPresentationResult(
    IReadOnlyList<P10CloseoutApiPresentationRow> Rows,
    P10CloseoutPresentationPermissionOracle PermissionOracle,
    IReadOnlyList<P10CloseoutPresentationMongoInput> Inputs);

internal sealed record P10CloseoutMongoStateRow(
    string ReconciliationId,
    string Status,
    long StateRevision,
    long ReviewDecisionRevision,
    string StateHash,
    string SourceReportId,
    long SourcePayloadRevision,
    string SourcePayloadHash,
    long SourceLifecycleRevision,
    string SourceLifecycleHash);

internal sealed record P10CloseoutTargetStateTransition(
    string ReconciliationId,
    string InitialStatus,
    string FinalStatus,
    long InitialStateRevision,
    long FinalStateRevision,
    long InitialReviewDecisionRevision,
    long FinalReviewDecisionRevision,
    string InitialStateHash,
    string FinalStateHash,
    bool SourceStable,
    bool FinalIntegrityPassed,
    int ReviewRowCount,
    bool ReviewRevisionSequenceExact,
    string ReviewSequenceSha256);

internal sealed record P10CloseoutStateParity(
    bool Passed,
    IReadOnlyList<string> ExpectedStatuses,
    bool EmptyDom,
    int EmptyApiRowCount,
    long EmptyMongoCount,
    IReadOnlyList<P10CloseoutDomPresentationRow> DomRows,
    IReadOnlyList<P10CloseoutApiPresentationRow> ApiRows,
    IReadOnlyList<P10CloseoutApiPresentationRow> MongoPresentationRows,
    IReadOnlyList<P10CloseoutMongoStateRow> MongoRows,
    P10CloseoutDetailPresentation RedactedDetail,
    P10CloseoutPresentationPermissionOracle PermissionOracle,
    IReadOnlyList<P10CloseoutPresentationMongoInput> MongoInputs,
    bool NonTargetRowsSealed,
    P10CloseoutTargetStateTransition TargetTransition,
    string DomRowsSha256,
    string ApiRowsSha256,
    string ApiDisplayedRowsSha256,
    string MongoPresentationRowsSha256,
    string DomIdentitySha256,
    string ApiIdentityStatusSha256,
    string MongoIdentityStatusSha256);

internal sealed record P10CloseoutSourceParity(
    bool Passed,
    string ReconciliationId,
    IReadOnlyDictionary<string, string> DomValues,
    IReadOnlyDictionary<string, string> ApiValues,
    IReadOnlyDictionary<string, string> MongoValues,
    string DomValuesSha256,
    string ApiValuesSha256,
    string MongoValuesSha256);

internal sealed record P10CloseoutObservedReview(
    string Gate,
    string ActorId,
    string CommandId,
    int HttpStatus,
    long StateRevisionBefore,
    long ExpectedStateRevision,
    long StateRevisionAfter,
    string SubmissionMode,
    string UiControlName,
    string UiStatusText,
    string UiStatusTextSha256,
    string ApiDecisionId,
    string ApiPermissionSnapshotSha256,
    string ApiDocumentSha256);

internal sealed record P10CloseoutReviewUiTrace(
    int Sequence,
    string Action,
    string Gate,
    string ActorId,
    string CommandId,
    string Method,
    string Path,
    string Selector,
    string AccessibleName,
    long StateRevisionBefore,
    long ExpectedStateRevision,
    int ResponseStatus,
    long StateRevisionAfter,
    string UiStatusTextSha256);

internal sealed record P10CloseoutMongoReviewPin(
    string Id,
    string RecordKind,
    string Status,
    string OperationCommandId,
    string RequestSha256,
    string ReconciliationId,
    string GenerationId,
    string GenerationSha256,
    string SemanticVerdictSha256,
    string ReviewRecordSha256,
    string Gate,
    string? Decision,
    string ReviewerActorId,
    string PermissionSnapshotSha256,
    int RowCountBeforeRedaction,
    int RowCountAfterRedaction,
    long StateRevision,
    string? SupersedesDecisionId,
    string? SupersededByGenerationId,
    string DocumentSha256,
    DateTime CreatedAtUtc)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DecisionId => Id;

    [System.Text.Json.Serialization.JsonIgnore]
    public string CommandId => OperationCommandId;

    [System.Text.Json.Serialization.JsonIgnore]
    public string ActorId => ReviewerActorId;
}

internal sealed record P10CloseoutNetworkRow(
    string Method,
    string Path,
    int HttpStatus);

internal sealed record P10CloseoutNetworkEvidence(
    IReadOnlyList<P10CloseoutNetworkRow> BrowserRows,
    IReadOnlyList<P10CloseoutNetworkRow> ProxyRows,
    bool ProxyReconciled);

internal sealed record P10CloseoutObservedDenial(
    string Kind,
    string CommandId,
    string ActorId,
    string Method,
    string Path,
    int HttpStatus,
    string? ErrorCode,
    string? ProblemTitle,
    int BodyBytes,
    long? ExpectedStateRevision,
    long? StateRevisionBefore,
    long? StateRevisionAfter,
    int? AuthorizedSummaryStatus,
    string UiRole,
    string UiAriaLive,
    string UiText,
    string UiTextSha256,
    int UiRetryButtonCount,
    int UiArticleCount);

internal sealed record P10CloseoutDenialParity(
    P10CloseoutObservedDenial StaleCas,
    P10CloseoutObservedDenial SeparationOfDuties,
    P10CloseoutObservedDenial OutsiderDetail,
    P10CloseoutObservedDenial OutsiderExport,
    bool DenialNetworkBound,
    bool SuccessNetworkBound,
    int DenialReviewRowCount,
    long DenialExportRowCount,
    string SeparationOfDutiesActorGuardField,
    long FinalStateRevision,
    long FinalReviewDecisionRevision,
    string FinalStateHash,
    bool FinalStateIntegrityPassed);

internal sealed record P10CloseoutReviewParity(
    bool Passed,
    IReadOnlyList<P10CloseoutObservedReview> BrowserRows,
    IReadOnlyList<P10CloseoutReviewUiTrace> UiTraceRows,
    IReadOnlyList<P10CloseoutMongoReviewPin> MongoRows,
    IReadOnlyList<P10CloseoutMongoReviewPin> LineageRows,
    string BrowserRowsSha256,
    string UiTraceRowsSha256,
    string MongoRowsSha256,
    string LineageRowsSha256,
    int ReviewerDetailRequestCount,
    int ExecutorDetailRequestCount,
    P10CloseoutDenialParity Denials);

internal sealed record P10CloseoutObservedExportSurface(
    string Label,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string ClaimedRowsSha256,
    string PermissionSnapshotSha256,
    string FileName,
    string DownloadPath,
    string DownloadSha256,
    JsonElement Manifest);

internal sealed record P10CloseoutEvidenceManifestPin(
    string SchemaVersion,
    string CommandId,
    string WorkId,
    string ScopeAssignmentId,
    string ReconciliationId,
    string GenerationId,
    string GenerationSha256,
    string SemanticVerdictSha256,
    string ReviewSignatureSha256,
    string FinalApprovalSha256,
    string PermissionSnapshotSha256,
    string Format,
    string DetailLevel,
    int RowCount,
    string RowManifestSha256,
    string SnapshotAtUtc,
    string CreatedAtUtc,
    string ExpiresAtUtc);

internal sealed record P10CloseoutMongoExportPin(
    string ExportId,
    string SchemaVersion,
    string CommandId,
    string WorkId,
    string ScopeAssignmentId,
    string ReconciliationId,
    string Format,
    string DetailLevel,
    string GenerationId,
    string GenerationSha256,
    string SemanticVerdictSha256,
    string ReviewSignatureSha256,
    string FinalApprovalSha256,
    string PermissionSnapshotSha256,
    string CreatedByActorId,
    string FileName,
    string ContentType,
    string ManifestSha256,
    string ContentSha256,
    long ContentLength,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    string DocumentSha256,
    P10CloseoutEvidenceManifestPin Manifest);

internal sealed record P10CloseoutExportCell(
    string ValueType,
    string ValueState,
    string CanonicalJson);

internal sealed record P10CloseoutExportGroupValue(
    string ValueState,
    string CanonicalValue,
    int DecimalScale,
    long OccurrenceCount);

internal sealed record P10CloseoutExportCanonicalRow(
    string Identity,
    string Config,
    P10CloseoutExportCell Expected,
    P10CloseoutExportCell Actual,
    P10CloseoutExportCell Delta,
    string Freshness,
    string Permission,
    string Verdict,
    IReadOnlyList<string> SourceStableIds);

internal sealed record P10CloseoutExportSemanticOracle(
    string ReconciliationId,
    string VerdictGenerationId,
    string VerdictGenerationSha256,
    string SemanticVerdictSha256,
    string Freshness,
    string Verdict,
    DateTime VerdictCreatedAtUtc,
    bool CompleteEvidence,
    bool CoherentSnapshot,
    bool UnknownRootCause,
    IReadOnlyList<string> SourceStableIds,
    IReadOnlyList<P10CloseoutMongoReviewPin> ReviewLineage,
    string ReviewSignatureSha256,
    bool FinalApproved,
    int FinalApprovedGateCount,
    string FinalApprovalSha256,
    IReadOnlyList<IReadOnlyList<string>> OperatorRows,
    IReadOnlyList<IReadOnlyList<string>> RedactedRows,
    string OperatorRowManifestSha256,
    string RedactedRowManifestSha256);

internal sealed record P10CloseoutExportParity(
    bool Passed,
    P10CloseoutDetailPresentation OperatorPresentation,
    P10CloseoutDetailPresentation RedactedPresentation,
    P10CloseoutExportSemanticOracle SemanticOracle,
    P10CloseoutApiPresentationRow OperatorMongoPresentation,
    P10CloseoutApiPresentationRow RedactedMongoPresentation,
    P10CloseoutPresentationPermissionOracle OperatorPermissionOracle,
    P10CloseoutPresentationPermissionOracle RedactedPermissionOracle,
    IReadOnlyList<P10CloseoutPresentationMongoInput> MongoPresentationInputs,
    IReadOnlyList<IReadOnlyList<string>> OperatorJsonRows,
    IReadOnlyList<IReadOnlyList<string>> OperatorCsvRows,
    IReadOnlyList<IReadOnlyList<string>> RedactedJsonRows,
    string OperatorJsonRowsSha256,
    string OperatorCsvRowsSha256,
    string RedactedJsonRowsSha256,
    bool OperatorJsonCsvAllColumnsEqual,
    bool OperatorRedactedNonPermissionEqual,
    bool ActorPermissionPinsDiffer,
    IReadOnlyList<P10CloseoutMongoExportPin> MongoExports);

internal sealed record P10CloseoutMeasuredParity(
    bool Passed,
    P10CloseoutStateParity State,
    P10CloseoutSourceParity Source,
    P10CloseoutReviewParity Review,
    P10CloseoutExportParity Export);
