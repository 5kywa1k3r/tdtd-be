using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private readonly Dictionary<string, P8TableFixture> _tableFixtures =
        new(StringComparer.Ordinal);
    private P8ConfigIdentity _tableMetricLabelA = default!;
    private P8ConfigIdentity _tableMetricLabelB = default!;
    private P8ConfigIdentity _tableMetricInactive = default!;
    private P8ConfigIdentity _tableMetricWrongUsage = default!;
    private P8ConfigIdentity _tableMetricWrongType = default!;
    private P8ConfigIdentity _tableRowLabelA = default!;
    private P8ConfigIdentity _tableRowLabelB = default!;
    private P8ConfigIdentity _tableRowInactive = default!;
    private P8ConfigIdentity _tableRowWrongUsage = default!;
    private P8ConfigIdentity _tableRowWrongType = default!;
    private string _tableReportId = default!;
    private bool _p803LabelsSeeded;

    private async Task SeedP803FixturesAsync(CancellationToken ct)
    {
        await SeedP803LabelsAsync(ct);
        var fixtures = new[]
        {
            NewTableFixture("001",
            [
                TableBlock("tbl-fixed", "FIXED_GRID",
                [
                    TableMetricFixture("metric:alpha", "NUMBER"),
                    TableMetricFixture("metric:beta", "NUMBER")
                ], reverseIndexMap: true)
            ]),
            NewTableFixture("002",
            [
                TableBlock("tbl-append-rows", "APPEND_ROWS",
                [
                    TableMetricFixture("metric:short", "SHORT_TEXT"),
                    TableMetricFixture("metric:multi", "MULTI_SELECT")
                ])
            ]),
            NewTableFixture("003",
            [
                TableBlock("tbl-append-columns", "APPEND_COLUMNS",
                [TableMetricFixture("metric:boolean", "BOOLEAN")])
            ]),
            NewTableFixture("004",
            [
                TableBlock("tbl-matrix", "MATRIX",
                [
                    TableMetricFixture("metric:date", "DATE"),
                    TableMetricFixture("metric:full-date", "FULL_DATE")
                ], reverseIndexMap: true)
            ]),
            NewTableFixture("005",
            [
                TableBlock("tbl-labels", "FIXED_GRID",
                [
                    TableMetricFixture("metric:first", "NUMBER"),
                    TableMetricFixture("metric:second", "NUMBER")
                ])
            ]),
            NewTableFixture("006",
            [
                TableBlock("tbl-label-invalid", "FIXED_GRID",
                [TableMetricFixture("metric:number", "NUMBER")])
            ]),
            NewTableFixture("007",
            [
                TableBlock("tbl-label-type", "FIXED_GRID",
                [TableMetricFixture("metric:number", "NUMBER")])
            ]),
            NewTableFixture("008",
            [
                TableBlock("tbl-collision", "FIXED_GRID",
                [
                    TableMetricFixture("metric:first", "NUMBER"),
                    TableMetricFixture("metric:second", "NUMBER")
                ])
            ]),
            NewTableFixture("009",
            [
                TableBlock("tbl-target-30", "FIXED_GRID",
                    Enumerable.Range(1, 30)
                        .Select(index => TableMetricFixture($"metric:{index:00}", "NUMBER"))
                        .ToArray())
            ]),
            NewTableFixture("010",
            [
                TableBlock("tbl-target-31", "FIXED_GRID",
                    Enumerable.Range(1, 31)
                        .Select(index => TableMetricFixture($"metric:{index:00}", "NUMBER"))
                        .ToArray())
            ]),
            NewTableFixture("011a",
                Enumerable.Range(1, 30)
                    .Select(index => TableBlock(
                        $"tbl-block-{index:00}",
                        "FIXED_GRID",
                        [TableMetricFixture($"metric:{index:00}", "NUMBER")]))
                    .ToArray()),
            NewTableFixture("011b",
                Enumerable.Range(1, 31)
                    .Select(index => TableBlock(
                        $"tbl-over-{index:00}",
                        "FIXED_GRID",
                        [TableMetricFixture($"metric:{index:00}", "NUMBER")]))
                    .ToArray()),
            NewTableFixture("012",
            [
                TableBlock("tbl-row-target", "APPEND_ROWS",
                    [TableMetricFixture("metric:amount", "NUMBER")],
                    rowLabelDataType: "NUMBER"),
                TableBlock("tbl-row-sibling", "FIXED_GRID",
                    [TableMetricFixture(
                        "metric:sibling", "NUMBER", ["COUNT"])])
            ]),
            NewTableFixture(
                "013",
                [],
                legacyBlock: TableBlock("tbl-legacy", "FIXED_GRID",
                    [TableMetricFixture(
                        "metric:legacy", "NUMBER", ["COUNT"])])),
            NewTableFixture(
                "014",
                [
                    TableBlock("tbl-canonical", "FIXED_GRID",
                        [TableMetricFixture("metric:canonical", "NUMBER")],
                        statisticsDisabled: true)
                ],
                legacyBlock: TableBlock("tbl-legacy-shadow", "FIXED_GRID",
                    [TableMetricFixture("metric:shadow", "NUMBER")],
                    statisticsDisabled: false)),
            NewTableFixture("015",
            [
                TableBlock("tbl-summary", "SUMMARY_TEMPLATE",
                    [TableMetricFixture("metric:summary-output", "NUMBER")])
            ]),
            NewTableFixture("016",
            [
                TableBlock("tbl-strict", "FIXED_GRID",
                    [TableMetricFixture("metric:strict", "NUMBER")])
            ]),
            NewTableFixture("017",
            [
                TableBlock("tbl-replay", "FIXED_GRID",
                    [TableMetricFixture("metric:replay", "NUMBER")])
            ]),
            NewTableFixture("018",
            [
                TableBlock("tbl-isolated", "FIXED_GRID",
                    [TableMetricFixture("metric:isolated", "NUMBER")]),
                TableBlock("tbl-untouched", "MATRIX",
                    [TableMetricFixture(
                        "metric:untouched", "NUMBER", ["COUNT"])])
            ])
        };
        foreach (var fixture in fixtures)
            _tableFixtures.Add(fixture.Key, fixture);

        await _database.GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
            .InsertManyAsync(fixtures.Select(fixture => fixture.Form.Template), cancellationToken: ct);

        foreach (var fixture in fixtures.Where(item => item.Key != "011b"))
        {
            var field = fixture.Form.Fields.Single();
            var labels = fixture.Key == "008"
                ? new[] { _tableMetricLabelA.LabelCode }
                : Array.Empty<string>();
            await PatchFieldConfigAsync(
                Actor("system_admin"),
                fixture.Form,
                $"p8-tbl-setup-field-{fixture.Key}",
                FieldPayload(FieldPatch(
                    field.Id,
                    ["COUNT"],
                    statisticLabelCodes: labels)),
                ct);
        }
        (_, _tableMetricLabelA) = await UpdateLabelAsync(
            Actor("system_admin"),
            _tableMetricLabelA,
            "p8-tbl-setup-metric-a-table-target",
            LabelPayload("p8.tbl.metric.a", "P8 table metric A", "GLOBAL", null,
                usage: "TABLE_TARGET", dataType: "NUMBER"), ct);


        _tableReportId = ObjectId.GenerateNewId().ToString();
        var reportFixture = new BsonDocument
        {
            ["_id"] = ObjectId.Parse(_tableReportId),
            ["dynamicFormTemplateId"] = ObjectId.Parse(TableFixture("018").Form.Id),
            ["dynamicFormVersionId"] = ObjectId.Parse(TableFixture("018").Form.Id),
            ["status"] = "DRAFT",
            ["payloadJson"] = "{}",
            ["createdByUserId"] = ObjectId.Parse(_adminId),
            ["updatedByUserId"] = ObjectId.Parse(_adminId),
            ["createdAtUtc"] = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            ["updatedAtUtc"] = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            ["isDeleted"] = false
        };
        await _database.GetCollection<BsonDocument>("work_assignment_reports")
            .InsertOneAsync(reportFixture, cancellationToken: ct);

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-03-fixtures.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                forms = fixtures.Select(fixture => new
                {
                    fixture.Key,
                    fixture.Form.Id,
                    fixture.Form.Code,
                    blockCount = fixture.Blocks.Count,
                    metricCount = fixture.Blocks.Sum(block => block.Metrics.Count),
                    legacyAdapter = fixture.LegacyBlock is not null
                }),
                labels = new[]
                {
                    _tableMetricLabelA.LabelId,
                    _tableMetricLabelB.LabelId,
                    _tableMetricInactive.LabelId,
                    _tableMetricWrongUsage.LabelId,
                    _tableMetricWrongType.LabelId,
                    _tableRowLabelA.LabelId,
                    _tableRowLabelB.LabelId,
                    _tableRowInactive.LabelId,
                    _tableRowWrongUsage.LabelId,
                    _tableRowWrongType.LabelId
                },
                existingReportId = _tableReportId,
                fixtureWritesOutsideCaseDeltas = true
            },
            ct);
    }

    private async Task SeedP803LabelsAsync(CancellationToken ct)
    {
        if (_p803LabelsSeeded)
            return;

        var actor = Actor("system_admin");
        (_, _tableMetricLabelA) = await CreateLabelAsync(actor, "p8-tbl-setup-metric-a",
            LabelPayload("p8.tbl.metric.a", "P8 table metric A", "GLOBAL", null,
                usage: "STATISTIC", dataType: "NUMBER"), ct);
        (_, _tableMetricLabelB) = await CreateLabelAsync(actor, "p8-tbl-setup-metric-b",
            LabelPayload("p8.tbl.metric.b", "P8 table metric B", "GLOBAL", null,
                usage: "TABLE_TARGET", dataType: "NUMBER"), ct);
        var (_, inactiveMetric) = await CreateLabelAsync(actor, "p8-tbl-setup-metric-inactive-create",
            LabelPayload("p8.tbl.metric.inactive", "P8 table inactive metric", "GLOBAL", null,
                usage: "TABLE_TARGET", dataType: "NUMBER"), ct);
        (_, _tableMetricInactive) = await UpdateLabelAsync(actor, inactiveMetric,
            "p8-tbl-setup-metric-inactive-disable",
            LabelPayload("p8.tbl.metric.inactive", "P8 table inactive metric", "GLOBAL", null,
                usage: "TABLE_TARGET", dataType: "NUMBER", isActive: false), ct);
        (_, _tableMetricWrongUsage) = await CreateLabelAsync(actor, "p8-tbl-setup-metric-usage",
            LabelPayload("p8.tbl.metric.usage", "P8 table wrong metric usage", "GLOBAL", null,
                usage: "STATISTIC", dataType: "NUMBER"), ct);
        (_, _tableMetricWrongType) = await CreateLabelAsync(actor, "p8-tbl-setup-metric-type",
            LabelPayload("p8.tbl.metric.type", "P8 table wrong metric type", "GLOBAL", null,
                usage: "TABLE_TARGET", dataType: "BOOLEAN"), ct);
        (_, _tableRowLabelA) = await CreateLabelAsync(actor, "p8-tbl-setup-row-a",
            LabelPayload("p8.tbl.row.a", "P8 table row A", "GLOBAL", null,
                usage: "TABLE_TARGET", dataType: "NUMBER"), ct);
        (_, _tableRowLabelB) = await CreateLabelAsync(actor, "p8-tbl-setup-row-b",
            LabelPayload("p8.tbl.row.b", "P8 table row B", "GLOBAL", null,
                usage: "TABLE_TARGET", dataType: "NUMBER"), ct);
        var (_, inactiveRow) = await CreateLabelAsync(actor, "p8-tbl-setup-row-inactive-create",
            LabelPayload("p8.tbl.row.inactive", "P8 table inactive row", "GLOBAL", null,
                usage: "TABLE_TARGET", dataType: "NUMBER"), ct);
        (_, _tableRowInactive) = await UpdateLabelAsync(actor, inactiveRow,
            "p8-tbl-setup-row-inactive-disable",
            LabelPayload("p8.tbl.row.inactive", "P8 table inactive row", "GLOBAL", null,
                usage: "TABLE_TARGET", dataType: "NUMBER", isActive: false), ct);
        (_, _tableRowWrongUsage) = await CreateLabelAsync(actor, "p8-tbl-setup-row-usage",
            LabelPayload("p8.tbl.row.usage", "P8 table wrong row usage", "GLOBAL", null,
                usage: "STATISTIC", dataType: "NUMBER"), ct);
        (_, _tableRowWrongType) = await CreateLabelAsync(actor, "p8-tbl-setup-row-type",
            LabelPayload("p8.tbl.row.type", "P8 table wrong row type", "GLOBAL", null,
                usage: "TABLE_TARGET", dataType: "BOOLEAN"), ct);
        _p803LabelsSeeded = true;
    }

    private P8TableFixture NewTableFixture(
        string key,
        IReadOnlyList<P8TableBlockFixture> blocks,
        P8TableBlockFixture? legacyBlock = null)
    {
        var id = ObjectId.GenerateNewId().ToString();
        const string sectionsJson =
            "[{\"id\":\"main\",\"title\":\"P8 table section\",\"description\":null,\"tagCodes\":[],\"order\":0}]";
        var field = FixtureField("guard", "number");
        var fields = new JsonArray(new JsonObject
        {
            ["id"] = field.Id,
            ["sectionId"] = "main",
            ["key"] = field.Key,
            ["name"] = "P8 table guard field",
            ["type"] = field.SchemaType,
            ["required"] = false,
            ["order"] = 0,
            ["statisticLabelCodes"] = new JsonArray(),
            ["isStatistic"] = false
        });
        var blockNodes = new JsonArray(
            blocks.Select(block => (JsonNode)BuildBlockNode(block)).ToArray());
        var blocksJson = blockNodes.ToJsonString();
        var legacyJson = legacyBlock is null
            ? null
            : BuildBlockNode(legacyBlock).ToJsonString();
        var fixedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        var template = new DynamicFormTemplate
        {
            Id = id,
            Code = $"P8_TBL_{key.ToUpperInvariant()}",
            Name = $"P8 table fixture {key}",
            Description = $"P8-TBL-{key} isolated integration fixture",
            TagCodes = [],
            CreatedByUsername = Actor("system_admin").Username,
            SchemaVersion = 1,
            VersionNo = 1,
            FamilyId = id,
            PreviousVersionId = null,
            ClonedFromVersionId = null,
            LineageStatus = DynamicFormLineageStatuses.Root,
            Revision = 1,
            IsActive = true,
            IsPublished = false,
            SectionsJson = sectionsJson,
            FieldsJson = fields.ToJsonString(),
            ExcelBlockJson = legacyJson,
            BlocksJson = blocksJson,
            CreatedByUserId = _adminId,
            UpdatedByUserId = _adminId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        var form = new P8FormFixture(
            key,
            id,
            template.Code,
            template.Name,
            template.Description,
            template.SchemaVersion,
            template.Revision,
            sectionsJson,
            template.FieldsJson,
            blocksJson,
            [field],
            null,
            null,
            template);
        return new P8TableFixture(key, form, blocks, legacyBlock);
    }

    private static P8TableBlockFixture TableBlock(
        string blockId,
        string tableMode,
        IReadOnlyList<P8TableMetricFixture> metrics,
        bool statisticsDisabled = false,
        string rowLabelDataType = "NUMBER",
        bool reverseIndexMap = false)
        => new(
            blockId,
            tableMode,
            statisticsDisabled,
            rowLabelDataType,
            metrics,
            reverseIndexMap);

    private static P8TableMetricFixture TableMetricFixture(
        string metricKey,
        string dataType,
        IReadOnlyList<string>? aggregateOps = null)
        => new(metricKey, dataType, aggregateOps);

    private static JsonObject BuildBlockNode(P8TableBlockFixture block)
    {
        var width = Math.Max(1, block.Metrics.Count);
        var orderedForIndex = block.ReverseIndexMap
            ? block.Metrics.Reverse().ToArray()
            : block.Metrics.ToArray();
        var indexMap = new JsonArray(orderedForIndex.Select((metric, index) =>
            (JsonNode)new JsonObject
            {
                ["index"] = index,
                ["rowKey"] = $"row:{index:00}",
                ["columnKey"] = $"column:{index:00}",
                ["metricKey"] = metric.MetricKey,
                ["dataType"] = metric.DataType
            }).ToArray());
        var metricRules = new JsonArray(block.Metrics.Select((metric, index) =>
            (JsonNode)new JsonObject
            {
                ["metricKey"] = metric.MetricKey,
                ["dataType"] = metric.DataType,
                ["sourceType"] = block.TableMode switch
                {
                    "APPEND_ROWS" => "COLUMN",
                    "APPEND_COLUMNS" => "ROW",
                    _ => "CELL"
                },
                ["rowKey"] = $"row:{index:00}",
                ["columnKey"] = $"column:{index:00}",
                ["aggregateOps"] = new JsonArray(
                    (metric.AggregateOps ?? Array.Empty<string>())
                    .Select(value => JsonValue.Create(value)).ToArray())
            }).ToArray());
        var node = new JsonObject
        {
            ["blockId"] = block.BlockId,
            ["sectionId"] = "main",
            ["name"] = $"P8 {block.BlockId}",
            ["tableMode"] = block.TableMode,
            ["rowLabelDataType"] = block.RowLabelDataType,
            ["w"] = width,
            ["h"] = 1,
            ["dataRect"] = new JsonObject
            {
                ["r0"] = 0,
                ["c0"] = 0,
                ["r1"] = 0,
                ["c1"] = width - 1
            },
            ["statisticsInputCellCount"] = block.Metrics.Count,
            ["statisticsInputCellLimit"] = 250,
            ["statisticsDisabled"] = block.StatisticsDisabled,
            ["indexMap"] = indexMap,
            ["metricRules"] = metricRules,
            ["metricLabelTargets"] = new JsonArray(),
            ["allowedRowLabelCodes"] = new JsonArray()
        };
        if (block.StatisticsDisabled)
            node["statisticsDisabledReason"] = "P8_STATISTIC_CONFIG_DISABLED";
        if (block.TableMode == "SUMMARY_TEMPLATE")
        {
            node["sourceBlockId"] = "summary-source";
            node["outputLayout"] = new JsonObject
            {
                ["rowLayout"] = new JsonArray(new JsonObject
                {
                    ["repeatFor"] = "selectedUnits",
                    ["rowsPerUnit"] = 1,
                    ["metrics"] = new JsonArray(
                        block.Metrics.Select(metric => JsonValue.Create(metric.MetricKey)).ToArray())
                })
            };
        }
        return node;
    }

    private P8TableFixture TableFixture(string key)
        => _tableFixtures.TryGetValue(key, out var fixture)
            ? fixture
            : throw new HarnessCaseNotRunnableException(
                $"P8 table fixture {key} was not seeded.");
}

internal sealed record P8TableMetricFixture(
    string MetricKey,
    string DataType,
    IReadOnlyList<string>? AggregateOps);

internal sealed record P8TableBlockFixture(
    string BlockId,
    string TableMode,
    bool StatisticsDisabled,
    string RowLabelDataType,
    IReadOnlyList<P8TableMetricFixture> Metrics,
    bool ReverseIndexMap);

internal sealed record P8TableFixture(
    string Key,
    P8FormFixture Form,
    IReadOnlyList<P8TableBlockFixture> Blocks,
    P8TableBlockFixture? LegacyBlock)
{
    public P8TableBlockFixture Block(string blockId)
        => Blocks.Single(block => string.Equals(
            block.BlockId,
            blockId,
            StringComparison.Ordinal));
}
