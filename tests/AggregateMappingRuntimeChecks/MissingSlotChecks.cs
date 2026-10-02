using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;
using tdtd_be.Services.DynamicFlows;
using tdtd_be.Services.WorkAssignmentReports.Payloads;

internal static class MissingSlotChecks
{
    internal static async Task Run(MongoDbContext db, IDynamicFlowDefinitionTransactionRunner runner, IConfiguration config,
        string run, AggregateRecipeDto prototype, Func<string, object, string?, HttpStatusCode, Task<JsonElement>> post, CancellationToken ct)
    {
        var payload = new WorkReportPayloadService(db);
        var store = new AggregateMongoStore(db, runner, payload, payload);
        var f = await PeriodicFixture.Seed(db, store, run + "-slots", ct);
        var target = f.Targets[1]; var source = f.Sources[1]; var actor = f.Root.Actor;
        var period = await db.WorkReportPeriods.Find(p => p.Id == source.WorkReportPeriodId).SingleAsync(ct);
        var slotKey = period.WorkTemplateAssigneeId + ":" + period.PeriodKey;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS missing-slot " + name); }
        Task<JsonElement> Call(string path, object body) => post("aggregate-v2/" + path, body, actor, HttpStatusCode.OK);
        async Task<AggregatePeriodContextDto> Context() => (await Call("editor/bootstrap", new { reportId = target.Id }))
            .GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
        AggregateFormPinDto Pin(DynamicFormTemplate form) => new(form.Id, form.FamilyId!, form.VersionNo, form.PublishedSchemaHash!);
        var recipe = prototype with { Nodes = prototype.Nodes.Select(n => n.Kind == "SOURCE" ? n with { Form = Pin(f.Root.SourceForm) }
            : n.Kind == "TARGET" ? n with { Form = Pin(f.Root.TargetForm) } : n).ToList() };
        var context = await Context();
        var created = await Call("configs", new AggregateConfigCreateCommandDto(context, new(run + "-slots-config", context.BindingId, Pin(f.Root.TargetForm), recipe)));
        var instanceId = (await Call("instances", new AggregateInstanceCreateCommandDto(run + "-slots-instance", context, created.GetProperty("id").GetString()!)))
            .GetProperty("id").GetString()!;
        async Task<AggregateInstanceState> State() => (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateInstanceState>(AggregateCollections.Instances, instanceId, token), ct))!.Value;
        async Task<AggregateLockState?> SlotLock() => (await store.ExecuteAsync((tx, token) => tx.GetAsync<AggregateLockState>(AggregateCollections.Locks, "SLOT:" + slotKey, token), ct))?.Value;
        async Task<string> Snapshot()
        {
            var rows = new List<string>();
            foreach (var name in AggregateCollections.All)
                rows.AddRange((await db.Db.GetCollection<BsonDocument>(name).Find(new BsonDocument("workId", f.Root.WorkId))
                    .Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(r => r.ToJson()));
            foreach (var report in await db.WorkAssignmentReports.Find(r => r.WorkId == f.Root.WorkId).SortBy(r => r.Id).ToListAsync(ct))
            {
                rows.Add(report.ToJson());
                var body = await payload.LoadReportPayloadAsync(report, ct);
                rows.Add(JsonSerializer.Serialize(new { body.FieldValuesJson, body.TableValuesJson }));
            }
            rows.AddRange((await db.WorkReportPeriods.Find(p => p.WorkId == f.Root.WorkId).SortBy(p => p.Id).ToListAsync(ct)).Select(p => p.ToJson()));
            return string.Join('\n', rows);
        }
        // Model a slot without a current report, preserving its old payload as historical evidence.
        // This is a real Mongo participant test, not acceptance of the native open/materialize API.
        async Task SetCurrent(bool current) => await runner.ExecuteAsync(async (session, token) => {
            await db.WorkAssignmentReports.UpdateOneAsync(session, r => r.Id == source.Id,
                Builders<WorkAssignmentReport>.Update.Set(r => r.IsCurrent, current), cancellationToken: token);
            await db.WorkReportPeriods.UpdateOneAsync(session, p => p.Id == period.Id,
                Builders<WorkReportPeriod>.Update.Set(p => p.CurrentReportId, current ? source.Id : null), cancellationToken: token);
            return true;
        }, ct);
        async Task Transition(string operation, WorkAssignmentReportStatus status, string? confirmation = null)
        {
            await runner.ExecuteAsync(async (session, token) => {
                var before = await db.WorkAssignmentReports.Find(session, r => r.Id == target.Id).SingleAsync(token);
                await AggregateHostIntegration.LifecycleAsync(db, session, runner, before, operation, actor, run + "-slots-" + operation,
                    status, before.LifecycleRevision + 1, token, config, run + "-session", confirmation);
                await db.WorkAssignmentReports.UpdateOneAsync(session, r => r.Id == before.Id,
                    Builders<WorkAssignmentReport>.Update.Set(r => r.Status, status).Inc(r => r.LifecycleRevision, 1), cancellationToken: token);
                return true;
            }, ct);
        }
        async Task<string> Confirmation() => (await Call($"reports/{target.Id}/submission-preview", new AggregateInstanceReadCommandDto(await Context())))
            .GetProperty("token").GetString()!;
        await SetCurrent(false);
        try
        {
            var state = await State(); context = await Context();
            var change = new AggregateMappingChangeDto(state.Revision, null, state.Selection, [], false);
            var before = await Snapshot();
            var review = await Call($"instances/{instanceId}/mapping/preview", new AggregateMappingPreviewCommandDto(context, change));
            var preview = review.GetProperty("preview").GetProperty("preview");
            Check(preview.GetProperty("state").GetString() == "VALID" && preview.GetProperty("completeness").GetString() == "MISSING_SOURCES"
                && preview.GetProperty("results")[0].GetProperty("state").GetString() == "NO_RESULT",
                "known missing occurrence stays explicit missing and NO_RESULT, never zero");
            Check(before == await Snapshot(), "mapping preview leaves aggregate rows, report headers, payloads and periods unchanged");
            await Call($"instances/{instanceId}/apply", new AggregateMappingApplyCommandDto(run + "-slots-apply", context, change, review.GetProperty("token").GetString()!));
            var native = await db.WorkAssignmentReports.Find(r => r.Id == target.Id).SingleAsync(ct);
            var fields = JsonDocument.Parse((await payload.LoadReportPayloadAsync(native, ct)).FieldValuesJson!).RootElement;
            Check(!fields.TryGetProperty("total", out var value) || value.ValueKind == JsonValueKind.Null,
                "Apply of known missing source leaves optional target empty");
            before = await Snapshot();
            var oldConfirmation = await Confirmation();
            Check(before == await Snapshot(), "submission preview creates no owner or frozen snapshot and does not mutate native data");
            // A source arrives after preview; old confirmation must fail and roll back lifecycle.
            await SetCurrent(true); before = await Snapshot();
            try { await Transition("SUBMIT", WorkAssignmentReportStatus.Submitted, oldConfirmation); throw new InvalidOperationException("stale missing-slot confirmation accepted"); }
            catch (AggregatePreviewException ex) when (ex.Code == "AGG_CONFIRMATION_STALE") { }
            Check(before == await Snapshot() && (await State()).State == "DRAFT" && (await SlotLock())?.Owners.Count is null or 0,
                "source arrival invalidates submission confirmation with atomic rollback");
            await SetCurrent(false);
            await Transition("SUBMIT", WorkAssignmentReportStatus.Submitted, await Confirmation());
            Check((await SlotLock())?.Owners.Single().SourceIdentity == slotKey && (await State()).State == "FROZEN",
                "submission owns the exact missing occurrence even without a current report");
            var frozen = await Snapshot();
            try
            {
                await runner.ExecuteAsync(async (session, token) => {
                    await AggregateHostIntegration.SlotAsync(db, session, f.Root.WorkId, period.WorkTemplateAssigneeId, period.PeriodKey, run + "-late", token);
                    await db.WorkReportPeriods.UpdateOneAsync(session, p => p.Id == period.Id,
                        Builders<WorkReportPeriod>.Update.Set(p => p.CurrentReportId, source.Id), cancellationToken: token);
                    return true;
                }, ct);
                throw new InvalidOperationException("locked occurrence materialized");
            }
            catch (AggregatePreviewException ex) when (ex.Code == "AGG_SOURCE_LOCKED") { }
            Check(frozen == await Snapshot(), "native slot participant blocks late materialization without writes");
            await runner.ExecuteAsync(async (session, token) => {
                await AggregateHostIntegration.SlotAsync(db, session, f.Root.WorkId, period.WorkTemplateAssigneeId, f.Sources[2].PeriodKey, run + "-other-period", token);
                return true;
            }, ct);
            Check((await SlotLock())?.Owners.Count == 1, "another occurrence of the same binding stays mutable while October remains locked");
            var frozenRows = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen).Find(new BsonDocument("target", target.Id)).ToListAsync(ct);
            await Transition("RETURN", WorkAssignmentReportStatus.Draft);
            Check((await SlotLock())?.Owners.Count == 0 && (await State()).State == "DRAFT",
                "return releases its missing-slot owner and restores draft mapping");
            await runner.ExecuteAsync(async (session, token) => {
                await AggregateHostIntegration.SlotAsync(db, session, f.Root.WorkId, period.WorkTemplateAssigneeId, period.PeriodKey, run + "-after-return", token);
                await db.WorkAssignmentReports.UpdateOneAsync(session, r => r.Id == source.Id, Builders<WorkAssignmentReport>.Update.Set(r => r.IsCurrent, true), cancellationToken: token);
                await db.WorkReportPeriods.UpdateOneAsync(session, p => p.Id == period.Id, Builders<WorkReportPeriod>.Update.Set(p => p.CurrentReportId, source.Id), cancellationToken: token);
                return true;
            }, ct);
            var afterFrozen = await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen).Find(new BsonDocument("target", target.Id)).ToListAsync(ct);
            Check((await db.WorkReportPeriods.Find(p => p.Id == period.Id).SingleAsync(ct)).CurrentReportId == source.Id
                && string.Join('\n', frozenRows.Select(r => r.ToJson())) == string.Join('\n', afterFrozen.Select(r => r.ToJson())),
                "slot can materialize after final owner release without rewriting frozen history");
        }
        finally { await SetCurrent(true); }
    }
}
