using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    public const string CloseoutBrowserCommandLineSwitch =
        "--p10-reconcile-closeout-browser-probe";

    private const string CloseoutPromptId = "P10-12";
    private const string CloseoutGroupId = "P10-CLOSE";
    private const string PublishedCurrentRawSha256 =
        "9899ca9a7495e9d903f520d185dee3098b45ad17a7a3da05caa96b8d31a392d9";
    private const string PublishedLockRawSha256 =
        "cec0b6893a109a697e2f7fa31017c1165c47039b15db85247a8eb9729a044ae0";

    internal static readonly string[] CloseoutBrowserCaseIds =
    [
        "P10-BROWSER-01",
        "P10-BROWSER-02",
        "P10-BROWSER-03"
    ];

    private readonly List<string> _closeoutSecretPaths = [];
    private readonly List<P10CloseoutBrowserCase> _closeoutBrowserCases = [];
    private P10CloseoutSourceFingerprint? _closeoutSourceBefore;
    private P10CloseoutSourceFingerprint? _closeoutSourceAfter;
    private P10CloseoutDatabaseSnapshot? _closeoutDatabaseBefore;
    private P10CloseoutDatabaseSnapshot? _closeoutDatabaseAfter;
    private P10CloseoutPreparedFixture? _closeoutPrepared;
    private P10CloseoutBoundaryProof? _closeoutBoundary;
    private P10CloseProcessResult? _closeoutBuild;
    private P10CloseProcessResult? _closeoutComponents;
    private bool _closeoutBrowserValidated;
    private bool _closeoutSecurityPassed;
    private bool _closeoutBootstrapBackendStopped = true;
    private bool _closeoutBootstrapBackendPortReleased = true;
    private string? _closeoutProductionExportArtifactDirectory;

    public static async Task<int> RunCloseoutBrowserAsync(string[] args)
    {
        var requestedChain = ReadOption(args, "--chain-id") ?? ChainId;
        if (!string.Equals(requestedChain, ChainId, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"P10-CLOSE browser probe refuses chain drift. Expected={ChainId}; Actual={requestedChain}.");
            return 1;
        }

        var runKey =
            $"p1012_browser_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";
        var paths = HarnessPaths.CreateP10(
            runKey,
            ChainId,
            CloseoutPromptId);
        return await new P10ReconciliationCoreProbe(paths, runKey)
            .ExecuteCloseoutBrowserAsync(CancellationToken.None);
    }

    private async Task<int> ExecuteCloseoutBrowserAsync(CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        var cleanupErrors = new List<string>();
        string? fatalFailure = null;

        try
        {
            ValidateCloseoutPublishedState();
            _closeoutSourceBefore = CaptureCloseoutSourceFingerprint();
            await BuildCloseoutFrontendAsync(ct);
            var closeoutJwtSigningKey = Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(48));
            RememberSecret(closeoutJwtSigningKey);

            _mongo = await MongoReplicaSetLease.StartP10Async(
                _paths,
                _iterationRoot,
                _runKey,
                1,
                ct);
            _database = _mongo.Client.GetDatabase(_mongo.DatabaseName);
            _backend = await BackendServerLease.StartAsync(
                _paths,
                Path.Combine(_iterationRoot, "production-bootstrap"),
                _runKey,
                _mongo,
                ct,
                CloseoutBackendOptions(closeoutJwtSigningKey));
            _api = new ApiHarnessClient(_backend.BaseUri);

            await BootstrapAndSeedFixtureAsync(ct);
            await RestartCloseoutBackendWithActualApiOwnerAsync(
                closeoutJwtSigningKey,
                ct);
            var lifecycle =
                await PrepareProductionDirectLifecycleFixtureAsync(ct);
            await PrepareCloseoutCaptureOwnersAsync(lifecycle, ct);
            await AwaitInfrastructureAsync(ct);
            await SeedCloseoutReviewersAsync(ct);
            _protectedBefore = await CaptureProtectedStoreSnapshotAsync(ct);
            _closeoutDatabaseBefore = await CaptureCloseoutDatabaseSnapshotAsync(ct);
            _closeoutPrepared = await PrepareCloseoutProductionFixtureAsync(ct);
            await WriteCloseoutBrowserFixturesAsync(ct);
            await RunCloseoutBrowserGateAsync(ct);
            _closeoutBrowserValidated = await ValidateCloseoutBrowserArtifactsAsync(ct);
            _closeoutBoundary = await CaptureCloseoutBoundaryProofAsync(ct);
            _closeoutDatabaseAfter = await CaptureCloseoutDatabaseSnapshotAsync(ct);
            _protectedAfter = await CaptureProtectedStoreSnapshotAsync(ct);
            _closeoutSourceAfter = CaptureCloseoutSourceFingerprint();

            HarnessAssert.True(
                P10TrustedSourceFingerprintScanner.AreEqual(
                    _closeoutSourceBefore,
                    _closeoutSourceAfter),
                "P10-CLOSE source fingerprint changed during execution");
            HarnessAssert.Equal(
                _protectedBefore.SemanticSha256,
                _protectedAfter.SemanticSha256,
                "P10-CLOSE P5-P9/P11/P12 protected stores changed");
        }
        catch (Exception exception)
        {
            fatalFailure = $"{exception.GetType().Name}: {exception.Message}";
            Console.Error.WriteLine(
                RedactCloseoutArtifactText(fatalFailure));
        }
        finally
        {
            DeleteCloseoutSecretFiles(cleanupErrors);
            await StopBackendAsync(cleanupErrors);
            DeleteCloseoutProductionExportArtifactDirectory(cleanupErrors);
            await CleanupMongoAsync(cleanupErrors);
        }

        var cleanupSucceeded = cleanupErrors.Count == 0 &&
            _closeoutSecretPaths.All(path => !File.Exists(path)) &&
            (_closeoutProductionExportArtifactDirectory is null ||
             !Directory.Exists(
                 _closeoutProductionExportArtifactDirectory)) &&
            _closeoutBootstrapBackendStopped &&
            _closeoutBootstrapBackendPortReleased &&
            _backend is { StopVerified: true, PortReleaseVerified: true } &&
            _mongo is
            {
                DatabaseDropVerified: true,
                ProcessStopVerified: true,
                PortReleaseVerified: true,
                DataDirectoryRemovalVerified: true
            };
        var completedAtUtc = DateTime.UtcNow;
        await WriteCloseoutCleanupArtifactAsync(
            cleanupSucceeded,
            cleanupErrors,
            completedAtUtc,
            CancellationToken.None);
        await WriteCloseoutSourceArtifactAsync(CancellationToken.None);
        _closeoutSecurityPassed = CloseoutArtifactsAreSecretFree(
            fatalFailure,
            cleanupErrors);
        var proofPath = await WriteCloseoutBrowserProofAsync(
            startedAtUtc,
            completedAtUtc,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            CancellationToken.None);
        var proofSha256 = HashBytes(await File.ReadAllBytesAsync(
            proofPath,
            CancellationToken.None));

        var passed = fatalFailure is null &&
            cleanupSucceeded &&
            _closeoutBuild?.ExitCode == 0 &&
            _closeoutComponents?.ExitCode == 0 &&
            _closeoutBrowserValidated &&
            _closeoutSecurityPassed &&
            _closeoutBoundary is
            {
                ProfileBlocked: true,
                P11P12Blocked: true,
                P9Available: true
            } &&
            _protectedBefore?.SemanticSha256 ==
                _protectedAfter?.SemanticSha256 &&
            P10TrustedSourceFingerprintScanner.AreEqual(
                _closeoutSourceBefore,
                _closeoutSourceAfter);

        Console.WriteLine(
            $"P10_CLOSE_BROWSER_PROOF={Path.GetFullPath(proofPath)}");
        Console.WriteLine($"P10_CLOSE_BROWSER_PROOF_SHA256={proofSha256}");
        Console.WriteLine(
            passed
                ? $"[DAT] P10-CLOSE production browser passed 3/3; artifacts={_paths.RunRoot}"
                : $"[KHONG_DAT] P10-CLOSE production browser failed; artifacts={_paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private static BackendServerOptions CloseoutBackendOptions(
        string jwtSigningKey,
        string? apiOwnerAuthorizationParameter = null)
        => new()
        {
            EnvironmentNameOverride = "Production",
            SuppressTestingFixedUtcNow = true,
            HangfireServerEnabled = false,
            P10ReconciliationMaxRetryCount = 2,
            P10ReconciliationLeaseSeconds = 120,
            P10ReconciliationRetryBaseSeconds = 1,
            P10ReconciliationRetryMaxSeconds = 5,
            P10ReconciliationSlaSeconds = 600,
            JwtSigningKey = jwtSigningKey,
            P10ActualApiOwnerServiceAuthorizationParameter =
                apiOwnerAuthorizationParameter
        };

    private async Task RestartCloseoutBackendWithActualApiOwnerAsync(
        string jwtSigningKey,
        CancellationToken ct,
        bool enableReconciliationWorker = false)
    {
        var previous = RequireBackend();
        var authorizationParameter = Actor("executor").Token;
        RememberSecret(previous.ActorPassword);
        RememberSecret(previous.BootstrapKey);
        RememberSecret(previous.DynamicFlowMappingPreviewSigningKey);
        _closeoutBootstrapBackendStopped = false;
        _closeoutBootstrapBackendPortReleased = false;
        _api?.Dispose();
        _api = null;
        await previous.StopAsync();
        _closeoutBootstrapBackendStopped = previous.StopVerified;
        _closeoutBootstrapBackendPortReleased =
            previous.PortReleaseVerified;
        HarnessAssert.True(
            _closeoutBootstrapBackendStopped &&
            _closeoutBootstrapBackendPortReleased,
            "P10-CLOSE bootstrap backend was not stopped and released");
        _backend = null;
        await previous.DisposeAsync();

        _backend = await BackendServerLease.StartAsync(
            _paths,
            Path.Combine(_iterationRoot, "production-v1.7"),
            $"{_runKey}_api_owner",
            RequireMongo(),
            ct,
            CloseoutBackendOptions(
                jwtSigningKey,
                authorizationParameter) with
            {
                HangfireServerEnabled = enableReconciliationWorker,
                HangfireRecurringRegistrationEnabled =
                    enableReconciliationWorker
            });
        _api = new ApiHarnessClient(_backend.BaseUri);
    }

    private void ValidateCloseoutPublishedState()
    {
        var currentPath = CloseoutWorkspacePath(
            "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json");
        var lockPath = CloseoutWorkspacePath(
            "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json");
        HarnessAssert.Equal(
            PublishedCurrentRawSha256,
            HashBytes(File.ReadAllBytes(currentPath)),
            "P10-CLOSE restored CURRENT v1.7 pin");
        HarnessAssert.Equal(
            PublishedLockRawSha256,
            HashBytes(File.ReadAllBytes(lockPath)),
            "P10-CLOSE append-only LOCK v1.7 pin");
    }

    private async Task BuildCloseoutFrontendAsync(CancellationToken ct)
    {
        var frontendRoot = CloseoutWorkspacePath("tdtd-fe");
        _closeoutBuild = await RunP10CloseProcessAsync(
            OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
            ["run", "build"],
            frontendRoot,
            null,
            TimeSpan.FromMinutes(5),
            ct);
        _closeoutComponents = await RunP10CloseProcessAsync(
            "node",
            [
                Path.Combine(frontendRoot, "node_modules", "vitest", "vitest.mjs"),
                "run",
                "tests/reconciliationUi.test.tsx"
            ],
            frontendRoot,
            null,
            TimeSpan.FromMinutes(4),
            ct);
        HarnessAssert.True(
            _closeoutBuild.ExitCode == 0 &&
            _closeoutComponents.ExitCode == 0 &&
            _closeoutComponents.StdOut.Contains(
                "24 passed",
                StringComparison.Ordinal),
            $"P10-CLOSE frontend build/component suite failed. build={P10CloseTail(_closeoutBuild.StdErr, 4000)} components={P10CloseTail(_closeoutComponents.StdOut + _closeoutComponents.StdErr, 6000)}");
    }

    private async Task RunCloseoutBrowserGateAsync(CancellationToken ct)
    {
        var secretPath = _closeoutSecretPaths.Single();
        var result = await RunP10CloseProcessAsync(
            "node",
            [CloseoutWorkspacePath(
                "scripts/p10-reconcile-closeout-browser-gate.mjs")],
            _paths.WorkspaceRoot,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["P10_CLOSE_BROWSER_FIXTURE"] = secretPath
            },
            TimeSpan.FromMinutes(8),
            ct);
        DeleteCloseoutSecretFile(secretPath);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"P10-CLOSE browser gate failed. stdout={P10CloseTail(result.StdOut, 8000)} stderr={P10CloseTail(result.StdErr, 8000)}");
        }
    }

    private static async Task<P10CloseProcessResult> RunP10CloseProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment)
                start.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException(
                $"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort kill followed by the owned-process cleanup proof.
            }
            throw new TimeoutException(
                $"Process {fileName} exceeded {timeout}.");
        }
        return new P10CloseProcessResult(
            process.ExitCode,
            await stdout,
            await stderr);
    }

    private string CloseoutWorkspacePath(string relative)
        => Path.GetFullPath(Path.Combine(
            _paths.WorkspaceRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string P10CloseTail(string value, int maximum)
        => value.Length <= maximum ? value : value[^maximum..];

    private void DeleteCloseoutSecretFiles(ICollection<string> errors)
    {
        foreach (var path in _closeoutSecretPaths.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                DeleteCloseoutSecretFile(path);
            }
            catch (Exception exception)
            {
                errors.Add(
                    $"secret-delete:{Path.GetFileName(path)}:{exception.Message}");
            }
        }
    }

    private static void DeleteCloseoutSecretFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        if (File.Exists(path))
        {
            throw new IOException(
                $"P10-CLOSE secret fixture was not deleted: {path}");
        }
    }
}

