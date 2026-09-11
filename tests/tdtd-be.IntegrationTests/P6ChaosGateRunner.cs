using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// P6-11 aggregate gate. The ten existing P6 real probes remain the topology
/// semantic owners, while one shared FLOW-T01/v1.2 probe owns the hard-kill,
/// lease-loss and transient materialization boundary. This runner executes all
/// eleven sequentially, discovers only roots created by the current child
/// invocation, and rejects incomplete, non-Kestrel, non-reconciled, insecure,
/// or incompletely-cleaned evidence.
/// </summary>
internal static class P6ChaosGateRunner
{
    private const string DeliberateCaseId = "P6-11-DELIBERATE-ORACLE";
    private const string GateId = "P6-11";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly ProbeDescriptor[] Descriptors =
    [
        new(
            "P601",
            "FLOW-T03",
            "p601_",
            "p6-01-sequential-result.json",
            "p6-01-sequential-api-exchanges.json",
            "p6-01-sequential-direct-mongo.json",
            ["P6-TOPO-001", "P6-TOPO-002", "P6-TOPO-003", "P6-TOPO-004", "P6-TOPO-015", "P6-TOPO-016", "P6-TOPO-017"],
            64,
            [200, 400, 403, 404, 409],
            ResultCarriesCandidate: true,
            P5MaterializationProbe.RunP601Async),
        new(
            "P602",
            "FLOW-T04",
            "p602_",
            "p6-02-parallel-fork-result.json",
            "p6-02-parallel-fork-api-exchanges.json",
            "p6-02-parallel-fork-direct-mongo.json",
            ["P6-TOPO-005"],
            50,
            [200, 400, 409],
            ResultCarriesCandidate: true,
            P5MaterializationProbe.RunP602Async),
        new(
            "P603",
            "FLOW-T05",
            "p603_",
            "p6-03-join-all-result.json",
            "p6-03-join-all-api-exchanges.json",
            "p6-03-join-all-direct-mongo.json",
            ["P6-TOPO-006"],
            53,
            [200, 500],
            ResultCarriesCandidate: true,
            P5MaterializationProbe.RunP603Async),
        new(
            "P604",
            "FLOW-T06",
            "p604_",
            "p6-04-join-quorum-result.json",
            "p6-04-join-quorum-api-exchanges.json",
            "p6-04-join-quorum-direct-mongo.json",
            ["P6-TOPO-007"],
            39,
            [200, 500],
            ResultCarriesCandidate: false,
            P5MaterializationProbe.RunP604Async),
        new(
            "P605",
            "FLOW-T07",
            "p605_",
            "p6-05-typed-conditional-result.json",
            "p6-05-typed-conditional-api-exchanges.json",
            "p6-05-typed-conditional-direct-mongo.json",
            ["P6-TOPO-008"],
            41,
            [200, 400, 403],
            ResultCarriesCandidate: false,
            P5MaterializationProbe.RunP605Async),
        new(
            "P606",
            "FLOW-T08",
            "p606_",
            "p6-06-review-loop-result.json",
            "p6-06-review-loop-api-exchanges.json",
            "p6-06-review-loop-direct-mongo.json",
            ["P6-TOPO-009"],
            35,
            [200, 403],
            ResultCarriesCandidate: false,
            P5MaterializationProbe.RunP606Async),
        new(
            "P607",
            "FLOW-T09",
            "p607_",
            "p6-07-subflow-result.json",
            "p6-07-subflow-api-exchanges.json",
            "p6-07-subflow-direct-mongo.json",
            ["P6-TOPO-010"],
            35,
            [200, 403],
            ResultCarriesCandidate: false,
            P5MaterializationProbe.RunP607Async),
        new(
            "P608",
            "FLOW-T10",
            "p608_",
            "p6-08-periodic-result.json",
            "p6-08-periodic-api-exchanges.json",
            "p6-08-periodic-direct-mongo.json",
            ["P6-TOPO-011"],
            11,
            [200],
            ResultCarriesCandidate: false,
            P5MaterializationProbe.RunP608Async),
        new(
            "P609",
            "FLOW-T11",
            "p609_",
            "p6-09-supplemental-result.json",
            "p6-09-supplemental-api-exchanges.json",
            "p6-09-supplemental-direct-mongo.json",
            ["P6-TOPO-012"],
            10,
            [200, 403],
            ResultCarriesCandidate: false,
            P5MaterializationProbe.RunP609Async),
        new(
            "P610",
            "FLOW-T12",
            "p610_",
            "p6-10-epoch-result.json",
            "p6-10-epoch-api-exchanges.json",
            "p6-10-epoch-direct-mongo.json",
            ["P6-TOPO-013", "P6-TOPO-014"],
            31,
            [200, 400, 403, 409],
            ResultCarriesCandidate: false,
            P5MaterializationProbe.RunP610Async),
        new(
            "P611CHAOS",
            "FLOW-T01-SHARED-SEAM",
            "p611chaos_",
            "p6-11-boundary-result.json",
            "p6-11-boundary-api.json",
            "p6-11-boundary-direct-mongo.json",
            ["P6-TOPO-019"],
            21,
            [200],
            ResultCarriesCandidate: false,
            P5MaterializationProbe.RunP611ChaosBoundaryAsync)
    ];

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
                "bootstrapKey",
                "X-System-Bootstrap-Key",
                "cookie",
                "Set-Cookie",
                "snapshotToken"
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
        var iterations = new List<P6ChaosIterationEvidence>();
        var orchestrationErrors = new List<string>();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.WriteLine($"P6-11 chaos-gate artifacts: {paths.RunRoot}");
        for (var iteration = 1; iteration <= requestedIterations; iteration++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var aggregateIterationRoot = paths.IterationRoot(iteration);
            var children = new List<ChildProbeEvidence>();

            foreach (var descriptor in Descriptors)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Console.WriteLine(
                    $"P6-11 iteration {iteration:00}: starting {descriptor.Id} ({descriptor.ArchetypeId}).");
                var child = await ExecuteChildAsync(
                    paths,
                    descriptor,
                    cancellation.Token);
                children.Add(child);
                if (child.OrchestrationFailure is not null)
                    orchestrationErrors.Add(
                        $"iteration-{iteration:00}/{descriptor.Id}: {child.OrchestrationFailure}");
            }

            var cleanCases = children
                .SelectMany(child => child.Cases)
                .ToList();
            cleanCases.Add(BuildAggregateMatrixCase(children));
            cleanCases.Add(BuildAggregateRecoveryCase(children));
            cleanCases.Add(BuildAggregateCleanupCase(children));
            cleanCases.Add(BuildAggregateIdentityCase(children));

            var cleanNormalizedSha256 = BuildNormalizedSha256(cleanCases);
            var finalCases = cleanCases.ToList();
            if (deliberateFailure && iteration == requestedIterations)
            {
                var deliberate = new HarnessCaseResult(
                    DeliberateCaseId,
                    HarnessVerdict.KHONG_DAT,
                    "Intentional P6-11 oracle failure appended after every real child case completed.",
                    "deliberate-oracle=false",
                    0);
                finalCases.Add(deliberate);
                PrintCase(deliberate);
            }

            var normalizedSha256 = BuildNormalizedSha256(finalCases);
            var baseCasesPassed = cleanCases.All(
                result => result.Verdict == HarnessVerdict.DAT);
            var cleanupPassed = children.All(child => child.Cleanup.Passed);
            var artifactSecurityPassed = children.All(child => child.Security.Passed);
            var childRunKeys = children
                .Select(child => child.ChildRunKey)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            var iterationChildRunIdentitiesDistinct =
                childRunKeys.Length == Descriptors.Length &&
                childRunKeys.Distinct(StringComparer.Ordinal).Count() ==
                childRunKeys.Length;

            var iterationEvidence = new P6ChaosIterationEvidence(
                iteration,
                cleanCases,
                finalCases,
                cleanNormalizedSha256,
                normalizedSha256,
                baseCasesPassed,
                cleanupPassed,
                artifactSecurityPassed,
                iterationChildRunIdentitiesDistinct,
                children);
            iterations.Add(iterationEvidence);

