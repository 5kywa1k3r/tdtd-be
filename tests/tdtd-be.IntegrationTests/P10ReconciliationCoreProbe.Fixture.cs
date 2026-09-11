using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.StatisticsReconciliation;
using tdtd_be.Services.StatisticsRun;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P10ReconciliationCoreProbe
{
    private static readonly string[] DirectStoreNames =
    [
        "work_report_field_stat_aggregates",
        "work_report_field_stat_values",
        "work_report_label_stat_aggregates",
        "work_report_label_stat_values",
        "work_report_table_stat_aggregates",
        "work_report_table_stat_values"
    ];

    private async Task BootstrapAndSeedFixtureAsync(CancellationToken ct)
    {
        var bootstrap = await RequireApi().PostAsync(
            "api/system/bootstrap",
            new { },
            headers: new Dictionary<string, string>
            {
                ["X-System-Bootstrap-Key"] = RequireBackend().BootstrapKey
            },
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            bootstrap,
            HttpStatusCode.OK,
            "P10 system bootstrap");
        _bootstrapPassword = ApiHarnessClient.RequiredString(
            bootstrap.Json,
            "defaultPassword");
        RememberSecret(_bootstrapPassword);
        var adminToken = await RequireApi().LoginAsync(
            "admin",
            _bootstrapPassword,
            ct);
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
        var fixedAt = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);
        var unitAId = ObjectId.GenerateNewId().ToString();
        var unitBId = ObjectId.GenerateNewId().ToString();
        await units.InsertManyAsync(
            [
                NewUnit(
                    unitAId,
                    "P10 Unit A",
                    "191001",
                    root.Level + 1,
                    root.Id,
                    admin.Id,
                    fixedAt),
                NewUnit(
                    unitBId,
                    "P10 Unit B",
                    "191002",
                    root.Level + 1,
                    root.Id,
                    admin.Id,
                    fixedAt)
            ],
            cancellationToken: ct);

        var actorSeeds = new[]
        {
            NewActor(
                "executor",
                "p10_executor",
                "P10 Executor",
                unitAId,
                ["MANAGER_LEVEL"],
                admin.Id,
                fixedAt),
            NewActor(
                "executor2",
                "p10_executor2",
                "P10 Executor Two",
                unitAId,
                ["MANAGER_LEVEL"],
                admin.Id,
                fixedAt),
            NewActor(
                "insufficient",
                "p10_insufficient",
                "P10 Insufficient",
                unitAId,
                [],
                admin.Id,
                fixedAt),
            NewActor(
                "outsider",
                "p10_outsider",
                "P10 Outsider",
                unitBId,
                ["MANAGER_LEVEL"],
                admin.Id,
                fixedAt)
        };
        var hasher = new PasswordHasher<AppUser>();
        foreach (var seed in actorSeeds)
        {
            seed.User.PasswordHash = hasher.HashPassword(
                seed.User,
                RequireBackend().ActorPassword);
        }
        await users.InsertManyAsync(
            actorSeeds.Select(seed => seed.User),
            cancellationToken: ct);

        _actors["admin"] = new P10Actor(
            "admin",
            admin.Id,
            admin.Username,
            admin.UnitId ?? string.Empty,
            admin.AccountKind ?? "SYSTEM_ADMIN",
            admin.Roles,
            adminToken);
        foreach (var seed in actorSeeds)
        {
            var token = await RequireApi().LoginAsync(
                seed.User.Username,
                RequireBackend().ActorPassword,
                ct);
            RememberSecret(token);
            _actors[seed.Key] = new P10Actor(
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
        _cleanupHandles.AddRange(
        [
            new P10CleanupHandle("units", unitAId),
            new P10CleanupHandle("units", unitBId),
            .. actorSeeds.Select(seed =>
                new P10CleanupHandle("users", seed.User.Id))
        ]);

        var fixtureEvidence = new
        {
            schemaVersion = "P10_CORE_FIXTURE_V1",
            chainId = ChainId,
            promptId = PromptId,
            groupId = GroupId,
            runKey = _runKey,
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
            cleanupHandles = _cleanupHandles,
            ownedDocuments = _cleanupHandles.Select(handle => new
            {
                collection = handle.Collection,
                id = handle.Id
            }),
            p8LockedConfig = new
            {
                collection = "dynamic_form_templates",
                ownerId = _fixture.DynamicFormVersionId,
                configId = _fixture.ConfigId,
                versionId = _fixture.ConfigVersionId,
                revision = _fixture.ConfigRevision,
                configHash = _fixture.ConfigHash,
                status = "LOCKED"
            },
            autonomous = true,
            sharedSeedDependency = false,
            developerSeedDependency = false,
            source = new
            {
                kind = "P9_LIFECYCLE_DIRECT_PROJECTION",
                currentPublication = true,
                directStoreNames = DirectStoreNames,
                coherentSnapshot = false,
                sourceSetSha256 = ExplicitJsonNull(),
                expectedAlgorithmSha256 = ExplicitJsonNull()
            }
        };
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "actor-fixture-matrix.json"),
            fixtureEvidence,
            ct);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "fixture-manifest.json"),
            fixtureEvidence,
            ct);
    }

    private async Task<P10Fixture> SeedDomainFixtureAsync(
        string adminId,
        string unitAId,
        string unitBId,
        DateTime fixedAt,
        CancellationToken ct)
    {
        var executor = Actor("executor");
        var workId = ObjectId.GenerateNewId().ToString();
        var assignmentId = ObjectId.GenerateNewId().ToString();
        var siblingAssignmentId = ObjectId.GenerateNewId().ToString();
        var reportId = ObjectId.GenerateNewId().ToString();
        var payloadId = ObjectId.GenerateNewId().ToString();
        var reportPeriodId = ObjectId.GenerateNewId().ToString();
        var dynamicFormVersionId = ObjectId.GenerateNewId().ToString();
        var dynamicFormFamilyId = ObjectId.GenerateNewId().ToString();
        var configId = ObjectId.GenerateNewId().ToString();
        var configVersionId = ObjectId.GenerateNewId().ToString();
        var p9RunId = ObjectId.GenerateNewId().ToString();
        var flowTemplateId = ObjectId.GenerateNewId().ToString();
        var flowTemplateVersionId = ObjectId.GenerateNewId().ToString();
        var flowInstanceId = ObjectId.GenerateNewId().ToString();
        var flowExecutionEpochId = ObjectId.GenerateNewId().ToString();
        var flowStepInstanceId = ObjectId.GenerateNewId().ToString();
        var flowBranchId = ObjectId.GenerateNewId().ToString();
        var exportId = ObjectId.GenerateNewId().ToString();
        var sourcePayloadHash = HashText("P10-AUTONOMOUS-SOURCE-PAYLOAD-V1");
        var sourceLifecycleEventKey = HashText(
            "P10-AUTONOMOUS-SOURCE-LIFECYCLE-EVENT-V1");
        var sourceMembershipSignature = HashText(
            "P10-AUTONOMOUS-SOURCE-MEMBERSHIP-V1");
        var configHash = HashText("P10-AUTONOMOUS-P8-CONFIG-V1");
        var generationId = HashText("P10-AUTONOMOUS-P9-GENERATION-ID-V1");
        var generationHash = HashText(
            "P10-AUTONOMOUS-P9-GENERATION-HASH-V1");
        var flowContributionPolicyHash = HashText(
            "P10-AUTONOMOUS-FLOW-CONTRIBUTION-POLICY-V1");
        var flowContributionLedgerHash = HashText(
            "P10-AUTONOMOUS-FLOW-CONTRIBUTION-LEDGER-V1");
        var publishedSchema =
            DynamicFormPublishedSchemaSnapshotBuilder.Build(
                1,
                "[]",
                "[]",
                "[]");
        var dynamicFormSchemaHash = publishedSchema.Sha256;
        var flowCatalogVersion =
            tdtd_be.Common.Capabilities.DynamicFormFlowCapabilityCatalogMetadata
                .CatalogVersion;
        var flowCatalogSemanticHash =
            tdtd_be.Common.Capabilities.DynamicFormFlowCapabilityCatalogMetadata
                .CatalogSha256;
        var flowPayloadNode = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["archetypeId"] = "FLOW-T01",
            ["entryStepId"] = "P10_CORE_STEP",
            ["rootDynamicFormTemplateId"] = dynamicFormVersionId,
            ["catalogVersion"] = flowCatalogVersion,
            ["catalogSemanticHash"] = flowCatalogSemanticHash,
            ["formNodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["formNodeId"] = "P10_CORE_FORM_NODE",
                    ["role"] = "ROOT",
                    ["dynamicFormTemplateId"] = dynamicFormVersionId,
                    ["dynamicFormFamilyId"] = dynamicFormFamilyId,
                    ["dynamicFormVersionNo"] = 1,
                    ["dynamicFormSchemaHash"] = dynamicFormSchemaHash,
                    ["dynamicFormSnapshotHash"] = dynamicFormSchemaHash
                }
            },
            ["nodes"] = new JsonArray
            {
                new JsonObject
                {
                    ["nodeId"] = "P10_CORE_STEP",
                    ["nodeCode"] = "P10_CORE_STEP",
                    ["nodeKind"] = "FORM_STEP",
                    ["formNodeId"] = "P10_CORE_FORM_NODE",
                    ["declaredRoles"] = new JsonArray("OWNER")
                }
            },
            ["edges"] = new JsonArray(),
            ["actorPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "p10-actor-owner-root",
                    ["stepId"] = "P10_CORE_STEP",
                    ["stepCode"] = "*",
                    ["actorRole"] = "OWNER",
                    ["allowForward"] = true
                }
            },
            ["fieldPolicies"] = new JsonArray
            {
                new JsonObject
                {
                    ["policyId"] = "p10-fields-owner-root",
                    ["dynamicFormTemplateId"] = "*",
                    ["stepId"] = "P10_CORE_STEP",
                    ["stepCode"] = "*",
                    ["actorRole"] = "OWNER",
                    ["fieldId"] = "*",
                    ["fieldKey"] = "*",
                    ["read"] = true,
                    ["write"] = true
                }
            },
            ["tableColumnPolicies"] = new JsonArray(),
            ["mappingRules"] = new JsonArray(),
            ["rollbackPolicy"] = new JsonObject(),
            ["finalResultPolicy"] = new JsonObject(),
            ["statisticProfile"] = new JsonObject()
        };
        var canonicalFlow =
            DynamicFlowDefinitionPayloadContract.CanonicalizeAndValidate(
                flowPayloadNode.ToJsonString(),
                new DynamicFlowDefinitionValidationOptions(
                    AllowLegacy: false,
                    AllowServerManagedPins: true,
                    RequireServerManagedPins: true,
                    AllowHistoricalCatalogPins: true));
        var flowPayloadJson = canonicalFlow.CanonicalJson;
        var flowPayloadHash = canonicalFlow.PayloadHash;
        var periodStart = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = new DateTime(2026, 8, 31, 23, 59, 59, DateTimeKind.Utc);
        var publicationScopeKey =
            StatisticReconciliationCanonicalJson.HashObject(new
            {
                version = "P9_DIRECT_PUBLICATION_SCOPE_V1",
                WorkId = workId,
                PeriodInstanceKey = "MONTH:2026-08",
                dynamicFormFamilyId,
                dynamicFormTemplateId = dynamicFormVersionId,
                dynamicFormVersionNo = 1,
                dynamicFormSchemaHash,
                configVersionId,
                configRevision = 1L,
                configHash
            });

        var assignment = NewAssignment(
            assignmentId,
            workId,
            dynamicFormVersionId,
            dynamicFormFamilyId,
            dynamicFormSchemaHash,
            flowTemplateId,
            flowTemplateVersionId,
            flowInstanceId,
            flowExecutionEpochId,
            flowStepInstanceId,
            flowBranchId,
            unitAId,
            executor.Id,
            adminId,
            fixedAt,
            "P10-CORE-A");
        assignment.LeaderWatcherUserIds =
        [
            Actor("executor2").Id,
            Actor("insufficient").Id
        ];
        var sibling = NewAssignment(
            siblingAssignmentId,
            workId,
            dynamicFormVersionId,
            dynamicFormFamilyId,
            dynamicFormSchemaHash,
            flowTemplateId,
            flowTemplateVersionId,
            flowInstanceId,
            flowExecutionEpochId,
            flowStepInstanceId,
            flowBranchId,
            unitAId,
            executor.Id,
            adminId,
            fixedAt,
            "P10-CORE-SIBLING");
        sibling.CreatedByUserId = adminId;
        sibling.LeaderWatcherUserIds = [];
        await RequireDatabase()
            .GetCollection<WorkAssignment>("work_assignments")
            .InsertManyAsync([assignment, sibling], cancellationToken: ct);

        var database = RequireDatabase();
        var work = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(workId),
            ["autoCode"] = "P10COREWORK",
            ["code"] = "P10-CORE-WORK",
            ["name"] = "P10 Autonomous Reconciliation Work",
            ["issuedByUnitId"] = ObjectId.Parse(unitAId),
            ["createdByUserId"] = ObjectId.Parse(executor.Id),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var period = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(reportPeriodId),
            ["workId"] = ObjectId.Parse(workId),
            ["workAssignmentId"] = ObjectId.Parse(assignmentId),
            ["workTemplateAssigneeId"] = ObjectId.Parse(assignmentId),
            ["dynamicFormTemplateId"] = ObjectId.Parse(dynamicFormVersionId),
            ["dynamicFormTemplateCode"] = "P10_CORE_FORM",
            ["dynamicFormTemplateName"] = "P10 Autonomous Core Form",
            ["dynamicFormFamilyId"] = ObjectId.Parse(dynamicFormFamilyId),
            ["dynamicFormVersionNo"] = 1,
            ["dynamicFormSchemaHash"] = dynamicFormSchemaHash,
            ["assigneeUserId"] = ObjectId.Parse(executor.Id),
            ["assigneeUnitId"] = ObjectId.Parse(unitAId),
            ["periodKey"] = "2026-08",
            ["periodInstanceKey"] = "MONTH:2026-08",
            ["periodKind"] = "SCHEDULED",
            ["periodStart"] = periodStart,
            ["periodEnd"] = periodEnd,
            ["currentReportId"] = ObjectId.Parse(reportId),
            ["sourceLifecycleReportId"] = ObjectId.Parse(reportId),
            ["sourceLifecycleRevision"] = 3,
            ["isActive"] = true,
            ["createdByUserId"] = ObjectId.Parse(executor.Id),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt.AddMinutes(2),
            ["isDeleted"] = false
        };
        var formTemplate = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(dynamicFormVersionId),
            ["code"] = "P10_CORE_FORM",
            ["name"] = "P10 Autonomous Core Form",
            ["schemaVersion"] = 1,
            ["versionNo"] = 1,
            ["revision"] = 1L,
            ["isActive"] = true,
            ["isPublished"] = true,
            ["publishedAtUtc"] = fixedAt,
            ["publishedByUserId"] = ObjectId.Parse(adminId),
            ["familyId"] = ObjectId.Parse(dynamicFormFamilyId),
            ["lineageStatus"] = "ROOT",
            ["sectionsJson"] = "[]",
            ["fieldsJson"] = "[]",
            ["blocksJson"] = "[]",
            ["publishedSchemaSnapshotJson"] = publishedSchema.Json,
            ["publishedSchemaHash"] = dynamicFormSchemaHash,
            ["statisticConfigId"] = ObjectId.Parse(configId),
            ["statisticConfigVersionId"] = ObjectId.Parse(configVersionId),
            ["statisticConfigVersionNo"] = 1,
            ["statisticConfigRevision"] = 1L,
            ["statisticConfigStatus"] = "LOCKED",
            ["statisticConfigHash"] = configHash,
            ["statisticConfigDependencyPins"] = new BsonArray
            {
                $"catalog-semantic:{P9CatalogSemanticSha256}",
                $"schema-semantic:{P9SchemaSemanticSha256}"
            },
            ["statisticConfigSections"] = new BsonDocument
            {
                ["fieldSectionJson"] = "[]",
                ["tableSectionJson"] = "[]"
            },
            ["statisticConfigSnapshots"] = new BsonArray
            {
                new BsonDocument
                {
                    ["versionId"] = ObjectId.Parse(configVersionId),
                    ["previousVersionId"] = BsonNull.Value,
                    ["versionNo"] = 1,
                    ["revision"] = 1L,
                    ["status"] = "LOCKED",
                    ["configHash"] = configHash,
                    ["createdAtUtc"] = fixedAt,
                    ["createdByUserId"] = ObjectId.Parse(adminId)
                }
            },
            ["createdByUserId"] = ObjectId.Parse(adminId),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var report = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(reportId),
            ["workId"] = ObjectId.Parse(workId),
            ["workAssignmentId"] = ObjectId.Parse(assignmentId),
            ["workReportPeriodId"] = ObjectId.Parse(reportPeriodId),
            ["dynamicFormTemplateId"] = ObjectId.Parse(dynamicFormVersionId),
            ["dynamicFormTemplateCode"] = "P10_CORE_FORM",
            ["dynamicFormTemplateName"] = "P10 Autonomous Core Form",
            ["dynamicFormFamilyId"] = ObjectId.Parse(dynamicFormFamilyId),
            ["dynamicFormVersionNo"] = 1,
            ["dynamicFormSchemaHash"] = dynamicFormSchemaHash,
            ["assigneeUserId"] = ObjectId.Parse(executor.Id),
            ["periodKey"] = "2026-08",
            ["periodInstanceKey"] = "MONTH:2026-08",
            ["periodKind"] = "SCHEDULED",
            ["periodStart"] = periodStart,
            ["periodEnd"] = periodEnd,
            ["status"] = 2,
            ["payloadRevision"] = 1,
            ["payloadHash"] = sourcePayloadHash,
            ["payloadSizeBytes"] = 6L,
            ["lifecycleRevision"] = 3,
            ["isCurrent"] = true,
            ["isActive"] = true,
            ["values1DJson"] = "{}",
            ["fieldValuesJson"] = "{}",
            ["tableValuesRootJson"] = "[]",
            ["summarySourceJson"] = "{}",
            ["submittedAtUtc"] = fixedAt.AddMinutes(1),
            ["approvedAtUtc"] = fixedAt.AddMinutes(2),
            ["approvedByUserId"] = ObjectId.Parse(adminId),
            ["createdByUserId"] = ObjectId.Parse(executor.Id),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt.AddMinutes(2),
            ["isDeleted"] = false
        };
        var payload = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(payloadId),
            ["reportId"] = ObjectId.Parse(reportId),
            ["payloadRevision"] = 1,
            ["values1DJson"] = "{}",
            ["fieldValuesJson"] = "{}",
            ["tableValuesRootJson"] = "[]",
            ["summarySourceJson"] = "{}",
            ["payloadHash"] = sourcePayloadHash,
            ["payloadSizeBytes"] = 6L,
            ["status"] = "Ready",
            ["createdByUserId"] = ObjectId.Parse(executor.Id),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt.AddMinutes(2),
            ["isDeleted"] = false
        };
        var flowTemplate = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(flowTemplateId),
            ["code"] = "P10_CORE_FLOW",
            ["name"] = "P10 Autonomous Core Flow",
            ["familyRevision"] = 1L,
            ["ownerUserId"] = ObjectId.Parse(adminId),
            ["ownerUnitId"] = ObjectId.Parse(unitAId),
            ["rootDynamicFormTemplateId"] = ObjectId.Parse(dynamicFormVersionId),
            ["originFamilyId"] = BsonNull.Value,
            ["originVersionId"] = BsonNull.Value,
            ["status"] = "ACTIVE",
            ["currentVersionId"] = ObjectId.Parse(flowTemplateVersionId),
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
            ["_id"] = ObjectId.Parse(flowTemplateVersionId),
            ["templateId"] = ObjectId.Parse(flowTemplateId),
            ["rootDynamicFormTemplateId"] = ObjectId.Parse(dynamicFormVersionId),
            ["versionNo"] = 1,
            ["status"] = "LOCKED",
            ["draftRevision"] = 1L,
            ["schemaVersion"] = canonicalFlow.Payload.SchemaVersion,
            ["adapterVersion"] = canonicalFlow.AdapterVersion,
            ["catalogVersion"] = flowCatalogVersion,
            ["catalogSemanticHash"] = flowCatalogSemanticHash,
            ["originFamilyId"] = BsonNull.Value,
            ["originVersionId"] = BsonNull.Value,
            ["payloadJson"] = flowPayloadJson,
            ["payloadHash"] = flowPayloadHash,
            ["definitionLockable"] = true,
            ["executionEligibility"] = "BLOCKED_UNTIL_TARGET_PHASE",
            ["executionBlockedReason"] = "TARGET_PHASE_NOT_IMPLEMENTED",
            ["blockedUntilPhase"] = canonicalFlow.BlockedUntilPhase,
            ["migrationState"] = "CANONICAL",
            ["contributionPolicy"] = "INCLUDE",
            ["contributionPolicyHash"] = flowContributionPolicyHash,
            ["lockedAtUtc"] = fixedAt,
            ["lockedByUserId"] = ObjectId.Parse(adminId),
            ["createdByUserId"] = ObjectId.Parse(adminId),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var flowInstance = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(flowInstanceId),
            ["workId"] = ObjectId.Parse(workId),
            ["workType"] = "REPORT",
            ["flowTemplateId"] = ObjectId.Parse(flowTemplateId),
            ["flowTemplateVersionId"] = ObjectId.Parse(flowTemplateVersionId),
            ["flowTemplateVersionNo"] = 1,
            ["flowPayloadHash"] = flowPayloadHash,
            ["executionEpoch"] = 1,
            ["entryFlowStepId"] = "P10_CORE_STEP",
            ["rootInstanceId"] = ObjectId.Parse(flowInstanceId),
            ["resultOwnerUserId"] = ObjectId.Parse(executor.Id),
            ["resultOwnerUnitId"] = ObjectId.Parse(unitAId),
            ["periodKey"] = "2026-08",
            ["state"] = "ACTIVE",
            ["revision"] = 1L,
            ["createdByUserId"] = ObjectId.Parse(adminId),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var flowEpoch = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(flowExecutionEpochId),
            ["flowInstanceId"] = ObjectId.Parse(flowInstanceId),
            ["executionEpoch"] = 1,
            ["state"] = "ACTIVE",
            ["checkpointNodeId"] = "P10_CORE_STEP",
            ["isCanonical"] = true,
            ["revision"] = 1L,
            ["openedAtUtc"] = fixedAt,
            ["createdByUserId"] = ObjectId.Parse(adminId),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt,
            ["isDeleted"] = false
        };
        var flowStep = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(flowStepInstanceId),
            ["flowInstanceId"] = ObjectId.Parse(flowInstanceId),
            ["flowStepId"] = "P10_CORE_STEP",
            ["flowStepCode"] = "P10_CORE_STEP",
            ["executionEpoch"] = 1,
            ["definitionRevision"] = flowPayloadHash,
            ["stepOrder"] = 1,
            ["formFamilyId"] = ObjectId.Parse(dynamicFormFamilyId),
            ["formVersionId"] = ObjectId.Parse(dynamicFormVersionId),
            ["formVersionNo"] = 1,
            ["formSchemaHash"] = dynamicFormSchemaHash,
            ["targetUnitId"] = ObjectId.Parse(unitAId),
            ["participantUserIds"] = new BsonArray
            {
                ObjectId.Parse(executor.Id),
                ObjectId.Parse(Actor("executor2").Id)
            },
            ["attemptNo"] = 1,
            ["branchId"] = ObjectId.Parse(flowBranchId),
            ["assignmentId"] = ObjectId.Parse(assignmentId),
            ["reportId"] = ObjectId.Parse(reportId),
            ["reportLifecycleRevision"] = 3,
            ["reportLifecycleStatus"] = "APPROVED",
            ["state"] = "APPROVED",
            ["revision"] = 1L,
            ["createdByUserId"] = ObjectId.Parse(adminId),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt,
            ["updatedAtUtc"] = fixedAt.AddMinutes(2),
            ["isDeleted"] = false
        };
        await database.GetCollection<BsonDocument>("works")
            .InsertOneAsync(work, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("work_report_periods")
            .InsertOneAsync(period, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_form_templates")
            .InsertOneAsync(formTemplate, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("work_assignment_report")
            .InsertOneAsync(report, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("work_report_payloads")
            .InsertOneAsync(payload, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_templates")
            .InsertOneAsync(flowTemplate, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_template_versions")
            .InsertOneAsync(flowVersion, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_instances")
            .InsertOneAsync(flowInstance, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_execution_epochs")
            .InsertOneAsync(flowEpoch, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("dynamic_flow_step_instances")
            .InsertOneAsync(flowStep, cancellationToken: ct);

        var directDigests = DirectStoreNames.Select(store =>
            new WorkReportDirectStoreDigest
            {
                Store = store,
                RowCount = 0,
                Sha256 = HashText($"P10-DIRECT-STORE:{store}:EMPTY")
            }).ToList();
        var p9 = new WorkReportStatisticRebuildJob
        {
            Id = p9RunId,
            DedupeKey = HashText("P10-AUTONOMOUS-P9-DEDUPE-V1"),
            ReceiptId = HashText("P10-AUTONOMOUS-P9-RECEIPT-V1"),
            CommandId = "p10-autonomous-p9-publication",
            RequestHash = HashText("P10-AUTONOMOUS-P9-REQUEST-V1"),
            ReceiptResponseHash = HashText(
                "P10-AUTONOMOUS-P9-RECEIPT-RESPONSE-V1"),
            ImmutableHeaderHash = HashText(
                "P10-AUTONOMOUS-P9-IMMUTABLE-HEADER-V1"),
            ReceiptAcceptedAtUtc = fixedAt,
            CapabilityId = "DIRECT_FIELD_TABLE_LABEL",
            RouteId = StatRunRouteRegistry.LifecycleDirectProjector,
            RunKind = WorkReportStatisticRebuildJobRunKinds
                .LifecycleDirectProjection,
            ActorUserId = executor.Id,
            TenantUnitId = unitAId,
            ScopeType = "WORK_PERIOD_TEMPLATE",
            ScopeId = workId,
            SourceReportId = reportId,
            SourcePayloadRevision = 1,
            SourcePayloadHash = sourcePayloadHash,
            SourceLifecycleRevision = 3,
            SourceLifecycleEventKey = sourceLifecycleEventKey,
            SourceMembershipSignature = sourceMembershipSignature,
            DirectSourceRevision = 1,
            PublicationScopeKey = publicationScopeKey,
            DirectPublicationRevision = 1,
            IsCurrentPublication = true,
            SourceStatus = "APPROVED",
            ConfigId = configId,
            ConfigVersionId = configVersionId,
            ConfigVersionNo = 1,
            ConfigRevision = 1,
            ConfigHash = configHash,
            CatalogVersion = "1.6",
            CatalogRawSha256 = P9CatalogRawSha256,
            CatalogSemanticSha256 = P9CatalogSemanticSha256,
            SchemaRawSha256 = P9SchemaRawSha256,
            SchemaSemanticSha256 = P9SchemaSemanticSha256,
            StageLockSha256 = StatRunCapabilityActivation
                .PublishedSealStageLockRawSha256,
            CandidateChainId = StatRunCapabilityActivation.RequiredChainId,
            CandidatePromptId = StatRunCapabilityActivation.PublishedPromptId,
            DynamicFormTemplateId = dynamicFormVersionId,
            DynamicFormFamilyId = dynamicFormFamilyId,
            DynamicFormVersionNo = 1,
            DynamicFormSchemaHash = dynamicFormSchemaHash,
            DynamicFormTemplateCode = "P10_CORE_FORM",
            DynamicFormTemplateName = "P10 Autonomous Core Form",
            ScopeKind = WorkReportStatisticRebuildJobScopeKinds.Bounded,
            WorkId = workId,
            WorkAssignmentId = assignmentId,
            FlowInstanceId = flowInstanceId,
            FlowInstanceRevision = 1,
            FlowInstanceState = "ACTIVE",
            FlowEffectiveStatus = "EFFECTIVE",
            FlowTemplateId = flowTemplateId,
            FlowFamilyRevision = 1,
            FlowTemplateVersionNo = 1,
            FlowTemplateVersionId = flowTemplateVersionId,
            FlowContributionOriginVersionId = flowTemplateVersionId,
            FlowPayloadHash = flowPayloadHash,
            FlowCatalogVersion = flowCatalogVersion,
            FlowCatalogSemanticHash = flowCatalogSemanticHash,
            FlowExecutionEpoch = 1,
            FlowExecutionEpochId = flowExecutionEpochId,
            FlowExecutionEpochRevision = 1,
            FlowExecutionEpochState = "ACTIVE",
            FlowBranchId = flowBranchId,
            FlowStepId = "P10_CORE_STEP",
            FlowAttemptNo = 1,
            FlowStepInstanceId = flowStepInstanceId,
            FlowStepInstanceRevision = 1,
            FlowStepInstanceState = "ACTIVE",
            FlowContributionPolicy = "INCLUDE",
            FlowContributionPolicyHash = flowContributionPolicyHash,
            FlowContributionOperationVersion = "P9-FLW-V1",
            FlowContributionLedgerHash = flowContributionLedgerHash,
            PeriodKey = "2026-08",
            PeriodInstanceKey = "MONTH:2026-08",
            PeriodKind = "SCHEDULED",
            PeriodStartUtc = periodStart,
            PeriodEndUtc = periodEnd,
            Status = WorkReportStatisticRebuildJobStatuses.Completed,
            StateRevision = 7,
            IsActive = false,
            RequestedByUserId = executor.Id,
            Priority = WorkReportStatisticRebuildJobPriorities.Normal,
            GenerationId = generationId,
            GenerationHash = generationHash,
            DirectStoreDigests = directDigests,
            PublishedAtUtc = fixedAt.AddMinutes(5),
            ComputedAtUtc = fixedAt.AddMinutes(4),
            CompletedAtUtc = fixedAt.AddMinutes(5),
            FreshnessState = WorkReportStatisticRebuildJobFreshnessStates.Fresh,
            CreatedByUserId = executor.Id,
            UpdatedByUserId = adminId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt.AddMinutes(5),
            IsDeleted = false
        };
        p9.StateHash = StatisticReconciliationCanonicalJson.HashObject(new
        {
            version = "P9_LIFECYCLE_DIRECT_STATE_V1",
            runId = p9.Id,
            status = p9.Status,
            revision = p9.StateRevision,
            claimToken = (string?)null,
            workerId = (string?)null,
            generationId = p9.GenerationId,
            generationHash = p9.GenerationHash
        });
        await RequireDatabase()
            .GetCollection<WorkReportStatisticRebuildJob>(
                "work_report_statistic_rebuild_jobs")
            .InsertOneAsync(p9, cancellationToken: ct);

        var export = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(exportId),
            ["commandId"] = "p10-autonomous-p9-export",
            ["requestHash"] = HashText("P10-AUTONOMOUS-P9-EXPORT-REQUEST-V1"),
            ["receiptId"] = HashText("P10-AUTONOMOUS-P9-EXPORT-RECEIPT-V1"),
            ["requestedByUserId"] = ObjectId.Parse(executor.Id),
            ["authorizationSnapshotHash"] = HashText("P10-AUTONOMOUS-P9-EXPORT-AUTH-V1"),
            ["capabilityId"] = "DIRECT_FIELD_TABLE_LABEL",
            ["resultKind"] = "DIRECT_FIELD",
            ["format"] = "CSV",
            ["workId"] = ObjectId.Parse(workId),
            ["scopeType"] = "WORK_PERIOD_TEMPLATE",
            ["scopeId"] = workId,
            ["periodInstanceKey"] = "MONTH:2026-08",
            ["resultId"] = p9RunId,
            ["resultHash"] = generationHash,
            ["configHash"] = configHash,
            ["sourceHash"] = sourcePayloadHash,
            ["lifecycleRevision"] = 3,
            ["catalogVersion"] = "1.6",
            ["catalogRawSha256"] = P9CatalogRawSha256,
            ["catalogSemanticSha256"] = P9CatalogSemanticSha256,
            ["stageLockSha256"] = StatRunCapabilityActivation.PublishedSealStageLockRawSha256,
            ["candidateChainId"] = StatRunCapabilityActivation.RequiredChainId,
            ["candidatePromptId"] = StatRunCapabilityActivation.PublishedPromptId,
            ["candidateStage"] = 12,
            ["filterHash"] = StatisticReconciliationCanonicalJson.HashObject(
                new Dictionary<string, string> { ["periodKey"] = "2026-08" }),
            ["schemaVersion"] = "P9_CANONICAL_EXPORT_V1",
            ["semanticHash"] = HashText("P10-AUTONOMOUS-P9-EXPORT-SEMANTIC-V1"),
            ["status"] = "COMPLETED",
            ["fileName"] = "p10-autonomous-p9-export.csv",
            ["contentType"] = "text/csv",
            ["storageKey"] = "p10/autonomous/p9-export.csv",
            ["contentHash"] = HashText("P10-AUTONOMOUS-P9-EXPORT-CONTENT-V1"),
            ["byteCount"] = 0L,
            ["rowCount"] = 0,
            ["columnCount"] = 0,
            ["downloadCount"] = 0,
            ["completedAtUtc"] = fixedAt.AddMinutes(5),
            ["expiresAtUtc"] = fixedAt.AddYears(1),
            ["createdByUserId"] = ObjectId.Parse(executor.Id),
            ["updatedByUserId"] = ObjectId.Parse(adminId),
            ["createdAtUtc"] = fixedAt.AddMinutes(5),
            ["updatedAtUtc"] = fixedAt.AddMinutes(5),
            ["isDeleted"] = false
        };
        await database.GetCollection<BsonDocument>(
                "work_report_statistic_exports")
            .InsertOneAsync(export, cancellationToken: ct);

        _cleanupHandles.AddRange(
        [
            new P10CleanupHandle("work_assignments", assignmentId),
            new P10CleanupHandle("work_assignments", siblingAssignmentId),
            new P10CleanupHandle("works", workId),
            new P10CleanupHandle("work_report_periods", reportPeriodId),
            new P10CleanupHandle("dynamic_form_templates", dynamicFormVersionId),
            new P10CleanupHandle("work_assignment_report", reportId),
            new P10CleanupHandle("work_report_payloads", payloadId),
            new P10CleanupHandle("dynamic_flow_templates", flowTemplateId),
            new P10CleanupHandle("dynamic_flow_template_versions", flowTemplateVersionId),
            new P10CleanupHandle("dynamic_flow_instances", flowInstanceId),
            new P10CleanupHandle("dynamic_flow_execution_epochs", flowExecutionEpochId),
            new P10CleanupHandle("dynamic_flow_step_instances", flowStepInstanceId),
            new P10CleanupHandle(
                "work_report_statistic_rebuild_jobs",
                p9RunId),
            new P10CleanupHandle("work_report_statistic_exports", exportId)
        ]);

        var configBundleHash =
            StatisticReconciliationCanonicalJson.HashObject(new
            {
                schema = "P10_P8_CONFIG_BUNDLE_PIN_V1",
                ownerId = dynamicFormVersionId,
                configId,
                configVersionId,
                configVersionNo = 1,
                configRevision = 1L,
                configHash
            });
        var filterHash = StatisticReconciliationCanonicalJson.HashObject(
            new Dictionary<string, string>
            {
                ["periodKey"] = "2026-08"
            });
        return new P10Fixture(
            workId,
            assignmentId,
            siblingAssignmentId,
            reportId,
            payloadId,
            reportPeriodId,
            dynamicFormVersionId,
            dynamicFormFamilyId,
            p9RunId,
            p9RunId,
            generationId,
            generationHash,
            sourcePayloadHash,
            1,
            3,
            configId,
            configVersionId,
            1,
            configHash,
            configBundleHash,
            "2026-08",
            "MONTH:2026-08",
            "SCHEDULED",
            "AMOUNT",
            "MONTH",
            filterHash,
            flowTemplateId,
            flowTemplateVersionId,
            flowInstanceId,
            flowExecutionEpochId,
            flowStepInstanceId,
            flowBranchId,
            flowPayloadHash,
            exportId,
            unitAId,
            unitBId);
    }

    private static WorkAssignment NewAssignment(
        string id,
        string workId,
        string dynamicFormVersionId,
        string dynamicFormFamilyId,
        string dynamicFormSchemaHash,
        string flowTemplateId,
        string flowTemplateVersionId,
        string flowInstanceId,
        string flowExecutionEpochId,
        string flowStepInstanceId,
        string flowBranchId,
        string unitId,
        string executorId,
        string adminId,
        DateTime fixedAt,
        string code)
        => new()
        {
            Id = id,
            WorkId = workId,
            DynamicFormTemplateId = dynamicFormVersionId,
            DynamicFormTemplateCode = "P10_CORE_FORM",
            DynamicFormTemplateName = "P10 Autonomous Core Form",
            DynamicFormFamilyId = dynamicFormFamilyId,
            DynamicFormVersionNo = 1,
            DynamicFormSchemaHash = dynamicFormSchemaHash,
            WorkType = "REPORT",
            AssignmentType = "USER",
            AggregationType = "NONE",
            Assignees = [new UserRef { UserId = executorId }],
            IsActive = true,
            RootAssignmentId = id,
            Level = 0,
            Code = code,
            Name = code,
            Path = id,
            FlowTemplateId = flowTemplateId,
            FlowTemplateVersionNo = 1,
            FlowInstanceId = flowInstanceId,
            FlowStepId = "P10_CORE_STEP",
            FlowStepCode = "P10_CORE_STEP",
            FlowStepOrder = 1,
            FlowBranchId = flowBranchId,
            FlowAttemptNo = 1,
            FlowExecutionEpoch = 1,
            FlowEffectiveStatus = "EFFECTIVE",
            IssuedByUnitId = unitId,
            TargetUnitIds = [unitId],
            CreatedByUserId = executorId,
            UpdatedByUserId = adminId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };

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

    private static P10ActorSeed NewActor(
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
}

internal sealed record P10ActorSeed(string Key, AppUser User);
