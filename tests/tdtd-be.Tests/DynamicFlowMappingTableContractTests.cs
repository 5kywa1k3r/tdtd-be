using System.Text.Json;
using System.Text.Json.Nodes;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

internal static class DynamicFlowMappingTableContractTests
{
    private sealed record ContractCase(string Id, string Semantic, Action Run);

    private const string SourceFormId = "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string TargetFormId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SourceStepId = "source-step";
    private const string TargetStepId = "target-step";

    private static readonly DateTime FrozenNowUtc =
        new(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<ContractCase> Cases =
    [
        new("MAP-TABLE-01", "FIXED_GRID maps by exact row/column/value-slot identity", FixedGridExactIdentity),
        new("MAP-TABLE-02", "FIXED_GRID source row and slot reorder is invariant", FixedGridSourceReorderInvariant),
        new("MAP-TABLE-03", "FIXED_GRID target property and slot order does not change semantic diff", FixedGridPropertyOrderInvariant),
        new("MAP-TABLE-04", "FIXED_GRID missing source row identity fails before projection", FixedGridMissingSourceIdentity),
        new("MAP-TABLE-05", "FIXED_GRID duplicate source slot identity fails before projection", FixedGridDuplicateSourceIdentity),
        new("MAP-TABLE-06", "FIXED_GRID missing target row slot fails before projection", FixedGridMissingTargetIdentity),
        new("MAP-TABLE-07", "FIXED_GRID duplicate target row slot fails before projection", FixedGridDuplicateTargetIdentity),
        new("MAP-TABLE-08", "FIXED_GRID cell evaluation rejects numeric-string coercion", FixedGridRejectsNumericString),
        new("MAP-TABLE-09", "FIXED_GRID SKIP null policy preserves sparse target coordinates", FixedGridSkipNull),
        new("MAP-TABLE-10", "FIXED_GRID preview, diff, and repeated draft projection stay aligned", FixedGridProjectionParity),
        new("MAP-TABLE-11", "APPEND_ROWS business key is scoped by mapping id and version", AppendRowsScopedBusinessKey),
        new("MAP-TABLE-12", "APPEND_ROWS exact retry upserts without duplicate rows", AppendRowsExactRetry),
        new("MAP-TABLE-13", "APPEND_ROWS duplicate source key fails with the frozen code", AppendRowsDuplicateSourceKey),
        new("MAP-TABLE-14", "APPEND_ROWS source reorder produces the same canonical rows", AppendRowsReorderInvariant),
        new("MAP-TABLE-15", "MATRIX maps exact sparse coordinates and synchronizes projections", MatrixSynchronizesCoordinates),
        new("MAP-TABLE-16", "MATRIX sparse/null and malformed-coordinate semantics fail closed", MatrixSparseAndMalformedCoordinates),
        new("MAP-TABLE-17", "Canonical row-key join supports exact APPEND_COLUMNS source reads", CanonicalJoinAndAppendColumnsRead),
        new("MAP-TABLE-18", "SOURCE_REPORT evaluates each source report under its exact identity", SourceReportGrain),
        new("MAP-TABLE-19", "GROUP and custom joinKey are intentional blocks with exact codes", GroupAndCustomJoinBlockers),
        new("MAP-TABLE-20", "Advanced target/cardinality blockers use the frozen support-matrix codes", AdvancedTargetBlockers)
    ];

    public static IReadOnlyDictionary<string, string> SemanticRegistry { get; } =
        Cases.ToDictionary(item => item.Id, item => item.Semantic, StringComparer.Ordinal);

    public static void Run()
    {
        AssertEqual(20, Cases.Count, "MAP-TABLE case count");
        AssertEqual(20, SemanticRegistry.Count, "MAP-TABLE unique semantic registry count");
        for (var number = 1; number <= 20; number++)
        {
            var id = $"MAP-TABLE-{number:00}";
            AssertTrue(SemanticRegistry.ContainsKey(id), $"semantic registry must contain {id}");
        }

        foreach (var contractCase in Cases)
        {
            try
            {
                contractCase.Run();
                Console.WriteLine($"PASS {contractCase.Id} {contractCase.Semantic}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{contractCase.Id} ({contractCase.Semantic}) failed: {ex.Message}",
                    ex);
            }
        }
    }

    private static void FixedGridExactIdentity()
    {
        var target = Target(FixedGridTarget(
            """
            [
              {"index":0,"rowKey":"r2","columnKey":"mapped"},
              {"index":1,"rowKey":"r1","columnKey":"mapped"}
            ]
            """,
            "[null,null]"));
        var source = Source(FixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[10,20]"));

        var preview = Preview(target, [source], [TableRule()]);
        AssertValues(preview, "target", 20m, 10m);
        AssertEqual(2, preview.Changes.Count(change => change.Status == "APPLIED"), "exact slot writes");
        AssertFalse(preview.HasBlockingConflicts, "valid fixed-grid projection");
    }

    private static void FixedGridSourceReorderInvariant()
    {
        var target = Target(FixedGridTarget(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"mapped"},
              {"index":1,"rowKey":"r2","columnKey":"mapped"}
            ]
            """,
            "[null,null]"));
        var first = Source(FixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[10,20]"));
        var reordered = Source(FixedGridSource(
            """
            [
              {"columnKey":"amount","rowKey":"r2","index":1},
              {"rowKey":"r1","index":0,"columnKey":"amount"}
            ]
            """,
            "[10,20]"));

        var a = Preview(target, [first], [TableRule()]);
        var b = Preview(target, [reordered], [TableRule()]);
        AssertEqual(TableSemantic(a), TableSemantic(b), "source reorder semantic projection");
    }

    private static void FixedGridPropertyOrderInvariant()
    {
        var firstTarget = Target(
            """
            {
              "blocks":[{
                "blockId":"target",
                "tableMode":"FIXED_GRID",
                "values1D":[null,null],
                "valueSlots":[
                  {"index":0,"rowKey":"r1","columnKey":"mapped"},
                  {"index":1,"rowKey":"r2","columnKey":"mapped"}
                ]
              }]
            }
            """);
        var reorderedTarget = Target(
            """
            {
              "blocks":[{
                "valueSlots":[
                  {"columnKey":"mapped","rowKey":"r2","index":1},
                  {"rowKey":"r1","columnKey":"mapped","index":0}
                ],
                "values1D":[null,null],
                "tableMode":"FIXED_GRID",
                "blockId":"target"
              }]
            }
            """);
        var source = Source(FixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[10,20]"));

        var a = Preview(firstTarget, [source], [TableRule()]);
        var b = Preview(reorderedTarget, [source], [TableRule()]);
        AssertEqual(TableSemantic(a), TableSemantic(b), "target property/slot order semantic diff");
    }

    private static void FixedGridMissingSourceIdentity()
    {
        var target = Target(StandardFixedGridTarget());
        var source = Source(FixedGridSource(
            """[{"index":0,"columnKey":"amount"}]""",
            "[10]"));
        AssertConflictWithoutMutation(
            target,
            Preview(target, [source], [TableRule()]),
            DynamicFlowMappingTableFailureReasons.FixedGridIdentityInvalid);
    }

    private static void FixedGridDuplicateSourceIdentity()
    {
        var target = Target(StandardFixedGridTarget());
        var source = Source(FixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r1","columnKey":"amount"}
            ]
            """,
            "[10,11]"));
        AssertConflictWithoutMutation(
            target,
            Preview(target, [source], [TableRule()]),
            DynamicFlowMappingTableFailureReasons.FixedGridIdentityInvalid);
    }

