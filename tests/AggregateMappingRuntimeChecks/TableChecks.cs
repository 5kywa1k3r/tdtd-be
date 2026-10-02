using System.Net;
using System.Text.Json;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class TableChecks
{
    internal static async Task Run(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner runner, string run,
        Func<string, object, string?, HttpStatusCode, Task<JsonElement>> post, CancellationToken ct)
    {
        foreach (var layout in new[] { "matrix", "vertical" }) await Layout(layout);
        async Task Layout(string layout)
        {
            var table = new DynamicFormNativeTableDto { Id = "table", SectionId = "part", Name = "Bảng", Layout = layout, Order = 0,
                Fields = [new() { Id = "a", Name = "Giá trị", Order = 0 }],
                Rows = layout == "matrix" ? [new() { Id = "r1", Name = "Tiếp nhận" }, new() { Id = "r2", Name = "Đã xử lý" }] : [],
                TypeConfig = new() { Version = 1, Sequence = 1, Rules = [new() { Order = 1, Target = new() { Scope = "column", FieldId = "a" }, Spec = new() { Type = "number", Required = false } }] }, StatisticTargets = [] };
            var f = await RuntimeFixture.Seed(db, run + "-" + layout, ct, configureForm: (form, _) =>
            {
                form.SectionsJson = "[{\"id\":\"part\",\"title\":\"Phần\",\"order\":0,\"tagCodes\":[]}]";
                form.FieldsJson = "[]";
                form.NativeTablesVersion = 1; form.TablesJson = JsonSerializer.Serialize(new[] { table }, AggregateCanonical.Json);
            });
            await FixtureProvenanceChecks.Migration(db, "table " + layout + " after seed", ct);
            var payload = new WorkReportPayloadService(db); var store = new AggregateMongoStore(db, runner, payload, payload);
            var declaration = new AggregateDataWindowDeclarationDto("2026-09-01", "2026-09-30", "USER_DECLARED", run, 1);
            foreach (var report in new[] { f.Report, f.Source })
            {
                var form = report.Id == f.Report.Id ? f.TargetForm : f.SourceForm;
                report.DynamicFormSchemaHash = form.PublishedSchemaHash;
                var rows = Enumerable.Range(1, 2).Select(r => new Dictionary<string, object?> {
                    [layout == "matrix" ? "rowId" : "recordId"] = "r" + r,
                    ["cells"] = report.Id == f.Report.Id ? new Dictionary<string, object>() : new Dictionary<string, object> { ["a"] = new { type = "number", state = "value", value = r * 10 } } }).ToArray();
                var values = JsonSerializer.Serialize(new { nativeTables = new { version = 1, schemaHash = form.PublishedSchemaHash,
                    tables = new[] { new Dictionary<string, object?> { ["tableId"] = "table", [layout == "matrix" ? "rows" : "records"] = rows } } } }, AggregateCanonical.Json);
                DynamicFormNativeTableValues.Validate(form, form.PublishedSchemaHash, values, false);
                var saved = await payload.SaveReportPayloadAsync(report, "[]", "{}", values, null, f.Actor, DateTime.UtcNow, ct);
                report.PayloadRevision = saved.PayloadRevision; report.PayloadHash = saved.PayloadHash; report.PayloadSizeBytes = saved.PayloadSizeBytes; report.PayloadStatus = saved.PayloadStatus;
                await db.WorkAssignmentReports.ReplaceOneAsync(r => r.Id == report.Id, report, cancellationToken: ct);
                var period = await db.WorkReportPeriods.Find(p => p.Id == report.WorkReportPeriodId).SingleAsync(ct);
                await store.ExecuteAsync(async (tx, token) => {
                    var slot = period.WorkTemplateAssigneeId + ":ONCE";
                    await tx.PutAsync(AggregateCollections.Declarations, "REPORT:" + report.Id, 0, new AggregateDeclarationState("REPORT", report.Id, f.WorkId, report.WorkAssignmentId, declaration), f.WorkId, report.Id, [], token);
                    await tx.PutAsync(AggregateCollections.Declarations, "SLOT:" + slot, 0, new AggregateDeclarationState("SLOT", slot, f.WorkId, report.WorkAssignmentId, declaration), f.WorkId, slot, [], token);
                    return true;
                }, ct);
            }
            Task<JsonElement> Call(string path, object body) => post("aggregate-v2/" + path, body, f.Actor, HttpStatusCode.OK);
            void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); Console.WriteLine("PASS table " + layout + " " + name); }
            var boot = await Call("editor/bootstrap", new { reportId = f.Report.Id });
            var context = boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
            AggregateFormPinDto Pin(DynamicFormTemplate form) => new(form.Id, form.FamilyId!, form.VersionNo, form.PublishedSchemaHash!);
            var schema = await Call("editor/source-schema", new AggregateEditorSchemaQueryDto(context, Pin(f.SourceForm)));
            var member = schema.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "table");
            Check(member.GetProperty("table").GetProperty("layout").GetString() == layout
                && member.GetProperty("table").GetProperty("columns")[0].GetProperty("id").GetString() == "a", "source schema exposes correct table layout and column identity");
            AggregateExpressionDto Filter(int threshold) => new() { Kind = "CALL", Name = "ONLY", Arguments = [new() {
                Kind = "TABLE_FILTER", Ref = "in", Area = new("ALL", null, null, null, null), ColumnIndex = 1,
                Predicate = new() { Kind = "BINARY", Name = ">", Arguments = [new() { Kind = "INPUT", Ref = "cell" }, new() { Kind = "NUMBER", Value = threshold.ToString() }] } }] };
            var recipe = new AggregateRecipeDto { SchemaVersion = 1, SemanticProfile = "REPORT_MAPPING_V1",
                Nodes = [new() { Id = "s", Kind = "SOURCE", Form = Pin(f.SourceForm), Origin = "DIRECT_CHILD_REPORTS", SourceCardinality = "SET", Inputs = [], Outputs = [new("out", "TABLE", "SET", "table", "w")] },
                    new() { Id = "c", Kind = "CALCULATION", Inputs = [new("in", "TABLE", "SET")], Outputs = [new("out", "TABLE", "SINGLE")], Expressions = [new("out", Filter(15))] },
                    new() { Id = "t", Kind = "TARGET", Form = Pin(f.TargetForm), Inputs = [new("in", "TABLE", "SINGLE", "table")], Outputs = [] }],
                Edges = [new("e1", new("s", "out"), new("c", "in")), new("e2", new("c", "out"), new("t", "in"))],
                TimeRules = [new() { Id = "w", Mode = "TARGET_DATA_WINDOW", SourceDateBasis = "DECLARED_DATA_WINDOW", Match = "CONTAINED" }] };
            var config = await Call("configs", new AggregateConfigCreateCommandDto(context, new(run + "-table" + layout, context.BindingId, Pin(f.TargetForm), recipe)));
            var configId = config.GetProperty("id").GetString()!;
            var instance = await Call("instances", new AggregateInstanceCreateCommandDto(run + "-tableInstance" + layout, context, configId));
            var id = instance.GetProperty("id").GetString()!;
            var beforeForm = (await db.DynamicFormTemplates.Find(t => t.Id == f.TargetForm.Id).SingleAsync(ct)).TablesJson;
            async Task<JsonElement> Apply(long revision, AggregateInstanceOverrideDto? overlay = null)
            {
                var change = new AggregateMappingChangeDto(revision, overlay, new([new("s", "FORM_SELECTOR", [], [])]), [], false);
                var preview = await Call($"instances/{id}/mapping/preview", new AggregateMappingPreviewCommandDto(context, change));
                await Call($"instances/{id}/apply", new AggregateMappingApplyCommandDto(run + "-tableApply" + layout + revision, context, change, preview.GetProperty("token").GetString()!));
                var current = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct);
                var saved = await payload.LoadReportPayloadAsync(current, ct); WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(current, saved);
                DynamicFormNativeTableValues.Validate(f.TargetForm, current.DynamicFormSchemaHash, saved.TableValuesJson, false);
                return JsonDocument.Parse(saved.TableValuesJson!).RootElement.GetProperty("nativeTables").GetProperty("tables")[0].Clone();
            }
            var result = await Apply(1); var entries = result.GetProperty(layout == "matrix" ? "rows" : "records");
            if (layout == "matrix") Check(entries.GetArrayLength() == 2 && entries[0].GetProperty("rowId").GetString() == "r1"
                && !entries[0].GetProperty("cells").TryGetProperty("a", out _) && entries[1].GetProperty("rowId").GetString() == "r2"
                && entries[1].GetProperty("cells").GetProperty("a").GetProperty("value").GetDecimal() == 20, "native write masks excluded row values while retaining fixed destination rows");
            else Check(entries.GetArrayLength() == 1 && entries[0].GetProperty("cells").GetProperty("a").GetProperty("value").GetDecimal() == 20,
                "native write retains only matching records in selected value region");
            var version = (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateConfigVersion>(AggregateCollections.Versions, AggregateCommandService.VersionKey(configId, 1), token), ct))!.Value;
            result = await Apply(2, new(version.RecipeHash, [], [new("c", "out", Filter(100))], [])); entries = result.GetProperty(layout == "matrix" ? "rows" : "records");
            Check(layout == "matrix" ? entries.GetArrayLength() == 2 && entries.EnumerateArray().All(r => !r.GetProperty("cells").EnumerateObject().Any()) : entries.GetArrayLength() == 0,
                "empty filtered result has no invented zero values");
            Check(beforeForm == (await db.DynamicFormTemplates.Find(t => t.Id == f.TargetForm.Id).SingleAsync(ct)).TablesJson,
                "native Apply preserves destination header, structure and cell type definition");
        }
    }
}
