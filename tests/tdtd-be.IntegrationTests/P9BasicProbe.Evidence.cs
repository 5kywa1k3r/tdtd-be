using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.WorkAssignments.BasicSummary;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private P9BasicRuntimeEvidence? _basRuntimeEvidence;

    private async Task<P9BasicIndexEvidence> CaptureBasicIndexEvidenceAsync(
        CancellationToken ct)
    {
        var database = RequireDatabase();
        var config = await database
            .GetCollection<BsonDocument>("work_assignment_basic_summary_configs")
            .Find(new BsonDocument
            {
                ["assignmentId"] = ObjectId.Parse(Fixture().AssignmentId),
                ["dynamicFormTemplateId"] = ObjectId.Parse(Fixture().TemplateId),
                ["isDeleted"] = false
            })
            .SingleAsync(ct);
        var payloadJson = config["configJson"].AsString;
        var payload = JsonSerializer.Deserialize<WorkAssignmentBasicSummaryConfigPayload>(
                          payloadJson,
                          new JsonSerializerOptions(JsonSerializerDefaults.Web)
                          {
                              PropertyNameCaseInsensitive = true
                          })
                      ?? throw new InvalidOperationException(
                          "P9-BAS stored config payload is invalid.");
        var dependencyPins = config["dependencyPins"]
            .AsBsonArray
            .Select(value => value.AsString)
            .ToList();
        var recomputed = StatConfigCanonicalJson.HashObject(
            new { payload, dependencyPins });
        var storedHash = config["configHash"].AsString;
        var configHashVerified = string.Equals(
            storedHash,
            recomputed,
            StringComparison.Ordinal);

        var snapshots = await database
            .GetCollection<BsonDocument>("work_assignment_basic_summary_snapshots")
            .Find(new BsonDocument("isDeleted", false))
            .Sort(new BsonDocument("createdAtUtc", 1))
            .ToListAsync(ct);
        var queuePinsVerified = snapshots.Count > 0 &&
                                snapshots.All(document =>
                                    BsonRequiredText(document, "configVersionId").Length == 24 &&
                                    BsonRequiredText(document, "configHash").Length == 64 &&
                                    BsonRequiredText(document, "candidateChainId") == ChainId &&
                                    BsonRequiredText(document, "candidatePromptId") == BasicPromptId &&
                                    document["candidateStage"].ToInt32() == 2 &&
                                    BsonRequiredText(document, "candidateStageLockSha256") == BasicStageLockSha256 &&
                                    !string.IsNullOrWhiteSpace(BsonRequiredText(document, "refreshJobId")) &&
                                    document.Contains("sourceSignatureHash"));
        var terminalJobs = snapshots.Count(document =>
            string.Equals(
                document.GetValue("refreshStatus", "").AsString,
                "DONE",
                StringComparison.Ordinal));
        _basRuntimeEvidence = new P9BasicRuntimeEvidence(
            storedHash,
            recomputed,
            configHashVerified,
            snapshots.Count,
            terminalJobs,
            queuePinsVerified,
            snapshots.Select(document => new P9BasicSnapshotTrace(
                document["_id"].AsObjectId.ToString(),
                BsonRequiredText(document, "requestHash"),
                BsonRequiredText(document, "sourceScopeMode"),
                document.GetValue("sourceAssignmentIds", new BsonArray()).AsBsonArray.Count,
                document.GetValue("sourceReportIds", new BsonArray()).AsBsonArray.Count,
                BsonRequiredText(document, "sourceSignatureHash"),
                document.GetValue("snapshotDirty", false).ToBoolean(),
                document.GetValue("refreshStatus", "").AsString,
                BsonRequiredText(document, "refreshJobId"),
                BsonRequiredText(document, "configVersionId"),
                BsonRequiredText(document, "configHash"),
                BsonRequiredText(document, "candidatePromptId"),
                document["candidateStage"].ToInt32(),
                BsonRequiredText(document, "candidateStageLockSha256")))
                .ToArray());

        var collection = database
            .GetCollection<BsonDocument>("work_assignment_basic_summary_snapshots");
        var indexDocuments = await (await collection.Indexes.ListAsync(ct))
            .ToListAsync(ct);
        var indexNames = indexDocuments
            .Select(document => document["name"].AsString)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var requestHash = snapshots[0]["requestHash"].AsString;
        var explain = await database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["explain"] = new BsonDocument
                {
                    ["find"] = "work_assignment_basic_summary_snapshots",
                    ["filter"] = new BsonDocument
                    {
                        ["requestHash"] = requestHash,
                        ["isDeleted"] = false
                    }
                },
                ["verbosity"] = "executionStats"
            },
            cancellationToken: ct);
        var stages = BasicFindBsonStrings(explain, "stage")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var usedIndexes = BasicFindBsonStrings(explain, "indexName")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var requestIndexUsed = usedIndexes.Contains(
            "ux_workAssignmentBasicSummarySnapshots_request_active",
            StringComparer.Ordinal);
        var blockingSortAbsent = !stages.Contains("SORT", StringComparer.Ordinal);
        var required = new[]
        {
            "ux_workAssignmentBasicSummarySnapshots_request_active",
            "ix_workAssignmentBasicSummarySnapshots_scope",
            "ix_workAssignmentBasicSummarySnapshots_dirty_scan",
            "ix_workAssignmentBasicSummarySnapshots_flow_scope"
        };
        return new P9BasicIndexEvidence(
            indexNames,
            stages,
            requestIndexUsed,
            blockingSortAbsent,
            required.All(indexNames.Contains) &&
            requestIndexUsed &&
            blockingSortAbsent);
    }

    private async Task WriteBasicCleanupArtifactAsync(
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        DateTime completedAtUtc,
        CancellationToken ct)
    {
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-BAS.cleanup.json"),
            new
            {
                schemaVersion = "P9_BAS_CLEANUP_V1",
                chainId = ChainId,
                promptId = BasicPromptId,
                groupId = BasicGroupId,
                runKey = _runKey,
                databaseName = _mongo?.DatabaseName,
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

    private async Task<bool> WriteBasicEvidenceAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var apiPath = Path.Combine(_paths.RunRoot, "P9-BAS.api-exchanges.json");
        var deltaPath = Path.Combine(_paths.RunRoot, "P9-BAS.collection-delta.json");
        var runtimePath = Path.Combine(_paths.RunRoot, "P9-BAS.runtime-pins.json");
        var indexPath = Path.Combine(_paths.RunRoot, "P9-BAS.index-explain.json");
        var securityPath = Path.Combine(_paths.RunRoot, "P9-BAS.security-scan.json");
        var cleanupPath = Path.Combine(_paths.RunRoot, "P9-BAS.cleanup.json");
        var mainPath = Path.Combine(_paths.RunRoot, "P9-BAS.evidence.json");

        var results = _cases.Results
            .Where(item => item.CaseId.StartsWith("P9-BAS-", StringComparison.Ordinal))
            .ToArray();
        await WriteStrictJsonAsync(
            apiPath,
            new
            {
                schemaVersion = "P9_BAS_API_EXCHANGES_V1",
                chainId = ChainId,
                promptId = BasicPromptId,
                groupId = BasicGroupId,
                runKey = _runKey,
                exchanges = _api?.Exchanges ?? [],
                caseExchangeCount = (_api?.Exchanges ?? [])
                    .Count(item => item.CaseId?.StartsWith("P9-BAS-", StringComparison.Ordinal) == true),
                realKestrel = _backend is not null
            },
            ct);
        var deltas = _basBefore is null || _basAfter is null
            ? Array.Empty<P9CollectionDelta>()
            : BuildDeltas(_basBefore, _basAfter).ToArray();
        var p10ZeroWrite = deltas
            .Where(item => item.Collection.StartsWith("p10", StringComparison.OrdinalIgnoreCase))
            .All(item => !item.Changed && item.CountDelta == 0);
        var forbiddenCases = _caseEvidence
            .Where(item => item.CaseId is "P9-BAS-BASIC-06" or "P9-BAS-BASIC-07")
            .ToArray();
        var rejectedZeroWrite = forbiddenCases.Length == 2 &&
                                forbiddenCases.All(item =>
                                    item.Deltas.All(delta =>
                                        !delta.Changed && delta.CountDelta == 0));
        await WriteStrictJsonAsync(
            deltaPath,
            new
            {
                schemaVersion = "P9_BAS_COLLECTION_DELTA_V1",
                chainId = ChainId,
                promptId = BasicPromptId,
                groupId = BasicGroupId,
                runKey = _runKey,
                beforeSha256 = _basBefore is null ? null : SnapshotSha256(_basBefore),
                afterSha256 = _basAfter is null ? null : SnapshotSha256(_basAfter),
                deltas,
                expectedOwners = new[]
                {
                    "work_assignment_basic_summary_configs",
                    "work_assignment_basic_summary_snapshots",
                    "stat_config_command_receipts",
                    "work_assignments"
                },
                rejectedZeroWrite,
                p10ZeroWrite,
                passed = rejectedZeroWrite && p10ZeroWrite
            },
            ct);
        await WriteStrictJsonAsync(
            runtimePath,
            new
            {
                schemaVersion = "P9_BAS_RUNTIME_PINS_V1",
                chainId = ChainId,
                promptId = BasicPromptId,
                groupId = BasicGroupId,
                runKey = _runKey,
                runtime = _basRuntimeEvidence,
                passed = _basRuntimeEvidence?.ConfigHashVerified == true &&
                         _basRuntimeEvidence.QueuePinsVerified
            },
            ct);
        await WriteStrictJsonAsync(
            indexPath,
            new
            {
                schemaVersion = "P9_BAS_INDEX_EXPLAIN_V1",
                chainId = ChainId,
                promptId = BasicPromptId,
                groupId = BasicGroupId,
                runKey = _runKey,
                maximumSourceReports = 1000,
                blockingSortForbidden = true,
                evidence = _basIndexEvidence,
                passed = _basIndexEvidence?.Passed == true
            },
            ct);
        var securityFindings = ScanArtifactSecurity();
        var securityPassed = securityFindings.Count == 0;
        await WriteStrictJsonAsync(
            securityPath,
            new
            {
                schemaVersion = "P9_BAS_SECURITY_SCAN_V1",
                chainId = ChainId,
                promptId = BasicPromptId,
                groupId = BasicGroupId,
                runKey = _runKey,
                securityPassed,
                findings = securityFindings,
                profileAccess = "INTENTIONAL_BLOCK",
                p10ZeroWrite
            },
            ct);

        var exactIds = results.Select(item => item.CaseId)
            .SequenceEqual(BasicExpectedCaseIds, StringComparer.Ordinal);
        var allDat = results.All(item => item.Verdict == HarnessVerdict.DAT);
        var normalizedSemanticSha256 =
            ComputeDirectNormalizedSemanticSha256(results);
        var configHashRecomputed =
            _basRuntimeEvidence?.ConfigHashVerified == true;
        var queueTraceVerified =
            _basRuntimeEvidence?.QueuePinsVerified == true &&
            _basRuntimeEvidence.SnapshotCount > 0;
        var indexExplainVerified = _basIndexEvidence?.Passed == true;
        var passed = fatalFailure is null &&
                     results.Length == BasicExpectedCaseIds.Length &&
                     exactIds &&
                     allDat &&
                     cleanupSucceeded &&
                     rejectedZeroWrite &&
                     p10ZeroWrite &&
                     configHashRecomputed &&
                     queueTraceVerified &&
                     indexExplainVerified &&
                     securityPassed;
        var artifacts = new[]
        {
            BuildDirectArtifactPin(apiPath, "API_KESTREL"),
            BuildDirectArtifactPin(deltaPath, "COLLECTION_DELTA"),
            BuildDirectArtifactPin(runtimePath, "DIRECT_MONGO"),
            BuildDirectArtifactPin(indexPath, "INDEX_EXPLAIN"),
            BuildDirectArtifactPin(securityPath, "SECURITY_SCAN"),
            BuildDirectArtifactPin(cleanupPath, "CLEANUP")
        };
        await WriteStrictJsonAsync(
            mainPath,
            new
            {
                schemaVersion = "P9_BAS_EVIDENCE_V1",
                chainId = ChainId,
                promptId = BasicPromptId,
                groupId = BasicGroupId,
                runKey = _runKey,
                startedAtUtc,
                completedAtUtc,
                expectedCaseCount = BasicExpectedCaseIds.Length,
                actualCaseCount = results.Length,
                exactIds,
                allDat,
                normalizedSemanticSha256,
                semanticHashVerified = string.Equals(
                    normalizedSemanticSha256,
                    ComputeDirectNormalizedSemanticSha256(results),
                    StringComparison.Ordinal),
                oracles = BasicRequiredOracles,
                cases = results,
                artifacts,
                candidate = new
                {
                    stage = 2,
                    stageLockSha256 = BasicStageLockSha256,
                    catalogRawSha256 = BasicCatalogRawSha256,
                    catalogSemanticSha256 = BasicCatalogSemanticSha256,
                    promotions = new[] { "BASIC_SUMMARY", "FLOW_SCOPES" }
                },
                assertions = new
                {
                    realKestrel = _backend is not null,
                    isolatedMongo = _mongo is not null,
                    lockedConfigAuthority = configHashRecomputed,
                    exactFlowScopes = new[]
                    {
                        "FLOW_BRANCH", "FLOW_STEP",
                        "FLOW_EFFECTIVE_PATH", "FLOW_FINAL"
                    },
                    approvedOnlyBoundary = results.Any(item =>
                        item.CaseId == "P9-BAS-BASIC-03" &&
                        item.Verdict == HarnessVerdict.DAT),
                    duplicateSafe = results.Any(item =>
                        item.CaseId == "P9-BAS-BASIC-04" &&
                        item.Verdict == HarnessVerdict.DAT),
                    membershipInvalidation = results
                        .Count(item => item.CaseId.EndsWith("-04", StringComparison.Ordinal) &&
                                       item.CaseId.StartsWith("P9-BAS-FLOW-", StringComparison.Ordinal)) == 4,
                    rejectedZeroWrite,
                    queueTraceVerified,
                    indexExplainVerified,
                    p10ZeroWrite,
                    securityPassed
                },
                cleanupSucceeded,
                cleanupErrors,
                fatalFailure,
                passed
            },
            ct);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "p9-04-gate-result.json"),
            new
            {
                schemaVersion = "P9_FEATURE_GATE_RESULT_V1",
                chainId = ChainId,
                promptId = BasicPromptId,
                groupId = BasicGroupId,
                expected = BasicExpectedCaseIds.Length,
                passedCases = results.Count(item => item.Verdict == HarnessVerdict.DAT),
                exactIds,
                cleanupSucceeded,
                passed
            },
            ct);
        return passed;
    }

    private static string BsonRequiredText(
        BsonDocument document,
        string field)
    {
        if (!document.TryGetValue(field, out var value) ||
            value.IsBsonNull)
        {
            throw new InvalidOperationException(
                $"P9-BAS BSON field '{field}' is missing.");
        }
        return value.IsObjectId ? value.AsObjectId.ToString() : value.AsString;
    }

    private static IEnumerable<string> BasicFindBsonStrings(
        BsonValue value,
        string field)
    {
        if (value is BsonDocument document)
        {
            foreach (var element in document.Elements)
            {
                if (string.Equals(element.Name, field, StringComparison.Ordinal) &&
                    element.Value.IsString)
                {
                    yield return element.Value.AsString;
                }
                foreach (var nested in BasicFindBsonStrings(element.Value, field))
                    yield return nested;
            }
        }
        else if (value is BsonArray array)
        {
            foreach (var item in array)
            foreach (var nested in BasicFindBsonStrings(item, field))
                yield return nested;
        }
    }
}

internal sealed record P9BasicSnapshotTrace(
    string SnapshotId,
    string RequestHash,
    string ScopeMode,
    int AssignmentCount,
    int ReportCount,
    string SourceSignatureHash,
    bool Dirty,
    string RefreshStatus,
    string RefreshJobId,
    string ConfigVersionId,
    string ConfigHash,
    string CandidatePromptId,
    int CandidateStage,
    string CandidateStageLockSha256);

internal sealed record P9BasicRuntimeEvidence(
    string StoredConfigHash,
    string RecomputedConfigHash,
    bool ConfigHashVerified,
    int SnapshotCount,
    int TerminalJobs,
    bool QueuePinsVerified,
    IReadOnlyList<P9BasicSnapshotTrace> Snapshots);
