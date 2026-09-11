using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string P811ChildSwitch = "--p8-p811-full-matrix-child";
    private const string P811DeliberateFailureSwitch = "--deliberate-failure";

    private static bool HasArgument(string[] args, string expected)
        => args.Any(argument => string.Equals(
            argument,
            expected,
            StringComparison.OrdinalIgnoreCase));

    private static async Task<int> RunP811AggregateAsync(
        string[] args,
        CancellationToken ct)
    {
        var requestedIterations = ParseP811Iterations(args);
        var deliberateFailure = HasArgument(
            args,
            P811DeliberateFailureSwitch);
        var startedAtUtc = DateTime.UtcNow;
        var aggregateRunKey =
            $"p811_full_{startedAtUtc:yyyyMMddHHmmss}_{Environment.ProcessId}_" +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(4))
                .ToLowerInvariant();
        var paths = HarnessPaths.CreateP8(
            aggregateRunKey,
            ChainId,
            "P8-11");
        var childArgs = BuildP811ChildArguments(args);
        var iterations = new List<P811ChildEvidence>();

        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "cleanup-manifest.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                state = "ALLOCATING",
                requestedIterations,
                requiredIterations = 2,
                ownedRoots = new[] { paths.RunRoot },
                startedAtUtc
            },
            ct);

        Console.WriteLine($"P8-11 aggregate artifacts: {paths.RunRoot}");
        for (var iteration = 1; iteration <= requestedIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine(
                $"P8-11 full 200-case child {iteration}/{requestedIterations} starting.");
            var child = new P8StatConfigProbe(11);
            var exitCode = await child.ExecuteAsync(childArgs, ct);
            var evidence = await LoadP811ChildEvidenceAsync(
                iteration,
                exitCode,
                child._paths.RunRoot,
                ct);
            iterations.Add(evidence);
            Console.WriteLine(
                $"P8-11 child {iteration}: exit={exitCode}; cases={evidence.ActualCaseCount}; semantic={evidence.NormalizedSemanticSha256}; cleanup={evidence.CleanupSucceeded}.");
        }

        var exactIterationCount =
            requestedIterations == 2 && iterations.Count == 2;
        var allChildrenPassed = exactIterationCount && iterations.All(item =>
            item.ExitCode == 0 &&
            item.Passed &&
            item.ExactIds &&
            item.ExactExpectedCaseCount == AllP8CaseIds.Length &&
            item.ActualCaseCount == AllP8CaseIds.Length &&
            item.CaseIds.SequenceEqual(AllP8CaseIds, StringComparer.Ordinal) &&
            item.AllCasesDat &&
            item.DirectMongoEvidenceCases == AllP8CaseIds.Length &&
            item.ProhibitedCollectionDeltaZero);
        var cleanupPassed = exactIterationCount &&
                            iterations.All(item => item.CleanupSucceeded);
        var securityPassed = exactIterationCount &&
                             iterations.All(item => item.SecurityScanPassed);
        var sourceFingerprintPassed = exactIterationCount &&
                                      iterations.All(item =>
                                          item.SourceFingerprintPassed);
        var p7RegressionPassed = exactIterationCount &&
                                 iterations.All(item =>
                                     item.P7RegressionPassed);
        var identitiesDistinct = exactIterationCount &&
                                 iterations.Select(item => item.ChildRunKey)
                                     .Distinct(StringComparer.Ordinal).Count() == 2 &&
                                 iterations.Select(item => item.DatabaseName)
                                     .Distinct(StringComparer.Ordinal).Count() == 2 &&
                                 iterations.Select(item => item.ReplicaSetName)
                                     .Distinct(StringComparer.Ordinal).Count() == 2 &&
                                 iterations.Select(item => item.BackendProcessId)
                                     .Distinct().Count() == 2 &&
                                 iterations.Select(item => item.MongoProcessId)
                                     .Distinct().Count() == 2;
        var semanticShaMatch = exactIterationCount &&
                               iterations.All(item =>
                                   IsLowerSha256(item.NormalizedSemanticSha256)) &&
                               iterations.Select(item =>
                                       item.NormalizedSemanticSha256)
                                   .Distinct(StringComparer.Ordinal).Count() == 1;
        var sourceShaMatch = exactIterationCount &&
                             iterations.Select(item =>
                                     item.SourceFingerprintSemanticSha256)
                                 .Distinct(StringComparer.Ordinal).Count() == 1;
        var realGateSatisfied = exactIterationCount &&
                                allChildrenPassed &&
                                cleanupPassed &&
                                securityPassed &&
                                sourceFingerprintPassed &&
                                p7RegressionPassed &&
                                identitiesDistinct &&
                                semanticShaMatch &&
                                sourceShaMatch;

        var deliberateControl = BuildP811DeliberateControl(
            deliberateFailure,
            realGateSatisfied,
            iterations.FirstOrDefault());
        var deliberateControlPassed = deliberateFailure &&
                                      realGateSatisfied &&
                                      deliberateControl is not null &&
                                      deliberateControl.NamedFailureCount == 1 &&
                                      deliberateControl.UnexpectedFailureCount == 0 &&
                                      deliberateControl.Verdict == "KHONG_DAT";
        var passed = !deliberateFailure && realGateSatisfied;
        var completedAtUtc = DateTime.UtcNow;
        var failureReason = BuildP811AggregateFailureReason(
            deliberateFailure,
            requestedIterations,
            allChildrenPassed,
            cleanupPassed,
            securityPassed,
            sourceFingerprintPassed,
            p7RegressionPassed,
            identitiesDistinct,
            semanticShaMatch,
            sourceShaMatch,
            deliberateControlPassed);

        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot,
                "p8-11-two-clean-run-manifest.json"),
            new
            {
                schemaVersion = 1,
                gate = "P8-11-TWO-CLEAN-FULL-200",
                requirementOwned = "P8-STAT-024",
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                startedAtUtc,
                completedAtUtc,
                requestedIterations,
                requiredIterations = 2,
                expectedRegistryCaseCount = AllP8CaseIds.Length,
                expectedGroups = new[]
                {
                    "P8-CORE", "P8-LBL", "P8-FLD", "P8-TBL",
                    "P8-BAS", "P8-ADV", "P8-DIF", "P8-FLW",
                    "P8-OPS", "P8-BND", "P8-UI", "P8-RACE"
                },
                browserProofDeferredTo = "P8-12",
                browserEvidenceClaimed = false,
                normalizationFields = new[]
                {
                    "caseId", "verdict", "fingerprint"
                },
                volatileFieldsExcluded = new[]
                {
                    "runKey", "databaseName", "replicaSetName",
                    "processId", "port", "timestamp", "durationMs"
                },
                iterations,
                checks = new
                {
                    exactIterationCount,
                    allChildrenPassed,
                    cleanupPassed,
                    securityPassed,
                    sourceFingerprintPassed,
                    p7RegressionPassed,
                    identitiesDistinct,
                    semanticShaMatch,
                    sourceShaMatch,
                    deliberateFailureMode = deliberateFailure,
                    deliberateControlPassed
                },
                normalizedSemanticSha256 =
                    iterations.FirstOrDefault()?.NormalizedSemanticSha256,
                sourceFingerprintSemanticSha256 =
                    iterations.FirstOrDefault()?.SourceFingerprintSemanticSha256,
                realGateSatisfied,
                passed,
                failureReason
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "deliberate-control.json"),
            new
            {
                schemaVersion = 1,
                requested = deliberateFailure,
                control = deliberateControl,
                exactlyOneNamedOracleFailed = deliberateControlPassed,
                realChildrenRemainGreen = realGateSatisfied
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "cleanup-manifest.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                state = cleanupPassed ? "CLEANED" : "CLEANUP_FAILED",
                ownedRoots = new[] { paths.RunRoot },
                children = iterations.Select(item => new
                {
                    item.Iteration,
                    item.ChildRunKey,
                    item.RunRoot,
                    item.DatabaseName,
                    item.BackendProcessId,
                    item.MongoProcessId,
                    item.CleanupSucceeded
                }),
                cleanupPassed,
                completedAtUtc
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "p8-11-gate-result.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                exactExpectedCaseCountPerIteration = AllP8CaseIds.Length,
                completeCaseExecutions = iterations.Sum(item =>
                    item.ActualCaseCount),
                normalizedSemanticSha256 =
                    iterations.FirstOrDefault()?.NormalizedSemanticSha256,
                semanticShaMatch,
                cleanupPassed,
                securityPassed,
                sourceFingerprintPassed,
                p7RegressionPassed,
                identitiesDistinct,
                deliberateFailureMode = deliberateFailure,
                deliberateControlPassed,
                passed,
                failureReason
            },
            ct);

        Console.WriteLine(passed
            ? $"[DAT] P8-11 passed two isolated full 200-case runs; semanticSha={iterations[0].NormalizedSemanticSha256}; artifacts={paths.RunRoot}"
            : $"[KHONG_DAT] P8-11 aggregate: {failureReason}; artifacts={paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private static string[] BuildP811ChildArguments(string[] args)
    {
        var result = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--iterations",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length)
                    index++;
                continue;
            }
            if (string.Equals(args[index], P811DeliberateFailureSwitch,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[index], P811ChildSwitch,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            result.Add(args[index]);
        }
        result.Add(P811ChildSwitch);
        return result.ToArray();
    }

    private static int ParseP811Iterations(string[] args)
    {
        var value = 2;
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--iterations",
                    StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length &&
                int.TryParse(args[index + 1], out var parsed))
            {
                value = parsed;
            }
        }
        if (value is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "P8-11 aggregate iterations must be one or two; PASS requires exactly two.");
        }
        return value;
    }

    private static async Task<P811ChildEvidence> LoadP811ChildEvidenceAsync(
        int iteration,
        int exitCode,
        string runRoot,
        CancellationToken ct)
    {
        var gatePath = Path.Combine(runRoot, "p8-11-gate-result.json");
        var cleanupPath = Path.Combine(runRoot, "cleanup-manifest.json");
        var environmentPath = Path.Combine(runRoot, "environment.json");
        var securityPath = Path.Combine(runRoot, "security-scan.json");
        var sourcePath = Path.Combine(runRoot,
            "p8-race-source-fingerprint.json");
        var p7Path = Path.Combine(runRoot,
            "p8-race-p7-regression.json");
        foreach (var path in new[]
                 {
                     gatePath, cleanupPath, environmentPath, securityPath,
                     sourcePath, p7Path
                 })
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"P8-11 child evidence is missing: {path}");
            }
        }

        var gate = await ReadP811ObjectAsync(gatePath, ct);
        var cleanup = await ReadP811ObjectAsync(cleanupPath, ct);
        var environment = await ReadP811ObjectAsync(environmentPath, ct);
        var security = await ReadP811ObjectAsync(securityPath, ct);
        var source = await ReadP811ObjectAsync(sourcePath, ct);
        var p7 = await ReadP811ObjectAsync(p7Path, ct);
        var results = gate["results"] as JsonArray
                      ?? throw new InvalidOperationException(
                          "P8-11 child gate lacks results array.");
        var cases = results.Select(node =>
        {
            var row = node as JsonObject
                      ?? throw new InvalidOperationException(
                          "P8-11 child result row is malformed.");
            return new P811CaseProjection(
                P811String(row, "caseId"),
                P811String(row, "verdict"),
                P811String(row, "fingerprint"));
        }).OrderBy(row => row.CaseId, StringComparer.Ordinal).ToArray();
        var kestrel = environment["kestrel"] as JsonObject
                      ?? throw new InvalidOperationException(
                          "P8-11 child environment lacks kestrel.");
        var mongo = environment["mongo"] as JsonObject
                    ?? throw new InvalidOperationException(
                        "P8-11 child environment lacks mongo.");
        var securityResult = security["securityScan"] as JsonObject
                             ?? throw new InvalidOperationException(
                                 "P8-11 child security result is absent.");
        var childRunKey = P811String(gate, "runKey");
        return new P811ChildEvidence(
            iteration,
            exitCode,
            childRunKey,
            runRoot,
            P811Int(gate, "exactExpectedCaseCount"),
            P811Int(gate, "actualCaseCount"),
            P811Bool(gate, "exactIds"),
            P811Bool(gate, "passed"),
            P811String(gate, "normalizedSemanticSha256"),
            cases.Select(row => row.CaseId).ToArray(),
            cases.All(row => string.Equals(
                row.Verdict, "DAT", StringComparison.Ordinal)),
            P811Int(gate, "directMongoEvidenceCases"),
            P811Bool(gate, "prohibitedCollectionDeltaZero"),
            P811Bool(cleanup, "cleanupSucceeded"),
            string.Equals(P811String(cleanup, "state"), "CLEANED",
                StringComparison.Ordinal),
            P811Bool(securityResult, "passed"),
            P811Bool(source, "passed"),
            P811String(source, "normalizedSemanticSha256"),
            P811Bool(p7, "passed"),
            P811String(mongo, "databaseName"),
            P811String(mongo, "replicaSetName"),
            P811Int(kestrel, "processId"),
            P811Int(mongo, "processId"),
            Sha256(await File.ReadAllBytesAsync(gatePath, ct)),
            Sha256(await File.ReadAllBytesAsync(sourcePath, ct)),
            cases);
    }

    private static async Task<JsonObject> ReadP811ObjectAsync(
        string path,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct)
                   as JsonObject
               ?? throw new InvalidOperationException(
                   $"Evidence JSON is not an object: {path}");
    }

    private static P811DeliberateControl? BuildP811DeliberateControl(
        bool requested,
        bool realGateSatisfied,
        P811ChildEvidence? first)
    {
        if (!requested)
            return null;
        var expected = first?.SourceFingerprintSemanticSha256 ??
                       new string('0', 64);
        var corrupted = expected.Length == 64
            ? (expected[0] == '0' ? "1" : "0") + expected[1..]
            : new string('f', 64);
        return new P811DeliberateControl(
            "P8-RACE-CONTROL-SOURCE-FINGERPRINT",
            "SOURCE_FINGERPRINT",
            "Flip exactly one hexadecimal nibble after both real children and cleanup complete.",
            expected,
            corrupted,
            realGateSatisfied ? "KHONG_DAT" : "CHUA_CHAY",
            realGateSatisfied ? 1 : 0,
            realGateSatisfied ? 0 : 1);
    }

    private static string? BuildP811AggregateFailureReason(
        bool deliberateFailure,
        int requestedIterations,
        bool allChildrenPassed,
        bool cleanupPassed,
        bool securityPassed,
        bool sourceFingerprintPassed,
        bool p7RegressionPassed,
        bool identitiesDistinct,
        bool semanticShaMatch,
        bool sourceShaMatch,
        bool deliberateControlPassed)
    {
        if (deliberateFailure)
        {
            return deliberateControlPassed
                ? "Expected deliberate SOURCE_FINGERPRINT oracle failure; root passed=false by design."
                : "Deliberate control did not isolate exactly one named oracle failure.";
        }
        if (requestedIterations != 2)
            return "P8-11 requires exactly two complete clean iterations.";
        if (!allChildrenPassed)
            return "At least one full 200-case child was incomplete or red.";
        if (!cleanupPassed)
            return "One or more P8-owned Kestrel/Mongo children failed cleanup.";
        if (!securityPassed)
            return "One or more child artifact security scans failed.";
        if (!sourceFingerprintPassed)
            return "Source/assembly/process fingerprint evidence failed.";
        if (!p7RegressionPassed)
            return "Immutable P7/successor-stale regression evidence failed.";
        if (!identitiesDistinct)
            return "The two children reused a run, DB, replica-set or process identity.";
        if (!semanticShaMatch)
            return "The two normalized 200-case semantic SHA-256 values differ.";
        if (!sourceShaMatch)
            return "The two source/assembly fingerprint semantic SHA-256 values differ.";
        return null;
    }

    private static bool IsLowerSha256(string value)
        => value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string P811String(JsonObject root, string name)
        => root[name]?.GetValue<string>()
           ?? throw new InvalidOperationException(
               $"P8-11 evidence lacks string '{name}'.");

    private static int P811Int(JsonObject root, string name)
        => root[name]?.GetValue<int>()
           ?? throw new InvalidOperationException(
               $"P8-11 evidence lacks integer '{name}'.");

    private static bool P811Bool(JsonObject root, string name)
        => root[name]?.GetValue<bool>()
           ?? throw new InvalidOperationException(
               $"P8-11 evidence lacks boolean '{name}'.");
}

