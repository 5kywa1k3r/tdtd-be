using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MongoDB.Bson;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string OperationsCommandLineSwitch = "--p9-operations-probe";
    private const string OperationsPromptId = "P9-08";
    private const string OperationsGroupId = "P9-OPS";

    private static readonly string[] OperationsRequiredOracles =
    [
        "API_KESTREL",
        "AUTH_BEFORE_EXISTENCE",
        "DIRECT_MONGO",
        "COLLECTION_DELTA",
        "RECEIPT",
        "QUEUE_TRACE",
        "SECURITY_SCAN",
        "CLEANUP"
    ];

    internal static readonly string[] OperationsExpectedCaseIds =
    [
        "P9-OPS-REVERSAL-01", "P9-OPS-REVERSAL-02",
        "P9-OPS-REVERSAL-03", "P9-OPS-REVERSAL-04",
        "P9-OPS-REVERSAL-05", "P9-OPS-REVERSAL-06",
        "P9-OPS-RETRY-01", "P9-OPS-RETRY-02",
        "P9-OPS-RETRY-03", "P9-OPS-RETRY-04",
        "P9-OPS-RETRY-05", "P9-OPS-RETRY-06",
        "P9-OPS-RESET-01", "P9-OPS-RESET-02",
        "P9-OPS-RESET-03", "P9-OPS-RESET-04",
        "P9-OPS-DIAG-01", "P9-OPS-DIAG-02",
        "P9-OPS-DIAG-03", "P9-OPS-DIAG-04",
        "P9-OPS-CLEANUP-01", "P9-OPS-CLEANUP-02"
    ];

    private readonly List<P9OperationsLifecycleTrace> _opsLifecycleTrace = [];
    private readonly List<P9OperationsJobTrace> _opsJobTrace = [];
    private readonly List<P9OperationsAuthTrace> _opsAuthTrace = [];
    private readonly List<P9OperationsReceiptTrace> _opsReceiptTrace = [];
    private readonly List<P9OperationsCleanupTrace> _opsCleanupTrace = [];
    private P9OperationsCandidatePins? _opsCandidatePins;
    private IReadOnlyDictionary<string, P9CollectionState>? _opsApprovedSnapshot;
    private IReadOnlyDictionary<string, P9CollectionState>? _opsReversedSnapshot;
    private IReadOnlyDictionary<string, P9CollectionState>? _opsReapprovedSnapshot;
    private BsonDocument? _opsApprovedJob;
    private BsonDocument? _opsReversalJob;
    private string? _opsApprovalEventKey;
    private string? _opsReversalEventKey;
    private string? _opsFoundationJobId;
    private string? _opsApprovedGenerationRowsHash;
    private JsonObject? _opsRecallRequest;
    private string? _opsWorkerId;
    private string? _opsClaimToken;
    private string? _opsCancelledJobId;
    private string? _opsCurrentLifecycleJobId;
    private string? _opsCleanupReferencedJobId;
    private JsonObject? _opsRetryRequest;
    private JsonObject? _opsResetRequest;
    private JsonObject? _opsCleanupApplyRequest;
    private JsonObject? _opsCleanupPreview;
    private DateTime? _opsCleanupOlderThanUtc;
    private readonly Dictionary<string, string> _opsDirtyMarkerIds =
        new(StringComparer.Ordinal);
    private bool _opsSecurityScanVerified;
    private bool _opsAuthBeforeExistenceVerified;
    private bool _opsReceiptVerified;
    private bool _opsQueueTraceVerified;
    private bool _opsDirectMongoVerified;
    private bool _opsCollectionDeltaVerified;

    public static async Task<int> RunOperationsAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9-OPS probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }

        var runKey =
            $"p908_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, OperationsPromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteOperationsAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteOperationsAsync(CancellationToken ct)
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
                    P9StatRunCandidate = BuildOperationsCandidateOptions()
                });
            _api = new ApiHarnessClient(_backend.BaseUri);

            await BootstrapAndSeedFixtureAsync(ct);
            await PrepareOperationsSingleReportRuntimeFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(ct);
            await PrepareLifecycleFixtureAsync(ct);
            await PrepareFlowContributionVersionsAsync(ct);
            await PrepareOperationsApprovedIncludeFixtureAsync(ct);
            await WriteStrictJsonAsync(
                Path.Combine(_paths.RunRoot, "environment.json"),
                BuildOperationsEnvironmentEvidence(startedAtUtc),
                ct);
            await RunOperationsCasesAsync(ct);
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
        var passed = await WriteOperationsEvidenceAsync(
            startedAtUtc,
            completedAtUtc,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            ct);
        Console.WriteLine(
            passed
                ? $"[DAT] P9-OPS passed 22/22; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9-OPS failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private P9StatRunCandidateOptions BuildOperationsCandidateOptions()
    {
        var pins = LoadOperationsCandidatePins();
        return new P9StatRunCandidateOptions(
            true,
            ChainId,
            RequireMongo().DatabaseName,
            "tdtd_p9_",
            pins.CatalogPath,
            pins.CatalogRawSha256,
            pins.CatalogSemanticSha256,
            pins.SchemaPath,
            pins.SchemaRawSha256,
            pins.SchemaSemanticSha256,
            pins.StageLockPath,
            pins.StageLockSha256);
    }

    private P9OperationsCandidatePins LoadOperationsCandidatePins()
    {
        if (_opsCandidatePins is not null)
            return _opsCandidatePins;
        var root = Path.Combine(
            _paths.WorkspaceRoot,
            ".p9-artifacts",
            "catalog-candidate",
            ChainId,
            OperationsPromptId);
        var catalogPath = Path.GetFullPath(Path.Combine(root, "catalog.json"));
        var schemaPath = Path.GetFullPath(Path.Combine(root, "schema.json"));
        var stageLockPath = Path.GetFullPath(Path.Combine(root, "stage-lock.json"));
        foreach (var path in new[] { catalogPath, schemaPath, stageLockPath })
        {
            if (!File.Exists(path))
                throw new HarnessCaseNotRunnableException(
                    $"P9-08 candidate artifact is missing: {path}");
        }

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var schemaBytes = File.ReadAllBytes(schemaPath);
        var stageBytes = File.ReadAllBytes(stageLockPath);
        var stage = JsonNode.Parse(stageBytes)?.AsObject()
                    ?? throw new InvalidOperationException("P9-08 stage-lock is not JSON object.");
        HarnessAssert.Equal("P9_CANDIDATE_STAGE_V1", RequiredString(stage, "schemaVersion"), "P9-08 stage schema");
        HarnessAssert.Equal(ChainId, RequiredString(stage, "chainId"), "P9-08 stage chain");
        HarnessAssert.Equal(OperationsPromptId, RequiredString(stage, "promptId"), "P9-08 stage prompt");
        HarnessAssert.Equal(6L, RequiredLong(stage, "stage"), "P9-08 stage number");
        HarnessAssert.True(
            stage["promotions"] is JsonArray { Count: 0 },
            "P9-08 must reattest with zero promotions.");

        _opsCandidatePins = new P9OperationsCandidatePins(
            catalogPath,
            HashBytes(catalogBytes),
            CanonicalJsonFileSha256(catalogBytes),
            schemaPath,
            HashBytes(schemaBytes),
            CanonicalJsonFileSha256(schemaBytes),
            stageLockPath,
            HashBytes(stageBytes));
        return _opsCandidatePins;
    }

    private object BuildOperationsEnvironmentEvidence(DateTime startedAtUtc)
    {
        var pins = LoadOperationsCandidatePins();
        return new
        {
            schemaVersion = "P9_OPS_ENVIRONMENT_V1",
            chainId = ChainId,
            promptId = OperationsPromptId,
            groupId = OperationsGroupId,
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
                pins.CatalogRawSha256,
                pins.CatalogSemanticSha256,
                pins.SchemaRawSha256,
                pins.SchemaSemanticSha256,
                pins.StageLockSha256,
                stage = 6,
                promotions = Array.Empty<string>()
            },
            fixture = new
            {
                approvedInclude = true,
                reportId = Fixture().ReportId,
                approvalEventKey = _opsApprovalEventKey,
                runId = _flwRunId,
                generationId = _flwGenerationId
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
}

internal sealed record P9OperationsCandidatePins(
    string CatalogPath,
    string CatalogRawSha256,
    string CatalogSemanticSha256,
    string SchemaPath,
    string SchemaRawSha256,
    string SchemaSemanticSha256,
    string StageLockPath,
    string StageLockSha256);

internal sealed record P9OperationsLifecycleTrace(
    string Step,
    string Operation,
    string EventKey,
    int LifecycleRevision,
    string BeforeSha256,
    string AfterSha256,
    string? RunId,
    string? GenerationId,
    string Detail);

internal sealed record P9OperationsJobTrace(
    string Step,
    string JobId,
    string InternalStatus,
    string ApiStatus,
    long StateRevision,
    int RetryCount,
    string StateHash,
    string? ClaimTokenHash,
    string? DiagnosticCode,
    DateTime ObservedAtUtc);

internal sealed record P9OperationsAuthTrace(
    string CaseId,
    string Route,
    int StatusCode,
    string BeforeSha256,
    string AfterSha256,
    bool AuthBeforeExistence,
    bool ZeroWriteVerified);

internal sealed record P9OperationsReceiptTrace(
    string CaseId,
    string Kind,
    string JobId,
    string CommandId,
    string RequestHash,
    string ReceiptHash,
    bool Replay,
    bool Verified);

internal sealed record P9OperationsCleanupTrace(
    string CaseId,
    string Mode,
    int Selected,
    int Deleted,
    string BeforeSha256,
    string AfterSha256,
    bool CurrentProtected,
    bool Verified);
