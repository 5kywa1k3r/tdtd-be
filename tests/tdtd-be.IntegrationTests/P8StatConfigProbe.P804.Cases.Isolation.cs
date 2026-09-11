using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP804IsolationCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-BAS-014",
            "system_admin",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = BasicFixture("014");
                var virtualConfig = await ReadBasicConfigAsync(
                    actor,
                    fixture,
                    ct,
                    requirePersisted: false);
                HarnessAssert.Equal(true, virtualConfig.IsVirtualEmpty,
                    "Unconfigured Basic owner did not return virtual empty metadata");
                HarnessAssert.Equal(1, virtualConfig.VersionNo,
                    "Virtual Basic version number mismatch");
                HarnessAssert.Equal(0L, virtualConfig.Revision,
                    "Virtual Basic revision mismatch");
                HarnessAssert.Equal("DRAFT", virtualConfig.Status,
                    "Virtual Basic status mismatch");
                HarnessAssert.Equal(EmptyConfigHash, virtualConfig.ConfigHash,
                    "Virtual Basic hash differs from shared empty hash");
                HarnessAssert.Equal(0, virtualConfig.Versions.Count,
                    "Virtual Basic readback exposed a persisted version snapshot");
                HarnessAssert.Equal(0,
                    (virtualConfig.Payload["targets"] as System.Text.Json.Nodes.JsonArray)
                    ?.Count ?? -1,
                    "Virtual Basic payload is not an explicit empty target set");
                var expectedVirtual = BasicDirectPayload(
                    Array.Empty<System.Text.Json.Nodes.JsonObject>(),
                    detailHints: BasicDetailHints(
                        includeSourceRows: false,
                        maxTextChars: 12000));
                RequireBasicPayloadContract(virtualConfig, expectedVirtual);

                var listed = await ListBasicVersionsAsync(actor, fixture, ct);
                HarnessAssert.Equal(0, listed.Count,
                    "Virtual Basic owner unexpectedly has persisted versions");
                var virtualDetail = await ReadBasicVersionAsync(
                    actor,
                    fixture,
                    1,
                    ct);
                HarnessAssert.Equal(true, virtualDetail.IsVirtualEmpty,
                    "Virtual version detail became an empty result");

                await RequireBasicRuntimeBlockedAsync(
                    "P8-BAS-014/virtual-not-result",
                    "summary",
                    actor,
                    BasicSummaryRequest(fixture),
                    ct);
                return new CaseObservation(
                    "A deterministic virtual empty config was readable without persistence and remained distinct from an executable empty result.",
                    "virtual=true;configId+versionId=deterministicObjectIds;version=1/revision0/DRAFT;emptyHash=true;versions=[];targets=[];summary=blocked+0W");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-BAS-015",
            "system_admin",
            ["p8-bas-015-empty-config"],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = BasicFixture("015");
                var payload = BasicPayload(
                    BasicSourceScope("SELF"),
                    BasicPeriodRule("ALL_PERIODS"),
                    Array.Empty<string>(),
                    BasicDetailHints(),
                    Array.Empty<System.Text.Json.Nodes.JsonObject>());
                var (_, config) = await PutBasicConfigAsync(
                    actor,
                    fixture,
                    "p8-bas-015-empty-config",
                    payload,
                    ct);
                HarnessAssert.Equal(false, config.IsVirtualEmpty,
                    "Persisted empty-target config still reports virtual empty");
                HarnessAssert.Equal(0,
                    (config.Payload["targets"] as System.Text.Json.Nodes.JsonArray)
                    ?.Count ?? -1,
                    "Persisted empty-target config gained an implicit target");
                RequireBasicPayloadContract(config, payload);

                await RequireBasicRuntimeBlockedAsync(
                    "P8-BAS-015/summary",
                    "summary",
                    actor,
                    BasicSummaryRequest(fixture),
                    ct);
                await RequireBasicRuntimeBlockedAsync(
                    "P8-BAS-015/once",
                    "once",
                    actor,
                    BasicSummaryRequest(fixture),
                    ct);
                return new CaseObservation(
                    "A persisted empty-target config stayed distinct from a materialized result; both summary surfaces stopped at the P9 barrier.",
                    "persisted=true;targets=[];isVirtualEmpty=false;summary=409+0W;once=409+0W;requestedOperation=BASIC_SUMMARY_RESULT;targetPhase=P9");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-BAS-016",
            "system_admin",
            [
                "p8-bas-016-isolation",
                "p8-bas-016-legacy-row-label-ambiguity"
            ],
            BasicMutationWrites,
            BasicMutationWrites,
            async () =>
            {
                await RequireP804SourceIsolationAsync(ct);
                var actor = Actor("system_admin");
                var fixture = BasicFixture("016");
                var reports = _database.GetCollection<BsonDocument>(
                    "work_assignment_reports");
                var reportFilter = Builders<BsonDocument>.Filter.Eq(
                    "_id",
                    ObjectId.Parse(_basicSourceReportId));
                var reportBefore = await reports.Find(reportFilter)
                    .SingleAsync(ct);
                var reportHashBefore = Sha256(reportBefore.ToBson());
                var payload = BasicPayload(
                    BasicSourceScope("DIRECT_CHILDREN_OR_SELF"),
                    BasicPeriodRule(
                        "PERIOD_RANGE",
                        periodKeyFrom: "2026-01",
                        periodKeyTo: "2026-12"),
                    ["ASSIGNMENT", "PERIOD", "UNIT"],
                    BasicDetailHints(
                        includeSourceRows: true,
                        maxTextChars: 64000),
                    [
                        BasicTarget(
                            "FIELD",
                            BasicTargetKeys["NUMBER"][3],
                            "NUMBER",
                            "MEAN"),
                        BasicTarget(
                            "FIELD",
                            BasicTargetKeys["TEXT"][0],
                            "TEXT",
                            "JOIN"),
                        BasicTarget(
                            "TABLE_METRIC",
                            "basic-table:metric:number",
                            "NUMBER",
                            "SUM"),
                        BasicTarget(
                            "ROW_LABEL",
                            BasicRowLabelCode,
                            "TEXT",
                            "COUNT")
                    ]);
                var (_, config) = await PutBasicConfigAsync(
                    actor,
                    fixture,
                    "p8-bas-016-isolation",
                    payload,
                    ct);
                RequireBasicPayloadContract(config, payload);
                RequireP804ModernStatisticDependencyPins(config);

                var legacyFixture = BasicFixture("legacy");
                var legacyCurrent = await ReadBasicConfigAsync(
                    actor,
                    legacyFixture,
                    ct,
                    requirePersisted: false);
                HarnessAssert.Equal(true, legacyCurrent.IsVirtualEmpty,
                    "Legacy ambiguity fixture unexpectedly has persisted Basic config");
                await RequireZeroWriteBasicRejectionAsync(
                    "P8-BAS-016/legacy-row-label-ambiguity",
                    () => _api.PutAsync(
                        BasicConfigRoute(legacyFixture),
                        Envelope(
                            "p8-bas-016-legacy-row-label-ambiguity",
                            legacyCurrent.Revision,
                            legacyCurrent.ConfigHash,
                            BasicDirectPayload([
                                BasicTarget(
                                    "ROW_LABEL",
                                    BasicRowLabelCode,
                                    "TEXT",
                                    "COUNT")
                            ])),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "STAT_CONFIG_SCHEMA_INVALID",
                    "$.payload.targets[0].conceptKey",
                    "BASIC_SUMMARY_ROW_LABEL_AMBIGUOUS",
                    ct);

                await RequireBasicRuntimeBlockedAsync(
                    "P8-BAS-016/force-refresh",
                    "summary",
                    actor,
                    BasicSummaryRequest(fixture, forceRefresh: true),
                    ct);
                await RequireBasicRuntimeBlockedAsync(
                    "P8-BAS-016/once",
                    "once",
                    actor,
                    BasicSummaryRequest(fixture),
                    ct);

                var reportAfter = await reports.Find(reportFilter)
                    .SingleAsync(ct);
                HarnessAssert.Equal(
                    reportHashBefore,
                    Sha256(reportAfter.ToBson()),
                    "Basic configuration/barrier path mutated source report state");
                var current = await ReadBasicConfigAsync(
                    actor,
                    fixture,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal(config.ConfigHash, current.ConfigHash,
                    "Blocked executor path changed Basic config state");
                return new CaseObservation(
                    "Modern TABLE_METRIC/ROW_LABEL configuration used frozen P8-03 pins while legacy cross-scope ambiguity rejected atomically and runtime remained isolated.",
                    "configOnly=true;modernSnapshotWins=true;legacyDuplicate=400+0W;sourceIsolation=staticBound+realAPI+Mongo;reportDocumentHash=stable;summaryForceRefresh=blocked+0W;once=blocked+0W;allResults=zero");
            },
            ct);
    }

    private async Task RequireBasicRuntimeBlockedAsync(
        string probeId,
        string route,
        P8Actor actor,
        System.Text.Json.Nodes.JsonObject body,
        CancellationToken ct)
    {
        var response = await RequireZeroWriteBasicRejectionAsync(
            probeId,
            () => _api.PostAsync(
                $"api/work-assignment-basic-summary/{route}",
                body,
                actor.Token,
                ct: ct),
            HttpStatusCode.Conflict,
            "DYNAMIC_FLOW_EXECUTION_BLOCKED_UNTIL_TARGET_PHASE",
            null,
            "P8_CONFIG_ONLY",
            ct);
        HarnessAssert.Equal(
            "BASIC_SUMMARY_RESULT",
            ApiHarnessClient.FindStringRecursive(
                response.Json,
                "requestedOperation"),
            "Basic runtime barrier requestedOperation mismatch");
        HarnessAssert.Equal(
            "P9",
            ApiHarnessClient.FindStringRecursive(response.Json, "targetPhase"),
            "Basic runtime barrier targetPhase mismatch");
    }
}
