using System.Security.Cryptography;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    public const string ExportCommandLineSwitch = "--p9-export-probe";
    private const string ExportPromptId = "P9-09";
    private const string ExportGroupId = "P9-EXP";
    private const string ExportCatalogRawSha256 =
        "b26b24d1bdf9337d85c3ab01c700e357b8a56080f2e808332d1ba612bceb9c68";
    private const string ExportCatalogSemanticSha256 =
        "b4de97a6975b94e4a4da4b7148844ba846af837d283f0e0065762aad10ffdb36";
    private const string ExportStageLockSha256 =
        "1098f3729d904f4bf42d60e1d22a5f7e6a7b334b6276c5e7e9d032a290be7718";

    private static readonly string[] ExportRequiredOracles =
    [
        "API_KESTREL", "AUTH_BEFORE_EXISTENCE", "DIRECT_MONGO",
        "CONFIG_HASH_RECOMPUTE", "RECEIPT", "EXPORT_PARSE",
        "SECURITY_SCAN", "CLEANUP"
    ];

    internal static readonly string[] ExportExpectedCaseIds =
    [
        "P9-EXP-CSV-01", "P9-EXP-CSV-02", "P9-EXP-CSV-03",
        "P9-EXP-CSV-04", "P9-EXP-CSV-05", "P9-EXP-CSV-06",
        "P9-EXP-CSV-07",
        "P9-EXP-XLSX-01", "P9-EXP-XLSX-02", "P9-EXP-XLSX-03",
        "P9-EXP-XLSX-04", "P9-EXP-XLSX-05", "P9-EXP-XLSX-06",
        "P9-EXP-XLSX-07",
        "P9-EXP-ACL-01", "P9-EXP-ACL-02", "P9-EXP-ACL-03",
        "P9-EXP-ACL-04"
    ];

    private readonly Dictionary<string, P9ExportCanonicalFixture> _exportFixtures =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, P9ExportReceiptTrace> _exportReceipts =
        new(StringComparer.Ordinal);
    private readonly List<P9ExportParseTrace> _exportParseTrace = [];
    private readonly List<P9ExportAuthTrace> _exportAuthTrace = [];
    private readonly List<P9ExportCleanupTrace> _exportCleanupTrace = [];
    private bool _exportDirectMongoVerified;
    private bool _exportConfigHashVerified;
    private bool _exportReceiptVerified;
    private bool _exportSecurityVerified;
    private bool _exportParseVerified;
    private bool _exportAuthVerified;
    private bool _exportCleanupVerified;

    public static async Task<int> RunExportAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P9-EXP probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }
        var runKey =
            $"p909_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP9(runKey, ChainId, ExportPromptId);
        return await new P9StatRunCoreProbe(paths, runKey)
            .ExecuteExportAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteExportAsync(CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;
        try
        {
            _mongo = await MongoReplicaSetLease.StartP9Async(
                _paths, _iterationRoot, _runKey, 1, ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(
                _paths,
                Path.Combine(_iterationRoot, "projection"),
                $"{_runKey}_projection",
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
            await RunDraftCasesAsync(ct);
            await RunSubmitCasesAsync(ct);
            await RunApproveCasesAsync(ct);
            await CaptureLifecycleOwnerIndexEvidenceAsync(ct);
            await NormalizeDirectRestartProvenanceAsync(ct);
            await RestartBackendForExportAsync(ct);
            await SeedCanonicalExportFixturesAsync(ct);
            await WriteStrictJsonAsync(
                Path.Combine(_paths.RunRoot, "environment.json"),
                BuildExportEnvironmentEvidence(startedAtUtc),
                ct);
            await RunExportCasesAsync(ct);
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
                               _backend is not null && _backend.StopVerified &&
                               _backend.PortReleaseVerified && _mongo is not null &&
                               _mongo.DatabaseDropVerified && _mongo.ProcessStopVerified &&
                               _mongo.PortReleaseVerified && _mongo.DataDirectoryRemovalVerified;
        var completedAtUtc = DateTime.UtcNow;
        var passed = await WriteExportEvidenceAsync(
            startedAtUtc, completedAtUtc, cleanupSucceeded,
            cleanupErrors, fatalFailure, ct);
        Console.WriteLine(
            passed
                ? $"[DAT] P9-EXP passed 18/18; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P9-EXP failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private async Task RestartBackendForExportAsync(CancellationToken ct)
    {
        var actorPassword = RequireBackend().ActorPassword;
        _api?.Dispose();
        if (_backend is not null)
        {
            await _backend.StopAsync();
            await _backend.DisposeAsync();
        }
        _backend = await BackendServerLease.StartAsync(
            _paths,
            Path.Combine(_iterationRoot, "export"),
            $"{_runKey}_export",
            RequireMongo(),
            ct,
            new BackendServerOptions
            {
                P9StatRunCandidate = BuildExportCandidateOptions()
            });
        _api = new ApiHarnessClient(_backend.BaseUri);
        foreach (var key in _actors.Keys.ToArray())
        {
            var password = key == "admin"
                ? _bootstrapPassword ?? throw new InvalidOperationException("Bootstrap password unavailable.")
                : actorPassword;
            var token = await _api.LoginAsync(_actors[key].Username, password, ct);
            RememberSecret(token);
            _actors[key] = _actors[key] with { Token = token };
        }
    }

    private P9StatRunCandidateOptions BuildExportCandidateOptions()
    {
        var root = Path.Combine(
            _paths.WorkspaceRoot, ".p9-artifacts", "catalog-candidate",
            ChainId, ExportPromptId);
        return new P9StatRunCandidateOptions(
            true, ChainId, RequireMongo().DatabaseName, "tdtd_p9_",
            Path.GetFullPath(Path.Combine(root, "catalog.json")),
            ExportCatalogRawSha256, ExportCatalogSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "schema.json")),
            SchemaRawSha256, SchemaSemanticSha256,
            Path.GetFullPath(Path.Combine(root, "stage-lock.json")),
            ExportStageLockSha256);
    }

    private object BuildExportEnvironmentEvidence(DateTime startedAtUtc)
        => new
        {
            schemaVersion = "P9_EXP_ENVIRONMENT_V1",
            chainId = ChainId,
            promptId = ExportPromptId,
            groupId = ExportGroupId,
            runKey = _runKey,
            startedAtUtc,
            workspaceRoot = _paths.WorkspaceRoot,
            runRoot = _paths.RunRoot,
            databaseName = RequireMongo().DatabaseName,
            replicaSetName = RequireMongo().ReplicaSetName,
            backendProcessId = RequireBackend().ProcessId,
            backendPort = RequireBackend().Port,
            candidate = new
            {
                catalogRawSha256 = ExportCatalogRawSha256,
                catalogSemanticSha256 = ExportCatalogSemanticSha256,
                schemaRawSha256 = SchemaRawSha256,
                schemaSemanticSha256 = SchemaSemanticSha256,
                stageLockSha256 = ExportStageLockSha256,
                stage = 7,
                promotions = Array.Empty<string>()
            },
            canonicalKinds = _exportFixtures.Keys.OrderBy(value => value, StringComparer.Ordinal),
            projection = new
            {
                ownerPrompt = LifecyclePromptId,
                runId = _lfcRunId,
                generationId = _lfcGenerationId,
                generationHash = _lfcGenerationHash
            }
        };
}

internal sealed record P9ExportCanonicalFixture(
    string ResultKind, string CapabilityId, string WorkId,
    string ScopeType, string ScopeId, string? PeriodInstanceKey,
    string ResultId, string ResultHash, string ConfigHash,
    string SourceHash, int LifecycleRevision, int ExpectedRows);

internal sealed record P9ExportReceiptTrace(
    string CommandId, string ExportId, string RequestHash,
    string ReceiptId, bool Replay, string ContentHash);

internal sealed record P9ExportParseTrace(
    string CaseId, string Format, string ExportId, int Rows,
    int Columns, string ContentHash, string Detail);

internal sealed record P9ExportAuthTrace(
    string CaseId, string Actor, string Target, int StatusCode,
    long BeforeWrites, long AfterWrites, bool AuthBeforeExistence);

internal sealed record P9ExportCleanupTrace(
    string CaseId, bool DryRun, int Selected, int Deleted,
    long MetadataBefore, long MetadataAfter, int FilesBefore, int FilesAfter);
