using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using tdtd_be.Models.Statistics;
using tdtd_be.Services.StatisticsReconciliation.ActualObservation;
using tdtd_be.Services.StatisticsRun;

var cases = new (string Id, Action Run)[]
{
    ("P10-XVIEW-01", AdvancedExportOnlyIsExplicitAndDeterministic),
    ("P10-XVIEW-02", DirectFieldEmptyPartitionCompletes),
    ("P10-XVIEW-03", DirectTableEmptyPartitionCompletes),
    ("P10-XVIEW-04", DirectLabelEveryCellCompletes),
    ("P10-XVIEW-05", BasicAndFlowUseIndependentPartitions),
    ("P10-XVIEW-06", DiffSharedPartitionCompletes),
    ("P10-XVIEW-07", MissingBaseAndUnsupportedMatrixFailClosed),
    ("P10-XVIEW-08", PagingOmissionAndMetadataMutationFail),
    ("P10-XVIEW-09", FilterPeriodAndResultKindMutationFail),
    ("P10-XVIEW-10", ApiRowOneBitMutationFails),
    ("P10-XVIEW-11", CopiedExportCellMutationCannotFalseGreen),
    ("P10-XVIEW-12", CopiedTotalsMutationCannotFalseGreen),
    ("P10-XVIEW-13", ExtraAndOmittedSourceCellsFail),
    ("P10-XVIEW-14", ExportPreimageAndManifestPinsAreRequired),
    ("P10-XVIEW-15", CrossViewHardeningCases.Run),
    ("P10-XVIEW-16", DirectFullApiFiltersAreAcceptedAndTyped),
    ("P10-XVIEW-17", DirectFieldNullableSchemaCompletes),
    ("P10-XVIEW-18", DirectFieldDeclaredSchemaTamperFails)
};

var passed = 0;
foreach (var test in cases)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Id}");
        passed++;
    }
    catch (Exception exception)
    {
        Console.WriteLine(
            $"FAIL {test.Id} {exception.GetType().Name}:{exception.Message}");
        return 1;
    }
}

Require(passed == cases.Length, "EXACT_CASE_COUNT");
Console.WriteLine($"P10_T23_CROSS_VIEW_PARITY_OK cases={passed}");
return 0;

static void AdvancedExportOnlyIsExplicitAndDeterministic()
{
    var fixture = Fixtures.Advanced();
    var first = Prove(fixture);
    var second = Prove(fixture);
    Complete(first, "ADVANCED_COMPLETE");
    Equal(first.ProofSha256, second.ProofSha256, "DETERMINISTIC_PROOF");
    Require(first.ApiRowCount == 0, "ADVANCED_API_NA");
}

static void DirectFieldEmptyPartitionCompletes()
    => Complete(Prove(Fixtures.DirectEmpty(
        StatisticReconciliationActualApiSurfaces.DirectField)),
        "DIRECT_FIELD_COMPLETE");

static void DirectFieldNullableSchemaCompletes()
{
    var fixture = Fixtures.DirectFieldNullable();
    Complete(Prove(fixture), "DIRECT_FIELD_NULLABLE_V2");
    var columns = fixture.Actual.ExportManifest.Columns;
    var bucket = columns.Single(value => value.Name == "bucketKey");
    var earliest = columns.Single(value => value.Name == "earliestDateUtc");
    Equal(StatisticReconciliationActualExportValueTypes.Text,
        bucket.ValueType, "DIRECT_FIELD_BUCKET_DECLARED_TYPE");
    Equal(StatisticReconciliationActualExportBlankPolicies.Null,
        bucket.BlankPolicy, "DIRECT_FIELD_BUCKET_NULL_POLICY");
    Equal(StatisticReconciliationActualExportValueTypes.UtcInstant,
        earliest.ValueType, "DIRECT_FIELD_DATE_DECLARED_TYPE");
    Equal(StatisticReconciliationActualExportBlankPolicies.Null,
        earliest.BlankPolicy, "DIRECT_FIELD_DATE_NULL_POLICY");

    var direct = Fixtures.DirectParity(fixture);
    var manifest = direct.Actual.ExportManifest;
    var capture = direct.Actual.Export;
    Equal(manifest.ExportId, direct.Plan.ExportId,
        "DIRECT_FIELD_V1_EXPORT_ID");
    Equal(manifest.ResultSha256, direct.Plan.P9GenerationSha256,
        "DIRECT_FIELD_V1_RESULT_SHA");
    Equal(new StatisticReconciliationActualExportParser()
            .ComputeManifestSha256(manifest), capture.ManifestSha256,
        "DIRECT_FIELD_V1_MANIFEST_SHA");
    Require(capture.Headers.SequenceEqual(
            manifest.Columns.Select(value => value.Name),
            StringComparer.Ordinal),
        "DIRECT_FIELD_V1_HEADERS");
    Equal(HS("P10_ACTUAL_EXPORT_ROWS_V1",
            capture.Rows.Select(value => value.RowSemanticSha256)),
        capture.RowsSemanticSha256, "DIRECT_FIELD_V1_ROWS_SHA");
    Equal(HS("P10_ACTUAL_EXPORT_TOTALS_V1",
            capture.FullFilterTotals.Select(value =>
                value.TotalSemanticSha256)),
        capture.TotalsSemanticSha256, "DIRECT_FIELD_V1_TOTALS_SHA");
    var proof = new StatisticReconciliationActualDirectCrossViewParity()
        .Prove(direct.Plan, direct.Actual);
    Require(proof.Complete,
        $"DIRECT_FIELD_NULLABLE_V1:{proof.FailureCode}");
}

static void DirectFieldDeclaredSchemaTamperFails()
{
    var fixture = Fixtures.DirectFieldNullable();
    var export = fixture.Base.Export;
    var index = Enumerable.Range(0, export.Columns.Length).Single(value =>
        export.Columns[value].Name == "earliestDateUtc");
    Require(index > 0, "DIRECT_FIELD_DATE_COLUMN_REQUIRED");
    var columns = export.Columns.SetItem(index,
        export.Columns[index] with
        {
            ValueType = StatisticReconciliationActualExportValueTypes.Text
        });
    Incomplete(Prove(fixture with
    {
        Base = fixture.Base with
        {
            Export = export with { Columns = columns }
        }
    }), "DIRECT_FIELD_DECLARED_TYPE_TAMPER");
}

static void DirectTableEmptyPartitionCompletes()    => Complete(Prove(Fixtures.DirectEmpty(
        StatisticReconciliationActualApiSurfaces.DirectTable)),
        "DIRECT_TABLE_COMPLETE");

static void DirectLabelEveryCellCompletes()
{
    var proof = Prove(Fixtures.DirectLabel());
    Complete(proof, "DIRECT_LABEL_COMPLETE");
    Require(proof.ApiRowCount == 1 && proof.ExportRowCount == 1,
        "DIRECT_LABEL_PARTITION");
    Require(proof.CellRelationCount > 20 &&
            proof.OrderedRowRelationCount == 1,
        "DIRECT_LABEL_ALL_CELLS");
}

