using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP806BarrierCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-DIF-014",
            "system_admin",
            [
                "p8-dif-014-legacy-post",
                "p8-dif-014-legacy-delete"
            ],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-014/legacy-list",
                    () => _api.GetAsync(
                        "api/work-report-statistic-diffs/configs?workId=" +
                        ObjectId.GenerateNewId(),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT",
                    null,
                    "DIFF_LEGACY_CONFIG_ROUTE_BLOCKED",
                    ct);
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-014/legacy-post",
                    () => _api.PostAsync(
                        "api/work-report-statistic-diffs/configs",
                        new JsonObject
                        {
                            ["id"] = ObjectId.GenerateNewId().ToString(),
                            ["configJson"] = "{\"legacy\":true}"
                        },
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT",
                    null,
                    "DIFF_LEGACY_MUTATION_BLOCKED_USE_CAS_CONFIG_ROUTE",
                    ct);
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-014/legacy-delete",
                    () => _api.DeleteAsync(
                        "api/work-report-statistic-diffs/configs/" +
                        ObjectId.GenerateNewId(),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT",
                    null,
                    "DIFF_LEGACY_DELETE_BLOCKED_USE_CAS_CONFIG_ROUTE",
                    ct);
                return new CaseObservation(
                    "All legacy list/save/delete paths failed closed before lookup and persisted no config, receipt, job, outbox or result.",
                    "legacyList=409+0W;legacyPost=409+0W;legacyDelete=409+0W;useCASRoute=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-DIF-015",
            "system_admin",
            ["p8-dif-015-draft", "p8-dif-015-run"],
            DiffConfigWrites,
            DiffConfigWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = DiffFixture("015");
                var selector = DiffSelector(
                    "FIELD",
                    _diffTableFixture.Form.Fields.Single().Id,
                    _p806FieldLabel.LabelCode);
                var (_, draft) = await PutDiffConfigAsync(
                    actor,
                    fixture,
                    "p8-dif-015-draft",
                    DiffPayload(selector),
                    ct);
                await RequireZeroWriteDiffRejectionAsync(
                    "P8-DIF-015/run",
                    () => _api.PostAsync(
                        "api/work-report-statistic-diffs/run",
                        new JsonObject
                        {
                            ["commandId"] = "p8-dif-015-run",
                            ["configId"] = draft.ConfigId,
                            ["assignmentId"] = fixture.Assignment.Id,
                            ["dynamicFormTemplateId"] =
                                fixture.DynamicFormTemplateId,
                            ["periodKey"] = "2026-08"
                        },
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.Conflict,
                    "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                    null,
                    "P8_CONFIG_ONLY",
                    ct,
                    expectedTargetPhase: "P9");
                return new CaseObservation(
                    "A persisted valid Diff DRAFT could not execute; /run returned the P9 phase barrier before data/result lookup and wrote nothing.",
                    "draft=config+receipt;run=409+0W;target=P9;reason=P8_CONFIG_ONLY;diffResult=0;job=0");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-DIF-016",
            "system_admin",
            ["p8-dif-016-run-unknown", "p8-dif-016-run-empty"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                foreach (var probe in new[]
                         {
                             (
                                 Id: "unknown",
                                 Command: "p8-dif-016-run-unknown",
                                 Body: new JsonObject
                                 {
                                     ["configId"] =
                                         ObjectId.GenerateNewId().ToString(),
                                     ["assignmentId"] =
                                         ObjectId.GenerateNewId().ToString(),
                                     ["periodKey"] = "2026-08",
                                     ["forceRefresh"] = true
                                 }),
                             (
                                 Id: "empty",
                                 Command: "p8-dif-016-run-empty",
                                 Body: new JsonObject())
                         })
                {
                    await RequireZeroWriteDiffRejectionAsync(
                        $"P8-DIF-016/{probe.Id}",
                        () => _api.PostAsync(
                            "api/work-report-statistic-diffs/run",
                            probe.Body,
                            actor.Token,
                            ct: ct),
                        HttpStatusCode.Conflict,
                        "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
                        null,
                        "P8_CONFIG_ONLY",
                        ct,
                        expectedTargetPhase: "P9");
                }
                foreach (var collection in ProhibitedCollections)
                {
                    HarnessAssert.Equal(0L,
                        await _database.GetCollection<BsonDocument>(collection)
                            .CountDocumentsAsync(
                                FilterDefinition<BsonDocument>.Empty,
                                cancellationToken: ct),
                        $"P8 Diff barrier left prohibited rows in {collection}");
                }
                return new CaseObservation(
                    "Run rejection was unconditional for unknown/empty input and every official result/snapshot/hierarchy/job/export/outbox collection stayed empty.",
                    "unknownRun=409+0W;emptyRun=409+0W;allProhibitedCounts=0;noMaterializedRead=true;noResultCache=true");
            },
            ct);
    }
}
