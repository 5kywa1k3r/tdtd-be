using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP803LegacyAndIsolationCasesAsync(
        CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-TBL-013",
            "system_admin",
            ["p8-tbl-013-legacy-read-only"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("013");
                var legacy = fixture.LegacyBlock
                             ?? throw new InvalidOperationException(
                                 "P8-TBL-013 legacy fixture is absent.");
                var current = await ReadFieldConfigAsync(
                    actor,
                    fixture.Form.Id,
                    ct,
                    requirePersisted: true);
                RequireTableReadback(
                    current,
                    legacy.BlockId,
                    legacy.TableMode,
                    false,
                    "LEGACY_EXCEL_BLOCK_ADAPTER",
                    MetricMap(("metric:legacy", "NUMBER", ["COUNT"])));
                var owner = await RequireFormDocumentAsync(fixture.Form.Id, ct);
                HarnessAssert.Equal("[]", BsonString(owner, "blocksJson"),
                    "Legacy adapter fixture unexpectedly has canonical blocks");
                RequireTableConfigMatchesOwner(current, owner);

                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-013/legacy-read-only",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                        Envelope(
                            "p8-tbl-013-legacy-read-only",
                            current.Revision,
                            current.ConfigHash,
                            TablePayload(TablePatch(
                                legacy.BlockId,
                                legacy.TableMode,
                                false,
                                [TableMetric(
                                    "metric:legacy",
                                    "NUMBER",
                                    ["COUNT", "SUM"])]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_STRUCTURE_INVALID",
                    "$.schema.blocks",
                    "LEGACY_TABLE_STATISTIC_CONFIG_READ_ONLY",
                    ct);
                return new CaseObservation(
                    "The explicit legacy Excel block adapter produced stable typed readback but remained mutation read-only.",
                    "schemaSource=LEGACY_EXCEL_BLOCK_ADAPTER;identity=blockId+metricKey;patch=400+0W;blocksJson=[]");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-014",
            "system_admin",
            [
                "p8-tbl-014-canonical-wins",
                "p8-tbl-014-reactivation"
            ],
            TableMutationWrites,
            TableMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("014");
                var block = fixture.Block("tbl-canonical");
                var current = await ReadFieldConfigAsync(
                    actor,
                    fixture.Form.Id,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal(1, RequiredTableConfig(current).Count,
                    "Canonical BlocksJson did not take precedence over legacy shadow data");
                HarnessAssert.True(RequiredTableConfig(current)
                        .OfType<JsonObject>()
                        .All(table => !string.Equals(
                            RequiredString(table, "blockId"),
                            "tbl-legacy-shadow",
                            StringComparison.Ordinal)),
                    "Legacy shadow block leaked into canonical tableConfig");

                var (_, identity) = await PatchTableConfigAsync(
                    actor,
                    fixture,
                    "p8-tbl-014-canonical-wins",
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        true,
                        [TableMetric(
                            "metric:canonical",
                            "NUMBER",
                            ["COUNT", "SUM"])])),
                    ct);
                RequireTableReadback(
                    identity,
                    block.BlockId,
                    block.TableMode,
                    true,
                    "BLOCKS_JSON",
                    MetricMap((
                        "metric:canonical",
                        "NUMBER",
                        ["COUNT", "SUM"])));

                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-014/reactivation",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                        Envelope(
                            "p8-tbl-014-reactivation",
                            identity.Revision,
                            identity.ConfigHash,
                            TablePayload(TablePatch(
                                block.BlockId,
                                block.TableMode,
                                false,
                                [TableMetric(
                                    "metric:canonical",
                                    "NUMBER",
                                    ["COUNT", "SUM"])]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_STRUCTURE_INVALID",
                    "$.payload.tables[0].statisticsDisabled",
                    "TABLE_STATISTICS_DISABLED_REACTIVATION_FORBIDDEN",
                    ct);
                return new CaseObservation(
                    "Non-empty BlocksJson won over conflicting legacy data and a canonically disabled block could not be reactivated.",
                    "canonicalWins=true;legacyShadowAbsent=true;disabledMonotonic=true;reactivation=400+0W;excelBlockJson=byteStable");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-015",
            "system_admin",
            ["p8-tbl-015-summary-input"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("015");
                var block = fixture.Block("tbl-summary");
                var current = await ReadFieldConfigAsync(
                    actor,
                    fixture.Form.Id,
                    ct,
                    requirePersisted: true);
                HarnessAssert.Equal(0, RequiredTableConfig(current).Count,
                    "Unconfigured SUMMARY_TEMPLATE leaked into statistic input config");
                var beforeOwner = await RequireFormDocumentAsync(fixture.Form.Id, ct);
                var summaryNode = ParseBlocks(BsonString(beforeOwner, "blocksJson"))
                    .OfType<JsonObject>()
                    .Single(item => string.Equals(
                        RequiredString(item, "blockId"),
                        block.BlockId,
                        StringComparison.Ordinal));
                HarnessAssert.True(summaryNode["outputLayout"] is JsonObject,
                    "SUMMARY_TEMPLATE fixture lacks its output-only layout");

                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-015/summary-input",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                        Envelope(
                            "p8-tbl-015-summary-input",
                            current.Revision,
                            current.ConfigHash,
                            TablePayload(TablePatch(
                                block.BlockId,
                                block.TableMode,
                                false,
                                [TableMetric(
                                    "metric:summary-output",
                                    "NUMBER",
                                    ["COUNT"])]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_TABLE_MODE_INVALID",
                    "$.payload.tables[0].tableMode",
                    "SUMMARY_TEMPLATE_STATISTIC_INPUT_FORBIDDEN",
                    ct);
                return new CaseObservation(
                    "SUMMARY_TEMPLATE remained output-only and could not become a statistic input target.",
                    "outputLayout=preserved;tableConfigInput=absent;patch=TABLE_MODE_INVALID+0W");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-016",
            "system_admin",
            [
                "p8-tbl-016-unknown",
                "p8-tbl-016-mixed-sections",
                "p8-tbl-016-mode",
                "p8-tbl-016-op-alias",
                "p8-tbl-016-duplicate-op"
            ],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("016");
                var block = fixture.Block("tbl-strict");
                var current = await ReadFieldConfigAsync(
                    actor,
                    fixture.Form.Id,
                    ct,
                    requirePersisted: true);

                async Task RejectPatch(
                    string probe,
                    string commandId,
                    JsonObject payload,
                    string code,
                    string path,
                    string reason)
                    => await RequireZeroWriteTableRejectionAsync(
                        $"P8-TBL-016/{probe}",
                        () => _api.PatchAsync(
                            $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                            Envelope(
                                commandId,
                                current.Revision,
                                current.ConfigHash,
                                payload),
                            actor.Token,
                            ct: ct),
                        HttpStatusCode.BadRequest,
                        code,
                        path,
                        reason,
                        ct);

                var unknownTable = TablePatch(
                    block.BlockId,
                    block.TableMode,
                    false,
                    [TableMetric("metric:strict", "NUMBER", ["COUNT"])]);
                unknownTable["columnIndex"] = 0;
                await RejectPatch(
                    "unknown",
                    "p8-tbl-016-unknown",
                    TablePayload(unknownTable),
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    "$.payload.tables[0].columnIndex",
                    "SCHEMA_MISMATCH");

                var mixed = TablePayload(TablePatch(
                    block.BlockId,
                    block.TableMode,
                    false,
                    [TableMetric("metric:strict", "NUMBER", ["COUNT"])]));
                mixed["fields"] = new JsonArray(FieldPatch(
                    fixture.Form.Fields.Single().Id,
                    ["COUNT"]));
                await RejectPatch(
                    "mixed-sections",
                    "p8-tbl-016-mixed-sections",
                    mixed,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    "$.payload",
                    "TABLE_AND_FIELD_MUTATIONS_CONFLICT");

                await RejectPatch(
                    "mode",
                    "p8-tbl-016-mode",
                    TablePayload(TablePatch(
                        block.BlockId,
                        "MATRIX",
                        false,
                        [TableMetric("metric:strict", "NUMBER", ["COUNT"])])),
                    "DYNAMIC_FORM_TABLE_MODE_INVALID",
                    "$.payload.tables[0].tableMode",
                    "TABLE_MODE_ASSERTION_MISMATCH");
                await RejectPatch(
                    "op-alias",
                    "p8-tbl-016-op-alias",
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        false,
                        [TableMetric("metric:strict", "NUMBER", ["AVG"])])),
                    "DYNAMIC_FORM_STATISTIC_OPERATION_INVALID",
                    "$.payload.tables[0].metrics[0].aggregateOps[0]",
                    "TABLE_METRIC_OPERATION_INCOMPATIBLE");
                await RejectPatch(
                    "duplicate-op",
                    "p8-tbl-016-duplicate-op",
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        false,
                        [TableMetric(
                            "metric:strict",
                            "NUMBER",
                            ["COUNT", "count"])])),
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    "$.payload.tables[0].metrics[0].aggregateOps[1]",
                    "DUPLICATE_OPERATION");

                var owner = await RequireFormDocumentAsync(fixture.Form.Id, ct);
                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-016/alternate-put",
                    () => _api.PutAsync(
                        $"api/dynamic-forms/{fixture.Form.Id}",
                        new JsonObject
                        {
                            ["name"] = fixture.Form.Name,
                            ["description"] = fixture.Form.Description,
                            ["tagCodes"] = new JsonArray(),
                            ["schemaVersion"] = fixture.Form.SchemaVersion,
                            ["sectionsJson"] = fixture.Form.SectionsJson,
                            ["fieldsJson"] = BsonString(owner, "fieldsJson"),
                            ["excelBlockJson"] = BsonString(owner, "excelBlockJson"),
                            ["blocksJson"] = BsonString(owner, "blocksJson"),
                            ["isActive"] = true,
                            ["expectedRevision"] = BsonInt(owner, "revision")
                        },
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_STRUCTURE_INVALID",
                    "$.schema.blocks[0].metricLabelTargets",
                    "USE_CANONICAL_STATISTICS_PATCH",
                    ct);
                return new CaseObservation(
                    "Strict table DTOs, exact enum/operation assertions and the canonical PATCH writer boundary all failed closed.",
                    "unknownProperty=0W;mixedSections=0W;modeAssertion=0W;AVG-alias=0W;duplicateOp=0W;alternatePut=0W");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-017",
            "system_admin",
            [
                "p8-tbl-017-replay",
                "p8-tbl-017-stale"
            ],
            TableMutationWrites,
            TableMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("017");
                var block = fixture.Block("tbl-replay");
                var initial = await ReadFieldConfigAsync(
                    actor,
                    fixture.Form.Id,
                    ct,
                    requirePersisted: true);
                var payload = TablePayload(TablePatch(
                    block.BlockId,
                    block.TableMode,
                    false,
                    [TableMetric(
                        "metric:replay",
                        "NUMBER",
                        ["COUNT", "SUM"])]));
                var first = await PatchTableConfigAsync(
                    actor,
                    fixture,
                    "p8-tbl-017-replay",
                    payload,
                    ct,
                    initial.Revision,
                    initial.ConfigHash);
                var afterFirst = await CaptureDatabaseSnapshotAsync(ct);

                var replay = await _api.PatchAsync(
                    $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                    Envelope(
                        "p8-tbl-017-replay",
                        initial.Revision,
                        initial.ConfigHash,
                        payload.DeepClone()),
                    actor.Token,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    replay,
                    HttpStatusCode.OK,
                    "exact P8 table command replay");
                HarnessAssert.Equal(
                    CanonicalResponse(first.Response),
                    CanonicalResponse(replay),
                    "Exact table replay response changed");
                var replayIdentity = ParseFieldIdentity(
                    replay.Json,
                    requireReceipt: true);
                await RequireDirectFieldIdentityAsync(
                    replayIdentity,
                    "p8-tbl-017-replay",
                    actor.Id,
                    ct);
                var afterReplay = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-TBL-017/replay",
                    BuildDeltas(afterFirst, afterReplay),
                    Array.Empty<string>(),
                    Array.Empty<string>());
                HarnessAssert.Equal(
                    1L,
                    await CountFormReceiptsAsync(
                        fixture.Form.Id,
                        "p8-tbl-017-replay",
                        ct),
                    "Exact table replay wrote a second receipt");

                var divergentPayload = TablePayload(TablePatch(
                    block.BlockId,
                    block.TableMode,
                    false,
                    [TableMetric("metric:replay", "NUMBER", ["COUNT"])]));
                var beforeDivergent = await CaptureDatabaseSnapshotAsync(ct);
                var divergent = await _api.PatchAsync(
                    $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                    Envelope(
                        "p8-tbl-017-replay",
                        initial.Revision,
                        initial.ConfigHash,
                        divergentPayload),
                    actor.Token,
                    ct: ct);
                ExpectFailure(
                    divergent,
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_COMMAND_REPLAY_CONFLICT");
                var afterDivergent = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-TBL-017/divergent",
                    BuildDeltas(beforeDivergent, afterDivergent),
                    Array.Empty<string>(),
                    Array.Empty<string>());

                var beforeStale = await CaptureDatabaseSnapshotAsync(ct);
                var stale = await _api.PatchAsync(
                    $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                    Envelope(
                        "p8-tbl-017-stale",
                        initial.Revision,
                        initial.ConfigHash,
                        payload.DeepClone()),
                    actor.Token,
                    ct: ct);
                ExpectFailure(
                    stale,
                    HttpStatusCode.Conflict,
                    "STAT_CONFIG_CAS_CONFLICT");
                var afterStale = await CaptureDatabaseSnapshotAsync(ct);
                VerifyCollectionContract(
                    "P8-TBL-017/stale",
                    BuildDeltas(beforeStale, afterStale),
                    Array.Empty<string>(),
                    Array.Empty<string>());
                return new CaseObservation(
                    "Exact replay reused one durable response/receipt, while divergent replay and stale CAS remained zero-write conflicts.",
                    "exactReplay=true+0W;receiptCount=1;divergentReplay=409+0W;staleCas=409+0W");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-018",
            "system_admin",
            ["p8-tbl-018-isolation"],
            TableMutationWrites,
            TableMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("018");
                var block = fixture.Block("tbl-isolated");
                var reports = _database.GetCollection<BsonDocument>(
                    "work_assignment_reports");
                var reportBefore = await reports.Find(
                        Builders<BsonDocument>.Filter.Eq(
                            "_id",
                            ObjectId.Parse(_tableReportId)))
                    .SingleAsync(ct);
                var (_, identity) = await PatchTableConfigAsync(
                    actor,
                    fixture,
                    "p8-tbl-018-isolation",
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        false,
                        [TableMetric(
                            "metric:isolated",
                            "NUMBER",
                            ["COUNT", "SUM", "AVERAGE"])])),
                    ct);
                RequireTableReadback(
                    identity,
                    block.BlockId,
                    block.TableMode,
                    false,
                    "BLOCKS_JSON",
                    MetricMap((
                        "metric:isolated",
                        "NUMBER",
                        ["COUNT", "SUM", "AVERAGE"])));
                RequireTableReadback(
                    identity,
                    "tbl-untouched",
                    "MATRIX",
                    false,
                    "BLOCKS_JSON",
                    MetricMap((
                        "metric:untouched",
                        "NUMBER",
                        ["COUNT"])));
                var reportAfter = await reports.Find(
                        Builders<BsonDocument>.Filter.Eq(
                            "_id",
                            ObjectId.Parse(_tableReportId)))
                    .SingleAsync(ct);
                HarnessAssert.Equal(
                    Sha256(reportBefore.ToBson()),
                    Sha256(reportAfter.ToBson()),
                    "Existing work-assignment report changed during table metadata PATCH");
                return new CaseObservation(
                    "A targeted table PATCH preserved sibling block metadata, field/legacy bytes and a pre-existing report without executor writes.",
                    "targetOps=COUNT,SUM,AVERAGE;siblingOps=COUNT+byteStable;fieldSection=byteStable;reportHash=stable;results+jobs+outbox=zero");
            },
            ct);
    }
}