static void DirectFullApiFiltersAreAcceptedAndTyped()
{
    var filters = new[]
    {
        (StatisticReconciliationActualApiSurfaces.DirectField,
            Canonical(new
            {
                periodInstanceKey = Fixtures.Period,
                statisticLabelCode = "missing-label",
                fieldType = "NUMBER",
                showInTree = true,
                showInDetail = false,
                periodKeyFrom = "2025",
                periodKeyTo = "2026",
                reportStatus = 3
            })),
        (StatisticReconciliationActualApiSurfaces.DirectTable,
            Canonical(new
            {
                periodInstanceKey = Fixtures.Period,
                dynamicExcelTemplateId = "missing-excel",
                tableMode = "FIXED_GRID",
                metricLabelCode = "missing-label",
                dataType = "NUMBER",
                reportStatus = 3
            })),
        (StatisticReconciliationActualApiSurfaces.DirectLabel,
            Canonical(new
            {
                periodInstanceKey = Fixtures.Period,
                dynamicExcelTemplateId = "missing-excel",
                reportStatus = 3
            }))
    };
    foreach (var (surface, filter) in filters)
        Complete(Prove(Fixtures.DirectEmpty(surface, filter)),
            $"DIRECT_FULL_FILTER_{surface}");
    Incomplete(Prove(Fixtures.DirectEmpty(
            StatisticReconciliationActualApiSurfaces.DirectField,
            Canonical(new
            {
                periodInstanceKey = Fixtures.Period,
                showInTree = "true"
            }))),
        "DIRECT_BOOLEAN_FILTER_TYPE");
    Incomplete(Prove(Fixtures.DirectEmpty(
            StatisticReconciliationActualApiSurfaces.DirectLabel,
            Canonical(new
            {
                periodInstanceKey = Fixtures.Period,
                reportStatus = "3"
            }))),
        "DIRECT_INTEGER_FILTER_TYPE");
}
static void BasicAndFlowUseIndependentPartitions()
{
    var basic = Prove(Fixtures.Basic(false));
    var flow = Prove(Fixtures.Basic(true));
    Complete(basic, "BASIC_COMPLETE");
    Complete(flow, "FLOW_COMPLETE");
    Require(basic.OrderedRowRelationCount == 0 &&
            flow.OrderedRowRelationCount == 0,
        "BASIC_FLOW_NO_FAKE_SHARED_ROWS");
}

static void DiffSharedPartitionCompletes()
{
    var proof = Prove(Fixtures.DiffEmpty());
    Complete(proof, "DIFF_COMPLETE");
    Require(proof.OrderedRowRelationCount == 0,
        "DIFF_EMPTY_SHARED_PARTITION");
}

static void MissingBaseAndUnsupportedMatrixFailClosed()
{
    var fixture = Fixtures.Advanced();
    Incomplete(new StatisticReconciliationActualCrossViewParityV2().Prove(
        fixture.Plan,
        null,
        fixture.Actual), "MISSING_BASE");
    Incomplete(Prove(fixture with
    {
        Plan = fixture.Plan with { Family = "UNKNOWN" }
    }), "UNKNOWN_FAMILY");
    Incomplete(Prove(fixture with
    {
        Plan = fixture.Plan with
        {
            ApiApplicability =
                StatisticReconciliationActualCrossViewApiApplicability.Required
        }
    }), "ADVANCED_FAKE_API");
}

static void PagingOmissionAndMetadataMutationFail()
{
    var fixture = Fixtures.DirectLabel();
    var api = fixture.Actual.Api!;
    Incomplete(Prove(fixture with
    {
        Actual = fixture.Actual with
        {
            Api = api with
            {
                Pages = ImmutableArray<StatisticReconciliationActualApiPageObservation>.Empty
            }
        }
    }), "PAGE_OMITTED");
    var page = api.Pages[0];
    Incomplete(Prove(fixture with
    {
        Actual = fixture.Actual with
        {
            Api = api with
            {
                Pages = [page with { TotalPages = page.TotalPages + 1 }]
            }
        }
    }), "TOTAL_PAGES_MUTATED");
    Incomplete(Prove(fixture with
    {
        Actual = fixture.Actual with
        {
            Api = api with
            {
                Pages = [page with { ReturnedRows = page.ReturnedRows + 1 }]
            }
        }
    }), "RETURNED_ROWS_MUTATED");
}

static void FilterPeriodAndResultKindMutationFail()
{
    var fixture = Fixtures.DirectLabel();
    var filter = Canonical(new
    {
        blockId = (string?)null,
        bucketKey = (string?)null,
        dynamicFormTemplateId = Fixtures.Template,
        fieldId = (string?)null,
        fieldKey = (string?)null,
        labelCode = (string?)null,
        metricKey = (string?)null,
        periodKey = "one-bit"
    });
    Incomplete(Prove(fixture with
    {
        Plan = fixture.Plan with
        {
            CanonicalExportFilterJson = filter,
            ExportFilterSha256 = RawSha(filter)
        }
    }), "FILTER_MUTATED");
    Incomplete(Prove(fixture with
    {
        Plan = fixture.Plan with { PeriodInstanceKey = "period-mutated" }
    }), "PERIOD_MUTATED");
    Incomplete(Prove(fixture with
    {
        Plan = fixture.Plan with { ExportResultKind = "DIFF" }
    }), "RESULT_KIND_MUTATED");
}

static void ApiRowOneBitMutationFails()
{
    var fixture = Fixtures.DirectLabel();
    var api = fixture.Actual.Api!;
    var page = api.Pages[0];
    var row = page.Rows[0];
    var mutatedJson = row.CanonicalRowJson.Replace(
        "\"labelName\":\"Label\"",
        "\"labelName\":\"LabeM\"",
        StringComparison.Ordinal);
    Require(mutatedJson != row.CanonicalRowJson, "ROW_MUTATION_APPLIED");
    Incomplete(Prove(fixture with
    {
        Actual = fixture.Actual with
        {
            Api = api with
            {
                Pages = [page with
                {
                    Rows = [row with { CanonicalRowJson = mutatedJson }]
                }]
            }
        }
    }), "API_CELL_MUTATED");
}

static void CopiedExportCellMutationCannotFalseGreen()
{
    var fixture = Fixtures.DirectLabel();
    var mutated = MutateExportCellEverywhere(fixture, "rowCount", "4");
    Incomplete(Prove(mutated), "COPIED_CELL_MUTATION");
}

static void CopiedTotalsMutationCannotFalseGreen()
{
    var fixture = Fixtures.DirectLabel();
    var mutated = MutateExportTotalEverywhere(fixture, "rowCount", "2");
    Incomplete(Prove(mutated), "COPIED_TOTAL_MUTATION");
}

