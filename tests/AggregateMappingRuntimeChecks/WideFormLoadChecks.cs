using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
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

// Explicit opt-in synthetic workload. Forty scalar values + 16 x 10 matrix cells,
// all mapped/calculated, not merely padding the schema with unused fields.
internal static class WideFormLoadChecks
{
    internal static async Task Run(MongoDbContext db, AggregateMongoStore store,
        IDynamicFlowDefinitionTransactionRunner runner, IConfiguration configuration, string run,
        Func<string, object, string?, HttpStatusCode, Task<JsonElement>> post, CancellationToken ct, bool mixedContent = false)
    {
        const int sources = 166, scalarCount = 40, rowCount = 16, columnCount = 10;
        var textCount = mixedContent ? 4 : 0;
        var numberCount = scalarCount - textCount;
        var watch = Stopwatch.StartNew();
        var payload = new WorkReportPayloadService(db);
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS wide " + message); }
        AggregateFormPinDto Pin(DynamicFormTemplate form) => new(form.Id, form.FamilyId!, form.VersionNo, form.PublishedSchemaHash!);
        string Id() => ObjectId.GenerateNewId().ToString();
        T Clone<T>(T value) => BsonSerializer.Deserialize<T>(value!.ToBson());
        var table = new DynamicFormNativeTableDto {
            Id = "table", SectionId = "main", Name = "Bảng 160 ô", Layout = "matrix", Order = scalarCount,
            Fields = Enumerable.Range(1, columnCount).Select(c => new DynamicFormNativeColumnDto { Id = "c" + c, Name = "Cột " + c, Order = c - 1 }).ToList(),
            Rows = Enumerable.Range(1, rowCount).Select(r => new DynamicFormNativeRowDto { Id = "r" + r, Name = "Dòng " + r }).ToList(),
            TypeConfig = new() { Version = 1, Sequence = columnCount, Rules = Enumerable.Range(1, columnCount).Select(c =>
                new DynamicFormNativeTypeRuleDto { Order = c, Target = new() { Scope = "column", FieldId = "c" + c }, Spec = new() { Type = "number", Required = false } }).ToList() },
            StatisticTargets = [] };
        var f = await RuntimeFixture.Seed(db, run + "-wide", ct, configureForm: (form, target) =>
        {
            form.FieldsJson = JsonSerializer.Serialize(Enumerable.Range(1, numberCount).Select(i =>
                new { id = "n" + i, key = "n" + i, name = "Giá trị " + i, label = "Giá trị " + i, sectionId = "main", type = "number", required = false, order = i - 1 })
                .Concat(Enumerable.Range(1, !target ? textCount : 0).Select(i =>
                    new { id = "text" + i, key = "text" + i, name = "Nội dung " + i, label = "Nội dung " + i, sectionId = "main", type = "longText", required = false, order = numberCount + i - 1 })));
            form.NativeTablesVersion = 1; form.TablesJson = JsonSerializer.Serialize(new[] { table }
                .Concat(Enumerable.Range(1, target ? textCount : 0).Select(WideContentChecks.Table)), AggregateCanonical.Json);
        });
        await FixtureProvenanceChecks.Migration(db, "wide after seed", ct);
        Task<JsonElement> Call(string path, object body) => post("aggregate-v2/" + path, body, f.Actor, HttpStatusCode.OK);
        var window = new AggregateDataWindowDeclarationDto("2026-09-01", "2026-09-30", "USER_DECLARED", run, 1);
        async Task WriteValues(WorkAssignmentReport report, DynamicFormTemplate form, int ordinal)
        {
            report.DynamicFormSchemaHash = form.PublishedSchemaHash;
            var values = ordinal == 0 ? new Dictionary<string, object>() : Enumerable.Range(1, numberCount).ToDictionary(i => "n" + i, i => (object)(ordinal * i));
            if (ordinal > 0) foreach (var i in Enumerable.Range(1, textCount)) values["text" + i] = WideContentChecks.Text(ordinal, i);
            var fields = JsonSerializer.Serialize(values);
            var rows = Enumerable.Range(1, rowCount).Select(r => new {
                rowId = "r" + r, cells = Enumerable.Range(1, columnCount).Where(_ => ordinal > 0).ToDictionary(c => "c" + c,
                    c => new { type = "number", state = "value", value = ordinal * (scalarCount + (r - 1) * columnCount + c) }) }).ToArray();
            var tables = JsonSerializer.Serialize(new { nativeTables = new { version = 1, schemaHash = form.PublishedSchemaHash,
                tables = new object[] { new { tableId = "table", rows } }.Concat(Enumerable.Range(1, ordinal == 0 ? textCount : 0)
                    .Select(i => (object)new { tableId = "content" + i, records = Array.Empty<object>() })) } }, AggregateCanonical.Json);
            DynamicFormNativeTableValues.Validate(form, form.PublishedSchemaHash, tables, false);
            var saved = await payload.SaveReportPayloadAsync(report, "[]", fields, tables, null, f.Actor, DateTime.UtcNow, ct);
            report.PayloadRevision = saved.PayloadRevision; report.PayloadHash = saved.PayloadHash;
            report.PayloadStatus = saved.PayloadStatus; report.PayloadSizeBytes = saved.PayloadSizeBytes;
        }
        async Task Declare(WorkAssignmentReport report)
        {
            await store.ExecuteAsync(async (tx, cancel) => {
                foreach (var item in new[] { ("REPORT", report.Id), ("SLOT", report.PeriodInstanceKey!) })
                    await tx.PutAsync(AggregateCollections.Declarations, item.Item1 + ":" + item.Item2, 0,
                        new AggregateDeclarationState(item.Item1, item.Item2, f.WorkId, report.WorkAssignmentId, window), f.WorkId, item.Item2, [], cancel);
                return true;
            }, ct);
        }
        foreach (var report in new[] { f.Report, f.Source })
        {
            await WriteValues(report, report.Id == f.Report.Id ? f.TargetForm : f.SourceForm, report.Id == f.Report.Id ? 0 : 1);
            await db.WorkAssignmentReports.ReplaceOneAsync(x => x.Id == report.Id, report, cancellationToken: ct);
            await Declare(report);
        }
        var templateAssignment = await db.WorkAssignments.Find(x => x.Id == f.Source.WorkAssignmentId).SingleAsync(ct);
        var templatePeriod = await db.WorkReportPeriods.Find(x => x.Id == f.Source.WorkReportPeriodId).SingleAsync(ct);
        var templateBinding = await db.WorkTemplateAssignees.Find(x => x.Id == templatePeriod.WorkTemplateAssigneeId).SingleAsync(ct);
        for (var ordinal = 2; ordinal <= sources; ordinal++)
        {
            var user = Id(); var unit = Id();
            await db.Users.InsertOneAsync(new AppUser { Id = user, Username = run + "-wide-" + ordinal, UnitId = unit, CreatedByUserId = f.Actor }, cancellationToken: ct);
            var assignment = Clone(templateAssignment); assignment.Id = Id(); assignment.Code = run + "-wide-" + ordinal;
            assignment.Path = "/" + f.Binding.WorkAssignmentId + "/" + assignment.Id;
            assignment.Assignees = [new UserRef { UserId = user, UnitId = unit }]; assignment.TargetUnitIds = [unit];
            var binding = Clone(templateBinding); binding.Id = Id(); binding.WorkAssignmentId = assignment.Id;
            binding.AssigneeUserId = user; binding.AssigneeUnitId = unit;
            var period = Clone(templatePeriod); period.Id = Id(); period.WorkAssignmentId = assignment.Id;
            period.AssigneeUserId = user; period.AssigneeUnitId = unit; period.WorkTemplateAssigneeId = binding.Id; period.PeriodInstanceKey = binding.Id + ":ONCE";
            var report = Clone(f.Source); report.Id = Id(); report.WorkAssignmentId = assignment.Id; report.AssigneeUserId = user;
            report.CreatedByUserId = user; report.WorkReportPeriodId = period.Id; report.PeriodInstanceKey = period.PeriodInstanceKey;
            report.PayloadRevision = 0; report.PayloadHash = null; period.CurrentReportId = report.Id;
            await WriteValues(report, f.SourceForm, ordinal);
            await db.WorkAssignments.InsertOneAsync(assignment, cancellationToken: ct);
            await db.WorkTemplateAssignees.InsertOneAsync(binding, cancellationToken: ct);
            await db.WorkReportPeriods.InsertOneAsync(period, cancellationToken: ct);
            await db.WorkAssignmentReports.InsertOneAsync(report, cancellationToken: ct);
            await Declare(report);
        }
        Console.WriteLine($"MEASURE wide seeded sources={sources} numberFields={numberCount} textFields={textCount} tableRows={rowCount} tableColumns={columnCount} valuesPerReport=200 totalSourceValues=33200 seedMs={watch.ElapsedMilliseconds} work={f.WorkId}");
        if (mixedContent) Console.WriteLine($"MEASURE mixed nonblankTextCharacters={WideContentChecks.Text(1, 1).Length} textRows={sources * textCount}; blank source=166 field=4; all fields mapped");
        watch.Restart();
        var boot = await Call("editor/bootstrap", new { reportId = f.Report.Id });
        Console.WriteLine($"MEASURE wide bootstrapMs={watch.ElapsedMilliseconds}");
        var context = boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var members = Enumerable.Range(1, numberCount).Select(i => (Id: "n" + i, Type: "NUMBER")).Append((Id: "table", Type: "TABLE")).ToArray();
        AggregateExpressionDto Sum(AggregateExpressionDto argument) => new() { Kind = "CALL", Name = "SUM", Arguments = [argument] };
        var recipe = new AggregateRecipeDto {
            SchemaVersion = 1, SemanticProfile = "REPORT_MAPPING_V1",
            Nodes = [new() { Id = "s", Kind = "SOURCE", Form = Pin(f.SourceForm), Origin = "DIRECT_CHILD_REPORTS", SourceCardinality = "SET", Inputs = [],
                Outputs = members.Select(m => new AggregatePortDto(m.Id, m.Type, "SET", m.Id, "w")).ToList() },
                new() { Id = "c", Kind = "CALCULATION", Inputs = members.Select(m => new AggregatePortDto("i" + m.Id, m.Type, "SET")).ToList(),
                    Outputs = members.Select(m => new AggregatePortDto(m.Id, m.Type, "SINGLE")).ToList(),
                    Expressions = members.Where(m => m.Type == "NUMBER").Select(m => new AggregateOutputExpressionDto(m.Id, Sum(new() { Kind = "INPUT", Ref = "i" + m.Id }))).ToList(),
                    TableAssignments = Enumerable.Range(1, rowCount).SelectMany(r => Enumerable.Range(1, columnCount).Select(c =>
                        new AggregateTableAssignmentDto("cell" + r + "_" + c, null, "table", null, new("CELL", r, r, c, c),
                            Sum(new() { Kind = "TABLE_RANGE", Ref = "itable", Area = new("CELL", r, r, c, c) })))).ToList() },
                new() { Id = "t", Kind = "TARGET", Form = Pin(f.TargetForm), Inputs = members.Select(m => new AggregatePortDto(m.Id, m.Type, "SINGLE", m.Id)).ToList(), Outputs = [] }],
            Edges = members.SelectMany(m => new[] { new AggregateEdgeDto("a" + m.Id, new("s", m.Id), new("c", "i" + m.Id)),
                new AggregateEdgeDto("b" + m.Id, new("c", m.Id), new("t", m.Id)) }).ToList(),
            TimeRules = [new() { Id = "w", Mode = "TARGET_DATA_WINDOW", SourceDateBasis = "DECLARED_DATA_WINDOW", Match = "CONTAINED" }] };
        foreach (var i in Enumerable.Range(1, textCount)) WideContentChecks.AddMapping(recipe, i);
        var sourceSchema = await Call("editor/source-schema", new AggregateEditorSchemaQueryDto(context, Pin(f.SourceForm)));
        Check(sourceSchema.GetProperty("members").GetArrayLength() == 41, "published source exposes 40 mapped scalar fields and one native matrix");
        var config = await Call("configs", new AggregateConfigCreateCommandDto(context, new(run + "-wide-config", context.BindingId, Pin(f.TargetForm), recipe)));
        var instance = await Call("instances", new AggregateInstanceCreateCommandDto(run + "-wide-instance", context, config.GetProperty("id").GetString()!));
        var instanceId = instance.GetProperty("id").GetString()!;
        var change = new AggregateMappingChangeDto(1, null, new([new("s", "FORM_SELECTOR", [], [])]), [], false);
        async Task<string> Snapshot()
        {
            var rows = new List<string>();
            foreach (var name in AggregateCollections.All)
                rows.AddRange((await db.Db.GetCollection<BsonDocument>(name).Find(new BsonDocument("workId", f.WorkId)).Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(x => x.ToJson()));
            var target = await db.WorkAssignmentReports.Find(x => x.Id == f.Report.Id).SingleAsync(ct);
            rows.Add(target.ToJson()); rows.Add((await payload.LoadReportPayloadAsync(target, ct)).ToJson());
            return AggregateDigest.Of(rows);
        }
        var before = await Snapshot();
        watch.Restart();
        JsonElement preview = default;
        // Mixed long-content UX uses the approved job path. Start it cold, without
        // a synchronous attempt pre-populating staging artifacts and skewing the measurement.
        try { if (!mixedContent) preview = await Call($"instances/{instanceId}/mapping/preview", new AggregateMappingPreviewCommandDto(context, change)); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("AGG_BUDGET_EXCEEDED"))
        { Console.WriteLine($"LIMIT wide synchronousMappingPreviewMs={watch.ElapsedMilliseconds} {ex.Message}"); }
        if (mixedContent) Console.WriteLine("MEASURE mixed cold job; synchronous preview deliberately not run before staging");
        if (preview.ValueKind != JsonValueKind.Undefined)
        {
            Console.WriteLine($"MEASURE wide synchronousMappingPreviewMs={watch.ElapsedMilliseconds} responseBytes={System.Text.Encoding.UTF8.GetByteCount(preview.GetRawText())}");
            foreach (var property in preview.GetProperty("preview").EnumerateObject())
                Console.WriteLine($"MEASURE wide envelopePart={property.Name} bytes={System.Text.Encoding.UTF8.GetByteCount(property.Value.GetRawText())}");
        }
        var totalOrdinals = sources * (sources + 1) / 2;
        void Verify(JsonElement envelope)
        {
            var p = envelope.GetProperty("preview"); var results = p.GetProperty("results");
            Check(p.GetProperty("linkedSources").GetArrayLength() == sources && results.GetArrayLength() == 41, "all 166 sources and 41 target members included");
            foreach (var item in results.EnumerateArray())
            {
                var member = item.GetProperty("portId").GetString()!;
                if (member.StartsWith("content", StringComparison.Ordinal))
                {
                    var reference = item.GetProperty("value").Deserialize<AggregateContentReference>(AggregateCanonical.Json)!;
                    Check(reference.RowCount == sources && reference.BlankRows == (member == "content4" ? 1 : 0), "content reference retains all units and approved blank " + member);
                    continue;
                }
                if (member != "table") Check(item.GetProperty("value").GetString() == (totalOrdinals * int.Parse(member[1..])).ToString(), "exact scalar " + member);
                else
                {
                    var rows = item.GetProperty("value").GetProperty("rows");
                    Check(rows.GetArrayLength() == rowCount && rows.EnumerateArray().All(row => row.GetProperty("cells").GetArrayLength() == columnCount), "matrix retains 16 x 10 shape");
                    for (var r = 0; r < rowCount; r++) for (var c = 0; c < columnCount; c++)
                        if (rows[r].GetProperty("cells")[c].GetProperty("value").GetString() != (totalOrdinals * (scalarCount + r * columnCount + c + 1)).ToString())
                            throw new InvalidOperationException($"Wrong matrix value at {r + 1},{c + 1}");
                    Check(true, "all 160 matrix sums match independently computed expectations");
                }
            }
        }
        if (preview.ValueKind != JsonValueKind.Undefined) Verify(preview.GetProperty("preview"));
        var jobs = new AggregatePreviewJobs(db, payload, configuration);
        watch.Restart();
        var request = new AggregatePreviewJobRequest("MAPPING", context, null, instanceId, change);
        var jobId = mixedContent ? (await Call("preview-jobs/start", request)).GetProperty("id").GetString()!
            : await jobs.Start(request, f.Actor, run + "-session", ct);
        Console.WriteLine($"MEASURE wide jobStartMs={watch.ElapsedMilliseconds}"); watch.Restart();
        async Task WaitForHangfire()
        {
            while ((await Call($"preview-jobs/{jobId}/read", new { })).GetProperty("state").GetString() is "QUEUED" or "RUNNING") await Task.Delay(200, ct);
        }
        var worker = mixedContent ? WaitForHangfire() : jobs.Run(jobId, runner, payload, ct); var seen = new List<int>();
        while (!worker.IsCompleted)
        {
            var state = JsonSerializer.SerializeToElement(await jobs.Read(jobId, f.Actor, run + "-session", ct), AggregateCanonical.Json);
            if (state.GetProperty("progress").ValueKind == JsonValueKind.Object) seen.Add(state.GetProperty("progress").GetProperty("processed").GetInt32());
            await Task.WhenAny(worker, Task.Delay(1000, ct));
        }
        await worker;
        var finished = JsonSerializer.SerializeToElement(await jobs.Read(jobId, f.Actor, run + "-session", ct), AggregateCanonical.Json);
        Console.WriteLine($"MEASURE wide jobAndReadMs={watch.ElapsedMilliseconds} state={finished.GetProperty("state")} error={finished.GetProperty("errorCode")} processed={string.Join(',', seen)} responseBytes={System.Text.Encoding.UTF8.GetByteCount(finished.GetRawText())}");
        if (finished.GetProperty("state").GetString() != "COMPLETED")
        {
            Check(finished.GetProperty("result").ValueKind == JsonValueKind.Null
                && finished.GetProperty("progress").ValueKind == JsonValueKind.Null, "failed job exposes no partial result or confirmation");
            Check(before == await Snapshot(), "failed sync/job preview leaves target payload and aggregate business records unchanged");
        }
        Check(finished.GetProperty("state").GetString() == "COMPLETED", "background mapping preview completed");
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, mixedContent ? "mixed-response.json" : "wide-response.json"), finished.GetRawText(), ct);
        var evidence = finished.GetProperty("result").GetProperty("preview").Deserialize<AggregatePreviewEnvelope>(AggregateCanonical.Json)!;
        var tableResult = evidence.Preview.Results.Single(r => r.PortId == "table");
        Check(evidence.Lineage[tableResult.LineageRef].Count == sources * rowCount * columnCount,
            "compact transport retains every matrix contribution including all coordinates");
        var tableSources = evidence.SourceValues.Where(s => s.PortId == "table").ToArray();
        Check(tableSources.Length == sources && tableSources.All(s => s.Value.GetProperty("value").GetProperty("rows").EnumerateArray()
            .SelectMany(r => r.GetProperty("cells").EnumerateArray()).All(c => c.GetProperty("denominator").GetString() == "1"
                && c.GetProperty("numerator").GetString() == c.GetProperty("value").GetString()
                && c.GetProperty("lineage").GetArrayLength() == 1)), "source evidence retains exact numbers and per-cell lineage without duplicate cell tree");
        Verify(finished.GetProperty("result").GetProperty("preview"));
        Check(before == await Snapshot(), "sync/job preview leaves target payload and aggregate business records unchanged");
        watch.Restart();
        await Call($"instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + "-wide-apply", context, change, finished.GetProperty("result").GetProperty("token").GetString()!));
        Console.WriteLine($"MEASURE wide applyMs={watch.ElapsedMilliseconds}");
        var current = await db.WorkAssignmentReports.Find(x => x.Id == f.Report.Id).SingleAsync(ct);
        var savedPayload = await payload.LoadReportPayloadAsync(current, ct);
        WorkReportPayloadConsistency.EnsureSnapshotFreshForStatisticProjection(current, savedPayload);
        DynamicFormNativeTableValues.Validate(f.TargetForm, current.DynamicFormSchemaHash, savedPayload.TableValuesJson, false);
        var nativeFields = JsonSerializer.Deserialize<JsonElement>(savedPayload.FieldValuesJson!);
        var nativeRows = JsonSerializer.Deserialize<JsonElement>(savedPayload.TableValuesJson!).GetProperty("nativeTables").GetProperty("tables")[0].GetProperty("rows");
        Check(Enumerable.Range(1, numberCount).All(i => nativeFields.GetProperty("n" + i).GetDecimal() == totalOrdinals * i), $"native readback all {numberCount} scalar sums");
        Check(Enumerable.Range(0, rowCount).All(r => nativeRows[r].GetProperty("rowId").GetString() == "r" + (r + 1)
            && Enumerable.Range(1, columnCount).All(c => nativeRows[r].GetProperty("cells").GetProperty("c" + c).GetProperty("value").GetDecimal() == totalOrdinals * (scalarCount + r * columnCount + c))),
            "native readback all 160 cells and fixed row identities");
        if (mixedContent) await WideContentChecks.ReadBack(db, f, savedPayload.TableValuesJson!, current.PayloadRevision, Call, Check, ct);
        var submission = await Call($"reports/{f.Report.Id}/submission-preview", new AggregateInstanceReadCommandDto(context));
        watch.Restart();
        await LifecycleChecks.Run(db, runner, configuration, f, run, instanceId, submission.GetProperty("token").GetString()!, ct);
        Console.WriteLine($"MEASURE wide lifecycleAndRefreshMs={watch.ElapsedMilliseconds}");
        var frozenRow = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen).Find(new BsonDocument("target", f.Report.Id)).SingleAsync(ct);
        var frozen = AggregateMongoTransaction.Read<AggregateFrozenState>(frozenRow).Value;
        Check(frozen.Instance.Applied == null && frozen.Preview.Lineage[tableResult.LineageRef].Count == sources * rowCount * columnCount,
            "frozen snapshot keeps one complete evidence copy and reads all 26560 table contributions");
        Console.WriteLine($"MEASURE wide frozenBytes={System.Text.Encoding.UTF8.GetByteCount(frozenRow["body"].AsString)}");
        Console.WriteLine("PASS wide exploratory 166 x 200 workload complete; lifecycle participant seam only, no browser, concurrency, year-scale or SLA claim; fixtures retained.");
    }
}
