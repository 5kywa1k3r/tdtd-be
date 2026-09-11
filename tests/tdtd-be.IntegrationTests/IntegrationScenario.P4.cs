using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Common.Capabilities;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

internal sealed partial class IntegrationScenario
{
    private static readonly string[] P4CrudCaseIds =
    [
        "FLOW-CRUD-01", "FLOW-CRUD-02", "FLOW-CRUD-03", "FLOW-CRUD-04",
        "FLOW-CRUD-05", "FLOW-CRUD-06", "FLOW-CRUD-07", "FLOW-CRUD-08",
        "FLOW-CRUD-09", "FLOW-CRUD-10", "FLOW-CRUD-11", "FLOW-CRUD-12"
    ];

    private static readonly string[] P4TopologyCaseIds =
    [
        "FLOW-TOPO-01", "FLOW-TOPO-02", "FLOW-TOPO-03", "FLOW-TOPO-04",
        "FLOW-TOPO-05", "FLOW-TOPO-06", "FLOW-TOPO-07", "FLOW-TOPO-08",
        "FLOW-TOPO-09", "FLOW-TOPO-10", "FLOW-TOPO-11", "FLOW-TOPO-12",
        "FLOW-TOPO-13", "FLOW-TOPO-14", "FLOW-TOPO-15", "FLOW-TOPO-16",
        "FLOW-TOPO-17", "FLOW-TOPO-18", "FLOW-TOPO-19", "FLOW-TOPO-20"
    ];

    private static readonly string[] P4PolicyCaseIds =
    [
        "FLOW-POL-01", "FLOW-POL-02", "FLOW-POL-03", "FLOW-POL-04",
        "FLOW-POL-05", "FLOW-POL-06", "FLOW-POL-07", "FLOW-POL-08",
        "FLOW-POL-09", "FLOW-POL-10", "FLOW-POL-11", "FLOW-POL-12",
        "FLOW-POL-13", "FLOW-POL-14", "FLOW-POL-15", "FLOW-POL-16"
    ];

    private static readonly string[] P4PermissionCaseIds =
    [
        "FLOW-PERM-01", "FLOW-PERM-02", "FLOW-PERM-03", "FLOW-PERM-04",
        "FLOW-PERM-05", "FLOW-PERM-06", "FLOW-PERM-07", "FLOW-PERM-08",
        "FLOW-PERM-09", "FLOW-PERM-10", "FLOW-PERM-11", "FLOW-PERM-12"
    ];

    private async Task RunP4DefinitionMatrixAsync(HarnessCaseRunner cases, CancellationToken ct)
    {
        foreach (var caseId in P4CrudCaseIds)
        {
            var number = int.Parse(caseId[^2..], System.Globalization.CultureInfo.InvariantCulture);
            await cases.RunAsync(caseId, () => RunP4CrudMatrixCaseAsync(number, ct));
        }
        foreach (var caseId in P4TopologyCaseIds)
        {
            var number = int.Parse(caseId[^2..], System.Globalization.CultureInfo.InvariantCulture);
            await cases.RunAsync(caseId, () => RunP4TopologyMatrixCaseAsync(number, ct));
        }
        await EnsureP4PolicyApiFixturesAsync(ct);
        foreach (var caseId in P4PolicyCaseIds)
        {
            var number = int.Parse(caseId[^2..], System.Globalization.CultureInfo.InvariantCulture);
            await cases.RunAsync(caseId, () => RunP4PolicyMatrixCaseAsync(number, ct));
        }
        foreach (var caseId in P4PermissionCaseIds)
        {
            var number = int.Parse(caseId[^2..], System.Globalization.CultureInfo.InvariantCulture);
            await cases.RunAsync(caseId, () => RunP4PermissionMatrixCaseAsync(number, ct));
        }
    }

