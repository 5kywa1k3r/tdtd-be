using System.Net;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private async Task RunP803TableCasesAsync(CancellationToken ct)
    {
        await SeedP803FixturesAsync(ct);
        await RunP803IdentityCasesAsync(ct);
        await RunP803LabelCasesAsync(ct);
        await RunP803LimitAndLocalityCasesAsync(ct);
        await RunP803LegacyAndIsolationCasesAsync(ct);
    }

    private async Task RunP803IdentityCasesAsync(CancellationToken ct)
    {
        await RunEvidenceCaseAsync(
            "P8-TBL-001",
            "system_admin",
            ["p8-tbl-001-fixed-grid"],
            TableMutationWrites,
            TableMutationWrites,
            async () =>
            {
                var fixture = TableFixture("001");
                var block = fixture.Block("tbl-fixed");
                var (_, identity) = await PatchTableConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-tbl-001-fixed-grid",
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        false,
                        [
                            TableMetric("metric:beta", "NUMBER",
                                ["COUNT", "SUM", "MIN", "MAX", "AVERAGE"]),
                            TableMetric("metric:alpha", "NUMBER",
                                ["COUNT", "SUM", "MIN", "MAX", "AVERAGE"])
                        ])),
                    ct);
                RequireTableReadback(
                    identity,
                    block.BlockId,
                    "FIXED_GRID",
                    false,
                    "BLOCKS_JSON",
                    MetricMap(
                        ("metric:alpha", "NUMBER", ["COUNT", "SUM", "MIN", "MAX", "AVERAGE"]),
                        ("metric:beta", "NUMBER", ["COUNT", "SUM", "MIN", "MAX", "AVERAGE"])));
                return new CaseObservation(
                    "FIXED_GRID resolved both targets by case-sensitive blockId+metricKey despite reversed indexMap and request order.",
                    "mode=FIXED_GRID;identity=blockId+metricKey;indexMap=reversed;requestMetrics=reversed;canonicalReadback=metricKeySorted");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-002",
            "system_admin",
            ["p8-tbl-002-append-rows"],
            TableMutationWrites,
            TableMutationWrites,
            async () =>
            {
                var fixture = TableFixture("002");
                var block = fixture.Block("tbl-append-rows");
                var (_, identity) = await PatchTableConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-tbl-002-append-rows",
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        false,
                        [
                            TableMetric("metric:multi", "MULTI_SELECT", ["COUNT", "BUCKET_COUNT"]),
                            TableMetric("metric:short", "SHORT_TEXT", ["COUNT", "BUCKET_COUNT"])
                        ])),
                    ct);
                RequireTableReadback(
                    identity,
                    block.BlockId,
                    "APPEND_ROWS",
                    false,
                    "BLOCKS_JSON",
                    MetricMap(
                        ("metric:multi", "MULTI_SELECT", ["COUNT", "BUCKET_COUNT"]),
                        ("metric:short", "SHORT_TEXT", ["COUNT", "BUCKET_COUNT"])));
                return new CaseObservation(
                    "APPEND_ROWS kept semantic metric identity independent of runtime row order.",
                    "mode=APPEND_ROWS;types=SHORT_TEXT,MULTI_SELECT;ops=COUNT,BUCKET_COUNT;positionalFallback=false");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-003",
            "system_admin",
            ["p8-tbl-003-append-columns"],
            TableMutationWrites,
            TableMutationWrites,
            async () =>
            {
                var fixture = TableFixture("003");
                var block = fixture.Block("tbl-append-columns");
                var (_, identity) = await PatchTableConfigAsync(
                    Actor("system_admin"),
                    fixture,
                    "p8-tbl-003-append-columns",
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        false,
                        [TableMetric("metric:boolean", "BOOLEAN",
                            ["COUNT", "TRUE_COUNT", "FALSE_COUNT"])])),
                    ct);
                RequireTableReadback(
                    identity,
                    block.BlockId,
                    "APPEND_COLUMNS",
                    false,
                    "BLOCKS_JSON",
                    MetricMap(("metric:boolean", "BOOLEAN",
                        ["COUNT", "TRUE_COUNT", "FALSE_COUNT"])));
                return new CaseObservation(
                    "APPEND_COLUMNS stored the BOOLEAN metric contract by stable key, not column instance position.",
                    "mode=APPEND_COLUMNS;type=BOOLEAN;ops=COUNT,TRUE_COUNT,FALSE_COUNT;identity=stable");
            },
            ct);

        await RunEvidenceCaseAsync(
            "P8-TBL-004",
            "system_admin",
            ["p8-tbl-004-matrix", "p8-tbl-004-positional"],
            TableMutationWrites,
            TableMutationWrites,
            async () =>
            {
                var actor = Actor("system_admin");
                var fixture = TableFixture("004");
                var block = fixture.Block("tbl-matrix");
                var (_, identity) = await PatchTableConfigAsync(
                    actor,
                    fixture,
                    "p8-tbl-004-matrix",
                    TablePayload(TablePatch(
                        block.BlockId,
                        block.TableMode,
                        false,
                        [
                            TableMetric("metric:full-date", "FULL_DATE",
                                ["COUNT", "EARLIEST", "LATEST"]),
                            TableMetric("metric:date", "DATE",
                                ["COUNT", "EARLIEST", "LATEST"])
                        ])),
                    ct);
                RequireTableReadback(
                    identity,
                    block.BlockId,
                    "MATRIX",
                    false,
                    "BLOCKS_JSON",
                    MetricMap(
                        ("metric:date", "DATE", ["COUNT", "EARLIEST", "LATEST"]),
                        ("metric:full-date", "FULL_DATE", ["COUNT", "EARLIEST", "LATEST"])));

                await RequireZeroWriteTableRejectionAsync(
                    "P8-TBL-004/positional-fallback",
                    () => _api.PatchAsync(
                        $"api/dynamic-forms/{fixture.Form.Id}/statistics",
                        Envelope(
                            "p8-tbl-004-positional",
                            identity.Revision,
                            identity.ConfigHash,
                            TablePayload(TablePatch(
                                block.BlockId,
                                block.TableMode,
                                false,
                                [TableMetric("index:0", "DATE", ["COUNT"])]))),
                        actor.Token,
                        ct: ct),
                    HttpStatusCode.BadRequest,
                    "DYNAMIC_FORM_STATISTIC_CONFIG_SCHEMA_INVALID",
                    "$.payload.tables[0].metrics[0].metricKey",
                    "TABLE_METRIC_NOT_FOUND",
                    ct);
                return new CaseObservation(
                    "MATRIX preserved date metric identities across reordered maps and rejected an index-shaped key with zero writes.",
                    "mode=MATRIX;types=DATE,FULL_DATE;ops=COUNT,EARLIEST,LATEST;index:0=TABLE_METRIC_NOT_FOUND+0W");
            },
            ct);
    }

    private static IReadOnlyDictionary<
        string,
        (string DataType, IReadOnlyList<string> Ops)> MetricMap(
        params (string MetricKey, string DataType, string[] Ops)[] entries)
        => entries.ToDictionary(
            entry => entry.MetricKey,
            entry => (entry.DataType, (IReadOnlyList<string>)entry.Ops),
            StringComparer.Ordinal);
}
