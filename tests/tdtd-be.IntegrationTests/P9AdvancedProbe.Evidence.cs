using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.WorkAssignments.AdvancedSummary;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private P9AdvancedRuntimeEvidence? _advRuntimeEvidence;

    private async Task<P9AdvancedIndexEvidence> CaptureAdvancedIndexEvidenceAsync(
        CancellationToken ct)
    {
        var database = RequireDatabase();
        var config = await database
            .GetCollection<BsonDocument>("work_assignment_advanced_summary_configs")
            .Find(new BsonDocument("_id", ObjectId.Parse(_advVersionId)))
            .SingleAsync(ct);
        var payload = JsonSerializer.Deserialize<WorkAssignmentAdvancedSummaryConfigPayload>(
                          config["configJson"].AsString,
                          new JsonSerializerOptions(JsonSerializerDefaults.Web)
                          {
                              PropertyNameCaseInsensitive = true
                          })
                      ?? throw new InvalidOperationException(
                          "P9-ADV stored config payload is invalid.");
        var dependencyPins = config["dependencyPins"].AsBsonArray
            .Select(value => value.AsString)
            .ToArray();
        var recomputedHash = StatConfigCanonicalJson.HashObject(
            new { payload, dependencyPins });
        var storedHash = config["configHash"].AsString;
        var configHashVerified = string.Equals(
            storedHash, recomputedHash, StringComparison.Ordinal);

        var traces = new List<P9AdvancedNodeTrace>();
        foreach (var (collectionName, grain) in AdvancedCollections())
        {
            var nodes = await database.GetCollection<BsonDocument>(collectionName)
                .Find(new BsonDocument
                {
                    ["configVersionId"] = ObjectId.Parse(_advVersionId),
                    ["isDeleted"] = false
                })
                .Sort(new BsonDocument("grainKey", 1))
                .ToListAsync(ct);
            traces.AddRange(nodes.Select(node => new P9AdvancedNodeTrace(
                collectionName,
                grain,
                BsonRequiredText(node, "grainKey"),
                BsonRequiredText(node, "status"),
                BsonRequiredText(node, "buildCommandId"),
                BsonRequiredText(node, "buildRequestHash"),
                BsonRequiredText(node, "buildReceiptId"),
                BsonRequiredText(node, "buildJobId"),
                BsonRequiredText(node, "configVersionId"),
                BsonRequiredText(node, "configHash"),
                BsonRequiredText(node, "candidatePromptId"),
                node["candidateStage"].ToInt32(),
                BsonRequiredText(node, "candidateStageLockSha256"),
                node.GetValue("fenceToken", 0).ToInt64(),
                node.GetValue("buildAttemptNo", 0).ToInt64(),
                node.GetValue("leaseOwner", BsonNull.Value).IsBsonNull)));
        }
        var receiptIds = traces.Select(trace => trace.BuildReceiptId).ToArray();
        var receiptUnique = receiptIds.Length > 0 &&
                            receiptIds.Distinct(StringComparer.Ordinal).Count() == receiptIds.Length;
        var queuePinsVerified = traces.Count > 0 && traces.All(trace =>
            trace.ConfigVersionId == _advVersionId &&
            trace.ConfigHash == _advConfigHash &&
            trace.CandidatePromptId == AdvancedPromptId &&
            trace.CandidateStage == 3 &&
            trace.CandidateStageLockSha256 == AdvancedStageLockSha256 &&
            trace.BuildRequestHash.Length == 64 &&
            trace.BuildReceiptId.Length == 64 &&
            !string.IsNullOrWhiteSpace(trace.BuildJobId));
        var broadEntries = await database
            .GetCollection<BsonDocument>("work_summary_token_ledgers")
            .CountDocumentsAsync(new BsonDocument
            {
                ["recordKind"] = "ENTRY",
                ["tokenKind"] = "ADVANCED_SUMMARY_BROAD_HISTORICAL_BUILD",
                ["outcome"] = "SUCCESS",
                ["isDeleted"] = false
            }, cancellationToken: ct);
        _advRuntimeEvidence = new P9AdvancedRuntimeEvidence(
            storedHash,
            recomputedHash,
            configHashVerified,
            traces.Count,
            traces.Count(trace => trace.Status is "CLEAN" or "FAILED"),
            receiptUnique,
            queuePinsVerified,
            broadEntries,
            traces);

        var indexMap = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var requiredIndexesPresent = true;
        foreach (var (collectionName, grain) in AdvancedCollections())
        {
            var indexes = await (await database
                    .GetCollection<BsonDocument>(collectionName)
                    .Indexes.ListAsync(ct))
                .ToListAsync(ct);
            var names = indexes.Select(item => item["name"].AsString)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            indexMap[collectionName] = names;
            var label = grain[0] + grain[1..].ToLowerInvariant();
            var required = new[]
            {
                $"ux_workAssignmentAdvancedSummary{label}Nodes_scope_grain",
                $"ix_workAssignmentAdvancedSummary{label}Nodes_scope_range",
                $"ix_workAssignmentAdvancedSummary{label}Nodes_status_dirty",
                $"ix_workAssignmentAdvancedSummary{label}Nodes_correlation",
                $"ux_workAssignmentAdvancedSummary{label}Nodes_buildReceipt",
                $"ix_workAssignmentAdvancedSummary{label}Nodes_lease"
            };
            requiredIndexesPresent &= required.All(names.Contains);
        }

        var year = _advYear ?? throw new InvalidOperationException(
            "P9-ADV YEAR evidence node unavailable.");
        var explain = await database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["explain"] = new BsonDocument
                {
                    ["find"] = "work_assignment_advanced_summary_year_nodes",
                    ["filter"] = new BsonDocument
                    {
                        ["assignmentId"] = year["assignmentId"],
                        ["dynamicFormTemplateId"] = year["dynamicFormTemplateId"],
                        ["sectionId"] = year["sectionId"],
                        ["configId"] = year["configId"],
                        ["configVersionId"] = year["configVersionId"],
                        ["configHash"] = year["configHash"],
                        ["timeAxis"] = year["timeAxis"],
                        ["yearKey"] = "2026",
                        ["isDeleted"] = false
                    }
                },
                ["verbosity"] = "executionStats"
            }, cancellationToken: ct);
        var stages = BasicFindBsonStrings(explain, "stage")
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var usedIndexes = BasicFindBsonStrings(explain, "indexName")
            .Distinct(StringComparer.Ordinal).ToArray();
        var identityIndexUsed = usedIndexes.Contains(
            "ux_workAssignmentAdvancedSummaryYearNodes_scope_grain",
            StringComparer.Ordinal);
        var blockingSortAbsent = !stages.Contains("SORT", StringComparer.Ordinal);
        return new P9AdvancedIndexEvidence(
            indexMap,
            stages,
            usedIndexes,
            requiredIndexesPresent,
            identityIndexUsed,
            blockingSortAbsent,
            requiredIndexesPresent && identityIndexUsed && blockingSortAbsent);
    }

    private static IEnumerable<(string Collection, string Grain)> AdvancedCollections()
    {
        yield return ("work_assignment_advanced_summary_day_nodes", "DAY");
        yield return ("work_assignment_advanced_summary_month_nodes", "MONTH");
        yield return ("work_assignment_advanced_summary_year_nodes", "YEAR");
    }

    private async Task WriteAdvancedCleanupArtifactAsync(
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        DateTime completedAtUtc,
        CancellationToken ct)
        => await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-ADV.cleanup.json"),
            new
            {
                schemaVersion = "P9_ADV_CLEANUP_V1",
                chainId = ChainId,
                promptId = AdvancedPromptId,
                groupId = AdvancedGroupId,
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
            }, ct);

    private async Task<bool> WriteAdvancedEvidenceAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var apiPath = Path.Combine(_paths.RunRoot, "P9-ADV.api-exchanges.json");
        var deltaPath = Path.Combine(_paths.RunRoot, "P9-ADV.collection-delta.json");
        var runtimePath = Path.Combine(_paths.RunRoot, "P9-ADV.runtime-pins.json");
        var receiptPath = Path.Combine(_paths.RunRoot, "P9-ADV.receipts.json");
        var queuePath = Path.Combine(_paths.RunRoot, "P9-ADV.queue-trace.json");
        var configPath = Path.Combine(_paths.RunRoot, "P9-ADV.config-hash.json");
        var indexPath = Path.Combine(_paths.RunRoot, "P9-ADV.index-explain.json");
        var cleanupPath = Path.Combine(_paths.RunRoot, "P9-ADV.cleanup.json");
        var mainPath = Path.Combine(_paths.RunRoot, "P9-ADV.evidence.json");
        var results = _cases.Results
            .Where(item => item.CaseId.StartsWith("P9-ADV-", StringComparison.Ordinal))
            .ToArray();

        await WriteStrictJsonAsync(apiPath, new
        {
            schemaVersion = "P9_ADV_API_EXCHANGES_V1",
            chainId = ChainId,
            promptId = AdvancedPromptId,
            groupId = AdvancedGroupId,
            runKey = _runKey,
            exchanges = _api?.Exchanges ?? [],
            realKestrel = _backend is not null
        }, ct);
        var deltas = _advBefore is null || _advAfter is null
            ? Array.Empty<P9CollectionDelta>()
            : BuildDeltas(_advBefore, _advAfter).ToArray();
        var rejectedCases = _caseEvidence.Where(item => item.CaseId is
            "P9-ADV-DAY-04" or "P9-ADV-DAY-05" or
            "P9-ADV-BUDGET-02" or "P9-ADV-BUDGET-03").ToArray();
        var rejectedZeroWrite = rejectedCases.Length == 4 && rejectedCases.All(item =>
            item.Deltas.All(delta => !delta.Changed && delta.CountDelta == 0));
        var p10ZeroWrite = deltas
            .Where(item => item.Collection.StartsWith("p10", StringComparison.OrdinalIgnoreCase))
            .All(item => !item.Changed && item.CountDelta == 0);
        await WriteStrictJsonAsync(deltaPath, new
        {
            schemaVersion = "P9_ADV_COLLECTION_DELTA_V1",
            chainId = ChainId,
            promptId = AdvancedPromptId,
            groupId = AdvancedGroupId,
            runKey = _runKey,
            beforeSha256 = _advBefore is null ? null : SnapshotSha256(_advBefore),
            afterSha256 = _advAfter is null ? null : SnapshotSha256(_advAfter),
            deltas,
            rejectedZeroWrite,
            quotaDeniedZeroWrite = _advQuotaDenied,
            p10ZeroWrite,
            passed = rejectedZeroWrite && _advQuotaDenied && p10ZeroWrite
        }, ct);
        await WriteStrictJsonAsync(runtimePath, new
        {
            schemaVersion = "P9_ADV_RUNTIME_PINS_V1",
            chainId = ChainId,
            promptId = AdvancedPromptId,
            groupId = AdvancedGroupId,
            runKey = _runKey,
            runtime = _advRuntimeEvidence,
            passed = _advRuntimeEvidence?.ConfigHashVerified == true &&
                     _advRuntimeEvidence.QueuePinsVerified
        }, ct);
        await WriteStrictJsonAsync(receiptPath, new
        {
            schemaVersion = "P9_ADV_RECEIPTS_V1",
            chainId = ChainId,
            promptId = AdvancedPromptId,
            receipts = _advRuntimeEvidence?.Nodes.Select(node => new
            {
                node.Collection,
                node.Grain,
                node.GrainKey,
                node.BuildCommandId,
                node.BuildRequestHash,
                node.BuildReceiptId
            }).ToArray() ?? [],
            passed = _advRuntimeEvidence?.ReceiptUnique == true
        }, ct);
        await WriteStrictJsonAsync(queuePath, new
        {
            schemaVersion = "P9_ADV_QUEUE_TRACE_V1",
            chainId = ChainId,
            promptId = AdvancedPromptId,
            nodes = _advRuntimeEvidence?.Nodes,
            passed = _advRuntimeEvidence?.QueuePinsVerified == true
        }, ct);
        await WriteStrictJsonAsync(configPath, new
        {
            schemaVersion = "P9_ADV_CONFIG_HASH_V1",
            chainId = ChainId,
            promptId = AdvancedPromptId,
            storedHash = _advRuntimeEvidence?.StoredConfigHash,
            recomputedHash = _advRuntimeEvidence?.RecomputedConfigHash,
            passed = _advRuntimeEvidence?.ConfigHashVerified == true
        }, ct);
        await WriteStrictJsonAsync(indexPath, new
        {
            schemaVersion = "P9_ADV_INDEX_EXPLAIN_V1",
            chainId = ChainId,
            promptId = AdvancedPromptId,
            groupId = AdvancedGroupId,
            maximumQueryDays = 3660,
            evidence = _advIndexEvidence,
            passed = _advIndexEvidence?.Passed == true
        }, ct);

        var exactIds = results.Select(item => item.CaseId)
            .SequenceEqual(AdvancedExpectedCaseIds, StringComparer.Ordinal);
        var allDat = results.All(item => item.Verdict == HarnessVerdict.DAT);
        var normalizedSemanticSha256 = ComputeDirectNormalizedSemanticSha256(results);
        var passed = fatalFailure is null &&
                     results.Length == AdvancedExpectedCaseIds.Length &&
                     exactIds && allDat && cleanupSucceeded &&
                     rejectedZeroWrite && _advQuotaDenied && p10ZeroWrite &&
                     _advRuntimeEvidence?.ConfigHashVerified == true &&
                     _advRuntimeEvidence.QueuePinsVerified &&
                     _advRuntimeEvidence.ReceiptUnique &&
                     _advRuntimeEvidence.BroadTokenEntryCount >= 3 &&
                     _advIndexEvidence?.Passed == true;
        var artifacts = new[]
        {
            BuildDirectArtifactPin(apiPath, "API_KESTREL"),
            BuildDirectArtifactPin(deltaPath, "COLLECTION_DELTA"),
            BuildDirectArtifactPin(runtimePath, "DIRECT_MONGO"),
            BuildDirectArtifactPin(configPath, "CONFIG_HASH_RECOMPUTE"),
            BuildDirectArtifactPin(receiptPath, "RECEIPT"),
            BuildDirectArtifactPin(queuePath, "QUEUE_TRACE"),
            BuildDirectArtifactPin(indexPath, "INDEX_EXPLAIN"),
            BuildDirectArtifactPin(cleanupPath, "CLEANUP")
        };
        await WriteStrictJsonAsync(mainPath, new
        {
            schemaVersion = "P9_ADV_EVIDENCE_V1",
            chainId = ChainId,
            promptId = AdvancedPromptId,
            groupId = AdvancedGroupId,
            runKey = _runKey,
            startedAtUtc,
            completedAtUtc,
            expectedCaseCount = AdvancedExpectedCaseIds.Length,
            actualCaseCount = results.Length,
            exactIds,
            allDat,
            normalizedSemanticSha256,
            oracles = AdvancedRequiredOracles,
            cases = results,
            artifacts,
            candidate = new
            {
                stage = 3,
                stageLockSha256 = AdvancedStageLockSha256,
                catalogRawSha256 = AdvancedCatalogRawSha256,
                catalogSemanticSha256 = AdvancedCatalogSemanticSha256,
                promotions = new[] { "ADVANCED_SUMMARY" }
            },
            assertions = new
            {
                lockedConfigAuthority = _advRuntimeEvidence?.ConfigHashVerified == true,
                exactHierarchyInputs = _advMonth is not null && _advYear is not null,
                replaySafeReceipts = _advRuntimeEvidence?.ReceiptUnique == true,
                queueTraceVerified = _advRuntimeEvidence?.QueuePinsVerified == true,
                leaseFencing = _advRuntimeEvidence?.Nodes
                    .Where(node => node.Status is "CLEAN" or "FAILED")
                    .All(node => node.LeaseReleased && node.FenceToken >= 1) == true,
                rejectedZeroWrite,
                broadQuotaDenied = _advQuotaDenied,
                indexExplainVerified = _advIndexEvidence?.Passed == true,
                p10ZeroWrite
            },
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            passed
        }, ct);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "p9-05-gate-result.json"),
            new
            {
                schemaVersion = "P9_FEATURE_GATE_RESULT_V1",
                chainId = ChainId,
                promptId = AdvancedPromptId,
                groupId = AdvancedGroupId,
                expected = AdvancedExpectedCaseIds.Length,
                passedCases = results.Count(item => item.Verdict == HarnessVerdict.DAT),
                exactIds,
                cleanupSucceeded,
                passed
            }, ct);
        return passed;
    }
}

internal sealed record P9AdvancedNodeTrace(
    string Collection,
    string Grain,
    string GrainKey,
    string Status,
    string BuildCommandId,
    string BuildRequestHash,
    string BuildReceiptId,
    string BuildJobId,
    string ConfigVersionId,
    string ConfigHash,
    string CandidatePromptId,
    int CandidateStage,
    string CandidateStageLockSha256,
    long FenceToken,
    long BuildAttemptNo,
    bool LeaseReleased);

internal sealed record P9AdvancedRuntimeEvidence(
    string StoredConfigHash,
    string RecomputedConfigHash,
    bool ConfigHashVerified,
    int NodeCount,
    int TerminalNodeCount,
    bool ReceiptUnique,
    bool QueuePinsVerified,
    long BroadTokenEntryCount,
    IReadOnlyList<P9AdvancedNodeTrace> Nodes);

internal sealed record P9AdvancedIndexEvidence(
    IReadOnlyDictionary<string, string[]> IndexNames,
    IReadOnlyList<string> ExplainStages,
    IReadOnlyList<string> UsedIndexes,
    bool RequiredIndexesPresent,
    bool IdentityIndexUsed,
    bool BlockingSortAbsent,
    bool Passed);
