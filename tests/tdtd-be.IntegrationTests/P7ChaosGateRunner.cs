using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Strict P7-11 aggregate contract.
///
/// The aggregate deliberately does not reinterpret the P7 unit registries as
/// real integration evidence. A dedicated child surface must execute every
/// core case and every race/chaos case against its own Kestrel process and
/// Mongo replica set, then publish the artifacts described by
/// <see cref="BuildExpectedChildContract"/>. Until that child surface exists,
/// this runner produces an explicit failing result without launching an
/// unrelated fallback harness.
/// </summary>
internal static class P7ChaosGateRunner
{
    internal const string CommandLineSwitch = "--p7-chaos-gate";
    internal const string ChildCommandLineSwitch = "--p7-mapping-chaos-child";

    private const string GateId = "P7-11";
    private const string ChildContract = "P7-11-CHILD-1";
    private const string CaseLedgerContract = "P7-11-CASE-LEDGER-1";
    private const string ApiContract = "P7-11-API-1";
    private const string MongoContract = "P7-11-MONGO-1";
    private const string RaceChaosContract = "P7-11-RACE-CHAOS-1";
    private const string RegressionContract = "P7-11-REGRESSION-1";
    private const string BarrierContract = "P7-11-P8-P9-BARRIER-1";
    private const string CleanupContract = "P7-11-CLEANUP-1";
    private const string StableSemanticOutcomeContract =
        "P7-11-STABLE-SEMANTIC-OUTCOME-1";
    private const string StableSemanticContractIdentityPrefix =
        "P7-11-SEMANTIC-CONTRACT/v1";
    private const int StableSemanticOutcomeSchemaVersion = 1;
    private const string DeliberateCaseId =
        "P7-11-DELIBERATE-ONE-ORACLE-FAILURE";
    private const string ChildSurfaceTypeName =
        "tdtd_be.IntegrationTests.P7MappingChaosChildRunner";
    private const string ChildRunPrefix = "p711child_";

    private const string ChildManifestFileName =
        "p7-11-child-manifest.json";
    private const string CaseLedgerFileName =
        "p7-11-case-ledger.json";
    private const string ApiFileName =
        "p7-11-api-exchanges.json";
    private const string MongoFileName =
        "p7-11-direct-mongo.json";
    private const string RaceChaosFileName =
        "p7-11-race-chaos.json";
    private const string RegressionFileName =
        "p7-11-regression.json";
    private const string BarrierFileName =
        "p7-11-p8-p9-zero-write.json";
    private const string CleanupFileName =
        "cleanup-manifest.json";
    private const string MongoCleanupFileName =
        "mongo-process-cleanup.json";
    private const string BackendCleanupRelativePath =
        "backend/backend-cleanup.json";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly string[] ExpectedCoreCaseIds =
    [
        .. Enumerable.Range(1, 20).Select(index => $"MAP-FIELD-{index:00}"),
        .. Enumerable.Range(1, 20).Select(index => $"MAP-TABLE-{index:00}"),
        .. Enumerable.Range(1, 12).Select(index => $"MAP-AUTH-{index:00}"),
        .. Enumerable.Range(1, 8).Select(index => $"MAP-RERUN-{index:00}")
    ];

    private static readonly string[] ExpectedChaosCaseIds =
    [
        "P7-11-RACE-SOURCE-DRIFT-VS-APPLY",
        "P7-11-RACE-TARGET-CAS-SINGLE-WINNER",
        "P7-11-REPLAY-EXACT",
        "P7-11-REPLAY-CHANGED",
        "P7-11-INVALIDATION-LIFECYCLE",
        "P7-11-INVALIDATION-EPOCH",
        "P7-11-FAULT-BEFORE-RECEIPT",
        "P7-11-FAULT-AFTER-RECEIPT",
        "P7-11-FAULT-BEFORE-TRANSACTION-COMMIT",
        "P7-11-FAULT-AFTER-TRANSACTION-COMMIT",
        "P7-11-FAULT-BEFORE-INTENT-COMMIT",
        "P7-11-FAULT-AFTER-INTENT-COMMIT",
        "P7-11-FAULT-BEFORE-PAYLOAD",
        "P7-11-FAULT-AFTER-PAYLOAD",
        "P7-11-FAULT-BEFORE-PROJECTION",
        "P7-11-FAULT-AFTER-PROJECTION",
        "P7-11-FAULT-BEFORE-AUDIT",
        "P7-11-FAULT-AFTER-AUDIT",
        "P7-11-FAULT-BEFORE-OUTBOX",
        "P7-11-FAULT-AFTER-OUTBOX",
        "P7-11-FAULT-LEASE-LOSS",
        "P7-11-CRASH-BEFORE-COMMIT-RESTART",
        "P7-11-CRASH-AFTER-COMMIT-RESTART",
        "P7-11-RECONCILE-EXACT-CONVERGENCE",
        "P7-11-P8-P9-ZERO-WRITE"
    ];

    private static readonly string[] ExpectedSemanticCaseIds =
    [
        .. ExpectedCoreCaseIds,
        .. ExpectedChaosCaseIds
    ];

    private static readonly HashSet<string> ExpectedSemanticCaseIdSet =
        new(ExpectedSemanticCaseIds, StringComparer.Ordinal);

    private static readonly string[] StableSemanticOutcomePropertyNames =
    [
        "schemaVersion",
        "contract",
        "semanticContractIdentity",
        "caseId",
        "verdict",
        "realKestrel",
        "directMongo",
        "evidenceBound"
    ];

    private static readonly string[] ExpectedHashedArtifactPaths =
    [
        $"iteration-01/{CaseLedgerFileName}",
        $"iteration-01/{ApiFileName}",
        $"iteration-01/{MongoFileName}",
        $"iteration-01/{RaceChaosFileName}",
        $"iteration-01/{RegressionFileName}",
        $"iteration-01/{BarrierFileName}",
        $"iteration-01/{CleanupFileName}",
        $"iteration-01/{MongoCleanupFileName}",
        $"iteration-01/{BackendCleanupRelativePath}"
    ];

    private static readonly HashSet<string> SensitivePropertyNames =
        new(
            [
                "password",
                "defaultPassword",
                "adminPassword",
                "accessToken",
                "refreshToken",
                "authorization",
                "apiKey",
                "clientSecret",
                "systemBootstrapKey",
                "bootstrapKey",
                "X-System-Bootstrap-Key",
                "cookie",
                "Set-Cookie",
                "snapshotToken",
                "previewToken",
                "previewSigningKey"
            ],
            StringComparer.OrdinalIgnoreCase);