static void ExtraAndOmittedSourceCellsFail()
{
    var fixture = Fixtures.DirectLabel();
    var source = fixture.Base.Export.Rows[0].CanonicalSourceRowJson;
    using var document = JsonDocument.Parse(source);
    var dictionary = document.RootElement.EnumerateObject()
        .ToDictionary(value => value.Name, value => value.Value.Clone(),
            StringComparer.Ordinal);
    dictionary["extra"] = JsonSerializer.SerializeToElement(1);
    var extra = Canonical(dictionary);
    Incomplete(Prove(WithSource(fixture, extra)), "EXTRA_SOURCE_CELL");
    dictionary.Remove("extra");
    dictionary.Remove("labelName");
    var omitted = Canonical(dictionary);
    Incomplete(Prove(WithSource(fixture, omitted)), "OMITTED_SOURCE_CELL");
}

static void ExportPreimageAndManifestPinsAreRequired()
{
    var fixture = Fixtures.DirectLabel();
    Incomplete(Prove(fixture with
    {
        Actual = fixture.Actual with
        {
            ExportManifest = fixture.Actual.ExportManifest with
            {
                CanonicalFilterJson = null
            }
        }
    }), "FILTER_PREIMAGE_MISSING");
    Incomplete(Prove(fixture with
    {
        Plan = fixture.Plan with
        {
            ExportColumnManifestSha256 = H("wrong-column-manifest")
        }
    }), "COLUMN_MANIFEST_MUTATED");
    Incomplete(Prove(fixture with
    {
        Plan = fixture.Plan with
        {
            ExportOwnerSemanticSha256 = H("wrong-owner")
        }
    }), "OWNER_SEMANTIC_MUTATED");
}

static Fixture WithSource(Fixture fixture, string source)
{
    var export = fixture.Base.Export;
    var row = export.Rows[0] with { CanonicalSourceRowJson = source };
    return fixture with
    {
        Base = fixture.Base with
        {
            Export = export with { Rows = [row] }
        }
    };
}

static Fixture MutateExportCellEverywhere(
    Fixture fixture, string columnName, string canonical)
{
    var manifest = fixture.Actual.ExportManifest;
    var capture = fixture.Actual.ExportCapture;
    var column = manifest.Columns.Single(value => value.Name == columnName);
    var cell = Cell(column, "VALUE", canonical, 0, false);
    var capturedRow = capture.Rows[0];
    var captureCells = capturedRow.Cells.SetItem(column.Ordinal, cell);
    var captureRowSha = HS("P10_ACTUAL_EXPORT_ROW_V1",
        captureCells.Select(value => value.CellSemanticSha256));
    var capturedRows = ImmutableArray.Create(capturedRow with
    {
        Cells = captureCells,
        RowSemanticSha256 = captureRowSha
    });
    var rowsSha = HS("P10_ACTUAL_EXPORT_ROWS_V1",
        capturedRows.Select(value => value.RowSemanticSha256));
    var captureSha = ExportCaptureSha(
        manifest,
        capture.ManifestSha256,
        rowsSha,
        capture.TotalsSemanticSha256);
    var newCapture = capture with
    {
        Rows = capturedRows,
        RowsSemanticSha256 = rowsSha,
        CaptureSemanticSha256 = captureSha
    };
    var baseExport = fixture.Base.Export;
    var baseRow = baseExport.Rows[0];
    var baseCells = baseRow.Cells.SetItem(column.Ordinal, cell);
    var baseRowSha = HS("P10_ACTUAL_EXPORT_ROW_V1",
        baseCells.Select(value => value.CellSemanticSha256));
    var newBase = baseExport with
    {
        Rows = [baseRow with
        {
            Cells = baseCells,
            RowSemanticSha256 = baseRowSha
        }]
    };
    return fixture with
    {
        Base = fixture.Base with { Export = newBase },
        Actual = fixture.Actual with { ExportCapture = newCapture }
    };
}

static Fixture MutateExportTotalEverywhere(
    Fixture fixture, string name, string canonical)
{
    var manifest = fixture.Actual.ExportManifest;
    var capture = fixture.Actual.ExportCapture;
    var total = capture.FullFilterTotals.Single(value => value.Name == name);
    var mutated = total with
    {
        CanonicalValue = canonical,
        TotalSemanticSha256 = HValues(
            "P10_ACTUAL_EXPORT_TOTAL_V1",
            total.Name,
            total.ValueType,
            total.ValueState,
            canonical,
            I(total.DecimalScale))
    };
    var totals = capture.FullFilterTotals
        .Select(value => value.Name == name ? mutated : value)
        .ToImmutableArray();
    var totalsSha = HS("P10_ACTUAL_EXPORT_TOTALS_V1",
        totals.Select(value => value.TotalSemanticSha256));
    var captureSha = ExportCaptureSha(
        manifest,
        capture.ManifestSha256,
        capture.RowsSemanticSha256,
        totalsSha);
    return fixture with
    {
        Base = fixture.Base with
        {
            Export = fixture.Base.Export with { FullFilterTotals = totals }
        },
        Actual = fixture.Actual with
        {
            ExportCapture = capture with
            {
                FullFilterTotals = totals,
                TotalsSemanticSha256 = totalsSha,
                CaptureSemanticSha256 = captureSha
            }
        }
    };
}

static string ExportCaptureSha(
    StatisticReconciliationActualExportManifest manifest,
    string manifestSha,
    string rowsSha,
    string totalsSha)
    => HValues(
        "P10_ACTUAL_EXPORT_CAPTURE_V2",
        manifest.ExportId,
        manifest.Format,
        manifest.ResultKind,
        manifest.ResultId,
        manifest.ResultSha256,
        manifest.ConfigSha256,
        manifest.SourceSha256,
        manifest.FilterSha256,
        manifest.PeriodInstanceKey ?? "~",
        manifest.CanonicalFilterJson ?? "~",
        I(manifest.LifecycleRevision),
        manifest.ContentSha256,
        manifestSha,
        manifest.OwnerSemanticSha256,
        rowsSha,
        totalsSha);

static StatisticReconciliationActualCrossViewParityV2Proof Prove(
    Fixture fixture)
    => new StatisticReconciliationActualCrossViewParityV2().Prove(
        fixture.Plan,
        fixture.Base,
        fixture.Actual);

static void Complete(
    StatisticReconciliationActualCrossViewParityV2Proof proof,
    string reason)
{
    Require(proof.Complete, $"{reason}:{proof.FailureCode}");
    Equal(StatisticReconciliationActualCrossViewParityV2Failures.None,
        proof.FailureCode, $"{reason}_FAILURE_CODE");
}

