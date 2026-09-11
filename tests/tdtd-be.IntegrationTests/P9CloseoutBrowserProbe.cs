using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string CloseoutBrowserCommandLineSwitch =
        "--p9-closeout-browser-probe";
    private const string CloseoutPromptId = "P9-12";
    private const string CloseoutGroupId = "P9-CLOSE";

    internal static readonly string[] CloseoutBrowserCaseIds =
    [
        "P9-CLOSE-BROWSER-01",
        "P9-CLOSE-BROWSER-02",
        "P9-CLOSE-BROWSER-03"
    ];

    private readonly List<P9CloseBrowserSliceEvidence> _closeBrowserSlices = [];
    private readonly List<P9CloseDatabaseTransition> _closeDatabaseTransitions = [];
    private readonly List<string> _closeSecretManifestPaths = [];
    private P9CloseExportParseEvidence? _closeExportParse;
    private P9CloseBoundaryEvidence? _closeBoundary;
    private P9RaceSourceFingerprint? _closeSourceBefore;
    private P9RaceSourceFingerprint? _closeSourceAfter;
    private string? _closeSeededActorPassword;
    private string? _closeQueuedJobId;
    private string? _closeApprovedJobId;
    private string? _closeReversalJobId;
    private string? _closeRebuiltJobId;
    private string? _closeRebuiltFoundationJobId;
    private bool _closeBuildPassed;

    public static async Task<int> RunCloseoutBrowserAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9-CLOSE browser probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }

        var runKey =
            $"p912_browser_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, CloseoutPromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteCloseoutBrowserAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteCloseoutBrowserAsync(CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;

        try
        {
            ValidateCloseoutBrowserStartGate();
            _closeSourceBefore = ComputeRaceSourceFingerprint();

            _mongo = await MongoReplicaSetLease.StartP9Async(
                _paths, _iterationRoot, _runKey, 1, ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(
                _paths,
                Path.Combine(_iterationRoot, "production-v1.6"),
                _runKey,
                _mongo,
                ct,
                new BackendServerOptions
                {
                    EnvironmentNameOverride = "Production",
                    HangfireServerEnabled = true
                });
            _closeSeededActorPassword = _backend.ActorPassword;
            RememberSecret(_closeSeededActorPassword);
            _api = new ApiHarnessClient(_backend.BaseUri);

            await BootstrapAndSeedFixtureAsync(ct);
            await PrepareOperationsSingleReportRuntimeFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(
                ct,
                allowPreinitializedHangfireInfrastructure: true);
            await PrepareLifecycleFixtureAsync(ct);
            await PrepareFlowContributionVersionsAsync(ct);
            await PrepareOperationsApprovedIncludeFixtureAsync(ct);
            await SeedCanonicalExportFixturesAsync(ct);

            await WriteCloseoutEnvironmentAsync(startedAtUtc, ct);
            await BuildCloseoutFrontendAsync(ct);

            await RunCloseoutBrowser03Async(ct);
            await PrepareCloseoutLifecycleBrowser02Async(ct);
            await PrepareCloseoutRebuiltFoundationAsync(ct);
            await CreateCloseoutQueuedJobAsync(
                "p9-close-browser-queued-readonly-001",
                ct);
            await RunCloseoutLifecycleBrowser02Async(ct);
            await RetireCloseoutQueuedJobAsync(ct);
            await RestartCloseoutBackendForResultBrowserAsync(ct);
            await CreateCloseoutQueuedJobAsync(
                "p9-close-browser-queued-before-results-002",
                ct);
            await RunCloseoutResultBrowser01Async(ct);

            _closeBoundary = await RunCloseoutBoundaryProbesAsync(ct);
            _closeSourceAfter = ComputeRaceSourceFingerprint();
            HarnessAssert.Equal(
                _closeSourceBefore.AggregateSha256,
                _closeSourceAfter.AggregateSha256,
                "P9-CLOSE source fingerprint changed during browser execution");
        }
        catch (Exception exception)
        {
            fatalFailure = $"{exception.GetType().Name}: {exception.Message}";
            Console.Error.WriteLine(exception);
        }
        finally
        {
            DeleteCloseoutSecretManifests(cleanupErrors);
            await StopBackendAsync(cleanupErrors);
            await CleanupMongoAsync(cleanupErrors);
        }

        var cleanupSucceeded = cleanupErrors.Count == 0 &&
                               _backend is not null &&
                               _backend.StopVerified &&
                               _backend.PortReleaseVerified &&
                               _mongo is not null &&
                               _mongo.DatabaseDropVerified &&
                               _mongo.ProcessStopVerified &&
                               _mongo.PortReleaseVerified &&
                               _mongo.DataDirectoryRemovalVerified;
        var completedAtUtc = DateTime.UtcNow;
        var closeoutSecurityPassed = CloseoutArtifactsAreSecretFree();
        var passed = fatalFailure is null &&
                      cleanupSucceeded &&
                      _closeBuildPassed &&
                      _closeBrowserSlices.Count == 10 &&
                      CloseoutBrowserCaseIds.All(caseId =>
                          _closeBrowserSlices.Any(item => item.CaseId == caseId)) &&
                      _closeBrowserSlices.All(item => item.Passed) &&
                      _closeBrowserSlices.Sum(item => item.NetworkMockCount) == 0 &&
                      _closeDatabaseTransitions.All(item => item.Passed) &&
                      _closeSecretManifestPaths.All(path => !File.Exists(path)) &&
                      closeoutSecurityPassed &&
                      _closeExportParse?.Passed == true &&
                     _closeBoundary is { Passed: true } &&
                     _closeSourceBefore is not null &&
                     _closeSourceAfter is not null &&
                     string.Equals(
                         _closeSourceBefore.AggregateSha256,
                         _closeSourceAfter.AggregateSha256,
                         StringComparison.Ordinal);

        await WriteCloseoutCleanupAsync(
            cleanupSucceeded,
            cleanupErrors,
            completedAtUtc,
            CancellationToken.None);
        var evidencePath = await WriteCloseoutBrowserEvidenceAsync(
            startedAtUtc,
            completedAtUtc,
            passed,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            CancellationToken.None);

        Console.WriteLine($"P9_CLOSE_BROWSER_EVIDENCE={Path.GetFullPath(evidencePath)}");
        Console.WriteLine(
            passed
                ? $"[DAT] P9-CLOSE browser passed 3/3; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9-CLOSE browser failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private void ValidateCloseoutBrowserStartGate()
    {
        var handoffRoot = Path.Combine(
            _paths.WorkspaceRoot,
            ".p9-artifacts",
            "handoffs",
            ChainId);
        var predecessor = Directory.Exists(handoffRoot)
            ? Directory.GetFiles(handoffRoot, "P9-11.attempt-*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .LastOrDefault()
            : null;
        if (predecessor is null)
            throw new HarnessCaseNotRunnableException(
                "P9-12 requires an immutable successful P9-11 handoff.");
        using (var handoff = JsonDocument.Parse(File.ReadAllBytes(predecessor)))
        {
            var root = handoff.RootElement;
            HarnessAssert.Equal("P9-11", root.GetProperty("promptId").GetString(),
                "P9-CLOSE predecessor prompt");
            HarnessAssert.Equal("PASS", root.GetProperty("status").GetString(),
                "P9-CLOSE predecessor status");
            HarnessAssert.Equal("P9-12", root.GetProperty("nextPrompt").GetString(),
                "P9-CLOSE predecessor next prompt");
        }

        var contractRoot = Path.Combine(
            _paths.BackendRoot,
            "Contracts",
            "DynamicFormFlow");
        RequireCloseoutFileHash(
            Path.Combine(contractRoot,
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json"),
            StatRunCapabilityActivation.PublishedCurrentRawSha256,
            "production CURRENT v1.6");
        RequireCloseoutFileHash(
            Path.Combine(contractRoot,
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json"),
            StatRunCapabilityActivation.PublishedLockRawSha256,
            "append-only production LOCK");
        RequireCloseoutFileHash(
            Path.Combine(contractRoot,
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json"),
            StatRunCapabilityActivation.PublishedCatalogRawSha256,
            "published v1.6 catalog");
        RequireCloseoutFileHash(
            Path.Combine(contractRoot,
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json"),
            StatRunCapabilityActivation.PublishedSchemaRawSha256,
            "published v1.6 schema");
    }

    private static void RequireCloseoutFileHash(
        string path,
        string expectedSha256,
        string context)
    {
        if (!File.Exists(path))
            throw new HarnessCaseNotRunnableException(
                $"P9-CLOSE {context} is missing: {path}");
        HarnessAssert.Equal(
            expectedSha256,
            HashBytes(File.ReadAllBytes(path)),
            $"P9-CLOSE {context} raw SHA-256");
    }

    private async Task WriteCloseoutEnvironmentAsync(
        DateTime startedAtUtc,
        CancellationToken ct)
    {
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "environment.json"),
            new
            {
                schemaVersion = "P9_CLOSE_BROWSER_ENVIRONMENT_V1",
                chainId = ChainId,
                promptId = CloseoutPromptId,
                groupId = CloseoutGroupId,
                runKey = _runKey,
                startedAtUtc,
                production = new
                {
                    environment = "Production",
                    candidateEnabled = false,
                    catalogVersion = StatRunCapabilityActivation.RequiredCatalogVersion,
                    catalogRawSha256 = StatRunCapabilityActivation.PublishedCatalogRawSha256,
                    catalogSemanticSha256 = StatRunCapabilityActivation.PublishedCatalogSemanticSha256,
                    schemaRawSha256 = StatRunCapabilityActivation.PublishedSchemaRawSha256,
                    schemaSemanticSha256 = StatRunCapabilityActivation.PublishedSchemaSemanticSha256,
                    currentRawSha256 = StatRunCapabilityActivation.PublishedCurrentRawSha256,
                    lockRawSha256 = StatRunCapabilityActivation.PublishedLockRawSha256
                },
                kestrel = new
                {
                    processId = RequireBackend().ProcessId,
                    port = RequireBackend().Port,
                    origin = RequireBackend().BaseUri.GetLeftPart(UriPartial.Authority)
                },
                mongo = new
                {
                    processId = RequireMongo().ProcessId,
                    port = RequireMongo().Port,
                    RequireMongo().DatabaseName,
                    RequireMongo().ReplicaSetName
                },
                isolation = new
                {
                    ownedMongoReplicaSet = true,
                    ownedDatabase = true,
                    ownedKestrel = true,
                    sharedSeedDependency = false,
                    redis = false,
                    networkMockEvidence =
                        "derived-from-browser-service-worker-interception-and-proxy-reconciliation"
                }
            },
            ct);
    }
}

internal sealed record P9CloseBrowserSliceEvidence(
    string SliceId,
    string CaseId,
    string ResultPath,
    string ResultSha256,
    string TracePath,
    string TraceSha256,
    string NetworkPath,
    string NetworkSha256,
    int ApiRequestCount,
    int ProxyRequestCount,
    int NetworkMockCount,
    int ScreenshotCount,
    int DownloadCount,
    string BeforeDatabaseSha256,
    string AfterDatabaseSha256,
    bool ZeroWrite,
    bool Passed);

internal sealed record P9CloseDatabaseTransition(
    string Id,
    string Purpose,
    string BeforeSha256,
    string AfterSha256,
    IReadOnlyList<P9CollectionDelta> Deltas,
    bool ExpectedMutation,
    bool Passed);

internal sealed record P9CloseExportParseEvidence(
    string CsvPath,
    string CsvSha256,
    int CsvRows,
    int CsvColumns,
    string XlsxPath,
    string XlsxSha256,
    int XlsxRows,
    int XlsxColumns,
    bool ServerHashesMatch,
    bool ParsedByIndependentReaders,
    bool FormulaSafetyObserved,
    bool Passed);

internal sealed record P9CloseBoundaryEvidence(
    int P8Status,
    string P8SchemaVersion,
    bool P8Regression,
    int P10Status,
    string P10ErrorCode,
    bool P10ZeroWrite,
    int ProfileStatus,
    bool ProfileBlocked,
    bool ProfileZeroWrite,
    string BeforeDatabaseSha256,
    string AfterDatabaseSha256,
    bool Passed);
