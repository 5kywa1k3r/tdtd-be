using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task WriteDirectCleanupArtifactAsync(
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        DateTime completedAtUtc,
        CancellationToken ct)
    {
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-DIR.cleanup.json"),
            new
            {
                schemaVersion = "P9_DIR_CLEANUP_V1",
                chainId = ChainId,
                promptId = DirectPromptId,
                groupId = DirectGroupId,
                runKey = _runKey,
                databaseName = _mongo?.DatabaseName,
                replicaSetName = _mongo?.ReplicaSetName,
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
                completedAtUtc,
                passed = cleanupSucceeded
            },
            ct);
    }

    private async Task<bool> WriteDirectEvidenceAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var apiPath = Path.Combine(_paths.RunRoot, "P9-DIR.api-exchanges.json");
        var mongoPath = Path.Combine(_paths.RunRoot, "P9-DIR.collection-delta.json");
        var indexPath = Path.Combine(_paths.RunRoot, "P9-DIR.index-explain.json");
        var securityPath = Path.Combine(_paths.RunRoot, "P9-DIR.security-scan.json");
        var cleanupPath = Path.Combine(_paths.RunRoot, "P9-DIR.cleanup.json");
        var mainPath = Path.Combine(_paths.RunRoot, "P9-DIR.evidence.json");

        var directResults = _cases.Results
            .Where(item => item.CaseId.StartsWith("P9-DIR-", StringComparison.Ordinal))
            .ToArray();
        var lifecycleResults = _cases.Results
            .Where(item => item.CaseId.StartsWith("P9-LFC-", StringComparison.Ordinal))
            .ToArray();

        await WriteStrictJsonAsync(
            apiPath,
            new
            {
                schemaVersion = "P9_DIR_API_EXCHANGES_V1",
                chainId = ChainId,
                promptId = DirectPromptId,
                groupId = DirectGroupId,
                runKey = _runKey,
                exchanges = _api?.Exchanges ?? [],
                directCaseExchangeCount = (_api?.Exchanges ?? [])
                    .Count(item => item.CaseId?.StartsWith("P9-DIR-", StringComparison.Ordinal) == true)
            },
            ct);

        var beforeHash = _dirBefore is null ? null : SnapshotSha256(_dirBefore);
        var afterHash = _dirAfter is null ? null : SnapshotSha256(_dirAfter);
        var deltas = _dirBefore is null || _dirAfter is null
            ? Array.Empty<P9CollectionDelta>()
            : BuildDeltas(_dirBefore, _dirAfter).ToArray();
        var zeroWriteVerified = beforeHash is not null &&
                                string.Equals(beforeHash, afterHash, StringComparison.Ordinal) &&
                                deltas.All(item => !item.Changed && item.CountDelta == 0);
        await WriteStrictJsonAsync(
            mongoPath,
            new
            {
                schemaVersion = "P9_DIR_COLLECTION_DELTA_V1",
                chainId = ChainId,
                promptId = DirectPromptId,
                groupId = DirectGroupId,
                runKey = _runKey,
                beforeSha256 = beforeHash,
                afterSha256 = afterHash,
                before = _dirBefore?.Values.OrderBy(item => item.Collection, StringComparer.Ordinal),
                after = _dirAfter?.Values.OrderBy(item => item.Collection, StringComparer.Ordinal),
                deltas,
                zeroWriteVerified,
                passed = zeroWriteVerified
            },
            ct);

        var indexExplainVerified = _lfcOwnerIndexEvidence?.ExplainIndexesVerified == true &&
                                   _lfcOwnerIndexEvidence.QueryIndexesVerified &&
                                   _lfcOwnerIndexEvidence.UniqueIndexesVerified;
        await WriteStrictJsonAsync(
            indexPath,
            new
            {
                schemaVersion = "P9_DIR_INDEX_EXPLAIN_V1",
                chainId = ChainId,
                promptId = DirectPromptId,
                groupId = DirectGroupId,
                runKey = _runKey,
                frozenBudget = "max(100,5*returnedRows)",
                blockingSortForbidden = true,
                ownerEvidence = _lfcOwnerIndexEvidence,
                passed = indexExplainVerified
            },
            ct);

        var securityFindings = ScanArtifactSecurity();
        var securityPassed = securityFindings.Count == 0;
        await WriteStrictJsonAsync(
            securityPath,
            new
            {
                schemaVersion = "P9_DIR_SECURITY_SCAN_V1",
                chainId = ChainId,
                promptId = DirectPromptId,
                groupId = DirectGroupId,
                runKey = _runKey,
                passed = securityPassed,
                exactSecretCount = _artifactSecrets.Count,
                findings = securityFindings,
                scannedAtUtc = DateTime.UtcNow
            },
            ct);

        var actualIds = directResults.Select(item => item.CaseId).ToArray();
        var exactIds = actualIds.SequenceEqual(DirectExpectedCaseIds, StringComparer.Ordinal);
        var allDat = directResults.All(item => item.Verdict == HarnessVerdict.DAT);
        var normalizedSemanticSha256 =
            ComputeDirectNormalizedSemanticSha256(directResults);
        var configHashRecomputed = _fixture is not null &&
                                   string.Equals(
                                       Fixture().ConfigHash,
                                       CanonicalJsonFileSha256(Encoding.UTF8.GetBytes(Fixture().ConfigPayloadJson)),
                                       StringComparison.Ordinal);
        var projectionCaseIds = LifecycleExpectedCaseIds.Take(12).ToArray();
        var lifecycleProjectionVerified = lifecycleResults.Length == projectionCaseIds.Length &&
                                          lifecycleResults.Select(item => item.CaseId).SequenceEqual(
                                              projectionCaseIds,
                                              StringComparer.Ordinal) &&
                                          lifecycleResults.All(item => item.Verdict == HarnessVerdict.DAT) &&
                                          _lfcOwnerIndexEvidence?.Passed == true &&
                                          !string.IsNullOrWhiteSpace(_lfcGenerationId);
        var passed = fatalFailure is null &&
                     directResults.Length == DirectExpectedCaseIds.Length &&
                     exactIds &&
                     allDat &&
                     cleanupSucceeded &&
                     zeroWriteVerified &&
                     configHashRecomputed &&
                     indexExplainVerified &&
                     securityPassed &&
                     lifecycleProjectionVerified;

        var artifacts = new[]
        {
            BuildDirectArtifactPin(apiPath, "API_KESTREL"),
            BuildDirectArtifactPin(mongoPath, "COLLECTION_DELTA"),
            BuildDirectArtifactPin(indexPath, "INDEX_EXPLAIN"),
            BuildDirectArtifactPin(securityPath, "SECURITY_SCAN"),
            BuildDirectArtifactPin(cleanupPath, "CLEANUP")
        };
        await WriteStrictJsonAsync(
            mainPath,
            new
            {
                schemaVersion = "P9_DIR_EVIDENCE_V1",
                chainId = ChainId,
                promptId = DirectPromptId,
                groupId = DirectGroupId,
                runKey = _runKey,
                startedAtUtc,
                completedAtUtc,
                expectedCaseCount = DirectExpectedCaseIds.Length,
                actualCaseCount = directResults.Length,
                exactIds,
                allDat,
                normalizedSemanticSha256,
                semanticHashVerified = string.Equals(
                    normalizedSemanticSha256,
                    ComputeDirectNormalizedSemanticSha256(directResults),
                    StringComparison.Ordinal),
                oracles = DirectRequiredOracles,
                cases = directResults,
                artifacts,
                projection = new
                {
                    runId = _lfcRunId,
                    generationId = _lfcGenerationId,
                    generationHash = _lfcGenerationHash,
                    ownerPrompt = LifecyclePromptId,
                    ownerStageLockSha256 = LifecycleStageLockSha256
                },
                candidate = new
                {
                    stage = 1,
                    stageLockSha256 = DirectStageLockSha256,
                    catalogRawSha256 = DirectCatalogRawSha256,
                    catalogSemanticSha256 = DirectCatalogSemanticSha256,
                    promotions = new[] { "DIRECT_FIELD_TABLE_LABEL" }
                },
                assertions = new
                {
                    realKestrel = _backend is not null,
                    isolatedMongo = _mongo is not null,
                    authorizationBeforeExistence = directResults.Any(item => item.CaseId == "P9-DIR-FIELD-06" && item.Verdict == HarnessVerdict.DAT),
                    fullFilterPaging = directResults.Where(item => item.CaseId.StartsWith("P9-DIR-PAGE-", StringComparison.Ordinal)).Count() == 12,
                    typedDrilldown = directResults.Any(item => item.CaseId == "P9-DIR-TABLE-04" && item.Verdict == HarnessVerdict.DAT),
                    labelLayersDistinct = directResults.Any(item => item.CaseId == "P9-DIR-LABEL-02" && item.Verdict == HarnessVerdict.DAT),
                    staleDoesNotReturnRows = new[] { "P9-DIR-FIELD-08", "P9-DIR-TABLE-08", "P9-DIR-LABEL-08" }
                        .All(id => directResults.Any(item => item.CaseId == id && item.Verdict == HarnessVerdict.DAT)),
                    configHashRecomputed,
                    zeroWriteVerified,
                    indexExplainVerified,
                    securityPassed,
                    lifecycleProjectionVerified
                },
                cleanupSucceeded,
                cleanupErrors,
                fatalFailure,
                passed
            },
            ct);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "p9-03-gate-result.json"),
            new
            {
                schemaVersion = "P9_03_GATE_RESULT_V1",
                chainId = ChainId,
                promptId = DirectPromptId,
                groupId = DirectGroupId,
                runKey = _runKey,
                passed,
                expectedCaseCount = DirectExpectedCaseIds.Length,
                actualCaseCount = directResults.Length,
                normalizedSemanticSha256,
                cleanupSucceeded,
                completedAtUtc
            },
            ct);
        return passed;
    }

    private object BuildDirectArtifactPin(string path, string purpose)
        => new
        {
            path = Path.GetRelativePath(_paths.WorkspaceRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'),
            sha256 = HashBytes(File.ReadAllBytes(path)),
            bytes = new FileInfo(path).Length,
            purpose
        };

    private static string ComputeDirectNormalizedSemanticSha256(
        IReadOnlyCollection<HarnessCaseResult> results)
    {
        var normalized = results
            .OrderBy(item => item.CaseId, StringComparer.Ordinal)
            .Select(item => new
            {
                caseId = item.CaseId,
                fingerprint = item.Fingerprint,
                verdict = item.Verdict.ToString()
            })
            .ToArray();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };
        return HashBytes(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(normalized, options)));
    }
}
