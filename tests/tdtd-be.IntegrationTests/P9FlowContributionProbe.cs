using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string FlowContributionCommandLineSwitch =
        "--p9-flow-contribution-probe";
    private const string FlowContributionPromptId = "P9-07";
    private const string FlowContributionGroupId = "P9-FLW";
    private const string FlowContributionStageLockSha256 =
        "877412a93932c5bc25f9a8539ec11560f9cd9b28ae093504edf8c7883a4f3786";
    private const string FlowContributionCatalogRawSha256 =
        "b26b24d1bdf9337d85c3ab01c700e357b8a56080f2e808332d1ba612bceb9c68";
    private const string FlowContributionCatalogSemanticSha256 =
        "b4de97a6975b94e4a4da4b7148844ba846af837d283f0e0065762aad10ffdb36";

    private static readonly string[] FlowContributionRequiredOracles =
    [
        "API_KESTREL",
        "DIRECT_MONGO",
        "COLLECTION_DELTA",
        "CONFIG_HASH_RECOMPUTE",
        "RECEIPT",
        "QUEUE_TRACE",
        "SECURITY_SCAN",
        "P10_ZERO_WRITE"
    ];

    internal static readonly string[] FlowContributionExpectedCaseIds =
    [
        "P9-FLW-EXCLUDE-01", "P9-FLW-EXCLUDE-02", "P9-FLW-EXCLUDE-03",
        "P9-FLW-EXCLUDE-04", "P9-FLW-EXCLUDE-05",
        "P9-FLW-INCLUDE-01", "P9-FLW-INCLUDE-02", "P9-FLW-INCLUDE-03",
        "P9-FLW-INCLUDE-04", "P9-FLW-INCLUDE-05",
        "P9-FLW-APPLY-01", "P9-FLW-APPLY-02", "P9-FLW-APPLY-03",
        "P9-FLW-APPLY-04", "P9-FLW-APPLY-05",
        "P9-FLW-REVERSAL-01", "P9-FLW-REVERSAL-02", "P9-FLW-REVERSAL-03",
        "P9-FLW-REVERSAL-04", "P9-FLW-REVERSAL-05"
    ];

    private string? _flwExcludeVersionId;
    private string? _flwIncludeVersionId;
    private string? _flwMappingReceiptId;
    private string? _flwMappingProvenanceId;
    private string? _flwMappingProvenanceHash;
    private string? _flwRunId;
    private string? _flwGenerationId;
    private string? _flwLedgerHash;
    private string? _flwReversalBaselineHash;
    private string? _flwApprovalEventKey;
    private string? _flwMappingBytesBefore;
    private IReadOnlyDictionary<string, P9CollectionState>? _flwExcludedSnapshot;
    private IReadOnlyDictionary<string, P9CollectionState>? _flwIncludedSnapshot;
    private BsonDocument? _flwPublishedJob;
    private bool _flwIndexVerified;
    private bool _flwSecurityScanVerified;
    private bool _flwP10ZeroWriteVerified;

    public static async Task<int> RunFlowContributionAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9-FLW probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }

        var runKey =
            $"p907_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(
            runKey,
            ChainId,
            FlowContributionPromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteFlowContributionAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteFlowContributionAsync(CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;
        try
        {
            _mongo = await MongoReplicaSetLease.StartP9Async(
                _paths,
                _iterationRoot,
                _runKey,
                1,
                ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(
                _paths,
                _iterationRoot,
                _runKey,
                _mongo,
                ct,
                new BackendServerOptions
                {
                    P9StatRunCandidate = BuildFlowContributionCandidateOptions()
                });
            _api = new ApiHarnessClient(_backend.BaseUri);

            await BootstrapAndSeedFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(ct);
            await PrepareLifecycleFixtureAsync(ct);
            await PrepareFlowContributionVersionsAsync(ct);
            await WriteStrictJsonAsync(
                Path.Combine(_paths.RunRoot, "environment.json"),
                BuildFlowContributionEnvironmentEvidence(startedAtUtc),
                ct);
            await RunFlowContributionCasesAsync(ct);
        }
        catch (Exception exception)
        {
            fatalFailure = $"{exception.GetType().Name}: {exception.Message}";
            Console.Error.WriteLine(exception);
        }
        finally
        {
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
        await WriteFlowContributionCleanupArtifactAsync(
            cleanupSucceeded,
            cleanupErrors,
            completedAtUtc,
            ct);
        var passed = await WriteFlowContributionEvidenceAsync(
            startedAtUtc,
            completedAtUtc,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            ct);
        Console.WriteLine(
            passed
                ? $"[DAT] P9-FLW passed 20/20; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9-FLW failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private P9StatRunCandidateOptions BuildFlowContributionCandidateOptions()
    {
        var root = Path.Combine(
            _paths.WorkspaceRoot,
            ".p9-artifacts",
            "catalog-candidate",
            ChainId,
            FlowContributionPromptId);
        return new P9StatRunCandidateOptions(
            true,
            ChainId,
            RequireMongo().DatabaseName,
            "tdtd_p9_",
            Path.GetFullPath(Path.Combine(root, "catalog.json")),
            FlowContributionCatalogRawSha256,
            FlowContributionCatalogSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "schema.json")),
            SchemaRawSha256,
            SchemaSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "stage-lock.json")),
            FlowContributionStageLockSha256);
    }

    private object BuildFlowContributionEnvironmentEvidence(DateTime startedAtUtc)
        => new
        {
            schemaVersion = "P9_FLW_ENVIRONMENT_V1",
            chainId = ChainId,
            promptId = FlowContributionPromptId,
            groupId = FlowContributionGroupId,
            runKey = _runKey,
            startedAtUtc,
            workspaceRoot = _paths.WorkspaceRoot,
            runRoot = _paths.RunRoot,
            databaseName = RequireMongo().DatabaseName,
            replicaSetName = RequireMongo().ReplicaSetName,
            mongoProcessId = RequireMongo().ProcessId,
            mongoPort = RequireMongo().Port,
            backendProcessId = RequireBackend().ProcessId,
            backendPort = RequireBackend().Port,
            candidate = new
            {
                catalogRawSha256 = FlowContributionCatalogRawSha256,
                catalogSemanticSha256 = FlowContributionCatalogSemanticSha256,
                schemaRawSha256 = SchemaRawSha256,
                schemaSemanticSha256 = SchemaSemanticSha256,
                stageLockSha256 = FlowContributionStageLockSha256,
                stage = 5,
                promotions = Array.Empty<string>()
            },
            isolation = new
            {
                ownedReplicaSet = true,
                ownedDatabase = true,
                ownedKestrel = true,
                sharedSeedDependency = false
            }
        };
}
