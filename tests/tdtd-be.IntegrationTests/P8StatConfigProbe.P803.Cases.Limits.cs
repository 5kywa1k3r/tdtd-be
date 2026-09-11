using System.Net;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP803LimitAndLocalityCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-TBL-009",
            "system_admin",
            ["p8-tbl-009-target-30"],
            TableMutationWrites,
            TableMutationWrites,
            async () =>
            {
                var fixture = TableFixture("009");
                var block = fixture.Block("tbl-target-30");
                var patches = block.Metrics
                    .Reverse()
                    .Select(metric => TableMetric(
                        metric.MetricKey,
                        metric.DataType,
                        ["COUNT"]))
                    .ToArray();
                var (_, identity) = await PatchTableConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-tbl-009-target-30",
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        false,
                        patches)),
                    ct);
                var expected = block.Metrics.ToDictionary(
                    metric => metric.MetricKey,
                    metric => (metric.DataType, (IReadOnlyList<string>)new[] { "COUNT" }),
                    StringComparer.Ordinal);
                RequireTableReadback(
                    identity,
                    block.BlockId,
                    block.TableMode,
                    false,
                    "BLOCKS_JSON",
                    expected);
                var table = RequireTable(identity, block.BlockId);
                HarnessAssert.Equal(30, RequiredInt(table, "statisticsInputCellCount"),
                    "30-target block input count readback mismatch");
                HarnessAssert.Equal(250, RequiredInt(table, "statisticsInputCellLimit"),
                    "30-target block input limit readback mismatch");
                return new CaseObservation(
                    "Exactly 30 stable metric identities were accepted and canonical-sorted without positional fallback.",
                    "metricTargets=30;limit=30;requestOrder=reversed;readbackOrder=metricKey;hash+mongo=true");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-010",
            "system_admin",
            ["p8-tbl-010-target-31"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("010");
                var block = fixture.Block("tbl-target-31");
                var current = await ReadFieldConfigAsync(
                    actor,
                    fixture.Form.Id,
                    ct,
                    requirePersisted: true);
                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-010/target-31",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                        Envelope(
                            "p8-tbl-010-target-31",
                            current.Revision,
                            current.ConfigHash,
                            TablePayload(TablePatch(
                                block.BlockId,
                                block.TableMode,
                                false,
                                block.Metrics.Select(metric => TableMetric(
                                    metric.MetricKey,
                                    metric.DataType,
                                    ["COUNT"]))))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_TARGET_LIMIT_EXCEEDED",
                    "$.payload.tables",
                    "TABLE_STATISTIC_TARGET_LIMIT_30",
                    ct);
                return new CaseObservation(
                    "The 31st configured metric identity failed the exact target budget with whole-DB zero delta.",
                    "metricTargets=31;limit=30;http=400;code=TARGET_LIMIT_EXCEEDED;delta=zero");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-011",
            "system_admin",
            ["p8-tbl-011-block-30", "p8-tbl-011-block-31"],
            TableMutationWrites,
            TableMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var accepted = TableFixture("011a");
                var acceptedPayload = TablePayload(accepted.Blocks
                    .Reverse()
                    .Select(block => TablePatch(
                        block.BlockId,
                        block.TableMode,
                        false,
                        [TableMetric(
                            block.Metrics.Single().MetricKey,
                            "NUMBER",
                            ["COUNT"])]))
                    .ToArray());
                var (_, identity) = await PatchTableConfigAsync(
                    actor,
                    accepted,
                    "p8-tbl-011-block-30",
                    acceptedPayload,
                    ct);
                HarnessAssert.Equal(30, RequiredTableConfig(identity).Count,
                    "Exactly 30 canonical blocks did not survive readback");

                var rejected = TableFixture("011b");
                var first = rejected.Blocks.First();
                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-011/block-31",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{rejected.Form.Id}/statistics",
                        Envelope(
                            "p8-tbl-011-block-31",
                            0,
                            new string('0', 64),
                            TablePayload(TablePatch(
                                first.BlockId,
                                first.TableMode,
                                false,
                                [TableMetric(
                                    first.Metrics.Single().MetricKey,
                                    "NUMBER",
                                    ["COUNT"])]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_LIMIT_EXCEEDED",
                    "$.schema.blocks",
                    "TABLE_BLOCK_LIMIT_30",
                    ct);
                return new CaseObservation(
                    "Thirty canonical table blocks were accepted while a 31-block owner failed closed without receipt or partial config.",
                    "blocks30=accepted;blocks31=TABLE_BLOCK_LIMIT_30+0W;targetOrder=reversed;typedReadback=30");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-012",
            "system_admin",
            [
                "p8-tbl-012-row-disable",
                "p8-tbl-012-row-usage",
                "p8-tbl-012-row-type",
                "p8-tbl-012-row-inactive"
            ],
            TableMutationWrites,
            TableMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("012");
                var block = fixture.Block("tbl-row-target");
                var (_, identity) = await PatchTableConfigAsync(
                    actor,
                    fixture,
                    "p8-tbl-012-row-disable",
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        true,
                        [TableMetric("metric:amount", "NUMBER", ["COUNT", "SUM"])],
                        allowedRowLabelCodes:
                        [
                            _tableRowLabelB.LabelCode,
                            _tableRowLabelA.LabelCode
                        ])),
                    ct);
                RequireTableReadback(
                    identity,
                    block.BlockId,
                    block.TableMode,
                    true,
                    "BLOCKS_JSON",
                    MetricMap(("metric:amount", "NUMBER", ["COUNT", "SUM"])),
                    expectedRowLabels:
                    [
                        _tableRowLabelA.LabelCode,
                        _tableRowLabelB.LabelCode
                    ]);
                RequirePinnedRowLabel(identity, block.BlockId, _tableRowLabelA);
                RequirePinnedRowLabel(identity, block.BlockId, _tableRowLabelB);
                var target = RequireTable(identity, block.BlockId);
                HarnessAssert.Equal("NUMBER", RequiredString(target, "rowLabelDataType"),
                    "Row-label datatype precedence did not resolve NUMBER");
                HarnessAssert.True(!string.IsNullOrWhiteSpace(
                        OptionalString(target, "statisticsDisabledReason")),
                    "Disabled block lacks a typed reason");
                var sibling = RequireTable(identity, "tbl-row-sibling");
                HarnessAssert.Equal(false, RequiredBool(sibling, "statisticsDisabled"),
                    "Disabling one block disabled its sibling");
                var siblingMetrics = sibling["metrics"] as System.Text.Json.Nodes.JsonArray
                                     ?? throw new InvalidOperationException(
                                         "Sibling metrics are absent.");
                HarnessAssert.True(siblingMetrics.OfType<System.Text.Json.Nodes.JsonObject>()
                        .Single()["aggregateOps"] is System.Text.Json.Nodes.JsonArray siblingOps &&
                    siblingOps.Select(item => item?.GetValue<string>() ?? string.Empty)
                        .SequenceEqual(["COUNT"], StringComparer.Ordinal),
                    "Target block mutation changed its preconfigured sibling metric");

                async Task RejectRow(
                    string commandId,
                    P8ConfigIdentity label,
                    string errorCode,
                    string reason)
                    => await RequireZeroWriteTableRejectionAsync(
                        $"P8-TBL-012/{commandId}",
                        () => _api.PatchAsync(
                            $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                            Envelope(
                                commandId,
                                identity.Revision,
                                identity.ConfigHash,
                                TablePayload(TablePatch(
                                    block.BlockId,
                                    block.TableMode,
                                    true,
                                    [TableMetric("metric:amount", "NUMBER", ["COUNT", "SUM"])],
                                    allowedRowLabelCodes: [label.LabelCode]))),
                            actor.Token,
                            ct: ct),
                        HttpStatusCode.BadRequest,
                        errorCode,
                        "$.payload.tables[0].allowedRowLabelCodes[0]",
                        reason,
                        ct);
                await RejectRow(
                    "p8-tbl-012-row-usage",
                    _tableRowWrongUsage,
                    "DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID",
                    "TABLE_ROW_LABEL_USAGE_INCOMPATIBLE");
                await RejectRow(
                    "p8-tbl-012-row-type",
                    _tableRowWrongType,
                    "DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID",
                    "TABLE_ROW_LABEL_TYPE_INCOMPATIBLE");
                await RejectRow(
                    "p8-tbl-012-row-inactive",
                    _tableRowInactive,
                    "DYNAMIC_FORM_LABEL_NOT_FOUND_OR_INACTIVE",
                    "TABLE_ROW_LABEL_NOT_FOUND_OR_INACTIVE");
                return new CaseObservation(
                    "TABLE_TARGET row labels were pinned by type, disabling stayed block-local, and invalid row labels wrote nothing.",
                    "rowLabels=2+pinned;rowType=NUMBER;disabledTarget=true;sibling=false+unchanged;invalidUsage/type/inactive=0W");
            },
            ct);
    }
}
