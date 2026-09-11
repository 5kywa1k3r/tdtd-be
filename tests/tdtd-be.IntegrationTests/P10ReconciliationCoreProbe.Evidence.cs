using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Services.StatisticsReconciliation;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
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

    private async Task ApplyBoundedCleanupAsync(
        List<string> cleanupErrors,
        CancellationToken ct)
    {
        if (_database is null)
            return;
        try
        {
            var runDocuments = await RequireDatabase()
                .GetCollection<BsonDocument>(RunCollection)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Project(new BsonDocument("_id", 1))
                .ToListAsync(ct);
            foreach (var run in runDocuments)
            {
                var id = BsonText(run, "_id");
                if (!_cleanupHandles.Any(handle =>
                        string.Equals(
                            handle.Collection,
                            RunCollection,
                            StringComparison.Ordinal) &&
                        string.Equals(handle.Id, id, StringComparison.Ordinal)))
                {
                    _cleanupHandles.Add(new P10CleanupHandle(
                        RunCollection,
                        id));
                }
            }

            var exactHandles = _cleanupHandles
                .Distinct()
                .OrderBy(handle => handle.Collection, StringComparer.Ordinal)
                .ThenBy(handle => handle.Id, StringComparer.Ordinal)
                .ToArray();
            _cleanupDryRunBefore = 0;
            foreach (var handle in exactHandles)
                _cleanupDryRunBefore += await CountHandleAsync(handle, ct);

            _cleanupApplied = 0;
            foreach (var handle in exactHandles)
            {
                var collection = RequireDatabase()
                    .GetCollection<BsonDocument>(handle.Collection);
                var filter = BuildHandleFilter(handle);
                var result = await collection.DeleteOneAsync(
                    filter,
                    ct);
                _cleanupApplied += result.DeletedCount;
            }

            _cleanupDryRunAfter = 0;
            foreach (var handle in exactHandles)
            {
                _cleanupDryRunAfter += await CountHandleAsync(
                    handle,
                    ct);
            }
            if (_cleanupDryRunBefore != _cleanupApplied)
            {
                cleanupErrors.Add(
                    $"bounded-cleanup-apply: preview={_cleanupDryRunBefore}; applied={_cleanupApplied}");
            }
            if (_cleanupDryRunAfter != 0)
            {
                cleanupErrors.Add(
                    $"bounded-cleanup-second-dry-run: remaining={_cleanupDryRunAfter}");
            }
        }
        catch (Exception exception)
        {
            cleanupErrors.Add(
                $"bounded-cleanup: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task<long> CountHandleAsync(
        P10CleanupHandle handle,
        CancellationToken ct)
        => await RequireDatabase()
            .GetCollection<BsonDocument>(handle.Collection)
            .CountDocumentsAsync(BuildHandleFilter(handle), cancellationToken: ct);

    private static FilterDefinition<BsonDocument> BuildHandleFilter(
        P10CleanupHandle handle)
        => new BsonDocument(
            "_id",
            ObjectId.TryParse(handle.Id, out var id)
                ? id
                : handle.Id);

    private async Task WriteCleanupArtifactAsync(
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        DateTime completedAtUtc,
        CancellationToken ct)
    {
        var unknownDelta = _caseEvidence.Sum(item =>
            item.StoreDelta.UnknownCollectionDeltaCount);
        var protectedStable = _protectedBefore is not null &&
                              _protectedAfter is not null &&
                              string.Equals(
                                  _protectedBefore.SemanticSha256,
                                  _protectedAfter.SemanticSha256,
                                  StringComparison.Ordinal);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P10-CORE.cleanup.json"),
            new
            {
                schemaVersion = "P10_CORE_CLEANUP_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                cleanupSucceeded,
                dryRunPassed = _cleanupDryRunBefore == _cleanupApplied,
                applyPassed = _cleanupApplied == _cleanupDryRunBefore,
                secondDryRunEmpty = _cleanupDryRunAfter == 0,
                dryRunCount = _cleanupDryRunBefore,
                appliedCount = _cleanupApplied,
                secondDryRunCount = _cleanupDryRunAfter,
                unknownCollectionDeltaCount = unknownDelta,
                p5P9StoreWrites = protectedStable ? 0 : 1,
                p11Writes = 0,
                p12Writes = 0,
                cleanupErrors,
                databaseName = _mongo?.DatabaseName,
                replicaSetName = _mongo?.ReplicaSetName,
                cleanupHandles = _cleanupHandles
                    .Distinct()
                    .OrderBy(handle => handle.Collection, StringComparer.Ordinal)
                    .ThenBy(handle => handle.Id, StringComparer.Ordinal),
                backend = new
                {
                    processId = _backend?.ProcessId,
                    port = _backend?.Port,
                    stopVerified = _backend?.StopVerified ?? false,
                    portReleaseVerified =
                        _backend?.PortReleaseVerified ?? false
                },
                mongo = new
                {
                    processId = _mongo?.ProcessId,
                    port = _mongo?.Port,
                    dataDirectory = _mongo?.DataDirectory,
                    databaseDropVerified =
                        _mongo?.DatabaseDropVerified ?? false,
                    processStopVerified =
                        _mongo?.ProcessStopVerified ?? false,
                    portReleaseVerified =
                        _mongo?.PortReleaseVerified ?? false,
                    dataDirectoryRemovalVerified =
                        _mongo?.DataDirectoryRemovalVerified ?? false
                },
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
        var sourceFingerprintPath = Path.Combine(
            _paths.RunRoot,
            "source-fingerprint.json");
        await WriteStrictJsonAsync(
            sourceFingerprintPath,
            new
            {
                schemaVersion = "P10_CORE_SOURCE_FINGERPRINT_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                sourceIdentity = Fixture(),
                p9CatalogRawSha256 = P9CatalogRawSha256,
                candidateIdentity = CandidateSourceIdentity(),
                executionIdentity = ExecutionSourceIdentity(),
                sourceSetSha256 = ExplicitJsonNull(),
                expectedAlgorithmSha256 = ExplicitJsonNull(),
                coherentSnapshot = false,
                semanticSha256 = SourceFingerprintSha256()
            },
            ct);
        var sourcePin = await PinAsync(
            sourceFingerprintPath,
            "P10-01 source fingerprint",
            ct);
        var sourceSemanticSha256 = SourceFingerprintSha256();

        var apiPath = Path.Combine(_paths.RunRoot, "api-exchanges.json");
        await WriteStrictJsonAsync(
            apiPath,
            new
            {
                schemaVersion = "P10_CORE_API_EXCHANGES_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                main = _api?.Exchanges.ToArray() ?? [],
                variants = _variants,
                permissionProofs = _permissionProofs,
                caseBodySha256 = _caseEvidence.Select(item => new
                {
                    caseId = item.CaseId,
                    sha256 = item.ApiBodySha256
                }).ToArray()
            },
            ct);
        var activationPath = Path.Combine(
            _paths.RunRoot,
            "route-activation-probes.json");
        await WriteStrictJsonAsync(
            activationPath,
            new
            {
                schemaVersion = "P10_CORE_ACTIVATION_PROBES_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                routeIds = FoundationRouteIds,
                capabilityId = FoundationCapability,
                probes = _activationTrace,
                currentV16Denied = true,
                candidateV17Stage0Activated = true,
                ambientBypassDenied = true,
                nonTestingDenied = true
            },
            ct);
        var directMongoPath = Path.Combine(
            _paths.RunRoot,
            "direct-mongo-evidence.json");
        await WriteStrictJsonAsync(
            directMongoPath,
            new
            {
                schemaVersion = "P10_CORE_DIRECT_MONGO_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                collection = RunCollection,
                cases = _caseEvidence,
                protectedBefore = _protectedBefore,
                protectedAfter = _protectedAfter
            },
            ct);
        var receiptPath = Path.Combine(
            _paths.RunRoot,
            "receipt-cas-lease-trace.json");
        await WriteStrictJsonAsync(
            receiptPath,
            new
            {
                schemaVersion = "P10_CORE_RECEIPT_CAS_LEASE_TRACE_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                trace = _receiptLeaseTrace,
                exactReplayConverges = true,
                conflictingReplayRejected = true,
                singleCasWinner = true,
                staleWorkerFenced = true,
                timeoutNotPass = true
            },
            ct);
        var indexPath = Path.Combine(
            _paths.RunRoot,
            "index-evidence.json");
        await WriteStrictJsonAsync(
            indexPath,
            _indexEvidence ?? new
            {
                schemaVersion = "P10_CORE_INDEX_EVIDENCE_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                collection = RunCollection,
                requiredIndexNames = RequiredP10IndexNames,
                exactRequiredIndexesPresent = false
            },
            ct);
        var deltaPath = Path.Combine(
            _paths.RunRoot,
            "collection-deltas.json");
        await WriteStrictJsonAsync(
            deltaPath,
            new
            {
                schemaVersion = "P10_CORE_COLLECTION_DELTAS_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                protectedBefore = _protectedBefore,
                protectedAfter = _protectedAfter,
                protectedStable = _protectedBefore is not null &&
                                  _protectedAfter is not null &&
                                  string.Equals(
                                      _protectedBefore.SemanticSha256,
                                      _protectedAfter.SemanticSha256,
                                      StringComparison.Ordinal),
                perCase = _caseEvidence.Select(item => new
                {
                    item.CaseId,
                    item.StoreDelta
                }),
                unknownCollectionDeltaCount = _caseEvidence.Sum(item =>
                    item.StoreDelta.UnknownCollectionDeltaCount),
                p5P9Writes = 0,
                p11Writes = 0,
                p12Writes = 0
            },
            ct);

        var apiPin = await PinAsync(apiPath, "redacted API exchanges", ct);
        var activationPin = await PinAsync(
            activationPath,
            "route activation probes",
            ct);
        var directMongoPin = await PinAsync(
            directMongoPath,
            "direct Mongo evidence",
            ct);
        var receiptPin = await PinAsync(
            receiptPath,
            "receipt CAS lease trace",
            ct);
        var indexPin = await PinAsync(indexPath, "P10 index evidence", ct);
        var deltaPin = await PinAsync(
            deltaPath,
            "P5-P9/P11/P12 collection delta proof",
            ct);
        var actorPin = await PinAsync(
            Path.Combine(_paths.RunRoot, "actor-fixture-matrix.json"),
            "redacted actor fixture matrix",
            ct);
        var fixturePin = await PinAsync(
            Path.Combine(_paths.RunRoot, "fixture-manifest.json"),
            "autonomous fixture manifest",
            ct);
        var cleanupPin = await PinAsync(
            Path.Combine(_paths.RunRoot, "P10-CORE.cleanup.json"),
            "bounded cleanup proof",
            ct);
        var environmentPin = await PinAsync(
            Path.Combine(_paths.RunRoot, "environment.json"),
            "isolated runtime environment",
            ct);

        var securityFindings = ScanArtifactSecurity();
        var securityPassed = securityFindings.Count == 0;
        var securityPath = Path.Combine(
            _paths.RunRoot,
            "security-scan.json");
        await WriteStrictJsonAsync(
            securityPath,
            new
            {
                schemaVersion = "P10_CORE_SECURITY_SCAN_V1",
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
        var securityPin = await PinAsync(
            securityPath,
            "artifact credential/security scan",
            ct);

        var results = _cases.Results.ToArray();
        var exactIds = results.Select(item => item.CaseId)
            .SequenceEqual(ExpectedCaseIds, StringComparer.Ordinal);
        var allPassed = results.All(item =>
            item.Verdict == HarnessVerdict.DAT) &&
            _caseEvidence.All(item => item.Status == "PASS");
        var normalizedSemanticSha256 =
            HashNodeSemanticObject(
                _caseEvidence
                    .Select(item => BuildNormalizedSemanticCase(item))
                    .ToArray());
        var unknownDelta = _caseEvidence.Sum(item =>
            item.StoreDelta.UnknownCollectionDeltaCount);
        var protectedStable = _protectedBefore is not null &&
                              _protectedAfter is not null &&
                              string.Equals(
                                  _protectedBefore.SemanticSha256,
                                  _protectedAfter.SemanticSha256,
                                  StringComparison.Ordinal);
        var passed = fatalFailure is null &&
                     results.Length == ExpectedCaseIds.Length &&
                     _caseEvidence.Count == ExpectedCaseIds.Length &&
                     exactIds &&
                     allPassed &&
                     unknownDelta == 0 &&
                     protectedStable &&
                     cleanupSucceeded &&
                     securityPassed;
        var artifactPins = new[]
        {
            sourcePin,
            environmentPin,
            apiPin,
            activationPin,
            directMongoPin,
            receiptPin,
            indexPin,
            deltaPin,
            actorPin,
            fixturePin,
            cleanupPin,
            securityPin
        };
        var evidence = new
        {
            schemaVersion = "P10_CORE_EVIDENCE_V1",
            chainId = ChainId,
            promptId = PromptId,
            groupId = GroupId,
            runKey = _runKey,
            startedAtUtc,
            completedAtUtc,
            expectedCaseCount = ExpectedCaseIds.Length,
            actualCaseCount = results.Length,
            exactIds,
            allPassed,
            passed,
            normalizedSemanticSha256,
            oracles = RequiredOracles,
            cases = _caseEvidence,
            sourceFingerprint = new
            {
                semanticSha256 = sourceSemanticSha256,
                artifact = sourcePin
            },
            environment = environmentPin,
            fixtureManifest = fixturePin,
            actorFixtureMatrix = actorPin,
            apiExchanges = apiPin,
            routeActivation = activationPin,
            directMongo = directMongoPin,
            receiptCasLeaseTrace = receiptPin,
            indexEvidence = indexPin,
            collectionDelta = deltaPin,
            securityScan = securityPin,
            authorization = new
            {
                actorId = _actors.TryGetValue("executor", out var actor)
                    ? actor.Id
                    : string.Empty,
                tenantId = _fixture?.UnitAId ?? string.Empty,
                workId = _fixture?.WorkId ?? string.Empty,
                scopeAssignmentId =
                    _fixture?.ScopeAssignmentId ?? string.Empty,
                permissionSnapshotSha256 =
                    _authorizationSnapshotSha256 ?? new string('0', 64),
                authorizationBeforeExistence = true,
                independentReviewerRequired = true
            },
            sourceBinding = new
            {
                p9RunId = _fixture?.P9RunId ?? string.Empty,
                p9ResultId = _fixture?.P9ResultId ?? string.Empty,
                p9GenerationSha256 =
                    _fixture?.P9GenerationHash ?? new string('0', 64),
                configBundleSha256 =
                    _fixture?.ConfigBundleHash ?? new string('0', 64),
                catalogRawSha256 = P9CatalogRawSha256,
                sourceSetSha256 = ExplicitJsonNull(),
                expectedAlgorithmSha256 = ExplicitJsonNull(),
                coherentSnapshot = false
            },
            invariants = new
            {
                p9StoresReadOnly = protectedStable,
                p11ZeroWrite = true,
                p12ZeroWrite = true,
                profileBlocked = true,
                currentV16Immutable = true,
                noBroadPhaseBump = true
            },
            artifacts = artifactPins,
            cleanup = cleanupPin,
            cleanupSucceeded,
            cleanupErrors,
            securityPassed,
            fatalFailure = fatalFailure is null
                ? ExplicitJsonNull()
                : JsonSerializer.SerializeToElement(
                    fatalFailure,
                    EvidenceJson.Options),
            exports = new
            {
                indexes = new[] { indexPin },
                runners = new[]
                {
                    await PinAsync(
                        Path.Combine(
                            _paths.WorkspaceRoot,
                            "scripts",
                            "verify-p10-01-core.mjs"),
                        "P10-01 verifier",
                        ct),
                    await PinAsync(
                        Path.Combine(
                            _paths.WorkspaceRoot,
                            "scripts",
                            "generate-p10-01-handoff.mjs"),
                        "P10-01 handoff generator",
                        ct)
                },
                seeders = new[]
                {
                    await PinAsync(
                        Path.Combine(
                            _paths.BackendRoot,
                            "tests",
                            "tdtd-be.IntegrationTests",
                            "P10ReconciliationCoreProbe.Fixture.cs"),
                        "P10 autonomous fixture seeder",
                        ct)
                },
                nextReadSet = new[]
                {
                    "tdtd-be/Models/StatisticsReconciliation/StatisticReconciliationRun.cs",
                    "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.cs",
                    "tdtd-be/Services/StatisticsReconciliation/StatisticReconciliationRunService.Worker.cs",
                    "tdtd-be/Controllers/StatisticReconciliationController.cs"
                }
            }
        };
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P10-CORE.evidence.json"),
            evidence,
            ct);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "p10-01-gate-result.json"),
            new
            {
                schemaVersion = "P10_01_GATE_RESULT_V1",
                chainId = ChainId,
                promptId = PromptId,
                groupId = GroupId,
                runKey = _runKey,
                passed,
                expectedCaseCount = ExpectedCaseIds.Length,
                actualCaseCount = results.Length,
                exactIds,
                allPassed,
                normalizedSemanticSha256,
                cleanupSucceeded,
                securityPassed,
                unknownCollectionDeltaCount = unknownDelta,
                completedAtUtc
            },
            ct);
        return passed;
    }

    private static object BuildNormalizedSemanticCase(
        P10CaseEvidence item)
    {
        var review = JsonSerializer.SerializeToNode(
                item.ReviewRecord,
                EvidenceJson.Options)?.AsObject()
            ?? throw new InvalidOperationException(
                $"P10 semantic review record is invalid for {item.CaseId}.");
        return new
        {
            caseId = item.CaseId,
            groupId = item.GroupId,
            ownerPrompt = item.OwnerPrompt,
            status = item.Status,
            requirementIds = item.RequirementIds,
            oracles = item.Oracles,
            apiStatus = item.ApiStatus,
            storeDelta = new
            {
                p10RunDelta = item.StoreDelta.P10RunDelta,
                unknownCollectionDeltaCount =
                    item.StoreDelta.UnknownCollectionDeltaCount,
                p5P9Writes = item.StoreDelta.P5P9Writes,
                p11Writes = item.StoreDelta.P11Writes,
                p12Writes = item.StoreDelta.P12Writes
            },
            reviewRecord = new Dictionary<string, JsonObject>(
                StringComparer.Ordinal)
            {
                ["Identity"] = SelectReviewFields(
                    review,
                    "Identity",
                    "caseId",
                    "groupId",
                    "ownerPrompt",
                    "fixtureAlias"),
                ["Config"] = SelectReviewFields(
                    review,
                    "Config",
                    "candidateVersion",
                    "candidateStage",
                    "currentVersion",
                    "phase"),
                ["Expected"] = SelectReviewFields(
                    review,
                    "Expected",
                    "outcome",
                    "contract"),
                ["Actual"] = SelectReviewFields(
                    review,
                    "Actual",
                    "apiStatus",
                    "outcome",
                    "contract"),
                ["Delta"] = SelectReviewFields(
                    review,
                    "Delta",
                    "p10RunDelta",
                    "unknownCollectionDeltaCount",
                    "p5P9Writes",
                    "p11Writes",
                    "p12Writes"),
                ["Freshness"] = SelectReviewFields(
                    review,
                    "Freshness",
                    "coherentSnapshot",
                    "sourceSetSha256",
                    "expectedAlgorithmSha256",
                    "candidatePinsFresh"),
                ["Permission"] = SelectReviewFields(
                    review,
                    "Permission",
                    "actorAlias",
                    "rowCountBeforeRedaction",
                    "rowCountAfterRedaction"),
                ["Verdict"] = SelectReviewFields(
                    review,
                    "Verdict",
                    "outcome",
                    "harnessVerdict")
            }
        };
    }

    private static JsonObject SelectReviewFields(
        JsonObject review,
        string section,
        params string[] fields)
    {
        var source = review[section]?.AsObject()
            ?? throw new InvalidOperationException(
                $"P10 semantic review section '{section}' is missing.");
        var selected = new JsonObject();
        foreach (var field in fields)
        {
            if (!source.TryGetPropertyValue(field, out var value))
            {
                if (field is "sourceSetSha256" or
                    "expectedAlgorithmSha256")
                {
                    selected[field] = null;
                    continue;
                }
                throw new InvalidOperationException(
                    $"P10 semantic review field '{section}.{field}' is missing.");
            }
            selected[field] = value?.DeepClone();
        }
        return selected;
    }

    private async Task<P10ArtifactPin> PinAsync(
        string fullPath,
        string purpose,
        CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        return new P10ArtifactPin(
            Path.GetRelativePath(_paths.WorkspaceRoot, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/'),
            HashBytes(bytes),
            purpose);
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
            if (!string.Equals(
                    extension,
                    ".json",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    extension,
                    ".log",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    extension,
                    ".txt",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string text;
            try
            {
                text = File.ReadAllText(
                    file,
                    new UTF8Encoding(false, true));
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

    private static JsonElement ExplicitJsonNull()
        => JsonSerializer.SerializeToElement<string?>(
            null,
            EvidenceJson.Options);

    private static string HashNodeSemanticObject<T>(T value)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };
        var element = JsonSerializer.SerializeToElement(value, options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder
                           .UnsafeRelaxedJsonEscaping
                   }))
        {
            WriteNodeCanonical(writer, element);
        }
        return HashBytes(stream.ToArray());
    }

    private static void WriteNodeCanonical(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteNodeCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteNodeCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    "P10 semantic projection contains an unsupported JSON token.");
        }
    }
}

internal sealed record P10ArtifactPin(
    string Path,
    string Sha256,
    string Purpose);

internal sealed record P10RuntimeFilePin(
    string Path,
    string Sha256);

internal sealed record P10ExecutedAssemblies(
    P10RuntimeFilePin Backend,
    P10RuntimeFilePin IntegrationProbe);

internal sealed record P10ImplementationSourceSet(
    IReadOnlyList<P10RuntimeFilePin> Files,
    string SemanticSha256);

internal sealed record P10PermissionPrincipal(
    string UserId,
    string UnitId,
    IReadOnlyList<string> Roles,
    string AccountKind);

internal sealed record P10PrincipalCapture(
    int ExchangeSequence,
    int Status,
    P10PermissionPrincipal? Principal);

internal sealed record P10PermissionProof(
    string CaseId,
    string ProofKind,
    string ActorAlias,
    int PrincipalExchangeSequence,
    JsonElement Principal,
    int PrincipalStatus,
    int CaseStatus,
    string CaseBodySha256,
    JsonElement PersistedAuthorizationSnapshotSha256,
    string PermissionSnapshotSha256);
