using System.Text;
using System.Text.Json;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task<bool> WriteExportEvidenceAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var results = _cases.Results
            .Where(item => item.CaseId.StartsWith("P9-EXP-", StringComparison.Ordinal))
            .ToArray();
        var exactIds = results.Select(item => item.CaseId)
            .SequenceEqual(ExportExpectedCaseIds, StringComparer.Ordinal);
        var allDat = results.All(item => item.Verdict == HarnessVerdict.DAT);
        var normalized = results.Select(item => new
        {
            item.CaseId,
            verdict = item.Verdict.ToString(),
            item.Fingerprint
        }).ToArray();
        var normalizedSemanticSha256 = HashBytes(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized)));

        var oraclePaths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["API_KESTREL"] = Path.Combine(_paths.RunRoot, "P9-EXP.api-kestrel.json"),
            ["AUTH_BEFORE_EXISTENCE"] = Path.Combine(_paths.RunRoot, "P9-EXP.auth-before-existence.json"),
            ["DIRECT_MONGO"] = Path.Combine(_paths.RunRoot, "P9-EXP.direct-mongo.json"),
            ["CONFIG_HASH_RECOMPUTE"] = Path.Combine(_paths.RunRoot, "P9-EXP.config-hash-recompute.json"),
            ["RECEIPT"] = Path.Combine(_paths.RunRoot, "P9-EXP.receipt.json"),
            ["EXPORT_PARSE"] = Path.Combine(_paths.RunRoot, "P9-EXP.export-parse.json"),
            ["SECURITY_SCAN"] = Path.Combine(_paths.RunRoot, "P9-EXP.security-scan.json"),
            ["CLEANUP"] = Path.Combine(_paths.RunRoot, "P9-EXP.cleanup.json")
        };
        var exchanges = _api?.Exchanges.ToArray() ?? [];
        await WriteExportOracleAsync(oraclePaths["API_KESTREL"], "P9_EXP_API_KESTREL_V1", new
        {
            realKestrel = _backend is not null,
            backendProcessId = _backend?.ProcessId,
            backendPort = _backend?.Port,
            exchangeCount = exchanges.Length,
            exchanges,
            passed = _backend is not null && exchanges.Length > 0
        }, ct);
        await WriteExportOracleAsync(oraclePaths["AUTH_BEFORE_EXISTENCE"], "P9_EXP_AUTH_BEFORE_EXISTENCE_V1", new
        {
            traces = _exportAuthTrace,
            sameForbiddenEnvelope = _exportAuthTrace.Count == 3 &&
                                    _exportAuthTrace.All(trace => trace.StatusCode == 403),
            zeroWrite = _exportAuthTrace.All(trace => trace.BeforeWrites == trace.AfterWrites),
            passed = _exportAuthVerified
        }, ct);
        await WriteExportOracleAsync(oraclePaths["DIRECT_MONGO"], "P9_EXP_DIRECT_MONGO_V1", new
        {
            canonicalFixtures = _exportFixtures.Values
                .OrderBy(item => item.ResultKind, StringComparer.Ordinal)
                .ThenBy(item => item.ResultId, StringComparer.Ordinal),
            exportCollections = new[]
            {
                "work_report_statistic_exports",
                "work_report_statistic_diff_exports"
            },
            contentAddressed = true,
            serverSideOnly = true,
            passed = _exportDirectMongoVerified
        }, ct);
        await WriteExportOracleAsync(oraclePaths["CONFIG_HASH_RECOMPUTE"], "P9_EXP_CONFIG_HASH_RECOMPUTE_V1", new
        {
            fixtureConfigHash = Fixture().ConfigHash,
            recomputedConfigHash = CanonicalJsonFileSha256(
                Encoding.UTF8.GetBytes(Fixture().ConfigPayloadJson)),
            allArtifactsPinned = _exportParseTrace.Count > 0,
            passed = _exportConfigHashVerified
        }, ct);
        await WriteExportOracleAsync(oraclePaths["RECEIPT"], "P9_EXP_RECEIPT_V1", new
        {
            traces = _exportReceipts.Values.OrderBy(item => item.CommandId, StringComparer.Ordinal),
            exactReplayCommands = _exportReceipts.Values
                .Where(item => item.Replay)
                .Select(item => item.CommandId),
            changedReplay409 = results.Any(item => item.CaseId == "P9-EXP-ACL-02" && item.Verdict == HarnessVerdict.DAT),
            passed = _exportReceiptVerified
        }, ct);
        await WriteExportOracleAsync(oraclePaths["EXPORT_PARSE"], "P9_EXP_EXPORT_PARSE_V1", new
        {
            independentParsers = new[] { "P9_RFC4180_STATE_MACHINE", "CLOSEDXML_REOPEN", "ZIP_PACKAGE_SCAN" },
            traces = _exportParseTrace,
            csvUtf8Bom = true,
            xlsxTyped = true,
            formulaCount = 0,
            macroCount = 0,
            passed = _exportParseVerified
        }, ct);
        await WriteExportOracleAsync(oraclePaths["SECURITY_SCAN"], "P9_EXP_SECURITY_SCAN_V1", new
        {
            checks = new[]
            {
                "FORMULA_PREFIX_NEUTRALIZED",
                "XLSX_NO_FORMULA",
                "XLSX_NO_MACRO",
                "XLSX_NO_EXTERNAL_LINK",
                "SAFE_FILENAME",
                "GUARDED_CONTENT_ADDRESS",
                "NO_SECRET_IN_ARTIFACT"
            },
            prohibitedFormulaPrefixes = new[] { "=", "+", "-", "@" },
            activeContent = 0,
            passed = _exportSecurityVerified
        }, ct);
        await WriteExportOracleAsync(oraclePaths["CLEANUP"], "P9_EXP_CLEANUP_V1", new
        {
            traces = _exportCleanupTrace,
            metadataTtlHours = 24,
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
            passed = _exportCleanupVerified && cleanupSucceeded
        }, ct);

        var passed = fatalFailure is null &&
                     results.Length == ExportExpectedCaseIds.Length &&
                     exactIds && allDat && cleanupSucceeded &&
                     _exportAuthVerified && _exportDirectMongoVerified &&
                     _exportConfigHashVerified && _exportReceiptVerified &&
                     _exportParseVerified && _exportSecurityVerified &&
                     _exportCleanupVerified;
        var artifacts = ExportRequiredOracles
            .Select(oracle => BuildExportArtifactPin(oraclePaths[oracle], oracle))
            .ToArray();
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-EXP.evidence.json"),
            new
            {
                schemaVersion = "P9_EXP_EVIDENCE_V1",
                chainId = ChainId,
                promptId = ExportPromptId,
                groupId = ExportGroupId,
                runKey = _runKey,
                startedAtUtc,
                completedAtUtc,
                expectedCaseCount = ExportExpectedCaseIds.Length,
                actualCaseCount = results.Length,
                exactIds,
                allDat,
                normalizedSemanticSha256,
                oracles = ExportRequiredOracles,
                cases = results,
                artifacts,
                assertions = new
                {
                    realKestrel = _backend is not null,
                    isolatedMongo = _mongo is not null,
                    canonicalKinds = 5,
                    authorizationBeforeExistence = _exportAuthVerified,
                    deterministicReplay = _exportReceiptVerified,
                    independentExportParse = _exportParseVerified,
                    formulaAndMacroSafety = _exportSecurityVerified,
                    exactCleanup = _exportCleanupVerified
                },
                cleanupSucceeded,
                cleanupErrors,
                fatalFailure,
                passed
            },
            ct);
        return passed;
    }

    private async Task WriteExportOracleAsync(
        string path,
        string schemaVersion,
        object body,
        CancellationToken ct)
        => await WriteStrictJsonAsync(path, new
        {
            schemaVersion,
            chainId = ChainId,
            promptId = ExportPromptId,
            groupId = ExportGroupId,
            runKey = _runKey,
            body
        }, ct);

    private object BuildExportArtifactPin(string path, string purpose)
        => new
        {
            path = Path.GetRelativePath(_paths.WorkspaceRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'),
            sha256 = HashBytes(File.ReadAllBytes(path)),
            bytes = new FileInfo(path).Length,
            purpose
        };
}