            await WriteIterationEvidenceAsync(
                paths,
                aggregateIterationRoot,
                runKey,
                iterationEvidence,
                cancellation.Token);
        }

        var completedAtUtc = DateTime.UtcNow;
        var requiredIterationCountSatisfied =
            deliberateFailure
                ? requestedIterations >= 1
                : requestedIterations >= 2;
        var allDescriptorsPresent = iterations.Count == requestedIterations &&
                                    iterations.All(
                                        iteration =>
                                            iteration.Children.Count == Descriptors.Length &&
                                            Descriptors.All(
                                                descriptor =>
                                                    iteration.Children.Count(
                                                        child =>
                                                            child.DescriptorId ==
                                                            descriptor.Id) == 1));
        var allBaseCasesPassed = iterations.All(iteration => iteration.BaseCasesPassed);
        var allCleanupPassed = iterations.All(iteration => iteration.CleanupPassed);
        var allArtifactSecurityPassed = iterations.All(
            iteration => iteration.ArtifactSecurityPassed);
        var allChildExitsClean = iterations
            .SelectMany(iteration => iteration.Children)
            .All(child => child.ExitCode == 0);
        var allChildArtifactsFresh = iterations
            .SelectMany(iteration => iteration.Children)
            .All(child => child.FreshRoot);
        var allRequirementsExact = iterations
            .SelectMany(iteration => iteration.Children)
            .All(child => child.RequirementsExact);
        var allIterationChildIdentitiesDistinct = iterations.All(
            iteration => iteration.ChildRunIdentitiesDistinct);
        var allChildRunKeys = iterations
            .SelectMany(iteration => iteration.Children)
            .Select(child => child.ChildRunKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var childRunIdentitiesDistinct =
            allChildRunKeys.Length == requestedIterations * Descriptors.Length &&
            allChildRunKeys.Distinct(StringComparer.Ordinal).Count() ==
            allChildRunKeys.Length;
        var cleanNormalizedShaMatch =
            iterations.Count == requestedIterations &&
            iterations.Select(iteration => iteration.CleanNormalizedSha256)
                .Distinct(StringComparer.Ordinal)
                .Count() == 1;
        var semanticReproducible =
            allBaseCasesPassed &&
            cleanNormalizedShaMatch;
        var deliberateRows = iterations
            .SelectMany(iteration => iteration.FinalCases)
            .Where(result => result.CaseId == DeliberateCaseId)
            .ToArray();
        var deliberateAssertionObserved =
            deliberateFailure &&
            deliberateRows.Length == 1 &&
            deliberateRows[0].Verdict == HarnessVerdict.KHONG_DAT;

        var cleanGateSatisfied =
            orchestrationErrors.Count == 0 &&
            requiredIterationCountSatisfied &&
            allDescriptorsPresent &&
            allBaseCasesPassed &&
            allCleanupPassed &&
            allArtifactSecurityPassed &&
            allChildExitsClean &&
            allChildArtifactsFresh &&
            allRequirementsExact &&
            allIterationChildIdentitiesDistinct &&
            childRunIdentitiesDistinct &&
            cleanNormalizedShaMatch;
        var passed = !deliberateFailure && cleanGateSatisfied;
        var failureReason = BuildFailureReason(
            deliberateFailure,
            deliberateAssertionObserved,
            orchestrationErrors,
            requiredIterationCountSatisfied,
            allDescriptorsPresent,
            allBaseCasesPassed,
            allCleanupPassed,
            allArtifactSecurityPassed,
            allChildExitsClean,
            allChildArtifactsFresh,
            allRequirementsExact,
            childRunIdentitiesDistinct,
            cleanNormalizedShaMatch);

        var resultsPath = Path.Combine(paths.RunRoot, "results.json");
        await EvidenceJson.WriteAsync(
            resultsPath,
            new
            {
                schemaVersion = 1,
                gate = GateId,
                requirementsOwned = new[]
                {
                    "P6-TOPO-018",
                    "P6-TOPO-019",
                    "P6-TOPO-020"
                },
                runKey,
                startedAtUtc,
                completedAtUtc,
                requestedIterations,
                deliberateFailureMode = deliberateFailure,
                iterations = iterations.Select(
                    iteration => BuildIterationProjection(paths, iteration)),
                checks = new
                {
                    requiredIterationCountSatisfied,
                    allDescriptorsPresent,
                    allBaseCasesPassed,
                    allCleanupPassed,
                    allArtifactSecurityPassed,
                    allChildExitsClean,
                    allChildArtifactsFresh,
                    allRequirementsExact,
                    allIterationChildIdentitiesDistinct,
                    childRunIdentitiesDistinct,
                    cleanNormalizedShaMatch,
                    semanticReproducible,
                    deliberateAssertionObserved
                },
                cleanNormalizedSha256 =
                    iterations.FirstOrDefault()?.CleanNormalizedSha256,
                deterministic = semanticReproducible,
                passed,
                failureReason
            },
            cancellation.Token);
        await EvidenceCsv.WriteCasesAsync(
            Path.Combine(paths.RunRoot, "results.csv"),
            iterations.SelectMany(
                iteration => iteration.FinalCases.Select(
                    result => (iteration.Iteration, Case: result))),
            cancellation.Token);
        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "reconciliation-ledger.json"),
            new
            {
                schemaVersion = 1,
                gate = GateId,
                runKey,
                expectedIterations = requestedIterations,
                actualIterations = iterations.Count,
                descriptorIds = Descriptors.Select(descriptor => descriptor.Id),
                archetypes = Descriptors.Select(descriptor => descriptor.ArchetypeId),
                iterations = iterations.Select(
                    iteration => new
                    {
                        iteration.Iteration,
                        caseCount = iteration.FinalCases.Count,
                        dat = iteration.FinalCases.Count(
                            result => result.Verdict == HarnessVerdict.DAT),
                        khongDat = iteration.FinalCases.Count(
                            result => result.Verdict == HarnessVerdict.KHONG_DAT),
                        iteration.CleanNormalizedSha256,
                        iteration.NormalizedSha256,
                        iteration.BaseCasesPassed,
                        iteration.CleanupPassed,
                        iteration.ArtifactSecurityPassed,
                        iteration.ChildRunIdentitiesDistinct,
                        childRuns = iteration.Children.Select(
                            child => new
                            {
                                child.DescriptorId,
                                child.ArchetypeId,
                                child.ChildRunKey,
                                child.ExitCode,
                                child.FreshRoot,
                                child.RequirementsExact,
                                directMongoVerified =
                                    CasePassed(child, "DIRECT-MONGO"),
                                apiKestrelVerified =
                                    CasePassed(child, "API-KESTREL"),
                                child.Cleanup.Passed,
                                artifactSecurityPassed = child.Security.Passed
                            })
                    }),
                cleanNormalizedShaMatch,
                semanticReproducible,
                childRunIdentitiesDistinct,
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
                gate = GateId,
                runKey,
                completedAtUtc,
                iterations = iterations.Select(
                    iteration => new
                    {
                        iteration.Iteration,
                        cleanupPassed = iteration.CleanupPassed,
                        artifactSecurityPassed =
                            iteration.ArtifactSecurityPassed,
                        children = iteration.Children.Select(
                            child => new
                            {
                                child.DescriptorId,
                                child.ChildRunKey,
                                runRoot = RelativePath(paths, child.RunRoot),
                                child.Cleanup,
                                child.Security
                            })
                    }),
                allCleanupPassed,
                allArtifactSecurityPassed,
                passed = allCleanupPassed && allArtifactSecurityPassed
            },
            cancellation.Token);

        Console.WriteLine(
            passed
                ? $"[DAT] P6-11 chaos gate passed {requestedIterations} clean iteration(s); sha={iterations[0].CleanNormalizedSha256}."
                : $"[KHONG_DAT] P6-11 chaos gate failed: {failureReason}; artifact={resultsPath}");
        return passed ? 0 : 1;
    }

    private static async Task<ChildProbeEvidence> ExecuteChildAsync(
        HarnessPaths paths,
        ProbeDescriptor descriptor,
        CancellationToken ct)
    {
        var before = EnumerateChildRoots(paths.IntegrationRoot, descriptor.RunPrefix)
            .ToHashSet(PathComparer);
        var timer = Stopwatch.StartNew();
        var exitCode = -1;
        string? invocationFailure = null;
        try
        {
            exitCode = await descriptor.Runner();
        }
        catch (Exception error)
        {
            invocationFailure = $"{error.GetType().Name}: {error.Message}";
        }

        ct.ThrowIfCancellationRequested();
        var freshRoots = EnumerateChildRoots(paths.IntegrationRoot, descriptor.RunPrefix)
            .Where(path => !before.Contains(path))
            .OrderBy(path => path, PathComparer)
            .ToArray();
        var freshRoot = freshRoots.Length == 1;
        var runRoot = freshRoot ? freshRoots[0] : null;
        var iterationRoot = runRoot is null
            ? null
            : Path.Combine(runRoot, "iteration-01");
        var resultPath = iterationRoot is null
            ? null
            : Path.Combine(iterationRoot, descriptor.ResultFileName);
        var apiPath = iterationRoot is null
            ? null
            : Path.Combine(iterationRoot, descriptor.ApiFileName);
        var directMongoPath = iterationRoot is null
            ? null
            : Path.Combine(iterationRoot, descriptor.DirectMongoFileName);

        var resultLoad = LoadJson(resultPath, JsonValueKind.Object);
        var apiLoad = LoadJson(apiPath, JsonValueKind.Array);
        var directMongoLoad = LoadJson(directMongoPath, JsonValueKind.Object);
        var childRunKey = resultLoad.Root is null
            ? runRoot is null ? string.Empty : Path.GetFileName(runRoot)
            : GetString(resultLoad.Root.Value, "runKey") ?? string.Empty;
        var requirements = resultLoad.Root is null
            ? []
            : ReadStringArray(resultLoad.Root.Value, "requirements");
        var requirementsExact = SetsEqual(
            descriptor.ExpectedRequirements,
            requirements);

        var cleanup = InspectCleanup(
            iterationRoot,
            resultLoad.Root);
        var security = InspectArtifactSecurity(runRoot);
        var cases = new List<HarnessCaseResult>
        {
            EvaluateCase(
                $"{GateId}-{descriptor.Id}-FRESH-IDENTITY",
                () => ValidateFreshIdentity(
                    descriptor,
                    freshRoots,
                    runRoot,
                    iterationRoot,
                    resultLoad.Root)),
            EvaluateCase(
                $"{GateId}-{descriptor.Id}-RESULT",
                () => ValidateResult(
                    descriptor,
                    exitCode,
                    runRoot,
                    iterationRoot,
                    resultLoad)),
            EvaluateCase(
                $"{GateId}-{descriptor.Id}-REQUIREMENTS",
                () => ValidateRequirements(
                    descriptor,
                    resultLoad,
                    requirements)),
            EvaluateCase(
                $"{GateId}-{descriptor.Id}-API-KESTREL",
                () => ValidateApi(descriptor, apiLoad)),
            EvaluateCase(
                $"{GateId}-{descriptor.Id}-DIRECT-MONGO",
                () => ValidateDirectMongo(
                    descriptor,
                    directMongoLoad,
                    childRunKey,
                    freshRoot)),
            EvaluateCase(
                $"{GateId}-{descriptor.Id}-CLEANUP",
                () => ValidateCleanup(descriptor, cleanup)),
            EvaluateCase(
                $"{GateId}-{descriptor.Id}-ARTIFACT-SECURITY",
                () => ValidateSecurity(descriptor, security))
        };

        foreach (var result in cases)
            PrintCase(result);

        var artifactHashes = BuildArtifactHashes(
            paths,
            resultPath,
            apiPath,
            directMongoPath,
            iterationRoot);
        var orchestrationFailures = new List<string>();
        if (invocationFailure is not null)
            orchestrationFailures.Add($"child invocation threw {invocationFailure}");
        if (!freshRoot)
        {
            orchestrationFailures.Add(
                $"expected exactly one fresh {descriptor.RunPrefix}* root, found {freshRoots.Length}");
        }
        if (exitCode != 0)
            orchestrationFailures.Add($"child exit code was {exitCode}");
        if (resultLoad.Error is not null)
            orchestrationFailures.Add($"result artifact: {resultLoad.Error}");

        return new ChildProbeEvidence(
            descriptor.Id,
            descriptor.ArchetypeId,
            exitCode,
            childRunKey,
            runRoot,
            iterationRoot,
            freshRoot,
            requirements,
            requirementsExact,
            cases,
            cleanup,
            security,
            artifactHashes,
            timer.ElapsedMilliseconds,
            orchestrationFailures.Count == 0
                ? null
                : string.Join("; ", orchestrationFailures));
    }

    private static HarnessCaseResult BuildAggregateMatrixCase(
        IReadOnlyList<ChildProbeEvidence> children)
        => EvaluateCase(
            $"{GateId}-FULL-T03-T12-MATRIX",
            () =>
            {
                Require(
                    children.Count == Descriptors.Length,
                    $"Expected {Descriptors.Length} child probes, found {children.Count}.");
                foreach (var descriptor in Descriptors)
                {
                    var child = children.SingleOrDefault(
                        candidate => candidate.DescriptorId == descriptor.Id);
                    Require(child is not null, $"Missing {descriptor.Id} child.");
                    Require(
                        CasePassed(child!, "RESULT"),
                        $"{descriptor.Id} result was not verified.");
                    Require(
                        CasePassed(child!, "API-KESTREL"),
                        $"{descriptor.Id} Kestrel API ledger was not verified.");
                    Require(
                        CasePassed(child!, "DIRECT-MONGO"),
                        $"{descriptor.Id} direct-Mongo oracle was not verified.");
                }

                return new CaseObservation(
                    "All ten FLOW-T03..T12 topology probes plus the shared FLOW-T01/v1.2 chaos seam passed result, real Kestrel API, and descriptor-specific direct-Mongo oracles.",
                    "matrix=T03..T12+shared-T01-chaos;api=Kestrel;mongo=direct;descriptors=11");
            });

    private static HarnessCaseResult BuildAggregateRecoveryCase(
        IReadOnlyList<ChildProbeEvidence> children)
        => EvaluateCase(
            $"{GateId}-REPLAY-RACE-RECOVERY",
            () =>
            {
                foreach (var id in new[]
                         {
                             "P601", "P602", "P603", "P604", "P605", "P606",
                             "P607", "P608", "P609", "P610", "P611CHAOS"
                         })
                {
                    var child = children.SingleOrDefault(
                        candidate => candidate.DescriptorId == id);
                    Require(child is not null, $"Missing {id} child.");
                    Require(
                        CasePassed(child!, "DIRECT-MONGO"),
                        $"{id} replay/race/recovery semantic oracle was not verified.");
                }

                return new CaseObservation(
                    "Replay, race, hard-kill transaction recovery, fault/restart, lease/reconcile, unique-index, and epoch terminal semantics were retained by their owning real probes.",
                    "replay=true;race=true;hard-kill=before+after;fault-restart=true;lease-reconcile=true;duplicates=0;orphans=0");
            });

    private static HarnessCaseResult BuildAggregateCleanupCase(
        IReadOnlyList<ChildProbeEvidence> children)
        => EvaluateCase(
            $"{GateId}-CLEANUP-AND-SECURITY",
            () =>
            {
                Require(
                    children.All(child => child.Cleanup.Passed),
                    "At least one child cleanup audit failed.");
                Require(
                    children.All(child => child.Security.Passed),
                    "At least one child artifact security audit failed.");
                return new CaseObservation(
                    "Every child reported zero cleanup errors; Mongo and Kestrel processes stopped, ports released, mongo-data disappeared, and artifacts were redacted.",
                    "cleanup=pass;mongo-data=absent;processes=stopped;ports=released;security=pass");
            });

    private static HarnessCaseResult BuildAggregateIdentityCase(
        IReadOnlyList<ChildProbeEvidence> children)
        => EvaluateCase(
            $"{GateId}-FRESH-CHILD-IDENTITIES",
            () =>
            {
                var runKeys = children
                    .Select(child => child.ChildRunKey)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                Require(
                    children.All(child => child.FreshRoot),
                    "At least one child root was not uniquely fresh.");
                Require(
                    runKeys.Length == Descriptors.Length,
                    "One or more child run keys were missing.");
                Require(
                    runKeys.Distinct(StringComparer.Ordinal).Count() ==
                    runKeys.Length,
                    "Child run keys were not distinct.");
                return new CaseObservation(
                    "All eleven child roots and run keys were newly created and distinct.",
                    "fresh-child-roots=11;distinct-run-keys=11");
            });

    private static CaseObservation ValidateFreshIdentity(
        ProbeDescriptor descriptor,
        IReadOnlyList<string> freshRoots,
        string? runRoot,
        string? iterationRoot,
        JsonElement? result)
    {
        Require(
            freshRoots.Count == 1,
            $"Expected one fresh root, found {freshRoots.Count}.");
        RequireDirectory(runRoot, "fresh child run root");
        RequireDirectory(iterationRoot, "child iteration root");
        Require(result is not null, "Result JSON was not loaded.");
        var resultValue = result.GetValueOrDefault();
        var runKey = RequireString(resultValue, "runKey");
        Require(
            runKey.StartsWith(descriptor.RunPrefix, StringComparison.Ordinal),
            $"Run key '{runKey}' does not start with '{descriptor.RunPrefix}'.");
        Require(
            string.Equals(
                Path.GetFileName(runRoot),
                runKey,
                StringComparison.Ordinal),
            "Result runKey does not match the discovered root.");
        var declaredArtifactRoot = RequireString(resultValue, "artifactRoot");
        Require(
            PathsEqual(declaredArtifactRoot, iterationRoot!),
            "Result artifactRoot does not match the discovered iteration root.");
        return new CaseObservation(
            $"{descriptor.Id} emitted one fresh child root with a self-consistent run identity.",
            $"descriptor={descriptor.Id};fresh-root=true;identity-self-consistent=true");
    }

    private static CaseObservation ValidateResult(
        ProbeDescriptor descriptor,
        int exitCode,
        string? runRoot,
        string? iterationRoot,
        JsonLoadResult load)
    {
        Require(exitCode == 0, $"Child exit code was {exitCode}.");
        RequireDirectory(runRoot, "child run root");
        RequireDirectory(iterationRoot, "child iteration root");
        var root = RequireLoaded(load, "result");
        RequireEqual("PASS", RequireString(root, "verdict"), "result verdict");
        var cleanupErrors = ReadStringArray(root, "cleanupErrors");
        Require(cleanupErrors.Count == 0, "Result reported cleanup errors.");

        if (descriptor.ResultCarriesCandidate)
        {
            RequireEqual(
                "1.3",
                RequireString(root, "candidate.version"),
                "candidate version");
            Require(
                RequireBoolean(root, "candidate.currentCatalogModified") == false,
                "Current catalog was reported modified.");
            Require(
                RequireBoolean(root, "candidate.testingActivationOnly") ==
                !DynamicFlowP6CatalogCandidate.ActivationEnabled,
                "Candidate activation-mode evidence does not match the sealed catalog state.");
            RequireSha256(root, "candidate.semanticHash");
            RequireEqual(
                DynamicFlowP6CatalogCandidate.SemanticHash,
                RequireString(root, "candidate.semanticHash"),
                "candidate semantic hash");
        }
        else if (descriptor.Id == "P611CHAOS")
        {
            RequireInt(root, "requiredCaseCount", 4);
            RequireInt(root, "passedCaseCount", 4);
            RequireTrue(root, "cleanupPassed");
        }

        return new CaseObservation(
            $"{descriptor.Id} returned PASS, exit 0, and zero cleanup errors.",
            descriptor.ResultCarriesCandidate
                ? $"descriptor={descriptor.Id};exit=0;verdict=PASS;cleanup-errors=0;candidate={DynamicFlowP6CatalogCandidate.SemanticHash}"
                : descriptor.Id == "P611CHAOS"
                    ? "descriptor=P611CHAOS;exit=0;verdict=PASS;cases=4/4;cleanup-errors=0"
                : $"descriptor={descriptor.Id};exit=0;verdict=PASS;cleanup-errors=0");
    }

    private static CaseObservation ValidateRequirements(
        ProbeDescriptor descriptor,
        JsonLoadResult load,
        IReadOnlyList<string> requirements)
    {
        RequireLoaded(load, "result");
        Require(
            SetsEqual(descriptor.ExpectedRequirements, requirements),
            $"Requirement set mismatch. Expected [{string.Join(",", descriptor.ExpectedRequirements)}], actual [{string.Join(",", requirements)}].");
        return new CaseObservation(
            $"{descriptor.Id} emitted its exact owned requirement set.",
            $"descriptor={descriptor.Id};requirements={string.Join("+", descriptor.ExpectedRequirements.OrderBy(value => value, StringComparer.Ordinal))}");
    }

    private static CaseObservation ValidateApi(
        ProbeDescriptor descriptor,
        JsonLoadResult load)
    {
        var root = RequireLoaded(load, "API exchange");
        var exchanges = root.EnumerateArray().ToArray();
        Require(
            exchanges.Length >= descriptor.MinimumApiExchangeCount,
            $"Expected at least {descriptor.MinimumApiExchangeCount} API exchanges, found {exchanges.Length}.");
        var statusCodes = new List<int>();
        foreach (var exchange in exchanges)
        {
            Require(
                exchange.ValueKind == JsonValueKind.Object,
                "API exchange row was not an object.");
            var method = RequireString(exchange, "method");
            Require(method.Length > 0, "API method was empty.");
            var path = RequireString(exchange, "path");
            Require(
                path.StartsWith("api/", StringComparison.Ordinal),
                $"API path '{path}' was not a canonical relative API path.");
            var statusCode = RequireInt32(exchange, "statusCode");
            Require(
                statusCode is >= 100 and <= 599,
                $"Invalid HTTP status code {statusCode}.");
            statusCodes.Add(statusCode);

            var headers = RequirePath(exchange, "responseHeaders");
            var server = RequireProperty(headers, "Server");
            Require(
                server.ValueKind == JsonValueKind.Array &&
                server.EnumerateArray().Any(
                    value =>
                        value.ValueKind == JsonValueKind.String &&
                        string.Equals(
                            value.GetString(),
                            "Kestrel",
                            StringComparison.OrdinalIgnoreCase)),
                "API response was not attested as Kestrel.");
        }

        foreach (var requiredStatus in descriptor.RequiredApiStatusCodes)
        {
            Require(
                statusCodes.Contains(requiredStatus),
                $"Required API status {requiredStatus} was not observed.");
        }
        if (descriptor.Id == "P611CHAOS")
            return ValidateP611ChaosApi(exchanges);
        if (descriptor.Id == "P608")
            return ValidateP608Api(exchanges);

        Require(
            ContainsString(root, descriptor.ArchetypeId),
            $"API ledger did not contain {descriptor.ArchetypeId}.");
        Require(
            ContainsString(root, "\"catalogVersion\":\"1.3\""),
            "API ledger did not attest the exact 1.3 candidate pin.");

        return new CaseObservation(
            $"{descriptor.Id} recorded real Kestrel exchanges, the exact archetype/candidate pin, and required positive/negative status classes.",
            $"descriptor={descriptor.Id};kestrel=true;archetype={descriptor.ArchetypeId};candidate=1.3;statuses={string.Join("+", descriptor.RequiredApiStatusCodes.Order())}");
    }

    private static CaseObservation ValidateP611ChaosApi(
        IReadOnlyList<JsonElement> exchanges)
    {
        const string bootstrapPath = "api/system/bootstrap";
        const string loginPath = "api/auth/login";
        const string preflightPath =
            @"^api/works/[^/]+/dynamic-flows/preflight$";
        const string confirmPath =
            @"^api/works/[^/]+/dynamic-flows/confirm$";
        const string reconcilePath =
            @"^api/admin/operations/dynamic-flow-runtime/instances/[^/]+/reconcile\?apply=true$";
        const string outboxProcessPath =
            "api/admin/operations/dynamic-flow-runtime/outbox/process?maxItems=1";
        var expectedCommands = new[]
        {
            "p6-11-hard-kill-before-instance-write",
            "p6-11-hard-kill-after-intent-commit",
            "p6-11-after-outbox-claim-lease-loss",
            "p6-11-transient-after-assignment-write"
        };

        Require(
            exchanges.Count == 21,
            $"Shared chaos seam expected exactly 21 completed Kestrel exchanges, found {exchanges.Count}.");
        Require(
            exchanges.All(
                exchange => string.Equals(
                    RequireString(exchange, "method"),
                    "POST",
                    StringComparison.Ordinal)),
            "Shared chaos seam emitted a non-POST exchange.");

        var bootstrapRows = exchanges.Where(
                exchange => string.Equals(
                    RequireString(exchange, "path"),
                    bootstrapPath,
                    StringComparison.Ordinal))
            .ToArray();
        var loginRows = exchanges.Where(
                exchange => string.Equals(
                    RequireString(exchange, "path"),
                    loginPath,
                    StringComparison.Ordinal))
            .ToArray();
        var preflightRows = exchanges.Where(
                exchange => Regex.IsMatch(
                    RequireString(exchange, "path"),
                    preflightPath,
                    RegexOptions.CultureInvariant))
            .ToArray();
        var confirmRows = exchanges.Where(
                exchange => Regex.IsMatch(
                    RequireString(exchange, "path"),
                    confirmPath,
                    RegexOptions.CultureInvariant))
            .ToArray();
        var reconcileRows = exchanges.Where(
                exchange => Regex.IsMatch(
                    RequireString(exchange, "path"),
                    reconcilePath,
                    RegexOptions.CultureInvariant))
            .ToArray();
        var outboxRows = exchanges.Where(
                exchange => string.Equals(
                    RequireString(exchange, "path"),
                    outboxProcessPath,
                    StringComparison.Ordinal))
            .ToArray();
        Require(
            bootstrapRows.Length == 1 &&
            loginRows.Length == 8 &&
            preflightRows.Length == 4 &&
            confirmRows.Length == 5 &&
            reconcileRows.Length == 2 &&
            outboxRows.Length == 1,
            "Shared chaos API route cardinality drifted from bootstrap1/login8/preflight4/confirm5/reconcile2/outbox1.");

        var preflightRequests = preflightRows
            .Select(row => RequireEmbeddedJsonObject(row, "requestBody"))
            .ToArray();
        var preflightResponses = preflightRows
            .Select(row => RequireEmbeddedJsonObject(row, "responseBody"))
            .ToArray();
        Require(
            SetsEqual(
                expectedCommands,
                preflightRequests.Select(
                    request => RequireString(request, "commandId"))),
            "Shared chaos preflight commands were incomplete or duplicated.");
        foreach (var response in preflightResponses)
        {
            RequireEqual(
                "FLOW-T01",
                RequireString(response, "flowPin.archetypeId"),
                "shared chaos archetype pin");
            RequireEqual(
                "1.2",
                RequireString(response, "flowPin.catalogVersion"),
                "shared chaos catalog pin");
            RequireEqual(
                "ELIGIBLE_CANDIDATE",
                RequireString(response, "eligibility"),
                "shared chaos eligibility");
        }

        var confirmResponses = confirmRows
            .Select(row => RequireEmbeddedJsonObject(row, "responseBody"))
            .ToArray();
        var completedConfirmCommands = confirmResponses
            .Select(response => RequireString(response, "commandId"))
            .ToArray();
        Require(
            completedConfirmCommands.Length == 5 &&
            expectedCommands
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(completedConfirmCommands) &&
            expectedCommands[..3].All(commandId =>
                completedConfirmCommands.Count(value =>
                    string.Equals(
                        value,
                        commandId,
                        StringComparison.Ordinal)) == 1) &&
            completedConfirmCommands.Count(value =>
                string.Equals(
                    value,
                    expectedCommands[3],
                    StringComparison.Ordinal)) == 2,
            "Shared chaos completed confirm responses did not cover all four commands.");
        foreach (var commandId in expectedCommands[..3])
        {
            var response = confirmResponses.Single(
                item => string.Equals(
                    RequireString(item, "commandId"),
                    commandId,
                    StringComparison.Ordinal));
            RequireEqual(
                "SUCCEEDED",
                RequireString(response, "status"),
                $"{commandId} replay/lease status");
        }
        var transientResponses = confirmResponses.Where(
                response => string.Equals(
                    RequireString(response, "commandId"),
                    expectedCommands[3],
                    StringComparison.Ordinal))
            .ToArray();
        Require(
            transientResponses.Length == 2 &&
            SetsEqual(
                ["RECOVERY_REQUIRED", "SUCCEEDED"],
                transientResponses.Select(
                    response => RequireString(response, "status"))),
            "Shared chaos transient command did not expose recovery-required then succeeded.");
        RequireEqual(
            RequireString(transientResponses[0], "flowInstanceId"),
            RequireString(transientResponses[1], "flowInstanceId"),
            "shared chaos transient replay instance");

        foreach (var row in reconcileRows)
        {
            var response = RequireEmbeddedJsonObject(row, "responseBody");
            RequireTrue(response, "converged");
            RequireTrue(response, "exactLedgerConverged");
            RequireTrue(response, "exactPinsAndRefs");
        }
        RequireEqual(
            1,
            RequireInt32(
                RequireEmbeddedJsonObject(
                    outboxRows.Single(),
                    "responseBody"),
                "processed"),
            "shared chaos fresh outbox worker count");

        return new CaseObservation(
            "P611CHAOS recorded 21 real Kestrel responses for four FLOW-T01/catalog-1.2 shared materialization boundaries, including restart reconcile and fresh lease processing.",
            "descriptor=P611CHAOS;kestrel=true;shared-seam=FLOW-T01@1.2;api=21;bootstrap=1;login=8;preflight=4;confirm=5;reconcile=2;outbox-process=1;statuses=200");
    }

    private static CaseObservation ValidateP608Api(
        IReadOnlyList<JsonElement> exchanges)
    {
        const string schedulePath =
            @"^api/works/[^/]+/dynamic-flows/periodic-schedules$";
        const string processPath =
            "api/admin/operations/dynamic-flow-runtime/periodic/process";
        const string rerunPath =
            @"^api/works/[^/]+/dynamic-flows/periodic-schedules/[^/]+/occurrences/[^/]+/rerun$";

        var scheduleRows = exchanges
            .Where(
                exchange =>
                    string.Equals(
                        RequireString(exchange, "method"),
                        "POST",
                        StringComparison.Ordinal) &&
                    Regex.IsMatch(
                        RequireString(exchange, "path"),
                        schedulePath,
                        RegexOptions.CultureInvariant))
            .ToArray();
        Require(
            scheduleRows.Length == 3,
            $"T10 expected three schedule-create exchanges, found {scheduleRows.Length}.");

        var scheduleRequests = scheduleRows
            .Select(row => RequireEmbeddedJsonObject(row, "requestBody"))
            .ToArray();
        var scheduleResponses = scheduleRows
            .Select(row => RequireEmbeddedJsonObject(row, "responseBody"))
            .ToArray();
        RequireEqual(
            "p6-08-create-on-time",
            RequireString(scheduleRequests[0], "commandId"),
            "T10 initial schedule command");
        RequireEqual(
            "p6-08-create-on-time",
            RequireString(scheduleRequests[1], "commandId"),
            "T10 schedule replay command");
        RequireEqual(
            RequireString(scheduleRows[0], "requestBody"),
            RequireString(scheduleRows[1], "requestBody"),
            "T10 exact schedule replay request");
        RequireEqual(
            RequireString(scheduleResponses[0], "scheduleId"),
            RequireString(scheduleResponses[1], "scheduleId"),
            "T10 replay schedule identity");
        RequireEqual(
            "p6-08-create-missed",
            RequireString(scheduleRequests[2], "commandId"),
            "T10 missed schedule command");
        RequireDifferent(
            RequireString(scheduleResponses[0], "scheduleId"),
            RequireString(scheduleResponses[2], "scheduleId"),
            "T10 on-time/missed schedule identity");
        foreach (var response in scheduleResponses)
        {
            RequireEqual(
                DynamicFlowPeriodicTopologyContract.PolicyVersion,
                RequireString(response, "policyVersion"),
                "T10 schedule policy version");
            RequireSha256(response, "scheduleIdentityHash");
            RequireEqual(
                "ACTIVE",
                RequireString(response, "state"),
                "T10 schedule state");
        }
        RequireEqual(
            RequireString(scheduleResponses[0], "scheduleIdentityHash"),
            RequireString(scheduleResponses[1], "scheduleIdentityHash"),
            "T10 replay schedule hash");
        RequireDifferent(
            RequireString(scheduleResponses[0], "scheduleIdentityHash"),
            RequireString(scheduleResponses[2], "scheduleIdentityHash"),
            "T10 on-time/missed schedule hash");

        var processRows = exchanges
            .Where(
                exchange =>
                    string.Equals(
                        RequireString(exchange, "method"),
                        "POST",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        RequireString(exchange, "path"),
                        processPath,
                        StringComparison.Ordinal))
            .ToArray();
        Require(
            processRows.Length == 3,
            $"T10 expected three periodic-process exchanges, found {processRows.Length}.");
        var processRequests = processRows
            .Select(row => RequireEmbeddedJsonObject(row, "requestBody"))
            .ToArray();
        var processResponses = processRows
            .Select(row => RequireEmbeddedJsonObject(row, "responseBody"))
            .ToArray();
        RequireEqual(
            RequireString(processRows[0], "requestBody"),
            RequireString(processRows[1], "requestBody"),
            "T10 restart replay process request");
        RequireEqual(
            1,
            RequireInt32(processResponses[0], "launchedCount"),
            "T10 initial launched count");
        RequireEqual(
            0,
            RequireInt32(processResponses[1], "launchedCount"),
            "T10 restart replay launched count");
        RequireEqual(
            1,
            RequireInt32(processResponses[2], "missedCount"),
            "T10 missed audit count");
        Require(
            RequireInt32(processResponses[0], "missedCount") == 0 &&
            RequireInt32(processResponses[1], "missedCount") == 0 &&
            RequireInt32(processResponses[2], "launchedCount") == 0,
            "T10 process ledgers mixed on-time, restart replay, or missed outcomes.");
        RequireDifferent(
            RequireString(processRequests[0], "observedAtUtc"),
            RequireString(processRequests[2], "observedAtUtc"),
            "T10 on-time/missed observation");

        var rerunRows = exchanges
            .Where(
                exchange =>
                    string.Equals(
                        RequireString(exchange, "method"),
                        "POST",
                        StringComparison.Ordinal) &&
                    Regex.IsMatch(
                        RequireString(exchange, "path"),
                        rerunPath,
                        RegexOptions.CultureInvariant))
            .ToArray();
        Require(
            rerunRows.Length == 2,
            $"T10 expected two manual-rerun exchanges, found {rerunRows.Length}.");
        RequireEqual(
            RequireString(rerunRows[0], "path"),
            RequireString(rerunRows[1], "path"),
            "T10 manual-rerun occurrence path");
        var rerunRequests = rerunRows
            .Select(row => RequireEmbeddedJsonObject(row, "requestBody"))
            .ToArray();
        var rerunResponses = rerunRows
            .Select(row => RequireEmbeddedJsonObject(row, "responseBody"))
            .ToArray();
        var commandIds = rerunRequests
            .Select(request => RequireString(request, "commandId"))
            .ToArray();
        Require(
            SetsEqual(
                ["p6-08-manual-rerun-1", "p6-08-manual-rerun-2"],
                commandIds),
            "T10 manual-rerun commands were not the exact distinct fixture commands.");
        foreach (var response in rerunResponses)
        {
            RequireEqual(
                "LAUNCHED",
                RequireString(response, "state"),
                "T10 manual-rerun state");
        }
        RequireEqual(
            RequireString(rerunResponses[0], "occurrenceId"),
            RequireString(rerunResponses[1], "occurrenceId"),
            "T10 manual-rerun occurrence identity");
        RequireEqual(
            RequireString(rerunResponses[0], "flowInstanceId"),
            RequireString(rerunResponses[1], "flowInstanceId"),
            "T10 manual-rerun instance identity");
        Require(
            SetsEqual(
                ["p6-08-manual-rerun-1"],
                RequireStringArray(
                    rerunResponses[0],
                    "manualCommandIds")),
            "T10 first rerun receipt set drifted.");
        Require(
            SetsEqual(
                ["p6-08-manual-rerun-1", "p6-08-manual-rerun-2"],
                RequireStringArray(
                    rerunResponses[1],
                    "manualCommandIds")),
            "T10 second rerun receipt set drifted.");

        return new CaseObservation(
            "P608 recorded real Kestrel schedule create/replay, restart dedupe, missed audit, and two exact manual reruns under the periodic policy.",
            $"descriptor=P608;kestrel=true;policy={DynamicFlowPeriodicTopologyContract.PolicyVersion};schedule-create=3;replay=same;process=launch1+restart0+missed1;reruns=2-distinct;statuses=200");
    }

    private static CaseObservation ValidateDirectMongo(
        ProbeDescriptor descriptor,
        JsonLoadResult load,
        string childRunKey,
        bool freshRoot)
    {
        var root = RequireLoaded(load, "direct-Mongo");
        Require(freshRoot, "Direct-Mongo artifact did not belong to one fresh root.");
        Require(
            !string.IsNullOrWhiteSpace(childRunKey),
            "Direct-Mongo child run key was unavailable.");
        RequireEqual(
            childRunKey,
            RequireString(root, "runKey"),
            "direct-Mongo run identity");
        return descriptor.Id switch
        {
            "P601" => ValidateP601(root),
            "P602" => ValidateP602(root),
            "P603" => ValidateP603(root),
            "P604" => ValidateP604(root),
            "P605" => ValidateP605(root),
            "P606" => ValidateP606(root),
            "P607" => ValidateP607(root),
            "P608" => ValidateP608(root),
            "P609" => ValidateP609(root),
            "P610" => ValidateP610(root),
            "P611CHAOS" => ValidateP611Chaos(root),
            _ => throw new InvalidOperationException(
                $"Unknown P6 descriptor '{descriptor.Id}'.")
        };
    }

    private static CaseObservation ValidateP601(JsonElement root)
    {
        RequireEqual(
            "COMPLETED",
            RequireString(root, "directMongoEvidence.terminalState"),
            "T03 terminal state");
        RequireTrue(root, "directMongoEvidence.exactPins");
        RequireTrue(root, "directMongoEvidence.branchScopedOwnerIdentity");
        RequireInt(root, "directMongoEvidence.stepIds", 3);
        RequireInt(root, "directMongoEvidence.assignmentIds", 3);
        RequireInt(root, "directMongoEvidence.forwardReceiptCount", 2);
        RequireInt(root, "directMongoEvidence.duplicateCount", 0);
        RequireInt(root, "directMongoEvidence.orphanCount", 0);
        RequireTrue(root, "replayEvidence.exactReplay");
        RequireInt(root, "replayEvidence.changedReplayHttpStatus", 409);
        RequireInt(root, "replayEvidence.staleRevisionHttpStatus", 409);
        RequireTrue(root, "replayEvidence.concurrentExactResults");
        RequireInt(root, "replayEvidence.outsiderReadHttpStatus", 404);
        RequireInt(root, "replayEvidence.outsiderForwardHttpStatus", 403);
        RequireTrue(root, "recoveryEvidence.faultInjected");
        RequireTrue(root, "recoveryEvidence.serverRestarted");
        RequireTrue(root, "recoveryEvidence.applyConverged");
        RequireTrue(root, "recoveryEvidence.replayConverged");
        RequireInt(root, "recoveryEvidence.duplicateCount", 0);
        RequireTrue(root, "regressionEvidence.t01.passed");
        RequireTrue(root, "regressionEvidence.t02.passed");
        RequireEqual(
            "1.2",
            RequireString(root, "regressionEvidence.catalogVersion"),
            "T01/T02 catalog");
        RequireBarrier(root, 9);
        return new CaseObservation(
            "T03 direct Mongo proved exact pins/owners, A-B-C completion, replay/CAS/ACL, fault recovery, T01/T02 regression, and zero duplicates/orphans.",
            "T03=COMPLETED;steps=3;assignments=3;replay=exact;changed=409;stale=409;ACL=404+403;recovery=converged;duplicate=0;orphan=0");
    }

    private static CaseObservation ValidateP602(JsonElement root)
    {
        RequireEqual(
            "COMPLETED",
            RequireString(root, "directMongoEvidence.terminalState"),
            "T04 terminal state");
        RequireInt(root, "directMongoEvidence.branchCount", 2);
        RequireInt(root, "directMongoEvidence.branches", 2);
        RequireInt(root, "directMongoEvidence.stepCount", 3);
        RequireInt(root, "directMongoEvidence.assignmentCount", 3);
        RequireInt(root, "directMongoEvidence.outboxCount", 3);
        RequireInt(root, "directMongoEvidence.duplicateCount", 0);
        RequireInt(root, "directMongoEvidence.orphanCount", 0);
        RequireTrue(root, "directMongoEvidence.exactPins");
        RequireTrue(root, "directMongoEvidence.independentBranchOwnerIdentity");
        RequireTrue(root, "recoveryEvidence.faultInjected");
        RequireTrue(root, "recoveryEvidence.serverRestarted");
        RequireTrue(root, "recoveryEvidence.applyConverged");
        RequireTrue(root, "recoveryEvidence.replayConverged");
        RequireInt(root, "recoveryEvidence.duplicateCount", 0);
        RequireInt(root, "recoveryEvidence.orphanCount", 0);
        RequireTrue(root, "raceEvidence.exactlyOneTerminalAggregate");
        RequireInt(root, "raceEvidence.completedBranchCount", 2);
        RequireBarrier(root, 8);
        return new CaseObservation(
            "T04 direct Mongo proved two deterministic branches/owners, one terminal aggregate, recovery convergence, and zero duplicates/orphans.",
            "T04=COMPLETED;branches=2;owners=independent;terminal-aggregate=1;recovery=converged;duplicate=0;orphan=0");
    }

    private static CaseObservation ValidateP603(JsonElement root)
    {
        RequireEqual(
            "SATISFIED",
            RequireString(root, "directMongoEvidence.gatewayState"),
            "T05 gateway state");
        RequireInt(root, "directMongoEvidence.expectedContributionCount", 2);
        RequireInt(root, "directMongoEvidence.arrivedContributionCount", 2);
        RequireInt(root, "directMongoEvidence.missingContributionCount", 0);
        RequireInt(root, "directMongoEvidence.contributionLedgerCount", 2);
        RequireInt(root, "directMongoEvidence.contributionAcceptedEventCount", 2);
        RequireInt(root, "directMongoEvidence.joinAllSatisfiedEventCount", 1);
        RequireInt(root, "directMongoEvidence.instanceCompletedEventCount", 1);
        RequireInt(root, "directMongoEvidence.duplicateCount", 0);
        RequireInt(root, "directMongoEvidence.orphanCount", 0);
        RequireTrue(root, "directMongoEvidence.readProjectionVerified");
        RequireTrue(root, "collectingEvidence.serverRestarted");
        RequireEqual(
            "AFTER_JOIN_CONTRIBUTION_WRITE",
            RequireString(root, "collectingEvidence.injectedFaultPoint"),
            "T05 fault point");
        RequireEqual(
            "COLLECTING",
            RequireString(root, "collectingEvidence.state"),
            "T05 intermediate state");
        RequireInt(root, "collectingEvidence.arrivedCount", 1);
        RequireInt(root, "collectingEvidence.missingCount", 1);
        RequireBarrier(root, 7);
        return new CaseObservation(
            "T05 direct Mongo proved COLLECTING then one ALL release with two contribution ledgers/events and no duplicate/orphan.",
            "T05=SATISFIED;expected=2;arrived=2;missing=0;release=1;fault=AFTER_JOIN_CONTRIBUTION_WRITE;duplicate=0;orphan=0");
    }

    private static CaseObservation ValidateP604(JsonElement root)
    {
        RequireTrue(root, "winnerEvidence.injectedRollback");
        RequireEqual(
            "SATISFIED",
            RequireString(root, "winnerEvidence.state"),
            "T06 winner state");
        RequireInt(root, "directMongoEvidence.requiredContributionCount", 1);
        RequireInt(root, "directMongoEvidence.arrived", 1);
        RequireInt(root, "directMongoEvidence.cancelled", 1);
        RequireInt(root, "directMongoEvidence.late", 1);
        RequireInt(root, "directMongoEvidence.contributionLedgerCount", 2);
        RequireInt(root, "directMongoEvidence.duplicateCount", 0);
        RequireInt(root, "directMongoEvidence.orphanCount", 0);
        RequireTrue(root, "directMongoEvidence.readProjectionVerified");
        RequireBarrier(root, 6);
        return new CaseObservation(
            "T06 direct Mongo proved transactional rollback, one quorum winner, one cancelled/late contribution, stable read state, and no duplicate/orphan.",
            "T06=SATISFIED;required=1;arrived=1;cancelled=1;late=1;rollback=true;duplicate=0;orphan=0");
    }

    private static CaseObservation ValidateP605(JsonElement root)
    {
        RequireEqual(
            "P6-TYPED-AST-1",
            RequireString(root, "decisionEvidence.evaluatorVersion"),
            "T07 evaluator");
        RequireEqual(
            "RULE_MATCHED",
            RequireString(root, "decisionEvidence.decisionReasonCode"),
            "T07 decision reason");
        RequireTrue(root, "decisionEvidence.changedFactReplayStable");
        RequireTrue(root, "decisionEvidence.aclZeroWrite");
        RequireEqual(
            "DYNAMIC_FLOW_CONDITION_TYPE_MISMATCH",
            RequireString(root, "failureEvidence.errorCode"),
            "T07 type mismatch code");
        RequireTrue(root, "failureEvidence.zeroWrite");
        RequireInt(root, "directMongoEvidence.stepCount", 2);
        RequireInt(root, "directMongoEvidence.assignmentCount", 2);
        RequireInt(root, "directMongoEvidence.outboxCount", 2);
        RequireFalse(root, "directMongoEvidence.factValuesPersisted");
        RequireTrue(root, "directMongoEvidence.readProjectionVerified");
        RequireInt(root, "directMongoEvidence.duplicateCount", 0);
        RequireInt(root, "directMongoEvidence.orphanCount", 0);
        RequireBarrier(root, 5);
        return new CaseObservation(
            "T07 direct Mongo proved typed deterministic selection, stable changed-fact replay, ACL/type zero-write, no fact leakage, and no duplicate/orphan.",
            "T07=evaluator:P6-TYPED-AST-1;reason=RULE_MATCHED;changed-fact-replay=stable;ACL=zero-write;type-error=zero-write;facts-persisted=false;duplicate=0;orphan=0");
    }

    private static CaseObservation ValidateP606(JsonElement root)
    {
        RequireTrue(root, "lifecycleEvidence.aclZeroWrite");
        RequireTrue(root, "lifecycleEvidence.exactReplayZeroWrite");
        RequireTrue(root, "lifecycleEvidence.backendRestartRecovered");
        RequireInt(root, "lifecycleEvidence.initialReviewCycleNo", 1);
        RequireInt(root, "lifecycleEvidence.nextReviewCycleNo", 2);
        RequireDifferent(
            RequireString(root, "lifecycleEvidence.initialStepInstanceId"),
            RequireString(root, "lifecycleEvidence.nextStepInstanceId"),
            "T08 step attempt identity");
        RequireDifferent(
            RequireString(root, "lifecycleEvidence.initialAssignmentId"),
            RequireString(root, "lifecycleEvidence.nextAssignmentId"),
            "T08 assignment attempt identity");
        RequireEqual(
            "FAILED",
            RequireString(root, "exhaustedEvidence.state"),
            "T08 exhausted state");
        RequireInt(root, "exhaustedEvidence.maxReviewCycles", 1);
        RequireInt(root, "exhaustedEvidence.attemptCount", 1);
        RequireTrue(root, "exhaustedEvidence.stableTerminalEvent");
        RequireInt(root, "directMongoEvidence.stepCount", 2);
        RequireInt(root, "directMongoEvidence.assignmentCount", 2);
        RequireInt(root, "directMongoEvidence.reportCount", 2);
        RequireTrue(root, "directMongoEvidence.oneActiveAttempt");
        RequireTrue(root, "directMongoEvidence.immutableLineage");
        RequireTrue(root, "directMongoEvidence.oldArtifactsReadonly");
        RequireTrue(root, "directMongoEvidence.readAndTimelineVerified");
        RequireInt(root, "directMongoEvidence.duplicateCount", 0);
        RequireInt(root, "directMongoEvidence.orphanCount", 0);
        RequireBarrier(root, 4);
        return new CaseObservation(
            "T08 direct Mongo proved immutable 1→2 attempt/report/assignment lineage, replay/ACL zero-write, restart recovery, max-cycle failure, and no duplicate/orphan.",
            "T08=cycles:1->2;steps=2;assignments=2;reports=2;lineage=immutable;old=readonly;max1=FAILED;duplicate=0;orphan=0");
    }

    private static CaseObservation ValidateP607(JsonElement root)
    {
        foreach (var prefix in new[] { "successEvidence", "failureEvidence" })
        {
            RequireInt(root, $"{prefix}.ancestryDepth", 1);
            RequireInt(root, $"{prefix}.duplicateCount", 0);
            RequireInt(root, $"{prefix}.orphanCount", 0);
            RequireTrue(root, $"{prefix}.immutableLineage");
            RequireNonEmpty(root, $"{prefix}.parentInstanceId");
            RequireNonEmpty(root, $"{prefix}.childInstanceId");
            RequireNonEmpty(root, $"{prefix}.exactChildFamilyId");
            RequireNonEmpty(root, $"{prefix}.exactChildVersionId");
        }
        RequireEqual(
            "COMPLETED",
            RequireString(root, "successEvidence.expectedInstanceState"),
            "T09 success state");
        RequireEqual(
            "FAILED",
            RequireString(root, "failureEvidence.expectedInstanceState"),
            "T09 failure state");
        RequireDifferent(
            RequireString(root, "successEvidence.childInstanceId"),
            RequireString(root, "failureEvidence.childInstanceId"),
            "T09 success/failure child identity");
        RequireBarrier(root, 3);
        return new CaseObservation(
            "T09 direct Mongo proved distinct exact-pinned child success/failure lineages, depth one, immutable ancestry, and no duplicate/orphan.",
            "T09=success:COMPLETED+failure:FAILED;depth=1;lineage=immutable;duplicate=0;orphan=0");
    }

    private static CaseObservation ValidateP608(JsonElement root)
    {
        RequireEqual(
            DynamicFlowPeriodicTopologyContract.ArchetypeId,
            RequireString(root, "directMongo.archetypeId"),
            "T10 archetype pin");
        RequireEqual(
            DynamicFlowP6CatalogCandidate.Version,
            RequireString(root, "directMongo.catalogVersion"),
            "T10 catalog version pin");
        RequireEqual(
            DynamicFlowP6CatalogCandidate.SemanticHash,
            RequireString(root, "directMongo.catalogSemanticHash"),
            "T10 catalog semantic hash pin");
        RequireTrue(root, "directMongo.exactPins");
        var payloadHashes = RequireStringArray(
            root,
            "directMongo.payloadHashes");
        Require(
            payloadHashes.Count == 2 &&
            payloadHashes.Distinct(StringComparer.Ordinal).Count() == 2,
            "T10 expected two distinct exact payload hashes.");
        foreach (var payloadHash in payloadHashes)
            RequireSha256Value(payloadHash, "T10 payload hash");
        RequireInt(root, "directMongo.scheduleCount", 2);
        RequireInt(root, "directMongo.occurrenceCount", 2);
        RequireInt(root, "directMongo.instanceCount", 2);
        RequireInt(root, "directMongo.participantSnapshotCount", 2);
        RequireInt(root, "directMongo.stepCount", 2);
        RequireInt(root, "directMongo.eventCount", 4);
        RequireInt(root, "directMongo.outboxCount", 2);
        RequireInt(root, "directMongo.assignmentCount", 2);
        RequireInt(root, "directMongo.bindingCount", 2);
        RequireInt(root, "directMongo.periodCount", 2);
        RequireInt(root, "directMongo.reportCount", 0);
        RequireTrue(root, "directMongo.ownerIdentitiesDistinct");
        RequireEqual(
            "MATERIALIZING",
            RequireString(root, "directMongo.instanceState"),
            "T10 post-materialization instance state");
        RequireEqual(
            "NOT_MATERIALIZED_UNTIL_REPORT_OPEN",
            RequireString(root, "directMongo.reportOwnerState"),
            "T10 report owner state");
        RequireTrue(root, "directMongo.uniqueDuplicateRejected");
        RequireTrue(root, "directMongo.noOrphans");
        RequireNonEmpty(root, "directMongo.onTimePeriodKey");
        RequireNonEmpty(root, "directMongo.missedPeriodKey");
        RequireDifferent(
            RequireString(root, "directMongo.onTimePeriodKey"),
            RequireString(root, "directMongo.missedPeriodKey"),
            "T10 on-time/missed period key");
        var commands = RequirePath(root, "directMongo.manualCommands");
        Require(
            commands.ValueKind == JsonValueKind.Array,
            "T10 manualCommands was not an array.");
        var commandValues = commands.EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();
        Require(commandValues.Length == 2, "T10 expected two manual commands.");
        Require(
            commandValues.Distinct(StringComparer.Ordinal).Count() == 2,
            "T10 manual command identities were not distinct.");
        return new CaseObservation(
            "T10 direct Mongo proved exact pins plus two snapshot/step/event/outbox/assignment/binding/period lineages, report-not-open state, schedule/occurrence uniqueness, distinct reruns, and no orphan.",
            $"T10=archetype:{DynamicFlowPeriodicTopologyContract.ArchetypeId};catalog:{DynamicFlowP6CatalogCandidate.Version}@{DynamicFlowP6CatalogCandidate.SemanticHash};payload-hashes=2-distinct;schedules:2;occurrences:2;instances:2-MATERIALIZING;snapshots:2;steps:2;events:4;outboxes:2;assignments:2;bindings:2;periods:2;reports:0-not-open;owners=distinct;manual-commands=2-distinct;unique-index=true;orphan=0");
    }

    private static CaseObservation ValidateP609(JsonElement root)
    {
        RequireEqual(
            "CANCELLED_BY_GATEWAY",
            RequireString(root, "directMongo.state"),
            "T11 final state");
        RequireTrue(root, "directMongo.completionRequired");
        RequireInt(root, "directMongo.receiptCount", 2);
        RequireTrue(root, "directMongo.uniqueDuplicateRejected");
        RequireSha256(root, "directMongo.forgedZeroWriteHash");
        var eventTypes = RequirePath(root, "directMongo.eventTypes");
        Require(
            eventTypes.ValueKind == JsonValueKind.Array,
            "T11 eventTypes was not an array.");
        var actual = eventTypes.EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();
        var expected = new[]
        {
            "SUPPLEMENTAL_ASSIGNMENT_MATERIALIZED",
            "SUPPLEMENTAL_STEP_ADDED",
            "SUPPLEMENTAL_STEP_CANCELLED"
        };
        Require(
            SetsEqual(expected, actual),
            "T11 event types were incomplete or duplicated.");
        return new CaseObservation(
            "T11 direct Mongo proved required audited add/materialize/cancel, exact receipts, forged zero-write, and unique supplemental identity.",
            "T11=CANCELLED_BY_GATEWAY;completion-required=true;receipts=2;events=add+materialize+cancel;forged=zero-write;unique-index=true");
    }

    private static CaseObservation ValidateP610(JsonElement root)
    {
        var epochs = RequirePath(root, "directMongo.rollbackEpochs");
        Require(
            epochs.ValueKind == JsonValueKind.Array &&
            epochs.GetArrayLength() == 2,
            "T12 rollback epochs must contain exactly two rows.");
        var epochRows = epochs.EnumerateArray().ToArray();
        var epochOne = epochRows.Single(
            row => RequireInt32(row, "executionEpoch") == 1);
        var epochTwo = epochRows.Single(
            row => RequireInt32(row, "executionEpoch") == 2);
        RequireEqual(
            "ROLLED_BACK",
            RequireString(epochOne, "state"),
            "T12 old epoch state");
        Require(
            RequireBoolean(epochOne, "isCanonical") == false,
            "T12 old epoch remained canonical.");
        RequireInt(epochOne, "replacedByExecutionEpoch", 2);
        RequireEqual(
            "ACTIVE",
            RequireString(epochTwo, "state"),
            "T12 replacement epoch state");
        RequireTrue(epochTwo, "isCanonical");
        RequireNonEmpty(root, "directMongo.invalidatedStepId");
        RequireNonEmpty(root, "directMongo.invalidatedGatewayId");
        RequireNonEmpty(root, "directMongo.invalidatedReportId");
        RequireInt(root, "directMongo.invalidatedStatisticIds", 3);
        RequireNonEmpty(root, "directMongo.replacementStepId");
        RequireNonEmpty(root, "directMongo.rebuildIntentId");
        RequireFalse(root, "directMongo.p8ExecutionEnabled");
        RequireSha256(root, "directMongo.prematureFinalizeZeroWriteHash");
        RequireSha256(root, "directMongo.forgedZeroWriteHash");
        RequireSha256(root, "directMongo.staleZeroWriteHash");
        RequireSha256(root, "directMongo.irreversibleZeroWriteHash");
        RequireOneOf(
            RequireString(root, "directMongo.raceWinner"),
            "restart",
            "terminate");
        RequireInt(root, "directMongo.raceReceiptCount", 2);
        RequireInt(root, "directMongo.raceEventCount", 2);
        RequireInt(root, "directMongo.rollbackReadEpochCount", 2);
        RequireTrue(root, "directMongo.rollbackTimelineObserved");
        RequireEqual(
            "FINALIZED",
            RequireString(root, "directMongo.finalizedEpochState"),
            "T12 finalized state");
        RequireInt(root, "directMongo.finalizedReadEpochCount", 1);
        RequireTrue(root, "directMongo.finalizedTimelineObserved");
        RequireTrue(root, "directMongo.uniqueDuplicateRejected");
        return new CaseObservation(
            "T12 direct Mongo proved canonical epoch replacement, full invalidation/rebuild intent without P8, one terminal race winner, irreversible finalize, read/timeline parity, and unique epoch index.",
            "T12=epoch1:ROLLED_BACK+epoch2:ACTIVE;closure=step+gateway+report+stats3;P8=false;race=one-winner;finalized=irreversible;unique-index=true");
    }

    private static CaseObservation ValidateP611Chaos(JsonElement root)
    {
        var cases = RequirePath(root, "cases");
        Require(
            cases.ValueKind == JsonValueKind.Array &&
            cases.GetArrayLength() == 4,
            "P6-11 shared chaos evidence must contain exactly four cases.");
        var rows = cases.EnumerateArray().ToArray();
        var expectedCaseIds = new[]
        {
            "P6-11-HARD-KILL-BEFORE-INSTANCE-WRITE",
            "P6-11-HARD-KILL-AFTER-INTENT-COMMIT",
            "P6-11-AFTER-OUTBOX-CLAIM-LEASE-LOSS",
            "P6-11-TRANSIENT-AFTER-ASSIGNMENT-WRITE"
        };
        Require(
            SetsEqual(
                expectedCaseIds,
                rows.Select(row => RequireString(row, "caseId")).ToArray()),
            "P6-11 shared chaos case identity set drifted.");
        Require(
            rows.All(row =>
                string.Equals(
                    RequireString(row, "verdict"),
                    "PASS",
                    StringComparison.Ordinal)),
            "P6-11 shared chaos contained a non-PASS boundary.");

        JsonElement Case(string caseId)
            => rows.Single(row =>
                string.Equals(
                    RequireString(row, "caseId"),
                    caseId,
                    StringComparison.Ordinal));

        var beforeCommit = Case(expectedCaseIds[0]);
        RequireTrue(beforeCommit, "interruptedRequest.interrupted");
        foreach (var path in new[]
                 {
                     "receiptCount",
                     "instanceCount",
                     "participantSnapshotCount",
                     "stepCount",
                     "eventCount",
                     "outboxCount",
                     "assignmentCount",
                     "bindingCount",
                     "periodCount",
                     "reportCount",
                     "queueCount",
                     "totalBusinessRows"
                 })
        {
            RequireInt(beforeCommit, $"afterCrash.{path}", 0);
        }
        var transactionBaseline = RequirePath(
            beforeCommit,
            "orphanTransactionRecovery.baseline");
        var transactionAfterKill = RequirePath(
            beforeCommit,
            "orphanTransactionRecovery.afterKill");
        var transactionTerminal = RequirePath(
            beforeCommit,
            "orphanTransactionRecovery.terminal");
        Require(
            RequireInt32(transactionBaseline, "currentOpen") == 0 &&
            RequireInt32(transactionBaseline, "currentActive") == 0 &&
            RequireInt32(transactionBaseline, "currentInactive") == 0 &&
            RequireInt32(transactionAfterKill, "currentOpen") == 1 &&
            RequireInt32(transactionAfterKill, "currentActive") == 0 &&
            RequireInt32(transactionAfterKill, "currentInactive") == 1 &&
            RequireInt32(transactionAfterKill, "totalStarted") ==
                RequireInt32(transactionBaseline, "totalStarted") + 1 &&
            RequireInt32(transactionTerminal, "currentOpen") == 0 &&
            RequireInt32(transactionTerminal, "currentActive") == 0 &&
            RequireInt32(transactionTerminal, "currentInactive") == 0 &&
            RequireInt32(transactionTerminal, "totalStarted") ==
                RequireInt32(transactionAfterKill, "totalStarted") &&
            RequireInt32(transactionTerminal, "totalAborted") >=
                RequireInt32(transactionBaseline, "totalAborted") + 1,
            "P6-11 orphan Mongo transaction did not become visible and abort terminally.");
        Require(
            RequireInt32(
                beforeCommit,
                "orphanTransactionRecovery.pollCount") > 0 &&
            RequireInt32(
                beforeCommit,
                "orphanTransactionRecovery.waitElapsedMilliseconds") > 0,
            "P6-11 orphan transaction recovery was not polled.");
        RequireInt(beforeCommit, "restartReplayCount", 1);
        ValidateP611ConvergedLedger(
            RequirePath(beforeCommit, "afterRestartReplay"),
            expectedEventCount: 2,
            expectedOutboxAttemptCount: 0,
            expectedTotalBusinessRows: 11);

        var afterIntent = Case(expectedCaseIds[1]);
        RequireTrue(afterIntent, "interruptedRequest.interrupted");
        RequireEqual(
            "PENDING",
            RequireString(afterIntent, "durableIntent.receiptStatus"),
            "P6-11 durable intent receipt");
        RequireInt(afterIntent, "durableIntent.instanceCount", 1);
        RequireInt(afterIntent, "durableIntent.stepCount", 1);
        RequireInt(afterIntent, "durableIntent.snapshotCount", 1);
        RequireInt(afterIntent, "durableIntent.eventCount", 1);
        RequireInt(afterIntent, "durableIntent.outboxCount", 1);
        RequireInt(afterIntent, "durableIntent.assignmentCount", 0);
        RequireInt(afterIntent, "durableIntent.bindingClaimCount", 1);
        RequireEqual(
            "MATERIALIZING",
            RequireStringArray(
                afterIntent,
                "afterCrash.instanceStates").Single(),
            "P6-11 after-intent instance state");
        RequireEqual(
            "PENDING",
            RequireStringArray(
                afterIntent,
                "afterCrash.outboxStatuses").Single(),
            "P6-11 after-intent outbox state");
        RequireInt(afterIntent, "afterCrash.assignmentCount", 0);
        RequireInt(afterIntent, "afterCrash.periodCount", 0);
        RequireInt(afterIntent, "afterCrash.totalBusinessRows", 7);
        RequireTrue(afterIntent, "reconcileConverged");
        RequireInt(afterIntent, "reconcileCount", 1);
        ValidateP611ConvergedLedger(
            RequirePath(afterIntent, "afterRestartReconcile"),
            expectedEventCount: 5,
            expectedOutboxAttemptCount: 0,
            expectedTotalBusinessRows: 14);

        var leaseLoss = Case(expectedCaseIds[2]);
        RequireNonEmpty(leaseLoss, "staleClaim.id");
        RequireNonEmpty(leaseLoss, "staleClaim.staleLeaseId");
        RequireNonEmpty(
            leaseLoss,
            "directMongoReplacement.replacementLeaseId");
        RequireDifferent(
            RequireString(leaseLoss, "staleClaim.staleLeaseId"),
            RequireString(
                leaseLoss,
                "directMongoReplacement.replacementLeaseId"),
            "P6-11 stale/replacement lease identity");
        RequireInt(
            leaseLoss,
            "directMongoReplacement.modifiedCount",
            1);
        RequireInt(leaseLoss, "freshWorkerProcessed", 1);
        RequireTrue(leaseLoss, "staleLeaseProtectionObserved");
        RequireEqual(
            "COMPLETED",
            RequireString(leaseLoss, "afterFreshWorkerStatus"),
            "P6-11 fresh worker status");
        ValidateP611ConvergedLedger(
            RequirePath(leaseLoss, "afterRelease"),
            expectedEventCount: 2,
            expectedOutboxAttemptCount: 0,
            expectedTotalBusinessRows: 11);

        var transient = Case(expectedCaseIds[3]);
        RequireEqual(
            "PENDING",
            RequireString(transient, "partial.receiptStatus"),
            "P6-11 transient receipt status");
        RequireEqual(
            "PARTIAL",
            RequireString(transient, "partial.instanceState"),
            "P6-11 transient instance state");
        RequireInt(transient, "partial.completedOutbox", 0);
        RequireInt(transient, "partial.failedOutbox", 1);
        RequireInt(transient, "partial.assignments", 1);
        RequireEqual(
            "PARTIAL",
            RequireStringArray(
                transient,
                "afterFault.instanceStates").Single(),
            "P6-11 transient durable instance state");
        RequireEqual(
            "FAILED",
            RequireStringArray(
                transient,
                "afterFault.outboxStatuses").Single(),
            "P6-11 transient durable outbox state");
        RequireInt(transient, "afterFault.assignmentCount", 1);
        RequireInt(transient, "afterFault.periodCount", 0);
        RequireInt(transient, "afterFault.totalBusinessRows", 8);
        RequireInt(transient, "reconcileCount", 1);
        RequireTrue(transient, "reconcileConverged");
        ValidateP611ConvergedLedger(
            RequirePath(transient, "afterRestartReconcile"),
            expectedEventCount: 5,
            expectedOutboxAttemptCount: 1,
            expectedTotalBusinessRows: 14);

        return new CaseObservation(
            "P611CHAOS direct Mongo proved pre-commit abort/zero-write, durable post-commit intent, stale-lease replacement, transient partial recovery, exact-once convergence, and no duplicate/orphan.",
            "shared-chaos=FLOW-T01@1.2;boundaries=before-commit+after-intent+lease-loss+transient-write;orphan-txn=aborted;reconcile=1+1;fresh-worker=1;replay=1;duplicates=0;orphans=0");
    }

    private static void ValidateP611ConvergedLedger(
        JsonElement row,
        int expectedEventCount,
        int expectedOutboxAttemptCount,
        int expectedTotalBusinessRows)
    {
        RequireInt(row, "receiptCount", 1);
        RequireInt(row, "instanceCount", 1);
        RequireInt(row, "participantSnapshotCount", 1);
        RequireInt(row, "stepCount", 1);
        RequireInt(row, "eventCount", expectedEventCount);
        RequireInt(row, "outboxCount", 1);
        RequireInt(row, "assignmentCount", 1);
        RequireInt(row, "bindingCount", 1);
        RequireInt(row, "periodCount", 1);
        RequireInt(row, "reportCount", 0);
        RequireInt(row, "queueCount", 1);
        RequireInt(row, "totalBusinessRows", expectedTotalBusinessRows);
        RequireEqual(
            "P5_FLOW_RUNTIME",
            RequireString(row, "workAssignmentTopologyOwner"),
            "P6-11 converged topology owner");
        RequireEqual(
            RequireString(row, "flowInstanceId"),
            RequireString(row, "workRuntimeInstanceId"),
            "P6-11 converged work/instance identity");
        RequireEqual(
            "ACTIVE",
            RequireStringArray(row, "instanceStates").Single(),
            "P6-11 converged instance state");
        RequireEqual(
            "COMPLETED",
            RequireStringArray(row, "outboxStatuses").Single(),
            "P6-11 converged outbox state");
        RequireEqual(
            expectedOutboxAttemptCount,
            RequireIntArray(row, "outboxAttemptCounts").Single(),
            "P6-11 converged outbox attempt count");
    }

    private static CaseObservation ValidateCleanup(
        ProbeDescriptor descriptor,
        CleanupAudit cleanup)
    {
        Require(
            cleanup.Passed,
            $"Cleanup failed: {string.Join("; ", cleanup.Failures)}");
        return new CaseObservation(
            $"{descriptor.Id} cleanup verified zero child errors, guarded DB-drop implication, stopped Mongo/Kestrel PIDs, released ports, and absent mongo-data.",
            $"descriptor={descriptor.Id};cleanup=true;db-drop-implied=true;mongo-stopped=true;mongo-port-released=true;backend-count={cleanup.Backends.Count};backend-stopped=true;backend-ports-released=true;mongo-data-absent=true");
    }

    private static CaseObservation ValidateSecurity(
        ProbeDescriptor descriptor,
        ArtifactSecurityAudit security)
    {
        Require(
            security.Passed,
            $"Artifact security failed: {string.Join("; ", security.Failures)}");
        return new CaseObservation(
            $"{descriptor.Id} JSON/text artifacts were valid and contained no unredacted secret property, bearer token, JWT, or forbidden secret/stop file.",
            $"descriptor={descriptor.Id};artifact-security=true;redaction=true;forbidden-leftovers=0");
    }

    private static CleanupAudit InspectCleanup(
        string? iterationRoot,
        JsonElement? result)
    {
        var failures = new List<string>();
        var resultCleanupErrors = result is null
            ? ["result JSON unavailable"]
            : ReadStringArray(result.Value, "cleanupErrors");
        var databaseDropImplied = result is not null &&
                                  resultCleanupErrors.Count == 0;
        if (!databaseDropImplied)
            failures.Add("Result cleanupErrors did not prove guarded database cleanup.");

        var mongoManifestPath = iterationRoot is null
            ? null
            : Path.Combine(iterationRoot, "mongo-process-cleanup.json");
        var mongo = InspectProcessManifest(
            mongoManifestPath,
            "Mongo",
            failures);

        var backendPaths = iterationRoot is not null &&
                           Directory.Exists(iterationRoot)
            ? Directory.EnumerateFiles(
                    iterationRoot,
                    "backend-cleanup.json",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, PathComparer)
                .ToArray()
            : [];
        if (backendPaths.Length == 0)
            failures.Add("No backend-cleanup.json manifest was found.");
        var backends = backendPaths
            .Select(
                path => InspectProcessManifest(
                    path,
                    "Backend",
                    failures))
            .ToArray();

        var mongoDataPath = iterationRoot is null
            ? null
            : Path.Combine(iterationRoot, "mongo-data");
        var mongoDataAbsent =
            mongoDataPath is not null &&
            !Directory.Exists(mongoDataPath) &&
            !File.Exists(mongoDataPath);
        if (!mongoDataAbsent)
            failures.Add("mongo-data still exists after child cleanup.");

        return new CleanupAudit(
            failures.Count == 0,
            databaseDropImplied,
            mongo,
            backends,
            mongoDataAbsent,
            failures.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static ProcessCleanupAudit InspectProcessManifest(
        string? path,
        string kind,
        ICollection<string> failures)
    {
        if (path is null || !File.Exists(path))
        {
            failures.Add($"{kind} cleanup manifest is missing.");
            return new ProcessCleanupAudit(
                path ?? string.Empty,
                0,
                0,
                ManifestStopped: false,
                ManifestPortReleased: false,
                ProcessAbsent: false,
                PortBindable: false);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
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

            if (!manifestStopped)
                failures.Add($"{kind} cleanup manifest did not attest process stop.");
            if (!manifestPortReleased)
                failures.Add($"{kind} cleanup manifest did not attest port release.");
            if (!processAbsent)
                failures.Add($"{kind} PID {processId} is still present.");
            if (!portBindable)
                failures.Add($"{kind} port {port} is not bindable after cleanup.");

            return new ProcessCleanupAudit(
                path,
                processId,
                port,
                manifestStopped,
                manifestPortReleased,
                processAbsent,
                portBindable);
        }
        catch (Exception error)
        {
            failures.Add(
                $"{kind} cleanup manifest is invalid: {error.GetType().Name}.");
            return new ProcessCleanupAudit(
                path,
                0,
                0,
                ManifestStopped: false,
                ManifestPortReleased: false,
                ProcessAbsent: false,
                PortBindable: false);
        }
    }

    private static ArtifactSecurityAudit InspectArtifactSecurity(string? runRoot)
    {
        var failures = new List<string>();
        var scannedJsonFiles = 0;
        var scannedTextFiles = 0;
        var skippedLogFiles = 0;
        if (runRoot is null || !Directory.Exists(runRoot))
        {
            failures.Add("Child artifact root is missing.");
            return new ArtifactSecurityAudit(
                Passed: false,
                scannedJsonFiles,
                scannedTextFiles,
                skippedLogFiles,
                failures);
        }

        foreach (var path in Directory.EnumerateFiles(
                     runRoot,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderBy(path => path, PathComparer))
        {
            var fileName = Path.GetFileName(path);
            if (IsForbiddenLeftoverFile(fileName))
                failures.Add($"{fileName}: forbidden secret/stop artifact.");

            if (string.Equals(
                    Path.GetExtension(path),
                    ".json",
                    StringComparison.OrdinalIgnoreCase))
            {
                scannedJsonFiles++;
                try
                {
                    var text = File.ReadAllText(path);
                    using var document = JsonDocument.Parse(text);
                    InspectJsonValue(
                        document.RootElement,
                        Path.GetRelativePath(runRoot, path),
                        "$",
                        failures);
                }
                catch (Exception error) when (
                    error is JsonException or IOException)
                {
                    failures.Add(
                        $"{Path.GetRelativePath(runRoot, path)}: invalid/unreadable JSON ({error.GetType().Name}).");
                }
                continue;
            }

            if (string.Equals(
                    Path.GetExtension(path),
                    ".jsonl",
                    StringComparison.OrdinalIgnoreCase))
            {
                scannedJsonFiles++;
                try
                {
                    var text = File.ReadAllText(path);
                    if (ContainsRawBearerOrJwt(text))
                    {
                        failures.Add(
                            $"{Path.GetRelativePath(runRoot, path)}: raw bearer/JWT-like token.");
                    }
                    InspectJsonLines(
                        text,
                        Path.GetRelativePath(runRoot, path),
                        failures);
                }
                catch (IOException error)
                {
                    failures.Add(
                        $"{Path.GetRelativePath(runRoot, path)}: unreadable JSONL ({error.GetType().Name}).");
                }
                continue;
            }

            if (!IsExcludedLog(path) && !IsTextArtifact(path))
                continue;

            scannedTextFiles++;
            try
            {
                var text = File.ReadAllText(path);
                if (ContainsRawBearerOrJwt(text))
                {
                    failures.Add(
                        $"{Path.GetRelativePath(runRoot, path)}: raw bearer/JWT-like token.");
                }
            }
            catch (IOException error)
            {
                failures.Add(
                    $"{Path.GetRelativePath(runRoot, path)}: unreadable text ({error.GetType().Name}).");
            }
        }

        return new ArtifactSecurityAudit(
            failures.Count == 0,
            scannedJsonFiles,
            scannedTextFiles,
            skippedLogFiles,
            failures.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void InspectJsonLines(
        string text,
        string relativePath,
        ICollection<string> failures)
    {
        using var reader = new StringReader(text);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                InspectJsonValue(
                    document.RootElement,
                    relativePath,
                    $"$line[{lineNumber}]",
                    failures);
            }
            catch (JsonException error)
            {
                failures.Add(
                    $"{relativePath}: invalid JSONL at line {lineNumber} ({error.GetType().Name}).");
            }
        }
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
                    InspectJsonValue(
                        property.Value,
                        relativePath,
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
                        relativePath,
                        $"{jsonPath}[{index}]",
                        failures);
                    index++;
                }
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                if (ContainsRawBearerOrJwt(value))
                {
                    failures.Add(
                        $"{relativePath}:{jsonPath}: raw bearer/JWT-like token.");
                }
                InspectNestedJsonString(
                    value,
                    relativePath,
                    jsonPath,
                    failures);
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
            using var document = JsonDocument.Parse(trimmed);
            InspectJsonValue(
                document.RootElement,
                relativePath,
                $"{jsonPath}<json>",
                failures);
        }
        catch (JsonException)
        {
            // The string only resembles JSON; it was still token-scanned.
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

    private static async Task WriteIterationEvidenceAsync(
        HarnessPaths paths,
        string aggregateIterationRoot,
        string runKey,
        P6ChaosIterationEvidence iteration,
        CancellationToken ct)
    {
        await EvidenceJson.WriteAsync(
            Path.Combine(aggregateIterationRoot, "semantic-cases.json"),
            new
            {
                schemaVersion = 1,
                gate = GateId,
                runKey,
                iteration = iteration.Iteration,
                cleanCaseCount = iteration.CleanCases.Count,
                finalCaseCount = iteration.FinalCases.Count,
                dat = iteration.FinalCases.Count(
                    result => result.Verdict == HarnessVerdict.DAT),
                khongDat = iteration.FinalCases.Count(
                    result => result.Verdict == HarnessVerdict.KHONG_DAT),
                iteration.CleanNormalizedSha256,
                iteration.NormalizedSha256,
                iteration.BaseCasesPassed,
                cases = iteration.FinalCases
            },
            ct);
        await EvidenceJson.WriteAsync(
            Path.Combine(aggregateIterationRoot, "artifact-index.json"),
            new
            {
                schemaVersion = 1,
                gate = GateId,
                runKey,
                iteration = iteration.Iteration,
                children = iteration.Children.Select(
                    child => new
                    {
                        child.DescriptorId,
                        child.ArchetypeId,
                        child.ChildRunKey,
                        child.ExitCode,
                        runRoot = RelativePath(paths, child.RunRoot),
                        iterationRoot = RelativePath(
                            paths,
                            child.IterationRoot),
                        child.FreshRoot,
                        child.Requirements,
                        child.RequirementsExact,
                        child.ElapsedMs,
                        child.Artifacts,
                        cleanupPassed = child.Cleanup.Passed,
                        artifactSecurityPassed = child.Security.Passed,
                        child.OrchestrationFailure
                    })
            },
            ct);
    }

    private static object BuildIterationProjection(
        HarnessPaths paths,
        P6ChaosIterationEvidence iteration)
        => new
        {
            iteration.Iteration,
            cleanCaseCount = iteration.CleanCases.Count,
            finalCaseCount = iteration.FinalCases.Count,
            dat = iteration.FinalCases.Count(
                result => result.Verdict == HarnessVerdict.DAT),
            khongDat = iteration.FinalCases.Count(
                result => result.Verdict == HarnessVerdict.KHONG_DAT),
            iteration.CleanNormalizedSha256,
            iteration.NormalizedSha256,
            iteration.BaseCasesPassed,
            iteration.CleanupPassed,
            iteration.ArtifactSecurityPassed,
            iteration.ChildRunIdentitiesDistinct,
            children = iteration.Children.Select(
                child => new
                {
                    child.DescriptorId,
                    child.ArchetypeId,
                    child.ChildRunKey,
                    child.ExitCode,
                    runRoot = RelativePath(paths, child.RunRoot),
                    child.FreshRoot,
                    child.RequirementsExact,
                    cleanupPassed = child.Cleanup.Passed,
                    artifactSecurityPassed = child.Security.Passed,
                    child.ElapsedMs,
                    child.OrchestrationFailure
                })
        };

    private static IReadOnlyList<ArtifactHash> BuildArtifactHashes(
        HarnessPaths paths,
        string? resultPath,
        string? apiPath,
        string? directMongoPath,
        string? iterationRoot)
    {
        var files = new List<string?>([resultPath, apiPath, directMongoPath]);
        if (iterationRoot is not null && Directory.Exists(iterationRoot))
        {
            files.AddRange(
                Directory.EnumerateFiles(
                    iterationRoot,
                    "*cleanup*.json",
                    SearchOption.AllDirectories));
        }

        return files
            .Where(path => path is not null && File.Exists(path))
            .Select(path => path!)
            .Distinct(PathComparer)
            .OrderBy(path => path, PathComparer)
            .Select(
                path => new ArtifactHash(
                    RelativePath(paths, path) ?? path,
                    HashFile(path)))
            .ToArray();
    }

    private static JsonLoadResult LoadJson(
        string? path,
        JsonValueKind expectedKind)
    {
        if (path is null || !File.Exists(path))
            return new JsonLoadResult(null, "file missing");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != expectedKind)
            {
                return new JsonLoadResult(
                    null,
                    $"expected {expectedKind}, got {document.RootElement.ValueKind}");
            }
            return new JsonLoadResult(document.RootElement.Clone(), null);
        }
        catch (Exception error) when (error is JsonException or IOException)
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

    private static void PrintCase(HarnessCaseResult result)
        => Console.WriteLine(
            $"[{result.Verdict}] {result.CaseId}: {result.Detail}");

    private static string BuildNormalizedSha256(
        IReadOnlyList<HarnessCaseResult> cases)
    {
        var normalized = cases
            .OrderBy(result => result.CaseId, StringComparer.Ordinal)
            .Select(
                result => new
                {
                    result.CaseId,
                    result.Verdict,
                    result.Fingerprint
                })
            .ToArray();
        var json = JsonSerializer.Serialize(normalized, EvidenceJson.Options);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private static int ParseIterations(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            string? raw = null;
            var argument = args[index];
            if (argument.StartsWith(
                    "--iterations=",
                    StringComparison.OrdinalIgnoreCase))
            {
                raw = argument["--iterations=".Length..];
            }
            else if (string.Equals(
                         argument,
                         "--iterations",
                         StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException(
                        "--iterations requires an integer from 1 through 5.");
                }
                raw = args[index + 1];
            }

            if (raw is null)
                continue;
            if (!int.TryParse(raw, out var parsed) || parsed is < 1 or > 5)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(args),
                    "--iterations must be an integer from 1 through 5.");
            }
            return parsed;
        }

        return 2;
    }

    private static string BuildRunKey()
    {
        var random = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(4))
            .ToLowerInvariant();
        return $"p611_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{random}";
    }

    private static IEnumerable<string> EnumerateChildRoots(
        string integrationRoot,
        string prefix)
    {
        if (!Directory.Exists(integrationRoot))
            return [];
        return Directory.EnumerateDirectories(
                integrationRoot,
                $"{prefix}*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, PathComparer)
            .ToArray();
    }

    private static string BuildFailureReason(
        bool deliberateFailure,
        bool deliberateAssertionObserved,
        IReadOnlyCollection<string> orchestrationErrors,
        bool requiredIterationCountSatisfied,
        bool allDescriptorsPresent,
        bool allBaseCasesPassed,
        bool allCleanupPassed,
        bool allArtifactSecurityPassed,
        bool allChildExitsClean,
        bool allChildArtifactsFresh,
        bool allRequirementsExact,
        bool childRunIdentitiesDistinct,
        bool cleanNormalizedShaMatch)
    {
        if (deliberateFailure)
        {
            if (orchestrationErrors.Count > 0 ||
                !allDescriptorsPresent ||
                !allBaseCasesPassed ||
                !allCleanupPassed ||
                !allArtifactSecurityPassed ||
                !allChildExitsClean ||
                !allChildArtifactsFresh ||
                !allRequirementsExact ||
                !childRunIdentitiesDistinct ||
                !cleanNormalizedShaMatch)
            {
                return "A non-deliberate P6-11 semantic, cleanup, security, identity, or reproducibility check failed during the deliberate control.";
            }
            return deliberateAssertionObserved
                ? "Expected deliberate oracle failure; root passed=false by design."
                : "Deliberate failure mode did not emit exactly one expected oracle failure.";
        }

        var failures = new List<string>();
        if (orchestrationErrors.Count > 0)
            failures.Add("child orchestration error");
        if (!requiredIterationCountSatisfied)
            failures.Add("fewer than two clean iterations");
        if (!allDescriptorsPresent)
            failures.Add("incomplete P601..P610 topology plus P611CHAOS set");
        if (!allBaseCasesPassed)
            failures.Add("one or more semantic cases failed");
        if (!allCleanupPassed)
            failures.Add("cleanup failed");
        if (!allArtifactSecurityPassed)
            failures.Add("artifact security failed");
        if (!allChildExitsClean)
            failures.Add("child exit was non-zero");
        if (!allChildArtifactsFresh)
            failures.Add("child artifact root was not uniquely fresh");
        if (!allRequirementsExact)
            failures.Add("child requirement set mismatch");
        if (!childRunIdentitiesDistinct)
            failures.Add("child run identities were not distinct");
        if (!cleanNormalizedShaMatch)
            failures.Add("normalized semantic SHA mismatch");
        return failures.Count == 0
            ? "All P6-11 aggregate checks passed."
            : string.Join("; ", failures);
    }

    private static bool CasePassed(
        ChildProbeEvidence child,
        string suffix)
        => child.Cases.Any(
            result =>
                result.CaseId.EndsWith(
                    $"-{suffix}",
                    StringComparison.Ordinal) &&
                result.Verdict == HarnessVerdict.DAT);

    private static JsonElement RequireLoaded(
        JsonLoadResult load,
        string subject)
    {
        if (load.Root is null)
            throw new InvalidDataException($"{subject} JSON unavailable: {load.Error}");
        return load.Root.Value;
    }

    private static JsonElement RequireEmbeddedJsonObject(
        JsonElement root,
        string path)
    {
        var json = RequireString(root, path);
        try
        {
            using var document = JsonDocument.Parse(json);
            Require(
                document.RootElement.ValueKind == JsonValueKind.Object,
                $"Embedded JSON '{path}' must be an object.");
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                $"Embedded JSON '{path}' was invalid.",
                error);
        }
    }

    private static JsonElement RequirePath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            current = RequireProperty(current, segment);
        return current;
    }

    private static JsonElement RequireProperty(
        JsonElement root,
        string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Cannot read '{propertyName}' from {root.ValueKind}.");
        }
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
        throw new InvalidDataException($"Missing JSON property '{propertyName}'.");
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

    private static string RequireString(JsonElement root, string path)
    {
        var value = RequirePath(root, path);
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"JSON '{path}' must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        var value = GetProperty(root, propertyName);
        return value is { ValueKind: JsonValueKind.String }
            ? value.Value.GetString()
            : null;
    }

    private static int RequireInt32(JsonElement root, string path)
    {
        var value = RequirePath(root, path);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var parsed))
        {
            throw new InvalidDataException(
                $"JSON '{path}' must be an Int32.");
        }
        return parsed;
    }

    private static int? GetInt32(JsonElement root, string propertyName)
    {
        var value = GetProperty(root, propertyName);
        return value is { ValueKind: JsonValueKind.Number } &&
               value.Value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static bool RequireBoolean(JsonElement root, string path)
    {
        var value = RequirePath(root, path);
        if (value.ValueKind is not (
            JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"JSON '{path}' must be boolean.");
        }
        return value.GetBoolean();
    }

    private static bool? GetBoolean(JsonElement root, string propertyName)
    {
        var value = GetProperty(root, propertyName);
        return value is { ValueKind: JsonValueKind.True or JsonValueKind.False }
            ? value.Value.GetBoolean()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement root,
        string propertyName)
    {
        var value = GetProperty(root, propertyName);
        if (value is null || value.Value.ValueKind != JsonValueKind.Array)
            return [];
        return value.Value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static IReadOnlyList<string> RequireStringArray(
        JsonElement root,
        string path)
    {
        var value = RequirePath(root, path);
        Require(
            value.ValueKind == JsonValueKind.Array,
            $"JSON '{path}' must be an array.");
        var rows = value.EnumerateArray().ToArray();
        Require(
            rows.All(
                item =>
                    item.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(item.GetString())),
            $"JSON '{path}' must contain only non-empty strings.");
        return rows
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static IReadOnlyList<int> RequireIntArray(
        JsonElement root,
        string path)
    {
        var value = RequirePath(root, path);
        Require(
            value.ValueKind == JsonValueKind.Array,
            $"JSON '{path}' must be an array.");
        var rows = value.EnumerateArray().ToArray();
        Require(
            rows.All(item =>
                item.ValueKind == JsonValueKind.Number &&
                item.TryGetInt32(out _)),
            $"JSON '{path}' must contain only Int32 values.");
        return rows
            .Select(item => item.GetInt32())
            .ToArray();
    }

    private static void RequireInt(
        JsonElement root,
        string path,
        int expected)
    {
        var value = RequirePath(root, path);
        if (value.ValueKind == JsonValueKind.Array)
        {
            RequireEqual(
                expected,
                value.GetArrayLength(),
                $"array count '{path}'");
            return;
        }
        RequireEqual(
            expected,
            RequireInt32(root, path),
            $"integer '{path}'");
    }

    private static void RequireTrue(JsonElement root, string path)
        => Require(
            RequireBoolean(root, path),
            $"JSON '{path}' must be true.");

    private static void RequireFalse(JsonElement root, string path)
        => Require(
            !RequireBoolean(root, path),
            $"JSON '{path}' must be false.");

    private static void RequireNonEmpty(JsonElement root, string path)
        => _ = RequireString(root, path);

    private static void RequireSha256(JsonElement root, string path)
    {
        var value = RequireString(root, path);
        RequireSha256Value(value, $"JSON '{path}'");
    }

    private static void RequireSha256Value(
        string value,
        string subject)
    {
        Require(
            Regex.IsMatch(
                value,
                "^[a-fA-F0-9]{64}$",
                RegexOptions.CultureInvariant),
            $"{subject} was not SHA-256.");
    }

    private static void RequireBarrier(JsonElement root, int expectedCount)
    {
        RequireInt(root, "barrierEvidence.count", expectedCount);
        var rows = RequirePath(root, "barrierEvidence.rows");
        Require(
            rows.ValueKind == JsonValueKind.Array &&
            rows.GetArrayLength() == expectedCount,
            $"Barrier row count was not {expectedCount}.");
        foreach (var row in rows.EnumerateArray())
        {
            var direct = GetProperty(row, "directMongoZeroWrite");
            var simple = GetProperty(row, "zeroWrite");
            var value = direct ?? simple;
            Require(
                value is { ValueKind: JsonValueKind.True },
                "Barrier row did not prove direct-Mongo zero-write.");
        }
    }

    private static void RequireOneOf(string value, params string[] allowed)
        => Require(
            allowed.Contains(value, StringComparer.OrdinalIgnoreCase),
            $"Value '{value}' was not one of [{string.Join(",", allowed)}].");

    private static void RequireDifferent(
        string left,
        string right,
        string subject)
        => Require(
            !string.Equals(left, right, StringComparison.Ordinal),
            $"{subject} values unexpectedly matched.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    private static void RequireEqual<T>(
        T expected,
        T actual,
        string subject)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidDataException(
                $"{subject} mismatch. Expected={expected}; Actual={actual}.");
        }
    }

    private static void RequireDirectory(string? path, string subject)
        => Require(
            path is not null && Directory.Exists(path),
            $"{subject} is missing.");

    private static bool SetsEqual(
        IEnumerable<string> expected,
        IEnumerable<string> actual)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualRows = actual.ToArray();
        var actualSet = actualRows.ToHashSet(StringComparer.Ordinal);
        return expectedSet.SetEquals(actualSet) &&
               actualRows.Length == actualSet.Count;
    }

    private static bool ContainsString(JsonElement root, string value)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.String:
                return (root.GetString() ?? string.Empty).Contains(
                    value,
                    StringComparison.Ordinal);
            case JsonValueKind.Object:
                return root.EnumerateObject().Any(
                    property => ContainsString(property.Value, value));
            case JsonValueKind.Array:
                return root.EnumerateArray().Any(
                    item => ContainsString(item, value));
            default:
                return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var normalizedLeft = Path.GetFullPath(left)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var normalizedRight = Path.GetFullPath(right)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        return PathComparer.Equals(normalizedLeft, normalizedRight);
    }

    private static bool IsProcessRunning(int processId)
    {
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
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                false);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static bool ContainsRawBearerOrJwt(string value)
        => BearerSecretPattern.IsMatch(value) || JwtPattern.IsMatch(value);

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

    private static bool IsExcludedLog(string path)
    {
        if (string.Equals(
                Path.GetExtension(path),
                ".log",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var fileName = Path.GetFileName(path);
        return fileName.Contains(
                   "stdout",
                   StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains(
                   "stderr",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextArtifact(string path)
        => Path.GetExtension(path).ToLowerInvariant()
            is ".txt" or ".csv" or ".md";

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

    private sealed record ProbeDescriptor(
        string Id,
        string ArchetypeId,
        string RunPrefix,
        string ResultFileName,
        string ApiFileName,
        string DirectMongoFileName,
        IReadOnlyList<string> ExpectedRequirements,
        int MinimumApiExchangeCount,
        IReadOnlyList<int> RequiredApiStatusCodes,
        bool ResultCarriesCandidate,
        Func<Task<int>> Runner);

    private sealed record JsonLoadResult(JsonElement? Root, string? Error);

    private sealed record ArtifactHash(string Path, string Sha256);

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
        bool DatabaseDropImplied,
        ProcessCleanupAudit Mongo,
        IReadOnlyList<ProcessCleanupAudit> Backends,
        bool MongoDataAbsent,
        IReadOnlyList<string> Failures);

    private sealed record ArtifactSecurityAudit(
        bool Passed,
        int ScannedJsonFiles,
        int ScannedTextFiles,
        int SkippedLogFiles,
        IReadOnlyList<string> Failures);

    private sealed record ChildProbeEvidence(
        string DescriptorId,
        string ArchetypeId,
        int ExitCode,
        string ChildRunKey,
        string? RunRoot,
        string? IterationRoot,
        bool FreshRoot,
        IReadOnlyList<string> Requirements,
        bool RequirementsExact,
        IReadOnlyList<HarnessCaseResult> Cases,
        CleanupAudit Cleanup,
        ArtifactSecurityAudit Security,
        IReadOnlyList<ArtifactHash> Artifacts,
        long ElapsedMs,
        string? OrchestrationFailure);

    private sealed record P6ChaosIterationEvidence(
        int Iteration,
        IReadOnlyList<HarnessCaseResult> CleanCases,
        IReadOnlyList<HarnessCaseResult> FinalCases,
        string CleanNormalizedSha256,
        string NormalizedSha256,
        bool BaseCasesPassed,
        bool CleanupPassed,
        bool ArtifactSecurityPassed,
        bool ChildRunIdentitiesDistinct,
        IReadOnlyList<ChildProbeEvidence> Children);
}


