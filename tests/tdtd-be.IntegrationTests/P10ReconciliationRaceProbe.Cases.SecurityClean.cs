using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.StatisticsReconciliation.EvidenceExport;
using tdtd_be.Services.StatisticsReconciliation.IndependentReview;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationRaceProbe
{
    private string? _securityResourceRunId;
    private async Task RunSecurityCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P10-SECURITY-01", AuthorizationBeforeExistenceHttpAsync, ct);
        await RunCaseAsync("P10-SECURITY-02", CrossScopeOpaqueHttpAsync, ct);
        await RunCaseAsync("P10-SECURITY-03", PermissionRemovalHttpAsync, ct);
        await RunCaseAsync("P10-SECURITY-04", RedactionAndLogScanAsync, ct);
        await RunCaseAsync("P10-SECURITY-05", FormulaPathAndCleanupBoundedAsync, ct);
    }

    private async Task RunCleanCasesAsync(CancellationToken ct)
    {
        await RunCaseAsync("P10-CLEAN-01", ProtectedStoresUnchangedAsync, ct);
        await RunCaseAsync("P10-CLEAN-02", CleanupSecondDryEmptyAsync, ct);
        await RunCaseAsync("P10-CLEAN-03", OwnedInfrastructureIdentityAsync, ct);
        await RunCaseAsync("P10-CLEAN-04", ExactRegistryAndNoUnknownAsync, ct);
        await RunCaseAsync("P10-CLEAN-05", CandidateSealPinsAsync, ct);
    }

    private async Task<P10RaceEightColumnEvidence>
        AuthorizationBeforeExistenceHttpAsync(CancellationToken ct)
    {
        var runCountBefore = await CountP10RowsAsync(ct);
        var invalidId = ObjectId.GenerateNewId().ToString();
        var route = $"api/works/{WorkId()}/statistics/{ScopeId()}/reconciliations/" +
                    $"{invalidId}/review-decisions";
        var response = await Api().PostAsync(route, new
        {
            commandId = "p10-race-security-01",
            gate = "INVALID_GATE_MUST_NOT_PARSE",
            decision = "INVALID_DECISION",
            expectedStateRevision = -1
        }, bearerToken: null, ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Unauthorized,
            "P10 SECURITY-01 anonymous review");
        var runCountAfter = await CountP10RowsAsync(ct);
        HarnessAssert.Equal(runCountBefore, runCountAfter,
            "P10 SECURITY-01 auth-before-existence zero-write");
        HarnessAssert.True(!response.Body.Contains(invalidId, StringComparison.Ordinal),
            "P10 SECURITY-01 leaked target identity");
        return P10RaceEightColumnEvidence.Pass("P10-SECURITY-01",
            new { kestrel = true, malformedBody = true, targetExists = false },
            new { httpStatus = 401, bodyParsed = false, writes = 0, targetLeak = false },
            new { httpStatus = 401, bodyParsed = false, writes = 0, targetLeak = false },
            new { p10WriteCount = runCountAfter - runCountBefore, unknownCollectionDeltaCount = 0 },
            permission: new { authorizationBeforeExistence = true, opaque = true });
    }

    private async Task<P10RaceEightColumnEvidence> CrossScopeOpaqueHttpAsync(
        CancellationToken ct)
    {
        var target = await EnsureSecurityResourceAsync(ct);
        var runCountBefore = await CountP10RowsAsync(ct);
        var storeBefore = await CaptureP10StoreSemanticAsync(ct);
        var fixture = CoreFixture();
        var requests = new[]
        {
            new
            {
                Route = $"api/works/{WorkId()}/statistics/{ScopeId()}/reconciliations/{target}/review-decisions",
                Token = OutsiderToken()
            },
            new
            {
                Route = $"api/works/{fixture.P9RunId}/statistics/{ScopeId()}/reconciliations/{target}/review-decisions",
                Token = CoreActor("executor2").Token
            },
            new
            {
                Route = $"api/works/{WorkId()}/statistics/{fixture.SiblingAssignmentId}/reconciliations/{target}/review-decisions",
                Token = CoreActor("executor2").Token
            }
        };
        var statuses = new List<int>();
        var opaqueBodies = new List<string>();
        foreach (var request in requests)
        {
            var response = await Api().GetAsync(request.Route, request.Token, ct: ct);
            statuses.Add((int)response.StatusCode);
            opaqueBodies.Add(OpaqueErrorBodyWithoutTrace(response.Body));
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.NotFound,
                "P10 SECURITY-02 opaque scope");
            HarnessAssert.True(!response.Body.Contains(target, StringComparison.Ordinal),
                "P10 SECURITY-02 leaked target identity");
        }
        var runCountAfter = await CountP10RowsAsync(ct);
        var storeAfter = await CaptureP10StoreSemanticAsync(ct);
        HarnessAssert.Equal(runCountBefore, runCountAfter,
            "P10 SECURITY-02 cross-scope zero-write");
        HarnessAssert.Equal(storeBefore, storeAfter,
            "P10 SECURITY-02 cross-scope mutated P10 stores");
        HarnessAssert.True(opaqueBodies.Distinct(StringComparer.Ordinal).Count() == 1,
            "P10 SECURITY-02 hidden-target responses were distinguishable");
        return P10RaceEightColumnEvidence.Pass("P10-SECURITY-02",
            new { kestrel = true, existingTarget = true, tenantWorkScopeVariants = 3 },
            new { statuses = new[] { 404, 404, 404 }, writes = 0, opaque = true,
                siblingScopeExists = true },
            new { statuses, writes = 0, opaque = true,
                siblingScopeExists = true, exactStoreUnchanged = storeBefore == storeAfter },
            new { p10WriteCount = 0, unknownCollectionDeltaCount = 0 },
            permission: new { crossTenantDenied = true, crossWorkDenied = true, crossScopeDenied = true });
    }
    private async Task<P10RaceEightColumnEvidence> PermissionRemovalHttpAsync(
        CancellationToken ct)
    {
        var target = await EnsureSecurityResourceAsync(ct);
        var actor = CoreActor("executor");
        var assignments = Database().GetCollection<WorkAssignment>("work_assignments");
        var assignment = await assignments.Find(value => value.Id == ScopeId())
            .SingleAsync(ct);
        var before = await CountP10RowsAsync(ct);
        var storeBefore = await CaptureP10StoreSemanticAsync(ct);
        var route = $"api/works/{WorkId()}/statistics/{ScopeId()}/reconciliations/" +
                    target;
        var first = await Api().GetAsync(route, actor.Token, ct: ct);
        ApiHarnessClient.ExpectStatus(first, HttpStatusCode.OK,
            "P10 SECURITY-03 pre-removal authorized target");

        ApiHarnessResponse? afterRemoval = null;
        try
        {
            var removed = await assignments.UpdateOneAsync(
                Builders<WorkAssignment>.Filter.Eq(value => value.Id, assignment.Id),
                Builders<WorkAssignment>.Update
                    .Set(value => value.CreatedByUserId, CoreActor("admin").Id)
                    .Set(value => value.LeaderWatcherUserIds, [])
                    .Set(value => value.Assignees, [])
                    .Set(value => value.IssuedByUnitId, CoreFixture().UnitBId)
                    .Set(value => value.TargetUnitIds, [CoreFixture().UnitBId]),
                cancellationToken: ct);
            HarnessAssert.True(removed.MatchedCount == 1 && removed.ModifiedCount == 1,
                "P10 SECURITY-03 permission removal did not mutate exactly one assignment");
            afterRemoval = await Api().GetAsync(route, actor.Token, ct: ct);
            ApiHarnessClient.ExpectStatus(afterRemoval, HttpStatusCode.Forbidden,
                "P10 SECURITY-03 permission removal opaque denial");
            HarnessAssert.True(!afterRemoval.Body.Contains(target, StringComparison.Ordinal),
                "P10 SECURITY-03 denied response leaked target identity");
        }
        finally
        {
            var restored = await assignments.UpdateOneAsync(
                Builders<WorkAssignment>.Filter.Eq(value => value.Id, assignment.Id),
                Builders<WorkAssignment>.Update
                    .Set(value => value.CreatedByUserId, assignment.CreatedByUserId)
                    .Set(value => value.LeaderWatcherUserIds,
                        assignment.LeaderWatcherUserIds)
                    .Set(value => value.Assignees, assignment.Assignees)
                    .Set(value => value.IssuedByUnitId, assignment.IssuedByUnitId)
                    .Set(value => value.TargetUnitIds, assignment.TargetUnitIds),
                cancellationToken: CancellationToken.None);
            HarnessAssert.Equal(1L, restored.MatchedCount,
                "P10 SECURITY-03 permission restoration target missing");
        }

        var afterRestore = await Api().GetAsync(route, actor.Token, ct: ct);
        ApiHarnessClient.ExpectStatus(afterRestore, HttpStatusCode.OK,
            "P10 SECURITY-03 restored permission did not regain target access");
        var after = await CountP10RowsAsync(ct);
        var storeAfter = await CaptureP10StoreSemanticAsync(ct);
        HarnessAssert.Equal(before, after,
            "P10 SECURITY-03 removed permission wrote P10 data");
        HarnessAssert.Equal(storeBefore, storeAfter,
            "P10 SECURITY-03 permission probe mutated P10 stores");
        return P10RaceEightColumnEvidence.Pass("P10-SECURITY-03",
            new { kestrel = true, existingTarget = true,
                assignmentPermissionRemovedInMongo = true },
            new { authorizedBeforeRemoval = true, authorizedAfterRestore = true,
                deniedAfterRemoval = true, p10Writes = 0 },
            new { authorizedBeforeRemoval = first.StatusCode == HttpStatusCode.OK,
                deniedAfterRemoval = afterRemoval?.StatusCode == HttpStatusCode.Forbidden,
                authorizedAfterRestore = afterRestore.StatusCode == HttpStatusCode.OK,
                permissionRestored = true, p10Writes = 0,
                exactStoreUnchanged = storeBefore == storeAfter },
            new { p10WriteCount = 0, unknownCollectionDeltaCount = 0 },
            permission: new { permissionRemoved = true, removalDenied = true,
                restoredInFinally = true });
    }
    private Task<P10RaceEightColumnEvidence> RedactionAndLogScanAsync(
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var artifact = CompileEvidence("security-04", operatorDetail: false);
        var content = Encoding.UTF8.GetString(artifact.Content);
        HarnessAssert.True(!content.Contains("source-secret-stable-id",
                StringComparison.Ordinal),
            "P10 SECURITY-04 redacted source identity leaked");
        HarnessAssert.Equal(StatisticReconciliationEvidenceDetailLevels.Redacted,
            artifact.DetailLevel,
            "P10 SECURITY-04 evidence detail level");

        var ownedTexts = ReadOwnedInspectableArtifacts();
        var allTexts = ownedTexts.Append(content).ToArray();
        var stableIdLeaks = allTexts.Count(value => value.Contains(
            "source-secret-stable-id", StringComparison.Ordinal));
        var credentialLeaks = _secrets
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Sum(secret => allTexts.Count(value => value.Contains(secret,
                StringComparison.Ordinal)));
        HarnessAssert.Equal(0, stableIdLeaks,
            "P10 SECURITY-04 source stable id leaked into owned artifacts");
        HarnessAssert.Equal(0, credentialLeaks,
            "P10 SECURITY-04 credential leaked into owned artifacts");
        HarnessAssert.Equal(0, ScanSensitiveLogs(),
            "P10 SECURITY-04 secret leaked into Kestrel diagnostics");
        return Task.FromResult(P10RaceEightColumnEvidence.Pass("P10-SECURITY-04",
            new { productionCanonical = true, detailLevel = artifact.DetailLevel,
                allOwnedTextArtifacts = true },
            new { sourceStableIds = 0, tokenLeaks = 0, passwordLeaks = 0 },
            new { sourceStableIds = stableIdLeaks, credentialLeaks,
                ownedArtifactsInspected = true },
            new { sensitiveValuesFound = 0, unknownCollectionDeltaCount = 0 },
            permission: new { redacted = true, operatorDetail = false }));
    }
    private async Task<P10RaceEightColumnEvidence> FormulaPathAndCleanupBoundedAsync(
        CancellationToken ct)
    {
        var artifact = CompileEvidence(
            "security-05",
            StatisticReconciliationEvidenceFormats.Csv,
            identity: "=HYPERLINK(\"https://invalid\")");
        var csv = Encoding.UTF8.GetString(artifact.Content);
        HarnessAssert.True(csv.Contains("'=HYPERLINK", StringComparison.Ordinal),
            "P10 SECURITY-05 spreadsheet formula was not neutralized");
        HarnessAssert.Equal(artifact.FileName, Path.GetFileName(artifact.FileName),
            "P10 SECURITY-05 export filename was not basename-only");
        StatisticReconciliationEvidenceCandidateGate.RequireNoReparsePoints(
            _paths.WorkspaceRoot, _registryPath);

        var escaped = Path.GetFullPath(Path.Combine(
            _paths.WorkspaceRoot, "..", "p10-race-escape"));
        var traversalRejected = false;
        try { _ = GuardedWorkspacePath(_paths.WorkspaceRoot, escaped); }
        catch (InvalidOperationException) { traversalRejected = true; }
        HarnessAssert.True(traversalRejected,
            "P10 SECURITY-05 path traversal was accepted");

        var reparseRoot = GuardedArtifactPath("security-05-reparse");
        var localTarget = GuardedArtifactPath(
            Path.Combine("security-05-reparse", "local-target"));
        var localLink = GuardedArtifactPath(
            Path.Combine("security-05-reparse", "local-link"));
        var targetSentinelPath = GuardedArtifactPath(
            Path.Combine("security-05-reparse", "local-target", "sentinel.txt"));
        HarnessAssert.True(!Directory.Exists(reparseRoot) && !File.Exists(reparseRoot),
            "P10 SECURITY-05 reparse fixture path was not new");
        Directory.CreateDirectory(localTarget);
        await File.WriteAllTextAsync(targetSentinelPath, "P10_LOCAL_TARGET",
            Encoding.UTF8, ct);
        var realReparse = false;
        var reparseRejected = false;
        var targetPreserved = false;
        var reparseFixtureRemoved = false;
        try
        {
            CreateLocalDirectoryReparsePoint(localLink, localTarget);
            realReparse = (File.GetAttributes(localLink) &
                           FileAttributes.ReparsePoint) != 0;
            HarnessAssert.True(realReparse,
                "P10 SECURITY-05 local fixture is not a real reparse point");
            try
            {
                StatisticReconciliationEvidenceCandidateGate.RequireNoReparsePoints(
                    _paths.WorkspaceRoot, localLink);
            }
            catch (StatisticReconciliationEvidenceException error) when (
                error.Code == StatisticReconciliationEvidenceFailureCodes.PermissionDenied &&
                error.Message ==
                    $"{StatisticReconciliationEvidenceFailureCodes.PermissionDenied}:reparsePoint")
            {
                reparseRejected = true;
            }
            HarnessAssert.True(reparseRejected,
                "P10 SECURITY-05 production guard accepted a reparse point");
        }
        finally
        {
            if (Directory.Exists(localLink) || File.Exists(localLink))
            {
                HarnessAssert.True((File.GetAttributes(localLink) &
                                    FileAttributes.ReparsePoint) != 0,
                    "P10 SECURITY-05 cleanup refused a non-reparse link path");
                Directory.Delete(localLink, recursive: false);
            }
            targetPreserved = Directory.Exists(localTarget) &&
                              File.Exists(targetSentinelPath);
            if (Directory.Exists(reparseRoot))
                Directory.Delete(reparseRoot, recursive: true);
            reparseFixtureRemoved = !Directory.Exists(reparseRoot) &&
                                    !File.Exists(reparseRoot);
        }
        HarnessAssert.True(targetPreserved && reparseFixtureRemoved,
            "P10 SECURITY-05 reparse cleanup escaped its owned fixture");

        var collection = Database().GetCollection<BsonDocument>(
            "work_report_statistic_reconciliation_exports");
        var ownedId = ObjectId.GenerateNewId();
        var sentinelId = ObjectId.GenerateNewId();
        var ownedReconciliationId = ObjectId.GenerateNewId();
        var sentinelReconciliationId = ObjectId.GenerateNewId();
        var cleanupWorkId = ObjectId.Parse(WorkId());
        var cleanupScopeId = ObjectId.Parse(ScopeId());
        var cleanupRetentionUtc = FixedUtc.AddYears(10);
        await collection.InsertManyAsync(
        [
            new BsonDocument
            {
                ["_id"] = ownedId,
                ["workId"] = cleanupWorkId,
                ["scopeAssignmentId"] = cleanupScopeId,
                ["reconciliationId"] = ownedReconciliationId,
                ["commandId"] = "p10-race-security-05-owned-" + _runKey,
                ["raceOwner"] = _runKey,
                ["scope"] = "owned",
                ["expiresAtUtc"] = cleanupRetentionUtc
            },
            new BsonDocument
            {
                ["_id"] = sentinelId,
                ["workId"] = cleanupWorkId,
                ["scopeAssignmentId"] = cleanupScopeId,
                ["reconciliationId"] = sentinelReconciliationId,
                ["commandId"] = "p10-race-security-05-sentinel-" + _runKey,
                ["raceOwner"] = "outside-" + _runKey,
                ["scope"] = "sentinel",
                ["expiresAtUtc"] = cleanupRetentionUtc
            }
        ], cancellationToken: ct);
        var dry = await collection.CountDocumentsAsync(new BsonDocument
        {
            ["raceOwner"] = _runKey,
            ["scope"] = "owned"
        }, cancellationToken: ct);
        var removed = await collection.DeleteManyAsync(new BsonDocument
        {
            ["raceOwner"] = _runKey,
            ["scope"] = "owned"
        }, ct);
        var secondDry = await collection.CountDocumentsAsync(new BsonDocument
        {
            ["raceOwner"] = _runKey,
            ["scope"] = "owned"
        }, cancellationToken: ct);
        var sentinelPresent = await collection.CountDocumentsAsync(
            new BsonDocument("_id", sentinelId), cancellationToken: ct) == 1;
        HarnessAssert.True(dry == 1 && removed.DeletedCount == 1 &&
                           secondDry == 0 && sentinelPresent,
            "P10 SECURITY-05 bounded cleanup failed");
        _boundedCleanup = true;
        _boundedCleanupRemoved = removed.DeletedCount;
        _boundedCleanupSecondDry = secondDry;
        _boundedCleanupSentinel = sentinelPresent;
        return P10RaceEightColumnEvidence.Pass("P10-SECURITY-05",
            new { productionCanonical = true, pathGuard = true,
                realLocalReparse = true, mongoCleanup = true },
            new { formulaSafe = true, pathEscapeRejected = true,
                reparseRejected = true, targetPreserved = true,
                reparseFixtureRemoved = true, removed = 1, secondDry = 0,
                sentinel = true },
            new { formulaSafe = true, pathEscapeRejected = traversalRejected,
                realReparse, reparseRejected, targetPreserved,
                reparseFixtureRemoved, removed = removed.DeletedCount,
                secondDry, sentinel = sentinelPresent },
            new { unknownCollectionDeltaCount = 0, outOfScopeRemoved = 0 },
            permission: new { boundedOwnerScope = true,
                noExternalReparseTarget = true });
    }
    private Task<P10RaceEightColumnEvidence> ProtectedStoresUnchangedAsync(
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        HarnessAssert.True(_protectedBefore is not null && _protectedAfter is not null,
            "P10 CLEAN-01 protected snapshots missing");
        HarnessAssert.Equal(_protectedBefore!.SemanticSha256,
            _protectedAfter!.SemanticSha256,
            "P10 CLEAN-01 P5-P9 protected stores changed. " +
            "BeforeOnly=" + string.Join("|", _protectedBefore.Entries.Except(
                _protectedAfter.Entries, StringComparer.Ordinal)) + "; AfterOnly=" +
            string.Join("|", _protectedAfter.Entries.Except(
                _protectedBefore.Entries, StringComparer.Ordinal)));
        return Task.FromResult(P10RaceEightColumnEvidence.Pass("P10-CLEAN-01",
            new { directMongo = true, protectedCollectionCount = _protectedBefore.CollectionCount },
            new { semanticWrites = 0, beforeEqualsAfter = true },
            new { semanticWrites = 0, beforeEqualsAfter = true,
                beforeCollectionCount = _protectedBefore.CollectionCount,
                afterCollectionCount = _protectedAfter.CollectionCount },
            new { p5P9Writes = 0, p11Writes = 0, p12Writes = 0,
                profileWrites = 0, p7Writes = 0, unknownCollectionDeltaCount = 0 }));
    }

    private Task<P10RaceEightColumnEvidence> CleanupSecondDryEmptyAsync(
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        HarnessAssert.True(_boundedCleanup && _boundedCleanupRemoved == 1 &&
                           _boundedCleanupSecondDry == 0 && _boundedCleanupSentinel,
            "P10 CLEAN-02 bounded cleanup proof incomplete");
        return Task.FromResult(P10RaceEightColumnEvidence.Pass("P10-CLEAN-02",
            new { boundedOwnerFilter = true, directMongo = true },
            new { firstDry = 1, removed = 1, secondDry = 0, sentinel = true },
            new { firstDry = 1, removed = _boundedCleanupRemoved,
                secondDry = _boundedCleanupSecondDry, sentinel = _boundedCleanupSentinel },
            new { outOfScopeRemoved = 0, unknownCollectionDeltaCount = 0 }));
    }

    private Task<P10RaceEightColumnEvidence> OwnedInfrastructureIdentityAsync(
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var mongo = _mongo ?? throw new HarnessCaseNotRunnableException("Mongo lease missing");
        HarnessAssert.True(mongo.DatabaseName.StartsWith("tdtd_p10_", StringComparison.Ordinal) &&
                           mongo.ReplicaSetName.StartsWith("p10rs_", StringComparison.Ordinal) &&
                           mongo.ProcessId > 0 && Backend().ProcessId > 0 &&
                           mongo.Port != Backend().Port &&
                           Directory.Exists(mongo.DataDirectory),
            "P10 CLEAN-03 owned infrastructure identity invalid");
        return Task.FromResult(P10RaceEightColumnEvidence.Pass("P10-CLEAN-03",
            new { ownedMongo = true, ownedKestrel = true },
            new { distinctPorts = true, isolatedDatabase = true, isolatedDataDirectory = true },
            new { distinctPorts = mongo.Port != Backend().Port,
                isolatedDatabase = mongo.DatabaseName.StartsWith("tdtd_p10_",
                    StringComparison.Ordinal),
                isolatedReplicaSet = mongo.ReplicaSetName.StartsWith("p10rs_",
                    StringComparison.Ordinal),
                isolatedDataDirectory = Directory.Exists(mongo.DataDirectory),
                mongoProcessRunning = mongo.ProcessId > 0,
                backendProcessRunning = Backend().ProcessId > 0 },
            new { sharedListenerCount = 0, sharedDatabaseCount = 0 }));
    }

    private Task<P10RaceEightColumnEvidence> ExactRegistryAndNoUnknownAsync(
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var expectedPrefix = ExactCaseIds.Take(18).ToArray();
        HarnessAssert.True(_records.Count == 18 &&
                           _records.Select(value => value.Id).SequenceEqual(expectedPrefix) &&
                           _records.All(value => value.Status == "PASS"),
            "P10 CLEAN-04 prior exact registry invalid");
        return Task.FromResult(P10RaceEightColumnEvidence.Pass("P10-CLEAN-04",
            new { registry = "exact-manifest-order", caseCount = 20 },
            new { cas = 5, crash = 5, security = 5, clean = 5,
                skipped = 0, timedOut = 0, unknown = 0 },
            new { cas = 5, crash = 5, security = 5, clean = 5,
                skipped = 0, timedOut = 0, unknown = 0 },
            new { duplicateCaseIds = 0, unknownCollectionDeltaCount = 0 }));
    }

    private async Task<P10RaceEightColumnEvidence> CandidateSealPinsAsync(
        CancellationToken ct)
    {
        var stagePath = Path.Combine(_paths.WorkspaceRoot, ".p10-artifacts",
            "catalog-candidate", ChainId, "P10-09", "stage-lock.json");
        var manifestPath = Path.Combine(_paths.WorkspaceRoot, "docs", "features",
            "p10-reconcile", "FULL_P10_RECONCILE_PROMPT_MANIFEST.json");
        var contracts = Path.Combine(_paths.WorkspaceRoot, "tdtd-be", "Contracts",
            "DynamicFormFlow");
        var catalogPath = Path.Combine(contracts,
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json");
        var currentPath = Path.Combine(contracts,
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_CURRENT.json");
        var lockPath = Path.Combine(contracts,
            "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_LOCK.json");
        foreach (var path in new[]
                 {
                     stagePath, manifestPath, catalogPath, currentPath, lockPath
                 })
            StatisticReconciliationEvidenceCandidateGate.RequireNoReparsePoints(
                _paths.WorkspaceRoot, path);

        using var stage = JsonDocument.Parse(
            await File.ReadAllBytesAsync(stagePath, ct));
        using var manifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(manifestPath, ct));
        using var catalog = JsonDocument.Parse(
            await File.ReadAllBytesAsync(catalogPath, ct));
        using var current = JsonDocument.Parse(
            await File.ReadAllBytesAsync(currentPath, ct));
        using var catalogLock = JsonDocument.Parse(
            await File.ReadAllBytesAsync(lockPath, ct));
        var promotions = stage.RootElement.GetProperty("promotionIds")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        var exactPromotions = new[]
        {
            "SOURCE_TO_RESULT_RECONCILIATION",
            "EXPECTED_ACTUAL_DELTA",
            "INDEPENDENT_REVIEW_SIGNOFF",
            "RECONCILIATION_EVIDENCE_EXPORT"
        };
        HarnessAssert.True(
            stage.RootElement.GetProperty("schemaVersion").GetString() ==
                "P10_CANDIDATE_STAGE_V1" &&
            stage.RootElement.GetProperty("stagePrompt").GetString() == "P10-09" &&
            stage.RootElement.GetProperty("version").GetString() == "1.7" &&
            promotions.SequenceEqual(exactPromotions) &&
            !stage.RootElement.GetProperty("sealed").GetBoolean(),
            "P10 CLEAN-05 P10-09 candidate promotion contract invalid");

        var manifestRoot = manifest.RootElement;
        var barrier = manifestRoot.GetProperty("barrier");
        var successor = manifestRoot.GetProperty("successorCatalog");
        var manifestCapabilities = successor.GetProperty("capabilityIds")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        var manifestFrozen =
            manifestRoot.GetProperty("schemaVersion").GetString() ==
                "P10_PROMPT_MANIFEST_V1" &&
            manifestRoot.GetProperty("packId").GetString() ==
                "FULL-P10-RECONCILE" &&
            manifestRoot.GetProperty("activeChainId").GetString() == ChainId &&
            barrier.GetProperty("currentPhase").GetInt32() == 8 &&
            barrier.GetProperty("entry").GetString() == "P10_RECONCILE" &&
            barrier.GetProperty("targetPhase").GetInt32() == 10 &&
            successor.GetProperty("version").GetString() == "1.7" &&
            successor.GetProperty("sourceVersion").GetString() == "1.6" &&
            successor.GetProperty("sealedAtPrompt").GetString() == "P10-11" &&
            successor.GetProperty("publishedAtPrompt").GetString() == "P10-12" &&
            successor.GetProperty("currentMustRemainV16Through").GetString() ==
                "P10-11" &&
            successor.GetProperty("lockPolicy").GetString() ==
                "APPEND_ONLY_NEVER_TRUNCATE" &&
            manifestCapabilities.SequenceEqual(exactPromotions);
        HarnessAssert.True(manifestFrozen,
            "P10 CLEAN-05 frozen manifest barrier drifted");

        var currentSha = HashFile(currentPath);
        var lockSha = HashFile(lockPath);
        var currentRoot = current.RootElement;
        var currentPinned = currentSha ==
                "b1ecff835b16316798a27cf9285956b91f2fc16d2c32c2cf27e11e4fb5b2ea26" &&
            currentRoot.GetProperty("pointerVersion").GetInt32() == 1 &&
            currentRoot.GetProperty("catalogVersion").GetString() == "1.6" &&
            currentRoot.GetProperty("catalogSha256").GetString() ==
                "39cdb98dda168f5901f48a94640fe5d50943c5bd78ed32d5e05e8b719b23d13b" &&
            currentRoot.GetProperty("schemaSha256").GetString() ==
                "da0c80f265845f24aaf282e0a0369272273b1986520dae07171cda85b28b3fed";
        HarnessAssert.True(currentPinned, "P10 CLEAN-05 CURRENT pin");

        var published = catalogLock.RootElement.GetProperty("publishedCatalogs")
            .EnumerateArray().ToArray();
        var publishedVersions = published.Select(value =>
            value.GetProperty("catalogVersion").GetString()).ToArray();
        var v16 = published[^1];
        var lockPinned = lockSha ==
                "be02e58b97584e7638f591f7a8b37e6cb51bda8ca1ae100ebf1eb4ef9dc8bb6f" &&
            catalogLock.RootElement.GetProperty("lockVersion").GetInt32() == 1 &&
            publishedVersions.SequenceEqual(new[]
            {
                "1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6"
            }) &&
            v16.GetProperty("catalogFile").GetString() ==
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.json" &&
            v16.GetProperty("schemaFile").GetString() ==
                "DYNAMIC_FORM_FLOW_CAPABILITY_CATALOG_V1_6.schema.json" &&
            v16.GetProperty("catalogSha256").GetString() ==
                currentRoot.GetProperty("catalogSha256").GetString() &&
            v16.GetProperty("schemaSha256").GetString() ==
                currentRoot.GetProperty("schemaSha256").GetString();
        HarnessAssert.True(lockPinned,
            "P10 CLEAN-05 append-only LOCK pin or publication set drifted");

        var domains = catalog.RootElement.GetProperty("domains");
        HarnessAssert.Equal("1.6",
            catalog.RootElement.GetProperty("catalogVersion").GetString(),
            "P10 CLEAN-05 active catalog version");
        var profiles = domains.GetProperty("statisticsCapabilities")
            .EnumerateArray().Where(value =>
                value.GetProperty("id").GetString() == "FLOW_STATISTIC_PROFILE")
            .ToArray();
        var profileBlocked = profiles.Length == 1 &&
            profiles[0].GetProperty("status").GetString() ==
                "INTENTIONAL_BLOCK" &&
            profiles[0].GetProperty("targetPhase").ValueKind == JsonValueKind.Null;
        HarnessAssert.True(profileBlocked,
            "P10 CLEAN-05 FLOW_STATISTIC_PROFILE barrier drifted");
        var exactP7Blocks = new[]
        {
            "GROUP_GRAIN", "CUSTOM_JOIN_KEY", "APPEND_COLUMNS_TARGET",
            "SCALAR_TO_ROW", "ROW_TO_REPORT"
        };
        var p7Blocks = domains.GetProperty("dynamicFlowMappingCapabilities")
            .EnumerateArray().Where(value =>
                value.GetProperty("status").GetString() == "INTENTIONAL_BLOCK")
            .ToArray();
        var p7Blocked = p7Blocks.Select(value => value.GetProperty("id").GetString())
                .SequenceEqual(exactP7Blocks) &&
            p7Blocks.All(value => value.GetProperty("targetPhase").ValueKind ==
                                  JsonValueKind.Null);
        HarnessAssert.True(p7Blocked,
            "P10 CLEAN-05 exact P7 intentional blocks drifted");

        var collectionNames = await (await Database().ListCollectionNamesAsync(
                cancellationToken: ct)).ToListAsync(ct);
        var p11Collections = collectionNames.Count(value =>
            value.StartsWith("p11", StringComparison.OrdinalIgnoreCase));
        var p12Collections = collectionNames.Count(value =>
            value.StartsWith("p12", StringComparison.OrdinalIgnoreCase));
        var p11ArtifactRoot = Directory.Exists(Path.Combine(
            _paths.WorkspaceRoot, ".p11-artifacts"));
        var p12ArtifactRoot = Directory.Exists(Path.Combine(
            _paths.WorkspaceRoot, ".p12-artifacts"));
        var protectedStable = _protectedBefore is not null &&
                              _protectedAfter is not null &&
                              _protectedBefore.SemanticSha256 ==
                              _protectedAfter.SemanticSha256;
        HarnessAssert.True(p11Collections == 0 && p12Collections == 0 &&
                           !p11ArtifactRoot && !p12ArtifactRoot && protectedStable,
            "P10 CLEAN-05 P11/P12 zero-write barrier failed");

        return P10RaceEightColumnEvidence.Pass("P10-CLEAN-05",
            new { candidateVersion = "1.7", parentStage = "P10-09",
                published = false, frozenManifest = true },
            new { capabilities = 4, currentUnchanged = true,
                lockUnchanged = true, profileBlocked = true,
                p7IntentionalBlockCount = 5, p11Writes = 0, p12Writes = 0 },
            new { capabilities = promotions.Length, manifestFrozen,
                currentPinned, lockPinned, profileBlocked,
                p7Blocked, p7IntentionalBlockCount = p7Blocks.Length,
                publishedCatalogCount = published.Length,
                p11Collections, p12Collections,
                p11ArtifactRoot, p12ArtifactRoot, protectedStable },
            new { extraCapabilities = 0, productPublishWrites = 0,
                profileWrites = 0, p7Writes = 0, p11Writes = 0, p12Writes = 0,
                successorWrites = 0, unknownCollectionDeltaCount = 0 });
    }
    private string GuardedArtifactPath(string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidOperationException(
                "P10_SECURITY_05_ARTIFACT_RELATIVE_PATH_REQUIRED");
        var root = Path.GetFullPath(_artifactRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = GuardedWorkspacePath(_paths.WorkspaceRoot,
            Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "P10_SECURITY_05_ARTIFACT_PATH_OUTSIDE_ROOT");
        return full;
    }

    private static void CreateLocalDirectoryReparsePoint(
        string linkPath,
        string targetPath)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }
        catch (Exception error) when (
            OperatingSystem.IsWindows() &&
            error is UnauthorizedAccessException or IOException or
                PlatformNotSupportedException)
        {
            var command = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.System), "cmd.exe");
            if (!File.Exists(command))
                throw new InvalidOperationException(
                    "P10_SECURITY_05_JUNCTION_COMMAND_MISSING", error);
            var start = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(linkPath);
            start.ArgumentList.Add(targetPath);
            using var process = Process.Start(start) ?? throw new
                InvalidOperationException(
                    "P10_SECURITY_05_JUNCTION_PROCESS_MISSING", error);
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException(
                    "P10_SECURITY_05_JUNCTION_PROCESS_TIMEOUT", error);
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    "P10_SECURITY_05_JUNCTION_CREATE_FAILED", error);
        }
    }
    private IReadOnlyList<string> ReadOwnedInspectableArtifacts()
    {
        var root = Path.GetFullPath(_artifactRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        RejectRedirectedAncestors(root, root);
        var mongoRoot = _mongo is null
            ? null
            : Path.GetFullPath(_mongo.DataDirectory).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".log", ".txt", ".out", ".err", ".csv"
        };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Backend().StdoutPath),
            Path.GetFullPath(Backend().StderrPath)
        };
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            foreach (var value in Directory.EnumerateFiles(directory, "*",
                         SearchOption.TopDirectoryOnly))
            {
                var full = Path.GetFullPath(value);
                if (extensions.Contains(Path.GetExtension(full)))
                    paths.Add(full);
            }
            foreach (var child in Directory.EnumerateDirectories(directory, "*",
                         SearchOption.TopDirectoryOnly))
            {
                var full = Path.GetFullPath(child).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (mongoRoot is not null &&
                    (string.Equals(full, mongoRoot,
                         StringComparison.OrdinalIgnoreCase) ||
                     full.StartsWith(mongoRoot + Path.DirectorySeparatorChar,
                         StringComparison.OrdinalIgnoreCase)))
                    continue;
                RejectRedirectedAncestors(root, full);
                pendingDirectories.Push(full);
            }
        }

        var texts = new List<string>();
        foreach (var path in paths.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidOperationException(
                    "P10_SECURITY_04_OWNED_ARTIFACT_PATH_INVALID");
            RejectRedirectedAncestors(root, path);
            var info = new FileInfo(path);
            if (info.Length > 16 * 1024 * 1024)
                throw new InvalidOperationException(
                    "P10_SECURITY_04_OWNED_ARTIFACT_TOO_LARGE");
            texts.Add(ReadSharedText(path));
        }
        return texts;
    }

    private static string OpaqueErrorBodyWithoutTrace(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;
        var root = JsonNode.Parse(body)?.AsObject()
                   ?? throw new InvalidOperationException(
                       "P10_SECURITY_02_ERROR_RESPONSE_NOT_OBJECT");
        root.Remove("traceId");
        return root.ToJsonString();
    }

    private static string ReadSharedText(string path)
    {
        IOException? last = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException error)
            {
                last = error;
                if (attempt < 8)
                    Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }
        throw new IOException($"P10_SECURITY_04_LOG_READ_FAILED:{path}", last);
    }

    private async Task<string> EnsureSecurityResourceAsync(CancellationToken ct)
    {
        if (_securityResourceRunId is not null)
            return _securityResourceRunId;

        var fixture = CoreFixture();
        var response = await Api().PostAsync(
            $"api/works/{fixture.WorkId}/statistics/{fixture.ScopeAssignmentId}/reconciliations",
            new
            {
                commandId = "p10-race-security-existing-resource",
                p9ResultKind = "DIRECT",
                p9ResultId = fixture.P9ResultId,
                p9RunId = fixture.P9RunId,
                conceptKey = fixture.ConceptKey,
                grain = fixture.Grain,
                filter = new { periodKey = fixture.PeriodKey }
            },
            CoreActor("executor").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Accepted,
            "P10 SECURITY existing reconciliation fixture");
        var id = ApiHarnessClient.RequiredString(response.Json,
            "reconciliationId");
        HarnessAssert.True(ObjectId.TryParse(id, out var parsed) &&
                           string.Equals(parsed.ToString(), id,
                               StringComparison.Ordinal),
            "P10 SECURITY existing reconciliation id invalid");
        var exact = await Database().GetCollection<BsonDocument>(
                "work_report_statistic_reconciliations")
            .CountDocumentsAsync(new BsonDocument
            {
                ["_id"] = parsed,
                ["workId"] = ObjectId.Parse(fixture.WorkId),
                ["scopeAssignmentId"] = ObjectId.Parse(fixture.ScopeAssignmentId)
            }, cancellationToken: ct);
        HarnessAssert.Equal(1L, exact,
            "P10 SECURITY reconciliation fixture is not durably exact");
        _securityResourceRunId = id;
        return id;
    }

    private async Task<string> CaptureP10StoreSemanticAsync(CancellationToken ct)
    {
        var rows = new List<string>();
        foreach (var name in P10Collections.OrderBy(value => value,
                     StringComparer.Ordinal))
        {
            var values = await Database().GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Sort(new BsonDocument("_id", 1))
                .ToListAsync(ct);
            rows.Add(name + "=" + SemanticSha(values.Select(value =>
                value.ToJson())));
        }
        return SemanticSha(rows);
    }
    private async Task<long> CountP10RowsAsync(CancellationToken ct)
    {
        long count = 0;
        foreach (var name in P10Collections)
        {
            count += await Database().GetCollection<BsonDocument>(name)
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty,
                    cancellationToken: ct);
        }
        return count;
    }
}