internal sealed record P10CloseProcessResult(
    int ExitCode,
    string StdOut,
    string StdErr);

internal sealed record P10CloseoutCollectionState(
    string Name,
    long Count,
    string Sha256);

internal sealed record P10CloseoutDatabaseSnapshot(
    IReadOnlyList<P10CloseoutCollectionState> Collections,
    string Sha256);

internal sealed record P10CloseoutPreparedFixture(
    string TargetReconciliationId,
    long TargetStateRevision,
    string TargetStatus,
    string RunningReconciliationId,
    string QueuedReconciliationId,
    string FailedReconciliationId,
    string P9StateSha256);

internal sealed record P10CloseoutBoundaryProof(
    bool ProfileBlocked,
    string ProfileStatus,
    bool P11P12Blocked,
    int CurrentPhase,
    string ManifestFinalPrompt,
    int P11CollectionCount,
    int P12CollectionCount,
    bool P11ArtifactRootAbsent,
    bool P12ArtifactRootAbsent,
    bool P9Available,
    int P9StatusCode,
    string P9BeforeSha256,
    string P9AfterSha256);

internal sealed record P10CloseoutBrowserCase(
    string CaseId,
    string Status,
    bool DomApiMongoParity,
    bool ExportMongoParity,
    bool AccessibilityPassed,
    bool SecurityPassed,
    int NetworkMockCount,
    string ResultPath,
    string TracePath,
    string NetworkPath,
    string ScreenshotPath,
    string DownloadPath);

internal sealed record P10CloseoutArtifactPin(
    string Path,
    string Sha256,
    long Bytes,
    string Purpose,
    string? CaseId = null);

