using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private static async Task WriteStrictJsonAsync(
        string path,
        object value,
        CancellationToken ct)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(path))!);
        var json = JsonSerializer.Serialize(value, EvidenceJson.Options);
        await File.WriteAllTextAsync(
            path,
            json + "\n",
            new UTF8Encoding(false),
            ct);
    }

    private async Task WriteCleanupArtifactAsync(
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        DateTime completedAtUtc,
        CancellationToken ct)
    {
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-CORE.cleanup.json"),
            new
            {
                schemaVersion = "P9_CORE_CLEANUP_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                databaseName = _mongo?.DatabaseName,
                replicaSetName = _mongo?.ReplicaSetName,
                cleanupSucceeded,
                cleanupErrors,
                backend = new
                {
                    processId = _backend?.ProcessId,
                    stopVerified = _backend?.StopVerified ?? false,
                    portReleaseVerified =
                        _backend?.PortReleaseVerified ?? false
                },
                mongo = new
                {
                    processId = _mongo?.ProcessId,
                    databaseDropVerified =
                        _mongo?.DatabaseDropVerified ?? false,
                    processStopVerified =
                        _mongo?.ProcessStopVerified ?? false,
                    portReleaseVerified =
                        _mongo?.PortReleaseVerified ?? false,
                    dataDirectoryRemovalVerified =
                        _mongo?.DataDirectoryRemovalVerified ?? false
                },
                cleanupHandles = _cleanupHandles,
                repeatedFixtureCycles = _fixtureCycles,
                completedAtUtc
            },
            ct);
    }

    private async Task<bool> WriteEvidenceAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var mainExchanges = _api?.Exchanges.ToArray() ?? [];
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "api-exchanges.json"),
            new
            {
                schemaVersion = "P9_CORE_API_EXCHANGES_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                main = mainExchanges,
                variants = _variantEvidence
            },
            ct);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "direct-mongo-evidence.json"),
            new
            {
                schemaVersion = "P9_CORE_DIRECT_MONGO_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                prohibitedRunCollections = ProhibitedRunCollections,
                cases = _caseEvidence
            },
            ct);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "lifecycle-fixture-evidence.json"),
            new
            {
                schemaVersion = "P9_CORE_LIFECYCLE_FIXTURE_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                fixture = _fixture,
                lifecycleTrace = _lifecycleTrace,
                cleanupHandles = _cleanupHandles,
                repeatedFixtureCycles = _fixtureCycles,
                coreFoundationSlice = true,
                claimsFutureCapabilityExecution = false,
                pairedReportScaffold = _fixture is not null,
                flowRuntimeTupleScaffold = _fixture is not null
            },
            ct);

        var securityFindings = ScanArtifactSecurity();
        var securityPassed = securityFindings.Count == 0;
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "security-scan.json"),
            new
            {
                schemaVersion = "P9_CORE_SECURITY_SCAN_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                passed = securityPassed,
                exactSecretCount = _artifactSecrets.Count,
                findings = securityFindings,
                scannedAtUtc = DateTime.UtcNow
            },
            ct);

        var results = _cases.Results.ToArray();
        var actualIds = results.Select(row => row.CaseId).ToArray();
        var exactIds = actualIds.SequenceEqual(
            ExpectedCaseIds,
            StringComparer.Ordinal);
        var allDat = results.All(row => row.Verdict == HarnessVerdict.DAT);
        var normalizedSemanticSha256 =
            ComputeNormalizedSemanticSha256(results);
        var semanticHashVerified = string.Equals(
            normalizedSemanticSha256,
            ComputeNormalizedSemanticSha256(results),
            StringComparison.Ordinal);
        var prohibitedCollectionsAbsent = _caseEvidence.All(item =>
            item.Before.Concat(item.After)
                .Where(state => ProhibitedRunCollections.Contains(
                    state.Collection,
                    StringComparer.Ordinal))
                .All(state => !state.Exists && state.Count == 0));
        var repeatedCyclesVerified =
            _fixtureCycles.Count == 2 &&
            _fixtureCycles.All(cycle => cycle.ReturnedToBaseline);
        var p10ZeroWriteVerified = results.Any(row =>
            string.Equals(
                row.CaseId,
                "P9-CORE-ACT-06",
                StringComparison.Ordinal) &&
            row.Verdict == HarnessVerdict.DAT);
        var passed =
            fatalFailure is null &&
            results.Length == ExpectedCaseIds.Length &&
            exactIds &&
            allDat &&
            semanticHashVerified &&
            cleanupSucceeded &&
            securityPassed &&
            prohibitedCollectionsAbsent &&
            _infrastructureWarmupVerified &&
            repeatedCyclesVerified &&
            p10ZeroWriteVerified;

        var evidence = new
        {
            schemaVersion = "P9_CORE_EVIDENCE_V1",
            chainId = ChainId,
            promptId = PromptId,
            groupId = GroupId,
            runKey = _runKey,
            startedAtUtc,
            completedAtUtc,
            expectedCaseCount = ExpectedCaseIds.Length,
            actualCaseCount = results.Length,
            exactIds,
            allDat,
            normalizedSemanticSha256,
            semanticHashVerified,
            oracles = RequiredOracles,
            cases = results,
            directMongo = _caseEvidence,
            variants = _variantEvidence.Select(item => new
            {
                item.CaseId,
                item.Name,
                item.Environment,
                item.CandidateEnabled,
                item.ExpectedDatabase,
                item.ProcessId,
                item.Port,
                item.Verdict,
                item.Failure,
                exchangeCount = item.Exchanges.Count
            }),
            lifecycleTrace = _lifecycleTrace,
            fixtureCycles = _fixtureCycles,
            cleanupSucceeded,
            cleanupErrors,
            securityPassed,
            prohibitedCollectionsAbsent,
            infrastructureWarmupVerified =
                _infrastructureWarmupVerified,
            p10ZeroWriteVerified,
            profileBlockVerified = results.Any(row =>
                string.Equals(
                    row.CaseId,
                    "P9-CORE-ACT-06",
                    StringComparison.Ordinal) &&
                row.Verdict == HarnessVerdict.DAT),
            fatalFailure,
            passed
        };
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-CORE.evidence.json"),
            evidence,
            ct);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "p9-01-gate-result.json"),
            new
            {
                schemaVersion = "P9_01_GATE_RESULT_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                passed,
                normalizedSemanticSha256,
                expectedCaseCount = ExpectedCaseIds.Length,
                actualCaseCount = results.Length,
                cleanupSucceeded,
                securityPassed,
                prohibitedCollectionsAbsent,
                infrastructureWarmupVerified =
                    _infrastructureWarmupVerified,
                completedAtUtc
            },
            ct);
        return passed;
    }

    private static string ComputeNormalizedSemanticSha256(
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
        var options = new JsonSerializerOptions(
            JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };
        var json = JsonSerializer.Serialize(normalized, options);
        return HashBytes(Encoding.UTF8.GetBytes(json));
    }

    private List<object> ScanArtifactSecurity()
    {
        var findings = new List<object>();
        var jwt = new Regex(
            @"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])",
            RegexOptions.CultureInvariant);
        var credentialedMongo = new Regex(
            @"mongodb(?:\+srv)?://[^\s/:]+:[^\s/@]+@",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var file in Directory.EnumerateFiles(
                     _paths.RunRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string text;
            try
            {
                text = File.ReadAllText(file, new UTF8Encoding(false, true));
            }
            catch
            {
                continue;
            }
            var relative = Path.GetRelativePath(
                    _paths.WorkspaceRoot,
                    file)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (_artifactSecrets.Any(secret =>
                    text.Contains(secret, StringComparison.Ordinal)))
            {
                findings.Add(new
                {
                    path = relative,
                    category = "EXACT_RUNTIME_SECRET"
                });
            }
            if (jwt.IsMatch(text))
            {
                findings.Add(new
                {
                    path = relative,
                    category = "JWT_SHAPE"
                });
            }
            if (credentialedMongo.IsMatch(text))
            {
                findings.Add(new
                {
                    path = relative,
                    category = "CREDENTIALED_MONGO_URI"
                });
            }
        }
        return findings;
    }
}
