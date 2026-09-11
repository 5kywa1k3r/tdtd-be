using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Capabilities;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// P10-12 dual-state closeout probe. Catalog mutation is owned by the
/// orchestrator; this process observes either the exact CURRENT-only rollback
/// state or the exact restored state and never writes catalog bytes.
/// </summary>
internal sealed partial class P10ReconciliationRollbackProbe
{
    internal const string CommandLineSwitch =
        "--p10-reconcile-rollback-probe";
    internal const string ChainId = "p10_chain_20260810002129_9f56";
    internal const string PromptId = "P10-12";
    internal const string GroupId = "P10-CLOSE";

    private const string RollbackMode = "rollback";
    private const string RestoredMode = "restored";
    private const string RollbackCurrentRawSha256 =
        "b1ecff835b16316798a27cf9285956b91f2fc16d2c32c2cf27e11e4fb5b2ea26";
    private const string RestoredCurrentRawSha256 =
        "9899ca9a7495e9d903f520d185dee3098b45ad17a7a3da05caa96b8d31a392d9";
    private const string AppendedLockRawSha256 =
        "cec0b6893a109a697e2f7fa31017c1165c47039b15db85247a8eb9729a044ae0";
    private const string PublishedCatalogSemanticSha256 =
        "ccb28afafc068ac1b720c046a25276a35d9d828b14f9cc9c9bc690077ca204c1";
    private const string PublishedSchemaSemanticSha256 =
        "5842baf176bf1eec453b718d07e50da55a417aa9036f4f097ccfbb6fc58c1978";

    private readonly HarnessPaths _paths;
    private readonly string _runKey;
    private readonly string _mode;
    private readonly string _iterationRoot;
    private readonly List<string> _secrets = [];
    private readonly List<P10RollbackLogicalRouteEvidence> _logicalRoutes = [];
    private readonly List<P10RollbackHttpSurfaceEvidence> _httpSurfaces = [];

    private MongoReplicaSetLease? _mongo;
    private BackendServerLease? _backend;
    private ApiHarnessClient? _api;
    private IMongoDatabase? _database;
    private P10ReconciliationCoreProbe? _fixtureBridge;
    private P10Fixture? _fixture;
    private IReadOnlyDictionary<string, P10Actor>? _actors;
    private string? _p9FoundationJobId;

