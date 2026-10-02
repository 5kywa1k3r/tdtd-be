using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class PreviewJobChecks
{
    internal static async Task Run(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner runner, IConfiguration config,
        string run, AggregateRecipeDto prototype, Func<string, object, string?, HttpStatusCode, Task<JsonElement>> post, CancellationToken ct)
    {
        var payload = new WorkReportPayloadService(db); var store = new AggregateMongoStore(db, runner, payload, payload);
        var f = await PeriodicFixture.Seed(db, store, run + "-job", ct);
        Task<JsonElement> Call(string path, object body) => post("aggregate-v2/" + path, body, f.Root.Actor, HttpStatusCode.OK);
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS preview-job " + name); }
        AggregateFormPinDto Pin(DynamicFormTemplate form) => new(form.Id, form.FamilyId!, form.VersionNo, form.PublishedSchemaHash!);
        var recipe = prototype with { Nodes = prototype.Nodes.Select(n => n.Kind == "SOURCE" ? n with { Form = Pin(f.Root.SourceForm) }
            : n.Kind == "TARGET" ? n with { Form = Pin(f.Root.TargetForm) } : n).ToList() };
        var boot = await Call("editor/bootstrap", new { reportId = f.Targets[0].Id });
        var context = boot.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        var expected = boot.GetProperty("revisions").Deserialize<AggregateExpectedRevisionsDto>(AggregateCanonical.Json)!;
        var raw = new AggregatePreviewRequestDto(context, "draft-preview", "draft-config", expected, recipe, new([new("s", "FORM_SELECTOR", [], [])]));
        var request = new AggregatePreviewJobRequest("RECIPE", context, raw, null, null);
        var jobs = new AggregatePreviewJobs(db, payload, config); var actor = f.Root.Actor; var session = run + "-session";
        async Task<JsonElement> Read(string id) => JsonSerializer.SerializeToElement(await jobs.Read(id, actor, session, ct), AggregateCanonical.Json);
        async Task<string> Business()
        {
            var rows = new List<string>();
            foreach (var name in AggregateCollections.All)
                rows.AddRange((await db.Db.GetCollection<BsonDocument>(name).Find(new BsonDocument("workId", f.Root.WorkId)).Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(r => r.ToJson()));
            rows.AddRange((await db.WorkAssignmentReports.Find(r => r.WorkId == f.Root.WorkId).SortBy(r => r.Id).ToListAsync(ct)).Select(r => r.ToJson()));
            return string.Join('\n', rows);
        }
        var before = await Business();
        var ids = await Task.WhenAll(jobs.Start(request, actor, session, ct), jobs.Start(request, actor, session, ct));
        Check(ids[0] == ids[1], "same request and source stamp deduplicate concurrent starts");
        Check((await Read(ids[0])).GetProperty("state").GetString() == "QUEUED", "durable queued state exists before executor runs");
        await Task.WhenAll(jobs.Run(ids[0], runner, payload, ct), jobs.Run(ids[0], runner, payload, ct));
        var done = await Read(ids[0]);
        Check(done.GetProperty("state").GetString() == "COMPLETED" && done.GetProperty("attempt").GetInt32() == 1
            && done.GetProperty("result").GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString() == "10",
            "only one executor owns a run and produces the native-source result");
        Check(done.GetProperty("result").GetProperty("preview").GetProperty("previewToken").ValueKind == JsonValueKind.Null,
            "raw job result cannot authorize Apply");
        Check(before == await Business(), "preview job writes derived job state only, preserving aggregate business rows and report headers");
        try { await jobs.Read(ids[0], f.Root.Outsider, session, ct); throw new InvalidOperationException("job exposed to outsider"); }
        catch (AggregatePreviewException ex) when (ex.Code == "AGG_CONTEXT_UNAVAILABLE") { Check(true, "outsider cannot read job progress or result"); }
        try { await jobs.Read(ids[0], actor, session + "-other", ct); throw new InvalidOperationException("job exposed across sessions"); }
        catch (AggregatePreviewException ex) when (ex.Code == "AGG_CONTEXT_UNAVAILABLE") { Check(true, "job result is isolated by login session"); }
        var altered = request with { Recipe = raw with { Recipe = recipe with { TimeRules = [recipe.TimeRules[0] with { Mode = "EXPLICIT_RANGE", StartDate = "2026-10-01", EndDate = "2026-10-31" }] } } };
        var cancelled = await jobs.Start(altered, actor, session, ct); await jobs.Cancel(cancelled, actor, session, ct);
        await jobs.Run(cancelled, runner, payload, ct);
        Check((await Read(cancelled)).GetProperty("state").GetString() == "CANCELLED", "queued cancellation prevents execution and exposes no result");
        Check(await jobs.Start(altered, actor, session, ct) == cancelled, "explicit retry reuses cancelled run identity");
        await jobs.Run(cancelled, runner, payload, ct);
        Check((await Read(cancelled)).GetProperty("result").GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString() == "20",
            "retry computes the changed window independently");
        await db.WorkAssignments.UpdateOneAsync(a => a.Id == f.Sources[0].WorkAssignmentId,
            Builders<WorkAssignment>.Update.Set(a => a.ParentAssignmentId, f.Root.Outsider), cancellationToken: ct);
        try
        {
            try { await Read(ids[0]); throw new InvalidOperationException("stale completed result exposed"); }
            catch (AggregatePreviewException ex) when (ex.Code is "AGG_INPUT_STALE" or "AGG_SOURCE_UNAVAILABLE") { Check(true, "source relationship change withholds previously completed result"); }
        }
        finally { await db.WorkAssignments.UpdateOneAsync(a => a.Id == f.Sources[0].WorkAssignmentId,
            Builders<WorkAssignment>.Update.Set(a => a.ParentAssignmentId, context.AssignmentId), cancellationToken: ct); }
        var head = await Call("configs", new AggregateConfigCreateCommandDto(context, new(run + "-job-config", context.BindingId, Pin(f.Root.TargetForm), recipe)));
        var instanceId = (await Call("instances", new AggregateInstanceCreateCommandDto(run + "-job-instance", context, head.GetProperty("id").GetString()!))).GetProperty("id").GetString()!;
        var mapping = new AggregatePreviewJobRequest("MAPPING", context, null, instanceId, new(1, null, raw.Selection, [], false));
        var mappingId = await jobs.Start(mapping, actor, session, ct); before = await Business();
        await jobs.Run(mappingId, runner, payload, ct); var prepared = await Read(mappingId);
        Check(prepared.GetProperty("state").GetString() == "COMPLETED", "persisted mapping preview runs outside a long transaction");
        var result = prepared.GetProperty("result");
        Check(result.GetProperty("token").GetString()!.Length > 0 && before == await Business(), "completed mapping gets confirmation without writing target");
        await Call($"instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + "-job-apply", context, mapping.Change!, result.GetProperty("token").GetString()!));
        var native = await db.WorkAssignmentReports.Find(r => r.Id == f.Targets[0].Id).SingleAsync(ct);
        Check(JsonDocument.Parse((await payload.LoadReportPayloadAsync(native, ct)).FieldValuesJson!).RootElement.GetProperty("total").GetDecimal() == 10,
            "existing Apply revalidates and accepts completed job confirmation");
        var configId = head.GetProperty("id").GetString()!;
        var impact = new AggregateConfigImpactRequestDto(1, recipe, [new(instanceId, 2)], []);
        var revisionJob = new AggregatePreviewJobRequest("CONFIG", context, null, null, null, configId, impact);
        var revisionId = await jobs.Start(revisionJob, actor, session, ct); before = await Business();
        await jobs.Run(revisionId, runner, payload, ct); var revision = await Read(revisionId);
        Check(revision.GetProperty("state").GetString() == "COMPLETED" && before == await Business(),
            "config revision job previews selected draft without publishing or writing report");
        var revisionResult = revision.GetProperty("result");
        Check(revisionResult.GetProperty("plan").GetProperty("previews").GetProperty(instanceId).GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString() == "10",
            "config revision preview uses existing evaluator for unchanged sources");
        await Call($"configs/{configId}/revisions", new AggregateConfigRevisionCommandDto(run + "-job-revision", context, impact, revisionResult.GetProperty("token").GetString()!));
        Check((await Call($"instances/{instanceId}/read", new { context })).GetProperty("instance").GetProperty("configRevision").GetInt32() == 2,
            "existing config revision commit accepts completed job confirmation");
        boot = await Call("editor/bootstrap", new { reportId = f.Targets[0].Id });
        raw = raw with { Expected = boot.GetProperty("revisions").Deserialize<AggregateExpectedRevisionsDto>(AggregateCanonical.Json)! };
        request = request with { Recipe = raw };
        var recoveryId = await jobs.Start(request, actor, session, ct);
        await db.Db.GetCollection<BsonDocument>(AggregatePreviewJobs.Collection).UpdateOneAsync(new BsonDocument("_id", recoveryId),
            new BsonDocument("$set", new BsonDocument { ["state"] = "RUNNING", ["lease"] = "lost-worker", ["leaseUntil"] = DateTime.UtcNow.AddMinutes(-1), ["attempt"] = 1 }), cancellationToken: ct);
        await jobs.Run(recoveryId, runner, payload, ct);
        Check((await Read(recoveryId)).GetProperty("state").GetString() == "COMPLETED" && (await Read(recoveryId)).GetProperty("attempt").GetInt32() == 2,
            "expired worker lease restarts safely as a new attempt under same run");
        Check(await jobs.Current(context, actor, session, ct) == recoveryId, "current preview restores latest eligible run");
        await db.Db.GetCollection<BsonDocument>(AggregatePreviewJobs.Collection).UpdateOneAsync(new BsonDocument("_id", recoveryId),
            new BsonDocument("$set", new BsonDocument("updatedAt", DateTime.UtcNow.AddMinutes(-4))), cancellationToken: ct);
        Check(await jobs.Start(request, actor, session, ct) == recoveryId, "expired completed preview restarts under the same request identity");
        var lastComplete = await jobs.Current(context, actor, session, ct, completedOnly: true);
        Check(lastComplete != null && lastComplete != recoveryId && (await Read(lastComplete)).GetProperty("state").GetString() == "COMPLETED"
            && (await Read(recoveryId)).GetProperty("result").ValueKind == JsonValueKind.Null,
            "preceding completed result stays separate from the restarted run and its empty provisional result");
        await jobs.Run(recoveryId, runner, payload, ct);
        var delayed = new BlockingPayloadReader(payload, f.Sources[0].Id);
        var interruptible = new AggregatePreviewJobs(db, delayed, config);
        var runningId = await interruptible.Start(request with { Recipe = raw with { InstanceId = "cancel-running" } }, actor, session, ct);
        var running = interruptible.Run(runningId, runner, payload, ct);
        await delayed.Entered.Task.WaitAsync(ct);
        var partial = await Read(runningId);
        Check(partial.GetProperty("state").GetString() == "RUNNING" && partial.GetProperty("progress").GetProperty("processed").GetInt32() == 0
            && partial.GetProperty("progress").GetProperty("results")[0].GetProperty("value").ValueKind == JsonValueKind.Null,
            "running job exposes actual progress without fabricating zero before first payload");
        await jobs.Cancel(runningId, actor, session, ct); delayed.Release.TrySetResult(); await running;
        var cancelledRunning = await Read(runningId);
        Check(cancelledRunning.GetProperty("state").GetString() == "CANCELLED" && cancelledRunning.GetProperty("progress").ValueKind == JsonValueKind.Null
            && cancelledRunning.GetProperty("result").ValueKind == JsonValueKind.Null, "running cancellation fences a late worker and removes partial values");
        delayed = new BlockingPayloadReader(payload, f.Sources[0].Id); interruptible = new AggregatePreviewJobs(db, delayed, config);
        runningId = await interruptible.Start(request with { Recipe = raw with { InstanceId = "revoke-running" } }, actor, session, ct);
        running = interruptible.Run(runningId, runner, payload, ct); await delayed.Entered.Task.WaitAsync(ct);
        await db.WorkAssignments.UpdateOneAsync(a => a.Id == f.Sources[0].WorkAssignmentId,
            Builders<WorkAssignment>.Update.Set(a => a.ParentAssignmentId, f.Root.Outsider), cancellationToken: ct);
        try
        {
            try { await Read(runningId); throw new InvalidOperationException("partial exposed after source revoke"); }
            catch (AggregatePreviewException ex) when (ex.Code is "AGG_INPUT_STALE" or "AGG_SOURCE_UNAVAILABLE") { Check(true, "source revocation withholds already published partial progress"); }
            delayed.Release.TrySetResult(); await running;
            var failed = await db.Db.GetCollection<BsonDocument>(AggregatePreviewJobs.Collection).Find(new BsonDocument("_id", runningId)).SingleAsync(ct);
            Check(failed["state"] == "FAILED" && !failed.Contains("progress") && !failed.Contains("result"), "source change during run fails and discards intermediate values");
        }
        finally { delayed.Release.TrySetResult(); await db.WorkAssignments.UpdateOneAsync(a => a.Id == f.Sources[0].WorkAssignmentId,
            Builders<WorkAssignment>.Update.Set(a => a.ParentAssignmentId, context.AssignmentId), cancellationToken: ct); }
        await PreviewJobHostChecks.Run(db, actor, request, run, ct);
    }
    private sealed class BlockingPayloadReader(IWorkReportPayloadReader inner, string sourceId) : IWorkReportPayloadReader
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WorkReportPayloadSnapshot> LoadReportPayloadAsync(WorkAssignmentReport report, CancellationToken ct = default)
        {
            if (report.Id == sourceId) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return await inner.LoadReportPayloadAsync(report, ct);
        }
        public Task<string?> LoadReportTableBlockAsync(WorkAssignmentReport report, string blockId, CancellationToken ct = default)
            => inner.LoadReportTableBlockAsync(report, blockId, ct);
        public Task<IReadOnlyDictionary<string, string>> LoadReportTableBlocksAsync(WorkAssignmentReport report, IEnumerable<string> blockIds, CancellationToken ct = default)
            => inner.LoadReportTableBlocksAsync(report, blockIds, ct);
    }
}