static void Incomplete(
    StatisticReconciliationActualCrossViewParityV2Proof proof,
    string reason)
{
    Require(!proof.Complete, $"{reason}_FALSE_GREEN");
    Require(proof.FailureCode !=
            StatisticReconciliationActualCrossViewParityV2Failures.None,
        $"{reason}_FAILURE_REQUIRED");
}

internal sealed record Fixture(
    StatisticReconciliationActualCrossViewParityV2Plan Plan,
    StatisticReconciliationActualCrossViewParityV2Base Base,
    StatisticReconciliationActualCrossViewParityV2Actual Actual);

internal static class Fixtures
{
    internal const string Work = "work-1";
    internal const string Scope = "scope-1";
    internal const string Template = "template-1";
    internal const string Period = "period-1";
    private static readonly ImmutableArray<string> DirectFieldExportHeaders =
    [
        "ordinal", "workId", "scopeType", "scopeId", "rootAssignmentId",
        "dynamicFormTemplateId", "dynamicFormTemplateCode",
        "dynamicFormTemplateName", "fieldId", "fieldKey", "fieldLabel",
        "fieldType", "statisticLabelCodes", "showInTree", "showInDetail",
        "bucketKey", "bucketLabel", "periodKey", "periodInstanceKey",
        "periodKind", "reportStatus", "valueCount", "numericValueCount",
        "sum", "min", "max", "average", "trueCount", "falseCount",
        "earliestDateUtc", "latestDateUtc", "reportCount", "updatedAtUtc"
    ];

    internal static Fixture Advanced()
        => ExportOnly(
            StatisticReconciliationActualCrossViewFamilies.Advanced,
            "ADVANCED",
            [Canonical(new { metric = "advanced", value = 2m })]);

    internal static Fixture DirectEmpty(string surface, string? apiFilter = null)
    {
        var owner = "p9-run";
        var generationId = H("p9-generation-id");
        var generationHash = H("p9-generation-hash");
        var export = Export(
            surface,
            [],
            directFilter: true,
            resultSha: generationHash,
            resultId: owner);
        apiFilter ??= Canonical(new { periodInstanceKey = Period });
        var apiGeneration = HS(
            "P10_ACTUAL_API_DIRECT_GENERATION_V1",
            [HValues("P10_ACTUAL_API_DIRECT_PUBLICATION_PIN_V1",
                owner, generationId, generationHash, I(1), I(1))]);
        var totals = surface ==
            StatisticReconciliationActualApiSurfaces.DirectLabel
            ? ApiTotals(
                ("totalReportCount", "INTEGER", "0"),
                ("totalRowCount", "INTEGER", "0"),
                ("totalRows", "INTEGER", "0"))
            : ApiTotals(
                ("totalReportCount", "INTEGER", "0"),
                ("totalRows", "INTEGER", "0"),
                ("totalSum", "DECIMAL", "0"),
                ("totalValueCount", "INTEGER", "0"));
        return WithApi(
            StatisticReconciliationActualCrossViewFamilies.Direct,
            surface,
            surface,
            true,
            export,
            owner,
            generationId,
            apiGeneration,
            generationHash,
            apiFilter,
            [],
            totals);
    }

    internal static Fixture DirectFieldNullable()
    {
        var owner = "p9-run";
        var generationId = H("p9-generation-id");
        var generationHash = H("p9-generation-hash");
        var source = Canonical(new
        {
            workId = Work,
            scopeType = "ASSIGNMENT",
            scopeId = Scope,
            rootAssignmentId = (string?)null,
            dynamicFormTemplateId = Template,
            dynamicFormTemplateCode = "FORM",
            dynamicFormTemplateName = "Form",
            fieldId = "amount",
            fieldKey = "amount",
            fieldLabel = "Amount",
            fieldType = "NUMBER",
            statisticLabelCodes = new[] { "amount" },
            showInTree = true,
            showInDetail = false,
            bucketKey = (string?)null,
            bucketLabel = (string?)null,
            periodKey = "2026-08",
            periodInstanceKey = Period,
            periodKind = "MONTH",
            reportStatus = 2,
            valueCount = 1L,
            numericValueCount = 1L,
            sum = 10m,
            min = 10m,
            max = 10m,
            average = 10m,
            trueCount = 0L,
            falseCount = 0L,
            earliestDateUtc = (DateTime?)null,
            latestDateUtc = (DateTime?)null,
            reportCount = 1L,
            updatedAtUtc = new DateTime(
                2026, 8, 14, 1, 2, 3, DateTimeKind.Utc)
        });
        var export = Export(
            StatisticReconciliationActualApiSurfaces.DirectField,
            [source],
            directFilter: true,
            resultSha: generationHash,
            resultId: owner);
        var apiFilter = Canonical(new { periodInstanceKey = Period });
        var apiGeneration = HS(
            "P10_ACTUAL_API_DIRECT_GENERATION_V1",
            [HValues("P10_ACTUAL_API_DIRECT_PUBLICATION_PIN_V1",
                owner, generationId, generationHash, I(1), I(1))]);
        using var document = JsonDocument.Parse(source);
        var root = document.RootElement;
        var identity = HValues(
            "P10_ACTUAL_API_DIRECT_FIELD_ROW_ID_V1",
            root.GetProperty("workId").GetString(),
            root.GetProperty("scopeType").GetString(),
            root.GetProperty("scopeId").GetString(),
            null,
            root.GetProperty("dynamicFormTemplateId").GetString(),
            root.GetProperty("fieldId").GetString(),
            root.GetProperty("fieldKey").GetString(),
            root.GetProperty("fieldType").GetString(),
            null,
            root.GetProperty("periodKey").GetString(),
            root.GetProperty("periodInstanceKey").GetString(),
            I(root.GetProperty("reportStatus").GetInt64()));
        var totals = ApiTotals(
            ("totalReportCount", "INTEGER", "1"),
            ("totalRows", "INTEGER", "1"),
            ("totalSum", "DECIMAL", "10"),
            ("totalValueCount", "INTEGER", "1"));
        return WithApi(
            StatisticReconciliationActualCrossViewFamilies.Direct,
            StatisticReconciliationActualApiSurfaces.DirectField,
            StatisticReconciliationActualApiSurfaces.DirectField,
            true,
            export,
            owner,
            generationId,
            apiGeneration,
            generationHash,
            apiFilter,
            [(identity, source)],
            totals);
    }