    private P10ReconciliationRollbackProbe(
        HarnessPaths paths,
        string runKey,
        string mode)
    {
        _paths = paths;
        _runKey = runKey;
        _mode = mode;
        _iterationRoot = paths.IterationRoot(1);
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        var mode = (ReadOption(args, "--mode") ?? string.Empty)
            .Trim().ToLowerInvariant();
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal) ||
            mode is not (RollbackMode or RestoredMode) ||
            args.Any(value => !AllowedArgument(value)))
        {
            Console.Error.WriteLine(
                "P10 rollback probe requires --p10-reconcile-rollback-probe " +
                "--mode rollback|restored [--chain-id exact-chain].");
            return 2;
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(4))
            .ToLowerInvariant();
        var runKey =
            $"p1012_{mode}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{nonce}";
        var paths = HarnessPaths.CreateP10(runKey, ChainId, PromptId);
        return await new P10ReconciliationRollbackProbe(paths, runKey, mode)
            .ExecuteAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;
        P10RollbackCatalogState? catalogState = null;
        P10RollbackP9ReadEvidence? p9Read = null;
        P10RollbackInventorySnapshot? globalBefore = null;
        P10RollbackInventorySnapshot? globalAfter = null;
        P10RollbackInventorySnapshot? unknownBefore = null;
        P10RollbackInventorySnapshot? unknownAfter = null;
        P10RollbackFutureBarrierEvidence? futureBarrier = null;
        P10RollbackTrustedSourceFingerprint? sourceBefore = null;
        P10RollbackTrustedSourceFingerprint? sourceAfter = null;
        var cleanupPath = Path.Combine(
            _paths.RunRoot,
            "P10-CLOSE.rollback-cleanup.json");

        try
        {
            sourceBefore = CaptureTrustedSourceFingerprint();
            await EvidenceJson.WriteAsync(
                cleanupPath,
                new
                {
                    schemaVersion = "P10_CLOSE_ROLLBACK_CLEANUP_V1",
                    chainId = ChainId,
                    promptId = PromptId,
                    runKey = _runKey,
                    mode = _mode,
                    state = "ALLOCATING",
                    startedAtUtc
                },
                ct);

            catalogState = ValidateCatalogState();
            _mongo = await MongoReplicaSetLease.StartP10Async(
                _paths, _iterationRoot, _runKey, 1, ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(
                _paths,
                _iterationRoot,
                _runKey,
                _mongo,
                ct,
                new BackendServerOptions
                {
                    EnvironmentNameOverride = "Testing",
                    HangfireServerEnabled = false
                });
            _api = new ApiHarnessClient(_backend.BaseUri);
            _secrets.Add(_backend.BootstrapKey);
            _secrets.Add(_backend.ActorPassword);
            await AdoptAutonomousFixtureAsync(ct);
            await CreateP9PublishedReadFixtureAsync(ct);
            await AwaitInventoryInfrastructureAsync(ct);
            await AwaitRunOwnedHangfireInfrastructureAsync(ct);

            globalBefore = await CaptureInventoryAsync(ct);
            unknownBefore = await CaptureUnknownCollectionInventoryAsync(ct);
            futureBarrier = await ProveFutureNamespacesBlockedAsync(ct);
            await RunLogicalRegistryAsync(ct);
            if (_mode == RollbackMode)
                await RunRollbackHttpSurfacesAsync(ct);
            p9Read = await ProveP9PublishedReadAsync(ct);
            globalAfter = await CaptureInventoryAsync(ct);
            unknownAfter = await CaptureUnknownCollectionInventoryAsync(ct);
            RequireSameInventory(
                "P10-12 global rollback/restored execution",
                globalBefore,
                globalAfter);
        }
        catch (Exception error)
        {
            fatalFailure = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            _api?.Dispose();
            await StopBackendAsync(cleanupErrors);
            await CleanupMongoAsync(cleanupErrors);
        }

        try
        {
            sourceAfter = CaptureTrustedSourceFingerprint();
        }
        catch (Exception error)
        {
            fatalFailure ??= $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }

        var completedAtUtc = DateTime.UtcNow;
        var backendStopped = _backend is
            { StopVerified: true, PortReleaseVerified: true };
        var mongoClean = _mongo is
        {
            DatabaseDropVerified: true,
            ProcessStopVerified: true,
            PortReleaseVerified: true,
            DataDirectoryRemovalVerified: true
        };
        var cleanupSucceeded = cleanupErrors.Count == 0 &&
            backendStopped && mongoClean;
        var ownedProcessCount = cleanupSucceeded ? 0 :
            (_backend is { StopVerified: false } ? 1 : 0) +
            (_mongo is { ProcessStopVerified: false } ? 1 : 0);
        var ownedListenerCount = cleanupSucceeded ? 0 :
            (_backend is { PortReleaseVerified: false } ? 1 : 0) +
            (_mongo is { PortReleaseVerified: false } ? 1 : 0);
        var ownedDatabaseCount = _mongo is { DatabaseDropVerified: true } ? 0 : 1;
        var sourceStable = sourceBefore is not null && sourceAfter is not null &&
            sourceBefore.Sha256 == sourceAfter.Sha256;
        var logicalExact = _logicalRoutes.Count == LogicalRouteRegistry.Length &&
            _logicalRoutes.Select(value => value.RouteId).SequenceEqual(
                LogicalRouteRegistry.Select(value => value.RouteId),
                StringComparer.Ordinal);
        var logicalPassed = logicalExact && _logicalRoutes.All(value =>
            value.Passed &&
            value.BeforeInventorySha256 == value.AfterInventorySha256);
        var httpPassed = _mode == RestoredMode ||
            (_httpSurfaces.Count == RollbackHttpSurfaces.Length &&
             _httpSurfaces.All(value => value.Passed && value.Writes == 0));
        var globalZero = globalBefore is not null && globalAfter is not null &&
            globalBefore.SemanticSha256 == globalAfter.SemanticSha256;
        var unknownCollectionDeltaCount = CountCollectionDeltas(
            unknownBefore,
            unknownAfter);
        var writes = _logicalRoutes.Sum(value => value.Writes) +
                     _httpSurfaces.Sum(value => value.Writes);
        var profileBlocked = futureBarrier?.ProfileBlocked == true;
        var p11P12Blocked = futureBarrier?.Passed == true;
        var p5P9StoresReadOnly = globalZero;
        var p9Available = p9Read is { Passed: true, HttpStatus: 200, Writes: 0 };

        var routeRows = CloseoutRouteRows(_logicalRoutes);
        var storeRows = CloseoutStoreRows(globalBefore, globalAfter);
        var logicalRouteIds = LogicalRouteRegistry
            .Select(value => value.RouteId)
            .ToArray();
        var routeRegistrySha256 = CompactStringArraySha(logicalRouteIds);
        var storeInventorySha256 = CompactStringArraySha(InventoryCollections);

        var matrixPath = Path.Combine(
            _paths.RunRoot,
            "P10-CLOSE.rollback-route-matrix.json");
        await EvidenceJson.WriteAsync(
            matrixPath,
            new
            {
                schemaVersion = "P10_CLOSE_ROLLBACK_ROUTE_MATRIX_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                mode = _mode,
                logicalRouteIds,
                routeRegistrySha256,
                routeProbesExpected = 19,
                routeProbesBlocked = _logicalRoutes.Count(value => value.Blocked),
                routeProbesEnabled = _logicalRoutes.Count(value => value.Enabled),
                routes = routeRows,
                reachableHttpSurfaces = _httpSurfaces,
                writes,
                passed = logicalPassed && httpPassed
            },
            CancellationToken.None);

        var storePath = Path.Combine(
            _paths.RunRoot,
            "P10-CLOSE.rollback-store-inventory.json");
        await EvidenceJson.WriteAsync(
            storePath,
            new
            {
                schemaVersion = "P10_CLOSE_ROLLBACK_STORE_INVENTORY_V1",
                chainId = ChainId,
                promptId = PromptId,
                runKey = _runKey,
                mode = _mode,
                storeCount = InventoryCollections.Length,
                storeInventorySha256,
                stores = storeRows,
                unknownCollectionDeltaCount,
                p5P9StoresReadOnly
            },
            CancellationToken.None);

        var sourcePath = Path.Combine(
            _paths.RunRoot,
            "P10-CLOSE.rollback-source-fingerprint.json");
        await EvidenceJson.WriteAsync(
            sourcePath,
            new
            {
                schemaVersion = "P10_CLOSE_ROLLBACK_SOURCE_FINGERPRINT_V1",
                chainId = ChainId,
                promptId = PromptId,
                runKey = _runKey,
                mode = _mode,
                before = sourceBefore,
                after = sourceAfter,
                stable = sourceStable
            },
            CancellationToken.None);

        await EvidenceJson.WriteAsync(
            cleanupPath,
            new
            {
                schemaVersion = "P10_CLOSE_ROLLBACK_CLEANUP_V1",
                chainId = ChainId,
                promptId = PromptId,
                runKey = _runKey,
                mode = _mode,
                state = cleanupSucceeded ? "CLEANED" : "CLEANUP_FAILED",
                backend = _backend is null ? null : new
                {
                    _backend.ProcessId,
                    _backend.Port,
                    _backend.StopVerified,
                    _backend.PortReleaseVerified
                },
                mongo = _mongo is null ? null : new
                {
                    _mongo.DatabaseName,
                    _mongo.ProcessId,
                    _mongo.Port,
                    _mongo.DatabaseDropVerified,
                    _mongo.ProcessStopVerified,
                    _mongo.PortReleaseVerified,
                    _mongo.DataDirectoryRemovalVerified
                },
                cleanupSucceeded,
                cleanupErrors,
                unknownCollectionDeltaCount,
                ownedProcessCount,
                ownedListenerCount,
                ownedDatabaseCount,
                completedAtUtc
            },
            CancellationToken.None);

        var artifactPins = new[]
        {
            Pin(matrixPath, "P10_ROLLBACK_ROUTE_MATRIX"),
            Pin(storePath, "P10_ROLLBACK_STORE_INVENTORY"),
            Pin(cleanupPath, "P10_ROLLBACK_CLEANUP"),
            Pin(sourcePath, "P10_ROLLBACK_SOURCE_FINGERPRINT")
        };
        var secretsAbsent = ArtifactsExcludeSecrets();
        var rollbackModeContract = _mode == RestoredMode ||
            (_logicalRoutes.Count(value => value.Blocked) == 19 &&
             _logicalRoutes.All(value =>
                 value.EvaluationReason == "CATALOG_STATE_ROLLED_BACK"));
        var passed = fatalFailure is null && catalogState is not null &&
            p9Available && logicalPassed && httpPassed && rollbackModeContract &&
            globalZero && unknownCollectionDeltaCount == 0 && sourceStable &&
            profileBlocked && p11P12Blocked && cleanupSucceeded &&
            ownedProcessCount == 0 && ownedListenerCount == 0 &&
            ownedDatabaseCount == 0 && secretsAbsent;

        var proofName = _mode == RollbackMode
            ? "P10-CLOSE.rollback-proof.json"
            : "P10-CLOSE.restore-proof.json";
        var proofPath = Path.Combine(_paths.RunRoot, proofName);
        await EvidenceJson.WriteAsync(
            proofPath,
            new
            {
                schemaVersion = _mode == RollbackMode
                    ? "P10_CLOSE_ROLLBACK_PROOF_V1"
                    : "P10_CLOSE_RESTORE_PROOF_V1",
                status = passed ? "PASS" : "FAIL",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                mode = _mode,
                startedAtUtc,
                completedAtUtc,
                routeProbesExpected = 19,
                routeProbesBlocked = _logicalRoutes.Count(value => value.Blocked),
                storeCount = InventoryCollections.Length,
                writes,
                currentRawSha256 = catalogState?.CurrentRawSha256,
                lockRawSha256 = catalogState?.LockRawSha256,
                p9Available,
                cleanupSucceeded,
                unknownCollectionDeltaCount,
                logicalRouteIds,
                routes = routeRows,
                routeRegistrySha256,
                stores = storeRows,
                storeInventorySha256,
                sourceFingerprint = new
                {
                    before = sourceBefore,
                    after = sourceAfter,
                    stable = sourceStable
                },
                activation = new
                {
                    p10Blocked = _mode == RollbackMode &&
                        _logicalRoutes.Count(value => value.Blocked) == 19,
                    reason = _mode == RollbackMode
                        ? "CATALOG_STATE_ROLLED_BACK"
                        : "ACTIVE",
                    routeCount = _logicalRoutes.Count
                },
                p9Regression = new
                {
                    available = p9Available,
                    statusCode = p9Read?.HttpStatus,
                    p9Read
                },
                restoreRequired = _mode == RollbackMode,
                profileBlocked,
                p11P12Blocked,
                p5P9StoresReadOnly,
                ownedProcessCount,
                ownedListenerCount,
                ownedDatabaseCount,
                catalogState,
                futureBarrier,
                generatedMetadata = new
                {
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                    DynamicFormFlowCapabilityCatalogMetadata.SchemaSha256
                },
                realInfrastructure = new
                {
                    kestrel = true,
                    mongoReplicaSet = true,
                    directMongoHashOracle = true,
                    backendEnvironment = "Testing"
                },
                artifactPins,
                artifactSecretsAbsent = secretsAbsent,
                cleanupErrors,
                fatalFailure = JsonSerializer.SerializeToElement(fatalFailure)
            },
            CancellationToken.None);

        var proofSha256 = HashFile(proofPath);
        var marker = _mode == RollbackMode
            ? "P10_ROLLBACK_PROOF"
            : "P10_RESTORE_PROOF";
        Console.WriteLine($"{marker}={Path.GetFullPath(proofPath)}");
        Console.WriteLine($"{marker}_SHA256={proofSha256}");
        Console.WriteLine(passed
            ? $"[DAT] P10-12 {_mode} logical=19/19 http={_httpSurfaces.Count} p9=200 stores=57"
            : $"[KHONG_DAT] P10-12 {_mode} failed: {fatalFailure ?? "contract drift"}");
        return passed ? 0 : 1;
    }

    private P10RollbackCatalogState ValidateCatalogState()
    {
        var contractRoot = Path.Combine(
            _paths.BackendRoot,
            "Contracts",
            "DynamicFormFlow");
        var currentPath = Path.Combine(
            contractRoot,
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json");
        var lockPath = Path.Combine(
            contractRoot,
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json");
        var expectedCurrent = _mode == RollbackMode
            ? RollbackCurrentRawSha256
            : RestoredCurrentRawSha256;
        var expectedVersion = _mode == RollbackMode ? "1.6" : "1.7";
        var currentRaw = HashFile(currentPath);
        var lockRaw = HashFile(lockPath);
        HarnessAssert.Equal(expectedCurrent, currentRaw,
            $"P10-12 {_mode} CURRENT raw SHA-256");
        HarnessAssert.Equal(AppendedLockRawSha256, lockRaw,
            $"P10-12 {_mode} append-only LOCK raw SHA-256");
        HarnessAssert.Equal(expectedVersion,
            DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
            $"P10-12 {_mode} generated catalog version");

        using var current = JsonDocument.Parse(File.ReadAllBytes(currentPath));
        HarnessAssert.Equal(expectedVersion,
            current.RootElement.GetProperty("catalogVersion").GetString(),
            $"P10-12 {_mode} CURRENT catalogVersion");
        using var lockDocument = JsonDocument.Parse(File.ReadAllBytes(lockPath));
        var published = lockDocument.RootElement
            .GetProperty("publishedCatalogs");
        HarnessAssert.Equal(8, published.GetArrayLength(),
            "P10-12 append-only LOCK entry count");
        var expectedVersions = new[]
            { "1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7" };
        HarnessAssert.True(
            published.EnumerateArray()
                .Select(value => value.GetProperty("catalogVersion").GetString())
                .SequenceEqual(expectedVersions, StringComparer.Ordinal),
            "P10-12 append-only LOCK version sequence");
        var terminal = published[7];
        HarnessAssert.Equal("DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.json",
            terminal.GetProperty("catalogFile").GetString(),
            "P10-12 LOCK terminal catalog file");
        HarnessAssert.Equal("DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_7.schema.json",
            terminal.GetProperty("schemaFile").GetString(),
            "P10-12 LOCK terminal schema file");
        HarnessAssert.Equal(PublishedCatalogSemanticSha256,
            terminal.GetProperty("catalogSha256").GetString(),
            "P10-12 LOCK terminal catalog SHA-256");
        HarnessAssert.Equal(PublishedSchemaSemanticSha256,
            terminal.GetProperty("schemaSha256").GetString(),
            "P10-12 LOCK terminal schema SHA-256");
        return new(
            _mode,
            expectedVersion,
            RelativeWorkspace(currentPath),
            currentRaw,
            RelativeWorkspace(lockPath),
            lockRaw,
            8,
            _mode == RollbackMode,
            true);
    }

    private async Task AdoptAutonomousFixtureAsync(CancellationToken ct)
    {
        var constructor = typeof(P10ReconciliationCoreProbe).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(HarnessPaths), typeof(string)],
            modifiers: null) ?? throw new InvalidOperationException(
                "P10_ROLLBACK_CORE_CONSTRUCTOR_MISSING");
        _fixtureBridge = (P10ReconciliationCoreProbe)(constructor.Invoke(
            [_paths, _runKey]) ?? throw new InvalidOperationException(
                "P10_ROLLBACK_CORE_CONSTRUCTOR_NULL"));

        static void Set(object target, string name, object value)
        {
            var field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException(
                    "P10_ROLLBACK_CORE_FIELD_MISSING:" + name);
            field.SetValue(target, value);
        }

        Set(_fixtureBridge, "_mongo", Mongo());
        Set(_fixtureBridge, "_backend", Backend());
        Set(_fixtureBridge, "_api", Api());
        Set(_fixtureBridge, "_database", Database());
        var bootstrap = typeof(P10ReconciliationCoreProbe).GetMethod(
            "BootstrapAndSeedFixtureAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "P10_ROLLBACK_CORE_BOOTSTRAP_MISSING");
        try
        {
            var task = bootstrap.Invoke(_fixtureBridge, [ct]) as Task ??
                throw new InvalidOperationException(
                    "P10_ROLLBACK_CORE_BOOTSTRAP_TASK_MISSING");
            await task.ConfigureAwait(false);
        }
        catch (TargetInvocationException error)
            when (error.InnerException is not null)
        {
            throw error.InnerException;
        }

        T Read<T>(string name) => (T)(typeof(P10ReconciliationCoreProbe)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(_fixtureBridge) ?? throw new InvalidOperationException(
                "P10_ROLLBACK_CORE_FIELD_EMPTY:" + name));
        _fixture = Read<P10Fixture>("_fixture");
        _actors = new Dictionary<string, P10Actor>(
            Read<Dictionary<string, P10Actor>>("_actors"),
            StringComparer.Ordinal);
        _secrets.Add(Read<string>("_bootstrapPassword"));
        _secrets.AddRange(_actors.Values.Select(value => value.Token));
    }

    private async Task CreateP9PublishedReadFixtureAsync(CancellationToken ct)
    {
        var fixture = Fixture();
        var flowInstanceUpdate = await Database()
            .GetCollection<BsonDocument>("dynamic_flow_instances")
            .UpdateOneAsync(
                new BsonDocument(
                    "_id",
                    ObjectId.Parse(fixture.FlowInstanceId)),
                new BsonDocument(
                    "$set",
                    new BsonDocument
                    {
                        ["catalogVersion"] =
                            tdtd_be.Common.Capabilities
                                .DynamicFormFlowCapabilityCatalogMetadata
                                .CatalogVersion,
                        ["catalogSemanticHash"] =
                            tdtd_be.Common.Capabilities
                                .DynamicFormFlowCapabilityCatalogMetadata
                                .CatalogSha256
                    }),
                cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            flowInstanceUpdate.MatchedCount,
            "P10-12 P9 foundation fixture flow instance");
        var flowStepUpdate = await Database()
            .GetCollection<BsonDocument>("dynamic_flow_step_instances")
            .UpdateOneAsync(
                new BsonDocument(
                    "_id",
                    ObjectId.Parse(fixture.FlowStepInstanceId)),
                new BsonDocument(
                    "$set",
                    new BsonDocument
                    {
                        ["isCanonicalEpoch"] = true,
                        ["reportLifecycleIsActive"] = true
                    }),
                cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            flowStepUpdate.MatchedCount,
            "P10-12 P9 foundation fixture flow step");
        var response = await Api().PostAsync(
            "api/stat-runs/DIRECT_FIELD_TABLE_LABEL/jobs",
            new JsonObject
            {
                ["commandId"] = $"p10-closeout-{_mode}-p9-foundation-read",
                ["workId"] = fixture.WorkId,
                ["scopeType"] = "ASSIGNMENT",
                ["scopeId"] = fixture.ScopeAssignmentId,
                ["sourceReportId"] = fixture.ReportId,
                ["dynamicFormTemplateId"] = fixture.DynamicFormVersionId,
                ["expectedConfigRevision"] = fixture.ConfigRevision,
                ["expectedConfigHash"] = fixture.ConfigHash,
                ["expectedSourceRevision"] = fixture.SourcePayloadRevision,
                ["expectedSourceHash"] = fixture.SourcePayloadHash,
                ["expectedLifecycleRevision"] = fixture.SourceLifecycleRevision,
                ["period"] = new JsonObject
                {
                    ["periodKey"] = fixture.PeriodKey,
                    ["periodInstanceKey"] = fixture.PeriodInstanceKey,
                    ["periodKind"] = fixture.PeriodKind,
                    ["periodStart"] = new DateTime(
                        2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                    ["periodEnd"] = new DateTime(
                        2026, 8, 31, 23, 59, 59, DateTimeKind.Utc)
                }
            },
            Actor("executor").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Accepted,
            $"P10-12 {_mode} create published P9 foundation read fixture");
        _p9FoundationJobId = ApiHarnessClient.FindStringRecursive(
            response.Json,
            "jobId") ?? throw new InvalidOperationException(
                "P10_ROLLBACK_P9_FOUNDATION_JOB_ID_MISSING");
    }

    private async Task AwaitRunOwnedHangfireInfrastructureAsync(
        CancellationToken ct)
    {
        if (_mode == RestoredMode)
            return;
        var warmup = RollbackHttpSurfaces.Single(value =>
            value.RouteId == StatisticReconciliationRouteRegistry.ReviewSubmit);
        var fakeId = ObjectId.GenerateNewId().ToString();
        var exportId = ObjectId.GenerateNewId().ToString();
        var warmupPath = ResolvePath(warmup.Path, fakeId, exportId);
        var businessBefore = await CaptureInventoryAsync(ct);
        var response = await Api().SendAsync(
            new HttpMethod(warmup.Method),
            warmupPath,
            BuildBody(warmup.Body, 99),
            Actor("admin").Token,
            headers: null,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.Conflict,
            "P10-12 rollback Hangfire infrastructure warmup");
        RequireExactBarrier(
            warmup.RouteId,
            (int)response.StatusCode,
            RequiredRecursive(response.Json, "errorCode", "code"),
            RequiredRecursive(response.Json, "entry"),
            RequiredRecursive(response.Json, "targetPhase"),
            RequiredRecursive(response.Json, "reason"),
            RequiredRecursive(response.Json, "eligibility"),
            RequiredRecursive(response.Json, "freshness"));
        var businessAfter = await CaptureInventoryAsync(ct);
        RequireSameInventory(
            "P10-12 Hangfire infrastructure warmup business stores",
            businessBefore,
            businessAfter);

        var runOwnedKey = new string(_runKey
                .Where(char.IsLetterOrDigit)
                .Take(24)
                .ToArray())
            .ToLowerInvariant();
        HarnessAssert.True(runOwnedKey.Length > 0,
            "P10-12 Hangfire baseline run-owned key");
        var prefix = $"p1hf_{runOwnedKey}";
        var expected = new[]
        {
            ".jobGraph",
            ".locks",
            ".migrationLock",
            ".notifications",
            ".schema",
            ".server",
            ".stateHistory"
        }.Select(value => prefix + value)
         .OrderBy(value => value, StringComparer.Ordinal)
         .ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? previousSha256 = null;
        var stableSamples = 0;
        string[] missing = expected;
        while (DateTime.UtcNow < deadline)
        {
            var current = await CaptureUnknownCollectionInventoryAsync(ct);
            var existing = current.Collections
                .Select(value => value.Collection)
                .ToHashSet(StringComparer.Ordinal);
            missing = expected.Where(value => !existing.Contains(value)).ToArray();
            if (missing.Length == 0)
            {
                stableSamples = string.Equals(
                        current.SemanticSha256,
                        previousSha256,
                        StringComparison.Ordinal)
                    ? stableSamples + 1
                    : 1;
                previousSha256 = current.SemanticSha256;
                if (stableSamples >= 3)
                    return;
            }
            else
            {
                previousSha256 = null;
                stableSamples = 0;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
        throw new InvalidOperationException(
            "P10_ROLLBACK_HANGFIRE_INFRASTRUCTURE_DID_NOT_STABILIZE:" +
            $"missing={string.Join(',', missing)};stableSamples={stableSamples}");
    }

    private async Task<P10RollbackP9ReadEvidence> ProveP9PublishedReadAsync(
        CancellationToken ct)
    {
        var jobId = _p9FoundationJobId ?? throw new InvalidOperationException(
            "P10_ROLLBACK_P9_FOUNDATION_JOB_UNAVAILABLE");
        var before = await CaptureInventoryAsync(ct);
        var response = await Api().GetAsync(
            $"api/stat-runs/jobs/{jobId}",
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P10-12 {_mode} published P9 read");
        var after = await CaptureInventoryAsync(ct);
        RequireSameInventory("P10-12 published P9 read", before, after);
        var responseJobId = ApiHarnessClient.FindStringRecursive(
            response.Json,
            "jobId") ?? ApiHarnessClient.FindStringRecursive(
            response.Json,
            "id");
        HarnessAssert.Equal(jobId, responseJobId,
            "P10-12 published P9 read identity");
        return new(
            _mode,
            "GET",
            $"api/stat-runs/jobs/{jobId}",
            (int)response.StatusCode,
            jobId,
            before.SemanticSha256,
            after.SemanticSha256,
            0,
            true);
    }


    private async Task StopBackendAsync(List<string> errors)
    {
        if (_backend is null) return;
        try { await _backend.StopAsync(); }
        catch (Exception error) { errors.Add("backend-stop:" + error.Message); }
        try { await _backend.DisposeAsync(); }
        catch (Exception error) { errors.Add("backend-dispose:" + error.Message); }
    }

    private async Task CleanupMongoAsync(List<string> errors)
    {
        if (_mongo is null) return;
        try { await _mongo.DropDatabaseGuardedAsync(CancellationToken.None); }
        catch (Exception error) { errors.Add("mongo-drop:" + error.Message); }
        try { await _mongo.StopProcessAsync(); }
        catch (Exception error) { errors.Add("mongo-stop:" + error.Message); }
        try { _mongo.RemoveDataDirectoryGuarded(); }
        catch (Exception error) { errors.Add("mongo-data:" + error.Message); }
        try { await _mongo.DisposeAsync(); }
        catch (Exception error) { errors.Add("mongo-dispose:" + error.Message); }
    }

    private bool ArtifactsExcludeSecrets()
    {
        foreach (var path in Directory.EnumerateFiles(
                     _paths.RunRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (IOException) { continue; }
            var text = Encoding.UTF8.GetString(bytes);
            if (_secrets.Any(secret => !string.IsNullOrWhiteSpace(secret) &&
                                       text.Contains(secret, StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    private static int CountCollectionDeltas(
        P10RollbackInventorySnapshot? before,
        P10RollbackInventorySnapshot? after)
    {
        if (before is null || after is null) return 1;
        var left = before.Collections.ToDictionary(
            value => value.Collection,
            StringComparer.Ordinal);
        var right = after.Collections.ToDictionary(
            value => value.Collection,
            StringComparer.Ordinal);
        return left.Keys.Union(right.Keys, StringComparer.Ordinal).Count(name =>
            !left.TryGetValue(name, out var a) ||
            !right.TryGetValue(name, out var b) ||
            a.Count != b.Count ||
            a.DocumentSetSha256 != b.DocumentSetSha256);
    }

    private static bool AllowedArgument(string value)
        => string.Equals(value, CommandLineSwitch,
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "--mode", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, RollbackMode, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, RestoredMode, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "--chain-id", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, ChainId, StringComparison.Ordinal);

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name,
                    StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }

    private string RelativeWorkspace(string path)
        => Path.GetRelativePath(_paths.WorkspaceRoot, Path.GetFullPath(path))
            .Replace(Path.DirectorySeparatorChar, '/');

    private static string HashFile(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private MongoReplicaSetLease Mongo() => _mongo ??
        throw new InvalidOperationException("P10 rollback Mongo unavailable.");
    private BackendServerLease Backend() => _backend ??
        throw new InvalidOperationException("P10 rollback backend unavailable.");
    private ApiHarnessClient Api() => _api ??
        throw new InvalidOperationException("P10 rollback API unavailable.");
    private IMongoDatabase Database() => _database ??
        throw new InvalidOperationException("P10 rollback database unavailable.");
    private P10Fixture Fixture() => _fixture ??
        throw new InvalidOperationException("P10 rollback fixture unavailable.");
    private P10Actor Actor(string key) =>
        _actors?.TryGetValue(key, out var actor) == true
            ? actor
            : throw new InvalidOperationException(
                "P10 rollback actor unavailable:" + key);
}

internal sealed record P10RollbackCatalogState(
    string Mode,
    string CurrentVersion,
    string CurrentPath,
    string CurrentRawSha256,
    string LockPath,
    string LockRawSha256,
    int LockEntryCount,
    bool CurrentOnlyRollback,
    bool LockAppendOnly);

internal sealed record P10RollbackP9ReadEvidence(
    string Mode,
    string Method,
    string Path,
    int HttpStatus,
    string JobId,
    string BeforeInventorySha256,
    string AfterInventorySha256,
    int Writes,
    bool Passed);

internal sealed class P10RollbackHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "tdtd-be";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } =
        new NullFileProvider();
}