    private static void FixedGridMissingTargetIdentity()
    {
        var target = Target(FixedGridTarget(
            """[{"index":0,"rowKey":"r1","columnKey":"mapped"}]""",
            "[null]"));
        var source = Source(FixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[10,20]"));
        AssertConflictWithoutMutation(
            target,
            Preview(target, [source], [TableRule()]),
            DynamicFlowMappingTableFailureReasons.FixedGridIdentityInvalid);
    }

    private static void FixedGridDuplicateTargetIdentity()
    {
        var target = Target(FixedGridTarget(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"mapped"},
              {"index":1,"rowKey":"r1","columnKey":"mapped"}
            ]
            """,
            "[null,null]"));
        var source = Source(FixedGridSource(
            """[{"index":0,"rowKey":"r1","columnKey":"amount"}]""",
            "[10]"));
        AssertConflictWithoutMutation(
            target,
            Preview(target, [source], [TableRule()]),
            DynamicFlowMappingTableFailureReasons.FixedGridIdentityInvalid);
    }

    private static void FixedGridRejectsNumericString()
    {
        var target = Target(FixedGridTarget(
            """[{"index":0,"rowKey":"r1","columnKey":"mapped"}]""",
            "[null]"));
        var source = Source(FixedGridSource(
            """[{"index":0,"rowKey":"r1","columnKey":"amount"}]""",
            """["10"]"""));
        AssertConflictWithoutMutation(
            target,
            Preview(target, [source], [TableRule()]),
            DynamicFlowMappingFieldFailureReasons.ValueKindInvalid);
    }

    private static void FixedGridSkipNull()
    {
        var target = Target(StandardFixedGridTarget());
        var source = Source(FixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[null,7]"));
        var preview = Preview(
            target,
            [source],
            [TableRule(nullPolicy: "SKIP")]);

        AssertValues(preview, "target", null, 7m);
        AssertEqual(1, preview.Changes.Count, "sparse SKIP change count");
        AssertEqual("r2", preview.Changes[0].Sources.Single().RowKey, "sparse source row identity");
    }

    private static void FixedGridProjectionParity()
    {
        var target = Target(StandardFixedGridTarget());
        var source = Source(FixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[10,20]"));

        var first = Preview(target, [source], [TableRule()]);
        AssertEqual("[10,20]", ValuesJson(first, "target"), "first mapped draft projection");
        AssertEqual("10", first.Changes[0].NextValueJson, "first diff matches values1D");
        AssertEqual(StandardFixedGridTarget(), target.TableValuesJson, "preview must not mutate target aggregate");

        var retryTarget = Target(first.TableValuesJson!);
        var second = Preview(retryTarget, [source], [TableRule()]);
        AssertEqual(Canonical(first.TableValuesJson), Canonical(second.TableValuesJson), "repeat projection parity");
        AssertTrue(second.Changes.All(change => change.Status == "UNCHANGED"), "repeat diff is unchanged");
    }

    private static void AppendRowsScopedBusinessKey()
    {
        var target = Target(AppendTarget());
        var source = Source(AppendSource(
            """
            [
              {"rowKey":"r1","cells":{"amount":5}}
            ]
            """));
        var firstRule = TableRule(mappingId: "map-a", targetBlockId: "append");
        var secondRule = TableRule(mappingId: "map-b", targetBlockId: "append");

        var preview = Preview(target, [source], [firstRule, secondRule]);
        var rows = Rows(preview, "append");
        AssertEqual(2, rows.Count, "same source row under two mapping identities");
        AssertEqual(2, rows.Select(RowBusinessKey).Distinct(StringComparer.Ordinal).Count(), "scoped business keys");
        AssertTrue(rows.Any(row => RowBusinessKey(row).Contains("map-a", StringComparison.Ordinal)), "map-a scope");
        AssertTrue(rows.Any(row => RowBusinessKey(row).Contains("map-b", StringComparison.Ordinal)), "map-b scope");
    }

    private static void AppendRowsExactRetry()
    {
        var source = Source(AppendSource(
            """
            [
              {"rowKey":"r2","cells":{"amount":20}},
              {"rowKey":"r1","cells":{"amount":10}}
            ]
            """));
        var first = Preview(
            Target(AppendTarget()),
            [source],
            [TableRule(mappingId: "append-map", mappingVersion: 3, targetBlockId: "append")]);
        var second = Preview(
            Target(first.TableValuesJson!),
            [source],
            [TableRule(mappingId: "append-map", mappingVersion: 3, targetBlockId: "append")]);

        AssertEqual(2, Rows(first, "append").Count, "initial append rows");
        AssertEqual(2, Rows(second, "append").Count, "retry row count");
        AssertEqual(Canonical(first.TableValuesJson), Canonical(second.TableValuesJson), "exact retry table bytes semantically");
        AssertTrue(second.Changes.All(change => change.Status == "UNCHANGED"), "exact retry changes");
    }

    private static void AppendRowsDuplicateSourceKey()
    {
        var target = Target(AppendTarget());
        var source = Source(AppendSource(
            """
            [
              {"rowKey":"dup","cells":{"amount":10}},
              {"rowKey":"dup","cells":{"amount":20}}
            ]
            """));
        AssertConflictWithoutMutation(
            target,
            Preview(target, [source], [TableRule(targetBlockId: "append")]),
            DynamicFlowMappingTableFailureReasons.DuplicateRowKey);
    }

    private static void AppendRowsReorderInvariant()
    {
        var target = Target(AppendTarget());
        var firstSource = Source(AppendSource(
            """
            [
              {"rowKey":"r2","cells":{"amount":20}},
              {"rowKey":"r1","cells":{"amount":10}}
            ]
            """));
        var reorderedSource = Source(AppendSource(
            """
            [
              {"cells":{"amount":10},"rowKey":"r1"},
              {"cells":{"amount":20},"rowKey":"r2"}
            ]
            """));
        var rule = TableRule(mappingId: "append-map", targetBlockId: "append");

        var a = Preview(target, [firstSource], [rule]);
        var b = Preview(target, [reorderedSource], [rule]);
        AssertEqual(Canonical(a.TableValuesJson), Canonical(b.TableValuesJson), "append row reorder");
        AssertEqual(TableSemantic(a), TableSemantic(b), "append semantic changes");
    }

    private static void MatrixSynchronizesCoordinates()
    {
        var target = Target(MatrixTarget());
        var source = Source(
            """
            {
              "blocks":[{
                "blockId":"source",
                "tableMode":"MATRIX",
                "cells":[
                  {"columnKey":"amount","rowKey":"r2","value":20},
                  {"value":10,"rowKey":"r1","columnKey":"amount"}
                ]
              }]
            }
            """);

        var preview = Preview(target, [source], [TableRule(targetBlockId: "matrix")]);
        AssertValues(preview, "matrix", 10m, 20m);
        var cells = Cells(preview, "matrix");
        AssertEqual(2, cells.Count, "matrix synchronized cells");
        AssertEqual(10m, CellValue(cells, "r1", "mapped"), "matrix r1");
        AssertEqual(20m, CellValue(cells, "r2", "mapped"), "matrix r2");
    }

    private static void MatrixSparseAndMalformedCoordinates()
    {
        var sparseSource = Source(
            """
            {
              "blocks":[{
                "blockId":"source",
                "tableMode":"MATRIX",
                "cells":[{"rowKey":"r2","columnKey":"amount","value":7}]
              }]
            }
            """);
        var sparse = Preview(
            Target(MatrixTarget()),
            [sparseSource],
            [TableRule(targetBlockId: "matrix", nullPolicy: "SKIP")]);
        AssertValues(sparse, "matrix", null, 7m);
        AssertEqual(1, sparse.Changes.Count, "sparse matrix change count");

        var target = Target(MatrixTarget());
        var malformed = Source(
            """
            {
              "blocks":[{
                "blockId":"source",
                "tableMode":"MATRIX",
                "cells":[{"columnKey":"amount","value":7}]
              }]
            }
            """);
        AssertConflictWithoutMutation(
            target,
            Preview(target, [malformed], [TableRule(targetBlockId: "matrix")]),
            DynamicFlowMappingTableFailureReasons.MatrixCoordinateInvalid);
    }

    private static void CanonicalJoinAndAppendColumnsRead()
    {
        var source = Source(
            """
            {
              "blocks":[{
                "blockId":"source",
                "tableMode":"APPEND_COLUMNS",
                "columns":[
                  {"columnKey":"factor","cells":{"r2":3,"r1":2}},
                  {"columnKey":"amount","cells":{"r2":20,"r1":10}}
                ]
              }]
            }
            """);
        var rule = TableRule(targetBlockId: "append");
        rule.Inputs =
        [
            TableInput("amount", "amount"),
            TableInput("factor", "factor")
        ];
        rule.Calculation = new DynamicFlowMappingCalculationDto
        {
            Kind = "DIRECT",
            Operation = "multiply",
            ResultDataType = "NUMBER"
        };

        var preview = Preview(Target(AppendTarget()), [source], [rule]);
        var rows = Rows(preview, "append");
        AssertEqual(2, rows.Count, "joined append-column rows");
        AssertEqual(20m, RowCell(rows, "r1", "mapped"), "joined r1");
        AssertEqual(60m, RowCell(rows, "r2", "mapped"), "joined r2");
    }

    private static void SourceReportGrain()
    {
        var first = SourceWithField("000000000000000000000011", 11m);
        var second = SourceWithField("000000000000000000000010", 10m);
        var rule = new DynamicFlowMappingRuleDto
        {
            MappingId = "source-report-map",
            MappingVersion = 1,
            EvaluationGrain = "SOURCE_REPORT",
            ErrorPolicy = "BLOCK_APPLY",
            ConflictPolicy = "OVERWRITE",
            Inputs =
            [
                new DynamicFlowMappingInputDto
                {
                    InputKey = "amount",
                    Source = new DynamicFlowMappingEndpointDto
                    {
                        Kind = "FIELD",
                        DynamicFormTemplateId = SourceFormId,
                        StepId = SourceStepId,
                        FieldKey = "amount",
                        DataType = "NUMBER"
                    },
                    DataType = "NUMBER",
                    Cardinality = "ONE",
                    NullPolicy = "ERROR"
                }
            ],
            Target = TableTarget("append", "mapped"),
            Calculation = CopyCalculation()
        };

        var preview = Preview(Target(AppendTarget()), [first, second], [rule]);
        var rows = Rows(preview, "append");
        AssertEqual(2, rows.Count, "source-report rows");
        AssertSequenceEqual(
            new[] { "000000000000000000000010", "000000000000000000000011" },
            rows.Select(RowKey).ToArray(),
            "source-report deterministic identities");
        AssertEqual(10m, RowCell(rows, "000000000000000000000010", "mapped"), "source report 10");
        AssertEqual(11m, RowCell(rows, "000000000000000000000011", "mapped"), "source report 11");
    }

    private static void GroupAndCustomJoinBlockers()
    {
        var group = TableRule(targetBlockId: "append");
        group.EvaluationGrain = "GROUP";
        ExpectReason(
            () => DynamicFlowMappingEngine.ValidateP7Rules([group]),
            DynamicFlowMappingTableFailureReasons.GroupIntentionalBlock);

        var customJoin = TableRule(targetBlockId: "append");
        customJoin.JoinKey = "departmentCode";
        ExpectReason(
            () => DynamicFlowMappingEngine.ValidateP7Rules([customJoin]),
            DynamicFlowMappingTableFailureReasons.CustomJoinKeyIntentionalBlock);
    }

    private static void AdvancedTargetBlockers()
    {
        var scalarToRow = TableRule(targetBlockId: "append");
        scalarToRow.EvaluationGrain = "FLOW_INSTANCE";
        ExpectReason(
            () => DynamicFlowMappingEngine.ValidateP7Rules([scalarToRow]),
            DynamicFlowMappingTableFailureReasons.ScalarToRowIntentionalBlock);

        var rowToReport = TableRule();
        rowToReport.Target = new DynamicFlowMappingEndpointDto
        {
            Kind = "FIELD",
            DynamicFormTemplateId = TargetFormId,
            StepId = TargetStepId,
            FieldKey = "total",
            DataType = "NUMBER"
        };
        ExpectReason(
            () => DynamicFlowMappingEngine.ValidateP7Rules([rowToReport]),
            DynamicFlowMappingTableFailureReasons.RowToReportIntentionalBlock);

        var source = Source(AppendSource(
            """[{"rowKey":"r1","cells":{"amount":5}}]"""));
        var appendColumnsTarget = Target(
            """{"blocks":[{"blockId":"blocked","tableMode":"APPEND_COLUMNS","columns":[]}]}""");
        AssertConflictWithoutMutation(
            appendColumnsTarget,
            Preview(
                appendColumnsTarget,
                [source],
                [TableRule(targetBlockId: "blocked")]),
            DynamicFlowMappingTableFailureReasons.AppendColumnsTargetIntentionalBlock);

        var summaryTarget = Target(
            """{"blocks":[{"blockId":"blocked","tableMode":"SUMMARY_TEMPLATE","rows":[]}]}""");
        AssertConflictWithoutMutation(
            summaryTarget,
            Preview(
                summaryTarget,
                [source],
                [TableRule(targetBlockId: "blocked")]),
            DynamicFlowMappingTableFailureReasons.SummaryTemplateReadOnly);
    }

    private static DynamicFlowMappingPreviewResponse Preview(
        WorkAssignmentReport target,
        IReadOnlyList<DynamicFlowMappingSourceReport> sources,
        IReadOnlyList<DynamicFlowMappingRuleDto> rules)
        => DynamicFlowMappingEngine.Preview(
            target,
            sources,
            rules,
            requestConflictPolicy: null,
            requestContributionPolicy: null,
            nowUtc: FrozenNowUtc,
            targetStepId: TargetStepId,
            targetStepCode: "TARGET",
            enforceP7Contract: true);

    private static DynamicFlowMappingRuleDto TableRule(
        string mappingId = "table-map",
        int mappingVersion = 1,
        string sourceBlockId = "source",
        string sourceColumnKey = "amount",
        string targetBlockId = "target",
        string targetColumnKey = "mapped",
        string nullPolicy = "ERROR")
        => new()
        {
            MappingId = mappingId,
            MappingVersion = mappingVersion,
            EvaluationGrain = "TABLE_ROW",
            ErrorPolicy = "BLOCK_APPLY",
            ConflictPolicy = "OVERWRITE",
            Inputs = [TableInput("amount", sourceColumnKey, sourceBlockId, nullPolicy)],
            Target = TableTarget(targetBlockId, targetColumnKey),
            Calculation = CopyCalculation()
        };

    private static DynamicFlowMappingInputDto TableInput(
        string inputKey,
        string columnKey,
        string blockId = "source",
        string nullPolicy = "ERROR")
        => new()
        {
            InputKey = inputKey,
            Source = new DynamicFlowMappingEndpointDto
            {
                Kind = "TABLE_COLUMN",
                DynamicFormTemplateId = SourceFormId,
                StepId = SourceStepId,
                BlockId = blockId,
                ColumnKey = columnKey,
                DataType = "NUMBER"
            },
            DataType = "NUMBER",
            Cardinality = "ONE",
            NullPolicy = nullPolicy
        };

    private static DynamicFlowMappingEndpointDto TableTarget(string blockId, string columnKey)
        => new()
        {
            Kind = "TABLE_COLUMN",
            DynamicFormTemplateId = TargetFormId,
            StepId = TargetStepId,
            BlockId = blockId,
            ColumnKey = columnKey,
            DataType = "NUMBER"
        };

    private static DynamicFlowMappingCalculationDto CopyCalculation()
        => new()
        {
            Kind = "DIRECT",
            Operation = "copy",
            ResultDataType = "NUMBER"
        };

    private static WorkAssignmentReport Target(string tableValuesJson)
        => new()
        {
            Id = "100000000000000000000001",
            WorkId = "100000000000000000000002",
            WorkAssignmentId = "100000000000000000000003",
            WorkReportPeriodId = "100000000000000000000004",
            DynamicFormTemplateId = TargetFormId,
            AssigneeUserId = "100000000000000000000005",
            PeriodKey = "2026-07",
            PeriodInstanceKey = "2026-07",
            FieldValuesJson = """{"values":{}}""",
            TableValuesJson = tableValuesJson
        };

    private static DynamicFlowMappingSourceReport Source(
        string tableValuesJson,
        string reportId = "200000000000000000000001")
    {
        var report = new WorkAssignmentReport
        {
            Id = reportId,
            WorkId = "100000000000000000000002",
            WorkAssignmentId = "200000000000000000000002",
            WorkReportPeriodId = "200000000000000000000003",
            DynamicFormTemplateId = SourceFormId,
            AssigneeUserId = "200000000000000000000004",
            PeriodKey = "2026-07",
            PeriodInstanceKey = "2026-07",
            FieldValuesJson = """{"values":{}}""",
            TableValuesJson = tableValuesJson
        };
        return new DynamicFlowMappingSourceReport(
            report,
            SourceStepId,
            "SOURCE",
            report.FieldValuesJson,
            report.TableValuesJson);
    }

    private static DynamicFlowMappingSourceReport SourceWithField(string reportId, decimal amount)
    {
        var source = Source("""{"blocks":[]}""", reportId);
        source.Report.FieldValuesJson = JsonSerializer.Serialize(new
        {
            values = new { amount }
        });
        return source with { FieldValuesJson = source.Report.FieldValuesJson };
    }

    private static string FixedGridSource(string slots, string values)
        => $$"""
        {
          "blocks":[{
            "blockId":"source",
            "tableMode":"FIXED_GRID",
            "values1D":{{values}},
            "valueSlots":{{slots}}
          }]
        }
        """;

    private static string FixedGridTarget(string slots, string values)
        => $$"""
        {
          "blocks":[{
            "blockId":"target",
            "tableMode":"FIXED_GRID",
            "values1D":{{values}},
            "valueSlots":{{slots}}
          }]
        }
        """;

    private static string StandardFixedGridTarget()
        => FixedGridTarget(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"mapped"},
              {"index":1,"rowKey":"r2","columnKey":"mapped"}
            ]
            """,
            "[null,null]");

    private static string AppendTarget()
        => """{"blocks":[{"blockId":"append","tableMode":"APPEND_ROWS","rows":[]}]}""";

    private static string AppendSource(string rows)
        => $$"""
        {
          "blocks":[{
            "blockId":"source",
            "tableMode":"APPEND_ROWS",
            "rows":{{rows}}
          }]
        }
        """;

    private static string MatrixTarget()
        => """
        {
          "blocks":[{
            "blockId":"matrix",
            "tableMode":"MATRIX",
            "values1D":[null,null],
            "valueSlots":[
              {"index":0,"rowKey":"r1","columnKey":"mapped"},
              {"index":1,"rowKey":"r2","columnKey":"mapped"}
            ],
            "cells":[]
          }]
        }
        """;

    private static List<JsonElement> Rows(
        DynamicFlowMappingPreviewResponse response,
        string blockId)
        => Block(response, blockId)
            .GetProperty("rows")
            .EnumerateArray()
            .Select(row => row.Clone())
            .ToList();

    private static List<JsonElement> Cells(
        DynamicFlowMappingPreviewResponse response,
        string blockId)
        => Block(response, blockId)
            .GetProperty("cells")
            .EnumerateArray()
            .Select(cell => cell.Clone())
            .ToList();

    private static JsonElement Block(
        DynamicFlowMappingPreviewResponse response,
        string blockId)
    {
        using var document = JsonDocument.Parse(response.TableValuesJson!);
        return document.RootElement
            .GetProperty("blocks")
            .EnumerateArray()
            .Single(block => block.GetProperty("blockId").GetString() == blockId)
            .Clone();
    }

    private static void AssertValues(
        DynamicFlowMappingPreviewResponse response,
        string blockId,
        params decimal?[] expected)
    {
        var values = Block(response, blockId).GetProperty("values1D");
        AssertEqual(expected.Length, values.GetArrayLength(), $"{blockId} values length");
        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index].HasValue)
                AssertEqual(expected[index]!.Value, values[index].GetDecimal(), $"{blockId} value {index}");
            else
                AssertEqual(JsonValueKind.Null, values[index].ValueKind, $"{blockId} null {index}");
        }
    }

    private static string ValuesJson(
        DynamicFlowMappingPreviewResponse response,
        string blockId)
        => Block(response, blockId).GetProperty("values1D").GetRawText();

    private static decimal CellValue(
        IReadOnlyCollection<JsonElement> cells,
        string rowKey,
        string columnKey)
        => cells.Single(cell =>
                cell.GetProperty("rowKey").GetString() == rowKey &&
                cell.GetProperty("columnKey").GetString() == columnKey)
            .GetProperty("value")
            .GetDecimal();

    private static string RowKey(JsonElement row)
        => row.GetProperty("rowKey").GetString()!;

    private static string RowBusinessKey(JsonElement row)
        => row.GetProperty("businessKey").GetString()!;

    private static decimal RowCell(
        IReadOnlyCollection<JsonElement> rows,
        string rowKey,
        string columnKey)
        => rows.Single(row => RowKey(row) == rowKey)
            .GetProperty("cells")
            .GetProperty(columnKey)
            .GetDecimal();

    private static string TableSemantic(DynamicFlowMappingPreviewResponse response)
    {
        var changes = response.Changes
            .OrderBy(change => change.MappingId, StringComparer.Ordinal)
            .ThenBy(change => change.MappingVersion)
            .ThenBy(change => change.Sources.FirstOrDefault()?.RowKey, StringComparer.Ordinal)
            .Select(change => new
            {
                change.MappingId,
                change.MappingVersion,
                change.TargetKey,
                change.PreviousValueJson,
                change.NextValueJson,
                change.Status,
                change.Reason,
                RowKey = change.Sources.FirstOrDefault()?.RowKey
            })
            .ToList();
        var root = JsonNode.Parse(response.TableValuesJson!)!.AsObject();
        var values = root["blocks"]!.AsArray()
            .OfType<JsonObject>()
            .OrderBy(block => block["blockId"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(block => new
            {
                BlockId = block["blockId"]!.GetValue<string>(),
                Values = block["values1D"]?.DeepClone(),
                Rows = block["rows"]?.DeepClone(),
                Cells = block["cells"]?.DeepClone()
            })
            .ToList();
        return Canonical(JsonSerializer.Serialize(new { values, changes }));
    }

    private static void AssertConflictWithoutMutation(
        WorkAssignmentReport target,
        DynamicFlowMappingPreviewResponse response,
        string expectedReason)
    {
        AssertTrue(response.HasBlockingConflicts, $"expected conflict {expectedReason}");
        AssertEqual(expectedReason, response.Changes.Single().Reason, "stable table failure reason");
        AssertEqual(Canonical(target.TableValuesJson), Canonical(response.TableValuesJson), "failed projection is zero-write");
    }

    private static void ExpectReason(Action action, string expectedReason)
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected stable reason {expectedReason}.");
        }
        catch (DynamicFlowMappingEvaluationException ex)
        {
            AssertEqual(expectedReason, ex.Reason, "stable support-matrix reason");
        }
    }

    private static string Canonical(string? json)
        => DynamicFlowDefinitionPayloadContract.CanonicalizeJson(json ?? "null");

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertFalse(bool condition, string message)
    {
        if (condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}. Expected {expected}, got {actual}.");
    }

    private static void AssertSequenceEqual<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string message)
    {
        if (expected.Count != actual.Count || !expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{message}. Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }
}
