using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P9StatRunCoreProbe
{
    private async Task PrepareDiffFixtureAsync(CancellationToken ct)
    {
        var fixture = Fixture();
        const string sectionId = "p9-diff-main";
        var sectionsJson = new JsonArray(new JsonObject
        {
            ["id"] = sectionId,
            ["title"] = "P9 Diff",
            ["description"] = null,
            ["tagCodes"] = new JsonArray(),
            ["order"] = 0
        }).ToJsonString();
        var fieldsJson = new JsonArray(new JsonObject
        {
            ["id"] = DiffFieldId,
            ["sectionId"] = sectionId,
            ["key"] = DiffFieldId,
            ["name"] = "P9 Diff Field",
            ["type"] = "number",
            ["required"] = false,
            ["order"] = 0,
            ["statisticLabelCodes"] = new JsonArray(),
            ["isStatistic"] = false
        }).ToJsonString();
        var blocksJson = new JsonArray(BuildDiffBlockNode(sectionId)).ToJsonString();
        var published = DynamicFormPublishedSchemaSnapshotBuilder.Build(
            1, sectionsJson, fieldsJson, blocksJson);
        var templates = RequireDatabase().GetCollection<BsonDocument>(
            "dynamic_form_templates");
        await templates.UpdateOneAsync(
            new BsonDocument("_id", ObjectId.Parse(fixture.TemplateId)),
            Builders<BsonDocument>.Update
                .Set("sectionsJson", sectionsJson)
                .Set("fieldsJson", fieldsJson)
                .Set("blocksJson", blocksJson)
                .Set("excelBlockJson", BsonNull.Value)
                .Set("publishedSchemaSnapshotJson", published.Json)
                .Set("publishedSchemaHash", published.Sha256)
                .Set("isPublished", true)
                .Set("revision", 2)
                .Unset("statisticConfigId")
                .Unset("statisticConfigVersionId")
                .Unset("statisticConfigPreviousVersionId")
                .Unset("statisticConfigVersionNo")
                .Unset("statisticConfigRevision")
                .Unset("statisticConfigStatus")
                .Unset("statisticConfigHash")
                .Unset("statisticConfigDependencyPins")
                .Unset("statisticConfigSections")
                .Unset("statisticConfigSnapshots"),
            cancellationToken: ct);
        await RequireDatabase().GetCollection<BsonDocument>("work_assignments")
            .UpdateOneAsync(
                new BsonDocument("_id", ObjectId.Parse(fixture.AssignmentId)),
                Builders<BsonDocument>.Update
                    .Set("assignmentType", "ONCE")
                    .Set("dynamicFormSchemaHash", published.Sha256),
                cancellationToken: ct);

        var fieldLabel = await CreateDiffLabelAsync(
            "p9-dif-label-field",
            "p9.dif.field",
            "P9 Diff field",
            "STATISTIC",
            ct);
        var metricLabel = await CreateDiffLabelAsync(
            "p9-dif-label-metric",
            "p9.dif.metric",
            "P9 Diff metric",
            "TABLE_TARGET",
            ct);
        var rowLabel = await CreateDiffLabelAsync(
            "p9-dif-label-row",
            "p9.dif.row",
            "P9 Diff row",
            "TABLE_TARGET",
            ct);
        await ConfigureDiffTemplateStatisticsAsync(
            fieldLabel.Code,
            metricLabel.Code,
            rowLabel.Code,
            ct);
        await CreateLockedDiffConfigsAsync(
            fieldLabel.Code,
            metricLabel.Code,
            rowLabel.Code,
            ct);
        await SeedDiffProjectionSourcesAsync(
            fieldLabel.Code,
            metricLabel.Code,
            rowLabel.Code,
            ct);
        await WriteStrictJsonAsync(
            Path.Combine(_paths.RunRoot, "P9-DIF.fixture.json"),
            new
            {
                schemaVersion = "P9_DIF_FIXTURE_V1",
                chainId = ChainId,
                promptId = DiffPromptId,
                fixture.AssignmentId,
                fixture.TemplateId,
                publishedSchemaHash = published.Sha256,
                selectors = new
                {
                    field = new { key = DiffFieldId, code = fieldLabel.Code },
                    tableMetric = new
                    {
                        key = $"{DiffBlockId}:{DiffMetricKey}",
                        code = metricLabel.Code
                    },
                    rowLabel = new
                    {
                        key = $"{DiffBlockId}:{rowLabel.Code}",
                        code = rowLabel.Code
                    }
                },
                configs = _difConfigs,
                periods = new[] { "2026-08", "2026-07" },
                autonomous = true
            },
            ct);
    }

    private static JsonObject BuildDiffBlockNode(string sectionId)
        => new()
        {
            ["blockId"] = DiffBlockId,
            ["sectionId"] = sectionId,
            ["name"] = "P9 Diff Table",
            ["tableMode"] = "APPEND_ROWS",
            ["rowLabelDataType"] = "NUMBER",
            ["w"] = 1,
            ["h"] = 1,
            ["dataRect"] = new JsonObject
            {
                ["r0"] = 0, ["c0"] = 0, ["r1"] = 0, ["c1"] = 0
            },
            ["statisticsInputCellCount"] = 1,
            ["statisticsInputCellLimit"] = 250,
            ["statisticsDisabled"] = false,
            ["indexMap"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["rowKey"] = "row:00",
                ["columnKey"] = "column:00",
                ["metricKey"] = DiffMetricKey,
                ["dataType"] = "NUMBER"
            }),
            ["metricRules"] = new JsonArray(new JsonObject
            {
                ["metricKey"] = DiffMetricKey,
                ["dataType"] = "NUMBER",
                ["sourceType"] = "COLUMN",
                ["rowKey"] = "row:00",
                ["columnKey"] = "column:00",
                ["aggregateOps"] = new JsonArray("COUNT", "SUM")
            }),
            ["metricLabelTargets"] = new JsonArray(),
            ["allowedRowLabelCodes"] = new JsonArray()
        };

    private async Task<P9DiffLabelPin> CreateDiffLabelAsync(
        string commandId,
        string code,
        string name,
        string usage,
        CancellationToken ct)
    {
        var response = await RequireApi().PostAsync(
            "api/labels/config",
            DiffEnvelope(
                commandId,
                0,
                StatConfigCanonicalJson.EmptyConfigHash,
                new JsonObject
                {
                    ["code"] = code,
                    ["name"] = name,
                    ["description"] = null,
                    ["color"] = "#336699",
                    ["groupCode"] = "p9-dif",
                    ["usage"] = usage,
                    ["dataType"] = "NUMBER",
                    ["valueSourceType"] = "NONE",
                    ["valueOptions"] = new JsonArray(),
                    ["valueSourceCatalogId"] = null,
                    ["scopeType"] = "GLOBAL",
                    ["scopeId"] = null,
                    ["isActive"] = true
                }),
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            response, HttpStatusCode.OK, $"P9-DIF create label {code}");
        var root = ApiHarnessClient.RequiredObject(response.Json, "P9-DIF label");
        var label = ApiHarnessClient.RequiredObject(root["label"], "P9-DIF label row");
        return new P9DiffLabelPin(
            ApiHarnessClient.RequiredString(label, "id"),
            ApiHarnessClient.RequiredString(label, "code"),
            ApiHarnessClient.RequiredString(root, "versionId"),
            ApiHarnessClient.RequiredString(root, "configHash"));
    }

    private async Task ConfigureDiffTemplateStatisticsAsync(
        string fieldLabelCode,
        string metricLabelCode,
        string rowLabelCode,
        CancellationToken ct)
    {
        var route = $"api/dynamic-forms/{Fixture().TemplateId}/statistics";
        var current = await ReadDiffIdentityRootAsync(route, Actor("admin").Token, ct);
        var fields = new JsonObject
        {
            ["fields"] = new JsonArray(new JsonObject
            {
                ["fieldId"] = DiffFieldId,
                ["isStatistic"] = true,
                ["statisticLabelCodes"] = new JsonArray(fieldLabelCode),
                ["statistic"] = new JsonObject
                {
                    ["aggregateOps"] = new JsonArray("COUNT", "SUM"),
                    ["bucketMode"] = "NONE",
                    ["showInDetail"] = true,
                    ["showInTree"] = true
                }
            })
        };
        var fieldResponse = await RequireApi().PatchAsync(
            route,
            DiffEnvelope(
                "p9-dif-template-field",
                DiffLong(current, "revision"),
                ApiHarnessClient.RequiredString(current, "configHash"),
                fields),
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            fieldResponse, HttpStatusCode.OK, "P9-DIF field statistic config");
        var afterField = ApiHarnessClient.RequiredObject(
            fieldResponse.Json, "P9-DIF field statistic response");
        var tables = new JsonObject
        {
            ["tables"] = new JsonArray(new JsonObject
            {
                ["blockId"] = DiffBlockId,
                ["tableMode"] = "APPEND_ROWS",
                ["statisticsDisabled"] = false,
                ["metrics"] = new JsonArray(new JsonObject
                {
                    ["metricKey"] = DiffMetricKey,
                    ["dataType"] = "NUMBER",
                    ["aggregateOps"] = new JsonArray("COUNT", "SUM")
                }),
                ["metricLabelTargets"] = new JsonArray(new JsonObject
                {
                    ["metricKey"] = DiffMetricKey,
                    ["statisticLabelCode"] = metricLabelCode
                }),
                ["allowedRowLabelCodes"] = new JsonArray(rowLabelCode)
            })
        };
        var tableResponse = await RequireApi().PatchAsync(
            route,
            DiffEnvelope(
                "p9-dif-template-table",
                DiffLong(afterField, "revision"),
                ApiHarnessClient.RequiredString(afterField, "configHash"),
                tables),
            Actor("admin").Token,
            ct: ct);
        ApiHarnessClient.ExpectStatus(
            tableResponse, HttpStatusCode.OK, "P9-DIF table statistic config");
    }

    private async Task CreateLockedDiffConfigsAsync(
        string fieldCode,
        string metricCode,
        string rowCode,
        CancellationToken ct)
    {
        var selectors = new[]
        {
            (Kind: "FIELD", Key: DiffFieldId, Code: fieldCode),
            (Kind: "TABLE_METRIC", Key: $"{DiffBlockId}:{DiffMetricKey}", Code: metricCode),
            (Kind: "ROW_LABEL", Key: $"{DiffBlockId}:{rowCode}", Code: rowCode)
        };
        var route = DiffConfigRoute();
        JsonObject current = await ReadDiffIdentityRootAsync(
            route, Actor("admin").Token, ct);
        for (var index = 0; index < selectors.Length; index++)
        {
            if (index > 0)
            {
                var next = await RequireApi().PostAsync(
                    $"{route}/next-draft",
                    DiffEnvelope(
                        $"p9-dif-next-{index + 1}",
                        DiffLong(current, "revision"),
                        ApiHarnessClient.RequiredString(current, "configHash"),
                        new JsonObject()),
                    Actor("admin").Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    next, HttpStatusCode.OK, $"P9-DIF next draft {index + 1}");
                current = ApiHarnessClient.RequiredObject(
                    ApiHarnessClient.RequiredObject(next.Json, "next draft")["identity"],
                    "next draft identity");
            }
            var selector = selectors[index];
            var put = await RequireApi().PutAsync(
                route,
                DiffEnvelope(
                    $"p9-dif-put-{index + 1}",
                    DiffLong(current, "revision"),
                    ApiHarnessClient.RequiredString(current, "configHash"),
                    BuildDiffConfigPayload(
                        selector.Kind, selector.Key, selector.Code)),
                Actor("admin").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                put, HttpStatusCode.OK, $"P9-DIF put config {index + 1}");
            var putRoot = ApiHarnessClient.RequiredObject(put.Json, "P9-DIF put");
            current = ApiHarnessClient.RequiredObject(
                putRoot["identity"], "P9-DIF put identity");
            var locked = await RequireApi().PostAsync(
                $"{route}/lock",
                DiffEnvelope(
                    $"p9-dif-lock-{index + 1}",
                    DiffLong(current, "revision"),
                    ApiHarnessClient.RequiredString(current, "configHash"),
                    new JsonObject()),
                Actor("admin").Token,
                ct: ct);
            ApiHarnessClient.ExpectStatus(
                locked, HttpStatusCode.OK, $"P9-DIF lock config {index + 1}");
            var lockedRoot = ApiHarnessClient.RequiredObject(
                locked.Json, "P9-DIF locked config");
            current = ApiHarnessClient.RequiredObject(
                lockedRoot["identity"], "P9-DIF locked identity");
            var pin = new P9DiffConfigPin(
                ApiHarnessClient.RequiredString(current, "configId"),
                ApiHarnessClient.RequiredString(current, "versionId"),
                DiffInt(current, "versionNo"),
                DiffLong(current, "revision"),
                ApiHarnessClient.RequiredString(current, "configHash"),
                ApiHarnessClient.RequiredString(current, "status"),
                selector.Kind,
                selector.Key,
                selector.Code);
            HarnessAssert.Equal("LOCKED", pin.Status, "P9-DIF config not locked");
            _difConfigs.Add(pin);
        }
    }

    private static JsonObject BuildDiffConfigPayload(
        string kind,
        string key,
        string code)
    {
        JsonObject Side(string period) => new()
        {
            ["selector"] = new JsonObject
            {
                ["conceptKind"] = kind,
                ["conceptKey"] = key,
                ["conceptCode"] = code,
                ["dataType"] = "NUMBER"
            },
            ["period"] = new JsonObject
            {
                ["mode"] = "EXACT",
                ["periodKey"] = period
            },
            ["sourceScope"] = new JsonObject
            {
                ["mode"] = "SELF",
                ["flowInstanceId"] = null,
                ["flowStepId"] = null,
                ["flowBranchId"] = null,
                ["flowEffectiveStatus"] = null
            }
        };
        return new JsonObject
        {
            ["name"] = $"P9 {kind} diff",
            ["left"] = Side("2026-08"),
            ["right"] = Side("2026-07"),
            ["direction"] = "LEFT_TO_RIGHT",
            ["missingPolicy"] = "INCLUDE",
            ["emptyPolicy"] = "INCLUDE"
        };
    }

    private async Task SeedDiffProjectionSourcesAsync(
        string fieldCode,
        string metricCode,
        string rowCode,
        CancellationToken ct)
    {
        var fixture = Fixture();
        var database = RequireDatabase();
        var reports = database.GetCollection<BsonDocument>("work_assignment_report");
        var augustReport = await reports.Find(
                new BsonDocument("_id", ObjectId.Parse(fixture.PairedReportId)))
            .SingleAsync(ct);
        var julyReportId = ObjectId.GenerateNewId();
        var julyPeriodId = ObjectId.GenerateNewId();
        var julyHash = DiffSha256("P9-DIF-JULY-SOURCE");
        var julyReport = (BsonDocument)augustReport.DeepClone();
        julyReport["_id"] = julyReportId;
        julyReport["workReportPeriodId"] = julyPeriodId;
        julyReport["periodKey"] = "2026-07";
        julyReport["periodInstanceKey"] = "MONTH:2026-07:P9-DIF";
        julyReport["periodStart"] = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        julyReport["periodEnd"] = new DateTime(2026, 7, 31, 23, 59, 59, DateTimeKind.Utc);
        julyReport["payloadRevision"] = 1;
        julyReport["payloadHash"] = julyHash;
        julyReport["lifecycleRevision"] = 4;
        julyReport["status"] = 2;
        julyReport["isCurrent"] = true;
        julyReport["isActive"] = true;
        julyReport["isDeleted"] = false;
        await reports.InsertOneAsync(julyReport, cancellationToken: ct);

        var augustRunId = ObjectId.GenerateNewId();
        var julyRunId = ObjectId.GenerateNewId();
        const string augustGeneration = "p9-dif-generation-2026-08";
        const string julyGeneration = "p9-dif-generation-2026-07";
        await database.GetCollection<BsonDocument>(
                "work_report_statistic_rebuild_jobs")
            .InsertManyAsync(
            [
                DiffDirectJob(augustRunId, augustGeneration, "aug"),
                DiffDirectJob(julyRunId, julyGeneration, "jul")
            ], cancellationToken: ct);

        var augustPin = DiffDirectPin(
            augustRunId,
            augustGeneration,
            ObjectId.Parse(fixture.PairedReportId),
            augustReport["payloadRevision"].ToInt32(),
            augustReport["payloadHash"].AsString,
            augustReport["lifecycleRevision"].ToInt32());
        var julyPin = DiffDirectPin(
            julyRunId, julyGeneration, julyReportId, 1, julyHash, 4);
        await database.GetCollection<BsonDocument>("work_report_field_stat_values")
            .InsertManyAsync(
            [
                DiffProjectionBase(fixture, "2026-08", augustReport,
                    ObjectId.Parse(fixture.PairedReportId), augustPin)
                    .Add("fieldId", DiffFieldId)
                    .Add("conceptCode", fieldCode)
                    .Add("sourceKey", "field:aug")
                    .Add("valueKind", "NUMBER")
                    .Add("numericValue", 10m),
                DiffProjectionBase(fixture, "2026-07", julyReport,
                    julyReportId, julyPin)
                    .Add("fieldId", DiffFieldId)
                    .Add("conceptCode", fieldCode)
                    .Add("sourceKey", "field:jul")
                    .Add("valueKind", "NUMBER")
                    .Add("numericValue", 7m)
            ], cancellationToken: ct);
        var tableFacts = new List<BsonDocument>();
        tableFacts.Add(DiffTableProjection(
            fixture, augustReport, ObjectId.Parse(fixture.PairedReportId),
            augustPin, "2026-08", metricCode, "row-a", 10m));
        tableFacts.Add(DiffTableProjection(
            fixture, augustReport, ObjectId.Parse(fixture.PairedReportId),
            augustPin, "2026-08", metricCode, "row-b", 20m));
        tableFacts.Add(DiffTableProjection(
            fixture, julyReport, julyReportId,
            julyPin, "2026-07", metricCode, "row-a", 7m));
        tableFacts.Add(DiffTableProjection(
            fixture, julyReport, julyReportId,
            julyPin, "2026-07", metricCode, "row-b", 20m));
        await database.GetCollection<BsonDocument>("work_report_table_stat_values")
            .InsertManyAsync(tableFacts, cancellationToken: ct);
        await database.GetCollection<BsonDocument>("work_report_label_stat_values")
            .InsertManyAsync(
            [
                DiffLabelProjection(fixture, augustReport,
                    ObjectId.Parse(fixture.PairedReportId), augustPin,
                    "2026-08", rowCode, "row-a", 0),
                DiffLabelProjection(fixture, augustReport,
                    ObjectId.Parse(fixture.PairedReportId), augustPin,
                    "2026-08", rowCode, "row-b", 1),
                DiffLabelProjection(fixture, julyReport, julyReportId,
                    julyPin, "2026-07", rowCode, "row-a", 0)
            ], cancellationToken: ct);
    }

    private static BsonDocument DiffDirectJob(
        ObjectId id,
        string generationId,
        string suffix)
        => new()
        {
            ["_id"] = id,
            ["dedupeKey"] = $"p9-dif-{suffix}-{id}",
            ["status"] = "COMPLETED",
            ["isCurrentPublication"] = true,
            ["generationId"] = generationId,
            ["generationHash"] = DiffSha256(generationId),
            ["isActive"] = false,
            ["isDeleted"] = false,
            ["createdAtUtc"] = DateTime.UtcNow,
            ["updatedAtUtc"] = DateTime.UtcNow
        };

    private static BsonDocument DiffDirectPin(
        ObjectId runId,
        string generationId,
        ObjectId reportId,
        int payloadRevision,
        string payloadHash,
        int lifecycleRevision)
        => new()
        {
            ["runId"] = runId,
            ["generationId"] = generationId,
            ["sourceReportId"] = reportId,
            ["sourcePayloadRevision"] = payloadRevision,
            ["sourcePayloadHash"] = payloadHash,
            ["sourceLifecycleRevision"] = lifecycleRevision
        };

    private static BsonDocument DiffProjectionBase(
        P9Fixture fixture,
        string periodKey,
        BsonDocument report,
        ObjectId reportId,
        BsonDocument pin)
        => new()
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["workId"] = ObjectId.Parse(fixture.WorkId),
            ["workAssignmentId"] = ObjectId.Parse(fixture.AssignmentId),
            ["assignmentIsActive"] = true,
            ["reportIsActive"] = true,
            ["rootAssignmentId"] = ObjectId.Parse(fixture.AssignmentId),
            ["workReportPeriodId"] = report["workReportPeriodId"],
            ["workAssignmentReportId"] = reportId,
            ["dynamicFormTemplateId"] = ObjectId.Parse(fixture.TemplateId),
            ["periodKey"] = periodKey,
            ["periodInstanceKey"] = report["periodInstanceKey"],
            ["periodKind"] = "SCHEDULED",
            ["reportStatus"] = 2,
            ["sourcePayloadRevision"] = report["payloadRevision"],
            ["sourcePayloadHash"] = report["payloadHash"],
            ["directProjection"] = pin,
            ["isDeleted"] = false,
            ["createdAtUtc"] = DateTime.UtcNow,
            ["updatedAtUtc"] = DateTime.UtcNow
        };

    private static BsonDocument DiffTableProjection(
        P9Fixture fixture,
        BsonDocument report,
        ObjectId reportId,
        BsonDocument pin,
        string period,
        string conceptCode,
        string rowKey,
        decimal value)
        => DiffProjectionBase(fixture, period, report, reportId, pin)
            .Add("blockId", DiffBlockId)
            .Add("tableMode", "APPEND_ROWS")
            .Add("metricKey", DiffMetricKey)
            .Add("metricLabelCode", conceptCode)
            .Add("conceptCode", conceptCode)
            .Add("rowKey", rowKey)
            .Add("columnKey", "column:00")
            .Add("sourceKey", $"{period}:{rowKey}")
            .Add("dataType", "NUMBER")
            .Add("valueKind", "NUMBER")
            .Add("value", value)
            .Add("numericValue", value);

    private static BsonDocument DiffLabelProjection(
        P9Fixture fixture,
        BsonDocument report,
        ObjectId reportId,
        BsonDocument pin,
        string period,
        string labelCode,
        string rowKey,
        int rowIndex)
        => DiffProjectionBase(fixture, period, report, reportId, pin)
            .Add("blockId", DiffBlockId)
            .Add("rowKey", rowKey)
            .Add("rowIndex", rowIndex)
            .Add("labelCode", labelCode)
            .Add("source", "ROW_LABEL");

    private string DiffConfigRoute()
        => $"api/work-report-statistic-diffs/assignments/{Fixture().AssignmentId}" +
           $"/templates/{Fixture().TemplateId}/config";

    private async Task<JsonObject> ReadDiffIdentityRootAsync(
        string route,
        string token,
        CancellationToken ct)
    {
        var response = await RequireApi().GetAsync(route, token, ct: ct);
        ApiHarnessClient.ExpectStatus(
            response, HttpStatusCode.OK, $"P9-DIF GET {route}");
        var root = ApiHarnessClient.RequiredObject(response.Json, route);
        return root["identity"] is JsonObject identity ? identity : root;
    }

    private static JsonObject DiffEnvelope(
        string commandId,
        long revision,
        string hash,
        JsonNode payload)
        => new()
        {
            ["commandId"] = commandId,
            ["expectedRevision"] = revision,
            ["expectedConfigHash"] = hash,
            ["payload"] = payload.DeepClone()
        };

    private static int DiffInt(JsonObject root, string name)
        => root[name]?.GetValue<int>()
           ?? throw new InvalidOperationException($"Missing integer {name}.");

    private static long DiffLong(JsonObject root, string name)
        => root[name]?.GetValue<long>()
           ?? throw new InvalidOperationException($"Missing long {name}.");
}

internal sealed record P9DiffLabelPin(
    string Id,
    string Code,
    string VersionId,
    string ConfigHash);