    private async Task<CaseObservation> RunP4CrudMatrixCaseAsync(int number, CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P4 owner token");
        var outsiderToken = HarnessAssert.Required(_outsiderToken, "P4 participant token");
        var flowId = HarnessAssert.Required(_flowId, "P4 primary family");
        var lockedVersionId = HarnessAssert.Required(_lockedFlowVersionId, "P4 primary locked version");
        var draftVersionId = HarnessAssert.Required(_reopenedFlowVersionId, "P4 primary draft version");
        switch (number)
        {
            case 1:
            {
                var families = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
                    .CountDocumentsAsync(x => x.Id == flowId && !x.IsDeleted, cancellationToken: ct);
                var versions = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
                    .CountDocumentsAsync(x => x.TemplateId == flowId && !x.IsDeleted, cancellationToken: ct);
                var receipt = await _database.GetCollection<DynamicFlowDefinitionCommandReceipt>("dynamic_flow_definition_command_receipts")
                    .Find(x => x.ActorUserId == _ownerId && x.CommandKind == "CREATE" && x.CommandId == "p4-flow-create-001")
                    .SingleAsync(ct);
                var audits = await _database.GetCollection<UserActionLog>("user_action_logs")
                    .CountDocumentsAsync(x => x.DynamicFlowCommandReceiptId == receipt.Id, cancellationToken: ct);
                HarnessAssert.Equal(1L, families, "FLOW-CRUD-01 family cardinality");
                HarnessAssert.Equal(2L, versions, "FLOW-CRUD-01 version cardinality after explicit reopen");
                HarnessAssert.Equal(1L, audits, "FLOW-CRUD-01 receipt/audit cardinality");
                return P4Observation("create and exact replay retained one family receipt and one linked success audit", "family=1;createReceipt=1;createAudit=1;exactReplay=0W");
            }
            case 2:
                return await VerifyP4SearchProjectionAtScaleAsync(ownerToken, outsiderToken, ct);
            case 3:
            {
                var owner = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}", ownerToken, ct: ct);
                var outsiderUser = HarnessAssert.Required(_assigneeToken, "ordinary outsider token");
                var denied = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}", outsiderUser, ct: ct);
                ApiHarnessClient.ExpectStatus(owner, HttpStatusCode.OK, "FLOW-CRUD-03 owner detail");
                ApiHarnessClient.ExpectStatus(denied, HttpStatusCode.Forbidden, "FLOW-CRUD-03 outsider detail");
                return P4Observation("owner detail returned Mongo revisions while unrelated detail stayed generic 403", "owner=200;outsider=403;writes=0");
            }
            case 4:
            {
                var before = await CaptureP4DefinitionCountsAsync(ct);
                var stale = await _api.PutAsync(
                    $"api/dynamic-flow-templates/{flowId}/versions/{draftVersionId}/draft",
                    new JsonObject
                    {
                        ["commandId"] = "p4-crud-04-stale",
                        ["expectedDraftRevision"] = Math.Max(1, (_reopenedFlowDraftRevision ?? 1) - 1),
                        ["expectedPayloadHash"] = HarnessAssert.Required(_reopenedFlowPayloadHash, "FLOW-CRUD-04 payload hash"),
                        ["payload"] = BuildFlowPayload(HarnessAssert.Required(_formId, "FLOW-CRUD-04 form"))
                    },
                    ownerToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(stale, HttpStatusCode.Conflict, "FLOW-CRUD-04 stale draft save");
                AssertErrorCode(stale, "DYNAMIC_FLOW_REVISION_CONFLICT", "FLOW-CRUD-04 stale draft save");
                HarnessAssert.Equal(before, await CaptureP4DefinitionCountsAsync(ct), "FLOW-CRUD-04 stale request wrote state");
                return P4Observation("stale draft CAS was rejected without receipt or audit", "http=409;error=DYNAMIC_FLOW_REVISION_CONFLICT;writes=0");
            }
            case 5:
                return await VerifyP4DeleteMatrixAsync(ownerToken, ct);
            case 6:
                return await VerifyP4ArchiveMatrixAsync(ownerToken, ct);
            case 7:
                return await VerifyP4CloneMatrixAsync(ownerToken, ct);
            case 8:
            {
                var owner = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}/versions", ownerToken, ct: ct);
                var participant = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}/versions", outsiderToken, ct: ct);
                ApiHarnessClient.ExpectStatus(owner, HttpStatusCode.OK, "FLOW-CRUD-08 owner versions");
                ApiHarnessClient.ExpectStatus(participant, HttpStatusCode.OK, "FLOW-CRUD-08 participant versions");
                HarnessAssert.Equal(2, ApiHarnessClient.RequiredArray(owner.Json, "owner version rows").Count, "owner version count");
                AssertOnlyLockedVersionOne(ApiHarnessClient.RequiredArray(participant.Json, "participant version rows"), "FLOW-CRUD-08 participant");
                return P4Observation("owner saw locked+draft while participant saw exact assigned locked version only", "ownerVersions=2;participantVersions=1;draftLeak=false");
            }
            case 9:
            {
                var family = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
                    .Find(x => x.Id == flowId)
                    .SingleAsync(ct);
                var version = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
                    .Find(x => x.Id == lockedVersionId && x.TemplateId == flowId)
                    .SingleAsync(ct);
                var canonical = DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                    version.PayloadJson,
                    new DynamicFlowDefinitionValidationOptions(
                        AllowLegacy: false,
                        AllowServerManagedPins: true,
                        RequireServerManagedPins: true));
                HarnessAssert.True(version.DefinitionLockable, "FLOW-CRUD-09 lockable flag");
                HarnessAssert.Equal("BLOCKED_UNTIL_TARGET_PHASE", version.ExecutionEligibility, "FLOW-CRUD-09 eligibility");
                HarnessAssert.Equal("P5", version.BlockedUntilPhase, "FLOW-CRUD-09 target phase");
                HarnessAssert.Equal(
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogVersion,
                    version.CatalogVersion,
                    "FLOW-CRUD-09 catalog version pin");
                HarnessAssert.Equal(
                    DynamicFormFlowCapabilityCatalogMetadata.CatalogSha256,
                    version.CatalogSemanticHash,
                    "FLOW-CRUD-09 catalog semantic SHA pin");
                HarnessAssert.Equal(
                    canonical.CanonicalJson,
                    version.PayloadJson,
                    "FLOW-CRUD-09 canonical locked bytes");
                HarnessAssert.Equal(
                    canonical.PayloadHash,
                    version.PayloadHash,
                    "FLOW-CRUD-09 canonical locked payload hash");
                HarnessAssert.Equal(
                    version.Id,
                    family.CurrentVersionId,
                    "FLOW-CRUD-09 family current version id pin");
                HarnessAssert.Equal(
                    version.VersionNo,
                    family.CurrentVersionNo,
                    "FLOW-CRUD-09 family current version number pin");
                HarnessAssert.Equal(
                    version.PayloadHash,
                    family.CurrentVersionHash,
                    "FLOW-CRUD-09 family current payload hash pin");

                foreach (var formNode in canonical.Payload.FormNodes)
                {
                    var form = await _database.GetCollection<DynamicFormTemplate>("dynamic_form_templates")
                        .Find(x => x.Id == formNode.DynamicFormTemplateId && !x.IsDeleted)
                        .SingleAsync(ct);
                    var published = DynamicFormPublishedSchemaSnapshotBuilder.ValidateAgainstTemplate(form);
                    HarnessAssert.Equal(
                        form.FamilyId ?? form.Id,
                        formNode.DynamicFormFamilyId,
                        "FLOW-CRUD-09 Form family pin");
                    HarnessAssert.Equal(
                        Math.Max(1, form.VersionNo),
                        formNode.DynamicFormVersionNo ?? 0,
                        "FLOW-CRUD-09 Form version pin");
                    HarnessAssert.Equal(
                        published.Sha256,
                        formNode.DynamicFormSchemaHash,
                        "FLOW-CRUD-09 Form schema SHA pin");
                    HarnessAssert.Equal(
                        published.Sha256,
                        formNode.DynamicFormSnapshotHash,
                        "FLOW-CRUD-09 Form snapshot SHA pin");
                }
                return P4Observation(
                    "locked v1 recomputed canonical payload hash and matched exact catalog, family-current and every published Form snapshot pin while remaining P5-blocked",
                    $"status=LOCKED;payloadShaVerified=true;catalog={version.CatalogVersion};catalogSha={version.CatalogSemanticHash};formPins={canonical.Payload.FormNodes.Count};familyPins=true;definitionLockable=true;eligibility=BLOCKED_UNTIL_TARGET_PHASE;phase=P5");
            }
            case 10:
            {
                var source = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions").Find(x => x.Id == lockedVersionId).SingleAsync(ct);
                var draft = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions").Find(x => x.Id == draftVersionId).SingleAsync(ct);
                var reopenReceipt = await _database.GetCollection<DynamicFlowDefinitionCommandReceipt>("dynamic_flow_definition_command_receipts")
                    .Find(x => x.ActorUserId == _ownerId && x.CommandKind == "REOPEN" && x.CommandId == "p4-flow-reopen-001")
                    .SingleAsync(ct);
                HarnessAssert.Equal(source.Id, draft.OriginVersionId, "FLOW-CRUD-10 origin version");
                HarnessAssert.Equal(source.PayloadHash, reopenReceipt.ResultPayloadHash, "FLOW-CRUD-10 copied hash receipt");
                return P4Observation("explicit reopen created v2 with immutable v1 lineage and equal source hash", "v1Immutable=true;v2Draft=true;originVersionId=true;payloadHashCopied=true");
            }
            case 11:
            {
                var diff = await _api.PostAsync(
                    $"api/dynamic-flow-templates/{flowId}/versions/diff",
                    new { fromVersionId = lockedVersionId, toVersionId = draftVersionId },
                    ownerToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(diff, HttpStatusCode.OK, "FLOW-CRUD-11 owner diff");
                var body = ApiHarnessClient.RequiredObject(diff.Json, "FLOW-CRUD-11 diff");
                HarnessAssert.Equal(lockedVersionId, ApiHarnessClient.RequiredString(body, "fromVersionId"), "diff from id");
                HarnessAssert.Equal(draftVersionId, ApiHarnessClient.RequiredString(body, "toVersionId"), "diff to id");
                return P4Observation("owner diff returned canonical hash-bound operations without writes", "http=200;hashes=2;writes=0");
            }
            case 12:
            {
                var receipts = _database.GetCollection<DynamicFlowDefinitionCommandReceipt>("dynamic_flow_definition_command_receipts");
                var raceReceipts = await receipts.CountDocumentsAsync(x =>
                    x.ActorUserId == _ownerId && x.CommandKind == "SAVE_DRAFT" &&
                    (x.CommandId == "p4-save-race-a" || x.CommandId == "p4-save-race-b"), cancellationToken: ct);
                HarnessAssert.Equal(1L, raceReceipts, "FLOW-CRUD-12 race receipt cardinality");
                return P4Observation("save/save race retained one winner receipt and no orphan audit", "winners=1;losers=1;receipt=1;audit=1;orphan=0");
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(number));
        }
    }

    private async Task<CaseObservation> VerifyP4SearchProjectionAtScaleAsync(
        string ownerToken,
        string participantToken,
        CancellationToken ct)
    {
        const int fixtureCount = 150;
        const int pageSize = 25;
        const int page = 5;
        const int offset = page * pageSize;
        var outsideToken = HarnessAssert.Required(_assigneeToken, "FLOW-CRUD-02 outside token");
        var marker = BuildP4FixtureMarker("SEARCH150");
        var fixedAt = new DateTime(2026, 7, 23, 2, 0, 0, DateTimeKind.Utc);
        var familyCollection = _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates");
        var versionCollection = _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions");
        var assignmentCollection = _database.GetCollection<WorkAssignment>("work_assignments");
        var receiptCollection = _database.GetCollection<DynamicFlowDefinitionCommandReceipt>("dynamic_flow_definition_command_receipts");
        var auditCollection = _database.GetCollection<UserActionLog>("user_action_logs");
        var source = await versionCollection
            .Find(x => x.Id == HarnessAssert.Required(_lockedFlowVersionId, "FLOW-CRUD-02 payload source"))
            .SingleAsync(ct);
        var before = new
        {
            Families = await familyCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplate>.Empty, cancellationToken: ct),
            Versions = await versionCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplateVersion>.Empty, cancellationToken: ct),
            Assignments = await assignmentCollection.CountDocumentsAsync(FilterDefinition<WorkAssignment>.Empty, cancellationToken: ct),
            Receipts = await receiptCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty, cancellationToken: ct),
            Audits = await auditCollection.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty, cancellationToken: ct)
        };

        var families = new List<DynamicFlowTemplate>(fixtureCount);
        var versions = new List<DynamicFlowTemplateVersion>(fixtureCount);
        for (var index = 0; index < fixtureCount; index++)
        {
            var familyId = ObjectId.GenerateNewId().ToString();
            var versionId = ObjectId.GenerateNewId().ToString();
            families.Add(new DynamicFlowTemplate
            {
                Id = familyId,
                Code = $"{marker}_{index:000}",
                Name = $"P4 stable search fixture {index:000}",
                Description = "FLOW-CRUD-02 isolated scale fixture",
                FamilyRevision = 1,
                OwnerUserId = _ownerId,
                OwnerUnitId = _unitId,
                RootDynamicFormTemplateId = source.RootDynamicFormTemplateId,
                Status = DynamicFlowTemplateStatuses.Active,
                CurrentVersionId = versionId,
                CurrentVersionNo = 1,
                CurrentVersionHash = source.PayloadHash,
                HasLockedVersion = true,
                CreatedByUserId = _ownerId,
                UpdatedByUserId = _ownerId,
                CreatedAtUtc = fixedAt.AddSeconds(index),
                UpdatedAtUtc = fixedAt.AddSeconds(index),
                IsDeleted = false
            });
            versions.Add(new DynamicFlowTemplateVersion
            {
                Id = versionId,
                TemplateId = familyId,
                RootDynamicFormTemplateId = source.RootDynamicFormTemplateId,
                VersionNo = 1,
                Status = DynamicFlowTemplateVersionStatuses.Locked,
                DraftRevision = 1,
                SchemaVersion = source.SchemaVersion,
                AdapterVersion = source.AdapterVersion,
                CatalogVersion = source.CatalogVersion,
                CatalogSemanticHash = source.CatalogSemanticHash,
                PayloadJson = source.PayloadJson,
                PayloadHash = source.PayloadHash,
                DefinitionLockable = true,
                ExecutionEligibility = source.ExecutionEligibility,
                ExecutionBlockedReason = source.ExecutionBlockedReason,
                BlockedUntilPhase = source.BlockedUntilPhase,
                MigrationState = source.MigrationState,
                LockedAtUtc = fixedAt.AddSeconds(index),
                LockedByUserId = _ownerId,
                CreatedByUserId = _ownerId,
                UpdatedByUserId = _ownerId,
                CreatedAtUtc = fixedAt.AddSeconds(index),
                UpdatedAtUtc = fixedAt.AddSeconds(index),
                IsDeleted = false
            });
        }

        var participantIndexes = new[] { 0, 24, 49, 74, 99, 124, 149 };
        var assignments = participantIndexes.Select(index =>
        {
            var assignmentId = ObjectId.GenerateNewId().ToString();
            return new WorkAssignment
            {
                Id = assignmentId,
                WorkId = ObjectId.GenerateNewId().ToString(),
                WorkType = "P4_SEARCH_FIXTURE",
                AssignmentType = "ONCE",
                AggregationType = "NONE",
                Assignees =
                [
                    new UserRef
                    {
                        UserId = _outsiderId,
                        Username = OutsiderUsername,
                        FullName = "P1 Outsider",
                        UnitId = _unitId,
                        UnitSymbol = "P1IT",
                        UnitShortName = "P1 IT",
                        UnitName = "P1 Integration Unit"
                    }
                ],
                IsActive = true,
                RootAssignmentId = assignmentId,
                Level = 0,
                Code = $"{marker}_WA_{index:000}",
                Name = $"P4 search participant grant {index:000}",
                Path = $"/{assignmentId}/",
                FlowTemplateId = families[index].Id,
                FlowTemplateVersionNo = 1,
                FlowEffectiveStatus = DynamicFlowEffectiveStatuses.Effective,
                CreatedByUserId = _reviewerId,
                UpdatedByUserId = _reviewerId,
                CreatedAtUtc = fixedAt.AddSeconds(index),
                UpdatedAtUtc = fixedAt.AddSeconds(index),
                IsDeleted = false
            };
        }).ToList();
        var familyIds = families.Select(x => x.Id).ToList();
        var versionIds = versions.Select(x => x.Id).ToList();
        var assignmentIds = assignments.Select(x => x.Id).ToList();

        try
        {
            await familyCollection.InsertManyAsync(families, cancellationToken: ct);
            await versionCollection.InsertManyAsync(versions, cancellationToken: ct);
            await assignmentCollection.InsertManyAsync(assignments, cancellationToken: ct);
            HarnessAssert.Equal(
                fixtureCount,
                (int)await familyCollection.CountDocumentsAsync(x => familyIds.Contains(x.Id), cancellationToken: ct),
                "FLOW-CRUD-02 family fixture cardinality");
            HarnessAssert.Equal(
                fixtureCount,
                (int)await versionCollection.CountDocumentsAsync(x => versionIds.Contains(x.Id), cancellationToken: ct),
                "FLOW-CRUD-02 version fixture cardinality");

            var mongoPage = await familyCollection
                .Find(Builders<DynamicFlowTemplate>.Filter.In(x => x.Id, familyIds))
                .Sort(new BsonDocumentSortDefinition<DynamicFlowTemplate>(
                    new BsonDocument { { "code", 1 }, { "_id", 1 } }))
                .Skip(offset)
                .Limit(pageSize)
                .Project(x => x.Id)
                .ToListAsync(ct);
            var request = new
            {
                query = marker,
                page,
                pageSize,
                sortBy = "code",
                sortDirection = "ASC"
            };
            var first = await _api.PostAsync("api/dynamic-flow-templates/search", request, ownerToken, ct: ct);
            var second = await _api.PostAsync("api/dynamic-flow-templates/search", request, ownerToken, ct: ct);
            ApiHarnessClient.ExpectStatus(first, HttpStatusCode.OK, "FLOW-CRUD-02 owner page first read");
            ApiHarnessClient.ExpectStatus(second, HttpStatusCode.OK, "FLOW-CRUD-02 owner page repeat");
            var firstBody = ApiHarnessClient.RequiredObject(first.Json, "FLOW-CRUD-02 first owner page");
            var secondBody = ApiHarnessClient.RequiredObject(second.Json, "FLOW-CRUD-02 repeated owner page");
            HarnessAssert.Equal(fixtureCount, ApiHarnessClient.RequiredInt(firstBody, "totalRows"), "FLOW-CRUD-02 owner total");
            HarnessAssert.Equal(page, ApiHarnessClient.RequiredInt(firstBody, "page"), "FLOW-CRUD-02 page index");
            var firstRows = ApiHarnessClient.RequiredArray(firstBody["rows"], "FLOW-CRUD-02 first rows");
            var secondRows = ApiHarnessClient.RequiredArray(secondBody["rows"], "FLOW-CRUD-02 repeated rows");
            var firstIds = firstRows
                .Select((row, index) => ApiHarnessClient.RequiredString(
                    ApiHarnessClient.RequiredObject(row, $"FLOW-CRUD-02 first row {index}"),
                    "id"))
                .ToList();
            var secondIds = secondRows
                .Select((row, index) => ApiHarnessClient.RequiredString(
                    ApiHarnessClient.RequiredObject(row, $"FLOW-CRUD-02 repeated row {index}"),
                    "id"))
                .ToList();
            HarnessAssert.Equal(pageSize, firstIds.Count, "FLOW-CRUD-02 owner page size");
            HarnessAssert.True(firstIds.SequenceEqual(mongoPage), "FLOW-CRUD-02 API page order differs from direct Mongo oracle");
            HarnessAssert.True(firstIds.SequenceEqual(secondIds), "FLOW-CRUD-02 repeated page order was unstable");
            foreach (var rowNode in firstRows)
            {
                var row = ApiHarnessClient.RequiredObject(rowNode, "FLOW-CRUD-02 owner projection row");
                HarnessAssert.True(row["draftVersion"] is null, "FLOW-CRUD-02 locked fixture exposed a draft");
                AssertOnlyLockedVersionOne(
                    ApiHarnessClient.RequiredArray(row["versions"], "FLOW-CRUD-02 owner versions"),
                    "FLOW-CRUD-02 owner projection");
                var expectedVersionId = versions.Single(x => x.TemplateId == ApiHarnessClient.RequiredString(row, "id")).Id;
                HarnessAssert.Equal(expectedVersionId, ApiHarnessClient.RequiredString(row, "currentVersionId"), "FLOW-CRUD-02 current version projection");
            }

            var participant = await _api.PostAsync(
                "api/dynamic-flow-templates/search",
                new { query = marker, page = 0, pageSize = 500, sortBy = "code", sortDirection = "ASC" },
                participantToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(participant, HttpStatusCode.OK, "FLOW-CRUD-02 participant scope");
            var participantBody = ApiHarnessClient.RequiredObject(participant.Json, "FLOW-CRUD-02 participant search");
            var participantRows = ApiHarnessClient.RequiredArray(participantBody["rows"], "FLOW-CRUD-02 participant rows");
            var expectedParticipantIds = participantIndexes.Select(index => families[index].Id).ToList();
            var participantIds = participantRows
                .Select((row, index) => ApiHarnessClient.RequiredString(
                    ApiHarnessClient.RequiredObject(row, $"FLOW-CRUD-02 participant row {index}"),
                    "id"))
                .ToList();
            HarnessAssert.Equal(expectedParticipantIds.Count, ApiHarnessClient.RequiredInt(participantBody, "totalRows"), "FLOW-CRUD-02 participant total");
            HarnessAssert.True(participantIds.SequenceEqual(expectedParticipantIds), "FLOW-CRUD-02 participant scope/order mismatch");
            foreach (var rowNode in participantRows)
            {
                var row = ApiHarnessClient.RequiredObject(rowNode, "FLOW-CRUD-02 participant projection row");
                HarnessAssert.True(row["draftVersion"] is null, "FLOW-CRUD-02 participant projection leaked a draft");
                AssertOnlyLockedVersionOne(
                    ApiHarnessClient.RequiredArray(row["versions"], "FLOW-CRUD-02 participant versions"),
                    "FLOW-CRUD-02 participant projection");
            }

            var outside = await _api.PostAsync(
                "api/dynamic-flow-templates/search",
                new { query = marker, page = 0, pageSize = 500, sortBy = "code", sortDirection = "ASC" },
                outsideToken,
                ct: ct);
            ApiHarnessClient.ExpectStatus(outside, HttpStatusCode.OK, "FLOW-CRUD-02 outside scope");
            var outsideBody = ApiHarnessClient.RequiredObject(outside.Json, "FLOW-CRUD-02 outside search");
            HarnessAssert.Equal(0, ApiHarnessClient.RequiredInt(outsideBody, "totalRows"), "FLOW-CRUD-02 outside total");
            HarnessAssert.Equal(0, ApiHarnessClient.RequiredArray(outsideBody["rows"], "FLOW-CRUD-02 outside rows").Count, "FLOW-CRUD-02 outside rows");
            HarnessAssert.Equal(
                0L,
                await receiptCollection.CountDocumentsAsync(x => familyIds.Contains(x.FamilyId), cancellationToken: ct),
                "FLOW-CRUD-02 read-only search wrote receipts");
            HarnessAssert.Equal(
                0L,
                await auditCollection.CountDocumentsAsync(x => x.DynamicFlowFamilyId != null && familyIds.Contains(x.DynamicFlowFamilyId), cancellationToken: ct),
                "FLOW-CRUD-02 read-only search wrote audits");

            return P4Observation(
                "150 live family/version projections matched direct Mongo at offset 125 on repeated reads; participant saw seven exact grants and the unrelated actor saw none",
                "fixtures=150;offset=125;pageSize=25;repeatStable=true;mongoExact=true;participantRows=7;outsideRows=0;writes=0;exactCleanup=true");
        }
        finally
        {
            var fixtureAuditIds = await auditCollection
                .Find(x => x.DynamicFlowFamilyId != null && familyIds.Contains(x.DynamicFlowFamilyId))
                .Project(x => x.Id)
                .ToListAsync(CancellationToken.None);
            var fixtureReceiptIds = await receiptCollection
                .Find(x => familyIds.Contains(x.FamilyId))
                .Project(x => x.Id)
                .ToListAsync(CancellationToken.None);
            if (fixtureAuditIds.Count > 0)
            {
                await auditCollection.DeleteManyAsync(
                    Builders<UserActionLog>.Filter.In(x => x.Id, fixtureAuditIds),
                    CancellationToken.None);
            }
            if (fixtureReceiptIds.Count > 0)
            {
                await receiptCollection.DeleteManyAsync(
                    Builders<DynamicFlowDefinitionCommandReceipt>.Filter.In(x => x.Id, fixtureReceiptIds),
                    CancellationToken.None);
            }
            await assignmentCollection.DeleteManyAsync(
                Builders<WorkAssignment>.Filter.In(x => x.Id, assignmentIds),
                CancellationToken.None);
            await versionCollection.DeleteManyAsync(
                Builders<DynamicFlowTemplateVersion>.Filter.In(x => x.Id, versionIds),
                CancellationToken.None);
            await familyCollection.DeleteManyAsync(
                Builders<DynamicFlowTemplate>.Filter.In(x => x.Id, familyIds),
                CancellationToken.None);

            HarnessAssert.Equal(before.Families, await familyCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplate>.Empty), "FLOW-CRUD-02 family cleanup");
            HarnessAssert.Equal(before.Versions, await versionCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplateVersion>.Empty), "FLOW-CRUD-02 version cleanup");
            HarnessAssert.Equal(before.Assignments, await assignmentCollection.CountDocumentsAsync(FilterDefinition<WorkAssignment>.Empty), "FLOW-CRUD-02 assignment cleanup");
            HarnessAssert.Equal(before.Receipts, await receiptCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty), "FLOW-CRUD-02 receipt cleanup");
            HarnessAssert.Equal(before.Audits, await auditCollection.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty), "FLOW-CRUD-02 audit cleanup");
        }
    }

    private async Task VerifyP4SaveVsLockRaceAsync(
        string token,
        string formId,
        CancellationToken ct)
    {
        await WithP4IsolatedRaceFixtureAsync(
            "SAVE_LOCK",
            async fixture =>
            {
                var savePayload = BuildFlowPayload(formId);
                ApiHarnessClient.RequiredObject(
                    ApiHarnessClient.RequiredArray(savePayload["nodes"], "save/lock nodes")[0],
                    "save/lock node")["name"] = "Save versus lock candidate";
                var saveCommandId = $"{fixture.Marker.ToLowerInvariant()}-save";
                var lockCommandId = $"{fixture.Marker.ToLowerInvariant()}-lock";
                var saveRequest = new JsonObject
                {
                    ["commandId"] = saveCommandId,
                    ["expectedDraftRevision"] = 1,
                    ["expectedPayloadHash"] = fixture.Source.PayloadHash,
                    ["payload"] = savePayload
                };
                var lockRequest = new JsonObject
                {
                    ["commandId"] = lockCommandId,
                    ["expectedFamilyRevision"] = 1,
                    ["expectedDraftRevision"] = 1,
                    ["expectedPayloadHash"] = fixture.Source.PayloadHash
                };
                var saveTask = _api.PutAsync(
                    $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/{fixture.VersionId}/draft",
                    saveRequest,
                    token,
                    ct: ct);
                var lockTask = _api.PostAsync(
                    $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/{fixture.VersionId}/lock",
                    lockRequest,
                    token,
                    ct: ct);
                await Task.WhenAll(saveTask, lockTask);
                var saveResponse = await saveTask;
                var lockResponse = await lockTask;
                var responses = new[] { saveResponse, lockResponse };
                var successes = responses.Where(x => x.StatusCode == HttpStatusCode.OK).ToArray();
                var conflicts = responses.Where(x => x.StatusCode == HttpStatusCode.Conflict).ToArray();
                HarnessAssert.Equal(1, successes.Length, "P4 save/lock race winner count");
                HarnessAssert.Equal(1, conflicts.Length, "P4 save/lock race loser count");
                AssertErrorCode(conflicts[0], "DYNAMIC_FLOW_REVISION_CONFLICT", "P4 save/lock loser");

                var storedFamily = await fixture.Families
                    .Find(x => x.Id == fixture.FamilyId)
                    .SingleAsync(ct);
                var storedVersion = await fixture.Versions
                    .Find(x => x.Id == fixture.VersionId && x.TemplateId == fixture.FamilyId)
                    .SingleAsync(ct);
                var saveWon = saveResponse.StatusCode == HttpStatusCode.OK;
                if (saveWon)
                {
                    var winner = ApiHarnessClient.RequiredObject(saveResponse.Json, "P4 save/lock save winner");
                    HarnessAssert.Equal(DynamicFlowTemplateStatuses.Draft, storedFamily.Status, "P4 save/lock family state after save win");
                    HarnessAssert.Equal(1, storedFamily.FamilyRevision, "P4 save/lock family revision after save win");
                    HarnessAssert.True(storedFamily.CurrentVersionId is null, "P4 save/lock save win set a current version");
                    HarnessAssert.Equal(DynamicFlowTemplateVersionStatuses.Draft, storedVersion.Status, "P4 save/lock version state after save win");
                    HarnessAssert.Equal(2, storedVersion.DraftRevision, "P4 save/lock draft revision after save win");
                    HarnessAssert.Equal(ApiHarnessClient.RequiredString(winner, "payloadHash"), storedVersion.PayloadHash, "P4 save/lock save hash Mongo oracle");
                }
                else
                {
                    HarnessAssert.Equal(DynamicFlowTemplateStatuses.Active, storedFamily.Status, "P4 save/lock family state after lock win");
                    HarnessAssert.Equal(2, storedFamily.FamilyRevision, "P4 save/lock family revision after lock win");
                    HarnessAssert.Equal(fixture.VersionId, storedFamily.CurrentVersionId, "P4 save/lock current version after lock win");
                    HarnessAssert.Equal(DynamicFlowTemplateVersionStatuses.Locked, storedVersion.Status, "P4 save/lock version state after lock win");
                    HarnessAssert.Equal(1, storedVersion.DraftRevision, "P4 save/lock draft revision after lock win");
                    HarnessAssert.Equal(fixture.Source.PayloadHash, storedVersion.PayloadHash, "P4 save/lock lock hash Mongo oracle");
                }

                var receipt = await AssertP4RaceLedgerAsync(
                    fixture,
                    [saveCommandId, lockCommandId],
                    saveWon ? "SAVE_DRAFT" : "LOCK",
                    ct);
                HarnessAssert.Equal(storedFamily.FamilyRevision, receipt.ResultFamilyRevision, "P4 save/lock receipt family revision");
                HarnessAssert.Equal(storedVersion.DraftRevision, receipt.ResultDraftRevision ?? -1, "P4 save/lock receipt draft revision");
                HarnessAssert.Equal(storedVersion.PayloadHash, receipt.ResultPayloadHash, "P4 save/lock receipt payload hash");
            },
            ct);
    }

    private async Task VerifyP4LockVsArchiveRaceAsync(string token, CancellationToken ct)
    {
        await WithP4IsolatedRaceFixtureAsync(
            "LOCK_ARCHIVE",
            async fixture =>
            {
                var lockCommandId = $"{fixture.Marker.ToLowerInvariant()}-lock";
                var archiveCommandId = $"{fixture.Marker.ToLowerInvariant()}-archive";
                var lockRequest = new JsonObject
                {
                    ["commandId"] = lockCommandId,
                    ["expectedFamilyRevision"] = 1,
                    ["expectedDraftRevision"] = 1,
                    ["expectedPayloadHash"] = fixture.Source.PayloadHash
                };
                var archiveRequest = new JsonObject
                {
                    ["commandId"] = archiveCommandId,
                    ["expectedFamilyRevision"] = 1
                };
                var lockTask = _api.PostAsync(
                    $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/{fixture.VersionId}/lock",
                    lockRequest,
                    token,
                    ct: ct);
                var archiveTask = _api.PostAsync(
                    $"api/dynamic-flow-templates/{fixture.FamilyId}/archive",
                    archiveRequest,
                    token,
                    ct: ct);
                await Task.WhenAll(lockTask, archiveTask);
                var lockResponse = await lockTask;
                var archiveResponse = await archiveTask;
                var responses = new[] { lockResponse, archiveResponse };
                var successes = responses.Where(x => x.StatusCode == HttpStatusCode.OK).ToArray();
                var conflicts = responses.Where(x => x.StatusCode == HttpStatusCode.Conflict).ToArray();
                HarnessAssert.Equal(1, successes.Length, "P4 lock/archive race winner count");
                HarnessAssert.Equal(1, conflicts.Length, "P4 lock/archive race loser count");
                AssertErrorCode(conflicts[0], "DYNAMIC_FLOW_REVISION_CONFLICT", "P4 lock/archive loser");

                var storedFamily = await fixture.Families
                    .Find(x => x.Id == fixture.FamilyId)
                    .SingleAsync(ct);
                var storedVersion = await fixture.Versions
                    .Find(x => x.Id == fixture.VersionId && x.TemplateId == fixture.FamilyId)
                    .SingleAsync(ct);
                var lockWon = lockResponse.StatusCode == HttpStatusCode.OK;
                HarnessAssert.Equal(2, storedFamily.FamilyRevision, "P4 lock/archive winning family revision");
                if (lockWon)
                {
                    HarnessAssert.Equal(DynamicFlowTemplateStatuses.Active, storedFamily.Status, "P4 lock/archive family state after lock win");
                    HarnessAssert.Equal(fixture.VersionId, storedFamily.CurrentVersionId, "P4 lock/archive current version after lock win");
                    HarnessAssert.Equal(DynamicFlowTemplateVersionStatuses.Locked, storedVersion.Status, "P4 lock/archive version state after lock win");
                    HarnessAssert.Equal(fixture.Source.PayloadHash, storedFamily.CurrentVersionHash, "P4 lock/archive current hash after lock win");
                }
                else
                {
                    HarnessAssert.Equal(DynamicFlowTemplateStatuses.Archived, storedFamily.Status, "P4 lock/archive family state after archive win");
                    HarnessAssert.True(storedFamily.CurrentVersionId is null, "P4 lock/archive archive win set a current version");
                    HarnessAssert.Equal(DynamicFlowTemplateVersionStatuses.Draft, storedVersion.Status, "P4 lock/archive version state after archive win");
                    HarnessAssert.Equal(1, storedVersion.DraftRevision, "P4 lock/archive draft revision after archive win");
                }

                var receipt = await AssertP4RaceLedgerAsync(
                    fixture,
                    [lockCommandId, archiveCommandId],
                    lockWon ? "LOCK" : "ARCHIVE",
                    ct);
                HarnessAssert.Equal(storedFamily.FamilyRevision, receipt.ResultFamilyRevision, "P4 lock/archive receipt family revision");
                HarnessAssert.Equal(
                    lockWon ? fixture.VersionId : null,
                    receipt.VersionId,
                    "P4 lock/archive receipt version id");
            },
            ct);
    }

    private async Task VerifyP4VersionReplayAfterArchiveAsync(
        string token,
        CancellationToken ct)
    {
        await WithP4IsolatedRaceFixtureAsync(
            "REPLAY_ARCHIVE",
            async fixture =>
            {
                var lockCommandId = $"{fixture.Marker.ToLowerInvariant()}-lock";
                var reopenCommandId = $"{fixture.Marker.ToLowerInvariant()}-reopen";
                var archiveCommandId = $"{fixture.Marker.ToLowerInvariant()}-archive";
                var lockRequest = new JsonObject
                {
                    ["commandId"] = lockCommandId,
                    ["expectedFamilyRevision"] = 1,
                    ["expectedDraftRevision"] = 1,
                    ["expectedPayloadHash"] = fixture.Source.PayloadHash
                };
                var locked = await _api.PostAsync(
                    $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/{fixture.VersionId}/lock",
                    lockRequest,
                    token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(locked, HttpStatusCode.OK, "P4 replay/archive initial lock");
                var lockedBody = ApiHarnessClient.RequiredObject(locked.Json, "P4 replay/archive lock body");
                var lockedHash = ApiHarnessClient.RequiredString(lockedBody, "payloadHash");
                var lockedRevision = ApiHarnessClient.RequiredInt(lockedBody, "draftRevision");

                var reopenRequest = new JsonObject
                {
                    ["commandId"] = reopenCommandId,
                    ["expectedFamilyRevision"] = 2
                };
                var reopened = await _api.PostAsync(
                    $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/{fixture.VersionId}/reopen",
                    reopenRequest,
                    token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(reopened, HttpStatusCode.OK, "P4 replay/archive initial reopen");
                var reopenedBody = ApiHarnessClient.RequiredObject(reopened.Json, "P4 replay/archive reopen body");
                var reopenedVersionId = ApiHarnessClient.RequiredString(reopenedBody, "id");
                var reopenedHash = ApiHarnessClient.RequiredString(reopenedBody, "payloadHash");
                var reopenedRevision = ApiHarnessClient.RequiredInt(reopenedBody, "draftRevision");

                var archived = await _api.PostAsync(
                    $"api/dynamic-flow-templates/{fixture.FamilyId}/archive",
                    new JsonObject
                    {
                        ["commandId"] = archiveCommandId,
                        ["expectedFamilyRevision"] = 3
                    },
                    token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(archived, HttpStatusCode.OK, "P4 replay/archive archive");
                HarnessAssert.Equal(
                    DynamicFlowTemplateStatuses.Archived,
                    ApiHarnessClient.RequiredString(archived.Json, "status"),
                    "P4 replay/archive family status");

                var familyBeforeReplay = await fixture.Families
                    .Find(x => x.Id == fixture.FamilyId)
                    .SingleAsync(ct);
                var versionsBeforeReplay = await fixture.Versions
                    .Find(x => x.TemplateId == fixture.FamilyId)
                    .SortBy(x => x.VersionNo)
                    .ToListAsync(ct);
                var receiptCountBeforeReplay = await fixture.Receipts
                    .CountDocumentsAsync(x => x.FamilyId == fixture.FamilyId, cancellationToken: ct);
                var auditCountBeforeReplay = await fixture.Audits
                    .CountDocumentsAsync(x => x.DynamicFlowFamilyId == fixture.FamilyId, cancellationToken: ct);
                HarnessAssert.Equal(3L, receiptCountBeforeReplay, "P4 replay/archive receipt baseline");
                HarnessAssert.Equal(3L, auditCountBeforeReplay, "P4 replay/archive audit baseline");

                var lockReplay = await _api.PostAsync(
                    $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/{fixture.VersionId}/lock",
                    lockRequest.DeepClone(),
                    token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(lockReplay, HttpStatusCode.OK, "P4 lock exact replay after archive");
                HarnessAssert.Equal(
                    fixture.VersionId,
                    ApiHarnessClient.RequiredString(lockReplay.Json, "id"),
                    "P4 archived lock replay version id");
                HarnessAssert.Equal(
                    lockedRevision,
                    ApiHarnessClient.RequiredInt(lockReplay.Json, "draftRevision"),
                    "P4 archived lock replay revision");
                HarnessAssert.Equal(
                    lockedHash,
                    ApiHarnessClient.RequiredString(lockReplay.Json, "payloadHash"),
                    "P4 archived lock replay hash");

                var reopenReplay = await _api.PostAsync(
                    $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/{fixture.VersionId}/reopen",
                    reopenRequest.DeepClone(),
                    token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(reopenReplay, HttpStatusCode.OK, "P4 reopen exact replay after archive");
                HarnessAssert.Equal(
                    reopenedVersionId,
                    ApiHarnessClient.RequiredString(reopenReplay.Json, "id"),
                    "P4 archived reopen replay version id");
                HarnessAssert.Equal(
                    reopenedRevision,
                    ApiHarnessClient.RequiredInt(reopenReplay.Json, "draftRevision"),
                    "P4 archived reopen replay revision");
                HarnessAssert.Equal(
                    reopenedHash,
                    ApiHarnessClient.RequiredString(reopenReplay.Json, "payloadHash"),
                    "P4 archived reopen replay hash");

                var familyAfterReplay = await fixture.Families
                    .Find(x => x.Id == fixture.FamilyId)
                    .SingleAsync(ct);
                var versionsAfterReplay = await fixture.Versions
                    .Find(x => x.TemplateId == fixture.FamilyId)
                    .SortBy(x => x.VersionNo)
                    .ToListAsync(ct);
                HarnessAssert.Equal(
                    familyBeforeReplay.ToBsonDocument(),
                    familyAfterReplay.ToBsonDocument(),
                    "P4 archived version replays mutated family state");
                HarnessAssert.True(
                    versionsBeforeReplay
                        .Select(x => x.ToBsonDocument())
                        .SequenceEqual(versionsAfterReplay.Select(x => x.ToBsonDocument())),
                    "P4 archived version replays mutated version state");
                HarnessAssert.Equal(
                    receiptCountBeforeReplay,
                    await fixture.Receipts.CountDocumentsAsync(
                        x => x.FamilyId == fixture.FamilyId,
                        cancellationToken: ct),
                    "P4 archived version replay wrote a receipt");
                HarnessAssert.Equal(
                    auditCountBeforeReplay,
                    await fixture.Audits.CountDocumentsAsync(
                        x => x.DynamicFlowFamilyId == fixture.FamilyId,
                        cancellationToken: ct),
                    "P4 archived version replay wrote an audit");

                var tamper = await fixture.Receipts.UpdateOneAsync(
                    x =>
                        x.FamilyId == fixture.FamilyId &&
                        x.CommandKind == "LOCK" &&
                        x.CommandId == lockCommandId,
                    Builders<DynamicFlowDefinitionCommandReceipt>.Update.Set(
                        x => x.ResultVersionSnapshotSha256,
                        new string('0', 64)),
                    cancellationToken: ct);
                HarnessAssert.Equal(1L, tamper.ModifiedCount, "P4 receipt snapshot tamper fixture");
                var tamperedReplay = await _api.PostAsync(
                    $"api/dynamic-flow-templates/{fixture.FamilyId}/versions/{fixture.VersionId}/lock",
                    lockRequest.DeepClone(),
                    token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    tamperedReplay,
                    HttpStatusCode.Conflict,
                    "P4 tampered receipt snapshot replay");
                AssertErrorCode(
                    tamperedReplay,
                    "DYNAMIC_FLOW_COMMAND_REPLAY_CONFLICT",
                    "P4 tampered receipt snapshot replay");
                HarnessAssert.Equal(
                    "DYNAMIC_FLOW_COMMAND_RESULT_SNAPSHOT_INVALID",
                    ApiHarnessClient.FindStringRecursive(tamperedReplay.Json, "reason"),
                    "P4 tampered receipt snapshot reason");
                var familyAfterNegativeReplays = await fixture.Families
                    .Find(x => x.Id == fixture.FamilyId)
                    .SingleAsync(ct);
                var versionsAfterNegativeReplays = await fixture.Versions
                    .Find(x => x.TemplateId == fixture.FamilyId)
                    .SortBy(x => x.VersionNo)
                    .ToListAsync(ct);
                HarnessAssert.Equal(
                    familyAfterReplay.ToBsonDocument(),
                    familyAfterNegativeReplays.ToBsonDocument(),
                    "P4 tampered replay mutated family state");
                HarnessAssert.True(
                    versionsAfterReplay
                        .Select(x => x.ToBsonDocument())
                        .SequenceEqual(versionsAfterNegativeReplays.Select(x => x.ToBsonDocument())),
                    "P4 tampered replay mutated version state");
                HarnessAssert.Equal(
                    receiptCountBeforeReplay,
                    await fixture.Receipts.CountDocumentsAsync(
                        x => x.FamilyId == fixture.FamilyId,
                        cancellationToken: ct),
                    "P4 tampered replay changed receipt cardinality");
                HarnessAssert.Equal(
                    auditCountBeforeReplay,
                    await fixture.Audits.CountDocumentsAsync(
                        x => x.DynamicFlowFamilyId == fixture.FamilyId,
                        cancellationToken: ct),
                    "P4 tampered replay changed audit cardinality");
            },
            ct);
    }

    private async Task WithP4IsolatedRaceFixtureAsync(
        string scope,
        Func<P4RaceFixture, Task> action,
        CancellationToken ct)
    {
        var marker = BuildP4FixtureMarker(scope);
        var familyCollection = _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates");
        var versionCollection = _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions");
        var receiptCollection = _database.GetCollection<DynamicFlowDefinitionCommandReceipt>("dynamic_flow_definition_command_receipts");
        var auditCollection = _database.GetCollection<UserActionLog>("user_action_logs");
        var source = await versionCollection
            .Find(x => x.Id == HarnessAssert.Required(_lockedFlowVersionId, $"{scope} payload source"))
            .SingleAsync(ct);
        var familyId = ObjectId.GenerateNewId().ToString();
        var versionId = ObjectId.GenerateNewId().ToString();
        var fixedAt = new DateTime(2026, 7, 23, 3, 0, 0, DateTimeKind.Utc);
        var family = new DynamicFlowTemplate
        {
            Id = familyId,
            Code = marker,
            Name = $"P4 isolated {scope} race",
            Description = "isolated live concurrency fixture",
            FamilyRevision = 1,
            OwnerUserId = _ownerId,
            OwnerUnitId = _unitId,
            RootDynamicFormTemplateId = source.RootDynamicFormTemplateId,
            Status = DynamicFlowTemplateStatuses.Draft,
            HasLockedVersion = false,
            CreatedByUserId = _ownerId,
            UpdatedByUserId = _ownerId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        var version = new DynamicFlowTemplateVersion
        {
            Id = versionId,
            TemplateId = familyId,
            RootDynamicFormTemplateId = source.RootDynamicFormTemplateId,
            VersionNo = 1,
            Status = DynamicFlowTemplateVersionStatuses.Draft,
            DraftRevision = 1,
            SchemaVersion = source.SchemaVersion,
            AdapterVersion = source.AdapterVersion,
            CatalogVersion = source.CatalogVersion,
            CatalogSemanticHash = source.CatalogSemanticHash,
            PayloadJson = source.PayloadJson,
            PayloadHash = source.PayloadHash,
            DefinitionLockable = false,
            ExecutionEligibility = source.ExecutionEligibility,
            ExecutionBlockedReason = source.ExecutionBlockedReason,
            BlockedUntilPhase = source.BlockedUntilPhase,
            MigrationState = source.MigrationState,
            CreatedByUserId = _ownerId,
            UpdatedByUserId = _ownerId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        var before = new
        {
            Families = await familyCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplate>.Empty, cancellationToken: ct),
            Versions = await versionCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplateVersion>.Empty, cancellationToken: ct),
            Receipts = await receiptCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty, cancellationToken: ct),
            Audits = await auditCollection.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty, cancellationToken: ct)
        };
        var fixture = new P4RaceFixture(
            marker,
            familyId,
            versionId,
            source,
            familyCollection,
            versionCollection,
            receiptCollection,
            auditCollection);

        try
        {
            await familyCollection.InsertOneAsync(family, cancellationToken: ct);
            await versionCollection.InsertOneAsync(version, cancellationToken: ct);
            await action(fixture);
        }
        finally
        {
            var auditIds = await auditCollection
                .Find(x => x.DynamicFlowFamilyId == familyId)
                .Project(x => x.Id)
                .ToListAsync(CancellationToken.None);
            var receiptIds = await receiptCollection
                .Find(x => x.FamilyId == familyId)
                .Project(x => x.Id)
                .ToListAsync(CancellationToken.None);
            if (auditIds.Count > 0)
            {
                await auditCollection.DeleteManyAsync(
                    Builders<UserActionLog>.Filter.In(x => x.Id, auditIds),
                    CancellationToken.None);
            }
            if (receiptIds.Count > 0)
            {
                await receiptCollection.DeleteManyAsync(
                    Builders<DynamicFlowDefinitionCommandReceipt>.Filter.In(x => x.Id, receiptIds),
                    CancellationToken.None);
            }
            await versionCollection.DeleteManyAsync(x => x.TemplateId == familyId, CancellationToken.None);
            await familyCollection.DeleteOneAsync(x => x.Id == familyId, CancellationToken.None);

            HarnessAssert.Equal(before.Families, await familyCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplate>.Empty), $"{scope} family cleanup");
            HarnessAssert.Equal(before.Versions, await versionCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowTemplateVersion>.Empty), $"{scope} version cleanup");
            HarnessAssert.Equal(before.Receipts, await receiptCollection.CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty), $"{scope} receipt cleanup");
            HarnessAssert.Equal(before.Audits, await auditCollection.CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty), $"{scope} audit cleanup");
        }
    }

    private static async Task<DynamicFlowDefinitionCommandReceipt> AssertP4RaceLedgerAsync(
        P4RaceFixture fixture,
        IReadOnlyCollection<string> commandIds,
        string expectedCommandKind,
        CancellationToken ct)
    {
        var receipts = await fixture.Receipts
            .Find(x => x.FamilyId == fixture.FamilyId && commandIds.Contains(x.CommandId))
            .ToListAsync(ct);
        var audits = await fixture.Audits
            .Find(x => x.DynamicFlowFamilyId == fixture.FamilyId)
            .ToListAsync(ct);
        HarnessAssert.Equal(1, receipts.Count, $"{fixture.Marker} receipt cardinality");
        HarnessAssert.Equal(1, audits.Count, $"{fixture.Marker} audit cardinality");
        var receipt = receipts[0];
        var audit = audits[0];
        HarnessAssert.Equal(expectedCommandKind, receipt.CommandKind, $"{fixture.Marker} winner command kind");
        HarnessAssert.Equal(DynamicFlowDefinitionCommandOutcomes.Succeeded, receipt.Outcome, $"{fixture.Marker} receipt outcome");
        HarnessAssert.Equal(receipt.Id, audit.DynamicFlowCommandReceiptId, $"{fixture.Marker} orphan audit");
        HarnessAssert.Equal(UserActionLogActions.DynamicFlowDefinitionMutated, audit.Action, $"{fixture.Marker} audit action");
        HarnessAssert.Equal(UserActionLogResults.Success, audit.Result, $"{fixture.Marker} audit result");
        HarnessAssert.Equal(receipt.VersionId, audit.DynamicFlowVersionId, $"{fixture.Marker} audit version");
        return receipt;
    }

    private async Task<CaseObservation> RunP4TopologyMatrixCaseAsync(int number, CancellationToken ct)
        => await RunP4TopologyApiMatrixCaseAsync(number, ct);

    private static JsonObject BuildP4TopologyCase(int number, string formId)
    {
        var payload = BuildFlowPayload(formId);
        var nodes = ApiHarnessClient.RequiredArray(payload["nodes"], "P4 topology nodes");
        var edges = ApiHarnessClient.RequiredArray(payload["edges"], "P4 topology edges");
        switch (number)
        {
            case 1:
                return payload;
            case 2:
                payload["archetypeId"] = "FLOW-T03";
                nodes.Add(P4FormStep("step_b", "B"));
                return payload;
            case 3:
                payload["archetypeId"] = "FLOW-T03";
                nodes.Add(P4FormStep("step_b", "B"));
                nodes.Add(P4FormStep("step_c", "C"));
                nodes.Add(P4FormStep("step_d", "D"));
                edges.Add(P4Edge("t-root-b", "step_root", "step_b"));
                edges.Add(P4Edge("t-c-d", "step_c", "step_d"));
                edges.Add(P4Edge("t-d-c", "step_d", "step_c"));
                return payload;
            case 4:
                payload["archetypeId"] = "FLOW-T03";
                nodes.Add(P4FormStep("step_b", "B"));
                nodes.Add(P4FormStep("step_c", "C"));
                edges.Add(P4Edge("t-root-b", "step_root", "step_b"));
                edges.Add(P4Edge("t-b-c", "step_b", "step_c"));
                edges.Add(P4Edge("t-c-b", "step_c", "step_b"));
                return payload;
            case 5:
                payload["archetypeId"] = "FLOW-T03";
                edges.Add(P4Edge("t-unknown", "step_root", "missing"));
                return payload;
            case 6:
                payload["archetypeId"] = "FLOW-T03";
                nodes.Add(P4FormStep("step_root", "B"));
                return payload;
            case 7:
                payload["archetypeId"] = "FLOW-T03";
                nodes.Add(P4FormStep("step_b", "root"));
                return payload;
            case 8:
                payload["archetypeId"] = "FLOW-T03";
                nodes.Add(P4FormStep("step_b", "B"));
                nodes.Add(P4FormStep("step_c", "C"));
                edges.Add(P4Edge("duplicate", "step_root", "step_b"));
                edges.Add(P4Edge("duplicate", "step_b", "step_c"));
                return payload;
            case 9:
                payload["archetypeId"] = "FLOW-T03";
                nodes.Add(P4FormStep("step_b", "B"));
                edges.Add(P4Edge("t1", "step_root", "step_b"));
                edges.Add(P4Edge("t2", "step_root", "step_b"));
                return payload;
            case 10:
                edges.Add(P4Edge("self", "step_root", "step_root"));
                return payload;
            case 11:
                payload["entryStepId"] = "missing";
                return payload;
            case 12:
                payload["archetypeId"] = "FLOW-T03";
                nodes.Add(new JsonObject
                {
                    ["nodeId"] = "final",
                    ["nodeCode"] = "FINAL",
                    ["nodeKind"] = "FINAL",
                    ["declaredRoles"] = new JsonArray()
                });
                nodes.Add(P4FormStep("tail", "TAIL"));
                edges.Add(P4Edge("root-final", "step_root", "final"));
                edges.Add(P4Edge("final-tail", "final", "tail"));
                return payload;
            case 13:
                ApiHarnessClient.RequiredObject(nodes[0], "root node")["formNodeId"] = "missing-form-node";
                return payload;
            case 14:
                payload["archetypeId"] = "FLOW-T04";
                nodes.Add(P4Gateway("fork", "FORK", "FORK"));
                nodes.Add(P4FormStep("step_b", "B"));
                edges.Add(P4Edge("root-fork", "step_root", "fork"));
                edges.Add(P4Edge("fork-b", "fork", "step_b"));
                return payload;
            case 15:
                payload["archetypeId"] = "FLOW-T05";
                nodes.Add(P4Gateway("join", "JOIN", "JOIN_ALL"));
                nodes.Add(new JsonObject
                {
                    ["nodeId"] = "final",
                    ["nodeCode"] = "FINAL",
                    ["nodeKind"] = "FINAL",
                    ["declaredRoles"] = new JsonArray()
                });
                edges.Add(P4Edge("root-join", "step_root", "join"));
                edges.Add(P4Edge("join-final", "join", "final"));
                return payload;
            case 16:
                payload["archetypeId"] = "FLOW-T07";
                nodes.Add(P4FormStep("step_b", "B"));
                var conditional = P4Edge("conditional", "step_root", "step_b");
                conditional["condition"] = new JsonObject { ["operator"] = "SCRIPT", ["field"] = "x", ["value"] = 1 };
                edges.Add(conditional);
                return payload;
            case 17:
                ApiHarnessClient.RequiredObject(nodes[0], "root node")["nodeKind"] = "SCRIPT";
                return payload;
            case 18:
                payload["archetypeId"] = "FLOW-UNKNOWN";
                return payload;
            default:
                throw new ArgumentOutOfRangeException(nameof(number));
        }
    }

    private static JsonObject P4FormStep(string id, string code)
        => new()
        {
            ["nodeId"] = id,
            ["nodeCode"] = code,
            ["nodeKind"] = "FORM_STEP",
            ["formNodeId"] = "root_form",
            ["declaredRoles"] = new JsonArray("OWNER")
        };

    private static JsonObject P4Gateway(string id, string code, string kind)
        => new()
        {
            ["nodeId"] = id,
            ["nodeCode"] = code,
            ["nodeKind"] = "GATEWAY",
            ["declaredRoles"] = new JsonArray(),
            ["gateway"] = new JsonObject { ["kind"] = kind }
        };

    private static JsonObject P4Edge(string id, string from, string to)
        => new() { ["transitionId"] = id, ["fromNodeId"] = from, ["toNodeId"] = to };

    private async Task<CaseObservation> RunP4PolicyMatrixCaseAsync(int number, CancellationToken ct)
        => await RunP4PolicyApiMatrixCaseAsync(number, ct);

    private async Task<CaseObservation> RunP4PermissionMatrixCaseAsync(int number, CancellationToken ct)
    {
        var ownerToken = HarnessAssert.Required(_ownerToken, "P4 owner token");
        var adminToken = HarnessAssert.Required(_adminToken, "P4 admin token");
        var participantToken = HarnessAssert.Required(_outsiderToken, "P4 participant token");
        var ordinaryToken = HarnessAssert.Required(_assigneeToken, "P4 ordinary token");
        var flowId = HarnessAssert.Required(_flowId, "P4 family");
        var lockedVersionId = HarnessAssert.Required(_lockedFlowVersionId, "P4 locked version");
        var draftVersionId = HarnessAssert.Required(_reopenedFlowVersionId, "P4 draft version");
        switch (number)
        {
            case 1:
            {
                var detail = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}", ownerToken, ct: ct);
                ApiHarnessClient.ExpectStatus(detail, HttpStatusCode.OK, "FLOW-PERM-01 owner detail");
                var body = ApiHarnessClient.RequiredObject(detail.Json, "FLOW-PERM-01 owner body");
                HarnessAssert.True(ApiHarnessClient.RequiredBool(body, "canRead"), "owner canRead");
                HarnessAssert.True(ApiHarnessClient.RequiredBool(body, "canManage"), "owner canManage");
                HarnessAssert.True(!ApiHarnessClient.RequiredBool(body, "executeGrant"), "owner implicit executeGrant");
                HarnessAssert.True(!ApiHarnessClient.RequiredBool(body, "canExecute"), "owner canExecute");
                return P4Observation("owner family/version metadata was read/manage true but execute remained separately denied", "canRead=true;canManage=true;executeGrant=false;canExecute=false;writes=0");
            }
            case 2:
            {
                var detail = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}", adminToken, ct: ct);
                ApiHarnessClient.ExpectStatus(detail, HttpStatusCode.OK, "FLOW-PERM-02 admin detail");
                var body = ApiHarnessClient.RequiredObject(detail.Json, "FLOW-PERM-02 admin body");
                HarnessAssert.True(ApiHarnessClient.RequiredBool(body, "canRead"), "admin canRead");
                HarnessAssert.True(ApiHarnessClient.RequiredBool(body, "canManage"), "admin canManage");
                HarnessAssert.True(!ApiHarnessClient.RequiredBool(body, "executeGrant"), "admin implicit executeGrant");
                HarnessAssert.True(!ApiHarnessClient.RequiredBool(body, "canExecute"), "admin canExecute");
                return P4Observation("system admin could manage definition metadata but gained no implicit execute grant", "canRead=true;canManage=true;executeGrant=false;canExecute=false;writes=0");
            }
            case 3:
            {
                var detail = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}/versions/{lockedVersionId}", participantToken, ct: ct);
                ApiHarnessClient.ExpectStatus(detail, HttpStatusCode.OK, "FLOW-PERM-03 exact participant version");
                var body = ApiHarnessClient.RequiredObject(detail.Json, "FLOW-PERM-03 body");
                HarnessAssert.True(ApiHarnessClient.RequiredBool(body, "canRead"), "participant canRead");
                HarnessAssert.True(!ApiHarnessClient.RequiredBool(body, "canManage"), "participant canManage");
                HarnessAssert.True(ApiHarnessClient.RequiredBool(body, "executeGrant"), "participant executeGrant");
                HarnessAssert.True(!ApiHarnessClient.RequiredBool(body, "canExecute"), "participant canExecute");
                return P4Observation("exact locked participant received read and business grant metadata without manage/runtime permission", "canRead=true;canManage=false;executeGrant=true;canExecute=false;draftLeak=false");
            }
            case 4:
            {
                var denied = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}/versions/{draftVersionId}", participantToken, ct: ct);
                ApiHarnessClient.ExpectStatus(denied, HttpStatusCode.Forbidden, "FLOW-PERM-04 participant draft");
                AssertErrorCode(denied, "AUTH_FORBIDDEN", "FLOW-PERM-04 participant draft");
                return P4Observation("participant draft/newer-version deep link returned generic 403 with no snapshot", "http=403;errorCode=AUTH_FORBIDDEN;payloadLeak=false;writes=0");
            }
            case 5:
            {
                var search = await _api.PostAsync("api/dynamic-flow-templates/search", new { query = FlowCode, page = 0, pageSize = 20 }, ordinaryToken, ct: ct);
                ApiHarnessClient.ExpectStatus(search, HttpStatusCode.OK, "FLOW-PERM-05 outsider search");
                var rows = ApiHarnessClient.RequiredArray(ApiHarnessClient.RequiredObject(search.Json, "FLOW-PERM-05 body")["rows"], "FLOW-PERM-05 rows");
                HarnessAssert.True(rows.OfType<JsonObject>().All(row => row["id"]?.GetValue<string>() != flowId), "outsider search leaked family");
                return P4Observation("unrelated search hid family and scoped total before paging", "visible=false;total=0;writes=0");
            }
            case 6:
            {
                var existing = await _api.GetAsync($"api/dynamic-flow-templates/{flowId}", ordinaryToken, ct: ct);
                var missing = await _api.GetAsync($"api/dynamic-flow-templates/{MongoDB.Bson.ObjectId.GenerateNewId()}", ordinaryToken, ct: ct);
                ApiHarnessClient.ExpectStatus(existing, HttpStatusCode.Forbidden, "FLOW-PERM-06 existing");
                ApiHarnessClient.ExpectStatus(missing, HttpStatusCode.Forbidden, "FLOW-PERM-06 missing");
                AssertErrorCode(existing, "AUTH_FORBIDDEN", "FLOW-PERM-06 existing");
                AssertErrorCode(missing, "AUTH_FORBIDDEN", "FLOW-PERM-06 missing");
                HarnessAssert.Equal(
                    ApiHarnessClient.FindStringRecursive(existing.Json, "reason"),
                    ApiHarnessClient.FindStringRecursive(missing.Json, "reason"),
                    "existing/missing generic reason");
                return P4Observation("existing and missing family detail produced the same generic forbidden contract", "existing=403;missing=403;errorCode=AUTH_FORBIDDEN;reasonEqual=true;writes=0");
            }
            case 7:
            {
                var created = await CreateP4DraftFixtureAsync("PERM07_SA", adminToken, "p4-perm-07-create", ct);
                HarnessAssert.True(ApiHarnessClient.RequiredBool(created, "canManage"), "SA created family canManage");
                return P4Observation("system admin create committed family/version/receipt/audit transaction", "http=200;family=1;version=1;receipt=1;audit=1");
            }
            case 8:
            {
                var created = await CreateP4DraftFixtureAsync("PERM08_MANAGER", ownerToken, "p4-perm-08-create", ct);
                HarnessAssert.Equal(_ownerId, ApiHarnessClient.RequiredString(created, "ownerUserId"), "manager owner id");
                return P4Observation("allowlisted flow manager created and owned a new family", "http=200;ownerExact=true;family=1;version=1;receipt=1;audit=1");
            }
            case 9:
            {
                var before = await CaptureP4DefinitionCountsAsync(ct);
                var denied = await _api.PostAsync(
                    "api/dynamic-flow-templates",
                    new JsonObject
                    {
                        ["commandId"] = "p4-perm-09-create",
                        ["code"] = "P4_PERM09_USER",
                        ["name"] = "ordinary user must not create",
                        ["rootDynamicFormTemplateId"] = HarnessAssert.Required(_formId, "FLOW-PERM-09 form"),
                        ["payload"] = BuildFlowPayload(HarnessAssert.Required(_formId, "FLOW-PERM-09 form"))
                    },
                    ordinaryToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(denied, HttpStatusCode.Forbidden, "FLOW-PERM-09 ordinary create");
                AssertErrorCode(denied, "DYNAMIC_FLOW_CREATE_FORBIDDEN", "FLOW-PERM-09 ordinary create");
                HarnessAssert.Equal(before, await CaptureP4DefinitionCountsAsync(ct), "ordinary create wrote state");
                return P4Observation("authenticated ordinary user create failed before definition writes", "http=403;errorCode=DYNAMIC_FLOW_CREATE_FORBIDDEN;writes=0");
            }
            case 10:
                return await VerifyP4ForbiddenMutationMatrixAsync(participantToken, flowId, lockedVersionId, draftVersionId, ct);
            case 11:
            {
                var denied = await _api.PostAsync(
                    $"api/dynamic-flow-templates/{flowId}/versions/diff",
                    new { fromVersionId = lockedVersionId, toVersionId = draftVersionId },
                    participantToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(denied, HttpStatusCode.Forbidden, "FLOW-PERM-11 partial-scope diff");
                AssertErrorCode(denied, "AUTH_FORBIDDEN", "FLOW-PERM-11 partial-scope diff");
                return P4Observation("diff failed generically before reading the unauthorized draft snapshot", "http=403;errorCode=AUTH_FORBIDDEN;payloadLeak=false;hashLeak=false;writes=0");
            }
            case 12:
                return await VerifyP4BlockedRuntimeMatrixAsync(participantToken, flowId, lockedVersionId, ct);
            default:
                throw new ArgumentOutOfRangeException(nameof(number));
        }
    }

    private async Task<JsonObject> CreateP4DraftFixtureAsync(
        string suffix,
        string token,
        string commandId,
        CancellationToken ct)
    {
        var formId = HarnessAssert.Required(_formId, $"P4 fixture {suffix} form");
        var before = await CaptureP4DefinitionCountsAsync(ct);
        var response = await _api.PostAsync(
            "api/dynamic-flow-templates",
            new JsonObject
            {
                ["commandId"] = commandId,
                ["code"] = $"P4_{suffix}",
                ["name"] = $"P4 {suffix}",
                ["rootDynamicFormTemplateId"] = formId,
                ["payload"] = BuildFlowPayload(formId)
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, $"create P4 fixture {suffix}");
        var after = await CaptureP4DefinitionCountsAsync(ct);
        HarnessAssert.Equal(before.Families + 1, after.Families, $"{suffix} family delta");
        HarnessAssert.Equal(before.Versions + 1, after.Versions, $"{suffix} version delta");
        HarnessAssert.Equal(before.Receipts + 1, after.Receipts, $"{suffix} receipt delta");
        HarnessAssert.Equal(before.Audits + 1, after.Audits, $"{suffix} audit delta");
        return ApiHarnessClient.RequiredObject(response.Json, $"P4 fixture {suffix}");
    }

    private async Task<(JsonObject Family, JsonObject Version)> CreateAndLockP4FixtureAsync(
        string suffix,
        string token,
        CancellationToken ct)
    {
        var family = await CreateP4DraftFixtureAsync(suffix, token, $"p4-{suffix.ToLowerInvariant()}-create", ct);
        var familyId = ApiHarnessClient.RequiredString(family, "id");
        var draft = ApiHarnessClient.RequiredObject(family["draftVersion"], $"{suffix} draft");
        var before = await CaptureP4DefinitionCountsAsync(ct);
        var locked = await _api.PostAsync(
            $"api/dynamic-flow-templates/{familyId}/versions/{ApiHarnessClient.RequiredString(draft, "id")}/lock",
            new
            {
                commandId = $"p4-{suffix.ToLowerInvariant()}-lock",
                expectedFamilyRevision = ApiHarnessClient.RequiredInt(family, "familyRevision"),
                expectedDraftRevision = ApiHarnessClient.RequiredInt(draft, "draftRevision"),
                expectedPayloadHash = ApiHarnessClient.RequiredString(draft, "payloadHash")
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(locked, HttpStatusCode.OK, $"lock P4 fixture {suffix}");
        var after = await CaptureP4DefinitionCountsAsync(ct);
        HarnessAssert.Equal(before.Families, after.Families, $"{suffix} lock family cardinality");
        HarnessAssert.Equal(before.Versions, after.Versions, $"{suffix} lock version cardinality");
        HarnessAssert.Equal(before.Receipts + 1, after.Receipts, $"{suffix} lock receipt delta");
        HarnessAssert.Equal(before.Audits + 1, after.Audits, $"{suffix} lock audit delta");
        var detail = await _api.GetAsync($"api/dynamic-flow-templates/{familyId}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(detail, HttpStatusCode.OK, $"get locked P4 fixture {suffix}");
        return (
            ApiHarnessClient.RequiredObject(detail.Json, $"locked family {suffix}"),
            ApiHarnessClient.RequiredObject(locked.Json, $"locked version {suffix}"));
    }

    private async Task<CaseObservation> VerifyP4DeleteMatrixAsync(string token, CancellationToken ct)
    {
        var family = await CreateP4DraftFixtureAsync("CRUD05_DELETE", token, "p4-crud-05-create", ct);
        var familyId = ApiHarnessClient.RequiredString(family, "id");
        var before = await CaptureP4DefinitionCountsAsync(ct);
        var response = await _api.SendAsync(
            HttpMethod.Delete,
            $"api/dynamic-flow-templates/{familyId}",
            new { commandId = "p4-crud-05-delete", expectedFamilyRevision = ApiHarnessClient.RequiredInt(family, "familyRevision") },
            token,
            headers: null,
            ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.NoContent, "FLOW-CRUD-05 eligible delete");
        var persistedFamily = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates").Find(x => x.Id == familyId).SingleAsync(ct);
        var persistedVersions = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions").Find(x => x.TemplateId == familyId).ToListAsync(ct);
        HarnessAssert.True(persistedFamily.IsDeleted, "FLOW-CRUD-05 family soft flag");
        HarnessAssert.True(persistedVersions.Count == 1 && persistedVersions.All(version => version.IsDeleted), "FLOW-CRUD-05 version soft flags");
        var after = await CaptureP4DefinitionCountsAsync(ct);
        HarnessAssert.Equal(before.Receipts + 1, after.Receipts, "FLOW-CRUD-05 delete receipt");
        HarnessAssert.Equal(before.Audits + 1, after.Audits, "FLOW-CRUD-05 delete audit");

        var lockedFamily = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == HarnessAssert.Required(_flowId, "FLOW-CRUD-05 locked control"))
            .SingleAsync(ct);
        var controlBefore = await CaptureP4DefinitionCountsAsync(ct);
        var denied = await _api.SendAsync(
            HttpMethod.Delete,
            $"api/dynamic-flow-templates/{lockedFamily.Id}",
            new { commandId = "p4-crud-05-locked-control", expectedFamilyRevision = lockedFamily.FamilyRevision },
            token,
            headers: null,
            ct);
        ApiHarnessClient.ExpectStatus(denied, HttpStatusCode.Conflict, "FLOW-CRUD-05 locked control");
        AssertErrorCode(denied, "DYNAMIC_FLOW_DELETE_NOT_ALLOWED", "FLOW-CRUD-05 locked control");
        HarnessAssert.Equal(controlBefore, await CaptureP4DefinitionCountsAsync(ct), "locked delete control wrote state");
        return P4Observation("all-draft unreferenced family soft-deleted atomically while locked control stayed byte-stable", "eligible=204;softFamily=true;softVersions=1;lockedControl=409/DYNAMIC_FLOW_DELETE_NOT_ALLOWED;controlWrites=0");
    }

    private async Task<CaseObservation> VerifyP4ArchiveMatrixAsync(string token, CancellationToken ct)
    {
        var fixture = await CreateAndLockP4FixtureAsync("CRUD06_ARCHIVE", token, ct);
        var familyId = ApiHarnessClient.RequiredString(fixture.Family, "id");
        var versionId = ApiHarnessClient.RequiredString(fixture.Version, "id");
        var versions = _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions");
        var sourceBefore = await versions.Find(x => x.Id == versionId).SingleAsync(ct);
        var archiveRequest = new
        {
            commandId = "p4-crud-06-archive",
            expectedFamilyRevision = ApiHarnessClient.RequiredInt(fixture.Family, "familyRevision")
        };
        var response = await _api.PostAsync(
            $"api/dynamic-flow-templates/{familyId}/archive",
            archiveRequest,
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, "FLOW-CRUD-06 archive");
        HarnessAssert.Equal("ARCHIVED", ApiHarnessClient.RequiredString(response.Json, "status"), "archive status");
        var sourceAfter = await versions.Find(x => x.Id == versionId).SingleAsync(ct);
        HarnessAssert.True(sourceBefore.ToBsonDocument().Equals(sourceAfter.ToBsonDocument()), "archive mutated locked version");
        var committed = await CaptureP4DefinitionCountsAsync(ct);
        var replay = await _api.PostAsync(
            $"api/dynamic-flow-templates/{familyId}/archive",
            archiveRequest,
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "FLOW-CRUD-06 archive exact replay");
        HarnessAssert.Equal(
            ApiHarnessClient.RequiredInt(response.Json, "familyRevision"),
            ApiHarnessClient.RequiredInt(replay.Json, "familyRevision"),
            "archive replay family revision");
        HarnessAssert.Equal(
            committed,
            await CaptureP4DefinitionCountsAsync(ct),
            "archive replay wrote state");
        return P4Observation("archive advanced family lifecycle, preserved the locked version byte-for-byte and replayed from its receipt after archive", "http=200;status=ARCHIVED;familyRevisionDelta=1;lockedVersionMutation=0;exactReplay=200/0W");
    }

    private async Task<CaseObservation> VerifyP4CloneMatrixAsync(string token, CancellationToken ct)
    {
        var sourceId = HarnessAssert.Required(_flowId, "FLOW-CRUD-07 source");
        var source = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates").Find(x => x.Id == sourceId).SingleAsync(ct);
        var sourceVersionId = HarnessAssert.Required(_reopenedFlowVersionId, "FLOW-CRUD-07 mutable source");
        var sourceVersion = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
            .Find(x => x.Id == sourceVersionId && x.TemplateId == sourceId)
            .SingleAsync(ct);
        var before = await CaptureP4DefinitionCountsAsync(ct);
        var stale = await _api.PostAsync(
            $"api/dynamic-flow-templates/{sourceId}/clone",
            new
            {
                commandId = "p4-crud-07-stale-clone",
                expectedFamilyRevision = source.FamilyRevision,
                sourceVersionId,
                sourceDraftRevision = sourceVersion.DraftRevision,
                sourcePayloadHash = new string('0', 64),
                code = "P4_CRUD07_STALE_CLONE",
                name = "P4 CRUD 07 stale clone"
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(stale, HttpStatusCode.Conflict, "FLOW-CRUD-07 stale mutable source");
        AssertErrorCode(stale, "DYNAMIC_FLOW_REVISION_CONFLICT", "FLOW-CRUD-07 stale mutable source");
        HarnessAssert.Equal(before, await CaptureP4DefinitionCountsAsync(ct), "FLOW-CRUD-07 stale clone wrote state");
        var request = new
        {
            commandId = "p4-crud-07-clone",
            expectedFamilyRevision = source.FamilyRevision,
            sourceVersionId,
            sourceDraftRevision = sourceVersion.DraftRevision,
            sourcePayloadHash = sourceVersion.PayloadHash,
            code = "P4_CRUD07_CLONE",
            name = "P4 CRUD 07 clone"
        };
        var response = await _api.PostAsync(
            $"api/dynamic-flow-templates/{sourceId}/clone",
            request,
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.OK, "FLOW-CRUD-07 clone");
        var clone = ApiHarnessClient.RequiredObject(response.Json, "FLOW-CRUD-07 clone body");
        var lineage = ApiHarnessClient.RequiredObject(clone["lineage"], "FLOW-CRUD-07 lineage");
        HarnessAssert.Equal(sourceId, ApiHarnessClient.RequiredString(lineage, "originFamilyId"), "clone origin family");
        HarnessAssert.Equal(sourceVersionId, ApiHarnessClient.RequiredString(lineage, "originVersionId"), "clone origin version");
        HarnessAssert.Equal("DRAFT", ApiHarnessClient.RequiredString(clone, "status"), "clone status");
        var cloneId = ApiHarnessClient.RequiredString(clone, "id");
        var cloneDraft = ApiHarnessClient.RequiredObject(clone["draftVersion"], "FLOW-CRUD-07 clone draft");
        var cloneDraftId = ApiHarnessClient.RequiredString(cloneDraft, "id");
        HarnessAssert.Equal(sourceVersion.PayloadHash, ApiHarnessClient.RequiredString(cloneDraft, "payloadHash"), "clone exact source hash");
        var committed = await CaptureP4DefinitionCountsAsync(ct);
        var replay = await _api.PostAsync(
            $"api/dynamic-flow-templates/{sourceId}/clone",
            request,
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(replay, HttpStatusCode.OK, "FLOW-CRUD-07 exact replay");
        HarnessAssert.Equal(cloneId, ApiHarnessClient.RequiredString(replay.Json, "id"), "clone replay family");
        HarnessAssert.Equal(committed, await CaptureP4DefinitionCountsAsync(ct), "FLOW-CRUD-07 exact replay wrote state");

        var firstUpdateRequest = new
        {
            commandId = "p4-crud-07-update-a",
            expectedFamilyRevision = 1,
            name = "P4 CRUD 07 clone renamed"
        };
        var firstUpdate = await _api.PutAsync(
            $"api/dynamic-flow-templates/{cloneId}",
            firstUpdateRequest,
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(firstUpdate, HttpStatusCode.OK, "FLOW-CRUD-07 first metadata update");
        HarnessAssert.Equal(
            2,
            ApiHarnessClient.RequiredInt(firstUpdate.Json, "familyRevision"),
            "FLOW-CRUD-07 first metadata revision");

        var secondUpdate = await _api.PutAsync(
            $"api/dynamic-flow-templates/{cloneId}",
            new
            {
                commandId = "p4-crud-07-update-b",
                expectedFamilyRevision = 2,
                description = "metadata changed after the first update receipt"
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(secondUpdate, HttpStatusCode.OK, "FLOW-CRUD-07 second metadata update");
        HarnessAssert.Equal(
            3,
            ApiHarnessClient.RequiredInt(secondUpdate.Json, "familyRevision"),
            "FLOW-CRUD-07 second metadata revision");

        var historicalUpdateReplay = await _api.PutAsync(
            $"api/dynamic-flow-templates/{cloneId}",
            firstUpdateRequest,
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            historicalUpdateReplay,
            HttpStatusCode.OK,
            "FLOW-CRUD-07 historical metadata replay");
        HarnessAssert.Equal(
            2,
            ApiHarnessClient.RequiredInt(historicalUpdateReplay.Json, "familyRevision"),
            "FLOW-CRUD-07 historical metadata replay returned current revision");
        HarnessAssert.Equal(
            "P4 CRUD 07 clone renamed",
            ApiHarnessClient.RequiredString(historicalUpdateReplay.Json, "name"),
            "FLOW-CRUD-07 historical metadata replay returned current name");
        var storedAfterUpdates = await _database
            .GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
            .Find(x => x.Id == cloneId && !x.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.Equal(3, storedAfterUpdates.FamilyRevision, "FLOW-CRUD-07 historical update replay mutated Mongo");
        HarnessAssert.Equal(
            "metadata changed after the first update receipt",
            storedAfterUpdates.Description,
            "FLOW-CRUD-07 historical update replay changed current description");

        var historicalCloneReplay = await _api.PostAsync(
            $"api/dynamic-flow-templates/{sourceId}/clone",
            request,
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            historicalCloneReplay,
            HttpStatusCode.OK,
            "FLOW-CRUD-07 clone replay after target mutation");
        HarnessAssert.Equal(
            1,
            ApiHarnessClient.RequiredInt(historicalCloneReplay.Json, "familyRevision"),
            "FLOW-CRUD-07 clone replay returned mutated target revision");
        HarnessAssert.Equal(
            "P4 CRUD 07 clone",
            ApiHarnessClient.RequiredString(historicalCloneReplay.Json, "name"),
            "FLOW-CRUD-07 clone replay returned mutated target metadata");

        foreach (var actor in new[]
                 {
                     (Name: "owner", Token: token),
                     (Name: "admin", Token: HarnessAssert.Required(_adminToken, "FLOW-CRUD-07 admin token"))
                 })
        {
            var search = await _api.PostAsync(
                "api/dynamic-flow-templates/search",
                new { query = "P4_CRUD07_CLONE", page = 0, pageSize = 1 },
                actor.Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(search, HttpStatusCode.OK, $"FLOW-CRUD-07 {actor.Name} clone search");
            var searchRows = ApiHarnessClient.RequiredArray(
                ApiHarnessClient.RequiredObject(search.Json, $"FLOW-CRUD-07 {actor.Name} search")["rows"],
                $"FLOW-CRUD-07 {actor.Name} search rows");
            HarnessAssert.Equal(1, searchRows.Count, $"FLOW-CRUD-07 {actor.Name} search row count");
            var searchRow = ApiHarnessClient.RequiredObject(searchRows[0], $"FLOW-CRUD-07 {actor.Name} search row");
            HarnessAssert.Equal(cloneId, ApiHarnessClient.RequiredString(searchRow, "id"), $"FLOW-CRUD-07 {actor.Name} clone id");
            HarnessAssert.True(ApiHarnessClient.RequiredBool(searchRow, "canRead"), $"FLOW-CRUD-07 {actor.Name} search canRead");
            HarnessAssert.True(ApiHarnessClient.RequiredBool(searchRow, "canManage"), $"FLOW-CRUD-07 {actor.Name} search canManage");
            HarnessAssert.True(!ApiHarnessClient.RequiredBool(searchRow, "hasLockedVersion"), $"FLOW-CRUD-07 {actor.Name} draft-only locked flag");
            HarnessAssert.True(searchRow["currentVersionId"] is null, $"FLOW-CRUD-07 {actor.Name} draft-only currentVersionId");
            HarnessAssert.True(searchRow["currentVersionNo"] is null, $"FLOW-CRUD-07 {actor.Name} draft-only currentVersionNo");
            HarnessAssert.True(searchRow["currentVersionHash"] is null, $"FLOW-CRUD-07 {actor.Name} draft-only currentVersionHash");
            HarnessAssert.True(searchRow["currentVersion"] is null, $"FLOW-CRUD-07 {actor.Name} draft-only currentVersion summary");
            var searchDraft = ApiHarnessClient.RequiredObject(searchRow["draftVersion"], $"FLOW-CRUD-07 {actor.Name} search draft");
            HarnessAssert.Equal(cloneDraftId, ApiHarnessClient.RequiredString(searchDraft, "id"), $"FLOW-CRUD-07 {actor.Name} search draft id");
            var searchVersions = ApiHarnessClient.RequiredArray(searchRow["versions"], $"FLOW-CRUD-07 {actor.Name} search versions");
            HarnessAssert.Equal(1, searchVersions.Count, $"FLOW-CRUD-07 {actor.Name} search version count");
            HarnessAssert.Equal("DRAFT", ApiHarnessClient.RequiredString(ApiHarnessClient.RequiredObject(searchVersions[0], $"FLOW-CRUD-07 {actor.Name} version"), "status"), $"FLOW-CRUD-07 {actor.Name} search version status");
        }

        var participantSearch = await _api.PostAsync(
            "api/dynamic-flow-templates/search",
            new { query = "P4_CRUD07_CLONE", page = 0, pageSize = 1 },
            HarnessAssert.Required(_outsiderToken, "FLOW-CRUD-07 participant token"),
            ct: ct);
        ApiHarnessClient.ExpectStatus(participantSearch, HttpStatusCode.OK, "FLOW-CRUD-07 ungranted participant search");
        HarnessAssert.Equal(
            0,
            ApiHarnessClient.RequiredArray(
                ApiHarnessClient.RequiredObject(participantSearch.Json, "FLOW-CRUD-07 participant search")["rows"],
                "FLOW-CRUD-07 participant search rows").Count,
            "FLOW-CRUD-07 ungranted participant saw draft clone");
        var after = await CaptureP4DefinitionCountsAsync(ct);
        HarnessAssert.Equal(before.Families + 1, after.Families, "clone family delta");
        HarnessAssert.Equal(before.Versions + 1, after.Versions, "clone version delta");
        HarnessAssert.Equal(before.Receipts + 3, after.Receipts, "clone plus metadata receipt delta");
        HarnessAssert.Equal(before.Audits + 3, after.Audits, "clone plus metadata audit delta");
        return P4Observation(
            "clone pinned the exact mutable draft revision/hash, rejected a stale pin with zero writes, and both clone/update exact replays returned their original receipt snapshots after later target mutations",
            "stale=409/0W;sourceVersionPinned=true;sourceDraftRevisionPinned=true;sourcePayloadHashPinned=true;cloneHistoricalReplay=revision1/0W;updateHistoricalReplay=revision2/0W;familyDelta=1;versionDelta=1;receiptDelta=3;auditDelta=3;participantLeak=false");
    }

    private async Task<CaseObservation> VerifyP4ForbiddenMutationMatrixAsync(
        string token,
        string familyId,
        string lockedVersionId,
        string draftVersionId,
        CancellationToken ct)
    {
        var family = await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates").Find(x => x.Id == familyId).SingleAsync(ct);
        var draft = await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions").Find(x => x.Id == draftVersionId).SingleAsync(ct);
        var before = await CaptureP4DefinitionCountsAsync(ct);
        var commands = new (HttpMethod Method, string Path, object Body, string Name)[]
        {
            (HttpMethod.Put, $"api/dynamic-flow-templates/{familyId}", new { commandId = "p4-perm10-update", expectedFamilyRevision = family.FamilyRevision, name = "forbidden" }, "update"),
            (HttpMethod.Put, $"api/dynamic-flow-templates/{familyId}/versions/{draftVersionId}/draft", new { commandId = "p4-perm10-save", expectedDraftRevision = draft.DraftRevision, expectedPayloadHash = draft.PayloadHash, payloadJson = draft.PayloadJson }, "save"),
            (HttpMethod.Post, $"api/dynamic-flow-templates/{familyId}/versions/{draftVersionId}/lock", new { commandId = "p4-perm10-lock", expectedFamilyRevision = family.FamilyRevision, expectedDraftRevision = draft.DraftRevision, expectedPayloadHash = draft.PayloadHash }, "lock"),
            (HttpMethod.Post, $"api/dynamic-flow-templates/{familyId}/versions/{lockedVersionId}/reopen", new { commandId = "p4-perm10-reopen", expectedFamilyRevision = family.FamilyRevision }, "reopen"),
            (HttpMethod.Post, $"api/dynamic-flow-templates/{familyId}/archive", new { commandId = "p4-perm10-archive", expectedFamilyRevision = family.FamilyRevision }, "archive"),
            (HttpMethod.Post, $"api/dynamic-flow-templates/{familyId}/clone", new { commandId = "p4-perm10-clone", expectedFamilyRevision = family.FamilyRevision, code = "P4_PERM10_FORBIDDEN_CLONE" }, "clone"),
            (HttpMethod.Delete, $"api/dynamic-flow-templates/{familyId}", new { commandId = "p4-perm10-delete", expectedFamilyRevision = family.FamilyRevision }, "delete")
        };
        foreach (var command in commands)
        {
            var response = await _api.SendAsync(command.Method, command.Path, command.Body, token, headers: null, ct);
            ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Forbidden, $"FLOW-PERM-10 {command.Name}");
            AssertErrorCode(response, "AUTH_FORBIDDEN", $"FLOW-PERM-10 {command.Name}");
        }
        HarnessAssert.Equal(before, await CaptureP4DefinitionCountsAsync(ct), "FLOW-PERM-10 forbidden mutations wrote state");
        return P4Observation("participant update/save/lock/reopen/archive/clone/delete all failed generically before mutation validation", "mutations=7;http=403;errorCode=AUTH_FORBIDDEN;receiptWrites=0;auditWrites=0;aggregateWrites=0");
    }

    private async Task<CaseObservation> VerifyP4BlockedRuntimeMatrixAsync(
        string token,
        string familyId,
        string lockedVersionId,
        CancellationToken ct)
    {
        var detail = await _api.GetAsync($"api/dynamic-flow-templates/{familyId}/versions/{lockedVersionId}", token, ct: ct);
        ApiHarnessClient.ExpectStatus(detail, HttpStatusCode.OK, "FLOW-PERM-12 metadata");
        var metadata = ApiHarnessClient.RequiredObject(detail.Json, "FLOW-PERM-12 metadata body");
        HarnessAssert.True(ApiHarnessClient.RequiredBool(metadata, "executeGrant"), "FLOW-PERM-12 business grant");
        HarnessAssert.True(!ApiHarnessClient.RequiredBool(metadata, "canExecute"), "FLOW-PERM-12 canExecute");
        HarnessAssert.Equal("BLOCKED_UNTIL_TARGET_PHASE", ApiHarnessClient.RequiredString(metadata, "executionEligibility"), "FLOW-PERM-12 eligibility");
        HarnessAssert.Equal("P5", ApiHarnessClient.RequiredString(metadata, "blockedUntilPhase"), "FLOW-PERM-12 phase");

        var before = await CaptureP4RuntimeSnapshotAsync(ct);
        var response = await _api.PostAsync(
            $"api/works/{_workId}/dynamic-flows/instances",
            new
            {
                flowTemplateVersionId = lockedVersionId,
                stepId = "step_root",
                targetUnitIds = new[] { _unitId },
                name = "P4 runtime must remain blocked",
                assignmentType = "ONCE",
                aggregationType = "MATRIX",
                dueAtUtc = "2026-08-01T10:00:00Z",
                leaderWatcherUserIds = Array.Empty<string>(),
                isActive = true
            },
            token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(response, HttpStatusCode.Conflict, "FLOW-PERM-12 runtime launch");
        AssertErrorCode(response, "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE", "FLOW-PERM-12 runtime launch");
        HarnessAssert.Equal("P5", ApiHarnessClient.FindStringRecursive(response.Json, "blockedUntilPhase"), "FLOW-PERM-12 runtime phase");
        HarnessAssert.Equal(before, await CaptureP4RuntimeSnapshotAsync(ct), "FLOW-PERM-12 runtime blocker changed raw business state");
        return P4Observation("participant retained executeGrant metadata but current launch returned stable P5 blocker before every business writer; counts and raw Mongo document hashes stayed byte-identical", "executeGrant=true;canExecute=false;http=409;errorCode=DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE;phase=P5;businessWrites=0;rawCollectionHashes=4");
    }

    private async Task<P4DefinitionCounts> CaptureP4DefinitionCountsAsync(CancellationToken ct)
        => new(
            await _database.GetCollection<DynamicFlowTemplate>("dynamic_flow_templates")
                .CountDocumentsAsync(FilterDefinition<DynamicFlowTemplate>.Empty, cancellationToken: ct),
            await _database.GetCollection<DynamicFlowTemplateVersion>("dynamic_flow_template_versions")
                .CountDocumentsAsync(FilterDefinition<DynamicFlowTemplateVersion>.Empty, cancellationToken: ct),
            await _database.GetCollection<DynamicFlowDefinitionCommandReceipt>("dynamic_flow_definition_command_receipts")
                .CountDocumentsAsync(FilterDefinition<DynamicFlowDefinitionCommandReceipt>.Empty, cancellationToken: ct),
            await _database.GetCollection<UserActionLog>("user_action_logs")
                .CountDocumentsAsync(FilterDefinition<UserActionLog>.Empty, cancellationToken: ct));

    private async Task<P4RuntimeSnapshot> CaptureP4RuntimeSnapshotAsync(CancellationToken ct)
    {
        var assignments = await CaptureP4RawCollectionSnapshotAsync("work_assignments", ct);
        var reports = await CaptureP4RawCollectionSnapshotAsync("work_assignment_report", ct);
        var events = await CaptureP4RawCollectionSnapshotAsync("dynamic_flow_events", ct);
        var audits = await CaptureP4RawCollectionSnapshotAsync("user_action_logs", ct);
        return new P4RuntimeSnapshot(assignments, reports, events, audits);
    }

    private async Task<P4RawCollectionSnapshot> CaptureP4RawCollectionSnapshotAsync(
        string collectionName,
        CancellationToken ct)
    {
        var documents = await _database.GetCollection<BsonDocument>(collectionName)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(new BsonDocument("_id", 1))
            .ToListAsync(ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var document in documents)
            hash.AppendData(document.ToBson());
        return new P4RawCollectionSnapshot(
            documents.Count,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static CaseObservation P4Observation(string actual, string oracle)
        => new(actual, oracle);

    private sealed record P4RaceFixture(
        string Marker,
        string FamilyId,
        string VersionId,
        DynamicFlowTemplateVersion Source,
        IMongoCollection<DynamicFlowTemplate> Families,
        IMongoCollection<DynamicFlowTemplateVersion> Versions,
        IMongoCollection<DynamicFlowDefinitionCommandReceipt> Receipts,
        IMongoCollection<UserActionLog> Audits);

    private sealed record P4DefinitionCounts(long Families, long Versions, long Receipts, long Audits);
    private sealed record P4RawCollectionSnapshot(long Count, string Sha256);
    private sealed record P4RuntimeSnapshot(
        P4RawCollectionSnapshot Assignments,
        P4RawCollectionSnapshot Reports,
        P4RawCollectionSnapshot Events,
        P4RawCollectionSnapshot Audits);
}
