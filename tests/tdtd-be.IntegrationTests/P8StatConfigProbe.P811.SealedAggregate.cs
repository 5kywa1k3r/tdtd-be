using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string P811SealedProjectionVersion =
        "P8-11-STABLE-SEMANTIC-PROJECTION-1";

    private static readonly string[] P811SealedArtifactNames =
    [
        "p8-11-gate-result.json",
        "cleanup-manifest.json",
        "environment.json",
        "security-scan.json",
        "p8-11-post-final-security-scan.json",
        "p8-race-source-fingerprint.json",
        "p8-race-p7-regression.json",
        "p8-race-backend-restart.json",
        "p8-ops-014-index-conflict.json",
        "api-exchanges.json",
        "collection-deltas.json",
        "owner-oracles.json",
        "P8-RACE.evidence.json",
        "p8-race-queue-trace.json"
    ];

    private static readonly string[] P811SealedOracleNames =
    [
        "API_KESTREL",
        "DIRECT_MONGO",
        "COLLECTION_DELTA",
        "RECEIPT",
        "QUEUE_TRACE",
        "SOURCE_FINGERPRINT",
        "SECURITY_SCAN",
        "P7_REGRESSION"
    ];

    private static readonly IReadOnlyDictionary<string, string>
        P811SealedP812DocumentationPins =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["docs/AUTOMATION_TEST_PLAN.md"] =
                    "0be84df2fc7c370a6c66a25cf341eb686881b51ce58da827f1f68fc2d33253d0",
                ["docs/WORK_DONE.md"] =
                    "6f9bfbc4afc170a08685b7634f6140ead561a1196d798aad2c5c42012a406a69",
                ["docs/WORK_PLANNED.md"] =
                    "b3db54f1d74469d6c6e9de56ff5ddb04724a8676c46185c3eec2ee7cd0a3b889",
                ["docs/features/DYNAMIC_FORM_FLOW_TRACE_MATRIX_V1.md"] =
                    "40d5acb8576382fd9935f6dc17b5f297aa48dd56ef325e449a79d33a9f477d3b",
                ["docs/features/DYNAMIC_FORM_FLOW_UI_ROUTE_OWNERSHIP_V1.md"] =
                    "bdebbf17587c972ff315905deb2949c211e49d0e20e4e36b04d58575a37e4f03"
            };

    private static readonly HashSet<string> P811ReceiptOracleCaseIds =
        new(
        [
            "P8-RACE-001", "P8-RACE-002", "P8-RACE-003",
            "P8-RACE-004", "P8-RACE-005", "P8-RACE-006",
            "P8-RACE-010", "P8-RACE-014"
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> P811QueueOracleCaseIds =
        new(
        [
            "P8-RACE-010", "P8-RACE-011", "P8-RACE-012",
            "P8-RACE-013", "P8-RACE-014"
        ],
        StringComparer.Ordinal);

    private static async Task<int> RunP811SealedAggregateAsync(
        string[] args,
        CancellationToken ct)
    {
        var requestedIterations = ParseP811Iterations(args);

        var deliberateFailure = HasArgument(
            args,
            P811DeliberateFailureSwitch);
        var startedAtUtc = DateTime.UtcNow;
        var aggregateRunKey =
            $"p811_sealed_{startedAtUtc:yyyyMMddHHmmss}_{Environment.ProcessId}_" +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(4))
                .ToLowerInvariant();
        var paths = HarnessPaths.CreateP8(
            aggregateRunKey,
            ChainId,
            "P8-11");
        var childArgs = BuildP811ChildArguments(args);
        var children = new List<P811SealedChildEvidence>(
            requestedIterations);

        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "cleanup-manifest.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                state = "ALLOCATING",
                requiredIterations = 2,
                ownedRoots = new[]
                {
                    P811SealedRelative(paths.WorkspaceRoot, paths.RunRoot)
                },
                startedAtUtc
            },
            ct);

        Console.WriteLine(
            $"P8-11 sealed aggregate artifacts: {paths.RunRoot}");
        for (var iteration = 1;
             iteration <= requestedIterations;
             iteration++)
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine(
                $"P8-11 sealed child {iteration}/{requestedIterations} starting.");
            var child = new P8StatConfigProbe(11);
            var exitCode = await child.ExecuteAsync(childArgs, ct);

            // ExecuteAsync writes its gate and cleanup last. This scan therefore
            // covers the complete child artifact set rather than the pre-final
            // subset covered by the child's ordinary security-scan.json.
            var postFinalSecurity =
                await child.RunArtifactSecurityScanAsync();
            await EvidenceJson.WriteAsync(
                Path.Combine(
                    child._paths.RunRoot,
                    "p8-11-post-final-security-scan.json"),
                new
                {
                    schemaVersion = 1,
                    chainId = ChainId,
                    promptId = "P8-11",
                    runKey = Path.GetFileName(child._paths.RunRoot),
                    scannedAfterChildGateAndCleanup = true,
                    postFinalSecurityScan = postFinalSecurity
                },
                ct);

            if (exitCode != 0)
            {
                await WriteP811SealedFailureEnvelopeAsync(
                    paths,
                    aggregateRunKey,
                    startedAtUtc,
                    iteration,
                    "CHILD_EXIT",
                    exitCode,
                    child._paths.RunRoot,
                    postFinalSecurity,
                    children,
                    exception: null);
                Console.WriteLine(
                    $"[KHONG_DAT] P8-11 sealed child {iteration} returned " +
                    $"exit={exitCode}; failure envelope={paths.RunRoot}");
                return 1;
            }

            P811SealedChildEvidence evidence;
            try
            {
                evidence = await LoadP811SealedChildEvidenceAsync(
                    iteration,
                    exitCode,
                    paths.WorkspaceRoot,
                    child._paths.RunRoot,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteP811SealedFailureEnvelopeAsync(
                    paths,
                    aggregateRunKey,
                    startedAtUtc,
                    iteration,
                    "CHILD_BINDING",
                    exitCode,
                    child._paths.RunRoot,
                    postFinalSecurity,
                    children,
                    ex);
                Console.WriteLine(
                    $"[KHONG_DAT] P8-11 child binding failed with " +
                    $"{ex.GetType().Name}; failure envelope={paths.RunRoot}");
                return 1;
            }
            children.Add(evidence);
            Console.WriteLine(
                $"P8-11 sealed child {iteration}: exit={exitCode}; " +
                $"cases={evidence.ActualCaseCount}; " +
                $"stableSemantic={evidence.StableSemanticSha256}; " +
                $"artifacts={evidence.ArtifactBindings.Count}.");
        }

        var exactIterationCount = requestedIterations == 2 &&
                                  children.Count == 2;
        var identitiesDistinct = exactIterationCount &&
                                 children.Select(item => item.ChildRunKey)
                                     .Distinct(StringComparer.Ordinal).Count() == 2 &&
                                 children.Select(item => item.RunRoot)
                                     .Distinct(StringComparer.Ordinal).Count() == 2 &&
                                 children.Select(item => item.DatabaseName)
                                     .Distinct(StringComparer.Ordinal).Count() == 2 &&
                                 children.Select(item => item.ReplicaSetName)
                                     .Distinct(StringComparer.Ordinal).Count() == 2 &&
                                 children.Select(item => item.BackendProcessId)
                                     .Distinct().Count() == 2 &&
                                 children.Select(item => item.MongoProcessId)
                                     .Distinct().Count() == 2;
        var childOracleEvaluations = children
            .Select(item => EvaluateP811SealedOracleBundle(item))
            .ToArray();
        var allEightOraclesPassed = exactIterationCount &&
                                    childOracleEvaluations.All(set =>
                                        set.Count == P811SealedOracleNames.Length &&
                                        set.Select(item => item.Oracle)
                                            .SequenceEqual(
                                                P811SealedOracleNames,
                                                StringComparer.Ordinal) &&
                                        set.All(item => item.Passed));
        var allChildrenPassed = exactIterationCount &&
                                children.All(item =>
                                    item.ExitCode == 0 &&
                                    item.GatePassed &&
                                    item.ExactCaseOrder &&
                                    item.ActualCaseCount == AllP8CaseIds.Length &&
                                    item.AllCasesDat &&
                                    item.RawSemanticVerified &&
                                    item.StableCases.All(row =>
                                        row.RealKestrel &&
                                        row.DirectMongo &&
                                        row.EvidenceBound));
        var semanticShaMatch = exactIterationCount &&
                               children.All(item =>
                                   IsLowerSha256(item.StableSemanticSha256)) &&
                               children.Select(item =>
                                       item.StableSemanticSha256)
                                   .Distinct(StringComparer.Ordinal).Count() == 1;
        var raceSemanticShaMatch = exactIterationCount &&
                                   children.All(item =>
                                       IsLowerSha256(
                                           item.RaceStableSemanticSha256)) &&
                                   children.Select(item =>
                                           item.RaceStableSemanticSha256)
                                       .Distinct(StringComparer.Ordinal).Count() == 1;
        var sourceShaMatch = exactIterationCount &&
                             children.All(item =>
                                 IsLowerSha256(
                                     item.SourceFingerprintSemanticSha256)) &&
                             children.Select(item =>
                                     item.SourceFingerprintSemanticSha256)
                                 .Distinct(StringComparer.Ordinal).Count() == 1;
        var allArtifactsBound = exactIterationCount &&
                                children.All(item =>
                                    item.ArtifactBindings.Count ==
                                    P811SealedArtifactNames.Length &&
                                    item.ArtifactBindings.All(binding =>
                                        binding.Bytes > 0 &&
                                        IsLowerSha256(binding.RawSha256) &&
                                        binding.Path.StartsWith(
                                            item.RunRoot.TrimEnd('/') + "/",
                                            StringComparison.Ordinal)));

        var normalGateSatisfied = exactIterationCount &&
                                  identitiesDistinct &&
                                  allChildrenPassed &&
                                  allEightOraclesPassed &&
                                  semanticShaMatch &&
                                  raceSemanticShaMatch &&
                                  sourceShaMatch &&
                                  allArtifactsBound;
        var deliberateControl = EvaluateP811SealedDeliberateControl(
            children[0]);
        var deliberateControlPassed = normalGateSatisfied &&
                                      deliberateControl.Passed;
        var contractSatisfied = normalGateSatisfied &&
                                deliberateControlPassed;
        var completedAtUtc = DateTime.UtcNow;

        var raceCases = children[0].StableCases
            .Where(row => row.CaseId.StartsWith(
                "P8-RACE-", StringComparison.Ordinal))
            .ToArray();
        var aggregateRaceEvidence = new
        {
            schemaVersion = 1,
            chainId = ChainId,
            promptId = "P8-11",
            group = "P8-RACE",
            expectedCaseCount = RaceCaseIds.Length,
            actualCaseCount = raceCases.Length,
            exactIds = raceCases.Select(row => row.CaseId)
                .SequenceEqual(RaceCaseIds, StringComparer.Ordinal),
            allDat = raceCases.All(row =>
                string.Equals(row.Verdict, "DAT", StringComparison.Ordinal)),
            semanticProjectionVersion = P811SealedProjectionVersion,
            semanticProjectionFields = new[]
            {
                "caseId", "verdict", "realKestrel", "directMongo",
                "evidenceBound"
            },
            excludedDeclaredVolatileFields = new[] { "fingerprint" },
            normalizedSemanticSha256 =
                children[0].RaceStableSemanticSha256,
            semanticHashVerified = raceSemanticShaMatch,
            cases = raceCases,
            childEvidence = children.Select(item => new
            {
                item.Iteration,
                item.ChildRunKey,
                item.RaceStableSemanticSha256,
                artifact = item.ArtifactBindings.Single(binding =>
                    string.Equals(
                        binding.Name,
                        "P8-RACE.evidence.json",
                        StringComparison.Ordinal))
            }),
            requiredOracles = P811SealedOracleNames,
            passed = contractSatisfied
        };
        var raceBinding = await WriteP811SealedArtifactAsync(
            paths,
            "P8-RACE.evidence.json",
            aggregateRaceEvidence,
            ct);

        var twoCleanBinding = await WriteP811SealedArtifactAsync(
            paths,
            "p8-11-two-clean-run-manifest.json",
            new
            {
                schemaVersion = 1,
                gate = "P8-11-TWO-CLEAN-FULL-200-SEALED",
                requirementOwned = "P8-STAT-024",
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                startedAtUtc,
                completedAtUtc,
                requiredIterations = 2,
                expectedRegistryCaseCount = AllP8CaseIds.Length,
                expectedCaseIds = AllP8CaseIds,
                semanticProjectionVersion = P811SealedProjectionVersion,
                semanticProjectionFields = new[]
                {
                    "caseId", "verdict", "realKestrel", "directMongo",
                    "evidenceBound"
                },
                excludedDeclaredVolatileFields = new[] { "fingerprint" },
                browserProofDeferredTo = "P8-12",
                browserEvidenceClaimed = false,
                children,
                childOracleEvaluations,
                checks = new
                {
                    exactIterationCount,
                    identitiesDistinct,
                    allChildrenPassed,
                    allEightOraclesPassed,
                    semanticShaMatch,
                    raceSemanticShaMatch,
                    sourceShaMatch,
                    allArtifactsBound
                },
                normalizedSemanticSha256 =
                    children[0].StableSemanticSha256,
                raceNormalizedSemanticSha256 =
                    children[0].RaceStableSemanticSha256,
                sourceFingerprintSemanticSha256 =
                    children[0].SourceFingerprintSemanticSha256,
                normalGateSatisfied
            },
            ct);

        var deliberateBinding = await WriteP811SealedArtifactAsync(
            paths,
            "deliberate-control.json",
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                control = deliberateControl,
                exactlyOneNamedOracleFailed =
                    deliberateControl.FailedOracles.SequenceEqual(
                        ["SOURCE_FINGERPRINT"],
                        StringComparer.Ordinal),
                realChildrenRemainGreen = normalGateSatisfied,
                passed = deliberateControlPassed
            },
            ct);

        var cleanupPassed = children.All(item =>
            EvaluateP811SealedCleanup(item).Passed);
        var cleanupBinding = await WriteP811SealedArtifactAsync(
            paths,
            "cleanup-manifest.json",
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                state = cleanupPassed ? "CLEANED" : "CLEANUP_FAILED",
                ownedRoots = new[]
                    {
                        P811SealedRelative(paths.WorkspaceRoot, paths.RunRoot)
                    }
                    .Concat(children.Select(item => item.RunRoot))
                    .ToArray(),
                children = children.Select(item => new
                {
                    item.Iteration,
                    item.ChildRunKey,
                    item.RunRoot,
                    item.DatabaseName,
                    item.ReplicaSetName,
                    item.BackendProcessId,
                    item.MongoProcessId,
                    cleanup = EvaluateP811SealedCleanup(item),
                    cleanupArtifact = item.ArtifactBindings.Single(binding =>
                        string.Equals(
                            binding.Name,
                            "cleanup-manifest.json",
                            StringComparison.Ordinal)),
                    restartArtifact = item.ArtifactBindings.Single(binding =>
                        string.Equals(
                            binding.Name,
                            "p8-race-backend-restart.json",
                            StringComparison.Ordinal))
                }),
                cleanupSucceeded = cleanupPassed,
                completedAtUtc
            },
            ct);

        var aggregateSecurity = await
            RunP811SealedAggregateSecurityScanAsync(paths.RunRoot, ct);
        var securityBinding = await WriteP811SealedArtifactAsync(
            paths,
            "aggregate-security-scan.json",
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                scannedAfterTwoCleanControlAndCleanup = true,
                aggregateSecurity
            },
            ct);

        var aggregateArtifacts = new[]
        {
            raceBinding,
            twoCleanBinding,
            deliberateBinding,
            cleanupBinding,
            securityBinding
        };
        var aggregatePassed = contractSatisfied &&
                              cleanupPassed &&
                              aggregateSecurity.Passed;
        var passed = !deliberateFailure && aggregatePassed;
        var failureReason = P811SealedFailureReason(
            deliberateFailure,
            exactIterationCount,
            identitiesDistinct,
            allChildrenPassed,
            allEightOraclesPassed,
            semanticShaMatch,
            raceSemanticShaMatch,
            sourceShaMatch,
            allArtifactsBound,
            deliberateControlPassed,
            cleanupPassed,
            aggregateSecurity.Passed);

        await EvidenceJson.WriteAsync(
            Path.Combine(paths.RunRoot, "p8-11-gate-result.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = "P8-11",
                aggregateRunKey,
                exactExpectedCaseCountPerIteration = AllP8CaseIds.Length,
                completeCaseExecutions = children.Sum(item =>
                    item.ActualCaseCount),
                semanticProjectionVersion = P811SealedProjectionVersion,
                semanticProjectionFields = new[]
                {
                    "caseId", "verdict", "realKestrel", "directMongo",
                    "evidenceBound"
                },
                excludedDeclaredVolatileFields = new[] { "fingerprint" },
                normalizedSemanticSha256 =
                    children[0].StableSemanticSha256,
                raceNormalizedSemanticSha256 =
                    children[0].RaceStableSemanticSha256,
                semanticShaMatch,
                raceSemanticShaMatch,
                sourceShaMatch,
                identitiesDistinct,
                childOracleEvaluations,
                deliberateControl,
                deliberateControlPassed,
                cleanupPassed,
                aggregateSecurityPassed = aggregateSecurity.Passed,
                deliberateFailureMode = deliberateFailure,
                expectedRed = deliberateFailure,
                contractSatisfied = aggregatePassed,
                artifacts = aggregateArtifacts,
                handoff = new
                {
                    caseGroup = "P8-RACE",
                    caseArtifact = raceBinding,
                    caseNormalizedSemanticSha256 =
                        children[0].RaceStableSemanticSha256,
                    cleanupArtifact = cleanupBinding,
                    verificationPassed = AllP8CaseIds.Length * 2,
                    verificationFailed = 0
                },
                passed,
                failureReason
            },
            ct);

        Console.WriteLine(passed
            ? $"[DAT] P8-11 sealed aggregate passed 400/400 with one observed SOURCE_FINGERPRINT control failure; artifacts={paths.RunRoot}"
            : $"[KHONG_DAT] P8-11 sealed aggregate: {failureReason}; artifacts={paths.RunRoot}");
        return passed ? 0 : 1;
    }

    private static async Task<P811SealedChildEvidence>
        LoadP811SealedChildEvidenceAsync(
            int iteration,
            int exitCode,
            string workspaceRoot,
            string runRoot,
            CancellationToken ct)
    {
        var fullRunRoot = Path.GetFullPath(runRoot);
        var configuredArtifactRoot = Environment.GetEnvironmentVariable(
            "TDTD_P8_ARTIFACT_ROOT");
        var p8Root = string.IsNullOrWhiteSpace(configuredArtifactRoot)
            ? Path.Combine(workspaceRoot, ".p8-artifacts", "test-runs")
            : Path.GetFullPath(configuredArtifactRoot);
        var expectedRoot = Path.GetFullPath(Path.Combine(
            p8Root,
            ChainId,
            "P8-11"));
        if (!P811SealedIsWithin(expectedRoot, fullRunRoot))
        {
            throw new InvalidOperationException(
                $"P8-11 child root escaped the owned directory: {runRoot}");
        }

        var bound = new Dictionary<string, P811SealedBoundDocument>(
            StringComparer.Ordinal);
        foreach (var name in P811SealedArtifactNames)
        {
            bound.Add(
                name,
                await ReadP811SealedBoundDocumentAsync(
                    workspaceRoot,
                    fullRunRoot,
                    name,
                    ct));
        }

        var gate = bound["p8-11-gate-result.json"].Document;
        var runKey = P811SealedRequiredString(gate, "runKey");
        if (!string.Equals(
                runKey,
                Path.GetFileName(fullRunRoot),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "P8-11 child runKey does not match its exclusive directory.");
        }
        foreach (var item in bound.Values)
        {
            P811SealedRequireIdentity(
                item.Document,
                item.Binding.Name,
                runKey);
        }

        var rawCases = P811SealedRequiredArray(gate, "results")
            .Select(node =>
            {
                var row = node as JsonObject
                          ?? throw new InvalidOperationException(
                              "P8-11 result row is not an object.");
                return new P811SealedRawCase(
                    P811SealedRequiredString(row, "caseId"),
                    P811SealedRequiredString(row, "verdict"),
                    P811SealedRequiredString(row, "fingerprint"));
            })
            .ToArray();
        var caseIds = rawCases.Select(row => row.CaseId).ToArray();
        var ownerIds = P811SealedCaseIds(
                bound["owner-oracles.json"].Document,
                "cases")
            .OrderBy(caseId => caseId, StringComparer.Ordinal)
            .ToArray();
        var deltaIds = P811SealedCaseIds(
                bound["collection-deltas.json"].Document,
                "cases")
            .OrderBy(caseId => caseId, StringComparer.Ordinal)
            .ToArray();
        var raceIds = P811SealedCaseIds(
            bound["P8-RACE.evidence.json"].Document,
            "cases");
        var exactCaseOrder =
            caseIds.SequenceEqual(AllP8CaseIds, StringComparer.Ordinal) &&
            ownerIds.SequenceEqual(AllP8CaseIds, StringComparer.Ordinal) &&
            deltaIds.SequenceEqual(AllP8CaseIds, StringComparer.Ordinal) &&
            raceIds.SequenceEqual(RaceCaseIds, StringComparer.Ordinal) &&
            caseIds.Distinct(StringComparer.Ordinal).Count() ==
            AllP8CaseIds.Length;
        var realKestrel = P811SealedRequiredBool(
            gate,
            "allMutationPathsUseRealKestrel");
        var ownerSet = ownerIds.ToHashSet(StringComparer.Ordinal);
        var deltaSet = deltaIds.ToHashSet(StringComparer.Ordinal);
        var stableCases = rawCases.Select(row =>
                new P811SealedStableCase(
                    row.CaseId,
                    row.Verdict,
                    realKestrel,
                    ownerSet.Contains(row.CaseId),
                    ownerSet.Contains(row.CaseId) &&
                    deltaSet.Contains(row.CaseId)))
            .ToArray();
        var rawSemanticSha256 = P811SealedComputeRawSemanticSha256(
            rawCases);
        var claimedRawSemanticSha256 = P811SealedRequiredString(
            gate,
            "normalizedSemanticSha256");
        var stableSemanticSha256 = P811SealedComputeStableSemanticSha256(
            stableCases);
        var raceStableSemanticSha256 =
            P811SealedComputeStableSemanticSha256(
                stableCases.Where(row => row.CaseId.StartsWith(
                    "P8-RACE-", StringComparison.Ordinal)));

        var environment = bound["environment.json"].Document;
        var kestrel = P811SealedRequiredObject(environment, "kestrel");
        var mongo = P811SealedRequiredObject(environment, "mongo");
        var source = bound["p8-race-source-fingerprint.json"].Document;

        return new P811SealedChildEvidence
        {
            Iteration = iteration,
            ExitCode = exitCode,
            ChildRunKey = runKey,
            RunRoot = P811SealedRelative(workspaceRoot, fullRunRoot),
            ExactExpectedCaseCount = P811SealedRequiredInt(
                gate,
                "exactExpectedCaseCount"),
            ActualCaseCount = P811SealedRequiredInt(gate, "actualCaseCount"),
            GatePassed = P811SealedRequiredBool(gate, "passed"),
            ExactCaseOrder = exactCaseOrder,
            AllCasesDat = rawCases.All(row => string.Equals(
                row.Verdict,
                "DAT",
                StringComparison.Ordinal)),
            ClaimedRawSemanticSha256 = claimedRawSemanticSha256,
            RawSemanticSha256 = rawSemanticSha256,
            RawSemanticVerified = IsLowerSha256(
                                      claimedRawSemanticSha256) &&
                                  string.Equals(
                                      claimedRawSemanticSha256,
                                      rawSemanticSha256,
                                      StringComparison.Ordinal),
            StableSemanticSha256 = stableSemanticSha256,
            RaceStableSemanticSha256 = raceStableSemanticSha256,
            SourceFingerprintSemanticSha256 =
                P811SealedRequiredString(
                    source,
                    "normalizedSemanticSha256"),
            DatabaseName = P811SealedRequiredString(mongo, "databaseName"),
            ReplicaSetName = P811SealedRequiredString(
                mongo,
                "replicaSetName"),
            BackendProcessId = P811SealedRequiredInt(kestrel, "processId"),
            MongoProcessId = P811SealedRequiredInt(mongo, "processId"),
            ArtifactBindings = bound.Values
                .Select(item => item.Binding)
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ToArray(),
            StableCases = stableCases,
            RawCases = rawCases,
            Documents = bound.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Document,
                StringComparer.Ordinal)
        };
    }

    private static IReadOnlyList<P811SealedOracleEvaluation>
        EvaluateP811SealedOracleBundle(
            P811SealedChildEvidence child,
            JsonObject? sourceOverride = null)
    {
        return
        [
            P811SealedOracle(
                child,
                "API_KESTREL",
                EvaluateP811SealedApiKestrel(child)),
            P811SealedOracle(
                child,
                "DIRECT_MONGO",
                EvaluateP811SealedDirectMongo(child)),
            P811SealedOracle(
                child,
                "COLLECTION_DELTA",
                EvaluateP811SealedCollectionDelta(child)),
            P811SealedOracle(
                child,
                "RECEIPT",
                EvaluateP811SealedReceipt(child)),
            P811SealedOracle(
                child,
                "QUEUE_TRACE",
                EvaluateP811SealedQueueTrace(child)),
            P811SealedOracle(
                child,
                "SOURCE_FINGERPRINT",
                EvaluateP811SealedSource(
                    sourceOverride ?? child.Documents[
                        "p8-race-source-fingerprint.json"])),
            P811SealedOracle(
                child,
                "SECURITY_SCAN",
                EvaluateP811SealedSecurity(child)),
            P811SealedOracle(
                child,
                "P7_REGRESSION",
                EvaluateP811SealedP7(child))
        ];
    }

    private static P811SealedDeliberateControl
        EvaluateP811SealedDeliberateControl(
            P811SealedChildEvidence child)
    {
        var source = (JsonObject)child.Documents[
            "p8-race-source-fingerprint.json"].DeepClone();
        var expected = P811SealedRequiredString(
            source,
            "normalizedSemanticSha256");
        var corrupted = P811SealedFlipOneNibble(expected);
        source["normalizedSemanticSha256"] = corrupted;

        var normal = EvaluateP811SealedOracleBundle(child);
        var control = EvaluateP811SealedOracleBundle(child, source);
        var failed = control.Where(item => !item.Passed)
            .Select(item => item.Oracle)
            .ToArray();
        var passed = normal.All(item => item.Passed) &&
                     failed.SequenceEqual(
                         ["SOURCE_FINGERPRINT"],
                         StringComparer.Ordinal) &&
                     control.Where(item => !string.Equals(
                             item.Oracle,
                             "SOURCE_FINGERPRINT",
                             StringComparison.Ordinal))
                         .All(item => item.Passed);
        return new P811SealedDeliberateControl(
            "P8-RACE-CONTROL-SOURCE-FINGERPRINT",
            "SOURCE_FINGERPRINT",
            "/normalizedSemanticSha256",
            expected,
            corrupted,
            normal,
            control,
            failed,
            passed ? "KHONG_DAT_EXPECTED" : "CONTROL_INVALID",
            passed);
    }

    private static P811SealedTypedCheck EvaluateP811SealedApiKestrel(
        P811SealedChildEvidence child)
        => P811SealedEvaluate("real Kestrel API evidence", () =>
        {
            var gate = child.Documents["p8-11-gate-result.json"];
            var environment = child.Documents["environment.json"];
            var kestrel = P811SealedRequiredObject(
                environment,
                "kestrel");
            var primary = P811SealedRequiredArray(
                child.Documents["api-exchanges.json"],
                "apiExchanges");
            var restart = child.Documents["p8-race-backend-restart.json"];
            var replacement = P811SealedRequiredArray(
                restart,
                "replacementApiExchanges");
            var exchanges = primary.Concat(replacement).ToArray();
            var exchangeRows = exchanges.OfType<JsonObject>().ToArray();
            var caseTaggedExchangeRows = exchangeRows
                .Where(row =>
                    row["caseId"] is JsonValue value &&
                    value.TryGetValue<string>(out var caseId) &&
                    !string.IsNullOrWhiteSpace(caseId))
                .ToArray();
            var caseIds = caseTaggedExchangeRows
                .Select(row => P811SealedRequiredString(row, "caseId"))
                .ToHashSet(StringComparer.Ordinal);
            var malformedCaseTag = exchangeRows.Any(row =>
                row.TryGetPropertyValue("caseId", out var node) &&
                (node is not JsonValue value ||
                 !value.TryGetValue<string>(out var caseId) ||
                 string.IsNullOrWhiteSpace(caseId)));
            // OPS-012/013 are direct-Mongo index oracles. OPS-014 proves a
            // real Kestrel startup fails closed before an HTTP exchange exists.
            var expectedExchangeCaseIds = AllP8CaseIds
                .Where(caseId => caseId is not (
                    "P8-OPS-012" or "P8-OPS-013" or "P8-OPS-014"))
                .ToHashSet(StringComparer.Ordinal);
            var replacementRows = replacement.OfType<JsonObject>().ToArray();
            var replacementCaseIds = replacementRows
                .Where(row => row["caseId"] is JsonValue value &&
                              value.TryGetValue<string>(out var caseId) &&
                              !string.IsNullOrWhiteSpace(caseId))
                .Select(row => P811SealedRequiredString(row, "caseId"))
                .ToHashSet(StringComparer.Ordinal);
            var expectedReplacementCaseIds = RaceCaseIds
                .Skip(10)
                .ToHashSet(StringComparer.Ordinal);
            var restartState = P811SealedRequiredObject(restart, "restart");
            var raceEvidence = P811SealedRequiredArray(
                child.Documents["P8-RACE.evidence.json"],
                "evidence");
            var raceRows = raceEvidence.OfType<JsonObject>().ToArray();
            var startupConflict = child.Documents[
                "p8-ops-014-index-conflict.json"];
            var beforeIndexSetSha256 = P811SealedRequiredString(
                startupConflict,
                "beforeIndexSetSha256");
            var afterIndexSetSha256 = P811SealedRequiredString(
                startupConflict,
                "afterIndexSetSha256");
            var startupFailure = P811SealedRequiredString(
                startupConflict,
                "startupFailure");
            return P811SealedRequiredBool(
                       gate,
                       "allMutationPathsUseRealKestrel") &&
                   P811SealedRequiredBool(kestrel, "realProcess") &&
                   P811SealedRequiredInt(kestrel, "processId") > 0 &&
                   P811SealedRequiredInt(kestrel, "port") > 0 &&
                   primary.Count > 0 &&
                   replacement.Count > 0 &&
                   exchangeRows.Length == exchanges.Length &&
                   replacementRows.Length == replacement.Count &&
                   !malformedCaseTag &&
                   caseTaggedExchangeRows.Length > 0 &&
                   caseTaggedExchangeRows.All(row =>
                       expectedExchangeCaseIds.Contains(
                           P811SealedRequiredString(row, "caseId"))) &&
                   caseIds.SetEquals(expectedExchangeCaseIds) &&
                   replacementCaseIds.SetEquals(
                       expectedReplacementCaseIds) &&
                   P811SealedRequiredInt(
                       restartState,
                       "previousProcessId") == child.BackendProcessId &&
                   P811SealedRequiredInt(
                       restartState,
                       "previousPort") ==
                   P811SealedRequiredInt(kestrel, "port") &&
                   P811SealedRequiredBool(
                       restartState,
                       "previousStopVerified") &&
                   P811SealedRequiredBool(
                       restartState,
                       "previousPortReleaseVerified") &&
                   P811SealedRequiredInt(
                       restartState,
                       "replacementProcessId") > 0 &&
                   P811SealedRequiredInt(
                       restartState,
                       "replacementProcessId") != child.BackendProcessId &&
                   P811SealedRequiredInt(
                       restartState,
                       "replacementPort") > 0 &&
                   string.Equals(
                       P811SealedRequiredString(
                           restartState,
                           "databaseName"),
                       child.DatabaseName,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       P811SealedRequiredString(restartState, "queue"),
                       "stat-config-readiness",
                       StringComparison.Ordinal) &&
                   P811SealedRequiredBool(
                       restartState,
                       "replacementAuthenticated") &&
                   P811SealedRequiredBool(restartState, "stopVerified") &&
                   P811SealedRequiredBool(
                       restartState,
                       "portReleaseVerified") &&
                   P811SealedRequiredBool(restart, "exactOwnedCleanup") &&
                   string.Equals(
                       P811SealedRequiredString(startupConflict, "caseId"),
                       "P8-OPS-014",
                       StringComparison.Ordinal) &&
                   string.Equals(
                       P811SealedRequiredString(
                           startupConflict,
                           "ownedDatabase"),
                       $"{child.DatabaseName}_p808_index_conflict",
                       StringComparison.Ordinal) &&
                   string.Equals(
                       P811SealedRequiredString(startupConflict, "collection"),
                       "stat_config_command_receipts",
                       StringComparison.Ordinal) &&
                   string.Equals(
                       P811SealedRequiredString(
                           startupConflict,
                           "desiredIndexName"),
                       "ux_statConfigCommandReceipts_owner_command",
                       StringComparison.Ordinal) &&
                   string.Equals(
                       P811SealedRequiredString(
                           startupConflict,
                           "conflictingIndexName"),
                       "p808_conflict_ux_statConfigCommandReceipts_owner_command",
                       StringComparison.Ordinal) &&
                   P811SealedRequiredBool(startupConflict, "desiredUnique") &&
                   !P811SealedRequiredBool(
                       startupConflict,
                       "conflictingUnique") &&
                   startupFailure.Contains(
                       "Backend exited",
                       StringComparison.Ordinal) &&
                   startupFailure.Contains(
                       "MongoIndexInitializer",
                       StringComparison.Ordinal) &&
                   IsLowerSha256(beforeIndexSetSha256) &&
                   string.Equals(
                       beforeIndexSetSha256,
                       afterIndexSetSha256,
                       StringComparison.Ordinal) &&
                   P811SealedRequiredBool(
                       startupConflict,
                       "conflictStillPresent") &&
                   !P811SealedRequiredBool(
                       startupConflict,
                       "desiredIndexCreated") &&
                   P811SealedRequiredBool(
                       startupConflict,
                       "cleanupVerified") &&
                   raceRows.Length == RaceCaseIds.Length &&
                   raceRows.Length == raceEvidence.Count &&
                   raceRows.Select(row => P811SealedRequiredString(
                           row,
                           "caseId"))
                       .SequenceEqual(RaceCaseIds, StringComparer.Ordinal) &&
                   raceRows.All(row => P811SealedRequiredArray(
                       row,
                       "apiExchangeSequences").Count > 0);
        });

    private static P811SealedTypedCheck EvaluateP811SealedDirectMongo(
        P811SealedChildEvidence child)
        => P811SealedEvaluate("direct Mongo owner evidence", () =>
        {
            var gate = child.Documents["p8-11-gate-result.json"];
            var environment = child.Documents["environment.json"];
            var mongo = P811SealedRequiredObject(environment, "mongo");
            var owner = child.Documents["owner-oracles.json"];
            var ownerIds = P811SealedCaseIds(owner, "cases")
                .OrderBy(caseId => caseId, StringComparer.Ordinal)
                .ToArray();
            return P811SealedRequiredInt(
                       gate,
                       "directMongoEvidenceCases") ==
                   AllP8CaseIds.Length &&
                   P811SealedRequiredBool(mongo, "isolatedReplicaSet") &&
                   P811SealedRequiredBool(mongo, "directMongoOracle") &&
                   P811SealedRequiredBool(owner, "directMongo") &&
                   ownerIds.SequenceEqual(
                       AllP8CaseIds,
                       StringComparer.Ordinal);
        });

    private static P811SealedTypedCheck EvaluateP811SealedCollectionDelta(
        P811SealedChildEvidence child)
        => P811SealedEvaluate("exact collection delta evidence", () =>
        {
            var gate = child.Documents["p8-11-gate-result.json"];
            var deltas = child.Documents["collection-deltas.json"];
            var deltaIds = P811SealedCaseIds(deltas, "cases")
                .OrderBy(caseId => caseId, StringComparer.Ordinal)
                .ToArray();
            var prohibited = P811SealedRequiredArray(
                deltas,
                "prohibited");
            return P811SealedRequiredBool(
                       gate,
                       "prohibitedCollectionDeltaZero") &&
                   deltaIds.SequenceEqual(
                       AllP8CaseIds,
                       StringComparer.Ordinal) &&
                   prohibited.Count == ProhibitedCollections.Length &&
                   prohibited.OfType<JsonObject>().All(row =>
                       P811SealedRequiredArray(
                           row,
                           "changedCases").Count == 0);
        });

    private static P811SealedTypedCheck EvaluateP811SealedReceipt(
        P811SealedChildEvidence child)
        => P811SealedEvaluate("receipt owner evidence", () =>
        {
            var race = child.Documents["P8-RACE.evidence.json"];
            var evidence = P811SealedRequiredArray(race, "evidence")
                .OfType<JsonObject>()
                .ToDictionary(
                    row => P811SealedRequiredString(row, "caseId"),
                    row => row,
                    StringComparer.Ordinal);
            return evidence.Count == RaceCaseIds.Length &&
                   RaceCaseIds.All(evidence.ContainsKey) &&
                   evidence.Values.All(row =>
                       string.Equals(
                           P811SealedRequiredString(row, "verdict"),
                           "DAT",
                           StringComparison.Ordinal) &&
                       row["ownerOracle"] is JsonObject) &&
                   P811ReceiptOracleCaseIds.All(caseId =>
                       P811SealedRequiredArray(
                           evidence[caseId],
                           "commandIds").Count > 0);
        });

    private static P811SealedTypedCheck EvaluateP811SealedQueueTrace(
        P811SealedChildEvidence child)
        => P811SealedEvaluate("queue trace evidence", () =>
        {
            var queue = child.Documents["p8-race-queue-trace.json"];
            var traceIds = P811SealedRequiredArray(queue, "traces")
                .OfType<JsonObject>()
                .Select(row => P811SealedRequiredString(row, "caseId"))
                .ToHashSet(StringComparer.Ordinal);
            return P811SealedRequiredBool(queue, "noDatasetWorker") &&
                   !string.IsNullOrWhiteSpace(
                       P811SealedRequiredString(queue, "queue")) &&
                   P811QueueOracleCaseIds.All(traceIds.Contains);
        });

    private static P811SealedTypedCheck EvaluateP811SealedSource(
        JsonObject source)
        => P811SealedEvaluate("source and assembly fingerprint", () =>
        {
            var sourceFiles = P811SealedRequiredArray(
                source,
                "sourceFiles");
            var assemblies = P811SealedRequiredArray(
                source,
                "assemblies");
            var normalized = new JsonObject
            {
                ["sourceFiles"] = P811SealedSortedFingerprintRows(sourceFiles),
                ["assemblies"] = P811SealedSortedFingerprintRows(assemblies)
            };
            var recomputed = Sha256(Encoding.UTF8.GetBytes(
                Canonicalize(normalized)));
            var claimed = P811SealedRequiredString(
                source,
                "normalizedSemanticSha256");
            return P811SealedRequiredBool(source, "passed") &&
                   P811SealedRequiredBool(source, "sourceUnchanged") &&
                   sourceFiles.Count >= 100 &&
                   assemblies.Count >= 2 &&
                   P811SealedRequiredInt(source, "sourceFileCount") ==
                   sourceFiles.Count &&
                   P811SealedRequiredInt(source, "assemblyCount") ==
                   assemblies.Count &&
                   IsLowerSha256(claimed) &&
                   string.Equals(claimed, recomputed,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       claimed,
                       P811SealedRequiredString(source, "beforeSha256"),
                       StringComparison.Ordinal) &&
                   string.Equals(
                       claimed,
                       P811SealedRequiredString(source, "afterSha256"),
                       StringComparison.Ordinal);
        });

    private static P811SealedTypedCheck EvaluateP811SealedSecurity(
        P811SealedChildEvidence child)
        => P811SealedEvaluate("pre-final and post-final security scans", () =>
        {
            var ordinary = P811SealedRequiredObject(
                child.Documents["security-scan.json"],
                "securityScan");
            var post = P811SealedRequiredObject(
                child.Documents[
                    "p8-11-post-final-security-scan.json"],
                "postFinalSecurityScan");
            return P811SealedSecurityResultPassed(ordinary) &&
                   P811SealedSecurityResultPassed(post) &&
                   P811SealedRequiredInt(post, "scannedFileCount") >=
                   P811SealedRequiredInt(ordinary, "scannedFileCount") &&
                   P811SealedRequiredBool(
                       child.Documents[
                           "p8-11-post-final-security-scan.json"],
                       "scannedAfterChildGateAndCleanup");
        });

    private static P811SealedTypedCheck EvaluateP811SealedP7(
        P811SealedChildEvidence child)
        => P811SealedEvaluate("P0-P7, historical catalog and P7 verifier", () =>
        {
            var p7 = child.Documents["p8-race-p7-regression.json"];
            var sourceFingerprint = child.Documents[
                "p8-race-source-fingerprint.json"];
            var overlay = P811SealedRequiredObject(p7, "successorOverlay");
            var p9CatalogOverlay = p7["p9CatalogSuccessorOverlay"]
                                   as JsonObject;
            var p9OverlayEnabled = p9CatalogOverlay is not null &&
                                   string.Equals(
                                       P811SealedRequiredString(
                                           p9CatalogOverlay,
                                           "overlayId"),
                                       P9SuccessorOverlayId,
                                       StringComparison.Ordinal);
            var expectedFailureCount = p9OverlayEnabled ? 80 : 52;
            var expectedPaths = p9OverlayEnabled
                ? P811ExpectedP9SuccessorPaths
                : P811ExpectedP7SuccessorPaths;
            var live = P811SealedRequiredObject(p7, "liveP7Verifier");
            var livePathNodes = P811SealedRequiredArray(
                live,
                "uniquePaths");
            var livePaths = livePathNodes
                .Select(node => node?.GetValue<string>()
                    ?? throw new InvalidOperationException(
                        "P8-11 live P7 path is null."))
                .ToArray();
            var provenanceNodes = P811SealedRequiredArray(
                overlay,
                "provenance");
            var provenanceRows = provenanceNodes
                .OfType<JsonObject>()
                .ToArray();
            var provenancePaths = provenanceRows
                .Select(row => P811SealedRequiredString(row, "path"))
                .ToArray();
            var sourceRows = P811SealedRequiredArray(
                    sourceFingerprint,
                    "sourceFiles")
                .OfType<JsonObject>()
                .ToArray();
            var sourceHashes = sourceRows.ToDictionary(
                row => P811SealedRequiredString(row, "path"),
                row => P811SealedRequiredString(row, "sha256"),
                StringComparer.Ordinal);
            var provenancePassed = provenanceRows.All(row =>
            {
                var path = P811SealedRequiredString(row, "path");
                var currentSha256 = P811SealedRequiredString(
                    row,
                    "currentSha256");
                var owningPrompts = P811SealedRequiredArray(
                    row,
                    "owningPrompts")
                    .Select(node => node?.GetValue<string>()
                        ?? throw new InvalidOperationException(
                            "P8-11 successor owner is null."))
                    .ToArray();
                var hasCurrentFingerprintOwner = owningPrompts.Contains(
                    "P8-11_CURRENT_SOURCE_FINGERPRINT",
                    StringComparer.Ordinal);
                if (!P811SealedRequiredBool(row, "passed") ||
                    !IsLowerSha256(currentSha256) ||
                    owningPrompts.Length == 0)
                {
                    return false;
                }
                if (!p9OverlayEnabled)
                {
                    return sourceHashes.TryGetValue(
                               path, out var sourceSha256) &&
                           string.Equals(
                               currentSha256,
                               sourceSha256,
                               StringComparison.Ordinal) &&
                           P811CurrentPromptP7SuccessorPaths.Contains(path) ==
                           hasCurrentFingerprintOwner;
                }
                if (sourceHashes.TryGetValue(
                        path, out var p9SourceSha256))
                {
                    return string.Equals(
                               currentSha256,
                               p9SourceSha256,
                               StringComparison.Ordinal) &&
                           (!hasCurrentFingerprintOwner ||
                            P811CurrentPromptP7SuccessorPaths.Contains(path));
                }
                return P811SealedP812DocumentationPins.TryGetValue(
                           path, out var documentationSha256) &&
                       string.Equals(
                           currentSha256,
                           documentationSha256,
                           StringComparison.Ordinal) &&
                       owningPrompts.Contains(
                           "P8-12",
                           StringComparer.Ordinal) &&
                       !hasCurrentFingerprintOwner;
            });
            var behavior = P811SealedRequiredArray(
                p7,
                "liveP7BehavioralRegression");
            var immutable = P811SealedRequiredArray(
                p7,
                "immutableP7Artifacts");
            var historical = P811SealedRequiredArray(
                p7,
                "historicalCatalogs");
            return P811SealedRequiredBool(p7, "passed") &&
                   P811SealedRequiredBool(
                       p7,
                       "historicalCatalogHashesExact") &&
                   P811SealedRequiredBool(
                       p7,
                       "p0ThroughP7SealedRegressionExact") &&
                   P811SealedRequiredBool(
                       p7,
                       "liveP7BehavioralRegressionPassed") &&
                   P811SealedRequiredBool(
                       p7,
                       "liveVerifierTruthfullyStale") &&
                   P811SealedRequiredBool(p7, "liveVerifierExecuted") &&
                   P811SealedRequiredBool(p7, "entryVerifierPassed") &&
                   P811SealedRequiredBool(p7, "successorOverlayPassed") &&
                   P811SealedRequiredBool(overlay, "passed") &&
                   string.Equals(
                       P811SealedRequiredString(overlay, "overlayId"),
                       p9OverlayEnabled
                           ? P9SuccessorOverlayId
                           : "P8-12-SUCCESSOR",
                       StringComparison.Ordinal) &&
                   P811SealedRequiredInt(
                       overlay,
                       "expectedFailureRecordCount") ==
                   expectedFailureCount &&
                   P811SealedRequiredInt(
                       overlay,
                       "expectedUniquePathCount") == expectedPaths.Length &&
                   P811SealedRequiredBool(overlay, "exactPathSet") &&
                   P811SealedRequiredInt(live, "exitCode") == 2 &&
                   P811SealedRequiredInt(live, "failureRecordCount") ==
                   expectedFailureCount &&
                   livePaths.SequenceEqual(
                       expectedPaths,
                       StringComparer.Ordinal) &&
                   P811SealedRequiredBool(live, "exactStaleHeader") &&
                   P811SealedRequiredBool(sourceFingerprint, "passed") &&
                   P811SealedRequiredBool(
                       sourceFingerprint,
                       "sourceUnchanged") &&
                   provenanceRows.Length == expectedPaths.Length &&
                   provenancePaths.SequenceEqual(
                       expectedPaths,
                       StringComparer.Ordinal) &&
                   provenancePassed &&
                   behavior.Count == 3 &&
                   behavior.OfType<JsonObject>().All(row =>
                       string.Equals(
                           P811SealedRequiredString(row, "verdict"),
                           "DAT",
                           StringComparison.Ordinal)) &&
                   immutable.Count == 6 &&
                   historical.Count == (p9OverlayEnabled ? 29 : 27) &&
                   (!p9OverlayEnabled ||
                    (p9CatalogOverlay is not null &&
                     P811SealedRequiredBool(p9CatalogOverlay, "passed") &&
                     string.Equals(
                         P811SealedRequiredString(
                             p9CatalogOverlay,
                             "rollbackCurrentSha256"),
                         CatalogCurrentV15Sha256,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         P811SealedRequiredString(
                             p9CatalogOverlay,
                             "historicalLockSha256"),
                         CatalogLockV15Sha256,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         P811SealedRequiredString(
                             p9CatalogOverlay,
                             "appendOnlyLockSha256"),
                         CatalogLockV16Sha256,
                         StringComparison.Ordinal) &&
                     P811SealedRequiredInt(
                         p9CatalogOverlay,
                         "historicalPrefixEntries") == 6 &&
                     P811SealedRequiredInt(
                         p9CatalogOverlay,
                         "appendedEntries") == 1 &&
                     string.Equals(
                         P811SealedRequiredString(
                             p9CatalogOverlay,
                             "catalogV16SemanticSha256"),
                         CatalogV16SemanticSha256,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         P811SealedRequiredString(
                             p9CatalogOverlay,
                             "schemaV16SemanticSha256"),
                         SchemaV16SemanticSha256,
                         StringComparison.Ordinal) &&
                     string.Equals(
                         P811SealedRequiredString(
                             p9CatalogOverlay,
                             "preterminalHandoffSha256"),
                         P911HandoffSha256,
                         StringComparison.Ordinal) &&
                     P811SealedRequiredInt(
                         p9CatalogOverlay,
                         "expectedP7VerifierFailures") == 80 &&
                     P811SealedRequiredInt(
                         p9CatalogOverlay,
                         "expectedP7VerifierUniquePaths") == 37)) &&
                   immutable.Concat(historical)
                       .OfType<JsonObject>()
                       .All(P811SealedFingerprintRowValid);
        });
    private static P811SealedTypedCheck EvaluateP811SealedCleanup(
        P811SealedChildEvidence child)
        => P811SealedEvaluate("owned process, DB, port and data cleanup", () =>
        {
            var cleanup = child.Documents["cleanup-manifest.json"];
            var backend = P811SealedRequiredObject(cleanup, "backend");
            var mongo = P811SealedRequiredObject(cleanup, "mongo");
            var restartArtifact = child.Documents[
                "p8-race-backend-restart.json"];
            var restart = P811SealedRequiredObject(
                restartArtifact,
                "restart");
            return string.Equals(
                       P811SealedRequiredString(cleanup, "state"),
                       "CLEANED",
                       StringComparison.Ordinal) &&
                   P811SealedRequiredBool(cleanup, "cleanupSucceeded") &&
                   P811SealedRequiredBool(backend, "stopVerified") &&
                   P811SealedRequiredBool(
                       backend,
                       "portReleaseVerified") &&
                   P811SealedRequiredBool(
                       mongo,
                       "databaseDropVerified") &&
                   P811SealedRequiredBool(mongo, "processStopVerified") &&
                   P811SealedRequiredBool(
                       mongo,
                       "portReleaseVerified") &&
                   P811SealedRequiredBool(
                       mongo,
                       "dataDirectoryRemovalVerified") &&
                   P811SealedRequiredBool(
                       restartArtifact,
                       "exactOwnedCleanup") &&
                   P811SealedRequiredBool(
                       restart,
                       "previousStopVerified") &&
                   P811SealedRequiredBool(
                       restart,
                       "previousPortReleaseVerified") &&
                   P811SealedRequiredBool(restart, "stopVerified") &&
                   P811SealedRequiredBool(
                       restart,
                       "portReleaseVerified");
        });

    private static P811SealedOracleEvaluation P811SealedOracle(
        P811SealedChildEvidence child,
        string oracle,
        P811SealedTypedCheck check)
    {
        var evidenceNames = oracle switch
        {
            "API_KESTREL" => new[]
            {
                "environment.json", "api-exchanges.json",
                "p8-race-backend-restart.json",
                "p8-ops-014-index-conflict.json", "P8-RACE.evidence.json"
            },
            "DIRECT_MONGO" => new[]
            {
                "environment.json", "owner-oracles.json"
            },
            "COLLECTION_DELTA" => new[]
            {
                "p8-11-gate-result.json", "collection-deltas.json"
            },
            "RECEIPT" => new[] { "P8-RACE.evidence.json" },
            "QUEUE_TRACE" => new[] { "p8-race-queue-trace.json" },
            "SOURCE_FINGERPRINT" => new[]
            {
                "p8-race-source-fingerprint.json"
            },
            "SECURITY_SCAN" => new[]
            {
                "security-scan.json",
                "p8-11-post-final-security-scan.json"
            },
            "P7_REGRESSION" => new[] { "p8-race-p7-regression.json" },
            _ => Array.Empty<string>()
        };
        var bindings = evidenceNames.Select(name =>
                child.ArtifactBindings.Single(binding => string.Equals(
                    binding.Name,
                    name,
                    StringComparison.Ordinal)))
            .ToArray();
        return new P811SealedOracleEvaluation(
            oracle,
            check.Passed,
            check.Detail,
            bindings);
    }

    private static P811SealedTypedCheck P811SealedEvaluate(
        string success,
        Func<bool> predicate)
    {
        try
        {
            var passed = predicate();
            return new P811SealedTypedCheck(
                passed,
                passed ? success : $"{success} failed a typed assertion");
        }
        catch (Exception ex)
        {
            return new P811SealedTypedCheck(
                false,
                $"{success}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<P811SealedBoundDocument>
        ReadP811SealedBoundDocumentAsync(
            string workspaceRoot,
            string runRoot,
            string name,
            CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(Path.Combine(runRoot, name));
        if (!P811SealedIsWithin(runRoot, fullPath))
        {
            throw new InvalidOperationException(
                $"P8-11 artifact escaped its child root: {name}");
        }
        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException(
                $"P8-11 artifact is empty: {name}");
        }
        var document = JsonNode.Parse(bytes) as JsonObject
                       ?? throw new InvalidOperationException(
                           $"P8-11 artifact is not a JSON object: {name}");
        return new P811SealedBoundDocument(
            new P811SealedArtifactBinding(
                name,
                P811SealedRelative(workspaceRoot, fullPath),
                bytes.LongLength,
                Sha256(bytes)),
            document);
    }

    private static async Task<P811SealedArtifactBinding>
        WriteP811SealedArtifactAsync(
            HarnessPaths paths,
            string name,
            object value,
            CancellationToken ct)
    {
        var fullPath = Path.Combine(paths.RunRoot, name);
        await EvidenceJson.WriteAsync(fullPath, value, ct);
        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        return new P811SealedArtifactBinding(
            name,
            P811SealedRelative(paths.WorkspaceRoot, fullPath),
            bytes.LongLength,
            Sha256(bytes));
    }

    private static string P811SealedComputeRawSemanticSha256(
        IEnumerable<P811SealedRawCase> cases)
    {
        var normalized = new JsonArray(cases
            .OrderBy(row => row.CaseId, StringComparer.Ordinal)
            .Select(row => (JsonNode)new JsonObject
            {
                ["caseId"] = row.CaseId,
                ["verdict"] = row.Verdict,
                ["fingerprint"] = row.Fingerprint
            })
            .ToArray());
        return Sha256(Encoding.UTF8.GetBytes(Canonicalize(normalized)));
    }

    private static string P811SealedComputeStableSemanticSha256(
        IEnumerable<P811SealedStableCase> cases)
    {
        var projection = new JsonArray(cases.Select(row =>
            (JsonNode)new JsonObject
            {
                ["caseId"] = row.CaseId,
                ["verdict"] = row.Verdict,
                ["realKestrel"] = row.RealKestrel,
                ["directMongo"] = row.DirectMongo,
                ["evidenceBound"] = row.EvidenceBound
            }).ToArray());
        return Sha256(Encoding.UTF8.GetBytes(Canonicalize(projection)));
    }

    private static JsonArray P811SealedSortedFingerprintRows(
        JsonArray rows)
    {
        var parsed = rows.OfType<JsonObject>()
            .Select(row => new
            {
                Path = P811SealedRequiredString(row, "path"),
                Bytes = P811SealedRequiredLong(row, "bytes"),
                Sha256 = P811SealedRequiredString(row, "sha256")
            })
            .OrderBy(row => row.Path, StringComparer.Ordinal)
            .ToArray();
        if (parsed.Length != rows.Count ||
            parsed.Any(row => row.Bytes <= 0 ||
                              !IsLowerSha256(row.Sha256)))
        {
            throw new InvalidOperationException(
                "Source fingerprint contains an invalid row.");
        }
        return new JsonArray(parsed.Select(row => (JsonNode)new JsonObject
        {
            ["path"] = row.Path,
            ["bytes"] = row.Bytes,
            ["sha256"] = row.Sha256
        }).ToArray());
    }

    private static bool P811SealedFingerprintRowValid(JsonObject row)
        => !string.IsNullOrWhiteSpace(
               P811SealedRequiredString(row, "path")) &&
           P811SealedRequiredLong(row, "bytes") > 0 &&
           IsLowerSha256(P811SealedRequiredString(row, "sha256"));

    private static bool P811SealedSecurityResultPassed(JsonObject scan)
        => P811SealedRequiredBool(scan, "passed") &&
           P811SealedRequiredInt(scan, "scannedFileCount") > 0 &&
           P811SealedRequiredArray(scan, "findings").Count == 0;

    private static async Task<P811SealedDirectorySecurityResult>
        RunP811SealedAggregateSecurityScanAsync(
            string runRoot,
            CancellationToken ct)
    {
        var findings = new List<P811SealedDirectorySecurityFinding>();
        var files = Directory.EnumerateFiles(
                runRoot,
                "*",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var credentialedMongo = new Regex(
            @"mongodb(?:\+srv)?://[^\s:/]+:[^\s@/]+@",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var bearerJwt = new Regex(
            @"Bearer\s+eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var sensitiveJson = new Regex(
            "\\\"(?:password|accessToken|bootstrapKey|connectionString)\\\"\\s*:\\s*\\\"(?!(?:<redacted>|REDACTED|\\*{3,})\\\")[^\\\"]+\\\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(runRoot, file)
                .Replace('\\', '/');
            var extension = Path.GetExtension(file);
            if (string.Equals(extension, ".secret",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".stop",
                    StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new P811SealedDirectorySecurityFinding(
                    relative,
                    "SENSITIVE_FILE",
                    extension));
                continue;
            }
            string text;
            try
            {
                text = await File.ReadAllTextAsync(file, ct);
            }
            catch (Exception ex)
            {
                findings.Add(new P811SealedDirectorySecurityFinding(
                    relative,
                    "READ_ERROR",
                    ex.GetType().Name));
                continue;
            }
            if (credentialedMongo.IsMatch(text))
                findings.Add(new P811SealedDirectorySecurityFinding(
                    relative, "MONGO_CREDENTIAL", "CREDENTIALED_URI"));
            if (bearerJwt.IsMatch(text))
                findings.Add(new P811SealedDirectorySecurityFinding(
                    relative, "BEARER_TOKEN", "JWT_PATTERN"));
            if (sensitiveJson.IsMatch(text))
                findings.Add(new P811SealedDirectorySecurityFinding(
                    relative, "SENSITIVE_JSON", "RAW_VALUE"));
        }
        return new P811SealedDirectorySecurityResult(
            findings.Count == 0,
            files.Length,
            findings);
    }

    private static string? P811SealedFailureReason(
        bool deliberateFailure,
        bool exactIterationCount,
        bool identitiesDistinct,
        bool allChildrenPassed,
        bool allEightOraclesPassed,
        bool semanticShaMatch,
        bool raceSemanticShaMatch,
        bool sourceShaMatch,
        bool allArtifactsBound,
        bool deliberateControlPassed,
        bool cleanupPassed,
        bool aggregateSecurityPassed)
    {
        if (deliberateFailure)
            return deliberateControlPassed
                ? "Expected-red deliberate SOURCE_FINGERPRINT control completed."
                : "Deliberate control did not isolate one named oracle.";
        if (!exactIterationCount)
            return "P8-11 requires exactly two complete children.";
        if (!identitiesDistinct)
            return "The two children reused a run, DB, replica-set or process identity.";
        if (!allChildrenPassed)
            return "A 200-case child was incomplete, red or out of exact order.";
        if (!allEightOraclesPassed)
            return "At least one required shared oracle failed typed evaluation.";
        if (!semanticShaMatch)
            return "Stable 200-case semantic projections differ.";
        if (!raceSemanticShaMatch)
            return "Stable P8-RACE semantic projections differ.";
        if (!sourceShaMatch)
            return "Source/assembly fingerprint semantic SHA values differ.";
        if (!allArtifactsBound)
            return "A required child artifact is missing or unbound.";
        if (!deliberateControlPassed)
            return "The real one-oracle deliberate evaluator failed.";
        if (!cleanupPassed)
            return "Owned child cleanup failed typed validation.";
        if (!aggregateSecurityPassed)
            return "Aggregate artifact security scan failed.";
        return null;
    }

    private static string P811SealedFlipOneNibble(string sha256)
    {
        if (!IsLowerSha256(sha256))
            throw new InvalidOperationException(
                "Deliberate SOURCE_FINGERPRINT input is not lowercase SHA-256.");
        return (sha256[0] == '0' ? "1" : "0") + sha256[1..];
    }

    private static void P811SealedRequireIdentity(
        JsonObject document,
        string artifact,
        string runKey)
    {
        if (!string.Equals(
                P811SealedRequiredString(document, "chainId"),
                ChainId,
                StringComparison.Ordinal) ||
            !string.Equals(
                P811SealedRequiredString(document, "promptId"),
                "P8-11",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"P8-11 artifact identity is invalid: {artifact}");
        }
        if (document["runKey"] is JsonNode runNode &&
            !string.Equals(
                runNode.GetValue<string>(),
                runKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"P8-11 artifact runKey is invalid: {artifact}");
        }
    }

    private static string[] P811SealedCaseIds(
        JsonObject root,
        string arrayName)
        => P811SealedRequiredArray(root, arrayName)
            .Select(node => node as JsonObject
                            ?? throw new InvalidOperationException(
                                $"{arrayName} row is not an object."))
            .Select(row => P811SealedRequiredString(row, "caseId"))
            .ToArray();

    private static JsonObject P811SealedRequiredObject(
        JsonObject root,
        string name)
        => root[name] as JsonObject
           ?? throw new InvalidOperationException(
               $"P8-11 JSON lacks object '{name}'.");

    private static JsonArray P811SealedRequiredArray(
        JsonObject root,
        string name)
        => root[name] as JsonArray
           ?? throw new InvalidOperationException(
               $"P8-11 JSON lacks array '{name}'.");

    private static string P811SealedRequiredString(
        JsonObject root,
        string name)
        => root[name]?.GetValue<string>()
           ?? throw new InvalidOperationException(
               $"P8-11 JSON lacks string '{name}'.");

    private static bool P811SealedRequiredBool(
        JsonObject root,
        string name)
        => root[name]?.GetValue<bool>()
           ?? throw new InvalidOperationException(
               $"P8-11 JSON lacks boolean '{name}'.");

    private static int P811SealedRequiredInt(
        JsonObject root,
        string name)
        => root[name]?.GetValue<int>()
           ?? throw new InvalidOperationException(
               $"P8-11 JSON lacks integer '{name}'.");

    private static long P811SealedRequiredLong(
        JsonObject root,
        string name)
        => root[name]?.GetValue<long>()
           ?? throw new InvalidOperationException(
               $"P8-11 JSON lacks integer '{name}'.");

    private static bool P811SealedIsWithin(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(
            fullRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string P811SealedRelative(
        string workspaceRoot,
        string fullPath)
        => Path.GetRelativePath(workspaceRoot, Path.GetFullPath(fullPath))
            .Replace('\\', '/');
}

internal sealed record P811SealedArtifactBinding(
    string Name,
    string Path,
    long Bytes,
    string RawSha256);

internal sealed record P811SealedRawCase(
    string CaseId,
    string Verdict,
    string Fingerprint);

internal sealed record P811SealedStableCase(
    string CaseId,
    string Verdict,
    bool RealKestrel,
    bool DirectMongo,
    bool EvidenceBound);

internal sealed record P811SealedTypedCheck(
    bool Passed,
    string Detail);

internal sealed record P811SealedOracleEvaluation(
    string Oracle,
    bool Passed,
    string Detail,
    IReadOnlyList<P811SealedArtifactBinding> Evidence);

internal sealed record P811SealedDeliberateControl(
    string CaseId,
    string Oracle,
    string MutationPath,
    string ExpectedSha256,
    string CorruptedSha256,
    IReadOnlyList<P811SealedOracleEvaluation> NormalEvaluation,
    IReadOnlyList<P811SealedOracleEvaluation> ControlEvaluation,
    IReadOnlyList<string> FailedOracles,
    string Verdict,
    bool Passed);

internal sealed record P811SealedDirectorySecurityFinding(
    string Path,
    string Kind,
    string Detection);

internal sealed record P811SealedDirectorySecurityResult(
    bool Passed,
    int ScannedFileCount,
    IReadOnlyList<P811SealedDirectorySecurityFinding> Findings);

internal sealed class P811SealedChildEvidence
{
    public int Iteration { get; init; }
    public int ExitCode { get; init; }
    public string ChildRunKey { get; init; } = string.Empty;
    public string RunRoot { get; init; } = string.Empty;
    public int ExactExpectedCaseCount { get; init; }
    public int ActualCaseCount { get; init; }
    public bool GatePassed { get; init; }
    public bool ExactCaseOrder { get; init; }
    public bool AllCasesDat { get; init; }
    public string ClaimedRawSemanticSha256 { get; init; } = string.Empty;
    public string RawSemanticSha256 { get; init; } = string.Empty;
    public bool RawSemanticVerified { get; init; }
    public string StableSemanticSha256 { get; init; } = string.Empty;
    public string RaceStableSemanticSha256 { get; init; } = string.Empty;
    public string SourceFingerprintSemanticSha256 { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string ReplicaSetName { get; init; } = string.Empty;
    public int BackendProcessId { get; init; }
    public int MongoProcessId { get; init; }
    public IReadOnlyList<P811SealedArtifactBinding> ArtifactBindings
        { get; init; } = [];
    public IReadOnlyList<P811SealedStableCase> StableCases
        { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyList<P811SealedRawCase> RawCases { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyDictionary<string, JsonObject> Documents
        { get; init; } = new Dictionary<string, JsonObject>();
}

internal sealed record P811SealedBoundDocument(
    P811SealedArtifactBinding Binding,
    JsonObject Document);
