using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private static readonly string LifecycleFieldsJson = new JsonArray
    {
        new JsonObject
        {
            ["id"] = "field_amount",
            ["sectionId"] = LifecycleSectionId,
            ["key"] = "amount",
            ["name"] = "P9 lifecycle amount",
            ["type"] = "number",
            ["isStatistic"] = true,
            ["statisticLabelCodes"] = new JsonArray(),
            ["statistic"] = new JsonObject
            {
                ["aggregateOps"] = new JsonArray { "SUM" },
                ["bucketMode"] = "NONE",
                ["showInTree"] = true,
                ["showInDetail"] = true
            }
        },
        new JsonObject
        {
            ["id"] = "field_approved",
            ["sectionId"] = LifecycleSectionId,
            ["key"] = "approved",
            ["name"] = "P9 lifecycle approved",
            ["type"] = "boolean",
            ["isStatistic"] = true,
            ["statisticLabelCodes"] = new JsonArray(),
            ["statistic"] = new JsonObject
            {
                ["aggregateOps"] = new JsonArray { "COUNT" },
                ["bucketMode"] = "NONE",
                ["showInTree"] = true,
                ["showInDetail"] = true
            }
        }
    }.ToJsonString();

    private static readonly string LifecycleBlocksJson = new JsonArray
    {
        new JsonObject
        {
            ["id"] = LifecycleBlockId,
            ["blockId"] = LifecycleBlockId,
            ["sectionId"] = LifecycleSectionId,
            ["tableMode"] = "FIXED_GRID",
            ["rowLabelDataType"] = "NUMBER",
            ["w"] = 1,
            ["h"] = 1,
            ["statisticsInputCellCount"] = 1,
            ["statisticsInputCellLimit"] = 250,
            ["statisticsDisabled"] = false,
            ["allowedRowLabelCodes"] = new JsonArray
            {
                LifecycleLabelCode
            },
            ["metricRules"] = new JsonArray
            {
                new JsonObject
                {
                    ["metricKey"] = "amount",
                    ["dataType"] = "NUMBER",
                    ["aggregateOps"] = new JsonArray { "SUM" }
                }
            }
        }
    }.ToJsonString();

    private static readonly string LifecycleTableValuesJson = new JsonObject
    {
        ["blocks"] = new JsonArray
        {
            new JsonObject
            {
                ["blockId"] = LifecycleBlockId,
                ["tableMode"] = "FIXED_GRID",
                ["w"] = 1,
                ["h"] = 1,
                ["statisticsInputCellCount"] = 1,
                ["values1D"] = new JsonArray { 7 },
                ["valueSlots"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["rowKey"] = "row_1",
                        ["columnKey"] = "col_1"
                    }
                },
                ["metricDefinitions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["metricKey"] = "amount",
                        ["rowKey"] = "row_1",
                        ["columnKey"] = "col_1",
                        ["dataType"] = "NUMBER"
                    }
                },
                ["rowLabels"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["sheetId"] = "sheet_1",
                        ["rowKey"] = "sheet_1:R1",
                        ["rowIndex"] = 0,
                        ["rowLabelCodes"] = new JsonArray
                        {
                            LifecycleLabelCode
                        },
                        ["source"] = "ROW_LABEL"
                    }
                }
            }
        }
    }.ToJsonString();

    private async Task PrepareLifecycleFixtureAsync(CancellationToken ct)
    {
        var fixture = Fixture();
        var database = RequireDatabase();
        var reportId = ObjectId.Parse(fixture.ReportId);
        var admin = Actor("admin");
        var executor = Actor("executor");
        var executor2 = Actor("executor2");

        var templates = database.GetCollection<BsonDocument>(
            "dynamic_form_templates");
        var template = await templates
            .Find(new BsonDocument("_id", ObjectId.Parse(fixture.TemplateId)))
            .SingleAsync(ct);
        var publishedSchema = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            template.GetValue("schemaVersion", 1).ToInt32(),
            template.GetValue("sectionsJson", "[]").AsString,
            LifecycleFieldsJson,
            LifecycleBlocksJson);
        await templates
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(fixture.TemplateId)),
                Builders<BsonDocument>.Update
                    .Set("fieldsJson", LifecycleFieldsJson)
                    .Set("blocksJson", LifecycleBlocksJson)
                    .Set("publishedSchemaSnapshotJson", publishedSchema.Json)
                    .Set("publishedSchemaHash", publishedSchema.Sha256)
                    .Unset("statisticConfigId")
                    .Unset("statisticConfigPreviousVersionId")
                    .Unset("statisticConfigVersionId")
                    .Unset("statisticConfigVersionNo")
                    .Unset("statisticConfigRevision")
                    .Unset("statisticConfigStatus")
                    .Unset("statisticConfigHash")
                    .Unset("statisticConfigDependencyPins")
                    .Unset("statisticConfigSections")
                    .Unset("statisticConfigSnapshots")
                    .Unset("statisticConfigUpdatedAtUtc")
                    .Unset("statisticConfigUpdatedByUserId")
                    .Unset("statisticConfigUpdateMonthKey")
                    .Unset("p9CanonicalConfigPayloadJson"),
                cancellationToken: ct);

        var flowVersions = database.GetCollection<DynamicFlowTemplateVersion>(
            "dynamic_flow_template_versions");
        var lockedFlowVersion = await flowVersions
            .Find(version =>
                version.Id == fixture.FlowVersionId &&
                version.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                !version.IsDeleted)
            .SingleAsync(ct);
        var contributionSelection =
            DynamicFlowContributionPolicyContract.ResolveLockSelection(
                DynamicFlowContributionPolicyContract.Include,
                acknowledgeWarning: true);
        DynamicFlowContributionPolicyContract.ApplyLockedPolicy(
            lockedFlowVersion,
            contributionSelection);
        DynamicFlowContributionPolicyContract.ValidateLockedPolicy(
            lockedFlowVersion);
        var contributionUpdate = await flowVersions.UpdateOneAsync(
            version =>
                version.Id == fixture.FlowVersionId &&
                version.Status == DynamicFlowTemplateVersionStatuses.Locked &&
                !version.IsDeleted,
            Builders<DynamicFlowTemplateVersion>.Update
                .Set(
                    version => version.ContributionPolicy,
                    lockedFlowVersion.ContributionPolicy)
                .Set(
                    version => version.ContributionPolicyHash,
                    lockedFlowVersion.ContributionPolicyHash)
                .Set(
                    version => version.ContributionWarning,
                    lockedFlowVersion.ContributionWarning),
            cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            contributionUpdate.MatchedCount,
            "Locked Flow contribution policy fixture match");
        HarnessAssert.Equal(
            1L,
            contributionUpdate.ModifiedCount,
            "Locked Flow contribution policy fixture write");
        var persistedFlowVersion = await flowVersions
            .Find(version => version.Id == fixture.FlowVersionId)
            .SingleAsync(ct);
        DynamicFlowContributionPolicyContract.ValidateLockedPolicy(
            persistedFlowVersion);
        HarnessAssert.Equal(
            DynamicFlowContributionPolicyContract.Include,
            persistedFlowVersion.ContributionPolicy,
            "Locked Flow contribution policy fixture");
        HarnessAssert.Equal(
            DynamicFlowContributionPolicyContract.IncludeWarning,
            persistedFlowVersion.ContributionWarning,
            "Locked Flow contribution warning fixture");

        var assignment = await database.GetCollection<BsonDocument>("work_assignments")
            .Find(new BsonDocument("_id", ObjectId.Parse(fixture.AssignmentId)))
            .SingleAsync(ct);
        assignment["createdByUserId"] = ObjectId.Parse(executor.Id);
        assignment["issuedByUnitId"] = ObjectId.Parse(admin.UnitId);
        assignment["dynamicFormSchemaHash"] = publishedSchema.Sha256;
        assignment["targetUnitIds"] = new BsonArray
        {
            ObjectId.Parse(fixture.UnitAId)
        };
        assignment["assignees"] = new BsonArray
        {
            new BsonDocument
            {
                ["userId"] = ObjectId.Parse(executor.Id),
                ["unitId"] = ObjectId.Parse(fixture.UnitAId)
            },
            new BsonDocument
            {
                ["userId"] = ObjectId.Parse(executor2.Id),
                ["unitId"] = ObjectId.Parse(fixture.UnitAId)
            }
        };
        await database.GetCollection<BsonDocument>("work_assignments")
            .ReplaceOneAsync(
                new BsonDocument("_id", assignment["_id"]),
                assignment,
                cancellationToken: ct);
        var siblingAssignmentProvenance = await database
            .GetCollection<BsonDocument>("work_assignments")
            .UpdateOneAsync(
                new BsonDocument(
                    "_id",
                    ObjectId.Parse(fixture.SiblingAssignmentId)),
                Builders<BsonDocument>.Update.Set(
                    "dynamicFormSchemaHash",
                    publishedSchema.Sha256),
                cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            siblingAssignmentProvenance.MatchedCount,
            "Lifecycle sibling assignment provenance fixture match");
        HarnessAssert.Equal(
            1L,
            siblingAssignmentProvenance.ModifiedCount,
            "Lifecycle sibling assignment provenance fixture write");

        var docRoleNow = DateTime.UtcNow;
        var assignmentDocRoles = new[]
        {
            (Actor: executor, Role: 10),
            (Actor: admin, Role: 11)
        }.Select(seed => new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["docType"] = 2,
            ["docId"] = assignment["_id"],
            ["userId"] = ObjectId.Parse(seed.Actor.Id),
            ["roles"] = new BsonArray { seed.Role },
            ["workId"] = assignment["workId"],
            ["assignmentId"] = assignment["_id"],
            ["rootAssignmentId"] = assignment["_id"],
            ["path"] = assignment.GetValue("path", fixture.AssignmentId),
            ["flowInstanceId"] = assignment["flowInstanceId"],
            ["visibleUnitIds"] = new BsonArray
            {
                ObjectId.Parse(admin.UnitId),
                ObjectId.Parse(fixture.UnitAId)
            },
            ["isDeleted"] = false,
            ["createdAtUtc"] = docRoleNow,
            ["updatedAtUtc"] = docRoleNow,
            ["createdByUserId"] = ObjectId.Parse(admin.Id),
            ["updatedByUserId"] = ObjectId.Parse(admin.Id)
        });
        await database.GetCollection<BsonDocument>(
                "assignment_list_doc_roles")
            .InsertManyAsync(assignmentDocRoles, cancellationToken: ct);

        await database.GetCollection<BsonDocument>("work_report_periods")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(fixture.ReportPeriodId)),
                Builders<BsonDocument>.Update
                    .Set("currentReportId", reportId)
                    .Set("assigneeUserId", ObjectId.Parse(executor.Id))
                    .Set("assigneeUnitId", ObjectId.Parse(fixture.UnitAId))
                    .Set("isActive", true),
                cancellationToken: ct);

        var reports = database.GetCollection<BsonDocument>(
            "work_assignment_report");
        var reportUpdate = Builders<BsonDocument>.Update
            .Set("status", 0)
            .Set("payloadRevision", fixture.SourceRevision)
            .Set("payloadHash", fixture.SourceHash)
            .Set("payloadStatus", WorkReportPayloadStatus.Ready)
            .Set("dynamicFormSchemaHash", publishedSchema.Sha256)
            .Set("lifecycleRevision", 1)
            .Set("isCurrent", true)
            .Set("isActive", true)
            .Set("isDeleted", false)
            .Set("cumulativeContributionMode", "INCLUDE")
            .Set("lifecycleProjectionOutbox", new BsonArray())
            .Set("lifecycleProjectionLastCompletedRevision", 0)
            .Set("lifecycleProjectionClaimToken", BsonNull.Value)
            .Set("lifecycleProjectionClaimedAtUtc", BsonNull.Value)
            .Set("lifecycleProjectionClaimExpiresAtUtc", BsonNull.Value)
            .Set("lifecycleProjectionLastError", BsonNull.Value)
            .Unset("submittedAtUtc")
            .Unset("submittedByUserId")
            .Unset("approvedAtUtc")
            .Unset("approvedByUserId")
            .Unset("lastLifecycleCommandId")
            .Unset("lastLifecycleCommandHash")
            .Unset("lastLifecycleCommandOperation")
            .Unset("lastLifecycleCommandRevision")
            .Unset("lastLifecycleCommandPayloadRevision")
            .Unset("lastLifecycleCommandStatus")
            .Unset("lastLifecycleCommandIsActive");
        await reports.UpdateOneAsync(
            new BsonDocument("_id", reportId),
            reportUpdate,
            cancellationToken: ct);
        var pairedReportProvenance = await reports.UpdateOneAsync(
            new BsonDocument(
                "_id",
                ObjectId.Parse(fixture.PairedReportId)),
            Builders<BsonDocument>.Update.Set(
                "dynamicFormSchemaHash",
                publishedSchema.Sha256),
            cancellationToken: ct);
        HarnessAssert.Equal(
            1L,
            pairedReportProvenance.MatchedCount,
            "Lifecycle paired report provenance fixture match");
        HarnessAssert.Equal(
            1L,
            pairedReportProvenance.ModifiedCount,
            "Lifecycle paired report provenance fixture write");

        var seededReport = await reports
            .Find(new BsonDocument("_id", reportId))
            .SingleAsync(ct);
        var payloadId = ObjectId.GenerateNewId().ToString();
        await database.GetCollection<WorkReportPayload>("work_report_payloads")
            .InsertOneAsync(
                new WorkReportPayload
                {
                    Id = payloadId,
                    ReportId = fixture.ReportId,
                    PayloadRevision = fixture.SourceRevision,
                    Values1DJson = fixture.SourcePayloadJson,
                    FieldValuesJson = fixture.SourcePayloadJson,
                    TableValuesRootJson = "[]",
                    SummarySourceJson = "{}",
                    PayloadHash = fixture.SourceHash,
                    PayloadSizeBytes = seededReport
                        .GetValue("payloadSizeBytes", 0L)
                        .ToInt64(),
                    Status = WorkReportPayloadStatus.Ready,
                    CreatedAtUtc = docRoleNow,
                    UpdatedAtUtc = docRoleNow,
                    CreatedByUserId = executor.Id,
                    UpdatedByUserId = executor.Id,
                    IsDeleted = false
                },
                cancellationToken: ct);
        _cleanupHandles.Add(new P9CleanupHandle(
            "work_report_payloads",
            payloadId));

        await database.GetCollection<BsonDocument>("dynamic_flow_step_instances")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(fixture.FlowStepInstanceId)),
                Builders<BsonDocument>.Update
                    .Set("state", "ASSIGNED")
                    .Set("reportLifecycleRevision", 1)
                    .Set("reportLifecycleStatus", "DRAFT")
                    .Set("reportLifecycleIsActive", true)
                    .Set("reportId", reportId)
                    .Set("formSchemaHash", publishedSchema.Sha256)
                    .Set("formSnapshotHash", publishedSchema.Sha256)
                    .Set("isCanonicalEpoch", true)
                    .Set("invalidatedByFlowEventId", BsonNull.Value)
                    .Set("supersededByStepInstanceId", BsonNull.Value),
                cancellationToken: ct);

        await PrepareLifecycleLockedConfigAsync(fixture, admin, ct);
        fixture = Fixture();

        _lfcSaveRequest = new JsonObject
        {
            ["expectedPayloadRevision"] = fixture.SourceRevision,
            ["commandId"] = "p9-lfc-draft-save-001",
            ["values1D"] = new JsonArray { 1250 },
            ["fieldValuesJson"] = new JsonObject
            {
                ["amount"] = 1250,
                ["approved"] = true
            }.ToJsonString(),
            ["tableValuesJson"] = LifecycleTableValuesJson,
            ["dataOrigin"] = "MANUAL",
            ["cumulativeContributionMode"] = "INCLUDE",
            ["summarySourceJson"] = "{}",
            ["note"] = "P9-LFC autonomous draft"
        };

        _lfcInitialDirect = await CaptureLifecycleDirectSnapshotAsync(ct);
        AddLifecycleMilestone("PREPARED_DRAFT", _lfcInitialDirect);
        AssertLifecycleDirectCounts(
            _lfcInitialDirect,
            expectedDirectRows: 0,
            expectedJobRows: 0,
            "prepared draft");
    }

    private async Task PrepareLifecycleLockedConfigAsync(
        P9Fixture fixture,
        P9Actor admin,
        CancellationToken ct)
    {
        const string emptyConfigHash =
            "74234e98afe7498fb5daf1f36ac2d78acc339464f950703b8c019892f982b90b";
        var label = await RequireApi().PostAsync(
            "api/labels/config",
            new JsonObject
            {
                ["commandId"] = "p9-lfc-label-config-001",
                ["expectedRevision"] = 0,
                ["expectedConfigHash"] = emptyConfigHash,
                ["payload"] = new JsonObject
                {
                    ["code"] = LifecycleLabelCode,
                    ["name"] = "P9 lifecycle label",
                    ["description"] = "P9 lifecycle row-label target",
                    ["color"] = "#336699",
                    ["groupCode"] = "p9",
                    ["usage"] = "TABLE_TARGET",
                    ["dataType"] = "NUMBER",
                    ["valueSourceType"] = "NONE",
                    ["valueOptions"] = new JsonArray(),
                    ["valueSourceCatalogId"] = null,
                    ["scopeType"] = "GLOBAL",
                    ["scopeId"] = null,
                    ["isActive"] = true
                }
            },
            admin.Token,
            ct: ct);
        ExpectSuccess(label, "P9-LFC label config");

        var initial = await RequireApi().GetAsync(
            $"api/dynamic-forms/{fixture.TemplateId}/statistics",
            admin.Token,
            ct: ct);
        ExpectSuccess(initial, "P9-LFC initial statistic config");
        var initialConfig = ApiHarnessClient.RequiredObject(
            initial.Json,
            "P9-LFC initial statistic config");
        var persisted = await RequireApi().PatchAsync(
            $"api/dynamic-forms/{fixture.TemplateId}/statistics",
            new JsonObject
            {
                ["commandId"] = "p9-lfc-field-config-001",
                ["expectedRevision"] = RequiredLong(initialConfig, "revision"),
                ["expectedConfigHash"] = RequiredString(initialConfig, "configHash"),
                ["payload"] = new JsonObject
                {
                    ["fields"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["fieldId"] = "field_amount",
                            ["isStatistic"] = true,
                            ["statisticLabelCodes"] = new JsonArray(),
                            ["statistic"] = new JsonObject
                            {
                                ["aggregateOps"] = new JsonArray { "SUM" },
                                ["bucketMode"] = "NONE",
                                ["showInDetail"] = true,
                                ["showInTree"] = true
                            }
                        },
                        new JsonObject
                        {
                            ["fieldId"] = "field_approved",
                            ["isStatistic"] = true,
                            ["statisticLabelCodes"] = new JsonArray(),
                            ["statistic"] = new JsonObject
                            {
                                ["aggregateOps"] = new JsonArray { "COUNT" },
                                ["bucketMode"] = "NONE",
                                ["showInDetail"] = true,
                                ["showInTree"] = true
                            }
                        }
                    }
                }
            },
            admin.Token,
            ct: ct);
        ExpectSuccess(persisted, "P9-LFC persist locked statistic config");
        var config = ApiHarnessClient.RequiredObject(
            persisted.Json,
            "P9-LFC persisted statistic config");
        HarnessAssert.Equal(
            "LOCKED",
            RequiredString(config, "status"),
            "P9-LFC persisted statistic config status");

        var owner = await RequireDatabase()
            .GetCollection<BsonDocument>("dynamic_form_templates")
            .Find(new BsonDocument("_id", ObjectId.Parse(fixture.TemplateId)))
            .SingleAsync(ct);
        var sections = owner.GetValue(
            "statisticConfigSections",
            new BsonDocument()).AsBsonDocument;
        var fieldConfig = JsonNode.Parse(
            sections.GetValue("fieldSectionJson", "[]").AsString)
            ?? new JsonArray();
        var tableConfig = JsonNode.Parse(
            sections.GetValue("tableSectionJson", "[]").AsString)
            ?? new JsonArray();
        var dependencyPins = owner.GetValue(
                "statisticConfigDependencyPins",
                new BsonArray())
            .AsBsonArray
            .Select(pin => pin.AsString)
            .ToArray();
        var configPayload = new JsonObject
        {
            ["ownerKind"] = "DYNAMIC_FORM",
            ["ownerId"] = fixture.TemplateId,
            ["fieldConfig"] = fieldConfig,
            ["tableConfig"] = tableConfig,
            ["dependencyPins"] = new JsonArray(
                dependencyPins
                    .Select(pin => JsonValue.Create(pin))
                    .ToArray())
        };
        var configHash = CanonicalJsonSha256(configPayload);
        HarnessAssert.Equal(
            RequiredString(config, "configHash"),
            configHash,
            "P9-LFC trusted config hash recompute");
        HarnessAssert.Equal(
            owner.GetValue("statisticConfigHash", string.Empty).AsString,
            configHash,
            "P9-LFC persisted owner config hash");

        _fixture = fixture with
        {
            ConfigId = RequiredString(config, "configId"),
            ConfigVersionId = RequiredString(config, "versionId"),
            ConfigVersionNo = ApiHarnessClient.RequiredInt(config, "versionNo"),
            ConfigRevision = RequiredLong(config, "revision"),
            ConfigHash = configHash,
            ConfigPayloadJson = configPayload.ToJsonString()
        };
    }

    private async Task<BsonDocument> LoadLifecycleReportAsync(
        CancellationToken ct)
        => await RequireDatabase()
            .GetCollection<BsonDocument>("work_assignment_report")
            .Find(new BsonDocument("_id", ObjectId.Parse(Fixture().ReportId)))
            .SingleAsync(ct);

    private static BsonDocument FindOutboxEntry(
        BsonDocument report,
        string operation)
    {
        var entries = report.GetValue(
            "lifecycleProjectionOutbox",
            new BsonArray()).AsBsonArray;
        return entries
            .Select(value => value.AsBsonDocument)
            .Single(entry => string.Equals(
                entry.GetValue("operation", string.Empty).AsString,
                operation,
                StringComparison.Ordinal));
    }

    private static void ExpectSuccess(
        ApiHarnessResponse response,
        string context)
    {
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted)
            return;
        throw new InvalidOperationException(
            $"{context} expected 200/202; actual={(int)response.StatusCode}; body={response.Body}");
    }
}