    internal static (
        StatisticReconciliationActualDirectCrossViewParityPlan Plan,
        StatisticReconciliationActualDirectCrossViewParityActual Actual)
        DirectParity(Fixture fixture)
    {
        var plan = fixture.Plan;
        var apiBase = fixture.Base.Api ?? throw new InvalidOperationException(
            "DIRECT_API_BASE_REQUIRED");
        var directPlan = new
            StatisticReconciliationActualDirectCrossViewParityPlan(
                StatisticReconciliationActualDirectCrossViewParitySchemas.V1,
                plan.ApiSurface!,
                plan.ExportResultKind,
                plan.WorkId,
                plan.ScopeId,
                plan.DynamicFormTemplateId,
                plan.PeriodInstanceKey,
                plan.ApiOwnerResultId!,
                plan.ApiGenerationId!,
                plan.DirectPublicationGenerationSha256!,
                plan.DirectSourceRevision!.Value,
                plan.DirectPublicationRevision!.Value,
                plan.AuthorizationSnapshotSha256,
                plan.CanonicalApiFilterJson!,
                plan.ApiFilterSha256!,
                plan.ApiExpectedTotalRows,
                plan.ApiPageSize,
                plan.ApiPageCount,
                plan.ExportId,
                plan.ExportResultId);
        var baseline = new
            StatisticReconciliationActualDirectCrossViewBaseProjection(
                StatisticReconciliationActualDirectCrossViewParitySchemas.V1,
                plan.ApiSurface!,
                plan.WorkId,
                plan.ScopeId,
                plan.DynamicFormTemplateId,
                plan.PeriodInstanceKey,
                plan.ApiOwnerResultId!,
                plan.ApiGenerationId!,
                plan.DirectPublicationGenerationSha256!,
                plan.DirectSourceRevision.Value,
                plan.DirectPublicationRevision.Value,
                plan.CanonicalApiFilterJson!,
                plan.ApiFilterSha256!,
                apiBase.Rows.Select(value => new
                        StatisticReconciliationActualDirectCrossViewBaseRow(
                            value.AbsoluteOrdinal,
                            value.Identity,
                            value.CanonicalRowJson))
                    .ToImmutableArray(),
                apiBase.FullFilterTotals);
        var manifest = fixture.Actual.ExportManifest;
        var capture = fixture.Actual.ExportCapture;
        var manifestSha = new StatisticReconciliationActualExportParser()
            .ComputeManifestSha256(manifest);
        var legacyCapture = capture with
        {
            CaptureSemanticSha256 = HValues(
                "P10_ACTUAL_EXPORT_CAPTURE_V1",
                manifest.ExportId,
                manifest.Format,
                manifest.ResultKind,
                manifest.ResultId,
                manifest.ResultSha256,
                manifest.ConfigSha256,
                manifest.SourceSha256,
                manifest.FilterSha256,
                I(manifest.LifecycleRevision),
                manifest.ContentSha256,
                manifestSha,
                manifest.OwnerSemanticSha256,
                capture.RowsSemanticSha256,
                capture.TotalsSemanticSha256)
        };
        var actual = new
            StatisticReconciliationActualDirectCrossViewParityActual(
                StatisticReconciliationActualDirectCrossViewParitySchemas.V1,
                baseline,
                fixture.Actual.Api!,
                manifest,
                legacyCapture,
                plan.PeriodInstanceKey,
                plan.CanonicalExportFilterJson);
        return (directPlan, actual);
    }

    internal static Fixture DirectLabel()    {
        var owner = "p9-run";
        var generationId = H("p9-generation-id");
        var generationHash = H("p9-generation-hash");
        var source = Canonical(new
        {
            workId = Work,
            scopeType = "ASSIGNMENT",
            scopeId = Scope,
            rootAssignmentId = "root-1",
            dynamicFormTemplateId = Template,
            dynamicFormTemplateCode = "FORM",
            dynamicFormTemplateName = "Form",
            dynamicExcelTemplateId = "excel-1",
            blockId = "block-1",
            labelCode = "label-1",
            labelName = "Label",
            labelColor = "#112233",
            labelDataType = "NUMBER",
            statisticLabelLayer = "FIELD_STATISTIC_LABEL",
            runtimeLabelLayer = "RUNTIME_ROW_LABEL",
            catalogLabelLayer = "LABEL_CATALOG",
            configurationLayer = "LOCKED_P8_CONFIG",
            periodKey = "2026",
            periodInstanceKey = Period,
            periodKind = "YEAR",
            reportStatus = 3,
            rowCount = 3L,
            reportCount = 2L,
            updatedAtUtc = new DateTime(
                2026, 8, 12, 1, 2, 3, DateTimeKind.Utc)
        });
        var export = Export(
            StatisticReconciliationActualApiSurfaces.DirectLabel,
            [source],
            directFilter: true,
            resultSha: generationHash,
            resultId: owner);
        var apiFilter = Canonical(new { periodInstanceKey = Period });
        var apiGeneration = HS(
            "P10_ACTUAL_API_DIRECT_GENERATION_V1",
            [HValues("P10_ACTUAL_API_DIRECT_PUBLICATION_PIN_V1",
                owner, generationId, generationHash, I(1), I(1))]);
        using var document = JsonDocument.Parse(source);
        var root = document.RootElement;
        var identity = HValues(
            "P10_ACTUAL_API_DIRECT_LABEL_ROW_ID_V1",
            root.GetProperty("workId").GetString(),
            root.GetProperty("scopeType").GetString(),
            root.GetProperty("scopeId").GetString(),
            root.GetProperty("rootAssignmentId").GetString(),
            root.GetProperty("dynamicFormTemplateId").GetString(),
            root.GetProperty("dynamicExcelTemplateId").GetString(),
            root.GetProperty("blockId").GetString(),
            root.GetProperty("labelCode").GetString(),
            root.GetProperty("periodKey").GetString(),
            root.GetProperty("periodInstanceKey").GetString(),
            I(root.GetProperty("reportStatus").GetInt64()));
        var totals = ApiTotals(
            ("totalReportCount", "INTEGER", "2"),
            ("totalRowCount", "INTEGER", "3"),
            ("totalRows", "INTEGER", "1"));
        return WithApi(
            StatisticReconciliationActualCrossViewFamilies.Direct,
            StatisticReconciliationActualApiSurfaces.DirectLabel,
            StatisticReconciliationActualApiSurfaces.DirectLabel,
            true,
            export,
            owner,
            generationId,
            apiGeneration,
            generationHash,
            apiFilter,
            [(identity, source)],
            totals);
    }

    internal static Fixture Basic(bool flow)
    {
        var kind = flow ? "FLOW" : "BASIC";
        var family = flow
            ? StatisticReconciliationActualCrossViewFamilies.Flow
            : StatisticReconciliationActualCrossViewFamilies.Basic;
        var owner = "snapshot-1";
        var export = Export(kind,
            [Canonical(new { meta = kind, value = 1m })],
            false,
            resultId: owner);
        var apiFilter = Canonical(new { });
        var basicGeneration = BasicGenerationPreimage(export);
        var generation = BasicGenerationSha(
            owner,
            export,
            basicGeneration);
        return WithApi(
            family,
            StatisticReconciliationActualApiSurfaces.BasicSource,
            kind,
            false,
            export,
            owner,
            owner,
            generation,
            export.Manifest.ResultSha256,
            apiFilter,
            [],
            ApiTotals(("totalRows", "INTEGER", "0")),
            basicGeneration);
    }

