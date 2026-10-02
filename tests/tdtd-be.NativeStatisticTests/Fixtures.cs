using System.Text.Json;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.StatisticsConfiguration;
using tdtd_be.Services.StatisticsRun;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Statistics;

namespace tdtd_be.NativeStatisticTests;

internal static class Fixtures
{
    internal const string Schema = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Sections = "[{\"id\":\"part\",\"title\":\"Phần\",\"order\":0,\"tagCodes\":[]}]";
    internal static readonly NativeStatisticCalculationLimits Limits = new(100, 1000, 100_000, 16_000_000, 16_000_000, 64);
    internal static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, StatConfigCanonicalJson.StrictJsonOptions);
    internal static DynamicFormNativeTableDto Table(string type = "number", string layout = "matrix") => new()
    {
        Id = "table", SectionId = "part", Name = "Bảng", Order = 0, Layout = layout,
        Fields = [new() { Id = "a", Name = "A", Order = 0 }, new() { Id = "b", Name = "B", Order = 1 }],
        Rows = layout == "matrix" ? [new() { Id = "r1", Name = "1" }, new() { Id = "r2", Name = "2" }] : [],
        TypeConfig = new() { Version = 1, Sequence = 2, Rules = new[] { "a", "b" }.Select((field, index) => new DynamicFormNativeTypeRuleDto { Order = index + 1, Target = new() { Scope = "column", FieldId = field },
            Spec = new() { Type = type, Required = false, Options = type is "singleSelect" or "multiSelect"
                ? [new() { Code = "A", Label = "Nhãn A" }, new() { Code = "B", Label = "Nhãn B" }] : null } }).ToList() },
        StatisticTargets = []
    };
    internal static string Plan(string[] methods, string grouping = "selection", string layout = "matrix",
        string reports = "reportIdAsc", string cells = "rowMajor", object? concatOptions = null, string bucket = "option")
    {
        var target = new DynamicFormNativeStatisticPlanTargetDto("table", "target",
            new() { Kind = layout == "matrix" ? "matrix" : "records", FieldIds = ["a", "b"],
                RowIds = layout == "matrix" ? ["r1", "r2"] : null, Records = layout == "matrix" ? null : "all" },
            grouping, new() { Mode = "all" }, new(cells, reports, "recordIdAsc"),
            methods.Select((method, i) => new DynamicFormNativeStatisticOperationDto("op" + i, method,
                Json(method == "CONCAT" ? concatOptions ?? ConcatOptions("\n")
                    : method == "BUCKET_COUNT" ? new { mode = bucket } : new { }))).ToArray(), [], true, true);
        return JsonSerializer.Serialize(new DynamicFormNativeStatisticPlanDto(2, [target]), StatConfigCanonicalJson.StrictJsonOptions);
    }
    internal static object ConcatOptions(string separator, string itemSeparator = "|", string blankPolicy = "keep", string duplicatePolicy = "keep", string choiceFormat = "code")
        => new { separator, itemSeparator, format = "canonical", choiceFormat, blankPolicy, duplicatePolicy };
    internal static (WorkAssignmentReport Report, WorkReportPayloadSnapshot Payload) Source(string type, object?[] values,
        int id = 1, string layout = "matrix", DateTime? time = null)
    {
        var rows = new List<Dictionary<string, object?>>();
        for (var r = 0; r < 2; r++)
        {
            var cells = new Dictionary<string, object?>();
            for (var f = 0; f < 2; f++)
                if (!ReferenceEquals(values[r * 2 + f], Missing)) cells[f == 0 ? "a" : "b"] = new { type, state = "value", value = values[r * 2 + f] };
            rows.Add(new() { [layout == "matrix" ? "rowId" : "recordId"] = "r" + (r + 1), ["cells"] = cells });
        }
        var raw = JsonSerializer.Serialize(new { nativeTables = new { version = 1, schemaHash = Schema,
            tables = new[] { new Dictionary<string, object?> { ["tableId"] = "table", [layout == "matrix" ? "rows" : "records"] = rows } } } }, StatConfigCanonicalJson.StrictJsonOptions);
        var hash = StatRunCanonicalJson.HashText(raw);
        var timestamp = time ?? new DateTime(2026, 9, 17, 0, 0, id, DateTimeKind.Utc);
        var report = new WorkAssignmentReport { Id = id.ToString("x24"), WorkId = "111111111111111111111111", WorkAssignmentId = "222222222222222222222222",
            PeriodInstanceKey = "period", PayloadRevision = 1, PayloadHash = hash, PayloadStatus = "Ready", PayloadUpdatedAtUtc = timestamp,
            LifecycleRevision = 1, IsCurrent = true, IsActive = true, Status = tdtd_be.Models.Enums.WorkAssignmentReportStatus.Approved };
        var payload = new WorkReportPayloadSnapshot("{}", null, raw, null, 1, hash, raw.Length, "Ready", true, true) { SourcePayloadUpdatedAtUtc = timestamp };
        return (report, payload);
    }
    internal static readonly object Missing = new();
    internal static NativeStatisticCalculationResult Calculate(string type, string[] methods, object?[] values,
        string grouping = "selection", string layout = "matrix", object? concatOptions = null,
        IEnumerable<(WorkAssignmentReport Report, WorkReportPayloadSnapshot Payload)>? sources = null,
        NativeStatisticCalculationLimits? limits = null, string reports = "reportIdAsc", string bucket = "option")
    {
        var input = (sources ?? [Source(type, values, layout: layout)]).ToArray();
        var pins = input.Select(s => WorkReportNativeSourcePin.Capture(s.Report, s.Payload)).ToArray();
        return NativeTableStatisticCalculator.Calculate(Plan(methods, grouping, layout, reports, concatOptions: concatOptions, bucket: bucket),
            [Table(type, layout)], Sections, "[]", "[]", Schema, pins, WorkReportNativeSourcePin.Digest(pins),
            input.Select(s => NativeStatisticCalculationSource.Capture(s.Report, s.Payload)), limits ?? Limits);
    }
    internal static WorkReportDirectGenerationContext Context(string run, string generation,
        IEnumerable<(WorkAssignmentReport Report, WorkReportPayloadSnapshot Payload)> sources) => new(run, generation,
            Schema, 1, "333333333333333333333333", "444444444444444444444444", 1, Schema,
            "555555555555555555555555", "666666666666666666666666", 1, 1, Schema, "test-candidate", "test-catalog",
            Schema, Schema, Schema, Schema, Schema, Schema,
            sources.ToDictionary(s => s.Report.Id, _ => new WorkReportDirectContributionBinding("INCLUDE", false)),
            new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc));
    internal static NativeStatisticGenerationArtifact Artifact(int identity = 1)
    {
        var inputs = new[] { Source("plainText", [new string('x', 1000) + "🌿\r\n", "A", "", "A"], 1), Source("plainText", ["Việt", "B", "C", "D"], 2) };
        var result = Calculate("plainText", ["CONCAT", "STACK_ROWS", "STACK_COLUMNS", "COUNT", "DISTINCT_COUNT"], [], sources: inputs,
            limits: Limits with { ChunkBytes = 2048 });
        var context = Context(identity.ToString("x24"), StatRunCanonicalJson.HashText("generation" + identity), inputs);
        return new(2, "111111111111111111111111", "period", context,
            JsonSerializer.Serialize(new { tables = new[] { Table("plainText") } }, StatConfigCanonicalJson.StrictJsonOptions),
            Plan(["CONCAT", "STACK_ROWS", "STACK_COLUMNS", "COUNT", "DISTINCT_COUNT"]), Limits,
            inputs.Select(s => new NativeStatisticStageSource(s.Report.Id, s.Report.WorkAssignmentId, s.Report.LifecycleRevision,
                null, WorkReportNativeSourcePin.Capture(s.Report, s.Payload))).ToArray(), result);
    }

    // Locked object assembled in RAM from a previously generated test draft.
    // PublicationCases explicitly seeds this synthetic owner into its dedicated
    // test database. No publish endpoint or real Form lifecycle is exercised.
    internal static (DynamicFormTemplate Template, WorkReportDirectGenerationContext Generation,
        (WorkAssignmentReport Report, WorkReportPayloadSnapshot Payload)[] Sources) LockedUnitInput(int identity = 30, DynamicFormTemplate? owner = null)
    {
        owner ??= JsonSerializer.Deserialize<DynamicFormTemplate>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "p8-draft-owner.fixture.json")), StatConfigCanonicalJson.StrictJsonOptions)!;
        owner.IsPublished = true;
        owner.StatisticConfigStatus = "LOCKED";
        owner.StatisticConfigSnapshots!.Single(s => s.VersionId == owner.StatisticConfigVersionId).Status = "LOCKED";
        var schema = DynamicFormPublishedSchemaSnapshotBuilder.Build(owner);
        owner.PublishedSchemaSnapshotJson = schema.Json; owner.PublishedSchemaHash = schema.Sha256;
        var definitions = DynamicFormNativeTableDefinition.ReadStored(owner.NativeTablesVersion, owner.TablesJson)!;
        var input = Enumerable.Range(1, 2).Select(id => {
            var tables = definitions.Select(table => {
                var matrix = table.Layout == "matrix";
                var resolver = DynamicFormNativeTableDefinition.CompileCellTypes(table);
                var rows = matrix ? table.Rows!.Select(r => r.Id!).ToArray() : new[] { "entry" };
                return new Dictionary<string, object?> { ["tableId"] = table.Id, [matrix ? "rows" : "records"] = rows.Select((row, i) =>
                    new Dictionary<string, object?> { [matrix ? "rowId" : "recordId"] = row,
                        ["cells"] = table.Fields!.ToDictionary(f => f.Id!, f => {
                            var type = resolver(f.Id!, matrix ? row : null).Type!;
                            object? value = type switch { "number" => (id - 1) * 4 + (i + 1) * 2, "boolean" => false,
                                "date" => "09/2026", "fullDate" => "17/09/2026", _ => "Việt " + id + "\r\n🌿" };
                            return new { type, state = "value", value };
                        }) }).ToArray() };
            }).ToArray();
            var raw = JsonSerializer.Serialize(new { nativeTables = new { version = 1, schemaHash = schema.Sha256, tables } }, StatConfigCanonicalJson.StrictJsonOptions);
            var pair = Source("number", [1, 2, 3, 4], id);
            pair.Report.DynamicFormTemplateId = owner.Id; pair.Report.DynamicFormFamilyId = owner.FamilyId;
            pair.Report.DynamicFormVersionNo = owner.VersionNo; pair.Report.DynamicFormSchemaHash = schema.Sha256;
            pair.Report.PayloadHash = StatRunCanonicalJson.HashText(raw); pair.Report.CumulativeContributionMode = "INCLUDE";
            return (pair.Report, pair.Payload with { TableValuesJson = raw, PayloadHash = pair.Report.PayloadHash, PayloadSizeBytes = raw.Length });
        }).ToArray();
        var generation = Context(identity.ToString("x24"), StatRunCanonicalJson.HashText("locked-unit" + identity), input) with {
            DynamicFormTemplateId = owner.Id, DynamicFormFamilyId = owner.FamilyId!, DynamicFormVersionNo = owner.VersionNo,
            DynamicFormSchemaHash = schema.Sha256, ConfigId = owner.StatisticConfigId!, ConfigVersionId = owner.StatisticConfigVersionId!,
            ConfigVersionNo = owner.StatisticConfigVersionNo, ConfigRevision = owner.StatisticConfigRevision, ConfigHash = owner.StatisticConfigHash! };
        return (owner, generation, input);
    }
}
