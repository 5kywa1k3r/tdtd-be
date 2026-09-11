using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Services.StatisticsReconciliation;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    internal const string CreateRouteId = "P10_RECONCILIATION_CREATE";
    internal const string ListRouteId = "P10_RECONCILIATION_LIST";
    internal const string ReadRouteId = "P10_RECONCILIATION_READ";
    internal const string CancelRouteId = "P10_RECONCILIATION_CANCEL";
    internal const string WorkerClaimRouteId = "P10_RECONCILIATION_WORKER_CLAIM";
    internal const string WorkerHeartbeatRouteId = "P10_RECONCILIATION_WORKER_HEARTBEAT";
    internal const string WorkerRetryRouteId = "P10_RECONCILIATION_WORKER_RETRY";
    internal const string WorkerPublishRouteId = "P10_RECONCILIATION_WORKER_PUBLISH";
    internal const string FoundationCapability = "SOURCE_TO_RESULT_RECONCILIATION";

    private async Task<P10CandidatePins> LoadCandidatePinsAsync(CancellationToken ct)
    {
        var root = Path.Combine(
            HistoricalWorkspaceRoot(), ".p10-artifacts", "catalog-candidate", ChainId, PromptId);
        var stagePath = Path.Combine(root, "stage-lock.json");
        var raw = await File.ReadAllBytesAsync(stagePath, ct);
        using var document = JsonDocument.Parse(raw);
        var stage = document.RootElement;
        HarnessAssert.Equal("P10_CANDIDATE_STAGE_V1", RequiredString(stage, "schemaVersion"), "P10 stage schema");
        HarnessAssert.Equal("P10-01", RequiredString(stage, "stagePrompt"), "P10 stage prompt");
        HarnessAssert.Equal("1.7", RequiredString(stage, "version"), "P10 candidate version");
        HarnessAssert.True(stage.GetProperty("promotionIds").GetArrayLength() == 0, "P10-01 candidate cannot contain promotions");
        HarnessAssert.True(!stage.GetProperty("sealed").GetBoolean(), "P10-01 candidate cannot be sealed");
        var catalog = stage.GetProperty("catalog");
        var schema = stage.GetProperty("schema");
        var generator = stage.GetProperty("generator");
        var evidence = stage.GetProperty("evidence");
        var candidate = new P10CandidatePins(
            RequiredString(stage, "stageId"),
            HistoricalWorkspacePath(RequiredString(catalog, "path")),
            RequiredString(catalog, "rawSha256"),
            RequiredString(catalog, "semanticSha256"),
            HistoricalWorkspacePath(RequiredString(schema, "path")),
            RequiredString(schema, "rawSha256"),
            RequiredString(schema, "semanticSha256"),
            stagePath,
            HashBytes(raw),
            HistoricalWorkspacePath(RequiredString(generator, "path")),
            RequiredString(generator, "sha256"),
            HistoricalWorkspacePath(RequiredString(evidence, "path")),
            RequiredString(evidence, "sha256"));
        await AssertFileShaAsync(candidate.CatalogPath, candidate.CatalogRawSha256, ct);
        await AssertFileShaAsync(candidate.SchemaPath, candidate.SchemaRawSha256, ct);
        await AssertFileShaAsync(candidate.GeneratorPath, candidate.GeneratorSha256, ct);
        await AssertFileShaAsync(candidate.EvidencePath, candidate.EvidenceSha256, ct);
        return candidate;
    }

    private async Task PrepareHistoricalContentRootAsync(CancellationToken ct)
    {
        var workspaceRoot = GuardedOwnedPath(
            _iterationRoot,
            "p10-core-historical-workspace");
        if (Directory.Exists(workspaceRoot) &&
            Directory.EnumerateFileSystemEntries(workspaceRoot).Any())
        {
            throw new InvalidOperationException(
                "P10 historical content-root fixture is not fresh.");
        }
        Directory.CreateDirectory(workspaceRoot);
        var contentRoot = GuardedOwnedPath(workspaceRoot, "tdtd-be");
        Directory.CreateDirectory(contentRoot);

        var pinnedFiles = new (string Relative, string Sha256, bool Archived)[]
        {
            ("tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json", StatisticReconciliationCapabilityActivation.RequiredCurrentRawSha256, false),
            ("tdtd-be/Contracts/DynamicFormFlow/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json", StatisticReconciliationCapabilityActivation.RequiredLockRawSha256, false),
            ("docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json", StatisticReconciliationCapabilityActivation.RequiredSourceCatalogRawSha256, false),
            ("docs/features/DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json", StatisticReconciliationCapabilityActivation.RequiredSourceSchemaRawSha256, false),
            ("docs/features/p10-reconcile/FULL_P10_RECONCILE_PROMPT_MANIFEST.json", StatisticReconciliationCapabilityActivation.RequiredManifestRawSha256, true),
            ($".p10-artifacts/handoffs/{ChainId}/P10-00.attempt-001.json", StatisticReconciliationCapabilityActivation.RequiredPreviousHandoffRawSha256, false),
            ($".p10-artifacts/baseline/{ChainId}/P10-00.baseline.json", StatisticReconciliationCapabilityActivation.RequiredBaselineRawSha256, false),
            ("scripts/generate-p10-stage0-candidate.mjs", StatisticReconciliationCapabilityActivation.RequiredGeneratorRawSha256, false),
            ($".p10-artifacts/catalog-candidate/{ChainId}/{PromptId}/catalog.json", StatisticReconciliationCapabilityActivation.RequiredCatalogRawSha256, false),
            ($".p10-artifacts/catalog-candidate/{ChainId}/{PromptId}/schema.json", StatisticReconciliationCapabilityActivation.RequiredSchemaRawSha256, false),
            ($".p10-artifacts/catalog-candidate/{ChainId}/{PromptId}/candidate-evidence.json", StatisticReconciliationCapabilityActivation.RequiredEvidenceRawSha256, false),
            ($".p10-artifacts/catalog-candidate/{ChainId}/{PromptId}/stage-lock.json", StatisticReconciliationCapabilityActivation.RequiredStageLockRawSha256, false)
        };
        foreach (var file in pinnedFiles)
        {
            var source = file.Archived
                ? GuardedWorkspacePath(
                    $".p10-artifacts/historical-sha256/{file.Sha256}.blob")
                : GuardedWorkspacePath(file.Relative);
            await CopyExactFixtureFileAsync(
                source,
                GuardedOwnedPath(workspaceRoot, file.Relative),
                file.Sha256,
                ct);
        }

        await CopyCurrentFixtureFileAsync(
            GuardedWorkspacePath("tdtd-be/tdtd-be.csproj"),
            GuardedOwnedPath(contentRoot, "tdtd-be.csproj"),
            ct);
        var appSettings = Directory
            .EnumerateFiles(_paths.BackendRoot, "appsettings*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        HarnessAssert.True(
            appSettings.Any(path => string.Equals(
                Path.GetFileName(path),
                "appsettings.json",
                StringComparison.OrdinalIgnoreCase)),
            "P10 historical fixture requires appsettings.json.");
        foreach (var source in appSettings)
        {
            await CopyCurrentFixtureFileAsync(
                source,
                GuardedOwnedPath(contentRoot, Path.GetFileName(source)),
                ct);
        }

        _historicalWorkspaceRoot = workspaceRoot;
        _historicalContentRoot = contentRoot;
    }

    private async Task CopyExactFixtureFileAsync(
        string source,
        string target,
        string expectedSha256,
        CancellationToken ct)
    {
        var bytes = await ReadRegularFixtureFileAsync(source, ct);
        HarnessAssert.Equal(
            expectedSha256,
            HashBytes(bytes),
            $"P10 historical fixture source SHA drift: {source}");
        await WriteOwnedFixtureFileAsync(target, bytes, ct);
        HarnessAssert.Equal(
            expectedSha256,
            HashBytes(await File.ReadAllBytesAsync(target, ct)),
            $"P10 historical fixture target SHA drift: {target}");
    }

    private async Task CopyCurrentFixtureFileAsync(
        string source,
        string target,
        CancellationToken ct)
    {
        var bytes = await ReadRegularFixtureFileAsync(source, ct);
        var expectedSha256 = HashBytes(bytes);
        await WriteOwnedFixtureFileAsync(target, bytes, ct);
        HarnessAssert.Equal(
            expectedSha256,
            HashBytes(await File.ReadAllBytesAsync(target, ct)),
            $"P10 current fixture target SHA drift: {target}");
    }

    private static async Task<byte[]> ReadRegularFixtureFileAsync(
        string source,
        CancellationToken ct)
    {
        if (!File.Exists(source) ||
            (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"P10 historical fixture source is missing or reparsed: {source}");
        }
        return await File.ReadAllBytesAsync(source, ct);
    }

    private static async Task WriteOwnedFixtureFileAsync(
        string target,
        byte[] bytes,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var stream = new FileStream(
            target,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private string GuardedWorkspacePath(string relative)
        => GuardedOwnedPath(_paths.WorkspaceRoot, relative);

    private static string GuardedOwnedPath(string root, string relative)
    {
        root = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var escaped = Path.GetRelativePath(root, path);
        if (escaped is "." or ".." ||
            escaped.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            Path.IsPathRooted(escaped))
        {
            throw new InvalidOperationException(
                $"P10 historical fixture path escaped its owned root: {relative}");
        }
        return path;
    }

    private string HistoricalWorkspaceRoot()
        => _historicalWorkspaceRoot
           ?? throw new HarnessCaseNotRunnableException(
               "P10 historical workspace is unavailable.");

    private string HistoricalContentRoot()
        => _historicalContentRoot
           ?? throw new HarnessCaseNotRunnableException(
               "P10 historical content root is unavailable.");

    private string HistoricalWorkspacePath(string relative)
        => GuardedOwnedPath(HistoricalWorkspaceRoot(), relative);
    private async Task AwaitInfrastructureAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await RequireDatabase().RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
                return;
            }
            catch (Exception exception)
            {
                last = exception;
                await Task.Delay(200, ct);
            }
        }
        throw new TimeoutException($"P10 infrastructure did not quiesce: {last?.Message}");
    }

    private async Task<P10StoreSnapshot> CaptureProtectedStoreSnapshotAsync(CancellationToken ct)
    {
        var database = RequireDatabase();
        var names = await (await database.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        var protectedNames = names
            .Where(name => !string.Equals(name, RunCollection, StringComparison.Ordinal) &&
                           !string.Equals(name, "work_report_statistic_reconciliation_observations", StringComparison.Ordinal) &&
                           !string.Equals(name, "work_report_statistic_reconciliation_reviews", StringComparison.Ordinal) &&
                           !string.Equals(name, "work_report_statistic_reconciliation_exports", StringComparison.Ordinal) &&
                           !string.Equals(name, "refresh_tokens", StringComparison.Ordinal) &&
                           !name.StartsWith("system.", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in protectedNames)
        {
            var collection = database.GetCollection<BsonDocument>(name);
            var documents = await collection.Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(new BsonDocument("_id", 1))
                .ToListAsync(ct);
            counts[name] = documents.Count;
            hashes[name] = HashBytes(documents.SelectMany(document => document.ToBson()).ToArray());
        }
        var semantic = JsonSerializer.Serialize(new { counts, hashes }, EvidenceJson.Options);
        return new P10StoreSnapshot(counts, hashes, HashText(semantic));
    }

    private async Task RunCaseAsync(
        string caseId,
        IReadOnlyList<string> requirements,
        IReadOnlyList<string> oracles,
        Func<Task<P10CaseObservation>> action)
    {
        var protectedBefore = await CaptureProtectedStoreSnapshotAsync(
            CancellationToken.None);
        var runsBefore = await CountRunsAsync(CancellationToken.None);
        P10CaseObservation? observation = null;
        P10PrincipalCapture? principalCapture = null;
        await _cases.RunAsync(caseId, async () =>
        {
            observation = await action();
            principalCapture = await CaptureCasePrincipalAsync(
                observation.ActorAlias,
                CancellationToken.None);
            return new CaseObservation(observation.Detail, observation.Fingerprint);
        });
        var protectedAfter = await CaptureProtectedStoreSnapshotAsync(
            CancellationToken.None);
        var runsAfter = await CountRunsAsync(CancellationToken.None);
        var result = _cases.Results[^1];
        observation ??= new P10CaseObservation(
            500,
            result.Detail,
            result.Fingerprint,
            0,
            0,
            0,
            "system");
        var status = result.Verdict == HarnessVerdict.DAT ? "PASS" : "FAIL";
        var unknownDelta = string.Equals(
            protectedBefore.SemanticSha256,
            protectedAfter.SemanticSha256,
            StringComparison.Ordinal)
            ? observation.UnknownCollectionDeltaCount
            : observation.UnknownCollectionDeltaCount + 1;
        var requestHash = observation.RequestHashSha256 ??
            HashText($"P10-EVIDENCE-REQUEST:{caseId}:{result.Fingerprint}");
        var absenceBasis = string.Join(
            "|",
            caseId,
            runsBefore,
            runsAfter,
            protectedBefore.SemanticSha256,
            protectedAfter.SemanticSha256);
        var receiptHash = observation.ReceiptHashSha256 ??
            HashText($"P10-RECEIPT-ABSENCE:{absenceBasis}");
        var stateHash = observation.StateHashSha256 ??
            HashText($"P10-STATE-ABSENCE:{absenceBasis}");
        var apiBodySha256 = HashText(JsonSerializer.Serialize(
            RequireApi().Exchanges
                .Where(exchange => string.Equals(
                    exchange.CaseId,
                    caseId,
                    StringComparison.Ordinal))
                .Select(exchange => new
                {
                    exchange.Method,
                    exchange.Path,
                    exchange.RequestBody,
                    exchange.StatusCode,
                    exchange.ResponseBody
                })
                .ToArray(),
            EvidenceJson.Options));
        var cleanupHandleSetSha256 =
            StatisticReconciliationCanonicalJson.HashObject(
                _cleanupHandles
                    .Distinct()
                    .OrderBy(handle => handle.Collection, StringComparer.Ordinal)
                    .ThenBy(handle => handle.Id, StringComparer.Ordinal)
                    .ToArray());
        if (principalCapture is null)
        {
            throw new InvalidOperationException(
                $"P10 permission principal capture is missing for {caseId}.");
        }
        var persistedAuthorizationSnapshotSha256 =
            await FindPersistedAuthorizationSnapshotAsync(
                observation,
                principalCapture,
                CancellationToken.None);
        var permissionProofKind =
            persistedAuthorizationSnapshotSha256 is not null
                ? "PERSISTED_RUN_AUTHORIZATION"
                : string.Equals(
                    observation.ActorAlias,
                    "anonymous",
                    StringComparison.Ordinal)
                    ? "ANONYMOUS_AUTH_DECISION"
                    : "SERVER_AUTH_DECISION";
        var principalElement = principalCapture.Principal is null
            ? ExplicitJsonNull()
            : JsonSerializer.SerializeToElement(
                principalCapture.Principal,
                EvidenceJson.Options);
        var persistedAuthorizationElement =
            persistedAuthorizationSnapshotSha256 is null
                ? ExplicitJsonNull()
                : JsonSerializer.SerializeToElement(
                    persistedAuthorizationSnapshotSha256,
                    EvidenceJson.Options);
        var semanticPermissionSha = HashNodeSemanticObject(new
        {
            schema = "P10_SERVER_PERMISSION_PROOF_V1",
            caseId,
            proofKind = permissionProofKind,
            principal = principalElement,
            principalStatus = principalCapture.Status,
            caseStatus = observation.ApiStatus,
            caseBodySha256 = apiBodySha256,
            persistedAuthorizationSnapshotSha256 =
                persistedAuthorizationElement
        });
        _permissionProofs.Add(new P10PermissionProof(
            caseId,
            permissionProofKind,
            observation.ActorAlias,
            principalCapture.ExchangeSequence,
            principalElement,
            principalCapture.Status,
            observation.ApiStatus,
            apiBodySha256,
            persistedAuthorizationElement,
            semanticPermissionSha));
        var reviewRecord = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Identity"] = new { caseId, groupId = GroupId, ownerPrompt = PromptId, fixtureAlias = "P10_AUTONOMOUS_FIXTURE" },
            ["Config"] = new { candidateVersion = "1.7", candidateStage = "P10-01", currentVersion = "1.6", phase = 8 },
            ["Expected"] = new { outcome = "PASS", contract = result.Fingerprint },
            ["Actual"] = new
            {
                apiStatus = observation.ApiStatus,
                outcome = status,
                contract = result.Fingerprint,
                requestHashSha256 = requestHash,
                receiptHashSha256 = receiptHash,
                stateHashSha256 = stateHash,
                apiBodySha256
            },
            ["Delta"] = new
            {
                p10RunDelta = runsAfter - runsBefore,
                unknownCollectionDeltaCount = unknownDelta,
                p5P9Writes = 0,
                p11Writes = 0,
                p12Writes = 0,
                protectedBeforeSha256 = protectedBefore.SemanticSha256,
                protectedAfterSha256 = protectedAfter.SemanticSha256
            },
            ["Freshness"] = new
            {
                coherentSnapshot = false,
                sourceSetSha256 = ExplicitJsonNull(),
                expectedAlgorithmSha256 = ExplicitJsonNull(),
                candidatePinsFresh = true
            },
            ["Permission"] = new
            {
                actorAlias = observation.ActorAlias,
                permissionSnapshotSha256 = semanticPermissionSha,
                permissionProofKind,
                persistedAuthorizationSnapshotSha256 =
                    persistedAuthorizationElement,
                rowCountBeforeRedaction = observation.RowCountBeforeRedaction,
                rowCountAfterRedaction = observation.RowCountAfterRedaction
            },
            ["Verdict"] = new
            {
                outcome = status,
                harnessVerdict = result.Verdict.ToString(),
                reason = result.Detail,
                durationMs = result.DurationMs,
                cleanupHandleSetSha256
            }
        };
        _caseEvidence.Add(new P10CaseEvidence(
            caseId,
            GroupId,
            PromptId,
            status,
            requirements,
            oracles,
            SourceFingerprintSha256(),
            requestHash,
            receiptHash,
            stateHash,
            apiBodySha256,
            cleanupHandleSetSha256,
            result.DurationMs,
            semanticPermissionSha,
            observation.ApiStatus,
            new P10CaseStoreDelta(
                runsAfter - runsBefore,
                unknownDelta,
                0,
                0,
                0),
            reviewRecord));
    }

    private async Task<P10PrincipalCapture> CaptureCasePrincipalAsync(
        string actorAlias,
        CancellationToken ct)
    {
        var isAnonymous = string.Equals(
            actorAlias,
            "anonymous",
            StringComparison.Ordinal);
        var response = await RequireApi().GetAsync(
            "api/me",
            isAnonymous ? null : Actor(actorAlias).Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response,
            isAnonymous
                ? HttpStatusCode.Unauthorized
                : HttpStatusCode.OK,
            $"P10 {actorAlias} principal proof");
        var exchange = RequireApi().Exchanges[^1];
        HarnessAssert.Equal(
            "api/me",
            exchange.Path,
            $"P10 {actorAlias} principal exchange path");
        if (isAnonymous)
        {
            return new P10PrincipalCapture(
                exchange.Sequence,
                (int)response.StatusCode,
                null);
        }

        var principal = response.Json as JsonObject
            ?? throw new InvalidOperationException(
                $"P10 {actorAlias} principal body is not an object.");
        var roles = principal["roles"]?.AsArray()
            .Select(value => value?.GetValue<string>() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray()
            ?? [];
        return new P10PrincipalCapture(
            exchange.Sequence,
            (int)response.StatusCode,
            new P10PermissionPrincipal(
                ApiHarnessClient.RequiredString(principal, "userId"),
                ApiHarnessClient.RequiredString(principal, "unitId"),
                roles,
                ApiHarnessClient.RequiredString(principal, "accountKind")));
    }

    private async Task<string?> FindPersistedAuthorizationSnapshotAsync(
        P10CaseObservation observation,
        P10PrincipalCapture principalCapture,
        CancellationToken ct)
    {
        if (!StatisticReconciliationCanonicalJson.IsCanonicalSha256(
                observation.StateHashSha256) ||
            principalCapture.Principal is null)
        {
            return null;
        }
        var run = await RequireDatabase()
            .GetCollection<BsonDocument>(RunCollection)
            .Find(new BsonDocument(
                "stateHash",
                observation.StateHashSha256))
            .FirstOrDefaultAsync(ct);
        if (run is null)
            return null;
        var principal = principalCapture.Principal;
        if (!string.Equals(
                BsonText(run, "actorUserId"),
                principal.UserId,
                StringComparison.Ordinal) ||
            !string.Equals(
                BsonText(run, "tenantUnitId"),
                principal.UnitId,
                StringComparison.Ordinal) ||
            !string.Equals(
                BsonText(run, "workId"),
                Fixture().WorkId,
                StringComparison.Ordinal) ||
            !string.Equals(
                BsonText(run, "scopeAssignmentId"),
                Fixture().ScopeAssignmentId,
                StringComparison.Ordinal))
        {
            return null;
        }
        var value = BsonText(run, "authorizationSnapshotHash");
        return StatisticReconciliationCanonicalJson.IsCanonicalSha256(value)
            ? value
            : null;
    }

    private string SourceFingerprintSha256()
    {
        var fixture = Fixture();
        return HashNodeSemanticObject(new
        {
            chainId = ChainId,
            promptId = PromptId,
            groupId = GroupId,
            sourceIdentity = fixture,
            p9CatalogRawSha256 = P9CatalogRawSha256,
            candidateIdentity = CandidateSourceIdentity(),
            executionIdentity = ExecutionSourceIdentity(),
            sourceSetSha256 = ExplicitJsonNull(),
            expectedAlgorithmSha256 = ExplicitJsonNull(),
            coherentSnapshot = false
        });
    }

    private object CandidateSourceIdentity()
    {
        var candidate = Candidate();
        return new
        {
            stageId = candidate.StageId,
            catalogRawSha256 = candidate.CatalogRawSha256,
            catalogSemanticSha256 = candidate.CatalogSemanticSha256,
            schemaRawSha256 = candidate.SchemaRawSha256,
            schemaSemanticSha256 = candidate.SchemaSemanticSha256,
            stageLockSha256 = candidate.StageLockSha256,
            generatorSha256 = candidate.GeneratorSha256,
            evidenceSha256 = candidate.EvidenceSha256
        };
    }

    private object ExecutionSourceIdentity()
        => new
        {
            backendAssemblySha256 = ExecutedAssemblies().Backend.Sha256,
            integrationProbeAssemblySha256 =
                ExecutedAssemblies().IntegrationProbe.Sha256,
            implementationSourceSetSha256 =
                ImplementationSourceSet().SemanticSha256
        };

    private void CaptureRuntimeImplementationPins()
    {
        _executedAssemblies = new P10ExecutedAssemblies(
            PinRuntimeFile(_paths.ResolveBackendDll()),
            PinRuntimeFile(typeof(P10ReconciliationCoreProbe)
                .Assembly.Location));
        var files = ImplementationSourcePaths
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => PinRuntimeFile(WorkspacePath(path)))
            .ToArray();
        _implementationSourceSet = new P10ImplementationSourceSet(
            files,
            HashNodeSemanticObject(files));
    }

    private P10RuntimeFilePin PinRuntimeFile(string fullPath)
    {
        var absolute = Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(
                _paths.WorkspaceRoot,
                absolute)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (relative == ".." ||
            relative.StartsWith("../", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException(
                $"P10 runtime pin escaped the workspace: {absolute}");
        }
        return new P10RuntimeFilePin(
            relative,
            HashBytes(File.ReadAllBytes(absolute)));
    }

    private P10ExecutedAssemblies ExecutedAssemblies()
        => _executedAssemblies
           ?? throw new InvalidOperationException(
               "P10 executed assembly pins are unavailable.");

    private P10ImplementationSourceSet ImplementationSourceSet()
        => _implementationSourceSet
           ?? throw new InvalidOperationException(
               "P10 implementation source-set pins are unavailable.");

    private string ReconciliationBasePath(string? workId = null, string? scopeId = null)
    {
        var fixture = Fixture();
        return $"api/works/{workId ?? fixture.WorkId}/statistics/{scopeId ?? fixture.ScopeAssignmentId}/reconciliations";
    }

    private JsonObject NewCreateRequest(string commandId, Action<JsonObject>? mutate = null)
    {
        var fixture = Fixture();
        var body = new JsonObject
        {
            ["commandId"] = commandId,
            ["p9ResultKind"] = "DIRECT",
            ["p9ResultId"] = fixture.P9ResultId,
            ["p9RunId"] = fixture.P9RunId,
            ["conceptKey"] = fixture.ConceptKey,
            ["grain"] = fixture.Grain,
            ["filter"] = new JsonObject { ["periodKey"] = fixture.PeriodKey }
        };
        mutate?.Invoke(body);
        return body;
    }

    private async Task<long> CountRunsAsync(CancellationToken ct)
        => await RequireDatabase().GetCollection<BsonDocument>(RunCollection)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: ct);

    private async Task<BsonDocument> LoadRunAsync(string id, CancellationToken ct)
        => await RequireDatabase().GetCollection<BsonDocument>(RunCollection)
            .Find(new BsonDocument("_id", ObjectId.Parse(id)))
            .SingleAsync(ct);

    private async Task<ApiHarnessResponse> CreateRunAsync(
        JsonObject request,
        string token,
        CancellationToken ct,
        string? workId = null,
        string? scopeId = null,
        IReadOnlyDictionary<string, string>? headers = null)
        => await RequireApi().PostAsync(
            ReconciliationBasePath(workId, scopeId),
            request.DeepClone(),
            token,
            headers,
            ct);

    private async Task<JsonObject> EvaluateActivationAsync(
        ApiHarnessClient api,
        string capabilityId,
        string routeId,
        string token,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = await api.PostAsync(
            "api/testing/p10/statistic-reconciliations/activation/evaluate",
            new { capabilityId, routeId },
            token,
            headers,
            ct);
        ApiHarnessClient.ExpectStatus(
            response,
            HttpStatusCode.OK,
            $"P10 activation {capabilityId}/{routeId}");
        return ApiHarnessClient.RequiredObject(
            response.Json,
            "P10 activation response");
    }

    private static void ExpectError(
        ApiHarnessResponse response,
        HttpStatusCode status,
        string errorCode,
        string context)
    {
        ApiHarnessClient.ExpectStatus(response, status, context);
        var actual = ApiHarnessClient.FindStringRecursive(
            response.Json,
            "errorCode") ?? ApiHarnessClient.FindStringRecursive(
            response.Json,
            "code");
        HarnessAssert.Equal(errorCode, actual, $"{context} error code");
    }

    private static string BsonText(BsonDocument document, string field)
    {
        if (!document.TryGetValue(field, out var value) || value.IsBsonNull)
            return string.Empty;
        return value.IsObjectId ? value.AsObjectId.ToString() : value.AsString;
    }

    private static long BsonLong(BsonDocument document, string field)
    {
        if (!document.TryGetValue(field, out var value) || !value.IsNumeric)
            throw new InvalidOperationException($"BSON field '{field}' is not numeric.");
        return value.ToInt64();
    }

    private static string EvidenceRequestHash(string route, JsonNode? body)
    {
        JsonElement? element = body is null
            ? null
            : JsonSerializer.Deserialize<JsonElement>(body.ToJsonString());
        return StatisticReconciliationCanonicalJson.HashObject(new
        {
            schema = "P10_CORE_EVIDENCE_REQUEST_V1",
            route,
            body = element
        });
    }

    private static P10CaseObservation ObserveRun(
        ApiHarnessResponse response,
        BsonDocument run,
        string detail,
        string fingerprint,
        string actorAlias = "executor",
        int beforeRedaction = 1,
        int afterRedaction = 1)
        => new(
            (int)response.StatusCode,
            detail,
            fingerprint,
            0,
            beforeRedaction,
            afterRedaction,
            actorAlias,
            BsonText(run, "requestHash"),
            BsonText(run, "receiptResponseHash"),
            BsonText(run, "stateHash"));

    private async Task<T> RunVariantAsync<T>(
        string caseId,
        string name,
        BackendServerOptions options,
        Func<ApiHarnessClient, string, Task<T>> action,
        CancellationToken ct)
    {
        var root = Path.Combine(_iterationRoot, "variants", name);
        Directory.CreateDirectory(root);
        var environmentName = options.EnvironmentNameOverride ?? "Testing";
        var isolatedDatabase = string.Equals(
                environmentName,
                "Testing",
                StringComparison.Ordinal)
            ? options.MongoDatabaseNameOverride
            : $"{RequireMongo().DatabaseName}_v_{HashBytes(
                Encoding.UTF8.GetBytes(name))[..8]}";
        HarnessAssert.True(
            isolatedDatabase is null ||
            isolatedDatabase.StartsWith("tdtd_p10_", StringComparison.Ordinal),
            "P10 variant database escaped the owned prefix.");
        var candidate = isolatedDatabase is not null &&
                        options.P10ReconciliationCandidate is not null
            ? options.P10ReconciliationCandidate with
            {
                ExpectedDatabase = isolatedDatabase
            }
            : options.P10ReconciliationCandidate;
        BackendServerLease? backend = null;
        ApiHarnessClient? api = null;
        try
        {
            backend = await BackendServerLease.StartAsync(
                _paths,
                root,
                $"{_runKey}_{name}",
                RequireMongo(),
                ct,
                options with
                {
                    SkipMongoIndexInitializationForTesting = true,
                    SuppressTestingFixedUtcNow = !string.Equals(
                        environmentName,
                        "Testing",
                        StringComparison.Ordinal),
                    MongoDatabaseNameOverride = isolatedDatabase,
                    P10ReconciliationCandidate = candidate,
                    ContentRootPathOverride = HistoricalContentRoot()
                });
            api = new ApiHarnessClient(backend.BaseUri);
            var token = string.Equals(
                    environmentName,
                    "Testing",
                    StringComparison.Ordinal)
                ? await api.LoginAsync(
                    "admin",
                    _bootstrapPassword ?? throw new HarnessCaseNotRunnableException(
                        "P10 bootstrap password is unavailable."),
                    ct)
                : await BootstrapVariantAdminAsync(api, backend, ct);
            RememberSecret(token);
            var result = await action(api, token);
            _variants.Add(new P10VariantEvidence(
                caseId,
                name,
                api.Exchanges.LastOrDefault()?.StatusCode,
                backend.ProcessId,
                backend.Port,
                "PASS",
                null));
            return result;
        }
        catch (Exception exception)
        {
            _variants.Add(new P10VariantEvidence(
                caseId,
                name,
                api?.Exchanges.LastOrDefault()?.StatusCode,
                backend?.ProcessId,
                backend?.Port,
                "FAIL",
                $"{exception.GetType().Name}: {exception.Message}"));
            throw;
        }
        finally
        {
            api?.Dispose();
            if (backend is not null)
            {
                await backend.StopAsync();
                await backend.DisposeAsync();
            }
            if (isolatedDatabase is not null && !string.Equals(
                    isolatedDatabase,
                    RequireMongo().DatabaseName,
                    StringComparison.Ordinal))
            {
                await RequireMongo().Client.DropDatabaseAsync(
                    isolatedDatabase,
                    cancellationToken: CancellationToken.None);
            }
        }
    }

    private async Task<string> BootstrapVariantAdminAsync(
        ApiHarnessClient api,
        BackendServerLease backend,
        CancellationToken ct)
    {
        var bootstrap = await api.PostAsync(
            "api/system/bootstrap",
            new { },
            headers: new Dictionary<string, string>
            {
                ["X-System-Bootstrap-Key"] = backend.BootstrapKey
            },
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            bootstrap,
            HttpStatusCode.OK,
            "P10 variant system bootstrap");
        var password = ApiHarnessClient.RequiredString(
            bootstrap.Json,
            "defaultPassword");
        RememberSecret(password);
        var token = await api.LoginAsync("admin", password, ct);
        RememberSecret(token);
        return token;
    }
    private static string RequireResponseString(ApiHarnessResponse response, string property)
        => ApiHarnessClient.FindStringRecursive(response.Json, property)
           ?? throw new InvalidOperationException($"Response property '{property}' is missing. Body={response.Body}");

    private static int RequireResponseInt(ApiHarnessResponse response, string property)
        => ApiHarnessClient.FindIntRecursive(response.Json, property)
           ?? throw new InvalidOperationException($"Response property '{property}' is missing. Body={response.Body}");

    private async Task AssertFileShaAsync(string fullPath, string expectedSha, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        HarnessAssert.Equal(expectedSha, HashBytes(bytes), $"File SHA drift: {fullPath}");
    }

    private string WorkspacePath(string relative)
        => Path.GetFullPath(Path.Combine(_paths.WorkspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RequiredString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!;
        throw new InvalidOperationException($"Candidate property '{property}' is missing.");
    }

    private static string HashBytes(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private void RememberSecret(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length >= 8) _artifactSecrets.Add(value);
    }

    private static IReadOnlyList<string> Req(params string[] ids) => ids;
    private static IReadOnlyList<string> O(params string[] ids) => ids;
}

internal sealed record P10CaseObservation(
    int ApiStatus,
    string Detail,
    string Fingerprint,
    int UnknownCollectionDeltaCount = 0,
    int RowCountBeforeRedaction = 0,
    int RowCountAfterRedaction = 0,
    string ActorAlias = "executor",
    string? RequestHashSha256 = null,
    string? ReceiptHashSha256 = null,
    string? StateHashSha256 = null);

internal sealed record P10CaseStoreDelta(
    long P10RunDelta,
    int UnknownCollectionDeltaCount,
    int P5P9Writes,
    int P11Writes,
    int P12Writes);

internal sealed record P10CaseEvidence(
    string CaseId,
    string GroupId,
    string OwnerPrompt,
    string Status,
    IReadOnlyList<string> RequirementIds,
    IReadOnlyList<string> Oracles,
    string SourceFingerprintSha256,
    string RequestHashSha256,
    string ReceiptHashSha256,
    string StateHashSha256,
    string ApiBodySha256,
    string CleanupHandleSetSha256,
    long DurationMs,
    string AuthorizationSnapshotSha256,
    int ApiStatus,
    P10CaseStoreDelta StoreDelta,
    IReadOnlyDictionary<string, object?> ReviewRecord);