    internal static Fixture DiffEmpty()
    {
        const string kind = "DIFF";
        var export = Export(kind, [], false);
        var apiFilter = Canonical(new { });
        var owner = export.Manifest.ResultId;
        var generation = export.Manifest.ResultSha256;
        return WithApi(
            StatisticReconciliationActualCrossViewFamilies.Diff,
            StatisticReconciliationActualApiSurfaces.P9Diff,
            kind,
            true,
            export,
            owner,
            owner,
            generation,
            generation,
            apiFilter,
            [],
            ApiTotals(
                ("totalChangedRowCount", "INTEGER", "0"),
                ("totalEqualRowCount", "INTEGER", "0"),
                ("totalRowCount", "INTEGER", "0"),
                ("totalRows", "INTEGER", "0")));
    }

    private static Fixture ExportOnly(
        string family,
        string resultKind,
        ImmutableArray<string> rows)
    {
        var export = Export(resultKind, rows, false);
        var plan = Plan(
            family,
            StatisticReconciliationActualCrossViewApiApplicability
                .NoProductionApi,
            null,
            resultKind,
            false,
            export,
            null, null, null, null, 0, 0, 0);
        return new(
            plan,
            new(
                StatisticReconciliationActualCrossViewParityV2Schemas.Base,
                null,
                export.Base),
            new(
                StatisticReconciliationActualCrossViewParityV2Schemas.Actual,
                null,
                export.Manifest,
                export.Capture));
    }

    private static Fixture WithApi(
        string family,
        string surface,
        string resultKind,
        bool shared,
        ExportData export,
        string owner,
        string generationId,
        string generationSha,
        string exportResultSha,
        string apiFilter,
        ImmutableArray<(string Identity, string Json)> rows,
        ImmutableArray<StatisticReconciliationActualApiTotalValue> totals,
        StatisticReconciliationActualCrossViewBasicGenerationPreimage?
            basicGeneration = null)
    {
        var auth = Authorization();
        var api = Api(surface, owner, generationId, generationSha,
            apiFilter, auth, rows, totals);
        var plan = Plan(
            family,
            StatisticReconciliationActualCrossViewApiApplicability.Required,
            surface,
            resultKind,
            shared,
            export with
            {
                Manifest = export.Manifest with
                {
                    ResultSha256 = exportResultSha
                }
            },
            owner,
            generationId,
            generationSha,
            apiFilter,
            rows.Length,
            200,
            1,
            basicGeneration);
        // Direct fixtures require the export result hash from the P9 producer;
        // rebuilding the manifest would change its digest, so construct Direct
        // exports with that producer hash from the outset.
        if (!string.Equals(export.Manifest.ResultSha256, exportResultSha,
                StringComparison.Ordinal))
        {
            export = Export(resultKind,
                export.Base.Rows.Select(row => row.CanonicalSourceRowJson)
                    .ToImmutableArray(),
                family == StatisticReconciliationActualCrossViewFamilies.Direct,
                resultSha: exportResultSha,
                resultId: export.Manifest.ResultId);
            plan = Plan(
                family,
                StatisticReconciliationActualCrossViewApiApplicability.Required,
                surface,
                resultKind,
                shared,
                export,
                owner,
                generationId,
                generationSha,
                apiFilter,
                rows.Length,
                200,
                1,
                basicGeneration);
        }
        return new(
            plan,
            new(
                StatisticReconciliationActualCrossViewParityV2Schemas.Base,
                new(
                    StatisticReconciliationActualCrossViewParityV2Schemas.Base,
                    Work,
                    Scope,
                    Template,
                    Period,
                    auth.AuthorizationSnapshotSha256,
                    surface,
                    owner,
                    generationId,
                    generationSha,
                    apiFilter,
                    RawSha(apiFilter),
                    rows.Select((row, index) =>
                            new StatisticReconciliationActualCrossViewApiBaseRow(
                                index, row.Identity, row.Json))
                        .ToImmutableArray(),
                    totals),
                export.Base),
            new(
                StatisticReconciliationActualCrossViewParityV2Schemas.Actual,
                api,
                export.Manifest,
                export.Capture));
    }

    private static StatisticReconciliationActualCrossViewParityV2Plan Plan(
        string family,
        string applicability,
        string? surface,
        string resultKind,
        bool shared,
        ExportData export,
        string? owner,
        string? generationId,
        string? generationSha,
        string? apiFilter,
        long apiRows,
        int pageSize,
        int pageCount,
        StatisticReconciliationActualCrossViewBasicGenerationPreimage?
            basicGeneration = null)
        => new(
            StatisticReconciliationActualCrossViewParityV2Schemas.Plan,
            family,
            applicability,
            surface,
            resultKind,
            shared,
            Work,
            "ASSIGNMENT",
            Scope,
            Template,
            Period,
            export.Manifest.AuthorizationSnapshotSha256,
            owner,
            generationId,
            generationSha,
            family == StatisticReconciliationActualCrossViewFamilies.Direct
                ? export.Manifest.ResultSha256
                : null,
            family == StatisticReconciliationActualCrossViewFamilies.Direct
                ? 1L
                : null,
            family == StatisticReconciliationActualCrossViewFamilies.Direct
                ? 1L
                : null,
            basicGeneration,
            apiFilter,
            apiFilter is null ? null : RawSha(apiFilter),
            apiRows,
            pageSize,
            pageCount,
            export.Manifest.ExportId,
            export.Manifest.ResultId,
            export.Manifest.ResultSha256,
            export.Manifest.ConfigSha256,
            export.Manifest.SourceSha256,
            export.Manifest.LifecycleRevision,
            export.Manifest.RequestSha256,
            export.Manifest.ContentSha256,
            export.ColumnManifestSha256,
            export.Manifest.OwnerSemanticSha256,
            export.Manifest.CanonicalFilterJson!,
            export.Manifest.FilterSha256);

