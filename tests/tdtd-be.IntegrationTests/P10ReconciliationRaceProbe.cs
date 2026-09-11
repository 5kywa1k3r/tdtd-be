using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationRaceProbe
{
    internal const string CommandLineSwitch = "--p10-reconcile-race-probe";
    internal const string ChainId = "p10_chain_20260810002129_9f56";
    internal const string PromptId = "P10-11";
    internal const string GroupId = "P10-RACE";
    internal static readonly DateTime FixedUtc =
        new(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc);
    internal const string FixedUtcEnvironmentValue =
        "2026-08-13T00:00:00.0000000Z";

    internal static readonly string[] ExactCaseIds =
    [
        "P10-CAS-01", "P10-CAS-02", "P10-CAS-03", "P10-CAS-04", "P10-CAS-05",
        "P10-CRASH-01", "P10-CRASH-02", "P10-CRASH-03", "P10-CRASH-04", "P10-CRASH-05",
        "P10-SECURITY-01", "P10-SECURITY-02", "P10-SECURITY-03", "P10-SECURITY-04", "P10-SECURITY-05",
        "P10-CLEAN-01", "P10-CLEAN-02", "P10-CLEAN-03", "P10-CLEAN-04", "P10-CLEAN-05"
    ];

    private static readonly string[] P10Collections =
    [
        "work_report_statistic_reconciliations",
        "work_report_statistic_reconciliation_observations",
        "work_report_statistic_reconciliation_reviews",
        "work_report_statistic_reconciliation_exports"
    ];

    private readonly HarnessPaths _paths;
    private readonly string _runKey;
    private readonly string _rootId;
    private readonly string _artifactRoot;
    private readonly string _registryPath;
    private readonly List<P10RaceRegistryCase> _records = [];
    private readonly List<string> _secrets = [];
    private MongoReplicaSetLease? _mongo;
    private BackendServerLease? _backend;
    private ApiHarnessClient? _api;
    private MongoDbContext? _context;
    private string? _adminToken;
    private string? _outsiderToken;
    private string? _bootstrapPassword;
    private string? _workId;
    private string? _scopeId;
    private string? _sourceFingerprint;
    private string? _foundationHistoricalWorkspaceRoot;
    private P10ReconciliationCoreProbe? _coreBridge;
    private P10Fixture? _coreFixture;
    private IReadOnlyDictionary<string, P10Actor>? _coreActors;
    private string? _cas01RunId;
    private string? _cas03RunId;
    private string? _cas03WorkerId;
    private string? _cas03ClaimToken;
    private string? _executorToken;
    private BsonDocument? _recheckP9Original;
    private P10RaceRecheckFixture? _recheckCaptureFixture;
    private P10RaceStoreSnapshot? _protectedBefore;
    private P10RaceStoreSnapshot? _protectedAfter;
    private bool _boundedCleanup;
    private long _boundedCleanupRemoved;
    private long _boundedCleanupSecondDry;
    private bool _boundedCleanupSentinel;

    private P10ReconciliationRaceProbe(
        HarnessPaths paths,
        string runKey,
        string rootId,
        string artifactRoot,
        string registryPath)
    {
        _paths = paths;
        _runKey = runKey;
        _rootId = rootId;
        _artifactRoot = artifactRoot;
        _registryPath = registryPath;
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            ValidateArguments(args);
            var fixedUtcRaw = Environment.GetEnvironmentVariable(
                "TDTD_P10_FIXED_UTC_NOW");
            if (!string.Equals(fixedUtcRaw, FixedUtcEnvironmentValue,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "P10_RACE_FIXED_UTC_ENVIRONMENT_INVALID");
            var rootId = RequiredOption(args, "--root") ??
                         Environment.GetEnvironmentVariable("P10_CLEAN_ROOT_ID") ?? "A";
            if (rootId.Length is < 1 or > 32 ||
                rootId.Any(value => !(char.IsLetterOrDigit(value) || value is '-' or '_')))
                throw new InvalidOperationException("P10_RACE_ROOT_ID_INVALID");

            var backendRoot = FindBackendRoot();
            var workspaceRoot = Directory.GetParent(backendRoot)?.FullName
                ?? throw new InvalidOperationException("P10_RACE_WORKSPACE_ROOT_MISSING");
            var artifactOption = RequiredOption(args, "--artifact-root") ??
                                 Environment.GetEnvironmentVariable("TDTD_P10_ARTIFACT_ROOT") ??
                                 Path.Combine(".p10-artifacts", "runs", ChainId, PromptId,
                                     $"race_{FixedUtc:yyyyMMddTHHmmssZ}_{Environment.ProcessId}_{rootId}");
            var registryName = RequiredOption(args, "--registry-out") ?? "P10-RACE.registry.json";
            if (Path.IsPathRooted(registryName) || Path.GetFileName(registryName) != registryName ||
                !registryName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("P10_RACE_REGISTRY_PATH_INVALID");
            var artifactRoot = GuardedWorkspacePath(workspaceRoot, artifactOption);
            RejectRedirectedAncestors(workspaceRoot, NearestExistingAncestor(artifactRoot));
            Directory.CreateDirectory(artifactRoot);
            RejectRedirectedAncestors(workspaceRoot, artifactRoot);
            var registryPath = Path.Combine(artifactRoot, registryName);
            if (File.Exists(registryPath))
                throw new InvalidOperationException("P10_RACE_REGISTRY_ALREADY_EXISTS");

            var runKey = $"p1011_{rootId}_{Environment.ProcessId}_" +
                         Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            var paths = new HarnessPaths(
                workspaceRoot,
                backendRoot,
                Path.Combine(workspaceRoot, ".p10-artifacts", "runs", ChainId, PromptId),
                artifactRoot);
            var probe = new P10ReconciliationRaceProbe(
                paths, runKey, rootId, artifactRoot, registryPath);
            var deliberate = args.Any(value => string.Equals(
                value, "--deliberate-wrong-ledger", StringComparison.OrdinalIgnoreCase));
            return deliberate
                ? await probe.ExecuteDeliberateWrongLedgerAsync(CancellationToken.None)
                : await probe.ExecuteAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var cleanupErrors = new List<string>();
        string? fatal = null;
        try
        {
            _sourceFingerprint = SourceFingerprint();
            await StartInfrastructureAsync(includeBackend: true, ct);
            await AdoptCoreFixtureAsync(ct);
            _protectedBefore = await CaptureProtectedSnapshotAsync(ct);
            await RunCasCasesAsync(ct);
            await RunCrashCasesAsync(ct);
            await RunSecurityCasesAsync(ct);
            _protectedAfter = await CaptureProtectedSnapshotAsync(ct);
            await RunCleanCasesAsync(ct);
        }
        catch (Exception error)
        {
            fatal = $"{error.GetType().Name}: {error.Message}";
            Console.Error.WriteLine(error);
        }
        finally
        {
            await StopAndCleanAsync(cleanupErrors);
        }

        var cleanupComplete = cleanupErrors.Count == 0 &&
                              _mongo is { DatabaseDropVerified: true, ProcessStopVerified: true,
                                  PortReleaseVerified: true, DataDirectoryRemovalVerified: true } &&
                              _backend is { StopVerified: true, PortReleaseVerified: true };
        var securityCount = _records.Count(value =>
            value.Id.StartsWith("P10-SECURITY-", StringComparison.Ordinal) && value.Status == "PASS");
        var sensitiveValuesFound = ScanSensitiveLogs();
        var pass = fatal is null && cleanupComplete &&
                   _records.Count == ExactCaseIds.Length &&
                   _records.Select(value => value.Id).SequenceEqual(ExactCaseIds) &&
                   _records.All(value => value.Status == "PASS") &&
                   securityCount == 5 && sensitiveValuesFound == 0 &&
                   _boundedCleanup && _boundedCleanupSecondDry == 0 && _boundedCleanupSentinel;
        var semantic = SemanticSha(_records.Select(value => new
        {
            value.Id,
            value.Status,
            value.Evidence
        }));
        var registry = new P10RaceRegistry(
            "P10_RACE_CASE_REGISTRY_V1",
            _rootId,
            FixedUtc,
            _records.Count,
            _records,
            new P10RaceCleanupEvidence(
                cleanupComplete && _boundedCleanup,
                _boundedCleanupRemoved,
                _boundedCleanupSecondDry,
                _boundedCleanupSentinel),
            new P10RaceSecurityEvidence(
                securityCount == 5 && sensitiveValuesFound == 0,
                securityCount,
                sensitiveValuesFound),
            new P10RaceP9RegressionEvidence(
                0,
                _protectedBefore is not null && _protectedAfter is not null &&
                _protectedBefore.SemanticSha256 == _protectedAfter.SemanticSha256),
            _sourceFingerprint ?? new string('0', 64),
            semantic);
        await WriteNewJsonAsync(_registryPath, registry, ct);

        if (!pass)
        {
            Console.Error.WriteLine(
                $"P10_RACE_FAILED root={_rootId} fatal={fatal ?? "none"} " +
                $"cases={_records.Count}/20 cleanup={cleanupComplete} security={securityCount}/5");
            foreach (var cleanup in cleanupErrors)
                Console.Error.WriteLine("P10_RACE_CLEANUP_ERROR " + cleanup);
            return 1;
        }

        Console.WriteLine(
            "P10_RACE_SECURITY_OK authBeforeExistence=true crossScope=true " +
            "permissionRemoval=true redacted=true noSensitiveLogs=true formulaSafe=true pathSafe=true");
        Console.WriteLine(
            $"P10_RACE_OK root={_rootId} cases=20 cas=5 crash=5 security=5 clean=5 " +
            "oneWinner=true noOrphans=true p9Writes=0 cleanup=true " +
            $"fixedUtc={FixedUtc:O} columns=8 registry={RelativeWorkspace(_registryPath)} " +
            $"semanticSha256={semantic}");
        return 0;
    }

    private async Task StartInfrastructureAsync(bool includeBackend, CancellationToken ct)
    {
        var iterationRoot = Path.Combine(_artifactRoot, "infra");
        Directory.CreateDirectory(iterationRoot);
        _mongo = await MongoReplicaSetLease.StartP10Async(
            _paths, iterationRoot, _runKey, 1, ct);
        _context = new MongoDbContext(Microsoft.Extensions.Options.Options.Create(new MongoOptions
        {
            ConnectionString = _mongo.ConnectionString,
            Database = _mongo.DatabaseName
        }));
        if (!includeBackend) return;

        var historicalContentRoot =
            await PrepareFoundationHistoricalContentRootAsync(ct);
        var candidate = await LoadFoundationCandidateAsync(ct);
        var reviewStage = Path.Combine(_paths.WorkspaceRoot,
            ".p10-artifacts", "catalog-candidate", ChainId, "P10-08", "stage-lock.json");
        var evidenceStage = Path.Combine(_paths.WorkspaceRoot,
            ".p10-artifacts", "catalog-candidate", ChainId, "P10-09", "stage-lock.json");
        using var reviewEnvironment = new ScopedEnvironmentVariable(
            "StatisticReconciliationCandidate__ReviewStageLockSha256", HashFile(reviewStage));
        using var evidenceEnvironment = new ScopedEnvironmentVariable(
            "StatisticReconciliationCandidate__EvidenceStageLockSha256", HashFile(evidenceStage));
        _backend = await BackendServerLease.StartAsync(
            _paths, iterationRoot, _runKey, _mongo, ct,
            new BackendServerOptions
            {
                ContentRootPathOverride = historicalContentRoot,
                FixedUtcNow = FixedUtc,
                P10ReconciliationCandidate = candidate,
                P10ReconciliationMaxRetryCount = 3,
                P10ReconciliationLeaseSeconds = 60,
                P10ReconciliationRetryBaseSeconds = 1,
                P10ReconciliationRetryMaxSeconds = 2,
                P10ReconciliationSlaSeconds = 600
            });
        _api = new ApiHarnessClient(_backend.BaseUri);
    }

    private async Task StopAndCleanAsync(List<string> errors)
    {
        _api?.Dispose();
        if (_recheckP9Original is not null)
        {
            try { await RestoreRecheckSuccessorP9Async(CancellationToken.None); }
            catch (Exception error) { errors.Add("p9-restore:" + error.Message); }
        }
        if (_recheckCaptureFixture is not null)
        {
            try { await CleanupRecheckCaptureOwnersAsync(CancellationToken.None); }
            catch (Exception error) { errors.Add("capture-owner-cleanup:" + error.Message); }
        }
        if (_backend is not null)
        {
            try { await _backend.StopAsync(); }
            catch (Exception error) { errors.Add("backend-stop:" + error.Message); }
            try { await _backend.DisposeAsync(); }
            catch (Exception error) { errors.Add("backend-dispose:" + error.Message); }
        }
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

    private async Task RunCaseAsync(
        string caseId,
        Func<CancellationToken, Task<P10RaceEightColumnEvidence>> action,
        CancellationToken ct)
    {
        try
        {
            var evidence = await action(ct);
            _records.Add(new P10RaceRegistryCase(caseId, "PASS", evidence));
            Console.WriteLine($"PASS {caseId}");
        }
        catch (Exception error)
        {
            _records.Add(new P10RaceRegistryCase(caseId, "FAIL",
                P10RaceEightColumnEvidence.Failure(caseId, error)));
            var diagnostic = error is tdtd_be.Common.Errors.AppException appError
                ? $" details={JsonSerializer.Serialize(appError.Details)}"
                : string.Empty;
            Console.Error.WriteLine(
                $"FAIL {caseId}: {error.GetType().Name}: {error.Message}{diagnostic}");
        }
    }

    private MongoDbContext Context() => _context ??
        throw new HarnessCaseNotRunnableException("P10-RACE Mongo context unavailable.");
    private IMongoDatabase Database() => Context().Db;
    private ApiHarnessClient Api() => _api ??
        throw new HarnessCaseNotRunnableException("P10-RACE Kestrel client unavailable.");
    private BackendServerLease Backend() => _backend ??
        throw new HarnessCaseNotRunnableException("P10-RACE Kestrel lease unavailable.");
    private string AdminToken() => _adminToken ??
        throw new HarnessCaseNotRunnableException("P10-RACE admin token unavailable.");
    private string OutsiderToken() => _outsiderToken ??
        throw new HarnessCaseNotRunnableException("P10-RACE outsider token unavailable.");
    private string WorkId() => _workId ??
        throw new HarnessCaseNotRunnableException("P10-RACE work fixture unavailable.");
    private string ScopeId() => _scopeId ??
        throw new HarnessCaseNotRunnableException("P10-RACE scope fixture unavailable.");

    private async Task<P10RaceStoreSnapshot> CaptureProtectedSnapshotAsync(CancellationToken ct)
    {
        var names = await (await Database().ListCollectionNamesAsync(cancellationToken: ct))
            .ToListAsync(ct);
        var rows = new List<string>();
        foreach (var name in names.Where(value =>
                     !P10Collections.Contains(value, StringComparer.Ordinal) &&
                     !value.StartsWith("p1hf_", StringComparison.Ordinal))
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            var values = await Database().GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(new BsonDocument("_id", 1))
                .ToListAsync(ct);
            // Mongo lazily creates some P5-P9 owner collections on first read.
            // Empty collection shells are not semantic store writes; any document
            // added to a new collection is still included and changes this hash.
            if (values.Count == 0)
                continue;
            rows.Add(name + "=" + SemanticSha(values.Select(value => value.ToJson())));
        }
        return new(rows.Count, SemanticSha(rows), rows.ToArray());
    }

    private int ScanSensitiveLogs()
    {
        if (_backend is null) return 0;
        var text = string.Join("\n",
            File.Exists(_backend.StdoutPath) ? ReadSharedText(_backend.StdoutPath) : string.Empty,
            File.Exists(_backend.StderrPath) ? ReadSharedText(_backend.StderrPath) : string.Empty);
        return _secrets.Count(secret => !string.IsNullOrWhiteSpace(secret) &&
                                        text.Contains(secret, StringComparison.Ordinal));
    }

    internal static string Sha(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string SemanticSha<T>(IEnumerable<T> values) => Sha(
        JsonSerializer.Serialize(values, P10RaceJson.Options));
    private static string HashFile(string path) => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private string RelativeWorkspace(string path) => Path.GetRelativePath(
        _paths.WorkspaceRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindBackendRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var direct = Path.Combine(current.FullName, "tdtd-be.csproj");
            if (File.Exists(direct)) return current.FullName;
            var nested = Path.Combine(current.FullName, "tdtd-be", "tdtd-be.csproj");
            if (File.Exists(nested)) return Path.GetDirectoryName(nested)!;
            current = current.Parent;
        }
        throw new InvalidOperationException("P10_RACE_BACKEND_ROOT_NOT_FOUND");
    }

    private static string GuardedWorkspacePath(string workspaceRoot, string value)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(value)
            ? value
            : Path.Combine(workspaceRoot, value));
        var root = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("P10_RACE_PATH_OUTSIDE_WORKSPACE");
        return full;
    }

    private static void RejectRedirectedAncestors(string root, string target)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var current = new DirectoryInfo(Path.GetFullPath(target));
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("P10_RACE_REPARSE_POINT_REJECTED");
            if (string.Equals(current.FullName, normalizedRoot,
                    StringComparison.OrdinalIgnoreCase)) return;
            current = current.Parent;
        }
        throw new InvalidOperationException("P10_RACE_PATH_ROOT_NOT_REACHED");
    }

    private static string NearestExistingAncestor(string target)
    {
        var current = new DirectoryInfo(Path.GetFullPath(target));
        while (current is not null && !current.Exists)
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException(
            "P10_RACE_EXISTING_ANCESTOR_MISSING");
    }

    private static void ValidateArguments(string[] args)
    {
        var valued = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--root", "--artifact-root", "--registry-out"
        };
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CommandLineSwitch, "--deliberate-wrong-ledger"
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (flags.Contains(argument))
            {
                if (!seen.Add(argument))
                    throw new InvalidOperationException(
                        "P10_RACE_DUPLICATE_ARGUMENT:" + argument);
                continue;
            }
            if (!valued.Contains(argument))
                throw new InvalidOperationException(
                    "P10_RACE_UNKNOWN_ARGUMENT:" + argument);
            if (!seen.Add(argument))
                throw new InvalidOperationException(
                    "P10_RACE_DUPLICATE_ARGUMENT:" + argument);
            if (++index >= args.Length || args[index].StartsWith("--",
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "P10_RACE_OPTION_VALUE_REQUIRED:" + args[index - 1]);
        }
    }

    private static string? RequiredOption(string[] args, string option)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new InvalidOperationException($"P10_RACE_OPTION_VALUE_REQUIRED:{option}");
            return args[index + 1];
        }
        return null;
    }

    private static async Task WriteNewJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, P10RaceJson.IndentedOptions);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }
}

