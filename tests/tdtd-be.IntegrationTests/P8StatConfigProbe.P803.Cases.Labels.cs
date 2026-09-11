using System.Net;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP803LabelCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-TBL-005",
            "system_admin",
            ["p8-tbl-005-label-pins"],
            TableMutationWrites,
            TableMutationWrites,
            async () =>
            {
                var fixture = TableFixture("005");
                var block = fixture.Block("tbl-labels");
                var (_, identity) = await PatchTableConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-tbl-005-label-pins",
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        false,
                        [
                            TableMetric("metric:second", "NUMBER", ["COUNT"]),
                            TableMetric("metric:first", "NUMBER", ["COUNT"])
                        ],
                        [
                            TableMetricLabelTarget("metric:second", _tableMetricLabelB.LabelCode),
                            TableMetricLabelTarget("metric:first", _tableMetricLabelA.LabelCode)
                        ])),
                    ct);
                RequireTableReadback(
                    identity,
                    block.BlockId,
                    block.TableMode,
                    false,
                    "BLOCKS_JSON",
                    MetricMap(
                        ("metric:first", "NUMBER", ["COUNT"]),
                        ("metric:second", "NUMBER", ["COUNT"])),
                    [
                        ("metric:first", _tableMetricLabelA.LabelCode),
                        ("metric:second", _tableMetricLabelB.LabelCode)
                    ]);
                RequirePinnedMetricLabel(
                    identity,
                    block.BlockId,
                    "metric:first",
                    _tableMetricLabelA);
                RequirePinnedMetricLabel(
                    identity,
                    block.BlockId,
                    "metric:second",
                    _tableMetricLabelB);
                return new CaseObservation(
                    "Active TABLE_TARGET metric labels were type-checked, pinned and canonical-sorted by metricKey+code.",
                    "metricLabels=2;usage=TABLE_TARGET;type=NUMBER;snapshots=pinned;declaredSet=canonicalSorted");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-006",
            "system_admin",
            [
                "p8-tbl-006-inactive",
                "p8-tbl-006-wrong-usage",
                "p8-tbl-006-unknown"
            ],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("006");
                var block = fixture.Block("tbl-label-invalid");
                var current = await ReadFieldConfigAsync(
                    actor,
                    fixture.Form.Id,
                    ct,
                    requirePersisted: true);
                async Task Reject(string commandId, string code, string reason)
                    => await RequireZeroWriteTableRejectionAsync(
                        $"P8-TBL-006/{commandId}",
                        () => _api.PatchAsync(
                            $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                            Envelope(
                                commandId,
                                current.Revision,
                                current.ConfigHash,
                                TablePayload(TablePatch(
                                    block.BlockId,
                                    block.TableMode,
                                    false,
                                    [TableMetric("metric:number", "NUMBER", ["COUNT"])],
                                    [TableMetricLabelTarget("metric:number", code)]))),
                            actor.Token,
                            ct: ct),
                        HttpStatusCode.BadRequest,
                        reason == "TABLE_METRIC_LABEL_NOT_FOUND_OR_INACTIVE"
                            ? "DYNAMIC_FORM_LABEL_NOT_FOUND_OR_INACTIVE"
                            : "DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID",
                        "$.payload.tables[0].metricLabelTargets[0].statisticLabelCode",
                        reason,
                        ct);

                await Reject(
                    "p8-tbl-006-inactive",
                    _tableMetricInactive.LabelCode,
                    "TABLE_METRIC_LABEL_NOT_FOUND_OR_INACTIVE");
                await Reject(
                    "p8-tbl-006-wrong-usage",
                    _tableMetricWrongUsage.LabelCode,
                    "TABLE_METRIC_LABEL_USAGE_INCOMPATIBLE");
                await Reject(
                    "p8-tbl-006-unknown",
                    "p8.tbl.metric.missing",
                    "TABLE_METRIC_LABEL_NOT_FOUND_OR_INACTIVE");
                return new CaseObservation(
                    "Inactive, unknown and non-TABLE_TARGET metric labels all failed closed.",
                    "inactive=0W;unknown=0W;wrongUsage=0W;path=metricLabelTargets[].statisticLabelCode");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-007",
            "system_admin",
            ["p8-tbl-007-label-type", "p8-tbl-007-metric-type-assertion"],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("007");
                var block = fixture.Block("tbl-label-type");
                var current = await ReadFieldConfigAsync(
                    actor,
                    fixture.Form.Id,
                    ct,
                    requirePersisted: true);
                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-007/label-type",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                        Envelope(
                            "p8-tbl-007-label-type",
                            current.Revision,
                            current.ConfigHash,
                            TablePayload(TablePatch(
                                block.BlockId,
                                block.TableMode,
                                false,
                                [TableMetric("metric:number", "NUMBER", ["COUNT"])],
                                [TableMetricLabelTarget(
                                    "metric:number",
                                    _tableMetricWrongType.LabelCode)]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_LABEL_STATISTIC_TARGET_INVALID",
                    "$.payload.tables[0].metricLabelTargets[0].statisticLabelCode",
                    "TABLE_METRIC_LABEL_TYPE_INCOMPATIBLE",
                    ct);
                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-007/metric-type-assertion",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                        Envelope(
                            "p8-tbl-007-metric-type-assertion",
                            current.Revision,
                            current.ConfigHash,
                            TablePayload(TablePatch(
                                block.BlockId,
                                block.TableMode,
                                false,
                                [TableMetric("metric:number", "BOOLEAN", ["COUNT"])]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    "$.payload.tables[0].metrics[0].dataType",
                    "TABLE_METRIC_DATATYPE_ASSERTION_MISMATCH",
                    ct);
                return new CaseObservation(
                    "Metric label type and payload datatype assertions could not override canonical metricRules datatype.",
                    "labelTypeMismatch=0W;payloadTypeAssertionMismatch=0W;canonicalType=NUMBER");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-008",
            "system_admin",
            [
                "p8-tbl-008-duplicate-metric",
                "p8-tbl-008-cross-metric-label",
                "p8-tbl-008-field-table-label"
            ],
            Array.Empty<string>(),
            Array.Empty<string>(),
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("008");
                var block = fixture.Block("tbl-collision");
                var current = await ReadFieldConfigAsync(
                    actor,
                    fixture.Form.Id,
                    ct,
                    requirePersisted: true);
                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-008/duplicate-metric",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                        Envelope(
                            "p8-tbl-008-duplicate-metric",
                            current.Revision,
                            current.ConfigHash,
                            TablePayload(TablePatch(
                                block.BlockId,
                                block.TableMode,
                                false,
                                [
                                    TableMetric("metric:first", "NUMBER", ["COUNT"]),
                                    TableMetric("metric:first", "NUMBER", ["SUM"])
                                ]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    "$.payload.tables[0].metrics[1].metricKey",
                    "DUPLICATE_METRIC_KEY",
                    ct);
                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-008/cross-metric-label",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                        Envelope(
                            "p8-tbl-008-cross-metric-label",
                            current.Revision,
                            current.ConfigHash,
                            TablePayload(TablePatch(
                                block.BlockId,
                                block.TableMode,
                                false,
                                [
                                    TableMetric("metric:first", "NUMBER", ["COUNT"]),
                                    TableMetric("metric:second", "NUMBER", ["COUNT"])
                                ],
                                [
                                    TableMetricLabelTarget(
                                        "metric:first",
                                        _tableMetricLabelB.LabelCode),
                                    TableMetricLabelTarget(
                                        "metric:second",
                                        _tableMetricLabelB.LabelCode)
                                ]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_LABEL_STATISTIC_TARGET_CONFLICT",
                    "$.payload",
                    "TABLE_STATISTIC_LABEL_TARGET_CONFLICT",
                    ct);
                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-008/field-table-label",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                        Envelope(
                            "p8-tbl-008-field-table-label",
                            current.Revision,
                            current.ConfigHash,
                            TablePayload(TablePatch(
                                block.BlockId,
                                block.TableMode,
                                false,
                                [
                                    TableMetric("metric:first", "NUMBER", ["COUNT"]),
                                    TableMetric("metric:second", "NUMBER", ["COUNT"])
                                ],
                                [TableMetricLabelTarget(
                                    "metric:first",
                                    _tableMetricLabelA.LabelCode)]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_LABEL_STATISTIC_TARGET_CONFLICT",
                    "$.payload",
                    "TABLE_STATISTIC_LABEL_TARGET_CONFLICT",
                    ct);
                return new CaseObservation(
                    "Duplicate metric identities and cross-metric/cross-field label collisions all rejected atomically.",
                    "duplicateMetric=0W;crossMetricLabel=0W;fieldTableLabel=0W;noAmbiguousTarget=true");
            },
            ct);
    }
}