    private static StatisticReconciliationActualApiCapture Api(
        string surface,
        string owner,
        string generationId,
        string generationSha,
        string filter,
        StatisticReconciliationActualApiAuthorizationContext authorization,
        ImmutableArray<(string Identity, string Json)> rows,
        ImmutableArray<StatisticReconciliationActualApiTotalValue> totals)
    {
        var route = StatisticReconciliationActualApiProtocol.RouteId(surface);
        var filterSha = RawSha(filter);
        var requestSha = StatisticReconciliationActualApiProtocol.RequestSha(
            surface, route, Work, Scope, Template, owner, filterSha,
            authorization.AuthorizationSnapshotSha256, 0, 200);
        var observedRows = rows.Select((row, index) =>
        {
            var rowSha = StatisticReconciliationActualApiObservationAdapter.RowSha(
                row.Identity, row.Json);
            return new StatisticReconciliationActualApiRowObservation(
                index, row.Identity, row.Json, rowSha, true);
        }).ToImmutableArray();
        var pageSha = HValues(
            "P10_ACTUAL_API_PAGE_V1",
            surface,
            route,
            Work,
            Scope,
            Template,
            owner,
            filterSha,
            authorization.AuthorizationSnapshotSha256,
            requestSha,
            $"\"sha256-{generationSha}\"",
            generationId,
            generationSha,
            "0",
            "200",
            I(rows.Length),
            "true", "true", "true", "true", "true",
            ApiTotalsSha(totals),
            HS("P10_ACTUAL_API_PAGE_ROWS_V1",
                observedRows.Select(row => row.RowSemanticSha256)));
        var page = new StatisticReconciliationActualApiPageObservation(
            0,
            200,
            rows.Length == 0 ? 0 : 1,
            rows.Length,
            rows.Length,
            totals,
            observedRows,
            $"\"sha256-{generationSha}\"",
            generationId,
            generationSha,
            true, true, true, true, true,
            pageSha);
        var captureSha = HValues(
            "P10_ACTUAL_API_CAPTURE_V1",
            surface,
            route,
            Work,
            Scope,
            Template,
            owner,
            filterSha,
            authorization.AuthorizationSnapshotSha256,
            I(rows.Length),
            StatisticReconciliationActualApiCaptureStates.Ready,
            null,
            "true", "true", "true", "true", "true",
            HS("P10_ACTUAL_API_PAGES_V1", [pageSha]));
        return new(
            surface, route, Work, Scope, Template, owner, filter, filterSha,
            authorization, [page], true, true, true, true,
            StatisticReconciliationActualApiCaptureStates.Ready,
            null,
            captureSha);
    }

    private static StatisticReconciliationActualApiAuthorizationContext
        Authorization()
    {
        var permissions = ImmutableArray.Create("READ");
        var sha = StatisticReconciliationActualApiObservationAdapter
            .AuthorizationSha("actor-1", Work, Scope, permissions, 1, 1);
        return new("actor-1", Work, Scope, permissions, 1, 1, sha, true);
    }

