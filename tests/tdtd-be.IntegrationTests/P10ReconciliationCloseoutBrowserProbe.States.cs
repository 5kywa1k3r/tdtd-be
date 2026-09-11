using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Time;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsReconciliation;
using tdtd_be.Models.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;
using tdtd_be.Services.StatisticsReconciliation.TypedDelta;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    internal static readonly string[] CloseoutProductionStatuses =
    [
        "EMPTY",
        "QUEUED",
        "RUNNING",
        "MATCHED",
        "MISMATCHED",
        "STALE",
        "FAILED"
    ];

    private P10CloseoutProductionStateRegistry? _closeoutStateRegistry;
    private P10CloseoutSuccessorBarrier? _closeoutSuccessorBarrier;

    private async Task<P10CloseoutPreparedFixture>
        PrepareCloseoutCanonicalStateRegistryAsync(CancellationToken ct)
    {
        var p9Collection = RequireDatabase().GetCollection<BsonDocument>(
            "work_report_statistic_rebuild_jobs");
        var p9Before = await p9Collection.Find(new BsonDocument(
                "_id",
                ObjectId.Parse(Fixture().P9RunId)))
            .SingleAsync(ct);
        var p9StateSha256 = HashBytes(p9Before.ToBson());

        var mongo = RequireMongo();
        var mongoOptions = Microsoft.Extensions.Options.Options.Create(
            new MongoOptions
            {
                ConnectionString = mongo.ConnectionString,
                Database = mongo.DatabaseName
            });
        var context = new MongoDbContext(mongoOptions);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] =
                    "p10-close-production-state-registry-signing-key-32-bytes",
                ["StatisticReconciliation:MaxRetryCount"] = "2",
                ["StatisticReconciliation:LeaseSeconds"] = "120",
                ["StatisticReconciliation:RetryBaseSeconds"] = "1",
                ["StatisticReconciliation:RetryMaxSeconds"] = "5",
                ["StatisticReconciliation:SlaSeconds"] = "600"
            })
            .Build();
        var environment = new P10RollbackHostEnvironment
        {
            EnvironmentName = "Production",
            ApplicationName = "tdtd-be",
            ContentRootPath = CloseoutWorkspacePath("tdtd-be")
        };
        var activation = new StatisticReconciliationCapabilityActivation(
            configuration,
            environment,
            mongoOptions);
        var time = new AppTimeService();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new StatisticReconciliationRunService(
            context,
            activation,
            new StatRunCapabilityActivation(
                configuration,
                environment,
                mongoOptions),
            time,
            configuration,
            cache);
        var actor = CloseoutSystemActor();

        // Each non-queued state is completed before the next create so the
        // production ClaimAsync ordering can select only the intended run.
        var matchedCaptureOwners = _closeoutCaptureOwnerFixture ??
            throw new InvalidOperationException(
                "P10-CLOSE capture owners are unavailable.");
        var matchedId = await CreateCloseoutRunAsync(
            "p10-close-browser-matched-001",
            ct,
            request => ApplyCloseoutMatchedCapturePlan(
                request,
                matchedCaptureOwners));
        var matchedFence = await ClaimCloseoutRunCanonicalAsync(
            service,
            actor,
            matchedId,
            "p10-close-production-worker-matched",
            ct);
        var capture = await RequireApi().PostAsync(
            $"api/admin/internal/statistics-reconciliation/{matchedId}/actual-capture/claimed",
            new
            {
                workerId = matchedFence.WorkerId,
                claimToken = matchedFence.ClaimToken
            },
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            capture,
            System.Net.HttpStatusCode.OK,
            "P10-CLOSE production trusted capture");
        HarnessAssert.True(
            ApiHarnessClient.FindStringRecursive(
                capture.Json,
                "reconciliationId") == matchedId &&
            ApiHarnessClient.FindStringRecursive(
                capture.Json,
                "generationId") is { Length: 64 },
            "P10-CLOSE trusted capture response identity");
        await RequireCloseoutRunStatusAsync(context, matchedId, "MATCHED", ct);

        var mismatchedId = await CreateCloseoutRunAsync(
            "p10-close-browser-mismatched-002",
            ct,
            request => request["conceptKey"] =
                $"{Fixture().ConceptKey}_mismatched");
        var mismatchedFence = await ClaimCloseoutRunCanonicalAsync(
            service,
            actor,
            mismatchedId,
            "p10-close-production-worker-mismatched",
            ct);
        await FinalizeCloseoutCanonicalAsync(
            context,
            service,
            activation,
            time,
            actor,
            mismatchedId,
            mismatchedFence,
            "MISMATCHED",
            ct);

        var staleId = await CreateCloseoutRunAsync(
            "p10-close-browser-stale-003",
            ct,
            request => request["conceptKey"] =
                $"{Fixture().ConceptKey}_stale");
        var staleFence = await ClaimCloseoutRunCanonicalAsync(
            service,
            actor,
            staleId,
            "p10-close-production-worker-stale",
            ct);
        await FinalizeCloseoutCanonicalAsync(
            context,
            service,
            activation,
            time,
            actor,
            staleId,
            staleFence,
            "STALE",
            ct);

        var failedId = await CreateCloseoutRunAsync(
            "p10-close-browser-failed-004",
            ct,
            request => request["conceptKey"] =
                $"{Fixture().ConceptKey}_failed");
        var failedFence = await ClaimCloseoutRunCanonicalAsync(
            service,
            actor,
            failedId,
            "p10-close-production-worker-failed",
            ct);
        var failed = await service.FailAsync(
            failedId,
            new StatisticReconciliationWorkerFailRequest
            {
                WorkerId = failedFence.WorkerId,
                ClaimToken = failedFence.ClaimToken,
                FailureCode = "P10_CLOSE_CANONICAL_NON_TRANSIENT",
                Transient = false
            },
            actor,
            ct);
        HarnessAssert.Equal(
            "FAILED",
            failed.Status,
            "P10-CLOSE product FailAsync terminal state");

        var runningId = await CreateCloseoutRunAsync(
            "p10-close-browser-running-005",
            ct,
            request => request["conceptKey"] =
                $"{Fixture().ConceptKey}_running");
        _ = await ClaimCloseoutRunCanonicalAsync(
            service,
            actor,
            runningId,
            "p10-close-production-worker-running",
            ct);
        var queuedId = await CreateCloseoutRunAsync(
            "p10-close-browser-queued-006",
            ct,
            request => request["conceptKey"] =
                $"{Fixture().ConceptKey}_queued");

        var expected = new Dictionary<string, (string Status, string Path)>(
            StringComparer.Ordinal)
        {
            [queuedId] = ("QUEUED", "PRODUCTION_HTTP_CREATE"),
            [runningId] = ("RUNNING", "PRODUCT_RUN_SERVICE_CLAIM"),
            [matchedId] = ("MATCHED", "PRODUCTION_TRUSTED_CAPTURE"),
            [mismatchedId] =
                ("MISMATCHED", "FIXTURE_SETUP_TRUSTED_PRODUCTION_COMPONENTS"),
            [staleId] =
                ("STALE", "FIXTURE_SETUP_TRUSTED_PRODUCTION_COMPONENTS"),
            [failedId] = ("FAILED", "PRODUCT_RUN_SERVICE_FAIL")
        };
        var rows = await context.StatisticReconciliationRuns.Find(value =>
                value.WorkId == Fixture().WorkId &&
                value.ScopeAssignmentId == Fixture().ScopeAssignmentId &&
                !value.IsDeleted)
            .ToListAsync(ct);
        HarnessAssert.True(
            rows.Count == expected.Count &&
            rows.Select(value => value.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(expected.Keys),
            "P10-CLOSE exact six production Mongo rows");
        foreach (var row in rows)
        {
            StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(
                row);
            HarnessAssert.Equal(
                expected[row.Id].Status,
                row.Status,
                $"P10-CLOSE direct Mongo status {row.Id}");
        }
        var emptyCount = await context.StatisticReconciliationRuns.CountDocumentsAsync(
            value => value.WorkId == Fixture().WorkId &&
                value.ScopeAssignmentId == Fixture().SiblingAssignmentId &&
                !value.IsDeleted,
            cancellationToken: ct);
        HarnessAssert.Equal(
            0L,
            emptyCount,
            "P10-CLOSE direct Mongo EMPTY sibling scope");

        var registryRows = new List<P10CloseoutProductionStateRow>
        {
            new(
                "EMPTY",
                null,
                Fixture().SiblingAssignmentId,
                "PRODUCTION_LIST_ZERO_ROWS",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null)
        };
        foreach (var status in CloseoutProductionStatuses.Skip(1))
        {
            var row = rows.Single(value => value.Status == status);
            registryRows.Add(new P10CloseoutProductionStateRow(
                status,
                row.Id,
                row.ScopeAssignmentId,
                expected[row.Id].Path,
                row.StateRevision,
                row.ReviewDecisionRevision,
                row.StateHash,
                row.SourceReportId,
                row.SourcePayloadRevision,
                row.SourcePayloadHash,
                row.SourceLifecycleRevision,
                row.SourceLifecycleHash));
        }
        var registrySha256 = HashText(string.Join(
            "\n",
            registryRows.Select(value => string.Join(
                "\u001f",
                value.Status,
                value.ReconciliationId ?? string.Empty,
                value.ScopeAssignmentId,
                value.FixturePath,
                value.StateRevision?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ??
                    string.Empty,
                value.ReviewDecisionRevision?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ??
                    string.Empty,
                value.StateHash ?? string.Empty,
                value.SourceReportId ?? string.Empty,
                value.SourcePayloadRevision?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ??
                    string.Empty,
                value.SourcePayloadHash ?? string.Empty,
                value.SourceLifecycleRevision?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ??
                    string.Empty,
                value.SourceLifecycleHash ?? string.Empty))));
        _closeoutStateRegistry = new P10CloseoutProductionStateRegistry(
            CloseoutProductionStatuses,
            registryRows,
            7,
            7,
            true,
            emptyCount,
            registrySha256);

        var target = rows.Single(value => value.Id == matchedId);
        return new P10CloseoutPreparedFixture(
            matchedId,
            target.StateRevision,
            target.Status,
            runningId,
            queuedId,
            failedId,
            p9StateSha256);
    }

    private async Task<P10CloseoutWorkerFence>
        ClaimCloseoutRunCanonicalAsync(
            StatisticReconciliationRunService service,
            MeResponse actor,
            string expectedReconciliationId,
            string workerId,
            CancellationToken ct)
    {
        var claim = await service.ClaimAsync(
            new StatisticReconciliationWorkerClaimRequest
            {
                WorkerId = workerId
            },
            actor,
            ct) ?? throw new InvalidOperationException(
                "P10_CLOSE_PRODUCT_CLAIM_MISSING");
        HarnessAssert.Equal(
            expectedReconciliationId,
            claim.Run.ReconciliationId,
            "P10-CLOSE production ClaimAsync selected intended run");
        return new P10CloseoutWorkerFence(workerId, claim.ClaimToken);
    }

    private async Task FinalizeCloseoutCanonicalAsync(
        MongoDbContext context,
        StatisticReconciliationRunService service,
        IStatisticReconciliationCandidateActivation activation,
        IAppTimeService time,
        MeResponse actor,
        string reconciliationId,
        P10CloseoutWorkerFence fence,
        string expectedStatus,
        CancellationToken ct)
    {
        var generation = await CommitActualGenerationAsync(
            reconciliationId,
            fence.WorkerId,
            fence.ClaimToken,
            ct);
        _ = await service.PublishPendingAsync(
            reconciliationId,
            new StatisticReconciliationPendingPublishRequest
            {
                WorkerId = fence.WorkerId,
                ClaimToken = fence.ClaimToken,
                GenerationId = generation.GenerationId,
                GenerationHash = generation.GenerationSemanticSha256
            },
            actor,
            ct);
        var pending = await context.StatisticReconciliationRuns.Find(value =>
                value.Id == reconciliationId)
            .SingleAsync(ct);
        var matched = InvokeTrustedFinalizerFixture<
            StatisticReconciliationFinalVerdictRequest>(
            "MatchedVerdict",
            reconciliationId,
            generation.GenerationId,
            generation.GenerationSemanticSha256);
        var verdict = expectedStatus switch
        {
            "MISMATCHED" => BuildCloseoutMismatchedVerdict(matched),
            "STALE" => BuildCloseoutStaleVerdict(matched),
            _ => matched
        };
        var decision = new StatisticReconciliationFinalVerdictEvaluator()
            .Evaluate(verdict);
        HarnessAssert.True(
            decision.PublicationAllowed &&
            !decision.Signable &&
            !decision.CloseoutAllowed &&
            !decision.UnknownBlocksCloseout &&
            (expectedStatus == "MISMATCHED"
                ? decision.Verdict == "MISMATCHED" &&
                  decision.RootCauseClass ==
                      StatisticReconciliationRootCauseClasses.Projection &&
                  decision.CompleteEvidence &&
                  !decision.AllRequiredLayersZero &&
                  !decision.MissingOrExtraIdentity
                : decision.Verdict == "FAILED" &&
                  decision.RootCauseClass == "FRESHNESS" &&
                  !decision.CompleteEvidence &&
                  !decision.AllRequiredLayersZero &&
                  !decision.MissingOrExtraIdentity),
            $"P10-CLOSE canonical {expectedStatus} verdict decision");

        var backend = new StatisticReconciliationReviewMongoBackend(context);
        var finalizer = new StatisticReconciliationTrustedFinalizer(
            context,
            activation,
            time,
            new StatisticReconciliationFinalVerdictPublisher(backend),
            backend);
        _ = await finalizer.FinalizePendingAsync(
            new StatisticReconciliationTrustedFinalizeCommand(
                reconciliationId,
                pending.StateRevision,
                pending.StateHash,
                pending.GenerationPublishRevision,
                verdict),
            actor,
            ct);
        await RequireCloseoutRunStatusAsync(
            context,
            reconciliationId,
            expectedStatus,
            ct);
    }

    private static StatisticReconciliationFinalVerdictRequest
        BuildCloseoutMismatchedVerdict(
            StatisticReconciliationFinalVerdictRequest matched)
    {
        var classifier = new StatisticReconciliationRootCauseClassifier();
        var root = matched.RootCauseRequest ?? throw new InvalidOperationException(
            "P10_CLOSE_MATCHED_ROOT_CAUSE_MISSING");
        var layers = root.Layers.ToArray();
        layers[1] = classifier.CreateLayerEvidence(
            1,
            StatisticReconciliationRootCauseLayers.Ordered[1],
            matched.ComparisonBindingSha256,
            true,
            HashText("p10-close-mismatched-expected-layer"),
            HashText("p10-close-mismatched-actual-layer"),
            HashText("p10-close-mismatched-delta-manifest"),
            1,
            1,
            0,
            0,
            0,
            StatisticReconciliationRootCauseLayerDeltaStates.Nonzero,
            StatisticReconciliationRootCauseAttributionStates.Proven,
            HashText("p10-close-mismatched-native-attribution"),
            null,
            []);
        var request = matched with
        {
            RootCauseRequest = root with
            {
                Layers = [.. layers]
            }
        };
        var decision = new StatisticReconciliationFinalVerdictEvaluator()
            .Evaluate(request);
        HarnessAssert.True(
            decision.PublicationAllowed &&
            decision.Verdict == StatisticReconciliationFinalVerdicts.Mismatched &&
            decision.RootCauseClass ==
                StatisticReconciliationRootCauseClasses.Projection &&
            !decision.Signable &&
            !decision.CloseoutAllowed &&
            decision.CompleteEvidence &&
            !decision.AllRequiredLayersZero &&
            !decision.MissingOrExtraIdentity &&
            !decision.UnknownBlocksCloseout,
            "P10-CLOSE canonical MISMATCHED request");
        return request;
    }

    private static StatisticReconciliationFinalVerdictRequest
        BuildCloseoutStaleVerdict(
            StatisticReconciliationFinalVerdictRequest matched)
    {
        var binding = new StatisticReconciliationComparisonBindingPins(
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            new string('4', 64),
            new string('5', 64));
        var captured = new StatisticReconciliationActualFreshnessPins(
            binding,
            HashText("p10-close-stale-result-owner"),
            matched.ActualGenerationSha256,
            HashText("p10-close-stale-export-owner"));
        var current = captured with
        {
            ResultOwnerSha256 = HashText(
                "p10-close-stale-result-owner-drift")
        };
        var stale = new StatisticReconciliationFreshnessEvaluator().Evaluate(
            new StatisticReconciliationFreshnessRequest(
                binding,
                captured,
                current,
                true,
                true,
                true));
        HarnessAssert.Equal(
            matched.ComparisonBindingSha256,
            stale.ExpectedBindingSha256,
            "P10-CLOSE STALE expected comparison binding");
        HarnessAssert.True(
            stale.State == StatisticReconciliationFreshnessStates.Stale &&
            stale.ReasonCode ==
                StatisticReconciliationFreshnessReasons.ResultDrift &&
            stale.CompleteEvidence &&
            !stale.MatchAllowed &&
            !stale.MismatchAsDataAllowed &&
            !stale.Signable,
            "P10-CLOSE canonical STALE freshness request");
        var root = matched.RootCauseRequest ?? throw new InvalidOperationException(
            "P10_CLOSE_MATCHED_ROOT_CAUSE_MISSING");
        return matched with
        {
            RootCauseRequest = root with
            {
                Freshness = stale,
                Layers = ImmutableArray<
                    StatisticReconciliationRootCauseLayerEvidence>.Empty
            }
        };
    }
    private static async Task RequireCloseoutRunStatusAsync(
        MongoDbContext context,
        string reconciliationId,
        string expectedStatus,
        CancellationToken ct)
    {
        var row = await context.StatisticReconciliationRuns.Find(value =>
                value.Id == reconciliationId)
            .SingleAsync(ct);
        StatisticReconciliationRunService.RequireActualCaptureReadIntegrity(row);
        HarnessAssert.Equal(
            expectedStatus,
            row.Status,
            $"P10-CLOSE direct Mongo {expectedStatus} postimage");
    }

    private MeResponse CloseoutSystemActor()
    {
        var actor = Actor("admin");
        return new MeResponse(
            actor.Id,
            actor.Username,
            actor.Username,
            ["SYSTEM"],
            actor.UnitId,
            null,
            null,
            null,
            [.. actor.Roles],
            null,
            false,
            actor.AccountKind);
    }

    private async Task<P10CloseoutSuccessorBarrier>
        CaptureCloseoutSuccessorBarrierAsync(
            bool manifestBarrierExact,
            CancellationToken ct)
    {
        const string namespacePattern =
            @"(^|[\/_.-])p(?:11|12)(?=$|[\/_.-])";
        const string p11Pattern = @"(^|[\/_.-])p11(?=$|[\/_.-])";
        const string p12Pattern = @"(^|[\/_.-])p12(?=$|[\/_.-])";
        var options = System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        var collectionNames = await (await RequireDatabase()
                .ListCollectionNamesAsync(cancellationToken: ct))
            .ToListAsync(ct);
        var mongoNamespaces = collectionNames.Where(value =>
                System.Text.RegularExpressions.Regex.IsMatch(
                    value,
                    namespacePattern,
                    options))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var p10ArtifactRoot = CloseoutWorkspacePath(".p10-artifacts");
        var artifactNamespaces = Directory.Exists(p10ArtifactRoot)
            ? Directory.EnumerateFileSystemEntries(
                    p10ArtifactRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Select(value => Path.GetRelativePath(
                        _paths.WorkspaceRoot,
                        value)
                    .Replace('\\', '/'))
                .Where(value => System.Text.RegularExpressions.Regex.IsMatch(
                    value,
                    namespacePattern,
                    options))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray()
            : [];
        var p11RootAbsent = !Directory.Exists(
            CloseoutWorkspacePath(".p11-artifacts"));
        var p12RootAbsent = !Directory.Exists(
            CloseoutWorkspacePath(".p12-artifacts"));
        var blocked = manifestBarrierExact && p11RootAbsent && p12RootAbsent &&
            mongoNamespaces.Length == 0 && artifactNamespaces.Length == 0;
        var result = new P10CloseoutSuccessorBarrier(
            blocked,
            manifestBarrierExact,
            namespacePattern,
            mongoNamespaces,
            artifactNamespaces,
            mongoNamespaces.Count(value =>
                System.Text.RegularExpressions.Regex.IsMatch(
                    value, p11Pattern, options)),
            mongoNamespaces.Count(value =>
                System.Text.RegularExpressions.Regex.IsMatch(
                    value, p12Pattern, options)),
            p11RootAbsent,
            p12RootAbsent,
            mongoNamespaces.Length + artifactNamespaces.Length);
        HarnessAssert.True(result.Blocked,
            "P10-CLOSE exact P11/P12 successor namespace barrier");
        _closeoutSuccessorBarrier = result;
        return result;
    }
}

internal sealed record P10CloseoutProductionStateRow(
    string Status,
    string? ReconciliationId,
    string ScopeAssignmentId,
    string FixturePath,
    long? StateRevision,
    long? ReviewDecisionRevision,
    string? StateHash,
    string? SourceReportId,
    long? SourcePayloadRevision,
    string? SourcePayloadHash,
    long? SourceLifecycleRevision,
    string? SourceLifecycleHash);

internal sealed record P10CloseoutProductionStateRegistry(
    IReadOnlyList<string> ExpectedStatuses,
    IReadOnlyList<P10CloseoutProductionStateRow> Rows,
    int Expected,
    int Passed,
    bool DirectMongoAsserted,
    long EmptyScopeMongoCount,
    string RegistrySha256);


internal sealed record P10CloseoutSuccessorBarrier(
    bool Blocked,
    bool ManifestBarrierExact,
    string NamespacePattern,
    IReadOnlyList<string> MongoNamespaces,
    IReadOnlyList<string> ArtifactNamespaces,
    int P11MongoNamespaceCount,
    int P12MongoNamespaceCount,
    bool P11ArtifactRootAbsent,
    bool P12ArtifactRootAbsent,
    int SuccessorNamespaceCount);
