using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private P9CloseBrowserSliceEvidence ValidateCloseoutBrowserSlice(
        P9CloseBrowserSliceRequest request,
        string resultPath,
        string beforeDatabaseSha256,
        string afterDatabaseSha256,
        bool zeroWrite)
    {
        if (!File.Exists(resultPath))
            throw new InvalidOperationException(
                $"P9-CLOSE browser result is missing: {resultPath}");
        using var document = JsonDocument.Parse(File.ReadAllBytes(resultPath));
        var root = document.RootElement;
        HarnessAssert.Equal(
            "P9_CLOSE_BROWSER_RESULT_V1",
            root.GetProperty("schemaVersion").GetString(),
            $"P9-CLOSE {request.SliceId} browser schema");
        HarnessAssert.Equal(
            "PASS",
            root.GetProperty("verdict").GetString(),
            $"P9-CLOSE {request.SliceId} verdict");
        HarnessAssert.Equal(
            0,
            root.GetProperty("networkMockCount").GetInt32(),
            $"P9-CLOSE {request.SliceId} network mocks");
        var noMockProof = root.GetProperty("noMockProof");
        HarnessAssert.True(
            noMockProof.GetProperty("serviceWorkerPolicy").GetString() == "block" &&
            noMockProof.GetProperty("serviceWorkerEventCount").GetInt32() == 0 &&
            noMockProof.GetProperty("serviceWorkerUrls").GetArrayLength() == 0 &&
            noMockProof.GetProperty("interceptionRegistrationCount").GetInt32() == 0 &&
            noMockProof.GetProperty("networkIdentityMismatchCount").GetInt32() == 0 &&
            noMockProof.GetProperty("exactApiIdentityMultiset").GetBoolean() &&
            noMockProof.GetProperty("browserApiRequestCount").GetInt32() ==
                root.GetProperty("apiRequestCount").GetInt32() &&
            noMockProof.GetProperty("proxyApiRequestCount").GetInt32() ==
                root.GetProperty("proxyRequestCount").GetInt32(),
            $"P9-CLOSE {request.SliceId} independently derived no-mock proof");
        var selected = root.GetProperty("selectedCaseIds")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        HarnessAssert.True(
            selected.SequenceEqual(new[] { request.CaseId }, StringComparer.Ordinal),
            $"P9-CLOSE {request.SliceId} selected case registry");
        var exactRegistry = root.GetProperty("exactCaseIds")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        HarnessAssert.True(
            exactRegistry.SequenceEqual(CloseoutBrowserCaseIds,
                StringComparer.Ordinal),
            $"P9-CLOSE {request.SliceId} exact case registry");
        HarnessAssert.True(
            root.GetProperty("exactIds").GetBoolean(),
            $"P9-CLOSE {request.SliceId} exact IDs");
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        HarnessAssert.True(
            cases.Length == 1 &&
            cases[0].GetProperty("caseId").GetString() == request.CaseId &&
            cases[0].GetProperty("verdict").GetString() == "DAT",
            $"P9-CLOSE {request.SliceId} case verdict");
        var artifacts = root.GetProperty("artifacts");
        var trace = artifacts.GetProperty("trace");
        var network = artifacts.GetProperty("network");
        var tracePath = trace.GetProperty("path").GetString()
            ?? throw new InvalidOperationException("P9-CLOSE trace path missing.");
        var networkPath = network.GetProperty("path").GetString()
            ?? throw new InvalidOperationException("P9-CLOSE network path missing.");
        RequireCloseoutArtifactPin(
            tracePath,
            trace.GetProperty("sha256").GetString(),
            "browser trace");
        RequireCloseoutArtifactPin(
            networkPath,
            network.GetProperty("sha256").GetString(),
            "browser network");
        foreach (var item in artifacts.GetProperty("screenshots").EnumerateArray())
        {
            RequireCloseoutArtifactPin(
                item.GetProperty("path").GetString()!,
                item.GetProperty("sha256").GetString(),
                "browser screenshot");
        }
        foreach (var item in artifacts.GetProperty("downloads").EnumerateArray())
        {
            RequireCloseoutArtifactPin(
                item.GetProperty("path").GetString()!,
                item.GetProperty("sha256").GetString(),
                "browser download");
        }
        var browser = root.GetProperty("environment").GetProperty("browser");
        var executablePath = browser.GetProperty("executablePath").GetString()!;
        RequireCloseoutArtifactPin(
            executablePath,
            browser.GetProperty("executableSha256").GetString(),
            "installed Chrome executable");
        HarnessAssert.True(
            root.GetProperty("environment").GetProperty("productionBuild")
                .GetBoolean() &&
            !string.IsNullOrWhiteSpace(browser.GetProperty("version").GetString()) &&
            root.GetProperty("apiRequestCount").GetInt32() > 0 &&
            root.GetProperty("proxyRequestCount").GetInt32() > 0,
            $"P9-CLOSE {request.SliceId} production browser/network fingerprint");
        var browserErrors = root.GetProperty("browserErrors");
        HarnessAssert.True(
            browserErrors.GetProperty("console").GetArrayLength() == 0 &&
            browserErrors.GetProperty("page").GetArrayLength() == 0 &&
            browserErrors.GetProperty("network").GetArrayLength() == 0,
            $"P9-CLOSE {request.SliceId} browser errors");
        var security = root.GetProperty("security");
        HarnessAssert.True(
            security.GetProperty("customRedactedTrace").GetBoolean() &&
            !security.GetProperty("nativeTracePersisted").GetBoolean() &&
            security.GetProperty("secretsAbsent").GetBoolean(),
            $"P9-CLOSE {request.SliceId} trace security");
        var cleanup = root.GetProperty("cleanup");
        HarnessAssert.True(
            cleanup.GetProperty("browserClosed").GetBoolean() &&
            cleanup.GetProperty("frontendListenerClosed").GetBoolean() &&
            cleanup.GetProperty("frontendPortReleased").GetBoolean(),
            $"P9-CLOSE {request.SliceId} browser cleanup");
        var screenshots = artifacts.GetProperty("screenshots").GetArrayLength();
        var downloads = artifacts.GetProperty("downloads").GetArrayLength();
        HarnessAssert.True(screenshots >= 1,
            $"P9-CLOSE {request.SliceId} screenshot missing");
        HarnessAssert.Equal(
            request.CaseId == CloseoutBrowserCaseIds[2] ? 2 : 0,
            downloads,
            $"P9-CLOSE {request.SliceId} download count");
        return new P9CloseBrowserSliceEvidence(
            request.SliceId,
            request.CaseId,
            Path.GetFullPath(resultPath),
            HashBytes(File.ReadAllBytes(resultPath)),
            Path.GetFullPath(tracePath),
            trace.GetProperty("sha256").GetString()!,
            Path.GetFullPath(networkPath),
            network.GetProperty("sha256").GetString()!,
            root.GetProperty("apiRequestCount").GetInt32(),
            root.GetProperty("proxyRequestCount").GetInt32(),
            root.GetProperty("networkMockCount").GetInt32(),
            screenshots,
            downloads,
            beforeDatabaseSha256,
            afterDatabaseSha256,
            zeroWrite,
            true);
    }

    private static void RequireCloseoutArtifactPin(
        string path,
        string? expectedSha256,
        string context)
    {
        HarnessAssert.True(File.Exists(path), $"P9-CLOSE {context} missing");
        HarnessAssert.Equal(
            expectedSha256,
            HashBytes(File.ReadAllBytes(path)),
            $"P9-CLOSE {context} SHA-256");
    }

    private static string[] ReadCloseoutResultKinds(string resultPath)
    {
        using var result = JsonDocument.Parse(File.ReadAllBytes(resultPath));
        var tracePath = result.RootElement
            .GetProperty("artifacts")
            .GetProperty("trace")
            .GetProperty("path")
            .GetString()!;
        using var trace = JsonDocument.Parse(File.ReadAllBytes(tracePath));
        return trace.RootElement.GetProperty("actions")
            .EnumerateArray()
            .Where(item => item.GetProperty("action").GetString() ==
                           "RESULT_MATRIX_COMPLETE")
            .SelectMany(item => item.GetProperty("detail")
                .GetProperty("exactKinds")
                .EnumerateArray()
                .Select(value => value.GetString()!))
            .ToArray();
    }

    private static P9CloseExportParseEvidence ValidateCloseoutDownloads(
        string browserResultPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(browserResultPath));
        var downloads = document.RootElement
            .GetProperty("artifacts")
            .GetProperty("downloads")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("format").GetString()!,
                item => item.Clone(),
                StringComparer.Ordinal);
        HarnessAssert.True(
            downloads.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(new[] { "CSV", "XLSX" }),
            "P9-CLOSE exact CSV/XLSX browser downloads");

        var csv = downloads["CSV"];
        var csvPath = csv.GetProperty("path").GetString()!;
        var csvBytes = File.ReadAllBytes(csvPath);
        var csvRows = ParseCsv(csvBytes);
        HarnessAssert.True(csvRows.Count >= 2, "P9-CLOSE CSV header/data rows");
        HarnessAssert.True(
            csvRows.All(row => row.Count == csvRows[0].Count),
            "P9-CLOSE CSV rectangular parse");
        var csvFormulaSafe = csvRows.Skip(1)
            .SelectMany(row => row)
            .Where(value => !string.IsNullOrEmpty(value))
            .All(value => value[0] is not ('=' or '+' or '-' or '@'));

        var xlsx = downloads["XLSX"];
        var xlsxPath = xlsx.GetProperty("path").GetString()!;
        var xlsxBytes = File.ReadAllBytes(xlsxPath);
        int xlsxRows;
        int xlsxColumns;
        bool xlsxFormulaSafe;
        using (var workbook = OpenWorkbook(xlsxBytes))
        {
            var result = workbook.Worksheet("Result");
            xlsxRows = Math.Max(0, result.LastRowUsed()?.RowNumber() - 1 ?? 0);
            xlsxColumns = result.LastColumnUsed()?.ColumnNumber() ?? 0;
            xlsxFormulaSafe = !result.CellsUsed().Any(cell => cell.HasFormula);
        }

        var csvHash = HashBytes(csvBytes);
        var xlsxHash = HashBytes(xlsxBytes);
        var hashesMatch =
            csvHash == csv.GetProperty("contentHash").GetString() &&
            xlsxHash == xlsx.GetProperty("contentHash").GetString();
        var countsMatch =
            csvRows.Count - 1 == csv.GetProperty("rowCount").GetInt32() &&
            csvRows[0].Count == csv.GetProperty("columnCount").GetInt32() &&
            xlsxRows == xlsx.GetProperty("rowCount").GetInt32() &&
            xlsxColumns == xlsx.GetProperty("columnCount").GetInt32();
        return new P9CloseExportParseEvidence(
            Path.GetFullPath(csvPath),
            csvHash,
            csvRows.Count - 1,
            csvRows[0].Count,
            Path.GetFullPath(xlsxPath),
            xlsxHash,
            xlsxRows,
            xlsxColumns,
            hashesMatch,
            countsMatch,
            csvFormulaSafe && xlsxFormulaSafe,
            hashesMatch && countsMatch && csvFormulaSafe && xlsxFormulaSafe);
    }

    private async Task<P9CloseBoundaryEvidence> RunCloseoutBoundaryProbesAsync(
        CancellationToken ct)
    {
        var before = await CaptureDatabaseSnapshotAsync(ct);
        var p8 = await RequireApi().GetAsync(
            $"api/stat-config/bundle?ownerKind=DYNAMIC_FORM&ownerId={Fixture().TemplateId}",
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(p8, HttpStatusCode.OK,
            "P9-CLOSE final P8 bundle regression");
        var p8Schema = ApiHarnessClient.RequiredString(p8.Json, "schemaVersion");
        HarnessAssert.Equal("P8-BUNDLE-1", p8Schema,
            "P9-CLOSE P8 bundle schema");
        var afterP8 = await CaptureDatabaseSnapshotAsync(ct);
        var p8ZeroWrite = SnapshotSha256(before) == SnapshotSha256(afterP8);
        HarnessAssert.True(p8ZeroWrite,
            "P9-CLOSE final P8 bundle regression changed Mongo");

        var beforeP10 = afterP8;
        var p10 = await RequireApi().PostAsync(
            "api/stat-config/barriers/P10_RECONCILE",
            new
            {
                ownerKind = "UNIT",
                ownerId = Fixture().UnitAId,
                commandId = "p9-close-p10-zero-write-001",
                expectedBundleHash = Fixture().ConfigHash
            },
            Actor("admin").Token,
            ct: ct);
        ExpectError(
            p10,
            HttpStatusCode.Conflict,
            "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
            "P9-CLOSE P10 reconcile barrier");
        var p10Code = ApiHarnessClient.FindStringRecursive(
            p10.Json, "errorCode") ?? string.Empty;
        var afterP10 = await CaptureDatabaseSnapshotAsync(ct);
        var p10ZeroWrite = SnapshotSha256(beforeP10) ==
                          SnapshotSha256(afterP10);
        HarnessAssert.True(p10ZeroWrite,
            "P9-CLOSE P10 reconcile barrier changed Mongo");

        var beforeProfile = afterP10;
        var profile = await RequireApi().PostAsync(
            $"api/dynamic-flows/{Fixture().FlowFamilyId}/statistic-profile",
            new
            {
                membership = new[] { Fixture().ReportId },
                commandId = "p9-close-profile-block-001"
            },
            Actor("admin").Token,
            ct: ct);
        HarnessAssert.True(
            profile.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict,
            "P9-CLOSE FLOW_STATISTIC_PROFILE remains blocked");
        var after = await CaptureDatabaseSnapshotAsync(ct);
        var profileZeroWrite = SnapshotSha256(beforeProfile) ==
                               SnapshotSha256(after);
        HarnessAssert.True(profileZeroWrite,
            "P9-CLOSE FLOW_STATISTIC_PROFILE denial changed Mongo");
        var beforeHash = SnapshotSha256(before);
        var afterHash = SnapshotSha256(after);
        var zeroWrite = beforeHash == afterHash;
        HarnessAssert.True(zeroWrite,
            "P9-CLOSE final P8/P10/profile probes changed Mongo");

        var evidence = new P9CloseBoundaryEvidence(
            (int)p8.StatusCode,
            p8Schema,
            p8ZeroWrite,
            (int)p10.StatusCode,
            p10Code,
            p10ZeroWrite,
            (int)profile.StatusCode,
            true,
            profileZeroWrite,
            beforeHash,
            afterHash,
            zeroWrite);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-CLOSE.browser-boundaries.json"),
            evidence,
            ct);
        return evidence;
    }

    private bool CloseoutArtifactsAreSecretFree()
    {
        if (!Directory.Exists(_paths.RunRoot))
            return true;
        var secretValues = _artifactSecrets.ToArray();
        return Directory.GetFiles(_paths.RunRoot, "*.json",
                SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(
                "fixture.runtime.secret.json",
                StringComparison.OrdinalIgnoreCase))
            .All(path =>
            {
                var value = File.ReadAllText(path);
                return secretValues.All(secret => !value.Contains(
                    secret,
                    StringComparison.Ordinal));
            });
    }

    private void DeleteCloseoutSecretManifests(List<string> errors)
    {
        foreach (var path in _closeSecretManifestPaths.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                DeleteCloseoutSecretManifest(path);
            }
            catch (Exception exception)
            {
                errors.Add($"secret-delete:{Path.GetFileName(path)}:{exception.Message}");
            }
        }
    }

    private static void DeleteCloseoutSecretManifest(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        if (File.Exists(path))
            throw new IOException($"Secret browser manifest was not deleted: {path}");
    }

    private async Task WriteCloseoutCleanupAsync(
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        DateTime completedAtUtc,
        CancellationToken ct)
    {
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-CLOSE.browser-cleanup.json"),
            new
            {
                schemaVersion = "P9_CLOSE_BROWSER_CLEANUP_V1",
                chainId = ChainId,
                promptId = CloseoutPromptId,
                runKey = _runKey,
                secretManifestCount = _closeSecretManifestPaths.Count,
                secretManifestsDeleted = _closeSecretManifestPaths.All(path => !File.Exists(path)),
                backend = new
                {
                    processId = _backend?.ProcessId,
                    stopped = _backend?.StopVerified == true,
                    portReleased = _backend?.PortReleaseVerified == true
                },
                mongo = new
                {
                    processId = _mongo?.ProcessId,
                    databaseDropped = _mongo?.DatabaseDropVerified == true,
                    stopped = _mongo?.ProcessStopVerified == true,
                    portReleased = _mongo?.PortReleaseVerified == true,
                    dataDirectoryRemoved = _mongo?.DataDirectoryRemovalVerified == true
                },
                cleanupSucceeded,
                cleanupErrors,
                completedAtUtc
            },
            ct);
    }

    private async Task<string> WriteCloseoutBrowserEvidenceAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool passed,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var databasePath = Path.Combine(
            _paths.RunRoot,
            "P9-CLOSE.browser-database-deltas.json");
        await WriteStrictJsonAsync(databasePath, new
        {
            schemaVersion = "P9_CLOSE_BROWSER_DATABASE_DELTAS_V1",
            chainId = ChainId,
            promptId = CloseoutPromptId,
            runKey = _runKey,
            transitions = _closeDatabaseTransitions,
            browserReadSliceCount = _closeBrowserSlices
                .Count(item => item.SliceId is
                    "browser02-lifecycle-multi-context" or
                    "browser01-direct" or
                    "browser01-advanced" or
                    "browser01-diff"),
            browserReadSlicesZeroWrite = _closeBrowserSlices
                .Where(item => item.SliceId is
                    "browser02-lifecycle-multi-context" or
                    "browser01-direct" or
                    "browser01-advanced" or
                    "browser01-diff")
                .All(item => item.ZeroWrite),
            passed = _closeDatabaseTransitions.All(item => item.Passed)
        }, ct);

        var securityPath = Path.Combine(
            _paths.RunRoot,
            "P9-CLOSE.browser-security.json");
        var jsonFiles = Directory.GetFiles(_paths.RunRoot, "*.json",
            SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(
                "fixture.runtime.secret.json",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var secretsAbsent = CloseoutArtifactsAreSecretFree();
        await WriteStrictJsonAsync(securityPath, new
        {
            schemaVersion = "P9_CLOSE_BROWSER_SECURITY_V1",
            networkMockCount = _closeBrowserSlices.Sum(item => item.NetworkMockCount),
            nativeBrowserTracePersisted = false,
            customTraceRedacted = true,
            requestResponseBodiesPersisted = false,
            authorizationHeadersPersisted = false,
            secretManifestsDeleted = _closeSecretManifestPaths.All(path => !File.Exists(path)),
            scannedJsonFiles = jsonFiles.Length,
            secretsAbsent,
            p10Blocked = _closeBoundary?.P10ZeroWrite == true,
            profileBlocked = _closeBoundary?.ProfileBlocked == true,
            passed = secretsAbsent &&
                     _closeBrowserSlices.Sum(item => item.NetworkMockCount) == 0
        }, ct);

        var caseRows = CloseoutBrowserCaseIds.Select(caseId => new
        {
            caseId,
            verdict = passed ? "DAT" : "KHONG_DAT",
            slices = _closeBrowserSlices
                .Where(item => item.CaseId == caseId)
                .Select(item => item.SliceId)
                .ToArray(),
            apiRequestCount = _closeBrowserSlices
                .Where(item => item.CaseId == caseId)
                .Sum(item => item.ApiRequestCount),
            screenshotCount = _closeBrowserSlices
                .Where(item => item.CaseId == caseId)
                .Sum(item => item.ScreenshotCount),
            downloadCount = _closeBrowserSlices
                .Where(item => item.CaseId == caseId)
                .Sum(item => item.DownloadCount)
        }).ToArray();
        var artifactPaths = new[]
        {
            Path.Combine(_paths.RunRoot, "environment.json"),
            Path.Combine(_paths.RunRoot, "P9-CLOSE.build-component.json"),
            Path.Combine(_paths.RunRoot, "P9-CLOSE.production-workers-stage.json"),
            Path.Combine(_paths.RunRoot, "P9-CLOSE.production-readonly-browser-stage.json"),
            databasePath,
            Path.Combine(_paths.RunRoot, "P9-CLOSE.export-parse.json"),
            Path.Combine(_paths.RunRoot, "P9-CLOSE.browser-boundaries.json"),
            securityPath,
            Path.Combine(_paths.RunRoot, "P9-CLOSE.browser-cleanup.json")
        };
        var sliceArtifactPins = BuildCloseoutSliceArtifactPins();
        if (passed)
        {
            HarnessAssert.Equal(
                10,
                _closeBrowserSlices.Select(item => item.SliceId)
                    .Distinct(StringComparer.Ordinal).Count(),
                "P9-CLOSE exact browser slice artifact roots");
            HarnessAssert.True(
                sliceArtifactPins.Count > _closeBrowserSlices.Count * 4 &&
                sliceArtifactPins.Select(item => item.Path)
                    .Distinct(StringComparer.Ordinal).Count() == sliceArtifactPins.Count &&
                sliceArtifactPins.Select(item => item.Purpose)
                    .Distinct(StringComparer.Ordinal).Count() == sliceArtifactPins.Count &&
                sliceArtifactPins.All(item => !Path.IsPathRooted(item.Path)),
                "P9-CLOSE recursive browser artifact registry");
        }
        var topLevelArtifactPins = artifactPaths.Where(File.Exists)
            .Select(path => PinUiArtifact(
                path,
                $"CLOSEOUT:{Path.GetFileNameWithoutExtension(path)}"))
            .ToArray();
        var allArtifactPins = topLevelArtifactPins
            .Concat(sliceArtifactPins)
            .ToArray();
        HarnessAssert.True(
            allArtifactPins.Select(item => item.Path)
                .Distinct(StringComparer.Ordinal).Count() == allArtifactPins.Length &&
            allArtifactPins.Select(item => item.Purpose)
                .Distinct(StringComparer.Ordinal).Count() == allArtifactPins.Length,
            "P9-CLOSE final artifact path/purpose registry");
        var summaryPath = Path.Combine(
            _paths.RunRoot,
            "P9-CLOSE.production-browser.json");
        await WriteStrictJsonAsync(summaryPath, new
        {
            schemaVersion = "P9_CLOSE_PRODUCTION_BROWSER_V1",
            chainId = ChainId,
            promptId = CloseoutPromptId,
            groupId = CloseoutGroupId,
            runKey = _runKey,
            startedAtUtc,
            completedAtUtc,
            expectedCaseCount = 3,
            actualCaseCount = caseRows.Length,
            exactCaseIds = CloseoutBrowserCaseIds,
            exactIds = caseRows.Select(item => item.caseId)
                .SequenceEqual(CloseoutBrowserCaseIds),
            allDat = caseRows.All(item => item.verdict == "DAT"),
            cases = caseRows,
            slices = _closeBrowserSlices,
            assertions = new
            {
                productionKestrel = true,
                candidateEnabled = false,
                isolatedMongoReplicaSet = true,
                productionFrontendBuild = _closeBuildPassed,
                realInstalledChrome = true,
                separateActorContexts = true,
                networkMockCount = _closeBrowserSlices.Sum(item => item.NetworkMockCount),
                csvXlsxParsed = _closeExportParse?.Passed == true,
                p8Regression = _closeBoundary?.P8Regression == true,
                p10ZeroWrite = _closeBoundary?.P10ZeroWrite == true,
                profileBlocked = _closeBoundary?.ProfileBlocked == true,
                sourceFingerprintStable = _closeSourceBefore is not null &&
                    _closeSourceAfter is not null &&
                    _closeSourceBefore.AggregateSha256 == _closeSourceAfter.AggregateSha256
            },
            sourceFingerprint = new
            {
                before = _closeSourceBefore?.AggregateSha256,
                after = _closeSourceAfter?.AggregateSha256
            },
            artifactRegistry = new
            {
                exactSliceCount = 10,
                sliceArtifactCount = sliceArtifactPins.Count,
                totalArtifactCount = allArtifactPins.Length,
                recursivelyHashed = true,
                repositoryRelativePaths = true,
                exactUniquePaths = true,
                exactUniquePurposes = true
            },
            artifacts = allArtifactPins,
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            passed
        }, ct);
        return summaryPath;
    }

    private IReadOnlyList<P9UiArtifactPin> BuildCloseoutSliceArtifactPins()
    {
        var pins = new List<P9UiArtifactPin>();
        foreach (var slice in _closeBrowserSlices
                     .OrderBy(item => item.SliceId, StringComparer.Ordinal))
        {
            var sliceRoot = Path.GetDirectoryName(slice.ResultPath)
                ?? throw new InvalidOperationException(
                    $"P9-CLOSE slice root missing for {slice.SliceId}.");
            var files = Directory.GetFiles(sliceRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(
                    "fixture.runtime.secret.json",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetRelativePath(sliceRoot, path),
                    StringComparer.Ordinal)
                .ToArray();
            HarnessAssert.True(
                files.Any(path => Path.GetFileName(path).Equals(
                    "browser-result.json", StringComparison.Ordinal)) &&
                files.Any(path => Path.GetFileName(path).Equals(
                    "browser-trace.json", StringComparison.Ordinal)) &&
                files.Any(path => Path.GetFileName(path).Equals(
                    "network.json", StringComparison.Ordinal)) &&
                files.Any(path => Path.GetExtension(path).Equals(
                    ".png", StringComparison.OrdinalIgnoreCase)),
                $"P9-CLOSE {slice.SliceId} recursive artifact minimum");
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(sliceRoot, file)
                    .Replace('\\', '/');
                var purpose = relative switch
                {
                    "browser-result.json" =>
                        $"BROWSER_SLICE_RESULT:{slice.SliceId}",
                    "browser-trace.json" =>
                        $"BROWSER_TRACE:{slice.SliceId}",
                    "network.json" =>
                        $"BROWSER_NETWORK:{slice.SliceId}",
                    "fixture.redacted.json" =>
                        $"BROWSER_FIXTURE_REDACTED:{slice.SliceId}",
                    _ when relative.StartsWith(
                        "screenshots/", StringComparison.Ordinal) =>
                        $"BROWSER_SCREENSHOT:{slice.SliceId}:{Path.GetFileName(file)}",
                    _ when relative.StartsWith(
                        "downloads/", StringComparison.Ordinal) =>
                        $"BROWSER_DOWNLOAD:{slice.SliceId}:{Path.GetFileName(file)}",
                    _ => throw new InvalidOperationException(
                        $"P9-CLOSE unregistered slice artifact: {relative}")
                };
                pins.Add(PinUiArtifact(file, purpose));
            }
        }
        return pins;
    }
}