    private static ExportData Export(
        string resultKind,
        ImmutableArray<string> sourceRows,
        bool directFilter,
        string? resultSha = null,
        string resultId = "result-1")
    {
        var declaredDirectHeaders = string.Equals(
            resultKind,
            StatRunExportResultKinds.DirectField,
            StringComparison.Ordinal) && sourceRows.Length > 0;
        var headers = declaredDirectHeaders
            ? DirectFieldExportHeaders.ToList()
            : new List<string> { "ordinal" };
        foreach (var source in declaredDirectHeaders
                     ? ImmutableArray<string>.Empty
                     : sourceRows)
        {
            using var document = JsonDocument.Parse(source);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var property in document.RootElement.EnumerateObject())
                if (!headers.Contains(property.Name, StringComparer.Ordinal))
                    headers.Add(property.Name);
        }
        var columns = BuildColumns(resultKind, headers, sourceRows);
        var columnSidecar = StatRunExportColumnManifestContract.Create(
            columns.Select(column => new StatRunExportColumnManifestEntry(
                column.Ordinal, column.Name, column.ValueType,
                column.BlankPolicy, column.IsFullFilterTotal)).ToArray());
        var csv = BuildCsv(columns, sourceRows);
        var contentSha = Convert.ToHexString(SHA256.HashData(csv))
            .ToLowerInvariant();
        var filter = ExportFilter(directFilter);
        var filterSha = RawSha(filter);
        resultSha ??= H($"{resultKind}-result");
        var config = H($"{resultKind}-config");
        var sourceSha = H($"{resultKind}-source");
        var lifecycleRevision = resultKind.StartsWith(
            "DIRECT_", StringComparison.Ordinal) ? 1 : 0;
        var ownerSha = StatRunExportColumnManifestContract
            .ComputeOwnerSemanticSha256(
                resultKind, Work, "ASSIGNMENT", Scope, resultId, resultSha,
                config, sourceSha, filterSha, lifecycleRevision,
                sourceRows.Length,
                columns.Length, columnSidecar.Sha256);
        var authorization = Authorization();
        var manifest = new StatisticReconciliationActualExportManifest(
            StatisticReconciliationActualExportParser.RequiredSchemaVersion,
            "export-1",
            H($"{resultKind}-request"),
            authorization.AuthorizationSnapshotSha256,
            StatisticReconciliationActualExportFormats.Csv,
            resultKind,
            "text/csv; charset=utf-8",
            "result.csv",
            contentSha,
            csv.LongLength,
            sourceRows.Length,
            columns.Length,
            Work,
            "ASSIGNMENT",
            Scope,
            resultId,
            resultSha,
            config,
            sourceSha,
            filterSha,
            lifecycleRevision,
            "catalog-v1",
            H("catalog-raw"),
            H("catalog-semantic"),
            H("stage-lock"),
            "chain-1",
            "prompt-1",
            1,
            ownerSha,
            new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc),
            columns,
            Period,
            filter);
        var parser = new StatisticReconciliationActualExportParser();
        var manifestSha = parser.ComputeManifestSha256(manifest);
        var capture = parser.Parse(new(
            manifest,
            manifestSha,
            csv));
        var baseRows = sourceRows.Select((source, index) =>
                new StatisticReconciliationActualCrossViewExportBaseRow(
                    index + 1,
                    source,
                    capture.Rows[index].Cells,
                    capture.Rows[index].RowSemanticSha256))
            .ToImmutableArray();
        var baseProjection =
            new StatisticReconciliationActualCrossViewExportBaseProjection(
                StatisticReconciliationActualCrossViewParityV2Schemas.Base,
                Work,
                "ASSIGNMENT",
                Scope,
                Template,
                authorization.AuthorizationSnapshotSha256,
                resultKind,
                manifest.ResultId,
                manifest.ResultSha256,
                manifest.ConfigSha256,
                manifest.SourceSha256,
                manifest.LifecycleRevision,
                Period,
                filter,
                filterSha,
                columns,
                baseRows,
                capture.FullFilterTotals);
        return new(manifest, capture, baseProjection,
            columnSidecar.Sha256);
    }

    private static
        StatisticReconciliationActualCrossViewBasicGenerationPreimage
        BasicGenerationPreimage(ExportData export)
        => new(
            H("basic-request"),
            H("basic-request-json"),
            export.Manifest.SourceSha256,
            "config-1",
            "config-version-1",
            1,
            1,
            export.Manifest.ConfigSha256,
            ImmutableArray<string>.Empty,
            "chain-1",
            "prompt-1",
            1,
            export.Manifest.CatalogRawSha256,
            export.Manifest.CatalogSemanticSha256,
            export.Manifest.StageLockSha256,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty);

    private static string BasicGenerationSha(
        string snapshotId,
        ExportData export,
        StatisticReconciliationActualCrossViewBasicGenerationPreimage input)
        => HValues(
            "P10_ACTUAL_API_BASIC_GENERATION_V1",
            snapshotId,
            input.SnapshotRequestSha256,
            input.SnapshotRequestJsonSha256,
            input.SourceSignatureSha256,
            input.ConfigId,
            input.ConfigVersionId,
            I(input.ConfigVersionNo),
            I(input.ConfigRevision),
            input.ConfigSha256,
            HS("P10_ACTUAL_API_BASIC_CONFIG_DEPENDENCIES_V1",
                input.ConfigDependencyPins),
            input.CandidateChainId,
            input.CandidatePromptId,
            I(input.CandidateStage),
            input.CandidateCatalogRawSha256,
            input.CandidateCatalogSemanticSha256,
            input.CandidateStageLockSha256,
            export.Manifest.ResultSha256,
            HS("P10_ACTUAL_API_BASIC_ASSIGNMENT_IDS_V1",
                input.AssignmentIds),
            HS("P10_ACTUAL_API_BASIC_REPORT_IDS_V1", input.ReportIds));

    private static ImmutableArray<StatisticReconciliationActualExportColumnContract>
        BuildColumns(string resultKind,
            IReadOnlyList<string> headers,
            ImmutableArray<string> sourceRows)
    {
        var documents = sourceRows.Select(value =>
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }).ToArray();
        var result = ImmutableArray.CreateBuilder<
            StatisticReconciliationActualExportColumnContract>(headers.Count);
        result.Add(new(0, "ordinal", "INTEGER", "FORBIDDEN", false));
        for (var index = 1; index < headers.Count; index++)
        {
            var name = headers[index];
            string? type = null;
            var declared = StatRunExportColumnManifestContract
                .DeclaredValueType(resultKind, name);
            var declaredSchema = resultKind ==
                StatRunExportResultKinds.DirectField;
            Require(!declaredSchema || declared is not null,
                "TEST_DECLARED_COLUMN");
            var sawNull = false;
            var sawEmpty = false;
            foreach (var document in documents)
            {
                if (!document.TryGetProperty(name, out var value) ||
                    value.ValueKind == JsonValueKind.Null)
                {
                    sawNull = true;
                    continue;
                }
                if (value.ValueKind == JsonValueKind.String &&
                    value.GetString()?.Length == 0)
                {
                    sawEmpty = true;
                    continue;
                }
                var current = Type(name, value);
                Require(declared is null || declared == current,
                    "TEST_DECLARED_TYPE");
                if (type is not null && type != current)
                    throw new InvalidOperationException("MIXED_TEST_COLUMN");
                type = current;
            }
            type ??= declared;
            Require(type is not null && !(sawNull && sawEmpty),
                "TEST_COLUMN_TYPE");
            result.Add(new(
                index,
                name,
                type!,
                sawNull ? "NULL" : sawEmpty ? "EMPTY" : "FORBIDDEN",
                name.Length > 5 && name.StartsWith("total",
                    StringComparison.Ordinal) &&
                name[5] is >= 'A' and <= 'Z'));
        }
        return result.MoveToImmutable();
    }

    private static byte[] BuildCsv(
        ImmutableArray<StatisticReconciliationActualExportColumnContract> columns,
        ImmutableArray<string> sourceRows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", columns.Select(column =>
            Quote(column.Name))));
        for (var rowIndex = 0; rowIndex < sourceRows.Length; rowIndex++)
        {
            using var document = JsonDocument.Parse(sourceRows[rowIndex]);
            var root = document.RootElement;
            var fields = columns.Select(column => column.Ordinal == 0
                ? I(rowIndex + 1)
                : root.TryGetProperty(column.Name, out var value)
                    ? CsvValue(column.Name, value)
                    : string.Empty);
            builder.AppendLine(string.Join(",", fields.Select(Quote)));
        }
        var payload = Encoding.UTF8.GetBytes(
            builder.ToString().Replace("\n", "\r\n", StringComparison.Ordinal)
                .Replace("\r\r\n", "\r\n", StringComparison.Ordinal));
        return [0xef, 0xbb, 0xbf, .. payload];
    }

    private static string CsvValue(string name, JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String when IsInstant(name, value.GetString()) =>
                DateTime.SpecifyKind(
                    DateTime.Parse(value.GetString()!,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal |
                        DateTimeStyles.AdjustToUniversal),
                    DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
            JsonValueKind.String => Safe(value.GetString() ?? string.Empty),
            _ => StatisticReconciliationActualJson.Canonicalize(value)
        };

    private static string Type(string name, JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Number => "DECIMAL",
            JsonValueKind.True or JsonValueKind.False => "BOOLEAN",
            JsonValueKind.String when IsInstant(name, value.GetString()) =>
                "UTC_INSTANT",
            JsonValueKind.String => "TEXT",
            JsonValueKind.Object or JsonValueKind.Array => "JSON",
            _ => throw new InvalidOperationException("TEST_TYPE")
        };

    private static bool IsInstant(string name, string? value)
        => value is not null &&
           (name.EndsWith("Utc", StringComparison.Ordinal) ||
            name.EndsWith("Date", StringComparison.Ordinal)) &&
           DateTime.TryParse(value, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
               out _);

    private static string Safe(string value)
        => value.Length > 0 && value[0] is '=' or '+' or '-' or '@'
            ? $"'{value}"
            : value;

    private static string Quote(string value)
        => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string ExportFilter(bool direct)
        => Canonical(new
        {
            blockId = (string?)null,
            bucketKey = (string?)null,
            dynamicFormTemplateId = direct ? Template : null,
            fieldId = (string?)null,
            fieldKey = (string?)null,
            labelCode = (string?)null,
            metricKey = (string?)null,
            periodKey = (string?)null
        });

    private static ImmutableArray<StatisticReconciliationActualApiTotalValue>
        ApiTotals(params (string Name, string Type, string Value)[] values)
        => values.OrderBy(value => value.Name, StringComparer.Ordinal)
            .Select(value => new StatisticReconciliationActualApiTotalValue(
                value.Name, value.Type, value.Value))
            .ToImmutableArray();

    private static string ApiTotalsSha(
        ImmutableArray<StatisticReconciliationActualApiTotalValue> totals)
        => HS("P10_ACTUAL_API_FULL_FILTER_TOTALS_V1",
            totals.Select(total => HValues("P10_ACTUAL_API_TOTAL_V1",
                total.Name, total.ValueType, total.CanonicalValue)));

    internal sealed record ExportData(
        StatisticReconciliationActualExportManifest Manifest,
        StatisticReconciliationActualExportCapture Capture,
        StatisticReconciliationActualCrossViewExportBaseProjection Base,
        string ColumnManifestSha256);
}
