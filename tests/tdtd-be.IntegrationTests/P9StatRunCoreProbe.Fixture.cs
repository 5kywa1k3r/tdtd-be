using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task BootstrapAndSeedFixtureAsync(CancellationToken ct)
    {
        var api = RequireApi();
        var bootstrap = await api.PostAsync(
            "api/system/bootstrap",
            new { },
            headers: new Dictionary<string, string>
            {
                ["X-System-Bootstrap-Key"] = RequireBackend().BootstrapKey
            },
            ct: ct);
        ApiHarnessClient.ExpectStatus(bootstrap, HttpStatusCode.OK, "P9 system bootstrap");
        _bootstrapPassword = ApiHarnessClient.RequiredString(
            bootstrap.Json,
            "defaultPassword");
        RememberSecret(_bootstrapPassword);
        var adminToken = await api.LoginAsync("admin", _bootstrapPassword, ct);
        RememberSecret(adminToken);

        var database = RequireDatabase();
        var users = database.GetCollection<AppUser>("users");
        var units = database.GetCollection<Unit>("units");
        var admin = await users.Find(user =>
                user.Username == "admin" && !user.IsDeleted)
            .SingleAsync(ct);
        var root = await units.Find(unit =>
                unit.Id == admin.UnitId && !unit.IsDeleted)
            .SingleAsync(ct);

        var fixedAt = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);
        var unitAId = ObjectId.GenerateNewId().ToString();
        var unitBId = ObjectId.GenerateNewId().ToString();
        await units.InsertManyAsync(
            new[]
            {
                NewUnit(unitAId, "P9 Unit A", "190901", root.Level + 1, root.Id, admin.Id, fixedAt),
                NewUnit(unitBId, "P9 Unit B", "190902", root.Level + 1, root.Id, admin.Id, fixedAt)
            },
            cancellationToken: ct);

        var actorSeeds = new[]
        {
            NewActor("executor", "p9_executor", "P9 Executor", unitAId,
                ["MANAGER_LEVEL"], admin.Id, fixedAt),
            NewActor("executor2", "p9_executor2", "P9 Executor Two", unitAId,
                ["MANAGER_LEVEL"], admin.Id, fixedAt),
            NewActor("insufficient", "p9_insufficient", "P9 Insufficient", unitAId,
                [], admin.Id, fixedAt),
            NewActor("outsider", "p9_outsider", "P9 Outsider", unitBId,
                ["MANAGER_LEVEL"], admin.Id, fixedAt)
        };
        var hasher = new PasswordHasher<AppUser>();
        foreach (var seed in actorSeeds)
            seed.User.PasswordHash = hasher.HashPassword(
                seed.User,
                RequireBackend().ActorPassword);
        await users.InsertManyAsync(
            actorSeeds.Select(seed => seed.User),
            cancellationToken: ct);

        _actors["admin"] = new P9Actor(
            "admin",
            admin.Id,
            admin.Username,
            admin.UnitId ?? string.Empty,
            admin.AccountKind ?? "SYSTEM_ADMIN",
            admin.Roles,
            adminToken);
        foreach (var seed in actorSeeds)
        {
            var token = await api.LoginAsync(
                seed.User.Username,
                RequireBackend().ActorPassword,
                ct);
            RememberSecret(token);
            _actors[seed.Key] = new P9Actor(
                seed.Key,
                seed.User.Id,
                seed.User.Username,
                seed.User.UnitId ?? string.Empty,
                seed.User.AccountKind ?? "NORMAL_USER",
                seed.User.Roles,
                token);
        }

        _fixture = await SeedDomainFixtureAsync(
            admin.Id,
            unitAId,
            unitBId,
            fixedAt,
            ct);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "actor-fixture-matrix.json"),
            new
            {
                schemaVersion = "P9_CORE_FIXTURE_V1",
                chainId = ChainId,
                promptId = PromptId,
                actors = _actors.Values.Select(actor => new
                {
                    actor.Key,
                    actor.Id,
                    actor.Username,
                    actor.UnitId,
                    actor.AccountKind,
                    actor.Roles,
                    authenticated = true
                }),
                fixture = _fixture,
                lifecycleTrace = _lifecycleTrace,
                cleanupHandles = _cleanupHandles,
                autonomous = true,
                sharedSeedDependency = false
            },
            ct);
    }

    private async Task<P9Fixture> SeedDomainFixtureAsync(
        string adminId,
        string unitAId,
        string unitBId,
        DateTime fixedAt,
        CancellationToken ct)
    {
        var executor = Actor("executor");
        var insufficient = Actor("insufficient");
        var workId = ObjectId.GenerateNewId();
        var assignmentId = ObjectId.GenerateNewId();
        var siblingAssignmentId = ObjectId.GenerateNewId();
        var reportId = ObjectId.GenerateNewId();
        var reportPeriodId = ObjectId.GenerateNewId();
        var pairedReportId = ObjectId.GenerateNewId();
        var pairedReportPeriodId = ObjectId.GenerateNewId();
        var templateId = ObjectId.GenerateNewId();
        var configId = ObjectId.GenerateNewId();
        var configVersionId = ObjectId.GenerateNewId();
        var flowFamilyId = ObjectId.GenerateNewId();
        var flowVersionId = ObjectId.GenerateNewId();
        var flowInstanceId = ObjectId.GenerateNewId();
        var flowEpochId = ObjectId.GenerateNewId();
        var flowStepInstanceId = ObjectId.GenerateNewId();
        var flowBranchId = ObjectId.GenerateNewId();
        var sourcePayload = new JsonObject
        {
            ["schemaVersion"] = "P9_SOURCE_PAYLOAD_V1",
            ["fields"] = new JsonObject
            {
                ["amount"] = 1250,
                ["approved"] = true,
                ["label"] = "P9 autonomous source"
            },
            ["tables"] = new JsonArray()
        };
        var pairedSourcePayload = new JsonObject
        {
            ["schemaVersion"] = "P9_SOURCE_PAYLOAD_V1",
            ["fields"] = new JsonObject
            {
                ["amount"] = 1100,
                ["approved"] = true,
                ["label"] = "P9 paired source B"
            },
            ["tables"] = new JsonArray()
        };
        var fieldConfig = new JsonArray
        {
            new JsonObject
            {
                ["fieldCode"] = "amount",
                ["aggregate"] = "SUM"
            },
            new JsonObject
            {
                ["fieldCode"] = "approved",
                ["aggregate"] = "COUNT"
            }
        };
        var tableConfig = new JsonArray();
        var dependencyPins = new[]
        {
            $"catalog-semantic:{CatalogSemanticSha256}",
            $"schema-semantic:{SchemaSemanticSha256}"
        };
        var sourcePayloadJson = sourcePayload.ToJsonString(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var pairedSourcePayloadJson = pairedSourcePayload.ToJsonString(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var fieldSectionJson = fieldConfig.ToJsonString(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var tableSectionJson = tableConfig.ToJsonString(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        const string tableRootJson = "[]";
        const string summarySourceJson = "{}";
        var sourceHash = ComputeWorkReportPayloadHash(
            sourcePayloadJson,
            sourcePayloadJson,
            tableRootJson,
            summarySourceJson);
        var pairedSourceHash = ComputeWorkReportPayloadHash(
            pairedSourcePayloadJson,
            pairedSourcePayloadJson,
            tableRootJson,
            summarySourceJson);
        var configPayload = new
        {
            ownerKind = "DYNAMIC_FORM",
            ownerId = templateId.ToString(),
            fieldConfig,
            tableConfig,
            dependencyPins
        };
        var configPayloadJson = JsonSerializer.Serialize(
            configPayload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var configHash = CanonicalJsonSha256(configPayload);
        var flowPayloadNode = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["statisticProfile"] = new JsonObject(),
            ["nodes"] = new JsonArray()
        };
        var flowPayloadJson = flowPayloadNode.ToJsonString(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var flowPayloadHash = CanonicalJsonSha256(flowPayloadNode);
        var periodStart = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 8, 31, 23, 59, 59, DateTimeKind.Utc);
        const string infrastructureWarmupSectionId = "p9-infrastructure-warmup";
        var sectionsJson = new JsonArray
        {
            new JsonObject
            {
                ["id"] = infrastructureWarmupSectionId,
                ["title"] = "P9 Infrastructure Warmup",
                ["description"] = null,
                ["tagCodes"] = new JsonArray(),
                ["order"] = 0
            }
        }.ToJsonString();
        var publishedSchema = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            1,
            sectionsJson,
            "[]",
            "[]");

        var work = new BsonDocument
        {
            ["_id"] = workId,
            ["autoCode"] = "P9COREWORK",
            ["code"] = "P9-CORE-WORK",
            ["name"] = "P9 Autonomous Core Work",
            ["issuedByUnitId"] = ObjectId.Parse(unitAId),
            ["createdByUserId"] = ObjectId.Parse(executor.Id),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var period = new BsonDocument
        {
            ["_id"] = reportPeriodId,
            ["workId"] = workId,
            ["workAssignmentId"] = assignmentId,
            ["periodKey"] = "2026-08",
            ["periodInstanceKey"] = "MONTH:2026-08",
            ["periodKind"] = "SCHEDULED",
            ["periodStart"] = periodStart,
            ["periodEnd"] = periodEnd,
            ["isActive"] = true,
            ["createdByUserId"] = ObjectId.Parse(executor.Id),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var pairedPeriod = new BsonDocument(period)
        {
            ["_id"] = pairedReportPeriodId,
            ["periodInstanceKey"] = "MONTH:2026-08:B"
        };
        var assignment = new BsonDocument
        {
            ["_id"] = assignmentId,
            ["workId"] = workId,
            ["dynamicFormTemplateId"] = templateId,
            ["dynamicFormTemplateCode"] = "P9_CORE_FORM",
            ["dynamicFormTemplateName"] = "P9 Core Form",
            ["dynamicFormFamilyId"] = templateId,
            ["dynamicFormVersionNo"] = 1,
            ["dynamicFormSchemaHash"] = publishedSchema.Sha256,
            ["workType"] = "REPORT",
            ["assignmentType"] = "USER",
            ["aggregationType"] = "NONE",
            ["assignees"] = new BsonArray
            {
                new BsonDocument("userId", ObjectId.Parse(executor.Id)),
                new BsonDocument(
                    "userId",
                    ObjectId.Parse(Actor("executor2").Id))
            },
            ["isActive"] = true,
            ["rootAssignmentId"] = assignmentId.ToString(),
            ["level"] = 0,
            ["code"] = "P9-CORE-A",
            ["name"] = "P9 Core Assignment",
            ["path"] = assignmentId.ToString(),
            ["issuedByUnitId"] = ObjectId.Parse(unitAId),
            ["targetUnitIds"] = new BsonArray { ObjectId.Parse(unitAId) },
            ["leaderWatcherUserIds"] = new BsonArray
            {
                insufficient.Id,
                Actor("executor2").Id
            },
            ["leaderWatchers"] = new BsonArray(),
            ["flowTemplateId"] = flowFamilyId,
            ["flowTemplateVersionNo"] = 1,
            ["flowInstanceId"] = flowInstanceId,
            ["flowStepId"] = "P9_CORE_STEP",
            ["flowStepCode"] = "P9_CORE_STEP",
            ["flowStepOrder"] = 1,
            ["flowBranchId"] = flowBranchId,
            ["flowAttemptNo"] = 1,
            ["flowExecutionEpoch"] = 1,
            ["flowEffectiveStatus"] = "EFFECTIVE",
            ["reportLifecycleSeriesRevision"] = 0L,
            ["dynamicFlowMaterializationRevision"] = 0L,
            ["createdByUserId"] = ObjectId.Parse(executor.Id),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var siblingAssignment = (BsonDocument)assignment.DeepClone();
        siblingAssignment["_id"] = siblingAssignmentId;
        siblingAssignment["rootAssignmentId"] = siblingAssignmentId.ToString();
        siblingAssignment["code"] = "P9-CORE-SIBLING-A";
        siblingAssignment["name"] = "P9 Core Sibling Assignment";
        siblingAssignment["path"] = siblingAssignmentId.ToString();
        siblingAssignment["flowTemplateId"] = BsonNull.Value;
        siblingAssignment["flowTemplateVersionNo"] = BsonNull.Value;
        siblingAssignment["flowInstanceId"] = BsonNull.Value;
        siblingAssignment["flowStepId"] = BsonNull.Value;
        siblingAssignment["flowStepCode"] = BsonNull.Value;
        siblingAssignment["flowStepOrder"] = BsonNull.Value;
        siblingAssignment["flowBranchId"] = BsonNull.Value;
        siblingAssignment["flowAttemptNo"] = BsonNull.Value;
        siblingAssignment["flowExecutionEpoch"] = BsonNull.Value;
        siblingAssignment["flowEffectiveStatus"] = BsonNull.Value;

        var report = new BsonDocument
        {
            ["_id"] = reportId,
            ["workId"] = workId,
            ["workAssignmentId"] = assignmentId,
            ["workReportPeriodId"] = reportPeriodId,
            ["dynamicFormTemplateId"] = templateId,
            ["dynamicFormTemplateCode"] = "P9_CORE_FORM",
            ["dynamicFormTemplateName"] = "P9 Core Form",
            ["dynamicFormFamilyId"] = templateId,
            ["dynamicFormVersionNo"] = 1,
            ["dynamicFormSchemaHash"] = publishedSchema.Sha256,
            ["assigneeUserId"] = ObjectId.Parse(executor.Id),
            ["periodKey"] = "2026-08",
            ["periodInstanceKey"] = "MONTH:2026-08",
            ["periodKind"] = "SCHEDULED",
            ["periodStart"] = periodStart,
            ["periodEnd"] = periodEnd,
            ["status"] = 0,
            ["payloadRevision"] = 1,
            ["payloadHash"] = sourceHash,
            ["payloadSizeBytes"] =
                Encoding.UTF8.GetByteCount(sourcePayloadJson) * 2 +
                Encoding.UTF8.GetByteCount(tableRootJson) +
                Encoding.UTF8.GetByteCount(summarySourceJson),
            ["lifecycleRevision"] = 1,
            ["isCurrent"] = true,
            ["isActive"] = true,
            ["scheduleSnapshotJson"] = "{}",
            ["dynamicExcelTemplateId"] = BsonNull.Value,
            ["dynamicExcelCode"] = string.Empty,
            ["dynamicExcelName"] = string.Empty,
            ["specJson"] = "{}",
            ["values1DJson"] = sourcePayloadJson,
            ["fieldValuesJson"] = sourcePayloadJson,
            ["tableValuesRootJson"] = tableRootJson,
            ["summarySourceJson"] = summarySourceJson,
            ["p9CanonicalSourcePayloadJson"] = sourcePayloadJson,
            ["createdByUserId"] = ObjectId.Parse(executor.Id),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var pairedReport = new BsonDocument(report)
        {
            ["_id"] = pairedReportId,
            ["workReportPeriodId"] = pairedReportPeriodId,
            ["periodInstanceKey"] = "MONTH:2026-08:B",
            ["status"] = 2,
            ["payloadHash"] = pairedSourceHash,
            ["payloadSizeBytes"] =
                Encoding.UTF8.GetByteCount(pairedSourcePayloadJson) * 2 +
                Encoding.UTF8.GetByteCount(tableRootJson) +
                Encoding.UTF8.GetByteCount(summarySourceJson),
            ["lifecycleRevision"] = 3,
            ["values1DJson"] = pairedSourcePayloadJson,
            ["fieldValuesJson"] = pairedSourcePayloadJson,
            ["p9CanonicalSourcePayloadJson"] = pairedSourcePayloadJson,
            ["submittedAtUtc"] = fixedAt.AddMinutes(1),
            ["approvedAtUtc"] = fixedAt.AddMinutes(2),
            ["approvedByUserId"] = ObjectId.Parse(adminId)
        };

        var template = new BsonDocument
        {
            ["_id"] = templateId,
            ["code"] = "P9_CORE_FORM",
            ["name"] = "P9 Core Form",
            ["tagCodes"] = new BsonArray(),
            ["createdByUsername"] = "admin",
            ["schemaVersion"] = 1,
            ["versionNo"] = 1,
            ["revision"] = 1,
            ["isActive"] = true,
            ["isPublished"] = true,
            ["publishedAtUtc"] = fixedAt,
            ["publishedByUserId"] = ObjectId.Parse(adminId),
            ["familyId"] = templateId,
            ["lineageStatus"] = "ROOT",
            ["sectionsJson"] = sectionsJson,
            ["fieldsJson"] = "[]",
            ["blocksJson"] = "[]",
            ["publishedSchemaSnapshotJson"] = publishedSchema.Json,
            ["publishedSchemaHash"] = publishedSchema.Sha256,
            ["statisticConfigId"] = configId,
            ["statisticConfigVersionId"] = configVersionId,
            ["statisticConfigVersionNo"] = 1,
            ["statisticConfigRevision"] = 1L,
            ["statisticConfigStatus"] = "LOCKED",
            ["statisticConfigHash"] = configHash,
            ["statisticConfigDependencyPins"] =
                new BsonArray(dependencyPins),
            ["statisticConfigSections"] = new BsonDocument
            {
                ["fieldSectionJson"] = fieldSectionJson,
                ["tableSectionJson"] = tableSectionJson
            },
            ["statisticConfigSnapshots"] = new BsonArray
            {
                new BsonDocument
                {
                    ["versionId"] = configVersionId,
                    ["previousVersionId"] = BsonNull.Value,
                    ["versionNo"] = 1,
                    ["revision"] = 1L,
                    ["status"] = "LOCKED",
                    ["configHash"] = configHash,
                    ["dependencyPins"] = new BsonArray(dependencyPins),
                    ["sections"] = new BsonDocument
                    {
                        ["fieldSectionJson"] = fieldSectionJson,
                        ["tableSectionJson"] = tableSectionJson
                    },
                    ["createdAtUtc"] = fixedAt,
                    ["createdByUserId"] = ObjectId.Parse(adminId)
                }
            },
            ["p9CanonicalConfigPayloadJson"] = configPayloadJson,
            ["createdByUserId"] = ObjectId.Parse(adminId),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };

        var flowFamily = new BsonDocument
        {
            ["_id"] = flowFamilyId,
            ["code"] = "P9_CORE_FLOW",
            ["name"] = "P9 Core Flow",
            ["familyRevision"] = 1,
            ["ownerUserId"] = ObjectId.Parse(adminId),
            ["ownerUnitId"] = ObjectId.Parse(unitAId),
            ["rootDynamicFormTemplateId"] = templateId,
            ["status"] = "ACTIVE",
            ["currentVersionId"] = flowVersionId,
            ["currentVersionNo"] = 1,
            ["currentVersionHash"] = flowPayloadHash,
            ["hasLockedVersion"] = true,
            ["createdByUserId"] = ObjectId.Parse(adminId),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var flowVersion = new BsonDocument
        {
            ["_id"] = flowVersionId,
            ["templateId"] = flowFamilyId,
            ["rootDynamicFormTemplateId"] = templateId,
            ["versionNo"] = 1,
            ["status"] = "LOCKED",
            ["draftRevision"] = 1,
            ["schemaVersion"] = 2,
            ["adapterVersion"] = 1,
            ["catalogVersion"] = "1.6",
            ["catalogSemanticHash"] = CatalogSemanticSha256,
            ["payloadJson"] = flowPayloadJson,
            ["payloadHash"] = flowPayloadHash,
            ["definitionLockable"] = true,
            ["executionEligibility"] = "BLOCKED_UNTIL_TARGET_PHASE",
            ["executionBlockedReason"] = "TARGET_PHASE_NOT_IMPLEMENTED",
            ["blockedUntilPhase"] = "P8",
            ["migrationState"] = "CANONICAL",
            ["lockedAtUtc"] = fixedAt,
            ["lockedByUserId"] = ObjectId.Parse(adminId),
            ["createdByUserId"] = ObjectId.Parse(adminId),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var participantSnapshotId = ObjectId.GenerateNewId();
        var flowInstance = new BsonDocument
        {
            ["_id"] = flowInstanceId,
            ["workId"] = workId,
            ["workType"] = "REPORT",
            ["flowTemplateId"] = flowFamilyId,
            ["flowTemplateVersionId"] = flowVersionId,
            ["flowTemplateVersionNo"] = 1,
            ["flowPayloadHash"] = flowPayloadHash,
            ["catalogVersion"] = "1.6",
            ["catalogSemanticHash"] = CatalogSemanticSha256,
            ["archetypeId"] = "P9_CORE",
            ["definitionRevision"] = flowPayloadHash,
            ["topologySnapshotJson"] = flowPayloadJson,
            ["topologySnapshotHash"] = flowPayloadHash,
            ["executionEpoch"] = 1,
            ["entryFlowStepId"] = "P9_CORE_STEP",
            ["rootInstanceId"] = flowInstanceId,
            ["ancestryPath"] = new BsonArray(),
            ["ancestryFlowFamilyIds"] = new BsonArray(),
            ["resultOwnerUserId"] = ObjectId.Parse(executor.Id),
            ["resultOwnerUnitId"] = ObjectId.Parse(unitAId),
            ["statisticOwnerIdentity"] = $"FLOW:{flowInstanceId}",
            ["periodKey"] = "2026-08",
            ["scheduleIdentityHash"] = flowPayloadHash,
            ["scheduleIdentityJson"] = "{}",
            ["participantSnapshotId"] = participantSnapshotId,
            ["participantSnapshotHash"] = flowPayloadHash,
            ["issuerUserId"] = ObjectId.Parse(adminId),
            ["issuerUnitId"] = ObjectId.Parse(unitAId),
            ["launchCommandId"] = "p9-core-flow-launch",
            ["state"] = "ACTIVE",
            ["revision"] = 1L,
            ["runtimeMaterializationFenceRevision"] = 1L,
            ["runtimeRecoveryEpoch"] = 0L,
            ["nextEventSequence"] = 1L,
            ["createdByUserId"] = ObjectId.Parse(adminId),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var flowEpoch = new BsonDocument
        {
            ["_id"] = flowEpochId,
            ["flowInstanceId"] = flowInstanceId,
            ["executionEpoch"] = 1,
            ["state"] = "ACTIVE",
            ["checkpointNodeId"] = "P9_CORE_STEP",
            ["isCanonical"] = true,
            ["openedByCommandId"] = "p9-core-flow-launch",
            ["openedAtUtc"] = fixedAt,
            ["revision"] = 1L,
            ["createdByUserId"] = ObjectId.Parse(adminId),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var flowStep = new BsonDocument
        {
            ["_id"] = flowStepInstanceId,
            ["flowInstanceId"] = flowInstanceId,
            ["flowStepId"] = "P9_CORE_STEP",
            ["flowStepCode"] = "P9_CORE_STEP",
            ["executionEpoch"] = 1,
            ["definitionRevision"] = flowPayloadHash,
            ["stepOrder"] = 1,
            ["formNodeId"] = "P9_CORE_FORM_NODE",
            ["formFamilyId"] = templateId,
            ["formVersionId"] = templateId,
            ["formVersionNo"] = 1,
            ["formSchemaHash"] = publishedSchema.Sha256,
            ["formSnapshotHash"] = publishedSchema.Sha256,
            ["targetUnitId"] = ObjectId.Parse(unitAId),
            ["participantUserIds"] = new BsonArray
            {
                ObjectId.Parse(executor.Id),
                ObjectId.Parse(Actor("executor2").Id)
            },
            ["participantSnapshotId"] = participantSnapshotId,
            ["attemptNo"] = 1,
            ["reviewCycleNo"] = 1,
            ["branchId"] = flowBranchId,
            ["nextNodeIds"] = new BsonArray(),
            ["isTerminalNode"] = true,
            ["isCanonicalEpoch"] = true,
            ["invalidatedByFlowEventId"] = BsonNull.Value,
            ["resultOwnerIdentity"] = $"FLOW:{flowInstanceId}:P9_CORE_STEP",
            ["statisticOwnerIdentity"] = $"FLOW:{flowInstanceId}",
            ["assignmentId"] = assignmentId,
            ["reportId"] = reportId,
            ["reportLifecycleRevision"] = 3,
            ["reportLifecycleStatus"] = "APPROVED",
            ["reportLifecycleIsActive"] = true,
            ["state"] = "APPROVED",
            ["revision"] = 1L,
            ["createdByUserId"] = ObjectId.Parse(adminId),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };

        var database = RequireDatabase();
        await database.GetCollection<BsonDocument>("works")
            .InsertOneAsync(work, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("work_report_periods")
            .InsertManyAsync([period, pairedPeriod], cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_templates")
            .InsertOneAsync(flowFamily, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_template_versions")
            .InsertOneAsync(flowVersion, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_instances")
            .InsertOneAsync(flowInstance, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_execution_epochs")
            .InsertOneAsync(flowEpoch, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_step_instances")
            .InsertOneAsync(flowStep, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("work_assignments")
            .InsertManyAsync(
                [assignment, siblingAssignment],
                cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_form_templates")
            .InsertOneAsync(template, cancellationToken: ct);
        var reports = database.GetCollection<BsonDocument>(
            "work_assignment_report");
        await reports.InsertManyAsync(
            [report, pairedReport],
            cancellationToken: ct);
        _lifecycleTrace.Add(new P9LifecycleTraceEntry(
            "DRAFT",
            0,
            1,
            sourceHash,
            HashBytes(report.ToBson()),
            fixedAt));

        var submittedAt = fixedAt.AddMinutes(1);
        await reports.UpdateOneAsync(
            new BsonDocument("_id", reportId),
            Builders<BsonDocument>.Update
                .Set("status", 1)
                .Set("lifecycleRevision", 2)
                .Set("submittedAtUtc", submittedAt)
                .Set("updatedAtUtc", submittedAt),
            cancellationToken: ct);
        var submitted = await reports
            .Find(new BsonDocument("_id", reportId))
            .SingleAsync(ct);
        _lifecycleTrace.Add(new P9LifecycleTraceEntry(
            "SUBMITTED",
            1,
            2,
            sourceHash,
            HashBytes(submitted.ToBson()),
            submittedAt));

        var approvedAt = fixedAt.AddMinutes(2);
        await reports.UpdateOneAsync(
            new BsonDocument("_id", reportId),
            Builders<BsonDocument>.Update
                .Set("status", 2)
                .Set("lifecycleRevision", 3)
                .Set("approvedAtUtc", approvedAt)
                .Set("approvedByUserId", ObjectId.Parse(adminId))
                .Set("updatedAtUtc", approvedAt),
            cancellationToken: ct);
        var approved = await reports
            .Find(new BsonDocument("_id", reportId))
            .SingleAsync(ct);
        _lifecycleTrace.Add(new P9LifecycleTraceEntry(
            "APPROVED",
            2,
            3,
            sourceHash,
            HashBytes(approved.ToBson()),
            approvedAt));

        _cleanupHandles.AddRange(
        [
            new P9CleanupHandle("works", workId.ToString()),
            new P9CleanupHandle(
                "work_report_periods",
                reportPeriodId.ToString()),
            new P9CleanupHandle(
                "work_report_periods",
                pairedReportPeriodId.ToString()),
            new P9CleanupHandle(
                "work_assignments",
                assignmentId.ToString()),
            new P9CleanupHandle(
                "work_assignments",
                siblingAssignmentId.ToString()),
            new P9CleanupHandle(
                "work_assignment_report",
                reportId.ToString()),
            new P9CleanupHandle(
                "work_assignment_report",
                pairedReportId.ToString()),
            new P9CleanupHandle(
                "dynamic_form_templates",
                templateId.ToString()),
            new P9CleanupHandle(
                "dynamic_flow_templates",
                flowFamilyId.ToString()),
            new P9CleanupHandle(
                "dynamic_flow_template_versions",
                flowVersionId.ToString()),
            new P9CleanupHandle(
                "dynamic_flow_instances",
                flowInstanceId.ToString()),
            new P9CleanupHandle(
                "dynamic_flow_execution_epochs",
                flowEpochId.ToString()),
            new P9CleanupHandle(
                "dynamic_flow_step_instances",
                flowStepInstanceId.ToString())
        ]);

        return new P9Fixture(
            workId.ToString(),
            reportPeriodId.ToString(),
            pairedReportPeriodId.ToString(),
            assignmentId.ToString(),
            siblingAssignmentId.ToString(),
            reportId.ToString(),
            pairedReportId.ToString(),
            templateId.ToString(),
            configId.ToString(),
            configVersionId.ToString(),
            1,
            unitAId,
            unitBId,
            1,
            configHash,
            1,
            sourceHash,
            pairedSourceHash,
            3,
            flowFamilyId.ToString(),
            flowVersionId.ToString(),
            flowInstanceId.ToString(),
            flowEpochId.ToString(),
            flowStepInstanceId.ToString(),
            flowPayloadHash,
            1,
            "2026-08",
            "MONTH:2026-08",
            "SCHEDULED",
            periodStart,
            periodEnd,
            configPayloadJson,
            sourcePayloadJson);
    }

    private static Unit NewUnit(
        string id,
        string name,
        string code,
        int level,
        string parentId,
        string adminId,
        DateTime fixedAt)
        => new()
        {
            Id = id,
            FullName = name,
            ShortName = name,
            Symbol = code,
            Code = code,
            Level = level,
            Version = 1,
            UnitTypeCodes = [],
            ParentUnitId = parentId,
            CreatedByUserId = adminId,
            UpdatedByUserId = adminId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };

    private static P9ActorSeed NewActor(
        string key,
        string username,
        string fullName,
        string unitId,
        IReadOnlyCollection<string> roles,
        string adminId,
        DateTime fixedAt)
        => new(
            key,
            new AppUser
            {
                Id = ObjectId.GenerateNewId().ToString(),
                Username = username,
                FullName = fullName,
                UnitId = unitId,
                AccountKind = "NORMAL_USER",
                Roles = roles.ToList(),
                CreatedByUserId = adminId,
                UpdatedByUserId = adminId,
                CreatedAtUtc = fixedAt,
                UpdatedAtUtc = fixedAt,
                IsDeleted = false
            });

    private void RememberSecret(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length >= 8)
            _artifactSecrets.Add(value);
    }

    private static string Sha256(string value)
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

internal sealed record P9Actor(
    string Key,
    string Id,
    string Username,
    string UnitId,
    string AccountKind,
    IReadOnlyCollection<string> Roles,
    string Token);

internal sealed record P9ActorSeed(string Key, AppUser User);

internal sealed record P9Fixture(
    string WorkId,
    string ReportPeriodId,
    string PairedReportPeriodId,
    string AssignmentId,
    string SiblingAssignmentId,
    string ReportId,
    string PairedReportId,
    string TemplateId,
    string ConfigId,
    string ConfigVersionId,
    int ConfigVersionNo,
    string UnitAId,
    string UnitBId,
    long ConfigRevision,
    string ConfigHash,
    int SourceRevision,
    string SourceHash,
    string PairedSourceHash,
    int LifecycleRevision,
    string FlowFamilyId,
    string FlowVersionId,
    string FlowInstanceId,
    string FlowEpochId,
    string FlowStepInstanceId,
    string FlowPayloadHash,
    int FlowExecutionEpoch,
    string PeriodKey,
    string PeriodInstanceKey,
    string PeriodKind,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    string ConfigPayloadJson,
    string SourcePayloadJson);

internal sealed record P9LifecycleTraceEntry(
    string State,
    int Status,
    int LifecycleRevision,
    string PayloadHash,
    string DocumentSha256,
    DateTime ChangedAtUtc);

internal sealed record P9CleanupHandle(
    string Collection,
    string Id);

internal sealed record P9FixtureCycleEvidence(
    int Cycle,
    IReadOnlyList<P9CleanupHandle> Handles,
    string BaselineSha256,
    string SeededSha256,
    string CleanedSha256,
    bool ReturnedToBaseline);
