using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    public const string CommandLineSwitch = "--p10-reconcile-core-probe";
    internal const string ChainId = "p10_chain_20260810002129_9f56";
    internal const string PromptId = "P10-01";
    internal const string GroupId = "P10-CORE";
    internal const string RunCollection = "work_report_statistic_reconciliations";
    internal const string P9CatalogRawSha256 = "a790be94e4598208de08af39f8782a267ce434db2c20242a811991429932233c";
    internal const string P9CatalogSemanticSha256 = "39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b";
    internal const string P9SchemaRawSha256 = "603304c9798805c972370494d3939bdc7da324939ba9ab98a82800241f1b6940";
    internal const string P9SchemaSemanticSha256 = "da0c80f265845f24aaf282e0a0369272273b1986520dae07171cda85b28b3fed";

    internal static readonly string[] RequirementIds =
    [
        "P10-REC-002", "P10-REC-003", "P10-REC-004",
        "P10-REC-005", "P10-REC-006"
    ];

    internal static readonly string[] ExpectedCaseIds =
    [
        "P10-ACT-01", "P10-ACT-02", "P10-ACT-03", "P10-ACT-04", "P10-ACT-05", "P10-ACT-06",
        "P10-ID-01", "P10-ID-02", "P10-ID-03", "P10-ID-04", "P10-ID-05", "P10-ID-06",
        "P10-AUTH-01", "P10-AUTH-02", "P10-AUTH-03", "P10-AUTH-04", "P10-AUTH-05", "P10-AUTH-06",
        "P10-JOB-01", "P10-JOB-02", "P10-JOB-03", "P10-JOB-04", "P10-JOB-05", "P10-JOB-06"
    ];

    internal static readonly string[] RequiredOracles =
    [
        "API_KESTREL", "AUTH_BEFORE_EXISTENCE", "DIRECT_MONGO", "COLLECTION_DELTA",
        "RECEIPT", "QUEUE_TRACE", "SOURCE_FINGERPRINT", "INDEX_EXPLAIN", "CLEANUP",
        "P9_REGRESSION", "P11_ZERO_WRITE", "P12_ZERO_WRITE", "PROFILE_BLOCK"
    ];

    internal static readonly string[] ImplementationSourcePaths =
    [
        "tdtd-be/Common/Errors/AppErrorCatalog.cs",
        "tdtd-be/Common/Errors/AppErrorCode.cs",
        "tdtd-be/Controllers/StatisticReconciliationController.cs",
        "tdtd-be/Data/Indexes/MongoIndexInitializer.cs",
        "tdtd-be/Data/Infrastructure/MongoOptions.cs",
        "tdtd-be/Data/MongoDbContext.cs",
        "tdtd-be/DTOs/StatisticsReconciliation/StatisticReconciliationRunDtos.cs",
        "tdtd-be/Models/StatisticsReconciliation/StatisticReconciliationRun.cs",
        "tdtd-be/Program.cs",
        "tdtd-be/Services/StatisticsReconciliation/IStatisticReconciliationRunService.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationCanonicalJson.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationCapabilityActivation.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.Helpers.cs",
        "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.Worker.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/ApiHarnessClient.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/BackendServerLease.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/HarnessModels.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/HarnessPaths.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/MongoReplicaSetLease.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/Program.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationCoreProbe.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationCoreProbe.Infrastructure.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationCoreProbe.Fixture.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationCoreProbe.Evidence.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationCoreProbe.Cases.Activation.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationCoreProbe.Cases.Identity.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationCoreProbe.Cases.Authorization.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationCoreProbe.Cases.Jobs.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationCoreProbe.ActualGeneration.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10ReconciliationRaceProbe.Cases.Durable.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P10TrustedFinalizerProbe.cs",
        "tdtd-be/tests/tdtd-be.P10T26Integration/PublicationV7Fixture.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/tdtd-be.IntegrationTests.csproj"
    ];

    private readonly HarnessPaths _paths;
    private readonly string _runKey;
    private readonly string _iterationRoot;
    private readonly HarnessCaseRunner _cases = new();
    private readonly Dictionary<string, P10Actor> _actors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _artifactSecrets = new(StringComparer.Ordinal);
    private readonly List<P10CleanupHandle> _cleanupHandles = [];
    private readonly List<P10CaseEvidence> _caseEvidence = [];
    private readonly List<P10PermissionProof> _permissionProofs = [];
    private readonly List<P10VariantEvidence> _variants = [];

    private MongoReplicaSetLease? _mongo;
    private BackendServerLease? _backend;
    private ApiHarnessClient? _api;
    private IMongoDatabase? _database;
    private P10Fixture? _fixture;
    private P10CandidatePins? _candidate;
    private P10ExecutedAssemblies? _executedAssemblies;
    private P10ImplementationSourceSet? _implementationSourceSet;
    private string? _historicalWorkspaceRoot;
    private string? _historicalContentRoot;
    private string? _bootstrapPassword;
    private P10StoreSnapshot? _protectedBefore;
    private P10StoreSnapshot? _protectedAfter;
    private string? _identityRunId;
    private string? _jobRunId;
    private long _cleanupDryRunBefore;
    private long _cleanupApplied;
    private long _cleanupDryRunAfter;
    private readonly List<object> _activationTrace = [];
    private readonly List<object> _receiptLeaseTrace = [];
    private object? _indexEvidence;
    private string? _authorizationSnapshotSha256;

    private P10ReconciliationCoreProbe(HarnessPaths paths, string runKey)
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
            Console.Error.WriteLine($"P10 probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }
        var runKey = $"p1001_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        return await new P10ReconciliationCoreProbe(
                HarnessPaths.CreateP10(runKey, ChainId, PromptId), runKey)
            .ExecuteAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;
        try
        {
            await PrepareHistoricalContentRootAsync(ct);
            _candidate = await LoadCandidatePinsAsync(ct);
            CaptureRuntimeImplementationPins();
            _mongo = await MongoReplicaSetLease.StartP10Async(_paths, _iterationRoot, _runKey, 1, ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(
                _paths, _iterationRoot, _runKey, _mongo, ct,
                new BackendServerOptions
                {
                    ContentRootPathOverride = HistoricalContentRoot(),
                    P10ReconciliationCandidate = BuildCandidateOptions(),
                    P10ReconciliationMaxRetryCount = 2,
                    P10ReconciliationLeaseSeconds = 15,
                    P10ReconciliationRetryBaseSeconds = 1,
                    P10ReconciliationRetryMaxSeconds = 5,
                    P10ReconciliationSlaSeconds = 30
                });
            _api = new ApiHarnessClient(_backend.BaseUri);
            await WriteStrictJsonAsync(Path.Combine(_paths.RunRoot, "environment.json"), BuildEnvironmentEvidence(startedAtUtc), ct);
            await BootstrapAndSeedFixtureAsync(ct);
            await AwaitInfrastructureAsync(ct);
            _protectedBefore = await CaptureProtectedStoreSnapshotAsync(ct);
            await RunActivationCasesAsync(ct);
            await RunIdentityCasesAsync(ct);
            await RunAuthorizationCasesAsync(ct);
            await RunJobCasesAsync(ct);
            _protectedAfter = await CaptureProtectedStoreSnapshotAsync(ct);
            HarnessAssert.Equal(_protectedBefore.SemanticSha256, _protectedAfter.SemanticSha256,
                "P5-P9/P11/P12 protected stores changed during P10-CORE");
        }
        catch (Exception exception)
        {
            fatalFailure = $"{exception.GetType().Name}: {exception.Message}";
            Console.Error.WriteLine(exception);
        }
        finally
        {
            await ApplyBoundedCleanupAsync(cleanupErrors, ct);
            await StopBackendAsync(cleanupErrors);
            await CleanupMongoAsync(cleanupErrors);
        }

        var cleanupSucceeded = cleanupErrors.Count == 0 &&
            _backend is { StopVerified: true, PortReleaseVerified: true } &&
            _mongo is { DatabaseDropVerified: true, ProcessStopVerified: true, PortReleaseVerified: true, DataDirectoryRemovalVerified: true };
        var completedAtUtc = DateTime.UtcNow;
        await WriteCleanupArtifactAsync(cleanupSucceeded, cleanupErrors, completedAtUtc, ct);
        var passed = await WriteEvidenceAsync(startedAtUtc, completedAtUtc, cleanupSucceeded, cleanupErrors, fatalFailure, ct);
        Console.WriteLine(passed
            ? $"[DAT] P10-CORE passed 24/24; artifacts={_paths.RunRoot}"
            : $"[KHONG_DAT] P10-CORE failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private P10ReconciliationCandidateOptions BuildCandidateOptions()
    {
        var candidate = Candidate();
        return new P10ReconciliationCandidateOptions(
            true, ChainId, RequireMongo().DatabaseName, "tdtd_p10_",
            candidate.CatalogPath, candidate.CatalogRawSha256, candidate.CatalogSemanticSha256,
            candidate.SchemaPath, candidate.SchemaRawSha256, candidate.SchemaSemanticSha256,
            candidate.EvidencePath, candidate.EvidenceSha256,
            candidate.StageLockPath, candidate.StageLockSha256);
    }

    private object BuildEnvironmentEvidence(DateTime startedAtUtc) => new
    {
        schemaVersion = "P10_CORE_ENVIRONMENT_V1", chainId = ChainId, promptId = PromptId,
        groupId = GroupId, runKey = _runKey, startedAtUtc,
        workspaceRoot = _paths.WorkspaceRoot, runRoot = _paths.RunRoot,
        databaseName = RequireMongo().DatabaseName, replicaSetName = RequireMongo().ReplicaSetName,
        mongoProcessId = RequireMongo().ProcessId, mongoPort = RequireMongo().Port,
        backendProcessId = RequireBackend().ProcessId, backendPort = RequireBackend().Port,
        candidate = Candidate(),
        executedAssemblies = ExecutedAssemblies(),
        implementationSourceSet = ImplementationSourceSet(),
        isolation = new { ownedReplicaSet = true, ownedDatabase = true, ownedKestrel = true, hangfireServer = false, redis = false, developerSeedDependency = false }
    };

    private async Task StopBackendAsync(List<string> errors)
    {
        _api?.Dispose();
        if (_backend is null) return;
        try { await _backend.StopAsync(); }
        catch (Exception exception) { errors.Add($"backend-stop: {exception.Message}"); }
        await _backend.DisposeAsync();
    }

    private async Task CleanupMongoAsync(List<string> errors)
    {
        if (_mongo is null) return;
        try { await _mongo.DropDatabaseGuardedAsync(CancellationToken.None); }
        catch (Exception exception) { errors.Add($"database-drop: {exception.Message}"); }
        try { await _mongo.StopProcessAsync(); }
        catch (Exception exception) { errors.Add($"mongo-stop: {exception.Message}"); }
        try { _mongo.RemoveDataDirectoryGuarded(); }
        catch (Exception exception) { errors.Add($"mongo-data-remove: {exception.Message}"); }
        await _mongo.DisposeAsync();
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    private MongoReplicaSetLease RequireMongo() => _mongo ?? throw new HarnessCaseNotRunnableException("P10 Mongo lease is unavailable.");
    private BackendServerLease RequireBackend() => _backend ?? throw new HarnessCaseNotRunnableException("P10 backend lease is unavailable.");
    private ApiHarnessClient RequireApi() => _api ?? throw new HarnessCaseNotRunnableException("P10 API client is unavailable.");
    private IMongoDatabase RequireDatabase() => _database ?? throw new HarnessCaseNotRunnableException("P10 Mongo database is unavailable.");
    private P10Fixture Fixture() => _fixture ?? throw new HarnessCaseNotRunnableException("P10 autonomous fixture is unavailable.");
    private P10CandidatePins Candidate() => _candidate ?? throw new HarnessCaseNotRunnableException("P10 candidate pins are unavailable.");
    private P10Actor Actor(string key) => _actors.TryGetValue(key, out var actor) ? actor : throw new HarnessCaseNotRunnableException($"P10 actor '{key}' is unavailable.");

    internal static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal sealed record P10CandidatePins(
    string StageId, string CatalogPath, string CatalogRawSha256, string CatalogSemanticSha256,
    string SchemaPath, string SchemaRawSha256, string SchemaSemanticSha256,
    string StageLockPath, string StageLockSha256, string GeneratorPath, string GeneratorSha256,
    string EvidencePath, string EvidenceSha256);

internal sealed record P10Actor(string Key, string Id, string Username, string UnitId, string AccountKind, IReadOnlyCollection<string> Roles, string Token);
internal sealed record P10CleanupHandle(string Collection, string Id);
internal sealed record P10Fixture(
    string WorkId, string ScopeAssignmentId, string SiblingAssignmentId, string ReportId,
    string PayloadId, string ReportPeriodId,
    string DynamicFormVersionId, string DynamicFormFamilyId, string P9ResultId, string P9RunId,
    string P9GenerationId, string P9GenerationHash, string SourcePayloadHash, int SourcePayloadRevision,
    int SourceLifecycleRevision, string ConfigId, string ConfigVersionId, long ConfigRevision,
    string ConfigHash, string ConfigBundleHash, string PeriodKey, string PeriodInstanceKey,
    string PeriodKind, string ConceptKey, string Grain, string FilterHash,
    string FlowTemplateId, string FlowTemplateVersionId, string FlowInstanceId,
    string FlowExecutionEpochId, string FlowStepInstanceId, string FlowBranchId,
    string FlowPayloadHash, string P9ExportId,
    string UnitAId, string UnitBId);
internal sealed record P10StoreSnapshot(IReadOnlyDictionary<string, long> Counts, IReadOnlyDictionary<string, string> Hashes, string SemanticSha256);
internal sealed record P10VariantEvidence(string CaseId, string Name, int? StatusCode, int? ProcessId, int? Port, string Verdict, string? Failure);
