using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string CommandLineSwitch = "--p9-stat-run-probe";
    internal const string ChainId = "p9_chain_20260804012144_7185";
    internal const string PromptId = "P9-01";
    private const string GroupId = "P9-CORE";
    private const string CatalogRawSha256 = "3739b331995975b34d84c0b66e43011cf84da5c8181fd2b68b5f76e89ce8dbfa";
    private const string CatalogSemanticSha256 = "0f0b900db1150969496bf42aa143e2527ce65bcecc6d674062eb86f2204d7265";
    private const string SchemaRawSha256 = "603304c9798805c972370494d3939bdc7da324939ba9ab98a82800241f1b6940";
    private const string SchemaSemanticSha256 = "da0c80f265845f24aaf282e0a0369272273b1986520dae07171cda85b28b3fed";
    private const string StageLockSha256 = "273ad1f24be203be3fd18d9f1032d0b1ed780212b9792b533be9d60f44ddf66a";

    private static readonly string[] RequiredOracles =
    [
        "API_KESTREL",
        "AUTH_BEFORE_EXISTENCE",
        "DIRECT_MONGO",
        "CONFIG_HASH_RECOMPUTE",
        "RECEIPT",
        "QUEUE_TRACE",
        "SOURCE_FINGERPRINT",
        "P10_ZERO_WRITE",
        "PROFILE_BLOCK"
    ];

    internal static readonly string[] ExpectedCaseIds =
    [
        "P9-CORE-ACT-01", "P9-CORE-ACT-02", "P9-CORE-ACT-03",
        "P9-CORE-ACT-04", "P9-CORE-ACT-05", "P9-CORE-ACT-06",
        "P9-CORE-ID-01", "P9-CORE-ID-02", "P9-CORE-ID-03",
        "P9-CORE-ID-04", "P9-CORE-ID-05", "P9-CORE-ID-06",
        "P9-CORE-AUTH-01", "P9-CORE-AUTH-02", "P9-CORE-AUTH-03",
        "P9-CORE-AUTH-04", "P9-CORE-AUTH-05", "P9-CORE-AUTH-06",
        "P9-CORE-JOB-01", "P9-CORE-JOB-02", "P9-CORE-JOB-03",
        "P9-CORE-JOB-04", "P9-CORE-JOB-05", "P9-CORE-JOB-06"
    ];

    private readonly HarnessPaths _paths;
    private readonly string _runKey;
    private readonly string _iterationRoot;
    private readonly HarnessCaseRunner _cases = new();
    private readonly List<P9CaseEvidence> _caseEvidence = [];
    private readonly List<P9VariantEvidence> _variantEvidence = [];
    private readonly List<P9LifecycleTraceEntry> _lifecycleTrace = [];
    private readonly List<P9CleanupHandle> _cleanupHandles = [];
    private readonly List<P9FixtureCycleEvidence> _fixtureCycles = [];
    private readonly Dictionary<string, P9Actor> _actors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _artifactSecrets = new(StringComparer.Ordinal);
    private bool _infrastructureWarmupVerified;

    private MongoReplicaSetLease? _mongo;
    private BackendServerLease? _backend;
    private ApiHarnessClient? _api;
    private IMongoDatabase? _database;
    private P9Fixture? _fixture;
    private string? _bootstrapPassword;
    private string? _identityJobId;
    private string? _jobCommandId;
    private JsonObject? _jobRequest;
    private JsonObject? _jobAcceptedResponse;

    private P9StatRunCoreProbe(HarnessPaths paths, string runKey)
    {
        _paths = paths;
        _runKey = runKey;
        _iterationRoot = paths.IterationRoot(1);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9 probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }

        var runKey =
            $"p901_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, PromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteAsync(CancellationToken ct)
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
                    P9StatRunCandidate = BuildCandidateOptions()
                });
            _api = new ApiHarnessClient(_backend.BaseUri);

            await WriteStrictJsonAsync(
                Path.Combine(_paths.RunRoot, "environment.json"),
                BuildEnvironmentEvidence(startedAtUtc),
                ct);
            await BootstrapAndSeedFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(ct);
            await RunActivationCasesAsync(ct);
            await RunIdentityCasesAsync(ct);
            await RunAuthorizationCasesAsync(ct);
            await DrainFoundationJobsAsync("p9-pre-job-drain", ct);
            await RunJobCasesAsync(ct);
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
        await WriteCleanupArtifactAsync(
            cleanupSucceeded,
            cleanupErrors,
            completedAtUtc,
            ct);
        var passed = await WriteEvidenceAsync(
            startedAtUtc,
            completedAtUtc,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            ct);

        Console.WriteLine(
            passed
                ? $"[DAT] P9-CORE passed 24/24; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9-CORE failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private async Task StopBackendAsync(List<string> cleanupErrors)
    {
        _api?.Dispose();
        if (_backend is null)
            return;
        try
        {
            await _backend.StopAsync();
        }
        catch (Exception exception)
        {
            cleanupErrors.Add($"backend-stop: {exception.Message}");
        }
        await _backend.DisposeAsync();
    }

    private async Task CleanupMongoAsync(List<string> cleanupErrors)
    {
        if (_mongo is null)
            return;
        try
        {
            await _mongo.DropDatabaseGuardedAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            cleanupErrors.Add($"database-drop: {exception.Message}");
        }
        try
        {
            await _mongo.StopProcessAsync();
        }
        catch (Exception exception)
        {
            cleanupErrors.Add($"mongo-stop: {exception.Message}");
        }
        try
        {
            _mongo.RemoveDataDirectoryGuarded();
        }
        catch (Exception exception)
        {
            cleanupErrors.Add($"mongo-data-remove: {exception.Message}");
        }
        await _mongo.DisposeAsync();
    }

    private P9StatRunCandidateOptions BuildCandidateOptions()
    {
        var root = Path.Combine(
            _paths.WorkspaceRoot,
            ".p9-artifacts",
            "catalog-candidate",
            ChainId,
            PromptId);
        return new P9StatRunCandidateOptions(
            true,
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
            StageLockSha256);
    }

    private object BuildEnvironmentEvidence(DateTime startedAtUtc)
        => new
        {
            schemaVersion = "P9_CORE_ENVIRONMENT_V1",
            chainId = ChainId,
            promptId = PromptId,
            groupId = GroupId,
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
                catalogRawSha256 = CatalogRawSha256,
                catalogSemanticSha256 = CatalogSemanticSha256,
                schemaRawSha256 = SchemaRawSha256,
                schemaSemanticSha256 = SchemaSemanticSha256,
                stageLockSha256 = StageLockSha256,
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

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }

    private MongoReplicaSetLease RequireMongo()
        => _mongo ?? throw new HarnessCaseNotRunnableException("P9 Mongo lease is unavailable.");

    private BackendServerLease RequireBackend()
        => _backend ?? throw new HarnessCaseNotRunnableException("P9 backend lease is unavailable.");

    private ApiHarnessClient RequireApi()
        => _api ?? throw new HarnessCaseNotRunnableException("P9 API client is unavailable.");

    private IMongoDatabase RequireDatabase()
        => _database ?? throw new HarnessCaseNotRunnableException("P9 Mongo database is unavailable.");

    private P9Fixture Fixture()
        => _fixture ?? throw new HarnessCaseNotRunnableException("P9 autonomous fixture is unavailable.");

    private P9Actor Actor(string key)
        => _actors.TryGetValue(key, out var actor)
            ? actor
            : throw new HarnessCaseNotRunnableException($"P9 actor '{key}' is unavailable.");
}
