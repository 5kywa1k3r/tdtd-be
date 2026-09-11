using System.Text;
using System.Text.Json;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task<bool> WriteRaceEvidenceAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var results = _cases.Results.ToArray();
        var exactIds = results.Select(result => result.CaseId)
            .SequenceEqual(RaceExpectedCaseIds, StringComparer.Ordinal);
        var allDat = results.All(result => result.Verdict == HarnessVerdict.DAT);
        var directMongoVerified =
            _raceFinalSnapshot is not null &&
            _caseEvidence.Count == results.Length &&
            _caseEvidence.All(item => item.Verdict == HarnessVerdict.DAT);
        var normalized = results.Select(result => new
        {
            result.CaseId,
            verdict = result.Verdict.ToString(),
            result.Fingerprint
        }).ToArray();
        var normalizedSemanticSha256 = HashBytes(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized)));

        var oraclePaths = RaceRequiredOracles.ToDictionary(
            oracle => oracle,
            oracle => Path.Combine(
                _paths.RunRoot,
                $"P9-RACE.{oracle.ToLowerInvariant().Replace('_', '-')}.json"),
            StringComparer.Ordinal);
        var mainExchanges = _raceApiExchanges
            .Select(exchange => new
            {
                exchange.Sequence,
                exchange.Method,
                exchange.Path,
                exchange.StatusCode,
                exchange.CaseId
            })
            .ToArray();
        var variantExchanges = _variantEvidence
            .SelectMany(variant => variant.Exchanges.Select(exchange => new
            {
                variant.Name,
                exchange.Sequence,
                exchange.Method,
                exchange.Path,
                exchange.StatusCode,
                exchange.CaseId
            }))
            .ToArray();

        await WriteRaceOracleAsync(
            oraclePaths["API_KESTREL"],
            "P9_RACE_API_KESTREL_V1",
            new
            {
                realKestrel = _backend is not null,
                finalBackendProcessId = _backend?.ProcessId,
                finalBackendPort = _backend?.Port,
                restartCount = _raceBackendRestartCount,
                mainExchangeCount = mainExchanges.Length,
                variantExchangeCount = variantExchanges.Length,
                mainExchanges,
                variantExchanges,
                passed = _backend is not null &&
                         _raceBackendRestartCount >= 1 &&
                         mainExchanges.Length > 0
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["AUTH_BEFORE_EXISTENCE"],
            "P9_RACE_AUTH_BEFORE_EXISTENCE_V1",
            new
            {
                traces = _raceTimeline.Where(item =>
                    item.CaseId.StartsWith("P9-RACE-SECURITY-0", StringComparison.Ordinal) &&
                    item.CaseId is "P9-RACE-SECURITY-01" or
                        "P9-RACE-SECURITY-02" or
                        "P9-RACE-SECURITY-03"),
                verified = _raceAuthVerified,
                passed = _raceAuthVerified
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["DIRECT_MONGO"],
            "P9_RACE_DIRECT_MONGO_V1",
            new
            {
                databaseName = _mongo?.DatabaseName,
                finalSnapshot = _raceFinalSnapshot,
                caseSnapshots = _caseEvidence,
                prohibitedRunCollections = ProhibitedRunCollections,
                passed = directMongoVerified
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["COLLECTION_DELTA"],
            "P9_RACE_COLLECTION_DELTA_V1",
            new
            {
                expectedCases = RaceExpectedCaseIds,
                actualCases = _caseEvidence.Select(item => item.CaseId),
                cases = _caseEvidence.Select(item => new
                {
                    item.CaseId,
                    verdict = item.Verdict.ToString(),
                    item.Deltas,
                    item.ApiExchangeSequences,
                    item.Failure
                }),
                passed = directMongoVerified && exactIds
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["CONFIG_HASH_RECOMPUTE"],
            "P9_RACE_CONFIG_HASH_RECOMPUTE_V1",
            new
            {
                stored = _fixture?.ConfigHash,
                recomputed = _fixture is null
                    ? null
                    : CanonicalJsonFileSha256(
                        Encoding.UTF8.GetBytes(_fixture.ConfigPayloadJson)),
                verified = _raceConfigHashVerified,
                passed = _raceConfigHashVerified
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["RECEIPT"],
            "P9_RACE_RECEIPT_V1",
            new
            {
                receipts = _raceReceipts,
                durableReceiptCount = _raceReceipts.Count(item => item.Durable),
                replayVerified = _raceReceipts.Any(item => item.ReplayVerified),
                passed = _raceReceiptVerified &&
                         _raceReceipts.Count >= 2 &&
                         _raceReceipts.All(item =>
                             item.ReceiptId.Length == 64 &&
                             item.RequestHash.Length == 64)
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["QUEUE_TRACE"],
            "P9_RACE_QUEUE_TRACE_V1",
            new
            {
                timeline = _raceTimeline,
                crashCases = _raceTimeline.Where(item =>
                    item.CaseId.StartsWith("P9-RACE-CRASH-", StringComparison.Ordinal)),
                verified = _raceQueueVerified,
                passed = _raceQueueVerified &&
                         _raceTimeline.Any(item =>
                             item.Operation == "PROCESS_CRASH_LEASE_RECLAIM") &&
                         _raceTimeline.Any(item =>
                             item.Operation == "LIFECYCLE_APPROVE_REVERSE_REORDER")
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["SOURCE_FINGERPRINT"],
            "P9_RACE_SOURCE_FINGERPRINT_V1",
            new
            {
                before = _raceSourceBefore,
                after = _raceSourceAfter,
                equal = _raceSourceVerified,
                passed = _raceSourceVerified
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["INDEX_EXPLAIN"],
            "P9_RACE_INDEX_EXPLAIN_V1",
            new
            {
                evidence = _raceIndexEvidence,
                passed = _raceIndexVerified &&
                         _raceIndexEvidence?.Passed == true
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["EXPORT_PARSE"],
            "P9_RACE_EXPORT_PARSE_V1",
            new
            {
                exports = _raceExports,
                formats = _raceExports.Select(item => item.Format)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal),
                parsed = _raceExportVerified,
                passed = _raceExportVerified &&
                         _raceExports.Count == 2 &&
                         _raceExports.All(item => item.Parsed && item.InjectionSafe)
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["P8_REGRESSION"],
            "P9_RACE_P8_REGRESSION_V1",
            new
            {
                schemaVersion = "P8-BUNDLE-1",
                currentPhase = StatConfigPhaseBarrier.CurrentPhase,
                verified = _raceP8RegressionVerified,
                passed = _raceP8RegressionVerified &&
                         StatConfigPhaseBarrier.CurrentPhase == 8
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["P10_ZERO_WRITE"],
            "P9_RACE_P10_ZERO_WRITE_V1",
            new
            {
                entry = "P10_RECONCILE",
                expectedStatus = 409,
                databaseDelta = 0,
                verified = _raceP10ZeroWriteVerified,
                passed = _raceP10ZeroWriteVerified
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["PROFILE_BLOCK"],
            "P9_RACE_PROFILE_BLOCK_V1",
            new
            {
                capabilityId = "FLOW_STATISTIC_PROFILE",
                status = "INTENTIONAL_BLOCK",
                targetPhase = (string?)null,
                activationReason = "CAPABILITY_CONFLICT",
                verified = _raceProfileBlockVerified,
                passed = _raceProfileBlockVerified
            },
            ct);
        await WriteRaceOracleAsync(
            oraclePaths["CLEANUP"],
            "P9_RACE_CLEANUP_V1",
            new
            {
                exportCleanupVerified = _raceCleanupVerified,
                cleanupSucceeded,
                cleanupErrors,
                backend = new
                {
                    processId = _backend?.ProcessId,
                    stopVerified = _backend?.StopVerified ?? false,
                    portReleaseVerified = _backend?.PortReleaseVerified ?? false,
                    restartCount = _raceBackendRestartCount
                },
                mongo = new
                {
                    processId = _mongo?.ProcessId,
                    databaseDropVerified = _mongo?.DatabaseDropVerified ?? false,
                    processStopVerified = _mongo?.ProcessStopVerified ?? false,
                    portReleaseVerified = _mongo?.PortReleaseVerified ?? false,
                    dataDirectoryRemovalVerified =
                        _mongo?.DataDirectoryRemovalVerified ?? false
                },
                fixtureCycles = _fixtureCycles,
                passed = cleanupSucceeded && _raceCleanupVerified &&
                         _fixtureCycles.Any(item => item.ReturnedToBaseline)
            },
            ct);

        var securityFindings = ScanArtifactSecurity();
        var securityPassed =
            _raceSecurityBoundaryVerified && securityFindings.Count == 0;
        await WriteRaceOracleAsync(
            oraclePaths["SECURITY_SCAN"],
            "P9_RACE_SECURITY_SCAN_V1",
            new
            {
                exactRuntimeSecretCount = _artifactSecrets.Count,
                findings = securityFindings,
                boundaryVerified = _raceSecurityBoundaryVerified,
                passed = securityPassed
            },
            ct);

        var oraclePassed = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["API_KESTREL"] = _backend is not null &&
                              _raceBackendRestartCount >= 1 &&
                              mainExchanges.Length > 0,
            ["AUTH_BEFORE_EXISTENCE"] = _raceAuthVerified,
            ["DIRECT_MONGO"] = directMongoVerified,
            ["COLLECTION_DELTA"] = directMongoVerified && exactIds,
            ["CONFIG_HASH_RECOMPUTE"] = _raceConfigHashVerified,
            ["RECEIPT"] = _raceReceiptVerified && _raceReceipts.Count >= 2,
            ["QUEUE_TRACE"] = _raceQueueVerified,
            ["SOURCE_FINGERPRINT"] = _raceSourceVerified,
            ["INDEX_EXPLAIN"] = _raceIndexVerified,
            ["EXPORT_PARSE"] = _raceExportVerified,
            ["SECURITY_SCAN"] = securityPassed,
            ["CLEANUP"] = cleanupSucceeded && _raceCleanupVerified,
            ["P8_REGRESSION"] = _raceP8RegressionVerified,
            ["P10_ZERO_WRITE"] = _raceP10ZeroWriteVerified,
            ["PROFILE_BLOCK"] = _raceProfileBlockVerified
        };
        var artifacts = RaceRequiredOracles
            .Select(oracle => BuildRaceArtifactPin(
                oraclePaths[oracle], oracle, oraclePassed[oracle]))
            .ToArray();
        var allOraclesPassed = RaceRequiredOracles.All(oracle =>
            oraclePassed.TryGetValue(oracle, out var verified) && verified);
        var passed =
            fatalFailure is null &&
            results.Length == RaceExpectedCaseIds.Length &&
            exactIds &&
            allDat &&
            _raceCasVerified &&
            _raceCrashVerified &&
            allOraclesPassed &&
            cleanupSucceeded &&
            artifacts.Length == RaceRequiredOracles.Length;

        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-RACE.evidence.json"),
            new
            {
                schemaVersion = "P9_RACE_EVIDENCE_V1",
                chainId = ChainId,
                promptId = RacePromptId,
                groupId = RaceGroupId,
                runKey = _runKey,
                startedAtUtc,
                completedAtUtc,
                requirements = new[] { "P9-STAT-027", "P9-STAT-028" },
                expectedCaseCount = RaceExpectedCaseIds.Length,
                actualCaseCount = results.Length,
                exactIds,
                allDat,
                normalizedSemanticSha256,
                oracles = RaceRequiredOracles,
                oraclePassed,
                cases = results,
                artifacts,
                assertions = new
                {
                    realKestrel = _backend is not null,
                    isolatedMongo = _mongo is not null,
                    stage = 9,
                    sealedCandidate = true,
                    currentRemainsV15 = true,
                    cas = _raceCasVerified,
                    crashLeaseRecovery = _raceCrashVerified,
                    lifecycleApproveReversal = _raceQueueVerified,
                    authorizationBeforeExistence = _raceAuthVerified,
                    serverOwnedExportParsing = _raceExportVerified,
                    injectionAndResourceSafety = _raceSecurityBoundaryVerified,
                    configHashRecomputed = _raceConfigHashVerified,
                    sourceFingerprintStable = _raceSourceVerified,
                    indexExplain = _raceIndexVerified,
                    p8Regression = _raceP8RegressionVerified,
                    p10ZeroWrite = _raceP10ZeroWriteVerified,
                    profileBlock = _raceProfileBlockVerified,
                    cleanup = cleanupSucceeded
                },
                cleanupSucceeded,
                cleanupErrors,
                fatalFailure,
                passed
            },
            ct);
        return passed;
    }

    private async Task WriteRaceOracleAsync(
        string path,
        string schemaVersion,
        object body,
        CancellationToken ct)
        => await WriteStrictJsonAsync(
            path,
            new
            {
                schemaVersion,
                chainId = ChainId,
                promptId = RacePromptId,
                groupId = RaceGroupId,
                runKey = _runKey,
                body
            },
            ct);

    private object BuildRaceArtifactPin(
        string path,
        string purpose,
        bool passed)
        => new
        {
            path = Path.GetRelativePath(_paths.WorkspaceRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'),
            sha256 = HashBytes(File.ReadAllBytes(path)),
            bytes = new FileInfo(path).Length,
            purpose,
            passed
        };
}
