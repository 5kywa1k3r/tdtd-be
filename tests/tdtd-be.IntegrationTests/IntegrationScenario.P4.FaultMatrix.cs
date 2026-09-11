using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal sealed partial class IntegrationScenario
{
    private static readonly string[] P4FaultFamilyThenVersionPoints =
    [
        DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite,
        DynamicFlowDefinitionFaultPoints.AfterFamilyWrite,
        DynamicFlowDefinitionFaultPoints.BeforeVersionWrite,
        DynamicFlowDefinitionFaultPoints.AfterVersionWrite,
        DynamicFlowDefinitionFaultPoints.BeforeReceiptWrite,
        DynamicFlowDefinitionFaultPoints.AfterReceiptWrite,
        DynamicFlowDefinitionFaultPoints.BeforeAuditWrite,
        DynamicFlowDefinitionFaultPoints.AfterAuditWrite
    ];

    private static readonly string[] P4FaultVersionThenFamilyPoints =
    [
        DynamicFlowDefinitionFaultPoints.BeforeVersionWrite,
        DynamicFlowDefinitionFaultPoints.AfterVersionWrite,
        DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite,
        DynamicFlowDefinitionFaultPoints.AfterFamilyWrite,
        DynamicFlowDefinitionFaultPoints.BeforeReceiptWrite,
        DynamicFlowDefinitionFaultPoints.AfterReceiptWrite,
        DynamicFlowDefinitionFaultPoints.BeforeAuditWrite,
        DynamicFlowDefinitionFaultPoints.AfterAuditWrite
    ];

    private static readonly string[] P4FaultFamilyOnlyPoints =
    [
        DynamicFlowDefinitionFaultPoints.BeforeFamilyWrite,
        DynamicFlowDefinitionFaultPoints.AfterFamilyWrite,
        DynamicFlowDefinitionFaultPoints.BeforeReceiptWrite,
        DynamicFlowDefinitionFaultPoints.AfterReceiptWrite,
        DynamicFlowDefinitionFaultPoints.BeforeAuditWrite,
        DynamicFlowDefinitionFaultPoints.AfterAuditWrite
    ];

    private async Task<CaseObservation> VerifyFlowDefinitionTransactionRollbackAsync(CancellationToken ct)
    {
        var token = HarnessAssert.Required(_ownerToken, "P1-BE-003 owner token");
        var actorUserId = HarnessAssert.Required(_ownerId, "P1-BE-003 owner id");
        var formId = HarnessAssert.Required(_formId, "P1-BE-005 form");
        var marker = BuildP4FixtureMarker("FAULT_MATRIX");
        var results = new List<P4FaultWalkResult>(capacity: 6);

        results.Add(await RunP4CreateFaultWalkAsync(marker, token, actorUserId, formId, ct));
        results.Add(await RunP4LockFaultWalkAsync(marker, token, actorUserId, ct));
        results.Add(await RunP4ReopenFaultWalkAsync(marker, token, actorUserId, ct));
        results.Add(await RunP4CloneFaultWalkAsync(marker, token, actorUserId, ct));
        results.Add(await RunP4ArchiveFaultWalkAsync(marker, token, actorUserId, ct));
        results.Add(await RunP4DeleteFaultWalkAsync(marker, token, actorUserId, ct));

        var faultAttempts = results.Sum(x => x.FaultAttempts);
        var commitAttempts = results.Count;
        var replayAttempts = results.Count;
        var totalRequests = results.Sum(x => x.TotalRequests);
        HarnessAssert.Equal(46, faultAttempts, "P4 fault matrix failure count");
        HarnessAssert.Equal(6, commitAttempts, "P4 fault matrix commit count");
        HarnessAssert.Equal(6, replayAttempts, "P4 fault matrix replay count");
        HarnessAssert.Equal(58, totalRequests, "P4 fault matrix request count");

        var commandIds = results.Select(x => x.CommandId).ToArray();
        await AssertP4FaultMatrixOrphansAsync(commandIds, actorUserId, ct);

        return new CaseObservation(
            "six definition mutations traversed every applicable family/version/receipt/audit fault boundary; 46 injected 500s rolled raw Mongo BSON back, then six commits and six exact replays produced one linked receipt/audit each with no orphans",
            "operations=6;faultAttempts=46;http500=46;rollbackBsonStable=46/46;commitAttempts=6;exactReplays=6;requests=58;receipts=6;audits=6;orphans=0");
    }

    private async Task<P4FaultWalkResult> RunP4CreateFaultWalkAsync(
        string marker,
        string token,
        string actorUserId,
        string formId,
        CancellationToken ct)
    {
        var commandId = $"{BackendServerLease.P4DefinitionFaultCommandIdPrefix}create-001";
        var code = $"{marker}_CREATE";
        var request = new JsonObject
        {
            ["commandId"] = commandId,
            ["code"] = code,
            ["name"] = "P4 transaction fault create",
            ["rootDynamicFormTemplateId"] = formId,
            ["payload"] = BuildFlowPayload(formId)
        };
        var result = await WalkP4FaultBoundariesAsync(
            "CREATE",
            commandId,
            P4FaultFamilyThenVersionPoints,
            actorUserId,
            familyIds: [],
            familyCodes: [code],
            () => _api.PostAsync("api/dynamic-flow-templates", request, token, ct: ct),
            HttpStatusCode.OK,
            ct);

        AssertP4FaultCountDelta(result, families: 1, versions: 1, receipts: 1, audits: 1);
        var family = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Code == code)
            .SingleAsync(ct);
        var version = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.TemplateId == family.Id)
            .SingleAsync(ct);
        HarnessAssert.Equal(1, family.FamilyRevision, "P4 create fault matrix family revision");
        HarnessAssert.Equal(DynamicFlowTemplateStatuses.Draft, family.Status, "P4 create fault matrix family status");
        HarnessAssert.Equal(1, version.VersionNo, "P4 create fault matrix version number");
        HarnessAssert.Equal(DynamicFlowTemplateVersionStatuses.Draft, version.Status, "P4 create fault matrix version status");
        await AssertP4FaultCommitArtifactsAsync(commandId, "CREATE", actorUserId, family.Id, version.Id, ct);
        return result;
    }

    private async Task<P4FaultWalkResult> RunP4LockFaultWalkAsync(
        string marker,
        string token,
        string actorUserId,
        CancellationToken ct)
    {
        var familyBody = await CreateP4DraftFixtureAsync(
            $"FM_LOCK_{marker[^12..]}",
            token,
            "p4-fault-setup-lock-create",
            ct);
        var familyId = ApiHarnessClient.RequiredString(familyBody, "id");
        var draftBody = ApiHarnessClient.RequiredObject(familyBody["draftVersion"], "P4 fault lock draft");
        var versionId = ApiHarnessClient.RequiredString(draftBody, "id");
        var familyBefore = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == familyId)
            .SingleAsync(ct);
        var versionBefore = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == versionId)
            .SingleAsync(ct);
        var commandId = $"{BackendServerLease.P4DefinitionFaultCommandIdPrefix}lock-001";
        var request = new
        {
            commandId,
            expectedFamilyRevision = familyBefore.FamilyRevision,
            expectedDraftRevision = versionBefore.DraftRevision,
            expectedPayloadHash = versionBefore.PayloadHash
        };
        var result = await WalkP4FaultBoundariesAsync(
            "LOCK",
            commandId,
            P4FaultVersionThenFamilyPoints,
            actorUserId,
            familyIds: [familyId],
            familyCodes: [],
            () => _api.PostAsync(
                $"api/dynamic-flow-templates/{familyId}/versions/{versionId}/lock",
                request,
                token,
                ct: ct),
            HttpStatusCode.OK,
            ct);

        AssertP4FaultCountDelta(result, families: 0, versions: 0, receipts: 1, audits: 1);
        var familyAfter = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == familyId)
            .SingleAsync(ct);
        var versionAfter = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == versionId)
            .SingleAsync(ct);
        HarnessAssert.Equal(familyBefore.FamilyRevision + 1, familyAfter.FamilyRevision, "P4 lock fault matrix family revision");
        HarnessAssert.Equal(DynamicFlowTemplateStatuses.Active, familyAfter.Status, "P4 lock fault matrix family status");
        HarnessAssert.Equal(versionId, familyAfter.CurrentVersionId, "P4 lock fault matrix current version");
        HarnessAssert.Equal(versionBefore.PayloadHash, familyAfter.CurrentVersionHash, "P4 lock fault matrix current hash");
        HarnessAssert.Equal(DynamicFlowTemplateVersionStatuses.Locked, versionAfter.Status, "P4 lock fault matrix version status");
        HarnessAssert.Equal(versionBefore.DraftRevision, versionAfter.DraftRevision, "P4 lock fault matrix draft revision");
        HarnessAssert.Equal(versionBefore.PayloadHash, versionAfter.PayloadHash, "P4 lock fault matrix payload hash");
        HarnessAssert.True(versionAfter.DefinitionLockable, "P4 lock fault matrix lockable flag");
        await AssertP4FaultCommitArtifactsAsync(commandId, "LOCK", actorUserId, familyId, versionId, ct);
        return result;
    }

    private async Task<P4FaultWalkResult> RunP4ReopenFaultWalkAsync(
        string marker,
        string token,
        string actorUserId,
        CancellationToken ct)
    {
        var fixture = await CreateAndLockP4FixtureAsync($"FM_REOPEN_{marker[^12..]}", token, ct);
        var familyId = ApiHarnessClient.RequiredString(fixture.Family, "id");
        var sourceVersionId = ApiHarnessClient.RequiredString(fixture.Version, "id");
        var familyBefore = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == familyId)
            .SingleAsync(ct);
        var sourceBefore = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == sourceVersionId)
            .SingleAsync(ct);
        var commandId = $"{BackendServerLease.P4DefinitionFaultCommandIdPrefix}reopen-001";
        var request = new
        {
            commandId,
            expectedFamilyRevision = familyBefore.FamilyRevision
        };
        var result = await WalkP4FaultBoundariesAsync(
            "REOPEN",
            commandId,
            P4FaultVersionThenFamilyPoints,
            actorUserId,
            familyIds: [familyId],
            familyCodes: [],
            () => _api.PostAsync(
                $"api/dynamic-flow-templates/{familyId}/versions/{sourceVersionId}/reopen",
                request,
                token,
                ct: ct),
            HttpStatusCode.OK,
            ct);

        AssertP4FaultCountDelta(result, families: 0, versions: 1, receipts: 1, audits: 1);
        var familyAfter = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == familyId)
            .SingleAsync(ct);
        var versionsAfter = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.TemplateId == familyId)
            .ToListAsync(ct);
        var sourceAfter = versionsAfter.Single(x => x.Id == sourceVersionId);
        var reopened = versionsAfter.Single(x => x.Id != sourceVersionId);
        HarnessAssert.True(
            sourceBefore.ToBsonDocument().Equals(sourceAfter.ToBsonDocument()),
            "P4 reopen fault matrix mutated locked source");
        HarnessAssert.Equal(familyBefore.FamilyRevision + 1, familyAfter.FamilyRevision, "P4 reopen fault matrix family revision");
        HarnessAssert.Equal(DynamicFlowTemplateVersionStatuses.Draft, reopened.Status, "P4 reopen fault matrix draft status");
        HarnessAssert.Equal(sourceBefore.VersionNo + 1, reopened.VersionNo, "P4 reopen fault matrix version number");
        HarnessAssert.Equal(sourceVersionId, reopened.OriginVersionId, "P4 reopen fault matrix origin version");
        HarnessAssert.Equal(familyId, reopened.OriginFamilyId, "P4 reopen fault matrix origin family");
        HarnessAssert.Equal(sourceBefore.PayloadHash, reopened.PayloadHash, "P4 reopen fault matrix payload hash");
        await AssertP4FaultCommitArtifactsAsync(commandId, "REOPEN", actorUserId, familyId, reopened.Id, ct);
        return result;
    }

    private async Task<P4FaultWalkResult> RunP4CloneFaultWalkAsync(
        string marker,
        string token,
        string actorUserId,
        CancellationToken ct)
    {
        var fixture = await CreateAndLockP4FixtureAsync($"FM_CLONE_{marker[^12..]}", token, ct);
        var sourceFamilyId = ApiHarnessClient.RequiredString(fixture.Family, "id");
        var sourceVersionId = ApiHarnessClient.RequiredString(fixture.Version, "id");
        var sourceFamilyBefore = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == sourceFamilyId)
            .SingleAsync(ct);
        var sourceVersionBefore = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == sourceVersionId)
            .SingleAsync(ct);
        var commandId = $"{BackendServerLease.P4DefinitionFaultCommandIdPrefix}clone-001";
        var cloneCode = $"{marker}_CLONE";
        var request = new
        {
            commandId,
            expectedFamilyRevision = sourceFamilyBefore.FamilyRevision,
            sourceVersionId,
            sourceDraftRevision = sourceVersionBefore.DraftRevision,
            sourcePayloadHash = sourceVersionBefore.PayloadHash,
            code = cloneCode,
            name = "P4 transaction fault clone"
        };
        var result = await WalkP4FaultBoundariesAsync(
            "CLONE",
            commandId,
            P4FaultFamilyThenVersionPoints,
            actorUserId,
            familyIds: [sourceFamilyId],
            familyCodes: [cloneCode],
            () => _api.PostAsync(
                $"api/dynamic-flow-templates/{sourceFamilyId}/clone",
                request,
                token,
                ct: ct),
            HttpStatusCode.OK,
            ct);

        AssertP4FaultCountDelta(result, families: 1, versions: 1, receipts: 1, audits: 1);
        var sourceFamilyAfter = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == sourceFamilyId)
            .SingleAsync(ct);
        var sourceVersionAfter = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == sourceVersionId)
            .SingleAsync(ct);
        HarnessAssert.True(
            sourceFamilyBefore.ToBsonDocument().Equals(sourceFamilyAfter.ToBsonDocument()),
            "P4 clone fault matrix mutated source family");
        HarnessAssert.True(
            sourceVersionBefore.ToBsonDocument().Equals(sourceVersionAfter.ToBsonDocument()),
            "P4 clone fault matrix mutated source version");
        var cloneFamily = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Code == cloneCode)
            .SingleAsync(ct);
        var cloneVersion = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.TemplateId == cloneFamily.Id)
            .SingleAsync(ct);
        HarnessAssert.Equal(sourceFamilyId, cloneFamily.OriginFamilyId, "P4 clone fault matrix origin family");
        HarnessAssert.Equal(sourceVersionId, cloneFamily.OriginVersionId, "P4 clone fault matrix family origin version");
        HarnessAssert.Equal(sourceVersionId, cloneVersion.OriginVersionId, "P4 clone fault matrix version origin");
        HarnessAssert.Equal(sourceVersionBefore.PayloadHash, cloneVersion.PayloadHash, "P4 clone fault matrix payload hash");
        await AssertP4FaultCommitArtifactsAsync(commandId, "CLONE", actorUserId, cloneFamily.Id, cloneVersion.Id, ct);
        return result;
    }

    private async Task<P4FaultWalkResult> RunP4ArchiveFaultWalkAsync(
        string marker,
        string token,
        string actorUserId,
        CancellationToken ct)
    {
        var fixture = await CreateAndLockP4FixtureAsync($"FM_ARCHIVE_{marker[^12..]}", token, ct);
        var familyId = ApiHarnessClient.RequiredString(fixture.Family, "id");
        var versionId = ApiHarnessClient.RequiredString(fixture.Version, "id");
        var familyBefore = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == familyId)
            .SingleAsync(ct);
        var versionBefore = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == versionId)
            .SingleAsync(ct);
        var commandId = $"{BackendServerLease.P4DefinitionFaultCommandIdPrefix}archive-001";
        var request = new
        {
            commandId,
            expectedFamilyRevision = familyBefore.FamilyRevision
        };
        var result = await WalkP4FaultBoundariesAsync(
            "ARCHIVE",
            commandId,
            P4FaultFamilyOnlyPoints,
            actorUserId,
            familyIds: [familyId],
            familyCodes: [],
            () => _api.PostAsync(
                $"api/dynamic-flow-templates/{familyId}/archive",
                request,
                token,
                ct: ct),
            HttpStatusCode.OK,
            ct);

        AssertP4FaultCountDelta(result, families: 0, versions: 0, receipts: 1, audits: 1);
        var familyAfter = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == familyId)
            .SingleAsync(ct);
        var versionAfter = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == versionId)
            .SingleAsync(ct);
        HarnessAssert.Equal(familyBefore.FamilyRevision + 1, familyAfter.FamilyRevision, "P4 archive fault matrix family revision");
        HarnessAssert.Equal(DynamicFlowTemplateStatuses.Archived, familyAfter.Status, "P4 archive fault matrix status");
        HarnessAssert.True(
            versionBefore.ToBsonDocument().Equals(versionAfter.ToBsonDocument()),
            "P4 archive fault matrix mutated version");
        await AssertP4FaultCommitArtifactsAsync(commandId, "ARCHIVE", actorUserId, familyId, null, ct);
        return result;
    }

    private async Task<P4FaultWalkResult> RunP4DeleteFaultWalkAsync(
        string marker,
        string token,
        string actorUserId,
        CancellationToken ct)
    {
        var familyBody = await CreateP4DraftFixtureAsync(
            $"FM_DELETE_{marker[^12..]}",
            token,
            "p4-fault-setup-delete-create",
            ct);
        var familyId = ApiHarnessClient.RequiredString(familyBody, "id");
        var familyBefore = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == familyId)
            .SingleAsync(ct);
        var versionsBefore = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.TemplateId == familyId)
            .ToListAsync(ct);
        HarnessAssert.Equal(1, versionsBefore.Count, "P4 delete fault matrix setup version count");
        var commandId = $"{BackendServerLease.P4DefinitionFaultCommandIdPrefix}delete-001";
        var request = new
        {
            commandId,
            expectedFamilyRevision = familyBefore.FamilyRevision
        };
        var result = await WalkP4FaultBoundariesAsync(
            "DELETE",
            commandId,
            P4FaultFamilyThenVersionPoints,
            actorUserId,
            familyIds: [familyId],
            familyCodes: [],
            () => _api.SendAsync(
                HttpMethod.Delete,
                $"api/dynamic-flow-templates/{familyId}",
                request,
                token,
                headers: null,
                ct),
            HttpStatusCode.NoContent,
            ct);

        AssertP4FaultCountDelta(result, families: 0, versions: 0, receipts: 1, audits: 1);
        var familyAfter = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == familyId)
            .SingleAsync(ct);
        var versionsAfter = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.TemplateId == familyId)
            .ToListAsync(ct);
        HarnessAssert.Equal(familyBefore.FamilyRevision + 1, familyAfter.FamilyRevision, "P4 delete fault matrix family revision");
        HarnessAssert.True(familyAfter.IsDeleted, "P4 delete fault matrix family soft-delete");
        HarnessAssert.True(versionsAfter.All(x => x.IsDeleted), "P4 delete fault matrix version soft-delete");
        await AssertP4FaultCommitArtifactsAsync(commandId, "DELETE", actorUserId, familyId, null, ct);
        return result;
    }

    private async Task<P4FaultWalkResult> WalkP4FaultBoundariesAsync(
        string operation,
        string commandId,
        IReadOnlyList<string> orderedFaultPoints,
        string actorUserId,
        IReadOnlyCollection<string> familyIds,
        IReadOnlyCollection<string> familyCodes,
        Func<Task<ApiHarnessResponse>> invoke,
        HttpStatusCode successStatus,
        CancellationToken ct)
    {
        HarnessAssert.True(
            commandId.StartsWith(BackendServerLease.P4DefinitionFaultCommandIdPrefix, StringComparison.Ordinal),
            $"{operation} fault command does not use the configured prefix");
        var baseline = await CaptureP4FaultMongoSnapshotAsync(
            familyIds,
            familyCodes,
            actorUserId,
            commandId,
            ct);
        HarnessAssert.Equal(0, baseline.Receipts.Count, $"{operation} fault setup receipt collision");
        HarnessAssert.Equal(0, baseline.Audits.Count, $"{operation} fault setup audit collision");

        foreach (var faultPoint in orderedFaultPoints)
        {
            var markerBefore = CountP4FaultLogMarker(faultPoint);
            var response = await invoke();
            ApiHarnessClient.ExpectStatus(
                response,
                HttpStatusCode.InternalServerError,
                $"{operation} fault {faultPoint}");
            AssertErrorCode(response, "COMMON_INTERNAL_ERROR", $"{operation} fault {faultPoint}");
            await WaitForP4FaultLogMarkerAsync(faultPoint, markerBefore + 1, ct);
            HarnessAssert.Equal(
                markerBefore + 1,
                CountP4FaultLogMarker(faultPoint),
                $"{operation} fault marker {faultPoint}");

            var rolledBack = await CaptureP4FaultMongoSnapshotAsync(
                familyIds,
                familyCodes,
                actorUserId,
                commandId,
                ct);
            AssertP4FaultMongoSnapshotEqual(
                baseline,
                rolledBack,
                $"{operation} rollback after {faultPoint}");
        }

        var committedResponse = await invoke();
        ApiHarnessClient.ExpectStatus(committedResponse, successStatus, $"{operation} commit after injected faults");
        var committed = await CaptureP4FaultMongoSnapshotAsync(
            familyIds,
            familyCodes,
            actorUserId,
            commandId,
            ct);
        HarnessAssert.Equal(1, committed.Receipts.Count, $"{operation} committed receipt count");
        HarnessAssert.Equal(1, committed.Audits.Count, $"{operation} committed audit count");

        var replayResponse = await invoke();
        ApiHarnessClient.ExpectStatus(replayResponse, successStatus, $"{operation} exact replay");
        var replayed = await CaptureP4FaultMongoSnapshotAsync(
            familyIds,
            familyCodes,
            actorUserId,
            commandId,
            ct);
        AssertP4FaultMongoSnapshotEqual(committed, replayed, $"{operation} exact replay");

        return new P4FaultWalkResult(
            operation,
            commandId,
            orderedFaultPoints.Count,
            orderedFaultPoints.Count + 2,
            baseline,
            committed);
    }

    private async Task<P4FaultMongoSnapshot> CaptureP4FaultMongoSnapshotAsync(
        IReadOnlyCollection<string> familyIds,
        IReadOnlyCollection<string> familyCodes,
        string actorUserId,
        string commandId,
        CancellationToken ct)
    {
        var familyCollection = _database.GetCollection<BsonDocument>("dynamic_flow_templates");
        var versionCollection = _database.GetCollection<BsonDocument>("dynamic_flow_template_versions");
        var receiptCollection = _database.GetCollection<BsonDocument>("dynamic_flow_definition_command_receipts");
        var auditCollection = _database.GetCollection<BsonDocument>("user_action_logs");
        var familyFilters = new List<FilterDefinition<BsonDocument>>();
        if (familyIds.Count > 0)
        {
            familyFilters.Add(Builders<BsonDocument>.Filter.In(
                "_id",
                familyIds.Select(x => (BsonValue)ObjectId.Parse(x))));
        }
        if (familyCodes.Count > 0)
            familyFilters.Add(Builders<BsonDocument>.Filter.In("code", familyCodes));
        var familyFilter = familyFilters.Count switch
        {
            0 => Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Empty),
            1 => familyFilters[0],
            _ => Builders<BsonDocument>.Filter.Or(familyFilters)
        };
        var families = await familyCollection
            .Find(familyFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        var selectedFamilyIds = familyIds
            .Select(ObjectId.Parse)
            .Concat(families.Select(x => x["_id"].AsObjectId))
            .Distinct()
            .ToArray();
        var versionFilter = selectedFamilyIds.Length == 0
            ? Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Empty)
            : Builders<BsonDocument>.Filter.In(
                "templateId",
                selectedFamilyIds.Select(x => (BsonValue)x));
        var versions = await versionCollection
            .Find(versionFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        var receiptFilter =
            Builders<BsonDocument>.Filter.Eq("actorUserId", ObjectId.Parse(actorUserId)) &
            Builders<BsonDocument>.Filter.Eq("commandId", commandId);
        var receipts = await receiptCollection
            .Find(receiptFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);
        var auditFilter =
            Builders<BsonDocument>.Filter.Eq("actorUserId", ObjectId.Parse(actorUserId)) &
            Builders<BsonDocument>.Filter.Eq("data.commandId", commandId);
        var audits = await auditCollection
            .Find(auditFilter)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(ct);

        return new P4FaultMongoSnapshot(
            await CaptureP4DefinitionCountsAsync(ct),
            families,
            versions,
            receipts,
            audits);
    }

    private static void AssertP4FaultMongoSnapshotEqual(
        P4FaultMongoSnapshot expected,
        P4FaultMongoSnapshot actual,
        string context)
    {
        HarnessAssert.Equal(expected.Counts, actual.Counts, $"{context} global counts");
        AssertP4FaultBsonListEqual(expected.Families, actual.Families, $"{context} families");
        AssertP4FaultBsonListEqual(expected.Versions, actual.Versions, $"{context} versions");
        AssertP4FaultBsonListEqual(expected.Receipts, actual.Receipts, $"{context} receipts");
        AssertP4FaultBsonListEqual(expected.Audits, actual.Audits, $"{context} audits");
    }

    private static void AssertP4FaultBsonListEqual(
        IReadOnlyList<BsonDocument> expected,
        IReadOnlyList<BsonDocument> actual,
        string context)
    {
        HarnessAssert.Equal(expected.Count, actual.Count, $"{context} count");
        for (var index = 0; index < expected.Count; index++)
        {
            HarnessAssert.True(
                expected[index].Equals(actual[index]),
                $"{context} BSON changed at index {index}");
        }
    }

    private static void AssertP4FaultCountDelta(
        P4FaultWalkResult result,
        long families,
        long versions,
        long receipts,
        long audits)
    {
        HarnessAssert.Equal(
            result.Baseline.Counts.Families + families,
            result.Committed.Counts.Families,
            $"{result.Operation} family count delta");
        HarnessAssert.Equal(
            result.Baseline.Counts.Versions + versions,
            result.Committed.Counts.Versions,
            $"{result.Operation} version count delta");
        HarnessAssert.Equal(
            result.Baseline.Counts.Receipts + receipts,
            result.Committed.Counts.Receipts,
            $"{result.Operation} receipt count delta");
        HarnessAssert.Equal(
            result.Baseline.Counts.Audits + audits,
            result.Committed.Counts.Audits,
            $"{result.Operation} audit count delta");
    }

    private async Task AssertP4FaultCommitArtifactsAsync(
        string commandId,
        string commandKind,
        string actorUserId,
        string familyId,
        string? versionId,
        CancellationToken ct)
    {
        var receipts = _database.GetCollection<DynamicFlowDefinitionCommandReceipt>(
            "dynamic_flow_definition_command_receipts");
        var audits = _database.GetCollection<UserActionLog>("user_action_logs");
        var receipt = await receipts
            .Find(x =>
                x.ActorUserId == actorUserId &&
                x.CommandKind == commandKind &&
                x.CommandId == commandId)
            .SingleAsync(ct);
        var linkedAudits = await audits
            .Find(x => x.DynamicFlowCommandReceiptId == receipt.Id)
            .ToListAsync(ct);
        HarnessAssert.Equal(DynamicFlowDefinitionCommandOutcomes.Succeeded, receipt.Outcome, $"{commandKind} receipt outcome");
        HarnessAssert.Equal(commandId, receipt.CorrelationId, $"{commandKind} receipt correlation");
        HarnessAssert.Equal(familyId, receipt.FamilyId, $"{commandKind} receipt family");
        HarnessAssert.Equal(versionId, receipt.VersionId, $"{commandKind} receipt version");
        HarnessAssert.Equal(1, linkedAudits.Count, $"{commandKind} linked audit count");
        var audit = linkedAudits[0];
        HarnessAssert.Equal(UserActionLogActions.DynamicFlowDefinitionMutated, audit.Action, $"{commandKind} audit action");
        HarnessAssert.Equal("DYNAMIC_FLOW_DEFINITION", audit.Scope, $"{commandKind} audit scope");
        HarnessAssert.Equal(UserActionLogResults.Success, audit.Result, $"{commandKind} audit result");
        HarnessAssert.Equal(commandKind, audit.Summary, $"{commandKind} audit summary");
        HarnessAssert.True(
            audit.Data is not null &&
            audit.Data.TryGetValue("commandId", out var auditCommandId) &&
            string.Equals(commandId, auditCommandId, StringComparison.Ordinal),
            $"{commandKind} audit command id");
        HarnessAssert.True(
            audit.Data is not null &&
            audit.Data.TryGetValue("requestHash", out var auditRequestHash) &&
            string.Equals(receipt.RequestHash, auditRequestHash, StringComparison.Ordinal),
            $"{commandKind} audit request hash");

        var family = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == familyId)
            .SingleAsync(ct);
        HarnessAssert.Equal(family.FamilyRevision, receipt.ResultFamilyRevision, $"{commandKind} result family revision");
        if (versionId is not null)
        {
            var version = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
                .Find(x => x.Id == versionId)
                .SingleAsync(ct);
            HarnessAssert.Equal(version.DraftRevision, receipt.ResultDraftRevision, $"{commandKind} result draft revision");
            HarnessAssert.Equal(version.PayloadHash, receipt.ResultPayloadHash, $"{commandKind} result payload hash");
        }
        else
        {
            HarnessAssert.Equal<int?>(null, receipt.ResultDraftRevision, $"{commandKind} null result draft revision");
            HarnessAssert.Equal<string?>(null, receipt.ResultPayloadHash, $"{commandKind} null result payload hash");
        }
    }

    private async Task AssertP4FaultMatrixOrphansAsync(
        IReadOnlyCollection<string> commandIds,
        string actorUserId,
        CancellationToken ct)
    {
        var receipts = await _database
            .GetCollection<DynamicFlowDefinitionCommandReceipt>("dynamic_flow_definition_command_receipts")
            .Find(x => x.ActorUserId == actorUserId && commandIds.Contains(x.CommandId))
            .ToListAsync(ct);
        var audits = await _database
            .GetCollection<UserActionLog>("user_action_logs")
            .Find(x =>
                x.ActorUserId == actorUserId &&
                x.DynamicFlowCommandReceiptId != null)
            .ToListAsync(ct);
        audits = audits
            .Where(x =>
                x.Data is not null &&
                x.Data.TryGetValue("commandId", out var commandId) &&
                commandIds.Contains(commandId))
            .ToList();
        HarnessAssert.Equal(6, receipts.Count, "P4 fault matrix receipt cardinality");
        HarnessAssert.Equal(6, audits.Count, "P4 fault matrix audit cardinality");
        HarnessAssert.Equal(6, receipts.Select(x => x.CommandId).Distinct(StringComparer.Ordinal).Count(), "P4 fault matrix distinct commands");

        foreach (var receipt in receipts)
        {
            HarnessAssert.Equal(
                1,
                audits.Count(x => x.DynamicFlowCommandReceiptId == receipt.Id),
                $"P4 fault matrix receipt {receipt.CommandId} linked audit count");
            HarnessAssert.Equal(
                1L,
                await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
                    .CountDocumentsAsync(x => x.Id == receipt.FamilyId, cancellationToken: ct),
                $"P4 fault matrix receipt {receipt.CommandId} orphan family");
            if (receipt.VersionId is not null)
            {
                HarnessAssert.Equal(
                    1L,
                    await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
                        .CountDocumentsAsync(x => x.Id == receipt.VersionId, cancellationToken: ct),
                    $"P4 fault matrix receipt {receipt.CommandId} orphan version");
            }
        }
        foreach (var audit in audits)
        {
            HarnessAssert.Equal(
                1,
                receipts.Count(x => x.Id == audit.DynamicFlowCommandReceiptId),
                $"P4 fault matrix audit {audit.Id} orphan receipt");
        }
    }

    private int CountP4FaultLogMarker(string faultPoint)
    {
        var marker = $"{DynamicFlowDefinitionFaultInjector.FailureMessage}:{faultPoint}";
        return CountTextOccurrences(ReadP4FaultLog(_backend.StdoutPath), marker) +
               CountTextOccurrences(ReadP4FaultLog(_backend.StderrPath), marker);
    }

    private async Task WaitForP4FaultLogMarkerAsync(
        string faultPoint,
        int expectedCount,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (CountP4FaultLogMarker(faultPoint) >= expectedCount)
                return;
            await Task.Delay(20, ct);
        }

        HarnessAssert.Equal(
            expectedCount,
            CountP4FaultLogMarker(faultPoint),
            $"P4 fault log marker {faultPoint}");
    }

    private static string ReadP4FaultLog(string path)
    {
        if (!File.Exists(path))
            return string.Empty;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int CountTextOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private sealed record P4FaultMongoSnapshot(
        P4DefinitionCounts Counts,
        IReadOnlyList<BsonDocument> Families,
        IReadOnlyList<BsonDocument> Versions,
        IReadOnlyList<BsonDocument> Receipts,
        IReadOnlyList<BsonDocument> Audits);

    private sealed record P4FaultWalkResult(
        string Operation,
        string CommandId,
        int FaultAttempts,
        int TotalRequests,
        P4FaultMongoSnapshot Baseline,
        P4FaultMongoSnapshot Committed);
}
