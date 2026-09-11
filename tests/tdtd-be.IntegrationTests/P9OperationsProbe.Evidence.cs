using System.Text;
using System.Text.Json;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task<bool> WriteOperationsEvidenceAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var results = _cases.Results.ToArray();
        var exactIds = results.Select(result => result.CaseId).SequenceEqual(
            OperationsExpectedCaseIds,
            StringComparer.Ordinal);
        var allDat = results.All(result => result.Verdict == HarnessVerdict.DAT);
        var normalized = results.Select(result => new
        {
            result.CaseId,
            verdict = result.Verdict.ToString(),
            result.Fingerprint
        }).ToArray();
        var normalizedSemanticSha256 = HashBytes(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized)));

        var oraclePaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["API_KESTREL"] = Path.Combine(_paths.RunRoot, "P9-OPS.api-kestrel.json"),
            ["AUTH_BEFORE_EXISTENCE"] = Path.Combine(_paths.RunRoot, "P9-OPS.auth-before-existence.json"),
            ["DIRECT_MONGO"] = Path.Combine(_paths.RunRoot, "P9-OPS.direct-mongo.json"),
            ["COLLECTION_DELTA"] = Path.Combine(_paths.RunRoot, "P9-OPS.collection-delta.json"),
            ["RECEIPT"] = Path.Combine(_paths.RunRoot, "P9-OPS.receipt.json"),
            ["QUEUE_TRACE"] = Path.Combine(_paths.RunRoot, "P9-OPS.queue-trace.json"),
            ["SECURITY_SCAN"] = Path.Combine(_paths.RunRoot, "P9-OPS.security-scan.json"),
            ["CLEANUP"] = Path.Combine(_paths.RunRoot, "P9-OPS.cleanup.json")
        };

        var exchanges = RequireApi().Exchanges.ToArray();
        await WriteOperationsOracleAsync(
            oraclePaths["API_KESTREL"],
            "P9_OPS_API_KESTREL_V1",
            new
            {
                realKestrel = _backend is not null,
                backendProcessId = _backend?.ProcessId,
                backendPort = _backend?.Port,
                exchangeCount = exchanges.Length,
                exchanges,
                passed = _backend is not null && exchanges.Length > 0
            },
            ct);
        await WriteOperationsOracleAsync(
            oraclePaths["AUTH_BEFORE_EXISTENCE"],
            "P9_OPS_AUTH_BEFORE_EXISTENCE_V1",
            new
            {
                traces = _opsAuthTrace,
                passed = _opsAuthBeforeExistenceVerified
            },
            ct);
        await WriteOperationsOracleAsync(
            oraclePaths["DIRECT_MONGO"],
            "P9_OPS_DIRECT_MONGO_V1",
            new
            {
                approvedRunId = _opsApprovedJob is null ? null : BsonString(_opsApprovedJob, "_id"),
                approvedGenerationId = _flwGenerationId,
                reversalRunId = _opsReversalJob is null ? null : BsonString(_opsReversalJob, "_id"),
                lifecycleTrace = _opsLifecycleTrace,
                jobTrace = _opsJobTrace,
                passed = _opsDirectMongoVerified
            },
            ct);
        await WriteOperationsOracleAsync(
            oraclePaths["COLLECTION_DELTA"],
            "P9_OPS_COLLECTION_DELTA_V1",
            new
            {
                caseEvidence = _caseEvidence,
                approvedSnapshot = _opsApprovedSnapshot,
                reversedSnapshot = _opsReversedSnapshot,
                reapprovedSnapshot = _opsReapprovedSnapshot,
                passed = _opsCollectionDeltaVerified
            },
            ct);
        await WriteOperationsOracleAsync(
            oraclePaths["RECEIPT"],
            "P9_OPS_RECEIPT_V1",
            new
            {
                traces = _opsReceiptTrace,
                passed = _opsReceiptVerified
            },
            ct);
        await WriteOperationsOracleAsync(
            oraclePaths["QUEUE_TRACE"],
            "P9_OPS_QUEUE_TRACE_V1",
            new
            {
                lifecycle = _opsLifecycleTrace,
                jobs = _opsJobTrace,
                passed = _opsQueueTraceVerified
            },
            ct);
        await WriteOperationsOracleAsync(
            oraclePaths["SECURITY_SCAN"],
            "P9_OPS_SECURITY_SCAN_V1",
            new
            {
                prohibitedTokens = new[]
                {
                    "connectionString",
                    "password",
                    "bearer",
                    "stackTrace",
                    "sourcePayloadJson"
                },
                securityScanVerified = _opsSecurityScanVerified,
                passed = _opsSecurityScanVerified
            },
            ct);
        await WriteOperationsOracleAsync(
            oraclePaths["CLEANUP"],
            "P9_OPS_CLEANUP_V1",
            new
            {
                traces = _opsCleanupTrace,
                cleanupSucceeded,
                cleanupErrors,
                backend = new
                {
                    processId = _backend?.ProcessId,
                    stopVerified = _backend?.StopVerified ?? false,
                    portReleaseVerified = _backend?.PortReleaseVerified ?? false
                },
                mongo = new
                {
                    processId = _mongo?.ProcessId,
                    databaseDropVerified = _mongo?.DatabaseDropVerified ?? false,
                    processStopVerified = _mongo?.ProcessStopVerified ?? false,
                    portReleaseVerified = _mongo?.PortReleaseVerified ?? false,
                    dataDirectoryRemovalVerified = _mongo?.DataDirectoryRemovalVerified ?? false
                },
                passed = cleanupSucceeded
            },
            ct);

        var passed = fatalFailure is null &&
                     results.Length == OperationsExpectedCaseIds.Length &&
                     exactIds &&
                     allDat &&
                     cleanupSucceeded &&
                     _opsAuthBeforeExistenceVerified &&
                     _opsDirectMongoVerified &&
                     _opsCollectionDeltaVerified &&
                     _opsReceiptVerified &&
                     _opsQueueTraceVerified &&
                     _opsSecurityScanVerified;
        var artifacts = OperationsRequiredOracles
            .Select(oracle => BuildOperationsArtifactPin(oraclePaths[oracle], oracle))
            .ToArray();
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-OPS.evidence.json"),
            new
            {
                schemaVersion = "P9_OPS_EVIDENCE_V1",
                chainId = ChainId,
                promptId = OperationsPromptId,
                groupId = OperationsGroupId,
                runKey = _runKey,
                startedAtUtc,
                completedAtUtc,
                expectedCaseCount = OperationsExpectedCaseIds.Length,
                actualCaseCount = results.Length,
                exactIds,
                allDat,
                normalizedSemanticSha256,
                oracles = OperationsRequiredOracles,
                cases = results,
                artifacts,
                assertions = new
                {
                    realKestrel = _backend is not null,
                    isolatedMongo = _mongo is not null,
                    approvedIncludeFixture = _opsApprovedJob is not null,
                    exactReversal = _opsReversalJob is not null,
                    authBeforeExistence = _opsAuthBeforeExistenceVerified,
                    directMongo = _opsDirectMongoVerified,
                    collectionDelta = _opsCollectionDeltaVerified,
                    receipt = _opsReceiptVerified,
                    queueTrace = _opsQueueTraceVerified,
                    securityScan = _opsSecurityScanVerified,
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

    private async Task WriteOperationsOracleAsync(
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
                promptId = OperationsPromptId,
                groupId = OperationsGroupId,
                runKey = _runKey,
                body
            },
            ct);

    private object BuildOperationsArtifactPin(string path, string purpose)
        => new
        {
            path = Path.GetRelativePath(_paths.WorkspaceRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'),
            sha256 = HashBytes(File.ReadAllBytes(path)),
            bytes = new FileInfo(path).Length,
            purpose
        };
}
