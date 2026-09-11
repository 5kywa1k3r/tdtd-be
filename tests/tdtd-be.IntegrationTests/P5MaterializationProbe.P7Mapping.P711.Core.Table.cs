using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Driver;
using tdtd_be.DTOs.DynamicFlows;
using tdtd_be.Models;
using tdtd_be.Services.DynamicFlows;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// P7-11 executable TABLE_ROW support-matrix evidence. The semantic checks
/// deliberately call the production mapping engine instead of translating a
/// different P7 case into MAP-TABLE evidence.
/// </summary>
internal static partial class P5MaterializationProbe
{
    private const string P711TableCapabilityPath =
        "api/capabilities/dynamic-form-flow";
    private const string P711TableSourceFormId =
        "bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string P711TableTargetFormId =
        "aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string P711TableSourceStepId = "source-step";
    private const string P711TableTargetStepId = "target-step";

    private static readonly DateTime P711TableFrozenNowUtc =
        new(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

    private static async Task RunP711TableCoreCasesAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        ApiHarnessClient api,
        IMongoDatabase database,
        string adminToken,
        P7MappingFixture fixture,
        CancellationToken ct)
    {
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-01",
            "FIXED_GRID maps by exact row/column/value-slot identity",
            P711TableAssertFixedGridExactIdentity,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-02",
            "FIXED_GRID source row and slot reorder is invariant",
            P711TableAssertFixedGridSourceReorderInvariant,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-03",
            "FIXED_GRID target property and slot order does not change semantic diff",
            P711TableAssertFixedGridPropertyOrderInvariant,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-04",
            "FIXED_GRID missing source row identity fails before projection",
            P711TableAssertFixedGridMissingSourceIdentity,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-05",
            "FIXED_GRID duplicate source slot identity fails before projection",
            P711TableAssertFixedGridDuplicateSourceIdentity,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-06",
            "FIXED_GRID missing target row slot fails before projection",
            P711TableAssertFixedGridMissingTargetIdentity,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-07",
            "FIXED_GRID duplicate target row slot fails before projection",
            P711TableAssertFixedGridDuplicateTargetIdentity,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-08",
            "FIXED_GRID cell evaluation rejects numeric-string coercion",
            P711TableAssertFixedGridRejectsNumericString,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-09",
            "FIXED_GRID SKIP null policy preserves sparse target coordinates",
            P711TableAssertFixedGridSkipNull,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-10",
            "FIXED_GRID preview, diff, and repeated draft projection stay aligned",
            P711TableAssertFixedGridProjectionParity,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-11",
            "APPEND_ROWS business key is scoped by mapping id and version",
            P711TableAssertAppendRowsScopedBusinessKey,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-12",
            "APPEND_ROWS exact retry upserts without duplicate rows",
            P711TableAssertAppendRowsExactRetry,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-13",
            "APPEND_ROWS duplicate source key fails with the frozen code",
            P711TableAssertAppendRowsDuplicateSourceKey,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-14",
            "APPEND_ROWS source reorder produces the same canonical rows",
            P711TableAssertAppendRowsReorderInvariant,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-15",
            "MATRIX maps exact sparse coordinates and synchronizes projections",
            P711TableAssertMatrixSynchronizesCoordinates,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-16",
            "MATRIX sparse/null and malformed-coordinate semantics fail closed",
            P711TableAssertMatrixSparseAndMalformedCoordinates,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-17",
            "Canonical row-key join supports exact APPEND_COLUMNS source reads",
            P711TableAssertCanonicalJoinAndAppendColumnsRead,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-18",
            "SOURCE_REPORT evaluates each source report under its exact identity",
            P711TableAssertSourceReportGrain,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-19",
            "GROUP and custom joinKey are intentional blocks with exact codes",
            P711TableAssertGroupAndCustomJoinBlockers,
            ct);
        await RunP711TableCoreCaseAsync(
            cases, mongoEvidence, api, database, adminToken, fixture,
            "MAP-TABLE-20",
            "Advanced target/cardinality blockers use the frozen support-matrix codes",
            P711TableAssertAdvancedTargetBlockers,
            ct);
    }

    private static async Task RunP711TableCoreCaseAsync(
        HarnessCaseRunner cases,
        List<object> mongoEvidence,
        ApiHarnessClient api,
        IMongoDatabase database,
        string adminToken,
        P7MappingFixture fixture,
        string caseId,
        string semantic,
        Action semanticAssertion,
        CancellationToken ct)
    {
        await cases.RunAsync(
            caseId,
            async () =>
            {
                ct.ThrowIfCancellationRequested();
                HarnessAssert.Equal(
                    caseId,
                    HarnessCaseRunner.ActiveCaseId,
                    $"{caseId} must execute under its own ActiveCaseId");

                var exchangeStart = api.Exchanges.Count;
                var capability = await api.GetAsync(
                    P711TableCapabilityPath,
                    adminToken,
                    ct: ct);
                ApiHarnessClient.ExpectStatus(
                    capability,
                    HttpStatusCode.OK,
                    $"{caseId} capability Kestrel probe");
                var ownedExchanges = api.Exchanges
                    .Skip(exchangeStart)
                    .ToList();
                HarnessAssert.Equal(
                    1,
                    ownedExchanges.Count,
                    $"{caseId} must own exactly one Kestrel exchange");
                HarnessAssert.Equal(
                    caseId,
                    ownedExchanges[0].CaseId,
                    $"{caseId} Kestrel exchange case tag");
                HarnessAssert.Equal(
                    P711TableCapabilityPath,
                    ownedExchanges[0].Path,
                    $"{caseId} Kestrel exchange path");

                var version = await database
                    .GetCollection<DynamicFlowTemplateVersion>(
                        "dynamic_flow_template_versions")
                    .Find(item => item.Id == fixture.VersionId)
                    .SingleAsync(ct);
                HarnessAssert.Equal(
                    fixture.VersionId,
                    version.Id,
                    $"{caseId} exact direct-Mongo version query");
                mongoEvidence.Add(new
                {
                    caseId,
                    source = "direct-mongo",
                    query = "dynamic_flow_template_versions._id == fixture.VersionId",
                    matchedDocumentId = version.Id,
                    version.PayloadHash,
                    version.MigrationState,
                    observedAt = "inside-case-before-production-semantic-assertion"
                });

                semanticAssertion();
                return new CaseObservation(
                    semantic,
                    P7MappingFingerprint(
                        caseId,
                        semantic,
                        version.Id,
                        version.PayloadHash,
                        version.MigrationState,
                        "KESTREL=1",
                        "DIRECT_MONGO=1"));
            });
    }

    private static void P711TableAssertFixedGridExactIdentity()
    {
        var target = P711TableTarget(P711TableFixedGridTarget(
            """
            [
              {"index":0,"rowKey":"r2","columnKey":"mapped"},
              {"index":1,"rowKey":"r1","columnKey":"mapped"}
            ]
            """,
            "[null,null]"));
        var source = P711TableSource(P711TableFixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[10,20]"));

        var preview = P711TablePreview(
            target,
            [source],
            [P711TableRule()]);
        P711TableAssertValues(preview, "target", 20m, 10m);
        HarnessAssert.Equal(
            2,
            preview.Changes.Count(change => change.Status == "APPLIED"),
            "MAP-TABLE-01 exact slot writes");
        HarnessAssert.True(
            !preview.HasBlockingConflicts,
            "MAP-TABLE-01 valid fixed-grid projection was blocked");
    }

    private static void P711TableAssertFixedGridSourceReorderInvariant()
    {
        var target = P711TableTarget(P711TableStandardFixedGridTarget());
        var first = P711TableSource(P711TableFixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[10,20]"));
        var reordered = P711TableSource(P711TableFixedGridSource(
            """
            [
              {"columnKey":"amount","rowKey":"r2","index":1},
              {"rowKey":"r1","index":0,"columnKey":"amount"}
            ]
            """,
            "[10,20]"));

        var firstPreview = P711TablePreview(
            target,
            [first],
            [P711TableRule()]);
        var reorderedPreview = P711TablePreview(
            target,
            [reordered],
            [P711TableRule()]);
        HarnessAssert.Equal(
            P711TableSemantic(firstPreview),
            P711TableSemantic(reorderedPreview),
            "MAP-TABLE-02 source reorder semantic projection");
    }

    private static void P711TableAssertFixedGridPropertyOrderInvariant()
    {
        var firstTarget = P711TableTarget(
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
        var reorderedTarget = P711TableTarget(
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
        var source = P711TableSource(P711TableFixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[10,20]"));

        var firstPreview = P711TablePreview(
            firstTarget,
            [source],
            [P711TableRule()]);
        var reorderedPreview = P711TablePreview(
            reorderedTarget,
            [source],
            [P711TableRule()]);
        HarnessAssert.Equal(
            P711TableSemantic(firstPreview),
            P711TableSemantic(reorderedPreview),
            "MAP-TABLE-03 target property/slot order semantic diff");
    }

    private static void P711TableAssertFixedGridMissingSourceIdentity()
    {
        var target = P711TableTarget(P711TableStandardFixedGridTarget());
        var source = P711TableSource(P711TableFixedGridSource(
            """[{"index":0,"columnKey":"amount"}]""",
            "[10]"));
        P711TableAssertConflictWithoutMutation(
            target,
            P711TablePreview(target, [source], [P711TableRule()]),
            DynamicFlowMappingTableFailureReasons.FixedGridIdentityInvalid,
            "MAP-TABLE-04");
    }

    private static void P711TableAssertFixedGridDuplicateSourceIdentity()
    {
        var target = P711TableTarget(P711TableStandardFixedGridTarget());
        var source = P711TableSource(P711TableFixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r1","columnKey":"amount"}
            ]
            """,
            "[10,11]"));
        P711TableAssertConflictWithoutMutation(
            target,
            P711TablePreview(target, [source], [P711TableRule()]),
            DynamicFlowMappingTableFailureReasons.FixedGridIdentityInvalid,
            "MAP-TABLE-05");
    }

    private static void P711TableAssertFixedGridMissingTargetIdentity()
    {
        var target = P711TableTarget(P711TableFixedGridTarget(
            """[{"index":0,"rowKey":"r1","columnKey":"mapped"}]""",
            "[null]"));
        var source = P711TableSource(P711TableFixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[10,20]"));
        P711TableAssertConflictWithoutMutation(
            target,
            P711TablePreview(target, [source], [P711TableRule()]),
            DynamicFlowMappingTableFailureReasons.FixedGridIdentityInvalid,
            "MAP-TABLE-06");
    }

    private static void P711TableAssertFixedGridDuplicateTargetIdentity()
    {
        var target = P711TableTarget(P711TableFixedGridTarget(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"mapped"},
              {"index":1,"rowKey":"r1","columnKey":"mapped"}
            ]
            """,
            "[null,null]"));
        var source = P711TableSource(P711TableFixedGridSource(
            """[{"index":0,"rowKey":"r1","columnKey":"amount"}]""",
            "[10]"));
        P711TableAssertConflictWithoutMutation(
            target,
            P711TablePreview(target, [source], [P711TableRule()]),
            DynamicFlowMappingTableFailureReasons.FixedGridIdentityInvalid,
            "MAP-TABLE-07");
    }

    private static void P711TableAssertFixedGridRejectsNumericString()
    {
        var target = P711TableTarget(P711TableFixedGridTarget(
            """[{"index":0,"rowKey":"r1","columnKey":"mapped"}]""",
            "[null]"));
        var source = P711TableSource(P711TableFixedGridSource(
            """[{"index":0,"rowKey":"r1","columnKey":"amount"}]""",
            """["10"]"""));
        P711TableAssertConflictWithoutMutation(
            target,
            P711TablePreview(target, [source], [P711TableRule()]),
            DynamicFlowMappingFieldFailureReasons.ValueKindInvalid,
            "MAP-TABLE-08");
    }

    private static void P711TableAssertFixedGridSkipNull()
    {
        var target = P711TableTarget(P711TableStandardFixedGridTarget());
        var source = P711TableSource(P711TableFixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[null,7]"));
        var preview = P711TablePreview(
            target,
            [source],
            [P711TableRule(nullPolicy: "SKIP")]);

        P711TableAssertValues(preview, "target", null, 7m);
        HarnessAssert.Equal(
            1,
            preview.Changes.Count,
            "MAP-TABLE-09 sparse SKIP change count");
        HarnessAssert.Equal(
            "r2",
            preview.Changes[0].Sources.Single().RowKey,
            "MAP-TABLE-09 sparse source row identity");
    }

    private static void P711TableAssertFixedGridProjectionParity()
    {
        var target = P711TableTarget(P711TableStandardFixedGridTarget());
        var source = P711TableSource(P711TableFixedGridSource(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"amount"},
              {"index":1,"rowKey":"r2","columnKey":"amount"}
            ]
            """,
            "[10,20]"));

        var first = P711TablePreview(
            target,
            [source],
            [P711TableRule()]);
        HarnessAssert.Equal(
            "[10,20]",
            P711TableValuesJson(first, "target"),
            "MAP-TABLE-10 first mapped draft projection");
        HarnessAssert.Equal(
            "10",
            first.Changes[0].NextValueJson,
            "MAP-TABLE-10 first diff matches values1D");
        HarnessAssert.Equal(
            P711TableStandardFixedGridTarget(),
            target.TableValuesJson,
            "MAP-TABLE-10 preview mutated target aggregate");

        var retryTarget = P711TableTarget(first.TableValuesJson!);
        var second = P711TablePreview(
            retryTarget,
            [source],
            [P711TableRule()]);
        HarnessAssert.Equal(
            P711TableCanonical(first.TableValuesJson),
            P711TableCanonical(second.TableValuesJson),
            "MAP-TABLE-10 repeat projection parity");
        HarnessAssert.True(
            second.Changes.All(change => change.Status == "UNCHANGED"),
            "MAP-TABLE-10 repeat diff is not unchanged");
    }

    private static void P711TableAssertAppendRowsScopedBusinessKey()
    {
        var target = P711TableTarget(P711TableAppendTarget());
        var source = P711TableSource(P711TableAppendSource(
            """
            [
              {"rowKey":"r1","cells":{"amount":5}}
            ]
            """));
        var firstRule = P711TableRule(
            mappingId: "map-a",
            targetBlockId: "append");
        var secondRule = P711TableRule(
            mappingId: "map-b",
            targetBlockId: "append");

        var preview = P711TablePreview(
            target,
            [source],
            [firstRule, secondRule]);
        var rows = P711TableRows(preview, "append");
        HarnessAssert.Equal(
            2,
            rows.Count,
            "MAP-TABLE-11 same source row under two mapping identities");
        HarnessAssert.Equal(
            2,
            rows.Select(P711TableRowBusinessKey)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            "MAP-TABLE-11 scoped business keys");
        HarnessAssert.True(
            rows.Any(row => P711TableRowBusinessKey(row)
                .Contains("map-a", StringComparison.Ordinal)),
            "MAP-TABLE-11 missing map-a scope");
        HarnessAssert.True(
            rows.Any(row => P711TableRowBusinessKey(row)
                .Contains("map-b", StringComparison.Ordinal)),
            "MAP-TABLE-11 missing map-b scope");
    }

    private static void P711TableAssertAppendRowsExactRetry()
    {
        var source = P711TableSource(P711TableAppendSource(
            """
            [
              {"rowKey":"r2","cells":{"amount":20}},
              {"rowKey":"r1","cells":{"amount":10}}
            ]
            """));
        var first = P711TablePreview(
            P711TableTarget(P711TableAppendTarget()),
            [source],
            [P711TableRule(
                mappingId: "append-map",
                mappingVersion: 3,
                targetBlockId: "append")]);
        var second = P711TablePreview(
            P711TableTarget(first.TableValuesJson!),
            [source],
            [P711TableRule(
                mappingId: "append-map",
                mappingVersion: 3,
                targetBlockId: "append")]);

        HarnessAssert.Equal(
            2,
            P711TableRows(first, "append").Count,
            "MAP-TABLE-12 initial append rows");
        HarnessAssert.Equal(
            2,
            P711TableRows(second, "append").Count,
            "MAP-TABLE-12 retry row count");
        HarnessAssert.Equal(
            P711TableCanonical(first.TableValuesJson),
            P711TableCanonical(second.TableValuesJson),
            "MAP-TABLE-12 exact retry table semantics");
        HarnessAssert.True(
            second.Changes.All(change => change.Status == "UNCHANGED"),
            "MAP-TABLE-12 exact retry changes are not unchanged");
    }

    private static void P711TableAssertAppendRowsDuplicateSourceKey()
    {
        var target = P711TableTarget(P711TableAppendTarget());
        var source = P711TableSource(P711TableAppendSource(
            """
            [
              {"rowKey":"dup","cells":{"amount":10}},
              {"rowKey":"dup","cells":{"amount":20}}
            ]
            """));
        P711TableAssertConflictWithoutMutation(
            target,
            P711TablePreview(
                target,
                [source],
                [P711TableRule(targetBlockId: "append")]),
            DynamicFlowMappingTableFailureReasons.DuplicateRowKey,
            "MAP-TABLE-13");
    }

    private static void P711TableAssertAppendRowsReorderInvariant()
    {
        var target = P711TableTarget(P711TableAppendTarget());
        var firstSource = P711TableSource(P711TableAppendSource(
            """
            [
              {"rowKey":"r2","cells":{"amount":20}},
              {"rowKey":"r1","cells":{"amount":10}}
            ]
            """));
        var reorderedSource = P711TableSource(P711TableAppendSource(
            """
            [
              {"cells":{"amount":10},"rowKey":"r1"},
              {"cells":{"amount":20},"rowKey":"r2"}
            ]
            """));
        var rule = P711TableRule(
            mappingId: "append-map",
            targetBlockId: "append");

        var first = P711TablePreview(target, [firstSource], [rule]);
        var reordered = P711TablePreview(
            target,
            [reorderedSource],
            [rule]);
        HarnessAssert.Equal(
            P711TableCanonical(first.TableValuesJson),
            P711TableCanonical(reordered.TableValuesJson),
            "MAP-TABLE-14 append row reorder");
        HarnessAssert.Equal(
            P711TableSemantic(first),
            P711TableSemantic(reordered),
            "MAP-TABLE-14 append semantic changes");
    }

    private static void P711TableAssertMatrixSynchronizesCoordinates()
    {
        var target = P711TableTarget(P711TableMatrixTarget());
        var source = P711TableSource(
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

        var preview = P711TablePreview(
            target,
            [source],
            [P711TableRule(targetBlockId: "matrix")]);
        P711TableAssertValues(preview, "matrix", 10m, 20m);
        var cells = P711TableCells(preview, "matrix");
        HarnessAssert.Equal(
            2,
            cells.Count,
            "MAP-TABLE-15 matrix synchronized cells");
        HarnessAssert.Equal(
            10m,
            P711TableCellValue(cells, "r1", "mapped"),
            "MAP-TABLE-15 matrix r1");
        HarnessAssert.Equal(
            20m,
            P711TableCellValue(cells, "r2", "mapped"),
            "MAP-TABLE-15 matrix r2");
    }

    private static void P711TableAssertMatrixSparseAndMalformedCoordinates()
    {
        var sparseSource = P711TableSource(
            """
            {
              "blocks":[{
                "blockId":"source",
                "tableMode":"MATRIX",
                "cells":[{"rowKey":"r2","columnKey":"amount","value":7}]
              }]
            }
            """);
        var sparse = P711TablePreview(
            P711TableTarget(P711TableMatrixTarget()),
            [sparseSource],
            [P711TableRule(
                targetBlockId: "matrix",
                nullPolicy: "SKIP")]);
        P711TableAssertValues(sparse, "matrix", null, 7m);
        HarnessAssert.Equal(
            1,
            sparse.Changes.Count,
            "MAP-TABLE-16 sparse matrix change count");

        var target = P711TableTarget(P711TableMatrixTarget());
        var malformed = P711TableSource(
            """
            {
              "blocks":[{
                "blockId":"source",
                "tableMode":"MATRIX",
                "cells":[{"columnKey":"amount","value":7}]
              }]
            }
            """);
        P711TableAssertConflictWithoutMutation(
            target,
            P711TablePreview(
                target,
                [malformed],
                [P711TableRule(targetBlockId: "matrix")]),
            DynamicFlowMappingTableFailureReasons.MatrixCoordinateInvalid,
            "MAP-TABLE-16 malformed coordinate");
    }

    private static void P711TableAssertCanonicalJoinAndAppendColumnsRead()
    {
        var source = P711TableSource(
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
        var rule = P711TableRule(targetBlockId: "append");
        rule.Inputs =
        [
            P711TableInput("amount", "amount"),
            P711TableInput("factor", "factor")
        ];
        rule.Calculation = new DynamicFlowMappingCalculationDto
        {
            Kind = "DIRECT",
            Operation = "multiply",
            ResultDataType = "NUMBER"
        };

        var preview = P711TablePreview(
            P711TableTarget(P711TableAppendTarget()),
            [source],
            [rule]);
        var rows = P711TableRows(preview, "append");
        HarnessAssert.Equal(
            2,
            rows.Count,
            "MAP-TABLE-17 joined append-column rows");
        HarnessAssert.Equal(
            20m,
            P711TableRowCell(rows, "r1", "mapped"),
            "MAP-TABLE-17 joined r1");
        HarnessAssert.Equal(
            60m,
            P711TableRowCell(rows, "r2", "mapped"),
            "MAP-TABLE-17 joined r2");
    }

    private static void P711TableAssertSourceReportGrain()
    {
        var first = P711TableSourceWithField(
            "000000000000000000000011",
            11m);
        var second = P711TableSourceWithField(
            "000000000000000000000010",
            10m);
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
                        DynamicFormTemplateId = P711TableSourceFormId,
                        StepId = P711TableSourceStepId,
                        FieldKey = "amount",
                        DataType = "NUMBER"
                    },
                    DataType = "NUMBER",
                    Cardinality = "ONE",
                    NullPolicy = "ERROR"
                }
            ],
            Target = P711TableTargetEndpoint("append", "mapped"),
            Calculation = P711TableCopyCalculation()
        };

        var preview = P711TablePreview(
            P711TableTarget(P711TableAppendTarget()),
            [first, second],
            [rule]);
        var rows = P711TableRows(preview, "append");
        HarnessAssert.Equal(
            2,
            rows.Count,
            "MAP-TABLE-18 source-report rows");
        P711TableAssertSequenceEqual(
            new[]
            {
                "000000000000000000000010",
                "000000000000000000000011"
            },
            rows.Select(P711TableRowKey).ToArray(),
            "MAP-TABLE-18 source-report deterministic identities");
        HarnessAssert.Equal(
            10m,
            P711TableRowCell(
                rows,
                "000000000000000000000010",
                "mapped"),
            "MAP-TABLE-18 source report 10");
        HarnessAssert.Equal(
            11m,
            P711TableRowCell(
                rows,
                "000000000000000000000011",
                "mapped"),
            "MAP-TABLE-18 source report 11");
    }

    private static void P711TableAssertGroupAndCustomJoinBlockers()
    {
        var group = P711TableRule(targetBlockId: "append");
        group.EvaluationGrain = "GROUP";
        P711TableExpectReason(
            () => DynamicFlowMappingEngine.ValidateP7Rules([group]),
            DynamicFlowMappingTableFailureReasons.GroupIntentionalBlock,
            "MAP-TABLE-19 GROUP");

        var customJoin = P711TableRule(targetBlockId: "append");
        customJoin.JoinKey = "departmentCode";
        P711TableExpectReason(
            () => DynamicFlowMappingEngine.ValidateP7Rules([customJoin]),
            DynamicFlowMappingTableFailureReasons.CustomJoinKeyIntentionalBlock,
            "MAP-TABLE-19 custom joinKey");
    }

    private static void P711TableAssertAdvancedTargetBlockers()
    {
        var scalarToRow = P711TableRule(targetBlockId: "append");
        scalarToRow.EvaluationGrain = "FLOW_INSTANCE";
        P711TableExpectReason(
            () => DynamicFlowMappingEngine.ValidateP7Rules([scalarToRow]),
            DynamicFlowMappingTableFailureReasons.ScalarToRowIntentionalBlock,
            "MAP-TABLE-20 scalar-to-row");

        var rowToReport = P711TableRule();
        rowToReport.Target = new DynamicFlowMappingEndpointDto
        {
            Kind = "FIELD",
            DynamicFormTemplateId = P711TableTargetFormId,
            StepId = P711TableTargetStepId,
            FieldKey = "total",
            DataType = "NUMBER"
        };
        P711TableExpectReason(
            () => DynamicFlowMappingEngine.ValidateP7Rules([rowToReport]),
            DynamicFlowMappingTableFailureReasons.RowToReportIntentionalBlock,
            "MAP-TABLE-20 row-to-report");

        var source = P711TableSource(P711TableAppendSource(
            """[{"rowKey":"r1","cells":{"amount":5}}]"""));
        var appendColumnsTarget = P711TableTarget(
            """{"blocks":[{"blockId":"blocked","tableMode":"APPEND_COLUMNS","columns":[]}]}""");
        P711TableAssertConflictWithoutMutation(
            appendColumnsTarget,
            P711TablePreview(
                appendColumnsTarget,
                [source],
                [P711TableRule(targetBlockId: "blocked")]),
            DynamicFlowMappingTableFailureReasons.AppendColumnsTargetIntentionalBlock,
            "MAP-TABLE-20 APPEND_COLUMNS target");

        var summaryTarget = P711TableTarget(
            """{"blocks":[{"blockId":"blocked","tableMode":"SUMMARY_TEMPLATE","rows":[]}]}""");
        P711TableAssertConflictWithoutMutation(
            summaryTarget,
            P711TablePreview(
                summaryTarget,
                [source],
                [P711TableRule(targetBlockId: "blocked")]),
            DynamicFlowMappingTableFailureReasons.SummaryTemplateReadOnly,
            "MAP-TABLE-20 SUMMARY_TEMPLATE");
    }

    private static DynamicFlowMappingPreviewResponse P711TablePreview(
        WorkAssignmentReport target,
        IReadOnlyList<DynamicFlowMappingSourceReport> sources,
        IReadOnlyList<DynamicFlowMappingRuleDto> rules)
        => DynamicFlowMappingEngine.Preview(
            target,
            sources,
            rules,
            requestConflictPolicy: null,
            requestContributionPolicy: null,
            nowUtc: P711TableFrozenNowUtc,
            targetStepId: P711TableTargetStepId,
            targetStepCode: "TARGET",
            enforceP7Contract: true);

    private static DynamicFlowMappingRuleDto P711TableRule(
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
            Inputs =
            [
                P711TableInput(
                    "amount",
                    sourceColumnKey,
                    sourceBlockId,
                    nullPolicy)
            ],
            Target = P711TableTargetEndpoint(
                targetBlockId,
                targetColumnKey),
            Calculation = P711TableCopyCalculation()
        };

    private static DynamicFlowMappingInputDto P711TableInput(
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
                DynamicFormTemplateId = P711TableSourceFormId,
                StepId = P711TableSourceStepId,
                BlockId = blockId,
                ColumnKey = columnKey,
                DataType = "NUMBER"
            },
            DataType = "NUMBER",
            Cardinality = "ONE",
            NullPolicy = nullPolicy
        };

    private static DynamicFlowMappingEndpointDto P711TableTargetEndpoint(
        string blockId,
        string columnKey)
        => new()
        {
            Kind = "TABLE_COLUMN",
            DynamicFormTemplateId = P711TableTargetFormId,
            StepId = P711TableTargetStepId,
            BlockId = blockId,
            ColumnKey = columnKey,
            DataType = "NUMBER"
        };

    private static DynamicFlowMappingCalculationDto
        P711TableCopyCalculation()
        => new()
        {
            Kind = "DIRECT",
            Operation = "copy",
            ResultDataType = "NUMBER"
        };

    private static WorkAssignmentReport P711TableTarget(
        string tableValuesJson)
        => new()
        {
            Id = "100000000000000000000001",
            WorkId = "100000000000000000000002",
            WorkAssignmentId = "100000000000000000000003",
            WorkReportPeriodId = "100000000000000000000004",
            DynamicFormTemplateId = P711TableTargetFormId,
            AssigneeUserId = "100000000000000000000005",
            PeriodKey = "2026-07",
            PeriodInstanceKey = "2026-07",
            FieldValuesJson = """{"values":{}}""",
            TableValuesJson = tableValuesJson
        };

    private static DynamicFlowMappingSourceReport P711TableSource(
        string tableValuesJson,
        string reportId = "200000000000000000000001")
    {
        var report = new WorkAssignmentReport
        {
            Id = reportId,
            WorkId = "100000000000000000000002",
            WorkAssignmentId = "200000000000000000000002",
            WorkReportPeriodId = "200000000000000000000003",
            DynamicFormTemplateId = P711TableSourceFormId,
            AssigneeUserId = "200000000000000000000004",
            PeriodKey = "2026-07",
            PeriodInstanceKey = "2026-07",
            FieldValuesJson = """{"values":{}}""",
            TableValuesJson = tableValuesJson
        };
        return new DynamicFlowMappingSourceReport(
            report,
            P711TableSourceStepId,
            "SOURCE",
            report.FieldValuesJson,
            report.TableValuesJson);
    }

    private static DynamicFlowMappingSourceReport
        P711TableSourceWithField(
            string reportId,
            decimal amount)
    {
        var source = P711TableSource("""{"blocks":[]}""", reportId);
        source.Report.FieldValuesJson = JsonSerializer.Serialize(new
        {
            values = new { amount }
        });
        return source with
        {
            FieldValuesJson = source.Report.FieldValuesJson
        };
    }

    private static string P711TableFixedGridSource(
        string slots,
        string values)
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

    private static string P711TableFixedGridTarget(
        string slots,
        string values)
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

    private static string P711TableStandardFixedGridTarget()
        => P711TableFixedGridTarget(
            """
            [
              {"index":0,"rowKey":"r1","columnKey":"mapped"},
              {"index":1,"rowKey":"r2","columnKey":"mapped"}
            ]
            """,
            "[null,null]");

    private static string P711TableAppendTarget()
        => """{"blocks":[{"blockId":"append","tableMode":"APPEND_ROWS","rows":[]}]}""";

    private static string P711TableAppendSource(string rows)
        => $$"""
        {
          "blocks":[{
            "blockId":"source",
            "tableMode":"APPEND_ROWS",
            "rows":{{rows}}
          }]
        }
        """;

    private static string P711TableMatrixTarget()
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

    private static List<JsonElement> P711TableRows(
        DynamicFlowMappingPreviewResponse response,
        string blockId)
        => P711TableBlock(response, blockId)
            .GetProperty("rows")
            .EnumerateArray()
            .Select(row => row.Clone())
            .ToList();

    private static List<JsonElement> P711TableCells(
        DynamicFlowMappingPreviewResponse response,
        string blockId)
        => P711TableBlock(response, blockId)
            .GetProperty("cells")
            .EnumerateArray()
            .Select(cell => cell.Clone())
            .ToList();

    private static JsonElement P711TableBlock(
        DynamicFlowMappingPreviewResponse response,
        string blockId)
    {
        using var document = JsonDocument.Parse(
            response.TableValuesJson!);
        return document.RootElement
            .GetProperty("blocks")
            .EnumerateArray()
            .Single(block =>
                block.GetProperty("blockId").GetString() == blockId)
            .Clone();
    }

    private static void P711TableAssertValues(
        DynamicFlowMappingPreviewResponse response,
        string blockId,
        params decimal?[] expected)
    {
        var values = P711TableBlock(response, blockId)
            .GetProperty("values1D");
        HarnessAssert.Equal(
            expected.Length,
            values.GetArrayLength(),
            $"{blockId} values length");
        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index].HasValue)
            {
                HarnessAssert.Equal(
                    expected[index]!.Value,
                    values[index].GetDecimal(),
                    $"{blockId} value {index}");
            }
            else
            {
                HarnessAssert.Equal(
                    JsonValueKind.Null,
                    values[index].ValueKind,
                    $"{blockId} null {index}");
            }
        }
    }

    private static string P711TableValuesJson(
        DynamicFlowMappingPreviewResponse response,
        string blockId)
        => P711TableBlock(response, blockId)
            .GetProperty("values1D")
            .GetRawText();

    private static decimal P711TableCellValue(
        IReadOnlyCollection<JsonElement> cells,
        string rowKey,
        string columnKey)
        => cells.Single(cell =>
                cell.GetProperty("rowKey").GetString() == rowKey &&
                cell.GetProperty("columnKey").GetString() == columnKey)
            .GetProperty("value")
            .GetDecimal();

    private static string P711TableRowKey(JsonElement row)
        => row.GetProperty("rowKey").GetString()!;

    private static string P711TableRowBusinessKey(JsonElement row)
        => row.GetProperty("businessKey").GetString()!;

    private static decimal P711TableRowCell(
        IReadOnlyCollection<JsonElement> rows,
        string rowKey,
        string columnKey)
        => rows.Single(row => P711TableRowKey(row) == rowKey)
            .GetProperty("cells")
            .GetProperty(columnKey)
            .GetDecimal();

    private static string P711TableSemantic(
        DynamicFlowMappingPreviewResponse response)
    {
        var changes = response.Changes
            .OrderBy(change => change.MappingId, StringComparer.Ordinal)
            .ThenBy(change => change.MappingVersion)
            .ThenBy(
                change => change.Sources.FirstOrDefault()?.RowKey,
                StringComparer.Ordinal)
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
            .OrderBy(
                block => block["blockId"]!.GetValue<string>(),
                StringComparer.Ordinal)
            .Select(block => new
            {
                BlockId = block["blockId"]!.GetValue<string>(),
                Values = block["values1D"]?.DeepClone(),
                Rows = block["rows"]?.DeepClone(),
                Cells = block["cells"]?.DeepClone()
            })
            .ToList();
        return P711TableCanonical(
            JsonSerializer.Serialize(new { values, changes }));
    }

    private static void P711TableAssertConflictWithoutMutation(
        WorkAssignmentReport target,
        DynamicFlowMappingPreviewResponse response,
        string expectedReason,
        string context)
    {
        HarnessAssert.True(
            response.HasBlockingConflicts,
            $"{context} expected conflict {expectedReason}");
        HarnessAssert.Equal(
            expectedReason,
            response.Changes.Single().Reason,
            $"{context} stable table failure reason");
        HarnessAssert.Equal(
            P711TableCanonical(target.TableValuesJson),
            P711TableCanonical(response.TableValuesJson),
            $"{context} failed projection was not zero-write");
    }

    private static void P711TableExpectReason(
        Action action,
        string expectedReason,
        string context)
    {
        try
        {
            action();
            throw new InvalidOperationException(
                $"{context} expected stable reason {expectedReason}.");
        }
        catch (DynamicFlowMappingEvaluationException ex)
        {
            HarnessAssert.Equal(
                expectedReason,
                ex.Reason,
                $"{context} stable support-matrix reason");
        }
    }

    private static string P711TableCanonical(string? json)
        => DynamicFlowDefinitionPayloadContract.CanonicalizeJson(
            json ?? "null");

    private static void P711TableAssertSequenceEqual<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string message)
    {
        HarnessAssert.True(
            expected.Count == actual.Count &&
            expected.SequenceEqual(actual),
            $"{message}. Expected=[{string.Join(", ", expected)}]; " +
            $"Actual=[{string.Join(", ", actual)}].");
    }
}
