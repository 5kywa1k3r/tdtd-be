using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string RaceCommandLineSwitch = "--p9-race-probe";
    private const string RacePromptId = "P9-11";
    private const string RaceGroupId = "P9-RACE";

    private static readonly string[] RaceRequiredOracles =
    [
        "API_KESTREL",
        "AUTH_BEFORE_EXISTENCE",
        "DIRECT_MONGO",
        "COLLECTION_DELTA",
        "CONFIG_HASH_RECOMPUTE",
        "RECEIPT",
        "QUEUE_TRACE",
        "SOURCE_FINGERPRINT",
        "INDEX_EXPLAIN",
        "EXPORT_PARSE",
        "SECURITY_SCAN",
        "CLEANUP",
        "P8_REGRESSION",
        "P10_ZERO_WRITE",
        "PROFILE_BLOCK"
    ];

    internal static readonly string[] RaceExpectedCaseIds =
    [
        "P9-RACE-CAS-01", "P9-RACE-CAS-02", "P9-RACE-CAS-03",
        "P9-RACE-CAS-04", "P9-RACE-CAS-05",
        "P9-RACE-CRASH-01", "P9-RACE-CRASH-02", "P9-RACE-CRASH-03",
        "P9-RACE-CRASH-04", "P9-RACE-CRASH-05",
        "P9-RACE-SECURITY-01", "P9-RACE-SECURITY-02",
        "P9-RACE-SECURITY-03", "P9-RACE-SECURITY-04",
        "P9-RACE-SECURITY-05",
        "P9-RACE-CLEAN-01", "P9-RACE-CLEAN-02", "P9-RACE-CLEAN-03",
        "P9-RACE-CLEAN-04", "P9-RACE-CLEAN-05"
    ];

    private readonly List<ApiExchangeEvidence> _raceApiExchanges = [];
    private readonly List<P9RaceTimelineEntry> _raceTimeline = [];
    private readonly List<P9RaceReceiptEvidence> _raceReceipts = [];
    private readonly List<P9RaceExportEvidence> _raceExports = [];
    private IReadOnlyDictionary<string, P9CollectionState>? _raceFinalSnapshot;
    private P9RaceSourceFingerprint? _raceSourceBefore;
    private P9RaceSourceFingerprint? _raceSourceAfter;
    private P9RaceIndexEvidence? _raceIndexEvidence;
    private P9ExportDownloaded? _raceCsvExport;
    private P9ExportDownloaded? _raceXlsxExport;
    private string? _raceSecurityJobId;
    private string? _raceCrashJobId;
    private string? _raceCrashOldWorker;
    private string? _raceCrashOldToken;
    private string? _raceCrashLiveWorker;
    private string? _raceCrashLiveToken;
    private string? _raceActorPassword;
    private int _raceBackendRestartCount;
    private bool _raceCasVerified;
    private bool _raceCrashVerified;
    private bool _raceAuthVerified;
    private bool _raceConfigHashVerified;
    private bool _raceReceiptVerified;
    private bool _raceQueueVerified;
    private bool _raceSourceVerified;
    private bool _raceIndexVerified;
    private bool _raceExportVerified;
    private bool _raceSecurityBoundaryVerified;
    private bool _raceCleanupVerified;
    private bool _raceP8RegressionVerified;
    private bool _raceP10ZeroWriteVerified;
    private bool _raceProfileBlockVerified;

    public static async Task<int> RunRaceAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9-RACE probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }

        var runKey =
            $"p911_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, RacePromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteRaceAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteRaceAsync(CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;

        try
        {
            _raceSourceBefore = ComputeRaceSourceFingerprint();
            _mongo = await MongoReplicaSetLease.StartP9Async(
                _paths, _iterationRoot, _runKey, 1, ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(
                _paths,
                Path.Combine(_iterationRoot, "race-stage-9"),
                _runKey,
                _mongo,
                ct,
                new BackendServerOptions
                {
                    P9StatRunCandidate = BuildRaceCandidateOptions()
                });
            _raceActorPassword = _backend.ActorPassword;
            RememberSecret(_raceActorPassword);
            _api = new ApiHarnessClient(_backend.BaseUri);

            await BootstrapAndSeedFixtureAsync(ct);
            await PrepareOperationsSingleReportRuntimeFixtureAsync(ct);
            await AwaitDatabaseInfrastructureQuiescenceAsync(ct);
            await PrepareLifecycleFixtureAsync(ct);
            await PrepareFlowContributionVersionsAsync(ct);
            await PrepareOperationsApprovedIncludeFixtureAsync(ct);
            await SetRaceAssignmentOwnerAsync("executor", ct);
            await NormalizeDirectRestartProvenanceAsync(ct);
            await WriteStrictJsonAsync(
                Path.Combine(_paths.RunRoot, "environment.json"),
                BuildRaceEnvironmentEvidence(startedAtUtc),
                ct);
            await RunRaceCasesAsync(ct);

            _raceFinalSnapshot = await CaptureDatabaseSnapshotAsync(ct);
            _raceSourceAfter = ComputeRaceSourceFingerprint();
            _raceSourceVerified = string.Equals(
                _raceSourceBefore.AggregateSha256,
                _raceSourceAfter.AggregateSha256,
                StringComparison.Ordinal);
            HarnessAssert.True(
                _raceSourceVerified,
                "P9-RACE source fingerprint changed during the probe.");
        }
        catch (Exception exception)
        {
            fatalFailure = $"{exception.GetType().Name}: {exception.Message}";
            Console.Error.WriteLine(exception);
        }
        finally
        {
            CaptureRaceApiSegment();
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
        var passed = await WriteRaceEvidenceAsync(
            startedAtUtc,
            completedAtUtc,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            CancellationToken.None);
        Console.WriteLine(
            passed
                ? $"[DAT] P9-RACE passed 20/20; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9-RACE failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private P9StatRunCandidateOptions BuildRaceCandidateOptions()
    {
        var root = Path.Combine(
            _paths.WorkspaceRoot,
            ".p9-artifacts",
            "catalog-candidate",
            ChainId,
            RacePromptId);
        var catalogPath = Path.GetFullPath(Path.Combine(root, "catalog.json"));
        var schemaPath = Path.GetFullPath(Path.Combine(root, "schema.json"));
        var stageLockPath = Path.GetFullPath(Path.Combine(root, "stage-lock.json"));
        foreach (var path in new[] { catalogPath, schemaPath, stageLockPath })
        {
            if (!File.Exists(path))
                throw new HarnessCaseNotRunnableException(
                    $"P9-11 sealed candidate artifact is missing: {path}");
        }

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var schemaBytes = File.ReadAllBytes(schemaPath);
        var stageBytes = File.ReadAllBytes(stageLockPath);
        HarnessAssert.Equal(
            StatRunCapabilityActivation.PublishedCatalogRawSha256,
            HashBytes(catalogBytes),
            "P9-11 sealed catalog raw hash");
        HarnessAssert.Equal(
            StatRunCapabilityActivation.PublishedCatalogSemanticSha256,
            CanonicalJsonFileSha256(catalogBytes),
            "P9-11 sealed catalog semantic hash");
        HarnessAssert.Equal(
            StatRunCapabilityActivation.PublishedSchemaRawSha256,
            HashBytes(schemaBytes),
            "P9-11 sealed schema raw hash");
        HarnessAssert.Equal(
            StatRunCapabilityActivation.PublishedSchemaSemanticSha256,
            CanonicalJsonFileSha256(schemaBytes),
            "P9-11 sealed schema semantic hash");
        HarnessAssert.Equal(
            StatRunCapabilityActivation.PublishedSealStageLockRawSha256,
            HashBytes(stageBytes),
            "P9-11 sealed stage-lock raw hash");

        var stage = JsonNode.Parse(stageBytes)?.AsObject()
                    ?? throw new InvalidOperationException(
                        "P9-11 stage-lock is not a JSON object.");
        HarnessAssert.Equal(
            "P9_CANDIDATE_STAGE_V1",
            RequiredString(stage, "schemaVersion"),
            "P9-11 stage schema");
        HarnessAssert.Equal(ChainId, RequiredString(stage, "chainId"), "P9-11 stage chain");
        HarnessAssert.Equal(RacePromptId, RequiredString(stage, "promptId"), "P9-11 stage prompt");
        HarnessAssert.Equal(9L, RequiredLong(stage, "stage"), "P9-11 stage number");
        HarnessAssert.True(
            stage["promotions"] is JsonArray { Count: 0 },
            "P9-11 seal must not add a promotion at activation time.");
        HarnessAssert.Equal(
            "SEALED",
            RequiredString(stage["seal"], "status"),
            "P9-11 seal status");

        return new P9StatRunCandidateOptions(
            true,
            ChainId,
            RequireMongo().DatabaseName,
            "tdtd_p9_",
            catalogPath,
            StatRunCapabilityActivation.PublishedCatalogRawSha256,
            StatRunCapabilityActivation.PublishedCatalogSemanticSha256,
            schemaPath,
            StatRunCapabilityActivation.PublishedSchemaRawSha256,
            StatRunCapabilityActivation.PublishedSchemaSemanticSha256,
            stageLockPath,
            StatRunCapabilityActivation.PublishedSealStageLockRawSha256);
    }

    private object BuildRaceEnvironmentEvidence(DateTime startedAtUtc)
        => new
        {
            schemaVersion = "P9_RACE_ENVIRONMENT_V1",
            chainId = ChainId,
            promptId = RacePromptId,
            groupId = RaceGroupId,
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
                promptId = RacePromptId,
                stage = 9,
                catalogRawSha256 = StatRunCapabilityActivation.PublishedCatalogRawSha256,
                catalogSemanticSha256 = StatRunCapabilityActivation.PublishedCatalogSemanticSha256,
                schemaRawSha256 = StatRunCapabilityActivation.PublishedSchemaRawSha256,
                schemaSemanticSha256 = StatRunCapabilityActivation.PublishedSchemaSemanticSha256,
                stageLockSha256 = StatRunCapabilityActivation.PublishedSealStageLockRawSha256,
                promotions = Array.Empty<string>(),
                sealedCandidate = true,
                published = false
            },
            isolation = new
            {
                ownedReplicaSet = true,
                ownedDatabase = true,
                ownedKestrel = true,
                sharedSeedDependency = false,
                hangfireServer = false,
                redis = false
            },
            sourceFingerprint = _raceSourceBefore
        };

    private async Task SeedRaceExportFixturesAsync(CancellationToken ct)
    {
        await SeedCanonicalExportFixturesAsync(ct);
        _exportFixtures["RACE_LIMIT"] = _exportFixtures["LIMIT"];
    }

    private async Task<JsonObject> BuildCurrentRaceCreateRequestAsync(
        string commandId,
        CancellationToken ct)
    {
        var request = BuildCreateRequest(commandId);
        var lifecycleJob = await LoadLifecycleJobAsync(ct);
        request["expectedConfigRevision"] = BsonLong(lifecycleJob, "configRevision");
        request["expectedConfigHash"] = BsonString(lifecycleJob, "configHash");
        request["expectedSourceRevision"] = BsonInt(lifecycleJob, "sourcePayloadRevision");
        request["expectedSourceHash"] = BsonString(lifecycleJob, "sourcePayloadHash");
        request["expectedLifecycleRevision"] = BsonInt(lifecycleJob, "sourceLifecycleRevision");
        return request;
    }

    private async Task RestartBackendForRaceAsync(CancellationToken ct)
    {
        CaptureRaceApiSegment();
        _api?.Dispose();
        var stoppedBackend = _backend;
        if (_backend is not null)
        {
            await _backend.StopAsync();
            await _backend.DisposeAsync();
            HarnessAssert.True(
                _backend.StopVerified && _backend.PortReleaseVerified,
                "Crashed P9-RACE Kestrel process did not stop and release its port.");
        }

        _raceBackendRestartCount++;
        _backend = await BackendServerLease.StartAsync(
            _paths,
            Path.Combine(_iterationRoot, $"race-restart-{_raceBackendRestartCount:00}"),
            $"{_runKey}_restart_{_raceBackendRestartCount:00}",
            RequireMongo(),
            ct,
            new BackendServerOptions
            {
                P9StatRunCandidate = BuildRaceCandidateOptions()
            });
        _api = new ApiHarnessClient(_backend.BaseUri);
        foreach (var key in _actors.Keys.ToArray())
        {
            var password = key == "admin"
                ? _bootstrapPassword
                  ?? throw new HarnessCaseNotRunnableException(
                      "P9-RACE bootstrap password is unavailable.")
                : _raceActorPassword
                  ?? throw new HarnessCaseNotRunnableException(
                      "P9-RACE actor password is unavailable.");
            var token = await _api.LoginAsync(_actors[key].Username, password, ct);
            RememberSecret(token);
            _actors[key] = _actors[key] with { Token = token };
        }

        _raceTimeline.Add(new P9RaceTimelineEntry(
            HarnessCaseRunner.ActiveCaseId ?? "SETUP",
            "KESTREL_RESTART",
            [stoppedBackend?.StopVerified == true ? 1 : 0, 1],
            "oldStopped=true;portReleased=true;newReady=true"));
    }

    private void CaptureRaceApiSegment()
    {
        if (_api is null)
            return;
        _raceApiExchanges.AddRange(_api.Exchanges);
    }

    private P9RaceSourceFingerprint ComputeRaceSourceFingerprint()
    {
        var files = new List<string>();
        var serviceRoot = Path.Combine(
            _paths.WorkspaceRoot, "tdtd-be", "Services", "StatisticsRun");
        if (Directory.Exists(serviceRoot))
        {
            files.AddRange(Directory.EnumerateFiles(
                serviceRoot, "*.cs", SearchOption.AllDirectories));
        }
        var candidateRoot = Path.Combine(
            _paths.WorkspaceRoot,
            ".p9-artifacts",
            "catalog-candidate",
            ChainId,
            RacePromptId);
        files.AddRange(new[]
        {
            Path.Combine(_paths.WorkspaceRoot, "tdtd-be", "Controllers", "StatRunController.cs"),
            Path.Combine(_paths.WorkspaceRoot, "tdtd-be", "Controllers", "StatRunOperationsController.cs"),
            Path.Combine(_paths.WorkspaceRoot, "tdtd-be", "Controllers", "StatRunExportsController.cs"),
            Path.Combine(_paths.WorkspaceRoot, "docs", "features", "p9-stat-run", "FULL_P9_STAT_RUN_PROMPT_MANIFEST.json"),
            Path.Combine(candidateRoot, "catalog.json"),
            Path.Combine(candidateRoot, "schema.json"),
            Path.Combine(candidateRoot, "stage-lock.json")
        });
        var entries = files
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new P9RaceFileFingerprint(
                Path.GetRelativePath(_paths.WorkspaceRoot, path)
                    .Replace(Path.DirectorySeparatorChar, '/'),
                HashBytes(File.ReadAllBytes(path))))
            .ToArray();
        var aggregate = HashBytes(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            entries.Select(entry => $"{entry.Path}|{entry.Sha256}"))));
        return new P9RaceSourceFingerprint(entries.Length, aggregate, entries);
    }
}

internal sealed record P9RaceTimelineEntry(
    string CaseId,
    string Operation,
    IReadOnlyList<int> Statuses,
    string Outcome);

internal sealed record P9RaceReceiptEvidence(
    string CaseId,
    string Kind,
    string ReceiptId,
    string RequestHash,
    bool Durable,
    bool ReplayVerified);

internal sealed record P9RaceExportEvidence(
    string CaseId,
    string Format,
    int RowCount,
    int ColumnCount,
    int Bytes,
    string ContentSha256,
    bool Parsed,
    bool InjectionSafe);

internal sealed record P9RaceIndexEvidence(
    string Collection,
    string IndexName,
    IReadOnlyList<string> Stages,
    bool Passed);

internal sealed record P9RaceSourceFingerprint(
    int FileCount,
    string AggregateSha256,
    IReadOnlyList<P9RaceFileFingerprint> Files);

internal sealed record P9RaceFileFingerprint(string Path, string Sha256);
