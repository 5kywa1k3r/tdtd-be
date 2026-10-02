using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.DynamicForms;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.DynamicForms;
using tdtd_be.Common.Errors;
using tdtd_be.Services.WorkAssignmentReports.Payloads;
using tdtd_be.Services.WorkAssignmentReports.Runtime;

internal static class ListRuntimeChecks
{
    internal static async Task Run(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner runner, IConfiguration configuration,
        string run, Func<string, object, string?, HttpStatusCode, Task<JsonElement>> post, CancellationToken ct, bool browserFixture = false, bool browserPeriodic = false, bool browserPaging = false)
    {
        if(browserFixture)await Mode(browserPeriodic);else {await Mode(false);await Mode(true);}
        async Task Mode(bool periodic)
        {
            var suffix = periodic ? "periodic" : "once";
            void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS L3 " + suffix + " " + message); }
            var catalogId = ObjectId.GenerateNewId().ToString();
            await db.LabelEnumCatalogs.InsertOneAsync(new LabelEnumCatalog { Id = catalogId, Code = run + suffix, Name = run,
                NameLower = run, CreatedByUsername = run, Options = [new() { Code = "A", Label = "Đơn vị công tác A", IsActive = true }, new() { Code = "B", Label = "Đơn vị công tác B", IsActive = true }] }, cancellationToken: ct);
            var table = new DynamicFormNativeTableDto { Id = "items", SectionId = "main", Name = "Danh sách thử L3", Order = 0, Layout = "vertical",
                Fields = [new() { Id = "score", Name = "Số vụ", Order = 0 }, new() { Id = "name", Name = "Ghi chú", Order = 1 },
                    new() { Id = "unit", Name = "Đơn vị công tác", Order = 2 }, new() { Id = "tags", Name = "Lĩnh vực", Order = 3 },
                    new() { Id = "day", Name = "Ngày tiếp nhận", Order = 4 }, new() { Id = "active", Name = "Đã xử lý", Order = 5 }], Rows = [],
                Presentation = new() { Kind = "LIST", ItemLabel = "Hồ sơ", AddLabel = "Thêm hồ sơ", SummaryFieldIds = ["name"] },
                ItemConstraints = new() { MinItems = 0, MaxItems = 200 }, StatisticTargets = [],
                TypeConfig = new() { Version = 1, Sequence = 6, Rules = [
                    new() { Order = 1, Target = new() { Scope = "column", FieldId = "score" }, Spec = new() { Type = "number", Required = false } },
                    new() { Order = 2, Target = new() { Scope = "column", FieldId = "name" }, Spec = new() { Type = "plainText", Required = false, MaxLength = 20000 } },
                    new() { Order = 3, Target = new() { Scope = "column", FieldId = "unit" }, Spec = new() { Type = "singleSelect", Required = false, ValueSource = new() { SourceType = "ENUM_CATALOG", CatalogId = catalogId } } },
                    new() { Order = 4, Target = new() { Scope = "column", FieldId = "tags" }, Spec = new() { Type = "multiSelect", Required = false, Options = [new() { Code = "X", Label = "X" }, new() { Code = "Y", Label = "Y" }] } },
                    new() { Order = 5, Target = new() { Scope = "column", FieldId = "day" }, Spec = new() { Type = "fullDate", Required = false } },
                    new() { Order = 6, Target = new() { Scope = "column", FieldId = "active" }, Spec = new() { Type = "boolean", Required = false } }] } };
            var f = await RuntimeFixture.Seed(db, run + "-" + suffix, ct, configureForm: (form, target) => {
                if (!target) form.FieldsJson = "[]";
                form.NativeTablesVersion = 2; form.TablesJson = JsonSerializer.Serialize(new[] { table }, AggregateCanonical.Json);
            }, publishForm: browserFixture ? (form, token) => ListL4BrowserFixture.Publish(db,form,catalogId,run,token) : null);
            var payloads = new WorkReportPayloadService(db); var store = new AggregateMongoStore(db, runner, payloads, payloads);
            var sourceBinding = await db.WorkTemplateAssignees.Find(b => b.WorkAssignmentId == f.Source.WorkAssignmentId).SingleAsync(ct);
            var source2 = BsonSerializer.Deserialize<WorkAssignmentReport>(f.Source.ToBson());
            source2.Id = ObjectId.GenerateNewId().ToString(); source2.WorkReportPeriodId = ObjectId.GenerateNewId().ToString(); source2.PayloadRevision = 0;
            var secondBinding = sourceBinding;
            if (periodic)
            {
                var schedule = new AssignmentSchedule { CycleType = "MONTHLY", StartDate = new DateTime(2026, 9, 1), MonthDays = [30] };
                await db.WorkAssignments.UpdateManyAsync(a => a.WorkId == f.WorkId, Builders<WorkAssignment>.Update.Set(a => a.AssignmentType, "PERIODIC").Set(a => a.Schedule, schedule), cancellationToken: ct);
                await db.WorkTemplateAssignees.UpdateManyAsync(b => b.WorkId == f.WorkId, Builders<WorkTemplateAssignee>.Update.Set(b => b.AssignmentType, "PERIODIC").Set(b => b.Schedule, schedule).Set(b => b.DueDate, new DateTime(2026, 10, 30)), cancellationToken: ct);
                f.Report.PeriodKey = "20261030"; f.Report.PeriodInstanceKey = f.Binding.Id + ":20261030";
                f.Source.PeriodKey = "20260930"; f.Source.PeriodInstanceKey = sourceBinding.Id + ":20260930";
                source2.PeriodKey = "20261030"; source2.PeriodInstanceKey = sourceBinding.Id + ":20261030";
            }
            else
            {
                var secondActor = ObjectId.GenerateNewId().ToString();
                await db.Users.InsertOneAsync(new AppUser { Id = secondActor, Username = run + "-" + secondActor,
                    UnitId = sourceBinding.AssigneeUnitId, CreatedByUserId = f.Actor }, cancellationToken: ct);
                var child = BsonSerializer.Deserialize<WorkAssignment>((await db.WorkAssignments.Find(a => a.Id == f.Source.WorkAssignmentId).SingleAsync(ct)).ToBson());
                child.Id = ObjectId.GenerateNewId().ToString(); child.Code += "-second"; child.Path += "/second";
                child.Assignees = [new UserRef { UserId = secondActor, UnitId = sourceBinding.AssigneeUnitId }];
                await db.WorkAssignments.InsertOneAsync(child, cancellationToken: ct);
                secondBinding = BsonSerializer.Deserialize<WorkTemplateAssignee>(sourceBinding.ToBson()); secondBinding.Id = ObjectId.GenerateNewId().ToString(); secondBinding.WorkAssignmentId = child.Id;
                secondBinding.AssigneeUserId = secondActor; source2.AssigneeUserId = secondActor;
                await db.WorkTemplateAssignees.InsertOneAsync(secondBinding, cancellationToken: ct);
                source2.WorkAssignmentId = child.Id; source2.PeriodInstanceKey = secondBinding.Id + ":ONCE";
            }
            if(browserFixture){
                // The periodic setup changed the stored schedule after these objects were loaded.
                // Re-read before attaching fixture units so a replacement cannot restore ONCE.
                if(periodic){sourceBinding=await db.WorkTemplateAssignees.Find(b=>b.Id==sourceBinding.Id).SingleAsync(ct);secondBinding=sourceBinding;}
                var unitA=ObjectId.GenerateNewId().ToString(); var unitB=ObjectId.GenerateNewId().ToString();
                var unitCode="991"+DateTime.UtcNow.ToString("HHmmss");
                await db.Units.InsertManyAsync(new[]{
                    new Unit{Id=f.Binding.AssigneeUnitId!,Code=unitCode,Level=3,FullName="Tỉnh thử L4 — Dữ liệu giả",Symbol="L4_TINH",CreatedByUserId=f.Actor},
                    new Unit{Id=unitA,Code=unitCode+"001",Level=4,FullName="Xã A thử L4 — Dữ liệu giả",Symbol="L4_XA_A",ParentUnitId=f.Binding.AssigneeUnitId,CreatedByUserId=f.Actor},
                    new Unit{Id=unitB,Code=unitCode+"002",Level=4,FullName="Xã B thử L4 — Dữ liệu giả",Symbol="L4_XA_B",ParentUnitId=f.Binding.AssigneeUnitId,CreatedByUserId=f.Actor}
                },cancellationToken:ct);
                foreach(var pair in new[]{(Binding:sourceBinding,Unit:unitA),(Binding:secondBinding,Unit:unitB)}.DistinctBy(p=>p.Binding.Id)){
                    pair.Binding.AssigneeUnitId=pair.Unit;
                    await db.WorkTemplateAssignees.ReplaceOneAsync(b=>b.Id==pair.Binding.Id,pair.Binding,cancellationToken:ct);
                    await db.Users.UpdateOneAsync(u=>u.Id==pair.Binding.AssigneeUserId,Builders<AppUser>.Update.Set(u=>u.UnitId,pair.Unit),cancellationToken:ct);
                    await db.WorkAssignments.UpdateOneAsync(a=>a.Id==pair.Binding.WorkAssignmentId,Builders<WorkAssignment>.Update
                        .Set(a=>a.TargetUnitIds,new List<string>{pair.Unit}).Set(a=>a.Assignees,new List<UserRef>{new(){UserId=pair.Binding.AssigneeUserId,UnitId=pair.Unit}}),cancellationToken:ct);
                }
            }
            string Values(WorkAssignmentReport report, int[] scores) => JsonSerializer.Serialize(new { nativeTables = new { version = 1,
                schemaHash = report.DynamicFormSchemaHash, tables = new[] { new { tableId = "items", records = scores.Select((score, i) => new {
                    recordId = $"00000000-0000-4000-8000-{(i + 1):000000000000}", cells = new { score = new { type = "number", state = "value", value = score },
                        name = new { type = "plainText", state = "value", value = (report.Id == f.Source.Id ? "An" : "Bình") + "-" + i + " " + new string('a', 8000) },
                        unit = new { type = "singleSelect", state = "value", value = i == 3 ? null : i == 0 ? "A" : "B" },
                        tags = new { type = "multiSelect", state = "value", value = i == 3 ? Array.Empty<string>() : new[] { "X", "Y" } },
                        day = new { type = "fullDate", state = "value", value = "29/09/2026" }, active = new { type = "boolean", state = "value", value = false }
                    } }) } } } }, AggregateCanonical.Json);
            async Task SaveSource(WorkAssignmentReport report, int[] scores)
            {
                var saved = await payloads.SaveReportPayloadAsync(report, "[]", "{}", Values(report, scores), null, f.Actor, DateTime.UtcNow, ct);
                report.PayloadRevision = saved.PayloadRevision; report.PayloadHash = saved.PayloadHash; report.PayloadSizeBytes = saved.PayloadSizeBytes; report.PayloadStatus = saved.PayloadStatus;
                await db.WorkAssignmentReports.ReplaceOneAsync(r => r.Id == report.Id, report, new ReplaceOptions { IsUpsert = true }, ct);
            }
            await SaveSource(f.Source, browserFixture ? browserPaging ? Enumerable.Range(1,22).ToArray() : [12, 9] : [12, 9, 6, 3]);
            await SaveSource(source2, browserFixture ? browserPaging ? Enumerable.Range(23,23).ToArray() : [11, 10, 8] : [11, 10, 8, 2]);
            // Exercise the authoritative validator without editing any stored/published form.
            var optionSets = new Dictionary<string, RuntimeEnumOptionSet> { [catalogId] = new(catalogId, new HashSet<string> { "A", "B" }) };
            DynamicFormNativeTableValues.Validate(f.SourceForm, f.Source.DynamicFormSchemaHash, Values(f.Source, [12, 9, 6, 3]), true, optionSets);
            Check(true, "optional single null and multi empty array remain valid on Submit");
            foreach (var fieldId in new[] { "unit", "tags" })
            {
                var requiredForm = BsonSerializer.Deserialize<DynamicFormTemplate>(f.SourceForm.ToBson());
                var requiredTable = table with { TypeConfig = table.TypeConfig! with { Rules = table.TypeConfig.Rules!.Select(r =>
                    r.Target!.FieldId == fieldId ? r with { Spec = r.Spec! with { Required = true } } : r).ToList() } };
                requiredForm.TablesJson = JsonSerializer.Serialize(new[] { requiredTable }, AggregateCanonical.Json);
                var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(requiredForm);
                requiredForm.PublishedSchemaHash = snapshot.Sha256; requiredForm.PublishedSchemaSnapshotJson = snapshot.Json;
                var requiredReport = BsonSerializer.Deserialize<WorkAssignmentReport>(f.Source.ToBson()); requiredReport.DynamicFormSchemaHash = snapshot.Sha256;
                var requiredPayload = Values(requiredReport, [12, 9, 6, 3]);
                DynamicFormNativeTableValues.Validate(requiredForm, snapshot.Sha256, requiredPayload, false, optionSets);
                var rejected = false;
                try { DynamicFormNativeTableValues.Validate(requiredForm, snapshot.Sha256, requiredPayload, true, optionSets); }
                catch (AppException ex) { rejected = JsonSerializer.Serialize(ex.Details).Contains("DYNAMIC_FORM_RUNTIME_FIELD_VALUE_REQUIRED", StringComparison.Ordinal); }
                Check(rejected, "required choice permits Draft but rejects blank on Submit " + fieldId);
                DynamicFormNativeTableValues.Validate(requiredForm, snapshot.Sha256, Values(requiredReport, [12]), true, optionSets);
                Check(true, "required choice accepts an actual selected value " + fieldId);
            }
            await db.WorkAssignmentReports.ReplaceOneAsync(r => r.Id == f.Report.Id, f.Report, cancellationToken: ct);
            foreach (var (report, binding, month) in new[] { (f.Report, f.Binding, periodic ? 10 : 9), (f.Source, sourceBinding, 9), (source2, secondBinding, periodic ? 10 : 9) })
            {
                await db.WorkReportPeriods.ReplaceOneAsync(p => p.Id == report.WorkReportPeriodId, new WorkReportPeriod {
                    Id = report.WorkReportPeriodId, WorkId = f.WorkId, WorkAssignmentId = report.WorkAssignmentId, WorkTemplateAssigneeId = binding.Id,
                    CurrentReportId = report.Id, AssigneeUserId = binding.AssigneeUserId, AssigneeUnitId = binding.AssigneeUnitId,
                    PeriodKey = report.PeriodKey, PeriodInstanceKey = report.PeriodInstanceKey, DynamicFormTemplateId = report.DynamicFormTemplateId,
                    DynamicFormFamilyId = report.DynamicFormFamilyId, DynamicFormVersionNo = report.DynamicFormVersionNo, DynamicFormSchemaHash = report.DynamicFormSchemaHash,
                    DueAtUtc = new DateTime(2026, month, 30), CreatedByUserId = f.Actor }, new ReplaceOptions { IsUpsert = true }, ct);
                var dates = new AggregateDataWindowDeclarationDto($"2026-{month:00}-01", $"2026-{month:00}-{DateTime.DaysInMonth(2026, month):00}", "USER_DECLARED", run, 1);
                await store.ExecuteAsync(async (tx, token) => {
                    await tx.PutAsync(AggregateCollections.Declarations, "REPORT:" + report.Id, 0, new AggregateDeclarationState("REPORT", report.Id, f.WorkId, report.WorkAssignmentId, dates), f.WorkId, report.Id, [], token);
                    var slot = binding.Id + ":" + report.PeriodKey;
                    await tx.PutAsync(AggregateCollections.Declarations, "SLOT:" + slot, 0, new AggregateDeclarationState("SLOT", slot, f.WorkId, report.WorkAssignmentId, dates), f.WorkId, slot, [], token);
                    return true;
                }, ct);
            }
            await FixtureProvenanceChecks.Migration(db, "L3 " + suffix + " final schema before pins", ct);
            Task<JsonElement> Call(string path, object body, HttpStatusCode status = HttpStatusCode.OK) => post("aggregate-v2/" + path, body, f.Actor, status);
            var boot = await Call("editor/bootstrap", new { reportId = f.Report.Id });
            var context = boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
            Check(boot.GetProperty("capabilities").GetProperty("listPipeline").GetProperty("supported").GetBoolean(), "List capability and native schema visible");
            Check(boot.GetProperty("capabilities").GetProperty("listPipeline").GetProperty("emptyMultiChoicePolicy").GetString() == "BLANK", "capability exposes approved empty multi choice policy");
            AggregateFormPinDto Pin(DynamicFormTemplate form) => new(form.Id, form.FamilyId!, form.VersionNo, form.PublishedSchemaHash!);
            var optionRequest = new AggregateListOptionsQueryDto(context, Pin(f.SourceForm), "items", "unit", PageSize: 1);
            var options = await Call("editor/list-options", optionRequest);
            Check(options.GetProperty("items")[0].GetProperty("code").GetString() == "A" && options.GetProperty("nextCursor").GetString() != null,
                "source-bound List catalog picker returns one page with labels");
            Check(!options.GetProperty("required").GetBoolean() && options.GetProperty("total").GetInt32() == 2
                && options.GetProperty("specialOptions")[0].GetProperty("label").GetString() == "Để trống"
                && options.GetProperty("specialOptions")[0].GetProperty("predicate").GetProperty("operator").GetString() == "IS_BLANK",
                "optional single picker has a separate blank predicate without a fake catalog code");
            var multiOptions = await Call("editor/list-options", optionRequest with { FieldId = "tags", Query = "missing" });
            Check(multiOptions.GetProperty("items").GetArrayLength() == 0 && multiOptions.GetProperty("specialOptions").GetArrayLength() == 1,
                "optional multi built-in blank option remains available outside catalog paging/search");
            var sourceSchema = await Call("editor/source-schema", new AggregateEditorSchemaQueryDto(context, Pin(f.SourceForm)));
            Check(sourceSchema.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "items")
                .GetProperty("list").GetProperty("fields").EnumerateArray().Where(c => c.GetProperty("id").GetString() is "unit" or "tags")
                .All(c => !c.GetProperty("required").GetBoolean() && c.GetProperty("specialOptions").GetArrayLength() == 1), "source schema carries required and special choice metadata");
            options = await Call("editor/list-options", optionRequest with { Cursor = options.GetProperty("nextCursor").GetString() });
            Check(options.GetProperty("items")[0].GetProperty("code").GetString() == "B", "List catalog picker next page is stable");
            await Call("editor/list-options", optionRequest with { FieldId = "name" }, HttpStatusCode.Conflict);
            await post("aggregate-v2/editor/list-options", optionRequest, f.Outsider, HttpStatusCode.NotFound);
            Check(true, "catalog options cannot bypass bound field or report context authority");
            var plan = new AggregateListPipelineDto { Version = 1, Scope = "ALL_SOURCES", Take = "TOP_N", TopN = 3,
                Sort = [new("score", "DESC", "LAST")], Project = table.Fields.Select(f => new AggregateListProjectionDto(f.Id!, f.Id!)).ToList(), Operation = "COLLECT" };
            if(browserPaging)plan=plan with {Take="ALL",TopN=null,Sort=[new("score","DESC","LAST"),new("name","ASC","LAST",true,false)]};
            AggregateExpressionDto Expression(AggregateListPipelineDto p) => new() { Kind = "LIST_PIPELINE", Ref = "in", ListPipeline = p };
            var recipe = new AggregateRecipeDto { SchemaVersion = 2, SemanticProfile = "REPORT_MAPPING_LIST_V1",
                Nodes = [new() { Id = "s", Kind = "SOURCE", Form = Pin(f.SourceForm), Origin = "DIRECT_CHILD_REPORTS", SourceCardinality = "SET", Inputs = [], Outputs = [new("out", "LIST", "SET", "items", "w")] },
                    new() { Id = "c", Kind = "CALCULATION", Inputs = [new("in", "LIST", "SET")], Outputs = [new("rows", "LIST", "SINGLE"), new("sum", "NUMBER", "SINGLE")],
                        Expressions = [new("rows", Expression(plan)), new("sum", Expression(plan with { Operation = "SUM", ValueFieldId = "score" }))] },
                    new() { Id = "t", Kind = "TARGET", Form = Pin(f.TargetForm), Inputs = [new("rows", "LIST", "SINGLE", "items"), new("sum", "NUMBER", "SINGLE", "total")], Outputs = [] }],
                Edges = [new("e1", new("s", "out"), new("c", "in")), new("e2", new("c", "rows"), new("t", "rows")), new("e3", new("c", "sum"), new("t", "sum"))],
                TimeRules = [new() { Id = "w", Mode = periodic ? "CUMULATIVE_FROM" : "TARGET_DATA_WINDOW", StartDate = periodic ? "2026-09-01" : null, SourceDateBasis = "DECLARED_DATA_WINDOW", Match = "CONTAINED" }] };
            var config = await Call("configs", new AggregateConfigCreateCommandDto(context, new(run + suffix + "config", context.BindingId, Pin(f.TargetForm), recipe)));
            var configId = config.GetProperty("id").GetString()!;
            var version = (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateConfigVersion>(AggregateCollections.Versions, AggregateCommandService.VersionKey(configId, 1), token), ct))!.Value;
            var instance = await Call("instances", new AggregateInstanceCreateCommandDto(run + suffix + "instance", context, configId));
            var instanceId = instance.GetProperty("id").GetString()!;
            if(browserFixture){
                // A fresh test-only actor can sign in through the real auth path. No existing account or fixture is changed.
                var actor = await db.Users.Find(u => u.Id == f.Actor).SingleAsync(ct);
                actor.Username = actor.Username.ToLowerInvariant();
                actor.FullName = "Kiểm thử L4 Danh sách";
                actor.PasswordHash = new PasswordHasher<AppUser>().HashPassword(actor, "123456@Aa");
                await db.Users.ReplaceOneAsync(u => u.Id == actor.Id, actor, cancellationToken: ct);
                // Distinct fixture-only assignment issuer for real reviewer Return UI.
                // Authorization is still derived by the normal role projector/policy.
                var reviewer = await db.Users.Find(u=>u.Id==f.Outsider).SingleAsync(ct);
                reviewer.Username=reviewer.Username.ToLowerInvariant();
                reviewer.FullName="Người duyệt L4 — Dữ liệu giả";
                reviewer.PasswordHash=new PasswordHasher<AppUser>().HashPassword(reviewer,"123456@Aa");
                await db.Users.ReplaceOneAsync(u=>u.Id==reviewer.Id,reviewer,cancellationToken:ct);
                await db.WorkAssignments.UpdateOneAsync(a=>a.Id==f.Report.WorkAssignmentId&&a.WorkId==f.WorkId,
                    Builders<WorkAssignment>.Update.Set(a=>a.CreatedByUserId,reviewer.Id),cancellationToken:ct);
                // The issuer also owns this newly seeded Work, so its normal detail route is readable.
                // Assignment review authority alone must not be treated as Work read authority.
                await db.Works.UpdateOneAsync(w=>w.Id==f.WorkId&&w.CreatedByUserId==f.Actor,
                    Builders<Work>.Update.Set(w=>w.CreatedByUserId,reviewer.Id),cancellationToken:ct);
                await ListL4BrowserFixture.Prepare(db,f.WorkId,ct);
                Console.WriteLine($"L4_REVIEWER username={reviewer.Username} actor={reviewer.Id} assignment={f.Report.WorkAssignmentId}");
                Console.WriteLine($"L4_FIXTURE username={actor.Username} work={f.WorkId} report={f.Report.Id} sourceA={f.Source.Id} sourceB={source2.Id} config={configId} instance={instanceId} counts={(browserPaging?"22+23":"2+3")}");
                return;
            }
            var selection = new AggregateInstanceSelectionDto([new("s", "FORM_SELECTOR", [], [])]);
            var change = new AggregateMappingChangeDto(1, null, selection, [], false);
            var badChoice = plan with { Where = new() { Operator = "IN", FieldId = "unit", Values = ["NOT_IN_BOUND_CATALOG"] } };
            var invalidChoice = await Call($"instances/{instanceId}/mapping/preview", new AggregateMappingPreviewCommandDto(context,
                change with { Overrides = new(version.RecipeHash, [], [new("c", "rows", Expression(badChoice))], []) }), HttpStatusCode.NotFound);
            Check(invalidChoice.GetProperty("code").GetString() == "AGG_LIST_CHOICE_UNAVAILABLE", "typed predicate rejects a code outside the source catalog");
            async Task<(string Id, JsonElement Result)> Preview(AggregateMappingChangeDto mutation)
            {
                var job = await Call("preview-jobs/start", new AggregatePreviewJobRequest("MAPPING", context, null, instanceId, mutation));
                var id = job.GetProperty("id").GetString()!;
                for (var i = 0; i < 150 && job.GetProperty("state").GetString() is "QUEUED" or "RUNNING"; i++)
                { await Task.Delay(100, ct); job = await Call($"preview-jobs/{id}/read", new { }); }
                Check(job.GetProperty("state").GetString() == "COMPLETED", "Hangfire job completes " + job.GetRawText()[..Math.Min(160, job.GetRawText().Length)]);
                return (id, job.GetProperty("result").Clone());
            }
            var before = (await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct)).ToJson();
            await db.Units.InsertOneAsync(new Unit {Id=f.Binding.AssigneeUnitId!,Code=run+suffix+"display",Level=3,
                FullName="Đơn vị báo cáo thử metadata",CreatedByUserId=f.Actor},cancellationToken:ct);
            var prepared = await Preview(change);
            var envelope = prepared.Result.GetProperty("preview"); var response = envelope.GetProperty("preview");
            Check(response.GetProperty("results")[1].GetProperty("value").GetString() == "33", "global top3 reads both eligible reports and sums33");
            Check(before == (await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct)).ToJson(), "preview never writes native report");
            Check(response.GetProperty("issues").EnumerateArray().Any(x => x.GetProperty("code").GetString() == "AGG_LIST_TOP_N_TRUNCATED"), "TopN truncation has explicit warning");
            var reference = response.GetProperty("results")[0].GetProperty("value");
            var query = new AggregateListPageRequestDto(f.Report.Id, prepared.Id, reference.GetProperty("id").GetString()!, reference.GetProperty("hash").GetString()!, Limit: 1);
            var page = await Call("lists/page", query);
            Check(page.GetProperty("total").GetInt32() == 3 && page.GetProperty("items").GetArrayLength() == 1 && page.GetRawText().Length < 2000, "page transfers excerpts instead of all long text and lineage");
            var unitCell=page.GetProperty("items")[0].GetProperty("cells").GetProperty("unit");
            Check(unitCell.GetProperty("displayOptions")[0].GetProperty("label").GetString()=="Đơn vị công tác A"
                && unitCell.GetProperty("labelState").GetString()=="AVAILABLE" && !page.TryGetProperty("metadata",out _),
                "page resolves only bound choice labels and never exposes internal metadata index");
            var manifest=await db.Db.GetCollection<BsonDocument>(AggregateListStore.Manifests).Find(new BsonDocument("_id",query.SnapshotId)).SingleAsync(ct);
            var legacyRowId=manifest["rowIds"].AsBsonArray[0];
            await db.Db.GetCollection<BsonDocument>(AggregateListStore.Rows).UpdateOneAsync(new BsonDocument { ["_id"]=legacyRowId,["scope"]=f.Report.Id },
                new BsonDocument("$unset",new BsonDocument("metadata","")),cancellationToken:ct);
            var legacyPage=await Call("lists/page",query);
            Check(legacyPage.GetProperty("items")[0].GetProperty("cells").GetProperty("unit").GetProperty("displayOptions")[0].GetProperty("label").GetString()=="Đơn vị công tác A",
                "pre-index immutable snapshot reads labels without changing its hash or native payload");
            var detail = await Call("lists/detail", query with { RowKey = page.GetProperty("items")[0].GetProperty("key").GetString(), FieldId = "name", IncludeLineage = true });
            Check(detail.GetProperty("cell").GetProperty("value").GetString()!.Length > 8000 && detail.GetProperty("origin").GetProperty("recordId").GetString() != null, "detail returns full text with stable item/source pins");
            Check(detail.GetProperty("origin").GetProperty("unitName").GetString()=="Đơn vị báo cáo thử metadata"
                && detail.GetProperty("origin").GetProperty("listName").GetString()==table.Name,
                "lineage uses reporting unit name separately from employment choice label");
            var choiceDetail=await Call("lists/detail",query with {RowKey=page.GetProperty("items")[0].GetProperty("key").GetString(),FieldId="unit"});
            Check(!choiceDetail.TryGetProperty("origin",out _)&&!choiceDetail.TryGetProperty("metadata",out _)
                &&choiceDetail.GetProperty("cell").GetProperty("displayOptions")[0].GetProperty("label").GetString()=="Đơn vị công tác A",
                "value-only choice detail includes display label without source identity");
            var listStore=new AggregateListStore(db);
            var originalList=await listStore.Load(f.Report.Id,reference.Deserialize<AggregateListReference>(AggregateCanonical.Json)!,ct);
            var aliasedList=originalList with {
                Schema=new(originalList.Schema.Fields.Select(field=>field.Id=="unit"?field with {Id="employment"}:field).ToArray()),
                Records=originalList.Records.Select(row=>row with {Cells=row.Cells.ToDictionary(p=>p.Key=="unit"?"employment":p.Key,p=>p.Value)}).ToArray()};
            var displayContext=await new AggregateMongoPreviewReader(db,payloads,new AggregateMongoDataWindows(db),true).BootstrapAsync(f.Report.Id,f.Actor,ct);
            var aliasedRef=await listStore.CaptureAsync(displayContext,aliasedList,ct);
            var aliasedPage=JsonSerializer.SerializeToNode(await listStore.Page(f.Report.Id,aliasedRef.Id,aliasedRef.Hash,0,1,ct),AggregateCanonical.Json)!;
            await new AggregateListDisplayMetadata(db).Enrich(aliasedPage,"PAGE",false,ct);
            Check(aliasedPage["items"]![0]!["cells"]!["employment"]!["displayOptions"]![0]!["label"]!.GetValue<string>()=="Đơn vị công tác A",
                "display projection follows original field lineage after output aliasing");
            await db.Units.UpdateOneAsync(u=>u.Id==f.Binding.AssigneeUnitId,Builders<Unit>.Update.Set(u=>u.IsDeleted,true),cancellationToken:ct);
            try {
                var rejectedUnit=await Call("lists/detail",query with {RowKey=page.GetProperty("items")[0].GetProperty("key").GetString(),FieldId="unit",IncludeLineage=true},HttpStatusCode.Conflict);
                Check(rejectedUnit.GetProperty("code").GetString()=="AGG_INPUT_STALE","unit authority change invalidates the existing preview scope");
                var missingUnit=JsonSerializer.SerializeToNode(await listStore.Detail(f.Report.Id,query.SnapshotId,query.SnapshotHash,
                    page.GetProperty("items")[0].GetProperty("key").GetString()!,"unit",ct),AggregateCanonical.Json)!;
                await new AggregateListDisplayMetadata(db).Enrich(missingUnit,"DETAIL",true,ct);
                Check(missingUnit["origin"]!["unitNameState"]!.GetValue<string>()=="UNAVAILABLE"&&missingUnit["origin"]!["unitName"]==null,
                    "display adapter marks a missing unit name explicitly instead of substituting employment choice");
            } finally {await db.Units.UpdateOneAsync(u=>u.Id==f.Binding.AssigneeUnitId,Builders<Unit>.Update.Set(u=>u.IsDeleted,false),cancellationToken:CancellationToken.None);}
            var counts = await Call("lists/counts", query);
            Check(counts.GetProperty("total").GetInt32() == 2 && counts.GetProperty("items").GetArrayLength() == 1, "per-report counts are paged and distinct from item payload");
            await Call("lists/page", query with { Limit = 51 }, HttpStatusCode.Conflict);
            await Call("lists/page", query with { SnapshotHash = new string('0', 64) }, HttpStatusCode.NotFound);
            Check(true, "page rejects excessive size and forged snapshot");
            var perReportPlan = plan with { Scope = "PER_REPORT" };
            var perReport = await Preview(change with { Overrides = new(version.RecipeHash, [], [new("c", "rows", Expression(perReportPlan)),
                new("c", "sum", Expression(perReportPlan with { Operation = "SUM", ValueFieldId = "score" }))], []) });
            Check(perReport.Result.GetProperty("preview").GetProperty("preview").GetProperty("results")[1].GetProperty("value").GetString() == "56",
                "HTTP job per-report Top3 yields six rows and56 versus global33");
            var allPlan = plan with { Take = "ALL", TopN = null };
            var allItems = await Preview(change with { Overrides = new(version.RecipeHash, [], [new("c", "rows", Expression(allPlan)),
                new("c", "sum", Expression(allPlan with { Operation = "SUM", ValueFieldId = "score" }))], []) });
            var allRef = allItems.Result.GetProperty("preview").GetProperty("preview").GetProperty("results")[0].GetProperty("value");
            var deepPage = await Call("lists/page", query with { JobId = allItems.Id, SnapshotId = allRef.GetProperty("id").GetString()!,
                SnapshotHash = allRef.GetProperty("hash").GetString()!, Offset = 6, Limit = 2 });
            Check(deepPage.GetProperty("total").GetInt32() == 8 && deepPage.GetProperty("items").GetArrayLength() == 2
                && deepPage.GetProperty("nextOffset").ValueKind == JsonValueKind.Null && deepPage.GetRawText().Length < 4000,
                "later page returns bounded excerpts and terminal cursor");
            Check(deepPage.GetProperty("items").EnumerateArray().All(r => r.GetProperty("cells").GetProperty("unit").GetProperty("state").GetString() == "BLANK"
                && r.GetProperty("cells").GetProperty("tags").GetProperty("state").GetString() == "BLANK"), "staged List page shows single null and multi empty as blank");
            foreach (var fieldId in new[] { "unit", "tags" })
            {
                var blankPlan = allPlan with { Where = new() { Operator = "IS_BLANK", FieldId = fieldId } };
                var blankPreview = await Preview(change with { Overrides = new(version.RecipeHash, [], [new("c", "rows", Expression(blankPlan)),
                    new("c", "sum", Expression(allPlan with { Operation = "COUNT_VALUES", ValueFieldId = fieldId }))], []) });
                var blankResults = blankPreview.Result.GetProperty("preview").GetProperty("preview").GetProperty("results");
                Check(blankResults[0].GetProperty("value").GetProperty("count").GetInt32() == 2 && blankResults[1].GetProperty("value").GetString() == "6",
                    "job filters two blank rows and COUNT_VALUES counts six actual choices " + fieldId);
            }
            if (periodic)
            {
                foreach (var mode in new[] { "TARGET_DATA_WINDOW", "EXPLICIT_RANGE" })
                {
                    var time = recipe.TimeRules[0] with { Mode = mode, StartDate = mode == "EXPLICIT_RANGE" ? "2026-10-01" : null,
                        EndDate = mode == "EXPLICIT_RANGE" ? "2026-10-31" : null };
                    var alternate = await Preview(change with { Overrides = new(version.RecipeHash, [time], [], []) });
                    Check(alternate.Result.GetProperty("preview").GetProperty("preview").GetProperty("results")[1].GetProperty("value").GetString() == "29", "time mode " + mode + " selects October29 rather than cumulative33");
                }
            }
            await post("aggregate-v2/lists/page", query, f.Outsider, HttpStatusCode.NotFound); Check(true, "page rejects another actor");
            await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Source.Id, Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Draft).Inc(r => r.LifecycleRevision, 1), cancellationToken: ct);
            try {
                await Call("lists/page", query, HttpStatusCode.Conflict);
                var stale = await Call($"instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + suffix + "stale", context, change, prepared.Result.GetProperty("token").GetString()!), HttpStatusCode.NotFound);
                Check(stale.GetProperty("code").GetString() == "AGG_PREVIEW_UNAVAILABLE", "incomplete source set refuses Apply without a result");
                Check(true, "source status change invalidates page and Apply token");
            }
            finally { await db.WorkAssignmentReports.UpdateOneAsync(r => r.Id == f.Source.Id && r.Status == WorkAssignmentReportStatus.Draft,
                Builders<WorkAssignmentReport>.Update.Set(r => r.Status, WorkAssignmentReportStatus.Approved).Inc(r => r.LifecycleRevision, 1), cancellationToken: CancellationToken.None); }
            prepared = await Preview(change);
            await Call($"instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + suffix + "apply", context, change, prepared.Result.GetProperty("token").GetString()!));
            var savedReport = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct);
            var saved = await payloads.LoadReportPayloadAsync(savedReport, ct);
            DynamicFormNativeTableValues.Validate(f.TargetForm, savedReport.DynamicFormSchemaHash, saved.TableValuesJson, false,
                new Dictionary<string, RuntimeEnumOptionSet> { [catalogId] = new(catalogId, new HashSet<string> { "A", "B" }) });
            var resultRows = JsonDocument.Parse(saved.TableValuesJson!).RootElement.GetProperty("nativeTables").GetProperty("tables")[0].GetProperty("records");
            Check(resultRows.GetArrayLength() == 3 && resultRows.EnumerateArray().All(r => Guid.TryParse(r.GetProperty("recordId").GetString(), out _)), "Apply/readback writes List native records with UUIDs");
            Check(JsonDocument.Parse(saved.FieldValuesJson!).RootElement.GetProperty("total").GetDecimal() == 33, "List scalar result uses authoritative field writer");
            var readApplied = await Call($"instances/{instanceId}/read", new AggregateInstanceReadCommandDto(context));
            var savedRef = readApplied.GetProperty("lastResults")[0].GetProperty("value");
            var savedQuery = query with { JobId = null, ReadId = readApplied.GetProperty("listReadId").GetString(),
                SnapshotId = savedRef.GetProperty("id").GetString()!, SnapshotHash = savedRef.GetProperty("hash").GetString()! };
            var savedPage = await Call("lists/page", savedQuery);
            Check(savedPage.GetProperty("total").GetInt32() == 3, "saved snapshot pages without a live preview job");
            var safeDetail = await Call("lists/detail", savedQuery with { RowKey = savedPage.GetProperty("items")[0].GetProperty("key").GetString(), FieldId = "name" });
            Check(!safeDetail.TryGetProperty("origin", out _) && !safeDetail.GetProperty("cell").TryGetProperty("lineage", out _)
                && safeDetail.GetProperty("cell").GetProperty("value").GetString()!.Length > 8000, "full text read is independent of source-lineage disclosure");
            await Call("lists/page", savedQuery with { SnapshotHash = new string('0', 64) }, HttpStatusCode.NotFound);
            await Call("lists/page", savedQuery with { ReportId = f.Source.Id }, HttpStatusCode.NotFound);
            await post("aggregate-v2/lists/page", savedQuery, f.Outsider, HttpStatusCode.NotFound);
            await Call("lists/page", savedQuery with { JobId = prepared.Id }, HttpStatusCode.Conflict);
            Check(true, "read scope rejects forged snapshot, report, actor and ambiguous authority");
            var firstCells = resultRows[0].GetProperty("cells");
            Check(firstCells.GetProperty("tags").GetProperty("value").GetArrayLength() == 2 && firstCells.GetProperty("unit").GetProperty("value").GetString() == "A"
                && !firstCells.GetProperty("active").GetProperty("value").GetBoolean() && firstCells.GetProperty("day").GetProperty("value").GetString() == "29/09/2026", "List enum, multi, date and false survive native Apply/readback");
            var idsBefore = resultRows.EnumerateArray().ToDictionary(r => r.GetProperty("cells").GetProperty("name").GetProperty("value").GetString()!, r => r.GetProperty("recordId").GetString());
            source2 = await db.WorkAssignmentReports.Find(r => r.Id == source2.Id).SingleAsync(ct); await SaveSource(source2, [14, 10, 8, 2]);
            await Call("lists/page", savedQuery, HttpStatusCode.Conflict);
            Check(true, "read scope rejects source revision change after issue");
            change = change with { ExpectedRevision = 2 }; prepared = await Preview(change);
            await Call($"instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + suffix + "reorder", context, change, prepared.Result.GetProperty("token").GetString()!));
            savedReport = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct); saved = await payloads.LoadReportPayloadAsync(savedReport, ct);
            resultRows = JsonDocument.Parse(saved.TableValuesJson!).RootElement.GetProperty("nativeTables").GetProperty("tables")[0].GetProperty("records");
            Check(resultRows.EnumerateArray().All(r => idsBefore[r.GetProperty("cells").GetProperty("name").GetProperty("value").GetString()!] == r.GetProperty("recordId").GetString()), "reordering after source change preserves output item identity");
            var blankChange = change with { ExpectedRevision = 3, Overrides = new(version.RecipeHash, [], [new("c", "rows", Expression(allPlan)),
                new("c", "sum", Expression(allPlan with { Operation = "SUM", ValueFieldId = "score" }))], []) };
            var allPrepared = await Preview(blankChange);
            await Call($"instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + suffix + "all-with-blanks", context, blankChange, allPrepared.Result.GetProperty("token").GetString()!));
            var allSaved = await payloads.LoadReportPayloadAsync(await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct), ct);
            var allSavedRows = JsonDocument.Parse(allSaved.TableValuesJson!).RootElement.GetProperty("nativeTables").GetProperty("tables")[0].GetProperty("records");
            Check(allSavedRows.GetArrayLength() == 8 && allSavedRows.EnumerateArray().Count(r => !r.GetProperty("cells").TryGetProperty("unit", out _)
                && !r.GetProperty("cells").TryGetProperty("tags", out _)) == 2, "Apply retains blank-choice records and writes canonical blank cells without fake enum codes");
            var unchangedSource = await payloads.LoadReportPayloadAsync(await db.WorkAssignmentReports.Find(r => r.Id == f.Source.Id).SingleAsync(ct), ct);
            var sourceBlankCells = JsonDocument.Parse(unchangedSource.TableValuesJson!).RootElement.GetProperty("nativeTables").GetProperty("tables")[0].GetProperty("records")[3].GetProperty("cells");
            Check(sourceBlankCells.GetProperty("tags").GetProperty("value").GetArrayLength() == 0 && sourceBlankCells.GetProperty("unit").GetProperty("value").ValueKind == JsonValueKind.Null,
                "Aggregate preview and Apply preserve the source null and empty-array payload");
            var overlay = new AggregateInstanceOverrideDto(version.RecipeHash, [], [new("c", "rows", Expression(plan with { TopN = 1 })), new("c", "sum", Expression(plan with { TopN = 1, Operation = "SUM", ValueFieldId = "score" }))], []);
            change = change with { ExpectedRevision = 4, Overrides = overlay }; prepared = await Preview(change);
            Check(prepared.Result.GetProperty("preview").GetProperty("preview").GetProperty("results")[1].GetProperty("value").GetString() == "14", "instance override previews independently of shared config");
            var selectedRef = prepared.Result.GetProperty("preview").GetProperty("preview").GetProperty("results")[0].GetProperty("value");
            var currentQuery = query with { JobId = prepared.Id, SnapshotId = selectedRef.GetProperty("id").GetString()!, SnapshotHash = selectedRef.GetProperty("hash").GetString()! };
            var changedInput = change with { Overrides = overlay with { Expressions = [new("c", "rows", Expression(plan)), new("c", "sum", Expression(plan with { Operation = "SUM", ValueFieldId = "score" }))] } };
            var inputDenied = await Call($"instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + suffix + "changed-input", context, changedInput, prepared.Result.GetProperty("token").GetString()!), HttpStatusCode.Conflict);
            Check(inputDenied.GetProperty("code").GetString() == "AGG_CONFIRMATION_STALE", "changed TopN input rejects old confirmation");
            await db.LabelEnumCatalogs.UpdateOneAsync(c => c.Id == catalogId, Builders<LabelEnumCatalog>.Update.Set(c => c.IsDeleted, true), cancellationToken: ct);
            try {
                var deniedCatalog = await Call("lists/page", currentQuery, HttpStatusCode.NotFound);
                Check(deniedCatalog.GetProperty("code").GetString() == "AGG_ENUM_CATALOG_UNAVAILABLE", "unavailable bound catalog denies frozen preview detail/page");
            } finally { await db.LabelEnumCatalogs.UpdateOneAsync(c => c.Id == catalogId, Builders<LabelEnumCatalog>.Update.Set(c => c.IsDeleted, false), cancellationToken: CancellationToken.None); }
            await db.WorkTemplateAssignees.UpdateOneAsync(b => b.Id == f.Binding.Id, Builders<WorkTemplateAssignee>.Update.Set(b => b.AssigneeUserId, f.Outsider), cancellationToken: ct);
            try {
                var deniedBinding = await Call("lists/page", currentQuery, HttpStatusCode.NotFound);
                Check(deniedBinding.GetProperty("code").GetString() == "AGG_CONTEXT_UNAVAILABLE", "revoked assignment owner cannot read old preview");
            } finally { await db.WorkTemplateAssignees.UpdateOneAsync(b => b.Id == f.Binding.Id, Builders<WorkTemplateAssignee>.Update.Set(b => b.AssigneeUserId, f.Actor), cancellationToken: CancellationToken.None); }
            var jobs = new AggregatePreviewJobs(db, payloads, configuration);
            var commands = new AggregateCommandService(store, new AggregateMongoCommandReader(db, payloads, true),
                new(Convert.FromBase64String(configuration["AggregateMapping:ConfirmationKeyBase64"]!)));
            await commands.SaveRawDraftAsync(new(run + suffix + "repair", "RAW_DRAFT", instanceId, f.Actor, run + "-session", DateTimeOffset.UtcNow),
                context, instanceId, change.ExpectedRevision, "{", ct);
            change = change with { ExpectedRevision = change.ExpectedRevision + 1 };
            var rawBeforeJob = (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, instanceId, token), ct))!.Value;
            var resetPreview = await Preview(change with { ResetToPinned = true });
            Check(resetPreview.Result.GetProperty("preview").GetProperty("preview").GetProperty("results")[1].GetProperty("value").GetString() == "36",
                "repair job can reset to pinned configuration with the same candidate rules as Apply");
            var queued = new AggregatePreviewJobRequest("MAPPING", context, null, instanceId, change with { Overrides = overlay with { Expressions = [new("c", "rows", Expression(plan with { TopN = 2 })), new("c", "sum", Expression(plan with { TopN = 2, Operation = "SUM", ValueFieldId = "score" }))] } });
            var queuedId = await jobs.Start(queued, f.Actor, run + "-session", ct);
            await jobs.Cancel(queuedId, f.Actor, run + "-session", ct);
            Check(JsonSerializer.SerializeToElement(await jobs.Read(queuedId, f.Actor, run + "-session", ct), AggregateCanonical.Json).GetProperty("state").GetString() == "CANCELLED", "cancel discards result without report mutation");
            var reopened = await jobs.Start(queued, f.Actor, run + "-session", ct); await jobs.Run(reopened, runner, payloads, ct);
            Check(JsonSerializer.SerializeToElement(await jobs.Read(reopened, f.Actor, run + "-session", ct), AggregateCanonical.Json).GetProperty("state").GetString() == "COMPLETED", "cancelled job can reopen on same authorized input");
            prepared = await Preview(change);
            var rawAfterJob = (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, instanceId, token), ct))!.Value;
            Check(AggregateCanonical.Hash(rawAfterJob.RawDraft) == AggregateCanonical.Hash(rawBeforeJob.RawDraft) && rawAfterJob.State == "NEEDS_REPAIR" && rawAfterJob.Revision == change.ExpectedRevision,
                "repair preview/cancel/reopen preserves stored raw draft and revision until confirmed Apply");
            await commands.SaveRawDraftAsync(new(run + suffix + "repair-edit", "RAW_DRAFT", instanceId, f.Actor, run + "-session", DateTimeOffset.UtcNow),
                context, instanceId, change.ExpectedRevision, "{\"changed\":true}", ct);
            var oldRepair = await Call($"instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + suffix + "stale-repair", context, change,
                prepared.Result.GetProperty("token").GetString()!), HttpStatusCode.Conflict);
            Check(oldRepair.GetProperty("code").GetString() == "AGG_REVISION_CONFLICT", "editing raw draft invalidates a previously confirmed repair candidate");
            change = change with { ExpectedRevision = change.ExpectedRevision + 1 }; prepared = await Preview(change);
            await Call($"instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + suffix + "top1", context, change, prepared.Result.GetProperty("token").GetString()!));
            var repaired = (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, instanceId, token), ct))!.Value;
            Check(repaired.RawDraft == null && repaired.State == "DRAFT", "confirmed repair Apply alone clears raw draft");
            var submission = await Call($"reports/{f.Report.Id}/submission-preview", new AggregateInstanceReadCommandDto(context));
            var submitRef = submission.GetProperty("instances")[0].GetProperty("preview").GetProperty("results")[0].GetProperty("value");
            var submitPage = await Call("lists/page", new AggregateListPageRequestDto(f.Report.Id, null, submitRef.GetProperty("id").GetString()!,
                submitRef.GetProperty("hash").GetString()!, ReadId: submission.GetProperty("listReadId").GetString()));
            Check(submitPage.GetProperty("total").GetInt32() == 1, "submission preview snapshot pages without preview-job authority");
            Check(submission.GetRawText().Contains(f.Source.Id, StringComparison.Ordinal), "source excluded by Top1 remains in submission dependency/lock set");
            await LifecycleChecks.Run(db, runner, configuration, f, run, instanceId, submission.GetProperty("token").GetString()!, ct, async () => {
                var frozenRead = await Call($"instances/{instanceId}/read", new AggregateInstanceReadCommandDto(context));
                Check(frozenRead.GetProperty("resultFreshness").GetString() == "FROZEN_SNAPSHOT", "frozen read retains snapshot state");
                var frozenQuery = new AggregateListPageRequestDto(f.Report.Id, null, submitRef.GetProperty("id").GetString()!,
                    submitRef.GetProperty("hash").GetString()!, ReadId: frozenRead.GetProperty("listReadId").GetString());
                var frozenPage = await Call("lists/page", frozenQuery);
                Check(frozenPage.GetProperty("total").GetInt32() == 1, "submitted readonly snapshot pages without editing or reopening a job");
                await Call("lists/page", frozenQuery with { ReadId = submission.GetProperty("listReadId").GetString() }, HttpStatusCode.Conflict);
                await db.Db.GetCollection<BsonDocument>(AggregateListReadScopes.Collection).UpdateOneAsync(new BsonDocument("_id", frozenQuery.ReadId),
                    new BsonDocument("$set", new BsonDocument("expiresAt", DateTime.UtcNow.AddMinutes(-1))), cancellationToken: ct);
                await Call("lists/page", frozenQuery, HttpStatusCode.NotFound);
                Check(true, "submission revision and read-scope expiry revoke old grants");
            });
            savedReport = await db.WorkAssignmentReports.Find(r => r.Id == f.Report.Id).SingleAsync(ct);
            saved = await payloads.LoadReportPayloadAsync(savedReport, ct);
            var sourcePayload = await payloads.LoadReportPayloadAsync(await db.WorkAssignmentReports.Find(r => r.Id == f.Source.Id).SingleAsync(ct), ct);
            var sourcePayload2 = await payloads.LoadReportPayloadAsync(await db.WorkAssignmentReports.Find(r => r.Id == source2.Id).SingleAsync(ct), ct);
            var evidence = JsonSerializer.SerializeToNode(new {
                recipe, context, change, instanceId, configId, sourceReportIds = new[] { f.Source.Id, source2.Id },
                sourceTableValuesJson = new[] { sourcePayload.TableValuesJson, sourcePayload2.TableValuesJson }, preview = prepared.Result,
                saved.FieldValuesJson, saved.TableValuesJson }, AggregateCanonical.Json)!;
            void Redact(System.Text.Json.Nodes.JsonNode? node)
            {
                if (node is System.Text.Json.Nodes.JsonObject obj)
                {
                    foreach (var key in new[] { "token", "confirmationToken", "previewToken" }) obj.Remove(key);
                    foreach (var property in obj.ToArray()) Redact(property.Value);
                }
                else if (node is System.Text.Json.Nodes.JsonArray array) foreach (var child in array) Redact(child);
            }
            Redact(evidence);
            await File.WriteAllTextAsync(Path.Combine("..", "outputs", run + "-" + suffix + "-handoff.json"), evidence.ToJsonString(new() { WriteIndented = true }), ct);
            Console.WriteLine($"L3_IDS mode={suffix} work={f.WorkId} report={f.Report.Id} sources={f.Source.Id},{source2.Id} config={configId} instance={instanceId}");
        }
    }
}
