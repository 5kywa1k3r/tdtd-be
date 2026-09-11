using System.Text;
using System.Text.Json;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task WriteFlowContributionCleanupArtifactAsync(
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        DateTime completedAtUtc,
        CancellationToken ct)
    {
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-FLW.cleanup.json"),
            new
            {
                schemaVersion = "P9_FLW_CLEANUP_V1",
                chainId = ChainId,
                promptId = FlowContributionPromptId,
                groupId = FlowContributionGroupId,
                runKey = _runKey,
                databaseName = _mongo?.DatabaseName,
                replicaSetName = _mongo?.ReplicaSetName,
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
                passed = cleanupSucceeded,
                completedAtUtc
            },
            ct);
    }

    private async Task<bool> WriteFlowContributionEvidenceAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var cleanupPath = Path.Combine(_paths.RunRoot, "P9-FLW.cleanup.json");
        var ledgerPath = Path.Combine(
            _paths.RunRoot,
            "P9-FLW.ledger-owner-evidence.json");
        var mainPath = Path.Combine(_paths.RunRoot, "P9-FLW.evidence.json");
        var results = _cases.Results.ToArray();
        var exactIds = results.Select(result => result.CaseId).SequenceEqual(
            FlowContributionExpectedCaseIds,
            StringComparer.Ordinal);
        var allDat = results.All(result => result.Verdict == HarnessVerdict.DAT);
        var normalized = results.Select(result => new
        {
            result.CaseId,
            verdict = result.Verdict.ToString(),
            result.Fingerprint
        }).ToArray();
        var normalizedSemanticSha256 = HashBytes(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized)));
        var expectedTargetCount = _flwIncludedSnapshot is null
            ? 0L
            : new[]
                {
                    "work_report_field_stat_values",
                    "work_report_table_stat_values",
                    "work_report_label_stat_values"
                }
                .Sum(name => _flwIncludedSnapshot[name].Count);
        var digestStores = _flwPublishedJob?
            .GetValue(
                "directStoreDigests",
                new MongoDB.Bson.BsonArray())
            .AsBsonArray
            .Select(value => BsonString(value.AsBsonDocument, "store"))
            .ToHashSet(StringComparer.Ordinal);
        var targetOwnerReuseVerified =
            _flwIncludedSnapshot is not null &&
            LifecycleDirectCollections.Sum(name =>
                _flwIncludedSnapshot[name].Count) > 0 &&
            digestStores is not null &&
            digestStores.SetEquals(LifecycleDirectCollections);
        var ownerLedgerVerified = _flwPublishedJob is not null &&
                                  IsCanonicalSha(_flwLedgerHash) &&
                                  IsCanonicalSha(_flwReversalBaselineHash) &&
                                  _flwPublishedJob.GetValue(
                                      "flowContributionSources",
                                      new MongoDB.Bson.BsonArray()).AsBsonArray.Count == 1 &&
                                  _flwPublishedJob.GetValue(
                                      "flowContributionTargets",
                                      new MongoDB.Bson.BsonArray()).AsBsonArray.Count ==
                                  expectedTargetCount &&
                                  expectedTargetCount > 0 &&
                                  targetOwnerReuseVerified;
        var noDoubleCountVerified = _flwIncludedSnapshot is not null &&
                                    _flwIncludedSnapshot[LifecycleJobCollection].Count == 1;
        var excludedZeroWriteVerified = _flwExcludedSnapshot is not null &&
                                        LifecycleDirectCollections.All(name =>
                                            _flwExcludedSnapshot[name].Count == 0) &&
                                        _flwExcludedSnapshot[LifecycleJobCollection].Count == 0;
        var p7BytesImmutable = fatalFailure is null &&
                               !string.IsNullOrWhiteSpace(_flwMappingBytesBefore);
        var passed = fatalFailure is null &&
                     results.Length == FlowContributionExpectedCaseIds.Length &&
                     exactIds &&
                     allDat &&
                     cleanupSucceeded &&
                     excludedZeroWriteVerified &&
                     ownerLedgerVerified &&
                     noDoubleCountVerified &&
                     p7BytesImmutable &&
                     _flwIndexVerified &&
                     _flwSecurityScanVerified &&
                     _flwP10ZeroWriteVerified;

        await WriteStrictJsonAsync(
            ledgerPath,
            new
            {
                schemaVersion = "P9_FLW_LEDGER_OWNER_EVIDENCE_V1",
                chainId = ChainId,
                promptId = FlowContributionPromptId,
                groupId = FlowContributionGroupId,
                runKey = _runKey,
                owner = "work_report_statistic_rebuild_jobs",
                valueOwners = LifecycleDirectCollections,
                noParallelFlowValueStore = true,
                excludeVersionId = _flwExcludeVersionId,
                includeVersionId = _flwIncludeVersionId,
                mappingReceiptId = _flwMappingReceiptId,
                mappingProvenanceId = _flwMappingProvenanceId,
                mappingProvenanceHash = _flwMappingProvenanceHash,
                mappingBytesSha256 = _flwMappingBytesBefore,
                runId = _flwRunId,
                generationId = _flwGenerationId,
                ledgerHash = _flwLedgerHash,
                reversalBaselineHash = _flwReversalBaselineHash,
                sourceCount = _flwPublishedJob is null
                    ? 0
                    : BsonInt(_flwPublishedJob, "flowContributionSourceCount"),
                targetCount = _flwPublishedJob is null
                    ? 0
                    : BsonInt(_flwPublishedJob, "flowContributionTargetCount"),
                excludedSnapshotSha256 = _flwExcludedSnapshot is null
                    ? null
                    : LifecycleSnapshotSha256(_flwExcludedSnapshot),
                includedSnapshotSha256 = _flwIncludedSnapshot is null
                    ? null
                    : LifecycleSnapshotSha256(_flwIncludedSnapshot),
                indexes = new[]
                {
                    "ix_workReportStatisticRebuildJobs_flow_contribution_source",
                    "ix_workReportStatisticRebuildJobs_flow_contribution_provenance"
                },
                assertions = new
                {
                    excludedZeroWriteVerified,
                    ownerLedgerVerified,
                    noDoubleCountVerified,
                    deterministicInverseBaseline = IsCanonicalSha(
                        _flwReversalBaselineHash),
                    p7BytesImmutable,
                    indexAndExplainVerified = _flwIndexVerified,
                    securityScanVerified = _flwSecurityScanVerified,
                    p10ZeroWriteVerified = _flwP10ZeroWriteVerified
                },
                passed
            },
            ct);

        var artifacts = new[]
        {
            BuildFlowContributionArtifactPin(ledgerPath, "LEDGER_OWNER"),
            BuildFlowContributionArtifactPin(cleanupPath, "CLEANUP")
        };
        await WriteStrictJsonAsync(
            mainPath,
            new
            {
                schemaVersion = "P9_FLW_EVIDENCE_V1",
                chainId = ChainId,
                promptId = FlowContributionPromptId,
                groupId = FlowContributionGroupId,
                runKey = _runKey,
                startedAtUtc,
                completedAtUtc,
                expectedCaseCount = FlowContributionExpectedCaseIds.Length,
                actualCaseCount = results.Length,
                exactIds,
                allDat,
                normalizedSemanticSha256,
                oracles = FlowContributionRequiredOracles,
                cases = results,
                artifacts,
                generation = new
                {
                    runId = _flwRunId,
                    generationId = _flwGenerationId,
                    ledgerHash = _flwLedgerHash,
                    reversalBaselineHash = _flwReversalBaselineHash,
                    sourceLifecycleEventKey = _flwApprovalEventKey,
                    candidateChainId = ChainId,
                    stageLockSha256 = FlowContributionStageLockSha256
                },
                assertions = new
                {
                    realKestrel = _backend is not null,
                    isolatedMongo = _mongo is not null,
                    excludedZeroWriteVerified,
                    exactIncludeOnce = ownerLedgerVerified && noDoubleCountVerified,
                    p7ReceiptProvenancePinned = p7BytesImmutable,
                    targetOwnerReuseVerified,
                    reversalLinkBaselineVerified = IsCanonicalSha(
                        _flwReversalBaselineHash),
                    indexAndExplainVerified = _flwIndexVerified,
                    securityScanVerified = _flwSecurityScanVerified,
                    p10ZeroWriteVerified = _flwP10ZeroWriteVerified
                },
                cleanupSucceeded,
                cleanupErrors,
                fatalFailure,
                passed
            },
            ct);
        return passed;
    }

    private object BuildFlowContributionArtifactPin(
        string path,
        string purpose)
        => new
        {
            path = Path.GetRelativePath(_paths.WorkspaceRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'),
            sha256 = HashBytes(File.ReadAllBytes(path)),
            bytes = new FileInfo(path).Length,
            purpose
        };
}
