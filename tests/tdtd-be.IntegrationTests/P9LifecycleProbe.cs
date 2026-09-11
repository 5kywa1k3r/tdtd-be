using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string LifecycleCommandLineSwitch = "--p9-lifecycle-probe";
    public const string LifecycleCompatibilityCommandLineSwitch =
        "--p9-stat-run-lifecycle-probe";
    // P9-02 remains the immutable artifact lineage for this historical probe.
    // Runtime projection follows the currently published P9 binding.
    private const string LifecyclePromptId = "P9-02";
    private const string LifecycleRuntimePromptId = "P9-12";
    private const string LifecycleGroupId = "P9-LFC";
    private const string LifecycleHistoricalStageLockSha256 =
        "b71bcbdc369ca7b147dc0aff95515b5c7297eb6cc34185177372d9484df5b565";
    private const string LifecycleStageLockSha256 =
        "9c1acc91c5c1d2683da51c074e08bdac264936bf7921c93cb024c1bbe4c0c396";
    private const string LifecycleCatalogRawSha256 =
        "a790be94e4598208de08af39f8782a267ce434db2c20242a811991429932233c";
    private const string LifecycleCatalogSemanticSha256 =
        "39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b";

    private static readonly string[] LifecycleRequiredOracles =
    [
        "API_KESTREL",
        "DIRECT_MONGO",
        "COLLECTION_DELTA",
        "CONFIG_HASH_RECOMPUTE",
        "RECEIPT",
        "QUEUE_TRACE",
        "P8_REGRESSION",
        "P10_ZERO_WRITE"
    ];

    internal static readonly string[] LifecycleExpectedCaseIds =
    [
        "P9-LFC-DRAFT-01", "P9-LFC-DRAFT-02", "P9-LFC-DRAFT-03", "P9-LFC-DRAFT-04",
        "P9-LFC-SUBMIT-01", "P9-LFC-SUBMIT-02", "P9-LFC-SUBMIT-03", "P9-LFC-SUBMIT-04",
        "P9-LFC-APPROVE-01", "P9-LFC-APPROVE-02", "P9-LFC-APPROVE-03", "P9-LFC-APPROVE-04",
        "P9-LFC-EFFECTIVE-01", "P9-LFC-EFFECTIVE-02", "P9-LFC-EFFECTIVE-03", "P9-LFC-EFFECTIVE-04",
        "P9-LFC-OUTBOX-01", "P9-LFC-OUTBOX-02", "P9-LFC-OUTBOX-03", "P9-LFC-OUTBOX-04"
    ];

    private static readonly string[] LifecycleDirectCollections =
    [
        "work_report_field_stat_values",
        "work_report_field_stat_aggregates",
        "work_report_table_stat_values",
        "work_report_table_stat_aggregates",
        "work_report_label_stat_values",
        "work_report_label_stat_aggregates"
    ];

    private const string LifecycleJobCollection =
        "work_report_statistic_rebuild_jobs";
    private const string LifecycleLabelCode = "p9_lfc_label";
    private const string LifecycleBlockId = "p9_lfc_table";
    private const string LifecycleSectionId = "p9-infrastructure-warmup";

    private readonly List<P9LifecycleMilestone> _lfcMilestones = [];
    private readonly List<P9LifecycleNegativeEvidence> _lfcNegativeEvidence = [];
    private readonly List<P9LifecycleQueueEvidence> _lfcQueueTrace = [];
    private P9LifecycleOwnerIndexEvidence? _lfcOwnerIndexEvidence;
    private IReadOnlyDictionary<string, P9CollectionState>? _lfcInitialDirect;
    private IReadOnlyDictionary<string, P9CollectionState>? _lfcPublishedDirect;
    private JsonObject? _lfcSaveRequest;
    private JsonObject? _lfcSubmitRequest;
    private JsonObject? _lfcApproveRequest;
    private string? _lfcApproveEventKey;
    private string? _lfcRunId;
    private string? _lfcGenerationId;
    private string? _lfcGenerationHash;
    private BsonDocument? _lfcPublishedApproveEntry;
    private bool _lfcP8RegressionVerified;
    private bool _lfcP10ZeroWriteVerified;

    public static async Task<int> RunLifecycleAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9-LFC probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }

        var runKey =
            $"p902_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, LifecyclePromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteLifecycleAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteLifecycleAsync(CancellationToken ct)
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
                    P9StatRunCandidate = BuildLifecycleCandidateOptions()
                });
            _api = new ApiHarnessClient(_backend.BaseUri);

            await BootstrapAndSeedFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(ct);
            await PrepareLifecycleFixtureAsync(ct);
            await WriteStrictJsonAsync(
                Path.Combine(_paths.RunRoot, "environment.json"),
                BuildLifecycleEnvironmentEvidence(startedAtUtc),
                ct);
            await RunLifecycleCasesAsync(ct);
            await CaptureLifecycleOwnerIndexEvidenceAsync(ct);
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
        await WriteLifecycleCleanupArtifactAsync(
            cleanupSucceeded,
            cleanupErrors,
            completedAtUtc,
            ct);
        var passed = await WriteLifecycleEvidenceAsync(
            startedAtUtc,
            completedAtUtc,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            ct);

        Console.WriteLine(
            passed
                ? $"[DAT] P9-LFC passed 20/20; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9-LFC failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private P9StatRunCandidateOptions BuildLifecycleCandidateOptions()
    {
        var root = Path.Combine(
            _paths.WorkspaceRoot,
            ".p9-artifacts",
            "catalog-candidate",
            ChainId,
            LifecyclePromptId);
        return new P9StatRunCandidateOptions(
            false,
            ChainId,
            RequireMongo().DatabaseName,
            "tdtd_p9_",
            Path.GetFullPath(Path.Combine(root, "catalog.json")),
            CatalogRawSha256,
            CatalogSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "schema.json")),
            SchemaRawSha256,
            SchemaSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "stage-lock.json")),
            LifecycleHistoricalStageLockSha256);
    }

    private object BuildLifecycleEnvironmentEvidence(DateTime startedAtUtc)
        => new
        {
            schemaVersion = "P9_LFC_ENVIRONMENT_V1",
            chainId = ChainId,
            promptId = LifecyclePromptId,
            groupId = LifecycleGroupId,
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
                catalogRawSha256 = LifecycleCatalogRawSha256,
                catalogSemanticSha256 = LifecycleCatalogSemanticSha256,
                schemaRawSha256 = SchemaRawSha256,
                schemaSemanticSha256 = SchemaSemanticSha256,
                stageLockSha256 = LifecycleStageLockSha256,
                runtimePromptId = LifecycleRuntimePromptId,
                runtimeStage = 9,
                activationMode = "PUBLISHED",
                stage = 0,
                promotions = Array.Empty<string>()
            },
            isolation = new
            {
                ownedReplicaSet = true,
                ownedDatabase = true,
                ownedKestrel = true,
                hangfireServer = false,
                redis = false
            }
        };
}