    private static readonly Regex BearerSecretPattern =
        new(
            @"\bBearer\s+(?!<redacted>|\[redacted\])\S+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex JwtPattern =
        new(
            @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
            RegexOptions.CultureInvariant);

    private static readonly Regex P7PreviewTokenPattern =
        new(
            @"\bp7p1\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
            RegexOptions.CultureInvariant);

    private static readonly Regex CredentialedMongoUriPattern =
        new(
            @"\bmongodb(?:\+srv)?://[^/\s:@]+:[^@\s/]+@",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SensitiveAssignmentPattern =
        new(
            @"\b(?:password|bootstrapKey|signingKey|authorization|cookie|apiKey|clientSecret|previewToken)\s*[:=]\s*(?!<redacted>|\[redacted\]|\*{3}|null\b|false\b)[^\s,;]{8,}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] RawSourceSentinels =
    [
        "P5_NOTE",
        "P7_HIDDEN_SOURCE",
        "P7-HIDDEN-SOURCE"
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        var requestedIterations = ParseIterations(args);
        var deliberateFailure = args.Any(
            argument => string.Equals(
                argument,
                "--deliberate-failure",
                StringComparison.OrdinalIgnoreCase));
        var startedAtUtc = DateTime.UtcNow;
        var runKey = BuildRunKey();
        var paths = HarnessPaths.Create(runKey);
        var iterations = new List<P7ChaosIterationEvidence>();
        var surface = InspectChildSurface();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "expected-child-contract.json"),
            BuildExpectedChildContract(runKey),
            cancellation.Token);

        Console.WriteLine($"P7-11 aggregate artifacts: {paths.RunRoot}");
        if (!surface.Available)
        {
            Console.Error.WriteLine(
                $"P7-11 child surface unavailable: {surface.Failure}");
        }

        for (var iteration = 1; iteration <= requestedIterations; iteration++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var aggregateIterationRoot = paths.IterationRoot(iteration);
            var child = surface.Available
                ? await ExecuteChildAsync(
                    paths,
                    runKey,
                    iteration,
                    aggregateIterationRoot,
                    cancellation.Token)
                : BuildMissingChildEvidence(
                    iteration,
                    surface.Failure ??
                    "Dedicated P7-11 child surface is unavailable.");
            iterations.Add(
                new P7ChaosIterationEvidence(
                    iteration,
                    child,
                    child.SemanticCases,
                    child.NormalizedSemanticSha256,
                    child.ValidationCases.All(
                        result => result.Verdict == HarnessVerdict.DAT) &&
                    child.SemanticCases.All(
                        result => result.Verdict == HarnessVerdict.DAT)));

            await WriteIterationEvidenceAsync(
                paths,
                runKey,
                aggregateIterationRoot,
                iterations[^1],
                cancellation.Token);
        }

        var aggregateCases = BuildAggregateCases(iterations);
        var allRealCasesGreen =
            iterations.Count == 2 &&
            iterations.All(iteration => iteration.AllRealCasesGreen);
        var cleanupPassed = iterations.Count == 2 &&
                            iterations.All(
                                iteration => iteration.Child.Cleanup.Passed);
        var securityPassed = iterations.Count == 2 &&
                             iterations.All(
                                 iteration => iteration.Child.Security.Passed);
        var p8P9ZeroWritePassed = iterations.Count == 2 &&
                                  iterations.All(
                                      iteration =>
                                          iteration.Child
                                              .P8P9ZeroWritePassed);
        var identitiesDistinct = ValidateDistinctIdentities(
            iterations.Select(iteration => iteration.Child).ToArray());
        var semanticShaMatch =
            iterations.Count == 2 &&
            iterations.All(
                iteration =>
                    IsLowerSha256(iteration.NormalizedSemanticSha256)) &&
            iterations.Select(
                    iteration => iteration.NormalizedSemanticSha256)
                .Distinct(StringComparer.Ordinal)
                .Count() == 1;
        var realGateSatisfied =
            requestedIterations == 2 &&
            surface.Available &&
            allRealCasesGreen &&
            cleanupPassed &&
            securityPassed &&
            p8P9ZeroWritePassed &&
            identitiesDistinct.Passed &&
            semanticShaMatch &&
            aggregateCases.All(
                result => result.Verdict == HarnessVerdict.DAT);

        var finalCases = aggregateCases.ToList();
        if (deliberateFailure)
        {
            finalCases.Add(
                new HarnessCaseResult(
                    DeliberateCaseId,
                    HarnessVerdict.KHONG_DAT,
                    "Intentional control oracle appended only after both real child iterations and cleanup completed.",
                    "deliberate-oracle=false",
                    0));
        }

        var deliberateRows = finalCases
            .Where(result => result.CaseId == DeliberateCaseId)
            .ToArray();
        var deliberateControlPassed =
            deliberateFailure &&
            realGateSatisfied &&
            deliberateRows.Length == 1 &&
            deliberateRows[0].Verdict == HarnessVerdict.KHONG_DAT &&
            finalCases.Count(
                result => result.Verdict == HarnessVerdict.KHONG_DAT) == 1;
        var passed = !deliberateFailure && realGateSatisfied;
        var completedAtUtc = DateTime.UtcNow;
        var failureReason = BuildFailureReason(
            deliberateFailure,
            surface,
            requestedIterations,
            allRealCasesGreen,
            cleanupPassed,
            securityPassed,
            p8P9ZeroWritePassed,
            identitiesDistinct,
            semanticShaMatch,
            aggregateCases,
            deliberateControlPassed);

        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "results.json"),
            new
            {
                schemaVersion = 1,
                gate = GateId,
                requirementOwned = "P7-MAP-024",
                childContract = ChildContract,
                runKey,
                startedAtUtc,
                completedAtUtc,
                requestedIterations,
                requiredIterations = 2,
                deliberateFailureMode = deliberateFailure,
                childSurface = surface,
                iterations = iterations.Select(
                    iteration => BuildIterationProjection(paths, iteration)),
                checks = new
                {
                    allRealCasesGreen,
                    cleanupPassed,
                    securityPassed,
                    p8P9ZeroWritePassed,
                    identitiesDistinct = identitiesDistinct.Passed,
                    identityFailure = identitiesDistinct.Failure,
                    semanticShaMatch,
                    deliberateControlPassed
                },
                normalizedSemanticSha256 =
                    iterations.FirstOrDefault()?.NormalizedSemanticSha256,
                aggregateCases = finalCases,
                passed,
                failureReason
            },
            cancellation.Token);
        await EvidenceCsv.WriteCasesAsync(
            Path.Combine(paths.RunRoot, "results.csv"),
            iterations.SelectMany(
                    iteration => iteration.Child.SemanticCases.Select(
                        result => (iteration.Iteration, Case: result)))
                .Concat(
                    finalCases.Select(
                        result => (requestedIterations + 1, Case: result))),
            cancellation.Token);
        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "reconciliation-ledger.json"),
            BuildReconciliationProjection(
                paths,
                runKey,
                iterations,
                identitiesDistinct,
                semanticShaMatch,
                deliberateFailure,
                deliberateControlPassed,
                passed),
            cancellation.Token);
        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "cleanup-manifest.json"),
            new
            {
                schemaVersion = 1,
                gate = GateId,
                runKey,
                completedAtUtc,
                children = iterations.Select(
                    iteration => new
                    {
                        iteration.Iteration,
                        iteration.Child.ChildRunKey,
                        cleanup = iteration.Child.Cleanup,
                        security = iteration.Child.Security
                    }),
                cleanupPassed,
                securityPassed,
                passed = cleanupPassed && securityPassed
            },
            cancellation.Token);

        Console.WriteLine(
            passed
                ? $"[DAT] P7-11 passed two isolated runs; semanticSha={iterations[0].NormalizedSemanticSha256}."
                : $"[KHONG_DAT] P7-11 failed: {failureReason}");
        return passed ? 0 : 1;
    }

    internal static object BuildExpectedChildContract(string parentRunKey)
        => new
        {
            schemaVersion = 1,
            contract = ChildContract,
            gate = GateId,
            parentRunKey,
            commandLineSwitch = ChildCommandLineSwitch,
            childSurfaceType = ChildSurfaceTypeName,
            childRunPrefix = ChildRunPrefix,
            requiredIterations = 2,
            childManifest = ChildManifestFileName,
            requiredFiles = ExpectedHashedArtifactPaths,
            manifestIdentity = new
            {
                required = new[]
                {
                    "runKey",
                    "parentRunKey",
                    "iteration",
                    "childProcessId",
                    "backendProcessId",
                    "mongoProcessId",
                    "backendPort",
                    "mongoPort",
                    "databaseName",
                    "replicaSetName"
                },
                realKestrel = true,
                realMongoReplicaSet = true,
                candidateVersion = "1.4",
                candidateIsCurrent = false,
                currentCatalogVersion = "1.3"
            },
            caseLedger = new
            {
                contract = CaseLedgerContract,
                exactCoreCount = ExpectedCoreCaseIds.Length,
                exactChaosCount = ExpectedChaosCaseIds.Length,
                coreCaseIds = ExpectedCoreCaseIds,
                chaosCaseIds = ExpectedChaosCaseIds,
                everyCaseRequires = new[]
                {
                    "verdict=DAT",
                    "lowercase SHA-256 fingerprint",
                    "real Kestrel evidence ID",
                    "direct Mongo evidence ID",
                    "evidenceBound=true",
                    "versioned stableSemanticOutcome",
                    "lowercase SHA-256 stableSemanticFingerprint"
                },
                stableSemanticOutcome = new
                {
                    schemaVersion = StableSemanticOutcomeSchemaVersion,
                    contract = StableSemanticOutcomeContract,
                    semanticContractIdentity =
                        $"{StableSemanticContractIdentityPrefix}/<exact-caseId>",
                    exactProperties = StableSemanticOutcomePropertyNames
                }
            },
            evidenceContracts = new
            {
                api = ApiContract,
                directMongo = MongoContract,
                raceChaos = RaceChaosContract,
                regression = RegressionContract,
                p8P9Barrier = BarrierContract,
                cleanup = CleanupContract,
                perCaseEvidence = new
                {
                    apiCaseEvidence =
                        $"exact {ExpectedCoreCaseIds.Length + ExpectedChaosCaseIds.Length} case IDs, each with non-empty apiEvidenceIds",
                    mongoCaseEvidence =
                        $"exact {ExpectedCoreCaseIds.Length + ExpectedChaosCaseIds.Length} case IDs, each with non-empty mongoEvidenceIds"
                }
            },
            determinism = new
            {
                normalizedTuple =
                    "caseId + verdict + stableSemanticFingerprint",
                bothIterationsMustMatch = true,
                identitiesMustDiffer = new[]
                {
                    "runKey",
                    "childProcessId",
                    "backendProcessId",
                    "mongoProcessId",
                    "databaseName",
                    "replicaSetName"
                }
            },
            security = new
            {
                scanJsonJsonlCsvTextMarkdownAndLogs = true,
                rawRequestOrResponseBodiesRecorded = false,
                rawSourceValuesRecorded = false,
                forbiddenLeftovers = new[] { "*.stop", "*.secret" }
            }
        };

    private static ChildSurfaceAudit InspectChildSurface()
    {
        var type = typeof(P7ChaosGateRunner).Assembly.GetType(
            ChildSurfaceTypeName,
            throwOnError: false,
            ignoreCase: false);
        if (type is null)
        {
            return new ChildSurfaceAudit(
                false,
                ChildSurfaceTypeName,
                ChildCommandLineSwitch,
                "Missing P7MappingChaosChildRunner. Current --p7-mapping-gate supports staged P7-04..P7-09 and does not emit a 60-case/chaos child manifest.");
        }

        var method = type.GetMethod(
            "RunAsync",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string[])],
            modifiers: null);
        if (method is null || method.ReturnType != typeof(Task<int>))
        {
            return new ChildSurfaceAudit(
                false,
                ChildSurfaceTypeName,
                ChildCommandLineSwitch,
                "P7MappingChaosChildRunner.RunAsync(string[]) returning Task<int> is missing.");
        }

        var switchMember = type.GetField(
            "CommandLineSwitch",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var switchValue = switchMember?.GetValue(null) as string;
        if (!string.Equals(
                switchValue,
                ChildCommandLineSwitch,
                StringComparison.Ordinal))
        {
            return new ChildSurfaceAudit(
                false,
                ChildSurfaceTypeName,
                ChildCommandLineSwitch,
                $"P7MappingChaosChildRunner.CommandLineSwitch must equal {ChildCommandLineSwitch}.");
        }

        return new ChildSurfaceAudit(
            true,
            ChildSurfaceTypeName,
            ChildCommandLineSwitch,
            null);
    }

    private static async Task<P7ChaosChildEvidence> ExecuteChildAsync(
        HarnessPaths paths,
        string parentRunKey,
        int iteration,
        string aggregateIterationRoot,
        CancellationToken ct)
    {
        var before = EnumerateChildRoots(paths.IntegrationRoot)
            .ToHashSet(PathComparer);
        var stdoutPath = Path.Combine(
            aggregateIterationRoot,
            "child-stdout.log");
        var stderrPath = Path.Combine(
            aggregateIterationRoot,
            "child-stderr.log");
        var timeout = ParseChildTimeout();
        var exitCode = -1;
        var childProcessId = 0;
        var timedOut = false;
        string? invocationFailure = null;

        try
        {
            using var process = new Process
            {
                StartInfo = BuildChildStartInfo(
                    paths,
                    parentRunKey,
                    iteration)
            };
            if (!process.Start())
                throw new InvalidOperationException("Child process did not start.");
            childProcessId = process.Id;
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (
                !ct.IsCancellationRequested)
            {
                timedOut = true;
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await File.WriteAllTextAsync(
                stdoutPath,
                stdout,
                new UTF8Encoding(false),
                CancellationToken.None);
            await File.WriteAllTextAsync(
                stderrPath,
                stderr,
                new UTF8Encoding(false),
                CancellationToken.None);
            exitCode = process.ExitCode;
        }
        catch (Exception error)
        {
            invocationFailure =
                $"{error.GetType().Name}: {error.Message}";
            await File.WriteAllTextAsync(
                stderrPath,
                invocationFailure,
                new UTF8Encoding(false),
                CancellationToken.None);
        }

        var freshRoots = EnumerateChildRoots(paths.IntegrationRoot)
            .Where(path => !before.Contains(path))
            .OrderBy(path => path, PathComparer)
            .ToArray();
        var runRoot = freshRoots.Length == 1 ? freshRoots[0] : null;
        var child = ValidateChildArtifacts(
            paths,
            parentRunKey,
            iteration,
            childProcessId,
            exitCode,
            timedOut,
            invocationFailure,
            freshRoots,
            runRoot,
            aggregateIterationRoot);
        foreach (var result in child.ValidationCases)
        {
            Console.WriteLine(
                $"[{result.Verdict}] {result.CaseId}: {result.Detail}");
        }
        return child;
    }

    private static ProcessStartInfo BuildChildStartInfo(
        HarnessPaths paths,
        string parentRunKey,
        int iteration)
    {
        var processPath = Environment.ProcessPath ??
                          throw new InvalidOperationException(
                              "Cannot resolve integration-test process path.");
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var info = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = paths.WorkspaceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(assemblyPath);
        }
        info.ArgumentList.Add(ChildCommandLineSwitch);
        info.ArgumentList.Add($"--parent-run-key={parentRunKey}");
        info.ArgumentList.Add($"--iteration={iteration}");
        info.Environment["TDTD_TEST_ARTIFACT_ROOT"] =
            paths.IntegrationRoot;
        info.Environment["TDTD_P7_CHAOS_PARENT_RUN_KEY"] =
            parentRunKey;
        info.Environment["TDTD_P7_CHAOS_ITERATION"] =
            iteration.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["TDTD_P7_CHAOS_CHILD_CONTRACT"] =
            ChildContract;
        return info;
    }

    private static P7ChaosChildEvidence ValidateChildArtifacts(
        HarnessPaths paths,
        string parentRunKey,
        int iteration,
        int spawnedChildProcessId,
        int exitCode,
        bool timedOut,
        string? invocationFailure,
        IReadOnlyList<string> freshRoots,
        string? runRoot,
        string aggregateIterationRoot)
    {
        var iterationRoot = runRoot is null
            ? null
            : Path.Combine(runRoot, "iteration-01");
        var manifest = LoadJson(
            runRoot is null
                ? null
                : Path.Combine(runRoot, ChildManifestFileName));
        var ledger = LoadJson(
            iterationRoot is null
                ? null
                : Path.Combine(iterationRoot, CaseLedgerFileName));
        var api = LoadJson(
            iterationRoot is null
                ? null
                : Path.Combine(iterationRoot, ApiFileName));
        var mongo = LoadJson(
            iterationRoot is null
                ? null
                : Path.Combine(iterationRoot, MongoFileName));
        var raceChaos = LoadJson(
            iterationRoot is null
                ? null
                : Path.Combine(iterationRoot, RaceChaosFileName));
        var regression = LoadJson(
            iterationRoot is null
                ? null
                : Path.Combine(iterationRoot, RegressionFileName));
        var barrier = LoadJson(
            iterationRoot is null
                ? null
                : Path.Combine(iterationRoot, BarrierFileName));
        var cleanupDocument = LoadJson(
            iterationRoot is null
                ? null
                : Path.Combine(iterationRoot, CleanupFileName));

        var identity = ReadIdentity(manifest.Root);
        var semanticLedger = ReadSemanticCases(ledger.Root);
        var semanticCases = semanticLedger.Cases;
        var computedSemanticSha =
            semanticLedger.NormalizedSemanticSha256;
        var cleanup = InspectCleanup(
            runRoot,
            iterationRoot,
            identity,
            cleanupDocument.Root);
        var security = InspectArtifactSecurity(
            [runRoot, aggregateIterationRoot]);
        var validationCases = new List<HarnessCaseResult>
        {
            EvaluateCase(
                $"P7-11-I{iteration:00}-CHILD-SURFACE",
                () =>
                {
                    Require(
                        invocationFailure is null,
                        $"Child invocation failed: {invocationFailure}");
                    Require(!timedOut, "Child process timed out.");
                    Require(
                        freshRoots.Count == 1,
                        $"Expected exactly one fresh {ChildRunPrefix}* root, found {freshRoots.Count}.");
                    Require(runRoot is not null, "Fresh child root is missing.");
                    Require(
                        exitCode == 0,
                        $"Child exit code was {exitCode}.");
                    RequireLoaded(manifest, ChildManifestFileName);
                    return Observation(
                        "Dedicated child exited cleanly and emitted one fresh P7-11 root.",
                        $"iteration={iteration};fresh-root=1;exit=0");
                }),
            EvaluateCase(
                $"P7-11-I{iteration:00}-MANIFEST",
                () => ValidateManifest(
                    manifest,
                    parentRunKey,
                    iteration,
                    spawnedChildProcessId,
                    runRoot,
                    identity,
                    computedSemanticSha)),
            EvaluateCase(
                $"P7-11-I{iteration:00}-CORE-60",
                () => ValidateCaseLedger(
                    ledger,
                    parentRunKey,
                    iteration,
                    identity,
                    semanticLedger)),
            EvaluateCase(
                $"P7-11-I{iteration:00}-API-KESTREL",
                () => ValidateApi(api, identity, semanticCases)),
            EvaluateCase(
                $"P7-11-I{iteration:00}-RACE-CHAOS",
                () => ValidateRaceChaos(
                    raceChaos,
                    identity,
                    semanticCases,
                    api.Root,
                    mongo.Root)),
            EvaluateCase(
                $"P7-11-I{iteration:00}-DIRECT-MONGO",
                () => ValidateMongo(
                    mongo,
                    identity,
                    semanticCases)),
            EvaluateCase(
                $"P7-11-I{iteration:00}-P8-P9-ZERO-WRITE",
                () => ValidateP8P9Barrier(barrier, identity, mongo.Root)),
            EvaluateCase(
                $"P7-11-I{iteration:00}-P0-P6-REGRESSION",
                () => ValidateRegression(regression, identity)),
            EvaluateCase(
                $"P7-11-I{iteration:00}-ARTIFACT-HASHES",
                () => ValidateArtifactHashes(
                    manifest,
                    runRoot)),
            EvaluateCase(
                $"P7-11-I{iteration:00}-CLEANUP",
                () =>
                {
                    Require(
                        cleanup.Passed,
                        string.Join("; ", cleanup.Failures));
                    return Observation(
                        "Database, Mongo/Kestrel processes, ports and data directory were independently confirmed clean.",
                        $"iteration={iteration};cleanup=true");
                }),
            EvaluateCase(
                $"P7-11-I{iteration:00}-ARTIFACT-SECURITY",
                () =>
                {
                    Require(
                        security.Passed,
                        string.Join("; ", security.Failures));
                    Require(
                        security.ScannedLogFiles > 0,
                        "No child/backend/Mongo/stdout/stderr log was scanned.");
                    return Observation(
                        "JSON, ledgers, CSV/text and all logs were scanned without raw tokens, source sentinels, secret fields or forbidden leftovers.",
                        $"iteration={iteration};json={security.ScannedJsonFiles};logs={security.ScannedLogFiles};security=true");
                })
        };

        var p8P9Passed =
            validationCases.Single(
                    result => result.CaseId.EndsWith(
                        "P8-P9-ZERO-WRITE",
                        StringComparison.Ordinal))
                .Verdict == HarnessVerdict.DAT;
        return new P7ChaosChildEvidence(
            iteration,
            exitCode,
            spawnedChildProcessId,
            identity.RunKey,
            runRoot,
            iterationRoot,
            freshRoots.Count == 1,
            identity,
            semanticCases,
            computedSemanticSha,
            validationCases,
            cleanup,
            security,
            p8P9Passed,
            invocationFailure ?? (timedOut ? "Child timed out." : null));
    }

    private static CaseObservation ValidateManifest(
        JsonLoadResult manifest,
        string parentRunKey,
        int iteration,
        int spawnedChildProcessId,
        string? runRoot,
        ChildIdentity identity,
        string computedSemanticSha)
    {
        var root = RequireLoaded(manifest, ChildManifestFileName);
        RequireEqual(
            ChildContract,
            RequireString(root, "contract"),
            "Child manifest contract drift.");
        RequireEqual(GateId, RequireString(root, "gate"), "Gate drift.");
        RequireEqual(
            parentRunKey,
            RequireString(root, "parentRunKey"),
            "Parent run key drift.");
        RequireEqual(
            iteration,
            RequireInt32(root, "iteration"),
            "Iteration drift.");
        RequireEqual(
            spawnedChildProcessId,
            identity.ChildProcessId,
            "Spawned/manifest child PID drift.");
        Require(
            identity.ChildProcessId > 0 &&
            identity.BackendProcessId > 0 &&
            identity.MongoProcessId > 0,
            "Child/backend/Mongo process identities must be positive.");
        Require(
            new[]
                {
                    identity.ChildProcessId,
                    identity.BackendProcessId,
                    identity.MongoProcessId
                }
                .Distinct()
                .Count() == 3,
            "Child/backend/Mongo PIDs must be distinct.");
        Require(
            identity.BackendPort is > 0 and <= 65535 &&
            identity.MongoPort is > 0 and <= 65535,
            "Backend/Mongo ports are invalid.");
        Require(
            !string.IsNullOrWhiteSpace(identity.RunKey) &&
            !string.IsNullOrWhiteSpace(identity.DatabaseName) &&
            !string.IsNullOrWhiteSpace(identity.ReplicaSetName),
            "Run/database/replica-set identity is incomplete.");
        RequireTrue(root, "realKestrel");
        RequireTrue(root, "realMongoReplicaSet");
        RequireEqual(
            "1.4",
            RequireString(root, "candidateVersion"),
            "Candidate version drift.");
        RequireFalse(root, "candidateIsCurrent");
        RequireEqual(
            "1.3",
            RequireString(root, "currentCatalogVersion"),
            "P7-11 must retain CURRENT v1.3.");
        RequireEqual(
            ExpectedCoreCaseIds.Length,
            RequireInt32(root, "coreCaseCount"),
            "Core case count drift.");
        RequireEqual(
            ExpectedChaosCaseIds.Length,
            RequireInt32(root, "chaosCaseCount"),
            "Chaos case count drift.");
        RequireEqual(
            ExpectedCoreCaseIds.Length + ExpectedChaosCaseIds.Length,
            RequireInt32(root, "semanticCaseCount"),
            "Semantic case count drift.");
        RequireEqual(
            computedSemanticSha,
            RequireString(root, "normalizedSemanticSha256"),
            "Manifest semantic SHA drift.");
        RequireTrue(root, "p0P6RegressionPassed");
        RequireTrue(root, "historicalCatalogPinsPassed");
        RequireTrue(root, "backendContractsPassed");
        RequireTrue(root, "frontendGatePassed");
        RequireTrue(root, "p8P9ZeroWritePassed");
        RequireTrue(root, "cleanupPassed");
        RequireTrue(root, "artifactSecuritySelfScanPassed");
        RequireTrue(root, "passed");
        RequireDirectory(runRoot, "child run root");
        return Observation(
            $"Child manifest binds exact process/runtime identities, 60+{ExpectedChaosCaseIds.Length} cases, regression/barrier/cleanup checks, CURRENT v1.3 and candidate v1.4.",
            $"iteration={iteration};contract={ChildContract};core=60;chaos={ExpectedChaosCaseIds.Length};current=1.3;candidate=1.4");
    }

    private static CaseObservation ValidateCaseLedger(
        JsonLoadResult ledger,
        string parentRunKey,
        int iteration,
        ChildIdentity identity,
        SemanticLedgerRead semanticLedger)
    {
        var root = RequireLoaded(ledger, CaseLedgerFileName);
        Require(
            semanticLedger.Failure is null,
            $"Stable semantic ledger validation failed: {semanticLedger.Failure}");
        var semanticCases = semanticLedger.Cases;
        var computedSemanticSha =
            semanticLedger.NormalizedSemanticSha256;

        RequireEqual(
            CaseLedgerContract,
            RequireString(root, "contract"),
            "Case ledger contract drift.");
        RequireEqual(
            identity.RunKey,
            RequireString(root, "runKey"),
            "Case ledger run key drift.");
        RequireEqual(
            parentRunKey,
            RequireString(root, "parentRunKey"),
            "Case ledger parent run key drift.");
        RequireEqual(
            iteration,
            RequireInt32(root, "iteration"),
            "Case ledger iteration drift.");

        var ids = semanticCases.Select(result => result.CaseId).ToArray();
        RequireEqual(
            ExpectedSemanticCaseIds.Length,
            semanticCases.Count,
            "Semantic case ledger count drift.");
        RequireEqual(
            ids.Length,
            ids.Distinct(StringComparer.Ordinal).Count(),
            "Semantic case IDs contain duplicates.");
        RequireSetsEqual(
            ExpectedCoreCaseIds,
            ids.Where(id => id.StartsWith("MAP-", StringComparison.Ordinal)),
            "Core case set drift.");
        RequireSetsEqual(
            ExpectedChaosCaseIds,
            ids.Where(id => id.StartsWith("P7-11-", StringComparison.Ordinal)),
            "Chaos case set drift.");
        Require(
            semanticCases.All(
                result =>
                    result.Verdict == HarnessVerdict.DAT &&
                    IsLowerSha256(result.Fingerprint)),
            "Every semantic case must be DAT with a lowercase raw evidence fingerprint.");

        RequireEqual(
            ExpectedSemanticCaseIds.Length,
            semanticLedger.StableCases.Count,
            "Verified stable semantic case count drift.");
        RequireSetsEqual(
            ids,
            semanticLedger.StableCases.Select(item => item.CaseId),
            "Raw/stable semantic case sets drift.");
        Require(
            semanticLedger.StableCases.All(
                item =>
                    string.Equals(
                        item.Verdict,
                        HarnessVerdict.DAT.ToString(),
                        StringComparison.Ordinal) &&
                    IsLowerSha256(item.StableSemanticFingerprint)),
            "Every verified semantic case must be DAT with a lowercase stable semantic fingerprint.");
        Require(
            IsLowerSha256(computedSemanticSha),
            "Computed normalized stable semantic SHA is invalid.");
        RequireEqual(
            computedSemanticSha,
            RequireString(root, "normalizedSemanticSha256"),
            "Case ledger stable semantic SHA drift.");

        foreach (var group in new[]
                 {
                     ("MAP-FIELD-", 20),
                     ("MAP-TABLE-", 20),
                     ("MAP-AUTH-", 12),
                     ("MAP-RERUN-", 8)
                 })
        {
            RequireEqual(
                group.Item2,
                ids.Count(
                    id => id.StartsWith(
                        group.Item1,
                        StringComparison.Ordinal)),
                $"{group.Item1} exact count drift.");
        }

        return Observation(
            "Exact MAP-FIELD 20, MAP-TABLE 20, MAP-AUTH 12, MAP-RERUN 8 and all named P7 race/chaos cases passed with independently reconstructed stable semantic fingerprints.",
            $"core=60;chaos={ExpectedChaosCaseIds.Length};sha={computedSemanticSha}");
    }

    private static CaseObservation ValidateApi(
        JsonLoadResult api,
        ChildIdentity identity,
        IReadOnlyList<HarnessCaseResult> semanticCases)
    {
        var root = RequireLoaded(api, ApiFileName);
        RequireEqual(
            ApiContract,
            RequireString(root, "contract"),
            "API artifact contract drift.");
        RequireEqual(
            identity.RunKey,
            RequireString(root, "runKey"),
            "API run key drift.");
        RequireEqual(
            identity.BackendProcessId,
            RequireInt32(root, "backendProcessId"),
            "API backend PID drift.");
        RequireTrue(root, "realKestrel");
        RequireFalse(root, "requestBodiesRecorded");
        RequireFalse(root, "responseBodiesRecorded");

        var exchanges = RequireArray(root, "exchanges");
        Require(
            exchanges.GetArrayLength() >= semanticCases.Count,
            "API exchange ledger has fewer rows than semantic cases.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var statuses = new HashSet<int>();
        var paths = new List<string>();
        var observedCrashTransport = false;
        foreach (var exchange in exchanges.EnumerateArray())
        {
            var id = RequireString(exchange, "evidenceId");
            Require(ids.Add(id), $"Duplicate API evidence ID {id}.");
            var path = RequireString(exchange, "path");
            paths.Add(path);
            var status = GetInt32(exchange, "statusCode");
            if (status is not null)
                statuses.Add(status.Value);
            observedCrashTransport |= string.Equals(
                GetString(exchange, "transportOutcome"),
                "CONNECTION_LOST",
                StringComparison.Ordinal);
            RequireFalse(exchange, "requestBodyRecorded");
            RequireFalse(exchange, "responseBodyRecorded");
        }

        Require(statuses.Contains(200), "API ledger lacks HTTP 200.");
        Require(statuses.Contains(403), "API ledger lacks HTTP 403.");
        Require(statuses.Contains(409), "API ledger lacks HTTP 409.");
        Require(
            statuses.Any(status => status >= 500) ||
            observedCrashTransport,
            "API ledger lacks a fault/crash transport observation.");
        Require(
            paths.Any(path => path.Contains(
                "preview-dynamic-flow-mapping",
                StringComparison.Ordinal)),
            "API ledger lacks the canonical preview endpoint.");
        Require(
            paths.Any(path => path.Contains(
                "apply-dynamic-flow-mapping",
                StringComparison.Ordinal)),
            "API ledger lacks the canonical apply endpoint.");

        var perCaseEvidence = ReadCaseEvidence(
            root,
            "apiEvidenceIds");
        RequireSetsEqual(
            semanticCases.Select(result => result.CaseId),
            perCaseEvidence.Keys,
            "API caseEvidence case set drift.");
        foreach (var (caseId, evidenceIds) in perCaseEvidence)
        {
            Require(
                evidenceIds.Count > 0,
                $"{caseId} lacks API evidence.");
            Require(
                evidenceIds.All(ids.Contains),
                $"{caseId} references unknown API evidence.");
        }
        RequireEvidenceOwnershipExclusive(
            perCaseEvidence,
            "API");
        return Observation(
            "Every semantic case is backed by redacted real-Kestrel exchange metadata, including success, authorization, conflict and crash/fault outcomes.",
            $"backend-pid={identity.BackendProcessId};api-evidence={ids.Count};statuses={string.Join('+', statuses.Order())}");
    }

    private static CaseObservation ValidateRaceChaos(
        JsonLoadResult raceChaos,
        ChildIdentity identity,
        IReadOnlyList<HarnessCaseResult> semanticCases,
        JsonElement? api,
        JsonElement? mongo)
    {
        var root = RequireLoaded(raceChaos, RaceChaosFileName);
        RequireEqual(
            RaceChaosContract,
            RequireString(root, "contract"),
            "Race/chaos contract drift.");
        RequireEqual(
            identity.RunKey,
            RequireString(root, "runKey"),
            "Race/chaos run key drift.");
        RequireTrue(root, "realKestrel");
        RequireTrue(root, "realMongoReplicaSet");
        var cases = RequireArray(root, "cases");
        RequireEqual(
            ExpectedChaosCaseIds.Length,
            cases.GetArrayLength(),
            "Race/chaos case count drift.");
        var apiIds = ReadEvidenceIds(api, "exchanges");
        var mongoIds = ReadEvidenceIds(mongo, "observations");
        var semanticById = semanticCases.ToDictionary(
            result => result.CaseId,
            StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in cases.EnumerateArray())
        {
            var caseId = RequireString(item, "caseId");
            Require(ids.Add(caseId), $"Duplicate chaos case {caseId}.");
            Require(
                semanticById.TryGetValue(caseId, out var semantic),
                $"Chaos case {caseId} is absent from the semantic ledger.");
            RequireEqual(
                "DAT",
                RequireString(item, "verdict"),
                $"{caseId} verdict drift.");
            RequireEqual(
                semantic!.Fingerprint,
                RequireString(item, "fingerprint"),
                $"{caseId} fingerprint drift.");
            RequireTrue(item, "realKestrel");
            RequireTrue(item, "directMongo");
            RequireTrue(item, "converged");
            RequireEqual(
                0,
                RequireInt32(item, "duplicateCount"),
                $"{caseId} duplicate count drift.");
            RequireEqual(
                0,
                RequireInt32(item, "orphanCount"),
                $"{caseId} orphan count drift.");
            RequireEqual(
                0,
                RequireInt32(item, "partialWriteSetCount"),
                $"{caseId} partial write-set count drift.");
            var itemApiIds = RequireStringArray(item, "apiEvidenceIds");
            var itemMongoIds = RequireStringArray(
                item,
                "mongoObservationIds");
            Require(itemApiIds.Count > 0, $"{caseId} lacks API evidence.");
            Require(
                itemMongoIds.Count > 0,
                $"{caseId} lacks direct-Mongo evidence.");
            Require(
                itemApiIds.All(apiIds.Contains),
                $"{caseId} references unknown API evidence.");
            Require(
                itemMongoIds.All(mongoIds.Contains),
                $"{caseId} references unknown Mongo evidence.");
            RequireNonEmpty(item, "scenario");
            RequireNonEmpty(item, "convergence");
        }
        RequireSetsEqual(
            ExpectedChaosCaseIds,
            ids,
            "Race/chaos case set drift.");
        return Observation(
            "Source/apply and target-CAS races, replay, lifecycle/epoch invalidation, all named fault seams, crash/restart, lease loss and exact reconciliation converged.",
            $"chaos={ids.Count};duplicates=0;orphans=0;partial=0");
    }

    private static CaseObservation ValidateMongo(
        JsonLoadResult mongo,
        ChildIdentity identity,
        IReadOnlyList<HarnessCaseResult> semanticCases)
    {
        var root = RequireLoaded(mongo, MongoFileName);
        RequireEqual(
            MongoContract,
            RequireString(root, "contract"),
            "Mongo artifact contract drift.");
        RequireEqual(
            identity.RunKey,
            RequireString(root, "runKey"),
            "Mongo run key drift.");
        RequireEqual(
            identity.DatabaseName,
            RequireString(root, "databaseName"),
            "Mongo database identity drift.");
        RequireEqual(
            identity.ReplicaSetName,
            RequireString(root, "replicaSetName"),
            "Mongo replica-set identity drift.");
        RequireEqual(
            identity.MongoProcessId,
            RequireInt32(root, "mongoProcessId"),
            "Mongo process identity drift.");
        RequireTrue(root, "realMongoReplicaSet");
        RequireTrue(root, "transactionsObserved");
        RequireTrue(root, "exactIds");
        RequireTrue(root, "exactRevisions");
        RequireTrue(root, "exactHashes");
        RequireTrue(root, "receiptsUnique");
        RequireTrue(root, "eventsUnique");
        RequireTrue(root, "outboxUnique");
        RequireTrue(root, "provenanceUnique");
        RequireTrue(root, "projectionIntentsConverged");
        RequireFalse(root, "rawSourceValuesRecorded");
        RequireEqual(
            0,
            RequireInt32(root, "duplicateCount"),
            "Direct-Mongo duplicate count drift.");
        RequireEqual(
            0,
            RequireInt32(root, "orphanCount"),
            "Direct-Mongo orphan count drift.");
        RequireEqual(
            0,
            RequireInt32(root, "partialWriteSetCount"),
            "Direct-Mongo partial write-set count drift.");
        RequireEqual(
            RequireInt32(root, "committedIntentCount"),
            RequireInt32(root, "convergedIntentCount"),
            "Committed/converged intent count drift.");
        Require(
            RequireInt32(root, "committedIntentCount") > 0,
            "Direct-Mongo evidence did not observe a committed intent.");
        RequireEqual(
            0,
            RequireInt32(root, "leasedOutboxCount"),
            "Outbox leases remained after reconciliation.");
        var observations = RequireArray(root, "observations");
        Require(
            observations.GetArrayLength() > 0,
            "Direct-Mongo observations are empty.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in observations.EnumerateArray())
        {
            Require(
                ids.Add(RequireString(observation, "evidenceId")),
                "Direct-Mongo observation IDs are duplicated.");
        }
        var perCaseEvidence = ReadCaseEvidence(
            root,
            "mongoEvidenceIds");
        RequireSetsEqual(
            semanticCases.Select(result => result.CaseId),
            perCaseEvidence.Keys,
            "Mongo caseEvidence case set drift.");
        foreach (var (caseId, evidenceIds) in perCaseEvidence)
        {
            Require(
                evidenceIds.Count > 0,
                $"{caseId} lacks direct-Mongo evidence.");
            Require(
                evidenceIds.All(ids.Contains),
                $"{caseId} references unknown direct-Mongo evidence.");
        }
        RequireEvidenceOwnershipExclusive(
            perCaseEvidence,
            "Mongo");
        return Observation(
            "Direct Mongo reconciled exact IDs/revisions/hashes and unique receipts/events/outbox/provenance with no duplicate, orphan, partial write or outstanding lease.",
            $"mongo-observations={ids.Count};duplicates=0;orphans=0;partial=0;leased=0");
    }

    private static CaseObservation ValidateP8P9Barrier(
        JsonLoadResult barrier,
        ChildIdentity identity,
        JsonElement? mongo)
    {
        var root = RequireLoaded(barrier, BarrierFileName);
        RequireEqual(
            BarrierContract,
            RequireString(root, "contract"),
            "P8/P9 barrier contract drift.");
        RequireEqual(
            identity.RunKey,
            RequireString(root, "runKey"),
            "P8/P9 barrier run key drift.");
        RequireEqual(
            "1.3",
            RequireString(root, "currentCatalogVersionBefore"),
            "Pre-gate CURRENT drift.");
        RequireEqual(
            "1.3",
            RequireString(root, "currentCatalogVersionAfter"),
            "Post-gate CURRENT drift.");
        RequireEqual(
            0,
            RequireInt32(root, "p8WriteCount"),
            "P8 write barrier failed.");
        RequireEqual(
            0,
            RequireInt32(root, "p9WriteCount"),
            "P9 write barrier failed.");
        RequireEqual(
            RequireString(root, "beforeSemanticSha256"),
            RequireString(root, "afterSemanticSha256"),
            "P8/P9 persistence fingerprint changed.");
        RequireTrue(root, "blocked");
        var collections = RequireStringArray(root, "collectionsChecked");
        Require(collections.Count > 0, "P8/P9 collection ledger is empty.");
        if (mongo is not null)
        {
            RequireEqual(
                0,
                RequireInt32(mongo.Value, "p8WriteCount"),
                "Mongo P8 count disagrees with barrier.");
            RequireEqual(
                0,
                RequireInt32(mongo.Value, "p9WriteCount"),
                "Mongo P9 count disagrees with barrier.");
        }
        return Observation(
            "P8/P9 collections remained byte-semantically unchanged and CURRENT stayed exact v1.3 while candidate v1.4 remained non-current.",
            "p8-writes=0;p9-writes=0;current=1.3");
    }

    private static CaseObservation ValidateRegression(
        JsonLoadResult regression,
        ChildIdentity identity)
    {
        var root = RequireLoaded(regression, RegressionFileName);
        RequireEqual(
            RegressionContract,
            RequireString(root, "contract"),
            "Regression contract drift.");
        RequireEqual(
            identity.RunKey,
            RequireString(root, "runKey"),
            "Regression run key drift.");
        foreach (var property in new[]
                 {
                     "p0ThroughP6Full",
                     "backendContracts",
                     "backendBuild",
                     "frontendTypes",
                     "frontendTests",
                     "frontendLint",
                     "frontendProductionBuild",
                     "historicalCatalogPins",
                     "passed"
                 })
        {
            RequireTrue(root, property);
        }
        var commands = RequireArray(root, "commands");
        Require(
            commands.GetArrayLength() >= 7,
            "Regression ledger lacks exact command coverage.");
        foreach (var command in commands.EnumerateArray())
        {
            RequireNonEmpty(command, "commandId");
            Require(
                IsLowerSha256(
                    RequireString(command, "semanticSha256")),
                "Regression command semanticSha256 must be a lowercase SHA-256.");
            RequireTrue(command, "passed");
        }
        return Observation(
            "Full P0-P6 regression, backend contracts/build, FE types/tests/lint/production build and historical catalog/pin oracles passed.",
            $"regression-commands={commands.GetArrayLength()};all=true");
    }

    private static CaseObservation ValidateArtifactHashes(
        JsonLoadResult manifest,
        string? runRoot)
    {
        var root = RequireLoaded(manifest, ChildManifestFileName);
        RequireDirectory(runRoot, "child run root");
        var declared = RequireArray(root, "artifactHashes");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in declared.EnumerateArray())
        {
            var path = NormalizeRelativePath(
                RequireString(item, "path"));
            var sha = RequireString(item, "sha256");
            Require(IsLowerSha256(sha), $"{path} SHA is invalid.");
            Require(
                hashes.TryAdd(path, sha),
                $"Duplicate artifact hash declaration {path}.");
        }
        RequireSetsEqual(
            ExpectedHashedArtifactPaths,
            hashes.Keys,
            "Hashed artifact path set drift.");
        foreach (var expected in ExpectedHashedArtifactPaths)
        {
            var fullPath = ResolveChildArtifactPath(runRoot!, expected);
            Require(
                File.Exists(fullPath),
                $"Hashed artifact is missing: {expected}.");
            RequireEqual(
                hashes[expected],
                HashFile(fullPath),
                $"Artifact SHA drift: {expected}.");
        }
        return Observation(
            "Every required child ledger, regression/barrier artifact and process cleanup manifest matched its sealed SHA-256.",
            $"artifact-hashes={hashes.Count};all-match=true");
    }

    private static CleanupAudit InspectCleanup(
        string? runRoot,
        string? iterationRoot,
        ChildIdentity identity,
        JsonElement? cleanupDocument)
    {
        var failures = new List<string>();
        if (cleanupDocument is null)
        {
            failures.Add($"{CleanupFileName} is missing or invalid.");
        }
        else
        {
            var root = cleanupDocument.Value;
            TryRequire(
                failures,
                () => RequireEqual(
                    CleanupContract,
                    RequireString(root, "contract"),
                    "Cleanup contract drift."));
            TryRequire(
                failures,
                () => RequireEqual(
                    identity.RunKey,
                    RequireString(root, "runKey"),
                    "Cleanup run key drift."));
            foreach (var property in new[]
                     {
                         "databaseDropped",
                         "backendStopped",
                         "backendPortReleased",
                         "mongoStopped",
                         "mongoPortReleased",
                         "mongoDataRemoved",
                         "cleanupPassed"
                     })
            {
                TryRequire(failures, () => RequireTrue(root, property));
            }
            TryRequire(
                failures,
                () => RequireEqual(
                    identity.BackendProcessId,
                    RequireInt32(root, "backendProcessId"),
                    "Cleanup backend PID drift."));
            TryRequire(
                failures,
                () => RequireEqual(
                    identity.MongoProcessId,
                    RequireInt32(root, "mongoProcessId"),
                    "Cleanup Mongo PID drift."));
            TryRequire(
                failures,
                () => RequireEqual(
                    identity.BackendPort,
                    RequireInt32(root, "backendPort"),
                    "Cleanup backend port drift."));
            TryRequire(
                failures,
                () => RequireEqual(
                    identity.MongoPort,
                    RequireInt32(root, "mongoPort"),
                    "Cleanup Mongo port drift."));
        }

        var mongoManifest = InspectProcessCleanupManifest(
            iterationRoot is null
                ? null
                : Path.Combine(iterationRoot, MongoCleanupFileName),
            identity.MongoProcessId,
            identity.MongoPort,
            "Mongo",
            failures);
        var backendManifest = InspectProcessCleanupManifest(
            iterationRoot is null
                ? null
                : Path.Combine(
                    iterationRoot,
                    BackendCleanupRelativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)),
            identity.BackendProcessId,
            identity.BackendPort,
            "Backend",
            failures);
        var childProcessAbsent =
            identity.ChildProcessId > 0 &&
            !IsProcessRunning(identity.ChildProcessId);
        if (!childProcessAbsent)
        {
            failures.Add(
                $"Child PID {identity.ChildProcessId} is still present.");
        }

        var dataPath = cleanupDocument is null
            ? null
            : GetString(cleanupDocument.Value, "mongoDataPath");
        var dataPathSafe = false;
        var dataPathAbsent = false;
        if (runRoot is not null && !string.IsNullOrWhiteSpace(dataPath))
        {
            var fullRunRoot = Path.GetFullPath(runRoot);
            var fullDataPath = Path.GetFullPath(dataPath);
            dataPathSafe = IsWithinRoot(fullRunRoot, fullDataPath);
            dataPathAbsent = dataPathSafe &&
                             !Directory.Exists(fullDataPath) &&
                             !File.Exists(fullDataPath);
        }
        if (!dataPathSafe)
            failures.Add("Mongo data path is absent or outside child root.");
        if (!dataPathAbsent)
            failures.Add("Mongo data directory still exists.");

        return new CleanupAudit(
            failures.Count == 0,
            childProcessAbsent,
            mongoManifest,
            backendManifest,
            dataPathSafe,
            dataPathAbsent,
            failures.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static ProcessCleanupAudit InspectProcessCleanupManifest(
        string? path,
        int expectedProcessId,
        int expectedPort,
        string kind,
        ICollection<string> failures)
    {
        var load = LoadJson(path);
        if (load.Root is null)
        {
            failures.Add(
                $"{kind} cleanup manifest: {load.Error ?? "missing"}.");
            return new ProcessCleanupAudit(
                path ?? string.Empty,
                expectedProcessId,
                expectedPort,
                false,
                false,
                false,
                false);
        }
        var root = load.Root.Value;
        var processId = GetInt32(root, "processId") ?? 0;
        var port = GetInt32(root, "port") ?? 0;
        var manifestStopped =
            GetBoolean(root, "processStopped") == true ||
            GetBoolean(root, "stopped") == true ||
            GetBoolean(root, "stopVerified") == true;
        var manifestPortReleased =
            GetBoolean(root, "portReleased") == true ||
            GetBoolean(root, "portReleaseVerified") == true ||
            GetBoolean(root, "portReleasedVerified") == true;
        var processAbsent = processId > 0 && !IsProcessRunning(processId);
        var portBindable = port is > 0 and <= 65535 &&
                           IsPortBindable(port);
        if (processId != expectedProcessId)
            failures.Add($"{kind} cleanup PID drift.");
        if (port != expectedPort)
            failures.Add($"{kind} cleanup port drift.");
        if (!manifestStopped)
            failures.Add($"{kind} manifest does not attest process stop.");
        if (!manifestPortReleased)
            failures.Add($"{kind} manifest does not attest port release.");
        if (!processAbsent)
            failures.Add($"{kind} PID {processId} is still present.");
        if (!portBindable)
            failures.Add($"{kind} port {port} is not bindable.");
        return new ProcessCleanupAudit(
            path ?? string.Empty,
            processId,
            port,
            manifestStopped,
            manifestPortReleased,
            processAbsent,
            portBindable);
    }

    private static ArtifactSecurityAudit InspectArtifactSecurity(
        IEnumerable<string?> roots)
    {
        var failures = new List<string>();
        var scannedJson = 0;
        var scannedText = 0;
        var scannedLogs = 0;
        var normalizedRoots = roots
            .Where(root => root is not null && Directory.Exists(root))
            .Select(root => Path.GetFullPath(root!))
            .Distinct(PathComparer)
            .ToArray();
        if (normalizedRoots.Length == 0)
        {
            return new ArtifactSecurityAudit(
                false,
                0,
                0,
                0,
                ["No artifact root was available for security scan."]);
        }

        foreach (var root in normalizedRoots)
        {
            foreach (var path in Directory.EnumerateFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories)
                     .OrderBy(path => path, PathComparer))
            {
                var relative = Path.GetRelativePath(root, path)
                    .Replace('\\', '/');
                var fileName = Path.GetFileName(path);
                if (IsForbiddenLeftoverFile(fileName))
                    failures.Add($"{relative}: forbidden leftover file.");
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".json")
                {
                    scannedJson++;
                    try
                    {
                        var text = File.ReadAllText(path);
                        InspectTextSecrets(text, relative, failures);
                        using var document = JsonDocument.Parse(text);
                        InspectJsonValue(
                            document.RootElement,
                            relative,
                            "$",
                            failures);
                    }
                    catch (Exception error) when (
                        error is IOException or JsonException)
                    {
                        failures.Add(
                            $"{relative}: invalid/unreadable JSON ({error.GetType().Name}).");
                    }
                    continue;
                }
                if (extension == ".jsonl")
                {
                    scannedJson++;
                    InspectTextFile(path, relative, failures);
                    continue;
                }
                if (extension is ".log" or ".out" or ".err")
                {
                    scannedLogs++;
                    InspectTextFile(path, relative, failures);
                    continue;
                }
                if (extension is ".txt" or ".csv" or ".md")
                {
                    scannedText++;
                    InspectTextFile(path, relative, failures);
                }
            }
        }

        return new ArtifactSecurityAudit(
            failures.Count == 0,
            scannedJson,
            scannedText,
            scannedLogs,
            failures.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void InspectTextFile(
        string path,
        string relative,
        ICollection<string> failures)
    {
        try
        {
            InspectTextSecrets(
                File.ReadAllText(path),
                relative,
                failures);
        }
        catch (IOException error)
        {
            failures.Add(
                $"{relative}: unreadable text ({error.GetType().Name}).");
        }
    }

    private static void InspectTextSecrets(
        string text,
        string relative,
        ICollection<string> failures)
    {
        if (BearerSecretPattern.IsMatch(text) ||
            JwtPattern.IsMatch(text) ||
            P7PreviewTokenPattern.IsMatch(text))
            failures.Add($"{relative}: raw bearer/JWT-like token.");
        if (CredentialedMongoUriPattern.IsMatch(text))
            failures.Add($"{relative}: credentialed Mongo URI.");
        if (SensitiveAssignmentPattern.IsMatch(text))
            failures.Add($"{relative}: raw secret/key assignment.");
        foreach (var sentinel in RawSourceSentinels)
        {
            if (text.Contains(sentinel, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{relative}: raw source sentinel {sentinel}.");
            }
        }
    }

    private static void InspectJsonValue(
        JsonElement element,
        string relative,
        string jsonPath,
        ICollection<string> failures)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = $"{jsonPath}.{property.Name}";
                    if (SensitivePropertyNames.Contains(property.Name) &&
                        !IsRedactedValue(property.Value))
                    {
                        failures.Add(
                            $"{relative}:{propertyPath}: sensitive value is not redacted.");
                    }
                    InspectJsonValue(
                        property.Value,
                        relative,
                        propertyPath,
                        failures);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    InspectJsonValue(
                        item,
                        relative,
                        $"{jsonPath}[{index++}]",
                        failures);
                }
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                if (BearerSecretPattern.IsMatch(value) ||
                    JwtPattern.IsMatch(value) ||
                    P7PreviewTokenPattern.IsMatch(value))
                {
                    failures.Add(
                        $"{relative}:{jsonPath}: raw bearer/JWT-like token.");
                }
                if (CredentialedMongoUriPattern.IsMatch(value))
                {
                    failures.Add(
                        $"{relative}:{jsonPath}: credentialed Mongo URI.");
                }
                break;
        }
    }

    private static IReadOnlyList<HarnessCaseResult> BuildAggregateCases(
        IReadOnlyList<P7ChaosIterationEvidence> iterations)
        =>
        [
            EvaluateCase(
                "P7-11-AGGREGATE-EXACT-60-PLUS-CHAOS",
                () =>
                {
                    RequireEqual(
                        2,
                        iterations.Count,
                        "Exactly two child iterations are required.");
                    foreach (var iteration in iterations)
                    {
                        RequireEqual(
                            ExpectedCoreCaseIds.Length +
                            ExpectedChaosCaseIds.Length,
                            iteration.SemanticCases.Count,
                            $"Iteration {iteration.Iteration} semantic case count drift.");
                        Require(
                            iteration.SemanticCases.All(
                                result =>
                                    result.Verdict ==
                                    HarnessVerdict.DAT),
                            $"Iteration {iteration.Iteration} has a non-DAT real case.");
                    }
                    return Observation(
                        "Both children passed exact 60 core MAP cases plus every required explicit race/chaos case.",
                        $"iterations=2;core-per-run=60;chaos-per-run={ExpectedChaosCaseIds.Length}");
                }),
            EvaluateCase(
                "P7-11-AGGREGATE-DETERMINISTIC-SHA",
                () =>
                {
                    RequireEqual(
                        2,
                        iterations.Count,
                        "Exactly two child iterations are required.");
                    var shas = iterations.Select(
                            iteration =>
                                iteration.NormalizedSemanticSha256)
                        .ToArray();
                    Require(
                        shas.All(IsLowerSha256),
                        "A normalized semantic SHA is invalid.");
                    RequireEqual(
                        1,
                        shas.Distinct(StringComparer.Ordinal).Count(),
                        "Clean child semantic SHAs differ.");
                    return Observation(
                        "Both clean children produced the same normalized caseId/verdict/fingerprint SHA.",
                        $"semantic-sha={shas[0]}");
                }),
            EvaluateCase(
                "P7-11-AGGREGATE-DISTINCT-IDENTITIES",
                () =>
                {
                    var result = ValidateDistinctIdentities(
                        iterations.Select(
                                iteration => iteration.Child)
                            .ToArray());
                    Require(result.Passed, result.Failure ?? "Identity drift.");
                    return Observation(
                        "Child/run/backend/Mongo/database/replica-set identities are distinct across both clean iterations.",
                        "distinct=run+childPid+backendPid+mongoPid+database+replicaSet");
                }),
            EvaluateCase(
                "P7-11-AGGREGATE-CLEANUP-SECURITY",
                () =>
                {
                    Require(
                        iterations.All(
                            iteration =>
                                iteration.Child.Cleanup.Passed),
                        "A child cleanup audit failed.");
                    Require(
                        iterations.All(
                            iteration =>
                                iteration.Child.Security.Passed),
                        "A child artifact/log security audit failed.");
                    return Observation(
                        "Both child process trees, ports, databases and data directories were cleaned and all logs/artifacts were security-scanned.",
                        "cleanup=true;security=true;iterations=2");
                }),
            EvaluateCase(
                "P7-11-AGGREGATE-P8-P9-ZERO-WRITE",
                () =>
                {
                    Require(
                        iterations.All(
                            iteration =>
                                iteration.Child.P8P9ZeroWritePassed),
                        "A child P8/P9 barrier failed.");
                    return Observation(
                        "Both runs retained the P8/P9 zero-write barriers and CURRENT v1.3.",
                        "p8-writes=0;p9-writes=0;current=1.3;iterations=2");
                })
        ];

    private static IdentityAudit ValidateDistinctIdentities(
        IReadOnlyList<P7ChaosChildEvidence> children)
    {
        if (children.Count != 2)
        {
            return new IdentityAudit(
                false,
                $"Expected two child identities, found {children.Count}.");
        }
        var failures = new List<string>();
        RequireDistinct(
            children.Select(child => child.ChildRunKey),
            "run keys",
            failures);
        RequireDistinct(
            children.Select(child => child.Identity.ChildProcessId),
            "child PIDs",
            failures);
        RequireDistinct(
            children.Select(child => child.Identity.BackendProcessId),
            "backend PIDs",
            failures);
        RequireDistinct(
            children.Select(child => child.Identity.MongoProcessId),
            "Mongo PIDs",
            failures);
        RequireDistinct(
            children.Select(child => child.Identity.DatabaseName),
            "database names",
            failures);
        RequireDistinct(
            children.Select(child => child.Identity.ReplicaSetName),
            "replica-set names",
            failures);
        return new IdentityAudit(
            failures.Count == 0,
            failures.Count == 0
                ? null
                : string.Join("; ", failures));
    }

    private static void RequireDistinct<T>(
        IEnumerable<T> values,
        string subject,
        ICollection<string> failures)
    {
        var array = values.ToArray();
        if (array.Length != 2 ||
            array.Any(value => value is null) ||
            array.Distinct().Count() != 2)
        {
            failures.Add($"{subject} are not two distinct values.");
        }
    }

    private static P7ChaosChildEvidence BuildMissingChildEvidence(
        int iteration,
        string failure)
    {
        var validation = new[]
        {
            new HarnessCaseResult(
                $"P7-11-I{iteration:00}-CHILD-SURFACE",
                HarnessVerdict.KHONG_DAT,
                failure,
                "missing-p7-11-child-surface",
                0)
        };
        return new P7ChaosChildEvidence(
            iteration,
            -1,
            0,
            string.Empty,
            null,
            null,
            false,
            ChildIdentity.Empty,
            [],
            string.Empty,
            validation,
            CleanupAudit.Failed(failure),
            ArtifactSecurityAudit.Failed(failure),
            false,
            failure);
    }

    private static async Task WriteIterationEvidenceAsync(
        HarnessPaths paths,
        string runKey,
        string iterationRoot,
        P7ChaosIterationEvidence iteration,
        CancellationToken ct)
    {
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "semantic-cases.json"),
            new
            {
                schemaVersion = 1,
                gate = GateId,
                runKey,
                iteration.Iteration,
                exactCoreCount = iteration.SemanticCases.Count(
                    result => result.CaseId.StartsWith(
                        "MAP-",
                        StringComparison.Ordinal)),
                exactChaosCount = iteration.SemanticCases.Count(
                    result => result.CaseId.StartsWith(
                        "P7-11-",
                        StringComparison.Ordinal)),
                iteration.NormalizedSemanticSha256,
                iteration.AllRealCasesGreen,
                cases = iteration.SemanticCases,
                validations = iteration.Child.ValidationCases
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(iterationRoot, "artifact-index.json"),
            new
            {
                schemaVersion = 1,
                gate = GateId,
                runKey,
                iteration.Iteration,
                child = new
                {
                    iteration.Child.ChildRunKey,
                    iteration.Child.ExitCode,
                    iteration.Child.SpawnedProcessId,
                    runRoot = RelativePath(paths, iteration.Child.RunRoot),
                    iterationRoot =
                        RelativePath(paths, iteration.Child.IterationRoot),
                    iteration.Child.FreshRoot,
                    iteration.Child.Identity,
                    iteration.Child.Cleanup,
                    iteration.Child.Security,
                    iteration.Child.P8P9ZeroWritePassed,
                    iteration.Child.OrchestrationFailure
                }
            },
            ct);
    }

    private static object BuildIterationProjection(
        HarnessPaths paths,
        P7ChaosIterationEvidence iteration)
        => new
        {
            iteration.Iteration,
            iteration.AllRealCasesGreen,
            semanticCaseCount = iteration.SemanticCases.Count,
            iteration.NormalizedSemanticSha256,
            child = new
            {
                iteration.Child.ChildRunKey,
                iteration.Child.ExitCode,
                iteration.Child.SpawnedProcessId,
                runRoot = RelativePath(paths, iteration.Child.RunRoot),
                iterationRoot =
                    RelativePath(paths, iteration.Child.IterationRoot),
                iteration.Child.FreshRoot,
                iteration.Child.Identity,
                validationPassed = iteration.Child.ValidationCases.All(
                    result => result.Verdict == HarnessVerdict.DAT),
                iteration.Child.Cleanup,
                iteration.Child.Security,
                iteration.Child.P8P9ZeroWritePassed,
                iteration.Child.OrchestrationFailure
            }
        };

    private static object BuildReconciliationProjection(
        HarnessPaths paths,
        string runKey,
        IReadOnlyList<P7ChaosIterationEvidence> iterations,
        IdentityAudit identity,
        bool semanticShaMatch,
        bool deliberateFailure,
        bool deliberateControlPassed,
        bool passed)
        => new
        {
            schemaVersion = 1,
            gate = GateId,
            runKey,
            expectedIterations = 2,
            actualIterations = iterations.Count,
            expectedCoreCaseIds = ExpectedCoreCaseIds,
            expectedChaosCaseIds = ExpectedChaosCaseIds,
            iterations = iterations.Select(
                iteration => new
                {
                    iteration.Iteration,
                    iteration.Child.ChildRunKey,
                    childRoot =
                        RelativePath(paths, iteration.Child.RunRoot),
                    iteration.Child.Identity,
                    coreCount = iteration.SemanticCases.Count(
                        result => result.CaseId.StartsWith(
                            "MAP-",
                            StringComparison.Ordinal)),
                    chaosCount = iteration.SemanticCases.Count(
                        result => result.CaseId.StartsWith(
                            "P7-11-",
                            StringComparison.Ordinal)),
                    iteration.NormalizedSemanticSha256,
                    iteration.AllRealCasesGreen,
                    cleanupPassed = iteration.Child.Cleanup.Passed,
                    securityPassed = iteration.Child.Security.Passed,
                    iteration.Child.P8P9ZeroWritePassed
                }),
            semanticShaMatch,
            identitiesDistinct = identity.Passed,
            identity.Failure,
            deliberateFailure,
            deliberateControlPassed,
            passed
        };

    private static ChildIdentity ReadIdentity(JsonElement? manifest)
    {
        if (manifest is null)
            return ChildIdentity.Empty;
        var root = manifest.Value;
        return new ChildIdentity(
            GetString(root, "runKey") ?? string.Empty,
            GetInt32(root, "childProcessId") ?? 0,
            GetInt32(root, "backendProcessId") ?? 0,
            GetInt32(root, "mongoProcessId") ?? 0,
            GetInt32(root, "backendPort") ?? 0,
            GetInt32(root, "mongoPort") ?? 0,
            GetString(root, "databaseName") ?? string.Empty,
            GetString(root, "replicaSetName") ?? string.Empty);
    }

    private static SemanticLedgerRead ReadSemanticCases(
        JsonElement? ledger)
    {
        if (ledger is null)
        {
            return new SemanticLedgerRead(
                [],
                [],
                string.Empty,
                "Case ledger is unavailable.");
        }

        var results = new List<HarnessCaseResult>();
        var stableCases = new List<VerifiedStableSemanticCase>();
        var failures = new List<string>();
        foreach (var property in new[] { "coreCases", "chaosCases" })
        {
            var array = GetProperty(ledger.Value, property);
            if (array is null ||
                array.Value.ValueKind != JsonValueKind.Array)
            {
                failures.Add($"{property} must be an array.");
                continue;
            }

            foreach (var item in array.Value.EnumerateArray())
            {
                var caseId = GetString(item, "caseId") ?? string.Empty;
                var verdictText = GetString(item, "verdict");
                var verdict = System.Enum.TryParse<HarnessVerdict>(
                    verdictText,
                    ignoreCase: false,
                    out var parsed)
                    ? parsed
                    : HarnessVerdict.KHONG_DAT;
                results.Add(
                    new HarnessCaseResult(
                        caseId,
                        verdict,
                        GetString(item, "detail") ?? string.Empty,
                        GetString(item, "fingerprint") ??
                        string.Empty,
                        GetInt64(item, "durationMs") ?? 0));

                try
                {
                    Require(
                        item.ValueKind == JsonValueKind.Object,
                        "Semantic case row must be an object.");
                    var realKestrel =
                        GetBoolean(item, "realKestrel") ?? false;
                    var directMongo =
                        GetBoolean(item, "directMongo") ?? false;
                    var evidenceBound =
                        GetBoolean(item, "evidenceBound") ?? false;
                    var expectedOutcome = BuildStableSemanticOutcome(
                        caseId,
                        verdictText ?? string.Empty,
                        realKestrel,
                        directMongo,
                        evidenceBound);

                    var outcomeRoot = RequireProperty(
                        item,
                        "stableSemanticOutcome");
                    Require(
                        outcomeRoot.ValueKind == JsonValueKind.Object,
                        $"{caseId} stableSemanticOutcome must be an object.");
                    var propertyNames = outcomeRoot
                        .EnumerateObject()
                        .Select(value => value.Name)
                        .ToArray();
                    RequireSetsEqual(
                        StableSemanticOutcomePropertyNames,
                        propertyNames,
                        $"{caseId} stable semantic outcome property set drift.");

                    var declaredOutcome = new StableSemanticOutcome(
                        RequireInt32(outcomeRoot, "schemaVersion"),
                        RequireString(outcomeRoot, "contract"),
                        RequireString(
                            outcomeRoot,
                            "semanticContractIdentity"),
                        RequireString(outcomeRoot, "caseId"),
                        RequireString(outcomeRoot, "verdict"),
                        GetBoolean(outcomeRoot, "realKestrel") ?? false,
                        GetBoolean(outcomeRoot, "directMongo") ?? false,
                        GetBoolean(outcomeRoot, "evidenceBound") ?? false);
                    RequireEqual(
                        expectedOutcome,
                        declaredOutcome,
                        $"{caseId} stable semantic outcome drift.");

                    var recomputedFingerprint =
                        BuildStableSemanticFingerprint(expectedOutcome);
                    var declaredFingerprint = RequireString(
                        item,
                        "stableSemanticFingerprint");
                    Require(
                        IsLowerSha256(declaredFingerprint),
                        $"{caseId} stable semantic fingerprint is invalid.");
                    RequireEqual(
                        recomputedFingerprint,
                        declaredFingerprint,
                        $"{caseId} stable semantic fingerprint drift.");
                    stableCases.Add(
                        new VerifiedStableSemanticCase(
                            caseId,
                            expectedOutcome.Verdict,
                            recomputedFingerprint));
                }
                catch (Exception error)
                {
                    failures.Add(
                        $"{(string.IsNullOrWhiteSpace(caseId) ? "<missing-caseId>" : caseId)}: {error.Message}");
                }
            }
        }

        var normalizedSemanticSha256 = string.Empty;
        if (failures.Count == 0)
        {
            try
            {
                normalizedSemanticSha256 =
                    BuildNormalizedSha256(stableCases);
            }
            catch (Exception error)
            {
                failures.Add(error.Message);
            }
        }

        return new SemanticLedgerRead(
            results,
            stableCases,
            normalizedSemanticSha256,
            failures.Count == 0
                ? null
                : string.Join("; ", failures));
    }

    private static IReadOnlySet<string> ReadEvidenceIds(
        JsonElement? root,
        string arrayProperty)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (root is null)
            return ids;
        var array = GetProperty(root.Value, arrayProperty);
        if (array is null ||
            array.Value.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }
        foreach (var item in array.Value.EnumerateArray())
        {
            var id = GetString(item, "evidenceId");
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }
        return ids;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>
        ReadCaseEvidence(
        JsonElement root,
        string propertyName)
    {
        var result =
            new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.Ordinal);
        var array = RequireArray(root, "caseEvidence");
        foreach (var item in array.EnumerateArray())
        {
            var caseId = RequireString(item, "caseId");
            Require(
                result.TryAdd(
                    caseId,
                    RequireStringArray(item, propertyName)),
                $"Duplicate caseEvidence row {caseId}.");
        }
        return result;
    }

    private static JsonLoadResult LoadJson(string? path)
    {
        if (path is null || !File.Exists(path))
            return new JsonLoadResult(null, "file missing");
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new JsonLoadResult(
                    null,
                    $"expected Object, got {document.RootElement.ValueKind}");
            }
            return new JsonLoadResult(
                document.RootElement.Clone(),
                null);
        }
        catch (Exception error) when (
            error is IOException or JsonException)
        {
            return new JsonLoadResult(
                null,
                $"{error.GetType().Name}: {error.Message}");
        }
    }

    private static HarnessCaseResult EvaluateCase(
        string caseId,
        Func<CaseObservation> action)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var observation = action();
            return new HarnessCaseResult(
                caseId,
                HarnessVerdict.DAT,
                observation.Detail,
                observation.Fingerprint,
                timer.ElapsedMilliseconds);
        }
        catch (Exception error)
        {
            return new HarnessCaseResult(
                caseId,
                HarnessVerdict.KHONG_DAT,
                $"{error.GetType().Name}: {error.Message}",
                $"validation-failed:{caseId}",
                timer.ElapsedMilliseconds);
        }
    }

    private static CaseObservation Observation(
        string detail,
        string fingerprint)
        => new(detail, fingerprint);

    private static StableSemanticOutcome BuildStableSemanticOutcome(
        string caseId,
        string verdict,
        bool realKestrel,
        bool directMongo,
        bool evidenceBound)
    {
        Require(
            ExpectedSemanticCaseIdSet.Contains(caseId),
            $"Unknown stable semantic case ID {caseId}.");
        RequireEqual(
            HarnessVerdict.DAT.ToString(),
            verdict,
            $"{caseId} stable semantic verdict drift.");
        Require(
            realKestrel,
            $"{caseId} stable semantic outcome is not real-Kestrel bound.");
        Require(
            directMongo,
            $"{caseId} stable semantic outcome is not direct-Mongo bound.");
        Require(
            evidenceBound,
            $"{caseId} stable semantic outcome is not evidence-bound.");
        return new StableSemanticOutcome(
            StableSemanticOutcomeSchemaVersion,
            StableSemanticOutcomeContract,
            $"{StableSemanticContractIdentityPrefix}/{caseId}",
            caseId,
            verdict,
            realKestrel,
            directMongo,
            evidenceBound);
    }

    private static string BuildStableSemanticFingerprint(
        StableSemanticOutcome outcome)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", outcome.SchemaVersion);
            writer.WriteString("contract", outcome.Contract);
            writer.WriteString(
                "semanticContractIdentity",
                outcome.SemanticContractIdentity);
            writer.WriteString("caseId", outcome.CaseId);
            writer.WriteString("verdict", outcome.Verdict);
            writer.WriteBoolean("realKestrel", outcome.RealKestrel);
            writer.WriteBoolean("directMongo", outcome.DirectMongo);
            writer.WriteBoolean("evidenceBound", outcome.EvidenceBound);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()))
            .ToLowerInvariant();
    }

    private static string BuildNormalizedSha256(
        IReadOnlyList<VerifiedStableSemanticCase> cases)
    {
        RequireEqual(
            ExpectedSemanticCaseIds.Length,
            cases.Count,
            "Normalized stable semantic case count drift.");
        var ids = cases.Select(item => item.CaseId).ToArray();
        RequireEqual(
            ids.Length,
            ids.Distinct(StringComparer.Ordinal).Count(),
            "Normalized stable semantic case IDs contain duplicates.");
        Require(
            ids.All(ExpectedSemanticCaseIdSet.Contains) &&
            ExpectedSemanticCaseIdSet.SetEquals(ids),
            "Normalized stable semantic case set drift.");
        Require(
            cases.All(
                item =>
                    string.Equals(
                        item.Verdict,
                        HarnessVerdict.DAT.ToString(),
                        StringComparison.Ordinal) &&
                    IsLowerSha256(item.StableSemanticFingerprint)),
            "Normalized stable semantic inputs are invalid.");

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var item in cases.OrderBy(
                         value => value.CaseId,
                         StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("caseId", item.CaseId);
                writer.WriteString("verdict", item.Verdict);
                writer.WriteString(
                    "stableSemanticFingerprint",
                    item.StableSemanticFingerprint);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()))
            .ToLowerInvariant();
    }

    private static int ParseIterations(string[] args)
    {
        var requested = 2;
        for (var index = 0; index < args.Length; index++)
        {
            string? raw = null;
            if (args[index].StartsWith(
                    "--iterations=",
                    StringComparison.OrdinalIgnoreCase))
            {
                raw = args[index]["--iterations=".Length..];
            }
            else if (string.Equals(
                         args[index],
                         "--iterations",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length)
            {
                raw = args[++index];
            }
            if (raw is not null)
            {
                if (!int.TryParse(raw, out requested))
                {
                    throw new ArgumentException(
                        "--iterations must be exactly 2 for P7-11.");
                }
            }
        }
        if (requested != 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "P7-11 requires exactly two clean isolated child iterations.");
        }
        return requested;
    }

    private static TimeSpan ParseChildTimeout()
    {
        var raw = Environment.GetEnvironmentVariable(
            "TDTD_P7_CHAOS_CHILD_TIMEOUT_SECONDS");
        var seconds = int.TryParse(raw, out var parsed)
            ? parsed
            : 3_600;
        if (seconds is < 60 or > 7_200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(raw),
                "TDTD_P7_CHAOS_CHILD_TIMEOUT_SECONDS must be 60..7200.");
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private static string BuildRunKey()
        => $"p711_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}";

    private static IEnumerable<string> EnumerateChildRoots(
        string integrationRoot)
    {
        if (!Directory.Exists(integrationRoot))
            return [];
        return Directory.EnumerateDirectories(
                integrationRoot,
                $"{ChildRunPrefix}*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, PathComparer)
            .ToArray();
    }

    private static string BuildFailureReason(
        bool deliberateFailure,
        ChildSurfaceAudit surface,
        int requestedIterations,
        bool allRealCasesGreen,
        bool cleanupPassed,
        bool securityPassed,
        bool p8P9ZeroWritePassed,
        IdentityAudit identity,
        bool semanticShaMatch,
        IReadOnlyList<HarnessCaseResult> aggregateCases,
        bool deliberateControlPassed)
    {
        var failures = new List<string>();
        if (!surface.Available)
            failures.Add(surface.Failure ?? "Child surface unavailable.");
        if (requestedIterations != 2)
            failures.Add("Exactly two iterations were not requested.");
        if (!allRealCasesGreen)
            failures.Add("One or more real child cases/validations failed.");
        if (!cleanupPassed)
            failures.Add("Child cleanup failed.");
        if (!securityPassed)
            failures.Add("Artifact/log security scan failed.");
        if (!p8P9ZeroWritePassed)
            failures.Add("P8/P9 zero-write barrier failed.");
        if (!identity.Passed)
            failures.Add(identity.Failure ?? "Child identities are not distinct.");
        if (!semanticShaMatch)
            failures.Add("Two-run normalized semantic SHA did not match.");
        if (aggregateCases.Any(
                result => result.Verdict != HarnessVerdict.DAT))
        {
            failures.Add("One or more aggregate oracles failed.");
        }
        if (deliberateFailure && !deliberateControlPassed)
        {
            failures.Add(
                "Deliberate mode did not produce exactly one named oracle failure after all real cases passed.");
        }
        return failures.Count == 0
            ? deliberateFailure
                ? "Deliberate control produced the required single named failure."
                : string.Empty
            : string.Join(" ", failures);
    }

    private static JsonElement RequireLoaded(
        JsonLoadResult load,
        string subject)
    {
        if (load.Root is null)
        {
            throw new InvalidOperationException(
                $"{subject}: {load.Error ?? "unavailable"}.");
        }
        return load.Root.Value;
    }

    private static JsonElement RequireProperty(
        JsonElement root,
        string propertyName)
    {
        var value = GetProperty(root, propertyName);
        if (value is null)
        {
            throw new InvalidOperationException(
                $"Missing JSON property {propertyName}.");
        }
        return value.Value;
    }

    private static JsonElement? GetProperty(
        JsonElement root,
        string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }
        return null;
    }

    private static JsonElement RequireArray(
        JsonElement root,
        string propertyName)
    {
        var value = RequireProperty(root, propertyName);
        Require(
            value.ValueKind == JsonValueKind.Array,
            $"{propertyName} must be an array.");
        return value;
    }

    private static string RequireString(
        JsonElement root,
        string propertyName)
    {
        var value = GetString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{propertyName} must be a non-empty string.");
        }
        return value;
    }

    private static string? GetString(
        JsonElement root,
        string propertyName)
    {
        var value = GetProperty(root, propertyName);
        return value is { ValueKind: JsonValueKind.String }
            ? value.Value.GetString()
            : null;
    }

    private static int RequireInt32(
        JsonElement root,
        string propertyName)
    {
        var value = GetInt32(root, propertyName);
        if (value is null)
        {
            throw new InvalidOperationException(
                $"{propertyName} must be an Int32.");
        }
        return value.Value;
    }

    private static int? GetInt32(
        JsonElement root,
        string propertyName)
    {
        var value = GetProperty(root, propertyName);
        return value is { ValueKind: JsonValueKind.Number } &&
               value.Value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static long? GetInt64(
        JsonElement root,
        string propertyName)
    {
        var value = GetProperty(root, propertyName);
        return value is { ValueKind: JsonValueKind.Number } &&
               value.Value.TryGetInt64(out var result)
            ? result
            : null;
    }

    private static bool? GetBoolean(
        JsonElement root,
        string propertyName)
    {
        var value = GetProperty(root, propertyName);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static void RequireTrue(
        JsonElement root,
        string propertyName)
        => RequireEqual(
            true,
            GetBoolean(root, propertyName),
            $"{propertyName} must be true.");

    private static void RequireFalse(
        JsonElement root,
        string propertyName)
        => RequireEqual(
            false,
            GetBoolean(root, propertyName),
            $"{propertyName} must be false.");

    private static void RequireNonEmpty(
        JsonElement root,
        string propertyName)
        => _ = RequireString(root, propertyName);

    private static IReadOnlyList<string> RequireStringArray(
        JsonElement root,
        string propertyName)
    {
        var array = RequireArray(root, propertyName);
        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            Require(
                item.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(item.GetString()),
                $"{propertyName} contains a non-string/blank item.");
            values.Add(item.GetString()!);
        }
        return values;
    }

    private static void RequireSetsEqual(
        IEnumerable<string> expected,
        IEnumerable<string> actual,
        string message)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualArray = actual.ToArray();
        var actualSet = actualArray.ToHashSet(StringComparer.Ordinal);
        Require(
            actualArray.Length == actualSet.Count,
            $"{message} Duplicate values found.");
        Require(
            expectedSet.SetEquals(actualSet),
            $"{message} Expected=[{string.Join(',', expectedSet.Order())}] Actual=[{string.Join(',', actualSet.Order())}].");
    }

    private static void RequireEvidenceOwnershipExclusive(
        IReadOnlyDictionary<string, IReadOnlyList<string>> perCaseEvidence,
        string subject)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (caseId, evidenceIds) in perCaseEvidence)
        {
            foreach (var evidenceId in evidenceIds)
            {
                Require(
                    owners.TryAdd(evidenceId, caseId),
                    $"{subject} evidence {evidenceId} is reused by {owners[evidenceId]} and {caseId}; every real case needs owned evidence.");
            }
        }
    }

    private static void RequireDirectory(
        string? path,
        string subject)
        => Require(
            path is not null && Directory.Exists(path),
            $"{subject} is missing.");

    private static void Require(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequireEqual<T>(
        T expected,
        T actual,
        string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected={expected}; Actual={actual}.");
        }
    }

    private static void TryRequire(
        ICollection<string> failures,
        Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            failures.Add(error.Message);
        }
    }

    private static bool IsLowerSha256(string? value)
        => value is not null &&
           value.Length == 64 &&
           value.All(
               character =>
                   character is >= '0' and <= '9' ||
                   character is >= 'a' and <= 'f');

    private static bool IsRedactedValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ||
                   string.Equals(
                       text,
                       "<redacted>",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       text,
                       "[redacted]",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "***", StringComparison.Ordinal);
        }
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().All(IsRedactedValue);
        return false;
    }

    private static bool IsForbiddenLeftoverFile(string fileName)
    {
        if (string.Equals(
                fileName,
                ".stop",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.GetExtension(fileName),
                ".stop",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                fileName,
                ".secret",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                Path.GetExtension(fileName),
                ".secret",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return Regex.IsMatch(
            Path.GetFileNameWithoutExtension(fileName),
            @"(^|[-_.])secrets?($|[-_.])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsProcessRunning(int processId)
    {
        if (processId <= 0)
            return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsPortBindable(int port)
    {
        if (port is <= 0 or > 65535)
            return false;
        try
        {
            using var listener = new TcpListener(
                IPAddress.Loopback,
                port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsWithinRoot(
        string root,
        string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal) &&
               !relative.StartsWith(
                   $"..{Path.AltDirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    private static string NormalizeRelativePath(string path)
    {
        Require(
            !Path.IsPathRooted(path),
            "Artifact path must be relative.");
        var normalized = path.Replace('\\', '/');
        Require(
            !normalized.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries)
                .Contains("..", StringComparer.Ordinal),
            "Artifact path cannot traverse parents.");
        return normalized;
    }

    private static string ResolveChildArtifactPath(
        string runRoot,
        string relativePath)
    {
        var fullRoot = Path.GetFullPath(runRoot);
        var fullPath = Path.GetFullPath(
            Path.Combine(
                fullRoot,
                NormalizeRelativePath(relativePath).Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
        Require(
            IsWithinRoot(fullRoot, fullPath),
            "Artifact path escaped child root.");
        return fullPath;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static string? RelativePath(
        HarnessPaths paths,
        string? path)
    {
        if (path is null)
            return null;
        return Path.GetRelativePath(paths.WorkspaceRoot, path)
            .Replace('\\', '/');
    }

    private sealed record StableSemanticOutcome(
        int SchemaVersion,
        string Contract,
        string SemanticContractIdentity,
        string CaseId,
        string Verdict,
        bool RealKestrel,
        bool DirectMongo,
        bool EvidenceBound);

    private sealed record VerifiedStableSemanticCase(
        string CaseId,
        string Verdict,
        string StableSemanticFingerprint);

    private sealed record SemanticLedgerRead(
        IReadOnlyList<HarnessCaseResult> Cases,
        IReadOnlyList<VerifiedStableSemanticCase> StableCases,
        string NormalizedSemanticSha256,
        string? Failure);

    private sealed record JsonLoadResult(
        JsonElement? Root,
        string? Error);

    private sealed record ChildSurfaceAudit(
        bool Available,
        string TypeName,
        string CommandSwitch,
        string? Failure);

    private sealed record ChildIdentity(
        string RunKey,
        int ChildProcessId,
        int BackendProcessId,
        int MongoProcessId,
        int BackendPort,
        int MongoPort,
        string DatabaseName,
        string ReplicaSetName)
    {
        public static ChildIdentity Empty { get; } =
            new(string.Empty, 0, 0, 0, 0, 0, string.Empty, string.Empty);
    }

    private sealed record ProcessCleanupAudit(
        string Path,
        int ProcessId,
        int Port,
        bool ManifestStopped,
        bool ManifestPortReleased,
        bool ProcessAbsent,
        bool PortBindable);

    private sealed record CleanupAudit(
        bool Passed,
        bool ChildProcessAbsent,
        ProcessCleanupAudit Mongo,
        ProcessCleanupAudit Backend,
        bool MongoDataPathSafe,
        bool MongoDataAbsent,
        IReadOnlyList<string> Failures)
    {
        public static CleanupAudit Failed(string failure)
            => new(
                false,
                false,
                new ProcessCleanupAudit(
                    string.Empty,
                    0,
                    0,
                    false,
                    false,
                    false,
                    false),
                new ProcessCleanupAudit(
                    string.Empty,
                    0,
                    0,
                    false,
                    false,
                    false,
                    false),
                false,
                false,
                [failure]);
    }

    private sealed record ArtifactSecurityAudit(
        bool Passed,
        int ScannedJsonFiles,
        int ScannedTextFiles,
        int ScannedLogFiles,
        IReadOnlyList<string> Failures)
    {
        public static ArtifactSecurityAudit Failed(string failure)
            => new(false, 0, 0, 0, [failure]);
    }

    private sealed record IdentityAudit(
        bool Passed,
        string? Failure);

    private sealed record P7ChaosChildEvidence(
        int Iteration,
        int ExitCode,
        int SpawnedProcessId,
        string ChildRunKey,
        string? RunRoot,
        string? IterationRoot,
        bool FreshRoot,
        ChildIdentity Identity,
        IReadOnlyList<HarnessCaseResult> SemanticCases,
        string NormalizedSemanticSha256,
        IReadOnlyList<HarnessCaseResult> ValidationCases,
        CleanupAudit Cleanup,
        ArtifactSecurityAudit Security,
        bool P8P9ZeroWritePassed,
        string? OrchestrationFailure);

    private sealed record P7ChaosIterationEvidence(
        int Iteration,
        P7ChaosChildEvidence Child,
        IReadOnlyList<HarnessCaseResult> SemanticCases,
        string NormalizedSemanticSha256,
        bool AllRealCasesGreen);
}