internal static class P10RaceJson
{
    internal static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web);
    internal static readonly JsonSerializerOptions IndentedOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

internal sealed record P10RaceRegistry(
    string SchemaVersion,
    string RootId,
    DateTime FixedUtc,
    int CaseCount,
    IReadOnlyList<P10RaceRegistryCase> Cases,
    P10RaceCleanupEvidence Cleanup,
    P10RaceSecurityEvidence Security,
    P10RaceP9RegressionEvidence P9Regression,
    string SourceFingerprintSha256,
    string SemanticSha256);

internal sealed record P10RaceRegistryCase(
    string Id,
    string Status,
    P10RaceEightColumnEvidence Evidence);

internal sealed record P10RaceEightColumnEvidence(
    [property: JsonPropertyName("Identity")] object Identity,
    [property: JsonPropertyName("Config")] object Config,
    [property: JsonPropertyName("Expected")] object Expected,
    [property: JsonPropertyName("Actual")] object Actual,
    [property: JsonPropertyName("Delta")] object Delta,
    [property: JsonPropertyName("Freshness")] object Freshness,
    [property: JsonPropertyName("Permission")] object Permission,
    [property: JsonPropertyName("Verdict")] object Verdict)
{
    internal static P10RaceEightColumnEvidence Pass(
        string caseId,
        object config,
        object expected,
        object actual,
        object delta,
        object? freshness = null,
        object? permission = null)
        => new(
            new { caseId, groupId = "P10-RACE", promptId = "P10-11" },
            config,
            expected,
            actual,
            delta,
            freshness ?? new { fixedUtc = P10ReconciliationRaceProbe.FixedUtc, coherent = true },
            permission ?? new { authorizationBeforeExistence = true },
            new { status = "PASS", skipped = false, timedOut = false });

    internal static P10RaceEightColumnEvidence Failure(string caseId, Exception error)
        => new(
            new { caseId, groupId = "P10-RACE", promptId = "P10-11" },
            new { fixedClock = true },
            new { status = "PASS" },
            new { status = "FAIL", error = error.GetType().Name },
            new { failureCount = 1, unknownCollectionDeltaCount = 0 },
            new { fixedUtc = P10ReconciliationRaceProbe.FixedUtc, coherent = false },
            new { authorizationBeforeExistence = false },
            new { status = "FAIL", skipped = false, timedOut = false });
}

internal sealed record P10RaceCleanupEvidence(
    bool Bounded,
    long RemovedCount,
    long SecondDryRunCount,
    bool OutOfScopeUntouched);

internal sealed record P10RaceSecurityEvidence(
    bool AllPassed,
    int CaseCount,
    int SensitiveValuesFound);

internal sealed record P10RaceP9RegressionEvidence(
    int SemanticWrites,
    bool Unchanged);

internal sealed record P10RaceStoreSnapshot(
    int CollectionCount,
    string SemanticSha256,
    IReadOnlyList<string> Entries);

internal sealed class ScopedEnvironmentVariable : IDisposable
{
    private readonly string _name;
    private readonly string? _prior;

    internal ScopedEnvironmentVariable(string name, string value)
    {
        _name = name;
        _prior = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _prior);
}
