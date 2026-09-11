using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Errors;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models.Enums;
using tdtd_be.Models.Statistics;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsReconciliation.ExpectedLedger;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.Recheck;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    public const string P11DirectReconciliationRegressionCommandLineSwitch =
        "--p11-direct-reconciliation-regression";

    private static readonly string[] P11DirectSummaryOwnerStores =
    [
        "work_assignment_basic_summary_snapshots",
        "work_assignment_advanced_summary_day_nodes",
        "work_assignment_advanced_summary_month_nodes",
        "work_assignment_advanced_summary_year_nodes",
        "work_report_statistic_diff_results"
    ];

    private bool _p11Attempt039RecoveryProbe;

    public static async Task<int> RunP11DirectReconciliationRegressionAsync(
        bool attempt039RecoveryProbe = false)
    {
        var runKey = attempt039RecoveryProbe
            ? $"p11a039_{DateTime.UtcNow:yyyyMMddHHmmss}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}"
            : $"p11_direct_reconciliation_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP10(
            runKey,
            ChainId,
            attempt039RecoveryProbe ? "R39" : "P11-DIRECT-RECONCILIATION-REGRESSION");
        return await new P10ReconciliationCoreProbe(paths, runKey)
            { _p11Attempt039RecoveryProbe = attempt039RecoveryProbe }
            .ExecuteP11DirectReconciliationRegressionAsync(
                CancellationToken.None);
    }

    private async Task<int> ExecuteP11DirectReconciliationRegressionAsync(
        CancellationToken ct)
    {
        var cleanupErrors = new List<string>();
        Exception? failure = null;
        P10ProductionDirectFixturePins? publication = null;
        StatisticReconciliationRun? completed = null;
        IReadOnlyDictionary<string, long>? summaryBefore = null;
        IReadOnlyDictionary<string, long>? summaryAfter = null;
        string? reconciliationId = null;
        string? exportId = null;
        string? mappingContributionPolicyJson = null;
        string? workerStageDiagnostic = null;
        string? verdictDiagnostic = null;
        string? recoveryP9GenerationId = null;
        string? recoveryRecheckMarkerId = null;
        var recoveryVerified = false;

        try
        {
            var jwtSigningKey = Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(48));
            RememberSecret(jwtSigningKey);
            _mongo = await MongoReplicaSetLease.StartP10Async(
                _paths,
                _iterationRoot,
                _runKey,
                1,
                ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(
                _paths,
                Path.Combine(_iterationRoot, "production-bootstrap"),
                _runKey,
                _mongo,
                ct,
                CloseoutBackendOptions(jwtSigningKey));
            _api = new ApiHarnessClient(_backend.BaseUri);

            await BootstrapAndSeedFixtureAsync(ct);
            await RestartCloseoutBackendWithActualApiOwnerAsync(
                jwtSigningKey,
                ct,
                enableReconciliationWorker: true);
            await AwaitInfrastructureAsync(ct);

            mappingContributionPolicyJson =
                BuildP11DirectFlowMappingContributionPolicy();
            publication = await PrepareProductionDirectLifecycleFixtureAsync(
                ct,
                useNativeNullPeriodPair: true,
                cumulativeContributionPolicyJson:
                    mappingContributionPolicyJson,
                includeSecondStatisticField: true);
            await RequireP11DirectFlowMappingContributionPolicyAsync(
                publication,
                mappingContributionPolicyJson,
                ct);
            HarnessAssert.True(
                publication.PeriodStartUtc is null &&
                publication.PeriodEndUtc is null,
                "P11 Direct-only regression must cover a null/null period pair.");

            var p9FieldRows = await RequireDatabase()
                .GetCollection<WorkReportFieldStatValue>(
                    "work_report_field_stat_values")
                .Find(value =>
                    value.DirectProjection != null &&
                    value.DirectProjection.RunId == publication.P9RunId &&
                    value.DirectProjection.GenerationId ==
                        publication.P9GenerationId &&
                    !value.IsDeleted)
                .SortBy(value => value.FieldId)
                .ToListAsync(ct);
            HarnessAssert.Equal(
                2,
                p9FieldRows.Count,
                "P11 multi-field P9 field-value row count");
            var p9FieldA = p9FieldRows.Single(value =>
                value.FieldId == P10ProductionDirectFieldId);
            HarnessAssert.True(
                p9FieldA.FieldKey == P10ProductionDirectFieldKey &&
                p9FieldA.NumericValue == 10m,
                "P11 multi-field P9 field A identity/value");
            var p9FieldB = p9FieldRows.Single(value =>
                value.FieldId == P10ProductionDirectSecondFieldId);
            HarnessAssert.True(
                p9FieldB.FieldKey == P10ProductionDirectSecondFieldKey &&
                p9FieldB.NumericValue == 20m,
                "P11 multi-field P9 field B identity/value");

            var p9FieldAggregates = await RequireDatabase()
                .GetCollection<WorkReportFieldStatAggregate>(
                    "work_report_field_stat_aggregates")
                .Find(value =>
                    value.DirectProjection != null &&
                    value.DirectProjection.RunId == publication.P9RunId &&
                    value.DirectProjection.GenerationId ==
                        publication.P9GenerationId &&
                    !value.IsDeleted)
                .ToListAsync(ct);
            HarnessAssert.Equal(
                6,
                p9FieldAggregates.Count,
                "P11 multi-field P9 field-aggregate row count");
            foreach (var fieldId in new[]
                     {
                         P10ProductionDirectFieldId,
                         P10ProductionDirectSecondFieldId
                     })
            {
                var scopes = p9FieldAggregates
                    .Where(value => value.FieldId == fieldId)
                    .Select(value => value.ScopeType)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                HarnessAssert.True(
                    scopes.SequenceEqual(
                        new[] { "ASSIGNMENT", "ROOT", "WORK" },
                        StringComparer.Ordinal),
                    $"P11 multi-field P9 aggregate scopes for {fieldId}");
            }

            summaryBefore = await CountP11DirectSummaryOwnersAsync(ct);
            HarnessAssert.True(
                summaryBefore.Values.All(value => value == 0),
                "P11 Direct-only fixture must start without BASIC/ADVANCED/DIFF owners.");

            var exportResponse = await RequireApi().PostAsync(
                "api/stat-runs/exports",
                new
                {
                    commandId = "p11-direct-regression-export-001",
                    format = "XLSX",
                    resultKind = "DIRECT_FIELD",
                    workId = publication.WorkId,
                    scopeType = "ASSIGNMENT",
                    scopeId = publication.WorkAssignmentId,
                    periodInstanceKey = publication.PeriodInstanceKey,
                    resultId = publication.P9RunId,
                    expectedResultHash = publication.P9GenerationHash,
                    expectedConfigHash = publication.ConfigHash,
                    expectedSourceHash = publication.SourcePayloadHash,
                    expectedLifecycleRevision =
                        publication.SourceLifecycleRevision,
                    filters = new
                    {
                        dynamicFormTemplateId =
                            publication.DynamicFormVersionId,
                        fieldId = P10ProductionDirectFieldId,
                        fieldKey = P10ProductionDirectFieldKey,
                        blockId = (string?)null,
                        metricKey = (string?)null,
                        labelCode = (string?)null,
                        periodKey = publication.PeriodKey,
                        bucketKey = (string?)null
                    }
                },
                Actor("executor").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                exportResponse,
                HttpStatusCode.Created,
                "P11 Direct-only regression export");
            var export = ApiHarnessClient.RequiredObject(
                exportResponse.Json,
                "P11 Direct-only regression export response");
            HarnessAssert.Equal(
                1,
                ApiHarnessClient.RequiredInt(export, "rowCount"),
                "P11 field A export row count");
            exportId = ApiHarnessClient.RequiredString(export, "exportId");
            var exportRoot = Path.GetFullPath(Path.Combine(
                CloseoutWorkspacePath("tdtd-be"),
                ".build",
                "stat-run-exports",
                RequireMongo().DatabaseName));
            TrackCloseoutProductionExportArtifactDirectory(exportRoot, exportId);

            var filter = new JsonObject
            {
                ["periodInstanceKey"] = publication.PeriodInstanceKey,
                ["fieldId"] = P10ProductionDirectFieldId,
                ["fieldKey"] = P10ProductionDirectFieldKey,
                ["bucketKey"] = null,
                ["periodKey"] = publication.PeriodKey
            };
            var basePath =
                $"api/works/{publication.WorkId}/statistics/{publication.WorkAssignmentId}/reconciliations";
            var preflightResponse = await RequireApi().PostAsync(
                $"{basePath}/capture-plan-preflight",
                new JsonObject
                {
                    ["p9ResultKind"] = "DIRECT",
                    ["p9ResultId"] = publication.P9ResultId,
                    ["p9RunId"] = publication.P9RunId,
                    ["conceptKey"] = P10ProductionDirectFieldKey,
                    ["grain"] = "MONTH",
                    ["filter"] = filter.DeepClone(),
                    ["exportId"] = exportId
                },
                Actor("executor").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                preflightResponse,
                HttpStatusCode.OK,
                "P11 Direct-only capture-plan preflight");
            var capturePlanToken = RequireResponseString(
                preflightResponse,
                "capturePlanToken");
            RememberSecret(capturePlanToken);
            var preflightPlanSha256 = RequireResponseString(
                preflightResponse,
                "planSha256");
            HarnessAssert.True(
                preflightPlanSha256.Length == 64,
                "P11 Direct-only preflight plan SHA-256.");

            var createBody = new JsonObject
            {
                ["commandId"] = "p11-direct-regression-create-001",
                ["p9ResultKind"] = "DIRECT",
                ["p9ResultId"] = publication.P9ResultId,
                ["p9RunId"] = publication.P9RunId,
                ["conceptKey"] = P10ProductionDirectFieldKey,
                ["grain"] = "MONTH",
                ["filter"] = filter.DeepClone(),
                ["capturePlanToken"] = capturePlanToken
            };
            var createResponse = await RequireApi().PostAsync(
                basePath,
                createBody,
                Actor("executor").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                createResponse,
                HttpStatusCode.Accepted,
                "P11 Direct-only reconciliation create");
            reconciliationId = RequireResponseString(
                createResponse,
                "reconciliationId");

            var replayResponse = await RequireApi().PostAsync(
                basePath,
                createBody,
                Actor("executor").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                replayResponse,
                HttpStatusCode.OK,
                "P11 Direct-only exact replay");
            HarnessAssert.Equal(
                reconciliationId,
                RequireResponseString(replayResponse, "reconciliationId"),
                "P11 Direct-only exact replay identity");

            completed = await WaitForP11DirectTerminalAsync(
                reconciliationId,
                TimeSpan.FromMinutes(3),
                ct);
            verdictDiagnostic = await ReadP11DirectVerdictDiagnosticAsync(
                reconciliationId, completed, ct);
            HarnessAssert.Equal(
                StatisticReconciliationRunStatuses.Matched,
                completed.Status,
                $"P11 Direct-only terminal status; diagnostic={completed.DiagnosticCode}");
            HarnessAssert.Equal(
                StatisticReconciliationActualCapturePlanVersions.V4,
                completed.ActualCapturePlan?.SchemaVersion,
                "P11 Direct-only capture plan schema");
            HarnessAssert.Equal(
                preflightPlanSha256,
                completed.ActualCapturePlanSha256,
                "P11 Direct-only preflight/create plan binding");
            HarnessAssert.Equal(
                StatisticReconciliationActualSummaryTargetDispositions.NotApplicable,
                completed.ActualCapturePlan?.Basic.Disposition,
                "P11 Direct-only BASIC disposition");
            HarnessAssert.Equal(
                StatisticReconciliationActualSummaryTargetDispositions.NotApplicable,
                completed.ActualCapturePlan?.Advanced.Disposition,
                "P11 Direct-only ADVANCED disposition");
            HarnessAssert.Equal(
                StatisticReconciliationActualSummaryTargetDispositions.NotApplicable,
                completed.ActualCapturePlan?.Diff.Disposition,
                "P11 Direct-only DIFF disposition");

            var finalVerdict = await RequireDatabase()
                .GetCollection<StatisticReconciliationReview>(
                    "work_report_statistic_reconciliation_reviews")
                .Find(value =>
                    value.ReconciliationId == reconciliationId &&
                    value.ActualGenerationId == completed.CurrentGenerationId &&
                    value.RecordKind ==
                        StatisticReconciliationReviewKinds.FinalVerdict)
                .SingleAsync(ct);
            HarnessAssert.True(
                finalVerdict.Verdict ==
                    StatisticReconciliationRunStatuses.Matched &&
                finalVerdict.CompleteEvidence &&
                finalVerdict.AllRequiredLayersZero &&
                !finalVerdict.MissingOrExtraIdentity &&
                !finalVerdict.UnknownBlocksCloseout &&
                finalVerdict.CloseoutAllowed &&
                finalVerdict.Signable,
                "P11 multi-field field-A final verdict must be MATCHED with zero drift.");

            var expectedPlans = await RequireDatabase()
                .GetCollection<StatisticReconciliationObservation>(
                    "work_report_statistic_reconciliation_observations")
                .Find(value =>
                    value.ReconciliationId == reconciliationId &&
                    value.GenerationId == finalVerdict.ExpectedGenerationId &&
                    value.RecordKind ==
                        StatisticReconciliationObservationRecordKinds
                            .ExpectedMetricPlan)
                .Limit(3)
                .ToListAsync(ct);
            HarnessAssert.Equal(
                2,
                expectedPlans.Count,
                "P11 full expected metric-plan count");
            var expectedMetricIdentities = expectedPlans
                .Select(value => value.MetricPlan ??
                    throw new InvalidOperationException(
                        "P11 expected metric plan payload is missing."))
                .Select(value =>
                    $"{value.Family}|{value.Kind}|{value.FieldId}|{value.MetricId}")
                .Order(StringComparer.Ordinal)
                .ToArray();
            HarnessAssert.True(
                expectedMetricIdentities.SequenceEqual(
                    new[]
                    {
                        $"{StatisticReconciliationExpectedMetricFamilies.Direct}|{StatisticReconciliationExpectedMetricKinds.Field}|{P10ProductionDirectFieldId}|{P10ProductionDirectFieldKey}",
                        $"{StatisticReconciliationExpectedMetricFamilies.Direct}|{StatisticReconciliationExpectedMetricKinds.Field}|{P10ProductionDirectSecondFieldId}|{P10ProductionDirectSecondFieldKey}"
                    }.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal),
                "P11 expected metric plans must retain fields A and B.");

            var expectedAtoms = await RequireDatabase()
                .GetCollection<StatisticReconciliationObservation>(
                    "work_report_statistic_reconciliation_observations")
                .Find(value =>
                    value.ReconciliationId == reconciliationId &&
                    value.GenerationId == finalVerdict.ExpectedGenerationId &&
                    value.RecordKind ==
                        StatisticReconciliationObservationRecordKinds
                            .ExpectedAtom)
                .ToListAsync(ct);
            var expectedDirectAtoms = expectedAtoms
                .Select(value => value.Atom ??
                    throw new InvalidOperationException(
                        "P11 expected atom payload is missing."))
                .ToImmutableArray();
            HarnessAssert.Equal(
                16,
                expectedDirectAtoms.Length,
                "P11 full expected direct ledger atom count");
            foreach (var expectedField in new[]
                     {
                         (FieldId: P10ProductionDirectFieldId,
                             MetricId: P10ProductionDirectFieldKey),
                         (FieldId: P10ProductionDirectSecondFieldId,
                             MetricId: P10ProductionDirectSecondFieldKey)
                     })
            {
                var fieldAtoms = expectedDirectAtoms
                    .Where(value =>
                        value.Family ==
                            StatisticReconciliationExpectedMetricFamilies.Direct &&
                        value.Kind ==
                            StatisticReconciliationExpectedMetricKinds.Field &&
                        value.FieldId == expectedField.FieldId &&
                        value.MetricId == expectedField.MetricId)
                    .ToArray();
                HarnessAssert.Equal(
                    8,
                    fieldAtoms.Length,
                    $"P11 full expected ledger atom count for {expectedField.FieldId}");
            }
            HarnessAssert.True(
                expectedDirectAtoms.All(value =>
                    P11IsComparableExpectedAtom(value) ||
                    P11ProvesOmittedZero(value)),
                "P11 expected direct ledger atoms must all be comparable or prove omitted zero.");
            var comparableExpectedDirectAtoms = expectedDirectAtoms
                .Where(P11IsComparableExpectedAtom)
                .ToImmutableArray();
            HarnessAssert.Equal(
                10,
                comparableExpectedDirectAtoms.Length,
                "P11 DIRECT/AGGREGATE expected comparison count");

            var projectedFieldAExpectedAtoms =
                StatisticReconciliationTrustedVerdictDeriver
                    .ProjectDirectFieldExpectedAtoms(
                        completed,
                        expectedDirectAtoms);
            HarnessAssert.True(
                projectedFieldAExpectedAtoms.Length == 8 &&
                projectedFieldAExpectedAtoms.All(value =>
                    value.Family ==
                        StatisticReconciliationExpectedMetricFamilies.Direct &&
                    value.Kind ==
                        StatisticReconciliationExpectedMetricKinds.Field &&
                    value.FieldId == P10ProductionDirectFieldId &&
                    value.MetricId == P10ProductionDirectFieldKey),
                "P11 API/EXPORT full expected projection must select field A only.");
            var comparableProjectedFieldAExpectedAtoms =
                projectedFieldAExpectedAtoms
                    .Where(P11IsComparableExpectedAtom)
                    .ToImmutableArray();
            HarnessAssert.Equal(
                5,
                comparableProjectedFieldAExpectedAtoms.Length,
                "P11 API/EXPORT expected comparison count");

            var sourceDecisions = await RequireDatabase()
                .GetCollection<StatisticReconciliationObservation>(
                    "work_report_statistic_reconciliation_observations")
                .Find(value =>
                    value.ReconciliationId == reconciliationId &&
                    value.GenerationId == finalVerdict.ExpectedGenerationId &&
                    value.RecordKind ==
                        StatisticReconciliationObservationRecordKinds.SourceDecision &&
                    value.SourceDecision!.ReportId == publication.SourceReportId)
                .Limit(2)
                .ToListAsync(ct);
            HarnessAssert.Equal(
                1,
                sourceDecisions.Count,
                "P11 Direct-only flow source decision count");
            var sourceDecision = sourceDecisions[0].SourceDecision!;
            HarnessAssert.Equal(
                tdtd_be.Services.StatisticsReconciliation.ExpectedLedger
                    .StatisticReconciliationExpectedContributionPolicies.Include,
                sourceDecision.ContributionPolicy,
                "P11 Direct-only locked flow contribution policy");
            HarnessAssert.Equal(
                publication.FlowTemplateVersionId,
                sourceDecision.ContributionVersionId,
                "P11 Direct-only locked flow contribution version");
            HarnessAssert.Equal(
                publication.FlowContributionPolicyHash,
                sourceDecision.ContributionPolicySha256,
                "P11 Direct-only locked flow contribution policy hash");
            HarnessAssert.Equal(
                "FLOW",
                sourceDecision.AuthoritativeRuntime?.RuntimeKind,
                "P11 Direct-only authoritative runtime kind");
            HarnessAssert.Equal(
                tdtd_be.Services.StatisticsReconciliation.ExpectedLedger
                    .StatisticReconciliationExpectedContributionPolicies.Include,
                sourceDecision.AuthoritativeRuntime?.ContributionPolicy,
                "P11 Direct-only authoritative runtime contribution policy");
            HarnessAssert.Equal(
                publication.FlowContributionPolicyHash,
                sourceDecision.AuthoritativeRuntime?.ContributionPolicySha256,
                "P11 Direct-only authoritative runtime contribution policy hash");
            HarnessAssert.Equal(
                publication.MappingResultSemanticHash,
                sourceDecision.AuthoritativeRuntime
                    ?.MappingResultSemanticSha256,
                "P11 Direct-only authoritative runtime mapping result semantic hash");
            HarnessAssert.Equal(
                publication.MappingReceiptId,
                sourceDecision.AuthoritativeRuntime?.MappingReceiptId,
                "P11 Direct-only authoritative mapping receipt");
            HarnessAssert.Equal(
                publication.MappingProvenanceId,
                sourceDecision.AuthoritativeRuntime?.MappingProvenanceId,
                "P11 Direct-only authoritative mapping provenance");
            HarnessAssert.Equal(
                true,
                sourceDecision.AuthoritativeRuntime?.MappingLocked,
                "P11 Direct-only authoritative mapping lock");

            var actualLayers = await RequireDatabase()
                .GetCollection<StatisticReconciliationObservation>(
                    "work_report_statistic_reconciliation_observations")
                .Find(value =>
                    value.ReconciliationId == reconciliationId &&
                    value.GenerationId == completed.CurrentGenerationId &&
                    value.RecordKind ==
                        StatisticReconciliationObservationRecordKinds.ActualLayer)
                .SortBy(value => value.ActualLayer!.Ordinal)
                .ToListAsync(ct);
            HarnessAssert.Equal(
                8,
                actualLayers.Count,
                "P11 Direct-only committed actual layer count");
            HarnessAssert.True(
                actualLayers.Select(value => value.ActualLayer?.Ordinal)
                    .SequenceEqual(
                        Enumerable.Range(0, 8).Select(value => (int?)value)),
                "P11 Direct-only actual layers must be ordered 0..7.");
            var expectedLayerCounts = new (string Layer, long Count)[]
            {
                ("SOURCE_MEMBERSHIP", 1),
                ("DIRECT_PROJECTION", 2),
                ("AGGREGATE", 6),
                ("BASIC", 0),
                ("ADVANCED", 0),
                ("DIFF", 0),
                ("API", 5),
                ("EXPORT", 3)
            };
            for (var index = 0; index < expectedLayerCounts.Length; index++)
            {
                var expectedLayer = expectedLayerCounts[index];
                var actualLayer = actualLayers[index];
                HarnessAssert.True(
                    actualLayer.LineagePin?.Layer == expectedLayer.Layer &&
                    actualLayer.ActualLayer?.ObservationCount ==
                        expectedLayer.Count,
                    $"P11 actual layer {index} expected {expectedLayer.Layer} count={expectedLayer.Count}; actual={actualLayer.LineagePin?.Layer ?? "<missing>"} count={actualLayer.ActualLayer?.ObservationCount.ToString() ?? "<missing>"}.");
            }

            var mongo = RequireMongo();
            var actualContext = new MongoDbContext(
                Microsoft.Extensions.Options.Options.Create(new MongoOptions
                {
                    ConnectionString = mongo.ConnectionString,
                    Database = mongo.DatabaseName
                }));
            var actualGeneration = await
                StatisticReconciliationActualGenerationPublisher
                    .ReadCompleteFromBackendAsync(
                        new StatisticReconciliationActualObservationMongoBackend(
                            actualContext),
                        reconciliationId,
                        completed.CurrentGenerationId!,
                        ct)
                ?? throw new InvalidOperationException(
                    "P11 committed actual generation is missing.");

            var actualAtomDocuments = await RequireDatabase()
                .GetCollection<StatisticReconciliationObservation>(
                    "work_report_statistic_reconciliation_observations")
                .Find(value =>
                    value.ReconciliationId == reconciliationId &&
                    value.GenerationId == completed.CurrentGenerationId &&
                    value.RecordKind ==
                        StatisticReconciliationObservationRecordKinds.ActualAtom)
                .ToListAsync(ct);
            var comparisonLayers = new[]
            {
                (Ordinal: 1,
                    Layer: "DIRECT_PROJECTION",
                    Expected: comparableExpectedDirectAtoms,
                    ComparisonCount: 10,
                    EntireTypedLayer: true),
                (Ordinal: 2,
                    Layer: "AGGREGATE",
                    Expected: comparableExpectedDirectAtoms,
                    ComparisonCount: 10,
                    EntireTypedLayer: true),
                (Ordinal: 6,
                    Layer: "API",
                    Expected: comparableProjectedFieldAExpectedAtoms,
                    ComparisonCount: 5,
                    EntireTypedLayer: false),
                (Ordinal: 7,
                    Layer: "EXPORT",
                    Expected: comparableProjectedFieldAExpectedAtoms,
                    ComparisonCount: 5,
                    EntireTypedLayer: false)
            };
            foreach (var comparisonLayer in comparisonLayers)
            {
                var typedLayerAtoms = actualGeneration.TypedObservations
                    .Where(value => value.Layer == comparisonLayer.Layer)
                    .ToArray();
                var comparableTypedLayerAtoms = comparisonLayer.EntireTypedLayer
                    ? typedLayerAtoms
                    : typedLayerAtoms
                        .Where(value =>
                            value.Family ==
                                StatisticReconciliationExpectedMetricFamilies.Direct &&
                            !value.MetricId.StartsWith(
                                "OWNER_OPAQUE:",
                                StringComparison.Ordinal) &&
                            !value.MetricId.StartsWith(
                                "OWNER_EXPLICIT:",
                                StringComparison.Ordinal))
                        .ToArray();
                var layerEvidence = P11DirectTypedLayerEvidence(
                    comparisonLayer.Ordinal,
                    finalVerdict.ComparisonBindingSha256,
                    comparisonLayer.Expected,
                    comparableTypedLayerAtoms);
                HarnessAssert.True(
                    layerEvidence.Ordinal == comparisonLayer.Ordinal &&
                    layerEvidence.EvidenceComplete &&
                    layerEvidence.ComparisonCount ==
                        comparisonLayer.ComparisonCount &&
                    layerEvidence.NonzeroCount == 0 &&
                    layerEvidence.MissingCount == 0 &&
                    layerEvidence.ExtraCount == 0 &&
                    layerEvidence.DeltaState ==
                        StatisticReconciliationRootCauseLayerDeltaStates.Zero,
                    $"P11 trusted {comparisonLayer.Layer} evidence expected compared={comparisonLayer.ComparisonCount}, nonzero=0, missing=0, extra=0; actual compared={layerEvidence.ComparisonCount}, nonzero={layerEvidence.NonzeroCount}, missing={layerEvidence.MissingCount}, extra={layerEvidence.ExtraCount}, delta={layerEvidence.DeltaState}.");

                var layerAtoms = actualAtomDocuments
                    .Where(value =>
                        value.LineagePin?.Layer == comparisonLayer.Layer)
                    .Select(value => value.Atom ??
                        throw new InvalidOperationException(
                            $"P11 {comparisonLayer.Layer} actual atom payload is missing."))
                    .ToArray();
                var comparableActualAtoms = layerAtoms
                    .Where(value =>
                        value.Family ==
                            StatisticReconciliationExpectedMetricFamilies.Direct &&
                        !value.MetricId.StartsWith(
                            "OWNER_OPAQUE:",
                            StringComparison.Ordinal) &&
                        !value.MetricId.StartsWith(
                            "OWNER_EXPLICIT:",
                            StringComparison.Ordinal))
                    .ToArray();
                HarnessAssert.True(
                    comparisonLayer.Expected.Length ==
                        comparisonLayer.ComparisonCount &&
                    comparableActualAtoms.Length ==
                        comparisonLayer.ComparisonCount,
                    $"P11 {comparisonLayer.Layer} compared count expected={comparisonLayer.ComparisonCount} actual={comparableActualAtoms.Length}.");
                if (comparisonLayer.EntireTypedLayer)
                {
                    HarnessAssert.Equal(
                        comparableActualAtoms.Length,
                        layerAtoms.Length,
                        $"P11 {comparisonLayer.Layer} full comparable atom set");
                }
                else
                {
                    HarnessAssert.True(
                        layerAtoms.Length > comparableActualAtoms.Length &&
                        comparableActualAtoms.All(value =>
                            value.FieldId == P10ProductionDirectFieldId &&
                            value.MetricId == P10ProductionDirectFieldKey),
                        $"P11 {comparisonLayer.Layer} must retain opaque audit atoms and compare field A only.");
                }

                var expectedSignatures = comparisonLayer.Expected
                    .Select(value =>
                        P11DirectComparableAtomSignature(
                            value,
                            normalizeExpectedCountValueType: true))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var actualSignatures = comparableActualAtoms
                    .Select(value =>
                        P11DirectComparableAtomSignature(
                            value,
                            normalizeExpectedCountValueType: false))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                HarnessAssert.True(
                    expectedSignatures.SequenceEqual(
                        actualSignatures,
                        StringComparer.Ordinal),
                    $"P11 {comparisonLayer.Layer} must have zero typed drift across {comparisonLayer.ComparisonCount} comparisons.");
            }

            var reviewReadResponse = await RequireApi().GetAsync(
                $"{basePath}/{reconciliationId}/review-decisions",
                Actor("executor").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                reviewReadResponse,
                HttpStatusCode.OK,
                "P11 field-A independent review read");
            var reviewRead = ApiHarnessClient.RequiredObject(
                reviewReadResponse.Json,
                "P11 field-A independent review response");
            var reviewSummary = ApiHarnessClient.RequiredObject(
                reviewRead["summary"],
                "P11 field-A independent review summary");
            HarnessAssert.True(
                ApiHarnessClient.RequiredString(
                    reviewSummary,
                    "reconciliationId") == reconciliationId &&
                ApiHarnessClient.RequiredString(
                    reviewSummary,
                    "generationId") ==
                    finalVerdict.VerdictGenerationId &&
                !ApiHarnessClient.RequiredBool(reviewSummary, "approved") &&
                ApiHarnessClient.RequiredInt(
                    reviewSummary,
                    "approvedGateCount") == 0 &&
                ApiHarnessClient.RequiredInt(
                    reviewSummary,
                    "rejectedGateCount") == 0,
                "P11 field-A independent review read summary");

            if (_p11Attempt039RecoveryProbe)
            {
                publication = await PrepareP11Attempt039RecoveryProbeAsync(
                    publication, completed, reconciliationId, basePath, ct);
            }

            // Freeze the queue before proving the recovery handoff so the
            // accepted 202 response and READY_TO_CLAIM post-state cannot race
            // the production worker. The isolated fixture is restarted with
            // the same signing key, database, and actual API owner binding.
            await RestartCloseoutBackendWithActualApiOwnerAsync(
                jwtSigningKey,
                ct,
                enableReconciliationWorker: false);
            await AwaitInfrastructureAsync(ct);

            var reviewLedgerBeforeRecovery =
                await SnapshotP11DirectReviewLedgerAsync(
                    reconciliationId,
                    ct);
            recoveryP9GenerationId = _p11Attempt039RecoveryProbe
                ? publication.P9GenerationId
                : await InstallP11DirectAuthoritativeP9SuccessorAsync(
                    publication,
                    ct);

            var driftReviewResponse = await RequireApi().GetAsync(
                $"{basePath}/{reconciliationId}/review-decisions",
                Actor("executor").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                driftReviewResponse,
                HttpStatusCode.Conflict,
                "P11 review after authoritative P9 topology drift");
            var driftProblem = ApiHarnessClient.RequiredObject(
                driftReviewResponse.Json,
                "P11 authoritative P9 topology drift problem");
            var forbiddenReviewProperties = new[]
            {
                "summary",
                "decisions",
                "auditRecords",
                "generationId",
                "generationSha256",
                "reviewRecord",
                "reviewRecordSha256",
                "reconciliationId"
            };
            HarnessAssert.True(
                ApiHarnessClient.RequiredString(driftProblem, "code") ==
                    StatisticReconciliationIndependentReviewFailureCodes
                        .TargetNotSignable &&
                ApiHarnessClient.RequiredInt(driftProblem, "status") ==
                    (int)HttpStatusCode.Conflict &&
                forbiddenReviewProperties.All(property =>
                    driftProblem[property] is null) &&
                !driftReviewResponse.Body.Contains(
                    reconciliationId,
                    StringComparison.Ordinal) &&
                !driftReviewResponse.Body.Contains(
                    completed.CurrentGenerationId!,
                    StringComparison.Ordinal) &&
                !driftReviewResponse.Body.Contains(
                    finalVerdict.VerdictGenerationId,
                    StringComparison.Ordinal),
                "P11 topology-drift review response must expose only the stable recovery discriminator, never stale review data.");

            var reviewLedgerAfterConflict =
                await SnapshotP11DirectReviewLedgerAsync(
                    reconciliationId,
                    ct);
            HarnessAssert.Equal(
                reviewLedgerBeforeRecovery,
                reviewLedgerAfterConflict,
                "P11 topology-drift review GET must not mutate the append-only review ledger");

            var runCollection = RequireDatabase()
                .GetCollection<StatisticReconciliationRun>(RunCollection);
            var pinnedTerminal = await runCollection
                .Find(value => value.Id == reconciliationId)
                .SingleAsync(ct);
            HarnessAssert.True(
                pinnedTerminal.InitialDeadlineAtUtc ==
                    pinnedTerminal.DeadlineAtUtc,
                "P11 new terminal row must persist its creation-time deadline pin");
            var legacyDeadlineAtUtc = pinnedTerminal.DeadlineAtUtc;
            var legacyUnset = await runCollection.UpdateOneAsync(
                value =>
                    value.Id == reconciliationId &&
                    value.StateRevision == completed.StateRevision &&
                    value.StateHash == completed.StateHash &&
                    value.InitialDeadlineAtUtc ==
                        pinnedTerminal.InitialDeadlineAtUtc,
                Builders<StatisticReconciliationRun>.Update.Unset(
                    value => value.InitialDeadlineAtUtc),
                cancellationToken: ct);
            HarnessAssert.True(
                legacyUnset.MatchedCount == 1 &&
                legacyUnset.ModifiedCount == 1,
                "P11 legacy deadline-pin fixture must use the exact terminal CAS tuple");

            var terminalBeforeRecheck = await runCollection
                .Find(value => value.Id == reconciliationId)
                .SingleAsync(ct);
            StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
                terminalBeforeRecheck);
            HarnessAssert.True(
                terminalBeforeRecheck.Status ==
                    StatisticReconciliationRunStatuses.Matched &&
                terminalBeforeRecheck.StateRevision ==
                    completed.StateRevision &&
                terminalBeforeRecheck.StateHash == completed.StateHash &&
                terminalBeforeRecheck.InitialDeadlineAtUtc is null &&
                terminalBeforeRecheck.DeadlineAtUtc ==
                    legacyDeadlineAtUtc &&
                terminalBeforeRecheck.CurrentGenerationId ==
                    completed.CurrentGenerationId &&
                terminalBeforeRecheck.CurrentGenerationHash ==
                    completed.CurrentGenerationHash &&
                terminalBeforeRecheck.Recheck is null,
                "P11 legacy terminal fallback must preserve the exact terminal CAS tuple");

            var recheckResponse = await RequireApi().PostAsync(
                $"{basePath}/{reconciliationId}/recheck",
                new
                {
                    commandId =
                        "p11-direct-regression-recheck-after-p9-drift-001",
                    expectedStateRevision = completed.StateRevision,
                    expectedStateHash = completed.StateHash
                },
                Actor("executor").Token,
                ct: ct);
            if (_p11Attempt039RecoveryProbe)
                await RecordP11Attempt039Async("RECOVERY_RECHECK", recheckResponse, ct);
            ApiHarnessClient.ExpectStatus(
                recheckResponse,
                HttpStatusCode.Accepted,
                "P11 recheck after authoritative P9 topology drift");
            var recheckAccepted = ApiHarnessClient.RequiredObject(
                recheckResponse.Json,
                "P11 accepted recovery recheck response");
            recoveryRecheckMarkerId = ApiHarnessClient.RequiredString(
                recheckAccepted,
                "recheckMarkerId");
            var acceptedStateHash = ApiHarnessClient.RequiredString(
                recheckAccepted,
                "stateHash");
            HarnessAssert.True(
                !ApiHarnessClient.RequiredBool(
                    recheckAccepted,
                    "isReplay") &&
                ApiHarnessClient.RequiredString(
                    recheckAccepted,
                    "reconciliationId") == reconciliationId &&
                ApiHarnessClient.RequiredString(
                    recheckAccepted,
                    "status") ==
                    StatisticReconciliationRunStatuses.Queued &&
                ApiHarnessClient.RequiredInt(
                    recheckAccepted,
                    "stateRevision") ==
                    completed.StateRevision + 1 &&
                StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                    recoveryRecheckMarkerId) &&
                StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                    acceptedStateHash),
                "P11 recovery recheck must return a fresh 202/QUEUED canonical tuple");

            var queuedAfterRecheck = await RequireDatabase()
                .GetCollection<StatisticReconciliationRun>(
                    RunCollection)
                .Find(value => value.Id == reconciliationId)
                .SingleAsync(ct);
            StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
                queuedAfterRecheck);
            HarnessAssert.True(queuedAfterRecheck.Recheck is not null,
                "P11 recovery recheck marker must be durable");
            StatisticReconciliationRecheckCanonical.RequireValidMarker(
                queuedAfterRecheck.Recheck!);
            HarnessAssert.True(
                queuedAfterRecheck.Status ==
                    StatisticReconciliationRunStatuses.Queued &&
                queuedAfterRecheck.StateRevision ==
                    completed.StateRevision + 1 &&
                queuedAfterRecheck.StateHash == acceptedStateHash &&
                queuedAfterRecheck.InitialDeadlineAtUtc ==
                    terminalBeforeRecheck.DeadlineAtUtc &&
                queuedAfterRecheck.DeadlineAtUtc >
                    queuedAfterRecheck.InitialDeadlineAtUtc!.Value &&
                queuedAfterRecheck.CurrentGenerationId ==
                    completed.CurrentGenerationId &&
                queuedAfterRecheck.CurrentGenerationHash ==
                    completed.CurrentGenerationHash &&
                queuedAfterRecheck.PendingGenerationId is null &&
                queuedAfterRecheck.PendingGenerationHash is null &&
                !queuedAfterRecheck.PendingGenerationPublishedAtUtc.HasValue &&
                queuedAfterRecheck.Recheck!.MarkerId ==
                    recoveryRecheckMarkerId &&
                queuedAfterRecheck.Recheck.Phase ==
                    StatisticReconciliationRecheckPhases.ReadyToClaim &&
                queuedAfterRecheck.Recheck.BeginExpectedStateRevision ==
                    completed.StateRevision &&
                queuedAfterRecheck.Recheck.BeginExpectedStateHash ==
                    completed.StateHash &&
                queuedAfterRecheck.Recheck.CaptureBinding.P9RunId ==
                    publication.P9RunId &&
                queuedAfterRecheck.Recheck.CaptureBinding.P9GenerationId ==
                    recoveryP9GenerationId &&
                queuedAfterRecheck.Recheck.CaptureBinding.P9GenerationHash ==
                    publication.P9GenerationHash,
                "P11 recovery recheck must preserve current evidence and bind the exact P9 successor");

            var tamperedDeadlinePin = await RequireDatabase()
                .GetCollection<StatisticReconciliationRun>(
                    RunCollection)
                .Find(value => value.Id == reconciliationId)
                .SingleAsync(ct);
            tamperedDeadlinePin.InitialDeadlineAtUtc =
                tamperedDeadlinePin.InitialDeadlineAtUtc!.Value
                    .AddMilliseconds(1);
            var deadlinePinTamperRejected = false;
            try
            {
                StatisticReconciliationRunService
                    .RequireActualCaptureReadIntegrity(tamperedDeadlinePin);
            }
            catch (AppException error) when (
                error.Code ==
                    AppErrorCode.STAT_RECONCILIATION_JOB_CONFLICT &&
                JsonSerializer.Serialize(error.Details).Contains(
                    "\"reason\":\"IMMUTABLE_INTEGRITY_INVALID\"",
                    StringComparison.Ordinal))
            {
                deadlinePinTamperRejected = true;
            }
            HarnessAssert.True(
                deadlinePinTamperRejected,
                "P11 immutable initial-deadline pin tamper must remain fail-closed");
            var reviewLedgerAfterRecheck =
                await SnapshotP11DirectReviewLedgerAsync(
                    reconciliationId,
                    ct);
            HarnessAssert.Equal(
                reviewLedgerBeforeRecovery,
                reviewLedgerAfterRecheck,
                "P11 recovery recheck begin must not expose or rewrite stale review rows");
            if (_p11Attempt039RecoveryProbe)
                await CompleteP11Attempt039RecheckAsync(
                    jwtSigningKey, reconciliationId, completed, ct);
            recoveryVerified = true;

            summaryAfter = await CountP11DirectSummaryOwnersAsync(ct);
            foreach (var store in P11DirectSummaryOwnerStores)
            {
                HarnessAssert.Equal(
                    summaryBefore[store],
                    summaryAfter[store],
                    $"P11 Direct-only zero-write summary owner {store}");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            workerStageDiagnostic = ReadP11DirectWorkerStageDiagnostic();
            Console.Error.WriteLine(exception);
        }
        finally
        {
            await StopBackendAsync(cleanupErrors);
            DeleteP11Attempt039Exports(cleanupErrors);
            DeleteCloseoutProductionExportArtifactDirectory(cleanupErrors);
            await CleanupMongoAsync(cleanupErrors);
        }

        var cleanupSucceeded = cleanupErrors.Count == 0 &&
            _backend is { StopVerified: true, PortReleaseVerified: true } &&
            _mongo is
            {
                DatabaseDropVerified: true,
                ProcessStopVerified: true,
                PortReleaseVerified: true,
                DataDirectoryRemovalVerified: true
            };
        if (cleanupErrors.Count > 0)
            Console.Error.WriteLine("cleanup=" + string.Join(" | ", cleanupErrors));

        var passed =
            failure is null &&
            cleanupSucceeded &&
            publication is not null &&
            completed?.Status == StatisticReconciliationRunStatuses.Matched &&
            reconciliationId is not null &&
            exportId is not null &&
            mappingContributionPolicyJson is not null &&
            summaryBefore is not null &&
            summaryAfter is not null &&
            recoveryVerified &&
            recoveryP9GenerationId is not null &&
            recoveryRecheckMarkerId is not null;
        Console.WriteLine(
            passed
                ? $"PASS P11 Direct-only reconciliation regression; run={reconciliationId}; p9FieldRows=2; selectedRows=1; layerCounts=1,2,6,0,0,0,5,3; expected=A+B; compared=10,10,5,5; apiExportScope=field-A; reviewRead=200; reviewDrift=409/P10_REVIEW_TARGET_NOT_SIGNABLE/no-stale-data; recheck=202/QUEUED/READY_TO_CLAIM; summaryWrites=0; export=XLSX; mongoTimestamp=millisecond; mappingPolicy=production-shaped; lockedFlowPolicy={publication!.FlowContributionPolicy}; artifacts={_paths.RunRoot}"
                : $"FAIL P11 Direct-only reconciliation regression; run={reconciliationId ?? "<none>"}; status={completed?.Status ?? "<none>"}; diagnostic={completed?.DiagnosticCode ?? "<none>"}; workerStage={workerStageDiagnostic ?? "<none>"}; verdict={verdictDiagnostic ?? "<none>"}; error={failure?.GetType().Name}:{failure?.Message}; cleanup={cleanupSucceeded}; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private async Task<string> InstallP11DirectAuthoritativeP9SuccessorAsync(
        P10ProductionDirectFixturePins publication,
        CancellationToken ct)
    {
        var collection = RequireDatabase()
            .GetCollection<WorkReportStatisticRebuildJob>(
                "work_report_statistic_rebuild_jobs");
        var original = await collection
            .Find(value =>
                value.Id == publication.P9RunId &&
                !value.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.True(
            original.GenerationId == publication.P9GenerationId &&
            original.GenerationHash == publication.P9GenerationHash &&
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                original.StateHash),
            "P11 recovery fixture must begin from the exact authoritative P9 tuple");

        var originalGenerationId = original.GenerationId!;
        var originalGenerationHash = original.GenerationHash!;
        var originalStateHash = original.StateHash!;
        var originalStateRevision = original.StateRevision;
        var originalStatus = original.Status;
        var successorGenerationId = HashText(
            $"P11-DIRECT-RECONCILIATION-RECOVERY-SUCCESSOR:{_runKey}");
        HarnessAssert.True(
            successorGenerationId != originalGenerationId,
            "P11 recovery successor generation must be distinct");

        original.GenerationId = successorGenerationId;
        original.StateHash = StatisticReconciliationCanonicalJson.HashObject(
            new
            {
                version = "P9_LIFECYCLE_DIRECT_STATE_V1",
                runId = original.Id,
                status = original.Status,
                revision = original.StateRevision,
                claimToken = (string?)null,
                workerId = (string?)null,
                generationId = original.GenerationId,
                generationHash = original.GenerationHash
            });
        var replace = await collection.ReplaceOneAsync(
            value =>
                value.Id == publication.P9RunId &&
                value.GenerationId == originalGenerationId &&
                value.GenerationHash == originalGenerationHash &&
                value.StateRevision == originalStateRevision &&
                value.StateHash == originalStateHash &&
                !value.IsDeleted,
            original,
            cancellationToken: ct);
        HarnessAssert.True(
            replace.MatchedCount == 1 && replace.ModifiedCount == 1,
            "P11 authoritative P9 successor CAS must replace exactly one owner");

        var persisted = await collection
            .Find(value => value.Id == publication.P9RunId)
            .SingleAsync(ct);
        HarnessAssert.True(
            persisted.GenerationId == successorGenerationId &&
            persisted.GenerationHash == originalGenerationHash &&
            persisted.StateRevision == originalStateRevision &&
            persisted.Status == originalStatus &&
            persisted.StateHash == original.StateHash &&
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                persisted.StateHash),
            "P11 authoritative P9 successor postimage must change only topology identity/state hash");
        return successorGenerationId;
    }

    private async Task<string> SnapshotP11DirectReviewLedgerAsync(
        string reconciliationId,
        CancellationToken ct)
    {
        var rows = await RequireDatabase()
            .GetCollection<BsonDocument>(
                StatisticReconciliationIndependentReviewMongoBackend
                    .CollectionName)
            .Find(new BsonDocument(
                "reconciliationId",
                reconciliationId))
            .Sort(new BsonDocument("_id", 1))
            .ToListAsync(ct);
        HarnessAssert.True(
            rows.Count >= 1,
            "P11 recovery fixture must retain the final-verdict review row");
        return HashText(string.Join(
            "\n",
            rows.Select(row => row.ToJson())));
    }
    private static StatisticReconciliationRootCauseLayerEvidence
        P11DirectTypedLayerEvidence(
            int ordinal,
            string comparisonBindingSha256,
            IEnumerable<StatisticReconciliationObservationAtom> expected,
            IEnumerable<StatisticReconciliationActualTypedObservation> actual)
    {
        var method = typeof(StatisticReconciliationTrustedVerdictDeriver)
            .GetMethods(
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)
            .Single(value =>
                value.Name == "TypedLayer" &&
                value.GetParameters().Length == 4);
        try
        {
            return (StatisticReconciliationRootCauseLayerEvidence)(
                method.Invoke(
                    null,
                    new object[]
                    {
                        ordinal,
                        comparisonBindingSha256,
                        expected,
                        actual
                    })
                ?? throw new InvalidOperationException(
                    "P11 trusted typed-layer evidence is missing."));
        }
        catch (System.Reflection.TargetInvocationException error)
            when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static bool P11IsComparableExpectedAtom(
        StatisticReconciliationObservationAtom atom)
        => atom.ValueType == StatisticReconciliationExpectedValueTypes.Number &&
           P11IsIsomorphicAtomKind(atom.AtomKind) &&
           atom.ValueState == StatisticReconciliationExpectedValueStates.Value;

    private static bool P11ProvesOmittedZero(
        StatisticReconciliationObservationAtom atom)
    {
        if (atom.DecimalScale != 0 || atom.OccurrenceCount != 0)
            return false;
        if (atom.AtomKind is
            StatisticReconciliationExpectedAtomKinds.Missing or
            StatisticReconciliationExpectedAtomKinds.Null or
            StatisticReconciliationExpectedAtomKinds.Empty)
        {
            return atom.ValueState == atom.AtomKind &&
                atom.CanonicalValue == "0";
        }

        return P11IsIsomorphicAtomKind(atom.AtomKind) &&
            atom.ValueState !=
                StatisticReconciliationExpectedValueStates.Value &&
            atom.CanonicalValue.Length == 0;
    }

    private static bool P11IsIsomorphicAtomKind(string atomKind)
        => atomKind is
            "REPORT_COUNT" or
            "ROW_COUNT" or
            "COUNT" or
            "NUMERIC_VALUE_COUNT" or
            "SUM" or
            "MIN" or
            "MAX" or
            "MEAN" or
            "BUCKET" or
            "DATE" or
            "FULL_DATE" or
            "PERIOD" or
            "BOOLEAN" or
            "ENUM" or
            "TEXT";

    private static string P11DirectComparableAtomSignature(
        StatisticReconciliationObservationAtom atom,
        bool normalizeExpectedCountValueType)
    {
        var isCountAtom = atom.AtomKind is
            "REPORT_COUNT" or
            "ROW_COUNT" or
            "COUNT" or
            "NUMERIC_VALUE_COUNT" or
            "ADDED_COUNT" or
            "REMOVED_COUNT" or
            "CHANGED_COUNT" or
            "UNCHANGED_COUNT";
        var hasValue =
            atom.ValueState == StatisticReconciliationExpectedValueStates.Value;
        return System.Text.Json.JsonSerializer.Serialize(new object?[]
        {
            atom.Family,
            atom.Kind,
            atom.MetricId,
            atom.PeriodKey,
            atom.FieldId,
            atom.TableId,
            atom.RowId,
            atom.LabelId,
            atom.BasicScope,
            atom.BasicScopeId,
            atom.AdvancedGrain,
            atom.DiffKind,
            atom.TransitionLeg ?? "NONE",
            atom.TransitionKind,
            atom.CollectionSemantics,
            atom.AtomKind,
            normalizeExpectedCountValueType && isCountAtom
                ? "NUMBER"
                : atom.ValueType,
            atom.ValueState,
            hasValue ? atom.CanonicalValue : null,
            hasValue ? atom.DecimalScale : 0,
            hasValue ? atom.CollectionSemantics : null,
            atom.OccurrenceCount,
            atom.ReportCount,
            atom.RowCount,
            atom.NumericValueCount
        });
    }

    private static string BuildP11DirectFlowMappingContributionPolicy()
    {
        const string mappingId = "p11-direct-regression-field-map";
        return new JsonObject
        {
            ["defaultMode"] = "EXCLUDE",
            ["rules"] = new JsonArray
            {
                new JsonObject
                {
                    ["targetKind"] = "FIELD",
                    ["targetKey"] = P10ProductionDirectFieldKey,
                    ["mode"] = "EXCLUDE",
                    ["source"] = "DYNAMIC_FLOW_MAPPING",
                    ["mappingId"] = mappingId
                }
            }
        }.ToJsonString();
    }

    private async Task
        RequireP11DirectFlowMappingContributionPolicyAsync(
            P10ProductionDirectFixturePins publication,
            string policyJson,
            CancellationToken ct)
    {
        const string mappingId = "p11-direct-regression-field-map";
        var reports = RequireDatabase()
            .GetCollection<BsonDocument>("work_assignment_report");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq(
                "_id",
                ObjectId.Parse(publication.SourceReportId)),
            Builders<BsonDocument>.Filter.Eq(
                "payloadRevision",
                publication.SourcePayloadRevision),
            Builders<BsonDocument>.Filter.Eq(
                "payloadHash",
                publication.SourcePayloadHash),
            Builders<BsonDocument>.Filter.Eq(
                "lifecycleRevision",
                publication.SourceLifecycleRevision),
            Builders<BsonDocument>.Filter.Eq(
                "dynamicFlowMappingReceiptId",
                ObjectId.Parse(publication.MappingReceiptId)),
            Builders<BsonDocument>.Filter.Eq(
                "dynamicFlowMappingProvenanceId",
                ObjectId.Parse(publication.MappingProvenanceId)));
        var persisted = await reports
            .Find(filter)
            .SingleAsync(ct);
        var persistedJson = persisted.GetValue(
            "cumulativeContributionPolicyJson").AsString;
        HarnessAssert.Equal(
            policyJson,
            persistedJson,
            "P11 Direct-only persisted flow mapping policy");
        var policy = JsonNode.Parse(persistedJson)?.AsObject()
            ?? throw new InvalidOperationException(
                "P11 Direct-only flow mapping policy did not parse as an object.");
        HarnessAssert.Equal(
            "EXCLUDE",
            policy["defaultMode"]?.GetValue<string>(),
            "P11 Direct-only flow mapping default mode");
        var rules = policy["rules"]?.AsArray()
            ?? throw new InvalidOperationException(
                "P11 Direct-only flow mapping policy rules are missing.");
        HarnessAssert.Equal(
            1,
            rules.Count,
            "P11 Direct-only flow mapping policy rule count");
        HarnessAssert.Equal(
            "DYNAMIC_FLOW_MAPPING",
            rules[0]?["source"]?.GetValue<string>(),
            "P11 Direct-only flow mapping policy source");
        HarnessAssert.Equal(
            mappingId,
            rules[0]?["mappingId"]?.GetValue<string>(),
            "P11 Direct-only flow mapping policy mapping id");
        HarnessAssert.Equal(
            WorkReportDataOrigin.PartialMapping,
            persisted.GetValue("dataOrigin").AsString,
            "P11 Direct-only flow mapping data origin");
        HarnessAssert.Equal(
            WorkReportCumulativeContributionMode.Exclude,
            persisted.GetValue("cumulativeContributionMode").AsString,
            "P11 Direct-only flow mapping report contribution mode");

        var canonicalPolicyHash =
            tdtd_be.Services.DynamicFlows
                .DynamicFlowMappingSecurityContract.ComputeCanonicalSha256(
                    policyJson);
        HarnessAssert.Equal(
            canonicalPolicyHash,
            publication.MappingContributionPolicyHash,
            "P11 Direct-only canonical mapping contribution policy hash");
        HarnessAssert.True(
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                publication.MappingResultSemanticHash) &&
            StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                publication.MappingResultSnapshotHash),
            "P11 Direct-only mapping semantic/snapshot hashes must be canonical.");

        var receipt = await RequireDatabase()
            .GetCollection<tdtd_be.Models.DynamicFlowMappingApplyReceipt>(
                "dynamic_flow_mapping_apply_receipts")
            .Find(value =>
                value.Id == publication.MappingReceiptId &&
                value.ProvenanceId == publication.MappingProvenanceId)
            .SingleAsync(ct);
        HarnessAssert.Equal(
            publication.MappingResultSemanticHash,
            receipt.ResultSemanticHash,
            "P11 Direct-only receipt result semantic hash");
        HarnessAssert.Equal(
            publication.MappingResultSnapshotHash,
            receipt.ResultSnapshotHash,
            "P11 Direct-only receipt result snapshot hash");
        HarnessAssert.Equal(
            receipt.ResultSnapshotHash,
            tdtd_be.Services.DynamicFlows.DynamicFlowMappingLifecycleContract
                .ComputeDocumentHash(receipt.ResultSnapshot),
            "P11 Direct-only receipt result snapshot hash recompute");
        HarnessAssert.Equal(
            publication.MappingResultSemanticHash,
            receipt.ResultSnapshot.GetValue("resultSemanticHash").AsString,
            "P11 Direct-only result snapshot semantic binding");
        HarnessAssert.Equal(
            canonicalPolicyHash,
            receipt.ResultSnapshot.GetValue("contributionPolicyHash").AsString,
            "P11 Direct-only result snapshot contribution policy binding");

        var provenance = await RequireDatabase()
            .GetCollection<
                tdtd_be.Models.DynamicFlowMappingProvenanceRecord>(
                "dynamic_flow_mapping_provenance")
            .Find(value =>
                value.Id == publication.MappingProvenanceId &&
                value.ReceiptId == publication.MappingReceiptId)
            .SingleAsync(ct);
        HarnessAssert.Equal(
            receipt.ResultSemanticHash,
            provenance.ResultSemanticHash,
            "P11 Direct-only receipt/provenance semantic parity");
        HarnessAssert.Equal(
            receipt.ResultSnapshotHash,
            provenance.ResultSnapshotHash,
            "P11 Direct-only receipt/provenance snapshot parity");
        HarnessAssert.Equal(
            canonicalPolicyHash,
            provenance.ResultSnapshot
                .GetValue("contributionPolicyHash").AsString,
            "P11 Direct-only provenance contribution policy binding");
    }

    private async Task<string?> ReadP11DirectVerdictDiagnosticAsync(
        string reconciliationId,
        StatisticReconciliationRun terminal,
        CancellationToken ct)
    {
        var verdicts = await RequireDatabase()
            .GetCollection<StatisticReconciliationReview>(
                "work_report_statistic_reconciliation_reviews")
            .Find(value =>
                value.ReconciliationId == reconciliationId &&
                value.ActualGenerationId == terminal.CurrentGenerationId &&
                value.RecordKind ==
                    StatisticReconciliationReviewKinds.FinalVerdict)
            .Limit(2)
            .ToListAsync(ct);
        if (verdicts.Count != 1)
            return $"count:{verdicts.Count}";

        var verdict = verdicts[0];
        return string.Join('/',
            verdict.Verdict,
            verdict.FailureKind,
            verdict.RootCauseClass ?? "NONE",
            verdict.CompleteEvidence ? "complete" : "incomplete",
            verdict.AllRequiredLayersZero ? "zero" : "nonzero",
            verdict.MissingOrExtraIdentity ? "identity-drift" : "identity-exact",
            verdict.UnknownBlocksCloseout ? "unknown-blocks" : "known",
            verdict.Signable ? "signable" : "unsigned");
    }
    private string? ReadP11DirectWorkerStageDiagnostic()
    {
        if (_backend is null)
            return null;

        var logs = string.Join('\n',
            LogTail.Read(_backend.StdoutPath, 400),
            LogTail.Read(_backend.StderrPath, 400));
        string[] allowed =
        [
            "RELATIONAL_V4_CENTRAL_PROJECT_INVALID",
            "RELATIONAL_V4_CENTRAL_RAW_INVALID",
            "RELATIONAL_V4_CENTRAL_DIRECT_PARITY_INVALID",
            "RELATIONAL_V4_CENTRAL_SUMMARY_PARITY_INVALID",
            "RELATIONAL_V4_CENTRAL_PARITY_INVALID",
            "RELATIONAL_V4_EXTENDED_SOURCE_INTEGRITY_INVALID",
            "RELATIONAL_V4_EXTENDED_OWNER_PARITY_INVALID",
            "RELATIONAL_V4_EXTENDED_SOURCE_REPLAY_INVALID",
            "RELATIONAL_V4_CROSS_VIEW_INVALID",
            "RELATIONAL_V4_CROSS_VIEW_INTEGRITY_INVALID",
            "RELATIONAL_V4_FACTS_INVALID"
        ];
        return allowed.FirstOrDefault(code =>
            logs.Contains(code, StringComparison.Ordinal));
    }
    private async Task<IReadOnlyDictionary<string, long>>
        CountP11DirectSummaryOwnersAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var store in P11DirectSummaryOwnerStores)
        {
            result[store] = await RequireDatabase()
                .GetCollection<BsonDocument>(store)
                .CountDocumentsAsync(
                    FilterDefinition<BsonDocument>.Empty,
                    cancellationToken: ct);
        }
        return result;
    }

    private async Task<StatisticReconciliationRun>
        WaitForP11DirectTerminalAsync(
            string reconciliationId,
            TimeSpan timeout,
            CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        StatisticReconciliationRun? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await RequireDatabase()
                .GetCollection<StatisticReconciliationRun>(RunCollection)
                .Find(value =>
                    value.Id == reconciliationId &&
                    !value.IsDeleted)
                .SingleAsync(ct);
            if (last.Status is
                StatisticReconciliationRunStatuses.Matched or
                StatisticReconciliationRunStatuses.Mismatched or
                StatisticReconciliationRunStatuses.Stale or
                StatisticReconciliationRunStatuses.Failed or
                StatisticReconciliationRunStatuses.Cancelled)
            {
                return last;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new TimeoutException(
            $"P11 Direct-only reconciliation did not reach terminal state. status={last?.Status ?? "<missing>"}; diagnostic={last?.DiagnosticCode ?? "<none>"}.");
    }
}
