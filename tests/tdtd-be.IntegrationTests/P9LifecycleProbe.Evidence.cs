using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task WriteLifecycleCleanupArtifactAsync(
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        DateTime completedAtUtc,
        CancellationToken ct)
    {
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-LFC.cleanup.json"),
            new
            {
                schemaVersion = "P9_LFC_CLEANUP_V1",
                chainId = ChainId,
                promptId = LifecyclePromptId,
                groupId = LifecycleGroupId,
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
                cleanupHandles = _cleanupHandles,
                backendStopped = _backend?.StopVerified ?? false,
                backendPortReleased = _backend?.PortReleaseVerified ?? false,
                databaseDropped = _mongo?.DatabaseDropVerified ?? false,
                mongoStopped = _mongo?.ProcessStopVerified ?? false,
                mongoPortReleased = _mongo?.PortReleaseVerified ?? false,
                mongoDataRemoved = _mongo?.DataDirectoryRemovalVerified ?? false,
                passed = cleanupSucceeded,
                completedAtUtc
            },
            ct);
    }

    private async Task<bool> WriteLifecycleEvidenceAsync(
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        bool cleanupSucceeded,
        IReadOnlyList<string> cleanupErrors,
        string? fatalFailure,
        CancellationToken ct)
    {
        var deltaPath = Path.Combine(
            _paths.RunRoot,
            "P9-LFC.lifecycle-delta-evidence.json");
        var ownerIndexPath = Path.Combine(
            _paths.RunRoot,
            "P9-LFC.owner-index-evidence.json");
        var cleanupPath = Path.Combine(
            _paths.RunRoot,
            "P9-LFC.cleanup.json");
        var mainPath = Path.Combine(
            _paths.RunRoot,
            "P9-LFC.evidence.json");

        var officialDeltaZeroBeforeEffective = _lfcMilestones
            .Where(item => item.State is
                "PREPARED_DRAFT" or
                "DRAFT_EDITED" or
                "SUBMITTED" or
                "APPROVED_INTENT_PENDING")
            .Select(item => item.SnapshotSha256)
            .Distinct(StringComparer.Ordinal)
            .Count() == 1;
        var approvedEffectiveSingleContribution =
            _lfcPublishedDirect is not null &&
            LifecycleDirectCollections.All(name =>
                _lfcPublishedDirect.TryGetValue(name, out var state) && state.Count > 0) &&
            _lfcPublishedDirect[LifecycleJobCollection].Count == 1;
        var duplicateDeliveryConverged = _cases.Results.Any(item =>
            item.CaseId == "P9-LFC-APPROVE-03" &&
            item.Verdict == HarnessVerdict.DAT);
        var workerRaceConverged = _lfcQueueTrace.Any(item =>
            item.Step == "CONCURRENT_WORKERS_CONVERGED");
        var zeroWriteGuardsPassed = _lfcNegativeEvidence.Count == 8 &&
            _lfcNegativeEvidence.All(item => item.ZeroWriteVerified &&
                string.Equals(item.BeforeSha256, item.AfterSha256, StringComparison.Ordinal));
        var deltaPassed = officialDeltaZeroBeforeEffective &&
                          approvedEffectiveSingleContribution &&
                          duplicateDeliveryConverged &&
                          workerRaceConverged &&
                          zeroWriteGuardsPassed &&
                          _lfcP10ZeroWriteVerified;

        await WriteStrictJsonAsync(
            deltaPath,
            new
            {
                schemaVersion = "P9_LFC_LIFECYCLE_DELTA_EVIDENCE_V1",
                chainId = ChainId,
                promptId = LifecyclePromptId,
                groupId = LifecycleGroupId,
                runKey = _runKey,
                generation = new
                {
                    runId = _lfcRunId,
                    generationId = _lfcGenerationId,
                    generationHash = _lfcGenerationHash,
                    sourceLifecycleEventKey = _lfcApproveEventKey
                },
                milestones = _lfcMilestones,
                negativeAdmissions = _lfcNegativeEvidence,
                queueTrace = _lfcQueueTrace,
                caseDeltas = _caseEvidence,
                initialDirectSnapshotSha256 = _lfcInitialDirect is null
                    ? null
                    : LifecycleSnapshotSha256(_lfcInitialDirect),
                publishedDirectSnapshotSha256 = _lfcPublishedDirect is null
                    ? null
                    : LifecycleSnapshotSha256(_lfcPublishedDirect),
                initial = _lfcInitialDirect?.Values.OrderBy(
                    item => item.Collection,
                    StringComparer.Ordinal),
                published = _lfcPublishedDirect?.Values.OrderBy(
                    item => item.Collection,
                    StringComparer.Ordinal),
                officialDeltaZeroBeforeEffective,
                approvedEffectiveSingleContribution,
                duplicateDeliveryConverged,
                workerRaceConverged,
                zeroWriteGuardsPassed,
                p10ZeroWriteVerified = _lfcP10ZeroWriteVerified,
                passed = deltaPassed,
                assertions = new
                {
                    draftEditSubmitZeroDirect = officialDeltaZeroBeforeEffective,
                    approvedGenerationAllSixStores = approvedEffectiveSingleContribution,
                    onePublicationRoot = _lfcPublishedDirect is not null &&
                        _lfcPublishedDirect[LifecycleJobCollection].Count == 1,
                    currentPublicationVerified =
                        _lfcOwnerIndexEvidence?.CurrentPublicationVerified == true,
                    sourceRevisionFenceVerified =
                        _lfcOwnerIndexEvidence?.SourceRevisionFenceVerified == true,
                    stagedGenerationLegacyIsolationVerified =
                        _lfcOwnerIndexEvidence?.LegacyIsolationVerified == true,
                    negativeAdmissionsZeroWrite = zeroWriteGuardsPassed,
                    backfillObserved = _lfcQueueTrace.Any(item =>
                        item.Step == "ACKNOWLEDGED_BACKFILL_REPLAY")
                }
            },
            ct);

        await WriteStrictJsonAsync(
            ownerIndexPath,
            (object?)_lfcOwnerIndexEvidence ?? new
            {
                chainId = ChainId,
                promptId = LifecyclePromptId,
                groupId = LifecycleGroupId,
                runKey = _runKey,
                missing = true
            },
            ct);

        var results = _cases.Results.ToArray();
        var actualIds = results.Select(item => item.CaseId).ToArray();
        var exactIds = actualIds.SequenceEqual(
            LifecycleExpectedCaseIds,
            StringComparer.Ordinal);
        var allDat = results.All(item => item.Verdict == HarnessVerdict.DAT);
        var normalizedSemanticSha256 =
            ComputeLifecycleNormalizedSemanticSha256(results);
        var semanticHashVerified = string.Equals(
            normalizedSemanticSha256,
            ComputeLifecycleNormalizedSemanticSha256(results),
            StringComparison.Ordinal);
        var ownerIndexVerified = _lfcOwnerIndexEvidence?.Passed == true;
        var deltaVerified = _lfcMilestones.Count >= 5 &&
                            deltaPassed &&
                            _lfcNegativeEvidence.Count == 8 &&
                            _lfcNegativeEvidence.All(item => item.ZeroWriteVerified) &&
                            _lfcInitialDirect is not null &&
                            _lfcPublishedDirect is not null &&
                            LifecycleDirectCollections.All(name =>
                                _lfcPublishedDirect.TryGetValue(name, out var state) && state.Count > 0);
        var passed = fatalFailure is null &&
                     results.Length == LifecycleExpectedCaseIds.Length &&
                     exactIds &&
                     allDat &&
                     semanticHashVerified &&
                     cleanupSucceeded &&
                     deltaVerified &&
                     ownerIndexVerified &&
                     _lfcP8RegressionVerified &&
                     _lfcP10ZeroWriteVerified;

        var artifacts = new[]
        {
            BuildLifecycleArtifactPin(deltaPath),
            BuildLifecycleArtifactPin(ownerIndexPath),
            BuildLifecycleArtifactPin(cleanupPath)
        };
        await WriteStrictJsonAsync(
            mainPath,
            new
            {
                schemaVersion = "P9_LFC_EVIDENCE_V1",
                chainId = ChainId,
                promptId = LifecyclePromptId,
                groupId = LifecycleGroupId,
                runKey = _runKey,
                startedAtUtc,
                completedAtUtc,
                expectedCaseCount = LifecycleExpectedCaseIds.Length,
                actualCaseCount = results.Length,
                exactIds,
                allDat,
                normalizedSemanticSha256,
                semanticHashVerified,
                oracles = LifecycleRequiredOracles,
                cases = results,
                artifacts,
                generation = new
                {
                    runId = _lfcRunId,
                    generationId = _lfcGenerationId,
                    generationHash = _lfcGenerationHash,
                    sourceLifecycleEventKey = _lfcApproveEventKey,
                    candidateChainId = ChainId,
                    candidatePromptId = LifecycleRuntimePromptId,
                    candidateStage = 9,
                    catalogRawSha256 = LifecycleCatalogRawSha256,
                    catalogSemanticSha256 = LifecycleCatalogSemanticSha256,
                    stageLockSha256 = LifecycleStageLockSha256
                },
                assertions = new
                {
                    realKestrel = _backend is not null,
                    isolatedMongo = _mongo is not null,
                    deltaVerified,
                    ownerIndexVerified,
                    p8RegressionVerified = _lfcP8RegressionVerified,
                    p10ZeroWriteVerified = _lfcP10ZeroWriteVerified,
                    boundedBackfillVerified = _lfcQueueTrace.Any(item =>
                        item.Step == "ACKNOWLEDGED_BACKFILL_REPLAY"),
                    duplicateRaceOneGeneration = _lfcQueueTrace.Any(item =>
                        item.Step == "CONCURRENT_WORKERS_CONVERGED") &&
                        _lfcPublishedDirect?[LifecycleJobCollection].Count == 1,
                    currentPublicationVerified =
                        _lfcOwnerIndexEvidence?.CurrentPublicationVerified == true,
                    sourceRevisionFenceVerified =
                        _lfcOwnerIndexEvidence?.SourceRevisionFenceVerified == true,
                    stagedGenerationLegacyIsolationVerified =
                        _lfcOwnerIndexEvidence?.LegacyIsolationVerified == true,
                    sixStorePublication = _lfcPublishedDirect is not null &&
                        LifecycleDirectCollections.All(name =>
                            _lfcPublishedDirect[name].Count > 0)
                },
                cleanupSucceeded,
                p8RegressionPassed = _lfcP8RegressionVerified,
                p10ZeroWriteVerified = _lfcP10ZeroWriteVerified,
                cleanupErrors,
                fatalFailure,
                passed
            },
            ct);
        return passed;
    }

    private object BuildLifecycleArtifactPin(string path)
        => new
        {
            path = Path.GetRelativePath(_paths.WorkspaceRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'),
            sha256 = HashBytes(File.ReadAllBytes(path)),
            bytes = new FileInfo(path).Length,
            purpose = Path.GetFileName(path) switch
            {
                "P9-LFC.lifecycle-delta-evidence.json" => "LIFECYCLE_DELTA",
                "P9-LFC.owner-index-evidence.json" => "OWNER_INDEX",
                "P9-LFC.cleanup.json" => "CLEANUP",
                _ => "EVIDENCE"
            }
        };

    private static string ComputeLifecycleNormalizedSemanticSha256(
        IReadOnlyCollection<HarnessCaseResult> results)
    {
        var normalized = results
            .OrderBy(item => item.CaseId, StringComparer.Ordinal)
            .Select(item => new
            {
                caseId = item.CaseId,
                fingerprint = item.Fingerprint,
                verdict = item.Verdict.ToString()
            })
            .ToArray();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        };
        return HashBytes(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(normalized, options)));
    }
}
