using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    private P10CloseoutSecretScan? _closeoutSecretScan;

    private static readonly HashSet<string> CloseoutExpectedMutableCollections =
        new(StringComparer.Ordinal)
        {
            "work_report_statistic_reconciliations",
            "work_report_statistic_reconciliation_observations",
            "work_report_statistic_reconciliation_reviews",
            "work_report_statistic_reconciliation_exports"
        };

    private async Task<bool> ValidateCloseoutBrowserArtifactsAsync(
        CancellationToken ct)
        => await ValidateCloseoutMeasuredBrowserArtifactsAsync(ct);

    private async Task WriteCloseoutCleanupArtifactAsync(
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        DateTime completedAtUtc,
        CancellationToken ct)
    {
        var path = Path.Combine(
            _paths.RunRoot,
            "P10-CLOSE.browser-cleanup.json");
        await WriteStrictJsonAsync(path, new
        {
            schemaVersion = "P10_CLOSE_BROWSER_CLEANUP_V1",
            chainId = ChainId,
            promptId = CloseoutPromptId,
            runKey = _runKey,
            secretManifestCount = _closeoutSecretPaths.Count,
            secretManifestsDeleted = _closeoutSecretPaths.All(
                item => !File.Exists(item)),
            backend = new
            {
                processId = _backend?.ProcessId,
                stopped = _closeoutBootstrapBackendStopped &&
                    _backend?.StopVerified == true,
                listenerReleased =
                    _closeoutBootstrapBackendPortReleased &&
                    _backend?.PortReleaseVerified == true
            },
            mongo = new
            {
                processId = _mongo?.ProcessId,
                databaseDropped = _mongo?.DatabaseDropVerified == true,
                stopped = _mongo?.ProcessStopVerified == true,
                listenerReleased = _mongo?.PortReleaseVerified == true,
                dataDirectoryRemoved =
                    _mongo?.DataDirectoryRemovalVerified == true
            },
            ownedProcessCount = cleanupSucceeded ? 0 : -1,
            ownedListenerCount = cleanupSucceeded ? 0 : -1,
            ownedDatabaseCount = cleanupSucceeded ? 0 : -1,
            cleanupSucceeded,
            cleanupErrors = cleanupErrors.Select(
                RedactCloseoutArtifactText).ToArray(),
            completedAtUtc
        }, ct);
    }

    private async Task WriteCloseoutSourceArtifactAsync(CancellationToken ct)
    {
        var path = Path.Combine(
            _paths.RunRoot,
            "P10-CLOSE.browser-source-fingerprint.json");
        await WriteStrictJsonAsync(path, new
        {
            schemaVersion = "P10_CLOSE_BROWSER_SOURCE_FINGERPRINT_V1",
            before = _closeoutSourceBefore,
            after = _closeoutSourceAfter,
            stable = P10TrustedSourceFingerprintScanner.AreEqual(
                _closeoutSourceBefore,
                _closeoutSourceAfter)
        }, ct);
    }

    private async Task<string> WriteCloseoutBrowserProofAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var changedCollections = CloseoutChangedCollections();
        var unknownCollections = changedCollections
            .Where(item => !CloseoutExpectedMutableCollections.Contains(
                item.Name))
            .ToArray();
        var p5P9ReadOnly = _protectedBefore is not null &&
            _protectedAfter is not null &&
            _protectedBefore.SemanticSha256 ==
                _protectedAfter.SemanticSha256;
        var sourceStable =
            P10TrustedSourceFingerprintScanner.AreEqual(
                _closeoutSourceBefore,
                _closeoutSourceAfter);
        var networkMocks = _closeoutBrowserCases.Sum(item =>
            item.NetworkMockCount);
        var parity = _closeoutMeasuredParity?.Passed == true &&
            _closeoutMeasuredParity?.Review.UiTraceRows.Count == 5 &&
            _closeoutMeasuredParity?.Review.UiTraceRowsSha256.Length == 64 &&
            _closeoutBrowserCases.Count == 3 &&
            _closeoutBrowserCases.All(item =>
                item.DomApiMongoParity && item.ExportMongoParity);
        var accessibility = _closeoutBrowserCases.Count == 3 &&
            _closeoutBrowserCases.All(item => item.AccessibilityPassed);
        var security = _closeoutSecurityPassed &&
            _closeoutSecretScan?.Passed == true &&
            _closeoutBrowserCases.Count == 3 &&
            _closeoutBrowserCases.All(item => item.SecurityPassed);
        var passed = fatalFailure is null &&
            cleanupSucceeded &&
            _closeoutBrowserValidated &&
            _closeoutBrowserCases.Count == 3 &&
            _closeoutBrowserCases.All(item => item.Status == "PASS") &&
            networkMocks == 0 &&
            parity &&
            accessibility &&
            security &&
            sourceStable &&
            _closeoutStateRegistry is
            {
                Expected: 7,
                Passed: 7,
                DirectMongoAsserted: true,
                EmptyScopeMongoCount: 0
            } &&
            _closeoutMeasuredParity?.Passed == true &&
            _closeoutSuccessorBarrier is
            {
                Blocked: true,
                SuccessorNamespaceCount: 0
            } &&
            p5P9ReadOnly &&
            unknownCollections.Length == 0 &&
            _closeoutBoundary is
            {
                ProfileBlocked: true,
                P11P12Blocked: true,
                P9Available: true
            };
        var artifactPins = BuildCloseoutBrowserArtifactPins();
        passed = passed && HasExactCloseoutBrowserArtifactRegistry(
            artifactPins);
        var proofPath = Path.Combine(
            _paths.RunRoot,
            "P10-CLOSE.production-browser.json");
        var proofPayload = new
        {
            schemaVersion = "P10_CLOSE_PRODUCTION_BROWSER_V1",
            status = passed ? "PASS" : "FAIL",
            expected = 3,
            passed = _closeoutBrowserCases.Count(item =>
                item.Status == "PASS"),
            networkMockCount = networkMocks,
            domApiExportMongoParity = parity,
            accessibilityPassed = accessibility,
            securityPassed = security,
            secretScan = _closeoutSecretScan,
            cleanupSucceeded,
            unknownCollectionDeltaCount = unknownCollections.Length,
            profileBlocked = _closeoutBoundary?.ProfileBlocked == true,
            p11P12Blocked = _closeoutBoundary?.P11P12Blocked == true,
            p11ArtifactRootAbsent =
                _closeoutSuccessorBarrier?.P11ArtifactRootAbsent == true,
            p12ArtifactRootAbsent =
                _closeoutSuccessorBarrier?.P12ArtifactRootAbsent == true,
            successorNamespaceCount =
                _closeoutSuccessorBarrier?.SuccessorNamespaceCount ?? -1,
            p5P9StoresReadOnly = p5P9ReadOnly,
            ownedProcessCount = cleanupSucceeded ? 0 : -1,
            ownedListenerCount = cleanupSucceeded ? 0 : -1,
            ownedDatabaseCount = cleanupSucceeded ? 0 : -1,
            caseIds = CloseoutBrowserCaseIds,
            cases = _closeoutBrowserCases.Select(item => new
            {
                item.CaseId,
                item.Status,
                item.DomApiMongoParity,
                item.ExportMongoParity,
                item.AccessibilityPassed,
                item.SecurityPassed,
                item.NetworkMockCount
            }),
            productionStateRegistry = _closeoutStateRegistry,
            measuredParity = _closeoutMeasuredParity,
            successorBarrier = _closeoutSuccessorBarrier,
            realInfrastructure = new
            {
                productionKestrel = true,
                productionFrontend = _closeoutBuild?.ExitCode == 0,
                ownedMongoReplicaSet = true,
                browser = true,
                networkMocks = false
            },
            sourceFingerprint = new
            {
                before = new
                {
                    sha256 = _closeoutSourceBefore?.Sha256
                },
                after = new
                {
                    sha256 = _closeoutSourceAfter?.Sha256
                },
                stable = sourceStable
            },
            catalogPins = new
            {
                currentRawSha256 = PublishedCurrentRawSha256,
                lockRawSha256 = PublishedLockRawSha256,
                p9Available = _closeoutBoundary?.P9Available == true,
                activationOwnedByRestoreProof = true
            },
            boundary = _closeoutBoundary,
            databaseDelta = new
            {
                beforeSha256 = _closeoutDatabaseBefore?.Sha256,
                afterSha256 = _closeoutDatabaseAfter?.Sha256,
                changedCollections,
                unknownCollections
            },
            build = new
            {
                production = ProcessEvidence(_closeoutBuild),
                components = ProcessEvidence(_closeoutComponents),
                exactComponentCaseCount = 24
            },
            artifactPins,
            chainId = ChainId,
            promptId = CloseoutPromptId,
            groupId = CloseoutGroupId,
            runKey = _runKey,
            startedAtUtc,
            completedAtUtc,
            cleanupErrors = cleanupErrors.Select(
                RedactCloseoutArtifactText).ToArray(),
            fatalFailure = JsonSerializer.SerializeToElement<string?>(
                RedactCloseoutArtifactText(fatalFailure))
        };
        var proofJsonOptions = new JsonSerializerOptions(EvidenceJson.Options)
        {
            DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };
        await WriteStrictJsonAsync(
            proofPath,
            JsonSerializer.SerializeToElement(proofPayload, proofJsonOptions),
            ct);
        return proofPath;
    }

    private IReadOnlyList<P10CloseoutArtifactPin>
        BuildCloseoutBrowserArtifactPins()
    {
        var pins = new List<P10CloseoutArtifactPin>();
        AddCloseoutArtifactPinIfPresent(
            pins,
            Path.Combine(_paths.RunRoot,
                "P10-CLOSE.browser-fixture.json"),
            "P10_BROWSER_FIXTURE");
        AddCloseoutArtifactPinIfPresent(
            pins,
            Path.Combine(_paths.RunRoot,
                "P10-CLOSE.browser-cleanup.json"),
            "P10_BROWSER_CLEANUP");
        AddCloseoutArtifactPinIfPresent(
            pins,
            Path.Combine(_paths.RunRoot,
                "P10-CLOSE.browser-source-fingerprint.json"),
            "P10_BROWSER_SOURCE_FINGERPRINT");
        foreach (var item in _closeoutBrowserCases)
        {
            AddCloseoutArtifactPinIfPresent(pins,
                item.ResultPath,
                "P10_BROWSER_CASE_RESULT",
                item.CaseId);
            AddCloseoutArtifactPinIfPresent(pins,
                item.TracePath,
                "P10_BROWSER_TRACE",
                item.CaseId);
            AddCloseoutArtifactPinIfPresent(pins,
                item.NetworkPath,
                "P10_BROWSER_NETWORK",
                item.CaseId);
            AddCloseoutArtifactPinIfPresent(pins,
                item.ScreenshotPath,
                "P10_BROWSER_SCREENSHOT",
                item.CaseId);
            AddCloseoutArtifactPinIfPresent(pins,
                item.DownloadPath,
                "P10_BROWSER_DOWNLOAD",
                item.CaseId);
        }
        HarnessAssert.True(
            pins.Select(item => item.Path).Distinct(
                    StringComparer.Ordinal).Count() == pins.Count,
            "P10-CLOSE browser artifact paths must be unique");
        return pins;
    }

    private static bool HasExactCloseoutBrowserArtifactRegistry(
        IReadOnlyList<P10CloseoutArtifactPin> pins)
    {
        return pins.Count == 18 &&
            pins.Count(item => item.Purpose ==
                "P10_BROWSER_FIXTURE") == 1 &&
            pins.Count(item => item.Purpose ==
                "P10_BROWSER_CLEANUP") == 1 &&
            pins.Count(item => item.Purpose ==
                "P10_BROWSER_SOURCE_FINGERPRINT") == 1 &&
            new[]
            {
                "P10_BROWSER_CASE_RESULT",
                "P10_BROWSER_TRACE",
                "P10_BROWSER_NETWORK",
                "P10_BROWSER_SCREENSHOT",
                "P10_BROWSER_DOWNLOAD"
            }.All(purpose => pins.Count(item =>
                item.Purpose == purpose) == 3);
    }

    private void AddCloseoutArtifactPinIfPresent(
        ICollection<P10CloseoutArtifactPin> pins,
        string fullPath,
        string purpose,
        string? caseId = null)
    {
        RequireCloseoutOwnedArtifact(fullPath);
        if (File.Exists(fullPath))
        {
            pins.Add(PinCloseoutArtifact(fullPath, purpose, caseId));
        }
    }

    private P10CloseoutArtifactPin PinCloseoutArtifact(
        string fullPath,
        string purpose,
        string? caseId = null)
    {
        RequireCloseoutOwnedArtifact(fullPath);
        var bytes = File.ReadAllBytes(fullPath);
        return new P10CloseoutArtifactPin(
            Path.GetRelativePath(_paths.WorkspaceRoot, fullPath)
                .Replace('\\', '/'),
            HashBytes(bytes),
            bytes.LongLength,
            purpose,
            caseId);
    }

    private void RequireCloseoutOwnedArtifact(string fullPath)
    {
        var root = Path.GetFullPath(_paths.RunRoot)
            .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(fullPath);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"P10-CLOSE artifact escaped owned run root: {target}");
        }
    }

    private IReadOnlyList<P10CloseoutCollectionDelta>
        CloseoutChangedCollections()
    {
        if (_closeoutDatabaseBefore is null ||
            _closeoutDatabaseAfter is null)
        {
            return [];
        }
        var before = _closeoutDatabaseBefore.Collections.ToDictionary(
            item => item.Name,
            StringComparer.Ordinal);
        var after = _closeoutDatabaseAfter.Collections.ToDictionary(
            item => item.Name,
            StringComparer.Ordinal);
        return before.Keys.Concat(after.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                before.TryGetValue(name, out var left);
                after.TryGetValue(name, out var right);
                return new P10CloseoutCollectionDelta(
                    name,
                    left?.Count ?? 0,
                    right?.Count ?? 0,
                    left?.Sha256 ?? HashBytes([]),
                    right?.Sha256 ?? HashBytes([]),
                    left?.Count != right?.Count ||
                        left?.Sha256 != right?.Sha256);
            })
            .Where(item => item.Changed)
            .ToArray();
    }

    private bool CloseoutArtifactsAreSecretFree(
        string? fatalFailure,
        IReadOnlyList<string> cleanupErrors)
    {
        const long maximumFileBytes = 16L * 1024 * 1024;
        var extensions = new[]
        {
            ".csv", ".err", ".json", ".jsonl", ".log", ".out", ".txt"
        };
        var files = new List<P10CloseoutSecretFilePin>();
        var explicitLogPaths = new List<string>();
        var inMemoryValues = new List<string>();
        var secretValues = CloseoutArtifactSecretValues();
        if (secretValues.Length == 0)
        {
            throw new InvalidOperationException(
                "P10_CLOSE_SECRET_SET_EMPTY");
        }
        var secretMatchCount = 0;
        var reparseSafe = false;
        var mongoDataDirectoryPruned = false;
        string? failure = null;
        try
        {
            if (!Directory.Exists(_paths.RunRoot) ||
                _closeoutSecretPaths.Any(File.Exists))
            {
                throw new InvalidOperationException(
                    "P10_CLOSE_SECRET_ROOT_OR_MANIFEST_INVALID");
            }
            var root = Path.GetFullPath(_paths.RunRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            CloseoutRejectRedirectedAncestors(root, root);
            var mongoDataRoot = _mongo is null
                ? null
                : Path.GetFullPath(_mongo.DataDirectory).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            mongoDataDirectoryPruned = mongoDataRoot is not null;
            var inspectable = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                CloseoutRejectRedirectedAncestors(root, directory);
                foreach (var value in Directory.EnumerateFiles(
                             directory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var full = Path.GetFullPath(value);
                    if (extensions.Contains(
                            Path.GetExtension(full),
                            StringComparer.OrdinalIgnoreCase))
                    {
                        inspectable.Add(full);
                    }
                }
                foreach (var value in Directory.EnumerateDirectories(
                             directory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var full = Path.GetFullPath(value).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                    if (mongoDataRoot is not null &&
                        (string.Equals(full, mongoDataRoot,
                             StringComparison.OrdinalIgnoreCase) ||
                         full.StartsWith(
                             mongoDataRoot + Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    CloseoutRejectRedirectedAncestors(root, full);
                    pending.Push(full);
                }
            }

            var explicitPaths = new[]
                {
                    _backend?.StdoutPath,
                    _backend?.StderrPath,
                    _mongo?.MongoLogPath,
                    _mongo?.StdoutPath,
                    _mongo?.StderrPath
                }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFullPath(value!))
                .ToArray();
            if (_backend is null || _mongo is null ||
                explicitPaths.Length != 5 ||
                explicitPaths.Any(value => !File.Exists(value)))
            {
                throw new InvalidOperationException(
                    "P10_CLOSE_SECRET_EXPLICIT_LOG_MISSING");
            }
            foreach (var path in explicitPaths)
            {
                inspectable.Add(path);
                explicitLogPaths.Add(Path.GetRelativePath(
                        _paths.WorkspaceRoot,
                        path)
                    .Replace('\\', '/'));
            }

            foreach (var path in inspectable.OrderBy(
                         value => value,
                         StringComparer.Ordinal))
            {
                if (!path.StartsWith(
                        root + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path))
                {
                    throw new InvalidOperationException(
                        "P10_CLOSE_SECRET_PATH_OUTSIDE_OWNED_ROOT");
                }
                CloseoutRejectRedirectedAncestors(root, path);
                var bytes = ReadCloseoutSharedBytes(
                    path,
                    maximumFileBytes);
                var text = new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true)
                    .GetString(bytes);
                secretMatchCount += secretValues.Count(secret =>
                    text.Contains(secret, StringComparison.Ordinal));
                files.Add(new P10CloseoutSecretFilePin(
                    Path.GetRelativePath(_paths.WorkspaceRoot, path)
                        .Replace('\\', '/'),
                    bytes.LongLength,
                    HashBytes(bytes)));
            }

            inMemoryValues.AddRange(new[]
            {
                _closeoutBuild?.StdOut ?? string.Empty,
                _closeoutBuild?.StdErr ?? string.Empty,
                _closeoutComponents?.StdOut ?? string.Empty,
                _closeoutComponents?.StdErr ?? string.Empty,
                fatalFailure ?? string.Empty
            });
            inMemoryValues.AddRange(cleanupErrors);
            secretMatchCount += inMemoryValues.Sum(value =>
                secretValues.Count(secret => value.Contains(
                    secret,
                    StringComparison.Ordinal)));
            reparseSafe = true;
        }
        catch (Exception exception)
        {
            failure = RedactCloseoutArtifactText(
                $"{exception.GetType().Name}: {exception.Message}");
        }

        var passed = failure is null &&
            secretMatchCount == 0 &&
            _closeoutSecretPaths.All(path => !File.Exists(path));
        _closeoutSecretScan = new P10CloseoutSecretScan(
            passed,
            extensions,
            maximumFileBytes,
            files.Count,
            files.Sum(value => value.Bytes),
            files,
            explicitLogPaths.OrderBy(
                value => value,
                StringComparer.Ordinal).ToArray(),
            inMemoryValues.Count,
            secretValues.Length,
            secretMatchCount,
            _closeoutSecretPaths.All(path => !File.Exists(path)),
            reparseSafe,
            mongoDataDirectoryPruned,
            failure);
        return passed;
    }

    private static byte[] ReadCloseoutSharedBytes(
        string path,
        long maximumBytes)
    {
        var info = new FileInfo(path);
        if (info.Length > maximumBytes)
        {
            throw new InvalidOperationException(
                "P10_CLOSE_SECRET_FILE_TOO_LARGE");
        }
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidOperationException(
                "P10_CLOSE_SECRET_FILE_GREW_TOO_LARGE");
        }
        using var buffer = new MemoryStream(
            checked((int)stream.Length));
        stream.CopyTo(buffer);
        if (buffer.Length != stream.Length || buffer.Length > maximumBytes)
        {
            throw new InvalidOperationException(
                "P10_CLOSE_SECRET_FILE_UNSTABLE");
        }
        return buffer.ToArray();
    }

    private static void CloseoutRejectRedirectedAncestors(
        string root,
        string target)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var current = new DirectoryInfo(Path.GetFullPath(target));
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "P10_CLOSE_SECRET_REPARSE_POINT_REJECTED");
            }
            if (string.Equals(
                    current.FullName,
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException(
            "P10_CLOSE_SECRET_PATH_ROOT_NOT_REACHED");
    }

    private string[] CloseoutArtifactSecretValues()
        => _artifactSecrets.Concat(new[]
            {
                _backend?.ActorPassword,
                _backend?.BootstrapKey,
                _backend?.DynamicFlowMappingPreviewSigningKey
            })
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private string? RedactCloseoutArtifactText(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var result = value;
        foreach (var secret in CloseoutArtifactSecretValues())
        {
            result = result.Replace(
                secret,
                "<REDACTED>",
                StringComparison.Ordinal);
        }
        return result;
    }

    private object? ProcessEvidence(P10CloseProcessResult? result)
        => result is null
            ? null
            : new
            {
                result.ExitCode,
                stdoutTail = RedactCloseoutArtifactText(
                    P10CloseTail(result.StdOut, 12000)),
                stderrTail = RedactCloseoutArtifactText(
                    P10CloseTail(result.StdErr, 12000))
            };
}

internal sealed record P10CloseoutCollectionDelta(
    string Name,
    long BeforeCount,
    long AfterCount,
    string BeforeSha256,
    string AfterSha256,
    bool Changed);

internal sealed record P10CloseoutSecretFilePin(
    string Path,
    long Bytes,
    string Sha256);

internal sealed record P10CloseoutSecretScan(
    bool Passed,
    IReadOnlyList<string> Extensions,
    long MaximumFileBytes,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<P10CloseoutSecretFilePin> Files,
    IReadOnlyList<string> ExplicitLogPaths,
    int InMemoryValueCount,
    int SecretValueCount,
    int SecretMatchCount,
    bool SecretManifestsDeleted,
    bool ReparseSafe,
    bool MongoDataDirectoryPruned,
    string? Failure);

