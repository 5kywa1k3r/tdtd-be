using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.DTOs.Statistics;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private P9DiffRuntimeEvidence? _difRuntimeEvidence;

    private async Task<P9DiffIndexEvidence> CaptureDiffIndexEvidenceAsync(
        CancellationToken ct)
    {
        var database = RequireDatabase();
        var ownerId = $"{Fixture().AssignmentId}:{Fixture().TemplateId}";
        var configs = await database
            .GetCollection<BsonDocument>("work_report_statistic_diff_configs")
            .Find(new BsonDocument
            {
                ["assignmentId"] = ObjectId.Parse(Fixture().AssignmentId),
                ["dynamicFormTemplateId"] = ObjectId.Parse(Fixture().TemplateId),
                ["isDeleted"] = false
            })
            .Sort(new BsonDocument("versionNo", 1))
            .ToListAsync(ct);
        var configTraces = configs.Select(config =>
        {
            var payload = JsonSerializer.Deserialize<
                              WorkReportStatisticDiffConfigPayload>(
                              config["configJson"].AsString,
                              StatConfigCanonicalJson.StrictJsonOptions)
                          ?? throw new InvalidOperationException(
                              "P9-DIF stored config payload invalid.");
            var dependencies = config["dependencyPins"].AsBsonArray
                .Select(value => value.AsString).ToArray();
            var recomputed = WorkReportStatisticDiffService.P806ComputeConfigHash(
                ownerId, payload, dependencies);
            return new P9DiffConfigTrace(
                config["_id"].AsObjectId.ToString(),
                config["configId"].AsObjectId.ToString(),
                config["versionNo"].ToInt32(),
                config["revision"].ToInt64(),
                config["status"].AsString,
                config["configHash"].AsString,
                recomputed,
                dependencies,
                string.Equals(config["configHash"].AsString,
                    recomputed, StringComparison.Ordinal));
        }).ToArray();
        var results = await database
            .GetCollection<BsonDocument>("work_report_statistic_diff_results")
            .Find(new BsonDocument("isDeleted", false))
            .Sort(new BsonDocument("configVersionNo", 1))
            .ToListAsync(ct);
        var resultTraces = results.Select(result => new P9DiffResultTrace(
            result["_id"].AsObjectId.ToString(),
            result["runId"].AsObjectId.ToString(),
            result["jobId"].AsString,
            result["status"].AsString,
            result["configVersionId"].AsObjectId.ToString(),
            result["configHash"].AsString,
            result["commandId"].AsString,
            result["requestHash"].AsString,
            result["receiptId"].AsString,
            DiffSha256($"{result["commandId"].AsString}\n{result["requestHash"].AsString}"),
            result["candidatePromptId"].AsString,
            result["candidateStage"].ToInt32(),
            result["candidateStageLockSha256"].AsString,
            result["fenceToken"].ToInt64(),
            result["attemptNo"].ToInt32(),
            result.GetValue("leaseOwner", BsonNull.Value).IsBsonNull,
            result["isCurrent"].ToBoolean(),
            result["isFresh"].ToBoolean(),
            result["isDirty"].ToBoolean(),
            result.GetValue("expiresAtUtc", BsonNull.Value).IsBsonDateTime,
            result["totalRowCount"].ToInt32(),
            result["sourcePins"].AsBsonArray.Count)).ToArray();
        var configHashesVerified = configTraces.Length == 3 &&
                                   configTraces.All(trace => trace.HashVerified);
        var receiptVerified = resultTraces.Length == 3 && resultTraces.All(trace =>
            trace.ReceiptId == trace.RecomputedReceiptId &&
            trace.RequestHash.Length == 64);
        var queuePinsVerified = resultTraces.Length == 3 && resultTraces.All(trace =>
            trace.ResultId == trace.RunId &&
            trace.ResultId == trace.JobId &&
            trace.Status == "COMPLETED" &&
            trace.CandidatePromptId == DiffPromptId &&
            trace.CandidateStage == 4 &&
            trace.CandidateStageLockSha256 == DiffStageLockSha256 &&
            trace.FenceToken >= 1 && trace.AttemptNo == 1 &&
            trace.LeaseReleased && trace.IsFresh && !trace.IsDirty);
        var currentPromotionVerified = resultTraces.Count(trace => trace.IsCurrent) == 1 &&
            resultTraces.Where(trace => trace.IsCurrent).All(trace => !trace.TtlEligible) &&
            resultTraces.Where(trace => !trace.IsCurrent).All(trace => trace.TtlEligible);
        _difRuntimeEvidence = new P9DiffRuntimeEvidence(
            configHashesVerified,
            receiptVerified,
            queuePinsVerified,
            currentPromotionVerified,
            configTraces,
            resultTraces);

        var collection = database.GetCollection<BsonDocument>(
            "work_report_statistic_diff_results");
        var indexDocuments = await (await collection.Indexes.ListAsync(ct))
            .ToListAsync(ct);
        var names = indexDocuments.Select(index => index["name"].AsString)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var requiredPresent = DiffRequiredIndexNames().All(names.Contains);
        var ttl = indexDocuments.SingleOrDefault(index =>
            index["name"].AsString ==
            "ix_workReportStatisticDiffResults_terminal_ttl");
        var ttlVerified = ttl is not null &&
                          ttl.GetValue("expireAfterSeconds", -1).ToInt64() == 0 &&
                          ttl.GetValue("partialFilterExpression", new BsonDocument())
                              .AsBsonDocument.GetValue("isCurrent", true)
                              .ToBoolean() == false;
        var explain = await database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["explain"] = new BsonDocument
                {
                    ["find"] = "work_report_statistic_diff_results",
                    ["filter"] = new BsonDocument
                    {
                        ["assignmentId"] = ObjectId.Parse(Fixture().AssignmentId),
                        ["dynamicFormTemplateId"] = ObjectId.Parse(Fixture().TemplateId),
                        ["commandId"] = "p9-dif-run-label",
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
            "ux_workReportStatisticDiffResults_command_active",
            StringComparer.Ordinal);
        var blockingSortAbsent = !stages.Contains("SORT", StringComparer.Ordinal);
        return new P9DiffIndexEvidence(
            names,
            stages,
            usedIndexes,
            requiredPresent,
            ttlVerified,
            identityIndexUsed,
            blockingSortAbsent,
            requiredPresent && ttlVerified && identityIndexUsed && blockingSortAbsent);
    }

    private async Task WriteDiffCleanupArtifactAsync(
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        DateTime completedAtUtc,
        CancellationToken ct)
        => await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-DIF.cleanup.json"),
            new
            {
                schemaVersion = "P9_DIF_CLEANUP_V1",
                chainId = ChainId,
                promptId = DiffPromptId,
                groupId = DiffGroupId,
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
                    dataDirectoryRemovalVerified =
                        _mongo?.DataDirectoryRemovalVerified ?? false
                },
                completedAtUtc,
                passed = cleanupSucceeded
            }, ct);

    private async Task<bool> WriteDiffEvidenceAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var apiPath = Path.Combine(_paths.RunRoot, "P9-DIF.api-exchanges.json");
        var deltaPath = Path.Combine(_paths.RunRoot, "P9-DIF.collection-delta.json");
        var runtimePath = Path.Combine(_paths.RunRoot, "P9-DIF.runtime-owner.json");
        var configPath = Path.Combine(_paths.RunRoot, "P9-DIF.config-hash.json");
        var receiptPath = Path.Combine(_paths.RunRoot, "P9-DIF.receipts.json");
        var queuePath = Path.Combine(_paths.RunRoot, "P9-DIF.queue-trace.json");
        var authPath = Path.Combine(_paths.RunRoot, "P9-DIF.auth-before-existence.json");
        var indexPath = Path.Combine(_paths.RunRoot, "P9-DIF.index-explain.json");
        var cleanupPath = Path.Combine(_paths.RunRoot, "P9-DIF.cleanup.json");
        var mainPath = Path.Combine(_paths.RunRoot, "P9-DIF.evidence.json");
        var results = _cases.Results
            .Where(item => item.CaseId.StartsWith("P9-DIF-", StringComparison.Ordinal))
            .ToArray();
        await WriteStrictJsonAsync(apiPath, new
        {
            schemaVersion = "P9_DIF_API_EXCHANGES_V1",
            chainId = ChainId,
            promptId = DiffPromptId,
            groupId = DiffGroupId,
            runKey = _runKey,
            exchanges = _api?.Exchanges ?? [],
            realKestrel = _backend is not null,
            passed = _backend is not null
        }, ct);
        var deltas = _difBefore is null || _difAfter is null
            ? Array.Empty<P9CollectionDelta>()
            : BuildDeltas(_difBefore, _difAfter).ToArray();
        var p10ZeroWrite = deltas
            .Where(delta => delta.Collection.StartsWith(
                "p10", StringComparison.OrdinalIgnoreCase))
            .All(delta => !delta.Changed && delta.CountDelta == 0);
        await WriteStrictJsonAsync(deltaPath, new
        {
            schemaVersion = "P9_DIF_COLLECTION_DELTA_V1",
            chainId = ChainId,
            promptId = DiffPromptId,
            beforeSha256 = _difBefore is null ? null : SnapshotSha256(_difBefore),
            afterSha256 = _difAfter is null ? null : SnapshotSha256(_difAfter),
            deltas,
            staleZeroWrite = _difStaleZeroWrite,
            forbiddenZeroWrite = _difForbiddenZeroWrite,
            p10ZeroWrite,
            passed = _difStaleZeroWrite && _difForbiddenZeroWrite && p10ZeroWrite
        }, ct);
        await WriteStrictJsonAsync(runtimePath, new
        {
            schemaVersion = "P9_DIF_RUNTIME_OWNER_V1",
            chainId = ChainId,
            promptId = DiffPromptId,
            ownerCollection = "work_report_statistic_diff_results",
            runtime = _difRuntimeEvidence,
            passed = _difRuntimeEvidence?.QueuePinsVerified == true &&
                     _difRuntimeEvidence.CurrentPromotionVerified
        }, ct);
        await WriteStrictJsonAsync(configPath, new
        {
            schemaVersion = "P9_DIF_CONFIG_HASH_V1",
            chainId = ChainId,
            promptId = DiffPromptId,
            configs = _difRuntimeEvidence?.Configs,
            passed = _difRuntimeEvidence?.ConfigHashesVerified == true
        }, ct);
        await WriteStrictJsonAsync(receiptPath, new
        {
            schemaVersion = "P9_DIF_RECEIPTS_V1",
            chainId = ChainId,
            promptId = DiffPromptId,
            receipts = _difRuntimeEvidence?.Results.Select(result => new
            {
                result.ResultId,
                result.CommandId,
                result.RequestHash,
                result.ReceiptId,
                result.RecomputedReceiptId
            }).ToArray() ?? [],
            passed = _difRuntimeEvidence?.ReceiptVerified == true
        }, ct);
        await WriteStrictJsonAsync(queuePath, new
        {
            schemaVersion = "P9_DIF_QUEUE_TRACE_V1",
            chainId = ChainId,
            promptId = DiffPromptId,
            results = _difRuntimeEvidence?.Results,
            synchronousAggregateOwner = true,
            passed = _difRuntimeEvidence?.QueuePinsVerified == true
        }, ct);
        await WriteStrictJsonAsync(authPath, new
        {
            schemaVersion = "P9_DIF_AUTH_BEFORE_EXISTENCE_V1",
            chainId = ChainId,
            promptId = DiffPromptId,
            forbiddenRunZeroWrite = _difForbiddenZeroWrite,
            missingResultHidden = _difForbiddenZeroWrite,
            passed = _difForbiddenZeroWrite
        }, ct);
        await WriteStrictJsonAsync(indexPath, new
        {
            schemaVersion = "P9_DIF_INDEX_EXPLAIN_V1",
            chainId = ChainId,
            promptId = DiffPromptId,
            evidence = _difIndexEvidence,
            passed = _difIndexEvidence?.Passed == true
        }, ct);

        var exactIds = results.Select(item => item.CaseId)
            .SequenceEqual(DiffExpectedCaseIds, StringComparer.Ordinal);
        var allDat = results.All(item => item.Verdict == HarnessVerdict.DAT);
        var normalizedSemanticSha256 = ComputeDirectNormalizedSemanticSha256(results);
        var passed = fatalFailure is null &&
                     results.Length == DiffExpectedCaseIds.Length &&
                     exactIds && allDat && cleanupSucceeded &&
                     _difStaleZeroWrite && _difForbiddenZeroWrite && p10ZeroWrite &&
                     _difRuntimeEvidence?.ConfigHashesVerified == true &&
                     _difRuntimeEvidence.ReceiptVerified &&
                     _difRuntimeEvidence.QueuePinsVerified &&
                     _difRuntimeEvidence.CurrentPromotionVerified &&
                     _difIndexEvidence?.Passed == true;
        var artifacts = new[]
        {
            BuildDirectArtifactPin(apiPath, "API_KESTREL"),
            BuildDirectArtifactPin(authPath, "AUTH_BEFORE_EXISTENCE"),
            BuildDirectArtifactPin(runtimePath, "DIRECT_MONGO"),
            BuildDirectArtifactPin(deltaPath, "COLLECTION_DELTA"),
            BuildDirectArtifactPin(configPath, "CONFIG_HASH_RECOMPUTE"),
            BuildDirectArtifactPin(receiptPath, "RECEIPT"),
            BuildDirectArtifactPin(queuePath, "QUEUE_TRACE"),
            BuildDirectArtifactPin(indexPath, "INDEX_EXPLAIN"),
            BuildDirectArtifactPin(cleanupPath, "CLEANUP")
        };
        await WriteStrictJsonAsync(mainPath, new
        {
            schemaVersion = "P9_DIF_EVIDENCE_V1",
            chainId = ChainId,
            promptId = DiffPromptId,
            groupId = DiffGroupId,
            runKey = _runKey,
            startedAtUtc,
            completedAtUtc,
            expectedCaseCount = DiffExpectedCaseIds.Length,
            actualCaseCount = results.Length,
            exactIds,
            allDat,
            normalizedSemanticSha256,
            oracles = DiffRequiredOracles,
            cases = results,
            artifacts,
            candidate = new
            {
                stage = 4,
                stageLockSha256 = DiffStageLockSha256,
                catalogRawSha256 = DiffCatalogRawSha256,
                catalogSemanticSha256 = DiffCatalogSemanticSha256,
                promotions = new[] { "DIFF" }
            },
            assertions = new
            {
                typedField = _difFieldResult is not null,
                typedTableMetric = _difTableResult is not null,
                typedRowLabel = _difLabelResult is not null,
                lockedConfigAuthority =
                    _difRuntimeEvidence?.ConfigHashesVerified == true,
                runIdEqualsResultId =
                    _difRuntimeEvidence?.Results.All(result =>
                        result.RunId == result.ResultId) == true,
                replaySafeReceipts = _difRuntimeEvidence?.ReceiptVerified == true,
                leaseFencing = _difRuntimeEvidence?.QueuePinsVerified == true,
                currentPromotion =
                    _difRuntimeEvidence?.CurrentPromotionVerified == true,
                staleZeroWrite = _difStaleZeroWrite,
                authBeforeExistence = _difForbiddenZeroWrite,
                indexExplainVerified = _difIndexEvidence?.Passed == true,
                p10ZeroWrite
            },
            cleanupSucceeded,
            cleanupErrors,
            fatalFailure,
            passed
        }, ct);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "p9-06-gate-result.json"),
            new
            {
                schemaVersion = "P9_FEATURE_GATE_RESULT_V1",
                chainId = ChainId,
                promptId = DiffPromptId,
                groupId = DiffGroupId,
                expected = DiffExpectedCaseIds.Length,
                passedCases = results.Count(item => item.Verdict == HarnessVerdict.DAT),
                exactIds,
                cleanupSucceeded,
                passed
            }, ct);
        return passed;
    }
}

internal sealed record P9DiffConfigTrace(
    string VersionId,
    string ConfigId,
    int VersionNo,
    long Revision,
    string Status,
    string StoredHash,
    string RecomputedHash,
    IReadOnlyList<string> DependencyPins,
    bool HashVerified);

internal sealed record P9DiffResultTrace(
    string ResultId,
    string RunId,
    string JobId,
    string Status,
    string ConfigVersionId,
    string ConfigHash,
    string CommandId,
    string RequestHash,
    string ReceiptId,
    string RecomputedReceiptId,
    string CandidatePromptId,
    int CandidateStage,
    string CandidateStageLockSha256,
    long FenceToken,
    int AttemptNo,
    bool LeaseReleased,
    bool IsCurrent,
    bool IsFresh,
    bool IsDirty,
    bool TtlEligible,
    int TotalRowCount,
    int SourcePinCount);

internal sealed record P9DiffRuntimeEvidence(
    bool ConfigHashesVerified,
    bool ReceiptVerified,
    bool QueuePinsVerified,
    bool CurrentPromotionVerified,
    IReadOnlyList<P9DiffConfigTrace> Configs,
    IReadOnlyList<P9DiffResultTrace> Results);
