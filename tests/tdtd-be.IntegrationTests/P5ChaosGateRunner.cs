using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace tdtd_be.IntegrationTests;

internal static partial class P5ChaosGateRunner
{
    private const string DeliberateCaseId = "P5-07-DELIBERATE-ASSERTION";
    private const string SummaryFileName = "p5-p507-gate-summary.json";
    private static readonly string[] RequiredAsnCases =
    [
        "ASN-FLOW-01",
        "ASN-FLOW-02",
        "ASN-FLOW-03",
        "ASN-FLOW-04",
        "ASN-FLOW-05",
        "ASN-FLOW-06",
        "ASN-FLOW-07",
        "ASN-FLOW-08",
        "ASN-FLOW-09",
        "ASN-FLOW-10"
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        var requestedIterations = ParseIterations(args);
        var deliberateFailure = args.Any(
            x => string.Equals(x, "--deliberate-failure", StringComparison.OrdinalIgnoreCase));
        var startedAtUtc = DateTime.UtcNow;
        var runKey = BuildRunKey();
        var paths = HarnessPaths.Create(runKey);
        var iterations = new List<P5ChaosIterationEvidence>();
        var orchestrationErrors = new List<string>();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.WriteLine($"P5-07 chaos-gate artifacts: {paths.RunRoot}");
        for (var iteration = 1; iteration <= requestedIterations; iteration++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var iterationRoot = paths.IterationRoot(iteration);
            var probeExitCode = -1;
            string? probeFailure = null;
            try
            {
                probeExitCode = await P5MaterializationProbe.RunAsync(
                    new P5MaterializationProbeOptions(
                        paths,
                        runKey,
                        iteration,
                        deliberateFailure && iteration == requestedIterations));
            }
            catch (Exception error)
            {
                probeFailure = $"{error.GetType().Name}: {error.Message}";
                orchestrationErrors.Add($"iteration-{iteration:00}: probe threw {error.GetType().Name}.");
                Console.Error.WriteLine(error);
            }

            var summaryPath = Path.Combine(iterationRoot, SummaryFileName);
            P5ChaosIterationEvidence evidence;
            if (File.Exists(summaryPath))
            {
                try
                {
                    evidence = await LoadIterationSummaryAsync(
                        summaryPath,
                        runKey,
                        iteration,
                        probeExitCode,
                        cancellation.Token);
                }
                catch (Exception error)
                {
                    orchestrationErrors.Add(
                        $"iteration-{iteration:00}: invalid {SummaryFileName}: {error.GetType().Name}.");
                    evidence = CreateInfrastructureFailure(
                        runKey,
                        iteration,
                        probeExitCode,
                        $"Invalid {SummaryFileName}: {error.Message}");
                }
            }
            else
            {
                orchestrationErrors.Add($"iteration-{iteration:00}: missing {SummaryFileName}.");
                evidence = CreateInfrastructureFailure(
                    runKey,
                    iteration,
                    probeExitCode,
                    probeFailure ?? $"Probe did not write {SummaryFileName}.");
            }

            var cleanupAudit = await InspectCleanupAsync(iterationRoot, cancellation.Token);
            evidence = evidence with
            {
                CleanupSucceeded = evidence.CleanupSucceeded && cleanupAudit.Passed,
                CleanupErrors = evidence.CleanupErrors
                    .Concat(cleanupAudit.Failures)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                CleanupAudit = cleanupAudit
            };
            iterations.Add(evidence);
        }

        if (deliberateFailure && iterations.Count > 0)
        {
            var deliberateIndex = iterations.Count - 1;
            iterations[deliberateIndex] = AppendDeliberateFailure(iterations[deliberateIndex]);
        }

        var artifactSecurity = await InspectArtifactSecurityAsync(paths.RunRoot, cancellation.Token);
        var completedAtUtc = DateTime.UtcNow;
        var allSummariesPresent = iterations.Count == requestedIterations &&
                                  iterations.All(x => x.SummaryLoaded);
        var allCaseCountsConsistent = iterations.All(x => x.CaseCount == x.Cases.Count);
        var allRequiredAsnCasesPresent = iterations.All(ContainsRequiredAsnCases);
        var allIterationSummariesPassed = iterations.All(x => x.Passed);
        var allCleanCasesDat = iterations.All(
            x => x.Cases
                .Where(c => !string.Equals(c.CaseId, DeliberateCaseId, StringComparison.Ordinal))
                .All(c => c.Verdict == HarnessVerdict.DAT));
        var allCleanupSucceeded = iterations.All(x => x.CleanupSucceeded);
        var allProbeExitsClean = iterations.All(
            x => x.ProbeExitCode == 0 ||
                 (deliberateFailure &&
                  x.Cases.Any(c => string.Equals(
                      c.CaseId,
                      DeliberateCaseId,
                      StringComparison.Ordinal))));
        var requiredCleanIterationCount = deliberateFailure || requestedIterations >= 2;
        var deterministic = !deliberateFailure &&
                            iterations.Count == requestedIterations &&
                            iterations.Select(x => x.NormalizedSha256)
                                .Distinct(StringComparer.Ordinal)
                                .Count() == 1 &&
                            iterations.Select(x => x.CaseCount).Distinct().Count() == 1;
        var identitiesPresent = iterations.All(
            x => !string.IsNullOrWhiteSpace(x.DatabaseName) &&
                 !string.IsNullOrWhiteSpace(x.ReplicaSetName) &&
                 !string.IsNullOrWhiteSpace(x.SeedIdentity));
        var isolated = identitiesPresent &&
                       AreDistinct(iterations.Select(x => x.DatabaseName)) &&
                       AreDistinct(iterations.Select(x => x.ReplicaSetName)) &&
                       AreDistinct(iterations.Select(x => x.SeedIdentity));
        var deliberateAssertionObserved = deliberateFailure &&
                                          iterations.SelectMany(x => x.Cases).Any(
                                              x => string.Equals(
                                                       x.CaseId,
                                                       DeliberateCaseId,
                                                       StringComparison.Ordinal) &&
                                                   x.Verdict == HarnessVerdict.KHONG_DAT);

        var cleanGateSatisfied = orchestrationErrors.Count == 0 &&
                                 allSummariesPresent &&
                                 allCaseCountsConsistent &&
                                 allRequiredAsnCasesPresent &&
                                 allIterationSummariesPassed &&
                                 allCleanCasesDat &&
                                 allCleanupSucceeded &&
                                 allProbeExitsClean &&
                                 requiredCleanIterationCount &&
                                 deterministic &&
                                 isolated &&
                                 artifactSecurity.Passed;
        var passed = !deliberateFailure && cleanGateSatisfied;
        var failureReason = passed
            ? null
            : BuildFailureReason(
                deliberateFailure,
                deliberateAssertionObserved,
                orchestrationErrors,
                allSummariesPresent,
                allCaseCountsConsistent,
                allRequiredAsnCasesPresent,
                allIterationSummariesPassed,
                allCleanCasesDat,
                allCleanupSucceeded,
                allProbeExitsClean,
                requiredCleanIterationCount,
                deterministic,
                isolated,
                artifactSecurity);

        var result = new
        {
            schemaVersion = 1,
            gate = "P5-07",
            runKey,
            startedAtUtc,
            completedAtUtc,
            requestedIterations,
            deliberateFailureMode = deliberateFailure,
            iterations,
            checks = new
            {
                allSummariesPresent,
                allCaseCountsConsistent,
                allRequiredAsnCasesPresent,
                allIterationSummariesPassed,
                allCleanCasesDat,
                allCleanupSucceeded,
                allProbeExitsClean,
                requiredCleanIterationCount,
                deterministic,
                identitiesPresent,
                isolated,
                artifactSecurityPassed = artifactSecurity.Passed,
                deliberateAssertionObserved
            },
            deterministic,
            passed,
            failureReason
        };
        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "results.json"),
            result,
            cancellation.Token);
        await EvidenceCsv.WriteCasesAsync(
            Path.Combine(paths.RunRoot, "results.csv"),
            iterations.SelectMany(
                x => x.Cases.Select(c => (x.Iteration, Case: c))),
            cancellation.Token);
        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "reconciliation-ledger.json"),
            new
            {
                schemaVersion = 1,
                gate = "P5-07",
                runKey,
                expectedIterations = requestedIterations,
                actualIterations = iterations.Count,
                requiredAsnCases = RequiredAsnCases,
                iterations = iterations.Select(x => new
                {
                    x.Iteration,
                    x.CaseCount,
                    x.NormalizedSha256,
                    allCasesDat = x.Cases.All(c => c.Verdict == HarnessVerdict.DAT),
                    allNonDeliberateCasesDat = x.Cases
                        .Where(c => c.CaseId != DeliberateCaseId)
                        .All(c => c.Verdict == HarnessVerdict.DAT),
                    requiredAsnCasesPresent = ContainsRequiredAsnCases(x),
                    x.DatabaseName,
                    x.ReplicaSetName,
                    x.SeedIdentity,
                    x.CleanupSucceeded,
                    x.ProbeExitCode
                }),
                normalizedSha256Match = deterministic,
                caseCountMatch = !deliberateFailure &&
                                 iterations.Select(x => x.CaseCount).Distinct().Count() == 1,
                isolated,
                deliberateFailureMode = deliberateFailure,
                deliberateAssertionObserved,
                passed
            },
            cancellation.Token);
        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "cleanup-manifest.json"),
            new
            {
                schemaVersion = 1,
                gate = "P5-07",
                runKey,
                completedAtUtc,
                iterations = iterations.Select(x => new
                {
                    x.Iteration,
                    x.DatabaseName,
                    x.ReplicaSetName,
                    x.CleanupSucceeded,
                    x.CleanupErrors,
                    x.CleanupAudit
                }),
                artifactSecurity,
                allCleanupSucceeded,
                passed = allCleanupSucceeded && artifactSecurity.Passed
            },
            cancellation.Token);
        await WriteEvidenceDraftAsync(
            paths.RunRoot,
            runKey,
            startedAtUtc,
            completedAtUtc,
            requestedIterations,
            deliberateFailure,
            iterations,
            deterministic,
            isolated,
            artifactSecurity,
            passed,
            failureReason ?? "All P5-07 chaos-gate checks passed.",
            cancellation.Token);

        Console.WriteLine(
            passed
                ? $"[DAT] P5-07 chaos gate passed {requestedIterations} clean isolated iteration(s); sha={iterations[0].NormalizedSha256}."
                : $"[KHONG_DAT] P5-07 chaos gate failed: {failureReason}");
        return passed ? 0 : 1;
    }

    private static int ParseIterations(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string? value = null;
            if (argument.StartsWith("--iterations=", StringComparison.OrdinalIgnoreCase))
                value = argument[("--iterations=".Length)..];
            else if (string.Equals(argument, "--iterations", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                    throw new ArgumentException("--iterations requires a positive integer.");
                value = args[index + 1];
            }

            if (value is null)
                continue;
            if (!int.TryParse(value, out var parsed) || parsed is < 1 or > 20)
                throw new ArgumentOutOfRangeException(
                    nameof(args),
                    "--iterations must be an integer from 1 through 20.");
            return parsed;
        }

        return 2;
    }

    private static string BuildRunKey()
    {
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"p507_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{random}";
    }

    private static async Task<P5ChaosIterationEvidence> LoadIterationSummaryAsync(
        string path,
        string expectedRunKey,
        int expectedIteration,
        int probeExitCode,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        RequireKind(root, JsonValueKind.Object, "P5-07 iteration summary root");

        var runKey = GetString(root, "runKey") ?? expectedRunKey;
        if (!string.Equals(runKey, expectedRunKey, StringComparison.Ordinal))
            throw new InvalidDataException("Iteration summary runKey does not match its enclosing run.");
        var iteration = GetInt32(root, "iteration") ?? expectedIteration;
        if (iteration != expectedIteration)
            throw new InvalidDataException("Iteration summary number does not match its enclosing directory.");

        var casesElement = GetProperty(root, "cases");
        if (casesElement is null || casesElement.Value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Iteration summary must contain a cases array.");
        var cases = new List<HarnessCaseResult>();
        foreach (var item in casesElement.Value.EnumerateArray())
        {
            RequireKind(item, JsonValueKind.Object, "P5-07 case row");
            var caseId = GetString(item, "caseId")
                         ?? throw new InvalidDataException("A P5-07 case row is missing caseId.");
            var verdict = ParseVerdict(
                GetString(item, "verdict")
                ?? throw new InvalidDataException($"Case {caseId} is missing verdict."));
            cases.Add(
                new HarnessCaseResult(
                    caseId,
                    verdict,
                    GetString(item, "detail") ?? string.Empty,
                    GetString(item, "fingerprint") ?? caseId,
                    GetInt64(item, "durationMs") ?? 0));
        }

        var declaredCaseCount = GetInt32(root, "caseCount") ?? cases.Count;
        var normalizedSha256 = GetString(root, "normalizedSha256");
        if (string.IsNullOrWhiteSpace(normalizedSha256))
            normalizedSha256 = BuildNormalizedSha256(cases);
        if (!Regex.IsMatch(normalizedSha256, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Iteration summary normalizedSha256 must be 64 hexadecimal characters.");
        normalizedSha256 = normalizedSha256.ToLowerInvariant();
        var databaseName = FindString(root, "databaseName") ?? string.Empty;
        var replicaSetName = FindString(root, "replicaSetName") ?? string.Empty;
        var seedIdentity = FindIdentity(root, "seedIdentity")
                           ?? FindString(root, "seedIdentitySha256")
                           ?? string.Empty;
        var cleanupErrors = ReadStringArray(root, "cleanupErrors");
        var cleanupSucceeded = FindBoolean(root, "cleanupSucceeded")
                               ?? FindBoolean(root, "cleanupPassed")
                               ?? (cleanupErrors.Count == 0);
        var declaredPassed = FindBoolean(root, "passed");
        var declaredVerdict = GetString(root, "verdict");
        var passed = declaredPassed ??
                     (declaredVerdict is null
                         ? cases.All(x => x.Verdict == HarnessVerdict.DAT)
                         : string.Equals(
                             declaredVerdict,
                             "PASS",
                             StringComparison.OrdinalIgnoreCase));

        return new P5ChaosIterationEvidence(
            iteration,
            runKey,
            databaseName,
            replicaSetName,
            seedIdentity,
            cases,
            declaredCaseCount,
            normalizedSha256,
            passed,
            cleanupSucceeded,
            cleanupErrors,
            probeExitCode,
            SummaryLoaded: true,
            CleanupAudit: null);
    }

    private static P5ChaosIterationEvidence CreateInfrastructureFailure(
        string runKey,
        int iteration,
        int probeExitCode,
        string detail)
    {
        var cases = new[]
        {
            new HarnessCaseResult(
                "P5-07-INFRASTRUCTURE",
                HarnessVerdict.KHONG_DAT,
                detail,
                "p507-infrastructure-failure",
                0)
        };
        return new P5ChaosIterationEvidence(
            iteration,
            runKey,
            string.Empty,
            string.Empty,
            string.Empty,
            cases,
            cases.Length,
            BuildNormalizedSha256(cases),
            Passed: false,
            CleanupSucceeded: false,
            CleanupErrors: [detail],
            probeExitCode,
            SummaryLoaded: false,
            CleanupAudit: null);
    }

    private static P5ChaosIterationEvidence AppendDeliberateFailure(
        P5ChaosIterationEvidence iteration)
    {
        if (iteration.Cases.Any(
                x => string.Equals(x.CaseId, DeliberateCaseId, StringComparison.Ordinal)))
            return iteration with { Passed = false };

        var cases = iteration.Cases
            .Append(
                new HarnessCaseResult(
                    DeliberateCaseId,
                    HarnessVerdict.KHONG_DAT,
                    "Intentional assertion failure injected after the clean semantic cases.",
                    "deliberate-assertion=false",
                    0))
            .ToArray();
        return iteration with
        {
            Cases = cases,
            CaseCount = cases.Length,
            NormalizedSha256 = BuildNormalizedSha256(cases),
            Passed = false
        };
    }

    private static bool ContainsRequiredAsnCases(P5ChaosIterationEvidence iteration)
    {
        var ids = iteration.Cases
            .Select(x => x.CaseId)
            .ToHashSet(StringComparer.Ordinal);
        return RequiredAsnCases.All(ids.Contains);
    }

    private static bool AreDistinct(IEnumerable<string> values)
    {
        var materialized = values.ToArray();
        return materialized.Distinct(StringComparer.Ordinal).Count() == materialized.Length;
    }

    private static string BuildNormalizedSha256(IEnumerable<HarnessCaseResult> cases)
    {
        var normalized = cases
            .OrderBy(x => x.CaseId, StringComparer.Ordinal)
            .Select(x => new { x.CaseId, x.Verdict, x.Fingerprint })
            .ToArray();
        var json = JsonSerializer.Serialize(normalized, EvidenceJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private static string BuildFailureReason(
        bool deliberateFailure,
        bool deliberateAssertionObserved,
        IReadOnlyList<string> orchestrationErrors,
        bool allSummariesPresent,
        bool allCaseCountsConsistent,
        bool allRequiredAsnCasesPresent,
        bool allIterationSummariesPassed,
        bool allCleanCasesDat,
        bool allCleanupSucceeded,
        bool allProbeExitsClean,
        bool requiredCleanIterationCount,
        bool deterministic,
        bool isolated,
        ArtifactSecurityAudit artifactSecurity)
    {
        if (deliberateFailure)
        {
            if (orchestrationErrors.Count > 0)
                return $"Deliberate control encountered an orchestration error: {orchestrationErrors[0]}";
            if (!allSummariesPresent)
                return "Deliberate control is missing one or more probe summaries.";
            if (!allCaseCountsConsistent)
                return "Deliberate control has an inconsistent declared case count.";
            if (!allRequiredAsnCasesPresent)
                return "Deliberate control is missing one or more ASN-FLOW-01..10 rows.";
            if (!allCleanCasesDat)
                return "A non-deliberate semantic case failed during the deliberate control.";
            if (!allCleanupSucceeded)
                return "Deliberate assertion was exercised, but cleanup verification failed.";
            if (!allProbeExitsClean)
                return "Deliberate control observed an unexpected nonzero probe exit.";
            if (!isolated)
                return "Deliberate control reused or omitted an isolation identity.";
            if (!artifactSecurity.Passed)
                return "Deliberate assertion was exercised, but artifact security verification failed.";
            return deliberateAssertionObserved
                ? "Expected deliberate assertion failure; root passed=false by design."
                : "Deliberate mode did not emit the required P5-07-DELIBERATE-ASSERTION row.";
        }

        if (orchestrationErrors.Count > 0)
            return orchestrationErrors[0];
        if (!allSummariesPresent)
            return "One or more probe summaries are missing.";
        if (!allCaseCountsConsistent)
            return "A declared case count does not match its case rows.";
        if (!allRequiredAsnCasesPresent)
            return "One or more exact ASN-FLOW-01..10 rows are absent.";
        if (!allIterationSummariesPassed)
            return "One or more iteration summaries report passed=false.";
        if (!allCleanCasesDat)
            return "One or more assertion-derived semantic cases did not pass.";
        if (!allCleanupSucceeded)
            return "One or more isolated iterations failed cleanup verification.";
        if (!allProbeExitsClean)
            return "One or more clean probe processes returned nonzero.";
        if (!requiredCleanIterationCount)
            return "Normal P5-07 mode requires at least two clean iterations.";
        if (!deterministic)
            return "Normalized SHA-256 or case count differs across clean iterations.";
        if (!isolated)
            return "Database, replica-set, or seed identities are missing or reused.";
        if (!artifactSecurity.Passed)
            return artifactSecurity.Failures.FirstOrDefault()
                   ?? "Artifact secret or leftover-file scan failed.";
        return "P5-07 gate failed without a classified reason.";
    }

    private static async Task WriteEvidenceDraftAsync(
        string runRoot,
        string runKey,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        int requestedIterations,
        bool deliberateFailure,
        IReadOnlyList<P5ChaosIterationEvidence> iterations,
        bool deterministic,
        bool isolated,
        ArtifactSecurityAudit security,
        bool passed,
        string failureReason,
        CancellationToken ct)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# P5-07 integration/chaos evidence (DRAFT)");
        builder.AppendLine();
        builder.AppendLine("> Draft machine-derived evidence only. P5 remains IN_PROGRESS; P5-08 owns final acceptance.");
        builder.AppendLine();
        builder.AppendLine($"- Run key: `{runKey}`");
        builder.AppendLine($"- Started UTC: `{startedAtUtc:O}`");
        builder.AppendLine($"- Completed UTC: `{completedAtUtc:O}`");
        builder.AppendLine($"- Requested iterations: `{requestedIterations}`");
        builder.AppendLine($"- Deliberate failure mode: `{deliberateFailure}`");
        builder.AppendLine($"- Normalized result deterministic: `{deterministic}`");
        builder.AppendLine($"- Isolation identities distinct: `{isolated}`");
        builder.AppendLine($"- Artifact security scan: `{security.Passed}`");
        builder.AppendLine($"- Root passed: `{passed}`");
        builder.AppendLine($"- Outcome: `{failureReason}`");
        builder.AppendLine();
        builder.AppendLine("## Iterations");
        builder.AppendLine();
        builder.AppendLine("| Iteration | Cases | SHA-256 | Cleanup | Probe exit |");
        builder.AppendLine("|---:|---:|---|---|---:|");
        foreach (var iteration in iterations)
        {
            builder.AppendLine(
                $"| {iteration.Iteration} | {iteration.CaseCount} | `{iteration.NormalizedSha256}` | {iteration.CleanupSucceeded} | {iteration.ProbeExitCode} |");
        }
        builder.AppendLine();
        builder.AppendLine("Machine-readable companions: `results.json`, `results.csv`, `reconciliation-ledger.json`, and `cleanup-manifest.json`.");

        await File.WriteAllTextAsync(
            Path.Combine(runRoot, "evidence-draft.md"),
            builder.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            ct);
    }

    private sealed record P5ChaosIterationEvidence(
        int Iteration,
        string RunKey,
        string DatabaseName,
        string ReplicaSetName,
        string SeedIdentity,
        IReadOnlyList<HarnessCaseResult> Cases,
        int CaseCount,
        string NormalizedSha256,
        bool Passed,
        bool CleanupSucceeded,
        IReadOnlyList<string> CleanupErrors,
        int ProbeExitCode,
        bool SummaryLoaded,
        CleanupAudit? CleanupAudit);
}

internal static partial class P5ChaosGateRunner
{
    private static readonly HashSet<string> SensitivePropertyNames =
        new(
            [
                "password",
                "defaultPassword",
                "adminPassword",
                "adminPasswordSet",
                "accessToken",
                "refreshToken",
                "authorization",
                "apiKey",
                "clientSecret",
                "systemBootstrapKey",
                "bootstrapKey"
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

    private static async Task<CleanupAudit> InspectCleanupAsync(
        string iterationRoot,
        CancellationToken ct)
    {
        var failures = new List<string>();
        var primaryPath = Path.Combine(iterationRoot, "p5-materialization-cleanup.json");
        var primaryFound = File.Exists(primaryPath);
        var databaseDropped = false;
        var mongoStopped = false;
        var mongoDataRemoved = false;
        var mongoPortReleased = false;
        if (!primaryFound)
        {
            failures.Add("p5-materialization-cleanup.json is missing.");
        }
        else
        {
            try
            {
                await using var stream = File.OpenRead(primaryPath);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = document.RootElement;
                databaseDropped = FindBoolean(root, "databaseDropped") == true;
                mongoStopped = FindBoolean(root, "mongoStopped") == true;
                mongoDataRemoved = FindBoolean(root, "mongoDataRemoved") == true;
                mongoPortReleased = FindBoolean(root, "mongoPortReleased") == true ||
                                    FindBoolean(root, "mongoPortReleaseVerified") == true ||
                                    FindBoolean(root, "portReleasedVerified") == true;
                var primaryCleanupErrors = ReadStringArray(root, "cleanupErrors");
                if (primaryCleanupErrors.Count > 0)
                    failures.Add("Primary cleanup manifest reports one or more cleanup errors.");
                if (!databaseDropped)
                    failures.Add("Mongo database drop was not verified.");
                if (!mongoStopped)
                    failures.Add("Mongo process stop was not verified.");
                if (!mongoDataRemoved)
                    failures.Add("Mongo data-directory removal was not verified.");
            }
            catch (Exception error)
            {
                failures.Add($"Primary cleanup manifest is invalid: {error.GetType().Name}.");
            }
        }

        var mongoProcessPath = Path.Combine(iterationRoot, "mongo-process-cleanup.json");
        var mongoProcessManifestFound = File.Exists(mongoProcessPath);
        if (mongoProcessManifestFound)
        {
            try
            {
                await using var stream = File.OpenRead(mongoProcessPath);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = document.RootElement;
                mongoStopped &= FindBoolean(root, "processStopped") == true ||
                                FindBoolean(root, "stopVerified") == true;
                mongoPortReleased = FindBoolean(root, "portReleased") == true ||
                                    FindBoolean(root, "portReleasedVerified") == true ||
                                    FindBoolean(root, "portReleaseVerified") == true;
            }
            catch (Exception error)
            {
                failures.Add($"mongo-process-cleanup.json is invalid: {error.GetType().Name}.");
            }
        }
        else if (!mongoPortReleased)
        {
            failures.Add("mongo-process-cleanup.json is missing.");
        }
        if (!mongoPortReleased)
            failures.Add("Mongo port release was not verified.");

        var backendPaths = Directory.Exists(iterationRoot)
            ? Directory.EnumerateFiles(
                    iterationRoot,
                    "backend-cleanup.json",
                    SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray()
            : [];
        if (backendPaths.Length == 0)
            failures.Add("No backend-cleanup.json manifest was written.");
        var backendRows = new List<BackendCleanupAudit>();
        foreach (var backendPath in backendPaths)
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(iterationRoot, backendPath);
            var stopped = false;
            var portReleased = false;
            try
            {
                await using var stream = File.OpenRead(backendPath);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = document.RootElement;
                stopped = FindBoolean(root, "stopped") == true ||
                          FindBoolean(root, "stopVerified") == true ||
                          FindBoolean(root, "processStopped") == true;
                portReleased = FindBoolean(root, "portReleased") == true ||
                               FindBoolean(root, "portReleasedVerified") == true ||
                               FindBoolean(root, "portReleaseVerified") == true;
            }
            catch (Exception error)
            {
                failures.Add($"{relativePath} is invalid: {error.GetType().Name}.");
            }

            if (!stopped)
                failures.Add($"{relativePath}: backend stop was not verified.");
            if (!portReleased)
                failures.Add($"{relativePath}: backend port release was not verified.");
            backendRows.Add(new BackendCleanupAudit(relativePath, stopped, portReleased));
        }

        return new CleanupAudit(
            failures.Count == 0,
            primaryFound,
            mongoProcessManifestFound,
            databaseDropped,
            mongoStopped,
            mongoDataRemoved,
            mongoPortReleased,
            backendRows,
            failures);
    }

    private static async Task<ArtifactSecurityAudit> InspectArtifactSecurityAsync(
        string runRoot,
        CancellationToken ct)
    {
        var failures = new List<string>();
        var scannedJsonFiles = 0;
        var skippedLogFiles = 0;
        var files = Directory.Exists(runRoot)
            ? Directory.EnumerateFiles(runRoot, "*", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray()
            : [];
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(runRoot, path);
            var fileName = Path.GetFileName(path);
            if (IsForbiddenLeftoverFile(fileName))
                failures.Add($"{relativePath}: forbidden leftover stop/secret file.");

            if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            {
                scannedJsonFiles++;
                try
                {
                    var text = await File.ReadAllTextAsync(path, ct);
                    using var document = JsonDocument.Parse(text);
                    InspectJsonValue(document.RootElement, relativePath, "$", failures);
                }
                catch (JsonException)
                {
                    failures.Add($"{relativePath}: invalid JSON cannot be security-scanned.");
                }
                catch (IOException error)
                {
                    failures.Add($"{relativePath}: security scan read failed ({error.GetType().Name}).");
                }
                continue;
            }

            if (IsExcludedLog(path))
            {
                skippedLogFiles++;
                continue;
            }

            if (!IsTextArtifact(path))
                continue;
            try
            {
                var text = await File.ReadAllTextAsync(path, ct);
                if (ContainsRawBearerOrJwt(text))
                    failures.Add($"{relativePath}: contains a raw bearer/JWT-like token.");
            }
            catch (IOException error)
            {
                failures.Add($"{relativePath}: security scan read failed ({error.GetType().Name}).");
            }
        }

        return new ArtifactSecurityAudit(
            failures.Count == 0,
            scannedJsonFiles,
            skippedLogFiles,
            failures.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void InspectJsonValue(
        JsonElement element,
        string relativePath,
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
                            $"{relativePath}:{propertyPath}: sensitive property is not redacted.");
                    }
                    InspectJsonValue(property.Value, relativePath, propertyPath, failures);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    InspectJsonValue(item, relativePath, $"{jsonPath}[{index}]", failures);
                    index++;
                }
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                if (ContainsRawBearerOrJwt(value))
                    failures.Add($"{relativePath}:{jsonPath}: contains a raw bearer/JWT-like token.");
                InspectNestedJsonString(value, relativePath, jsonPath, failures);
                break;
        }
    }

    private static void InspectNestedJsonString(
        string value,
        string relativePath,
        string jsonPath,
        ICollection<string> failures)
    {
        var trimmed = value.Trim();
        if (trimmed.Length < 2 ||
            !((trimmed[0] == '{' && trimmed[^1] == '}') ||
              (trimmed[0] == '[' && trimmed[^1] == ']')))
        {
            return;
        }

        try
        {
            using var nested = JsonDocument.Parse(trimmed);
            InspectJsonValue(nested.RootElement, relativePath, $"{jsonPath}<json>", failures);
        }
        catch (JsonException)
        {
            // It merely resembles JSON; the outer artifact remains valid and is still token-scanned.
        }
    }

    private static bool IsRedactedValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ||
                   string.Equals(text, "<redacted>", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "[redacted]", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "***", StringComparison.Ordinal);
        }
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().All(IsRedactedValue);
        return false;
    }

    private static bool ContainsRawBearerOrJwt(string value)
        => BearerSecretPattern.IsMatch(value) || JwtPattern.IsMatch(value);

    private static bool IsForbiddenLeftoverFile(string fileName)
    {
        if (string.Equals(fileName, ".stop", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(fileName), ".stop", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, ".secret", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(fileName), ".secret", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return Regex.IsMatch(
            stem,
            @"(^|[-_.])secrets?($|[-_.])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsExcludedLog(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase))
            return true;
        var fileName = Path.GetFileName(path);
        return fileName.Contains("stdout", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("stderr", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextArtifact(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".csv" or ".md";

    private sealed record CleanupAudit(
        bool Passed,
        bool PrimaryManifestFound,
        bool MongoProcessManifestFound,
        bool DatabaseDropped,
        bool MongoStopped,
        bool MongoDataRemoved,
        bool MongoPortReleased,
        IReadOnlyList<BackendCleanupAudit> BackendProcesses,
        IReadOnlyList<string> Failures);

    private sealed record BackendCleanupAudit(
        string RelativePath,
        bool Stopped,
        bool PortReleased);

    private sealed record ArtifactSecurityAudit(
        bool Passed,
        int ScannedJsonFiles,
        int SkippedLogFiles,
        IReadOnlyList<string> Failures);
}

internal static partial class P5ChaosGateRunner
{
    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        return null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        var property = GetProperty(element, name);
        if (property is null || property.Value.ValueKind != JsonValueKind.String)
            return null;
        return property.Value.GetString();
    }

    private static int? GetInt32(JsonElement element, string name)
    {
        var property = GetProperty(element, name);
        if (property is null || property.Value.ValueKind != JsonValueKind.Number)
            return null;
        return property.Value.TryGetInt32(out var value) ? value : null;
    }

    private static long? GetInt64(JsonElement element, string name)
    {
        var property = GetProperty(element, name);
        if (property is null || property.Value.ValueKind != JsonValueKind.Number)
            return null;
        return property.Value.TryGetInt64(out var value) ? value : null;
    }

    private static string? FindString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }

                var nested = FindString(property.Value, name);
                if (nested is not null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, name);
                if (nested is not null)
                    return nested;
            }
        }
        return null;
    }

    private static bool? FindBoolean(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return property.Value.GetBoolean();
                }

                var nested = FindBoolean(property.Value, name);
                if (nested is not null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindBoolean(item, name);
                if (nested is not null)
                    return nested;
            }
        }
        return null;
    }

    private static string? FindIdentity(JsonElement element, string name)
    {
        var property = GetProperty(element, name);
        if (property is not null)
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var bytes = Encoding.UTF8.GetBytes(property.Value.GetRawText());
                return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var child in element.EnumerateObject())
            {
                var result = FindIdentity(child.Value, name);
                if (result is not null)
                    return result;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var result = FindIdentity(child, name);
                if (result is not null)
                    return result;
            }
        }
        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        var property = GetProperty(element, name);
        if (property is null || property.Value.ValueKind != JsonValueKind.Array)
            return [];
        return property.Value
            .EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToArray();
    }

    private static HarnessVerdict ParseVerdict(string value)
        => value.ToUpperInvariant() switch
        {
            "DAT" or "PASS" => HarnessVerdict.DAT,
            "KHONG_DAT" or "FAIL" or "FAILED" or "BLOCKED" => HarnessVerdict.KHONG_DAT,
            "CHUA_CHAY" or "NOT_RUN" or "SKIPPED" => HarnessVerdict.CHUA_CHAY,
            _ => throw new InvalidDataException($"Unknown P5-07 case verdict '{value}'.")
        };

    private static void RequireKind(
        JsonElement element,
        JsonValueKind kind,
        string subject)
    {
        if (element.ValueKind != kind)
            throw new InvalidDataException($"{subject} must be {kind}.");
    }
}