internal sealed record P811CaseProjection(
    string CaseId,
    string Verdict,
    string Fingerprint);

internal sealed record P811ChildEvidence(
    int Iteration,
    int ExitCode,
    string ChildRunKey,
    string RunRoot,
    int ExactExpectedCaseCount,
    int ActualCaseCount,
    bool ExactIds,
    bool Passed,
    string NormalizedSemanticSha256,
    IReadOnlyList<string> CaseIds,
    bool AllCasesDat,
    int DirectMongoEvidenceCases,
    bool ProhibitedCollectionDeltaZero,
    bool CleanupSucceeded,
    bool CleanupStateCleaned,
    bool SecurityScanPassed,
    bool SourceFingerprintPassed,
    string SourceFingerprintSemanticSha256,
    bool P7RegressionPassed,
    string DatabaseName,
    string ReplicaSetName,
    int BackendProcessId,
    int MongoProcessId,
    string GateManifestRawSha256,
    string SourceFingerprintRawSha256,
    IReadOnlyList<P811CaseProjection> Cases);

internal sealed record P811DeliberateControl(
    string CaseId,
    string Oracle,
    string Mutation,
    string ExpectedSha256,
    string CorruptedSha256,
    string Verdict,
    int NamedFailureCount,
    int UnexpectedFailureCount);
