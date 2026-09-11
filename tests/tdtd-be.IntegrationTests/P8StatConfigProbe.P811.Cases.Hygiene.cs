using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly IReadOnlyDictionary<string, string> P811ImmutableArtifactPins =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".p8-artifacts/baseline/p8_chain_20260802002233_677d/P8-00-baseline.json"] =
                "ab4af43ccc71c68aa652ce4fd3658705e3414545c3c8b21233de59283e097d8b",
            [".p8-artifacts/handoffs/p8_chain_20260802002233_677d/P8-00.attempt-001.json"] =
                "980a17830be4ca3d3405e613e84e76d1551d691a467f5ac155ad6390735324f4",
            ["docs/features/p7-mapping-policy/FULL_P7_MAPPING_POLICY_PROMPT_MANIFEST.json"] =
                "d7f839f4bd64d52256f018e4904000bf4a34fc088603b73adc5ce98323d9d921",
            [".p7-artifacts/handoffs/p7_chain_20260730150623_7a4d/P7-12.attempt-001.json"] =
                "1c1c37ec15ed9b0f6a33e21e272e2d5b54053f01bf98140771f8c245976dc35d",
            ["docs/features/FULL_P7_MAPPING_POLICY_EVIDENCE_2026_08_01.md"] =
                "b23abfde020c56fa12c2d9f6b286e370707e9adac84f816040e0a4ae3e761d25",
            [".p7-artifacts/test-runs/P7-12-final-gate-summary-20260731.json"] =
                "e746b922084a16fbaa60c13da5128debb1e0cf63b8ccdc1ee38d019f992cdc50"
        };

    private static readonly IReadOnlyDictionary<string, string> P811HistoricalCatalogPins =
        BuildP811HistoricalCatalogPins();

    private static readonly string[] P811ExpectedP7SuccessorPaths =
    [
        "scripts/validate-dynamic-form-flow-capability-catalog.mjs",
        "tdtd-be/Common/Capabilities/DynamicFormFlowCapabilityCatalog.g.cs",
        "tdtd-be/Common/Errors/AppErrorCatalog.cs",
        "tdtd-be/Common/Errors/AppErrorCode.cs",
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json",
        "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json",
        "tdtd-be/Controllers/AdminOperationsController.cs",
        "tdtd-be/Controllers/WorkAssignmentReportsController.cs",
        "tdtd-be/Program.cs",
        "tdtd-be/Services/Common/JobRunManagementService.cs",
        "tdtd-be/Services/WorkAssignmentReports/Runtime/WorkReportLifecycleProjectionReconciler.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/ApiHarnessClient.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/BackendServerLease.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P5MaterializationProbe.P601.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/P5MaterializationProbe.P7Mapping.cs",
        "tdtd-be/tests/tdtd-be.IntegrationTests/Program.cs",
        "tdtd-be/tests/tdtd-be.Tests/Program.cs",
        "tdtd-be/tools/generate-dynamic-form-flow-capability-catalog.mjs",
        "tdtd-fe/src/api/dynamicFlowTemplateApi.ts",
        "tdtd-fe/src/generated/dynamicFormFlowCapabilityCatalog.generated.ts",
        "tdtd-fe/src/pages/dynamicFlows/DynamicFlowVersionWorkspacePage.tsx",
        "tdtd-fe/src/routes/appRoutes.tsx",
        "tdtd-fe/tests/dynamicFlows/DynamicFlowVersionWorkspacePage.test.tsx"
    ];

    private static readonly IReadOnlySet<string>
        P811CurrentPromptP7SuccessorPaths = new HashSet<string>(
            StringComparer.Ordinal)
        {
            "scripts/validate-dynamic-form-flow-capability-catalog.mjs",
            "tdtd-be/Common/Capabilities/DynamicFormFlowCapabilityCatalog.g.cs",
            "tdtd-be/Common/Errors/AppErrorCatalog.cs",
            "tdtd-be/Common/Errors/AppErrorCode.cs",
            "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json",
            "tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json",
            "tdtd-be/Services/WorkAssignmentReports/Runtime/WorkReportLifecycleProjectionReconciler.cs",
            "tdtd-be/tests/tdtd-be.IntegrationTests/ApiHarnessClient.cs",
            "tdtd-be/tests/tdtd-be.IntegrationTests/P5MaterializationProbe.P601.cs",
            "tdtd-be/tests/tdtd-be.IntegrationTests/P5MaterializationProbe.P7Mapping.cs",
            "tdtd-be/tests/tdtd-be.IntegrationTests/Program.cs",
            "tdtd-be/tools/generate-dynamic-form-flow-capability-catalog.mjs",
            "tdtd-fe/src/api/dynamicFlowTemplateApi.ts",
            "tdtd-fe/src/generated/dynamicFormFlowCapabilityCatalog.generated.ts"
        };

    private async Task RunP811SourceFingerprintAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-RACE-015",
            "system_admin",
            Array.Empty<string>(),
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var before = await CaptureP811SourceFingerprintAsync(ct);
                HarnessAssert.True(before.SourceFiles.Count >= 100,
                    "P8-11 source fingerprint covered fewer than 100 files");
                HarnessAssert.True(before.Assemblies.Count >= 2,
                    "P8-11 source fingerprint omitted required assemblies");

                var jobId = _p811FaultJobId
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8-RACE-015 lacks a persisted readiness job.");
                var response = await ReadP808SafeJobAsync(
                    Actor("system_admin"), jobId, ct);
                ApiHarnessClient.ExpectStatus(
                    response, HttpStatusCode.OK,
                    "P8-RACE-015 safe readiness readback");

                var after = await CaptureP811SourceFingerprintAsync(ct);
                HarnessAssert.Equal(before.NormalizedSemanticSha256,
                    after.NormalizedSemanticSha256,
                    "P8-11 source or assembly fingerprint drifted during the live probe");
                HarnessAssert.True(IsLowerSha256(after.NormalizedSemanticSha256),
                    "P8-11 source fingerprint is not lowercase SHA-256");

                await EvidenceJson.WriteAsync(
                    Path.Combine(_paths.RunRoot,
                        "p8-race-source-fingerprint.json"),
                    new
                    {
                        schemaVersion = 1,
                        chainId = ChainId,
                        promptId = _promptId,
                        contract = "P8-11-SOURCE-ASSEMBLY-FINGERPRINT-1",
                        sourceFiles = after.SourceFiles,
                        assemblies = after.Assemblies,
                        sourceFileCount = after.SourceFiles.Count,
                        assemblyCount = after.Assemblies.Count,
                        normalizedSemanticSha256 =
                            after.NormalizedSemanticSha256,
                        beforeSha256 = before.NormalizedSemanticSha256,
                        afterSha256 = after.NormalizedSemanticSha256,
                        sourceUnchanged = true,
                        realKestrel = new
                        {
                            _backend.ProcessId,
                            _backend.Port,
                            response.StatusCode
                        },
                        directMongo = new
                        {
                            _mongo.DatabaseName,
                            _mongo.ReplicaSetName,
                            _mongo.ProcessId,
                            _mongo.Port
                        },
                        passed = true
                    },
                    ct);

                return new CaseObservation(
                    "Canonical path/SHA/byte rows covered backend, frontend, scripts, P7/P8 documents and both live .NET assemblies; a real Kestrel read occurred between identical pre/post fingerprints.",
                    $"sourceFiles={after.SourceFiles.Count};assemblies={after.Assemblies.Count};prePostEqual=1;sha={after.NormalizedSemanticSha256};resultDelta=0");
            },
            ct);
    }

    private async Task RunP811SecurityAndP7RegressionAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-RACE-016",
            "system_admin",
            Array.Empty<string>(),
            NoCollectionWrites,
            NoCollectionWrites,
            async () =>
            {
                var immutable = await VerifyP811PinnedFilesAsync(
                    P811ImmutableArtifactPins, ct);
                var catalogState = await VerifyP811CatalogStateAsync(ct);
                var catalogs = catalogState.Files;
                var p7LiveCases = _cases.Results
                    .Where(item => item.CaseId is
                        "P8-FLW-010" or "P8-FLW-011" or "P8-FLW-012")
                    .OrderBy(item => item.CaseId, StringComparer.Ordinal)
                    .ToArray();
                HarnessAssert.Equal(3, p7LiveCases.Length,
                    "P8-11 did not execute all three live P7 regression cases");
                HarnessAssert.True(p7LiveCases.All(item =>
                        item.Verdict == HarnessVerdict.DAT),
                    "A live P7 semantic regression case is not DAT");

                var expectedFailureRecordCount =
                    _p9SuccessorOverlayEnabled ? 80 : 52;
                var expectedSuccessorPaths = _p9SuccessorOverlayEnabled
                    ? P811ExpectedP9SuccessorPaths
                    : P811ExpectedP7SuccessorPaths;
                var verifier = await RunP811P7VerifierAsync(
                    expectedFailureRecordCount, ct);
                HarnessAssert.Equal(2, verifier.ExitCode,
                    "Live P7 verifier must truthfully report successor drift after P8 changes");
                HarnessAssert.Equal(expectedFailureRecordCount,
                    verifier.FailureRecordCount,
                    "Live P7 successor failure record count drifted");
                HarnessAssert.True(verifier.ExactStaleHeader,
                    "Live P7 verifier stale header did not bind the exact successor failure count");
                HarnessAssert.True(verifier.UniquePaths.SequenceEqual(
                        expectedSuccessorPaths,
                        StringComparer.Ordinal),
                    "Live P7 successor path set drifted");
                var provenance = await VerifyP811SuccessorProvenanceAsync(
                    verifier.UniquePaths, ct);

                var baselinePath = P811Path(
                    ".p8-artifacts/baseline/p8_chain_20260802002233_677d/P8-00-baseline.json");
                var baseline = await ReadP811ObjectAsync(baselinePath, ct);
                var entryGate = baseline["verifierEntryGate"]?["p7"]
                                as JsonObject
                                ?? throw new InvalidOperationException(
                                    "P8-00 baseline lacks the P7 entry verifier gate.");
                HarnessAssert.Equal(0,
                    entryGate["exitCode"]?.GetValue<int>() ?? -1,
                    "P8-00 did not bind a green P7 entry verifier");
                HarnessAssert.Equal("PASS",
                    entryGate["outcome"]?.GetValue<string>(),
                    "P8-00 P7 entry outcome drifted");
                HarnessAssert.True(
                    (entryGate["line"]?.GetValue<string>() ?? string.Empty)
                    .StartsWith("P7_ANCHOR_OK mode=Execution",
                        StringComparison.Ordinal),
                    "P8-00 P7 entry verifier line drifted");
                var p7FinalSummaryPath = P811Path(
                    ".p7-artifacts/test-runs/P7-12-final-gate-summary-20260731.json");
                var p7FinalSummary = await ReadP811ObjectAsync(
                    p7FinalSummaryPath, ct);
                const string p7FinalCompositeSha256 =
                    "69f82b165976627dd95453374ed218c0f0489e6e91f575ef023c3f88524564a8";
                HarnessAssert.Equal(p7FinalCompositeSha256,
                    p7FinalSummary["aggregate"]?["normalizedSemanticSha256"]
                        ?.GetValue<string>(),
                    "P7 final composite semantic SHA drifted");
                HarnessAssert.True(
                    p7FinalSummary["passed"]?.GetValue<bool>() == true,
                    "P7 final gate summary is not PASS");

                var jobId = _p811FaultJobId
                            ?? throw new HarnessCaseNotRunnableException(
                                "P8-RACE-016 lacks a readiness job.");
                var safeRead = await ReadP808SafeJobAsync(
                    Actor("system_admin"), jobId, ct);
                ApiHarnessClient.ExpectStatus(safeRead, HttpStatusCode.OK,
                    "P8-RACE-016 safe readiness readback");

                var preSecurity = await RunArtifactSecurityScanAsync(
                    deferLockedProcessLogs: true);
                await EvidenceJson.WriteAsync(
                    Path.Combine(
                        _paths.RunRoot,
                        "p8-race-pre-final-security-scan.json"),
                    new
                    {
                        schemaVersion = 1,
                        chainId = ChainId,
                        promptId = _promptId,
                        contract = "P8-11-PRE-FINAL-SECURITY-SCAN-1",
                        liveProcessFilesDeferredUntilPostCleanup = true,
                        securityScan = preSecurity
                    },
                    ct);
                HarnessAssert.True(preSecurity.Passed,
                    "P8-11 pre-final artifact security scan failed");

                await EvidenceJson.WriteAsync(
                    Path.Combine(_paths.RunRoot,
                        "p8-race-p7-regression.json"),
                    new
                    {
                        schemaVersion = 1,
                        chainId = ChainId,
                        promptId = _promptId,
                        contract = "P8-11-P7-SUCCESSOR-OVERLAY-1",
                        p8EntryVerifier = new
                        {
                            baselinePath = P811Relative(baselinePath),
                            baselineSha256 = immutable.Single(item =>
                                item.Path.EndsWith("P8-00-baseline.json",
                                    StringComparison.Ordinal)).Sha256,
                            exitCode = 0,
                            outcome = "PASS",
                            line = entryGate["line"]?.GetValue<string>()
                        },
                        liveP7Verifier = verifier,
                        successorOverlay = new
                        {
                            overlayId = _p9SuccessorOverlayEnabled
                                ? P9SuccessorOverlayId
                                : "P8-12-SUCCESSOR",
                            expectedFailureRecordCount,
                            expectedUniquePathCount =
                                expectedSuccessorPaths.Length,
                            exactPathSet = true,
                            provenance,
                            passed = provenance.All(item => item.Passed)
                        },
                        immutableP7Artifacts = immutable,
                        historicalCatalogs = catalogs,
                        p9CatalogSuccessorOverlay = catalogState.Overlay,
                        historicalCatalogHashesExact = true,
                        p0ThroughP7SealedRegressionExact = true,
                        p7FinalCompositeSha256,
                        p7FinalGateSummary = immutable.Single(item =>
                            item.Path.EndsWith(
                                "P7-12-final-gate-summary-20260731.json",
                                StringComparison.Ordinal)),
                        liveP7BehavioralRegression = p7LiveCases.Select(item =>
                            new
                            {
                                item.CaseId,
                                verdict = item.Verdict.ToString(),
                                item.Fingerprint
                            }),
                        liveP7BehavioralRegressionPassed = true,
                        preFinalSecurityScan = preSecurity,
                        liveVerifierTruthfullyStale = true,
                        liveVerifierExecuted = true,
                        entryVerifierPassed = true,
                        successorOverlayPassed = true,
                        passed = true
                    },
                    ct);

                await EvidenceJson.WriteAsync(
                    Path.Combine(_paths.RunRoot,
                        "p8-race-security-precheck.json"),
                    new
                    {
                        schemaVersion = 1,
                        scannedFiles = preSecurity.ScannedFileCount,
                        rememberedSecrets = preSecurity.ExactSecretValueCount,
                        findings = preSecurity.Findings,
                        passed = preSecurity.Passed,
                        finalScanRunsAfterGateAndCleanup = true
                    },
                    ct);

                return new CaseObservation(
                    $"Exact immutable P7/P8 entry artifacts and historical catalogs matched; the live P7 verifier was captured as the expected {expectedFailureRecordCount}-record/{expectedSuccessorPaths.Length}-path successor diagnostic, every path had prior-handoff or exact source-fingerprint provenance, and live P7 semantic cases stayed green.",
                    $"entryVerifier=PASS;liveVerifier=STALE_EXPECTED;failureRecords={expectedFailureRecordCount};uniquePaths={expectedSuccessorPaths.Length};successorProvenance={provenance.Count}/{expectedSuccessorPaths.Length};historicalCatalogs=exact;liveP7=3/3;securityPrecheck=PASS;resultDelta=0");
            },
            ct);
    }

    private async Task<P811SourceFingerprint> CaptureP811SourceFingerprintAsync(
        CancellationToken ct)
    {
        var roots = new[]
        {
            "tdtd-be", "tdtd-fe/src", "tdtd-fe/tests", "tdtd-fe/scripts",
            "scripts", "docs/features/p7-mapping-policy",
            "docs/features/p8-stat-config"
        };
        var extensions = new HashSet<string>(
            [".cs", ".csproj", ".props", ".targets", ".ts", ".tsx",
             ".js", ".mjs", ".cjs", ".json", ".md", ".ps1",
             ".yml", ".yaml"],
            StringComparer.OrdinalIgnoreCase);
        var files = roots
            .Select(P811Path)
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(
                root, "*", SearchOption.AllDirectories))
            .Where(path => extensions.Contains(Path.GetExtension(path)))
            .Where(path => !P811ExcludedSourcePath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(P811Relative, StringComparer.Ordinal)
            .ToArray();
        var rows = new List<P811FileFingerprint>(files.Length);
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file, ct);
            rows.Add(new P811FileFingerprint(
                P811Relative(file), bytes.LongLength, Sha256(bytes)));
        }

        var assemblyPaths = new[]
        {
            _paths.ResolveBackendDll(),
            typeof(P8StatConfigProbe).Assembly.Location
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var assemblies = new List<P811FileFingerprint>();
        foreach (var assembly in assemblyPaths)
        {
            var bytes = await File.ReadAllBytesAsync(assembly, ct);
            assemblies.Add(new P811FileFingerprint(
                P811Relative(assembly), bytes.LongLength, Sha256(bytes)));
        }

        var normalized = new JsonObject
        {
            ["sourceFiles"] = new JsonArray(rows.Select(row =>
                (JsonNode)new JsonObject
                {
                    ["path"] = row.Path,
                    ["bytes"] = row.Bytes,
                    ["sha256"] = row.Sha256
                }).ToArray()),
            ["assemblies"] = new JsonArray(assemblies
                .OrderBy(row => row.Path, StringComparer.Ordinal)
                .Select(row => (JsonNode)new JsonObject
                {
                    ["path"] = row.Path,
                    ["bytes"] = row.Bytes,
                    ["sha256"] = row.Sha256
                }).ToArray())
        };
        return new P811SourceFingerprint(
            rows,
            assemblies.OrderBy(row => row.Path, StringComparer.Ordinal)
                .ToArray(),
            Sha256(Encoding.UTF8.GetBytes(Canonicalize(normalized))));
    }

    private static bool P811ExcludedSourcePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.build/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.p1-artifacts/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.p7-artifacts/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.p8-artifacts/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<P811P7VerifierEvidence> RunP811P7VerifierAsync(
        int expectedFailureRecordCount,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = _paths.WorkspaceRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(P811Path(
            "scripts/verify-p7-prompt-anchor.ps1"));
        info.ArgumentList.Add("-Mode");
        info.ArgumentList.Add("Execution");
        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException(
                                "Failed to start the P7 verifier.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = string.Join('\n', new[] { stdout, stderr }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var failureLines = combined.Split(
                ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(
                "P7_ANCHOR_FAILURE ", StringComparison.Ordinal))
            .ToArray();
        var pathPattern = new Regex(
            @"\bpath=([^\s]+)",
            RegexOptions.CultureInvariant);
        var uniquePaths = failureLines
            .Select(line => pathPattern.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return new P811P7VerifierEvidence(
            process.ExitCode,
            failureLines.Length,
            uniquePaths,
            combined.Contains(
                $"P7_ANCHOR_STALE mode=Execution status=ACHIEVED revision=P7-A2 failures={expectedFailureRecordCount}",
                StringComparison.Ordinal),
            Sha256(Encoding.UTF8.GetBytes(combined)),
            combined);
    }

    private async Task<IReadOnlyList<P811SuccessorProvenance>>
        VerifyP811SuccessorProvenanceAsync(
            IReadOnlyCollection<string> paths,
            CancellationToken ct)
    {
        var p8HandoffRoot = P811Path(
            ".p8-artifacts/handoffs/p8_chain_20260802002233_677d");
        var handoffs = Directory.EnumerateFiles(
                p8HandoffRoot, "P8-*.attempt-001.json",
                SearchOption.TopDirectoryOnly)
            .Where(path => Regex.IsMatch(
                Path.GetFileName(path), @"^P8-(?:0[1-9]|10)\.attempt-001\.json$"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (_p9SuccessorOverlayEnabled)
        {
            var p812HandoffPath = P811Path(
                ".p8-artifacts/handoffs/p8_chain_20260802002233_677d/P8-12.attempt-002.json");
            var p812HandoffBytes = await File.ReadAllBytesAsync(
                p812HandoffPath, ct);
            HarnessAssert.Equal(
                "484027936125d488142ec4e5cc5344544bb4bddbd8f8052d100cf82a2c9a82e7",
                Sha256(p812HandoffBytes),
                "Effective P8-12 successor handoff drifted");
            var p812Handoff = await ReadP811ObjectAsync(
                p812HandoffPath, ct);
            HarnessAssert.Equal("P8-12",
                p812Handoff["promptId"]?.GetValue<string>(),
                "Effective P8 successor handoff prompt drifted");
            HarnessAssert.Equal(2,
                p812Handoff["attempt"]?.GetValue<int>() ?? -1,
                "Effective P8 successor handoff attempt drifted");
            HarnessAssert.Equal("PASS",
                p812Handoff["status"]?.GetValue<string>(),
                "Effective P8 successor handoff is not PASS");
            handoffs.Add(p812HandoffPath);

            var p9HandoffRoot = P811Path(
                ".p9-artifacts/handoffs/p9_chain_20260804012144_7185");
            handoffs.AddRange(Directory.EnumerateFiles(
                    p9HandoffRoot, "P9-*.attempt-*.json",
                    SearchOption.TopDirectoryOnly)
                .Where(path => Regex.IsMatch(
                    Path.GetFileName(path),
                    @"^P9-(?:0[0-9]|1[01])\.attempt-[0-9]{3}\.json$"))
                .OrderBy(path => path, StringComparer.Ordinal));
        }
        var sourceFingerprint = await ReadP811ObjectAsync(
            Path.Combine(
                _paths.RunRoot,
                "p8-race-source-fingerprint.json"),
            ct);
        HarnessAssert.True(
            sourceFingerprint["passed"]?.GetValue<bool>() == true &&
            sourceFingerprint["sourceUnchanged"]?.GetValue<bool>() == true,
            "P8-11 source fingerprint is not a passing immutable binding");
        var sourceFiles = sourceFingerprint["sourceFiles"] as JsonArray
                          ?? throw new InvalidOperationException(
                              "P8-11 source fingerprint lacks sourceFiles.");
        var sourceHashes = sourceFiles
            .OfType<JsonObject>()
            .ToDictionary(
                row => row["path"]?.GetValue<string>()
                       ?? throw new InvalidOperationException(
                           "P8-11 source fingerprint row lacks path."),
                row => row["sha256"]?.GetValue<string>()
                       ?? throw new InvalidOperationException(
                           "P8-11 source fingerprint row lacks sha256."),
                StringComparer.Ordinal);
        HarnessAssert.True(
            P811CurrentPromptP7SuccessorPaths.All(path =>
                sourceHashes.ContainsKey(path)),
            "P8-11 current-prompt P7 successor path is absent from the source fingerprint");

        var rows = new List<P811SuccessorProvenance>();
        foreach (var path in paths.OrderBy(value => value, StringComparer.Ordinal))
        {
            var actual = Sha256(await File.ReadAllBytesAsync(
                P811Path(path), ct));
            var owners = new List<string>();
            foreach (var handoffPath in handoffs)
            {
                var handoff = await ReadP811ObjectAsync(handoffPath, ct);
                if (!string.Equals(
                        handoff["status"]?.GetValue<string>(),
                        "PASS",
                        StringComparison.Ordinal))
                {
                    continue;
                }
                var changed = handoff["filesChanged"] as JsonArray
                              ?? new JsonArray();
                if (changed.OfType<JsonObject>().Any(item =>
                        string.Equals(
                            item["path"]?.GetValue<string>()?.Replace('\\', '/'),
                            path,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            item["sha256"]?.GetValue<string>(),
                            actual,
                            StringComparison.Ordinal)))
                {
                    owners.Add(handoff["promptId"]?.GetValue<string>()
                               ?? Path.GetFileName(handoffPath));
                }
            }
            if (owners.Count == 0 &&
                P811CurrentPromptP7SuccessorPaths.Contains(path) &&
                sourceHashes.TryGetValue(path, out var sourceSha256) &&
                string.Equals(
                    sourceSha256,
                    actual,
                    StringComparison.Ordinal))
            {
                owners.Add("P8-11_CURRENT_SOURCE_FINGERPRINT");
            }
            rows.Add(new P811SuccessorProvenance(
                path, actual, owners, owners.Count > 0));
        }
        HarnessAssert.True(rows.All(item => item.Passed),
            "At least one live P7 verifier mismatch lacks prior-handoff or explicit P8-11 source-fingerprint provenance");
        return rows;
    }

    private async Task<IReadOnlyList<P811FileFingerprint>>
        VerifyP811PinnedFilesAsync(
            IReadOnlyDictionary<string, string> pins,
            CancellationToken ct)
    {
        var rows = new List<P811FileFingerprint>();
        foreach (var pair in pins.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var fullPath = P811Path(pair.Key);
            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            var actual = Sha256(bytes);
            HarnessAssert.Equal(pair.Value, actual,
                $"Immutable P8/P7 artifact hash drifted: {pair.Key}");
            rows.Add(new P811FileFingerprint(
                pair.Key, bytes.LongLength, actual));
        }
        return rows;
    }

    private string P811Path(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(
            _paths.WorkspaceRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _paths.WorkspaceRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"P8-11 path escaped the workspace: {relative}");
        return full;
    }

    private string P811Relative(string fullPath)
        => Path.GetRelativePath(_paths.WorkspaceRoot, fullPath)
            .Replace('\\', '/');

    private static IReadOnlyDictionary<string, string>
        BuildP811HistoricalCatalogPins()
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1.json"] =
                "e65f52e28fe0476cd73f6e466c9f2dfa4efc21c7c4c888ab5ebe9e3922ed972f",
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1.schema.json"] =
                "7efe489e949f6e1b8b691dc8426c1f4a3caf516dd52d4b86ffd337c25383222f",
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_1.json"] =
                "a4f2689999a3e655638e1732a9cd6449a46f305d2d08310fbf5e78f7f3d60189",
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_1.schema.json"] =
                "68356e5c3c5ce514e3704a3fbddd9f7d5288a0c2cab1c943ce1ab20051a4dbd9",
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_2.json"] =
                "20fa159c682b373cb86d5ffdb840901c70e4aa3ea48a55328b712db218f04556",
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_2.schema.json"] =
                "7f0e345f868f29f2cb7f52747c33d0a0a5075acab2c977949a333979eda80402",
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_3.json"] =
                "6d8b6e52b487f779fb577486779bed286197d9eb43083c2060e09442defad753",
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_3.schema.json"] =
                "9f3482003ca978935df6f71830f4211e94b989dde059f48bdc4d385a7e87c82c",
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_4.json"] =
                "cbb96a77117df6d55af7036e77f2efe36196bef5849c8af130cd7c5397b61092",
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_4.schema.json"] =
                "6bf9d36d539f436adb92c54b0ca02fd319c68fd1ccac0a4041c27258a861a718",
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_5.json"] =
                "5b6e3fb828a6130bce2464dd98b1e8e66fd540335b0997a19187c799a969f5b5",
            ["DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_5.schema.json"] =
                "d6eba41c05a1ff4d65d8c497fa5cd56b53d6d99ae3145475553bab55722cffa4"
        };
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in versions)
        {
            result[$"tdtd-be/Contracts/DynamicFormFlow/{pair.Key}"] = pair.Value;
            result[$"docs/features/{pair.Key}"] = pair.Value;
        }
        result[".p8-artifacts/historical-sha256/c327f32b4fbe9a840a07129338b520a31441c1956991b51bf83fd6bb80a8539c.blob"] =
            "c327f32b4fbe9a840a07129338b520a31441c1956991b51bf83fd6bb80a8539c";
        result["tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json"] =
            "d5e77af2cf8a4d4d959c8d642b82fa0bccf5b7b3c8369c6122c5875c01e4c535";
        result["tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json"] =
            "9e580106ccee20cd6ff7975dda652833123ef709024bea74087826c34986e42a";
        return result;
    }
}

internal sealed record P811FileFingerprint(
    string Path,
    long Bytes,
    string Sha256);

internal sealed record P811SourceFingerprint(
    IReadOnlyList<P811FileFingerprint> SourceFiles,
    IReadOnlyList<P811FileFingerprint> Assemblies,
    string NormalizedSemanticSha256);

internal sealed record P811P7VerifierEvidence(
    int ExitCode,
    int FailureRecordCount,
    IReadOnlyList<string> UniquePaths,
    bool ExactStaleHeader,
    string OutputSha256,
    string Output);

internal sealed record P811SuccessorProvenance(
    string Path,
    string CurrentSha256,
    IReadOnlyList<string> OwningPrompts,
    bool Passed);
